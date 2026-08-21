using DiSkyAtlas.Models;
using Markdig.Parsers;
using Markdig.Syntax;

namespace DiSkyAtlas.Services.Docs;

/// <summary>
/// A one-line reference directive, e.g. <c>syntax: guild#ban</c> or <c>toggle: use-cache "Use cache"</c>.
/// The whole line must parse as directive grammar (argument + known flags / key:value options /
/// quoted label); any line carrying free prose falls through to a normal paragraph.
/// </summary>
public sealed class DirectiveBlock(BlockParser parser) : LeafBlock(parser)
{
    public string Name { get; set; } = "";
    public string Argument { get; set; } = "";
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set at load time by the doc indexer so the renderer never re-resolves.</summary>
    public AtlasRef? ResolvedRef { get; set; }
}

/// <summary>
/// Parser for <see cref="DirectiveBlock"/>. The accepted directive names and their allowed
/// bare flags come from the component registry, so registering a new directive component
/// automatically teaches the parser its name.
/// </summary>
public sealed class DirectiveParser : BlockParser
{
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _directives;

    public DirectiveParser(IReadOnlyDictionary<string, IReadOnlySet<string>> directives)
    {
        _directives = directives;
        OpeningCharacters = directives.Keys
            .Where(n => n.Length > 0)
            .Select(n => char.ToLowerInvariant(n[0]))
            .Distinct()
            .ToArray();
    }

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
            return BlockState.None;

        var line = processor.Line.ToString();
        var colon = line.IndexOf(':');
        if (colon <= 0)
            return BlockState.None;

        var name = line[..colon];
        if (!_directives.TryGetValue(name, out var allowedFlags))
            return BlockState.None;

        var rest = line[(colon + 1)..].Trim();
        if (rest.Length == 0)
            return BlockState.None;

        if (!TryParseTokens(rest, allowedFlags, out var argument, out var options))
            return BlockState.None;

        var block = new DirectiveBlock(this)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, processor.Line.End),
            Name = name.ToLowerInvariant(),
            Argument = argument
        };
        foreach (var (k, v) in options)
            block.Options[k] = v;

        processor.NewBlocks.Push(block);
        return BlockState.BreakDiscard;
    }

    /// <summary>
    /// Tokenizes the text after "name:". First token is the argument; the rest must each be a
    /// quoted string (stored as "label"), a key:value / key=value pair, or a bare flag from
    /// <paramref name="allowedFlags"/>. Anything else rejects the whole line.
    /// </summary>
    private static bool TryParseTokens(
        string rest, IReadOnlySet<string> allowedFlags,
        out string argument, out List<KeyValuePair<string, string>> options)
    {
        argument = "";
        options = [];

        var tokens = Tokenize(rest);
        if (tokens is null || tokens.Count == 0)
            return false;

        var (first, firstQuoted) = tokens[0];
        if (firstQuoted || first.Length == 0)
            return false;
        argument = first;

        foreach (var (token, quoted) in tokens.Skip(1))
        {
            if (quoted)
            {
                options.Add(new("label", token));
                continue;
            }

            var sep = token.IndexOfAny([':', '=']);
            if (sep > 0 && sep < token.Length - 1)
            {
                options.Add(new(token[..sep], token[(sep + 1)..]));
                continue;
            }

            if (allowedFlags.Contains(token))
            {
                options.Add(new(token, "true"));
                continue;
            }

            return false; // free prose, not a directive line
        }

        return true;
    }

    /// <summary>Splits on whitespace, keeping "quoted strings" as single tokens. Null on unbalanced quotes.</summary>
    private static List<(string Token, bool Quoted)>? Tokenize(string text)
    {
        var tokens = new List<(string, bool)>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }

            if (text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0) return null;
                tokens.Add((text[(i + 1)..end], true));
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                tokens.Add((text[start..i], false));
            }
        }
        return tokens;
    }
}
