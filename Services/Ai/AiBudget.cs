using System.Globalization;
using System.Text.Json;

namespace DiSkyAtlas.Services.Ai;

/// <summary>
/// Persistent weekly spending counter for the AI assistant. The spent amount comes from
/// OpenRouter's real per-request cost (usage.cost), is checked BEFORE every paid call and
/// persisted to data/ai-usage.json (atomic tmp+move) so the cap survives restarts.
/// The cap can only ever be overshot by the requests already in flight, never more.
/// </summary>
public sealed class AiBudget
{
    private readonly string _path;
    private readonly ILogger<AiBudget> _logger;
    private readonly object _sync = new();
    private string _weekKey = "";
    private double _spentUsd;

    public AiBudget(IWebHostEnvironment env, ILogger<AiBudget> logger)
    {
        _logger = logger;
        _path = Path.Combine(env.ContentRootPath, "data", "ai-usage.json");
        Load();
    }

    /// <summary>ISO week key, e.g. "2026-W35"; the counter resets when it changes.</summary>
    private static string CurrentWeekKey()
    {
        var today = DateTime.UtcNow.Date;
        return $"{ISOWeek.GetYear(today)}-W{ISOWeek.GetWeekOfYear(today):00}";
    }

    public double SpentUsd
    {
        get { lock (_sync) { RollWeek(); return _spentUsd; } }
    }

    public bool CanSpend(double weeklyBudgetUsd)
    {
        lock (_sync)
        {
            RollWeek();
            return _spentUsd < weeklyBudgetUsd;
        }
    }

    public void Record(double costUsd)
    {
        if (costUsd <= 0) return;
        lock (_sync)
        {
            RollWeek();
            _spentUsd += costUsd;
            Save();
        }
    }

    private void RollWeek()
    {
        var week = CurrentWeekKey();
        if (week == _weekKey) return;
        _weekKey = week;
        _spentUsd = 0;
        Save();
    }

    private void Load()
    {
        _weekKey = CurrentWeekKey();
        try
        {
            if (!File.Exists(_path)) return;
            var doc = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path));
            if (doc is not null && doc.WeekKey == _weekKey)
                _spentUsd = doc.SpentUsd;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "AI budget: could not read {Path}; starting the week at $0", _path);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Persisted(_weekKey, _spentUsd)));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "AI budget: could not persist {Path}; the in-memory counter still applies", _path);
        }
    }

    private sealed record Persisted(string WeekKey, double SpentUsd);
}
