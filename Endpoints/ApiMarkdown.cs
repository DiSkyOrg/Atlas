using System.Text;
using DiSkyAtlas.Models;
using DiSkyAtlas.Services;
using DiSkyAtlas.Services.Docs;

namespace DiSkyAtlas.Endpoints;

/// <summary>
/// Markdown renderers for the agent-facing API (<c>/api/v1</c>). Markdown is the default
/// format because the audience is LLM context windows: roughly half the tokens of the
/// equivalent JSON and readable without parsing.
/// </summary>
internal static class ApiMarkdown
{
    /// <summary>The one-line envelope prepended to every markdown response.</summary>
    public static StringBuilder Envelope(ManifestService manifest)
    {
        var m = manifest.Manifest;
        return new StringBuilder()
            .Append("> DiSky Atlas API v1 · DiSky ").Append(m.DiskyVersion ?? "?")
            .Append(" · data generated ").Append(m.GeneratedAt ?? "?")
            .Append(" · index: /api/v1\n\n");
    }

    // ---- Syntaxes ----------------------------------------------------------

    /// <summary>One-line listing entry: name, kind, owner, first pattern, return type.</summary>
    public static void SyntaxCompact(StringBuilder sb, SyntaxInfo s, string ownerId, bool showKind = true, bool withRef = false)
    {
        sb.Append("- **").Append(s.Name).Append("**");
        if (showKind)
            sb.Append(" (").Append(ManifestService.KindSlug(s.Kind)).Append(" on ").Append(ownerId).Append(')');
        if (s.Patterns.Count > 0)
            sb.Append(" — `").Append(s.Patterns[0]).Append('`');
        if (s.ReturnType is not null)
        {
            sb.Append(" → ").Append(s.ReturnType.Name);
            if (s.ReturnList) sb.Append(" (list)");
        }
        if (s.Deprecated) sb.Append(" — DEPRECATED");
        if (withRef)
            sb.Append(" — ref: `").Append(Ref(s, ownerId)).Append('`');
        sb.Append('\n');
    }

    /// <summary>Canonical resolvable ref for a syntax (e.g. "guild#name", "events#guild-join").</summary>
    public static string Ref(SyntaxInfo s, string ownerId) =>
        ownerId == "events"
            ? $"events#{ManifestService.SyntaxAnchor("event", s.Id)}"
            : $"{ownerId}#{ManifestService.SyntaxAnchor(ownerId, s.Id)}";

    /// <summary>Full detail block for a syntax (also used for events via <see cref="Event"/>).</summary>
    public static void Syntax(StringBuilder sb, SyntaxInfo s, string ownerId, ManifestService manifest)
    {
        if (s.Kind == SyntaxKind.Event)
        {
            Event(sb, s);
            return;
        }

        sb.Append("### ").Append(s.Name).Append(" — ").Append(ManifestService.KindSlug(s.Kind))
          .Append(" on ").Append(ownerId).Append("\n\n");
        Patterns(sb, s.Patterns);

        if (s.ReturnType is not null)
        {
            sb.Append("- returns: ").Append(s.ReturnType.Name);
            if (s.ReturnList) sb.Append(" (list)");
            sb.Append(" (type id: `").Append(s.ReturnType.Id).Append("`)\n");
        }
        if (s.WriteOnly)
            sb.Append("- write-only: reading yields <none>; the return type describes what a change accepts\n");
        if (s.ChangeModes.Count > 0)
            sb.Append("- changers: ").AppendJoin(", ", s.ChangeModes.Select(ManifestService.ChangeModeSlug)).Append('\n');
        if (s.Async is { } a)
        {
            var flags = new List<string>();
            if (a.Awaitable) flags.Add("awaitable (prefix with `await`)");
            if (a.RestBacked) flags.Add("rest-backed");
            if (a.RetrieveOnly) flags.Add("retrieve-only");
            if (flags.Count > 0) sb.Append("- async: ").AppendJoin(", ", flags).Append('\n');
        }
        if (s.Shared)
        {
            var everywhere = manifest.SharedWith(s.Name);
            if (everywhere.Count > 0)
                sb.Append("- shared: also on ").AppendJoin(", ", everywhere.Select(e => '`' + e.Id + '`')).Append('\n');
        }
        if (s.RequiredIntents.Count > 0)
            sb.Append("- required intents: ").AppendJoin(", ", s.RequiredIntents).Append('\n');
        if (!string.IsNullOrEmpty(s.Since))
            sb.Append("- since: ").Append(s.Since).Append('\n');
        if (s.Deprecated)
            sb.Append("- DEPRECATED").Append(string.IsNullOrEmpty(s.DeprecatedReason) ? "" : ": " + s.DeprecatedReason).Append('\n');
        sb.Append("- site: ").Append(SiteHref(s, ownerId)).Append('\n');

        Description(sb, s.Description);
        Examples(sb, s.ProcessedExamples);
    }

    /// <summary>Full detail block for an event, including event-values and event expressions.</summary>
    public static void Event(StringBuilder sb, SyntaxInfo ev)
    {
        sb.Append("### ").Append(ev.Name).Append(" — event\n\n");
        Patterns(sb, ev.Patterns);

        var d = ev.Event;
        sb.Append("- cancellable: ").Append(d?.Cancellable == true ? "yes" : "no").Append('\n');
        var intents = (d?.Intents ?? []).Concat(ev.RequiredIntents).Distinct().ToList();
        if (intents.Count > 0)
            sb.Append("- required intents: ").AppendJoin(", ", intents).Append('\n');
        if (!string.IsNullOrEmpty(ev.Since))
            sb.Append("- since: ").Append(ev.Since).Append('\n');
        if (ev.Deprecated)
            sb.Append("- DEPRECATED").Append(string.IsNullOrEmpty(ev.DeprecatedReason) ? "" : ": " + ev.DeprecatedReason).Append('\n');
        sb.Append("- site: /events#").Append(ManifestService.SyntaxAnchor("event", ev.Id)).Append('\n');

        Description(sb, ev.Description);

        if (d is { Values.Count: > 0 })
        {
            sb.Append("\nEvent values (use `event-<name>`):\n");
            foreach (var v in d.Values)
            {
                sb.Append("- `event-").Append(v.Name).Append('`');
                if (v.Type is not null)
                {
                    sb.Append(" → ").Append(v.Type.Name);
                    if (v.List) sb.Append(" (list)");
                }
                if (!string.IsNullOrEmpty(v.Time) && v.Time != "present")
                    sb.Append(" [").Append(v.Time).Append(']');
                sb.Append('\n');
            }
        }

        if (d is { Expressions.Count: > 0 })
        {
            sb.Append("\nEvent-scoped expressions:\n");
            foreach (var e in d.Expressions)
            {
                sb.Append("- `").Append(e.Pattern).Append('`');
                if (e.Type is not null)
                {
                    sb.Append(" → ").Append(e.Type.Name);
                    if (e.List) sb.Append(" (list)");
                }
                sb.Append('\n');
            }
        }

        Examples(sb, ev.ProcessedExamples);
    }

    // ---- Entities ----------------------------------------------------------

    /// <summary>Full entity page: hierarchy, blurb, kind-grouped compact syntax lines, doc backlinks.</summary>
    public static void Entity(StringBuilder sb, EntityInfo entity, ManifestService manifest, DocsService docs)
    {
        var display = manifest.DisplayName(entity);
        sb.Append("# ").Append(display).Append(" — entity `").Append(entity.Id).Append("`\n\n");
        sb.Append("> ").Append(EntityBlurbs.For(entity, display)).Append("\n\n");

        var ancestors = manifest.Ancestors(entity);
        if (ancestors.Count > 0)
            sb.Append("- hierarchy: ").AppendJoin(" > ", ancestors.Select(a => '`' + a.Id + '`')).Append(" > `").Append(entity.Id).Append("`\n");
        var children = manifest.Children(entity.Id);
        if (children.Count > 0)
            sb.Append("- child entities (inherit these syntaxes): ").AppendJoin(", ", children.Select(c => '`' + c.Id + '`')).Append('\n');
        if (!string.IsNullOrEmpty(entity.JdaType))
            sb.Append("- JDA type: ").Append(entity.JdaType).Append('\n');
        sb.Append("- site: /").Append(entity.Id).Append('\n');
        sb.Append("- full detail per syntax: `/api/v1/resolve?ref=").Append(entity.Id).Append("%23<name-anchor>`\n");

        foreach (var group in manifest.GroupByKind(entity.Syntaxes))
        {
            sb.Append("\n## ").Append(group.Label).Append(" (").Append(group.Items.Count).Append(")\n");
            foreach (var s in group.Items)
                SyntaxCompact(sb, s, entity.Id, showKind: false);
        }

        if (entity.Syntaxes.Count == 0)
            sb.Append("\nNo own syntaxes; see the parent entity's syntaxes and child entities.\n");

        var guides = docs.DocsForEntity(entity.Id);
        if (guides.Count > 0)
        {
            sb.Append("\n## Documented in\n");
            foreach (var g in guides)
                sb.Append("- ").Append(g.Page.Title).Append(" — /api/v1/docs/").Append(g.Page.Slug).Append('\n');
        }
    }

    // ---- Shared fragments --------------------------------------------------

    public static void Patterns(StringBuilder sb, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0) return;
        sb.Append("```\n");
        foreach (var p in patterns)
            sb.Append(p).Append('\n');
        sb.Append("```\n\n");
    }

    private static void Description(StringBuilder sb, IReadOnlyList<string> paragraphs)
    {
        if (paragraphs.Count == 0) return;
        sb.Append('\n');
        foreach (var p in paragraphs)
            sb.Append(p).Append('\n');
    }

    private static void Examples(StringBuilder sb, IReadOnlyList<string> examples)
    {
        if (examples.Count == 0) return;
        sb.Append("\nExamples:\n```\n");
        foreach (var e in examples)
            sb.Append(e).Append('\n');
        sb.Append("```\n");
    }

    /// <summary>Site deep link for a non-event syntax (mirrors the entity/core page anchors).</summary>
    public static string SiteHref(SyntaxInfo s, string ownerId) =>
        ownerId == "core"
            ? $"/core/{ManifestService.KindPlural(s.Kind)}#{ManifestService.SyntaxAnchor("core", s.Id)}"
            : $"/{ownerId}#{ManifestService.SyntaxAnchor(ownerId, s.Id)}";

    // ---- Discovery ---------------------------------------------------------

    /// <summary>The self-describing instructions served at /api/v1 — this IS the API documentation.</summary>
    public static string Discovery(ManifestService manifest, DocsService docs, bool askAvailable)
    {
        var m = manifest.Manifest;
        var sb = Envelope(manifest);

        sb.Append("# DiSky Atlas API v1\n\n");
        sb.Append("Read-only reference API for **DiSky v").Append(m.DiskyVersion ?? "5")
          .Append("**, the Discord addon for Skript (Minecraft). It serves the same database as the ")
          .Append("site: ").Append(m.Entities.Count).Append(" entities, ")
          .Append(m.Entities.Sum(e => e.Syntaxes.Count)).Append(" entity syntaxes, ")
          .Append(m.Core.Count).Append(" core/global syntaxes, ")
          .Append(m.Events.Count).Append(" events, ")
          .Append(m.Types.Count).Append(" types, plus the hand-written documentation pages.\n\n");
        sb.Append("All responses are markdown (`text/markdown`). Endpoints marked [json] also accept ")
          .Append("`?format=json` and then return `{ diskyVersion, generatedAt, data }`.\n\n");

        sb.Append("## Endpoints\n\n");
        sb.Append("| Endpoint | Description |\n|---|---|\n");
        sb.Append("| `GET /api/v1` | This document. |\n");
        sb.Append("| `GET /api/v1/search?q=<text>&limit=20` | Fuzzy search across entities, syntaxes, events and doc pages. Best first stop. [json] |\n");
        sb.Append("| `GET /api/v1/syntaxes?q=<query>&entity=<id>&limit=50` | Filter every syntax with the query DSL below. [json] |\n");
        sb.Append("| `GET /api/v1/entities` | Entity tree (Discord types: bot, guild, member, channel…). [json] |\n");
        sb.Append("| `GET /api/v1/entities/{id}` | One entity with all its syntaxes (`core` = core/global syntaxes). [json] |\n");
        sb.Append("| `GET /api/v1/resolve?ref=<ref>` | Full detail for one reference (see formats below). [json] |\n");
        sb.Append("| `GET /api/v1/events?category=<name>` | All events, grouped by category. [json] |\n");
        sb.Append("| `GET /api/v1/events/{id}` | One event: patterns, event-values, intents, examples. [json] |\n");
        sb.Append("| `GET /api/v1/types` | Type catalog; enum types list their accepted literals. [json] |\n");
        sb.Append("| `GET /api/v1/types/{id}` | One type: literals + every syntax returning it. [json] |\n");
        sb.Append("| `GET /api/v1/docs` | Documentation page index. |\n");
        sb.Append("| `GET /api/v1/docs/{slug}` | One documentation page as raw markdown. |\n");
        if (askAvailable)
            sb.Append("| `POST /api/v1/ask` | Ask the AI assistant a DiSky question — body `{\"question\": \"...\"}`. Strictly rate-limited. |\n");
        sb.Append('\n');

        sb.Append("## Reference formats (`/api/v1/resolve?ref=`)\n\n");
        sb.Append("Refs mirror the site's URLs. URL-encode `#` as `%23` in the query string.\n\n");
        sb.Append("- `guild` — an entity\n");
        sb.Append("- `guild%23name` (`guild#name`) — a syntax on an entity, by its page anchor\n");
        sb.Append("- `core%23login` (`core#login`) — a core/global syntax\n");
        sb.Append("- `events%23guild-join` (`events#guild-join`) — an event\n");
        sb.Append("- a bare globally-unique syntax id, no encoding needed (e.g. `effect-login-bot`, `event-bot-ready-event`) — ids appear in this API's responses\n\n");

        sb.Append("## Query DSL (`/api/v1/syntaxes?q=`)\n\n");
        sb.Append("Space-separated clauses, AND-combined. Bare words match name + patterns + description (substring). Filters:\n\n");
        sb.Append("- `kind:` (or `type:`) — property, getter, expression, effect, condition, event, section, structure\n");
        sb.Append("- `return:` — return type name, e.g. `return:member`\n");
        sb.Append("- `change:` — set, add, remove, remove_all, delete, reset\n");
        sb.Append("- `name:` — substring of the syntax name\n");
        sb.Append("- `since:` / `version:` — with comparators: `since:5.0.0`, `since:>=5.1.0`, `since:<5.2.0`\n");
        sb.Append("- `intent:` — required gateway intent, e.g. `intent:guild_members`\n");
        sb.Append("- booleans: `shared:true`, `writeonly:true`, `async:true`, `deprecated:true`, `cancellable:true`\n");
        sb.Append("- plus the separate `&entity=<id>` parameter to scope to one entity (`core`, `events`, or an entity id)\n\n");
        sb.Append("Example: `/api/v1/syntaxes?q=kind:effect+message&limit=10`\n\n");

        sb.Append("## Writing DiSky code (conventions)\n\n");
        sb.Append("- Create bots imperatively: the `a new discord bot` expression followed by the `login` effect. Never use the legacy `define bot` structure.\n");
        sb.Append("- Pair every `login` with a `shutdown`.\n");
        sb.Append("- Patterns use Skript notation: `%type%` is an argument slot, `[...]` optional, `(a|b)` alternatives.\n\n");

        sb.Append("## Documentation pages\n\n");
        sb.Append("`/api/v1/docs/{slug}` returns the page's raw markdown, including its YAML frontmatter ")
          .Append("(title, description, tags, `syntaxes:` refs) and custom doc constructs: `syntax: <ref>` ")
          .Append("directive lines (a live syntax card on the site) and `::: name … :::` containers. ")
          .Append("Any ref you meet can be expanded via `/api/v1/resolve`.\n\n");

        var pages = docs.Sections.SelectMany(s => s.Index is { } i ? s.Pages.Prepend(i) : s.Pages).ToList();
        if (pages.Count > 0)
        {
            sb.Append("Available pages:\n");
            foreach (var p in pages)
            {
                sb.Append("- `").Append(p.Slug).Append("` — ").Append(p.Title);
                if (!string.IsNullOrEmpty(p.Description)) sb.Append(": ").Append(p.Description);
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        sb.Append("## Limits & caching\n\n");
        sb.Append("- Rate limit: 60 requests/minute per IP; beyond that, HTTP 429 with a `Retry-After` header.\n");
        sb.Append("- Responses carry an `ETag` and `Cache-Control: max-age=3600`; send `If-None-Match` to get a cheap 304.\n");
        sb.Append("- Bulk download: the full database as JSON at `/data/atlas.json` (~300 KB). Prefer the API for targeted, token-efficient lookups.\n");

        return sb.ToString();
    }
}
