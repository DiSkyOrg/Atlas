using DiSkyAtlas.Models;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace DiSkyAtlas.Services.Docs;

/// <summary>
/// Loads the hand-written markdown pages from the Docs/ folder (folder = section, file = page,
/// index.md = section metadata + landing page) and exposes them the same way ManifestService
/// exposes atlas.json: parsed once into an immutable snapshot with all indexes precomputed
/// (nav tree, search rows, atlas → docs reverse links, lint warnings).
/// In Development the snapshot transparently rebuilds when a file changes (throttled stat scan),
/// so editing a page is just save + refresh.
/// </summary>
public sealed class DocsService
{
    private readonly ManifestService _manifest;
    private readonly DocComponentRegistry _registry;
    private readonly ILogger<DocsService> _logger;
    private readonly MarkdownPipeline _pipeline;
    private readonly string _root;
    private readonly bool _devReload;
    private readonly object _sync = new();
    private volatile Snapshot _snapshot;
    private long _lastCheck;

    public DocsService(IWebHostEnvironment env, ManifestService manifest, DocComponentRegistry registry, ILogger<DocsService> logger)
    {
        _manifest = manifest;
        _registry = registry;
        _logger = logger;
        _root = Path.Combine(env.ContentRootPath, "Docs");
        _pipeline = DocPipeline.Build(registry.DirectiveGrammar);
        _devReload = env.IsDevelopment();
        _snapshot = Load();
        LogSummary(_snapshot);
    }

    // ---- Public API --------------------------------------------------------

    public bool HasDocs => Current.Sections.Count > 0;

    public IReadOnlyList<DocSection> Sections => Current.Sections;

    public DocSection? GetSection(string id) =>
        Current.Sections.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public DocPage? GetPage(string? slug) =>
        slug is not null && Current.PagesBySlug.TryGetValue(slug.Trim('/'), out var page) ? page : null;

    /// <summary>Search rows for the ⌘K palette (Type "doc"; EntityId carries the slug).</summary>
    public IReadOnlyList<SearchItem> SearchIndex => Current.SearchIndex;

    /// <summary>Docs that declare this syntax in their frontmatter; the "read this guide" card.</summary>
    public IReadOnlyList<DocRefEntry> GuidesFor(string syntaxId) =>
        Current.Guides.TryGetValue(syntaxId, out var list) ? list : [];

    /// <summary>Docs whose body references this syntax (cards or inline links), deep-linked near the mention.</summary>
    public IReadOnlyList<DocRefEntry> MentionsOf(string syntaxId) =>
        Current.Mentions.TryGetValue(syntaxId, out var list) ? list : [];

    /// <summary>Docs that reference this entity or any of its syntaxes; the entity page's "Documented in" section.</summary>
    public IReadOnlyList<DocRefEntry> DocsForEntity(string entityId) =>
        Current.DocsByEntity.TryGetValue(entityId, out var list) ? list : [];

    /// <summary>Neighbours of a page in reading order (sections in order, index page first in each).</summary>
    public (DocPage? Prev, DocPage? Next) PrevNext(DocPage page)
    {
        var sequence = Current.NavSequence;
        for (var i = 0; i < sequence.Count; i++)
        {
            if (!ReferenceEquals(sequence[i], page)) continue;
            return (i > 0 ? sequence[i - 1] : null, i < sequence.Count - 1 ? sequence[i + 1] : null);
        }
        return (null, null);
    }

    public IReadOnlyList<DocLintWarning> LintWarnings => Current.Warnings;

    /// <summary>UTC stamp of the current snapshot (newest .md write time); cache validator for the API.</summary>
    public DateTime ContentStampUtc => Current.StampUtc;

    /// <summary>Resolves an atlas/doc reference (used by components that receive a raw ref).</summary>
    public AtlasRef Resolve(string raw)
    {
        var reference = new AtlasRefResolver(_manifest).Resolve(raw);
        if (reference.Kind == AtlasRefKind.Doc && reference.DocSlug is { } slug && GetPage(slug) is not null)
            reference = reference with { Href = $"/docs/{slug}" };
        return reference;
    }

    // ---- Snapshot ----------------------------------------------------------

    private sealed class Snapshot
    {
        public IReadOnlyList<DocSection> Sections = [];
        public Dictionary<string, DocPage> PagesBySlug = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<DocPage> NavSequence = [];
        public IReadOnlyList<SearchItem> SearchIndex = [];
        public Dictionary<string, List<DocRefEntry>> Guides = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<DocRefEntry>> Mentions = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<DocRefEntry>> DocsByEntity = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<DocLintWarning> Warnings = [];
        public DateTime StampUtc;
        public int FileCount;
    }

    private Snapshot Current
    {
        get
        {
            MaybeReload();
            return _snapshot;
        }
    }

    private void MaybeReload()
    {
        if (!_devReload) return;

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastCheck) < 1000) return;

        lock (_sync)
        {
            if (now - Interlocked.Read(ref _lastCheck) < 1000) return;
            Interlocked.Exchange(ref _lastCheck, now);

            var (stamp, count) = Stamp();
            if (stamp == _snapshot.StampUtc && count == _snapshot.FileCount) return;

            _snapshot = Load();
            LogSummary(_snapshot);
        }
    }

    private (DateTime Stamp, int Count) Stamp()
    {
        if (!Directory.Exists(_root)) return (DateTime.MinValue, 0);
        var stamp = DateTime.MinValue;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "*.md", SearchOption.AllDirectories))
        {
            count++;
            var write = File.GetLastWriteTimeUtc(file);
            if (write > stamp) stamp = write;
        }
        return (stamp, count);
    }

    private void LogSummary(Snapshot snapshot)
    {
        _logger.LogInformation("Docs: {Sections} section(s), {Pages} page(s), {Warnings} lint warning(s)",
            snapshot.Sections.Count, snapshot.PagesBySlug.Count, snapshot.Warnings.Count);
        foreach (var warning in snapshot.Warnings)
            _logger.LogWarning("Docs lint [{Slug}]: {Message}", warning.PageSlug, warning.Message);
    }

    // ---- Loading -----------------------------------------------------------

    private Snapshot Load()
    {
        var snapshot = new Snapshot();
        var warnings = new List<DocLintWarning>();
        (snapshot.StampUtc, snapshot.FileCount) = Stamp();

        if (!Directory.Exists(_root))
        {
            snapshot.Warnings = warnings;
            return snapshot;
        }

        var resolver = new AtlasRefResolver(_manifest);
        var pages = new List<DocPage>();

        foreach (var file in Directory.EnumerateFiles(_root, "*.md", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var page = ParsePage(file, resolver, warnings);
            if (page is null) continue;

            if (snapshot.PagesBySlug.TryAdd(page.Slug, page))
                pages.Add(page);
            else
                warnings.Add(new DocLintWarning(page.Slug, file, $"duplicate slug “{page.Slug}”; page ignored"));
        }

        // Second pass: needs the full slug map (doc: refs, relative links).
        var mentionsByPage = new Dictionary<DocPage, List<DocIndexer.Mention>>();
        foreach (var page in pages)
        {
            mentionsByPage[page] = DocIndexer.Index(page, _root, resolver, snapshot.PagesBySlug, _registry,
                message => warnings.Add(new DocLintWarning(page.Slug, page.FilePath, message)));
            page.SearchText = string.Join(' ',
                new[] { page.Title, page.Slug, string.Join(' ', page.Tags), page.Description ?? "" }
                    .Concat(page.Headings.Select(h => h.Text)));
        }

        snapshot.Sections = BuildSections(pages);
        snapshot.NavSequence = snapshot.Sections
            .SelectMany(s => s.Index is null ? s.Pages.AsEnumerable() : s.Pages.Prepend(s.Index))
            .ToList();

        snapshot.SearchIndex = snapshot.NavSequence
            .Select(p => new SearchItem(
                Type: "doc",
                EntityId: p.Slug,
                SyntaxId: null,
                Anchor: null,
                Name: p.Title,
                Kind: "doc",
                Parent: SectionTitle(snapshot.Sections, p.SectionId),
                Haystack: p.SearchText.ToLowerInvariant()))
            .ToList();

        BuildReverseIndexes(snapshot, mentionsByPage);

        snapshot.Warnings = warnings;
        return snapshot;
    }

    private DocPage? ParsePage(string file, AtlasRefResolver resolver, List<DocLintWarning> warnings)
    {
        var rel = Path.GetRelativePath(_root, file).Replace('\\', '/');
        var segments = rel.Split('/');

        if (segments.Length == 1)
        {
            warnings.Add(new DocLintWarning(rel, file,
                "root-level pages are ignored; put pages inside a section folder (Docs/<section>/<page>.md)"));
            return null;
        }

        var sectionId = DocIndexer.Slug(segments[0]);
        var fileName = Path.GetFileNameWithoutExtension(segments[^1]);
        var isSectionIndex = segments.Length == 2 && fileName.Equals("index", StringComparison.OrdinalIgnoreCase);

        var slugSegments = new List<string> { sectionId };
        slugSegments.AddRange(segments[1..^1].Select(DocIndexer.Slug));
        if (!fileName.Equals("index", StringComparison.OrdinalIgnoreCase))
            slugSegments.Add(DocIndexer.Slug(fileName));
        var slug = string.Join('/', slugSegments);

        void Warn(string message) => warnings.Add(new DocLintWarning(slug, file, message));

        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(File.ReadAllText(file), _pipeline);
        }
        catch (Exception e)
        {
            Warn($"failed to parse markdown: {e.Message}");
            return null;
        }

        var meta = ReadFrontmatter(document, Warn);

        var title = DocFrontmatter.GetString(meta, "title");
        if (title is null)
        {
            title = Humanize(fileName.Equals("index", StringComparison.OrdinalIgnoreCase) ? segments[0] : fileName);
            Warn($"frontmatter has no “title:”; using “{title}”");
        }

        var syntaxRefs = new List<AtlasRef>();
        foreach (var raw in DocFrontmatter.GetList(meta, "syntaxes"))
        {
            var reference = resolver.Resolve(raw);
            if (reference.Kind == AtlasRefKind.Doc)
            {
                Warn($"frontmatter syntaxes: “{raw}” is a doc ref; only atlas refs belong here");
                continue;
            }
            if (!reference.Resolved)
                Warn($"frontmatter syntaxes: unresolved reference “{raw}”");
            syntaxRefs.Add(reference);
        }

        return new DocPage
        {
            Slug = slug,
            SectionId = sectionId,
            FilePath = file,
            Title = title,
            Icon = DocFrontmatter.GetString(meta, "icon"),
            Description = DocFrontmatter.GetString(meta, "description"),
            Tags = DocFrontmatter.GetList(meta, "tags"),
            Syntaxes = syntaxRefs,
            Order = DocFrontmatter.GetInt(meta, "order", 1000),
            Hidden = DocFrontmatter.GetBool(meta, "hidden"),
            Document = document,
            IsIndex = isSectionIndex
        };
    }

    private static Dictionary<string, object> ReadFrontmatter(MarkdownDocument document, Action<string> warn)
    {
        var block = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (block is null)
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var lines = new List<string>(block.Lines.Count);
        for (var i = 0; i < block.Lines.Count; i++)
            lines.Add(block.Lines.Lines[i].Slice.ToString());

        return DocFrontmatter.Parse(lines, warn);
    }

    private static IReadOnlyList<DocSection> BuildSections(List<DocPage> pages) =>
        pages
            .GroupBy(p => p.SectionId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var index = group.FirstOrDefault(p => p.IsIndex);
                var visible = group
                    .Where(p => !p.IsIndex && !p.Hidden)
                    .OrderBy(p => p.Order)
                    .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new DocSection(
                    group.Key,
                    index?.Title ?? Humanize(group.Key),
                    index?.Icon,
                    index?.Order ?? 1000,
                    index,
                    visible);
            })
            .Where(s => s.Index is not null || s.Pages.Count > 0)
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void BuildReverseIndexes(Snapshot snapshot, Dictionary<DocPage, List<DocIndexer.Mention>> mentionsByPage)
    {
        static void Add(Dictionary<string, List<DocRefEntry>> map, string key, DocRefEntry entry)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = [];
            if (list.All(e => !ReferenceEquals(e.Page, entry.Page)))
                list.Add(entry);
        }

        // Iterate in reading order so backlink lists come out ordered too.
        foreach (var page in snapshot.NavSequence)
        {
            foreach (var reference in page.Syntaxes)
            {
                if (reference.Syntax is not null)
                    Add(snapshot.Guides, reference.Syntax.Id, new DocRefEntry(page, true, null));
                if (reference.EntityId is { } entityId && entityId != "events")
                    Add(snapshot.DocsByEntity, entityId, new DocRefEntry(page, true, null));
            }

            if (!mentionsByPage.TryGetValue(page, out var mentions)) continue;
            foreach (var mention in mentions)
            {
                var reference = mention.Ref;
                if (reference.Syntax is not null &&
                    !(snapshot.Guides.TryGetValue(reference.Syntax.Id, out var guides) &&
                      guides.Any(g => ReferenceEquals(g.Page, page))))
                    Add(snapshot.Mentions, reference.Syntax.Id, new DocRefEntry(page, false, mention.NearestHeadingAnchor));
                if (reference.EntityId is { } entityId && entityId != "events")
                    Add(snapshot.DocsByEntity, entityId, new DocRefEntry(page, false, mention.NearestHeadingAnchor));
            }
        }
    }

    private static string SectionTitle(IReadOnlyList<DocSection> sections, string sectionId) =>
        sections.FirstOrDefault(s => s.Id.Equals(sectionId, StringComparison.OrdinalIgnoreCase))?.Title
        ?? Humanize(sectionId);

    /// <summary>"writing-pages" → "Writing pages".</summary>
    private static string Humanize(string value)
    {
        var words = value.Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? value : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
