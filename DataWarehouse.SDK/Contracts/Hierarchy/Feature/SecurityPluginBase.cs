using DataWarehouse.SDK.Security;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for security service plugins (access control, key management, compliance, threat detection).
/// </summary>
public abstract class SecurityPluginBase : FeaturePluginBase
{
    /// <inheritdoc/>
    public override string FeatureCategory => "Security";

    /// <summary>Security sub-domain (e.g., "AccessControl", "KeyManagement", "Compliance").</summary>
    public abstract string SecurityDomain { get; }

    /// <summary>
    /// Optional key store reference for security plugins that need key access.
    /// Set via <see cref="SetKeyStore"/>.
    /// </summary>
    protected IKeyStore? KeyStore { get; private set; }

    /// <summary>
    /// Configures the key store for this security plugin.
    /// </summary>
    /// <param name="keyStore">The key store instance.</param>
    public virtual void SetKeyStore(IKeyStore keyStore)
    {
        KeyStore = keyStore ?? throw new System.ArgumentNullException(nameof(keyStore));
    }

    /// <summary>
    /// Retrieves a key as a <see cref="NativeKeyHandle"/> for secure zero-copy operations.
    /// Delegates to <see cref="IKeyStore.GetKeyNativeAsync"/> on the configured key store.
    /// The caller MUST dispose the returned handle to trigger secure wipe.
    /// </summary>
    /// <param name="keyId">The key identifier.</param>
    /// <param name="context">Security context for ACL validation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="NativeKeyHandle"/> containing the key material in unmanaged memory.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown if no key store is configured.</exception>
    protected virtual Task<NativeKeyHandle> GetKeyNativeAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
    {
        var keyStore = KeyStore
            ?? throw new System.InvalidOperationException("No key store configured. Call SetKeyStore first.");
        return keyStore.GetKeyNativeAsync(keyId, context, ct);
    }

    /// <summary>AI hook: Evaluate access with intelligence.</summary>
    protected virtual Task<Dictionary<string, object>> EvaluateAccessWithIntelligenceAsync(Dictionary<string, object> request, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object> { ["allowed"] = true });

    /// <summary>AI hook: Detect anomalous access patterns.</summary>
    protected virtual Task<bool> DetectAnomalousAccessAsync(Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["SecurityDomain"] = SecurityDomain;
        return metadata;
    }
}
