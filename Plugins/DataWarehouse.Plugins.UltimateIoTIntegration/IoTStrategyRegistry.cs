using System;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIoTIntegration;

/// <summary>
/// Registry for IoT strategies.
/// </summary>
/// <remarks>
/// <b>Migration note (65.4-07):</b> <see cref="UltimateIoTIntegrationPlugin"/> inherits
/// <see cref="DataWarehouse.SDK.Contracts.Hierarchy.StreamingPluginBase"/> which exposes
/// <c>RegisterStrategy</c> and <c>StrategyRegistry</c> from PluginBase for unified lifecycle.
/// Strategies are now also registered via the base-class registry. This custom registry is
/// retained as a typed lookup layer for domain interfaces (IDeviceManagementStrategy,
/// ISensorIngestionStrategy, IProtocolStrategy, etc.) that are not expressible via
/// the generic IStreamingStrategy contract.
/// New plugins should prefer the base-class strategy registry over this class.
/// </remarks>
public sealed class IoTStrategyRegistry
{
    private readonly BoundedDictionary<string, IIoTStrategyBase> _strategies = new BoundedDictionary<string, IIoTStrategyBase>(1000);
    private IMessageBus? _messageBus;

    /// <summary>
    /// Gets the count of registered strategies.
    /// </summary>
    public int Count => _strategies.Count;

    /// <summary>
    /// Registers a strategy.
    /// </summary>
    public void Register(IIoTStrategyBase strategy)
    {
        _strategies[strategy.StrategyId] = strategy;
        strategy.ConfigureIntelligence(_messageBus);
    }

    /// <summary>
    /// Gets a strategy by ID.
    /// </summary>
    public IIoTStrategyBase? GetStrategy(string strategyId)
    {
        _strategies.TryGetValue(strategyId, out var strategy);
        return strategy;
    }

    /// <summary>
    /// Gets a typed strategy by ID.
    /// </summary>
    public T GetStrategy<T>(string strategyId) where T : class, IIoTStrategyBase
    {
        if (!_strategies.TryGetValue(strategyId, out var strategy))
            throw new InvalidOperationException($"Strategy '{strategyId}' not found");

        if (strategy is not T typedStrategy)
            throw new InvalidOperationException($"Strategy '{strategyId}' is not of type {typeof(T).Name}");

        return typedStrategy;
    }

    /// <summary>
    /// Gets all strategies.
    /// </summary>
    public IReadOnlyCollection<IIoTStrategyBase> GetAllStrategies()
    {
        return _strategies.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets strategies by category.
    /// </summary>
    public IReadOnlyCollection<IIoTStrategyBase> GetByCategory(IoTStrategyCategory category)
    {
        return _strategies.Values.Where(s => s.Category == category).ToList().AsReadOnly();
    }

    /// <summary>
    /// Selects the best strategy for a category.
    /// </summary>
    public IIoTStrategyBase? SelectBestStrategy(IoTStrategyCategory category)
    {
        return _strategies.Values
            .Where(s => s.Category == category && s.IsAvailable)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets category summary.
    /// </summary>
    public Dictionary<string, int> GetCategorySummary()
    {
        return _strategies.Values
            .GroupBy(s => s.Category)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
    }

    /// <summary>
    /// Configures Intelligence for all strategies.
    /// </summary>
    public void ConfigureIntelligence(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
        foreach (var strategy in _strategies.Values)
        {
            strategy.ConfigureIntelligence(messageBus);
        }
    }

    /// <summary>
    /// Gets all strategy capabilities.
    /// </summary>
    public IEnumerable<RegisteredCapability> GetAllStrategyCapabilities()
    {
        return _strategies.Values.SelectMany(s => s.GetCapabilities());
    }

    /// <summary>
    /// Gets all strategy knowledge objects.
    /// </summary>
    public IEnumerable<KnowledgeObject> GetAllStrategyKnowledge()
    {
        return _strategies.Values.SelectMany(s => s.GetKnowledge());
    }
}

/// <summary>
/// Topics for IoT Intelligence integration.
/// </summary>
public static class IoTTopics
{
    public const string IntelligenceDeviceRecommendation = "iot.intelligence.device.recommendation";
    public const string IntelligenceDeviceRecommendationResponse = "iot.intelligence.device.recommendation.response";
    public const string IntelligenceAnomalyDetection = "iot.intelligence.anomaly.detection";
    public const string IntelligenceAnomalyDetectionResponse = "iot.intelligence.anomaly.detection.response";
    public const string TelemetryIngested = "iot.telemetry.ingested";
    public const string DeviceRegistered = "iot.device.registered";
    public const string DeviceProvisioned = "iot.device.provisioned";
    public const string CommandSent = "iot.command.sent";
    public const string EdgeDeployed = "iot.edge.deployed";
}
