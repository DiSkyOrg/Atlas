namespace DiSkyAtlas.Services.Docs;

/// <summary>
/// Minimal YAML-subset frontmatter parser. Supports exactly what doc pages need:
/// <c>key: value</c> scalars (optionally quoted), <c>key: [a, b]</c> inline lists and
/// <c>key:</c> + dash-list items. Anything else produces a lint warning, never an exception.
/// </summary>
public static class DocFrontmatter
{
    public static readonly IReadOnlySet<string> KnownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "title", "icon", "description", "tags", "syntaxes", "order", "hidden"
    };

    /// <summary>Parses frontmatter lines into scalars (string) and lists (List&lt;string&gt;).</summary>
    public static Dictionary<string, object> Parse(IEnumerable<string> lines, Action<string> warn)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        string? pendingListKey = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0) continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue; // full-line comment

            // Dash item for the list key opened on a previous line.
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
            {
                if (pendingListKey is null)
                {
                    warn($"frontmatter: dash item “{trimmed}” without a preceding “key:” line");
                    continue;
                }
                var item = Unquote(trimmed.Length > 1 ? trimmed[1..].Trim() : "");
                if (item.Length > 0)
                    ((List<string>)result[pendingListKey]).Add(item);
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                warn($"frontmatter: can't parse line “{trimmed}”");
                pendingListKey = null;
                continue;
            }

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim();

            if (!KnownKeys.Contains(key))
                warn($"frontmatter: unknown key “{key}” (known: {string.Join(", ", KnownKeys)})");

            if (value.Length == 0)
            {
                // "key:" opens a dash list.
                result[key] = new List<string>();
                pendingListKey = key;
            }
            else if (value.StartsWith('[') && value.EndsWith(']'))
            {
                var items = value[1..^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Unquote)
                    .Where(s => s.Length > 0)
                    .ToList();
                result[key] = items;
                pendingListKey = null;
            }
            else
            {
                result[key] = Unquote(value);
                pendingListKey = null;
            }
        }

        return result;
    }

    public static string? GetString(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

    public static IReadOnlyList<string> GetList(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var v) switch
        {
            true when v is List<string> list => list,
            true when v is string s && s.Length > 0 => [s], // a single scalar is a 1-item list
            _ => []
        };

    public static int GetInt(Dictionary<string, object> map, string key, int fallback) =>
        map.TryGetValue(key, out var v) && v is string s && int.TryParse(s, out var i) ? i : fallback;

    public static bool GetBool(Dictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var v) && v is string s &&
        (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
}
