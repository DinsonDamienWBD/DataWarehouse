using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// On-disk node for Be-tree (Buffered epsilon tree).
/// </summary>
/// <remarks>
/// Leaf nodes store sorted key-value entries (like B-tree leaves).
/// Internal nodes store sorted pivot keys, child block pointers, and a message buffer.
/// The message buffer capacity is computed as blockSize^epsilon where epsilon = 0.5
/// (square root of the block capacity in entries).
///
/// Serialization format (big-endian):
/// [IsLeaf:1][EntryCount:4][BufferCount:4][Entries...][Messages...][Pivots...][Children...]
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-02 Be-tree node")]
public sealed class BeTreeNode
{
    /// <summary>
    /// Gets or sets whether this node is a leaf (true) or internal (false).
    /// </summary>
    public bool IsLeaf { get; set; }

    /// <summary>
    /// Gets or sets the block number this node occupies on disk.
    /// </summary>
    public long BlockNumber { get; set; }

    /// <summary>
    /// Gets the sorted key-value entries for leaf nodes.
    /// </summary>
    public List<(byte[] Key, long Value)> Entries { get; } = new();

    /// <summary>
    /// Gets the sorted pivot keys for internal nodes.
    /// </summary>
    public List<byte[]> PivotKeys { get; } = new();

    /// <summary>
    /// Gets the child block pointers for internal nodes.
    /// PivotKeys.Count + 1 children (one per range between pivots, plus leftmost and rightmost).
    /// </summary>
    public List<long> ChildPointers { get; } = new();

    /// <summary>
    /// Gets the message buffer for internal nodes.
    /// Messages are batched here and flushed down when the buffer overflows.
    /// </summary>
    public List<BeTreeMessage> Messages { get; } = new();

    /// <summary>
    /// Gets the buffer capacity computed as sqrt(maxEntriesPerBlock).
    /// For a 4KB block with ~100 entries/block, this yields buffer capacity ~10.
    /// </summary>
    public int BufferCapacity { get; }

    /// <summary>
    /// Gets the maximum number of entries a leaf can hold before splitting.
    /// </summary>
    public int MaxLeafEntries { get; }

    /// <summary>
    /// Gets whether the message buffer is full and needs flushing.
    /// </summary>
    public bool IsBufferFull => Messages.Count >= BufferCapacity;

    /// <summary>
    /// Gets whether a leaf node is full and needs splitting.
    /// </summary>
    public bool IsLeafFull => IsLeaf && Entries.Count >= MaxLeafEntries;

    /// <summary>
    /// Gets whether an internal node is full (too many pivots) and needs splitting.
    /// </summary>
    public bool IsInternalFull => !IsLeaf && PivotKeys.Count >= MaxLeafEntries;

    /// <summary>
    /// Creates a new Be-tree node.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="epsilon">Epsilon parameter (default 0.5). Buffer capacity = maxEntries^epsilon.</param>
    public BeTreeNode(int blockSize, double epsilon = 0.5)
    {
        // Estimate entries per block: each entry ~40 bytes (key overhead + 8-byte value + overhead)
        // Subtract header overhead (9 bytes for IsLeaf + EntryCount + BufferCount)
        int estimatedEntrySize = 40;
        int maxEntries = Math.Max(4, (blockSize - 9) / estimatedEntrySize);
        MaxLeafEntries = maxEntries;
        BufferCapacity = Math.Max(2, (int)Math.Sqrt(maxEntries));
    }

    /// <summary>
    /// Serializes this node to a byte array for on-disk storage.
    /// Format: [IsLeaf:1][EntryCount:4][BufferCount:4]
    ///         [Entries: KeyLen:4 + Key:N + Value:8 each]
    ///         [Messages: serialized BeTreeMessage each]
    ///         [PivotCount:4][Pivots: KeyLen:4 + Key:N each]
    ///         [ChildCount:4][Children: 8 bytes each]
    /// All multi-byte integers use big-endian format.
    /// </summary>
    /// <param name="blockSize">Block size for padding.</param>
    /// <returns>Serialized byte array of exactly blockSize bytes.</returns>
    public byte[] Serialize(int blockSize)
    {
        var buffer = new byte[blockSize];
        int offset = 0;

        // Header
        buffer[offset++] = IsLeaf ? (byte)1 : (byte)0;

        int entryCount = IsLeaf ? Entries.Count : 0;
        int bufferCount = IsLeaf ? 0 : Messages.Count;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), entryCount);
        offset += 4;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), bufferCount);
        offset += 4;

        // Leaf entries
        if (IsLeaf)
        {
            for (int i = 0; i < Entries.Count && offset < blockSize - 12; i++)
            {
                var (key, value) = Entries[i];
                BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), key.Length);
                offset += 4;
                if (offset + key.Length + 8 > blockSize) break;
                key.AsSpan().CopyTo(buffer.AsSpan(offset));
                offset += key.Length;
                BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset), value);
                offset += 8;
            }
        }
        else
        {
            // Messages
            for (int i = 0; i < Messages.Count && offset < blockSize - 32; i++)
            {
                int written = Messages[i].Serialize(buffer.AsSpan(offset));
                offset += written;
            }

            // Pivots
            int pivotCount = PivotKeys.Count;
            if (offset + 4 < blockSize)
            {
                BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), pivotCount);
                offset += 4;

                for (int i = 0; i < pivotCount && offset < blockSize - 8; i++)
                {
                    var key = PivotKeys[i];
                    BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), key.Length);
                    offset += 4;
                    if (offset + key.Length > blockSize) break;
                    key.AsSpan().CopyTo(buffer.AsSpan(offset));
                    offset += key.Length;
                }
            }

            // Children
            int childCount = ChildPointers.Count;
            if (offset + 4 < blockSize)
            {
                BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), childCount);
                offset += 4;

                for (int i = 0; i < childCount && offset + 8 <= blockSize; i++)
                {
                    BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset), ChildPointers[i]);
                    offset += 8;
                }
            }
        }

        return buffer;
    }

    /// <summary>
    /// Deserializes a Be-tree node from a byte array.
    /// </summary>
    /// <param name="data">The serialized data (one full block).</param>
    /// <returns>The deserialized node.</returns>
    public static BeTreeNode Deserialize(byte[] data)
    {
        return Deserialize(data, data.Length);
    }

    /// <summary>
    /// Deserializes a Be-tree node from a byte array with explicit block size.
    /// </summary>
    /// <param name="data">The serialized data.</param>
    /// <param name="blockSize">The block size for capacity computation.</param>
    /// <returns>The deserialized node.</returns>
    public static BeTreeNode Deserialize(byte[] data, int blockSize)
    {
        var node = new BeTreeNode(blockSize);
        int offset = 0;

        node.IsLeaf = data[offset++] == 1;
        int entryCount = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
        offset += 4;
        int bufferCount = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
        offset += 4;

        if (node.IsLeaf)
        {
            for (int i = 0; i < entryCount && offset < data.Length - 4; i++)
            {
                int keyLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
                offset += 4;
                if (offset + keyLen + 8 > data.Length) break;
                var key = data.AsSpan(offset, keyLen).ToArray();
                offset += keyLen;
                long value = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(offset));
                offset += 8;
                node.Entries.Add((key, value));
            }
        }
        else
        {
            // Messages
            for (int i = 0; i < bufferCount && offset < data.Length - 4; i++)
            {
                var msg = BeTreeMessage.Deserialize(data.AsSpan(offset), out int bytesRead);
                offset += bytesRead;
                node.Messages.Add(msg);
            }

            // Pivots
            if (offset + 4 <= data.Length)
            {
                int pivotCount = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
                offset += 4;

                for (int i = 0; i < pivotCount && offset < data.Length - 4; i++)
                {
                    int keyLen = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
                    offset += 4;
                    if (offset + keyLen > data.Length) break;
                    var key = data.AsSpan(offset, keyLen).ToArray();
                    offset += keyLen;
                    node.PivotKeys.Add(key);
                }
            }

            // Children
            if (offset + 4 <= data.Length)
            {
                int childCount = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
                offset += 4;

                for (int i = 0; i < childCount && offset + 8 <= data.Length; i++)
                {
                    long childBlock = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(offset));
                    offset += 8;
                    node.ChildPointers.Add(childBlock);
                }
            }
        }

        return node;
    }
}
