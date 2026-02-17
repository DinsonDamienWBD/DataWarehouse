using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract base class for container/partition manager plugins.
    /// Provides storage-agnostic partition management.
    /// Supports intelligent quota management and access suggestions.
    /// </summary>
    public abstract class ContainerManagerPluginBase : ComputePluginBase, IContainerManager
    {
        /// <inheritdoc/>
        public override string RuntimeType => "Container";

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, object> { ["status"] = "delegated-to-container-manager" });

        /// <summary>
        /// Category is always OrchestrationProvider for container managers.
        /// </summary>
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        public abstract Task<ContainerInfo> CreateContainerAsync(
            ISecurityContext context,
            string containerId,
            ContainerOptions? options = null,
            CancellationToken ct = default);

        public abstract Task<ContainerInfo?> GetContainerAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public abstract IAsyncEnumerable<ContainerInfo> ListContainersAsync(
            ISecurityContext context,
            CancellationToken ct = default);

        public abstract Task DeleteContainerAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public abstract Task GrantAccessAsync(
            ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            ContainerAccessLevel level,
            CancellationToken ct = default);

        public abstract Task RevokeAccessAsync(
            ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            CancellationToken ct = default);

        public abstract Task<ContainerAccessLevel> GetAccessLevelAsync(
            ISecurityContext context,
            string containerId,
            string? userId = null,
            CancellationToken ct = default);

        public abstract IAsyncEnumerable<ContainerAccessEntry> ListAccessAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public virtual Task<ContainerQuota> GetQuotaAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ContainerQuota());
        }

        public virtual Task SetQuotaAsync(
            ISecurityContext adminContext,
            string containerId,
            ContainerQuota quota,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "ContainerManager";
            metadata["SupportsQuotas"] = true;
            metadata["SupportsAccessControl"] = true;
            metadata["StorageAgnostic"] = true;
            return metadata;
        }
    }
}
