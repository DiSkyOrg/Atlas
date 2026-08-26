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

    private readonly string _dir;
    private readonly ILogger<AiChatLog> _logger;
    private readonly object _sync = new();

    public AiChatLog(IWebHostEnvironment env, ILogger<AiChatLog> logger)
    {
        _logger = logger;
        _dir = Path.Combine(env.ContentRootPath, "data", "ai-chats");
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
