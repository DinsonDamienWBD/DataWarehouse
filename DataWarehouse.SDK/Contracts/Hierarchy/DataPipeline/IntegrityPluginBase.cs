using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for integrity pipeline plugins. Verifies data at boundaries.
/// </summary>
public abstract class IntegrityPluginBase : DataPipelinePluginBase
{
    /// <inheritdoc/>
    public override bool MutatesData => false;

    /// <summary>Verify integrity of an object.</summary>
    public abstract Task<Dictionary<string, object>> VerifyAsync(string key, CancellationToken ct = default);

    /// <summary>Compute hash of a data stream.</summary>
    public abstract Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);

    /// <summary>Validate integrity chain between two keys. Defaults to false (fail-closed) — subclasses must implement real validation.</summary>
    public virtual Task<bool> ValidateChainAsync(string startKey, string endKey, CancellationToken ct = default)
        => Task.FromResult(false);

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Verifies integrity of an object using the specified or default strategy.
    /// When a strategyId is provided, enforces ACL via the ACL provider
    /// before delegating to <see cref="VerifyAsync"/>. This provides a strategy-aware
    /// API surface consistent with other DataPipeline bases (Storage, Replication, Transit).
    /// </summary>
    /// <param name="key">The data key to verify.</param>
    /// <param name="strategyId">Optional strategy ID for ACL enforcement and routing.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result dictionary.</returns>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected virtual async Task<Dictionary<string, object>> VerifyWithStrategyAsync(
        string key,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        // ACL check if provider is configured and a strategyId is explicitly requested
        if (!string.IsNullOrEmpty(strategyId) && identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use integrity strategy '{strategyId}'.");
        }

        // Delegate to the abstract domain method — integrity plugins implement VerifyAsync
        return await VerifyAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes a hash of the provided data stream using the specified or default strategy.
    /// When a strategyId is provided, enforces ACL via the ACL provider
    /// before delegating to <see cref="ComputeHashAsync"/>. This provides a strategy-aware
    /// API surface consistent with other DataPipeline bases.
    /// </summary>
    /// <param name="data">The data stream to hash.</param>
    /// <param name="strategyId">Optional strategy ID for ACL enforcement and routing.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The computed hash bytes.</returns>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected virtual async Task<byte[]> ComputeHashWithStrategyAsync(
        Stream data,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        // ACL check if provider is configured and a strategyId is explicitly requested
        if (!string.IsNullOrEmpty(strategyId) && identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use integrity strategy '{strategyId}'.");
        }

        // Delegate to the abstract domain method — integrity plugins implement ComputeHashAsync
        return await ComputeHashAsync(data, ct).ConfigureAwait(false);
    }

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["IntegrityVerification"] = true;
        return metadata;
    }
}
