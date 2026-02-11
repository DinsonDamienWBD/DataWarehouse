using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Contracts.Transit
{
    /// <summary>
    /// Statistics tracking for data transit operations.
    /// Used for monitoring, auditing, and performance analysis.
    /// </summary>
    public sealed record TransitStatistics
    {
        /// <summary>
        /// Total number of completed transfer operations.
        /// </summary>
        public long TransferCount { get; init; }

        /// <summary>
        /// Total bytes transferred across all operations.
        /// </summary>
        public long BytesTransferred { get; init; }

        /// <summary>
        /// Number of failed transfer operations.
        /// </summary>
        public long Failures { get; init; }

        /// <summary>
        /// Number of currently active transfers.
        /// </summary>
        public long ActiveTransfers { get; init; }

        /// <summary>
        /// Timestamp when statistics tracking started.
        /// </summary>
        public DateTime StartTime { get; init; }

        /// <summary>
        /// Timestamp of the last statistics update.
        /// </summary>
        public DateTime LastUpdateTime { get; init; }

        /// <summary>
        /// Creates an empty statistics snapshot.
        /// </summary>
        public static TransitStatistics Empty => new()
        {
            StartTime = DateTime.UtcNow,
            LastUpdateTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Abstract base class for data transit strategies.
    /// Provides common functionality for transfer ID generation, statistics tracking,
    /// progress reporting, cancellation management, and Intelligence integration.
    /// </summary>
    /// <remarks>
    /// All concrete transit strategies should inherit from this base class to get
    /// consistent behavior for transfer tracking, cancellation, and AI-enhanced features.
    /// Thread-safe for concurrent transfer operations.
    /// </remarks>
    public abstract class DataTransitStrategyBase : IDataTransitStrategy
    {
        private long _transferCount;
        private long _bytesTransferred;
        private long _failures;
        private long _activeTransfers;
        private readonly DateTime _startTime;
        private DateTime _lastUpdateTime;

        /// <summary>
        /// Thread-safe dictionary tracking active transfer cancellation tokens.
        /// Keyed by transfer ID.
        /// </summary>
        protected readonly ConcurrentDictionary<string, CancellationTokenSource> ActiveTransferCancellations = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="DataTransitStrategyBase"/> class.
        /// </summary>
        protected DataTransitStrategyBase()
        {
            _startTime = DateTime.UtcNow;
            _lastUpdateTime = DateTime.UtcNow;
        }

        /// <inheritdoc/>
        public abstract string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string Name { get; }

        /// <inheritdoc/>
        public abstract TransitCapabilities Capabilities { get; }

        /// <inheritdoc/>
        public abstract Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default);

        /// <inheritdoc/>
        public virtual Task<TransitResult> ResumeTransferAsync(string transferId, IProgress<TransitProgress>? progress = null, CancellationToken ct = default)
        {
            if (!Capabilities.SupportsResumable)
            {
                throw new NotSupportedException($"Strategy '{StrategyId}' does not support resumable transfers.");
            }

            throw new NotSupportedException($"Strategy '{StrategyId}' has not implemented resume logic.");
        }

        /// <inheritdoc/>
        public virtual Task CancelTransferAsync(string transferId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transferId);

            if (ActiveTransferCancellations.TryRemove(transferId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public virtual Task<TransitHealthStatus> GetHealthAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new TransitHealthStatus
            {
                StrategyId = StrategyId,
                IsHealthy = true,
                LastCheckTime = DateTime.UtcNow,
                ActiveTransfers = (int)Interlocked.Read(ref _activeTransfers)
            });
        }

        /// <summary>
        /// Generates a unique transfer ID using the strategy ID and a GUID.
        /// Ensures no collision across strategies per research pitfall 6.
        /// </summary>
        /// <returns>A unique transfer identifier in the format "{StrategyId}-{guid}".</returns>
        protected string GenerateTransferId()
        {
            return $"{StrategyId}-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Records a successful transfer in the statistics.
        /// </summary>
        /// <param name="bytesTransferred">Number of bytes transferred.</param>
        protected void RecordTransferSuccess(long bytesTransferred)
        {
            Interlocked.Increment(ref _transferCount);
            Interlocked.Add(ref _bytesTransferred, bytesTransferred);
            _lastUpdateTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Records a failed transfer in the statistics.
        /// </summary>
        protected void RecordTransferFailure()
        {
            Interlocked.Increment(ref _failures);
            _lastUpdateTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Increments the active transfer count. Call when a transfer starts.
        /// </summary>
        protected void IncrementActiveTransfers()
        {
            Interlocked.Increment(ref _activeTransfers);
        }

        /// <summary>
        /// Decrements the active transfer count. Call when a transfer completes or fails.
        /// </summary>
        protected void DecrementActiveTransfers()
        {
            Interlocked.Decrement(ref _activeTransfers);
        }

        /// <summary>
        /// Gets the current transit statistics snapshot.
        /// </summary>
        /// <returns>A snapshot of the current statistics.</returns>
        public TransitStatistics GetStatistics()
        {
            return new TransitStatistics
            {
                TransferCount = Interlocked.Read(ref _transferCount),
                BytesTransferred = Interlocked.Read(ref _bytesTransferred),
                Failures = Interlocked.Read(ref _failures),
                ActiveTransfers = Interlocked.Read(ref _activeTransfers),
                StartTime = _startTime,
                LastUpdateTime = _lastUpdateTime
            };
        }

        /// <summary>
        /// Resets all statistics counters.
        /// </summary>
        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _transferCount, 0);
            Interlocked.Exchange(ref _bytesTransferred, 0);
            Interlocked.Exchange(ref _failures, 0);
            Interlocked.Exchange(ref _activeTransfers, 0);
            _lastUpdateTime = DateTime.UtcNow;
        }

        #region Intelligence Integration

        /// <summary>
        /// Message bus reference for Intelligence communication.
        /// </summary>
        protected IMessageBus? MessageBus { get; private set; }

        /// <summary>
        /// Configures Intelligence integration for this strategy.
        /// Called by the plugin orchestrator to enable AI-enhanced features.
        /// </summary>
        /// <param name="messageBus">The message bus instance for communication.</param>
        public virtual void ConfigureIntelligence(IMessageBus? messageBus)
        {
            MessageBus = messageBus;
        }

        /// <summary>
        /// Whether Intelligence is available for this strategy.
        /// </summary>
        protected bool IsIntelligenceAvailable => MessageBus != null;

        /// <summary>
        /// Gets static knowledge about this strategy for AI discovery.
        /// Override to provide strategy-specific knowledge.
        /// </summary>
        /// <returns>A KnowledgeObject describing this strategy's capabilities.</returns>
        public virtual KnowledgeObject GetStrategyKnowledge()
        {
            return new KnowledgeObject
            {
                Id = $"strategy.{StrategyId}",
                Topic = GetKnowledgeTopic(),
                SourcePluginId = "sdk.strategy",
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = GetStrategyDescription(),
                Payload = GetKnowledgePayload(),
                Tags = GetKnowledgeTags()
            };
        }

        /// <summary>
        /// Gets the registered capability for this strategy.
        /// </summary>
        /// <returns>A RegisteredCapability describing this strategy.</returns>
        public virtual RegisteredCapability GetStrategyCapability()
        {
            return new RegisteredCapability
            {
                CapabilityId = $"strategy.{StrategyId}",
                DisplayName = Name,
                Description = GetStrategyDescription(),
                Category = GetCapabilityCategory(),
                PluginId = "sdk.strategy",
                PluginName = Name,
                PluginVersion = "1.0.0",
                Tags = GetKnowledgeTags(),
                Metadata = GetCapabilityMetadata(),
                SemanticDescription = GetSemanticDescription()
            };
        }

        /// <summary>
        /// Gets the knowledge topic for this strategy type.
        /// </summary>
        /// <returns>The knowledge topic string.</returns>
        protected virtual string GetKnowledgeTopic() => "transit";

        /// <summary>
        /// Gets the capability category for this strategy type.
        /// </summary>
        /// <returns>The capability category.</returns>
        protected virtual CapabilityCategory GetCapabilityCategory() => CapabilityCategory.Transport;

        /// <summary>
        /// Gets a description for this strategy.
        /// </summary>
        /// <returns>The strategy description.</returns>
        protected virtual string GetStrategyDescription() =>
            $"{Name} transit strategy for data transfer over {string.Join(", ", Capabilities.SupportedProtocols)}";

        /// <summary>
        /// Gets the knowledge payload for this strategy.
        /// </summary>
        /// <returns>Dictionary of knowledge payload entries.</returns>
        protected virtual Dictionary<string, object> GetKnowledgePayload() => new()
        {
            ["strategyId"] = StrategyId,
            ["protocols"] = Capabilities.SupportedProtocols,
            ["supportsResumable"] = Capabilities.SupportsResumable,
            ["supportsStreaming"] = Capabilities.SupportsStreaming,
            ["supportsDelta"] = Capabilities.SupportsDelta,
            ["supportsCompression"] = Capabilities.SupportsCompression,
            ["supportsEncryption"] = Capabilities.SupportsEncryption,
            ["maxTransferSizeBytes"] = Capabilities.MaxTransferSizeBytes
        };

        /// <summary>
        /// Gets tags for this strategy.
        /// </summary>
        /// <returns>Array of tag strings.</returns>
        protected virtual string[] GetKnowledgeTags() =>
        [
            "strategy",
            "transit",
            "transport",
            .. Capabilities.SupportedProtocols
        ];

        /// <summary>
        /// Gets capability metadata for this strategy.
        /// </summary>
        /// <returns>Dictionary of capability metadata entries.</returns>
        protected virtual Dictionary<string, object> GetCapabilityMetadata() => new()
        {
            ["strategyId"] = StrategyId,
            ["protocols"] = Capabilities.SupportedProtocols,
            ["supportsResumable"] = Capabilities.SupportsResumable
        };

        /// <summary>
        /// Gets the semantic description for AI-driven discovery.
        /// </summary>
        /// <returns>The semantic description string.</returns>
        protected virtual string GetSemanticDescription() =>
            $"Use {Name} for transferring data via {string.Join("/", Capabilities.SupportedProtocols)} protocols";

        #endregion
    }
}
