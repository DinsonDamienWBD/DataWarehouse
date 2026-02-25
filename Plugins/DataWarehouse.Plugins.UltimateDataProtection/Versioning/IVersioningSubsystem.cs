namespace DataWarehouse.Plugins.UltimateDataProtection.Versioning
{
    /// <summary>
    /// Versioning modes for controlling when versions are created.
    /// </summary>
    public enum VersioningMode
    {
        /// <summary>User-triggered versioning only.</summary>
        Manual,

        /// <summary>Versions created at configured intervals.</summary>
        Scheduled,

        /// <summary>Versions created on specific events (save, commit, schema change).</summary>
        EventBased,

        /// <summary>Continuous versioning - every write operation creates a version (CDP).</summary>
        Continuous,

        /// <summary>AI-decides versioning based on change significance and patterns.</summary>
        Intelligent
    }

    /// <summary>
    /// Metadata associated with a version.
    /// </summary>
    public sealed record VersionMetadata
    {
        /// <summary>Version label or name.</summary>
        public string? Label { get; init; }

        /// <summary>Description of the version.</summary>
        public string? Description { get; init; }

        /// <summary>User or system that created the version.</summary>
        public string? CreatedBy { get; init; }

        /// <summary>Event that triggered version creation.</summary>
        public string? TriggerEvent { get; init; }

        /// <summary>Change significance score (0.0 - 1.0).</summary>
        public double? ChangeSignificance { get; init; }

        /// <summary>Custom tags for the version.</summary>
        public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

        /// <summary>Parent version ID for delta tracking.</summary>
        public string? ParentVersionId { get; init; }

        /// <summary>Application-specific data.</summary>
        public IReadOnlyDictionary<string, object> ApplicationData { get; init; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Information about a version.
    /// </summary>
    public sealed record VersionInfo
    {
        /// <summary>Unique version identifier.</summary>
        public string VersionId { get; init; } = string.Empty;

        /// <summary>Item ID this version belongs to.</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>Version number (sequential within item).</summary>
        public int VersionNumber { get; init; }

        /// <summary>Version creation timestamp.</summary>
        public DateTimeOffset CreatedAt { get; init; }

        /// <summary>Version metadata.</summary>
        public VersionMetadata Metadata { get; init; } = new();

        /// <summary>Size of the version content in bytes.</summary>
        public long SizeBytes { get; init; }

        /// <summary>Stored size after compression/deduplication.</summary>
        public long StoredSizeBytes { get; init; }

        /// <summary>Content hash for integrity verification.</summary>
        public string ContentHash { get; init; } = string.Empty;

        /// <summary>Hash algorithm used.</summary>
        public string HashAlgorithm { get; init; } = "SHA256";

        /// <summary>Whether the version is immutable (locked).</summary>
        public bool IsImmutable { get; init; }

        /// <summary>Legal hold status.</summary>
        public bool IsOnLegalHold { get; init; }

        /// <summary>Expiration time (null = never expires).</summary>
        public DateTimeOffset? ExpiresAt { get; init; }

        /// <summary>Whether the version has been verified.</summary>
        public bool IsVerified { get; init; }

        /// <summary>Last verification time.</summary>
        public DateTimeOffset? LastVerifiedAt { get; init; }

        /// <summary>Storage tier for this version.</summary>
        public StorageTier StorageTier { get; init; } = StorageTier.Standard;
    }

    /// <summary>
    /// Storage tiers for versioned content.
    /// </summary>
    public enum StorageTier
    {
        /// <summary>Standard tier - immediate access.</summary>
        Standard,

        /// <summary>Infrequent access tier - reduced cost.</summary>
        InfrequentAccess,

        /// <summary>Archive tier - lowest cost, retrieval delay.</summary>
        Archive,

        /// <summary>Deep archive - minimal cost, longest retrieval.</summary>
        DeepArchive,

        /// <summary>Glacier-style cold storage.</summary>
        Glacier
    }

    /// <summary>
    /// Query parameters for listing versions.
    /// </summary>
    public sealed class VersionQuery
    {
        /// <summary>Filter by minimum creation time.</summary>
        public DateTimeOffset? CreatedAfter { get; init; }

        /// <summary>Filter by maximum creation time.</summary>
        public DateTimeOffset? CreatedBefore { get; init; }

        /// <summary>Filter by label pattern.</summary>
        public string? LabelPattern { get; init; }

        /// <summary>Filter by creator.</summary>
        public string? CreatedBy { get; init; }

        /// <summary>Filter by minimum version number.</summary>
        public int? MinVersionNumber { get; init; }

        /// <summary>Filter by maximum version number.</summary>
        public int? MaxVersionNumber { get; init; }

        /// <summary>Include expired versions.</summary>
        public bool IncludeExpired { get; init; }

        /// <summary>Include only immutable versions.</summary>
        public bool OnlyImmutable { get; init; }

        /// <summary>Filter by storage tier.</summary>
        public StorageTier? StorageTier { get; init; }

        /// <summary>Maximum results to return.</summary>
        public int MaxResults { get; init; } = 100;

        /// <summary>Sort order.</summary>
        public VersionSortOrder SortOrder { get; init; } = VersionSortOrder.CreatedDescending;
    }

    /// <summary>
    /// Sort order for version queries.
    /// </summary>
    public enum VersionSortOrder
    {
        /// <summary>Newest first.</summary>
        CreatedDescending,

        /// <summary>Oldest first.</summary>
        CreatedAscending,

        /// <summary>Highest version number first.</summary>
        VersionDescending,

        /// <summary>Lowest version number first.</summary>
        VersionAscending,

        /// <summary>Largest size first.</summary>
        SizeDescending,

        /// <summary>Smallest size first.</summary>
        SizeAscending
    }

    /// <summary>
    /// Difference between two versions.
    /// </summary>
    public sealed record VersionDiff
    {
        /// <summary>Source version ID.</summary>
        public string SourceVersionId { get; init; } = string.Empty;

        /// <summary>Target version ID.</summary>
        public string TargetVersionId { get; init; } = string.Empty;

        /// <summary>Items added in target.</summary>
        public IReadOnlyList<DiffItem> AddedItems { get; init; } = Array.Empty<DiffItem>();

        /// <summary>Items modified in target.</summary>
        public IReadOnlyList<DiffItem> ModifiedItems { get; init; } = Array.Empty<DiffItem>();

        /// <summary>Items deleted in target.</summary>
        public IReadOnlyList<DiffItem> DeletedItems { get; init; } = Array.Empty<DiffItem>();

        /// <summary>Total bytes changed.</summary>
        public long TotalBytesChanged { get; init; }

        /// <summary>Change percentage.</summary>
        public double ChangePercentage { get; init; }

        /// <summary>Comparison timestamp.</summary>
        public DateTimeOffset ComparedAt { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// An item in a version diff.
    /// </summary>
    public sealed record DiffItem
    {
        /// <summary>Item path or identifier.</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>Item type.</summary>
        public string ItemType { get; init; } = "file";

        /// <summary>Size in source version.</summary>
        public long? SourceSize { get; init; }

        /// <summary>Size in target version.</summary>
        public long? TargetSize { get; init; }

        /// <summary>Modification type.</summary>
        public DiffType DiffType { get; init; }
    }

    /// <summary>
    /// Types of differences.
    /// </summary>
    public enum DiffType
    {
        /// <summary>Item was added.</summary>
        Added,

        /// <summary>Item was modified.</summary>
        Modified,

        /// <summary>Item was deleted.</summary>
        Deleted,

        /// <summary>Item was renamed.</summary>
        Renamed,

        /// <summary>Item permissions changed.</summary>
        PermissionsChanged
    }

    /// <summary>
    /// Retention decision for a version.
    /// </summary>
    public sealed record VersionRetentionDecision
    {
        /// <summary>Whether the version should be retained.</summary>
        public bool ShouldRetain { get; init; }

        /// <summary>Reason for the decision.</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>Suggested action if not retaining.</summary>
        public RetentionAction SuggestedAction { get; init; }

        /// <summary>Suggested new expiration time.</summary>
        public DateTimeOffset? NewExpirationTime { get; init; }

        /// <summary>Suggested storage tier transition.</summary>
        public StorageTier? SuggestedTierTransition { get; init; }
    }

    /// <summary>
    /// Actions for version retention.
    /// </summary>
    public enum RetentionAction
    {
        /// <summary>Keep the version as-is.</summary>
        Keep,

        /// <summary>Delete the version.</summary>
        Delete,

        /// <summary>Move to a lower storage tier.</summary>
        Archive,

        /// <summary>Extend the retention period.</summary>
        ExtendRetention,

        /// <summary>Mark as immutable.</summary>
        MakeImmutable
    }

    /// <summary>
    /// Context for versioning policy decisions.
    /// </summary>
    public sealed record VersionContext
    {
        /// <summary>Item being versioned.</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>Current item size.</summary>
        public long CurrentSize { get; init; }

        /// <summary>Previous version size (if any).</summary>
        public long? PreviousSize { get; init; }

        /// <summary>Change size in bytes.</summary>
        public long ChangeSize { get; init; }

        /// <summary>Last version timestamp.</summary>
        public DateTimeOffset? LastVersionTime { get; init; }

        /// <summary>Number of existing versions.</summary>
        public int ExistingVersionCount { get; init; }

        /// <summary>Event that triggered the version consideration.</summary>
        public string TriggerEvent { get; init; } = string.Empty;

        /// <summary>User or process requesting the version.</summary>
        public string? RequestedBy { get; init; }

        /// <summary>Item's criticality level (0.0 - 1.0).</summary>
        public double CriticalityLevel { get; init; }

        /// <summary>Recent access frequency.</summary>
        public int RecentAccessCount { get; init; }

        /// <summary>Additional context data.</summary>
        public IReadOnlyDictionary<string, object> AdditionalContext { get; init; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Complete versioning interface providing version lifecycle management.
    /// Supports manual, scheduled, event-based, continuous, and intelligent versioning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The versioning subsystem provides:
    /// </para>
    /// <list type="bullet">
    ///   <item>Version creation with metadata and deduplication</item>
    ///   <item>Version listing and retrieval</item>
    ///   <item>Version restoration and comparison</item>
    ///   <item>Policy-based retention management</item>
    ///   <item>Storage tier transitions</item>
    /// </list>
    /// </remarks>
    public interface IVersioningSubsystem
    {
        /// <summary>
        /// Gets the current versioning mode.
        /// </summary>
        VersioningMode CurrentMode { get; }

        /// <summary>
        /// Gets the current versioning policy.
        /// </summary>
        IVersioningPolicy? CurrentPolicy { get; }

        /// <summary>
        /// Gets whether the versioning subsystem is enabled.
        /// </summary>
        bool IsEnabled { get; }

        #region Version Operations

        /// <summary>
        /// Creates a new version for an item.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="metadata">Version metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Information about the created version.</returns>
        Task<VersionInfo> CreateVersionAsync(
            string itemId,
            VersionMetadata metadata,
            CancellationToken ct = default);

        /// <summary>
        /// Creates a version with content.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="content">Version content.</param>
        /// <param name="metadata">Version metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Information about the created version.</returns>
        Task<VersionInfo> CreateVersionWithContentAsync(
            string itemId,
            ReadOnlyMemory<byte> content,
            VersionMetadata metadata,
            CancellationToken ct = default);

        /// <summary>
        /// Lists versions for an item.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="query">Query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Matching versions.</returns>
        Task<IEnumerable<VersionInfo>> ListVersionsAsync(
            string itemId,
            VersionQuery query,
            CancellationToken ct = default);

        /// <summary>
        /// Gets a specific version.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="versionId">Version identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Version information, or null if not found.</returns>
        Task<VersionInfo?> GetVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default);

        /// <summary>
        /// Gets the content of a version.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="versionId">Version identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Version content.</returns>
        Task<byte[]> GetVersionContentAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default);

        /// <summary>
        /// Restores an item to a specific version.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="versionId">Version to restore to.</param>
        /// <param name="ct">Cancellation token.</param>
        Task RestoreVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default);

        /// <summary>
        /// Deletes a specific version.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="versionId">Version to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        Task DeleteVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default);

        /// <summary>
        /// Compares two versions.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="versionId1">First version.</param>
        /// <param name="versionId2">Second version.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Difference between versions.</returns>
        Task<VersionDiff> CompareVersionsAsync(
            string itemId,
            string versionId1,
            string versionId2,
            CancellationToken ct = default);

        #endregion

        #region Version Management

        /// <summary>
        /// Marks a version as immutable.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="versionId">Version to lock.</param>
        /// <param name="ct">Cancellation token.</param>
        Task LockVersionAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default);

        /// <summary>
        /// Places a legal hold on a version.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="versionId">Version to hold.</param>
        /// <param name="holdReason">Reason for the hold.</param>
        /// <param name="ct">Cancellation token.</param>
        Task PlaceLegalHoldAsync(
            string itemId,
            string versionId,
            string holdReason,
            CancellationToken ct = default);

        /// <summary>
        /// Removes a legal hold from a version.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="versionId">Version to release.</param>
        /// <param name="ct">Cancellation token.</param>
        Task RemoveLegalHoldAsync(
            string itemId,
            string versionId,
            CancellationToken ct = default);

        /// <summary>
        /// Transitions a version to a different storage tier.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="versionId">Version to transition.</param>
        /// <param name="targetTier">Target storage tier.</param>
        /// <param name="ct">Cancellation token.</param>
        Task TransitionStorageTierAsync(
            string itemId,
            string versionId,
            StorageTier targetTier,
            CancellationToken ct = default);

        #endregion

        #region Policy Management

        /// <summary>
        /// Sets the active versioning policy.
        /// </summary>
        /// <param name="policy">Policy to activate.</param>
        /// <param name="ct">Cancellation token.</param>
        Task SetPolicyAsync(IVersioningPolicy policy, CancellationToken ct = default);

        /// <summary>
        /// Gets all available versioning policies.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Available policies.</returns>
        Task<IEnumerable<IVersioningPolicy>> GetAvailablePoliciesAsync(CancellationToken ct = default);

        /// <summary>
        /// Evaluates retention for all versions of an item.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Retention decisions for each version.</returns>
        Task<IEnumerable<(VersionInfo Version, VersionRetentionDecision Decision)>> EvaluateRetentionAsync(
            string itemId,
            CancellationToken ct = default);

        /// <summary>
        /// Applies retention policy to all versions.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Number of versions affected.</returns>
        Task<int> ApplyRetentionPolicyAsync(CancellationToken ct = default);

        #endregion

        #region Statistics

        /// <summary>
        /// Gets versioning statistics for an item.
        /// </summary>
        /// <param name="itemId">Item identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Version statistics.</returns>
        Task<VersioningStatistics> GetStatisticsAsync(string itemId, CancellationToken ct = default);

        /// <summary>
        /// Gets global versioning statistics.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Global statistics.</returns>
        Task<VersioningStatistics> GetGlobalStatisticsAsync(CancellationToken ct = default);

        #endregion
    }

    /// <summary>
    /// Versioning statistics.
    /// </summary>
    public sealed record VersioningStatistics
    {
        /// <summary>Total version count.</summary>
        public long TotalVersions { get; init; }

        /// <summary>Total stored bytes across all versions.</summary>
        public long TotalStoredBytes { get; init; }

        /// <summary>Total original bytes across all versions.</summary>
        public long TotalOriginalBytes { get; init; }

        /// <summary>Deduplication ratio.</summary>
        public double DeduplicationRatio => TotalOriginalBytes > 0
            ? (double)TotalStoredBytes / TotalOriginalBytes : 1.0;

        /// <summary>Space savings percentage.</summary>
        public double SpaceSavingsPercent => TotalOriginalBytes > 0
            ? (1.0 - DeduplicationRatio) * 100 : 0;

        /// <summary>Versions created today.</summary>
        public int VersionsCreatedToday { get; init; }

        /// <summary>Versions deleted today.</summary>
        public int VersionsDeletedToday { get; init; }

        /// <summary>Oldest version timestamp.</summary>
        public DateTimeOffset? OldestVersion { get; init; }

        /// <summary>Newest version timestamp.</summary>
        public DateTimeOffset? NewestVersion { get; init; }

        /// <summary>Immutable version count.</summary>
        public int ImmutableVersions { get; init; }

        /// <summary>Versions on legal hold.</summary>
        public int VersionsOnLegalHold { get; init; }

        /// <summary>Versions by storage tier.</summary>
        public IReadOnlyDictionary<StorageTier, int> VersionsByTier { get; init; }
            = new Dictionary<StorageTier, int>();
    }
}
