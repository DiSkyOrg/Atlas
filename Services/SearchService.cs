using DiSkyAtlas.Services.Docs;

namespace DiSkyAtlas.Services;

/// <summary>
/// Fuzzy search over the two flat indexes (atlas + docs), shared by the ⌘K palette and the
/// agent API so both rank identically. Substring match wins; otherwise subsequence match;
/// a haystack-only hit scores half a name hit.
/// </summary>
public sealed class SearchService(ManifestService manifest, DocsService docs)
{
    public IReadOnlyList<ScoredHit> Search(string query, int limit)
    {
        query = query.Trim();
        if (query.Length == 0) return [];

        return manifest.SearchIndex.Concat(docs.SearchIndex)
            .Select(r => new ScoredHit(r, Score(query, r)))
            .Where(h => h.Score >= 0)
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList();
    }

    /// <summary>The site page a search hit navigates to (same mapping as the palette).</summary>
    public static string SiteUrl(SearchItem item) => item switch
    {
        { Type: "doc" } => $"/docs/{item.EntityId}",
        { Type: "event" } => $"/events#{item.Anchor}",
        { Type: "syntax", EntityId: "core" } => $"/core/{CorePlural(item.Kind)}#{item.Anchor}",
        { Type: "syntax" } => $"/{item.EntityId}#{item.Anchor}",
        _ => $"/{item.EntityId}"
    };

    public static string CorePlural(string kindSlug) => kindSlug == "property" ? "properties" : kindSlug + "s";

    // Fuzzy score: substring match wins; otherwise subsequence match. Mirrors the design kit.
    public static int Score(string query, SearchItem item)
    {
        var s = Fuzzy(query, item.Name);
        if (s >= 0) return s;
        var h = Fuzzy(query, item.Haystack);
        return h < 0 ? -1 : h / 2;
    }

    private static int Fuzzy(string query, string text)
    {
        query = query.ToLowerInvariant();
        text = text.ToLowerInvariant();
        if (query.Length == 0) return 0;

        var idx = text.IndexOf(query, StringComparison.Ordinal);
        if (idx >= 0) return 100 - Math.Min(idx, 99);

        int qi = 0, score = 0;
        for (var i = 0; i < text.Length && qi < query.Length; i++)
        {
            if (text[i] == query[qi]) { score++; qi++; }
        }
        return qi == query.Length ? score : -1;
    }
}

/// <summary>A search index row with its score for the current query.</summary>
public sealed record ScoredHit(SearchItem Item, int Score);
