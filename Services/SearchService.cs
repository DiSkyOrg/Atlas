using DiSkyAtlas.Services.Docs;

namespace DiSkyAtlas.Services;

/// <summary>
/// Fuzzy search over the two flat indexes (atlas + docs), shared by the ⌘K palette and the
/// agent API so both rank identically. A single-word query is typeahead: substring match wins,
/// otherwise subsequence match; a haystack-only hit scores half a name hit. A multi-word query
/// is scored per term with substring matching only — subsequence over a long haystack matches
/// almost anything, which turns "no result" into "one wrong result" — and term coverage
/// dominates the ranking, so the item hitting the most terms wins.
/// </summary>
public sealed class SearchService(ManifestService manifest, DocsService docs)
{
    public IReadOnlyList<ScoredHit> Search(string query, int limit)
    {
        var terms = Tokenize(query);
        if (terms.Length == 0) return [];

        return manifest.SearchIndex.Concat(docs.SearchIndex)
            .Select(r => new ScoredHit(r, Score(terms, r)))
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

    private static readonly char[] Separators =
        [' ', '\t', '\n', '\r', ',', '.', '?', '!', ';', ':', '"', '\'', '’', '(', ')', '[', ']', '{', '}', '/', '\\', '`', '#', '-', '_'];

    // Question words only: never a term that appears in a syntax name or pattern ("create",
    // "new", "send", "message" all stay searchable). Dropping them matters because a bare
    // "a" or "de" substring-matches most of the index.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "any", "are", "be", "can", "did", "do", "does", "for", "from", "how", "i",
        "in", "is", "it", "me", "my", "of", "on", "or", "should", "some", "that", "the", "there",
        "this", "to", "using", "what", "which", "why", "with", "would", "you", "your",
        "au", "aux", "avec", "ce", "cet", "cette", "comment", "dans", "de", "des", "du", "en", "est",
        "et", "faire", "il", "je", "la", "le", "les", "ma", "mes", "mon", "ne", "ou", "par", "pas",
        "peut", "pour", "puis", "que", "quel", "quelle", "qui", "quoi", "sont", "sur", "tu", "un", "une"
    };

    /// <summary>Lowercase terms of a query; stopwords are dropped unless nothing else remains.</summary>
    public static string[] Tokenize(string query)
    {
        var raw = query.ToLowerInvariant()
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (raw.Length <= 1) return raw;

        var kept = raw.Where(t => t.Length > 1 && !Stopwords.Contains(t)).ToArray();
        return kept.Length > 0 ? kept : raw;
    }

    public static int Score(string query, SearchItem item) => Score(Tokenize(query), item);

    public static int Score(string[] terms, SearchItem item)
    {
        // Haystack is already lowercase (built that way); only the display name needs folding.
        var name = item.Name.ToLowerInvariant();

        if (terms.Length == 1)
        {
            var s = Fuzzy(terms[0], name);
            if (s >= 0) return s;
            var h = Fuzzy(terms[0], item.Haystack);
            return h < 0 ? -1 : h / 2;
        }

        int coverage = 0, total = 0;
        foreach (var term in terms)
        {
            // A term found in the name is worth three found deep in a description, otherwise a
            // long description hoards coverage and outranks the syntax actually named after it.
            var weight = 3;
            var s = Substring(term, name);
            if (s < 0)
            {
                var h = Substring(term, item.Haystack);
                (s, weight) = (h < 0 ? -1 : h / 2, 1);
            }
            if (s < 0) continue;
            coverage += weight;
            total += s;
        }
        // Coverage first, position quality as the tiebreak.
        return coverage == 0 ? -1 : coverage * 1000 + total;
    }

    private static int Fuzzy(string query, string text)
    {
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

    private static int Substring(string term, string text)
    {
        var idx = text.IndexOf(term, StringComparison.Ordinal);
        return idx < 0 ? -1 : 100 - Math.Min(idx, 99);
    }
}

/// <summary>A search index row with its score for the current query.</summary>
public sealed record ScoredHit(SearchItem Item, int Score);
