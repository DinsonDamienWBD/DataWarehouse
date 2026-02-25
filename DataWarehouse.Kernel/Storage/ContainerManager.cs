using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Kernel.Storage
{
    /// <summary>
    /// Production-ready container manager implementing namespace/partition management
    /// with quota enforcement and access control. Essential for multi-tenant deployments.
    /// </summary>
    public class ContainerManager : ContainerManagerPluginBase
    {
        private readonly BoundedDictionary<string, Container> _containers = new BoundedDictionary<string, Container>(1000);
        private readonly BoundedDictionary<string, InternalQuota> _quotas = new BoundedDictionary<string, InternalQuota>(1000);
        private readonly BoundedDictionary<string, List<AccessEntry>> _accessEntries = new BoundedDictionary<string, List<AccessEntry>>(1000);
        private readonly BoundedDictionary<string, ContainerUsage> _usage = new BoundedDictionary<string, ContainerUsage>(1000);
        private readonly IKernelContext _context;
        private readonly ContainerManagerConfig _config;

        public override string Id => "kernel-container-manager";
        public override string Name => "Kernel Container Manager";
        public override string Version => "1.0.0";

        public ContainerManager(IKernelContext context, ContainerManagerConfig? config = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _config = config ?? new ContainerManagerConfig();
        }

        #region Lifecycle

        public override Task StartAsync(CancellationToken ct = default)
        {
            _context.LogInfo("[ContainerManager] Started");
            return Task.CompletedTask;
        }

        public override Task StopAsync()
        {
            _context.LogInfo("[ContainerManager] Stopped");
            return Task.CompletedTask;
        }

        #endregion

        #region Container Lifecycle

        public override async Task<ContainerInfo> CreateContainerAsync(
            ISecurityContext context,
            string containerId,
            ContainerOptions? options = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(containerId))
                throw new ArgumentException("Container name cannot be empty", nameof(containerId));

            if (_containers.ContainsKey(containerId))
                throw new InvalidOperationException($"Container '{containerId}' already exists");

            options ??= new ContainerOptions();

            var container = new Container
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = containerId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = context.UserId,
                State = ContainerState.Active,
                Metadata = options.Metadata ?? new Dictionary<string, object>(),
                Tags = options.Tags ?? new List<string>()
            };

            // Initialize quota
            var quota = new InternalQuota
            {
                ContainerId = container.Id,
                MaxSizeBytes = options.MaxSizeBytes ?? _config.DefaultMaxSizeBytes,
                MaxItemCount = _config.DefaultMaxItemCount,
                MaxBandwidthBytesPerSecond = null
            };

            // Initialize usage tracking
            var usage = new ContainerUsage
            {
                ContainerId = container.Id,
                CurrentSizeBytes = 0,
                CurrentItemCount = 0,
                LastUpdated = DateTime.UtcNow
            };

            // Initialize access entries with owner
            var accessEntries = new List<AccessEntry>
            {
                new AccessEntry
                {
                    SubjectId = context.UserId,
                    SubjectType = SubjectType.User,
                    AccessLevel = ContainerAccessLevel.Owner,
                    GrantedAt = DateTime.UtcNow,
                    GrantedBy = "system"
                }
            };

            _containers[containerId] = container;
            _quotas[container.Id] = quota;
            _usage[container.Id] = usage;
            _accessEntries[container.Id] = accessEntries;

            _context.LogInfo($"[Container] Created container '{containerId}' (ID: {container.Id})");

            return await Task.FromResult(ToContainerInfo(container, quota, usage));
        }

        public override async Task<ContainerInfo?> GetContainerAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerId, out var container))
            {
                return null;
            }

            _quotas.TryGetValue(container.Id, out var quota);
            _usage.TryGetValue(container.Id, out var usage);

            return await Task.FromResult(ToContainerInfo(container, quota, usage));
        }

        public override async IAsyncEnumerable<ContainerInfo> ListContainersAsync(
            ISecurityContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var containers = _containers.Values.AsEnumerable();

            foreach (var container in containers.OrderBy(c => c.Name))
            {
                ct.ThrowIfCancellationRequested();

                _quotas.TryGetValue(container.Id, out var quota);
                _usage.TryGetValue(container.Id, out var usage);

                yield return ToContainerInfo(container, quota, usage);
                await Task.Yield();
            }
        }

        /// <summary>
        /// Lists containers matching a prefix filter (convenience overload).
        /// </summary>
        public async IAsyncEnumerable<ContainerInfo> ListContainersAsync(
            ISecurityContext context,
            string? prefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var containers = _containers.Values.AsEnumerable();

            if (!string.IsNullOrEmpty(prefix))
            {
                containers = containers.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var container in containers.OrderBy(c => c.Name))
            {
                ct.ThrowIfCancellationRequested();

                _quotas.TryGetValue(container.Id, out var quota);
                _usage.TryGetValue(container.Id, out var usage);

                yield return ToContainerInfo(container, quota, usage);
                await Task.Yield();
            }
        }

        public override async Task DeleteContainerAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerId, out var container))
            {
                throw new InvalidOperationException($"Container '{containerId}' not found");
            }

            _containers.TryRemove(containerId, out _);
            _quotas.TryRemove(container.Id, out _);
            _usage.TryRemove(container.Id, out _);
            _accessEntries.TryRemove(container.Id, out _);

            _context.LogInfo($"[Container] Deleted container '{containerId}' (ID: {container.Id})");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Deletes a container with force option (convenience overload).
        /// </summary>
        public async Task DeleteContainerAsync(
            ISecurityContext context,
            string containerId,
            bool force,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerId, out var container))
            {
                throw new InvalidOperationException($"Container '{containerId}' not found");
            }

            // Check if container is empty (unless force)
            if (!force && _usage.TryGetValue(container.Id, out var usage) && usage.CurrentItemCount > 0)
            {
                throw new InvalidOperationException(
                    $"Container '{containerId}' is not empty ({usage.CurrentItemCount} items). Use force=true to delete anyway.");
            }

            await DeleteContainerAsync(context, containerId, ct);
        }

        #endregion

        #region Access Control

        public override async Task GrantAccessAsync(
            ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            ContainerAccessLevel level,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerId, out var container))
            {
                throw new InvalidOperationException($"Container '{containerId}' not found");
            }

            if (!_accessEntries.TryGetValue(container.Id, out var entries))
            {
                entries = new List<AccessEntry>();
                _accessEntries[container.Id] = entries;
            }

            // Remove existing entry for this subject
            entries.RemoveAll(e => e.SubjectId == targetUserId);

            // Add new entry
            entries.Add(new AccessEntry
            {
                SubjectId = targetUserId,
                SubjectType = SubjectType.User, // Could be extended to support groups
                AccessLevel = level,
                GrantedAt = DateTime.UtcNow,
                GrantedBy = ownerContext.UserId
            });

            _context.LogInfo($"[Container] Granted {level} access to '{targetUserId}' on '{containerId}'");

            await Task.CompletedTask;
        }

        public override async Task RevokeAccessAsync(
            ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerId, out var container))
            {
                throw new InvalidOperationException($"Container '{containerId}' not found");
            }

            if (_accessEntries.TryGetValue(container.Id, out var entries))
            {
                var removed = entries.RemoveAll(e => e.SubjectId == targetUserId);
                if (removed > 0)
                {
                    _context.LogInfo($"[Container] Revoked access for '{targetUserId}' on '{containerId}'");
                }
            }

            await Task.CompletedTask;
        }

        public override async Task<ContainerAccessLevel> GetAccessLevelAsync(
            ISecurityContext context,
            string containerId,
            string? userId = null,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerId, out var container))
            {
                return ContainerAccessLevel.None;
            }

            if (!_accessEntries.TryGetValue(container.Id, out var entries))
            {
                return ContainerAccessLevel.None;
            }

            var subjectId = userId ?? context.UserId;
            var entry = entries.FirstOrDefault(e => e.SubjectId == subjectId);
            return await Task.FromResult(entry?.AccessLevel ?? ContainerAccessLevel.None);
        }

        public override async IAsyncEnumerable<ContainerAccessEntry> ListAccessAsync(
            ISecurityContext context,
            string containerId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerId, out var container))
            {
                yield break;
            }

            if (!_accessEntries.TryGetValue(container.Id, out var entries))
            {
                yield break;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                yield return new ContainerAccessEntry
                {
                    UserId = entry.SubjectId,
                    Level = entry.AccessLevel,
                    GrantedAt = entry.GrantedAt,
                    GrantedBy = entry.GrantedBy,
                    ExpiresAt = null
                };

                await Task.Yield();
            }
        }

        /// <summary>
        /// Checks if a subject has at least the specified access level.
        /// </summary>
        public async Task<bool> HasAccessAsync(ISecurityContext context, string containerId, ContainerAccessLevel requiredLevel)
        {
            var level = await GetAccessLevelAsync(context, containerId, context.UserId);
            return level >= requiredLevel;
        }

        #endregion

        #region Quota Management

        /// <summary>
        /// Gets the quota configuration for a container.
        /// </summary>
        public override Task<ContainerQuota> GetQuotaAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerId, out var container))
            {
                return Task.FromResult(new ContainerQuota());
            }

            _quotas.TryGetValue(container.Id, out var internalQuota);
            _usage.TryGetValue(container.Id, out var usage);

            return Task.FromResult(new ContainerQuota
            {
                MaxSizeBytes = internalQuota?.MaxSizeBytes,
                UsedSizeBytes = usage?.CurrentSizeBytes ?? 0,
                MaxItems = internalQuota?.MaxItemCount,
                UsedItems = usage?.CurrentItemCount ?? 0
            });
        }

        /// <summary>
        /// Sets the quota configuration for a container.
        /// </summary>
        public override Task SetQuotaAsync(
            ISecurityContext adminContext,
            string containerId,
            ContainerQuota quota,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerId, out var container))
            {
                throw new InvalidOperationException($"Container '{containerId}' not found");
            }

            var internalQuota = new InternalQuota
            {
                ContainerId = container.Id,
                MaxSizeBytes = quota.MaxSizeBytes,
                MaxItemCount = quota.MaxItems
            };
            _quotas[container.Id] = internalQuota;

            _context.LogInfo($"[Container] Updated quota for '{containerId}': " +
                           $"MaxSize={quota.MaxSizeBytes / (1024 * 1024)}MB, MaxItems={quota.MaxItems}");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets current usage for a container.
        /// </summary>
        public Task<ContainerUsage?> GetUsageAsync(string containerName, CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                return Task.FromResult<ContainerUsage?>(null);
            }

            _usage.TryGetValue(container.Id, out var usage);
            return Task.FromResult<ContainerUsage?>(usage);
        }

        /// <summary>
        /// Checks if an operation would exceed quota.
        /// </summary>
        public QuotaCheckResult CheckQuota(string containerName, long additionalBytes, int additionalItems)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                return new QuotaCheckResult { Allowed = false, Reason = "Container not found" };
            }

            if (!_quotas.TryGetValue(container.Id, out var internalQuota))
            {
                return new QuotaCheckResult { Allowed = true }; // No quota set
            }

            if (!_usage.TryGetValue(container.Id, out var usage))
            {
                return new QuotaCheckResult { Allowed = true }; // No usage tracked
            }

            // Check size quota
            if (internalQuota.MaxSizeBytes.HasValue)
            {
                var projectedSize = usage.CurrentSizeBytes + additionalBytes;
                if (projectedSize > internalQuota.MaxSizeBytes.Value)
                {
                    return new QuotaCheckResult
                    {
                        Allowed = false,
                        Reason = $"Size quota exceeded: {projectedSize} > {internalQuota.MaxSizeBytes.Value}",
                        QuotaType = "Size",
                        Current = usage.CurrentSizeBytes,
                        Limit = internalQuota.MaxSizeBytes.Value,
                        Requested = additionalBytes
                    };
                }
            }

            // Check item count quota
            if (internalQuota.MaxItemCount.HasValue)
            {
                var projectedCount = usage.CurrentItemCount + additionalItems;
                if (projectedCount > internalQuota.MaxItemCount.Value)
                {
                    return new QuotaCheckResult
                    {
                        Allowed = false,
                        Reason = $"Item count quota exceeded: {projectedCount} > {internalQuota.MaxItemCount.Value}",
                        QuotaType = "ItemCount",
                        Current = usage.CurrentItemCount,
                        Limit = internalQuota.MaxItemCount.Value,
                        Requested = additionalItems
                    };
                }
            }

            return new QuotaCheckResult { Allowed = true };
        }

        /// <summary>
        /// Records usage after a successful operation.
        /// </summary>
        public void RecordUsage(string containerName, long bytesChanged, int itemsChanged)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                return;
            }

            if (!_usage.TryGetValue(container.Id, out var usage))
            {
                usage = new ContainerUsage { ContainerId = container.Id };
                _usage[container.Id] = usage;
            }

            usage.CurrentSizeBytes = Math.Max(0, usage.CurrentSizeBytes + bytesChanged);
            usage.CurrentItemCount = Math.Max(0, usage.CurrentItemCount + itemsChanged);
            usage.LastUpdated = DateTime.UtcNow;

            if (bytesChanged > 0)
                usage.TotalBytesWritten += bytesChanged;
            else
                usage.TotalBytesDeleted += Math.Abs(bytesChanged);
        }

        #endregion

        #region Container State Management

        /// <summary>
        /// Suspends a container (read-only mode).
        /// </summary>
        public Task SuspendAsync(string containerName, string? reason = null, CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                throw new InvalidOperationException($"Container '{containerName}' not found");
            }

            container.State = ContainerState.Suspended;
            container.Metadata["SuspendedAt"] = DateTime.UtcNow;
            container.Metadata["SuspendReason"] = reason ?? "Manual suspension";

            _context.LogWarning($"[Container] Suspended container '{containerName}': {reason}");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Resumes a suspended container.
        /// </summary>
        public Task ResumeAsync(string containerName, CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                throw new InvalidOperationException($"Container '{containerName}' not found");
            }

            container.State = ContainerState.Active;
            container.Metadata.Remove("SuspendedAt");
            container.Metadata.Remove("SuspendReason");

            _context.LogInfo($"[Container] Resumed container '{containerName}'");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Checks if a container allows writes.
        /// </summary>
        public bool AllowsWrites(string containerName)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                return false;
            }

            return container.State == ContainerState.Active;
        }

        #endregion

        #region Helper Methods

        private static ContainerInfo ToContainerInfo(Container container, InternalQuota? quota, ContainerUsage? usage)
        {
            return new ContainerInfo
            {
                ContainerId = container.Id,
                DisplayName = container.Name,
                OwnerId = container.CreatedBy,
                CreatedAt = container.CreatedAt,
                LastModifiedAt = usage?.LastUpdated ?? container.CreatedAt,
                SizeBytes = usage?.CurrentSizeBytes ?? 0,
                ItemCount = usage?.CurrentItemCount ?? 0,
                EncryptByDefault = false,
                CompressByDefault = false,
                Tags = container.Tags,
                Metadata = container.Metadata
            };
        }

        #endregion

        #region Internal Classes

        private class Container
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public string CreatedBy { get; set; } = string.Empty;
            public ContainerState State { get; set; }
            public Dictionary<string, object> Metadata { get; set; } = new();
            public List<string> Tags { get; set; } = new();
        }

        private class AccessEntry
        {
            public string SubjectId { get; set; } = string.Empty;
            public SubjectType SubjectType { get; set; }
            public ContainerAccessLevel AccessLevel { get; set; }
            public DateTime GrantedAt { get; set; }
            public string GrantedBy { get; set; } = string.Empty;
        }

        private enum SubjectType
        {
            User,
            Group,
            Role,
            Service
        }

        private class InternalQuota
        {
            public string ContainerId { get; set; } = string.Empty;
            public long? MaxSizeBytes { get; set; }
            public int? MaxItemCount { get; set; }
            public long? MaxBandwidthBytesPerSecond { get; set; }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Configuration for the container manager.
    /// </summary>
    public class ContainerManagerConfig
    {
        public long DefaultMaxSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB
        public int DefaultMaxItemCount { get; set; } = 1_000_000;
        public bool EnforceQuotas { get; set; } = true;
    }

    /// <summary>
    /// Container usage statistics (internal tracking).
    /// </summary>
    public class ContainerUsage
    {
        public string ContainerId { get; set; } = string.Empty;
        public long CurrentSizeBytes { get; set; }
        public int CurrentItemCount { get; set; }
        public long TotalBytesWritten { get; set; }
        public long TotalBytesDeleted { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Result of a quota check.
    /// </summary>
    public class QuotaCheckResult
    {
        public bool Allowed { get; set; }
        public string? Reason { get; set; }
        public string? QuotaType { get; set; }
        public long Current { get; set; }
        public long Limit { get; set; }
        public long Requested { get; set; }
    }

    /// <summary>
    /// Container state.
    /// </summary>
    public enum ContainerState
    {
        Active,
        Suspended,
        ReadOnly,
        Deleted
    }

    #endregion
}
