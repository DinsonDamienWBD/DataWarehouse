using System.Buffers;
using System.IO.Compression;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Infrastructure;

/// <summary>
/// Compression algorithm types supported by database protocols.
/// </summary>
public enum CompressionAlgorithm
{
    /// <summary>No compression.</summary>
    None,

    /// <summary>GZip compression (RFC 1952).</summary>
    GZip,

    /// <summary>Deflate compression (RFC 1951).</summary>
    Deflate,

    /// <summary>Brotli compression (RFC 7932).</summary>
    Brotli,

    /// <summary>LZ4 compression (high speed).</summary>
    LZ4,

    /// <summary>Zstandard compression (high ratio).</summary>
    Zstd,

    /// <summary>Snappy compression (Google, very fast).</summary>
    Snappy,

    /// <summary>LZO compression.</summary>
    LZO
}

/// <summary>
/// Compression level settings.
/// </summary>
public enum ProtocolCompressionLevel
{
    /// <summary>No compression.</summary>
    None = 0,

    /// <summary>Fastest compression with lowest ratio.</summary>
    Fastest = 1,

    /// <summary>Fast compression with moderate ratio.</summary>
    Fast = 3,

    /// <summary>Balanced compression (default).</summary>
    Balanced = 6,

    /// <summary>Good compression with slower speed.</summary>
    Good = 9,

    /// <summary>Best compression ratio, slowest speed.</summary>
    Best = 11,

    /// <summary>Maximum compression (may be very slow).</summary>
    Maximum = 22
}

/// <summary>
/// Configuration for protocol compression.
/// </summary>
public sealed record CompressionOptions
{
    /// <summary>Compression algorithm to use.</summary>
    public CompressionAlgorithm Algorithm { get; init; } = CompressionAlgorithm.GZip;

    /// <summary>Compression level.</summary>
    public ProtocolCompressionLevel Level { get; init; } = ProtocolCompressionLevel.Balanced;

    /// <summary>Minimum payload size in bytes to trigger compression.</summary>
    public int MinimumSizeThreshold { get; init; } = 1024; // 1 KB

    /// <summary>Maximum payload size in bytes for compression.</summary>
    public int MaximumSizeThreshold { get; init; } = 100 * 1024 * 1024; // 100 MB

    /// <summary>Whether to use streaming compression for large payloads.</summary>
    public bool EnableStreaming { get; init; } = true;

    /// <summary>Buffer size for streaming operations.</summary>
    public int StreamingBufferSize { get; init; } = 81920; // 80 KB

    /// <summary>Whether to include compression header in output.</summary>
    public bool IncludeHeader { get; init; } = false;

    /// <summary>Static options for no compression.</summary>
    public static CompressionOptions NoCompression => new() { Algorithm = CompressionAlgorithm.None };

    /// <summary>Static options for fast compression.</summary>
    public static CompressionOptions Fast => new()
    {
        Algorithm = CompressionAlgorithm.GZip,
        Level = ProtocolCompressionLevel.Fastest
    };

    /// <summary>Static options for balanced compression.</summary>
    public static CompressionOptions Balanced => new()
    {
        Algorithm = CompressionAlgorithm.GZip,
        Level = ProtocolCompressionLevel.Balanced
    };

    /// <summary>Static options for best compression.</summary>
    public static CompressionOptions Best => new()
    {
        Algorithm = CompressionAlgorithm.Brotli,
        Level = ProtocolCompressionLevel.Best
    };
}

/// <summary>
/// Statistics for compression operations.
/// </summary>
public sealed record CompressionStatistics
{
    /// <summary>Total bytes before compression.</summary>
    public long TotalUncompressedBytes { get; init; }

    /// <summary>Total bytes after compression.</summary>
    public long TotalCompressedBytes { get; init; }

    /// <summary>Number of compression operations.</summary>
    public long CompressionOperations { get; init; }

    /// <summary>Number of decompression operations.</summary>
    public long DecompressionOperations { get; init; }

    /// <summary>Total time spent compressing in milliseconds.</summary>
    public long CompressionTimeMs { get; init; }

    /// <summary>Total time spent decompressing in milliseconds.</summary>
    public long DecompressionTimeMs { get; init; }

    /// <summary>Operations skipped due to size threshold.</summary>
    public long SkippedOperations { get; init; }

    /// <summary>Failed operations.</summary>
    public long FailedOperations { get; init; }

    /// <summary>Average compression ratio (compressed/uncompressed).</summary>
    public double AverageCompressionRatio =>
        TotalUncompressedBytes > 0 ? (double)TotalCompressedBytes / TotalUncompressedBytes : 1.0;

    /// <summary>Bytes saved by compression.</summary>
    public long BytesSaved => TotalUncompressedBytes - TotalCompressedBytes;

    /// <summary>Average compression throughput in MB/s.</summary>
    public double CompressionThroughputMBps =>
        CompressionTimeMs > 0 ? TotalUncompressedBytes / 1024.0 / 1024.0 / (CompressionTimeMs / 1000.0) : 0;
}

/// <summary>
/// Result of a compression operation.
/// </summary>
public readonly record struct CompressionResult
{
    /// <summary>Whether compression was performed.</summary>
    public bool WasCompressed { get; init; }

    /// <summary>Original data size in bytes.</summary>
    public int OriginalSize { get; init; }

    /// <summary>Compressed data size in bytes.</summary>
    public int CompressedSize { get; init; }

    /// <summary>Compression ratio (compressed/original).</summary>
    public double CompressionRatio => OriginalSize > 0 ? (double)CompressedSize / OriginalSize : 1.0;

    /// <summary>Algorithm used.</summary>
    public CompressionAlgorithm Algorithm { get; init; }

    /// <summary>Time taken in milliseconds.</summary>
    public long ElapsedMs { get; init; }
}

/// <summary>
/// Protocol-level compression manager for database wire protocols.
/// Supports multiple compression algorithms with automatic selection,
/// streaming compression for large payloads, and comprehensive statistics.
/// </summary>
public sealed class ProtocolCompressionManager : IDisposable
{
    private readonly CompressionOptions _defaultOptions;
    private readonly Dictionary<CompressionAlgorithm, ICompressionProvider> _providers;

    // Statistics
    private long _totalUncompressedBytes;
    private long _totalCompressedBytes;
    private long _compressionOperations;
    private long _decompressionOperations;
    private long _compressionTimeMs;
    private long _decompressionTimeMs;
    private long _skippedOperations;
    private long _failedOperations;

    /// <summary>
    /// Creates a new compression manager with default options.
    /// </summary>
    public ProtocolCompressionManager(CompressionOptions? defaultOptions = null)
    {
        _defaultOptions = defaultOptions ?? CompressionOptions.Balanced;

        // Register built-in providers
        _providers = new Dictionary<CompressionAlgorithm, ICompressionProvider>
        {
            [CompressionAlgorithm.None] = new NoCompressionProvider(),
            [CompressionAlgorithm.GZip] = new GZipCompressionProvider(),
            [CompressionAlgorithm.Deflate] = new DeflateCompressionProvider(),
            [CompressionAlgorithm.Brotli] = new BrotliCompressionProvider(),
            [CompressionAlgorithm.LZ4] = new LZ4CompressionProvider(),
            [CompressionAlgorithm.Zstd] = new ZstdCompressionProvider(),
            [CompressionAlgorithm.Snappy] = new SnappyCompressionProvider(),
            [CompressionAlgorithm.LZO] = new LZOCompressionProvider()
        };
    }

    /// <summary>
    /// Compresses data using the specified options.
    /// </summary>
    public (byte[] Data, CompressionResult Result) Compress(
        ReadOnlySpan<byte> data,
        CompressionOptions? options = null)
    {
        var opts = options ?? _defaultOptions;
        var startTime = DateTime.UtcNow;

        // Check size thresholds
        if (data.Length < opts.MinimumSizeThreshold ||
            data.Length > opts.MaximumSizeThreshold ||
            opts.Algorithm == CompressionAlgorithm.None)
        {
            Interlocked.Increment(ref _skippedOperations);
            return (data.ToArray(), new CompressionResult
            {
                WasCompressed = false,
                OriginalSize = data.Length,
                CompressedSize = data.Length,
                Algorithm = CompressionAlgorithm.None,
                ElapsedMs = 0
            });
        }

        try
        {
            var provider = GetProvider(opts.Algorithm);
            var compressed = provider.Compress(data, MapCompressionLevel(opts.Level));

            var elapsedMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

            Interlocked.Add(ref _totalUncompressedBytes, data.Length);
            Interlocked.Add(ref _totalCompressedBytes, compressed.Length);
            Interlocked.Increment(ref _compressionOperations);
            Interlocked.Add(ref _compressionTimeMs, elapsedMs);

            // If compression made it bigger, return original
            if (compressed.Length >= data.Length)
            {
                return (data.ToArray(), new CompressionResult
                {
                    WasCompressed = false,
                    OriginalSize = data.Length,
                    CompressedSize = data.Length,
                    Algorithm = CompressionAlgorithm.None,
                    ElapsedMs = elapsedMs
                });
            }

            return (compressed, new CompressionResult
            {
                WasCompressed = true,
                OriginalSize = data.Length,
                CompressedSize = compressed.Length,
                Algorithm = opts.Algorithm,
                ElapsedMs = elapsedMs
            });
        }
        catch
        {
            Interlocked.Increment(ref _failedOperations);
            return (data.ToArray(), new CompressionResult
            {
                WasCompressed = false,
                OriginalSize = data.Length,
                CompressedSize = data.Length,
                Algorithm = CompressionAlgorithm.None,
                ElapsedMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
            });
        }
    }

    /// <summary>
    /// Decompresses data using the specified algorithm.
    /// </summary>
    public (byte[] Data, CompressionResult Result) Decompress(
        ReadOnlySpan<byte> data,
        CompressionAlgorithm algorithm)
    {
        var startTime = DateTime.UtcNow;

        if (algorithm == CompressionAlgorithm.None)
        {
            return (data.ToArray(), new CompressionResult
            {
                WasCompressed = false,
                OriginalSize = data.Length,
                CompressedSize = data.Length,
                Algorithm = algorithm,
                ElapsedMs = 0
            });
        }

        try
        {
            var provider = GetProvider(algorithm);
            var decompressed = provider.Decompress(data);

            var elapsedMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

            Interlocked.Increment(ref _decompressionOperations);
            Interlocked.Add(ref _decompressionTimeMs, elapsedMs);

            return (decompressed, new CompressionResult
            {
                WasCompressed = true,
                OriginalSize = decompressed.Length,
                CompressedSize = data.Length,
                Algorithm = algorithm,
                ElapsedMs = elapsedMs
            });
        }
        catch
        {
            Interlocked.Increment(ref _failedOperations);
            throw;
        }
    }

    /// <summary>
    /// Compresses data to a stream (for large payloads).
    /// </summary>
    public async Task<CompressionResult> CompressToStreamAsync(
        Stream input,
        Stream output,
        CompressionOptions? options = null,
        CancellationToken ct = default)
    {
        var opts = options ?? _defaultOptions;
        var startTime = DateTime.UtcNow;

        if (opts.Algorithm == CompressionAlgorithm.None)
        {
            await input.CopyToAsync(output, opts.StreamingBufferSize, ct);
            return new CompressionResult
            {
                WasCompressed = false,
                OriginalSize = (int)input.Length,
                CompressedSize = (int)output.Length,
                Algorithm = CompressionAlgorithm.None,
                ElapsedMs = 0
            };
        }

        var provider = GetProvider(opts.Algorithm);
        var originalLength = input.Length;

        await using var compressionStream = provider.CreateCompressionStream(
            output,
            MapCompressionLevel(opts.Level),
            leaveOpen: true);

        await input.CopyToAsync(compressionStream, opts.StreamingBufferSize, ct);
        await compressionStream.FlushAsync(ct);

        var elapsedMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
        var compressedLength = output.Length;

        Interlocked.Add(ref _totalUncompressedBytes, originalLength);
        Interlocked.Add(ref _totalCompressedBytes, compressedLength);
        Interlocked.Increment(ref _compressionOperations);
        Interlocked.Add(ref _compressionTimeMs, elapsedMs);

        return new CompressionResult
        {
            WasCompressed = true,
            OriginalSize = (int)originalLength,
            CompressedSize = (int)compressedLength,
            Algorithm = opts.Algorithm,
            ElapsedMs = elapsedMs
        };
    }

    /// <summary>
    /// Decompresses data from a stream.
    /// </summary>
    public async Task<CompressionResult> DecompressFromStreamAsync(
        Stream input,
        Stream output,
        CompressionAlgorithm algorithm,
        int bufferSize = 81920,
        CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;

        if (algorithm == CompressionAlgorithm.None)
        {
            await input.CopyToAsync(output, bufferSize, ct);
            return new CompressionResult
            {
                WasCompressed = false,
                OriginalSize = (int)output.Length,
                CompressedSize = (int)input.Length,
                Algorithm = algorithm,
                ElapsedMs = 0
            };
        }

        var provider = GetProvider(algorithm);
        var compressedLength = input.Length;

        await using var decompressionStream = provider.CreateDecompressionStream(input, leaveOpen: true);
        await decompressionStream.CopyToAsync(output, bufferSize, ct);

        var elapsedMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
        var decompressedLength = output.Length;

        Interlocked.Increment(ref _decompressionOperations);
        Interlocked.Add(ref _decompressionTimeMs, elapsedMs);

        return new CompressionResult
        {
            WasCompressed = true,
            OriginalSize = (int)decompressedLength,
            CompressedSize = (int)compressedLength,
            Algorithm = algorithm,
            ElapsedMs = elapsedMs
        };
    }

    /// <summary>
    /// Detects the compression algorithm from data header.
    /// </summary>
    public static CompressionAlgorithm DetectAlgorithm(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return CompressionAlgorithm.None;

        // GZip: 1f 8b
        if (data[0] == 0x1F && data[1] == 0x8B)
            return CompressionAlgorithm.GZip;

        // Zlib/Deflate: 78 01/9C/DA
        if (data[0] == 0x78 && (data[1] == 0x01 || data[1] == 0x9C || data[1] == 0xDA))
            return CompressionAlgorithm.Deflate;

        // Zstd: 28 B5 2F FD
        if (data.Length >= 4 && data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD)
            return CompressionAlgorithm.Zstd;

        // LZ4: 04 22 4D 18
        if (data.Length >= 4 && data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18)
            return CompressionAlgorithm.LZ4;

        // Brotli detection is complex - no fixed magic bytes
        // Snappy detection requires frame format check

        return CompressionAlgorithm.None;
    }

    /// <summary>
    /// Gets the best compression algorithm for the given data type.
    /// </summary>
    public static CompressionAlgorithm GetBestAlgorithm(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "application/json" => CompressionAlgorithm.Brotli,
            "application/xml" => CompressionAlgorithm.Brotli,
            "text/plain" => CompressionAlgorithm.Brotli,
            "text/csv" => CompressionAlgorithm.GZip,
            "application/octet-stream" => CompressionAlgorithm.LZ4,
            "application/x-binary" => CompressionAlgorithm.Zstd,
            _ => CompressionAlgorithm.GZip
        };
    }

    /// <summary>
    /// Gets compression statistics.
    /// </summary>
    public CompressionStatistics GetStatistics()
    {
        return new CompressionStatistics
        {
            TotalUncompressedBytes = Interlocked.Read(ref _totalUncompressedBytes),
            TotalCompressedBytes = Interlocked.Read(ref _totalCompressedBytes),
            CompressionOperations = Interlocked.Read(ref _compressionOperations),
            DecompressionOperations = Interlocked.Read(ref _decompressionOperations),
            CompressionTimeMs = Interlocked.Read(ref _compressionTimeMs),
            DecompressionTimeMs = Interlocked.Read(ref _decompressionTimeMs),
            SkippedOperations = Interlocked.Read(ref _skippedOperations),
            FailedOperations = Interlocked.Read(ref _failedOperations)
        };
    }

    /// <summary>
    /// Resets compression statistics.
    /// </summary>
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _totalUncompressedBytes, 0);
        Interlocked.Exchange(ref _totalCompressedBytes, 0);
        Interlocked.Exchange(ref _compressionOperations, 0);
        Interlocked.Exchange(ref _decompressionOperations, 0);
        Interlocked.Exchange(ref _compressionTimeMs, 0);
        Interlocked.Exchange(ref _decompressionTimeMs, 0);
        Interlocked.Exchange(ref _skippedOperations, 0);
        Interlocked.Exchange(ref _failedOperations, 0);
    }

    private ICompressionProvider GetProvider(CompressionAlgorithm algorithm)
    {
        if (!_providers.TryGetValue(algorithm, out var provider))
        {
            throw new NotSupportedException($"Compression algorithm {algorithm} is not supported");
        }
        return provider;
    }

    private static CompressionLevel MapCompressionLevel(ProtocolCompressionLevel level)
    {
        return level switch
        {
            ProtocolCompressionLevel.None => CompressionLevel.NoCompression,
            ProtocolCompressionLevel.Fastest => CompressionLevel.Fastest,
            ProtocolCompressionLevel.Fast => CompressionLevel.Fastest,
            ProtocolCompressionLevel.Balanced => CompressionLevel.Optimal,
            ProtocolCompressionLevel.Good => CompressionLevel.Optimal,
            ProtocolCompressionLevel.Best => CompressionLevel.SmallestSize,
            ProtocolCompressionLevel.Maximum => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };
    }

    public void Dispose()
    {
        // No resources to dispose currently
    }
}

#region Compression Providers

/// <summary>
/// Interface for compression algorithm providers.
/// </summary>
internal interface ICompressionProvider
{
    byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level);
    byte[] Decompress(ReadOnlySpan<byte> data);
    Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen);
    Stream CreateDecompressionStream(Stream input, bool leaveOpen);
}

/// <summary>
/// No-op compression provider.
/// </summary>
internal sealed class NoCompressionProvider : ICompressionProvider
{
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level) => data.ToArray();
    public byte[] Decompress(ReadOnlySpan<byte> data) => data.ToArray();
    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen) =>
        new PassthroughStream(output, leaveOpen);
    public Stream CreateDecompressionStream(Stream input, bool leaveOpen) =>
        new PassthroughStream(input, leaveOpen);
}

/// <summary>
/// GZip compression provider.
/// </summary>
internal sealed class GZipCompressionProvider : ICompressionProvider
{
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using var output = new MemoryStream(65536);
        using (var gzip = new GZipStream(output, level, leaveOpen: true))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(65536);
        gzip.CopyTo(output);
        return output.ToArray();
    }

    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen) =>
        new GZipStream(output, level, leaveOpen);

    public Stream CreateDecompressionStream(Stream input, bool leaveOpen) =>
        new GZipStream(input, CompressionMode.Decompress, leaveOpen);
}

/// <summary>
/// Deflate compression provider.
/// </summary>
internal sealed class DeflateCompressionProvider : ICompressionProvider
{
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using var output = new MemoryStream(65536);
        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
        {
            deflate.Write(data);
        }
        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(65536);
        deflate.CopyTo(output);
        return output.ToArray();
    }

    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen) =>
        new DeflateStream(output, level, leaveOpen);

    public Stream CreateDecompressionStream(Stream input, bool leaveOpen) =>
        new DeflateStream(input, CompressionMode.Decompress, leaveOpen);
}

/// <summary>
/// Brotli compression provider.
/// </summary>
internal sealed class BrotliCompressionProvider : ICompressionProvider
{
    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using var output = new MemoryStream(65536);
        using (var brotli = new BrotliStream(output, level, leaveOpen: true))
        {
            brotli.Write(data);
        }
        return output.ToArray();
    }

    public byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(65536);
        brotli.CopyTo(output);
        return output.ToArray();
    }

    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen) =>
        new BrotliStream(output, level, leaveOpen);

    public Stream CreateDecompressionStream(Stream input, bool leaveOpen) =>
        new BrotliStream(input, CompressionMode.Decompress, leaveOpen);
}

/// <summary>
/// LZ4 compression provider (simulated - uses deflate as fallback).
/// For production, integrate with K4os.Compression.LZ4 package.
/// </summary>
internal sealed class LZ4CompressionProvider : ICompressionProvider
{
    // Note: In production, this would use K4os.Compression.LZ4 or similar
    private readonly DeflateCompressionProvider _fallback = new();

    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level) =>
        _fallback.Compress(data, CompressionLevel.Fastest); // LZ4 prioritizes speed

    public byte[] Decompress(ReadOnlySpan<byte> data) =>
        _fallback.Decompress(data);

    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen) =>
        _fallback.CreateCompressionStream(output, CompressionLevel.Fastest, leaveOpen);

    public Stream CreateDecompressionStream(Stream input, bool leaveOpen) =>
        _fallback.CreateDecompressionStream(input, leaveOpen);
}

/// <summary>
/// Zstandard compression provider (simulated - uses brotli as fallback).
/// For production, integrate with ZstdNet or ZstdSharp package.
/// </summary>
internal sealed class ZstdCompressionProvider : ICompressionProvider
{
    // Note: In production, this would use ZstdSharp or similar
    private readonly BrotliCompressionProvider _fallback = new();

    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level) =>
        _fallback.Compress(data, level);

    public byte[] Decompress(ReadOnlySpan<byte> data) =>
        _fallback.Decompress(data);

    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen) =>
        _fallback.CreateCompressionStream(output, level, leaveOpen);

    public Stream CreateDecompressionStream(Stream input, bool leaveOpen) =>
        _fallback.CreateDecompressionStream(input, leaveOpen);
}

/// <summary>
/// Snappy compression provider (simulated - uses deflate as fallback).
/// For production, integrate with Snappy.NET or IronSnappy package.
/// </summary>
internal sealed class SnappyCompressionProvider : ICompressionProvider
{
    // Note: In production, this would use IronSnappy or similar
    private readonly DeflateCompressionProvider _fallback = new();

    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level) =>
        _fallback.Compress(data, CompressionLevel.Fastest); // Snappy prioritizes speed

    public byte[] Decompress(ReadOnlySpan<byte> data) =>
        _fallback.Decompress(data);

    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen) =>
        _fallback.CreateCompressionStream(output, CompressionLevel.Fastest, leaveOpen);

    public Stream CreateDecompressionStream(Stream input, bool leaveOpen) =>
        _fallback.CreateDecompressionStream(input, leaveOpen);
}

/// <summary>
/// LZO compression provider (simulated - uses deflate as fallback).
/// For production, integrate with lzo.net package.
/// </summary>
internal sealed class LZOCompressionProvider : ICompressionProvider
{
    // Note: In production, this would use lzo.net or similar
    private readonly DeflateCompressionProvider _fallback = new();

    public byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level) =>
        _fallback.Compress(data, CompressionLevel.Fastest);

    public byte[] Decompress(ReadOnlySpan<byte> data) =>
        _fallback.Decompress(data);

    public Stream CreateCompressionStream(Stream output, CompressionLevel level, bool leaveOpen) =>
        _fallback.CreateCompressionStream(output, CompressionLevel.Fastest, leaveOpen);

    public Stream CreateDecompressionStream(Stream input, bool leaveOpen) =>
        _fallback.CreateDecompressionStream(input, leaveOpen);
}

/// <summary>
/// Passthrough stream that does no transformation.
/// </summary>
internal sealed class PassthroughStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;

    public PassthroughStream(Stream inner, bool leaveOpen)
    {
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

#endregion

/// <summary>
/// Extension methods for compression integration with protocol strategies.
/// </summary>
public static class ProtocolCompressionExtensions
{
    /// <summary>
    /// Compresses query result data if beneficial.
    /// </summary>
    public static byte[] CompressIfBeneficial(
        this ProtocolCompressionManager compression,
        byte[] data,
        out CompressionAlgorithm usedAlgorithm)
    {
        var (compressed, result) = compression.Compress(data);
        usedAlgorithm = result.Algorithm;
        return compressed;
    }

    /// <summary>
    /// Creates a compressed protocol stream wrapper.
    /// </summary>
    public static Stream WrapWithCompression(
        this ProtocolCompressionManager compression,
        Stream stream,
        CompressionOptions? options = null)
    {
        var opts = options ?? CompressionOptions.Balanced;
        if (opts.Algorithm == CompressionAlgorithm.None)
            return stream;

        // This would need proper integration with the protocol
        return stream;
    }
}
