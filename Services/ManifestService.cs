using System.Text.Json;
using DiSkyAtlas.Models;

namespace DiSkyAtlas.Services;

/// <summary>
/// Loads atlas.json once and exposes the manifest as an entity tree + lookups + a flat
/// search index. Registered as a singleton; the data is static and read-only.
/// </summary>
public sealed class ManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Kind display order on an entity / core page. (Events are rendered on their own page.)
    private static readonly SyntaxKind[] KindOrder =
        [SyntaxKind.Property, SyntaxKind.Getter, SyntaxKind.Effect, SyntaxKind.Condition, SyntaxKind.Expression,
         SyntaxKind.Section, SyntaxKind.Structure, SyntaxKind.Event, SyntaxKind.Type];

    // Curated ordering for the root entities; everything else falls back to alphabetical.
    private static readonly Dictionary<string, int> RootPriority = new()
    {
        ["bot"] = 0, ["guild"] = 1, ["member"] = 2, ["user"] = 3,
        ["role"] = 4, ["message"] = 5, ["channel"] = 6
    };

    public SyntaxManifest Manifest { get; }

    /// <summary>Synthetic entity gathering the hand-written Core/Global syntaxes (manifest.core).</summary>
    public EntityInfo CoreEntity { get; }

    public bool HasCore => CoreEntity.Syntaxes.Count > 0;

    /// <summary>The highest <c>since</c> version found across all syntaxes (latest documented).</summary>
    public string? LatestVersion { get; }

    private readonly Dictionary<string, EntityInfo> _byId;
    private readonly Dictionary<string, List<EntityInfo>> _childrenByParent;
    private readonly List<EntityInfo> _roots;
    private readonly Dictionary<string, string> _displayNames;
    private readonly Dictionary<string, List<EntityInfo>> _sharedByName;
    private readonly Dictionary<string, List<ReturnedByEntry>> _returnedBy;

    public IReadOnlyList<SearchItem> SearchIndex { get; }

    public ManifestService(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.WebRootPath, "data", "atlas.json");
        using var stream = File.OpenRead(path);
        Manifest = JsonSerializer.Deserialize<SyntaxManifest>(stream, JsonOptions)
                   ?? throw new InvalidOperationException($"Could not parse manifest at {path}.");

        CoreEntity = new EntityInfo
        {
            Id = "core",
            Name = "Core / Global",
            Syntaxes = Manifest.Core
        };

        _byId = Manifest.Entities.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        _byId[CoreEntity.Id] = CoreEntity;

        _displayNames = Manifest.Entities.ToDictionary(
            e => e.Id,
            e => DeriveDisplayName(e.JdaType, e.Name),
            StringComparer.OrdinalIgnoreCase);
        _displayNames[CoreEntity.Id] = CoreEntity.Name;

        _childrenByParent = Manifest.Entities
            .Where(e => e.ParentId is not null)
            .GroupBy(e => e.ParentId!)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        _roots = Manifest.Entities
            .Where(e => e.ParentId is null)
            .OrderBy(e => RootPriority.TryGetValue(e.Id, out var p) ? p : 100)
            .ThenBy(DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Shared syntaxes (e.g. "name", "jump url") carry the same name across entities.
        // Group them once so the "shared" chip can list everywhere a property appears.
        _sharedByName = Manifest.Entities
            .SelectMany(e => e.Syntaxes.Where(s => s.Shared).Select(s => (s.Name, Entity: e)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Entity)
                      .DistinctBy(e => e.Id)
                      .OrderBy(RootDepth)
                      .ThenBy(DisplayName, StringComparer.OrdinalIgnoreCase)
                      .ToList(),
                StringComparer.OrdinalIgnoreCase);

        LatestVersion = Manifest.Entities.SelectMany(e => e.Syntaxes)
            .Concat(Manifest.Core)
            .Concat(Manifest.Events)
            .Select(s => s.Since)
            .Where(s => !string.IsNullOrEmpty(s))
            .OrderByDescending(s => s!, Comparer<string>.Create(CompareVersions))
            .FirstOrDefault();

        _returnedBy = BuildReturnedByIndex();

        SearchIndex = BuildSearchIndex();
    }

    private Dictionary<string, List<ReturnedByEntry>> BuildReturnedByIndex()
    {
        var map = new Dictionary<string, List<ReturnedByEntry>>(StringComparer.OrdinalIgnoreCase);

        void Add(SyntaxInfo s, string sourceName, string href)
        {
            if (s.ReturnType is null) return;
            if (!map.TryGetValue(s.ReturnType.Id, out var list))
                map[s.ReturnType.Id] = list = [];
            list.Add(new ReturnedByEntry(s, sourceName, href));
        }

        foreach (var entity in Manifest.Entities)
            foreach (var syntax in entity.Syntaxes)
                Add(syntax, DisplayName(entity), $"/{entity.Id}#{SyntaxAnchor(entity.Id, syntax.Id)}");

        foreach (var syntax in Manifest.Core)
            Add(syntax, $"Core · {KindLabel(syntax.Kind)}",
                $"/core/{KindPlural(syntax.Kind)}#{SyntaxAnchor("core", syntax.Id)}");

        foreach (var list in map.Values)
            list.Sort((a, b) =>
            {
                var c = string.Compare(a.SourceName, b.SourceName, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Syntax.Name, b.Syntax.Name, StringComparison.OrdinalIgnoreCase);
            });

        return map;
    }

    private int RootDepth(EntityInfo e)
    {
        var depth = 0;
        var current = e.ParentId;
        while (current is not null && _byId.TryGetValue(current, out var parent) && depth < 32)
        {
            depth++;
            current = parent.ParentId;
        }
        return depth;
    }

    // ---- Lookups -----------------------------------------------------------

    public EntityInfo? GetEntity(string? id) =>
        id is not null && _byId.TryGetValue(id, out var e) ? e : null;

    public IReadOnlyList<EntityInfo> Roots => _roots;

    public IReadOnlyList<EntityInfo> Children(string entityId) =>
        _childrenByParent.TryGetValue(entityId, out var c) ? c : [];

    public bool HasChildren(string entityId) => _childrenByParent.ContainsKey(entityId);

    /// <summary>Own syntaxes plus those of every descendant entity.</summary>
    public int TotalSyntaxCount(EntityInfo entity) =>
        entity.Syntaxes.Count + Children(entity.Id).Sum(TotalSyntaxCount);

    /// <summary>Every entity that exposes the shared syntax with this name (incl. self).</summary>
    public IReadOnlyList<EntityInfo> SharedWith(string syntaxName) =>
        _sharedByName.TryGetValue(syntaxName, out var list) ? list : [];

    public string DisplayName(EntityInfo entity) => DisplayName(entity.Id);

    public string DisplayName(string entityId) =>
        _displayNames.TryGetValue(entityId, out var n) ? n : entityId;

    /// <summary>The parent → … → root chain (root first), excluding the entity itself.</summary>
    public IReadOnlyList<EntityInfo> Ancestors(EntityInfo entity)
    {
        var chain = new List<EntityInfo>();
        var current = entity.ParentId;
        var guard = 0;
        while (current is not null && _byId.TryGetValue(current, out var parent) && guard++ < 32)
        {
            chain.Add(parent);
            current = parent.ParentId;
        }
        chain.Reverse();
        return chain;
    }

    public SyntaxInfo? FindSyntax(string entityId, string syntaxId) =>
        GetEntity(entityId)?.Syntaxes.FirstOrDefault(s => s.Id == syntaxId);

    public IReadOnlyList<SyntaxInfo> Events => Manifest.Events;

    /// <summary>A coarse category for grouping events: the first word of the display name.</summary>
    public static string EventCategory(SyntaxInfo ev)
    {
        var space = ev.Name.IndexOf(' ');
        return space > 0 ? ev.Name[..space] : ev.Name;
    }

    // ---- Core / Global syntaxes by kind --------------------------------------

    /// <summary>Kinds present in core[], in display order (Effects, Expressions, …).</summary>
    public IReadOnlyList<SyntaxKind> CoreKinds =>
        KindOrder.Where(k => Manifest.Core.Any(s => s.Kind == k)).ToList();

    public IReadOnlyList<SyntaxInfo> CoreSyntaxes(SyntaxKind kind) =>
        Manifest.Core.Where(s => s.Kind == kind).ToList();

    /// <summary>URL slug for a kind = its lowercased plural label: Effect → "effects".</summary>
    public static string KindPlural(SyntaxKind kind) => KindLabel(kind).ToLowerInvariant();

    public SyntaxKind? ParseCoreKindSlug(string slug)
    {
        foreach (var kind in Enum.GetValues<SyntaxKind>())
            if (string.Equals(KindPlural(kind), slug, StringComparison.OrdinalIgnoreCase))
                return kind;
        return null;
    }

    /// <summary>Syntaxes anywhere in DiSky whose return type is the given type id.</summary>
    public IReadOnlyList<ReturnedByEntry> ReturnedBy(string typeId) =>
        _returnedBy.TryGetValue(typeId, out var list) ? list : [];

    // ---- Grouping ----------------------------------------------------------

    /// <summary>Syntaxes grouped by kind in display order; empty groups omitted.</summary>
    public IReadOnlyList<KindGroup> GroupByKind(IEnumerable<SyntaxInfo> syntaxes)
    {
        var all = syntaxes.ToList();
        return KindOrder
            .Select(k => new KindGroup(k, KindLabel(k), all.Where(s => s.Kind == k).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
    }

    /// <summary>Jump-nav entries for a kind-grouped page (entity / core), one per group, kind-tinted.</summary>
    public static IReadOnlyList<SectionRef> KindSections(IEnumerable<KindGroup> groups) =>
        groups.Select(g => new SectionRef(KindAnchor(g.Kind), g.Label, g.Items.Count, KindSlug(g.Kind))).ToList();

    public static string KindLabel(SyntaxKind kind) => kind switch
    {
        SyntaxKind.Property => "Properties",
        SyntaxKind.Getter => "Getters",
        SyntaxKind.Expression => "Expressions",
        SyntaxKind.Effect => "Effects",
        SyntaxKind.Condition => "Conditions",
        SyntaxKind.Event => "Events",
        SyntaxKind.Section => "Sections",
        SyntaxKind.Structure => "Structures",
        SyntaxKind.Type => "Types",
        _ => kind.ToString()
    };

    /// <summary>Lowercase slug for CSS kind classes (kind-expression, kind-effect, …).</summary>
    public static string KindSlug(SyntaxKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>DOM anchor id for a kind section on a kind-grouped page (e.g. "kind-properties").</summary>
    public static string KindAnchor(SyntaxKind kind) => "kind-" + KindSlug(kind);

    /// <summary>DOM anchor id for an arbitrary category section (e.g. an event category → "cat-guild").</summary>
    public static string CategoryAnchor(string category) => "cat-" + Slugify(category);

    private static string Slugify(string value) =>
        new(value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    /// <summary>A Lucide icon name for an entity, used in the tree, page header and home cards.</summary>
    public static string EntityIcon(EntityInfo entity)
    {
        var id = entity.Id.ToLowerInvariant();
        return id switch
        {
            "bot" => "bot",
            "guild" => "server",
            "member" => "user-round",
            "user" => "user",
            "userprofile" => "id-card",
            "applicationinfo" => "app-window",
            "role" => "shield",
            "rolecolor" => "palette",
            "message" => "message-circle",
            "messagepoll" => "vote",
            "pollanswer" => "list-checks",
            "discorderror" => "triangle-alert",
            "ban" => "ban",
            "automod" => "shield-alert",
            "logentry" => "scroll-text",
            "webhook" => "webhook",
            "invite" => "ticket",
            "attachment" => "paperclip",
            "emote" => "smile",
            "sticker" or "guildsticker" => "sticker",
            "forumtag" => "tag",
            "scheduledevent" => "calendar-clock",
            "activity" => "activity",
            "snowflake" => "snowflake",
            "mentionable" => "at-sign",
            "category" => "folder",
            "forumchannel" => "messages-square",
            "mediachannel" => "image",
            "newschannel" => "megaphone",
            "privatechannel" => "mail",
            "threadchannel" or "threadcontainer" => "message-square",
            "slowmodechannel" => "timer",
            "stagechannel" => "mic",
            "voicechannel" or "audiochannel" => "volume-2",
            "core" => "command",
            _ when id.Contains("channel") => "hash",
            _ => "box"
        };
    }

    /// <summary>A Lucide icon name for a syntax kind (core-section pages, sidebar links).</summary>
    public static string KindIcon(SyntaxKind kind) => kind switch
    {
        SyntaxKind.Property => "tag",
        SyntaxKind.Getter => "scan-search",
        SyntaxKind.Expression => "square-function",
        SyntaxKind.Effect => "wand-sparkles",
        SyntaxKind.Condition => "circle-check",
        SyntaxKind.Event => "zap",
        SyntaxKind.Section => "braces",
        SyntaxKind.Structure => "blocks",
        SyntaxKind.Type => "shapes",
        _ => "box"
    };

    public static string ChangeModeSlug(ChangeMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>
    /// Short, stable anchor for a syntax within its entity page: the syntax id with the
    /// owning entity's id prefix stripped (e.g. "slowmodechannel-slowmode" → "slowmode"),
    /// giving clean deep links like /slowmodechannel#slowmode.
    /// </summary>
    public static string SyntaxAnchor(string entityId, string syntaxId)
    {
        var prefix = entityId + "-";
        return syntaxId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? syntaxId[prefix.Length..]
            : syntaxId;
    }

    /// <summary>Numeric, segment-by-segment version comparison (e.g. "5.10.0" &gt; "5.9.0").</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var x = i < pa.Length && int.TryParse(pa[i], out var xv) ? xv : 0;
            var y = i < pb.Length && int.TryParse(pb[i], out var yv) ? yv : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>
    /// A JDA Javadoc URL for a net.dv8tion.jda.* type, or null for DiSky-internal classes.
    /// Splits the lowercase package path from the (possibly nested) capitalised class name.
    /// </summary>
    public static string? JavadocUrl(string? jdaType)
    {
        if (string.IsNullOrWhiteSpace(jdaType) || !jdaType.StartsWith("net.dv8tion.jda", StringComparison.Ordinal))
            return null;

        var segments = jdaType.Split('.');
        var classStart = Array.FindIndex(segments, s => s.Length > 0 && char.IsUpper(s[0]));
        if (classStart < 0) return null;

        var package = string.Join('/', segments[..classStart]);
        var className = string.Join('.', segments[classStart..]);
        return $"https://docs.jda.wiki/{package}/{className}.html";
    }

    // ---- Search index ------------------------------------------------------

    private List<SearchItem> BuildSearchIndex()
    {
        var index = new List<SearchItem>();

        foreach (var entity in _roots.Concat(Manifest.Entities.Where(e => e.ParentId is not null)))
        {
            var display = DisplayName(entity);
            var parentLabel = entity.ParentId is not null ? DisplayName(entity.ParentId) : "Entities";
            index.Add(new SearchItem(
                Type: "entity",
                EntityId: entity.Id,
                SyntaxId: null,
                Anchor: null,
                Name: display,
                Kind: "type",
                Parent: parentLabel,
                Haystack: $"{display} {entity.CodeName} {entity.Id}"));
        }

        foreach (var entity in _byId.Values)
        {
            var display = DisplayName(entity);
            foreach (var syntax in entity.Syntaxes)
            {
                index.Add(new SearchItem(
                    Type: "syntax",
                    EntityId: entity.Id,
                    SyntaxId: syntax.Id,
                    Anchor: SyntaxAnchor(entity.Id, syntax.Id),
                    Name: syntax.Name,
                    Kind: KindSlug(syntax.Kind),
                    Parent: display,
                    Haystack: $"{syntax.Name} {string.Join(' ', syntax.Patterns)}"));
            }
        }

        foreach (var ev in Manifest.Events)
        {
            index.Add(new SearchItem(
                Type: "event",
                EntityId: "events",
                SyntaxId: ev.Id,
                Anchor: SyntaxAnchor("event", ev.Id),
                Name: ev.Name,
                Kind: "event",
                Parent: "Events",
                Haystack: $"{ev.Name} {string.Join(' ', ev.Patterns)}"));
        }

        return index;
    }

    // ---- Helpers -----------------------------------------------------------

    /// <summary>
    /// Turns a JDA type ("…channel.concrete.TextChannel", "…entities.User.Profile") into a clean
    /// PascalCase display name ("TextChannel", "UserProfile") by joining the trailing capitalised
    /// dot-segments. Falls back to the jar's title-cased name.
    /// </summary>
    private static string DeriveDisplayName(string? jdaType, string fallback)
    {
        if (string.IsNullOrWhiteSpace(jdaType))
            return fallback;

        var segments = jdaType.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var tail = new List<string>();
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var s = segments[i];
            if (s.Length > 0 && char.IsUpper(s[0]))
                tail.Insert(0, s);
            else
                break;
        }

        return tail.Count > 0 ? string.Concat(tail) : fallback;
    }
}

/// <summary>A kind section on an entity page (e.g. "Properties" with its syntaxes).</summary>
public sealed record KindGroup(SyntaxKind Kind, string Label, IReadOnlyList<SyntaxInfo> Items);

/// <summary>
/// A jump target for the on-page section nav (<see cref="DiSkyAtlas.Components.Syntax.SectionJump"/>):
/// a stable DOM anchor + label + count, optionally tinted by a kind slug (null = neutral).
/// </summary>
public sealed record SectionRef(string AnchorId, string Label, int Count, string? KindSlug = null);

/// <summary>A syntax (with a deep link to its source) that returns a given type.</summary>
public sealed record ReturnedByEntry(SyntaxInfo Syntax, string SourceName, string Href);

/// <summary>A flat, searchable row over entities + syntaxes for the ⌘K palette.</summary>
public sealed record SearchItem(
    string Type,
    string EntityId,
    string? SyntaxId,
    string? Anchor,
    string Name,
    string Kind,
    string Parent,
    string Haystack);
