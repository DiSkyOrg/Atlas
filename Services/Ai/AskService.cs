using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DiSkyAtlas.Endpoints;
using DiSkyAtlas.Models;
using DiSkyAtlas.Services.Docs;
using Microsoft.Extensions.Options;

namespace DiSkyAtlas.Services.Ai;

/// <summary>
/// The DiSky assistant: a bounded tool-calling loop against OpenRouter's OpenAI-compatible
/// chat-completions endpoint. The four tools call the same in-memory services and
/// <see cref="ApiMarkdown"/> renderers as the public /api/v1 endpoints, so the model reads
/// exactly what external agents read — without the HTTP hop or the public rate limit.
/// Every request carries usage accounting, so token counts and cost per conversation are
/// OpenRouter's real numbers, which feed <see cref="AiBudget"/> and <see cref="AiChatLog"/>.
/// </summary>
public sealed class AskService(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<AiOptions> options,
    ManifestService manifest,
    DocsService docs,
    SearchService search,
    AiBudget budget,
    ILogger<AskService> logger)
{
    public const string HttpClientName = "openrouter";
    public const string ApiKeyVariable = "OPENROUTER_API_KEY";

    private const string SystemPrompt =
        """
        You are the DiSky Atlas assistant. DiSky v5 is the Discord addon for Skript (Minecraft).
        Answer questions about DiSky using ONLY the provided tools — never from memory: look up
        every syntax before using its pattern in an answer.

        Rules:
        - Answer in the language of the question. Be concise: a short explanation plus one Skript code block.
        - Code must use the exact patterns returned by the tools. Cite each syntax you use with its
          ref in backticks (e.g. `member#ban`) so readers can open it on the site.
        - Create bots imperatively: the `a new discord bot` expression followed by the `login` effect,
          and pair every `login` with a `shutdown`. Never use the legacy `define bot` structure.
        - If the question is not about DiSky or Skript, politely decline in one sentence without calling tools.
        - If the tools don't cover the question, say so instead of guessing. DiSky's syntax set is
          partial: when no tool returns a syntax for part of a question, state that this version
          does not provide it - never invent a pattern, a property name or a ref.

        Workflow: search_atlas first (or filter_syntaxes for kind/entity filtering), then resolve_ref
        for the full pattern and examples of each syntax you use; read_doc for guide pages. When the
        question already carries automatic search_atlas results, go straight to resolve_ref instead of
        repeating that same search.
        """;

    private const string ForceAnswerPrompt =
        """
        Stop calling tools and answer now, using only what the tools already returned.
        DiSky's syntax set is limited: if it has no syntax for part of the question, say plainly
        that this version does not provide it rather than guessing a pattern.
        """;

    private int _inFlight;

    public bool HasApiKey => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ApiKeyVariable));

    /// <summary>
    /// The assistant kill-switch, hot-reloadable through <c>Ai:Enabled</c>. False hides every
    /// surface it has: the /ask page and its sidebar entry, the sitemap entry and the
    /// /api/v1 endpoint listing; the endpoint itself then refuses with 503 via AskGuards.
    /// </summary>
    public bool Available => options.CurrentValue.Enabled && HasApiKey;

    /// <summary>Concurrency guard (hot-reloadable limit); every successful TryEnter needs an Exit.</summary>
    public bool TryEnter()
    {
        var max = options.CurrentValue.MaxConcurrent;
        while (true)
        {
            var current = _inFlight;
            if (current >= max) return false;
            if (Interlocked.CompareExchange(ref _inFlight, current + 1, current) == current) return true;
        }
    }

    public void Exit() => Interlocked.Decrement(ref _inFlight);

    /// <param name="onEvent">Optional live-progress callback (the /ask page's tool timeline);
    /// invoked from the request loop, so Blazor callers must marshal to the renderer.</param>
    public async Task<AskResult> AskAsync(string question, CancellationToken ct, Action<AskEvent>? onEvent = null)
    {
        var opts = options.CurrentValue;
        var stopwatch = Stopwatch.StartNew();
        var toolCalls = new List<AiToolCall>();
        int promptTokens = 0, completionTokens = 0;
        double cost = 0;
        string? answer = null;
        var outcome = "ok";
        var rounds = 0;

        void Account(JsonObject response)
        {
            if (response["usage"] is not JsonObject usage) return;
            promptTokens += ReadInt(usage, "prompt_tokens");
            completionTokens += ReadInt(usage, "completion_tokens");
            cost += ReadDouble(usage, "cost");
        }

        try
        {
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = SeedUserMessage(question, opts, toolCalls, onEvent) }
            };

            for (rounds = 1; rounds <= opts.MaxToolRounds; rounds++)
            {
                onEvent?.Invoke(new AskEvent(AskEventKind.Thinking, rounds, null, null));

                // Last round asks for tool_choice "none" - a hint some providers honour and
                // Gemini does not, hence the salvage call after the loop.
                var response = await CallOpenRouter(messages, opts, allowTools: rounds < opts.MaxToolRounds, ct);
                Account(response);

                if (response["choices"]?[0]?["message"] is not JsonObject message)
                    throw new InvalidOperationException("OpenRouter response carries no message.");

                if (message["tool_calls"] is JsonArray calls && calls.Count > 0)
                {
                    messages.Add(message.DeepClone());
                    foreach (var call in calls)
                    {
                        var function = call?["function"] as JsonObject;
                        var name = function?["name"]?.GetValue<string>() ?? "";
                        var args = function?["arguments"]?.GetValue<string>() ?? "{}";
                        toolCalls.Add(new AiToolCall(name, Truncate(args, 500)));
                        onEvent?.Invoke(new AskEvent(AskEventKind.Tool, rounds, name, args));

                        string result;
                        try
                        {
                            result = ExecuteTool(name, args);
                        }
                        catch (Exception e)
                        {
                            result = $"Tool error: {e.Message}";
                        }
                        onEvent?.Invoke(new AskEvent(AskEventKind.ToolDone, rounds, name, args));

                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = call?["id"]?.GetValue<string>() ?? "",
                            ["content"] = result
                        });
                    }
                    continue;
                }

                answer = message["content"]?.GetValue<string>();
                break;
            }

            // A model that keeps calling tools through the last round leaves the loop with no
            // content at all. Rather than burn the whole conversation on a 502, ask once more
            // with an explicit stop instruction - the history already holds everything it read.
            if (string.IsNullOrWhiteSpace(answer))
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = ForceAnswerPrompt });
                var salvage = await CallOpenRouter(messages, opts, allowTools: false, ct);
                Account(salvage);
                answer = salvage["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            }

            if (string.IsNullOrWhiteSpace(answer))
                outcome = "error";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = "timeout";
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "AI assistant call failed");
            outcome = "error";
        }
        finally
        {
            budget.Record(cost);
        }

        return new AskResult(outcome, answer,
            new AskStats(opts.Model, rounds, toolCalls, promptTokens, completionTokens, cost, stopwatch.ElapsedMilliseconds));
    }

    // ---- OpenRouter ----------------------------------------------------------

    private async Task<JsonObject> CallOpenRouter(JsonArray messages, AiOptions opts, bool allowTools, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = opts.Model,
            ["max_tokens"] = opts.MaxOutputTokens,
            // Usage accounting: the response's usage block then includes the real cost in USD.
            ["usage"] = new JsonObject { ["include"] = true },
            ["messages"] = messages.DeepClone(),
            ["tools"] = ToolsJson()
        };
        if (!allowTools)
            body["tool_choice"] = "none";

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            Environment.GetEnvironmentVariable(ApiKeyVariable));
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter {(int)response.StatusCode}: {Truncate(text, 300)}");

        return JsonNode.Parse(text) as JsonObject
               ?? throw new InvalidOperationException("OpenRouter returned non-object JSON.");
    }

    private static JsonArray ToolsJson() =>
    [
        Tool("search_atlas",
            "Fuzzy search across DiSky entities, syntaxes, events and documentation pages. The best first step for any question.",
            new JsonObject
            {
                ["query"] = Param("string", "Free-text search terms, e.g. \"ban member\""),
                ["limit"] = Param("integer", "Max results (default 10, max 20)")
            }, required: ["query"]),
        Tool("filter_syntaxes",
            "Filter every DiSky syntax with a query DSL: free words (name+pattern+description) plus kind:/return:/change:/name:/since:/intent: and booleans shared:/writeonly:/async:/deprecated:/cancellable:. Optional entity scopes to one owner (e.g. \"guild\", \"core\", \"events\").",
            new JsonObject
            {
                ["query"] = Param("string", "DSL query, e.g. \"kind:effect message\""),
                ["entity"] = Param("string", "Owner entity id to scope to (optional)"),
                ["limit"] = Param("integer", "Max results (default 20, max 50)")
            }),
        Tool("resolve_ref",
            "Full detail (patterns, return type, changers, description, examples) for one reference: an entity id (\"guild\"), a syntax ref (\"member#ban\", \"core#login\"), an event ref (\"events#guild-join\") or a bare syntax id (\"effect-login-bot\").",
            new JsonObject
            {
                ["ref"] = Param("string", "The reference to resolve")
            }, required: ["ref"]),
        Tool("read_doc",
            "Read a documentation page as raw markdown, by its slug (from search_atlas doc hits).",
            new JsonObject
            {
                ["slug"] = Param("string", "Page slug, e.g. \"contributing/style-guide\"")
            }, required: ["slug"])
    ];

    private static JsonObject Tool(string name, string description, JsonObject properties, string[]? required = null) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. (required ?? []).Select(r => (JsonNode)r)])
            }
        }
    };

    private static JsonObject Param(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    // ---- Tool execution ------------------------------------------------------

    private string ExecuteTool(string name, string argsJson)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject ?? [];
        return name switch
        {
            "search_atlas" => SearchAtlas(args),
            "filter_syntaxes" => FilterSyntaxes(args),
            "resolve_ref" => ResolveRef(args),
            "read_doc" => ReadDoc(args),
            _ => $"Unknown tool \"{name}\"."
        };
    }

    private string SearchAtlas(JsonObject args)
    {
        var query = args["query"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(query)) return "Missing \"query\".";
        return SearchAtlas(query, Math.Clamp(ReadInt(args, "limit", 10), 1, 20));
    }

    private string SearchAtlas(string query, int limit)
    {
        var hits = search.Search(query, limit);
        if (hits.Count == 0)
            return $"No results for \"{query}\". Try ONE broader keyword, or filter_syntaxes. "
                 + "If a second search also comes back empty, DiSky has no syntax for this - say so.";

        var sb = new StringBuilder();
        foreach (var hit in hits)
        {
            var item = hit.Item;
            // Syntaxes and events render like filter_syntaxes - pattern and return type inline - so
            // a simple question is answerable without a follow-up resolve_ref round.
            if (LookupSyntax(item) is { } syntax)
            {
                ApiMarkdown.SyntaxCompact(sb, syntax, item.EntityId, withRef: true);
                continue;
            }
            sb.Append("- **").Append(item.Name).Append("** (").Append(item.Type)
              .Append('/').Append(item.Kind).Append(", ").Append(item.Parent).Append(')');
            sb.Append(item switch
            {
                { Type: "doc" } => $" — doc slug: `{item.EntityId}` (use read_doc)",
                { Type: "event" } => $" — ref: `events#{item.Anchor}`",
                { Type: "syntax" } => $" — ref: `{item.EntityId}#{item.Anchor}`",
                _ => $" — entity: `{item.EntityId}`"
            });
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private string FilterSyntaxes(JsonObject args)
    {
        var query = args["query"]?.GetValue<string>();
        var entity = args["entity"]?.GetValue<string>()?.Trim();
        var limit = Math.Clamp(ReadInt(args, "limit", 20), 1, 50);

        var filter = SyntaxFilter.Parse(query);
        var pool = manifest.AllSyntaxes();
        if (!string.IsNullOrEmpty(entity))
            pool = pool.Where(x => x.OwnerId.Equals(entity, StringComparison.OrdinalIgnoreCase));

        var matches = pool.Where(x => filter.Matches(x.Syntax)).ToList();
        if (matches.Count == 0)
            return "No matching syntaxes; loosen the query or check the entity id. "
                 + "If a second query also comes back empty, DiSky has no syntax for this - say so "
                 + "in your answer instead of searching again.";

        var sb = new StringBuilder();
        sb.Append(matches.Count).Append(" match(es)");
        if (matches.Count > limit) sb.Append(", showing ").Append(limit);
        sb.Append(":\n");
        foreach (var x in matches.Take(limit))
            ApiMarkdown.SyntaxCompact(sb, x.Syntax, x.OwnerId, withRef: true);
        return sb.ToString();
    }

    private string ResolveRef(JsonObject args)
    {
        var raw = args["ref"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(raw)) return "Missing \"ref\".";

        var reference = docs.Resolve(raw);
        if (!reference.Resolved)
            return $"Unresolved reference \"{raw}\" - it does not exist. Only ever cite refs returned "
                 + "by search_atlas or filter_syntaxes; never construct one. Formats: entity id, "
                 + "entity#anchor, core#anchor, events#anchor, or a bare syntax id.";

        var sb = new StringBuilder();
        switch (reference.Kind)
        {
            case AtlasRefKind.Doc:
                return ReadDocBySlug(reference.DocSlug!);
            case AtlasRefKind.Entity:
                ApiMarkdown.Entity(sb, manifest.GetEntity(reference.EntityId)!, manifest, docs);
                break;
            default:
                ApiMarkdown.Syntax(sb, reference.Syntax!, reference.EntityId ?? "core", manifest);
                break;
        }
        return sb.ToString();
    }

    private string ReadDoc(JsonObject args)
    {
        var slug = args["slug"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(slug)) return "Missing \"slug\".";
        return ReadDocBySlug(slug);
    }

    private string ReadDocBySlug(string slug)
    {
        var page = docs.GetPage(slug);
        if (page is null)
        {
            var known = docs.Sections
                .SelectMany(s => s.Index is { } i ? s.Pages.Prepend(i) : s.Pages)
                .Select(p => p.Slug);
            return $"Unknown doc page \"{slug}\". Available: {string.Join(", ", known)}.";
        }
        return File.ReadAllText(page.FilePath);
    }

    private SyntaxInfo? LookupSyntax(SearchItem item) => item.Type switch
    {
        "syntax" => manifest.FindSyntax(item.EntityId, item.SyntaxId ?? ""),
        "event" => manifest.Events.FirstOrDefault(e => e.Id == item.SyntaxId),
        _ => null
    };

    /// <summary>
    /// Pre-retrieval: the search the model would spend its first round on is run here instead and
    /// rides along with the question. The whole history is resent every round, so dropping a round
    /// saves its tokens on all the later ones - and a weak model no longer has to phrase the query.
    /// </summary>
    private string SeedUserMessage(string question, AiOptions opts, List<AiToolCall> toolCalls, Action<AskEvent>? onEvent)
    {
        if (opts.SeedSearchLimit <= 0) return question;

        var args = new JsonObject { ["query"] = question, ["limit"] = opts.SeedSearchLimit }.ToJsonString();
        toolCalls.Add(new AiToolCall("search_atlas", args));
        onEvent?.Invoke(new AskEvent(AskEventKind.Tool, 0, "search_atlas", args));
        var results = SearchAtlas(question, opts.SeedSearchLimit);
        onEvent?.Invoke(new AskEvent(AskEventKind.ToolDone, 0, "search_atlas", args));

        return $"""
                {question}

                ---
                Automatic `search_atlas` results for this question (already run for you; resolve_ref
                anything you cite, and only search again with different terms):
                {results}
                """;
    }

    // ---- Helpers -------------------------------------------------------------

    private static int ReadInt(JsonObject o, string key, int fallback = 0) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    private static double ReadDouble(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>Outcome of one assistant conversation: "ok", "error" or "timeout".</summary>
public sealed record AskResult(string Outcome, string? Answer, AskStats Stats);

/// <summary>A live-progress event emitted while a conversation runs (the /ask page's timeline).</summary>
public sealed record AskEvent(AskEventKind Kind, int Round, string? Tool, string? Args);

public enum AskEventKind
{
    /// <summary>An OpenRouter round is in flight (where the wall-clock time goes).</summary>
    Thinking,
    /// <summary>The model requested a tool call (about to execute).</summary>
    Tool,
    /// <summary>The tool call executed; its result is being fed back.</summary>
    ToolDone
}

/// <summary>Real usage numbers for one conversation, accumulated over every OpenRouter round.</summary>
public sealed record AskStats(
    string Model,
    int Rounds,
    IReadOnlyList<AiToolCall> ToolCalls,
    int PromptTokens,
    int CompletionTokens,
    double CostUsd,
    long DurationMs);
