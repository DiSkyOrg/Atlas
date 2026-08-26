namespace DiSkyAtlas.Services.Ai;

/// <summary>
/// The "Ai" section of appsettings.json, consumed through IOptionsMonitor so every value
/// (model, budget, limits, kill-switch) is hot-reloadable without a restart. The OpenRouter
/// API key deliberately stays out of here: it comes from the OPENROUTER_API_KEY env var.
/// </summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>Kill-switch: false makes /api/v1/ask answer 503 without spending anything.</summary>
    public bool Enabled { get; set; }

    /// <summary>OpenRouter model id (e.g. "anthropic/claude-haiku-4.5").</summary>
    public string Model { get; set; } = "anthropic/claude-haiku-4.5";

    /// <summary>Hard weekly spending cap in USD; requests are refused once reached.</summary>
    public double WeeklyBudgetUsd { get; set; } = 5.0;

    /// <summary>Maximum tool-call rounds per question before the model must answer.</summary>
    public int MaxToolRounds { get; set; } = 5;

    /// <summary>
    /// How many search_atlas hits to run and attach to the question before the first round
    /// (pre-retrieval). 0 disables it and makes the model do that search itself.
    /// </summary>
    public int SeedSearchLimit { get; set; } = 8;

    /// <summary>max_tokens forwarded to OpenRouter for each completion.</summary>
    public int MaxOutputTokens { get; set; } = 1024;

    /// <summary>Questions longer than this are rejected with 400 before any spend.</summary>
    public int MaxQuestionChars { get; set; } = 500;

    public int PerIpPerMinute { get; set; } = 3;
    public int PerIpPerDay { get; set; } = 20;

    /// <summary>Simultaneous OpenRouter conversations; excess requests get 429.</summary>
    public int MaxConcurrent { get; set; } = 2;
}
