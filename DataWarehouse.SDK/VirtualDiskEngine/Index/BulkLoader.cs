using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Index;

/// <summary>
/// Efficient bottom-up B-Tree construction for initial population.
/// </summary>
/// <remarks>
/// Bulk loading is 5-10x faster than sequential inserts for large datasets because:
/// - No node splits during construction
/// - Sequential writes (good for disk I/O)
/// - Minimal tree traversal
/// Requires pre-sorted input data.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE B-Tree index (VDE-05)")]
public static class BulkLoader
{
    /// <summary>
    /// Bulk loads a B-Tree from pre-sorted key-value pairs.
    /// </summary>
    /// <param name="sortedEntries">Pre-sorted key-value pairs (caller must ensure sorted order).</param>
    /// <param name="device">Block device for I/O.</param>
    /// <param name="allocator">Block allocator for new nodes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Block number of the root node.</returns>
    /// <exception cref="ArgumentException">Thrown if entries are not sorted.</exception>
    public static async Task<long> BulkLoadAsync(
        IEnumerable<(byte[] Key, long Value)> sortedEntries,
        IBlockDevice device,
        IBlockAllocator allocator,
        int blockSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sortedEntries);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(allocator);

        var entryList = sortedEntries.ToList();
        if (entryList.Count == 0)
        {
            // Create empty root leaf node
            var emptyRoot = new BTreeNode(blockSize, avgKeySize: 64)
            {
                BlockNumber = allocator.AllocateBlock(ct),
                Header = new BTreeNodeHeader
                {
                    Flags = BTreeNodeHeader.FlagRoot | BTreeNodeHeader.FlagLeaf,
                    KeyCount = 0,
                    ParentBlock = -1,
                    NextLeafBlock = -1,
                    PrevLeafBlock = -1
                }
            };
            await WriteNodeDirectAsync(emptyRoot, device, blockSize, ct).ConfigureAwait(false);
            return emptyRoot.BlockNumber;
        }

        // Validate sorted order
        for (int i = 1; i < entryList.Count; i++)
        {
            if (BTreeNode.CompareKeys(entryList[i - 1].Key, entryList[i].Key) >= 0)
                throw new ArgumentException("Entries must be sorted in ascending order", nameof(sortedEntries));
        }

        // Build leaf nodes
        var leafNodes = await BuildLeafLevelAsync(entryList, device, allocator, blockSize, ct).ConfigureAwait(false);

        // Build internal levels bottom-up
        var currentLevel = leafNodes;
        while (currentLevel.Count > 1)
        {
            currentLevel = await BuildInternalLevelAsync(currentLevel, device, allocator, blockSize, ct).ConfigureAwait(false);
        }

        // The last remaining node is the root
        var root = currentLevel[0];
        var rootHeader = root.Header;
        rootHeader.Flags |= BTreeNodeHeader.FlagRoot;
        root.Header = rootHeader;
        await WriteNodeDirectAsync(root, device, blockSize, ct).ConfigureAwait(false);

        return root.BlockNumber;
    }

    /// <summary>
    /// Bulk loads a B-Tree from a streaming async enumerable of pre-sorted key-value pairs.
    /// </summary>
    /// <param name="sortedEntries">Pre-sorted key-value pairs (caller must ensure sorted order).</param>
    /// <param name="device">Block device for I/O.</param>
    /// <param name="allocator">Block allocator for new nodes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Block number of the root node.</returns>
    public static async Task<long> BulkLoadAsync(
        IAsyncEnumerable<(byte[] Key, long Value)> sortedEntries,
        IBlockDevice device,
        IBlockAllocator allocator,
        int blockSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sortedEntries);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(allocator);

        var materialized = new List<(byte[] Key, long Value)>();
        byte[]? previousKey = null;

        await foreach (var entry in sortedEntries.WithCancellation(ct))
        {
            // Validate sorted order
            if (previousKey != null && BTreeNode.CompareKeys(previousKey, entry.Key) >= 0)
                throw new ArgumentException("Entries must be sorted in ascending order", nameof(sortedEntries));

            materialized.Add(entry);
            previousKey = entry.Key;
        }

        return await BulkLoadAsync(materialized, device, allocator, blockSize, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the leaf level of the B-Tree from sorted entries.
    /// </summary>
    private static async Task<List<BTreeNode>> BuildLeafLevelAsync(
        List<(byte[] Key, long Value)> entries,
        IBlockDevice device,
        IBlockAllocator allocator,
        int blockSize,
        CancellationToken ct)
    {
        var leafNodes = new List<BTreeNode>();
        var maxKeys = BTreeNode.ComputeMaxKeys(blockSize, avgKeySize: 64);
        BTreeNode? currentLeaf = null;
        BTreeNode? previousLeaf = null;

        for (int i = 0; i < entries.Count; i++)
        {
            if (currentLeaf == null || currentLeaf.Header.KeyCount >= maxKeys)
            {
                // Allocate new leaf
                if (currentLeaf != null)
                {
                    await WriteNodeDirectAsync(currentLeaf, device, blockSize, ct).ConfigureAwait(false);
                    leafNodes.Add(currentLeaf);
                    previousLeaf = currentLeaf;
                }

                currentLeaf = new BTreeNode(blockSize, avgKeySize: 64)
                {
                    BlockNumber = allocator.AllocateBlock(ct),
                    Header = new BTreeNodeHeader
                    {
                        Flags = BTreeNodeHeader.FlagLeaf,
                        KeyCount = 0,
                        ParentBlock = -1,
                        NextLeafBlock = -1,
                        PrevLeafBlock = previousLeaf?.BlockNumber ?? -1
                    }
                };

                // Link previous leaf to current
                if (previousLeaf != null)
                {
                    var prevHeader = previousLeaf.Header;
                    prevHeader.NextLeafBlock = currentLeaf.BlockNumber;
                    previousLeaf.Header = prevHeader;
                    await WriteNodeDirectAsync(previousLeaf, device, blockSize, ct).ConfigureAwait(false);
                }
            }

            // Add entry to current leaf
            currentLeaf.Keys[currentLeaf.Header.KeyCount] = entries[i].Key;
            currentLeaf.Values[currentLeaf.Header.KeyCount] = entries[i].Value;
            var leafHeader = currentLeaf.Header;
            leafHeader.KeyCount++;
            currentLeaf.Header = leafHeader;
        }

        // Write final leaf
        if (currentLeaf != null)
        {
            await WriteNodeDirectAsync(currentLeaf, device, blockSize, ct).ConfigureAwait(false);
            leafNodes.Add(currentLeaf);
        }

        return leafNodes;
    }

    /// <summary>
    /// Builds an internal level of the B-Tree from the level below.
    /// </summary>
    private static async Task<List<BTreeNode>> BuildInternalLevelAsync(
        List<BTreeNode> childLevel,
        IBlockDevice device,
        IBlockAllocator allocator,
        int blockSize,
        CancellationToken ct)
    {
        var parentNodes = new List<BTreeNode>();
        var maxKeys = BTreeNode.ComputeMaxKeys(blockSize, avgKeySize: 64);
        BTreeNode? currentParent = null;

        for (int i = 0; i < childLevel.Count; i++)
        {
            var child = childLevel[i];

            if (currentParent == null || currentParent.Header.KeyCount >= maxKeys)
            {
                // Allocate new internal node
                if (currentParent != null)
                {
                    await WriteNodeDirectAsync(currentParent, device, blockSize, ct).ConfigureAwait(false);
                    parentNodes.Add(currentParent);
                }

                currentParent = new BTreeNode(blockSize, avgKeySize: 64)
                {
                    BlockNumber = allocator.AllocateBlock(ct),
                    Header = new BTreeNodeHeader
                    {
                        Flags = 0, // Internal node
                        KeyCount = 0,
                        ParentBlock = -1,
                        NextLeafBlock = -1,
                        PrevLeafBlock = -1
                    }
                };

                // First child pointer (no key before it)
                currentParent.ChildPointers[0] = child.BlockNumber;
                var childHeader = child.Header;
                childHeader.ParentBlock = currentParent.BlockNumber;
                child.Header = childHeader;
            }
            else
            {
                // Promote first key of child to parent (separator key)
                currentParent.Keys[currentParent.Header.KeyCount] = child.Keys[0];
                currentParent.ChildPointers[currentParent.Header.KeyCount + 1] = child.BlockNumber;
                var parentHeader = currentParent.Header;
                parentHeader.KeyCount++;
                currentParent.Header = parentHeader;
                var childHeader2 = child.Header;
                childHeader2.ParentBlock = currentParent.BlockNumber;
                child.Header = childHeader2;
            }

            // Update child's parent pointer
            await WriteNodeDirectAsync(child, device, blockSize, ct).ConfigureAwait(false);
        }

        // Write final parent
        if (currentParent != null)
        {
            await WriteNodeDirectAsync(currentParent, device, blockSize, ct).ConfigureAwait(false);
            parentNodes.Add(currentParent);
        }

        return parentNodes;
    }

    /// <summary>
    /// Writes a node directly to disk (no WAL during bulk load).
    /// </summary>
    private static async Task WriteNodeDirectAsync(BTreeNode node, IBlockDevice device, int blockSize, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            node.Serialize(buffer.AsSpan(0, blockSize), blockSize);
            await device.WriteBlockAsync(node.BlockNumber, buffer.AsMemory(0, blockSize), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
