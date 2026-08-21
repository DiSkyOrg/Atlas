using Markdig;
using Markdig.Extensions.AutoIdentifiers;

namespace DiSkyAtlas.Services.Docs;

/// <summary>
/// The single place the docs' Markdig extension set lives. The block coverage is a deliberately
/// closed set: every enabled construct has a matching arm in the DocBlock/DocInlines renderers,
/// and raw HTML is disabled (it renders as literal text), so nothing ever reaches the page as
/// unescaped markup.
/// </summary>
public static class DocPipeline
{
    public static MarkdownPipeline Build(IReadOnlyDictionary<string, IReadOnlySet<string>> directiveGrammar)
    {
        var builder = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .UsePipeTables()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .UseAutoLinks()
            .UseEmphasisExtras()
            .UseTaskLists()
            .UseDefinitionLists()
            .UseCustomContainers()
            .UseGenericAttributes() // must come last of the built-ins (Markdig requirement)
            .DisableHtml();

        // Custom constructs, tried before the built-in parsers.
        builder.BlockParsers.Insert(0, new AdmonitionParser());
        builder.BlockParsers.Insert(0, new TabItemParser());
        builder.BlockParsers.Insert(0, new DirectiveParser(directiveGrammar));

        return builder.Build();
    }
}
