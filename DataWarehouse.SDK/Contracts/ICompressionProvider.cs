namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Supported compression algorithms in the DataWarehouse system.
/// </summary>
public enum CompressionAlgorithm
{
    /// <summary>No compression.</summary>
    None = 0,
    /// <summary>GZip compression (RFC 1952).</summary>
    GZip = 1,
    /// <summary>Deflate compression (RFC 1951).</summary>
    Deflate = 2,
    /// <summary>Brotli compression (RFC 7932).</summary>
    Brotli = 3,
    /// <summary>LZ4 high-speed compression.</summary>
    LZ4 = 4,
    /// <summary>Zstandard (Zstd) compression.</summary>
    Zstd = 5
}

/// <summary>
/// Common interface for all compression providers.
/// Enables Kernel and SDK to use compression without direct implementation dependencies.
/// </summary>
public interface ICompressionProvider
{
    /// <summary>Gets the compression algorithm type.</summary>
    CompressionAlgorithm Algorithm { get; }

    /// <summary>Gets the human-readable name of the compression provider.</summary>
    string Name { get; }

    /// <summary>Compresses data synchronously.</summary>
    byte[] Compress(byte[] data);

    /// <summary>Decompresses data synchronously.</summary>
    byte[] Decompress(byte[] data);

    /// <summary>Compresses data asynchronously.</summary>
    Task<byte[]> CompressAsync(byte[] data, CancellationToken ct = default);

    /// <summary>Decompresses data asynchronously.</summary>
    Task<byte[]> DecompressAsync(byte[] data, CancellationToken ct = default);

    /// <summary>Compresses a stream, returning a new stream with compressed data.</summary>
    Stream CompressStream(Stream input);

    /// <summary>Decompresses a stream, returning a new stream with decompressed data.</summary>
    Stream DecompressStream(Stream input);
}

/// <summary>
/// Configuration options for compression providers.
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>Compression level (Fastest, Optimal, SmallestSize).</summary>
    public System.IO.Compression.CompressionLevel Level { get; set; } = System.IO.Compression.CompressionLevel.Optimal;

    /// <summary>Buffer size for stream operations.</summary>
    public int BufferSize { get; set; } = 81920;

    /// <summary>For LZ4: use high compression mode.</summary>
    public bool LZ4HighCompression { get; set; } = false;

    /// <summary>For LZ4: block size.</summary>
    public int LZ4BlockSize { get; set; } = 64 * 1024;

    /// <summary>For Zstd: compression level (1-22).</summary>
    public int ZstdLevel { get; set; } = 3;
}

/// <summary>
/// Interface for compression provider registry.
/// Enables Kernel discovery and selection of compression algorithms.
/// </summary>
public interface ICompressionRegistry
{
    /// <summary>
    /// Registers a compression provider factory.
    /// </summary>
    void RegisterProvider(CompressionAlgorithm algorithm, Func<CompressionOptions?, ICompressionProvider> factory);

    /// <summary>
    /// Gets a compression provider for the specified algorithm.
    /// </summary>
    ICompressionProvider GetProvider(CompressionAlgorithm algorithm, CompressionOptions? options = null);

    /// <summary>
    /// Gets a compression provider by name (case-insensitive).
    /// </summary>
    ICompressionProvider GetProviderByName(string algorithmName, CompressionOptions? options = null);

    /// <summary>
    /// Gets all available compression algorithms.
    /// </summary>
    IReadOnlyList<CompressionAlgorithm> GetAvailableAlgorithms();

    /// <summary>
    /// Checks if an algorithm is available.
    /// </summary>
    bool IsAlgorithmAvailable(CompressionAlgorithm algorithm);
}
