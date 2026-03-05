using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Cache;

/// <summary>
/// L3 NVMe read cache that stores warm blocks (evicted from L1) on a dedicated
/// fast NVMe device. Organizes the cache file as a hash table with open addressing
/// and linear probing for collision resolution.
/// </summary>
/// <remarks>
/// Slot layout: [BlockNumber:8][Data:blockSize][XxHash64Checksum:8]
/// Uses FileStream with WriteThrough | Asynchronous for durability.
/// Disabled when the device path is null or empty.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: L3 NVMe tiered read cache (VOPT-05)")]
public sealed class ArcCacheL3NVMe : IArcCache, IDisposable
{
    private const long EmptySlotMarker = -1;
    private const int MaxProbeDistance = 16;
    private const int BlockNumberSize = 8;
    private const int ChecksumSize = 8;

    private readonly string? _l3DevicePath;
    private readonly int _blockSize;
    private readonly long _maxCacheBytes;
    private readonly long _slotCount;
    private readonly int _slotSize;

    private FileStream? _fileStream;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private volatile bool _initialized;
    private volatile bool _disposed;

    // Statistics
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _count;

    /// <summary>
    /// Creates a new L3 NVMe cache.
    /// </summary>
    /// <param name="l3DevicePath">Path to the NVMe cache file. Pass null or empty to disable.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="maxCacheBytes">Maximum size of the cache file in bytes.</param>
    public ArcCacheL3NVMe(string? l3DevicePath, int blockSize, long maxCacheBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCacheBytes);

        _l3DevicePath = l3DevicePath;
        _blockSize = blockSize;
        _maxCacheBytes = maxCacheBytes;
        _slotSize = BlockNumberSize + blockSize + ChecksumSize;
        _slotCount = maxCacheBytes / _slotSize;

        if (_slotCount < 1)
            _slotCount = 1;
    }

    /// <inheritdoc />
    public long Capacity => _slotCount;

    /// <inheritdoc />
    public long Count => Interlocked.Read(ref _count);

    /// <inheritdoc />
    public async ValueTask<byte[]?> GetAsync(long blockNumber, CancellationToken ct = default)
    {
        if (!IsEnabled())
        {
            Interlocked.Increment(ref _misses);
            return null;
        }

        EnsureInitialized();
        if (_fileStream == null)
        {
            Interlocked.Increment(ref _misses);
            return null;
        }

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long startSlot = blockNumber % _slotCount;
            if (startSlot < 0) startSlot += _slotCount;

            byte[] slotBuffer = new byte[_slotSize];

            for (int probe = 0; probe < MaxProbeDistance; probe++)
            {
                long slotIndex = (startSlot + probe) % _slotCount;
                long fileOffset = slotIndex * _slotSize;

                _fileStream.Seek(fileOffset, SeekOrigin.Begin);
                int bytesRead = await _fileStream.ReadAsync(slotBuffer.AsMemory(), ct).ConfigureAwait(false);

                if (bytesRead < _slotSize)
                {
                    // Slot not written yet (past end of file or short read)
                    break;
                }

                long storedBlockNumber = BinaryPrimitives.ReadInt64LittleEndian(slotBuffer.AsSpan(0, BlockNumberSize));

                if (storedBlockNumber == EmptySlotMarker)
                    break;

                if (storedBlockNumber == blockNumber)
                {
                    // Verify checksum
                    ReadOnlySpan<byte> dataSpan = slotBuffer.AsSpan(BlockNumberSize, _blockSize);
                    ulong storedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(
                        slotBuffer.AsSpan(BlockNumberSize + _blockSize, ChecksumSize));

                    ulong computedChecksum = XxHash64.HashToUInt64(dataSpan);

                    if (storedChecksum == computedChecksum)
                    {
                        Interlocked.Increment(ref _hits);
                        return dataSpan.ToArray();
                    }

                    // Checksum mismatch: treat as miss (corrupted slot)
                    break;
                }
            }

            Interlocked.Increment(ref _misses);
            return null;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask PutAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (!IsEnabled()) return;

        EnsureInitialized();
        if (_fileStream == null) return;

        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long startSlot = blockNumber % _slotCount;
            if (startSlot < 0) startSlot += _slotCount;

            byte[] slotBuffer = new byte[_slotSize];

            for (int probe = 0; probe < MaxProbeDistance; probe++)
            {
                long slotIndex = (startSlot + probe) % _slotCount;
                long fileOffset = slotIndex * _slotSize;

                // Check if slot is free or contains this block
                bool slotAvailable = false;
                bool isOverwrite = false;

                if (fileOffset < _fileStream.Length)
                {
                    _fileStream.Seek(fileOffset, SeekOrigin.Begin);
                    int bytesRead = await _fileStream.ReadAsync(slotBuffer.AsMemory(0, BlockNumberSize), ct).ConfigureAwait(false);

                    if (bytesRead < BlockNumberSize)
                    {
                        slotAvailable = true;
                    }
                    else
                    {
                        long storedBlockNumber = BinaryPrimitives.ReadInt64LittleEndian(slotBuffer.AsSpan(0, BlockNumberSize));
                        if (storedBlockNumber == EmptySlotMarker || storedBlockNumber == blockNumber)
                        {
                            slotAvailable = true;
                            isOverwrite = storedBlockNumber == blockNumber;
                        }
                    }
                }
                else
                {
                    slotAvailable = true;
                }

                if (slotAvailable)
                {
                    // Write slot: [blockNumber:8][data:blockSize][checksum:8]
                    BinaryPrimitives.WriteInt64LittleEndian(slotBuffer.AsSpan(0, BlockNumberSize), blockNumber);
                    data.Span.CopyTo(slotBuffer.AsSpan(BlockNumberSize, _blockSize));

                    ulong checksum = XxHash64.HashToUInt64(slotBuffer.AsSpan(BlockNumberSize, _blockSize));
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        slotBuffer.AsSpan(BlockNumberSize + _blockSize, ChecksumSize), checksum);

                    _fileStream.Seek(fileOffset, SeekOrigin.Begin);
                    await _fileStream.WriteAsync(slotBuffer.AsMemory(), ct).ConfigureAwait(false);

                    if (!isOverwrite)
                    {
                        Interlocked.Increment(ref _count);
                    }

                    return;
                }
            }

            // All probe slots occupied: evict first slot and write there
            long evictSlot = startSlot;
            long evictOffset = evictSlot * _slotSize;

            BinaryPrimitives.WriteInt64LittleEndian(slotBuffer.AsSpan(0, BlockNumberSize), blockNumber);
            data.Span.CopyTo(slotBuffer.AsSpan(BlockNumberSize, _blockSize));

            ulong evictChecksum = XxHash64.HashToUInt64(slotBuffer.AsSpan(BlockNumberSize, _blockSize));
            BinaryPrimitives.WriteUInt64LittleEndian(
                slotBuffer.AsSpan(BlockNumberSize + _blockSize, ChecksumSize), evictChecksum);

            _fileStream.Seek(evictOffset, SeekOrigin.Begin);
            await _fileStream.WriteAsync(slotBuffer.AsMemory(), ct).ConfigureAwait(false);

            Interlocked.Increment(ref _evictions);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public void Evict(long blockNumber)
    {
        if (!IsEnabled() || _fileStream == null) return;

        _ioLock.Wait();
        try
        {
            long startSlot = blockNumber % _slotCount;
            if (startSlot < 0) startSlot += _slotCount;

            byte[] header = new byte[BlockNumberSize];

            for (int probe = 0; probe < MaxProbeDistance; probe++)
            {
                long slotIndex = (startSlot + probe) % _slotCount;
                long fileOffset = slotIndex * _slotSize;

                if (fileOffset >= _fileStream.Length) break;

                _fileStream.Seek(fileOffset, SeekOrigin.Begin);
                int bytesRead = _fileStream.Read(header, 0, BlockNumberSize);
                if (bytesRead < BlockNumberSize) break;

                long storedBlockNumber = BinaryPrimitives.ReadInt64LittleEndian(header);

                if (storedBlockNumber == EmptySlotMarker) break;

                if (storedBlockNumber == blockNumber)
                {
                    // Mark slot as empty
                    BinaryPrimitives.WriteInt64LittleEndian(header, EmptySlotMarker);
                    _fileStream.Seek(fileOffset, SeekOrigin.Begin);
                    _fileStream.Write(header, 0, BlockNumberSize);

                    Interlocked.Decrement(ref _count);
                    Interlocked.Increment(ref _evictions);
                    return;
                }
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_fileStream == null) return;

        _ioLock.Wait();
        try
        {
            _fileStream.SetLength(0);
            Interlocked.Exchange(ref _count, 0);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public ArcCacheStats GetStats()
    {
        return new ArcCacheStats
        {
            Hits = Interlocked.Read(ref _hits),
            Misses = Interlocked.Read(ref _misses),
            Evictions = Interlocked.Read(ref _evictions),
            T1Size = Interlocked.Read(ref _count),
            T2Size = 0,
            B1Size = 0,
            B2Size = 0,
        };
    }

    private bool IsEnabled() => !string.IsNullOrEmpty(_l3DevicePath);

    private void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_ioLock)
        {
            if (_initialized) return;

            try
            {
                _fileStream = new FileStream(
                    _l3DevicePath!,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: _slotSize,
                    FileOptions.WriteThrough | FileOptions.Asynchronous);

                _initialized = true;
            }
            catch (Exception)
            {
                _fileStream = null;
                _initialized = true; // Don't retry
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fileStream?.Dispose();
        _fileStream = null;
        _ioLock.Dispose();
    }
}
