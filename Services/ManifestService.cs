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

    // Kind display order on an entity page, mirroring the design's KIND_ORDER.
    private static readonly SyntaxKind[] KindOrder =
        [SyntaxKind.Event, SyntaxKind.Effect, SyntaxKind.Expression, SyntaxKind.Condition,
         SyntaxKind.Section, SyntaxKind.Structure, SyntaxKind.Type];

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

    private readonly Dictionary<string, EntityInfo> _byId;
    private readonly Dictionary<string, List<EntityInfo>> _childrenByParent;
    private readonly List<EntityInfo> _roots;
    private readonly Dictionary<string, string> _displayNames;

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

        SearchIndex = BuildSearchIndex();
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

    public static string KindLabel(SyntaxKind kind) => kind switch
    {
        SyntaxKind.Event => "Events",
        SyntaxKind.Effect => "Effects",
        SyntaxKind.Expression => "Properties & Expressions",
        SyntaxKind.Condition => "Conditions",
        SyntaxKind.Section => "Sections",
        SyntaxKind.Structure => "Structures",
        SyntaxKind.Type => "Types",
        _ => kind.ToString()
    };

    /// <summary>Lowercase slug for CSS kind classes (kind-expression, kind-effect, …).</summary>
    public static string KindSlug(SyntaxKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>A Lucide icon name for an entity, used in the tree, page header and home cards.</summary>
    public static string EntityIcon(EntityInfo entity)
    {
        var id = entity.Id.ToLowerInvariant();
        return id switch
        {
            "bot" => "bot",
            "guild" => "server",
            "member" => "users",
            "user" => "user",
            "userprofile" => "id-card",
            "role" => "shield",
            "message" => "message-circle",
            "discorderror" => "triangle-alert",
            "category" => "folder",
            "forumchannel" => "messages-square",
            "threadchannel" or "threadcontainer" => "message-square",
            "slowmodechannel" => "timer",
            "stagechannel" => "mic",
            "voicechannel" or "audiochannel" => "volume-2",
            "core" => "command",
            _ when id.Contains("channel") => "hash",
            _ => "box"
        };
    }

    public static string ChangeModeSlug(ChangeMode mode) => mode.ToString().ToLowerInvariant();

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
                    Name: syntax.Name,
                    Kind: KindSlug(syntax.Kind),
                    Parent: display,
                    Haystack: $"{syntax.Name} {string.Join(' ', syntax.Patterns)}"));
            }
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

/// <summary>A kind section on an entity page (e.g. "Properties & Expressions" with its syntaxes).</summary>
public sealed record KindGroup(SyntaxKind Kind, string Label, IReadOnlyList<SyntaxInfo> Items);

/// <summary>A flat, searchable row over entities + syntaxes for the ⌘K palette.</summary>
public sealed record SearchItem(
    string Type,
    string EntityId,
    string? SyntaxId,
    string Name,
    string Kind,
    string Parent,
    string Haystack);
