using System.Text.Json.Serialization;

namespace DiSkyAtlas.Models;

/// <summary>
/// Root document produced by the DiSky jar at boot (plugins/DiSky/disky-syntax.json,
/// dropped here as wwwroot/data/atlas.json). Mirrors the jar's SyntaxManifest 1:1.
/// </summary>
public sealed class SyntaxManifest
{
    public int SchemaVersion { get; init; }
    public string? DiskyVersion { get; init; }
    public string? GeneratedAt { get; init; }

    /// <summary>Every documented Discord entity (a Skript type), with its syntaxes.</summary>
    public List<EntityInfo> Entities { get; init; } = [];

    /// <summary>Hand-written, non-entity syntaxes (define bot, await, try, …). Pinned under "Core / Global".</summary>
    public List<SyntaxInfo> Core { get; init; } = [];

    /// <summary>Discord events (each a <see cref="SyntaxInfo"/> of kind EVENT carrying <see cref="SyntaxInfo.Event"/>).</summary>
    public List<SyntaxInfo> Events { get; init; } = [];

    /// <summary>Catalog of every referenced type; enum types carry their accepted literals in <see cref="TypeEntry.Values"/>.</summary>
    public List<TypeEntry> Types { get; init; } = [];
}

/// <summary>A top-level type-catalog entry. Distinct from <see cref="TypeRef"/> (inline refs stay {id,name}).</summary>
public sealed class TypeEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>For an enum type, the canonical accepted literals (one per constant); empty otherwise.</summary>
    public List<string> Values { get; init; } = [];
}

/// <summary>A documented Discord entity = a Skript type with a place in the type hierarchy.</summary>
public sealed class EntityInfo
{
    public required string Id { get; init; }

    /// <summary>Raw (title-cased code name) from the jar, e.g. "Standardguildmessagechannel". Prefer <see cref="DisplayName"/>.</summary>
    public required string Name { get; init; }

    public string? CodeName { get; init; }
    public string? JdaType { get; init; }

    /// <summary>Parent entity id in the type hierarchy, or null for a root type.</summary>
    public string? ParentId { get; init; }

    public string? Description { get; init; }
    public string? Since { get; init; }

    public List<SyntaxInfo> Syntaxes { get; init; } = [];
}

/// <summary>One Skript syntax (expression / effect / condition / event / section / structure / type).</summary>
public sealed class SyntaxInfo
{
    public required string Id { get; init; }

    /// <summary>Owning entity id, or null for a Core/Global syntax.</summary>
    public string? EntityId { get; init; }

    public SyntaxKind Kind { get; init; }

    /// <summary>Short display name, e.g. "bitrate", "roles".</summary>
    public required string Name { get; init; }

    /// <summary>The real Skript pattern(s), e.g. "[the] bitrate of %audiochannel%".</summary>
    public List<string> Patterns { get; init; } = [];

    public TypeRef? ReturnType { get; init; }

    /// <summary>True when the syntax returns a list/collection of <see cref="ReturnType"/>.</summary>
    public bool ReturnList { get; init; }

    public List<ChangeMode> ChangeModes { get; init; } = [];

    /// <summary>Async metadata, or null for a plain synchronous syntax.</summary>
    public AsyncInfo? Async { get; init; }

    /// <summary>True when the same syntax is registered on several entities (e.g. "name", "jump url").</summary>
    public bool Shared { get; init; }

    public string? Since { get; init; }

    /// <summary>Description paragraphs.</summary>
    public List<string> Description { get; init; } = [];

    /// <summary>Skript code examples.</summary>
    public List<string> Examples { get; init; } = [];

    [JsonIgnore]
    public List<string> ProcessedExamples
    {
        get
        {
            var processed = new List<string>();
            var currentBlock = new List<string>();

            foreach (var example in Examples)
            {
                if (example.StartsWith('\t') || example.StartsWith("    "))
                {
                    currentBlock.Add(example);
                }
                else
                {
                    if (currentBlock.Count > 0)
                    {
                        processed.Add(string.Join("\n", currentBlock));
                        currentBlock.Clear();
                    }
                    processed.Add(example);
                }
            }

            if (currentBlock.Count > 0)
            {
                processed.Add(string.Join("\n", currentBlock));
            }

            return processed;
        }
    }

    public List<string> RequiredIntents { get; init; } = [];
    public bool Deprecated { get; init; }
    public string? DeprecatedReason { get; init; }

    /// <summary>Event-only metadata. Non-null only when <see cref="Kind"/> is <see cref="SyntaxKind.Event"/>.</summary>
    public EventDetails? Event { get; init; }
}

/// <summary>Event-only metadata attached to a <see cref="SyntaxInfo"/> of kind EVENT.</summary>
public sealed class EventDetails
{
    public bool Cancellable { get; init; }

    /// <summary>Gateway intent names the event needs.</summary>
    public List<string> Intents { get; init; } = [];

    /// <summary>Event-values, accessed as <c>event-&lt;name&gt;</c> (by type).</summary>
    public List<EventValueDetail> Values { get; init; } = [];

    /// <summary>Event-scoped expressions, accessed by their own pattern (e.g. "used command").</summary>
    public List<EventExpressionDetail> Expressions { get; init; } = [];
}

/// <summary>An <c>event-&lt;name&gt;</c> value available inside an event.</summary>
public sealed class EventValueDetail
{
    public required string Name { get; init; }
    public TypeRef? Type { get; init; }
    public bool List { get; init; }

    /// <summary>"present" | "past" | "future"; update events expose a past/present pair.</summary>
    public string? Time { get; init; }
}

/// <summary>An event-scoped expression, accessed by its pattern.</summary>
public sealed class EventExpressionDetail
{
    public required string Pattern { get; init; }
    public TypeRef? Type { get; init; }
    public bool List { get; init; }
}

/// <summary>Resolution behaviour for a syntax that talks to Discord over REST.</summary>
public sealed class AsyncInfo
{
    /// <summary>Can be prefixed with <c>await</c> to run on the Skript thread without blocking.</summary>
    public bool Awaitable { get; init; }

    /// <summary>Backed by a REST call (vs. cache).</summary>
    public bool RestBacked { get; init; }

    /// <summary>Only retrievable (read-only fetch), never settable.</summary>
    public bool RetrieveOnly { get; init; }
}

/// <summary>A reference to a Skript type (its id + display name).</summary>
public sealed class TypeRef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyntaxKind
{
    /// <summary>A <c>&lt;name&gt; of %type%</c> property, both per-type and cross-type (shared); listed together.</summary>
    Property,
    /// <summary>A non-property expression (e.g. <c>a new discord bot</c>).</summary>
    Expression,
    /// <summary>A cache lookup returning an entity by id (e.g. <c>news channel with id %string%</c>); attached to its return-type entity.</summary>
    Getter,
    Effect,
    Condition,
    Event,
    Section,
    Structure,
    Type
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeMode
{
    Set,
    Add,
    Remove,
    [JsonStringEnumMemberName("REMOVE_ALL")] RemoveAll,
    Delete,
    Reset
}
