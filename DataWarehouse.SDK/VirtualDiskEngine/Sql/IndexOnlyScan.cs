using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

/// <summary>
/// Performs index-only scans by using zone map metadata to skip non-matching data extents.
/// Returns query results using only index metadata without reading actual data blocks when possible.
/// </summary>
/// <remarks>
/// <para>
/// Zone maps store min/max statistics per extent, enabling the scan to skip entire extents
/// that cannot contain matching rows. This is highly effective for range predicates on
/// clustered or nearly-sorted data.
/// </para>
/// <para>
/// When no zone map filter is provided, or when zone map information is unavailable,
/// the scan falls back to a full index range scan via the adaptive index.
/// </para>
/// <para>
/// Uses the existing <see cref="ZoneMapEntry"/> struct from the zone map index infrastructure
/// which includes min/max values, null count, row count, and extent location metadata.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-15 index-only scan with zone maps")]
public sealed class IndexOnlyScan
{
    private readonly IAdaptiveIndex _index;
    private readonly int _blockSize;

    /// <summary>
    /// Zone map entries for each extent, indexed by extent number.
    /// Populated externally or by reading extent headers.
    /// </summary>
    public List<ZoneMapEntry> ZoneMap { get; } = new();

    /// <summary>
    /// Initializes a new index-only scan.
    /// </summary>
    /// <param name="index">The adaptive index to scan.</param>
    /// <param name="blockSize">Block size in bytes for extent calculations.</param>
    public IndexOnlyScan(IAdaptiveIndex index, int blockSize)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));

        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be positive.");

        _blockSize = blockSize;
    }

    /// <summary>
    /// Scans the index, using zone maps to skip extents that cannot contain matching rows.
    /// Only reads extent headers for zone map checks, then reads matching extents.
    /// Falls back to a full range scan if no zone map is available.
    /// </summary>
    /// <param name="startKey">Inclusive start key, or null for scan from beginning.</param>
    /// <param name="endKey">Inclusive end key, or null for scan to end.</param>
    /// <param name="zoneMapFilter">Optional predicate to filter extents by zone map entry. If null, all extents are included.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of (Key, Value) pairs from matching extents.</returns>
    public async IAsyncEnumerable<(byte[] Key, long Value)> ScanAsync(
        byte[]? startKey,
        byte[]? endKey,
        Predicate<ZoneMapEntry>? zoneMapFilter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (zoneMapFilter is not null && ZoneMap.Count > 0)
        {
            // Zone-map-filtered scan: determine which extents match
            var matchingExtents = new HashSet<int>();

            for (int i = 0; i < ZoneMap.Count; i++)
            {
                if (zoneMapFilter(ZoneMap[i]))
                {
                    matchingExtents.Add(i);
                }
            }

            if (matchingExtents.Count == 0)
            {
                // No extents match the filter; return empty
                yield break;
            }

            // For matching extents, delegate to the index range query
            // The index handles the actual key-based filtering
            await foreach (var entry in _index.RangeQueryAsync(startKey, endKey, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                // If we have zone map info, check if this entry's value falls
                // within a matching extent
                int extentIndex = GetExtentIndex(entry.Value);
                if (extentIndex >= 0 && extentIndex < ZoneMap.Count)
                {
                    if (!matchingExtents.Contains(extentIndex))
                    {
                        // This entry is in a non-matching extent; skip
                        continue;
                    }
                }

                yield return entry;
            }
        }
        else
        {
            // No zone map filter or no zone map available; full range scan fallback
            await foreach (var entry in _index.RangeQueryAsync(startKey, endKey, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Determines whether all requested columns are covered by the index columns,
    /// allowing a pure index-only scan without reading data blocks.
    /// </summary>
    /// <param name="requestedColumns">The columns required by the query.</param>
    /// <param name="indexColumns">The columns stored in the index.</param>
    /// <returns>True if all requested columns are available in the index.</returns>
    public bool CanSatisfyFromIndexOnly(string[] requestedColumns, string[] indexColumns)
    {
        if (requestedColumns is null || requestedColumns.Length == 0)
            return true;

        if (indexColumns is null || indexColumns.Length == 0)
            return false;

        var indexSet = new HashSet<string>(indexColumns, StringComparer.OrdinalIgnoreCase);

        foreach (string col in requestedColumns)
        {
            if (!indexSet.Contains(col))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Calculates the extent index for a given block number based on block size.
    /// Each extent contains a fixed number of entries determined by block size.
    /// </summary>
    private int GetExtentIndex(long blockNumber)
    {
        if (blockNumber < 0) return -1;

        // Extents are groups of blocks; each extent covers _blockSize / 8 entries (8 bytes per entry)
        int entriesPerExtent = _blockSize / 8;
        if (entriesPerExtent <= 0) return 0;

        return (int)(blockNumber / entriesPerExtent);
    }
}
