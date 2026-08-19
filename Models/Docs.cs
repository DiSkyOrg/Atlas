using Markdig.Syntax;

namespace DiSkyAtlas.Models;

/// <summary>A heading collected from a doc page (used for the on-page ToC, deep links and search).</summary>
public sealed record DocHeading(int Level, string Text, string AnchorId);

/// <summary>What an atlas/doc reference points at.</summary>
public enum AtlasRefKind
{
    Syntax,
    Entity,
    Event,
    Doc
}

/// <summary>
/// A resolved (or failed) reference from a doc page to an atlas element or another doc page.
/// <see cref="Href"/> is pre-rendered at load time (e.g. "/guild#emotes"); null means unresolved.
/// </summary>
public sealed record AtlasRef(
    AtlasRefKind Kind,
    string Raw,
    string? Href,
    SyntaxInfo? Syntax = null,
    string? EntityId = null,
    string? DocSlug = null)
{
    public bool Resolved => Href is not null;
}

/// <summary>One hand-written markdown page, parsed once at load time.</summary>
public sealed class DocPage
{
    /// <summary>Route slug under /docs, e.g. "contributing/writing-pages". A section's index.md gets the section id itself.</summary>
    public required string Slug { get; init; }

    /// <summary>Owning section id (top-level folder name).</summary>
    public required string SectionId { get; init; }

    /// <summary>Absolute file path, for lint messages.</summary>
    public required string FilePath { get; init; }

    public required string Title { get; init; }

    /// <summary>Lucide icon code name from frontmatter, or null (pages fall back to "book-open").</summary>
    public string? Icon { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Resolved frontmatter <c>syntaxes:</c> refs — these syntaxes show a "read this guide" card.</summary>
    public IReadOnlyList<AtlasRef> Syntaxes { get; set; } = [];

    /// <summary>Sort order within the section (frontmatter <c>order:</c>; unspecified pages sort last, alphabetically).</summary>
    public int Order { get; init; }

    /// <summary>Hidden pages are routable but excluded from nav, prev/next and search.</summary>
    public bool Hidden { get; init; }

    /// <summary>The cached Markdig AST (parsed once; rendered by the DocMarkdown component tree).</summary>
    public required MarkdownDocument Document { get; init; }

    /// <summary>h2/h3 headings in document order.</summary>
    public IReadOnlyList<DocHeading> Headings { get; set; } = [];

    /// <summary>Palette haystack: title + slug + tags + description + heading texts.</summary>
    public string SearchText { get; set; } = "";

    /// <summary>True for a section's index.md.</summary>
    public bool IsIndex { get; init; }
}

/// <summary>A docs section = a top-level folder under Docs/. Metadata comes from its index.md frontmatter.</summary>
public sealed record DocSection(
    string Id,
    string Title,
    string? Icon,
    int Order,
    DocPage? Index,
    IReadOnlyList<DocPage> Pages);

/// <summary>Reverse-index row: a doc page that references a syntax/entity.</summary>
public sealed record DocRefEntry(DocPage Page, bool FromFrontmatter, string? NearestHeadingAnchor);

/// <summary>A non-fatal authoring problem detected while loading the docs (logged + shown in the dev banner).</summary>
public sealed record DocLintWarning(string PageSlug, string FilePath, string Message);
