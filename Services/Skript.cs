using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace DiSkyAtlas.Services;

/// <summary>
/// Pragmatic Skript syntax highlighter: a C# port of the design kit's highlight.js.
/// Emits HTML with .tok-* spans (styled by ds-components.css). Not a full parser; good
/// enough for reference examples. Input is HTML-escaped, so the output is safe to render.
/// </summary>
public static partial class SkriptHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "if", "else", "loop", "while", "stop", "return", "trigger", "command",
        "function", "is", "to", "from", "in", "and", "or", "not", "with", "of", "wait",
        "continue", "set", // note: "set" also an effect; effects win below
        "event-bot", "event-guild", "event-member", "event-user", "event-channel",
        "event-message", "event-string", "event-dropdown", "arg-1", "arg-2"
    };

    private static readonly HashSet<string> Effects = new(StringComparer.OrdinalIgnoreCase)
    {
        "set", "add", "remove", "delete", "reset", "make", "reply", "post", "send",
        "kick", "ban", "edit", "update", "create", "start", "clear", "broadcast",
        "await", "join", "size"
    };

    private static readonly HashSet<string> NumConst = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "now", "none", "green", "red", "blue", "white", "black",
        "orange", "yellow", "online", "idle", "all", "default"
    };

    [GeneratedRegex(@"(""(?:[^""\\]|\\.)*"")|(\{[^}]*\})|(%[^%]*%)|(\b\d+(?:\.\d+)?\b)")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z-]*|[:%{}()]")]
    private static partial Regex WordRegex();

    public static MarkupString Highlight(string code)
    {
        var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(HighlightLine(lines[i]));
        }
        return new MarkupString(sb.ToString());
    }

    private static string HighlightLine(string line)
    {
        // Pull a trailing comment off the end, but only if the '#' is outside a string.
        var comment = "";
        var hash = line.IndexOf('#');
        if (hash != -1)
        {
            var before = line[..hash];
            var quotes = before.Count(c => c == '"');
            if (quotes % 2 == 0)
            {
                comment = line[hash..];
                line = before;
            }
        }

        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in TokenRegex().Matches(line))
        {
            if (m.Index > last)
                sb.Append(HighlightWords(line[last..m.Index]));

            if (m.Groups[1].Success) sb.Append($"<span class=\"tok-str\">{Escape(m.Value)}</span>");
            else if (m.Groups[2].Success) sb.Append($"<span class=\"tok-var\">{Escape(m.Value)}</span>");
            else if (m.Groups[3].Success) sb.Append($"<span class=\"tok-type\">{Escape(m.Value)}</span>");
            else if (m.Groups[4].Success) sb.Append($"<span class=\"tok-num\">{Escape(m.Value)}</span>");

            last = m.Index + m.Length;
        }
        if (last < line.Length)
            sb.Append(HighlightWords(line[last..]));

        if (comment.Length > 0)
            sb.Append($"<span class=\"tok-comment\">{Escape(comment)}</span>");

        return sb.ToString();
    }

    private static string HighlightWords(string text)
    {
        // Escape first; the word regex only wraps known tokens, returning others verbatim,
        // so escaped entities (&lt; &gt; &amp;) pass through untouched.
        var escaped = Escape(text);
        return WordRegex().Replace(escaped, m =>
        {
            var w = m.Value;
            if (w.Length == 1 && ":%{}()".Contains(w[0]))
                return $"<span class=\"tok-punc\">{w}</span>";
            if (NumConst.Contains(w)) return $"<span class=\"tok-num\">{w}</span>";
            if (Effects.Contains(w)) return $"<span class=\"tok-eff\">{w}</span>";
            if (Keywords.Contains(w)) return $"<span class=\"tok-kw\">{w}</span>";
            return w;
        });
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>
/// Tokenises a Skript pattern into plain text, %type% placeholders and [optional] segments
/// so a signature line can render them javadoc-style. Mirrors the design kit's Signature.
/// </summary>
public static partial class PatternTokenizer
{
    public enum TokenKind { Text, Placeholder, Optional }

    public readonly record struct Token(TokenKind Kind, string Value);

    [GeneratedRegex(@"(%[^%]*%)|(\[[^\]]*\])")]
    private static partial Regex PatternRegex();

    public static IReadOnlyList<Token> Tokenize(string pattern)
    {
        var tokens = new List<Token>();
        var last = 0;
        foreach (Match m in PatternRegex().Matches(pattern))
        {
            if (m.Index > last)
                tokens.Add(new Token(TokenKind.Text, pattern[last..m.Index]));

            tokens.Add(m.Groups[1].Success
                ? new Token(TokenKind.Placeholder, m.Value)
                : new Token(TokenKind.Optional, m.Value));

            last = m.Index + m.Length;
        }
        if (last < pattern.Length)
            tokens.Add(new Token(TokenKind.Text, pattern[last..]));

        return tokens;
    }
}
