using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// Status of a key slot in the encryption header.
/// </summary>
public enum KeySlotStatus : byte
{
    /// <summary>Slot is empty / unused.</summary>
    Empty = 0,

    /// <summary>Slot holds an active encryption key.</summary>
    Active = 1,

    /// <summary>Slot holds a retired key (kept for decryption of old data).</summary>
    Retired = 2,

    /// <summary>Slot holds a compromised key (must not be used for encryption).</summary>
    Compromised = 3
}

/// <summary>
/// Reason for a key rotation event.
/// </summary>
public enum KeyRotationReason : ushort
{
    /// <summary>Scheduled periodic rotation.</summary>
    Scheduled = 0,

    /// <summary>Key was compromised.</summary>
    Compromised = 1,

    /// <summary>Policy change required rotation.</summary>
    PolicyChange = 2,

    /// <summary>Manual rotation by administrator.</summary>
    Manual = 3
}

/// <summary>
/// A single key slot in the encryption header. Each slot holds wrapped key material,
/// KDF parameters, and lifecycle timestamps. Total serialized size: 124 bytes.
/// </summary>
public readonly struct KeySlot : IEquatable<KeySlot>
{
    /// <summary>Serialized size per slot in bytes.</summary>
    public const int SerializedSize = 124;

    /// <summary>Fixed size of the wrapped key field.</summary>
    public const int WrappedKeySize = 64;

    /// <summary>Fixed size of the KDF salt field.</summary>
    public const int KeySaltSize = 32;

    /// <summary>Index of this slot (0..62).</summary>
    public byte SlotIndex { get; }

    /// <summary>Status of the key in this slot.</summary>
    public byte Status { get; }

    /// <summary>Algorithm identifier (1=AES-256-GCM, 2=ChaCha20-Poly1305, etc.).</summary>
    public ushort AlgorithmId { get; }

    /// <summary>Encrypted key material, zero-padded to 64 bytes.</summary>
    public byte[] WrappedKey { get; }

    /// <summary>KDF salt (32 bytes).</summary>
    public byte[] KeySalt { get; }

    /// <summary>KDF iteration count (PBKDF2/Argon2).</summary>
    public uint KdfIterations { get; }

    /// <summary>KDF algorithm (0=PBKDF2-SHA256, 1=Argon2id, 2=scrypt).</summary>
    public ushort KdfAlgorithmId { get; }

    /// <summary>Reserved field for future use.</summary>
    public ushort Reserved { get; }

    /// <summary>UTC ticks when this slot was created.</summary>
    public long CreatedUtcTicks { get; }

    /// <summary>UTC ticks when this slot was retired (0 if still active).</summary>
    public long RetiredUtcTicks { get; }

    public KeySlot(
        byte slotIndex,
        byte status,
        ushort algorithmId,
        byte[] wrappedKey,
        byte[] keySalt,
        uint kdfIterations,
        ushort kdfAlgorithmId,
        ushort reserved,
        long createdUtcTicks,
        long retiredUtcTicks)
    {
        if (wrappedKey is null) throw new ArgumentNullException(nameof(wrappedKey));
        if (keySalt is null) throw new ArgumentNullException(nameof(keySalt));
        if (wrappedKey.Length > WrappedKeySize)
            throw new ArgumentException($"Wrapped key must be at most {WrappedKeySize} bytes.", nameof(wrappedKey));
        if (keySalt.Length > KeySaltSize)
            throw new ArgumentException($"Key salt must be at most {KeySaltSize} bytes.", nameof(keySalt));

        SlotIndex = slotIndex;
        Status = status;
        AlgorithmId = algorithmId;
        KdfIterations = kdfIterations;
        KdfAlgorithmId = kdfAlgorithmId;
        Reserved = reserved;
        CreatedUtcTicks = createdUtcTicks;
        RetiredUtcTicks = retiredUtcTicks;

        // Zero-pad to fixed sizes
        WrappedKey = new byte[WrappedKeySize];
        wrappedKey.AsSpan().CopyTo(WrappedKey);

        KeySalt = new byte[KeySaltSize];
        keySalt.AsSpan().CopyTo(KeySalt);
    }

    /// <summary>Writes this slot into the buffer at the given offset. Returns bytes written (always 124).</summary>
    internal int WriteTo(Span<byte> buffer, int offset)
    {
        buffer[offset] = SlotIndex;
        buffer[offset + 1] = Status;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset + 2), AlgorithmId);
        WrappedKey.AsSpan().CopyTo(buffer.Slice(offset + 4, WrappedKeySize));
        KeySalt.AsSpan().CopyTo(buffer.Slice(offset + 68, KeySaltSize));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset + 100), KdfIterations);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset + 104), KdfAlgorithmId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset + 106), Reserved);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 108), CreatedUtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 116), RetiredUtcTicks);
        return SerializedSize;
    }

    /// <summary>Reads a slot from the buffer at the given offset.</summary>
    internal static KeySlot ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var slotIndex = buffer[offset];
        var status = buffer[offset + 1];
        var algorithmId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 2));
        var wrappedKey = buffer.Slice(offset + 4, WrappedKeySize).ToArray();
        var keySalt = buffer.Slice(offset + 68, KeySaltSize).ToArray();
        var kdfIterations = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset + 100));
        var kdfAlgorithmId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 104));
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 106));
        var createdUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 108));
        var retiredUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 116));

        return new KeySlot(slotIndex, status, algorithmId, wrappedKey, keySalt,
                          kdfIterations, kdfAlgorithmId, reserved, createdUtcTicks, retiredUtcTicks);
    }

    /// <inheritdoc />
    public bool Equals(KeySlot other) =>
        SlotIndex == other.SlotIndex && Status == other.Status && AlgorithmId == other.AlgorithmId
        && KdfIterations == other.KdfIterations && KdfAlgorithmId == other.KdfAlgorithmId
        && CreatedUtcTicks == other.CreatedUtcTicks && RetiredUtcTicks == other.RetiredUtcTicks;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KeySlot other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(SlotIndex, Status, AlgorithmId, CreatedUtcTicks);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(KeySlot left, KeySlot right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(KeySlot left, KeySlot right) => !left.Equals(right);
}

/// <summary>
/// A key rotation event recorded in the encryption header rotation log.
/// Total serialized size: 12 bytes.
/// </summary>
public readonly struct KeyRotationEvent : IEquatable<KeyRotationEvent>
{
    /// <summary>Serialized size per event in bytes.</summary>
    public const int SerializedSize = 12;

    /// <summary>UTC ticks when the rotation occurred.</summary>
    public long TimestampUtcTicks { get; }

    /// <summary>Slot index of the old (rotated-from) key.</summary>
    public byte OldSlotIndex { get; }

    /// <summary>Slot index of the new (rotated-to) key.</summary>
    public byte NewSlotIndex { get; }

    /// <summary>Reason for the rotation.</summary>
    public ushort Reason { get; }

    public KeyRotationEvent(long timestampUtcTicks, byte oldSlotIndex, byte newSlotIndex, ushort reason)
    {
        TimestampUtcTicks = timestampUtcTicks;
        OldSlotIndex = oldSlotIndex;
        NewSlotIndex = newSlotIndex;
        Reason = reason;
    }

    /// <summary>Writes this event into the buffer at the given offset.</summary>
    internal int WriteTo(Span<byte> buffer, int offset)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), TimestampUtcTicks);
        buffer[offset + 8] = OldSlotIndex;
        buffer[offset + 9] = NewSlotIndex;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset + 10), Reason);
        return SerializedSize;
    }

    /// <summary>Reads an event from the buffer at the given offset.</summary>
    internal static KeyRotationEvent ReadFrom(ReadOnlySpan<byte> buffer, int offset)
    {
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        var oldSlot = buffer[offset + 8];
        var newSlot = buffer[offset + 9];
        var reason = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset + 10));
        return new KeyRotationEvent(timestamp, oldSlot, newSlot, reason);
    }

    /// <inheritdoc />
    public bool Equals(KeyRotationEvent other) =>
        TimestampUtcTicks == other.TimestampUtcTicks
        && OldSlotIndex == other.OldSlotIndex
        && NewSlotIndex == other.NewSlotIndex
        && Reason == other.Reason;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KeyRotationEvent other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TimestampUtcTicks, OldSlotIndex, NewSlotIndex, Reason);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(KeyRotationEvent left, KeyRotationEvent right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(KeyRotationEvent left, KeyRotationEvent right) => !left.Equals(right);
}

/// <summary>
/// Encryption Header region: a 2-block structure managing 63 key slots with KDF parameters
/// and a key rotation event log. Key slots are split across two blocks based on available
/// payload space. The rotation log occupies remaining space in block 1 after the overflow slots.
/// </summary>
/// <remarks>
/// Block 0 layout: [ActiveSlotIndex:1][TotalActiveSlots:1][RotationEventCount:2][Reserved:4]
///                  [KeySlot x slotsInBlock0][zero-fill][UniversalBlockTrailer]
/// Block 1 layout: [KeySlot x remainingSlots][RotationEvent entries][zero-fill][UniversalBlockTrailer]
///
/// The number of slots in block 0 is computed dynamically based on block size:
///   slotsInBlock0 = (payloadSize - headerSize) / KeySlot.SerializedSize
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 72: VDE regions -- Encryption Header (VREG-02)")]
public sealed class EncryptionHeaderRegion
{
    /// <summary>Number of blocks this region occupies.</summary>
    public const int BlockCount = 2;

    /// <summary>Maximum number of key slots (from <see cref="FormatConstants.MaxKeySlots"/>).</summary>
    public const int MaxKeySlots = FormatConstants.MaxKeySlots;

    /// <summary>Size of the fixed header at the start of block 0.</summary>
    private const int Block0HeaderSize = 8;

    private readonly KeySlot[] _slots = new KeySlot[MaxKeySlots];
    private readonly List<KeyRotationEvent> _rotationLog = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Creates a new empty encryption header region.</summary>
    public EncryptionHeaderRegion()
    {
    }

    /// <summary>
    /// Sets a key slot at the specified index.
    /// </summary>
    /// <param name="index">Slot index (0..62).</param>
    /// <param name="slot">The key slot data.</param>
    /// <exception cref="ArgumentOutOfRangeException">Index is outside 0..62.</exception>
    public void SetKeySlot(int index, KeySlot slot)
    {
        if (index < 0 || index >= MaxKeySlots)
            throw new ArgumentOutOfRangeException(nameof(index), $"Slot index must be 0..{MaxKeySlots - 1}.");
        _slots[index] = slot;
    }

    /// <summary>
    /// Gets the key slot at the specified index.
    /// </summary>
    /// <param name="index">Slot index (0..62).</param>
    /// <returns>The key slot data.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Index is outside 0..62.</exception>
    public KeySlot GetKeySlot(int index)
    {
        if (index < 0 || index >= MaxKeySlots)
            throw new ArgumentOutOfRangeException(nameof(index), $"Slot index must be 0..{MaxKeySlots - 1}.");
        return _slots[index];
    }

    /// <summary>
    /// Finds the first key slot with <see cref="KeySlotStatus.Active"/> status.
    /// </summary>
    /// <returns>The slot index (0..62) or -1 if no active slot exists.</returns>
    public int FindActiveSlot()
    {
        for (int i = 0; i < MaxKeySlots; i++)
        {
            if (_slots[i].Status == (byte)KeySlotStatus.Active)
                return i;
        }
        return -1;
    }

    /// <summary>Returns all 63 key slots as a read-only list.</summary>
    public IReadOnlyList<KeySlot> GetAllSlots() => _slots;

    /// <summary>
    /// Records a key rotation event. The log keeps the most recent events that fit
    /// in the available block 1 space (computed at serialization time).
    /// </summary>
    public void RecordRotation(KeyRotationEvent evt)
    {
        _rotationLog.Add(evt);
    }

    /// <summary>Returns the rotation log entries.</summary>
    public IReadOnlyList<KeyRotationEvent> GetRotationLog() => _rotationLog.AsReadOnly();

    /// <summary>
    /// Computes the number of key slots that fit in block 0 for a given block size.
    /// </summary>
    private static int SlotsInBlock0(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        return (payloadSize - Block0HeaderSize) / KeySlot.SerializedSize;
    }

    /// <summary>
    /// Computes the maximum number of rotation events that fit in block 1 after
    /// the overflow key slots.
    /// </summary>
    private static int MaxRotationEvents(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int slotsInB0 = SlotsInBlock0(blockSize);
        int overflowSlots = MaxKeySlots - slotsInB0;
        int usedBySlots = overflowSlots * KeySlot.SerializedSize;
        int remaining = payloadSize - usedBySlots;
        return remaining > 0 ? remaining / KeyRotationEvent.SerializedSize : 0;
    }

    /// <summary>
    /// Serializes the encryption header into a 2-block buffer with
    /// <see cref="UniversalBlockTrailer"/> on each block.
    /// </summary>
    /// <param name="buffer">Target buffer, must be at least <paramref name="blockSize"/> * 2 bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int totalSize = blockSize * BlockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        // Clear entire buffer
        buffer.Slice(0, totalSize).Clear();

        int slotsInB0 = SlotsInBlock0(blockSize);
        int overflowSlots = MaxKeySlots - slotsInB0;
        int maxEvents = MaxRotationEvents(blockSize);

        // Count active slots
        byte totalActive = 0;
        byte activeSlotIndex = 0xFF; // sentinel for "none"
        for (int i = 0; i < MaxKeySlots; i++)
        {
            if (_slots[i].Status == (byte)KeySlotStatus.Active)
            {
                totalActive++;
                if (activeSlotIndex == 0xFF)
                    activeSlotIndex = (byte)i;
            }
        }

        // Trim rotation log to max events (keep most recent)
        int eventCount = Math.Min(_rotationLog.Count, maxEvents);
        int eventSkip = _rotationLog.Count - eventCount;

        // ── Block 0: [Header:8][KeySlot x slotsInB0][zero-fill][Trailer] ──
        var block0 = buffer.Slice(0, blockSize);
        block0[0] = activeSlotIndex;
        block0[1] = totalActive;
        BinaryPrimitives.WriteUInt16LittleEndian(block0.Slice(2), (ushort)eventCount);
        // bytes 4..7 reserved (already zeroed)

        int offset = Block0HeaderSize;
        for (int i = 0; i < slotsInB0 && i < MaxKeySlots; i++)
            offset += _slots[i].WriteTo(block0, offset);

        UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.ENCR, Generation);

        // ── Block 1: [KeySlot x overflow][RotationEvents][zero-fill][Trailer] ──
        var block1 = buffer.Slice(blockSize, blockSize);
        offset = 0;
        for (int i = slotsInB0; i < MaxKeySlots; i++)
            offset += _slots[i].WriteTo(block1, offset);

        for (int i = 0; i < eventCount; i++)
            offset += _rotationLog[eventSkip + i].WriteTo(block1, offset);

        UniversalBlockTrailer.Write(block1, blockSize, BlockTypeTags.ENCR, Generation);
    }

    /// <summary>
    /// Deserializes a 2-block buffer, verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing 2 blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>A populated <see cref="EncryptionHeaderRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static EncryptionHeaderRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        int totalSize = blockSize * BlockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        var block0 = buffer.Slice(0, blockSize);
        var block1 = buffer.Slice(blockSize, blockSize);

        // Verify block trailers
        if (!UniversalBlockTrailer.Verify(block0, blockSize))
            throw new InvalidDataException("Encryption Header block 0 trailer verification failed.");
        if (!UniversalBlockTrailer.Verify(block1, blockSize))
            throw new InvalidDataException("Encryption Header block 1 trailer verification failed.");

        // Read generation from block 0 trailer
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        var region = new EncryptionHeaderRegion
        {
            Generation = trailer.GenerationNumber
        };

        // Read header
        int rotationEventCount = BinaryPrimitives.ReadUInt16LittleEndian(block0.Slice(2));

        // Read slots from block 0
        int slotsInB0 = SlotsInBlock0(blockSize);
        int offset = Block0HeaderSize;
        for (int i = 0; i < slotsInB0 && i < MaxKeySlots; i++)
        {
            region._slots[i] = KeySlot.ReadFrom(block0, offset);
            offset += KeySlot.SerializedSize;
        }

        // Read overflow slots from block 1
        offset = 0;
        int overflowSlots = MaxKeySlots - slotsInB0;
        for (int i = 0; i < overflowSlots; i++)
        {
            region._slots[slotsInB0 + i] = KeySlot.ReadFrom(block1, offset);
            offset += KeySlot.SerializedSize;
        }

        // Read rotation events from block 1
        for (int i = 0; i < rotationEventCount; i++)
        {
            region._rotationLog.Add(KeyRotationEvent.ReadFrom(block1, offset));
            offset += KeyRotationEvent.SerializedSize;
        }

        return region;
    }
}
