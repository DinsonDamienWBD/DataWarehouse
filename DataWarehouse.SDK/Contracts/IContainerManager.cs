using DataWarehouse.SDK.Security;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Interface for container/partition/room management.
    ///
    /// A Container (also called Partition or Room) is a logical isolation unit:
    /// - Every user/app/module gets a separate partitioned space
    /// - All their data goes into this partition
    /// - There's a shared "public" partition for shared data
    /// - Users can grant/revoke access to other users/apps/modules
    ///
    /// This interface is STORAGE-AGNOSTIC - it manages logical partitions,
    /// not the actual storage layer (memory, disk, S3, network, etc.).
    /// The underlying storage is handled by IStorageProvider plugins.
    /// </summary>
    public interface IContainerManager
    {
        // --- Container Lifecycle ---

        /// <summary>
        /// Create a new container (partition/room).
        /// The caller becomes the owner with full access.
        /// </summary>
        /// <param name="context">Security context of the caller</param>
        /// <param name="containerId">Unique identifier for the container</param>
        /// <param name="options">Container creation options</param>
        /// <returns>Container information</returns>
        Task<ContainerInfo> CreateContainerAsync(
            ISecurityContext context,
            string containerId,
            ContainerOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Get container information.
        /// </summary>
        Task<ContainerInfo?> GetContainerAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        /// <summary>
        /// List all containers the caller has access to.
        /// </summary>
        IAsyncEnumerable<ContainerInfo> ListContainersAsync(
            ISecurityContext context,
            CancellationToken ct = default);

        /// <summary>
        /// Delete a container and all its contents.
        /// Only the owner can delete a container.
        /// </summary>
        Task DeleteContainerAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        // --- Access Control ---

        /// <summary>
        /// Grant access to a container.
        /// Only the owner or users with Admin access can grant access.
        /// </summary>
        Task GrantAccessAsync(
            ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            ContainerAccessLevel level,
            CancellationToken ct = default);

        /// <summary>
        /// Revoke access from a container.
        /// Only the owner or users with Admin access can revoke access.
        /// </summary>
        Task RevokeAccessAsync(
            ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            CancellationToken ct = default);

        /// <summary>
        /// Get access level for a user on a container.
        /// </summary>
        Task<ContainerAccessLevel> GetAccessLevelAsync(
            ISecurityContext context,
            string containerId,
            string? userId = null, // null = check caller's access
            CancellationToken ct = default);

        /// <summary>
        /// List all users with access to a container.
        /// </summary>
        IAsyncEnumerable<ContainerAccessEntry> ListAccessAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        // --- Quota Management ---

        /// <summary>
        /// Get container quota and usage.
        /// </summary>
        Task<ContainerQuota> GetQuotaAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        /// <summary>
        /// Set container quota (admin only).
        /// </summary>
        Task SetQuotaAsync(
            ISecurityContext adminContext,
            string containerId,
            ContainerQuota quota,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Container access levels.
    /// </summary>
    public enum ContainerAccessLevel
    {
        /// <summary>No access.</summary>
        None = 0,

        /// <summary>Can read data from the container.</summary>
        Read = 1,

        /// <summary>Can read and write data.</summary>
        ReadWrite = 2,

        /// <summary>Can read, write, and manage access for others.</summary>
        Admin = 3,

        /// <summary>Full control including deletion. Only container creator has this by default.</summary>
        Owner = 4
    }

    /// <summary>
    /// Container creation options.
    /// </summary>
    public class ContainerOptions
    {
        /// <summary>
        /// Human-readable name for the container.
        /// </summary>
        public string? DisplayName { get; init; }

        /// <summary>
        /// Description of the container's purpose.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Whether to encrypt data in this container by default.
        /// </summary>
        public bool EncryptByDefault { get; init; }

        /// <summary>
        /// Whether to compress data in this container by default.
        /// </summary>
        public bool CompressByDefault { get; init; }

        /// <summary>
        /// Maximum storage quota in bytes (null = unlimited).
        /// </summary>
        public long? MaxSizeBytes { get; init; }

        /// <summary>
        /// Tags for categorization.
        /// </summary>
        public List<string> Tags { get; init; } = new();

        /// <summary>
        /// Custom metadata.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Container information.
    /// </summary>
    public class ContainerInfo
    {
        /// <summary>
        /// Unique container identifier.
        /// </summary>
        public string ContainerId { get; init; } = string.Empty;

        /// <summary>
        /// Human-readable display name.
        /// </summary>
        public string? DisplayName { get; init; }

        /// <summary>
        /// Owner user ID.
        /// </summary>
        public string OwnerId { get; init; } = string.Empty;

        /// <summary>
        /// When the container was created.
        /// </summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>
        /// When the container was last modified.
        /// </summary>
        public DateTime LastModifiedAt { get; init; }

        /// <summary>
        /// Current size in bytes.
        /// </summary>
        public long SizeBytes { get; init; }

        /// <summary>
        /// Number of items in the container.
        /// </summary>
        public int ItemCount { get; init; }

        /// <summary>
        /// Whether encryption is enabled by default.
        /// </summary>
        public bool EncryptByDefault { get; init; }

        /// <summary>
        /// Whether compression is enabled by default.
        /// </summary>
        public bool CompressByDefault { get; init; }

        /// <summary>
        /// Container tags.
        /// </summary>
        public List<string> Tags { get; init; } = new();

        /// <summary>
        /// Custom metadata.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Container access entry.
    /// </summary>
    public class ContainerAccessEntry
    {
        /// <summary>
        /// User ID with access.
        /// </summary>
        public string UserId { get; init; } = string.Empty;

        /// <summary>
        /// Access level granted.
        /// </summary>
        public ContainerAccessLevel Level { get; init; }

        /// <summary>
        /// When access was granted.
        /// </summary>
        public DateTime GrantedAt { get; init; }

        /// <summary>
        /// Who granted the access.
        /// </summary>
        public string GrantedBy { get; init; } = string.Empty;

        /// <summary>
        /// Optional expiration time.
        /// </summary>
        public DateTime? ExpiresAt { get; init; }
    }

    /// <summary>
    /// Container quota information.
    /// </summary>
    public class ContainerQuota
    {
        /// <summary>
        /// Maximum storage in bytes (null = unlimited).
        /// </summary>
        public long? MaxSizeBytes { get; init; }

        /// <summary>
        /// Current usage in bytes.
        /// </summary>
        public long UsedSizeBytes { get; init; }

        /// <summary>
        /// Maximum number of items (null = unlimited).
        /// </summary>
        public int? MaxItems { get; init; }

        /// <summary>
        /// Current item count.
        /// </summary>
        public int UsedItems { get; init; }

        /// <summary>
        /// Percentage of quota used (0-100).
        /// </summary>
        public double UsagePercent => MaxSizeBytes.HasValue && MaxSizeBytes > 0
            ? (double)UsedSizeBytes / MaxSizeBytes.Value * 100
            : 0;
    }
}
