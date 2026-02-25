using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Single entry point for all metadata residency decisions in the VDE system.
    /// <para>
    /// Plugins call <see cref="ResolveAsync"/> to determine where to read/write their metadata
    /// (VDE inode vs plugin-managed storage). The VDE engine calls <see cref="ShouldFallbackToPlugin"/>
    /// on the hot path during inode field reads to decide Tier 1 (VDE) vs Tier 2 (plugin) behavior.
    /// </para>
    /// <para>
    /// Per-field granularity (MRES-10) enables mixed-state inodes where different fields within the same
    /// inode have different residency modes. For example, encryption key references may be
    /// <see cref="MetadataResidencyMode.PluginOnly"/> (HSM-backed) while all other fields are
    /// <see cref="MetadataResidencyMode.VdePrimary"/>.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Metadata residency interfaces (MRES-02, MRES-10)")]
    public interface IMetadataResidencyResolver
    {
        /// <summary>
        /// Resolves the residency configuration for a specific feature and metadata type.
        /// Returns the <see cref="MetadataResidencyConfig"/> that determines where metadata lives
        /// (VDE inode vs plugin storage), how writes are coordinated, how reads fall back, and
        /// what action is taken on corruption.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature (e.g., "compression", "encryption").</param>
        /// <param name="metadataType">The type of metadata being queried (e.g., "key_metadata", "codec_config").</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>The resolved metadata residency configuration for the specified feature and metadata type.</returns>
        Task<MetadataResidencyConfig> ResolveAsync(string featureId, string metadataType, CancellationToken ct = default);

        /// <summary>
        /// Resolves per-field residency overrides within a single inode for a feature.
        /// Returns field-level overrides that allow mixed-state inodes where some fields are Tier 1 (VDE)
        /// and others are Tier 2 (plugin-managed). Returns an empty list if no per-field overrides exist.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature to resolve field overrides for.</param>
        /// <param name="metadataType">The type of metadata being queried.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>
        /// A read-only list of field-level residency overrides. Empty if the entire metadata type
        /// uses a uniform residency mode.
        /// </returns>
        Task<IReadOnlyList<FieldResidencyOverride>> ResolveFieldOverridesAsync(string featureId, string metadataType, CancellationToken ct = default);

        /// <summary>
        /// Gets the default residency configuration used when no feature-specific or field-specific
        /// override exists. This is the system-wide fallback configuration.
        /// </summary>
        MetadataResidencyConfig DefaultConfig { get; }

        /// <summary>
        /// Checks if a specific field within an inode should use plugin fallback (Tier 2) instead
        /// of VDE storage (Tier 1). This is the hot-path method called during inode reads to
        /// determine per-field behavior without the overhead of a full async resolution.
        /// </summary>
        /// <param name="featureId">Unique identifier of the feature owning the field.</param>
        /// <param name="metadataType">The type of metadata containing the field.</param>
        /// <param name="fieldName">The specific field name within the inode to check.</param>
        /// <returns>True if the field should fall back to plugin storage; false if VDE storage is used.</returns>
        bool ShouldFallbackToPlugin(string featureId, string metadataType, string fieldName);
    }
}
