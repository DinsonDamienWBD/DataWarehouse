using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory single-node implementation of <see cref="IAutoTier"/>.
    /// Operates with a single default tier and always recommends keeping data where it is.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryAutoTier : IAutoTier
    {
        private readonly string _defaultTierName;
        private readonly TierInfo _defaultTier;

        /// <summary>
        /// Initializes a new single-tier auto-placement engine.
        /// </summary>
        /// <param name="defaultTierName">The name of the default tier.</param>
        public InMemoryAutoTier(string defaultTierName = "default")
        {
            _defaultTierName = defaultTierName;
            _defaultTier = new TierInfo
            {
                TierName = _defaultTierName,
                Description = "Default single-node storage tier",
                CapacityBytes = long.MaxValue,
                UsedBytes = 0,
                CostPerGBMonth = 0,
                PerformanceClass = TierPerformanceClass.Hot
            };
        }

        /// <inheritdoc />
        public event Action<TierEvent>? OnTierEvent;

        /// <inheritdoc />
        public Task<TierPlacement> EvaluatePlacementAsync(TierEvaluationContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new TierPlacement
            {
                DataKey = context.DataKey,
                RecommendedTier = _defaultTierName,
                CurrentTier = _defaultTierName,
                Reason = "Single-tier mode: all data stays in default tier",
                ConfidenceScore = 1.0
            });
        }

        /// <inheritdoc />
        public Task<TierMigrationResult> MigrateAsync(TierMigrationRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            OnTierEvent?.Invoke(new TierEvent
            {
                EventType = TierEventType.MigrationCompleted,
                DataKey = request.DataKey,
                FromTier = request.SourceTier,
                ToTier = request.TargetTier,
                Timestamp = DateTimeOffset.UtcNow
            });
            return Task.FromResult(TierMigrationResult.Ok(
                request.DataKey, request.SourceTier, request.TargetTier, TimeSpan.Zero));
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<TierInfo>> GetTiersAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TierInfo>>(new[] { _defaultTier });
        }
    }
}
