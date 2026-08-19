using System.Text.RegularExpressions;
using Markdig.Parsers;
using Markdig.Syntax;

namespace DiSkyAtlas.Services.Docs;

/// <summary>
/// One mkdocs-style content tab: <c>=== "Tab title"</c> with a 4-space-indented body.
/// Consecutive tab blocks are grouped into a single tab strip by the renderer.
/// </summary>
public sealed class TabItemBlock(BlockParser parser) : ContainerBlock(parser)
{
    public string Title { get; set; } = "";
}

/// <summary>Block parser for <see cref="TabItemBlock"/>. Only the exact quoted form opens a tab,
/// so plain <c>===</c> setext-heading underlines are unaffected.</summary>
public sealed partial class TabItemParser : BlockParser
{
    [GeneratedRegex("""^===\s+"([^"]+)"\s*$""")]
    private static partial Regex MarkerRegex();

    public TabItemParser() => OpeningCharacters = ['='];

    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent)
            return BlockState.None;

        var match = MarkerRegex().Match(processor.Line.ToString());
        if (!match.Success)
            return BlockState.None;

        processor.NewBlocks.Push(new TabItemBlock(this)
        {
            Column = processor.Column,
            Span = new SourceSpan(processor.Start, processor.Line.End),
            Title = match.Groups[1].Value
        });
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        if (processor.IsBlankLine)
            return BlockState.Continue;

        if (processor.Indent >= 4)
        {
            processor.GoToColumn(processor.ColumnBeforeIndent + 4);
            return BlockState.Continue;
        }

        processor.Close(block);
        return BlockState.None;
    }
}
