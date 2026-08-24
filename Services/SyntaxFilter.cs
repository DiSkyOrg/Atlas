using DiSkyAtlas.Models;

namespace DiSkyAtlas.Services;

/// <summary>
/// A tiny query language for filtering an entity's syntaxes. Supports free-text terms
/// (substring over name + patterns + description, AND-combined) and key:value filters:
///   type:expression   kind:effect
///   since:>5.1.0   since:&gt;=5.0.0   since:&lt;5.2.0   since:5.0.0
///   return:Text   change:set   shared:true   writeonly:true   async:true   intent:guild_members   name:roles
/// All clauses are AND-combined.
/// </summary>
public sealed class SyntaxFilter
{
    public static readonly SyntaxFilter Empty = new([], []);

    private readonly List<string> _freeTerms;
    private readonly List<Func<SyntaxInfo, bool>> _predicates;

    private SyntaxFilter(List<string> freeTerms, List<Func<SyntaxInfo, bool>> predicates)
    {
        _freeTerms = freeTerms;
        _predicates = predicates;
    }

    public bool IsEmpty => _freeTerms.Count == 0 && _predicates.Count == 0;

    public static SyntaxFilter Parse(string? query)
    {
        var freeTerms = new List<string>();
        var predicates = new List<Func<SyntaxInfo, bool>>();

        if (string.IsNullOrWhiteSpace(query))
            return Empty;

        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1)
            {
                freeTerms.Add(token.ToLowerInvariant());
                continue;
            }

            var key = token[..colon].ToLowerInvariant();
            var value = token[(colon + 1)..];
            var predicate = BuildPredicate(key, value);
            if (predicate is not null)
                predicates.Add(predicate);
            else
                freeTerms.Add(token.ToLowerInvariant());
        }

        return freeTerms.Count == 0 && predicates.Count == 0 ? Empty : new SyntaxFilter(freeTerms, predicates);
    }

    public bool Matches(SyntaxInfo syntax)
    {
        foreach (var predicate in _predicates)
            if (!predicate(syntax)) return false;

        if (_freeTerms.Count > 0)
        {
            var haystack = $"{syntax.Name} {string.Join(' ', syntax.Patterns)} {string.Join(' ', syntax.Description)}"
                .ToLowerInvariant();
            foreach (var term in _freeTerms)
                if (!haystack.Contains(term, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static Func<SyntaxInfo, bool>? BuildPredicate(string key, string value)
    {
        var v = value.ToLowerInvariant();
        switch (key)
        {
            case "type":
            case "kind":
                return s => ManifestService.KindSlug(s.Kind).StartsWith(v, StringComparison.Ordinal);

            case "return":
            case "returns":
                return s => s.ReturnType is not null &&
                            s.ReturnType.Name.Contains(value, StringComparison.OrdinalIgnoreCase);

            case "change":
            case "changemode":
                return s => s.ChangeModes.Any(m => ManifestService.ChangeModeSlug(m).StartsWith(v, StringComparison.Ordinal));

            case "shared":
                return s => s.Shared == ParseBool(v);

            case "write":
            case "writeonly":
                return s => s.WriteOnly == (v == "only" || ParseBool(v));

            case "async":
            case "awaitable":
                return s => (s.Async is not null) == ParseBool(v);

            case "deprecated":
                return s => s.Deprecated == ParseBool(v);

            case "intent":
            case "intents":
                return s => s.RequiredIntents
                    .Concat(s.Event?.Intents ?? Enumerable.Empty<string>())
                    .Any(i => i.Contains(value, StringComparison.OrdinalIgnoreCase));

            case "cancellable":
                return s => (s.Event?.Cancellable ?? false) == ParseBool(v);

            case "name":
                return s => s.Name.Contains(value, StringComparison.OrdinalIgnoreCase);

            case "since":
            case "version":
                return BuildSincePredicate(value);

            default:
                return null;
        }
    }

    private static Func<SyntaxInfo, bool> BuildSincePredicate(string value)
    {
        var op = "=";
        var version = value;
        foreach (var candidate in new[] { ">=", "<=", ">", "<", "=" })
        {
            if (value.StartsWith(candidate, StringComparison.Ordinal))
            {
                op = candidate;
                version = value[candidate.Length..];
                break;
            }
        }

        version = version.Trim();
        return s =>
        {
            if (string.IsNullOrEmpty(s.Since)) return false;
            var cmp = ManifestService.CompareVersions(s.Since, version);
            return op switch
            {
                ">" => cmp > 0,
                ">=" => cmp >= 0,
                "<" => cmp < 0,
                "<=" => cmp <= 0,
                _ => cmp == 0
            };
        };
    }

    private static bool ParseBool(string v) => v is "true" or "yes" or "1" or "on";
}
