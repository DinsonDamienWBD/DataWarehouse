using System.Buffers.Binary;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A single immutable audit log entry with SHA-256 hash chaining to the preceding entry.
/// </summary>
/// <remarks>
/// Serialized layout:
/// [SequenceNumber:8 LE][TimestampUtcTicks:8 LE][ActorId:16][EventType:2 LE]
/// [TargetObjectId:16][PreviousEntryHash:32][DetailsLength:4 LE][Details:variable]
/// Fixed overhead: 8+8+16+2+16+32+4 = 86 bytes.
/// </remarks>
[SdkCompatibility("6.0.0")]
public readonly record struct AuditLogEntry
{
    /// <summary>Fixed-size portion of the serialized entry (excluding variable-length Details).</summary>
    public const int FixedSize = 86;

    /// <summary>Maximum allowed length of the Details payload.</summary>
    public const int MaxDetailsLength = 256;

    /// <summary>SHA-256 hash length in bytes.</summary>
    public const int HashSize = 32;

    /// <summary>Monotonically increasing, gapless sequence number.</summary>
    public long SequenceNumber { get; init; }

    /// <summary>UTC ticks when the event occurred.</summary>
    public long TimestampUtcTicks { get; init; }

    /// <summary>Identity of the actor who performed the action.</summary>
    public Guid ActorId { get; init; }

    /// <summary>
    /// Operation type: 0=Read, 1=Write, 2=Delete, 3=Create, 4=Modify,
    /// 5=Access, 6=PolicyChange, 7=AuthEvent, 8=SystemEvent.
    /// </summary>
    public ushort EventType { get; init; }

    /// <summary>Object the action was performed on.</summary>
    public Guid TargetObjectId { get; init; }

    /// <summary>
    /// 32-byte SHA-256 hash of the preceding entry's serialized bytes.
    /// All-zero for the first entry in the chain.
    /// </summary>
    public byte[] PreviousEntryHash { get; init; }

    /// <summary>Variable-length event details (max 256 bytes).</summary>
    public byte[] Details { get; init; }

    /// <summary>Total serialized size of this entry including variable-length details.</summary>
    public int SerializedSize => FixedSize + (Details?.Length ?? 0);

    /// <summary>Writes this entry into the buffer at the given offset.</summary>
    /// <param name="buffer">Target buffer.</param>
    /// <param name="offset">Byte offset to start writing.</param>
    /// <returns>Number of bytes written.</returns>
    internal int WriteTo(Span<byte> buffer, int offset)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), SequenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 8), TimestampUtcTicks);
        ActorId.TryWriteBytes(buffer.Slice(offset + 16, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset + 32), EventType);
        TargetObjectId.TryWriteBytes(buffer.Slice(offset + 34, 16));

        var hashSpan = PreviousEntryHash ?? new byte[HashSize];
        hashSpan.AsSpan(0, HashSize).CopyTo(buffer.Slice(offset + 50, HashSize));

        int detailsLen = Details?.Length ?? 0;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset + 82), detailsLen);
        if (detailsLen > 0)
            Details.AsSpan(0, detailsLen).CopyTo(buffer.Slice(offset + FixedSize, detailsLen));

        return FixedSize + detailsLen;
    }

    /// <summary>Reads an entry from the buffer at the given offset.</summary>
    /// <param name="buffer">Source buffer.</param>
    /// <param name="offset">Byte offset to start reading.</param>
    /// <param name="bytesRead">Number of bytes consumed.</param>
    /// <returns>The deserialized entry.</returns>
    internal static AuditLogEntry ReadFrom(ReadOnlySpan<byte> buffer, int offset, out int bytesRead)
    {
        long seqNum = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 8));
        var actorId = new Guid(buffer.Slice(offset + 16, 16));
        ushort eventType = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 32));
        var targetId = new Guid(buffer.Slice(offset + 34, 16));
        byte[] prevHash = buffer.Slice(offset + 50, HashSize).ToArray();
        int detailsLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset + 82));
        if (detailsLen < 0 || detailsLen > MaxDetailsLength)
            throw new InvalidDataException($"AuditLogEntry detailsLen {detailsLen} exceeds maximum {MaxDetailsLength}.");
        if (offset + FixedSize + detailsLen > buffer.Length)
            throw new InvalidDataException($"AuditLogEntry details at offset {offset} extends beyond buffer (need {FixedSize + detailsLen}, have {buffer.Length - offset}).");
        byte[] details = detailsLen > 0
            ? buffer.Slice(offset + FixedSize, detailsLen).ToArray()
            : Array.Empty<byte>();

        bytesRead = FixedSize + detailsLen;

        return new AuditLogEntry
        {
            SequenceNumber = seqNum,
            TimestampUtcTicks = timestamp,
            ActorId = actorId,
            EventType = eventType,
            TargetObjectId = targetId,
            PreviousEntryHash = prevHash,
            Details = details
        };
    }
}

/// <summary>
/// Audit Log Region: an append-only, SHA-256 hash-chained log of all operations.
/// This region is deliberately designed with no truncation, deletion, or modification API.
/// The append-only constraint is enforced at the API level to ensure audit integrity
/// even against privileged processes.
/// Serialized using <see cref="BlockTypeTags.ALOG"/> type tag.
/// </summary>
/// <remarks>
/// Header block layout:
/// [EntryCount:8 LE][NextSequenceNumber:8 LE][AuditLogEntry entries...]
/// [UniversalBlockTrailer]
///
/// Entries are variable-length and overflow to additional blocks as needed.
/// Each entry's PreviousEntryHash is the SHA-256 of the preceding entry's serialized bytes,
/// creating a tamper-evident chain that can be verified end-to-end.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Audit Log (VREG-14)")]
public sealed class AuditLogRegion
{
    /// <summary>Size of the fixed header: [EntryCount:8][NextSequenceNumber:8] = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    private readonly object _entriesLock = new();
    private readonly List<AuditLogEntry> _entries = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of entries in the audit log.</summary>
    public long EntryCount => _entries.Count;

    /// <summary>Next expected sequence number (equals current entry count).</summary>
    public long NextSequenceNumber { get; private set; }

    /// <summary>
    /// Appends a new entry to the audit log, computing the SHA-256 hash chain
    /// from the previous entry's serialized bytes.
    /// </summary>
    /// <param name="actorId">Identity of the actor performing the action.</param>
    /// <param name="eventType">Operation type code.</param>
    /// <param name="targetObjectId">Object the action targets.</param>
    /// <param name="details">Variable-length event details (max 256 bytes).</param>
    /// <returns>The created audit log entry.</returns>
    /// <exception cref="ArgumentException">Thrown if details exceeds 256 bytes.</exception>
    public AuditLogEntry Append(Guid actorId, ushort eventType, Guid targetObjectId, byte[] details)
    {
        if (details is not null && details.Length > AuditLogEntry.MaxDetailsLength)
            throw new ArgumentException(
                $"Details must not exceed {AuditLogEntry.MaxDetailsLength} bytes. Got {details.Length}.",
                nameof(details));

        lock (_entriesLock)
        {
            byte[] previousHash;
            if (_entries.Count == 0)
            {
                previousHash = new byte[AuditLogEntry.HashSize]; // all-zero for first entry
            }
            else
            {
                var lastEntry = _entries[_entries.Count - 1];
                previousHash = ComputeEntryHash(lastEntry);
            }

            var entry = new AuditLogEntry
            {
                SequenceNumber = NextSequenceNumber,
                TimestampUtcTicks = DateTime.UtcNow.Ticks,
                ActorId = actorId,
                EventType = eventType,
                TargetObjectId = targetObjectId,
                PreviousEntryHash = previousHash,
                Details = details ?? Array.Empty<byte>()
            };

            _entries.Add(entry);
            NextSequenceNumber++;
            return entry;
        }
    }

    /// <summary>
    /// Returns the entry at the specified sequence number in O(1) time.
    /// </summary>
    /// <param name="sequenceNumber">Zero-based sequence number.</param>
    /// <returns>The audit log entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if sequence number is out of range.</exception>
    public AuditLogEntry GetEntry(long sequenceNumber)
    {
        if (sequenceNumber < 0 || sequenceNumber >= _entries.Count)
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber),
                $"Sequence number must be 0..{_entries.Count - 1}.");
        return _entries[(int)sequenceNumber];
    }

    /// <summary>
    /// Returns a range of entries starting at the specified sequence number.
    /// </summary>
    /// <param name="fromSequence">Starting sequence number (inclusive).</param>
    /// <param name="count">Maximum number of entries to return.</param>
    /// <returns>A read-only list of entries in the range.</returns>
    public IReadOnlyList<AuditLogEntry> GetEntries(long fromSequence, int count)
    {
        if (fromSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(fromSequence), "From sequence must be non-negative.");
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be non-negative.");

        int start = (int)Math.Min(fromSequence, _entries.Count);
        int available = _entries.Count - start;
        int take = Math.Min(count, available);
        return _entries.GetRange(start, take).AsReadOnly();
    }

    /// <summary>
    /// Returns a snapshot of all entries in the audit log.
    /// </summary>
    public IReadOnlyList<AuditLogEntry> GetAllEntries() => _entries.AsReadOnly();

    /// <summary>
    /// Verifies the integrity of the entire hash chain by walking all entries
    /// and recomputing each PreviousEntryHash from the preceding entry's serialized bytes.
    /// </summary>
    /// <returns>True if the chain is intact; false if any hash mismatch is detected.</returns>
    public bool VerifyChainIntegrity()
    {
        if (_entries.Count == 0)
            return true;

        // First entry must have all-zero PreviousEntryHash
        var first = _entries[0];
        if (first.PreviousEntryHash is null || first.PreviousEntryHash.Length != AuditLogEntry.HashSize)
            return false;
        for (int i = 0; i < AuditLogEntry.HashSize; i++)
        {
            if (first.PreviousEntryHash[i] != 0)
                return false;
        }

        // Each subsequent entry's PreviousEntryHash must equal SHA-256 of the prior entry
        for (int i = 1; i < _entries.Count; i++)
        {
            var current = _entries[i];
            var previous = _entries[i - 1];
            byte[] expectedHash = ComputeEntryHash(previous);

            if (current.PreviousEntryHash is null
                || current.PreviousEntryHash.Length != AuditLogEntry.HashSize
                || !current.PreviousEntryHash.AsSpan().SequenceEqual(expectedHash))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verifies a single entry's hash chain link by checking its PreviousEntryHash
    /// against the hash of the preceding entry.
    /// </summary>
    /// <param name="sequenceNumber">Sequence number of the entry to verify.</param>
    /// <returns>True if the entry's hash link is valid.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if sequence number is out of range.</exception>
    public bool VerifyEntryIntegrity(long sequenceNumber)
    {
        if (sequenceNumber < 0 || sequenceNumber >= _entries.Count)
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber),
                $"Sequence number must be 0..{_entries.Count - 1}.");

        var entry = _entries[(int)sequenceNumber];

        if (sequenceNumber == 0)
        {
            // First entry must have all-zero hash
            if (entry.PreviousEntryHash is null || entry.PreviousEntryHash.Length != AuditLogEntry.HashSize)
                return false;
            for (int i = 0; i < AuditLogEntry.HashSize; i++)
            {
                if (entry.PreviousEntryHash[i] != 0)
                    return false;
            }
            return true;
        }

        var previous = _entries[(int)(sequenceNumber - 1)];
        byte[] expectedHash = ComputeEntryHash(previous);
        return entry.PreviousEntryHash is not null
            && entry.PreviousEntryHash.Length == AuditLogEntry.HashSize
            && entry.PreviousEntryHash.AsSpan().SequenceEqual(expectedHash);
    }

    /// <summary>
    /// Computes the number of blocks required to serialize this region.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Total number of blocks required.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int totalEntryBytes = 0;
        foreach (var entry in _entries)
            totalEntryBytes += entry.SerializedSize;

        int availableInBlock0 = payloadSize - HeaderFieldsSize;
        if (totalEntryBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalEntryBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the audit log region into blocks with <see cref="UniversalBlockTrailer"/>
    /// on each block.
    /// </summary>
    /// <param name="buffer">Target buffer (must be at least RequiredBlocks * blockSize bytes).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int requiredBlocks = RequiredBlocks(blockSize);
        int totalSize = blockSize * requiredBlocks;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        buffer.Slice(0, totalSize).Clear();

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        // Block 0 header: [EntryCount:8][NextSequenceNumber:8]
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt64LittleEndian(block0, _entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(block0.Slice(8), NextSequenceNumber);

        int offset = HeaderFieldsSize;
        int entryIndex = 0;
        int currentBlock = 0;

        while (entryIndex < _entries.Count)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;
            int startOffset = currentBlock == 0 ? HeaderFieldsSize : 0;

            if (currentBlock > 0)
                offset = 0;

            while (entryIndex < _entries.Count)
            {
                int entrySize = _entries[entryIndex].SerializedSize;
                if (offset + entrySize > blockPayloadEnd)
                    break;

                _entries[entryIndex].WriteTo(block, offset);
                offset += entrySize;
                entryIndex++;
            }

            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.ALOG, Generation);

            if (entryIndex < _entries.Count)
            {
                currentBlock++;
                offset = 0;
            }
        }

        // Write trailer for block 0 if no entries
        if (_entries.Count == 0)
            UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.ALOG, Generation);

        // Write trailers for any remaining blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.ALOG, Generation);
        }
    }

    /// <summary>
    /// Deserializes an audit log region from blocks, verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="AuditLogRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static AuditLogRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
    {
        int totalSize = blockSize * blockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");
        if (blockCount < 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "At least one block is required.");

        // Verify all block trailers
        for (int blk = 0; blk < blockCount; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            if (!UniversalBlockTrailer.Verify(block, blockSize))
                throw new InvalidDataException($"Audit log region block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        long entryCount = BinaryPrimitives.ReadInt64LittleEndian(block0);
        long nextSeqNum = BinaryPrimitives.ReadInt64LittleEndian(block0.Slice(8));

        var region = new AuditLogRegion
        {
            Generation = trailer.GenerationNumber,
            NextSequenceNumber = nextSeqNum
        };

        // Read entries across blocks
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        long entriesRead = 0;

        while (entriesRead < entryCount)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            while (entriesRead < entryCount && offset + AuditLogEntry.FixedSize <= blockPayloadEnd)
            {
                var entry = AuditLogEntry.ReadFrom(block, offset, out int bytesRead);
                region._entries.Add(entry);
                offset += bytesRead;
                entriesRead++;
            }

            currentBlock++;
            offset = 0;
        }

        return region;
    }

    /// <summary>
    /// Computes the SHA-256 hash of an entry's serialized bytes.
    /// </summary>
    private static byte[] ComputeEntryHash(AuditLogEntry entry)
    {
        int size = entry.SerializedSize;
        byte[] temp = new byte[size];
        entry.WriteTo(temp.AsSpan(), 0);
        return SHA256.HashData(temp);
    }
}
