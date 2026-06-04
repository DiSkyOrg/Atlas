using DiSkyAtlas.Models;

namespace DiSkyAtlas.Services;

/// <summary>
/// Short, friendly one-liners for entities. The manifest's per-entity descriptions
/// are currently null, so we supply curated blurbs for the well-known types and a
/// generic fallback for the rest. Cosmetic only.
/// </summary>
public static class EntityBlurbs
{
    private static readonly Dictionary<string, string> Curated = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bot"] = "Your Discord bot — guilds, gateway ping, self user and lifecycle.",
        ["guild"] = "A Discord server — members, channels, roles, boosts and settings.",
        ["member"] = "A user within a guild — roles, nickname, flags, activities and dates.",
        ["user"] = "A Discord user account — badges, profile and mutual guilds.",
        ["userprofile"] = "Extended user profile — banner image and accent colour.",
        ["role"] = "A guild role — colour, name and permissions.",
        ["message"] = "A sent message — content, author, embeds, reactions and mentions.",
        ["discorderror"] = "A failed Discord request — error code, meaning and message.",
        ["channel"] = "Base type for everything you can read from or send to.",
        ["guildchannel"] = "A channel that lives inside a guild.",
        ["category"] = "A category grouping channels together.",
        ["voicechannel"] = "A voice channel — bitrate, region and live voice status.",
        ["stagechannel"] = "A stage channel for hosting audiences.",
        ["textchannel"] = "A standard text channel.",
        ["threadchannel"] = "A thread inside a text or forum channel.",
        ["forumchannel"] = "A forum channel of threaded posts."
    };

    public static string For(EntityInfo entity, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(entity.Description))
            return entity.Description!;
        if (Curated.TryGetValue(entity.Id, out var blurb))
            return blurb;
        return $"The {displayName} entity and its Skript syntaxes.";
    }
}
