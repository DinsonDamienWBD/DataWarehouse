using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.AppPlatform.Models;

namespace DataWarehouse.Plugins.AppPlatform.Strategies;

/// <summary>
/// Strategy that manages per-application AI workflow configurations and routes
/// AI requests to UltimateIntelligence via the message bus. Enforces budget limits,
/// concurrency caps, operation restrictions, and approval requirements before
/// forwarding requests.
/// </summary>
/// <remarks>
/// <para>
/// Each registered application can configure its own AI workflow mode (Auto, Manual,
/// Budget, or Approval), set monthly and per-request budget limits, specify preferred
/// providers and models, restrict allowed operations, and cap concurrent requests.
/// </para>
/// <para>
/// All communication with UltimateIntelligence is through the message bus:
/// <list type="bullet">
///   <item><c>intelligence.workflow.configure</c>: Send per-app AI configuration to Intelligence</item>
///   <item><c>intelligence.workflow.remove</c>: Remove per-app AI configuration from Intelligence</item>
///   <item><c>intelligence.request</c>: Forward an AI request with budget and mode enforcement</item>
/// </list>
/// </para>
/// <para>
/// Usage tracking maintains per-app monthly spend totals, request counts, and active
/// concurrent request counts using thread-safe <see cref="Interlocked"/> operations.
/// </para>
/// </remarks>
internal sealed class AppAiWorkflowStrategy
{
    /// <summary>
    /// Thread-safe dictionary storing per-app AI workflow configurations keyed by AppId.
    /// </summary>
    private readonly BoundedDictionary<string, AppAiWorkflowConfig> _configs = new BoundedDictionary<string, AppAiWorkflowConfig>(1000);

    /// <summary>
    /// Thread-safe dictionary storing per-app AI usage tracking keyed by AppId.
    /// Active concurrent request counts are managed via <see cref="Interlocked"/> operations
    /// on the values in <see cref="_concurrentCounts"/> for atomicity.
    /// </summary>
    private readonly BoundedDictionary<string, AiUsageTracking> _usageTracking = new BoundedDictionary<string, AiUsageTracking>(1000);

    /// <summary>
    /// Thread-safe counters for active concurrent requests per app.
    /// Managed via <see cref="Interlocked.Increment(ref int)"/> and
    /// <see cref="Interlocked.Decrement(ref int)"/> for lock-free thread safety.
    /// </summary>
    private readonly BoundedDictionary<string, int> _concurrentCounts = new BoundedDictionary<string, int>(1000);

    /// <summary>
    /// Message bus for communicating with UltimateIntelligence.
    /// </summary>
    private readonly IMessageBus _messageBus;

    /// <summary>
    /// Plugin identifier used as the source in outgoing messages.
    /// </summary>
    private readonly string _pluginId;

    /// <summary>
    /// Lock object for thread-safe usage tracking updates (monthly spend, request count).
    /// </summary>
    private readonly object _usageLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AppAiWorkflowStrategy"/> class.
    /// </summary>
    /// <param name="messageBus">The message bus for inter-plugin communication.</param>
    /// <param name="pluginId">The plugin identifier used as message source.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="messageBus"/> or <paramref name="pluginId"/> is <c>null</c>.
    /// </exception>
    public AppAiWorkflowStrategy(IMessageBus messageBus, string pluginId)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
    }

    /// <summary>
    /// Configures the AI workflow for an application by storing the configuration locally
    /// and sending it to UltimateIntelligence via the <c>intelligence.workflow.configure</c>
    /// message bus topic.
    /// </summary>
    /// <param name="config">The AI workflow configuration for the application.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UltimateIntelligence confirming the configuration.</returns>
    public async Task<MessageResponse> ConfigureWorkflowAsync(AppAiWorkflowConfig config, CancellationToken ct = default)
    {
        _configs[config.AppId] = config;

        // Initialize usage tracking if not already present
        _usageTracking.TryAdd(config.AppId, new AiUsageTracking
        {
            AppId = config.AppId,
            PeriodStart = GetCurrentPeriodStart()
        });

        _concurrentCounts.TryAdd(config.AppId, 0);

        var response = await _messageBus.SendAsync("intelligence.workflow.configure", new PluginMessage
        {
            Type = "intelligence.workflow.configure",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["AppId"] = config.AppId,
                ["Mode"] = config.Mode.ToString(),
                ["PreferredProvider"] = config.PreferredProvider ?? string.Empty,
                ["PreferredModel"] = config.PreferredModel ?? string.Empty,
                ["MaxConcurrentRequests"] = config.MaxConcurrentRequests,
                ["AllowedOperations"] = config.AllowedOperations,
                ["BudgetLimitPerMonth"] = config.BudgetLimitPerMonth?.ToString() ?? string.Empty,
                ["BudgetLimitPerRequest"] = config.BudgetLimitPerRequest?.ToString() ?? string.Empty,
                ["RequireApproval"] = config.RequireApproval
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Removes the AI workflow configuration for the specified application and notifies
    /// UltimateIntelligence via the <c>intelligence.workflow.remove</c> message bus topic.
    /// </summary>
    /// <param name="appId">The application identifier whose AI workflow should be removed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UltimateIntelligence confirming the removal.</returns>
    public async Task<MessageResponse> RemoveWorkflowAsync(string appId, CancellationToken ct = default)
    {
        _configs.TryRemove(appId, out _);
        _usageTracking.TryRemove(appId, out _);
        _concurrentCounts.TryRemove(appId, out _);

        var response = await _messageBus.SendAsync("intelligence.workflow.remove", new PluginMessage
        {
            Type = "intelligence.workflow.remove",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["AppId"] = appId
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Retrieves the current AI workflow configuration for the specified application.
    /// </summary>
    /// <param name="appId">The application identifier to look up.</param>
    /// <returns>The <see cref="AppAiWorkflowConfig"/> if found; <c>null</c> otherwise.</returns>
    public Task<AppAiWorkflowConfig?> GetWorkflowAsync(string appId)
    {
        _configs.TryGetValue(appId, out var config);
        return Task.FromResult<AppAiWorkflowConfig?>(config);
    }

    /// <summary>
    /// Updates an existing AI workflow configuration by replacing it locally, setting
    /// the <see cref="AppAiWorkflowConfig.UpdatedAt"/> timestamp, and re-sending the
    /// configuration to UltimateIntelligence.
    /// </summary>
    /// <param name="updatedConfig">The updated AI workflow configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UltimateIntelligence confirming the update.</returns>
    public async Task<MessageResponse> UpdateWorkflowAsync(AppAiWorkflowConfig updatedConfig, CancellationToken ct = default)
    {
        var withTimestamp = updatedConfig with { UpdatedAt = DateTime.UtcNow };
        _configs[withTimestamp.AppId] = withTimestamp;

        return await ConfigureWorkflowAsync(withTimestamp, ct);
    }

    /// <summary>
    /// Processes an AI request for the specified application by enforcing all configured
    /// constraints (budget, concurrency, operation, approval) before forwarding the enriched
    /// request to UltimateIntelligence via the <c>intelligence.request</c> message bus topic.
    /// </summary>
    /// <param name="appId">The application identifier making the request.</param>
    /// <param name="originalRequest">The original AI request message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="MessageResponse"/> containing either the Intelligence response,
    /// an approval-required notification, or an error describing the constraint violation.
    /// </returns>
    public async Task<MessageResponse> ProcessAiRequestAsync(
        string appId,
        PluginMessage originalRequest,
        CancellationToken ct = default)
    {
        // Get config for app
        if (!_configs.TryGetValue(appId, out var config))
        {
            return MessageResponse.Error("No AI workflow configured for app", "AI_NO_CONFIG");
        }

        // Ensure usage tracking period is current
        EnsureCurrentPeriod(appId);

        // Budget enforcement: monthly limit
        if (config.BudgetLimitPerMonth.HasValue)
        {
            if (_usageTracking.TryGetValue(appId, out var usage) &&
                usage.TotalSpentThisMonth >= config.BudgetLimitPerMonth.Value)
            {
                return MessageResponse.Error(
                    $"Monthly AI budget exceeded: spent {usage.TotalSpentThisMonth:C} of {config.BudgetLimitPerMonth.Value:C} limit",
                    "AI_BUDGET_EXCEEDED");
            }
        }

        // Concurrency enforcement
        _concurrentCounts.TryGetValue(appId, out var currentConcurrent);
        if (currentConcurrent >= config.MaxConcurrentRequests)
        {
            return MessageResponse.Error(
                $"Max concurrent AI requests reached: {config.MaxConcurrentRequests}",
                "AI_CONCURRENCY_EXCEEDED");
        }

        // Operation enforcement
        var operation = "chat"; // Default operation
        if (originalRequest.Payload.TryGetValue("Operation", out var opObj))
        {
            operation = opObj?.ToString() ?? "chat";
        }

        if (!config.AllowedOperations.Contains(operation, StringComparer.OrdinalIgnoreCase))
        {
            return MessageResponse.Error(
                $"Operation not allowed for this app: '{operation}'. Allowed: {string.Join(", ", config.AllowedOperations)}",
                "AI_OPERATION_NOT_ALLOWED");
        }

        // Approval enforcement
        if (config.RequireApproval && config.BudgetLimitPerRequest.HasValue)
        {
            // Check estimated cost from request payload
            var estimatedCost = 0m;
            if (originalRequest.Payload.TryGetValue("EstimatedCost", out var costObj))
            {
                estimatedCost = costObj switch
                {
                    decimal d => d,
                    double dbl => (decimal)dbl,
                    int i => i,
                    long l => l,
                    string s when decimal.TryParse(s, out var parsed) => parsed,
                    _ => 0m
                };
            }

            if (estimatedCost > config.BudgetLimitPerRequest.Value)
            {
                return MessageResponse.Ok(new Dictionary<string, object>
                {
                    ["NeedsApproval"] = true,
                    ["EstimatedCost"] = estimatedCost,
                    ["BudgetLimitPerRequest"] = config.BudgetLimitPerRequest.Value,
                    ["AppId"] = appId,
                    ["Operation"] = operation,
                    ["Reason"] = $"Estimated cost {estimatedCost:C} exceeds per-request limit {config.BudgetLimitPerRequest.Value:C}"
                });
            }
        }

        // Increment concurrent count atomically
        _concurrentCounts.AddOrUpdate(appId, 1, (_, current) => Interlocked.Increment(ref current));

        try
        {
            // Build enriched message with AI mode, preferences, and budget info
            var enrichedPayload = new Dictionary<string, object>(originalRequest.Payload)
            {
                ["AiMode"] = config.Mode.ToString(),
                ["AppId"] = appId,
                ["PreferredProvider"] = config.PreferredProvider ?? string.Empty,
                ["PreferredModel"] = config.PreferredModel ?? string.Empty
            };

            if (config.BudgetLimitPerRequest.HasValue)
            {
                enrichedPayload["BudgetLimitPerRequest"] = config.BudgetLimitPerRequest.Value;
            }

            var enrichedMessage = new PluginMessage
            {
                Type = "intelligence.request",
                SourcePluginId = _pluginId,
                Payload = enrichedPayload,
                CorrelationId = originalRequest.CorrelationId
            };

            // Forward to Intelligence
            var response = await _messageBus.SendAsync("intelligence.request", enrichedMessage, ct);

            // Update usage tracking on success
            if (response.Success)
            {
                var cost = 0m;
                if (response.Metadata.TryGetValue("Cost", out var responseCost))
                {
                    cost = responseCost switch
                    {
                        decimal d => d,
                        double dbl => (decimal)dbl,
                        string s when decimal.TryParse(s, out var parsed) => parsed,
                        _ => 0m
                    };
                }

                UpdateUsageTracking(appId, cost);
            }

            return response;
        }
        finally
        {
            // Decrement concurrent count atomically
            _concurrentCounts.AddOrUpdate(appId, 0, (_, current) => Math.Max(0, Interlocked.Decrement(ref current)));
        }
    }

    /// <summary>
    /// Resets the monthly AI usage tracking for the specified application.
    /// Zeroes out <see cref="AiUsageTracking.TotalSpentThisMonth"/> and
    /// <see cref="AiUsageTracking.RequestCountThisMonth"/>, and sets a new period start.
    /// </summary>
    /// <param name="appId">The application identifier whose usage should be reset.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task ResetMonthlyUsageAsync(string appId)
    {
        if (_usageTracking.TryGetValue(appId, out _))
        {
            _usageTracking[appId] = new AiUsageTracking
            {
                AppId = appId,
                PeriodStart = GetCurrentPeriodStart()
            };
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Retrieves the current AI usage tracking data for the specified application.
    /// </summary>
    /// <param name="appId">The application identifier to look up.</param>
    /// <returns>The <see cref="AiUsageTracking"/> if found; <c>null</c> otherwise.</returns>
    public Task<AiUsageTracking?> GetUsageAsync(string appId)
    {
        _usageTracking.TryGetValue(appId, out var usage);

        // Merge in the live concurrent count
        if (usage is not null && _concurrentCounts.TryGetValue(appId, out var concurrent))
        {
            usage = usage with { ActiveConcurrentRequests = concurrent };
        }

        return Task.FromResult(usage);
    }

    /// <summary>
    /// Updates the usage tracking for an application after a successful AI request.
    /// Increments the request count and adds the cost to the monthly total.
    /// </summary>
    /// <param name="appId">The application identifier.</param>
    /// <param name="cost">The cost of the completed request in USD.</param>
    private void UpdateUsageTracking(string appId, decimal cost)
    {
        lock (_usageLock)
        {
            if (_usageTracking.TryGetValue(appId, out var current))
            {
                _usageTracking[appId] = current with
                {
                    TotalSpentThisMonth = current.TotalSpentThisMonth + cost,
                    RequestCountThisMonth = current.RequestCountThisMonth + 1,
                    LastRequestTime = DateTime.UtcNow
                };
            }
        }
    }

    /// <summary>
    /// Ensures the usage tracking period is current. If the period start is in a previous
    /// month, resets the monthly counters automatically.
    /// </summary>
    /// <param name="appId">The application identifier.</param>
    private void EnsureCurrentPeriod(string appId)
    {
        if (_usageTracking.TryGetValue(appId, out var usage))
        {
            var currentPeriodStart = GetCurrentPeriodStart();
            if (usage.PeriodStart < currentPeriodStart)
            {
                _usageTracking[appId] = new AiUsageTracking
                {
                    AppId = appId,
                    PeriodStart = currentPeriodStart
                };
            }
        }
    }

    /// <summary>
    /// Gets the start of the current billing period (first day of the current month at midnight UTC).
    /// </summary>
    /// <returns>The UTC <see cref="DateTime"/> representing the start of the current billing period.</returns>
    private static DateTime GetCurrentPeriodStart()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
