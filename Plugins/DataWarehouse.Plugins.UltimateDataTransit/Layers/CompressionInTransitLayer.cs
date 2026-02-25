using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataTransit.Layers;

/// <summary>
/// Decorator layer that adds compression to any <see cref="IDataTransitStrategy"/>.
/// Wraps an inner strategy, compressing outbound data before passing to the inner
/// strategy for transfer. Delegates compression to the UltimateCompression plugin
/// via message bus when available, falling back to GZip compression.
/// </summary>
/// <remarks>
/// <para>
/// This decorator follows the composable layer pattern (research Pattern 2).
/// The orchestrator applies layers in correct order: compression first (inner),
/// encryption second (outer), ensuring data is compressed before encryption
/// and decrypted before decompression on the receiving end.
/// </para>
/// <para>
/// Idempotency: If data is already marked as compressed via the "transit-compressed"
/// metadata marker, this layer passes through without re-compressing.
/// </para>
/// </remarks>
internal sealed class CompressionInTransitLayer : IDataTransitStrategy
{
    private readonly IDataTransitStrategy _inner;
    private readonly IMessageBus _messageBus;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompressionInTransitLayer"/> class.
    /// </summary>
    /// <param name="inner">The inner transit strategy to wrap with compression.</param>
    /// <param name="messageBus">The message bus for requesting compression from UltimateCompression plugin.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> or <paramref name="messageBus"/> is null.</exception>
    public CompressionInTransitLayer(IDataTransitStrategy inner, IMessageBus messageBus)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(messageBus);
        _inner = inner;
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public string StrategyId => $"compressed-{_inner.StrategyId}";

    /// <inheritdoc/>
    public string Name => $"Compressed {_inner.Name}";

    /// <inheritdoc/>
    public TransitCapabilities Capabilities => new()
    {
        SupportsResumable = _inner.Capabilities.SupportsResumable,
        SupportsStreaming = _inner.Capabilities.SupportsStreaming,
        SupportsDelta = _inner.Capabilities.SupportsDelta,
        SupportsMultiPath = _inner.Capabilities.SupportsMultiPath,
        SupportsP2P = _inner.Capabilities.SupportsP2P,
        SupportsOffline = _inner.Capabilities.SupportsOffline,
        SupportsCompression = true,
        SupportsEncryption = _inner.Capabilities.SupportsEncryption,
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

        // Pass through if compression not requested
        if (request.Layers?.EnableCompression != true)
        {
            return await _inner.TransferAsync(request, progress, ct).ConfigureAwait(false);
        }

        // Idempotency check: skip if already compressed
        if (request.Metadata.TryGetValue("transit-compressed", out var compressed) &&
            compressed == "true")
        {
            return await _inner.TransferAsync(request, progress, ct).ConfigureAwait(false);
        }

        // Compress the data stream
        long originalSize = request.SizeBytes > 0 ? request.SizeBytes : 0;
        Stream compressedStream;

        if (request.DataStream != null)
        {
            compressedStream = await CompressStreamAsync(
                request.DataStream,
                request.Layers?.CompressionAlgorithm,
                ct).ConfigureAwait(false);
        }
        else
        {
            // No data stream to compress, pass through
            return await _inner.TransferAsync(request, progress, ct).ConfigureAwait(false);
        }

        // Build modified metadata with compression markers
        var metadata = new Dictionary<string, string>(request.Metadata)
        {
            ["transit-compressed"] = "true",
            ["compression-originalSize"] = originalSize.ToString()
        };

        // Create modified request with compressed stream
        var modifiedRequest = request with
        {
            DataStream = compressedStream,
            SizeBytes = compressedStream.CanSeek ? compressedStream.Length : request.SizeBytes,
            Metadata = metadata
        };

        var result = await _inner.TransferAsync(modifiedRequest, progress, ct).ConfigureAwait(false);

        // Enrich result metadata with compression info
        var resultMetadata = new Dictionary<string, string>(result.Metadata)
        {
            ["compressionApplied"] = "true",
            ["originalSizeBytes"] = originalSize.ToString()
        };

        if (originalSize > 0 && result.BytesTransferred > 0)
        {
            var ratio = (double)result.BytesTransferred / originalSize;
            resultMetadata["compressionRatio"] = ratio.ToString("F4");
        }

        return result with { Metadata = resultMetadata };
    }

    /// <summary>
    /// Compresses a data stream, first attempting via message bus (UltimateCompression plugin),
    /// falling back to GZip compression if the message bus request fails.
    /// </summary>
    /// <param name="source">The source data stream to compress.</param>
    /// <param name="algorithm">The preferred compression algorithm, or null for default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A compressed data stream.</returns>
    private async Task<Stream> CompressStreamAsync(Stream source, string? algorithm, CancellationToken ct)
    {
        // Attempt compression via message bus (UltimateCompression plugin)
        try
        {
            var response = await _messageBus.SendAsync(
                "compression.compress",
                new PluginMessage
                {
                    Type = "compression.compress",
                    SourcePluginId = "com.datawarehouse.transit.ultimate",
                    Payload = new Dictionary<string, object>
                    {
                        ["algorithm"] = algorithm ?? "gzip",
                        ["dataSize"] = source.Length > 0 ? source.Length : 0L
                    }
                },
                TimeSpan.FromSeconds(5),
                ct).ConfigureAwait(false);

            if (response.Success && response.Payload is Stream compressedViaPlugin)
            {
                return compressedViaPlugin;
            }
        }
        catch
        {

            // Message bus unavailable or compression plugin not responding -- fall back to GZip
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        // Fallback: GZip compression using System.IO.Compression
        return await CompressWithGZipAsync(source, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Compresses data using GZip as a fallback when the UltimateCompression plugin is unavailable.
    /// </summary>
    /// <param name="source">The source stream to compress.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A memory stream containing the GZip-compressed data.</returns>
    private static async Task<Stream> CompressWithGZipAsync(Stream source, CancellationToken ct)
    {
        var outputStream = new MemoryStream(4096);

        using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            await source.CopyToAsync(gzipStream, ct).ConfigureAwait(false);
        }

        outputStream.Position = 0;
        return outputStream;
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
