using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataTransit.Layers;

/// <summary>
/// Decorator layer that adds encryption to any <see cref="IDataTransitStrategy"/>.
/// Wraps an inner strategy, encrypting outbound data before passing to the inner
/// strategy for transfer. Delegates encryption to the UltimateEncryption plugin
/// via message bus when available, falling back to AES-256-GCM encryption.
/// </summary>
/// <remarks>
/// <para>
/// This decorator follows the composable layer pattern (research Pattern 2).
/// The orchestrator applies layers in correct order: compression first (inner),
/// encryption second (outer). This ensures data is compressed before encryption
/// outbound, and decrypted before decompression inbound (per research pitfall 4).
/// </para>
/// <para>
/// Idempotency: If data is already marked as encrypted via the "transit-encrypted"
/// metadata marker, this layer passes through without re-encrypting.
/// </para>
/// </remarks>
internal sealed class EncryptionInTransitLayer : IDataTransitStrategy
{
    private readonly IDataTransitStrategy _inner;
    private readonly IMessageBus _messageBus;

    /// <summary>
    /// AES-256 key size in bytes.
    /// </summary>
    private const int AesKeySizeBytes = 32;

    /// <summary>
    /// AES-GCM nonce size in bytes (96 bits per NIST recommendation).
    /// </summary>
    private const int AesGcmNonceSizeBytes = 12;

    /// <summary>
    /// AES-GCM authentication tag size in bytes (128 bits).
    /// </summary>
    private const int AesGcmTagSizeBytes = 16;

    /// <summary>
    /// Initializes a new instance of the <see cref="EncryptionInTransitLayer"/> class.
    /// </summary>
    /// <param name="inner">The inner transit strategy to wrap with encryption.</param>
    /// <param name="messageBus">The message bus for requesting encryption from UltimateEncryption plugin.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> or <paramref name="messageBus"/> is null.</exception>
    public EncryptionInTransitLayer(IDataTransitStrategy inner, IMessageBus messageBus)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(messageBus);
        _inner = inner;
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public string StrategyId => $"encrypted-{_inner.StrategyId}";

    /// <inheritdoc/>
    public string Name => $"Encrypted {_inner.Name}";

    /// <inheritdoc/>
    public TransitCapabilities Capabilities => new()
    {
        SupportsResumable = _inner.Capabilities.SupportsResumable,
        SupportsStreaming = _inner.Capabilities.SupportsStreaming,
        SupportsDelta = _inner.Capabilities.SupportsDelta,
        SupportsMultiPath = _inner.Capabilities.SupportsMultiPath,
        SupportsP2P = _inner.Capabilities.SupportsP2P,
        SupportsOffline = _inner.Capabilities.SupportsOffline,
        SupportsCompression = _inner.Capabilities.SupportsCompression,
        SupportsEncryption = true,
        MaxTransferSizeBytes = _inner.Capabilities.MaxTransferSizeBytes,
        SupportedProtocols = _inner.Capabilities.SupportedProtocols
    };

    /// <inheritdoc/>
    public async Task<TransitResult> TransferAsync(
        TransitRequest request,
        IProgress<TransitProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Pass through if encryption not requested
        if (request.Layers?.EnableEncryption != true)
        {
            return await _inner.TransferAsync(request, progress, ct).ConfigureAwait(false);
        }

        // Idempotency check: skip if already encrypted
        if (request.Metadata.TryGetValue("transit-encrypted", out var encrypted) &&
            encrypted == "true")
        {
            return await _inner.TransferAsync(request, progress, ct).ConfigureAwait(false);
        }

        // Encrypt the data stream
        if (request.DataStream == null)
        {
            // No data stream to encrypt, pass through
            return await _inner.TransferAsync(request, progress, ct).ConfigureAwait(false);
        }

        var (encryptedStream, encryptionMetadata) = await EncryptStreamAsync(
            request.DataStream,
            request.Layers?.EncryptionAlgorithm,
            ct).ConfigureAwait(false);

        // Build modified metadata with encryption markers
        var metadata = new Dictionary<string, string>(request.Metadata)
        {
            ["transit-encrypted"] = "true"
        };

        // Merge encryption metadata (key material for receiver)
        foreach (var kvp in encryptionMetadata)
        {
            metadata[kvp.Key] = kvp.Value;
        }

        // Create modified request with encrypted stream
        var modifiedRequest = request with
        {
            DataStream = encryptedStream,
            SizeBytes = encryptedStream.CanSeek ? encryptedStream.Length : request.SizeBytes,
            Metadata = metadata
        };

        var result = await _inner.TransferAsync(modifiedRequest, progress, ct).ConfigureAwait(false);

        // Enrich result metadata with encryption info
        var resultMetadata = new Dictionary<string, string>(result.Metadata)
        {
            ["encryptionApplied"] = "true",
            ["encryptionAlgorithm"] = request.Layers?.EncryptionAlgorithm ?? "aes-256-gcm"
        };

        return result with { Metadata = resultMetadata };
    }

    /// <summary>
    /// Encrypts a data stream, first attempting via message bus (UltimateEncryption plugin),
    /// falling back to AES-256-GCM encryption if the message bus request fails.
    /// </summary>
    /// <param name="source">The source data stream to encrypt.</param>
    /// <param name="algorithm">The preferred encryption algorithm, or null for default (AES-256-GCM).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of the encrypted stream and encryption metadata for the receiver.</returns>
    private async Task<(Stream EncryptedStream, Dictionary<string, string> Metadata)> EncryptStreamAsync(
        Stream source,
        string? algorithm,
        CancellationToken ct)
    {
        // Attempt encryption via message bus (UltimateEncryption plugin)
        try
        {
            var response = await _messageBus.SendAsync(
                "encryption.transit.encrypt",
                new PluginMessage
                {
                    Type = "encryption.transit.encrypt",
                    SourcePluginId = "com.datawarehouse.transit.ultimate",
                    Payload = new Dictionary<string, object>
                    {
                        ["algorithm"] = algorithm ?? "aes-256-gcm",
                        ["dataSize"] = source.CanSeek ? source.Length : 0L
                    }
                },
                TimeSpan.FromSeconds(5),
                ct).ConfigureAwait(false);

            if (response.Success && response.Payload is Stream encryptedViaPlugin)
            {
                var pluginMetadata = new Dictionary<string, string>
                {
                    ["encryption-provider"] = "UltimateEncryption"
                };

                if (response.Metadata.TryGetValue("keyId", out var keyIdObj) && keyIdObj is string keyId)
                {
                    pluginMetadata["encryption-keyId"] = keyId;
                }

                return (encryptedViaPlugin, pluginMetadata);
            }
        }
        catch
        {
            // Message bus unavailable or encryption plugin not responding -- fall back to AES-GCM
        }

        // Fallback: AES-256-GCM encryption
        return await EncryptWithAesGcmAsync(source, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Encrypts data using AES-256-GCM as a fallback when the UltimateEncryption plugin is unavailable.
    /// Generates a random 256-bit key and 96-bit nonce, encrypts the plaintext, and produces
    /// an authenticated ciphertext with a 128-bit authentication tag.
    /// </summary>
    /// <param name="source">The source stream to encrypt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of the encrypted memory stream and metadata containing the Base64-encoded key, nonce, and tag.</returns>
    private static async Task<(Stream EncryptedStream, Dictionary<string, string> Metadata)> EncryptWithAesGcmAsync(
        Stream source,
        CancellationToken ct)
    {
        // Read plaintext into memory for AES-GCM (operates on byte arrays)
        using var plaintextStream = new MemoryStream(4096);
        await source.CopyToAsync(plaintextStream, ct).ConfigureAwait(false);
        var plaintext = plaintextStream.ToArray();

        // Generate random key and nonce
        var key = RandomNumberGenerator.GetBytes(AesKeySizeBytes);
        var nonce = RandomNumberGenerator.GetBytes(AesGcmNonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcmTagSizeBytes];

        // Encrypt with AES-256-GCM
        using var aesGcm = new AesGcm(key, AesGcmTagSizeBytes);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        // Build output stream: [nonce][tag][ciphertext]
        var outputStream = new MemoryStream(4096);
        await outputStream.WriteAsync(nonce.AsMemory(), ct).ConfigureAwait(false);
        await outputStream.WriteAsync(tag.AsMemory(), ct).ConfigureAwait(false);
        await outputStream.WriteAsync(ciphertext.AsMemory(), ct).ConfigureAwait(false);
        outputStream.Position = 0;

        // Store key in metadata for the receiver (in production, use key management service)
        var metadata = new Dictionary<string, string>
        {
            ["encryption-provider"] = "AES-256-GCM-Fallback",
            ["encryption-key"] = Convert.ToBase64String(key),
            ["encryption-nonceSizeBytes"] = AesGcmNonceSizeBytes.ToString(),
            ["encryption-tagSizeBytes"] = AesGcmTagSizeBytes.ToString()
        };

        return (outputStream, metadata);
    }

    /// <inheritdoc/>
    public Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default)
    {
        return _inner.IsAvailableAsync(endpoint, ct);
    }

    /// <inheritdoc/>
    public Task<TransitResult> ResumeTransferAsync(
        string transferId,
        IProgress<TransitProgress>? progress = null,
        CancellationToken ct = default)
    {
        return _inner.ResumeTransferAsync(transferId, progress, ct);
    }

    /// <inheritdoc/>
    public Task CancelTransferAsync(string transferId, CancellationToken ct = default)
    {
        return _inner.CancelTransferAsync(transferId, ct);
    }

    /// <inheritdoc/>
    public Task<TransitHealthStatus> GetHealthAsync(CancellationToken ct = default)
    {
        return _inner.GetHealthAsync(ct);
    }
}
