using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace DataWarehouse.SDK.VirtualDiskEngine.Journal;

/// <summary>
/// Types of journal entries in the write-ahead log.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-03 WAL)")]
public enum JournalEntryType : byte
{
    /// <summary>Marks the beginning of a transaction.</summary>
    BeginTransaction = 1,

    /// <summary>Records a data block write operation.</summary>
    BlockWrite = 2,

    /// <summary>Records a block deallocation operation.</summary>
    BlockFree = 3,

    /// <summary>Records an inode update operation.</summary>
    InodeUpdate = 4,

    /// <summary>Records a B-Tree structural modification.</summary>
    BTreeModify = 5,

    /// <summary>Marks the successful commit of a transaction.</summary>
    CommitTransaction = 6,

    /// <summary>Marks the abortion of a transaction.</summary>
    AbortTransaction = 7,

    /// <summary>Marks a checkpoint boundary.</summary>
    Checkpoint = 8
}

/// <summary>
/// Represents a single entry in the write-ahead log.
/// Contains before/after images for crash recovery and redo/undo operations.
/// Note: Mutable properties to support sequence number assignment during append.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-03 WAL)")]
public sealed class JournalEntry
{
    /// <summary>
    /// Fixed header size in bytes (before variable-length data).
    /// Layout: [SequenceNumber:8][TransactionId:8][Type:1][TargetBlock:8][DataLength:4][BeforeLength:4][AfterLength:4][Checksum:8]
    /// </summary>
    public const int HeaderSize = 8 + 8 + 1 + 8 + 4 + 4 + 4 + 8;

    /// <summary>
    /// Monotonically increasing sequence number (never reused).
    /// </summary>
    public long SequenceNumber { get; set; }

    /// <summary>
    /// Transaction identifier (groups related entries).
    /// </summary>
    public long TransactionId { get; set; }

    /// <summary>
    /// Type of journal entry.
    /// </summary>
    public JournalEntryType Type { get; set; }

    /// <summary>
    /// Target block number affected by this entry (-1 for transaction markers).
    /// </summary>
    public long TargetBlockNumber { get; set; }

    /// <summary>
    /// Original block data before modification (for undo). Null for transaction markers.
    /// </summary>
    public byte[]? BeforeImage { get; set; }

    /// <summary>
    /// New block data after modification (for redo). Null for transaction markers and BlockFree.
    /// </summary>
    public byte[]? AfterImage { get; set; }

    /// <summary>
    /// XxHash64 checksum of all fields except checksum itself.
    /// </summary>
    public ulong Checksum { get; set; }

    /// <summary>
    /// Gets the total length of before and after images.
    /// </summary>
    public int DataLength => (BeforeImage?.Length ?? 0) + (AfterImage?.Length ?? 0);

    /// <summary>
    /// Serializes this journal entry to a buffer.
    /// </summary>
    /// <param name="buffer">Destination buffer. Must be at least GetSerializedSize() bytes.</param>
    /// <returns>Number of bytes written.</returns>
    /// <exception cref="ArgumentException">Buffer too small.</exception>
    public int Serialize(Span<byte> buffer)
    {
        int beforeLen = BeforeImage?.Length ?? 0;
        int afterLen = AfterImage?.Length ?? 0;
        int requiredSize = HeaderSize + beforeLen + afterLen;

        if (buffer.Length < requiredSize)
        {
            throw new ArgumentException($"Buffer too small: need {requiredSize} bytes, got {buffer.Length}", nameof(buffer));
        }

        int offset = 0;

        // Write header fields
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), SequenceNumber);
        offset += 8;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), TransactionId);
        offset += 8;

        buffer[offset++] = (byte)Type;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), TargetBlockNumber);
        offset += 8;

        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset, 4), beforeLen + afterLen);
        offset += 4;

        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset, 4), beforeLen);
        offset += 4;

        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset, 4), afterLen);
        offset += 4;

        // Compute checksum over header fields + data (excluding checksum field itself)
        Span<byte> checksumData = stackalloc byte[offset + beforeLen + afterLen];
        buffer.Slice(0, offset).CopyTo(checksumData);

        if (beforeLen > 0)
        {
            BeforeImage.AsSpan().CopyTo(checksumData.Slice(offset));
        }
        if (afterLen > 0)
        {
            AfterImage.AsSpan().CopyTo(checksumData.Slice(offset + beforeLen));
        }

        ulong checksum = XxHash64.HashToUInt64(checksumData);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(offset, 8), checksum);
        offset += 8;

        // Write data images
        if (beforeLen > 0)
        {
            BeforeImage.AsSpan().CopyTo(buffer.Slice(offset, beforeLen));
            offset += beforeLen;
        }

        if (afterLen > 0)
        {
            AfterImage.AsSpan().CopyTo(buffer.Slice(offset, afterLen));
            offset += afterLen;
        }

        return offset;
    }

    /// <summary>
    /// Deserializes a journal entry from a buffer with checksum validation.
    /// </summary>
    /// <param name="buffer">Source buffer containing serialized entry.</param>
    /// <param name="entry">Deserialized entry (only valid if return is true).</param>
    /// <param name="bytesRead">Number of bytes read from buffer.</param>
    /// <returns>True if deserialization and checksum validation succeeded, false otherwise.</returns>
    public static bool Deserialize(ReadOnlySpan<byte> buffer, out JournalEntry entry, out int bytesRead)
    {
        entry = null!;
        bytesRead = 0;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        int offset = 0;

        long sequenceNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        long transactionId = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        var type = (JournalEntryType)buffer[offset++];

        long targetBlockNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        int totalDataLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
        offset += 4;

        int beforeLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
        offset += 4;

        int afterLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
        offset += 4;

        ulong storedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(offset, 8));
        offset += 8;

        // Validate data length consistency
        if (beforeLen + afterLen != totalDataLength || totalDataLength < 0 || beforeLen < 0 || afterLen < 0)
        {
            return false;
        }

        // Check if buffer contains full entry
        int totalSize = HeaderSize + totalDataLength;
        if (buffer.Length < totalSize)
        {
            return false;
        }

        // Read data images
        byte[]? beforeImage = null;
        if (beforeLen > 0)
        {
            beforeImage = new byte[beforeLen];
            buffer.Slice(offset, beforeLen).CopyTo(beforeImage);
            offset += beforeLen;
        }

        byte[]? afterImage = null;
        if (afterLen > 0)
        {
            afterImage = new byte[afterLen];
            buffer.Slice(offset, afterLen).CopyTo(afterImage);
            offset += afterLen;
        }

        // Verify checksum
        Span<byte> checksumData = stackalloc byte[HeaderSize - 8 + totalDataLength];
        buffer.Slice(0, HeaderSize - 8).CopyTo(checksumData);
        if (totalDataLength > 0)
        {
            buffer.Slice(HeaderSize, totalDataLength).CopyTo(checksumData.Slice(HeaderSize - 8));
        }

        ulong computedChecksum = XxHash64.HashToUInt64(checksumData);
        if (computedChecksum != storedChecksum)
        {
            return false;
        }

        bytesRead = totalSize;
        entry = new JournalEntry
        {
            SequenceNumber = sequenceNumber,
            TransactionId = transactionId,
            Type = type,
            TargetBlockNumber = targetBlockNumber,
            BeforeImage = beforeImage,
            AfterImage = afterImage,
            Checksum = storedChecksum
        };

        return true;
    }

    /// <summary>
    /// Calculates the serialized size of this entry in bytes.
    /// </summary>
    public int GetSerializedSize() => HeaderSize + DataLength;
}
