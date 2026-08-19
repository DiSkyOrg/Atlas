using DiSkyAtlas.Models;

namespace DiSkyAtlas.Services.Docs;

/// <summary>
/// Resolves doc-side references to atlas elements. The canonical format mirrors the site's
/// URLs — an author copies a deep link and pastes it without the origin:
/// <list type="bullet">
/// <item><c>guild#emotes</c> — a syntax on an entity page</item>
/// <item><c>core/effects#await</c> (or <c>core#await</c>) — a Core/Global syntax</item>
/// <item><c>events#guild-join</c> — an event</item>
/// <item><c>guild</c> — an entity page itself</item>
/// </list>
/// An optional scheme (<c>syntax:</c>, <c>entity:</c>, <c>event:</c>, <c>doc:</c>) may prefix the
/// path (inline links always carry one). Never throws — unresolved refs come back with a null
/// <see cref="AtlasRef.Href"/> and the caller emits a lint warning.
/// </summary>
public sealed class AtlasRefResolver(ManifestService manifest)
{
    public AtlasRef Resolve(string raw)
    {
        var path = raw.Trim();

        // Optional scheme.
        string? scheme = null;
        var colon = path.IndexOf(':');
        if (colon > 0 && path[..colon] is "syntax" or "entity" or "event" or "doc")
        {
            scheme = path[..colon];
            path = path[(colon + 1)..].Trim();
        }

        path = path.TrimStart('/');

        if (scheme == "doc")
            return new AtlasRef(AtlasRefKind.Doc, raw, Href: null, DocSlug: path.TrimEnd('/'));

        var hash = path.IndexOf('#');
        var location = hash >= 0 ? path[..hash] : path;
        var anchor = hash >= 0 ? path[(hash + 1)..] : null;

        if (string.IsNullOrEmpty(anchor))
        {
            // No anchor → an entity page reference ("guild").
            var entity = manifest.GetEntity(location);
            return entity is null
                ? new AtlasRef(AtlasRefKind.Entity, raw, Href: null)
                : new AtlasRef(AtlasRefKind.Entity, raw, $"/{entity.Id}", EntityId: entity.Id);
        }

        // events#anchor
        if (location.Equals("events", StringComparison.OrdinalIgnoreCase))
        {
            var ev = manifest.Events.FirstOrDefault(s => AnchorMatches("event", s, anchor));
            return ev is null
                ? new AtlasRef(AtlasRefKind.Event, raw, Href: null)
                : new AtlasRef(AtlasRefKind.Event, raw, $"/events#{ManifestService.SyntaxAnchor("event", ev.Id)}", ev, EntityId: "events");
        }

        // core#anchor / core/<kindPlural>#anchor
        if (location.Equals("core", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("core/", StringComparison.OrdinalIgnoreCase))
        {
            var kindSlug = location.Length > 5 ? location[5..] : null;
            var core = manifest.Manifest.Core.FirstOrDefault(s =>
                AnchorMatches("core", s, anchor) &&
                (kindSlug is null || ManifestService.KindPlural(s.Kind).Equals(kindSlug, StringComparison.OrdinalIgnoreCase)));
            return core is null
                ? new AtlasRef(AtlasRefKind.Syntax, raw, Href: null)
                : new AtlasRef(AtlasRefKind.Syntax, raw,
                    $"/core/{ManifestService.KindPlural(core.Kind)}#{ManifestService.SyntaxAnchor("core", core.Id)}",
                    core, EntityId: "core");
        }

        // <entityId>#anchor
        if (manifest.GetEntity(location) is { } owner)
        {
            var syntax = owner.Syntaxes.FirstOrDefault(s => AnchorMatches(owner.Id, s, anchor));
            if (syntax is not null)
                return new AtlasRef(AtlasRefKind.Syntax, raw,
                    $"/{owner.Id}#{ManifestService.SyntaxAnchor(owner.Id, syntax.Id)}", syntax, owner.Id);
        }

        return ResolveByRawId(raw, anchor) ?? new AtlasRef(AtlasRefKind.Syntax, raw, Href: null);
    }

    /// <summary>Last-resort scan: the anchor (or whole path) is a raw, globally unique syntax id.</summary>
    private AtlasRef? ResolveByRawId(string raw, string id)
    {
        foreach (var entity in manifest.Manifest.Entities)
            foreach (var s in entity.Syntaxes)
                if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    return new AtlasRef(AtlasRefKind.Syntax, raw,
                        $"/{entity.Id}#{ManifestService.SyntaxAnchor(entity.Id, s.Id)}", s, entity.Id);

        foreach (var s in manifest.Manifest.Core)
            if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return new AtlasRef(AtlasRefKind.Syntax, raw,
                    $"/core/{ManifestService.KindPlural(s.Kind)}#{ManifestService.SyntaxAnchor("core", s.Id)}", s, "core");

        foreach (var s in manifest.Manifest.Events)
            if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return new AtlasRef(AtlasRefKind.Event, raw,
                    $"/events#{ManifestService.SyntaxAnchor("event", s.Id)}", s, "events");

        return null;
    }

    private static bool AnchorMatches(string entityId, SyntaxInfo syntax, string anchor) =>
        ManifestService.SyntaxAnchor(entityId, syntax.Id).Equals(anchor, StringComparison.OrdinalIgnoreCase);
}
