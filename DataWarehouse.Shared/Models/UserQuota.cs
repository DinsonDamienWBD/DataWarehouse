// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Shared.Models;

/// <summary>
/// Quota tier levels for AI usage.
/// </summary>
public enum QuotaTier
{
    /// <summary>Free tier - Limited requests, basic models only.</summary>
    Free = 0,

    /// <summary>Basic tier - More requests, standard models.</summary>
    Basic = 1,

    /// <summary>Pro tier - High volume, all models, priority.</summary>
    Pro = 2,

    /// <summary>Enterprise tier - Unlimited, custom models, dedicated support.</summary>
    Enterprise = 3,

    /// <summary>BYOK (Bring Your Own Key) - User provides their own API keys.</summary>
    BringYourOwnKey = 4
}

/// <summary>
/// Usage limits configuration for a quota tier.
/// </summary>
public sealed class UsageLimits
{
    /// <summary>
    /// Maximum requests allowed per day.
    /// </summary>
    public int DailyRequestLimit { get; init; }

    /// <summary>
    /// Maximum tokens allowed per day.
    /// </summary>
    public int DailyTokenLimit { get; init; }

    /// <summary>
    /// Maximum tokens per single request.
    /// </summary>
    public int MaxTokensPerRequest { get; init; }

    /// <summary>
    /// Maximum concurrent requests allowed.
    /// </summary>
    public int MaxConcurrentRequests { get; init; }

    /// <summary>
    /// List of allowed AI models. Use "*" for all models.
    /// </summary>
    public IReadOnlyList<string> AllowedModels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// List of allowed AI providers. Use "*" for all providers.
    /// </summary>
    public IReadOnlyList<string> AllowedProviders { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether streaming responses are enabled.
    /// </summary>
    public bool StreamingEnabled { get; init; }

    /// <summary>
    /// Whether function/tool calling is enabled.
    /// </summary>
    public bool FunctionCallingEnabled { get; init; }

    /// <summary>
    /// Whether vision/image analysis is enabled.
    /// </summary>
    public bool VisionEnabled { get; init; }

    /// <summary>
    /// Whether embeddings generation is enabled.
    /// </summary>
    public bool EmbeddingsEnabled { get; init; }

    /// <summary>
    /// Monthly spending budget in USD (null for unlimited).
    /// </summary>
    public decimal? MonthlyBudgetUsd { get; init; }

    /// <summary>
    /// Gets default limits for a quota tier.
    /// </summary>
    /// <param name="tier">The quota tier.</param>
    /// <returns>Default usage limits for the tier.</returns>
    public static UsageLimits GetDefaultLimits(QuotaTier tier) => tier switch
    {
        QuotaTier.Free => new UsageLimits
        {
            DailyRequestLimit = 50,
            DailyTokenLimit = 50_000,
            MaxTokensPerRequest = 1024,
            MaxConcurrentRequests = 1,
            AllowedModels = new[] { "gpt-4o-mini", "claude-3-haiku-20240307", "gemini-1.5-flash" },
            AllowedProviders = new[] { "openai", "anthropic", "google" },
            StreamingEnabled = false,
            FunctionCallingEnabled = false,
            VisionEnabled = false,
            EmbeddingsEnabled = false,
            MonthlyBudgetUsd = null
        },
        QuotaTier.Basic => new UsageLimits
        {
            DailyRequestLimit = 500,
            DailyTokenLimit = 500_000,
            MaxTokensPerRequest = 4096,
            MaxConcurrentRequests = 3,
            AllowedModels = new[] { "gpt-4o-mini", "gpt-4o", "claude-3-haiku-20240307", "claude-3-sonnet-20240229", "gemini-1.5-flash", "gemini-1.5-pro" },
            AllowedProviders = new[] { "openai", "anthropic", "google", "mistral" },
            StreamingEnabled = true,
            FunctionCallingEnabled = true,
            VisionEnabled = false,
            EmbeddingsEnabled = true,
            MonthlyBudgetUsd = 20.0m
        },
        QuotaTier.Pro => new UsageLimits
        {
            DailyRequestLimit = 5000,
            DailyTokenLimit = 5_000_000,
            MaxTokensPerRequest = 16384,
            MaxConcurrentRequests = 10,
            AllowedModels = new[] { "*" },
            AllowedProviders = new[] { "*" },
            StreamingEnabled = true,
            FunctionCallingEnabled = true,
            VisionEnabled = true,
            EmbeddingsEnabled = true,
            MonthlyBudgetUsd = 100.0m
        },
        QuotaTier.Enterprise => new UsageLimits
        {
            DailyRequestLimit = int.MaxValue,
            DailyTokenLimit = int.MaxValue,
            MaxTokensPerRequest = 128000,
            MaxConcurrentRequests = 100,
            AllowedModels = new[] { "*" },
            AllowedProviders = new[] { "*" },
            StreamingEnabled = true,
            FunctionCallingEnabled = true,
            VisionEnabled = true,
            EmbeddingsEnabled = true,
            MonthlyBudgetUsd = null
        },
        QuotaTier.BringYourOwnKey => new UsageLimits
        {
            DailyRequestLimit = int.MaxValue,
            DailyTokenLimit = int.MaxValue,
            MaxTokensPerRequest = 128000,
            MaxConcurrentRequests = 50,
            AllowedModels = new[] { "*" },
            AllowedProviders = new[] { "*" },
            StreamingEnabled = true,
            FunctionCallingEnabled = true,
            VisionEnabled = true,
            EmbeddingsEnabled = true,
            MonthlyBudgetUsd = null
        },
        _ => GetDefaultLimits(QuotaTier.Free)
    };

    /// <summary>
    /// Checks if a model is allowed under these limits.
    /// </summary>
    /// <param name="model">Model identifier to check.</param>
    /// <returns>True if the model is allowed.</returns>
    public bool IsModelAllowed(string model)
    {
        if (AllowedModels.Contains("*")) return true;
        return AllowedModels.Any(m => model.StartsWith(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if a provider is allowed under these limits.
    /// </summary>
    /// <param name="provider">Provider identifier to check.</param>
    /// <returns>True if the provider is allowed.</returns>
    public bool IsProviderAllowed(string provider)
    {
        if (AllowedProviders.Contains("*")) return true;
        return AllowedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Per-user quota tracking and enforcement.
/// </summary>
public sealed class UserQuota
{
    /// <summary>
    /// User identifier.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Provider identifier (for provider-specific quotas).
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>
    /// User's quota tier.
    /// </summary>
    public QuotaTier Tier { get; set; } = QuotaTier.Free;

    /// <summary>
    /// Usage limits based on tier.
    /// </summary>
    public UsageLimits Limits { get; set; } = UsageLimits.GetDefaultLimits(QuotaTier.Free);

    /// <summary>
    /// Start of the current tracking period.
    /// </summary>
    public DateTime PeriodStart { get; set; } = DateTime.UtcNow.Date;

    /// <summary>
    /// Number of requests made today.
    /// </summary>
    public int RequestsToday { get; set; }

    /// <summary>
    /// Number of tokens used today.
    /// </summary>
    public long TokensToday { get; set; }

    /// <summary>
    /// Amount spent this month in USD.
    /// </summary>
    public decimal SpentThisMonth { get; set; }

    /// <summary>
    /// Start of the current billing month.
    /// </summary>
    public DateTime MonthStart { get; set; } = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

    /// <summary>
    /// Remaining requests for today.
    /// </summary>
    public int RemainingRequests => Math.Max(0, Limits.DailyRequestLimit - RequestsToday);

    /// <summary>
    /// Remaining tokens for today.
    /// </summary>
    public long RemainingTokens => Math.Max(0, Limits.DailyTokenLimit - TokensToday);

    /// <summary>
    /// Remaining budget for the month (null if unlimited).
    /// </summary>
    public decimal? RemainingBudget => Limits.MonthlyBudgetUsd.HasValue
        ? Math.Max(0, Limits.MonthlyBudgetUsd.Value - SpentThisMonth)
        : null;

    /// <summary>
    /// Checks if a request can be made with the estimated token count.
    /// </summary>
    /// <param name="estimatedTokens">Estimated tokens for the request.</param>
    /// <param name="reason">Reason if request cannot be made.</param>
    /// <returns>True if the request can be made.</returns>
    public bool CanMakeRequest(int estimatedTokens, out string? reason)
    {
        reason = null;

        // Reset period if needed
        ResetPeriodIfNeeded();

        if (RequestsToday >= Limits.DailyRequestLimit)
        {
            reason = $"Daily request limit ({Limits.DailyRequestLimit:N0}) exceeded. Upgrade tier or wait until tomorrow.";
            return false;
        }

        if (TokensToday + estimatedTokens > Limits.DailyTokenLimit)
        {
            reason = $"Daily token limit ({Limits.DailyTokenLimit:N0}) would be exceeded. Upgrade tier or wait until tomorrow.";
            return false;
        }

        if (estimatedTokens > Limits.MaxTokensPerRequest)
        {
            reason = $"Request exceeds maximum tokens per request ({Limits.MaxTokensPerRequest:N0}).";
            return false;
        }

        if (Limits.MonthlyBudgetUsd.HasValue && SpentThisMonth >= Limits.MonthlyBudgetUsd.Value)
        {
            reason = $"Monthly budget (${Limits.MonthlyBudgetUsd:F2}) exceeded. Upgrade tier or add funds.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records usage after a successful request.
    /// </summary>
    /// <param name="inputTokens">Tokens used in the prompt.</param>
    /// <param name="outputTokens">Tokens used in the response.</param>
    /// <param name="costUsd">Estimated cost in USD.</param>
    public void RecordUsage(int inputTokens, int outputTokens, decimal costUsd)
    {
        ResetPeriodIfNeeded();

        RequestsToday++;
        TokensToday += inputTokens + outputTokens;
        SpentThisMonth += costUsd;
    }

    /// <summary>
    /// Resets tracking periods if they have elapsed.
    /// </summary>
    private void ResetPeriodIfNeeded()
    {
        var now = DateTime.UtcNow;

        // Reset daily counters
        if (now.Date > PeriodStart)
        {
            RequestsToday = 0;
            TokensToday = 0;
            PeriodStart = now.Date;
        }

        // Reset monthly spending
        var currentMonthStart = new DateTime(now.Year, now.Month, 1);
        if (currentMonthStart > MonthStart)
        {
            SpentThisMonth = 0;
            MonthStart = currentMonthStart;
        }
    }

    /// <summary>
    /// Creates a summary of current quota status.
    /// </summary>
    /// <returns>Quota status summary.</returns>
    public QuotaStatus GetStatus()
    {
        ResetPeriodIfNeeded();
        return new QuotaStatus
        {
            UserId = UserId,
            ProviderId = ProviderId,
            Tier = Tier,
            RequestsUsed = RequestsToday,
            RequestsLimit = Limits.DailyRequestLimit,
            TokensUsed = TokensToday,
            TokensLimit = Limits.DailyTokenLimit,
            SpentThisMonth = SpentThisMonth,
            MonthlyBudget = Limits.MonthlyBudgetUsd,
            PeriodResetAt = PeriodStart.AddDays(1),
            MonthResetAt = MonthStart.AddMonths(1)
        };
    }
}

/// <summary>
/// Summary of user's quota status.
/// </summary>
public sealed record QuotaStatus
{
    /// <summary>User identifier.</summary>
    public required string UserId { get; init; }

    /// <summary>Provider identifier (if provider-specific).</summary>
    public string? ProviderId { get; init; }

    /// <summary>User's quota tier.</summary>
    public QuotaTier Tier { get; init; }

    /// <summary>Requests used today.</summary>
    public int RequestsUsed { get; init; }

    /// <summary>Daily request limit.</summary>
    public int RequestsLimit { get; init; }

    /// <summary>Tokens used today.</summary>
    public long TokensUsed { get; init; }

    /// <summary>Daily token limit.</summary>
    public long TokensLimit { get; init; }

    /// <summary>Amount spent this month.</summary>
    public decimal SpentThisMonth { get; init; }

    /// <summary>Monthly budget (null if unlimited).</summary>
    public decimal? MonthlyBudget { get; init; }

    /// <summary>When daily limits reset.</summary>
    public DateTime PeriodResetAt { get; init; }

    /// <summary>When monthly budget resets.</summary>
    public DateTime MonthResetAt { get; init; }

    /// <summary>Percentage of daily requests used.</summary>
    public double RequestsUsedPercent => RequestsLimit > 0 ? (double)RequestsUsed / RequestsLimit * 100 : 0;

    /// <summary>Percentage of daily tokens used.</summary>
    public double TokensUsedPercent => TokensLimit > 0 ? (double)TokensUsed / TokensLimit * 100 : 0;

    /// <summary>Percentage of monthly budget used (null if unlimited).</summary>
    public double? BudgetUsedPercent => MonthlyBudget.HasValue && MonthlyBudget > 0
        ? (double)(SpentThisMonth / MonthlyBudget.Value) * 100
        : null;
}

/// <summary>
/// Usage record for tracking individual AI operations.
/// </summary>
public sealed record UsageRecord
{
    /// <summary>Unique record identifier.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>User who made the request.</summary>
    public required string UserId { get; init; }

    /// <summary>AI provider used.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Model used for the request.</summary>
    public required string Model { get; init; }

    /// <summary>Type of operation (chat, completion, embedding, etc.).</summary>
    public required string OperationType { get; init; }

    /// <summary>Tokens used in the prompt/input.</summary>
    public int InputTokens { get; init; }

    /// <summary>Tokens used in the response/output.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Total tokens used.</summary>
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>Estimated cost in USD.</summary>
    public decimal CostUsd { get; init; }

    /// <summary>Request latency in milliseconds.</summary>
    public long LatencyMs { get; init; }

    /// <summary>Whether the request succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if request failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Timestamp of the request.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Additional metadata about the request.</summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}
