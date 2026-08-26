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
        - If the tools don't cover the question, say so instead of guessing.

        Workflow: start with search_atlas (or filter_syntaxes for kind/entity filtering), then
        resolve_ref for the full pattern and example of each syntax you use; read_doc for guide pages.
        """;

    private int _inFlight;

    public bool HasApiKey => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ApiKeyVariable));

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

    public async Task<AskResult> AskAsync(string question, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var stopwatch = Stopwatch.StartNew();
        var toolCalls = new List<AiToolCall>();
        int promptTokens = 0, completionTokens = 0;
        double cost = 0;
        string? answer = null;
        var outcome = "ok";
        var rounds = 0;

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = question }
        };

        try
        {
            for (rounds = 1; rounds <= opts.MaxToolRounds; rounds++)
            {
                // Last round: tool_choice "none" forces a final answer from what was gathered.
                var response = await CallOpenRouter(messages, opts, allowTools: rounds < opts.MaxToolRounds, ct);

                if (response["usage"] is JsonObject usage)
                {
                    promptTokens += ReadInt(usage, "prompt_tokens");
                    completionTokens += ReadInt(usage, "completion_tokens");
                    cost += ReadDouble(usage, "cost");
                }

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

                        string result;
                        try
                        {
                            result = ExecuteTool(name, args);
                        }
                        catch (Exception e)
                        {
                            result = $"Tool error: {e.Message}";
                        }

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
        var limit = Math.Clamp(ReadInt(args, "limit", 10), 1, 20);

        var hits = search.Search(query, limit);
        if (hits.Count == 0) return $"No results for \"{query}\". Try broader terms or filter_syntaxes.";

        var sb = new StringBuilder();
        foreach (var hit in hits)
        {
            var item = hit.Item;
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
        if (matches.Count == 0) return "No matching syntaxes; loosen the query or check the entity id.";

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
            return $"Unresolved reference \"{raw}\". Formats: entity id, entity#anchor, core#anchor, events#anchor, or a bare syntax id; search_atlas returns valid refs.";

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

/// <summary>Real usage numbers for one conversation, accumulated over every OpenRouter round.</summary>
public sealed record AskStats(
    string Model,
    int Rounds,
    IReadOnlyList<AiToolCall> ToolCalls,
    int PromptTokens,
    int CompletionTokens,
    double CostUsd,
    long DurationMs);
