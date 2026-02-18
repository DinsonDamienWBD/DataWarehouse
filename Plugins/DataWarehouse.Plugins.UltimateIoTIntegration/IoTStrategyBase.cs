using System;
using System.Collections.Generic;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIoTIntegration;

/// <summary>
/// Base class for all IoT strategies.
/// </summary>
public abstract class IoTStrategyBase : IIoTStrategyBase
{
    protected IMessageBus? MessageBus { get; private set; }

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
