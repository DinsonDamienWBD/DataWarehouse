// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateDataIntegrity.Hashing;
using Microsoft.Extensions.Logging;
using HashAlgorithmType = DataWarehouse.SDK.Contracts.TamperProof.HashAlgorithmType;

namespace DataWarehouse.Plugins.UltimateDataIntegrity;

/// <summary>
/// Ultimate Data Integrity Plugin - Comprehensive hash computation provider.
/// Owns ALL hash computation functionality extracted from TamperProof plugin.
///
/// Supports 15+ hash algorithms:
/// - SHA-2 family: SHA-256, SHA-384, SHA-512
/// - SHA-3 family: SHA3-256, SHA3-384, SHA3-512
/// - Keccak family: Keccak-256, Keccak-384, Keccak-512
/// - HMAC variants: HMAC-SHA256, HMAC-SHA384, HMAC-SHA512, HMAC-SHA3-256, HMAC-SHA3-384, HMAC-SHA3-512
///
/// Features:
/// - Span-based API for zero-copy hashing
/// - Stream-based async hashing
/// - Shard hashing with contextual metadata
/// - Batch shard hashing with parallelism
/// - Constant-time verification (HMAC)
/// - Hardware acceleration (SHA-2 via System.Security.Cryptography)
/// </summary>
public sealed class UltimateDataIntegrityPlugin : IntegrityProviderPluginBase
{
    private readonly ILogger<UltimateDataIntegrityPlugin>? _logger;
    private bool _disposed;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.integrity.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Data Integrity";

    /// <inheritdoc/>
    public override string Version => "1.0.0";


    /// <summary>
    /// List of all supported hash algorithms.
    /// </summary>
    /// <remarks>
    /// HMAC algorithms (HMAC-SHA256, HMAC-SHA384, HMAC-SHA512, HMAC-SHA3-256, HMAC-SHA3-384, HMAC-SHA3-512)
    /// are supported via the keyed async API but are NOT listed here because the span-based
    /// <see cref="ComputeHashCore"/> cannot accept key material.  Advertising them in this list
    /// caused callers to hit <see cref="NotSupportedException"/> at runtime.
    /// </remarks>
    public override IReadOnlyList<HashAlgorithmType> SupportedAlgorithms { get; } = new[]
    {
        HashAlgorithmType.SHA256,
        HashAlgorithmType.SHA384,
        HashAlgorithmType.SHA512,
        HashAlgorithmType.SHA3_256,
        HashAlgorithmType.SHA3_384,
        HashAlgorithmType.SHA3_512,
        HashAlgorithmType.Keccak256,
        HashAlgorithmType.Keccak384,
        HashAlgorithmType.Keccak512
    };

    /// <summary>
    /// Initializes a new instance of the Ultimate Data Integrity plugin.
    /// </summary>
    public UltimateDataIntegrityPlugin()
    {
        _logger = null;
    }

    /// <summary>
    /// Initializes a new instance with an explicit logger.
    /// </summary>
    public UltimateDataIntegrityPlugin(ILogger<UltimateDataIntegrityPlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        // Register knowledge and capabilities
        await RegisterAllKnowledgeAsync();

        response.Metadata["SupportedAlgorithms"] = string.Join(", ", SupportedAlgorithms);
        response.Metadata["DefaultAlgorithm"] = DefaultAlgorithm.ToString();
        response.Metadata["SupportsHMAC"] = "true";
        response.Metadata["SupportsSHA3"] = "true";
        response.Metadata["SupportsKeccak"] = "true";

        _logger?.LogInformation("UltimateDataIntegrity plugin initialized with {Count} algorithms", SupportedAlgorithms.Count);

        return response;
    }

    /// <summary>
    /// Core hash computation implementation.
    /// Dispatches to the appropriate hash provider based on algorithm type.
    /// </summary>
    protected override byte[] ComputeHashCore(ReadOnlySpan<byte> data, HashAlgorithmType algorithm)
    {
        IHashProvider provider = algorithm switch
        {
            HashAlgorithmType.SHA256 => new Sha256Provider(),
            HashAlgorithmType.SHA384 => new Sha384Provider(),
            HashAlgorithmType.SHA512 => new Sha512Provider(),
            HashAlgorithmType.SHA3_256 => new Sha3_256Provider(),
            HashAlgorithmType.SHA3_384 => new Sha3_384Provider(),
            HashAlgorithmType.SHA3_512 => new Sha3_512Provider(),
            HashAlgorithmType.Keccak256 => new Keccak256Provider(),
            HashAlgorithmType.Keccak384 => new Keccak384Provider(),
            HashAlgorithmType.Keccak512 => new Keccak512Provider(),
            // HMAC algorithms require a key, which we don't have in this method signature.
            // For HMAC, callers should use the async methods with explicit key material.
            // For now, throw for HMAC algorithms in the span-based API.
            HashAlgorithmType.HMAC_SHA256 or
            HashAlgorithmType.HMAC_SHA384 or
            HashAlgorithmType.HMAC_SHA512 or
            HashAlgorithmType.HMAC_SHA3_256 or
            HashAlgorithmType.HMAC_SHA3_384 or
            HashAlgorithmType.HMAC_SHA3_512 =>
                throw new NotSupportedException($"HMAC algorithm {algorithm} requires key material. Use ComputeHashAsync with key."),
            _ => throw new NotSupportedException($"Hash algorithm {algorithm} is not supported.")
        };

        return provider.ComputeHash(data);
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case "integrity.hash.compute":
                    await HandleHashComputeAsync(message, CancellationToken.None);
                    break;

                case "integrity.hash.verify":
                    await HandleHashVerifyAsync(message, CancellationToken.None);
                    break;

                case "integrity.supported.algorithms":
                    HandleSupportedAlgorithms(message);
                    break;

                default:
                    _logger?.LogDebug("UltimateDataIntegrity received unknown message type: {Type}", message.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling message of type {Type}", message.Type);
            message.Payload["error"] = $"Message handling failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles integrity.hash.compute message bus topic.
    /// Computes a hash of provided data using the specified algorithm.
    /// </summary>
    private async Task HandleHashComputeAsync(PluginMessage message, CancellationToken ct)
    {
        try
        {
            if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
            {
                message.Payload["error"] = "Missing or invalid 'data' field";
                return;
            }

            var algorithm = HashAlgorithmType.SHA256;
            if (message.Payload.TryGetValue("algorithm", out var algObj) && algObj is string algStr)
            {
                if (Enum.TryParse<HashAlgorithmType>(algStr, true, out var parsedAlg))
                {
                    algorithm = parsedAlg;
                }
            }

            _logger?.LogDebug("Computing hash using algorithm {Algorithm}", algorithm);

            var hash = await ComputeHashAsync(data, algorithm, ct);

            message.Payload["hash"] = Convert.FromHexString(hash.HashValue);
            message.Payload["hashHex"] = hash.HashValue;
            message.Payload["algorithm"] = hash.Algorithm.ToString();
            message.Payload["computedAt"] = hash.ComputedAt;

            _logger?.LogDebug("Hash computed successfully using {Algorithm}", algorithm);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to compute hash");
            message.Payload["error"] = $"Hash computation failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles integrity.hash.verify message bus topic.
    /// Verifies a hash against provided data using constant-time comparison.
    /// </summary>
    private async Task HandleHashVerifyAsync(PluginMessage message, CancellationToken ct)
    {
        try
        {
            if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
            {
                message.Payload["error"] = "Missing or invalid 'data' field";
                message.Payload["valid"] = false;
                return;
            }

            if (!message.Payload.TryGetValue("expectedHash", out var expectedHashObj) || expectedHashObj is not byte[] expectedHashBytes)
            {
                message.Payload["error"] = "Missing or invalid 'expectedHash' field";
                message.Payload["valid"] = false;
                return;
            }

            var algorithm = HashAlgorithmType.SHA256;
            if (message.Payload.TryGetValue("algorithm", out var algObj) && algObj is string algStr)
            {
                if (Enum.TryParse<HashAlgorithmType>(algStr, true, out var parsedAlg))
                {
                    algorithm = parsedAlg;
                }
            }

            _logger?.LogDebug("Verifying hash using algorithm {Algorithm}", algorithm);

            var actualHash = await ComputeHashAsync(data, algorithm, ct);
            var actualHashBytes = Convert.FromHexString(actualHash.HashValue);

            // Constant-time comparison to prevent timing attacks
            bool isValid = CryptographicOperations.FixedTimeEquals(actualHashBytes, expectedHashBytes);

            message.Payload["valid"] = isValid;
            message.Payload["actualHashHex"] = actualHash.HashValue;

            _logger?.LogDebug("Hash verification {Result} using {Algorithm}",
                isValid ? "succeeded" : "failed", algorithm);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to verify hash");
            message.Payload["error"] = $"Hash verification failed: {ex.Message}";
            message.Payload["valid"] = false;
        }
    }

    /// <summary>
    /// Handles integrity.supported.algorithms message bus topic.
    /// Returns list of supported algorithms.
    /// </summary>
    private void HandleSupportedAlgorithms(PluginMessage message)
    {
        message.Payload["algorithms"] = SupportedAlgorithms.Select(a => a.ToString()).ToArray();
        message.Payload["count"] = SupportedAlgorithms.Count;
        message.Payload["defaultAlgorithm"] = DefaultAlgorithm.ToString();
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "integrity.hash.compute", DisplayName = "Compute Hash", Description = "Compute hash using selected algorithm" },
            new() { Name = "integrity.hash.verify", DisplayName = "Verify Hash", Description = "Verify hash with constant-time comparison" },
            new() { Name = "integrity.supported.algorithms", DisplayName = "Supported Algorithms", Description = "List all supported hash algorithms" },
            new() { Name = "integrity.shard.hash", DisplayName = "Shard Hash", Description = "Compute hash for RAID shard with metadata" },
            new() { Name = "integrity.batch.hash", DisplayName = "Batch Hash", Description = "Compute hashes for multiple shards in parallel" }
        ];
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "IntegrityProvider";
        metadata["AlgorithmCount"] = SupportedAlgorithms.Count;
        metadata["SupportsSHA2"] = true;
        metadata["SupportsSHA3"] = true;
        metadata["SupportsKeccak"] = true;
        metadata["SupportsHMAC"] = true;
        metadata["HardwareAcceleration"] = "SHA-2";
        return metadata;
    }

    /// <summary>
    /// Disposes resources used by the plugin.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _logger?.LogDebug("Disposing UltimateDataIntegrityPlugin");
        }

        _disposed = true;
        base.Dispose(disposing);
    }
}
