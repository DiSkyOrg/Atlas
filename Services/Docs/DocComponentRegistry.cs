using DiSkyAtlas.Models;
using Markdig.Syntax;

namespace DiSkyAtlas.Services.Docs;

/// <summary>
/// Everything a pluggable doc component receives: the construct name, its argument
/// (directive argument or container info arguments), parsed options, the nested markdown
/// body (null for one-line directives) and the owning page.
/// </summary>
public sealed record DocDirectiveContext(
    string Name,
    string Argument,
    IReadOnlyDictionary<string, string> Options,
    ContainerBlock? Body,
    DocPage Page)
{
    /// <summary>The resolved atlas/doc reference, when the load-time indexer attached one.</summary>
    public AtlasRef? ResolvedRef { get; init; }
}

/// <summary>
/// Maps doc constructs to Blazor components. Two families share one component contract
/// (a single <c>Context</c> parameter of type <see cref="DocDirectiveContext"/>):
/// <list type="bullet">
/// <item>directives — one-line <c>name: argument [options]</c> (also teaches the markdown parser the name),</item>
/// <item>containers — fenced <c>::: name args</c> blocks with a markdown body.</item>
/// </list>
/// Adding a construct = one component + one Register call in Program.cs.
/// </summary>
public sealed class DocComponentRegistry
{
    private readonly Dictionary<string, Type> _components = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlySet<string>> _directives = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a one-line directive (e.g. "syntax"). <paramref name="flags"/> are the
    /// bare option words the line may carry (e.g. "full"); any other bare word makes the line
    /// parse as a normal paragraph instead.</summary>
    public DocComponentRegistry RegisterDirective(string name, Type componentType, params string[] flags)
    {
        _components[name] = componentType;
        _directives[name] = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);
        return this;
    }

    /// <summary>Registers a fenced-container construct (e.g. "steps" for <c>::: steps</c>).</summary>
    public DocComponentRegistry RegisterContainer(string name, Type componentType)
    {
        _components[name] = componentType;
        return this;
    }

    public bool TryGet(string name, out Type componentType) =>
        _components.TryGetValue(name, out componentType!);

    /// <summary>Directive names + allowed flags, consumed by the markdown parser.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> DirectiveGrammar => _directives;
}
