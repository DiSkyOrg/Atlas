using System.Collections.Concurrent;

namespace DiSkyAtlas.Services.Ai;

/// <summary>
/// In-memory per-IP daily quota for the AI assistant (the per-minute layer is the "ask"
/// rate-limiter policy). Resets at UTC midnight; stale entries are purged opportunistically.
/// </summary>
public sealed class AiIpQuota
{
    private readonly ConcurrentDictionary<string, (DateOnly Day, int Count)> _counts = new();

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
}
