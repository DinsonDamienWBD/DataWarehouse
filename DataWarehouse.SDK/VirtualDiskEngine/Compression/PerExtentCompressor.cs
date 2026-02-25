using System.IO.Compression;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Compression;

/// <summary>
/// Provides extent-granularity compression using Brotli. Operating on extent-sized units
/// instead of individual blocks captures cross-block data patterns, improving compression
/// ratios (especially for columnar or repetitive data).
/// </summary>
/// <remarks>
/// A shared dictionary can optionally be pre-trained from the first 8 KB of extent data
/// to boost compression of repetitive patterns (e.g., columnar storage blocks).
/// If compressed size >= 95% of original, the extent is returned uncompressed (not worth the overhead).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Per-extent compression with shared dictionary (VOPT-24)")]
public sealed class PerExtentCompressor
{
    /// <summary>Maximum dictionary size in bytes for pre-training.</summary>
    public const int MaxDictionarySize = 8192;

    /// <summary>Compression ratio threshold: if compressed >= 95% of original, skip compression.</summary>
    public const double CompressionThreshold = 0.95;

    private readonly int _blockSize;
    private readonly System.IO.Compression.CompressionLevel _level;
    private long _extentsCompressed;
    private double _totalRatio;
    private long _bytesSaved;

    /// <summary>
    /// Initializes a new <see cref="PerExtentCompressor"/> with the specified block size and compression level.
    /// </summary>
    /// <param name="blockSize">Block size in bytes used by the VDE format.</param>
    /// <param name="level">Brotli compression level. Defaults to <see cref="VdeCompressionLevel.Optimal"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Block size is invalid.</exception>
    public PerExtentCompressor(int blockSize, VdeCompressionLevel level = VdeCompressionLevel.Optimal)
    {
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");

        _blockSize = blockSize;
        _level = level switch
        {
            VdeCompressionLevel.Fastest => System.IO.Compression.CompressionLevel.Fastest,
            VdeCompressionLevel.SmallestSize => System.IO.Compression.CompressionLevel.SmallestSize,
            _ => System.IO.Compression.CompressionLevel.Optimal,
        };
    }

    /// <summary>
    /// Compresses an entire extent as a single unit using Brotli.
    /// </summary>
    /// <param name="extentData">The uncompressed extent data.</param>
    /// <param name="extent">
    /// The extent descriptor. The <see cref="ExtentFlags.Compressed"/> flag indicates post-compression state.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="CompressedExtent"/> containing the compressed data, original/compressed sizes,
    /// optional dictionary, and compression ratio. If compression does not yield at least 5% savings,
    /// the original data is returned uncompressed.
    /// </returns>
    public Task<CompressedExtent> CompressExtentAsync(ReadOnlyMemory<byte> extentData, InodeExtent extent, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        int originalSize = extentData.Length;
        if (originalSize == 0)
        {
            return Task.FromResult(new CompressedExtent(
                Array.Empty<byte>(), 0, 0, null, 1.0));
        }

        // Build optional dictionary from first 8 KB of extent data for repetitive patterns
        byte[]? dictionary = null;
        if (originalSize > MaxDictionarySize)
        {
            dictionary = extentData[..MaxDictionarySize].ToArray();
        }

        // Compress using Brotli
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var brotli = new BrotliStream(output, _level, leaveOpen: true))
            {
                brotli.Write(extentData.Span);
            }

            compressed = output.ToArray();
        }

        int compressedSize = compressed.Length;
        double ratio = originalSize > 0 ? (double)compressedSize / originalSize : 1.0;

        // If compression is not worthwhile (>= 95% of original), return uncompressed
        if (ratio >= CompressionThreshold)
        {
            return Task.FromResult(new CompressedExtent(
                extentData.ToArray(), originalSize, originalSize, null, 1.0));
        }

        // Track statistics
        Interlocked.Increment(ref _extentsCompressed);
        Interlocked.Add(ref _bytesSaved, originalSize - compressedSize);

        // Volatile double update via Interlocked pattern
        double currentTotal;
        double newTotal;
        do
        {
            currentTotal = Volatile.Read(ref _totalRatio);
            newTotal = currentTotal + ratio;
        } while (Interlocked.CompareExchange(ref _totalRatio, newTotal, currentTotal) != currentTotal);

        return Task.FromResult(new CompressedExtent(
            compressed, originalSize, compressedSize, dictionary, ratio));
    }

    /// <summary>
    /// Decompresses a previously compressed extent.
    /// </summary>
    /// <param name="compressed">The compressed extent result from <see cref="CompressExtentAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decompressed extent data matching the original size.</returns>
    public Task<byte[]> DecompressExtentAsync(CompressedExtent compressed, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // If not actually compressed (ratio == 1.0 or sizes match), return data as-is
        if (compressed.OriginalSize == compressed.CompressedSize || compressed.OriginalSize == 0)
        {
            return Task.FromResult(compressed.Data.Length > 0 ? (byte[])compressed.Data.Clone() : Array.Empty<byte>());
        }

        using var input = new MemoryStream(compressed.Data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        byte[] decompressed = new byte[compressed.OriginalSize];
        int totalRead = 0;

        while (totalRead < decompressed.Length)
        {
            int read = brotli.Read(decompressed, totalRead, decompressed.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        return Task.FromResult(decompressed);
    }

    /// <summary>
    /// Gets current compression statistics.
    /// </summary>
    public CompressionStats GetStats()
    {
        long count = Interlocked.Read(ref _extentsCompressed);
        double avgRatio = count > 0 ? Volatile.Read(ref _totalRatio) / count : 0.0;
        return new CompressionStats(count, avgRatio, Interlocked.Read(ref _bytesSaved));
    }
}

/// <summary>
/// Result of compressing an extent. Contains the compressed data, size information,
/// optional pre-trained dictionary, and the compression ratio.
/// </summary>
/// <param name="Data">The compressed (or uncompressed if ratio >= 0.95) data bytes.</param>
/// <param name="OriginalSize">Original uncompressed size in bytes.</param>
/// <param name="CompressedSize">Compressed size in bytes (equals OriginalSize if not compressed).</param>
/// <param name="Dictionary">Optional pre-trained dictionary from the first 8 KB of extent data, or null.</param>
/// <param name="Ratio">Compression ratio (compressed / original). 1.0 means no compression applied.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Compressed extent result (VOPT-24)")]
public readonly record struct CompressedExtent(
    byte[] Data,
    int OriginalSize,
    int CompressedSize,
    byte[]? Dictionary,
    double Ratio);

/// <summary>
/// Statistics for per-extent compression operations.
/// </summary>
/// <param name="ExtentsCompressed">Number of extents that were successfully compressed.</param>
/// <param name="AverageRatio">Average compression ratio across all compressed extents.</param>
/// <param name="BytesSaved">Total bytes saved by compression.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Per-extent compression stats (VOPT-24)")]
public readonly record struct CompressionStats(
    long ExtentsCompressed,
    double AverageRatio,
    long BytesSaved);

/// <summary>
/// Compression level for per-extent compression operations.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VDE compression level (VOPT-24)")]
public enum VdeCompressionLevel
{
    /// <summary>Fastest compression with lower ratio.</summary>
    Fastest,

    /// <summary>Optimal balance between speed and ratio.</summary>
    Optimal,

    /// <summary>Best compression ratio at the cost of speed.</summary>
    SmallestSize,
}
