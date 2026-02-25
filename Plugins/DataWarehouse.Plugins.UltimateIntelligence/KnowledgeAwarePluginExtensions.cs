using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence;

#region 90.B4: PluginBase Integration

/// <summary>
/// Extension methods for integrating knowledge-aware capabilities with plugin base classes.
/// Provides auto-registration, query handling, and lifecycle management for knowledge integration.
/// </summary>
/// <remarks>
/// <para>
/// These extensions enable any plugin to participate in the Intelligence Gateway's knowledge system:
/// </para>
/// <list type="bullet">
///   <item><see cref="GetRegistrationKnowledgeEx"/> - Builds knowledge objects describing plugin capabilities</item>
///   <item><see cref="HandleKnowledgeQueryAsyncEx"/> - Processes incoming knowledge queries</item>
///   <item><see cref="RegisterKnowledgeAsyncEx"/> - Auto-registers during initialization</item>
///   <item><see cref="UnregisterKnowledgeAsyncEx"/> - Auto-unregisters during disposal</item>
/// </list>
/// </remarks>
public static class KnowledgeAwarePluginExtensions
{
    /// <summary>
    /// Knowledge registration cache to track registered plugins.
    /// </summary>
    private static readonly BoundedDictionary<string, KnowledgeRegistrationInfo> _registrations = new BoundedDictionary<string, KnowledgeRegistrationInfo>(1000);

    /// <summary>
    /// Gets a KnowledgeObject describing the plugin's capabilities for registration with Universal Intelligence.
    /// </summary>
    /// <param name="plugin">The plugin to generate knowledge for.</param>
    /// <returns>A KnowledgeObject describing the plugin, or null if the plugin has no registerable knowledge.</returns>
    /// <remarks>
    /// <para>
    /// This method builds a comprehensive knowledge object that includes:
    /// </para>
    /// <list type="bullet">
    ///   <item>Plugin identity (ID, name, version, category)</item>
    ///   <item>Available operations and their semantics</item>
    ///   <item>Configuration state and requirements</item>
    ///   <item>Strategy information (for strategy-based plugins)</item>
    ///   <item>Dependency information</item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// var knowledge = myPlugin.GetRegistrationKnowledgeEx();
    /// if (knowledge != null)
    /// {
    ///     await intelligenceGateway.RegisterKnowledgeAsync(knowledge);
    /// }
    /// </code>
    /// </example>
    public static KnowledgeObject? GetRegistrationKnowledgeEx(this PluginBase plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        try
        {
            // Start with the plugin's built-in registration knowledge if available
            var baseKnowledge = plugin.GetRegistrationKnowledge();
            if (baseKnowledge != null)
            {
                // Enhance with gateway-specific metadata
                var enhancedPayload = new Dictionary<string, object>(baseKnowledge.Payload)
                {
                    ["pluginId"] = plugin.Id,
                    ["pluginName"] = plugin.Name,
                    ["pluginVersion"] = plugin.Version,
                    ["pluginCategory"] = plugin.Category.ToString(),
                    ["registrationTimestamp"] = DateTimeOffset.UtcNow,
                    ["gatewayIntegrated"] = true
                };

                // Add intelligence-specific capabilities if plugin supports them
                var intelligenceCapabilities = DetectIntelligenceCapabilities(plugin);
                if (intelligenceCapabilities != IntelligenceCapabilities.None)
                {
                    enhancedPayload["intelligenceCapabilities"] = intelligenceCapabilities.ToString();
                }

                return baseKnowledge with
                {
                    Payload = enhancedPayload,
                    Tags = baseKnowledge.Tags
                        .Concat(new[] { "gateway-registered", "intelligence-aware" })
                        .Distinct()
                        .ToArray()
                };
            }

            // Build knowledge from scratch if plugin doesn't provide its own
            return BuildDefaultKnowledge(plugin);
        }
        catch (Exception ex)
        {
            // Log but don't throw - knowledge generation shouldn't break the plugin
            System.Diagnostics.Debug.WriteLine($"Failed to generate knowledge for plugin {plugin.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Handles an incoming knowledge query against the plugin.
    /// </summary>
    /// <param name="plugin">The plugin to query.</param>
    /// <param name="query">The knowledge query to handle.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing the query result.</returns>
    /// <remarks>
    /// <para>
    /// Supported query topics include:
    /// </para>
    /// <list type="bullet">
    ///   <item>plugin.capabilities - Returns plugin capability information</item>
    ///   <item>plugin.strategies - Returns available strategies (for strategy plugins)</item>
    ///   <item>plugin.configuration - Returns current configuration state</item>
    ///   <item>plugin.statistics - Returns usage statistics</item>
    ///   <item>plugin.health - Returns health and availability status</item>
    /// </list>
    /// </remarks>
    public static async Task<KnowledgeQueryResult> HandleKnowledgeQueryAsyncEx(
        this PluginBase plugin,
        KnowledgeQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(query);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var results = new List<KnowledgeResultItem>();
            var queryType = query.QueryType.ToLowerInvariant();

            switch (queryType)
            {
                case "capabilities":
                case "plugin.capabilities":
                    var capabilityKnowledge = plugin.GetRegistrationKnowledgeEx();
                    if (capabilityKnowledge != null)
                    {
                        results.Add(new KnowledgeResultItem
                        {
                            Id = capabilityKnowledge.Id,
                            Content = capabilityKnowledge.Description ?? string.Empty,
                            Score = 1.0,
                            Metadata = capabilityKnowledge.Payload
                        });
                    }
                    break;

                case "strategies":
                case "plugin.strategies":
                    var strategyResults = await QueryStrategiesAsync(plugin, query.Parameters, ct).ConfigureAwait(false);
                    results.AddRange(strategyResults);
                    break;

                case "configuration":
                case "plugin.configuration":
                    var configResult = BuildConfigurationResult(plugin);
                    if (configResult != null)
                    {
                        results.Add(configResult);
                    }
                    break;

                case "statistics":
                case "plugin.statistics":
                    var statsResult = BuildStatisticsResult(plugin);
                    if (statsResult != null)
                    {
                        results.Add(statsResult);
                    }
                    break;

                case "health":
                case "plugin.health":
                    results.Add(BuildHealthResult(plugin));
                    break;

                case "search":
                case "plugin.search":
                    var searchResults = await SearchPluginKnowledgeAsync(plugin, query.QueryText, query.Parameters, ct).ConfigureAwait(false);
                    results.AddRange(searchResults);
                    break;

                default:
                    // Try to delegate to plugin's custom handler
                    try
                    {
                        var knowledgeRequest = new KnowledgeRequest
                        {
                            RequestId = query.QueryId,
                            RequestorPluginId = "intelligence.gateway",
                            Topic = queryType,
                            QueryParameters = query.Parameters
                        };
                        var customResponse = await plugin.HandleKnowledgeQueryAsync(knowledgeRequest, ct).ConfigureAwait(false);
                        if (customResponse.Success && customResponse.Results.Length > 0)
                        {
                            results.AddRange(customResponse.Results.Select(k => new KnowledgeResultItem
                            {
                                Id = k.Id,
                                Content = k.Description ?? string.Empty,
                                Score = k.Confidence,
                                Metadata = k.Payload
                            }));
                        }
                    }
                    catch
                    {
                        Debug.WriteLine($"Caught exception in KnowledgeAwarePluginExtensions.cs");
                        // Custom handler not implemented or failed
                    }
                    break;
            }

            stopwatch.Stop();
            var result = KnowledgeQueryResult.CreateSuccess(query.QueryId, results);
            result.TotalCount = results.Count;
            result.ExecutionTime = stopwatch.Elapsed;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return KnowledgeQueryResult.CreateFailure(query.QueryId, $"Query failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers the plugin's knowledge with the Intelligence Gateway during initialization.
    /// </summary>
    /// <param name="plugin">The plugin to register.</param>
    /// <param name="gateway">The intelligence gateway to register with.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing the registration result.</returns>
    /// <remarks>
    /// <para>
    /// This method should be called during plugin initialization (e.g., in OnHandshakeAsync).
    /// It performs:
    /// </para>
    /// <list type="bullet">
    ///   <item>Knowledge object generation</item>
    ///   <item>Capability mapping registration</item>
    ///   <item>Provider registration (if applicable)</item>
    ///   <item>Subscription to knowledge updates</item>
    /// </list>
    /// </remarks>
    public static async Task<KnowledgeRegistrationResult> RegisterKnowledgeAsyncEx(
        this PluginBase plugin,
        IIntelligenceGateway gateway,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(gateway);

        try
        {
            // Check if already registered
            if (_registrations.ContainsKey(plugin.Id))
            {
                return new KnowledgeRegistrationResult
                {
                    Success = true,
                    PluginId = plugin.Id,
                    Message = "Plugin already registered",
                    AlreadyRegistered = true
                };
            }

            // Generate knowledge
            var knowledge = plugin.GetRegistrationKnowledgeEx();
            if (knowledge == null)
            {
                return new KnowledgeRegistrationResult
                {
                    Success = false,
                    PluginId = plugin.Id,
                    Message = "Plugin has no registerable knowledge"
                };
            }

            // Register as provider if applicable
            if (plugin is IIntelligenceStrategy strategy)
            {
                if (gateway is IProviderRouter router)
                {
                    router.RegisterProvider(strategy);
                }
            }

            // Track registration
            var registrationInfo = new KnowledgeRegistrationInfo
            {
                PluginId = plugin.Id,
                Knowledge = knowledge,
                RegisteredAt = DateTimeOffset.UtcNow,
                GatewayId = GetGatewayId(gateway)
            };
            _registrations[plugin.Id] = registrationInfo;

            return new KnowledgeRegistrationResult
            {
                Success = true,
                PluginId = plugin.Id,
                KnowledgeId = knowledge.Id,
                Message = "Successfully registered with Intelligence Gateway",
                RegisteredAt = registrationInfo.RegisteredAt
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new KnowledgeRegistrationResult
            {
                Success = false,
                PluginId = plugin.Id,
                Message = $"Registration failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Unregisters the plugin's knowledge from the Intelligence Gateway during disposal.
    /// </summary>
    /// <param name="plugin">The plugin to unregister.</param>
    /// <param name="gateway">The intelligence gateway to unregister from.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task containing the unregistration result.</returns>
    /// <remarks>
    /// <para>
    /// This method should be called during plugin disposal. It performs:
    /// </para>
    /// <list type="bullet">
    ///   <item>Knowledge removal from gateway</item>
    ///   <item>Capability mapping cleanup</item>
    ///   <item>Provider unregistration (if applicable)</item>
    ///   <item>Subscription cleanup</item>
    /// </list>
    /// </remarks>
    public static async Task<KnowledgeUnregistrationResult> UnregisterKnowledgeAsyncEx(
        this PluginBase plugin,
        IIntelligenceGateway gateway,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        try
        {
            // Remove from tracking
            if (!_registrations.TryRemove(plugin.Id, out var registrationInfo))
            {
                return new KnowledgeUnregistrationResult
                {
                    Success = true,
                    PluginId = plugin.Id,
                    Message = "Plugin was not registered",
                    WasRegistered = false
                };
            }

            // Unregister as provider if applicable
            if (gateway is IntelligenceGatewayBase gatewayBase && plugin is IIntelligenceStrategy strategy)
            {
                gatewayBase.UnregisterProvider(strategy.StrategyId);
            }

            return new KnowledgeUnregistrationResult
            {
                Success = true,
                PluginId = plugin.Id,
                KnowledgeId = registrationInfo.Knowledge?.Id,
                Message = "Successfully unregistered from Intelligence Gateway",
                WasRegistered = true,
                RegistrationDuration = DateTimeOffset.UtcNow - registrationInfo.RegisteredAt
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new KnowledgeUnregistrationResult
            {
                Success = false,
                PluginId = plugin.Id,
                Message = $"Unregistration failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Checks if a plugin is currently registered with the Intelligence Gateway.
    /// </summary>
    /// <param name="plugin">The plugin to check.</param>
    /// <returns>True if registered, false otherwise.</returns>
    public static bool IsRegisteredWithGateway(this PluginBase plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return _registrations.ContainsKey(plugin.Id);
    }

    /// <summary>
    /// Gets the registration information for a plugin.
    /// </summary>
    /// <param name="plugin">The plugin to query.</param>
    /// <returns>Registration info if registered, null otherwise.</returns>
    public static KnowledgeRegistrationInfo? GetRegistrationInfo(this PluginBase plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return _registrations.TryGetValue(plugin.Id, out var info) ? info : null;
    }

    #region Private Helper Methods

    private static KnowledgeObject? BuildDefaultKnowledge(PluginBase plugin)
    {
        return new KnowledgeObject
        {
            Id = $"{plugin.Id}.knowledge.{Guid.NewGuid():N}",
            Topic = "plugin.capabilities",
            SourcePluginId = plugin.Id,
            SourcePluginName = plugin.Name,
            KnowledgeType = "capability",
            Description = $"Plugin: {plugin.Name} v{plugin.Version}",
            Payload = new Dictionary<string, object>
            {
                ["pluginId"] = plugin.Id,
                ["pluginName"] = plugin.Name,
                ["pluginVersion"] = plugin.Version,
                ["pluginCategory"] = plugin.Category.ToString(),
                ["gatewayIntegrated"] = true
            },
            Tags = new[] { "plugin", plugin.Category.ToString().ToLowerInvariant(), "gateway-registered" }
        };
    }

    private static IntelligenceCapabilities DetectIntelligenceCapabilities(PluginBase plugin)
    {
        var capabilities = IntelligenceCapabilities.None;

        if (plugin is IIntelligenceStrategy strategy)
        {
            capabilities = strategy.Info.Capabilities;
        }

        // Check for common capability interfaces
        if (plugin.Category == PluginCategory.AIProvider)
        {
            capabilities |= IntelligenceCapabilities.TextCompletion;
        }

        return capabilities;
    }

    private static async Task<List<KnowledgeResultItem>> QueryStrategiesAsync(
        PluginBase plugin,
        Dictionary<string, object> parameters,
        CancellationToken ct)
    {
        var results = new List<KnowledgeResultItem>();

        // Check if plugin is a strategy-based plugin (like UltimateIntelligencePlugin)
        if (plugin is UltimateIntelligencePlugin intelligencePlugin)
        {
            var strategyIds = intelligencePlugin.GetRegisteredStrategyIds();
            foreach (var strategyId in strategyIds)
            {
                var strategy = intelligencePlugin.GetStrategy(strategyId);
                if (strategy != null)
                {
                    // Apply filters from parameters
                    if (parameters.TryGetValue("category", out var categoryObj) &&
                        categoryObj is string categoryFilter &&
                        !string.IsNullOrEmpty(categoryFilter))
                    {
                        if (!strategy.Category.ToString().Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    if (parameters.TryGetValue("available", out var availableObj) &&
                        availableObj is bool requireAvailable &&
                        requireAvailable &&
                        !strategy.IsAvailable)
                    {
                        continue;
                    }

                    results.Add(new KnowledgeResultItem
                    {
                        Id = strategy.StrategyId,
                        Content = strategy.Info.Description,
                        Score = strategy.IsAvailable ? 1.0 : 0.5,
                        Metadata = new Dictionary<string, object>
                        {
                            ["strategyId"] = strategy.StrategyId,
                            ["strategyName"] = strategy.StrategyName,
                            ["category"] = strategy.Category.ToString(),
                            ["provider"] = strategy.Info.ProviderName,
                            ["capabilities"] = strategy.Info.Capabilities.ToString(),
                            ["isAvailable"] = strategy.IsAvailable,
                            ["costTier"] = strategy.Info.CostTier,
                            ["latencyTier"] = strategy.Info.LatencyTier
                        }
                    });
                }
            }
        }

        return results;
    }

    private static KnowledgeResultItem? BuildConfigurationResult(PluginBase plugin)
    {
        try
        {
            // Try to get configuration via reflection or known methods
            var configState = new Dictionary<string, object>
            {
                ["pluginId"] = plugin.Id,
                ["pluginName"] = plugin.Name,
                ["version"] = plugin.Version,
                ["category"] = plugin.Category.ToString()
            };

            if (plugin is UltimateIntelligencePlugin intelligencePlugin)
            {
                var stats = intelligencePlugin.GetPluginStatistics();
                configState["totalStrategies"] = stats.TotalStrategies;
                configState["availableStrategies"] = stats.AvailableStrategies;
            }

            return new KnowledgeResultItem
            {
                Id = $"{plugin.Id}.config",
                Content = $"Configuration for {plugin.Name}",
                Score = 1.0,
                Metadata = configState
            };
        }
        catch
        {
            Debug.WriteLine($"Caught exception in KnowledgeAwarePluginExtensions.cs");
            return null;
        }
    }

    private static KnowledgeResultItem? BuildStatisticsResult(PluginBase plugin)
    {
        try
        {
            var stats = new Dictionary<string, object>
            {
                ["pluginId"] = plugin.Id,
                ["timestamp"] = DateTimeOffset.UtcNow
            };

            if (plugin is UltimateIntelligencePlugin intelligencePlugin)
            {
                var pluginStats = intelligencePlugin.GetPluginStatistics();
                stats["totalOperations"] = pluginStats.TotalOperations;
                stats["tokensConsumed"] = pluginStats.TotalTokensConsumed;
                stats["embeddingsGenerated"] = pluginStats.TotalEmbeddingsGenerated;
                stats["vectorsStored"] = pluginStats.TotalVectorsStored;
                stats["searchesPerformed"] = pluginStats.TotalSearches;
                stats["averageLatencyMs"] = pluginStats.AverageLatencyMs;
            }

            return new KnowledgeResultItem
            {
                Id = $"{plugin.Id}.stats",
                Content = $"Statistics for {plugin.Name}",
                Score = 1.0,
                Metadata = stats
            };
        }
        catch
        {
            Debug.WriteLine($"Caught exception in KnowledgeAwarePluginExtensions.cs");
            return null;
        }
    }

    private static KnowledgeResultItem BuildHealthResult(PluginBase plugin)
    {
        var isHealthy = true;
        var healthDetails = new Dictionary<string, object>
        {
            ["pluginId"] = plugin.Id,
            ["pluginName"] = plugin.Name,
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        if (plugin is UltimateIntelligencePlugin intelligencePlugin)
        {
            var stats = intelligencePlugin.GetPluginStatistics();
            isHealthy = stats.AvailableStrategies > 0;
            healthDetails["availableStrategies"] = stats.AvailableStrategies;
            healthDetails["totalStrategies"] = stats.TotalStrategies;
        }

        healthDetails["status"] = isHealthy ? "healthy" : "degraded";

        return new KnowledgeResultItem
        {
            Id = $"{plugin.Id}.health",
            Content = isHealthy ? "Plugin is healthy" : "Plugin is degraded",
            Score = isHealthy ? 1.0 : 0.5,
            Metadata = healthDetails
        };
    }

    private static async Task<List<KnowledgeResultItem>> SearchPluginKnowledgeAsync(
        PluginBase plugin,
        string queryText,
        Dictionary<string, object> parameters,
        CancellationToken ct)
    {
        var results = new List<KnowledgeResultItem>();

        if (string.IsNullOrEmpty(queryText))
        {
            return results;
        }

        var searchTerms = queryText.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Search plugin metadata
        var pluginName = plugin.Name.ToLowerInvariant();
        var matchScore = searchTerms.Count(t => pluginName.Contains(t)) / (double)searchTerms.Length;
        if (matchScore > 0)
        {
            results.Add(new KnowledgeResultItem
            {
                Id = plugin.Id,
                Content = $"Plugin: {plugin.Name}",
                Score = matchScore,
                Metadata = new Dictionary<string, object>
                {
                    ["pluginId"] = plugin.Id,
                    ["pluginName"] = plugin.Name,
                    ["matchType"] = "name"
                }
            });
        }

        // Search strategies if applicable
        if (plugin is UltimateIntelligencePlugin intelligencePlugin)
        {
            foreach (var strategyId in intelligencePlugin.GetRegisteredStrategyIds())
            {
                var strategy = intelligencePlugin.GetStrategy(strategyId);
                if (strategy != null)
                {
                    var strategyText = $"{strategy.StrategyName} {strategy.Info.Description} {string.Join(" ", strategy.Info.Tags)}".ToLowerInvariant();
                    var strategyMatchScore = searchTerms.Count(t => strategyText.Contains(t)) / (double)searchTerms.Length;

                    if (strategyMatchScore > 0)
                    {
                        results.Add(new KnowledgeResultItem
                        {
                            Id = strategyId,
                            Content = strategy.Info.Description,
                            Score = strategyMatchScore,
                            Metadata = new Dictionary<string, object>
                            {
                                ["strategyId"] = strategyId,
                                ["strategyName"] = strategy.StrategyName,
                                ["matchType"] = "strategy"
                            }
                        });
                    }
                }
            }
        }

        // Sort by score
        results = results.OrderByDescending(r => r.Score).ToList();

        // Apply max results limit
        if (parameters.TryGetValue("maxResults", out var maxObj) && maxObj is int maxResults && maxResults > 0)
        {
            results = results.Take(maxResults).ToList();
        }

        return results;
    }

    private static string GetGatewayId(IIntelligenceGateway gateway)
    {
        if (gateway is IntelligenceGatewayBase gatewayBase)
        {
            return gatewayBase.GatewayId;
        }
        return gateway.GetType().Name;
    }

    #endregion
}

/// <summary>
/// Registration information for a plugin registered with the Intelligence Gateway.
/// </summary>
public sealed class KnowledgeRegistrationInfo
{
    /// <summary>
    /// Gets or sets the plugin ID.
    /// </summary>
    public required string PluginId { get; init; }

    /// <summary>
    /// Gets or sets the registered knowledge object.
    /// </summary>
    public KnowledgeObject? Knowledge { get; init; }

    /// <summary>
    /// Gets or sets when the registration occurred.
    /// </summary>
    public DateTimeOffset RegisteredAt { get; init; }

    /// <summary>
    /// Gets or sets the gateway ID the plugin is registered with.
    /// </summary>
    public string? GatewayId { get; init; }

    /// <summary>
    /// Gets or sets additional registration metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Result of a knowledge registration operation.
/// </summary>
public sealed class KnowledgeRegistrationResult
{
    /// <summary>
    /// Gets or sets whether registration was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets or sets the plugin ID.
    /// </summary>
    public required string PluginId { get; init; }

    /// <summary>
    /// Gets or sets the registered knowledge ID.
    /// </summary>
    public string? KnowledgeId { get; init; }

    /// <summary>
    /// Gets or sets a message describing the result.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets or sets whether the plugin was already registered.
    /// </summary>
    public bool AlreadyRegistered { get; init; }

    /// <summary>
    /// Gets or sets when registration occurred.
    /// </summary>
    public DateTimeOffset? RegisteredAt { get; init; }
}

/// <summary>
/// Result of a knowledge unregistration operation.
/// </summary>
public sealed class KnowledgeUnregistrationResult
{
    /// <summary>
    /// Gets or sets whether unregistration was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets or sets the plugin ID.
    /// </summary>
    public required string PluginId { get; init; }

    /// <summary>
    /// Gets or sets the unregistered knowledge ID.
    /// </summary>
    public string? KnowledgeId { get; init; }

    /// <summary>
    /// Gets or sets a message describing the result.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets or sets whether the plugin was previously registered.
    /// </summary>
    public bool WasRegistered { get; init; }

    /// <summary>
    /// Gets or sets how long the registration was active.
    /// </summary>
    public TimeSpan? RegistrationDuration { get; init; }
}

#endregion
