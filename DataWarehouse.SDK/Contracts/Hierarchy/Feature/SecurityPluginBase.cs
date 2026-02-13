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
