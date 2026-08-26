using Microsoft.Extensions.Options;

namespace DiSkyAtlas.Services.Ai;

/// <summary>Why an assistant request was refused before spending anything (None = admitted).</summary>
public enum AskRefusal
{
    None,
    Disabled,
    Budget,
    MissingQuestion,
    TooLong,
    MinuteQuota,
    DailyQuota,
    Busy
}

/// <summary>
/// The refusal chain shared by both assistant surfaces — the /api/v1/ask endpoint and the
/// /ask Blazor page — so their protections can never drift. Order: kill-switch, weekly
/// budget, question validation, (per-minute), per-day quota, concurrency.
/// </summary>
public sealed class AskGuards(
    IOptionsMonitor<AiOptions> options,
    AskService ask,
    AiBudget budget,
    AiIpQuota quota)
{
    /// <summary>
    /// Admits or refuses one question. On <see cref="AskRefusal.None"/> the quotas are
    /// consumed and the concurrency slot is taken: the caller MUST call
    /// <see cref="AskService.Exit"/> in a finally block. <paramref name="includeMinuteLayer"/>
    /// is for callers not behind the HTTP "ask" rate-limiter policy (the Blazor page).
    /// </summary>
    public AskRefusal TryBegin(string? question, string ipKey, bool includeMinuteLayer)
    {
        var opts = options.CurrentValue;

        if (!ask.Available)
            return AskRefusal.Disabled;
        if (!budget.CanSpend(opts.WeeklyBudgetUsd))
            return AskRefusal.Budget;
        if (string.IsNullOrWhiteSpace(question))
            return AskRefusal.MissingQuestion;
        if (question.Length > opts.MaxQuestionChars)
            return AskRefusal.TooLong;
        if (includeMinuteLayer && !quota.TryTakeMinute(ipKey, opts.PerIpPerMinute))
            return AskRefusal.MinuteQuota;
        if (!quota.TryTake(ipKey, opts.PerIpPerDay))
            return AskRefusal.DailyQuota;
        if (!ask.TryEnter())
            return AskRefusal.Busy;

        return AskRefusal.None;
    }
}
