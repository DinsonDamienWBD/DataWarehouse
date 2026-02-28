using System.Buffers.Binary;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// Integrity Tree region: a binary Merkle tree stored as a flat array that provides
/// cryptographic verification of any data block in O(log N) operations. Supports
/// incremental path updates when a single leaf changes, Merkle proof generation,
/// and static proof verification without requiring the full tree.
/// </summary>
/// <remarks>
/// Tree layout uses 1-indexed heap ordering:
///   - Index 1 = root
///   - Index i: left child = 2*i, right child = 2*i+1
///   - Leaf nodes for data block j start at index TreeCapacity (next power of 2 >= LeafCount)
///
/// Each node stores a 32-byte SHA-256 hash. Empty/unused leaf slots use all-zero hashes.
/// The root hash (index 1) corresponds to <see cref="IntegrityAnchor.MerkleRootHash"/>.
///
/// Serialization layout:
///   - Block 0 (header): [LeafCount:4 LE][TreeCapacity:4 LE][RootHash:32][Reserved:zero-fill][Trailer]
///   - Block 1+: packed node hashes (32 bytes each, indices 1..TotalNodes-1), each block with Trailer
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- Integrity Tree (VREG-03)")]
public sealed class IntegrityTreeRegion
{
    /// <summary>Size of each hash node in bytes (SHA-256 = 32 bytes).</summary>
    public const int HashSize = 32;

    /// <summary>Size of the header in block 0 before reserved area (LeafCount + TreeCapacity + RootHash).</summary>
    private const int HeaderFieldsSize = 4 + 4 + HashSize; // 40 bytes

    /// <summary>
    /// Pre-computed SHA-256 of 64 zero bytes (hash of two empty children).
    /// Used as the parent hash when both children are empty/unused leaves.
    /// </summary>
    private static readonly byte[] ZeroChildrenHash = SHA256.HashData(new byte[HashSize * 2]);

    /// <summary>
    /// Flat array of 32-byte hashes. Index 0 is unused (1-indexed heap).
    /// Indices 1..TotalNodes-1 hold the tree nodes.
    /// </summary>
    private readonly byte[][] _nodes;

    /// <summary>Actual number of data blocks (leaf count).</summary>
    public int LeafCount { get; }

    /// <summary>Next power of 2 >= LeafCount. Determines tree shape.</summary>
    public int TreeCapacity { get; }

    /// <summary>Total array length including unused index 0: 2 * TreeCapacity.</summary>
    public int TotalNodes => 2 * TreeCapacity;

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>
    /// Creates a new integrity tree for the specified number of data blocks.
    /// All nodes are initialized to zero hashes.
    /// </summary>
    /// <param name="leafCount">Number of data blocks to cover. Must be > 0.</param>
    /// <exception cref="ArgumentOutOfRangeException">leafCount is not positive.</exception>
    public IntegrityTreeRegion(int leafCount)
    {
        if (leafCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(leafCount), "Leaf count must be positive.");

        LeafCount = leafCount;
        TreeCapacity = NextPowerOfTwo(leafCount);
        _nodes = new byte[TotalNodes][];

        // Initialize all nodes to zero hashes
        for (int i = 0; i < TotalNodes; i++)
            _nodes[i] = new byte[HashSize];
    }

    /// <summary>
    /// Private constructor for deserialization.
    /// </summary>
    private IntegrityTreeRegion(int leafCount, int treeCapacity, byte[][] nodes)
    {
        LeafCount = leafCount;
        TreeCapacity = treeCapacity;
        _nodes = nodes;
    }

    // ── Leaf Operations ──────────────────────────────────────────────────

    /// <summary>
    /// Sets the hash for a data block leaf and incrementally updates the path to root.
    /// </summary>
    /// <param name="leafIndex">0-based data block index (0..LeafCount-1).</param>
    /// <param name="hash">32-byte SHA-256 hash of the block data.</param>
    /// <exception cref="ArgumentOutOfRangeException">leafIndex is out of range.</exception>
    /// <exception cref="ArgumentException">Hash is not exactly 32 bytes.</exception>
    public void SetLeafHash(int leafIndex, byte[] hash)
    {
        if (leafIndex < 0 || leafIndex >= LeafCount)
            throw new ArgumentOutOfRangeException(nameof(leafIndex),
                $"Leaf index must be 0..{LeafCount - 1}.");
        if (hash is null || hash.Length != HashSize)
            throw new ArgumentException($"Hash must be exactly {HashSize} bytes.", nameof(hash));

        int nodeIndex = TreeCapacity + leafIndex;
        Buffer.BlockCopy(hash, 0, _nodes[nodeIndex], 0, HashSize);
        UpdatePath(nodeIndex);
    }

    /// <summary>
    /// Computes SHA-256 of the block data and sets the leaf hash, then updates path to root.
    /// </summary>
    /// <param name="leafIndex">0-based data block index (0..LeafCount-1).</param>
    /// <param name="blockData">Raw block data to hash.</param>
    public void SetLeafHash(int leafIndex, ReadOnlySpan<byte> blockData)
    {
        var hash = SHA256.HashData(blockData);
        SetLeafHash(leafIndex, hash);
    }

    /// <summary>
    /// Incrementally recomputes parent hashes from the given node up to the root.
    /// Each parent = SHA256(leftChild || rightChild). O(log N) operations.
    /// </summary>
    /// <param name="nodeIndex">Starting node index (typically a leaf).</param>
    private void UpdatePath(int nodeIndex)
    {
        Span<byte> combined = stackalloc byte[HashSize * 2];
        int current = nodeIndex / 2; // start at parent
        while (current >= 1)
        {
            int left = current * 2;
            int right = current * 2 + 1;

            // Concatenate left || right and hash
            _nodes[left].AsSpan().CopyTo(combined);
            _nodes[right].AsSpan().CopyTo(combined.Slice(HashSize));

            var parentHash = SHA256.HashData(combined);
            Buffer.BlockCopy(parentHash, 0, _nodes[current], 0, HashSize);

            current /= 2;
        }
    }

    // ── Query Operations ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the root hash (index 1). This value matches IntegrityAnchor.MerkleRootHash.
    /// </summary>
    /// <returns>A copy of the 32-byte root hash.</returns>
    public byte[] GetRootHash()
    {
        var result = new byte[HashSize];
        Buffer.BlockCopy(_nodes[1], 0, result, 0, HashSize);
        return result;
    }

    /// <summary>
    /// Verifies a data block by comparing its SHA-256 hash against the stored leaf hash.
    /// O(1) leaf-only check.
    /// </summary>
    /// <param name="leafIndex">0-based data block index.</param>
    /// <param name="blockData">Raw block data to verify.</param>
    /// <returns>True if the computed hash matches the stored leaf hash.</returns>
    public bool VerifyBlock(int leafIndex, ReadOnlySpan<byte> blockData)
    {
        if (leafIndex < 0 || leafIndex >= LeafCount)
            throw new ArgumentOutOfRangeException(nameof(leafIndex),
                $"Leaf index must be 0..{LeafCount - 1}.");

        int nodeIndex = TreeCapacity + leafIndex;
        var computed = SHA256.HashData(blockData);
        return CryptographicOperations.FixedTimeEquals(computed, _nodes[nodeIndex]);
    }

    /// <summary>
    /// Verifies a data block AND recomputes the entire path to root to confirm consistency.
    /// O(log N) full path verification.
    /// </summary>
    /// <param name="leafIndex">0-based data block index.</param>
    /// <param name="blockData">Raw block data to verify.</param>
    /// <returns>True if the leaf hash matches AND the path to root is internally consistent.</returns>
    public bool VerifyBlockWithProof(int leafIndex, ReadOnlySpan<byte> blockData)
    {
        if (leafIndex < 0 || leafIndex >= LeafCount)
            throw new ArgumentOutOfRangeException(nameof(leafIndex),
                $"Leaf index must be 0..{LeafCount - 1}.");

        int nodeIndex = TreeCapacity + leafIndex;
        var leafHash = SHA256.HashData(blockData);

        // Check leaf
        if (!CryptographicOperations.FixedTimeEquals(leafHash, _nodes[nodeIndex]))
            return false;

        // Walk up verifying each parent
        Span<byte> combined = stackalloc byte[HashSize * 2];
        int current = nodeIndex;
        while (current > 1)
        {
            int parent = current / 2;
            int left = parent * 2;
            int right = parent * 2 + 1;

            _nodes[left].AsSpan().CopyTo(combined);
            _nodes[right].AsSpan().CopyTo(combined.Slice(HashSize));

            var expectedParent = SHA256.HashData(combined);
            if (!CryptographicOperations.FixedTimeEquals(expectedParent, _nodes[parent]))
                return false;

            current = parent;
        }

        return true;
    }

    // ── Merkle Proofs ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the sibling hashes from leaf to root (the Merkle proof).
    /// Array length = log2(TreeCapacity).
    /// </summary>
    /// <param name="leafIndex">0-based data block index.</param>
    /// <returns>Array of sibling hashes from leaf level up to root's child level.</returns>
    public byte[][] GetProof(int leafIndex)
    {
        if (leafIndex < 0 || leafIndex >= LeafCount)
            throw new ArgumentOutOfRangeException(nameof(leafIndex),
                $"Leaf index must be 0..{LeafCount - 1}.");

        int depth = Log2(TreeCapacity);
        var proof = new byte[depth][];

        int nodeIndex = TreeCapacity + leafIndex;
        for (int i = 0; i < depth; i++)
        {
            // Sibling is the other child of the same parent
            int sibling = nodeIndex ^ 1; // flip last bit
            proof[i] = new byte[HashSize];
            Buffer.BlockCopy(_nodes[sibling], 0, proof[i], 0, HashSize);
            nodeIndex /= 2; // move to parent
        }

        return proof;
    }

    /// <summary>
    /// Statically verifies a Merkle proof without requiring the full tree.
    /// Reconstructs the root from the leaf hash and sibling hashes, then
    /// compares with the expected root.
    /// </summary>
    /// <param name="leafHash">SHA-256 hash of the leaf data (32 bytes).</param>
    /// <param name="proof">Sibling hashes from leaf to root.</param>
    /// <param name="leafIndex">0-based leaf index within the tree.</param>
    /// <param name="treeCapacity">Tree capacity (must be power of 2).</param>
    /// <param name="expectedRoot">Expected root hash (32 bytes).</param>
    /// <returns>True if the proof reconstructs to the expected root.</returns>
    public static bool VerifyProof(byte[] leafHash, byte[][] proof, int leafIndex,
                                    int treeCapacity, byte[] expectedRoot)
    {
        if (leafHash is null || leafHash.Length != HashSize)
            throw new ArgumentException($"Leaf hash must be exactly {HashSize} bytes.", nameof(leafHash));
        if (expectedRoot is null || expectedRoot.Length != HashSize)
            throw new ArgumentException($"Expected root must be exactly {HashSize} bytes.", nameof(expectedRoot));
        if (proof is null)
            throw new ArgumentNullException(nameof(proof));

        int depth = Log2(treeCapacity);
        if (proof.Length != depth)
            return false;

        var currentHash = new byte[HashSize];
        Buffer.BlockCopy(leafHash, 0, currentHash, 0, HashSize);

        int nodeIndex = treeCapacity + leafIndex;
        Span<byte> combined = stackalloc byte[HashSize * 2];

        for (int i = 0; i < depth; i++)
        {
            if (proof[i] is null || proof[i].Length != HashSize)
                return false;

            // Determine if current node is left or right child
            if ((nodeIndex & 1) == 0)
            {
                // Current is left child, sibling is right
                currentHash.AsSpan().CopyTo(combined);
                proof[i].AsSpan().CopyTo(combined.Slice(HashSize));
            }
            else
            {
                // Current is right child, sibling is left
                proof[i].AsSpan().CopyTo(combined);
                currentHash.AsSpan().CopyTo(combined.Slice(HashSize));
            }

            currentHash = SHA256.HashData(combined);
            nodeIndex /= 2;
        }

        return CryptographicOperations.FixedTimeEquals(currentHash, expectedRoot);
    }

    // ── Bulk Operations ──────────────────────────────────────────────────

    /// <summary>
    /// Sets all leaves from the provided hashes and rebuilds the entire tree bottom-up. O(N).
    /// </summary>
    /// <param name="leafHashes">Array of 32-byte hashes, one per data block. Length must equal LeafCount.</param>
    /// <exception cref="ArgumentException">Incorrect number of leaf hashes provided.</exception>
    public void BuildFromLeaves(byte[][] leafHashes)
    {
        if (leafHashes is null)
            throw new ArgumentNullException(nameof(leafHashes));
        if (leafHashes.Length != LeafCount)
            throw new ArgumentException($"Expected {LeafCount} leaf hashes, got {leafHashes.Length}.", nameof(leafHashes));

        // Set all provided leaves
        for (int i = 0; i < LeafCount; i++)
        {
            var hash = leafHashes[i];
            if (hash is null || hash.Length != HashSize)
                throw new ArgumentException($"Leaf hash at index {i} must be exactly {HashSize} bytes.", nameof(leafHashes));

            int nodeIndex = TreeCapacity + i;
            Buffer.BlockCopy(hash, 0, _nodes[nodeIndex], 0, HashSize);
        }

        // Zero out unused leaf slots (already zeroed from constructor, but
        // ensure consistency if called multiple times)
        for (int i = LeafCount; i < TreeCapacity; i++)
        {
            int nodeIndex = TreeCapacity + i;
            Array.Clear(_nodes[nodeIndex], 0, HashSize);
        }

        // Build internal nodes bottom-up
        Span<byte> combined = stackalloc byte[HashSize * 2];
        for (int i = TreeCapacity - 1; i >= 1; i--)
        {
            int left = i * 2;
            int right = i * 2 + 1;

            _nodes[left].AsSpan().CopyTo(combined);
            _nodes[right].AsSpan().CopyTo(combined.Slice(HashSize));

            var parentHash = SHA256.HashData(combined);
            Buffer.BlockCopy(parentHash, 0, _nodes[i], 0, HashSize);
        }
    }

    // ── Serialization ────────────────────────────────────────────────────

    /// <summary>
    /// Computes how many blocks are needed to store this tree.
    /// 1 (header) + ceil((TotalNodes - 1) * HashSize / payloadSize).
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Total block count for the MTRK region.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int nodeDataBytes = (TotalNodes - 1) * HashSize; // nodes 1..TotalNodes-1
        int dataBlocks = (nodeDataBytes + payloadSize - 1) / payloadSize;
        return 1 + dataBlocks; // 1 header + data blocks
    }

    /// <summary>
    /// Serializes the tree into a multi-block buffer. Each block carries a
    /// <see cref="UniversalBlockTrailer"/> with <see cref="BlockTypeTags.MTRK"/>.
    /// </summary>
    /// <param name="buffer">Target buffer, must be at least RequiredBlocks * blockSize bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int totalBlocks = RequiredBlocks(blockSize);
        int totalSize = totalBlocks * blockSize;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        // Clear entire buffer
        buffer.Slice(0, totalSize).Clear();

        // ── Block 0 (header): [LeafCount:4][TreeCapacity:4][RootHash:32][Reserved][Trailer] ──
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(block0, LeafCount);
        BinaryPrimitives.WriteInt32LittleEndian(block0.Slice(4), TreeCapacity);
        _nodes[1].AsSpan().CopyTo(block0.Slice(8, HashSize)); // root hash copy
        UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.MTRK, Generation);

        // ── Data blocks (block 1+): packed node hashes, indices 1..TotalNodes-1 ──
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int nodeCount = TotalNodes - 1; // nodes to write (1-indexed, skip 0)
        int nodeWritten = 0;

        for (int b = 1; b < totalBlocks; b++)
        {
            var block = buffer.Slice(b * blockSize, blockSize);
            int offset = 0;
            int nodesThisBlock = Math.Min((payloadSize) / HashSize, nodeCount - nodeWritten);

            for (int i = 0; i < nodesThisBlock; i++)
            {
                int nodeIndex = nodeWritten + 1; // 1-indexed
                _nodes[nodeIndex].AsSpan().CopyTo(block.Slice(offset, HashSize));
                offset += HashSize;
                nodeWritten++;
            }

            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.MTRK, Generation);
        }
    }

    /// <summary>
    /// Deserializes a multi-block buffer into an IntegrityTreeRegion, verifying all block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the serialized tree blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer.</param>
    /// <returns>A populated <see cref="IntegrityTreeRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Block trailer verification fails.</exception>
    public static IntegrityTreeRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
    {
        int totalSize = blockSize * blockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");
        if (blockCount < 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "Must have at least 1 block.");

        // ── Verify and read header (block 0) ──
        var block0 = buffer.Slice(0, blockSize);
        if (!UniversalBlockTrailer.Verify(block0, blockSize))
            throw new InvalidDataException("Integrity Tree header block trailer verification failed.");

        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int leafCount = BinaryPrimitives.ReadInt32LittleEndian(block0);
        int treeCapacity = BinaryPrimitives.ReadInt32LittleEndian(block0.Slice(4));

        if (leafCount <= 0)
            throw new InvalidDataException($"Invalid leaf count: {leafCount}.");
        const int MaxLeafCount = 16_777_216; // 16M leaves is plenty for any practical tree
        if (leafCount > MaxLeafCount)
            throw new InvalidDataException($"Leaf count {leafCount} exceeds maximum {MaxLeafCount}.");
        if (treeCapacity != NextPowerOfTwo(leafCount))
            throw new InvalidDataException($"Tree capacity {treeCapacity} is inconsistent with leaf count {leafCount}.");

        int totalNodes = 2 * treeCapacity;
        var nodes = new byte[totalNodes][];
        for (int i = 0; i < totalNodes; i++)
            nodes[i] = new byte[HashSize];

        // ── Read node hashes from data blocks (block 1+) ──
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int nodeCount = totalNodes - 1; // nodes 1..totalNodes-1
        int nodeRead = 0;

        for (int b = 1; b < blockCount; b++)
        {
            var block = buffer.Slice(b * blockSize, blockSize);
            if (!UniversalBlockTrailer.Verify(block, blockSize))
                throw new InvalidDataException($"Integrity Tree data block {b} trailer verification failed.");

            int offset = 0;
            int nodesThisBlock = Math.Min(payloadSize / HashSize, nodeCount - nodeRead);

            for (int i = 0; i < nodesThisBlock; i++)
            {
                int nodeIndex = nodeRead + 1; // 1-indexed
                block.Slice(offset, HashSize).CopyTo(nodes[nodeIndex]);
                offset += HashSize;
                nodeRead++;
            }
        }

        var region = new IntegrityTreeRegion(leafCount, treeCapacity, nodes)
        {
            Generation = trailer.GenerationNumber
        };
        return region;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the smallest power of 2 that is >= value.
    /// </summary>
    internal static int NextPowerOfTwo(int value)
    {
        if (value <= 1) return 1;

        // Use bit manipulation for efficient computation
        int v = value - 1;
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return v + 1;
    }

    /// <summary>
    /// Returns floor(log2(value)) for positive powers of 2.
    /// </summary>
    private static int Log2(int value)
    {
        int result = 0;
        int v = value;
        while (v > 1)
        {
            v >>= 1;
            result++;
        }
        return result;
    }
}
