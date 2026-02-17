using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for encryption pipeline plugins. Provides key management infrastructure,
/// algorithm selection, and cryptographic statistics tracking.
/// </summary>
public abstract class EncryptionPluginBase : DataTransformationPluginBase
{
    /// <inheritdoc/>
    public override string SubCategory => "Encryption";

    /// <summary>Key size in bytes (e.g., 32 for AES-256).</summary>
    public abstract int KeySizeBytes { get; }

    /// <summary>IV/nonce size in bytes.</summary>
    public abstract int IvSizeBytes { get; }

    /// <summary>Authentication tag size in bytes (0 if non-AEAD).</summary>
    public virtual int TagSizeBytes => 0;

    /// <summary>Algorithm identifier (e.g., "AES-256-GCM").</summary>
    public abstract string AlgorithmId { get; }

    /// <inheritdoc/>
    /// <remarks>Default returns <see cref="AlgorithmId"/> for encryption-specific selection.</remarks>
    protected override Task<string> SelectOptimalAlgorithmAsync(Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(AlgorithmId);

    /// <summary>AI hook: Evaluate key strength.</summary>
    protected virtual Task<int> EvaluateKeyStrengthAsync(byte[] keyMaterial, CancellationToken ct = default)
        => Task.FromResult(100);

    /// <summary>AI hook: Detect encryption anomalies.</summary>
    protected virtual Task<bool> DetectEncryptionAnomalyAsync(Dictionary<string, object> operationContext, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["AlgorithmId"] = AlgorithmId;
        metadata["KeySizeBytes"] = KeySizeBytes;
        metadata["IvSizeBytes"] = IvSizeBytes;
        metadata["TagSizeBytes"] = TagSizeBytes;
        return metadata;
    }
}
