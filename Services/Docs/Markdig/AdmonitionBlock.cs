using System.Text.RegularExpressions;
using Markdig.Parsers;
using Markdig.Syntax;

namespace DiSkyAtlas.Services.Docs;

/// <summary>
/// mkdocs-style admonition: <c>!!! kind "Optional title"</c> with a 4-space-indented body.
/// <c>??? kind</c> renders collapsed, <c>???+ kind</c> collapsible but initially open.
/// </summary>
public sealed class AdmonitionBlock(BlockParser parser) : ContainerBlock(parser)
{
    public string Kind { get; set; } = "note";
    public string? Title { get; set; }
    public bool Collapsible { get; set; }
    public bool InitiallyOpen { get; set; }
}

/// <summary>Block parser for <see cref="AdmonitionBlock"/>. Body = lines indented by 4+ columns.</summary>
public sealed partial class AdmonitionParser : BlockParser
{
    [GeneratedRegex("""^(!!!|\?\?\?\+?)\s+([a-zA-Z][\w-]*)(?:\s+"([^"]*)")?\s*$""")]
    private static partial Regex MarkerRegex();

    public AdmonitionParser() => OpeningCharacters = ['!', '?'];

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
            return BlockState.None;

        var match = MarkerRegex().Match(processor.Line.ToString());
        if (!match.Success)
            return BlockState.None;

        var marker = match.Groups[1].Value;
        processor.NewBlocks.Push(new AdmonitionBlock(this)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, processor.Line.End),
            Kind = match.Groups[2].Value.ToLowerInvariant(),
            Title = match.Groups[3].Success ? match.Groups[3].Value : null,
            Collapsible = marker[0] == '?',
            InitiallyOpen = marker is "???+" or "!!!"
        });
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        if (processor.IsBlankLine)
            return BlockState.Continue;

        if (processor.Indent >= 4)
        {
            // Consume exactly 4 columns of indent; deeper indent stays available to child blocks.
            processor.GoToColumn(processor.ColumnBeforeIndent + 4);
            return BlockState.Continue;
        }

        processor.Close(block);
        return BlockState.None;
    }
}
