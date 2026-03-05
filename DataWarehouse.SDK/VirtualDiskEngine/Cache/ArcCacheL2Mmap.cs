using DataWarehouse.SDK.Contracts;
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Cache;

/// <summary>
/// L2 memory-mapped cache for large VDEs. Uses <see cref="MemoryMappedFile"/> for
/// zero-copy read access, leveraging the OS page cache for eviction management.
/// Activated automatically when VDE size exceeds available RAM.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: L2 mmap cache for zero-copy reads (VOPT-04)")]
public sealed class ArcCacheL2Mmap : IArcCache, IDisposable
{
    private readonly string _vdeFilePath;
    private readonly long _regionStartOffset;
    private readonly long _regionLength;
    private readonly int _blockSize;
    private readonly long _blockCount;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private readonly object _initLock = new();
    private volatile bool _initialized;
    private volatile bool _disposed;

    // Statistics
    private long _hits;
    private long _misses;

    /// <summary>
    /// Gets or sets whether memory-mapped access is enabled for this region.
    /// </summary>
    public bool MmapEnabled { get; set; } = true;

    /// <summary>
    /// Creates a new L2 memory-mapped cache for a VDE file region.
    /// </summary>
    /// <param name="vdeFilePath">Path to the VDE file.</param>
    /// <param name="regionStartOffset">Byte offset where the cached region starts.</param>
    /// <param name="regionLength">Length of the cached region in bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public ArcCacheL2Mmap(string vdeFilePath, long regionStartOffset, long regionLength, int blockSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(vdeFilePath);
        ArgumentOutOfRangeException.ThrowIfNegative(regionStartOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(regionLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);

        _vdeFilePath = vdeFilePath;
        _regionStartOffset = regionStartOffset;
        _regionLength = regionLength;
        _blockSize = blockSize;
        _blockCount = regionLength / blockSize;
    }

    /// <inheritdoc />
    public long Capacity => _blockCount;

    /// <inheritdoc />
    public long Count => _blockCount; // All blocks in the region are accessible

    /// <inheritdoc />
    public ValueTask<byte[]?> GetAsync(long blockNumber, CancellationToken ct = default)
    {
        if (!MmapEnabled || blockNumber < 0 || blockNumber >= _blockCount)
        {
            Interlocked.Increment(ref _misses);
            return new ValueTask<byte[]?>((byte[]?)null);
        }

        EnsureInitialized();

        if (_accessor == null)
        {
            Interlocked.Increment(ref _misses);
            return new ValueTask<byte[]?>((byte[]?)null);
        }

        try
        {
            long offset = blockNumber * _blockSize;
            byte[] buffer = new byte[_blockSize];
            _accessor.ReadArray(offset, buffer, 0, _blockSize);

            Interlocked.Increment(ref _hits);
            return new ValueTask<byte[]?>(buffer);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _misses);
            return new ValueTask<byte[]?>((byte[]?)null);
        }
    }

    /// <inheritdoc />
    /// <remarks>No-op for L2: OS page cache manages eviction.</remarks>
    public ValueTask PutAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        // No-op: OS page cache manages L2 eviction
        return default;
    }

    /// <inheritdoc />
    /// <remarks>No-op for L2: OS page cache manages eviction.</remarks>
    public void Evict(long blockNumber)
    {
        // No-op: OS manages
    }

    /// <inheritdoc />
    public void Clear()
    {
        // Closing and re-opening would flush the mmap; for L2 this is a no-op
    }

    /// <inheritdoc />
    public ArcCacheStats GetStats()
    {
        return new ArcCacheStats
        {
            Hits = Interlocked.Read(ref _hits),
            Misses = Interlocked.Read(ref _misses),
            Evictions = 0,
            T1Size = _blockCount,
            T2Size = 0,
            B1Size = 0,
            B2Size = 0,
        };
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            try
            {
                _mmf = MemoryMappedFile.CreateFromFile(
                    _vdeFilePath,
                    FileMode.Open,
                    mapName: null,
                    _regionStartOffset + _regionLength,
                    MemoryMappedFileAccess.Read);

                _accessor = _mmf.CreateViewAccessor(
                    _regionStartOffset,
                    _regionLength,
                    MemoryMappedFileAccess.Read);

                _initialized = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ArcCacheL2Mmap.EnsureInitialized] {ex.GetType().Name}: {ex.Message}");
                _mmf?.Dispose();
                _mmf = null;
                _accessor = null;
                _initialized = true; // Don't retry
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
    }
}
