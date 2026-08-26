using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiSkyAtlas.Services.Ai;

/// <summary>
/// Append-only JSONL journal of every assistant conversation (data/ai-chats/yyyy-MM.jsonl):
/// question, answer, tool calls, token counts, real cost, duration, outcome. IPs are stored
/// as truncated SHA-256 hashes, never in clear. Aggregated stats are computed offline from
/// these files; there is no public stats endpoint.
/// </summary>
public sealed class AiChatLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int RingCapacity = 100;

    private readonly string _dir;
    private readonly ILogger<AiChatLog> _logger;
    private readonly object _sync = new();

    // Newest-last ring of successful conversations, backing the /ask page's shared
    // "Recent answers" list. Seeded from the newest JSONL files so it survives restarts.
    private readonly List<AiChatRecord> _recent = [];

    public AiChatLog(IWebHostEnvironment env, ILogger<AiChatLog> logger)
    {
        _logger = logger;
        _dir = Path.Combine(env.ContentRootPath, "data", "ai-chats");
        LoadRecent();
    }

    /// <summary>The newest successful conversations with pairwise-distinct questions, newest first.</summary>
    public IReadOnlyList<AiChatRecord> RecentDistinct(int count)
    {
        lock (_sync)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AiChatRecord>(count);
            for (var i = _recent.Count - 1; i >= 0 && result.Count < count; i--)
            {
                if (seen.Add(_recent[i].Question.Trim()))
                    result.Add(_recent[i]);
            }
            return result;
        }
    }

    private void LoadRecent()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            foreach (var file in Directory.EnumerateFiles(_dir, "*.jsonl")
                         .OrderBy(f => f, StringComparer.Ordinal)
                         .TakeLast(2))
            {
                foreach (var line in File.ReadLines(file))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<AiChatRecord>(line, JsonOptions) is { Outcome: "ok", Answer: not null } record)
                            Append(record);
                    }
                    catch (JsonException)
                    {
                        // A malformed line never blocks startup.
                    }
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "AI chat log: could not seed recent answers from {Dir}", _dir);
        }
    }

    private void Append(AiChatRecord record)
    {
        _recent.Add(record);
        if (_recent.Count > RingCapacity)
            _recent.RemoveRange(0, _recent.Count - RingCapacity);
    }

    public static string HashIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return "unknown";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ip));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    public void Write(AiChatRecord record)
    {
        _logger.LogInformation(
            "AI chat [{Outcome}] model={Model} rounds={Rounds} tools={Tools} tokens={Tokens} cost=${Cost:F4} in {Ms}ms",
            record.Outcome, record.Model, record.Rounds, record.ToolCalls.Count,
            record.TotalTokens, record.CostUsd, record.DurationMs);
        try
        {
            var line = JsonSerializer.Serialize(record, JsonOptions);
            lock (_sync)
            {
                if (record is { Outcome: "ok", Answer: not null })
                    Append(record);
                Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, $"{DateTime.UtcNow:yyyy-MM}.jsonl"), line + "\n");
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "AI chat log: could not append to {Dir}", _dir);
        }
    }
}

/// <summary>One logged conversation. TotalTokens/CostUsd come from OpenRouter's usage accounting.</summary>
public sealed record AiChatRecord(
    DateTime At,
    string IpHash,
    string Model,
    string Question,
    string? Answer,
    int Rounds,
    IReadOnlyList<AiToolCall> ToolCalls,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    double CostUsd,
    long DurationMs,
    string Outcome);

/// <summary>A tool invocation made by the model during a conversation.</summary>
public sealed record AiToolCall(string Name, string Args);
