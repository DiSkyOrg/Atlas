using System.Collections.Concurrent;

namespace DiSkyAtlas.Services.Ai;

/// <summary>
/// In-memory per-IP daily quota for the AI assistant (the per-minute layer is the "ask"
/// rate-limiter policy). Resets at UTC midnight; stale entries are purged opportunistically.
/// </summary>
public sealed class AiIpQuota
{
    private readonly ConcurrentDictionary<string, (DateOnly Day, int Count)> _counts = new();
    private readonly ConcurrentDictionary<string, (long Minute, int Count)> _minuteCounts = new();

    public bool TryTake(string ip, int perDay)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var entry = _counts.AddOrUpdate(ip,
            _ => (today, 1),
            (_, current) => current.Day == today ? (today, current.Count + 1) : (today, 1));

        if (entry.Count == 1 && _counts.Count > 10_000)
            foreach (var kv in _counts)
                if (kv.Value.Day != today)
                    _counts.TryRemove(kv.Key, out _);

        return entry.Count <= perDay;
    }

    /// <summary>Read-only peek at today's usage (the /ask page's "N left" display).</summary>
    public int UsedToday(string key) =>
        _counts.TryGetValue(key, out var entry) && entry.Day == DateOnly.FromDateTime(DateTime.UtcNow)
            ? entry.Count
            : 0;

    /// <summary>
    /// Per-minute layer for callers that bypass the HTTP "ask" rate-limiter policy —
    /// Blazor circuits talk over SignalR, which never traverses that middleware.
    /// </summary>
    public bool TryTakeMinute(string key, int perMinute)
    {
        var minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
        var entry = _minuteCounts.AddOrUpdate(key,
            _ => (minute, 1),
            (_, current) => current.Minute == minute ? (minute, current.Count + 1) : (minute, 1));

        if (entry.Count == 1 && _minuteCounts.Count > 10_000)
            foreach (var kv in _minuteCounts)
                if (kv.Value.Minute != minute)
                    _minuteCounts.TryRemove(kv.Key, out _);

        return entry.Count <= perMinute;
    }
}
