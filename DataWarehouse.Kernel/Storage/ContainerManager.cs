using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Storage
{
    /// <summary>
    /// Production-ready container manager implementing namespace/partition management
    /// with quota enforcement and access control. Essential for multi-tenant deployments.
    /// </summary>
    public class ContainerManager : ContainerManagerPluginBase
    {
        private readonly ConcurrentDictionary<string, Container> _containers = new();
        private readonly ConcurrentDictionary<string, ContainerQuota> _quotas = new();
        private readonly ConcurrentDictionary<string, List<AccessEntry>> _accessEntries = new();
        private readonly ConcurrentDictionary<string, ContainerUsage> _usage = new();
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

        #region Container Lifecycle

        public override async Task<ContainerInfo> CreateContainerAsync(
            string name,
            ContainerOptions? options = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Container name cannot be empty", nameof(name));

            if (_containers.ContainsKey(name))
                throw new InvalidOperationException($"Container '{name}' already exists");

            options ??= new ContainerOptions();

            var container = new Container
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = options.Owner ?? "system",
                State = ContainerState.Active,
                Metadata = options.Metadata ?? new Dictionary<string, object>(),
                Tags = options.Tags ?? new List<string>()
            };

            // Initialize quota
            var quota = new ContainerQuota
            {
                ContainerId = container.Id,
                MaxSizeBytes = options.MaxSizeBytes ?? _config.DefaultMaxSizeBytes,
                MaxItemCount = options.MaxItemCount ?? _config.DefaultMaxItemCount,
                MaxBandwidthBytesPerSecond = options.MaxBandwidthBytesPerSecond
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
                    SubjectId = options.Owner ?? "system",
                    SubjectType = SubjectType.User,
                    AccessLevel = ContainerAccessLevel.Owner,
                    GrantedAt = DateTime.UtcNow,
                    GrantedBy = "system"
                }
            };

            _containers[name] = container;
            _quotas[container.Id] = quota;
            _usage[container.Id] = usage;
            _accessEntries[container.Id] = accessEntries;

            _context.LogInfo($"[Container] Created container '{name}' (ID: {container.Id})");

            return await Task.FromResult(ToContainerInfo(container, quota, usage));
        }

        public override async Task<ContainerInfo?> GetContainerAsync(
            string name,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(name, out var container))
            {
                return null;
            }

            _quotas.TryGetValue(container.Id, out var quota);
            _usage.TryGetValue(container.Id, out var usage);

            return await Task.FromResult(ToContainerInfo(container, quota, usage));
        }

        public override async IAsyncEnumerable<ContainerInfo> ListContainersAsync(
            string? prefix = null,
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
            string name,
            bool force = false,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(name, out var container))
            {
                throw new InvalidOperationException($"Container '{name}' not found");
            }

            // Check if container is empty (unless force)
            if (!force && _usage.TryGetValue(container.Id, out var usage) && usage.CurrentItemCount > 0)
            {
                throw new InvalidOperationException(
                    $"Container '{name}' is not empty ({usage.CurrentItemCount} items). Use force=true to delete anyway.");
            }

            _containers.TryRemove(name, out _);
            _quotas.TryRemove(container.Id, out _);
            _usage.TryRemove(container.Id, out _);
            _accessEntries.TryRemove(container.Id, out _);

            _context.LogInfo($"[Container] Deleted container '{name}' (ID: {container.Id})");

            await Task.CompletedTask;
        }

        #endregion

        #region Access Control

        public override async Task GrantAccessAsync(
            string containerName,
            string subjectId,
            ContainerAccessLevel accessLevel,
            string? grantedBy = null,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                throw new InvalidOperationException($"Container '{containerName}' not found");
            }

            if (!_accessEntries.TryGetValue(container.Id, out var entries))
            {
                entries = new List<AccessEntry>();
                _accessEntries[container.Id] = entries;
            }

            // Remove existing entry for this subject
            entries.RemoveAll(e => e.SubjectId == subjectId);

            // Add new entry
            entries.Add(new AccessEntry
            {
                SubjectId = subjectId,
                SubjectType = SubjectType.User, // Could be extended to support groups
                AccessLevel = accessLevel,
                GrantedAt = DateTime.UtcNow,
                GrantedBy = grantedBy ?? "system"
            });

            _context.LogInfo($"[Container] Granted {accessLevel} access to '{subjectId}' on '{containerName}'");

            await Task.CompletedTask;
        }

        public override async Task RevokeAccessAsync(
            string containerName,
            string subjectId,
            string? revokedBy = null,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                throw new InvalidOperationException($"Container '{containerName}' not found");
            }

            if (_accessEntries.TryGetValue(container.Id, out var entries))
            {
                var removed = entries.RemoveAll(e => e.SubjectId == subjectId);
                if (removed > 0)
                {
                    _context.LogInfo($"[Container] Revoked access for '{subjectId}' on '{containerName}'");
                }
            }

            await Task.CompletedTask;
        }

        public override async Task<ContainerAccessLevel> GetAccessLevelAsync(
            string containerName,
            string subjectId,
            CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                return ContainerAccessLevel.None;
            }

            if (!_accessEntries.TryGetValue(container.Id, out var entries))
            {
                return ContainerAccessLevel.None;
            }

            var entry = entries.FirstOrDefault(e => e.SubjectId == subjectId);
            return await Task.FromResult(entry?.AccessLevel ?? ContainerAccessLevel.None);
        }

        public override async IAsyncEnumerable<ContainerAccessEntry> ListAccessAsync(
            string containerName,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerName, out var container))
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
                    SubjectId = entry.SubjectId,
                    SubjectType = entry.SubjectType.ToString(),
                    AccessLevel = entry.AccessLevel,
                    GrantedAt = entry.GrantedAt,
                    GrantedBy = entry.GrantedBy
                };

                await Task.Yield();
            }
        }

        /// <summary>
        /// Checks if a subject has at least the specified access level.
        /// </summary>
        public bool HasAccess(string containerName, string subjectId, ContainerAccessLevel requiredLevel)
        {
            var level = GetAccessLevelAsync(containerName, subjectId).GetAwaiter().GetResult();
            return level >= requiredLevel;
        }

        #endregion

        #region Quota Management

        /// <summary>
        /// Gets the quota configuration for a container.
        /// </summary>
        public Task<ContainerQuota?> GetQuotaAsync(string containerName, CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                return Task.FromResult<ContainerQuota?>(null);
            }

            _quotas.TryGetValue(container.Id, out var quota);
            return Task.FromResult(quota);
        }

        /// <summary>
        /// Sets the quota configuration for a container.
        /// </summary>
        public Task SetQuotaAsync(string containerName, ContainerQuota quota, CancellationToken ct = default)
        {
            if (!_containers.TryGetValue(containerName, out var container))
            {
                throw new InvalidOperationException($"Container '{containerName}' not found");
            }

            quota.ContainerId = container.Id;
            _quotas[container.Id] = quota;

            _context.LogInfo($"[Container] Updated quota for '{containerName}': " +
                           $"MaxSize={quota.MaxSizeBytes / (1024 * 1024)}MB, MaxItems={quota.MaxItemCount}");

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
            return Task.FromResult(usage);
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

            if (!_quotas.TryGetValue(container.Id, out var quota))
            {
                return new QuotaCheckResult { Allowed = true }; // No quota set
            }

            if (!_usage.TryGetValue(container.Id, out var usage))
            {
                return new QuotaCheckResult { Allowed = true }; // No usage tracked
            }

            // Check size quota
            if (quota.MaxSizeBytes.HasValue)
            {
                var projectedSize = usage.CurrentSizeBytes + additionalBytes;
                if (projectedSize > quota.MaxSizeBytes.Value)
                {
                    return new QuotaCheckResult
                    {
                        Allowed = false,
                        Reason = $"Size quota exceeded: {projectedSize} > {quota.MaxSizeBytes.Value}",
                        QuotaType = "Size",
                        Current = usage.CurrentSizeBytes,
                        Limit = quota.MaxSizeBytes.Value,
                        Requested = additionalBytes
                    };
                }
            }

            // Check item count quota
            if (quota.MaxItemCount.HasValue)
            {
                var projectedCount = usage.CurrentItemCount + additionalItems;
                if (projectedCount > quota.MaxItemCount.Value)
                {
                    return new QuotaCheckResult
                    {
                        Allowed = false,
                        Reason = $"Item count quota exceeded: {projectedCount} > {quota.MaxItemCount.Value}",
                        QuotaType = "ItemCount",
                        Current = usage.CurrentItemCount,
                        Limit = quota.MaxItemCount.Value,
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

        private static ContainerInfo ToContainerInfo(Container container, ContainerQuota? quota, ContainerUsage? usage)
        {
            return new ContainerInfo
            {
                Id = container.Id,
                Name = container.Name,
                CreatedAt = container.CreatedAt,
                CreatedBy = container.CreatedBy,
                State = container.State,
                Metadata = container.Metadata,
                Tags = container.Tags,
                // Quota info
                MaxSizeBytes = quota?.MaxSizeBytes,
                MaxItemCount = quota?.MaxItemCount,
                // Usage info
                CurrentSizeBytes = usage?.CurrentSizeBytes ?? 0,
                CurrentItemCount = usage?.CurrentItemCount ?? 0,
                LastUpdated = usage?.LastUpdated
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
    /// Options for creating a container.
    /// </summary>
    public class ContainerOptions
    {
        public string? Owner { get; set; }
        public long? MaxSizeBytes { get; set; }
        public int? MaxItemCount { get; set; }
        public long? MaxBandwidthBytesPerSecond { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
        public List<string>? Tags { get; set; }
    }

    /// <summary>
    /// Container quota configuration.
    /// </summary>
    public class ContainerQuota
    {
        public string ContainerId { get; set; } = string.Empty;
        public long? MaxSizeBytes { get; set; }
        public int? MaxItemCount { get; set; }
        public long? MaxBandwidthBytesPerSecond { get; set; }
    }

    /// <summary>
    /// Container usage statistics.
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

    /// <summary>
    /// Container information.
    /// </summary>
    public class ContainerInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public ContainerState State { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public List<string> Tags { get; set; } = new();
        public long? MaxSizeBytes { get; set; }
        public int? MaxItemCount { get; set; }
        public long CurrentSizeBytes { get; set; }
        public int CurrentItemCount { get; set; }
        public DateTime? LastUpdated { get; set; }
    }

    /// <summary>
    /// Container access entry.
    /// </summary>
    public class ContainerAccessEntry
    {
        public string SubjectId { get; set; } = string.Empty;
        public string SubjectType { get; set; } = string.Empty;
        public ContainerAccessLevel AccessLevel { get; set; }
        public DateTime GrantedAt { get; set; }
        public string GrantedBy { get; set; } = string.Empty;
    }

    #endregion
}
