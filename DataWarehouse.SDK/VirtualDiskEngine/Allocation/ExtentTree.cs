using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Allocation;

/// <summary>
/// B+tree of extent descriptors that maps logical file offsets to physical block ranges.
/// Replaces the traditional indirect/double-indirect/triple-indirect block pointer scheme
/// with a compact extent tree providing O(log n) lookup, insert, and remove.
/// </summary>
/// <remarks>
/// Integration with InodeV2: the first 4 extents are stored inline in the inode's Extents array
/// (ExtentCount 0-4). When more than 4 extents are needed, overflow extents are stored in this
/// B+tree, rooted at InodeV2.IndirectExtentBlock.
///
/// For a 1TB file with 4KB blocks, approximately 64 extents are needed instead of ~268M indirect pointers.
/// A single-level tree (1 root leaf) holds up to 168 extents. Two levels hold up to 168 * 253 = ~42K extents.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Extent tree B+tree operations (VOPT-09)")]
public sealed class ExtentTree
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly int _blockSize;
    private long _rootBlockNumber;

    /// <summary>Minimum fill ratio below which nodes are merged after delete.</summary>
    private const double MergeThreshold = 0.40;

    /// <summary>Current tree depth (1 = root is leaf, 2 = root + leaves, etc.).</summary>
    public int Height { get; private set; }

    /// <summary>Total number of nodes in the tree.</summary>
    public long NodeCount { get; private set; }

    /// <summary>Block number of the root node.</summary>
    public long RootBlockNumber => _rootBlockNumber;

    /// <summary>
    /// Creates an ExtentTree instance attached to an existing root block.
    /// </summary>
    /// <param name="device">Block device for I/O.</param>
    /// <param name="allocator">Block allocator for new tree nodes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="rootBlockNumber">Block number of the existing root node.</param>
    public ExtentTree(IBlockDevice device, IBlockAllocator allocator, int blockSize, long rootBlockNumber)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _blockSize = blockSize;
        _rootBlockNumber = rootBlockNumber;
        Height = 1;
        NodeCount = 1;
    }

    /// <summary>
    /// Creates a new empty extent tree by allocating a root block and initializing an empty leaf node.
    /// </summary>
    /// <param name="device">Block device for I/O.</param>
    /// <param name="allocator">Block allocator for new tree nodes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new ExtentTree with an empty root leaf node.</returns>
    public static async Task<ExtentTree> CreateAsync(
        IBlockDevice device, IBlockAllocator allocator, int blockSize, CancellationToken ct = default)
    {
        long rootBlock = allocator.AllocateBlock(ct);

        // Initialize empty leaf root
        var rootNode = ExtentTreeNode.CreateLeaf(blockSize);
        var buffer = new byte[blockSize];
        ExtentTreeNode.Serialize(rootNode, buffer, blockSize);
        await device.WriteBlockAsync(rootBlock, buffer, ct).ConfigureAwait(false);

        return new ExtentTree(device, allocator, blockSize, rootBlock)
        {
            Height = 1,
            NodeCount = 1,
        };
    }

    /// <summary>
    /// Looks up the extent covering the given logical offset by traversing from root to leaf.
    /// </summary>
    /// <param name="logicalOffset">The logical byte offset to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching extent, or null if no extent covers the offset.</returns>
    public async Task<InodeExtent?> LookupAsync(long logicalOffset, CancellationToken ct = default)
    {
        long currentBlock = _rootBlockNumber;
        var buffer = new byte[_blockSize];

        while (true)
        {
            await _device.ReadBlockAsync(currentBlock, buffer, ct).ConfigureAwait(false);
            var node = ExtentTreeNode.Deserialize(buffer, _blockSize);

            if (node.IsLeaf)
            {
                return node.FindExtent(logicalOffset);
            }

            // Internal node: find the child whose range covers this offset
            currentBlock = FindChildBlock(node, logicalOffset);
        }
    }

    /// <summary>
    /// Inserts an extent into the tree. Splits nodes as needed when full (standard B+tree split).
    /// </summary>
    /// <param name="extent">The extent to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task InsertAsync(InodeExtent extent, CancellationToken ct = default)
    {
        // Find the leaf node for insertion, tracking the path from root to leaf
        var path = new List<(long BlockNumber, ExtentTreeNode Node)>();
        long currentBlock = _rootBlockNumber;
        var buffer = new byte[_blockSize];

        while (true)
        {
            await _device.ReadBlockAsync(currentBlock, buffer, ct).ConfigureAwait(false);
            var node = ExtentTreeNode.Deserialize(buffer, _blockSize);
            path.Add((currentBlock, node));

            if (node.IsLeaf)
                break;

            currentBlock = FindChildBlock(node, extent.LogicalOffset);
        }

        // Insert into the leaf
        var (leafBlock, leafNode) = path[^1];
        int insertResult = leafNode.InsertEntry(extent, _blockSize);

        if (insertResult >= 0)
        {
            // Leaf had space, write it back
            await WriteNodeAsync(leafBlock, leafNode, ct).ConfigureAwait(false);
            return;
        }

        // Leaf is full, need to split upward
        await SplitAndInsertAsync(path, extent, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the extent covering the given logical offset. Merges underfull nodes.
    /// </summary>
    /// <param name="logicalOffset">The logical offset of the extent to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if an extent was removed, false if none found.</returns>
    public async Task<bool> RemoveAsync(long logicalOffset, CancellationToken ct = default)
    {
        // Find the leaf and path
        var path = new List<(long BlockNumber, ExtentTreeNode Node)>();
        long currentBlock = _rootBlockNumber;
        var buffer = new byte[_blockSize];

        while (true)
        {
            await _device.ReadBlockAsync(currentBlock, buffer, ct).ConfigureAwait(false);
            var node = ExtentTreeNode.Deserialize(buffer, _blockSize);
            path.Add((currentBlock, node));

            if (node.IsLeaf)
                break;

            currentBlock = FindChildBlock(node, logicalOffset);
        }

        var (leafBlock, leafNode) = path[^1];

        // Find the entry to remove
        int removeIndex = -1;
        for (int i = 0; i < leafNode.EntryCount; i++)
        {
            if (leafNode.LeafEntries[i].LogicalOffset == logicalOffset)
            {
                removeIndex = i;
                break;
            }
        }

        if (removeIndex < 0)
            return false;

        // Shift entries down
        for (int i = removeIndex; i < leafNode.EntryCount - 1; i++)
        {
            leafNode.LeafEntries[i] = leafNode.LeafEntries[i + 1];
        }
        leafNode.LeafEntries[leafNode.EntryCount - 1] = default;
        leafNode.EntryCount--;

        await WriteNodeAsync(leafBlock, leafNode, ct).ConfigureAwait(false);

        // Check if underflow and merge/redistribute if needed
        if (path.Count > 1)
        {
            int maxEntries = ExtentTreeNode.MaxLeafEntries(_blockSize);
            double fillRatio = (double)leafNode.EntryCount / maxEntries;
            if (fillRatio < MergeThreshold && leafNode.EntryCount > 0)
            {
                await TryMergeOrRedistributeAsync(path, ct).ConfigureAwait(false);
            }
        }

        return true;
    }

    /// <summary>
    /// Scans the leaf chain from leftmost to rightmost, returning all extents in logical order.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All extents in the tree, sorted by logical offset.</returns>
    public async Task<IReadOnlyList<InodeExtent>> GetAllExtentsAsync(CancellationToken ct = default)
    {
        var result = new List<InodeExtent>();
        var buffer = new byte[_blockSize];

        // Navigate to leftmost leaf
        long currentBlock = _rootBlockNumber;
        while (true)
        {
            await _device.ReadBlockAsync(currentBlock, buffer, ct).ConfigureAwait(false);
            var node = ExtentTreeNode.Deserialize(buffer, _blockSize);

            if (node.IsLeaf)
            {
                // Collect entries from this leaf and follow sibling chain
                for (int i = 0; i < node.EntryCount; i++)
                    result.Add(node.LeafEntries[i]);

                long nextBlock = node.NextSiblingBlock;
                while (nextBlock != 0)
                {
                    await _device.ReadBlockAsync(nextBlock, buffer, ct).ConfigureAwait(false);
                    var sibling = ExtentTreeNode.Deserialize(buffer, _blockSize);
                    for (int i = 0; i < sibling.EntryCount; i++)
                        result.Add(sibling.LeafEntries[i]);
                    nextBlock = sibling.NextSiblingBlock;
                }

                return result;
            }

            // Go to leftmost child
            if (node.EntryCount > 0)
                currentBlock = node.InternalEntries[0].ChildBlockNumber;
            else
                return result; // Empty tree
        }
    }

    /// <summary>
    /// Translates a logical byte offset to a physical block number by looking up the covering extent.
    /// </summary>
    /// <param name="logicalOffset">The logical byte offset within the file.</param>
    /// <param name="blockSize">The block size in bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The physical block number, or -1 if no extent covers the offset.</returns>
    public async Task<long> GetPhysicalBlockAsync(long logicalOffset, int blockSize, CancellationToken ct = default)
    {
        var extent = await LookupAsync(logicalOffset, ct).ConfigureAwait(false);
        if (extent is null)
            return -1;

        var ex = extent.Value;
        long extentEndOffset = ex.LogicalOffset + (long)ex.BlockCount * blockSize;
        if (logicalOffset < ex.LogicalOffset || logicalOffset >= extentEndOffset)
            return -1;

        // Calculate block offset within the extent
        long offsetWithinExtent = logicalOffset - ex.LogicalOffset;
        long blockIndex = offsetWithinExtent / blockSize;
        return ex.StartBlock + blockIndex;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Finds the child block number for a given logical offset within an internal node.
    /// Uses binary search to find the rightmost entry whose LogicalOffset &lt;= offset.
    /// </summary>
    private static long FindChildBlock(ExtentTreeNode node, long logicalOffset)
    {
        if (node.EntryCount == 0)
            throw new InvalidOperationException("Internal node has no entries.");

        // Binary search: find the last entry with LogicalOffset <= logicalOffset
        int lo = 0, hi = node.EntryCount - 1;
        int bestIndex = 0; // Default to first child

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (node.InternalEntries[mid].LogicalOffset <= logicalOffset)
            {
                bestIndex = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return node.InternalEntries[bestIndex].ChildBlockNumber;
    }

    /// <summary>
    /// Writes a node to disk with proper serialization and trailer.
    /// </summary>
    private async Task WriteNodeAsync(long blockNumber, ExtentTreeNode node, CancellationToken ct)
    {
        var buffer = new byte[_blockSize];
        ExtentTreeNode.Serialize(node, buffer, _blockSize);
        await _device.WriteBlockAsync(blockNumber, buffer, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a node from disk.
    /// </summary>
    private async Task<ExtentTreeNode> ReadNodeAsync(long blockNumber, CancellationToken ct)
    {
        var buffer = new byte[_blockSize];
        await _device.ReadBlockAsync(blockNumber, buffer, ct).ConfigureAwait(false);
        return ExtentTreeNode.Deserialize(buffer, _blockSize);
    }

    /// <summary>
    /// Handles split-and-insert when a leaf is full. Splits the leaf 50/50 and pushes
    /// the median key up to the parent. If the parent is also full, splits recursively.
    /// If the root splits, a new root is allocated and the tree grows taller.
    /// </summary>
    private async Task SplitAndInsertAsync(
        List<(long BlockNumber, ExtentTreeNode Node)> path, InodeExtent extent, CancellationToken ct)
    {
        // Collect all entries from the full leaf plus the new extent, sorted
        var (leafBlock, leafNode) = path[^1];
        var allEntries = new List<InodeExtent>(leafNode.EntryCount + 1);
        for (int i = 0; i < leafNode.EntryCount; i++)
            allEntries.Add(leafNode.LeafEntries[i]);
        allEntries.Add(extent);
        allEntries.Sort((a, b) => a.LogicalOffset.CompareTo(b.LogicalOffset));

        // Split 50/50
        int splitPoint = allEntries.Count / 2;

        // Left node gets entries [0..splitPoint)
        leafNode.EntryCount = 0;
        for (int i = 0; i < splitPoint; i++)
        {
            leafNode.LeafEntries[i] = allEntries[i];
            leafNode.EntryCount++;
        }
        // Clear remaining slots
        for (int i = splitPoint; i < leafNode.LeafEntries.Length; i++)
            leafNode.LeafEntries[i] = default;

        // Right node gets entries [splitPoint..)
        long newBlock = _allocator.AllocateBlock(ct);
        var rightNode = ExtentTreeNode.CreateLeaf(_blockSize);
        for (int i = splitPoint; i < allEntries.Count; i++)
        {
            rightNode.LeafEntries[i - splitPoint] = allEntries[i];
            rightNode.EntryCount++;
        }

        // Maintain leaf chain
        rightNode.NextSiblingBlock = leafNode.NextSiblingBlock;
        rightNode.PrevSiblingBlock = leafBlock;
        leafNode.NextSiblingBlock = newBlock;

        // Update the old next sibling's prev pointer
        if (rightNode.NextSiblingBlock != 0)
        {
            var nextSibling = await ReadNodeAsync(rightNode.NextSiblingBlock, ct).ConfigureAwait(false);
            nextSibling.PrevSiblingBlock = newBlock;
            await WriteNodeAsync(rightNode.NextSiblingBlock, nextSibling, ct).ConfigureAwait(false);
        }

        // Write both leaves
        await WriteNodeAsync(leafBlock, leafNode, ct).ConfigureAwait(false);
        await WriteNodeAsync(newBlock, rightNode, ct).ConfigureAwait(false);
        NodeCount++;

        // Push the median key (first key of right node) up to the parent
        long medianKey = allEntries[splitPoint].LogicalOffset;
        await PushUpAsync(path, path.Count - 2, medianKey, newBlock, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes a key and child pointer up to the parent at the given path index.
    /// If the parent is full, splits it recursively. If no parent exists (root split),
    /// allocates a new root and grows the tree.
    /// </summary>
    private async Task PushUpAsync(
        List<(long BlockNumber, ExtentTreeNode Node)> path, int parentIndex,
        long key, long childBlock, CancellationToken ct)
    {
        if (parentIndex < 0)
        {
            // Root split: create new root
            long newRootBlock = _allocator.AllocateBlock(ct);
            var newRoot = ExtentTreeNode.CreateInternal(
                (ushort)(path[0].Node.Level + 1), _blockSize);

            // First entry points to old root (covers everything <= key)
            newRoot.InternalEntries[0] = (long.MinValue, path[0].BlockNumber);
            newRoot.InternalEntries[1] = (key, childBlock);
            newRoot.EntryCount = 2;

            await WriteNodeAsync(newRootBlock, newRoot, ct).ConfigureAwait(false);
            _rootBlockNumber = newRootBlock;
            Height++;
            NodeCount++;
            return;
        }

        var (parentBlock, parentNode) = path[parentIndex];
        int maxEntries = ExtentTreeNode.MaxInternalEntries(_blockSize);

        if (parentNode.EntryCount < maxEntries)
        {
            // Parent has space, insert the key+child
            InsertInternalEntry(parentNode, key, childBlock);
            await WriteNodeAsync(parentBlock, parentNode, ct).ConfigureAwait(false);
            return;
        }

        // Parent is full, split it
        var allEntries = new List<(long LogicalOffset, long ChildBlockNumber)>(parentNode.EntryCount + 1);
        for (int i = 0; i < parentNode.EntryCount; i++)
            allEntries.Add(parentNode.InternalEntries[i]);
        allEntries.Add((key, childBlock));
        allEntries.Sort((a, b) => a.LogicalOffset.CompareTo(b.LogicalOffset));

        int splitPoint = allEntries.Count / 2;

        // Left node
        parentNode.EntryCount = 0;
        for (int i = 0; i < splitPoint; i++)
        {
            parentNode.InternalEntries[i] = allEntries[i];
            parentNode.EntryCount++;
        }
        for (int i = splitPoint; i < parentNode.InternalEntries.Length; i++)
            parentNode.InternalEntries[i] = default;

        // Right node
        long newBlock = _allocator.AllocateBlock(ct);
        var rightNode = ExtentTreeNode.CreateInternal(parentNode.Level, _blockSize);
        for (int i = splitPoint; i < allEntries.Count; i++)
        {
            rightNode.InternalEntries[i - splitPoint] = allEntries[i];
            rightNode.EntryCount++;
        }

        await WriteNodeAsync(parentBlock, parentNode, ct).ConfigureAwait(false);
        await WriteNodeAsync(newBlock, rightNode, ct).ConfigureAwait(false);
        NodeCount++;

        // Push median up to grandparent
        long medianKey = allEntries[splitPoint].LogicalOffset;
        await PushUpAsync(path, parentIndex - 1, medianKey, newBlock, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts a key+child entry into an internal node maintaining sorted order.
    /// </summary>
    private static void InsertInternalEntry(ExtentTreeNode node, long key, long childBlock)
    {
        int insertPos = 0;
        for (int i = 0; i < node.EntryCount; i++)
        {
            if (node.InternalEntries[i].LogicalOffset <= key)
                insertPos = i + 1;
            else
                break;
        }

        // Shift entries
        for (int i = node.EntryCount; i > insertPos; i--)
            node.InternalEntries[i] = node.InternalEntries[i - 1];

        node.InternalEntries[insertPos] = (key, childBlock);
        node.EntryCount++;
    }

    /// <summary>
    /// Attempts to merge an underfull leaf with a sibling or redistribute entries.
    /// </summary>
    private async Task TryMergeOrRedistributeAsync(
        List<(long BlockNumber, ExtentTreeNode Node)> path, CancellationToken ct)
    {
        if (path.Count < 2)
            return; // Root node cannot be merged

        var (leafBlock, leafNode) = path[^1];
        int maxEntries = ExtentTreeNode.MaxLeafEntries(_blockSize);

        // Try to merge with next sibling
        if (leafNode.NextSiblingBlock != 0)
        {
            var sibling = await ReadNodeAsync(leafNode.NextSiblingBlock, ct).ConfigureAwait(false);
            int combinedCount = leafNode.EntryCount + sibling.EntryCount;

            if (combinedCount <= maxEntries)
            {
                // Merge: copy sibling entries into this node
                for (int i = 0; i < sibling.EntryCount; i++)
                {
                    leafNode.LeafEntries[leafNode.EntryCount + i] = sibling.LeafEntries[i];
                }
                leafNode.EntryCount = (ushort)combinedCount;
                leafNode.NextSiblingBlock = sibling.NextSiblingBlock;

                // Update the sibling's next prev pointer
                if (sibling.NextSiblingBlock != 0)
                {
                    var nextNext = await ReadNodeAsync(sibling.NextSiblingBlock, ct).ConfigureAwait(false);
                    nextNext.PrevSiblingBlock = leafBlock;
                    await WriteNodeAsync(sibling.NextSiblingBlock, nextNext, ct).ConfigureAwait(false);
                }

                await WriteNodeAsync(leafBlock, leafNode, ct).ConfigureAwait(false);

                // Free the merged sibling block
                _allocator.FreeBlock(leafNode.NextSiblingBlock == sibling.NextSiblingBlock
                    ? path[^1].Node.NextSiblingBlock
                    : leafNode.NextSiblingBlock);
                NodeCount--;
                return;
            }

            // Redistribute: move entries from sibling to this node to balance
            if (sibling.EntryCount > leafNode.EntryCount)
            {
                int toMove = (sibling.EntryCount - leafNode.EntryCount) / 2;
                if (toMove > 0)
                {
                    for (int i = 0; i < toMove; i++)
                    {
                        leafNode.LeafEntries[leafNode.EntryCount + i] = sibling.LeafEntries[i];
                    }
                    leafNode.EntryCount += (ushort)toMove;

                    // Shift sibling entries
                    for (int i = 0; i < sibling.EntryCount - toMove; i++)
                        sibling.LeafEntries[i] = sibling.LeafEntries[i + toMove];
                    for (int i = sibling.EntryCount - toMove; i < sibling.EntryCount; i++)
                        sibling.LeafEntries[i] = default;
                    sibling.EntryCount -= (ushort)toMove;

                    await WriteNodeAsync(leafBlock, leafNode, ct).ConfigureAwait(false);
                    await WriteNodeAsync(leafNode.NextSiblingBlock, sibling, ct).ConfigureAwait(false);
                }
            }
        }
    }
}
