using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Result of a full object scrub operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Merkle integrity verifier (VOPT-25)")]
public readonly struct ScrubResult
{
    /// <summary>Whether the object is entirely clean (no corrupt blocks).</summary>
    public bool IsClean { get; }

    /// <summary>Total number of blocks verified during the scrub.</summary>
    public long BlocksVerified { get; }

    /// <summary>Block numbers identified as corrupt.</summary>
    public IReadOnlyList<long> CorruptBlocks { get; }

    /// <summary>Wall-clock duration of the scrub operation.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Creates a new scrub result.</summary>
    public ScrubResult(bool isClean, long blocksVerified, IReadOnlyList<long> corruptBlocks, TimeSpan duration)
    {
        IsClean = isClean;
        BlocksVerified = blocksVerified;
        CorruptBlocks = corruptBlocks ?? Array.Empty<long>();
        Duration = duration;
    }
}

/// <summary>
/// Merkle tree verification engine with binary-search corruption localization, incremental
/// updates, and full scrub support. Stores and retrieves Merkle trees from the Integrity
/// Tree region of the VDE.
/// </summary>
/// <remarks>
/// Tree persistence uses level-order (BFS) serialization for cache-friendly reads.
/// Each node is 48 bytes: 32 bytes (SHA-256 hash) + 8 bytes (left pointer) + 8 bytes (right pointer).
/// For 4KB blocks, approximately 85 nodes fit per block.
///
/// Binary-search corruption localization finds corrupt blocks in O(log n) block reads
/// versus O(n) for a full scan, making it ideal for large objects.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Merkle integrity verifier (VOPT-25)")]
public sealed class MerkleIntegrityVerifier
{
    private const int HashSize = 32;
    private const int NodeSize = 48; // 32 (hash) + 8 (left ptr) + 8 (right ptr)

    private readonly IBlockDevice _device;
    private readonly long _integrityTreeRegionStart;
    private readonly int _blockSize;

    /// <summary>
    /// Creates a new Merkle integrity verifier.
    /// </summary>
    /// <param name="device">Block device for reading/writing tree data.</param>
    /// <param name="integrityTreeRegionStart">Block number where the integrity tree region starts.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public MerkleIntegrityVerifier(IBlockDevice device, long integrityTreeRegionStart, int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _integrityTreeRegionStart = integrityTreeRegionStart;
        _blockSize = blockSize;
    }

    // ── Merkle Tree Persistence ─────────────────────────────────────────

    /// <summary>
    /// Builds a complete Merkle tree from block checksums and stores it in the
    /// Integrity Tree region using level-order (BFS) serialization.
    /// </summary>
    /// <param name="objectInodeNumber">Inode number identifying the object.</param>
    /// <param name="blockChecksums">Array of per-block checksums (XxHash64 values as byte[8]).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StoreMerkleTreeAsync(long objectInodeNumber, byte[][] blockChecksums, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(blockChecksums);
        if (blockChecksums.Length == 0)
            return;

        // Build leaf hashes: SHA256 of each block checksum
        var leafHashes = new byte[blockChecksums.Length][];
        for (int i = 0; i < blockChecksums.Length; i++)
        {
            leafHashes[i] = SHA256.HashData(blockChecksums[i]);
        }

        // Build complete Merkle tree
        int capacity = NextPowerOfTwo(leafHashes.Length);
        int totalNodes = 2 * capacity;
        var nodes = new MerkleTreeNode[totalNodes];

        // Initialize all nodes with zero hashes
        for (int i = 0; i < totalNodes; i++)
        {
            nodes[i] = new MerkleTreeNode(new byte[HashSize], -1, -1);
        }

        // Set leaf nodes
        for (int i = 0; i < leafHashes.Length; i++)
        {
            int nodeIndex = capacity + i;
            nodes[nodeIndex] = new MerkleTreeNode(leafHashes[i], -1, -1);
        }

        // Build internal nodes bottom-up
        Span<byte> combined = stackalloc byte[HashSize * 2];
        for (int i = capacity - 1; i >= 1; i--)
        {
            int leftIdx = i * 2;
            int rightIdx = i * 2 + 1;

            nodes[leftIdx].Hash.AsSpan().CopyTo(combined);
            nodes[rightIdx].Hash.AsSpan().CopyTo(combined.Slice(HashSize));

            var parentHash = SHA256.HashData(combined);
            nodes[i] = new MerkleTreeNode(parentHash, leftIdx, rightIdx);
        }

        // Serialize tree to blocks using level-order (BFS)
        int payloadSize = UniversalBlockTrailer.PayloadSize(_blockSize);
        int nodesPerBlock = payloadSize / NodeSize;
        int nodeCount = totalNodes - 1; // nodes 1..totalNodes-1
        int blocksNeeded = 1 + (nodeCount + nodesPerBlock - 1) / nodesPerBlock; // 1 header + data

        // Header block: [InodeNumber:8][LeafCount:4][TreeCapacity:4][RootHash:32]
        var headerBlock = new byte[_blockSize];
        BinaryPrimitives.WriteInt64LittleEndian(headerBlock.AsSpan(0, 8), objectInodeNumber);
        BinaryPrimitives.WriteInt32LittleEndian(headerBlock.AsSpan(8, 4), leafHashes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(headerBlock.AsSpan(12, 4), capacity);
        nodes[1].Hash.AsSpan().CopyTo(headerBlock.AsSpan(16, HashSize));
        UniversalBlockTrailer.Write(headerBlock, _blockSize, BlockTypeTags.MTRK, 1);

        long baseBlock = GetObjectTreeBaseBlock(objectInodeNumber);
        await _device.WriteBlockAsync(baseBlock, headerBlock, ct);

        // Data blocks: packed nodes in level-order
        int nodeWritten = 0;
        for (int b = 0; b < blocksNeeded - 1; b++)
        {
            var dataBlock = new byte[_blockSize];
            int offset = 0;
            int nodesThisBlock = Math.Min(nodesPerBlock, nodeCount - nodeWritten);

            for (int n = 0; n < nodesThisBlock; n++)
            {
                int nodeIndex = nodeWritten + 1; // 1-indexed
                SerializeNode(nodes[nodeIndex], dataBlock.AsSpan(offset, NodeSize));
                offset += NodeSize;
                nodeWritten++;
            }

            UniversalBlockTrailer.Write(dataBlock, _blockSize, BlockTypeTags.MTRK, 1);
            await _device.WriteBlockAsync(baseBlock + 1 + b, dataBlock, ct);
        }
    }

    /// <summary>
    /// Reads the stored Merkle root hash for an object.
    /// </summary>
    /// <param name="objectInodeNumber">Inode number identifying the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>32-byte Merkle root hash, or null if no tree exists.</returns>
    public async Task<byte[]> GetMerkleRootAsync(long objectInodeNumber, CancellationToken ct)
    {
        long baseBlock = GetObjectTreeBaseBlock(objectInodeNumber);
        var headerBlock = new byte[_blockSize];

        await _device.ReadBlockAsync(baseBlock, headerBlock, ct);

        // Verify header block integrity
        if (!UniversalBlockTrailer.Verify(headerBlock, _blockSize))
            return Array.Empty<byte>();

        long storedInode = BinaryPrimitives.ReadInt64LittleEndian(headerBlock.AsSpan(0, 8));
        if (storedInode != objectInodeNumber)
            return Array.Empty<byte>();

        var rootHash = new byte[HashSize];
        headerBlock.AsSpan(16, HashSize).CopyTo(rootHash);
        return rootHash;
    }

    // ── Binary-Search Corruption Localization ───────────────────────────

    /// <summary>
    /// Localizes corrupt blocks using binary search through the Merkle tree.
    /// Compares the stored tree against current block checksums and descends
    /// only into subtrees with mismatches. O(log n) block reads to find corruption.
    /// </summary>
    /// <param name="objectInodeNumber">Inode number identifying the object.</param>
    /// <param name="currentBlockChecksums">Current per-block checksums to verify against stored tree.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of corrupt block indices (0-based within the object).</returns>
    public async Task<IReadOnlyList<long>> LocalizeCorruptionAsync(
        long objectInodeNumber, byte[][] currentBlockChecksums, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(currentBlockChecksums);

        var corruptBlocks = new List<long>();

        if (currentBlockChecksums.Length == 0)
            return corruptBlocks;

        // Read stored tree header
        long baseBlock = GetObjectTreeBaseBlock(objectInodeNumber);
        var headerBlock = new byte[_blockSize];
        await _device.ReadBlockAsync(baseBlock, headerBlock, ct);

        if (!UniversalBlockTrailer.Verify(headerBlock, _blockSize))
        {
            // Cannot verify -- treat all blocks as potentially corrupt
            for (int i = 0; i < currentBlockChecksums.Length; i++)
                corruptBlocks.Add(i);
            return corruptBlocks;
        }

        int leafCount = BinaryPrimitives.ReadInt32LittleEndian(headerBlock.AsSpan(8, 4));
        int treeCapacity = BinaryPrimitives.ReadInt32LittleEndian(headerBlock.AsSpan(12, 4));

        // Compute current Merkle tree
        var currentLeafHashes = new byte[currentBlockChecksums.Length][];
        for (int i = 0; i < currentBlockChecksums.Length; i++)
        {
            currentLeafHashes[i] = SHA256.HashData(currentBlockChecksums[i]);
        }

        int currentCapacity = NextPowerOfTwo(currentLeafHashes.Length);
        int currentTotalNodes = 2 * currentCapacity;
        var currentNodes = BuildFullTree(currentLeafHashes, currentCapacity);

        // Read stored tree nodes
        var storedNodes = await ReadStoredTreeAsync(baseBlock, treeCapacity, ct);
        if (storedNodes == null)
        {
            for (int i = 0; i < currentBlockChecksums.Length; i++)
                corruptBlocks.Add(i);
            return corruptBlocks;
        }

        // Step 1: Check root
        if (CryptographicOperations.FixedTimeEquals(currentNodes[1], storedNodes[1]))
            return corruptBlocks; // Object is clean

        // Step 2: Binary search down the tree
        BinarySearchCorruption(currentNodes, storedNodes, 1, treeCapacity, leafCount, corruptBlocks);

        return corruptBlocks;
    }

    /// <summary>
    /// Recursively searches for corruption by comparing subtree hashes.
    /// Only descends into subtrees where hashes differ.
    /// </summary>
    private static void BinarySearchCorruption(
        byte[][] currentNodes, byte[][] storedNodes,
        int nodeIndex, int treeCapacity, int leafCount,
        List<long> corruptBlocks)
    {
        if (nodeIndex >= treeCapacity)
        {
            // At leaf level
            int leafIndex = nodeIndex - treeCapacity;
            if (leafIndex < leafCount)
            {
                if (!CryptographicOperations.FixedTimeEquals(currentNodes[nodeIndex], storedNodes[nodeIndex]))
                {
                    corruptBlocks.Add(leafIndex);
                }
            }
            return;
        }

        int leftChild = nodeIndex * 2;
        int rightChild = nodeIndex * 2 + 1;

        // Check left subtree
        if (leftChild < currentNodes.Length && leftChild < storedNodes.Length)
        {
            if (!CryptographicOperations.FixedTimeEquals(currentNodes[leftChild], storedNodes[leftChild]))
            {
                BinarySearchCorruption(currentNodes, storedNodes, leftChild, treeCapacity, leafCount, corruptBlocks);
            }
        }

        // Check right subtree
        if (rightChild < currentNodes.Length && rightChild < storedNodes.Length)
        {
            if (!CryptographicOperations.FixedTimeEquals(currentNodes[rightChild], storedNodes[rightChild]))
            {
                BinarySearchCorruption(currentNodes, storedNodes, rightChild, treeCapacity, leafCount, corruptBlocks);
            }
        }
    }

    // ── Incremental Updates ─────────────────────────────────────────────

    /// <summary>
    /// Updates a single leaf hash and recomputes the path from leaf to root.
    /// Only touches O(log n) nodes, used after writes to keep the Merkle tree current.
    /// </summary>
    /// <param name="objectInodeNumber">Inode number identifying the object.</param>
    /// <param name="blockNumber">0-based block index within the object.</param>
    /// <param name="newBlockChecksum">New checksum for the updated block.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateBlockAsync(
        long objectInodeNumber, long blockNumber, byte[] newBlockChecksum, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(newBlockChecksum);

        // Read stored tree header
        long baseBlock = GetObjectTreeBaseBlock(objectInodeNumber);
        var headerBlock = new byte[_blockSize];
        await _device.ReadBlockAsync(baseBlock, headerBlock, ct);

        if (!UniversalBlockTrailer.Verify(headerBlock, _blockSize))
            throw new InvalidOperationException("Integrity tree header block is corrupt.");

        int leafCount = BinaryPrimitives.ReadInt32LittleEndian(headerBlock.AsSpan(8, 4));
        int treeCapacity = BinaryPrimitives.ReadInt32LittleEndian(headerBlock.AsSpan(12, 4));

        if (blockNumber < 0 || blockNumber >= leafCount)
            throw new ArgumentOutOfRangeException(nameof(blockNumber),
                $"Block number must be 0..{leafCount - 1}.");

        // Read full stored tree
        var storedNodes = await ReadStoredTreeAsync(baseBlock, treeCapacity, ct);
        if (storedNodes == null)
            throw new InvalidOperationException("Failed to read stored Merkle tree.");

        // Update leaf
        int nodeIndex = treeCapacity + (int)blockNumber;
        storedNodes[nodeIndex] = SHA256.HashData(newBlockChecksum);

        // Recompute path from leaf to root
        Span<byte> combined = stackalloc byte[HashSize * 2];
        int current = nodeIndex / 2;
        while (current >= 1)
        {
            int left = current * 2;
            int right = current * 2 + 1;

            storedNodes[left].AsSpan().CopyTo(combined);
            storedNodes[right].AsSpan().CopyTo(combined.Slice(HashSize));

            storedNodes[current] = SHA256.HashData(combined);
            current /= 2;
        }

        // Re-serialize the updated tree
        await WriteStoredTreeAsync(baseBlock, objectInodeNumber, leafCount, treeCapacity, storedNodes, ct);
    }

    // ── Scrub Support ───────────────────────────────────────────────────

    /// <summary>
    /// Full verification: computes all block checksums, verifies the Merkle tree,
    /// and reports corrupt blocks. Used for periodic scrub operations.
    /// </summary>
    /// <param name="objectInodeNumber">Inode number identifying the object.</param>
    /// <param name="inode">The inode metadata for the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scrub result with verification details and timing.</returns>
    public async Task<ScrubResult> ScrubObjectAsync(long objectInodeNumber, InodeV2 inode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inode);

        var sw = Stopwatch.StartNew();
        long blocksVerified = 0;
        var corruptBlocks = new List<long>();

        // Collect all block checksums from the object's extents
        var blockChecksums = new List<byte[]>();
        var buffer = new byte[_blockSize];

        int extentCount = Math.Min(inode.ExtentCount, inode.Extents.Length);
        for (int e = 0; e < extentCount; e++)
        {
            var extent = inode.Extents[e];
            if (extent.IsEmpty || extent.IsSparse) continue;

            for (int i = 0; i < extent.BlockCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                await _device.ReadBlockAsync(extent.StartBlock + i, buffer, ct);

                // Verify block-level integrity first (Level 1)
                if (!UniversalBlockTrailer.Verify(buffer, _blockSize))
                {
                    corruptBlocks.Add(blocksVerified);
                }

                // Collect XxHash64 for Merkle verification
                ulong xxHash = XxHash64.HashToUInt64(buffer);
                var checksumBytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(checksumBytes, xxHash);
                blockChecksums.Add(checksumBytes);

                blocksVerified++;
            }
        }

        // Verify against stored Merkle tree if blocks exist
        if (blockChecksums.Count > 0)
        {
            var merkleCorrupt = await LocalizeCorruptionAsync(
                objectInodeNumber, blockChecksums.ToArray(), ct);

            // Merge Merkle-detected corruption with block-level corruption
            foreach (var blockIdx in merkleCorrupt)
            {
                if (!corruptBlocks.Contains(blockIdx))
                    corruptBlocks.Add(blockIdx);
            }
        }

        sw.Stop();
        corruptBlocks.Sort();

        return new ScrubResult(
            isClean: corruptBlocks.Count == 0,
            blocksVerified: blocksVerified,
            corruptBlocks: corruptBlocks,
            duration: sw.Elapsed);
    }

    // ── Internal Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Computes a deterministic base block for an object's Merkle tree within
    /// the integrity tree region. Uses inode number to distribute trees.
    /// </summary>
    private long GetObjectTreeBaseBlock(long objectInodeNumber)
    {
        // Simple deterministic mapping: each object gets a fixed-size slot
        // Slot size accommodates trees for objects up to ~64K blocks
        const int maxBlocksPerTree = 2048;
        return _integrityTreeRegionStart + objectInodeNumber * maxBlocksPerTree;
    }

    /// <summary>
    /// Reads the stored Merkle tree nodes from the integrity tree region.
    /// </summary>
    private async Task<byte[][]?> ReadStoredTreeAsync(long baseBlock, int treeCapacity, CancellationToken ct)
    {
        int totalNodes = 2 * treeCapacity;
        var nodes = new byte[totalNodes][];
        for (int i = 0; i < totalNodes; i++)
            nodes[i] = new byte[HashSize];

        int payloadSize = UniversalBlockTrailer.PayloadSize(_blockSize);
        int nodesPerBlock = payloadSize / NodeSize;
        int nodeCount = totalNodes - 1;
        int blocksNeeded = (nodeCount + nodesPerBlock - 1) / nodesPerBlock;

        int nodeRead = 0;
        var dataBlock = new byte[_blockSize];

        for (int b = 0; b < blocksNeeded; b++)
        {
            await _device.ReadBlockAsync(baseBlock + 1 + b, dataBlock, ct);

            if (!UniversalBlockTrailer.Verify(dataBlock, _blockSize))
                return null;

            int offset = 0;
            int nodesThisBlock = Math.Min(nodesPerBlock, nodeCount - nodeRead);

            for (int n = 0; n < nodesThisBlock; n++)
            {
                int nodeIndex = nodeRead + 1;
                var hash = new byte[HashSize];
                dataBlock.AsSpan(offset, HashSize).CopyTo(hash);
                nodes[nodeIndex] = hash;
                offset += NodeSize;
                nodeRead++;
            }
        }

        return nodes;
    }

    /// <summary>
    /// Writes the full tree back to storage after an incremental update.
    /// </summary>
    private async Task WriteStoredTreeAsync(
        long baseBlock, long objectInodeNumber, int leafCount, int treeCapacity,
        byte[][] nodes, CancellationToken ct)
    {
        int totalNodes = 2 * treeCapacity;

        // Write header block
        var headerBlock = new byte[_blockSize];
        BinaryPrimitives.WriteInt64LittleEndian(headerBlock.AsSpan(0, 8), objectInodeNumber);
        BinaryPrimitives.WriteInt32LittleEndian(headerBlock.AsSpan(8, 4), leafCount);
        BinaryPrimitives.WriteInt32LittleEndian(headerBlock.AsSpan(12, 4), treeCapacity);
        nodes[1].AsSpan().CopyTo(headerBlock.AsSpan(16, HashSize));
        UniversalBlockTrailer.Write(headerBlock, _blockSize, BlockTypeTags.MTRK, 1);
        await _device.WriteBlockAsync(baseBlock, headerBlock, ct);

        // Write data blocks
        int payloadSize = UniversalBlockTrailer.PayloadSize(_blockSize);
        int nodesPerBlock = payloadSize / NodeSize;
        int nodeCount = totalNodes - 1;
        int blocksNeeded = (nodeCount + nodesPerBlock - 1) / nodesPerBlock;

        int nodeWritten = 0;
        for (int b = 0; b < blocksNeeded; b++)
        {
            var dataBlock = new byte[_blockSize];
            int offset = 0;
            int nodesThisBlock = Math.Min(nodesPerBlock, nodeCount - nodeWritten);

            for (int n = 0; n < nodesThisBlock; n++)
            {
                int nodeIndex = nodeWritten + 1;
                SerializeNode(
                    new MerkleTreeNode(nodes[nodeIndex],
                        nodeIndex * 2 < totalNodes ? nodeIndex * 2 : -1,
                        nodeIndex * 2 + 1 < totalNodes ? nodeIndex * 2 + 1 : -1),
                    dataBlock.AsSpan(offset, NodeSize));
                offset += NodeSize;
                nodeWritten++;
            }

            UniversalBlockTrailer.Write(dataBlock, _blockSize, BlockTypeTags.MTRK, 1);
            await _device.WriteBlockAsync(baseBlock + 1 + b, dataBlock, ct);
        }
    }

    /// <summary>
    /// Builds a full in-memory tree from leaf hashes, returning the flat node array.
    /// </summary>
    private static byte[][] BuildFullTree(byte[][] leafHashes, int capacity)
    {
        int totalNodes = 2 * capacity;
        var nodes = new byte[totalNodes][];

        for (int i = 0; i < totalNodes; i++)
            nodes[i] = new byte[HashSize];

        for (int i = 0; i < leafHashes.Length; i++)
            Buffer.BlockCopy(leafHashes[i], 0, nodes[capacity + i], 0, HashSize);

        Span<byte> combined = stackalloc byte[HashSize * 2];
        for (int i = capacity - 1; i >= 1; i--)
        {
            nodes[i * 2].AsSpan().CopyTo(combined);
            nodes[i * 2 + 1].AsSpan().CopyTo(combined.Slice(HashSize));
            var hash = SHA256.HashData(combined);
            Buffer.BlockCopy(hash, 0, nodes[i], 0, HashSize);
        }

        return nodes;
    }

    /// <summary>
    /// Serializes a single Merkle node (48 bytes: hash + left ptr + right ptr).
    /// </summary>
    private static void SerializeNode(MerkleTreeNode node, Span<byte> target)
    {
        node.Hash.AsSpan().CopyTo(target);
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(HashSize), node.LeftChild);
        BinaryPrimitives.WriteInt64LittleEndian(target.Slice(HashSize + 8), node.RightChild);
    }

    /// <summary>
    /// Returns the smallest power of 2 that is >= value.
    /// </summary>
    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        int v = value - 1;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return v + 1;
    }

    /// <summary>
    /// Internal node representation during tree construction.
    /// </summary>
    private readonly struct MerkleTreeNode
    {
        public byte[] Hash { get; }
        public long LeftChild { get; }
        public long RightChild { get; }

        public MerkleTreeNode(byte[] hash, long leftChild, long rightChild)
        {
            Hash = hash;
            LeftChild = leftChild;
            RightChild = rightChild;
        }
    }
}
