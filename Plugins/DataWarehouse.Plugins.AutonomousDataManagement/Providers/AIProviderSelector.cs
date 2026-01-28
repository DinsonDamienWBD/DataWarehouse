using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Providers;

/// <summary>
/// Configuration interface for AIOps provider selection.
/// </summary>
public interface IAIOpsProviderConfig
{
    /// <summary>
    /// Default provider ID to use when no capability-specific mapping exists.
    /// </summary>
    string DefaultProviderId { get; }

    /// <summary>
    /// Maps specific capabilities to preferred providers.
    /// Key: capability name (e.g., "anomaly-detection", "cost-optimization")
    /// Value: provider ID (e.g., "openai", "anthropic", "ollama")
    /// </summary>
    Dictionary<string, string> CapabilityProviderMap { get; }

    /// <summary>
    /// Fallback providers in order of preference.
    /// </summary>
    List<string> FallbackProviders { get; }

    /// <summary>
    /// Model preferences per provider.
    /// </summary>
    Dictionary<string, string> ProviderModelMap { get; }
}

/// <summary>
/// Default implementation of AIOps provider configuration.
/// </summary>
public sealed class AIOpsProviderConfig : IAIOpsProviderConfig
{
    public string DefaultProviderId { get; set; } = "openai";

    public Dictionary<string, string> CapabilityProviderMap { get; set; } = new()
    {
        ["anomaly-detection"] = "openai",
        ["tiering-optimization"] = "openai",
        ["cost-optimization"] = "openai",
        ["predictive-scaling"] = "openai",
        ["security-analysis"] = "anthropic",
        ["compliance-analysis"] = "anthropic",
        ["capacity-planning"] = "openai",
        ["performance-tuning"] = "openai"
    };

    public List<string> FallbackProviders { get; set; } = new() { "openai", "anthropic", "ollama" };

    public Dictionary<string, string> ProviderModelMap { get; set; } = new()
    {
        ["openai"] = "gpt-4o",
        ["anthropic"] = "claude-sonnet-4-20250514",
        ["ollama"] = "llama3.1"
    };
}

/// <summary>
/// Capability types for AIOps operations.
/// </summary>
public static class AIOpsCapabilities
{
    public const string AnomalyDetection = "anomaly-detection";
    public const string TieringOptimization = "tiering-optimization";
    public const string CostOptimization = "cost-optimization";
    public const string PredictiveScaling = "predictive-scaling";
    public const string SecurityAnalysis = "security-analysis";
    public const string ComplianceAnalysis = "compliance-analysis";
    public const string CapacityPlanning = "capacity-planning";
    public const string PerformanceTuning = "performance-tuning";
    public const string FailurePrediction = "failure-prediction";
    public const string QueryOptimization = "query-optimization";
}

/// <summary>
/// Selects the appropriate AI provider based on capability requirements and configuration.
/// Uses SDK's IAIProviderRegistry for provider management.
/// </summary>
public sealed class AIProviderSelector
{
    private readonly IAIProviderRegistry _registry;
    private readonly IAIOpsProviderConfig _config;
    private readonly ConcurrentDictionary<string, ProviderUsageStats> _usageStats;

    /// <summary>
    /// Creates a new provider selector.
    /// </summary>
    public AIProviderSelector(IAIProviderRegistry registry, IAIOpsProviderConfig? config = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _config = config ?? new AIOpsProviderConfig();
        _usageStats = new ConcurrentDictionary<string, ProviderUsageStats>();
    }

    /// <summary>
    /// Gets the best available provider for a specific capability.
    /// </summary>
    /// <param name="capability">The capability required (e.g., "anomaly-detection").</param>
    /// <returns>The selected provider, or null if none available.</returns>
    public IAIProvider? GetProviderForCapability(string capability)
    {
        // Try to get capability-specific provider
        if (_config.CapabilityProviderMap.TryGetValue(capability, out var preferredProviderId))
        {
            var provider = _registry.GetProvider(preferredProviderId);
            if (provider != null && provider.IsAvailable)
            {
                return provider;
            }
        }

        // Fall back to default provider
        var defaultProvider = _registry.GetProvider(_config.DefaultProviderId);
        if (defaultProvider != null && defaultProvider.IsAvailable)
        {
            return defaultProvider;
        }

        // Try fallback providers in order
        foreach (var fallbackId in _config.FallbackProviders)
        {
            var fallback = _registry.GetProvider(fallbackId);
            if (fallback != null && fallback.IsAvailable)
            {
                return fallback;
            }
        }

        // Return any available provider
        return _registry.GetDefaultProvider();
    }

    /// <summary>
    /// Gets a provider with specific capabilities required.
    /// </summary>
    /// <param name="required">Required AI capabilities.</param>
    /// <returns>Provider matching capabilities, or null.</returns>
    public IAIProvider? GetProviderWithCapabilities(AICapabilities required)
    {
        var matching = _registry.GetProvidersWithCapabilities(required);
        return matching.FirstOrDefault(p => p.IsAvailable);
    }

    /// <summary>
    /// Gets the preferred model for a provider.
    /// </summary>
    public string? GetModelForProvider(string providerId)
    {
        return _config.ProviderModelMap.TryGetValue(providerId, out var model) ? model : null;
    }

    /// <summary>
    /// Lists all available providers.
    /// </summary>
    public IEnumerable<ProviderInfo> GetAvailableProviders()
    {
        return _registry.GetAllProviders()
            .Where(p => p.IsAvailable)
            .Select(p => new ProviderInfo
            {
                ProviderId = p.ProviderId,
                DisplayName = p.DisplayName,
                Capabilities = p.Capabilities,
                IsDefault = _registry.GetDefaultProvider()?.ProviderId == p.ProviderId,
                PreferredModel = GetModelForProvider(p.ProviderId)
            });
    }

    /// <summary>
    /// Records usage of a provider for tracking and optimization.
    /// </summary>
    public void RecordUsage(string providerId, string capability, TimeSpan latency, bool success, int tokens = 0)
    {
        var stats = _usageStats.GetOrAdd(providerId, _ => new ProviderUsageStats { ProviderId = providerId });

        stats.TotalRequests++;
        stats.TotalTokens += tokens;
        stats.TotalLatencyMs += (long)latency.TotalMilliseconds;

        if (success)
            stats.SuccessfulRequests++;
        else
            stats.FailedRequests++;

        stats.CapabilityUsage.AddOrUpdate(
            capability,
            1,
            (_, count) => count + 1);
    }

    /// <summary>
    /// Gets usage statistics for all providers.
    /// </summary>
    public IEnumerable<ProviderUsageStats> GetUsageStats()
    {
        return _usageStats.Values.ToList();
    }

    /// <summary>
    /// Creates an AI request for a specific capability.
    /// </summary>
    public AIRequest CreateRequest(string capability, string prompt, string? systemMessage = null)
    {
        var provider = GetProviderForCapability(capability);
        var model = provider != null ? GetModelForProvider(provider.ProviderId) : null;

        return new AIRequest
        {
            Prompt = prompt,
            SystemMessage = systemMessage ?? GetDefaultSystemMessage(capability),
            Model = model,
            MaxTokens = GetMaxTokensForCapability(capability),
            Temperature = GetTemperatureForCapability(capability)
        };
    }

    /// <summary>
    /// Executes an AI request with automatic provider selection and failover.
    /// </summary>
    public async Task<AIResponse> ExecuteWithFailoverAsync(
        string capability,
        string prompt,
        string? systemMessage = null,
        CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var providers = GetProvidersForCapability(capability);

        foreach (var provider in providers)
        {
            try
            {
                var request = new AIRequest
                {
                    Prompt = prompt,
                    SystemMessage = systemMessage ?? GetDefaultSystemMessage(capability),
                    Model = GetModelForProvider(provider.ProviderId),
                    MaxTokens = GetMaxTokensForCapability(capability),
                    Temperature = GetTemperatureForCapability(capability)
                };

                var response = await provider.CompleteAsync(request, ct);
                var latency = DateTime.UtcNow - startTime;

                RecordUsage(provider.ProviderId, capability, latency, response.Success, response.Usage?.TotalTokens ?? 0);

                if (response.Success)
                    return response;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                RecordUsage(provider.ProviderId, capability, DateTime.UtcNow - startTime, false);
                // Continue to next provider
            }
        }

        return new AIResponse
        {
            Success = false,
            ErrorMessage = "All providers failed for capability: " + capability
        };
    }

    private IEnumerable<IAIProvider> GetProvidersForCapability(string capability)
    {
        var providers = new List<IAIProvider>();

        // Add capability-specific provider first
        if (_config.CapabilityProviderMap.TryGetValue(capability, out var preferredId))
        {
            var preferred = _registry.GetProvider(preferredId);
            if (preferred != null && preferred.IsAvailable)
                providers.Add(preferred);
        }

        // Add default provider
        var defaultProvider = _registry.GetProvider(_config.DefaultProviderId);
        if (defaultProvider != null && defaultProvider.IsAvailable && !providers.Contains(defaultProvider))
            providers.Add(defaultProvider);

        // Add fallback providers
        foreach (var fallbackId in _config.FallbackProviders)
        {
            var fallback = _registry.GetProvider(fallbackId);
            if (fallback != null && fallback.IsAvailable && !providers.Contains(fallback))
                providers.Add(fallback);
        }

        return providers;
    }

    private string GetDefaultSystemMessage(string capability)
    {
        return capability switch
        {
            AIOpsCapabilities.AnomalyDetection => "You are an expert at detecting anomalies in system metrics and data patterns. Analyze the provided data and identify any anomalies, providing severity scores and root cause analysis.",
            AIOpsCapabilities.TieringOptimization => "You are a data tiering optimization expert. Analyze access patterns and recommend optimal storage tier placement to balance performance and cost.",
            AIOpsCapabilities.CostOptimization => "You are a cloud cost optimization specialist. Analyze resource usage and spending patterns to identify cost-saving opportunities.",
            AIOpsCapabilities.PredictiveScaling => "You are a capacity planning expert. Analyze historical metrics to predict future resource requirements and recommend scaling actions.",
            AIOpsCapabilities.SecurityAnalysis => "You are a security analyst. Analyze the provided security events and identify potential threats, vulnerabilities, or policy violations.",
            AIOpsCapabilities.ComplianceAnalysis => "You are a compliance auditor. Analyze data and operations against regulatory requirements and identify compliance gaps.",
            AIOpsCapabilities.CapacityPlanning => "You are a capacity planning specialist. Analyze growth trends and forecast future capacity needs.",
            AIOpsCapabilities.PerformanceTuning => "You are a performance optimization expert. Analyze performance metrics and recommend configuration changes for optimal performance.",
            AIOpsCapabilities.FailurePrediction => "You are a reliability engineer. Analyze system health metrics to predict potential failures before they occur.",
            AIOpsCapabilities.QueryOptimization => "You are a database optimization expert. Analyze query patterns and recommend optimizations for better performance.",
            _ => "You are an AI operations assistant helping to optimize data warehouse operations."
        };
    }

    private int GetMaxTokensForCapability(string capability)
    {
        return capability switch
        {
            AIOpsCapabilities.AnomalyDetection => 2048,
            AIOpsCapabilities.SecurityAnalysis => 4096,
            AIOpsCapabilities.ComplianceAnalysis => 4096,
            AIOpsCapabilities.CostOptimization => 2048,
            _ => 2048
        };
    }

    private float GetTemperatureForCapability(string capability)
    {
        return capability switch
        {
            AIOpsCapabilities.AnomalyDetection => 0.1f,  // Deterministic for consistency
            AIOpsCapabilities.SecurityAnalysis => 0.2f,   // Low variance for accuracy
            AIOpsCapabilities.ComplianceAnalysis => 0.1f, // Very deterministic
            AIOpsCapabilities.CostOptimization => 0.3f,   // Allow some creativity
            AIOpsCapabilities.PerformanceTuning => 0.2f,  // Mostly deterministic
            _ => 0.3f
        };
    }
}

/// <summary>
/// Information about an available provider.
/// </summary>
public sealed class ProviderInfo
{
    public string ProviderId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public AICapabilities Capabilities { get; init; }
    public bool IsDefault { get; init; }
    public string? PreferredModel { get; init; }
}

/// <summary>
/// Usage statistics for a provider.
/// </summary>
public sealed class ProviderUsageStats
{
    public string ProviderId { get; init; } = string.Empty;
    public long TotalRequests;
    public long SuccessfulRequests;
    public long FailedRequests;
    public long TotalTokens;
    public long TotalLatencyMs;
    public ConcurrentDictionary<string, long> CapabilityUsage { get; } = new();

    public double AverageLatencyMs => TotalRequests > 0 ? (double)TotalLatencyMs / TotalRequests : 0;
    public double SuccessRate => TotalRequests > 0 ? (double)SuccessfulRequests / TotalRequests : 0;
}
