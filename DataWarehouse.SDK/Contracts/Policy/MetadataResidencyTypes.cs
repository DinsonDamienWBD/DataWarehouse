using System;
using System.ComponentModel;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Defines where metadata resides relative to the VDE inode and plugin storage.
    /// Controls the primary source of truth for metadata fields.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Metadata residency foundation (MRES-01)")]
    public enum MetadataResidencyMode
    {
        /// <summary>
        /// Metadata lives exclusively in VDE inode fields; no plugin fallback.
        /// Highest consistency but no redundancy if VDE metadata is corrupted.
        /// </summary>
        [Description("Metadata lives exclusively in VDE inode fields; no plugin fallback")]
        VdeOnly = 0,

        /// <summary>
        /// Metadata lives in VDE with plugin as Tier 2 fallback on corruption.
        /// Provides redundancy while maintaining VDE as the primary source of truth.
        /// </summary>
        [Description("Metadata in VDE with plugin as Tier 2 fallback on corruption")]
        VdePrimary = 1,

        /// <summary>
        /// Metadata managed entirely by plugin (e.g., hardware-backed keys in HSM);
        /// VDE inode fields remain empty for this feature.
        /// </summary>
        [Description("Metadata managed entirely by plugin; VDE inode fields remain empty")]
        PluginOnly = 2
    }

    /// <summary>
    /// Defines how metadata writes are coordinated between VDE and plugin storage.
    /// Controls the durability and consistency guarantees of write operations.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Metadata residency foundation (MRES-02)")]
    public enum WriteStrategy
    {
        /// <summary>
        /// Write to VDE and plugin in the same transaction; rollback both on failure.
        /// Strongest consistency guarantee at the cost of higher latency.
        /// </summary>
        [Description("Write to VDE and plugin in same transaction; rollback both on failure")]
        Atomic = 0,

        /// <summary>
        /// Write VDE first, then synchronously write plugin copy.
        /// VDE is always up-to-date; plugin may lag briefly on VDE-write failure recovery.
        /// </summary>
        [Description("Write VDE first, then synchronously write plugin copy")]
        VdeFirstSync = 1,

        /// <summary>
        /// Write VDE immediately, plugin copy flushed on a configurable interval.
        /// Lowest latency but plugin copy may be stale by up to the flush interval.
        /// </summary>
        [Description("Write VDE immediately; plugin copy flushed on configurable interval")]
        VdeFirstLazy = 2
    }

    /// <summary>
    /// Defines how metadata reads prioritize VDE versus plugin storage.
    /// Controls fallback behavior when VDE metadata is unavailable or corrupt.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Metadata residency foundation (MRES-03)")]
    public enum ReadStrategy
    {
        /// <summary>
        /// Read VDE first; on corruption or empty field, fall back to plugin metadata.
        /// Provides resilience at the cost of potentially reading stale plugin data.
        /// </summary>
        [Description("Read VDE first; fall back to plugin metadata on corruption or empty field")]
        VdeFallback = 0,

        /// <summary>
        /// Read VDE only; fail if VDE metadata is corrupt or missing (no fallback).
        /// Strictest consistency but no resilience to VDE metadata issues.
        /// </summary>
        [Description("Read VDE only; fail if corrupt or missing with no fallback")]
        VdeStrict = 1
    }

    /// <summary>
    /// Defines the action taken when metadata corruption is detected during a read or verification operation.
    /// Controls the trade-off between availability, data integrity, and operator involvement.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Metadata residency foundation (MRES-04, MRES-09)")]
    public enum CorruptionAction
    {
        /// <summary>
        /// Fall back to plugin metadata, auto-repair VDE copy, and emit a CorruptionDetected bus event.
        /// Maximizes availability while self-healing the corruption.
        /// </summary>
        [Description("Fall back to plugin, auto-repair VDE copy, emit CorruptionDetected event")]
        FallbackAndRepair = 0,

        /// <summary>
        /// Fall back to plugin metadata and log the discrepancy, but do not auto-repair.
        /// Preserves the corrupt state for forensic analysis.
        /// </summary>
        [Description("Fall back to plugin, log discrepancy, do not auto-repair")]
        FallbackOnly = 1,

        /// <summary>
        /// Fail the operation, emit an alert, and require manual intervention.
        /// Strictest integrity guarantee; no data served until corruption is resolved.
        /// </summary>
        [Description("Fail operation, emit alert, require manual intervention")]
        FailAndAlert = 2,

        /// <summary>
        /// Isolate the corrupt object, preventing further reads and writes until repaired.
        /// Protects downstream consumers from corrupt data propagation.
        /// </summary>
        [Description("Isolate corrupt object; prevent reads/writes until repaired")]
        Quarantine = 3
    }

    /// <summary>
    /// Complete metadata residency configuration combining mode, write strategy, read strategy,
    /// and corruption handling into a single composable unit.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Metadata residency foundation (MRES-01 through MRES-09)")]
    public sealed record MetadataResidencyConfig
    {
        /// <summary>
        /// Primary residency mode controlling where metadata lives.
        /// </summary>
        public MetadataResidencyMode Mode { get; init; }

        /// <summary>
        /// Strategy for coordinating writes between VDE and plugin storage.
        /// </summary>
        public WriteStrategy Write { get; init; }

        /// <summary>
        /// Strategy for reading metadata with fallback behavior.
        /// </summary>
        public ReadStrategy Read { get; init; }

        /// <summary>
        /// Action taken when metadata corruption is detected.
        /// </summary>
        public CorruptionAction OnCorruption { get; init; }

        /// <summary>
        /// Flush interval for lazy writes. Only used when <see cref="Write"/> is
        /// <see cref="WriteStrategy.VdeFirstLazy"/>. Defaults to 30 seconds.
        /// </summary>
        public TimeSpan? LazyFlushInterval { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Creates the default metadata residency configuration:
        /// VdePrimary mode, VdeFirstSync writes, VdeFallback reads, FallbackAndRepair on corruption.
        /// </summary>
        /// <returns>A new <see cref="MetadataResidencyConfig"/> with production-safe defaults.</returns>
        public static MetadataResidencyConfig Default() => new()
        {
            Mode = MetadataResidencyMode.VdePrimary,
            Write = WriteStrategy.VdeFirstSync,
            Read = ReadStrategy.VdeFallback,
            OnCorruption = CorruptionAction.FallbackAndRepair
        };
    }

    /// <summary>
    /// Allows per-field residency overrides within a single VDE inode, enabling mixed-state inodes
    /// where some fields are Tier 1 (VDE-managed) and others are Tier 2 (plugin-managed).
    /// For example, encryption keys may be PluginOnly (HSM-backed) while compression metadata is VdeOnly.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Metadata residency foundation (MRES-10)")]
    public sealed record FieldResidencyOverride
    {
        /// <summary>
        /// Inode field identifier that this override applies to (e.g., "encryption_key", "compression_codec").
        /// </summary>
        public required string FieldName { get; init; }

        /// <summary>
        /// Override residency mode for this specific field, independent of the inode-level default.
        /// </summary>
        public required MetadataResidencyMode Mode { get; init; }
    }
}
