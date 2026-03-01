using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Quota;

/// <summary>
/// Defines the available quota tiers for the intelligence system.
/// </summary>
public enum QuotaTier
{
    /// <summary>Free tier with basic limits.</summary>
    Free = 0,

    /// <summary>Basic paid tier with moderate limits.</summary>
    Basic = 1,

    /// <summary>Professional tier with high limits.</summary>
    Pro = 2,

    /// <summary>Enterprise tier with very high limits.</summary>
    Enterprise = 3,

    /// <summary>Bring Your Own Key - user provides their own API keys with no platform limits.</summary>
    BringYourOwnKey = 99
}

/// <summary>
/// Defines usage limits for a specific quota tier.
/// </summary>
/// <param name="DailyRequests">Maximum requests per day (0 = unlimited).</param>
/// <param name="MonthlyRequests">Maximum requests per month (0 = unlimited).</param>
/// <param name="DailyTokens">Maximum tokens per day (0 = unlimited).</param>
/// <param name="MonthlyTokens">Maximum tokens per month (0 = unlimited).</param>
/// <param name="ConcurrentRequests">Maximum concurrent requests.</param>
/// <param name="RequestsPerMinute">Maximum requests per minute.</param>
/// <param name="AllowedModels">List of allowed model identifiers (empty = all allowed).</param>
/// <param name="AllowMultiTurn">Whether multi-turn conversations are allowed.</param>
/// <param name="AllowFunctionCalling">Whether function calling is allowed.</param>
/// <param name="AllowVision">Whether vision capabilities are allowed.</param>
/// <param name="MaxContextTokens">Maximum context window tokens.</param>
/// <param name="DailyCostLimitUsd">Maximum daily cost in USD (0 = unlimited).</param>
/// <param name="MonthlyCostLimitUsd">Maximum monthly cost in USD (0 = unlimited).</param>
public record UsageLimits(
    int DailyRequests,
    int MonthlyRequests,
    long DailyTokens,
    long MonthlyTokens,
    int ConcurrentRequests,
    int RequestsPerMinute,
    HashSet<string> AllowedModels,
    bool AllowMultiTurn,
    bool AllowFunctionCalling,
    bool AllowVision,
    int MaxContextTokens,
    decimal DailyCostLimitUsd,
    decimal MonthlyCostLimitUsd
)
{
    /// <summary>
    /// Gets the default limits for Free tier.
    /// </summary>
    public static UsageLimits Free => new(
        DailyRequests: 50,
        MonthlyRequests: 1000,
        DailyTokens: 50_000,
        MonthlyTokens: 1_000_000,
        ConcurrentRequests: 1,
        RequestsPerMinute: 5,
        AllowedModels: new HashSet<string> { "gpt-3.5-turbo", "claude-instant" },
        AllowMultiTurn: false,
        AllowFunctionCalling: false,
        AllowVision: false,
        MaxContextTokens: 4096,
        DailyCostLimitUsd: 1.0m,
        MonthlyCostLimitUsd: 10.0m
    );

    /// <summary>
    /// Gets the default limits for Basic tier.
    /// </summary>
    public static UsageLimits Basic => new(
        DailyRequests: 500,
        MonthlyRequests: 10_000,
        DailyTokens: 500_000,
        MonthlyTokens: 10_000_000,
        ConcurrentRequests: 3,
        RequestsPerMinute: 20,
        AllowedModels: new HashSet<string> { "gpt-3.5-turbo", "gpt-4", "claude-2", "claude-instant" },
        AllowMultiTurn: true,
        AllowFunctionCalling: true,
        AllowVision: false,
        MaxContextTokens: 16384,
        DailyCostLimitUsd: 10.0m,
        MonthlyCostLimitUsd: 100.0m
    );

    /// <summary>
    /// Gets the default limits for Pro tier.
    /// </summary>
    public static UsageLimits Pro => new(
        DailyRequests: 5000,
        MonthlyRequests: 100_000,
        DailyTokens: 5_000_000,
        MonthlyTokens: 100_000_000,
        ConcurrentRequests: 10,
        RequestsPerMinute: 60,
        AllowedModels: new HashSet<string>(),
        AllowMultiTurn: true,
        AllowFunctionCalling: true,
        AllowVision: true,
        MaxContextTokens: 128000,
        DailyCostLimitUsd: 50.0m,
        MonthlyCostLimitUsd: 500.0m
    );

    /// <summary>
    /// Gets the default limits for Enterprise tier.
    /// </summary>
    public static UsageLimits Enterprise => new(
        DailyRequests: 0,
        MonthlyRequests: 0,
        DailyTokens: 0,
        MonthlyTokens: 0,
        ConcurrentRequests: 50,
        RequestsPerMinute: 300,
        AllowedModels: new HashSet<string>(),
        AllowMultiTurn: true,
        AllowFunctionCalling: true,
        AllowVision: true,
        MaxContextTokens: 200000,
        DailyCostLimitUsd: 0,
        MonthlyCostLimitUsd: 0
    );

    /// <summary>
    /// Gets the default limits for BYOK tier (no platform limits).
    /// </summary>
    public static UsageLimits BringYourOwnKey => new(
        DailyRequests: 0,
        MonthlyRequests: 0,
        DailyTokens: 0,
        MonthlyTokens: 0,
        ConcurrentRequests: 20,
        RequestsPerMinute: 100,
        AllowedModels: new HashSet<string>(),
        AllowMultiTurn: true,
        AllowFunctionCalling: true,
        AllowVision: true,
        MaxContextTokens: 200000,
        DailyCostLimitUsd: 0,
        MonthlyCostLimitUsd: 0
    );

    /// <summary>
    /// Gets the limits for a specific tier.
    /// </summary>
    public static UsageLimits ForTier(QuotaTier tier) => tier switch
    {
        QuotaTier.Free => Free,
        QuotaTier.Basic => Basic,
        QuotaTier.Pro => Pro,
        QuotaTier.Enterprise => Enterprise,
        QuotaTier.BringYourOwnKey => BringYourOwnKey,
        _ => Free
    };
}

/// <summary>
/// Tracks quota usage for a specific user.
/// </summary>
public class UserQuota
{
    private readonly object _lock = new();

    /// <summary>
    /// Gets the user identifier.
    /// </summary>
    public string UserId { get; }

    /// <summary>
    /// Gets the current quota tier.
    /// </summary>
    public QuotaTier Tier { get; private set; }

    /// <summary>
    /// Gets the usage limits for this user.
    /// </summary>
    public UsageLimits Limits { get; private set; }

    /// <summary>
    /// Gets the current day's request count.
    /// </summary>
    public int DailyRequestCount { get; private set; }

    /// <summary>
    /// Gets the current month's request count.
    /// </summary>
    public int MonthlyRequestCount { get; private set; }

    /// <summary>
    /// Gets the current day's token usage.
    /// </summary>
    public long DailyTokenCount { get; private set; }

    /// <summary>
    /// Gets the current month's token usage.
    /// </summary>
    public long MonthlyTokenCount { get; private set; }

    /// <summary>
    /// Gets the current day's cost in USD.
    /// </summary>
    public decimal DailyCostUsd { get; private set; }

    /// <summary>
    /// Gets the current month's cost in USD.
    /// </summary>
    public decimal MonthlyCostUsd { get; private set; }

    /// <summary>
    /// Gets the last daily reset timestamp.
    /// </summary>
    public DateTime LastDailyReset { get; private set; }

    /// <summary>
    /// Gets the last monthly reset timestamp.
    /// </summary>
    public DateTime LastMonthlyReset { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UserQuota"/> class.
    /// </summary>
    public UserQuota(string userId, QuotaTier tier = QuotaTier.Free)
    {
        UserId = userId ?? throw new ArgumentNullException(nameof(userId));
        Tier = tier;
        Limits = UsageLimits.ForTier(tier);
        LastDailyReset = DateTime.UtcNow;
        LastMonthlyReset = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates the user's tier and limits.
    /// </summary>
    public void UpdateTier(QuotaTier newTier)
    {
        lock (_lock)
        {
            Tier = newTier;
            Limits = UsageLimits.ForTier(newTier);
        }
    }

    /// <summary>
    /// Records usage for a request.
    /// </summary>
    public void RecordUsage(int promptTokens, int completionTokens, decimal costUsd)
    {
        lock (_lock)
        {
            CheckAndResetIfNeeded();

            DailyRequestCount++;
            MonthlyRequestCount++;

            var totalTokens = promptTokens + completionTokens;
            DailyTokenCount += totalTokens;
            MonthlyTokenCount += totalTokens;

            DailyCostUsd += costUsd;
            MonthlyCostUsd += costUsd;
        }
    }

    /// <summary>
    /// Checks if the user can make a request based on current usage and limits.
    /// </summary>
    public bool CanMakeRequest(out string? reason)
    {
        lock (_lock)
        {
            CheckAndResetIfNeeded();

            // Check daily request limit
            if (Limits.DailyRequests > 0 && DailyRequestCount >= Limits.DailyRequests)
            {
                reason = $"Daily request limit ({Limits.DailyRequests}) exceeded";
                return false;
            }

            // Check monthly request limit
            if (Limits.MonthlyRequests > 0 && MonthlyRequestCount >= Limits.MonthlyRequests)
            {
                reason = $"Monthly request limit ({Limits.MonthlyRequests}) exceeded";
                return false;
            }

            // Check daily token limit
            if (Limits.DailyTokens > 0 && DailyTokenCount >= Limits.DailyTokens)
            {
                reason = $"Daily token limit ({Limits.DailyTokens}) exceeded";
                return false;
            }

            // Check monthly token limit
            if (Limits.MonthlyTokens > 0 && MonthlyTokenCount >= Limits.MonthlyTokens)
            {
                reason = $"Monthly token limit ({Limits.MonthlyTokens}) exceeded";
                return false;
            }

            // Check daily cost limit
            if (Limits.DailyCostLimitUsd > 0 && DailyCostUsd >= Limits.DailyCostLimitUsd)
            {
                reason = $"Daily cost limit (${Limits.DailyCostLimitUsd}) exceeded";
                return false;
            }

            // Check monthly cost limit
            if (Limits.MonthlyCostLimitUsd > 0 && MonthlyCostUsd >= Limits.MonthlyCostLimitUsd)
            {
                reason = $"Monthly cost limit (${Limits.MonthlyCostLimitUsd}) exceeded";
                return false;
            }

            reason = null;
            return true;
        }
    }

    /// <summary>
    /// Checks if a specific model is allowed for this user.
    /// </summary>
    public bool IsModelAllowed(string modelId)
    {
        lock (_lock)
        {
            // Empty set means all models allowed
            return Limits.AllowedModels.Count == 0 || Limits.AllowedModels.Contains(modelId);
        }
    }

    /// <summary>
    /// Resets daily and monthly counters if needed.
    /// </summary>
    private void CheckAndResetIfNeeded()
    {
        var now = DateTime.UtcNow;

        // Reset daily counters if a day has passed
        if (now.Date > LastDailyReset.Date)
        {
            DailyRequestCount = 0;
            DailyTokenCount = 0;
            DailyCostUsd = 0;
            LastDailyReset = now;
        }

        // Reset monthly counters if a month has passed
        if (now.Month != LastMonthlyReset.Month || now.Year != LastMonthlyReset.Year)
        {
            MonthlyRequestCount = 0;
            MonthlyTokenCount = 0;
            MonthlyCostUsd = 0;
            LastMonthlyReset = now;
        }
    }

    /// <summary>
    /// Gets a summary of current usage.
    /// </summary>
    public string GetUsageSummary()
    {
        lock (_lock)
        {
            CheckAndResetIfNeeded();

            return $"Tier: {Tier}\n" +
                   $"Daily: {DailyRequestCount}/{(Limits.DailyRequests > 0 ? Limits.DailyRequests : "∞")} requests, " +
                   $"{DailyTokenCount}/{(Limits.DailyTokens > 0 ? Limits.DailyTokens : "∞")} tokens, " +
                   $"${DailyCostUsd:F2}/{(Limits.DailyCostLimitUsd > 0 ? $"{Limits.DailyCostLimitUsd:F2}" : "∞")}\n" +
                   $"Monthly: {MonthlyRequestCount}/{(Limits.MonthlyRequests > 0 ? Limits.MonthlyRequests : "∞")} requests, " +
                   $"{MonthlyTokenCount}/{(Limits.MonthlyTokens > 0 ? Limits.MonthlyTokens : "∞")} tokens, " +
                   $"${MonthlyCostUsd:F2}/{(Limits.MonthlyCostLimitUsd > 0 ? $"{Limits.MonthlyCostLimitUsd:F2}" : "∞")}";
        }
    }
}

/// <summary>
/// Provides authentication and quota management for the intelligence system.
/// </summary>
public interface IIntelligenceAuthProvider
{
    /// <summary>
    /// Validates an API key and returns the associated user ID.
    /// </summary>
    /// <param name="apiKey">The API key to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>User ID if valid, null otherwise.</returns>
    Task<string?> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the quota information for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>User quota information.</returns>
    Task<UserQuota> GetUserQuotaAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates usage information for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="promptTokens">Number of prompt tokens used.</param>
    /// <param name="completionTokens">Number of completion tokens used.</param>
    /// <param name="costUsd">Cost in USD.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateUsageAsync(string userId, int promptTokens, int completionTokens, decimal costUsd,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the usage limits for a specific tier.
    /// </summary>
    /// <param name="tier">The quota tier.</param>
    /// <returns>Usage limits for the tier.</returns>
    UsageLimits GetTierLimits(QuotaTier tier);

    /// <summary>
    /// Checks if a user has a valid BYOK (Bring Your Own Key) configuration.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="provider">The provider name (e.g., "openai", "anthropic").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if user has a valid BYOK key for the provider.</returns>
    Task<bool> HasByokKeyAsync(string userId, string provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user's BYOK API key for a specific provider.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="provider">The provider name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The API key if configured, null otherwise.</returns>
    Task<string?> GetByokKeyAsync(string userId, string provider, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory implementation of authentication provider for development and testing.
/// </summary>
public class InMemoryAuthProvider : IIntelligenceAuthProvider
{
    private readonly BoundedDictionary<string, string> _apiKeyToUserId = new BoundedDictionary<string, string>(1000);
    private readonly BoundedDictionary<string, UserQuota> _quotas = new BoundedDictionary<string, UserQuota>(1000);
    private readonly BoundedDictionary<string, Dictionary<string, string>> _byokKeys = new BoundedDictionary<string, Dictionary<string, string>>(1000);

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryAuthProvider"/> class.
    /// </summary>
    public InMemoryAuthProvider()
    {
        // P2-3080: Hardcoded dev API keys removed from production code.
        // Register keys explicitly via RegisterApiKeyAsync or inject via constructor.
        // #if DEBUG block retained for local testing only — never shipped in Release.
#if DEBUG
        _apiKeyToUserId["dev-free"] = "user-free";
        _apiKeyToUserId["dev-basic"] = "user-basic";
        _apiKeyToUserId["dev-pro"] = "user-pro";
        _apiKeyToUserId["dev-enterprise"] = "user-enterprise";
        _apiKeyToUserId["dev-byok"] = "user-byok";

        _quotas["user-free"] = new UserQuota("user-free", QuotaTier.Free);
        _quotas["user-basic"] = new UserQuota("user-basic", QuotaTier.Basic);
        _quotas["user-pro"] = new UserQuota("user-pro", QuotaTier.Pro);
        _quotas["user-enterprise"] = new UserQuota("user-enterprise", QuotaTier.Enterprise);
        _quotas["user-byok"] = new UserQuota("user-byok", QuotaTier.BringYourOwnKey);
#endif
    }

    /// <inheritdoc/>
    public Task<string?> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        _apiKeyToUserId.TryGetValue(apiKey, out var userId);
        return Task.FromResult<string?>(userId);
    }

    /// <inheritdoc/>
    public Task<UserQuota> GetUserQuotaAsync(string userId, CancellationToken cancellationToken = default)
    {
        var quota = _quotas.GetOrAdd(userId, id => new UserQuota(id, QuotaTier.Free));
        return Task.FromResult(quota);
    }

    /// <inheritdoc/>
    public Task UpdateUsageAsync(string userId, int promptTokens, int completionTokens, decimal costUsd,
        CancellationToken cancellationToken = default)
    {
        if (_quotas.TryGetValue(userId, out var quota))
        {
            quota.RecordUsage(promptTokens, completionTokens, costUsd);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public UsageLimits GetTierLimits(QuotaTier tier) => UsageLimits.ForTier(tier);

    /// <inheritdoc/>
    public Task<bool> HasByokKeyAsync(string userId, string provider, CancellationToken cancellationToken = default)
    {
        var hasKey = _byokKeys.TryGetValue(userId, out var keys) && keys.ContainsKey(provider);
        return Task.FromResult(hasKey);
    }

    /// <inheritdoc/>
    public Task<string?> GetByokKeyAsync(string userId, string provider, CancellationToken cancellationToken = default)
    {
        string? key = null;
        if (_byokKeys.TryGetValue(userId, out var keys))
        {
            keys.TryGetValue(provider, out key);
        }
        return Task.FromResult(key);
    }

    /// <summary>
    /// Adds a BYOK key for testing.
    /// </summary>
    public void SetByokKey(string userId, string provider, string apiKey)
    {
        var keys = _byokKeys.GetOrAdd(userId, _ => new Dictionary<string, string>());
        keys[provider] = apiKey;
    }

    /// <summary>
    /// Registers a new API key for testing.
    /// </summary>
    public void RegisterApiKey(string apiKey, string userId, QuotaTier tier = QuotaTier.Free)
    {
        _apiKeyToUserId[apiKey] = userId;
        _quotas[userId] = new UserQuota(userId, tier);
    }
}

/// <summary>
/// Manages quota enforcement and usage tracking.
/// </summary>
public class QuotaManager
{
    private readonly IIntelligenceAuthProvider _authProvider;
    private readonly CostEstimator _costEstimator;
    private readonly RateLimiter _rateLimiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="QuotaManager"/> class.
    /// </summary>
    public QuotaManager(IIntelligenceAuthProvider authProvider)
    {
        _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
        _costEstimator = new CostEstimator();
        _rateLimiter = new RateLimiter();
    }

    /// <summary>
    /// Checks if a user can make a request.
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> CanMakeRequestAsync(
        string userId,
        string modelId,
        int estimatedPromptTokens,
        CancellationToken cancellationToken = default)
    {
        var quota = await _authProvider.GetUserQuotaAsync(userId, cancellationToken);

        // Check quota limits
        if (!quota.CanMakeRequest(out var quotaReason))
        {
            return (false, quotaReason);
        }

        // Check model restrictions
        if (!quota.IsModelAllowed(modelId))
        {
            return (false, $"Model '{modelId}' not allowed for tier {quota.Tier}");
        }

        // Check rate limits
        if (!_rateLimiter.TryAcquire(userId, quota.Limits))
        {
            return (false, "Rate limit exceeded");
        }

        // Check concurrent request limits
        if (!_rateLimiter.CanStartConcurrentRequest(userId, quota.Limits.ConcurrentRequests))
        {
            return (false, $"Concurrent request limit ({quota.Limits.ConcurrentRequests}) exceeded");
        }

        return (true, null);
    }

    /// <summary>
    /// Records usage for a completed request.
    /// </summary>
    public async Task RecordUsageAsync(
        string userId,
        string modelId,
        int promptTokens,
        int completionTokens,
        CancellationToken cancellationToken = default)
    {
        var costUsd = _costEstimator.EstimateCost(modelId, promptTokens, completionTokens);
        await _authProvider.UpdateUsageAsync(userId, promptTokens, completionTokens, costUsd, cancellationToken);
        _rateLimiter.ReleaseRequest(userId);
    }

    /// <summary>
    /// Starts tracking a concurrent request.
    /// </summary>
    public void StartConcurrentRequest(string userId)
    {
        _rateLimiter.StartRequest(userId);
    }

    /// <summary>
    /// Ends tracking a concurrent request.
    /// </summary>
    public void EndConcurrentRequest(string userId)
    {
        _rateLimiter.ReleaseRequest(userId);
    }

    /// <summary>
    /// Estimates the cost for a request.
    /// </summary>
    public decimal EstimateCost(string modelId, int promptTokens, int estimatedCompletionTokens)
    {
        return _costEstimator.EstimateCost(modelId, promptTokens, estimatedCompletionTokens);
    }

    /// <summary>
    /// Gets usage summary for a user.
    /// </summary>
    public async Task<string> GetUsageSummaryAsync(string userId, CancellationToken cancellationToken = default)
    {
        var quota = await _authProvider.GetUserQuotaAsync(userId, cancellationToken);
        return quota.GetUsageSummary();
    }
}

/// <summary>
/// Estimates costs for different models and providers.
/// </summary>
public class CostEstimator
{
    private readonly Dictionary<string, (decimal PromptPer1K, decimal CompletionPer1K)> _modelPricing = new()
    {
        // OpenAI pricing (as of 2024)
        ["gpt-3.5-turbo"] = (0.0005m, 0.0015m),
        ["gpt-4"] = (0.03m, 0.06m),
        ["gpt-4-turbo"] = (0.01m, 0.03m),
        ["gpt-4o"] = (0.005m, 0.015m),

        // Anthropic pricing
        ["claude-instant"] = (0.0008m, 0.0024m),
        ["claude-2"] = (0.008m, 0.024m),
        ["claude-3-haiku"] = (0.00025m, 0.00125m),
        ["claude-3-sonnet"] = (0.003m, 0.015m),
        ["claude-3-opus"] = (0.015m, 0.075m),

        // Google pricing
        ["gemini-pro"] = (0.00025m, 0.0005m),
        ["gemini-ultra"] = (0.00125m, 0.00375m),
    };

    /// <summary>
    /// Estimates the cost for a request in USD.
    /// </summary>
    public decimal EstimateCost(string modelId, int promptTokens, int completionTokens)
    {
        if (!_modelPricing.TryGetValue(modelId, out var pricing))
        {
            // Default pricing for unknown models
            pricing = (0.001m, 0.002m);
        }

        var promptCost = (promptTokens / 1000.0m) * pricing.PromptPer1K;
        var completionCost = (completionTokens / 1000.0m) * pricing.CompletionPer1K;

        return promptCost + completionCost;
    }

    /// <summary>
    /// Adds or updates pricing for a model.
    /// </summary>
    public void SetModelPricing(string modelId, decimal promptPer1K, decimal completionPer1K)
    {
        _modelPricing[modelId] = (promptPer1K, completionPer1K);
    }
}

/// <summary>
/// Implements per-user rate limiting with sliding window algorithm.
/// </summary>
public class RateLimiter
{
    private readonly BoundedDictionary<string, UserRateLimitState> _states = new BoundedDictionary<string, UserRateLimitState>(1000);

    private class UserRateLimitState
    {
        public Queue<DateTime> RequestTimestamps { get; } = new();
        public int ConcurrentRequests { get; set; }
        public readonly object Lock = new();
    }

    /// <summary>
    /// Tries to acquire a rate limit slot for a user.
    /// </summary>
    public bool TryAcquire(string userId, UsageLimits limits)
    {
        var state = _states.GetOrAdd(userId, _ => new UserRateLimitState());

        lock (state.Lock)
        {
            var now = DateTime.UtcNow;
            var oneMinuteAgo = now.AddMinutes(-1);

            // Remove timestamps older than 1 minute
            while (state.RequestTimestamps.Count > 0 && state.RequestTimestamps.Peek() < oneMinuteAgo)
            {
                state.RequestTimestamps.Dequeue();
            }

            // Check if under limit
            if (state.RequestTimestamps.Count >= limits.RequestsPerMinute)
            {
                return false;
            }

            // Add new timestamp
            state.RequestTimestamps.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// Checks if a user can start a new concurrent request.
    /// </summary>
    public bool CanStartConcurrentRequest(string userId, int maxConcurrent)
    {
        var state = _states.GetOrAdd(userId, _ => new UserRateLimitState());

        lock (state.Lock)
        {
            return state.ConcurrentRequests < maxConcurrent;
        }
    }

    /// <summary>
    /// Starts tracking a concurrent request.
    /// </summary>
    public void StartRequest(string userId)
    {
        var state = _states.GetOrAdd(userId, _ => new UserRateLimitState());

        lock (state.Lock)
        {
            state.ConcurrentRequests++;
        }
    }

    /// <summary>
    /// Releases a concurrent request slot.
    /// </summary>
    public void ReleaseRequest(string userId)
    {
        if (_states.TryGetValue(userId, out var state))
        {
            lock (state.Lock)
            {
                if (state.ConcurrentRequests > 0)
                {
                    state.ConcurrentRequests--;
                }
            }
        }
    }

    /// <summary>
    /// Gets current rate limit statistics for a user.
    /// </summary>
    public (int RequestsInLastMinute, int ConcurrentRequests) GetStats(string userId)
    {
        if (!_states.TryGetValue(userId, out var state))
        {
            return (0, 0);
        }

        lock (state.Lock)
        {
            var now = DateTime.UtcNow;
            var oneMinuteAgo = now.AddMinutes(-1);

            // Clean old timestamps
            while (state.RequestTimestamps.Count > 0 && state.RequestTimestamps.Peek() < oneMinuteAgo)
            {
                state.RequestTimestamps.Dequeue();
            }

            return (state.RequestTimestamps.Count, state.ConcurrentRequests);
        }
    }
}
