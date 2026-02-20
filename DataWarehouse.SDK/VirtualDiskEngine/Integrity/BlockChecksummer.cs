using DataWarehouse.SDK.Contracts;
using System;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// XxHash3-based block checksum computation and verification.
/// Provides fast, non-cryptographic checksums for data integrity.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-04 Checksumming)")]
public sealed class BlockChecksummer : IBlockChecksummer
{
    private const int MaxVerifiedCacheEntries = 10000;

    private readonly ChecksumTable _table;

    // Cache of recently verified blocks (key: block number, value: true if verified)
    // Avoids redundant re-verification on repeated reads
    private readonly BoundedDictionary<long, bool> _verifiedCache = new BoundedDictionary<long, bool>(1000);

    /// <summary>
    /// Initializes a new block checksummer.
    /// </summary>
    /// <param name="table">Checksum table for persistent storage.</param>
    public BlockChecksummer(ChecksumTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    /// <inheritdoc/>
    public ulong ComputeChecksum(ReadOnlySpan<byte> blockData)
    {
        // Use XxHash3 from System.IO.Hashing (available in .NET 9+)
        // XxHash3 is extremely fast and provides good collision resistance
        return XxHash3.HashToUInt64(blockData);
    }

    /// <inheritdoc/>
    public bool VerifyChecksum(ReadOnlySpan<byte> blockData, ulong expectedChecksum)
    {
        ulong actualChecksum = ComputeChecksum(blockData);
        return actualChecksum == expectedChecksum;
    }

    /// <inheritdoc/>
    public async Task StoreChecksumAsync(long blockNumber, ulong checksum, CancellationToken ct = default)
    {
        await _table.SetChecksumAsync(blockNumber, checksum, ct);

        // Update verified cache
        _verifiedCache[blockNumber] = true;

        // Evict oldest if cache too large
        if (_verifiedCache.Count > MaxVerifiedCacheEntries)
        {
            // Simplified eviction: remove first entry
            // In production, use LRU or similar
            foreach (var key in _verifiedCache.Keys)
            {
                if (_verifiedCache.TryRemove(key, out _))
                {
                    break;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<ulong> GetStoredChecksumAsync(long blockNumber, CancellationToken ct = default)
    {
        return await _table.GetChecksumAsync(blockNumber, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> VerifyBlockAsync(long blockNumber, ReadOnlyMemory<byte> blockData, CancellationToken ct = default)
    {
        // Check verified cache first
        if (_verifiedCache.TryGetValue(blockNumber, out bool verified) && verified)
        {
            // Block was recently verified and hasn't been written since
            return true;
        }

        // Compute checksum
        ulong actualChecksum = ComputeChecksum(blockData.Span);

        // Get stored checksum
        ulong expectedChecksum = await _table.GetChecksumAsync(blockNumber, ct);

        // Compare
        bool matches = actualChecksum == expectedChecksum;

        if (matches)
        {
            // Add to verified cache
            _verifiedCache[blockNumber] = true;

            // Evict oldest if cache too large
            if (_verifiedCache.Count > MaxVerifiedCacheEntries)
            {
                foreach (var key in _verifiedCache.Keys)
                {
                    if (_verifiedCache.TryRemove(key, out _))
                    {
                        break;
                    }
                }
            }
        }

        return matches;
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _table.FlushAsync(ct);
    }

    /// <inheritdoc/>
    public void InvalidateCacheEntry(long blockNumber)
    {
        // Remove from verified cache when block is written
        _verifiedCache.TryRemove(blockNumber, out _);
    }
}
