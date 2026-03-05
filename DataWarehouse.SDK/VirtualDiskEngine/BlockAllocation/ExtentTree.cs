using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;

/// <summary>
/// In-memory sorted structure tracking free extents for efficient multi-block allocation.
/// Automatically merges adjacent free regions.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public sealed class ExtentTree
{
    private readonly SortedSet<FreeExtent> _extentsByStart = new(new ExtentByStartComparer());
    private readonly SortedDictionary<int, SortedSet<FreeExtent>> _extentsBySize = new();

    /// <summary>
    /// Gets the total number of free extents.
    /// </summary>
    public int ExtentCount => _extentsByStart.Count;

    /// <summary>
    /// Adds a free extent to the tree, merging with adjacent extents if possible.
    /// </summary>
    /// <param name="start">Start block of the extent.</param>
    /// <param name="count">Number of blocks in the extent.</param>
    public void AddFreeExtent(long start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0);

        var extent = new FreeExtent(start, count);

        // Check for adjacent extents to merge
        var toMerge = new List<FreeExtent>();

        // Look for extent immediately before
        var view = _extentsByStart.GetViewBetween(new FreeExtent(0, 1), new FreeExtent(start, 1));
        if (view.Count > 0)
        {
            var prev = view.Max;
            if (prev != null && prev.EndBlock == start)
            {
                toMerge.Add(prev);
            }
        }

        // Look for extent immediately after
        var next = _extentsByStart.FirstOrDefault(e => e.StartBlock == start + count);
        if (next != null)
        {
            toMerge.Add(next);
        }

        // Remove all extents to merge
        foreach (var e in toMerge)
        {
            RemoveExtentInternal(e);
        }

        // Compute merged extent
        long mergedStart = toMerge.Count > 0 ? Math.Min(start, toMerge.Min(e => e.StartBlock)) : start;
        long mergedEnd = start + count; // End of new extent
        if (toMerge.Count > 0)
            mergedEnd = Math.Max(mergedEnd, toMerge.Max(e => e.EndBlock));
        int mergedCount = (int)(mergedEnd - mergedStart);

        extent = new FreeExtent(mergedStart, mergedCount);

        // Add merged extent
        AddExtentInternal(extent);
    }

    /// <summary>
    /// Finds the smallest extent that can satisfy the allocation request (best-fit).
    /// </summary>
    /// <param name="minBlocks">Minimum number of blocks required.</param>
    /// <returns>The extent, or null if no suitable extent is found.</returns>
    public FreeExtent? FindExtent(int minBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minBlocks, 0);

        // Search by size for best-fit
        foreach (var kvp in _extentsBySize)
        {
            if (kvp.Key >= minBlocks && kvp.Value.Count > 0)
            {
                return kvp.Value.First();
            }
        }

        return null;
    }

    /// <summary>
    /// Removes an extent from the tree.
    /// </summary>
    /// <param name="extent">The extent to remove.</param>
    public void RemoveExtent(FreeExtent extent)
    {
        RemoveExtentInternal(extent);
    }

    /// <summary>
    /// Splits an extent: allocates a portion from the beginning, returns the remainder to the tree.
    /// </summary>
    /// <param name="extent">The extent to split.</param>
    /// <param name="allocatedBlocks">Number of blocks to allocate from the start.</param>
    /// <returns>The remainder extent, or null if the entire extent was allocated.</returns>
    public FreeExtent? SplitExtent(FreeExtent extent, int allocatedBlocks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(allocatedBlocks, 0);

        if (allocatedBlocks > extent.BlockCount)
        {
            throw new ArgumentException($"Cannot allocate {allocatedBlocks} blocks from an extent of {extent.BlockCount} blocks.", nameof(allocatedBlocks));
        }

        RemoveExtentInternal(extent);

        if (allocatedBlocks == extent.BlockCount)
        {
            return null; // Entire extent allocated
        }

        var remainder = new FreeExtent(extent.StartBlock + allocatedBlocks, extent.BlockCount - allocatedBlocks);
        AddExtentInternal(remainder);

        return remainder;
    }

    /// <summary>
    /// Builds the extent tree from a bitmap (scan for consecutive zero-bit runs).
    /// </summary>
    /// <param name="bitmap">The bitmap array (0=free, 1=allocated).</param>
    /// <param name="totalBlocks">Total number of blocks tracked.</param>
    public void BuildFromBitmap(byte[] bitmap, long totalBlocks)
    {
        _extentsByStart.Clear();
        _extentsBySize.Clear();

        long currentStart = -1;
        int currentCount = 0;

        for (long i = 0; i < totalBlocks; i++)
        {
            bool isFree = !GetBit(bitmap, i);

            if (isFree)
            {
                if (currentStart < 0)
                {
                    currentStart = i;
                    currentCount = 1;
                }
                else
                {
                    currentCount++;
                }
            }
            else
            {
                if (currentStart >= 0)
                {
                    // End of free run
                    AddExtentInternal(new FreeExtent(currentStart, currentCount));
                    currentStart = -1;
                    currentCount = 0;
                }
            }
        }

        // Handle final run
        if (currentStart >= 0)
        {
            AddExtentInternal(new FreeExtent(currentStart, currentCount));
        }
    }

    /// <summary>
    /// Returns a non-destructive snapshot of all free extents ordered by start block.
    /// This enumerates the internal sorted set directly without modifying tree state.
    /// </summary>
    public FreeExtent[] GetAllExtents() => [.. _extentsByStart];

    private void AddExtentInternal(FreeExtent extent)
    {
        _extentsByStart.Add(extent);

        if (!_extentsBySize.TryGetValue(extent.BlockCount, out var set))
        {
            set = new SortedSet<FreeExtent>(new ExtentByStartComparer());
            _extentsBySize[extent.BlockCount] = set;
        }

        set.Add(extent);
    }

    private void RemoveExtentInternal(FreeExtent extent)
    {
        _extentsByStart.Remove(extent);

        if (_extentsBySize.TryGetValue(extent.BlockCount, out var set))
        {
            set.Remove(extent);
            if (set.Count == 0)
            {
                _extentsBySize.Remove(extent.BlockCount);
            }
        }
    }

    private static bool GetBit(byte[] bitmap, long bitIndex)
    {
        long byteIndex = bitIndex / 8;
        int bitOffset = (int)(bitIndex % 8);
        return (bitmap[byteIndex] & (1 << bitOffset)) != 0;
    }

    private class ExtentByStartComparer : IComparer<FreeExtent>
    {
        public int Compare(FreeExtent? x, FreeExtent? y)
        {
            if (x == null || y == null) return 0;
            return x.StartBlock.CompareTo(y.StartBlock);
        }
    }
}

/// <summary>
/// Represents a contiguous free extent in the virtual disk.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-01/VDE-04)")]
public sealed record FreeExtent(long StartBlock, int BlockCount)
{
    /// <summary>
    /// Gets the end block (exclusive).
    /// </summary>
    public long EndBlock => StartBlock + BlockCount;
}
