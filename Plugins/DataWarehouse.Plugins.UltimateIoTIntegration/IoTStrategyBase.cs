using System;
using System.Collections.Generic;
using System.Threading;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIoTIntegration;

/// <summary>
/// Health status for an IoT strategy.
/// </summary>
public enum IoTStrategyHealthStatus
{
    /// <summary>Strategy is healthy and operational.</summary>
    Healthy,
    /// <summary>Strategy is operational but degraded.</summary>
    Degraded,
    /// <summary>Strategy is not operational.</summary>
    Unhealthy
}

/// <summary>
/// Health report for an IoT strategy.
/// </summary>
public sealed class IoTStrategyHealthReport
{
    /// <summary>Overall health status.</summary>
    public required IoTStrategyHealthStatus Status { get; init; }
    /// <summary>Strategy identifier.</summary>
    public required string StrategyId { get; init; }
    /// <summary>Total operations executed.</summary>
    public long TotalOperations { get; init; }
    /// <summary>Failed operations count.</summary>
    public long FailedOperations { get; init; }
    /// <summary>Last activity timestamp.</summary>
    public DateTimeOffset? LastActivity { get; init; }
    /// <summary>Additional details.</summary>
    public string? Details { get; init; }
}

/// <summary>
/// Base class for all IoT strategies with production-ready health tracking and metrics.
/// </summary>
public abstract class IoTStrategyBase : IIoTStrategyBase
{
    protected IMessageBus? MessageBus { get; private set; }
    private long _totalOperations;
    private long _failedOperations;
    private DateTimeOffset? _lastActivity;

    /// <inheritdoc/>
    public abstract string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string StrategyName { get; }

    /// <inheritdoc/>
    public abstract IoTStrategyCategory Category { get; }

    /// <inheritdoc/>
    public abstract string Description { get; }

    /// <inheritdoc/>
    public virtual string[] Tags => Array.Empty<string>();

    /// <inheritdoc/>
    public virtual bool IsAvailable => true;

    /// <summary>
    /// Gets the health report for this strategy.
    /// </summary>
    public virtual IoTStrategyHealthReport GetHealthReport()
    {
        var total = Interlocked.Read(ref _totalOperations);
        var failed = Interlocked.Read(ref _failedOperations);
        var errorRate = total > 0 ? (double)failed / total : 0;

        return new IoTStrategyHealthReport
        {
            Status = !IsAvailable ? IoTStrategyHealthStatus.Unhealthy
                : errorRate > 0.5 ? IoTStrategyHealthStatus.Degraded
                : IoTStrategyHealthStatus.Healthy,
            StrategyId = StrategyId,
            TotalOperations = total,
            FailedOperations = failed,
            LastActivity = _lastActivity
        };
    }

    /// <summary>
    /// Records a successful operation for metrics.
    /// </summary>
    protected void RecordOperation()
    {
        Interlocked.Increment(ref _totalOperations);
        _lastActivity = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Records a failed operation for metrics.
    /// </summary>
    protected void RecordFailure()
    {
        Interlocked.Increment(ref _totalOperations);
        Interlocked.Increment(ref _failedOperations);
        _lastActivity = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc/>
    public void ConfigureIntelligence(IMessageBus? messageBus)
    {
        MessageBus = messageBus;
        OnIntelligenceConfigured();
    }

    /// <summary>
    /// Called when Intelligence is configured.
    /// </summary>
    protected virtual void OnIntelligenceConfigured() { }

    /// <inheritdoc/>
    public virtual IEnumerable<KnowledgeObject> GetKnowledge()
    {
        yield return new KnowledgeObject
        {
            Id = $"iot.strategy.{StrategyId}",
            Topic = "iot-strategy",
            SourcePluginId = "com.datawarehouse.iot.ultimate",
            SourcePluginName = "Ultimate IoT Integration",
            KnowledgeType = "strategy",
            Description = Description,
            Payload = new Dictionary<string, object>
            {
                ["strategyId"] = StrategyId,
                ["strategyName"] = StrategyName,
                ["category"] = Category.ToString(),
                ["tags"] = Tags
            },
            Tags = Tags,
            Confidence = 1.0f,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    public virtual IEnumerable<RegisteredCapability> GetCapabilities()
    {
        yield return new RegisteredCapability
        {
            CapabilityId = $"iot.{Category.ToString().ToLowerInvariant()}.{StrategyId}",
            DisplayName = StrategyName,
            Description = Description,
            Category = SDK.Contracts.CapabilityCategory.Custom,
            SubCategory = "IoT",
            PluginId = "com.datawarehouse.iot.ultimate",
            PluginName = "Ultimate IoT Integration",
            PluginVersion = "1.0.0",
            Tags = Tags,
            IsAvailable = IsAvailable
        };
    }

    /// <summary>
    /// Publishes a message to the message bus.
    /// </summary>
    protected Task PublishMessage(string topic, PluginMessage message)
    {
        if (MessageBus != null)
        {
            return Task.Run(async () =>
            {
                try
                {
                    await MessageBus.PublishAsync(topic, message);
                }
                catch (Exception ex)
                {
                    // Gracefully handle message bus unavailability
                    System.Diagnostics.Debug.WriteLine($"IoT message publish failed: {ex.Message}");
                }
            });
        }
        return Task.CompletedTask;
    }
}
