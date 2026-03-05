using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Read Stage 6: performs post-read updates including ARC cache promotion hints,
/// read counter increments, and heat map updates. This stage always runs (no module
/// gate) and is the final stage in every read pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This stage does not modify the data buffer or extent list. It signals cache
/// management and statistics systems via the property bag so that post-pipeline
/// background tasks can update caches and monitoring data.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE read pipeline post-read updates stage (VOPT-90)")]
public sealed class PostReadUpdatesStage : IVdeReadStage
{
    /// <summary>Property key for ARC cache promotion hint.</summary>
    public const string ArcPromoteKey = "ArcPromote";

    /// <summary>Property key for health summary update data.</summary>
    public const string HealthSummaryUpdateKey = "HealthSummaryUpdate";

    /// <summary>Property key for heat map entry update.</summary>
    public const string HeatMapUpdateKey = "HeatMapUpdate";

    /// <inheritdoc />
    public string StageName => "PostReadUpdates";

    /// <inheritdoc />
    /// <remarks>Always null: post-read updates are unconditional for every read.</remarks>
    public ModuleId? ModuleGate => null;

    /// <inheritdoc />
    public Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        // Signal ARC cache to promote this inode (recently accessed)
        context.SetProperty(ArcPromoteKey, true);

        // Increment read counters for health monitoring
        var healthUpdate = new HealthSummaryUpdate
        {
            InodeNumber = context.Inode?.InodeNumber ?? context.InodeNumber,
            ReadTimestamp = DateTimeOffset.UtcNow,
            BytesRead = context.DataBuffer.Length,
            ExtentsTraversed = context.Extents.Count,
        };
        context.SetProperty(HealthSummaryUpdateKey, healthUpdate);

        // Update heat map entry for access pattern tracking
        var heatMapEntry = new HeatMapUpdate
        {
            InodeNumber = context.Inode?.InodeNumber ?? context.InodeNumber,
            AccessEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            AccessType = HeatMapAccessType.Read,
        };
        context.SetProperty(HeatMapUpdateKey, heatMapEntry);

        // Update inode access timestamp
        if (context.Inode is not null)
        {
            var now = DateTimeOffset.UtcNow;
            context.Inode.AccessedUtc = now.UtcTicks;
            context.Inode.AccessedNs = now.UtcTicks * 100;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Describes a health summary update for post-read statistics.
    /// </summary>
    public sealed class HealthSummaryUpdate
    {
        /// <summary>Inode number that was read.</summary>
        public long InodeNumber { get; init; }

        /// <summary>Timestamp of the read operation.</summary>
        public DateTimeOffset ReadTimestamp { get; init; }

        /// <summary>Total bytes read.</summary>
        public int BytesRead { get; init; }

        /// <summary>Number of extents traversed during the read.</summary>
        public int ExtentsTraversed { get; init; }
    }

    /// <summary>
    /// Describes a heat map update for access pattern tracking.
    /// </summary>
    public sealed class HeatMapUpdate
    {
        /// <summary>Inode number accessed.</summary>
        public long InodeNumber { get; init; }

        /// <summary>Unix epoch of the access.</summary>
        public long AccessEpoch { get; init; }

        /// <summary>Type of access.</summary>
        public HeatMapAccessType AccessType { get; init; }
    }

    /// <summary>
    /// Type of access for heat map tracking.
    /// </summary>
    public enum HeatMapAccessType : byte
    {
        /// <summary>Read access.</summary>
        Read = 0,

        /// <summary>Write access.</summary>
        Write = 1,
    }
}
