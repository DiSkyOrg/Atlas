using System.Text;
using DiSkyAtlas.Models;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdigExt = Markdig.Extensions;

namespace DiSkyAtlas.Services.Docs;

/// <summary>
/// Load-time AST passes over a parsed doc page: collects headings (ToC + search + deep links),
/// rewrites cross-reference link URLs in place (<c>syntax:</c>/<c>entity:</c>/<c>event:</c>/<c>doc:</c>
/// schemes and relative <c>*.md</c> paths), attaches resolved refs to directive blocks, validates
/// <c>::: when</c> expressions, and reports every reference so the service can build the
/// reverse (atlas → docs) indexes.
/// </summary>
public static class DocIndexer
{
    /// <summary>Markdig data key under which a rewritten inline link keeps its resolved <see cref="AtlasRef"/>.</summary>
    public static readonly object ResolvedRefKey = new();

    /// <summary>The atlas reference an inline link was rewritten from, or null for ordinary links.</summary>
    public static AtlasRef? GetResolvedRef(LinkInline link) => link.GetData(ResolvedRefKey) as AtlasRef;

    /// <summary>One atlas reference found in a page body, with the nearest preceding h2 anchor.</summary>
    public sealed record Mention(AtlasRef Ref, string? NearestHeadingAnchor);

    public static List<Mention> Index(
        DocPage page,
        string docsRoot,
        AtlasRefResolver resolver,
        IReadOnlyDictionary<string, DocPage> pagesBySlug,
        DocComponentRegistry registry,
        Action<string> warn)
    {
        var mentions = new List<Mention>();
        var headings = new List<DocHeading>();
        string? lastH2 = null;

        var relDir = Path.GetDirectoryName(Path.GetRelativePath(docsRoot, page.FilePath))?.Replace('\\', '/') ?? "";

        foreach (var node in page.Document.Descendants())
        {
            switch (node)
            {
                case HeadingBlock heading:
                {
                    var text = ExtractText(heading.Inline);
                    var anchor = heading.TryGetAttributes()?.Id ?? Slug(text);
                    if (heading.Level is 2 or 3 && text.Length > 0)
                        headings.Add(new DocHeading(heading.Level, text, anchor));
                    if (heading.Level == 2)
                        lastH2 = anchor;
                    break;
                }

                case DirectiveBlock directive:
                    IndexDirective(directive, resolver, pagesBySlug, mentions, lastH2, warn);
                    break;

                case MarkdigExt.CustomContainers.CustomContainer container:
                {
                    var name = container.Info?.Trim() ?? "";
                    if (name.Length == 0)
                        warn("container “:::” without a name");
                    else if (!registry.TryGet(name, out _))
                        warn($"unknown container “::: {name}”");
                    else if (name.Equals("when", StringComparison.OrdinalIgnoreCase) &&
                             !DocCondition.TryParse(container.Arguments ?? "", out _, out var error))
                        warn($"invalid “::: when” condition “{container.Arguments}”: {error}");
                    break;
                }

                case LinkInline link:
                    RewriteLink(link, relDir, resolver, pagesBySlug, mentions, lastH2, warn);
                    break;
            }
        }

        page.Headings = headings;
        return mentions;
    }

    private static void IndexDirective(
        DirectiveBlock directive,
        AtlasRefResolver resolver,
        IReadOnlyDictionary<string, DocPage> pagesBySlug,
        List<Mention> mentions,
        string? lastH2,
        Action<string> warn)
    {
        switch (directive.Name)
        {
            case "syntax" or "entity" or "event":
            {
                var reference = resolver.Resolve($"{directive.Name}:{directive.Argument}");
                directive.ResolvedRef = reference;
                if (reference.Resolved)
                    mentions.Add(new Mention(reference, lastH2));
                else
                    warn($"unresolved reference “{directive.Name}: {directive.Argument}”");
                break;
            }

            case "doc":
            {
                var slug = directive.Argument.Trim('/');
                var found = pagesBySlug.ContainsKey(slug);
                if (!found)
                    warn($"unknown doc page “doc: {directive.Argument}”");
                directive.ResolvedRef = new AtlasRef(AtlasRefKind.Doc, directive.Argument,
                    found ? $"/docs/{slug}" : null, DocSlug: slug);
                break;
            }

            case "toggle":
                if (!directive.Argument.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
                    warn($"toggle id “{directive.Argument}” should only use letters, digits, “-” and “_”");
                break;
        }
    }

    private static void RewriteLink(
        LinkInline link,
        string relDir,
        AtlasRefResolver resolver,
        IReadOnlyDictionary<string, DocPage> pagesBySlug,
        List<Mention> mentions,
        string? lastH2,
        Action<string> warn)
    {
        var url = link.Url;
        if (string.IsNullOrEmpty(url))
            return;

        if (link.IsImage)
        {
            if (!url.StartsWith('/') && !url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("data:", StringComparison.Ordinal))
                warn($"image “{url}” uses a relative path; put doc images in wwwroot/assets/docs/ and reference them as /assets/docs/…");
            return;
        }

        // Atlas schemes → resolved deep link.
        if (url.StartsWith("syntax:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("entity:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
        {
            var reference = resolver.Resolve(url);
            if (reference.Resolved)
            {
                link.Url = reference.Href;
                link.SetData(ResolvedRefKey, reference); // DocInlines renders a hover preview from it
                mentions.Add(new Mention(reference, lastH2));
            }
            else
            {
                warn($"unresolved link “{url}”"); // renderer degrades on the intact scheme
            }
            return;
        }

        // Doc scheme → /docs/… route.
        if (url.StartsWith("doc:", StringComparison.OrdinalIgnoreCase))
        {
            var (slugPart, fragment) = SplitFragment(url[4..].Trim('/'));
            if (!pagesBySlug.ContainsKey(slugPart))
                warn($"unknown doc page “{url}”");
            link.Url = $"/docs/{slugPart}{fragment}";
            return;
        }

        // Relative *.md link → /docs/… route, resolved against this page's folder.
        if (!url.StartsWith('/') && !url.Contains(':', StringComparison.Ordinal))
        {
            var (pathPart, fragment) = SplitFragment(url);
            if (!pathPart.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                return;

            var slug = ResolveRelative(relDir, pathPart);
            if (slug is null || !pagesBySlug.ContainsKey(slug))
                warn($"broken relative link “{url}”");
            link.Url = $"/docs/{slug ?? pathPart}{fragment}";
        }
    }

    /// <summary>Resolves a relative "*.md" path against a page folder into a doc slug, or null when it escapes Docs/.</summary>
    private static string? ResolveRelative(string relDir, string path)
    {
        var segments = new List<string>();
        if (relDir.Length > 0)
            segments.AddRange(relDir.Split('/'));

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    break;
                case "..":
                    if (segments.Count == 0) return null;
                    segments.RemoveAt(segments.Count - 1);
                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        if (segments.Count == 0)
            return null;

        var last = segments[^1];
        if (last.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            last = last[..^3];
        if (last.Equals("index", StringComparison.OrdinalIgnoreCase))
            segments.RemoveAt(segments.Count - 1);
        else
            segments[^1] = last;

        return segments.Count == 0 ? null : string.Join('/', segments.Select(Slug));
    }

    private static (string Path, string Fragment) SplitFragment(string url)
    {
        var i = url.IndexOf('#');
        return i < 0 ? (url, "") : (url[..i], url[i..]);
    }

    /// <summary>Plain text of an inline tree (headings, card titles).</summary>
    public static string ExtractText(ContainerInline? inline)
    {
        if (inline is null) return "";
        var sb = new StringBuilder();
        Append(inline, sb);
        return sb.ToString().Trim();

        static void Append(Inline node, StringBuilder sb)
        {
            switch (node)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case ContainerInline container:
                    for (var child = container.FirstChild; child is not null; child = child.NextSibling)
                        Append(child, sb);
                    break;
            }
        }
    }

    /// <summary>Same slug rules as ManifestService.Slugify (lowercase; non-alphanumerics → "-").</summary>
    public static string Slug(string value) =>
        new(value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
