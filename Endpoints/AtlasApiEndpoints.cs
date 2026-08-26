using System.Security.Cryptography;
using System.Text;
using DiSkyAtlas.Models;
using DiSkyAtlas.Services;
using DiSkyAtlas.Services.Ai;
using DiSkyAtlas.Services.Docs;
using Microsoft.Extensions.Options;

namespace DiSkyAtlas.Endpoints;

/// <summary>
/// The agent-facing read API (/api/v1) + /llms.txt. Everything is served from the same
/// in-memory singletons as the pages (ManifestService, DocsService, SearchService), so
/// handlers are allocation-light string builds. /api/v1 itself documents the surface.
/// </summary>
public static class AtlasApiEndpoints
{
    public static void MapAtlasApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireRateLimiting("api");

        // Shared cache validator + CORS for the whole group: the data only changes on
        // deploy (atlas.json) or docs edit, so one ETag covers every endpoint.
        api.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            var manifest = http.RequestServices.GetRequiredService<ManifestService>();
            var docs = http.RequestServices.GetRequiredService<DocsService>();
            var etag = ComputeETag(manifest, docs);

            var headers = http.Response.Headers;
            headers.ETag = etag;
            headers.CacheControl = "public, max-age=3600";
            headers.AccessControlAllowOrigin = "*";

            if (http.Request.Headers.IfNoneMatch.Any(v => v is not null && v.Contains(etag, StringComparison.Ordinal)))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            return await next(invocation);
        });

        // ---- Discovery ----------------------------------------------------
        api.MapGet("", (ManifestService manifest, DocsService docs) =>
            Markdown(ApiMarkdown.Discovery(manifest, docs)));

        // ---- Fuzzy search -------------------------------------------------
        api.MapGet("/search", (SearchService search, ManifestService manifest, HttpContext http,
            string? q, int limit = 20) =>
        {
            limit = Math.Clamp(limit, 1, 50);
            if (string.IsNullOrWhiteSpace(q))
                return NotFoundMd(manifest, "Missing `q`. Usage: `/api/v1/search?q=<text>&limit=20`.", 400);

            var hits = search.Search(q, limit);

            if (WantsJson(http))
                return Json(manifest, hits.Select(h => new
                {
                    h.Item.Name,
                    h.Item.Type,
                    h.Item.Kind,
                    h.Item.Parent,
                    h.Score,
                    Site = SearchService.SiteUrl(h.Item),
                    Api = ApiDetailUrl(h.Item)
                }));

            var sb = ApiMarkdown.Envelope(manifest);
            sb.Append("# Search: \"").Append(q).Append("\" — ").Append(hits.Count).Append(" hit(s)\n\n");
            foreach (var h in hits)
                sb.Append("- **").Append(h.Item.Name).Append("** (").Append(h.Item.Type)
                  .Append('/').Append(h.Item.Kind).Append(", ").Append(h.Item.Parent)
                  .Append(") — api: ").Append(ApiDetailUrl(h.Item))
                  .Append(" — site: ").Append(SearchService.SiteUrl(h.Item)).Append('\n');
            if (hits.Count == 0)
                sb.Append("No results. Try a broader term, or the query DSL: `/api/v1/syntaxes?q=…`.\n");
            return Markdown(sb.ToString());
        });

        // ---- DSL-filtered syntaxes ---------------------------------------
        api.MapGet("/syntaxes", (ManifestService manifest, HttpContext http,
            string? q, string? entity, int limit = 50) =>
        {
            limit = Math.Clamp(limit, 1, 200);
            var filter = SyntaxFilter.Parse(q);

            var pool = manifest.AllSyntaxes();
            if (!string.IsNullOrWhiteSpace(entity))
                pool = pool.Where(x => x.OwnerId.Equals(entity.Trim(), StringComparison.OrdinalIgnoreCase));

            var matches = pool.Where(x => filter.Matches(x.Syntax)).ToList();
            var page = matches.Take(limit).ToList();

            if (WantsJson(http))
                return Json(manifest, new
                {
                    Total = matches.Count,
                    Returned = page.Count,
                    Syntaxes = page.Select(x => SyntaxJson(x.OwnerId, x.Syntax))
                });

            var sb = ApiMarkdown.Envelope(manifest);
            sb.Append("# Syntaxes");
            if (!string.IsNullOrWhiteSpace(q)) sb.Append(" matching `").Append(q).Append('`');
            if (!string.IsNullOrWhiteSpace(entity)) sb.Append(" on `").Append(entity).Append('`');
            sb.Append(" — ").Append(matches.Count).Append(" match(es)");
            if (matches.Count > page.Count)
                sb.Append(", showing ").Append(page.Count).Append(" (narrow the query or raise `limit`, max 200)");
            sb.Append("\n\n");
            foreach (var x in page)
                ApiMarkdown.SyntaxCompact(sb, x.Syntax, x.OwnerId, withRef: true);
            if (matches.Count == 0)
                sb.Append("No matches. DSL reference: `/api/v1` → Query DSL.\n");
            else
                sb.Append("\nFull detail per syntax: `/api/v1/resolve?ref=<ref>` (encode `#` as `%23`).\n");
            return Markdown(sb.ToString());
        });

        // ---- Entities -----------------------------------------------------
        api.MapGet("/entities", (ManifestService manifest, HttpContext http) =>
        {
            var tree = manifest.EntitiesInTreeOrder().ToList();

            if (WantsJson(http))
                return Json(manifest, tree.Select(e => new
                {
                    e.Id,
                    Name = manifest.DisplayName(e),
                    e.ParentId,
                    Blurb = EntityBlurbs.For(e, manifest.DisplayName(e)),
                    SyntaxCount = e.Syntaxes.Count
                }).Append(new
                {
                    Id = "core",
                    Name = manifest.CoreEntity.Name,
                    ParentId = (string?)null,
                    Blurb = "Hand-written core/global syntaxes (bot creation, login, await…).",
                    SyntaxCount = manifest.CoreEntity.Syntaxes.Count
                }));

            var sb = ApiMarkdown.Envelope(manifest);
            sb.Append("# Entities — ").Append(tree.Count).Append(" Discord types\n\n");
            sb.Append("Indentation = type hierarchy (children inherit the parent's syntaxes). ")
              .Append("Detail: `/api/v1/entities/{id}`.\n\n");
            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in tree)
            {
                var d = e.ParentId is not null && depth.TryGetValue(e.ParentId, out var pd) ? pd + 1 : 0;
                depth[e.Id] = d;
                sb.Append(new string(' ', d * 2)).Append("- `").Append(e.Id).Append("` — ")
                  .Append(EntityBlurbs.For(e, manifest.DisplayName(e)))
                  .Append(" (").Append(e.Syntaxes.Count).Append(" syntaxes)\n");
            }
            sb.Append("- `core` — Hand-written core/global syntaxes (bot creation, login, await…). (")
              .Append(manifest.CoreEntity.Syntaxes.Count).Append(" syntaxes)\n");
            return Markdown(sb.ToString());
        });

        api.MapGet("/entities/{id}", (ManifestService manifest, DocsService docs, HttpContext http, string id) =>
        {
            var entity = manifest.GetEntity(id);
            if (entity is null)
                return NotFoundMd(manifest, $"Unknown entity `{id}`. List them: `/api/v1/entities`.");

            if (WantsJson(http))
                return Json(manifest, new
                {
                    entity.Id,
                    Name = manifest.DisplayName(entity),
                    entity.ParentId,
                    entity.JdaType,
                    Blurb = EntityBlurbs.For(entity, manifest.DisplayName(entity)),
                    Children = manifest.Children(entity.Id).Select(c => c.Id),
                    Syntaxes = entity.Syntaxes.Select(s => SyntaxJson(entity.Id, s))
                });

            var sb = ApiMarkdown.Envelope(manifest);
            ApiMarkdown.Entity(sb, entity, manifest, docs);
            return Markdown(sb.ToString());
        });

        // ---- Ref resolution ----------------------------------------------
        api.MapGet("/resolve", (ManifestService manifest, DocsService docs, HttpContext http, string? @ref) =>
        {
            if (string.IsNullOrWhiteSpace(@ref))
                return NotFoundMd(manifest,
                    "Missing `ref`. Usage: `/api/v1/resolve?ref=guild%23name` (encode `#` as `%23`), " +
                    "or a bare syntax id: `/api/v1/resolve?ref=effect-login-bot`. Formats: `/api/v1`.", 400);

            var reference = docs.Resolve(@ref);
            if (!reference.Resolved)
                return NotFoundMd(manifest,
                    $"Unresolved reference `{@ref}`. Formats: `entity`, `entity%23anchor`, `core%23anchor`, " +
                    "`events%23anchor`, or a bare globally-unique syntax id. Try `/api/v1/search?q=…` to find the right one.");

            switch (reference.Kind)
            {
                case AtlasRefKind.Doc:
                    return Results.Redirect($"/api/v1/docs/{reference.DocSlug}");

                case AtlasRefKind.Entity:
                {
                    var entity = manifest.GetEntity(reference.EntityId)!;
                    if (WantsJson(http))
                        return Json(manifest, new
                        {
                            Kind = "entity",
                            entity.Id,
                            Name = manifest.DisplayName(entity),
                            Syntaxes = entity.Syntaxes.Select(s => SyntaxJson(entity.Id, s))
                        });
                    var sb = ApiMarkdown.Envelope(manifest);
                    ApiMarkdown.Entity(sb, entity, manifest, docs);
                    return Markdown(sb.ToString());
                }

                default:
                {
                    var owner = reference.EntityId ?? "core";
                    if (WantsJson(http))
                        return Json(manifest, SyntaxJson(owner, reference.Syntax!));
                    var sb = ApiMarkdown.Envelope(manifest);
                    ApiMarkdown.Syntax(sb, reference.Syntax!, owner, manifest);
                    AppendGuides(sb, docs, reference.Syntax!);
                    return Markdown(sb.ToString());
                }
            }
        });

        // ---- Events -------------------------------------------------------
        api.MapGet("/events", (ManifestService manifest, HttpContext http, string? category) =>
        {
            var events = manifest.Events
                .Where(ev => category is null ||
                             ManifestService.EventCategory(ev).Equals(category.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (WantsJson(http))
                return Json(manifest, events.Select(ev => new
                {
                    Id = ManifestService.SyntaxAnchor("event", ev.Id),
                    ev.Name,
                    Category = ManifestService.EventCategory(ev),
                    ev.Since,
                    Cancellable = ev.Event?.Cancellable ?? false,
                    ev.Patterns
                }));

            var sb = ApiMarkdown.Envelope(manifest);
            sb.Append("# Events — ").Append(events.Count).Append(" event(s)");
            if (category is not null) sb.Append(" in category `").Append(category).Append('`');
            sb.Append("\n\nDetail: `/api/v1/events/{id}`. Filter: `?category=<name>`.\n");
            foreach (var group in events.GroupBy(ManifestService.EventCategory))
            {
                sb.Append("\n## ").Append(group.Key).Append('\n');
                foreach (var ev in group)
                {
                    sb.Append("- **").Append(ev.Name).Append("** — id: `")
                      .Append(ManifestService.SyntaxAnchor("event", ev.Id)).Append('`');
                    if (ev.Event?.Cancellable == true) sb.Append(" (cancellable)");
                    if (ev.Deprecated) sb.Append(" — DEPRECATED");
                    sb.Append('\n');
                }
            }
            if (events.Count == 0)
                sb.Append("\nNo events in that category; drop `category` to list them all.\n");
            return Markdown(sb.ToString());
        });

        api.MapGet("/events/{id}", (ManifestService manifest, DocsService docs, HttpContext http, string id) =>
        {
            var ev = manifest.Events.FirstOrDefault(s =>
                s.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                ManifestService.SyntaxAnchor("event", s.Id).Equals(id, StringComparison.OrdinalIgnoreCase));
            if (ev is null)
                return NotFoundMd(manifest, $"Unknown event `{id}`. List them: `/api/v1/events`.");

            if (WantsJson(http))
                return Json(manifest, SyntaxJson("events", ev));

            var sb = ApiMarkdown.Envelope(manifest);
            ApiMarkdown.Event(sb, ev);
            AppendGuides(sb, docs, ev);
            return Markdown(sb.ToString());
        });

        // ---- Types --------------------------------------------------------
        api.MapGet("/types", (ManifestService manifest, HttpContext http) =>
        {
            var types = manifest.Manifest.Types;

            if (WantsJson(http))
                return Json(manifest, types.Select(t => new { t.Id, t.Name, IsEnum = t.Values.Count > 0, t.Values }));

            var sb = ApiMarkdown.Envelope(manifest);
            sb.Append("# Types — ").Append(types.Count).Append(" type(s)\n\n");
            sb.Append("Detail (enum literals + which syntaxes return it): `/api/v1/types/{id}`.\n\n");
            foreach (var t in types.OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("- `").Append(t.Id).Append("` — ").Append(t.Name);
                if (t.Values.Count > 0) sb.Append(" (enum, ").Append(t.Values.Count).Append(" literals)");
                sb.Append('\n');
            }
            return Markdown(sb.ToString());
        });

        api.MapGet("/types/{id}", (ManifestService manifest, HttpContext http, string id) =>
        {
            var type = manifest.Manifest.Types
                .FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (type is null)
                return NotFoundMd(manifest, $"Unknown type `{id}`. List them: `/api/v1/types`.");

            var returnedBy = manifest.ReturnedBy(type.Id);

            if (WantsJson(http))
                return Json(manifest, new
                {
                    type.Id,
                    type.Name,
                    type.Values,
                    ReturnedBy = returnedBy.Select(r => new { r.Syntax.Name, Source = r.SourceName, Site = r.Href })
                });

            var sb = ApiMarkdown.Envelope(manifest);
            sb.Append("# Type: ").Append(type.Name).Append(" (`").Append(type.Id).Append("`)\n\n");
            if (type.Values.Count > 0)
            {
                sb.Append("Accepted literals (enum):\n");
                foreach (var v in type.Values)
                    sb.Append("- `").Append(v).Append("`\n");
                sb.Append('\n');
            }
            if (returnedBy.Count > 0)
            {
                sb.Append("Returned by ").Append(returnedBy.Count).Append(" syntax(es):\n");
                foreach (var r in returnedBy)
                    sb.Append("- **").Append(r.Syntax.Name).Append("** (").Append(r.SourceName)
                      .Append(") — site: ").Append(r.Href).Append('\n');
            }
            return Markdown(sb.ToString());
        });

        // ---- Docs ---------------------------------------------------------
        api.MapGet("/docs", (ManifestService manifest, DocsService docs) =>
        {
            var sb = ApiMarkdown.Envelope(manifest);
            sb.Append("# Documentation pages\n\n");
            sb.Append("Raw markdown: `/api/v1/docs/{slug}`.\n");
            foreach (var section in docs.Sections)
            {
                sb.Append("\n## ").Append(section.Title).Append('\n');
                foreach (var page in section.Index is { } i ? section.Pages.Prepend(i) : section.Pages)
                {
                    sb.Append("- `").Append(page.Slug).Append("` — ").Append(page.Title);
                    if (!string.IsNullOrEmpty(page.Description)) sb.Append(": ").Append(page.Description);
                    if (page.Tags.Count > 0) sb.Append(" [").AppendJoin(", ", page.Tags).Append(']');
                    sb.Append('\n');
                }
            }
            if (!docs.HasDocs) sb.Append("\nNo documentation pages yet.\n");
            return Markdown(sb.ToString());
        });

        api.MapGet("/docs/{**slug}", (ManifestService manifest, DocsService docs, string slug) =>
        {
            var page = docs.GetPage(slug);
            if (page is null)
                return NotFoundMd(manifest, $"Unknown doc page `{slug}`. List them: `/api/v1/docs`.");

            var sb = ApiMarkdown.Envelope(manifest);
            sb.Append(File.ReadAllText(page.FilePath));
            return Markdown(sb.ToString());
        });

        // ---- AI assistant --------------------------------------------------
        // Mapped outside the group: a paid POST must not inherit the shared
        // ETag/Cache-Control/304 filter, and it has its own stricter policy.
        app.MapPost("/api/v1/ask", async (AskRequest? body, AskService ask, AskGuards guards, AiChatLog chatLog,
            IOptionsMonitor<AiOptions> aiOptions, ManifestService manifest,
            HttpContext http, CancellationToken ct) =>
        {
            var opts = aiOptions.CurrentValue;
            var question = body?.Question?.Trim();
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // The per-minute layer is the HTTP "ask" rate-limiter policy on this endpoint.
            var refusal = guards.TryBegin(question, ip, includeMinuteLayer: false);
            switch (refusal)
            {
                case AskRefusal.Disabled:
                    return NotFoundMd(manifest, "The AI assistant is currently disabled. The rest of the API remains available.", 503);
                case AskRefusal.Budget:
                    return NotFoundMd(manifest, "The AI assistant reached its weekly budget; try again next week. The rest of the API remains available.", 503);
                case AskRefusal.MissingQuestion:
                    return NotFoundMd(manifest, "Missing question. POST JSON: {\"question\": \"...\"}.", 400);
                case AskRefusal.TooLong:
                    return NotFoundMd(manifest, $"Question too long (max {opts.MaxQuestionChars} characters).", 400);
                case AskRefusal.DailyQuota:
                    http.Response.Headers.RetryAfter = "86400";
                    return NotFoundMd(manifest, $"Daily question quota reached ({opts.PerIpPerDay}/day per IP).", 429);
                case AskRefusal.Busy:
                    http.Response.Headers.RetryAfter = "10";
                    return NotFoundMd(manifest, "The assistant is busy; retry in a few seconds.", 429);
            }

            try
            {
                var result = await ask.AskAsync(question!, ct);
                chatLog.Write(new AiChatRecord(
                    DateTime.UtcNow, AiChatLog.HashIp(ip), result.Stats.Model, question!, result.Answer,
                    result.Stats.Rounds, result.Stats.ToolCalls,
                    result.Stats.PromptTokens, result.Stats.CompletionTokens,
                    result.Stats.PromptTokens + result.Stats.CompletionTokens,
                    result.Stats.CostUsd, result.Stats.DurationMs, result.Outcome));

                if (result.Outcome != "ok" || result.Answer is null)
                    return NotFoundMd(manifest, "The assistant could not answer; try again later.", 502);

                var sb = ApiMarkdown.Envelope(manifest);
                sb.Append(result.Answer.TrimEnd()).Append('\n');
                return Markdown(sb.ToString());
            }
            finally
            {
                ask.Exit();
            }
        }).RequireRateLimiting("ask");

        // ---- /llms.txt (outside the group: crawler-facing, not rate limited,
        //      same spirit as /sitemap.xml) --------------------------------
        app.MapGet("/llms.txt", (ManifestService manifest, DocsService docs, HttpContext context) =>
        {
            var m = manifest.Manifest;
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var sb = new StringBuilder();
            sb.Append("# DiSky Atlas\n\n");
            sb.Append("> Syntax reference and documentation for DiSky v").Append(m.DiskyVersion ?? "5")
              .Append(", the Discord addon for Skript (Minecraft): ").Append(m.Entities.Count)
              .Append(" entities, ").Append(m.Entities.Sum(e => e.Syntaxes.Count) + m.Core.Count)
              .Append(" syntaxes, ").Append(m.Events.Count).Append(" events, ")
              .Append(m.Types.Count).Append(" types.\n\n");
            sb.Append("## API\n\n");
            sb.Append("- [API index & instructions](").Append(baseUrl).Append("/api/v1): start here — endpoints, ref formats, query DSL\n");
            sb.Append("- [Search](").Append(baseUrl).Append("/api/v1/search?q=): fuzzy search across everything\n");
            sb.Append("- [Entities](").Append(baseUrl).Append("/api/v1/entities): the Discord type tree\n");
            sb.Append("- [Events](").Append(baseUrl).Append("/api/v1/events): every Discord event\n\n");
            sb.Append("## Documentation\n\n");
            foreach (var section in docs.Sections)
                foreach (var page in section.Index is { } i ? section.Pages.Prepend(i) : section.Pages)
                {
                    sb.Append("- [").Append(page.Title).Append("](").Append(baseUrl)
                      .Append("/api/v1/docs/").Append(page.Slug).Append(')');
                    if (!string.IsNullOrEmpty(page.Description)) sb.Append(": ").Append(page.Description);
                    sb.Append('\n');
                }
            sb.Append("\n## Data\n\n");
            sb.Append("- [Full database, JSON](").Append(baseUrl).Append("/data/atlas.json): the complete manifest (~300 KB)\n");
            return Results.Text(sb.ToString(), "text/markdown", Encoding.UTF8);
        });
    }

    // ---- Helpers -------------------------------------------------------------

    private static IResult Markdown(string content) =>
        Results.Text(content, "text/markdown", Encoding.UTF8);

    private static IResult NotFoundMd(ManifestService manifest, string message, int status = StatusCodes.Status404NotFound) =>
        Results.Text(ApiMarkdown.Envelope(manifest).Append(message).Append('\n').ToString(),
            "text/markdown", Encoding.UTF8, status);

    private static bool WantsJson(HttpContext http) =>
        string.Equals(http.Request.Query["format"], "json", StringComparison.OrdinalIgnoreCase);

    private static IResult Json(ManifestService manifest, object data) =>
        Results.Json(new
        {
            DiskyVersion = manifest.Manifest.DiskyVersion,
            GeneratedAt = manifest.Manifest.GeneratedAt,
            Data = data
        });

    /// <summary>Flat JSON projection of a syntax carrying an explicit owner id.</summary>
    private static object SyntaxJson(string ownerId, SyntaxInfo s) => new
    {
        s.Id,
        EntityId = ownerId,
        Ref = ApiMarkdown.Ref(s, ownerId),
        s.Kind,
        s.Name,
        s.Patterns,
        s.ReturnType,
        s.ReturnList,
        s.ChangeModes,
        s.Async,
        s.Shared,
        s.WriteOnly,
        s.Since,
        s.Description,
        s.Examples,
        s.RequiredIntents,
        s.Deprecated,
        s.DeprecatedReason,
        s.Event
    };

    /// <summary>API detail URL for a search hit.</summary>
    private static string ApiDetailUrl(SearchItem item) => item switch
    {
        { Type: "doc" } => $"/api/v1/docs/{item.EntityId}",
        { Type: "event" } => $"/api/v1/events/{item.Anchor}",
        { Type: "syntax" } => $"/api/v1/resolve?ref={item.EntityId}%23{item.Anchor}",
        _ => $"/api/v1/entities/{item.EntityId}"
    };

    private static void AppendGuides(StringBuilder sb, DocsService docs, SyntaxInfo syntax)
    {
        var pages = docs.GuidesFor(syntax.Id).Concat(docs.MentionsOf(syntax.Id))
            .DistinctBy(g => g.Page.Slug).ToList();
        if (pages.Count == 0) return;
        sb.Append("\nGuides mentioning this syntax:\n");
        foreach (var g in pages)
            sb.Append("- ").Append(g.Page.Title).Append(" — /api/v1/docs/").Append(g.Page.Slug).Append('\n');
    }

    /// <summary>POST body of /api/v1/ask.</summary>
    public sealed record AskRequest(string? Question);

    /// <summary>One ETag for the whole API: atlas.json build + docs snapshot stamp.</summary>
    private static string ComputeETag(ManifestService manifest, DocsService docs)
    {
        var raw = $"{manifest.Manifest.DiskyVersion}|{manifest.Manifest.GeneratedAt}|{docs.ContentStampUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"\"{Convert.ToHexString(hash.AsSpan(0, 8))}\"";
    }
}
