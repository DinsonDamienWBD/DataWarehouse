using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Format;

/// <summary>
/// Manages the standalone 2-block Region Directory at blocks 8-9 in the DWVD v2.0 format.
/// Contains 127 region pointer slots (each 32 bytes) spread across 2 blocks with Universal
/// Block Trailers. Block 0 holds all 127 slots (4064 bytes). Block 1 holds a directory
/// header (version, slot count, active count, checksum of block 0 data) for verification
/// and future expansion.
///
/// The Region Directory is a standalone copy of region pointers kept in sync with the
/// Region Pointer Table in Superblock Block 1 at a higher level.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 71: VDE v2.0 region directory (VDE2-03)")]
public sealed class RegionDirectory
{
    /// <summary>Number of region pointer slots (127).</summary>
    public int SlotCount => FormatConstants.MaxRegionSlots;

    /// <summary>Monotonic generation number for Universal Block Trailers.</summary>
    public uint Generation { get; set; }

    private readonly RegionPointer[] _slots;

    // ── Directory Header layout (Block 1) ────────────────────────────────
    // Offset 0: Version      (uint16) = 1
    // Offset 2: SlotCount    (uint16) = 127
    // Offset 4: ActiveCount  (uint16)
    // Offset 6: Reserved     (uint16) = 0
    // Offset 8: Block0Hash   (ulong)  = XxHash64 of block 0 payload
    private const int HeaderSize = 16;
    private const ushort DirectoryVersion = 1;

    /// <summary>Creates a new empty Region Directory with all slots free.</summary>
    public RegionDirectory()
    {
        _slots = new RegionPointer[FormatConstants.MaxRegionSlots];
    }

    /// <summary>Creates a Region Directory from an existing slot array.</summary>
    /// <param name="slots">Array of exactly 127 region pointers.</param>
    private RegionDirectory(RegionPointer[] slots)
    {
        _slots = slots;
    }

    /// <summary>Gets the region pointer at the specified slot index.</summary>
    /// <param name="index">Slot index (0 to 126).</param>
    /// <returns>The region pointer at the specified index.</returns>
    public RegionPointer GetSlot(int index)
    {
        if ((uint)index >= (uint)FormatConstants.MaxRegionSlots)
            throw new ArgumentOutOfRangeException(nameof(index), $"Index must be 0..{FormatConstants.MaxRegionSlots - 1}.");
        return _slots[index];
    }

    /// <summary>
    /// Adds a new region to the first free slot in the directory.
    /// </summary>
    /// <param name="regionTypeId">Block type tag identifying the region type.</param>
    /// <param name="flags">Region flags (Active flag is added automatically).</param>
    /// <param name="startBlock">First block number of the region.</param>
    /// <param name="blockCount">Total number of blocks in the region.</param>
    /// <returns>The slot index where the region was added.</returns>
    /// <exception cref="InvalidOperationException">No free slots available.</exception>
    public int AddRegion(uint regionTypeId, RegionFlags flags, long startBlock, long blockCount)
    {
        for (int i = 0; i < FormatConstants.MaxRegionSlots; i++)
        {
            if (_slots[i].IsFree)
            {
                _slots[i] = new RegionPointer(regionTypeId, flags | RegionFlags.Active, startBlock, blockCount, 0);
                return i;
            }
        }
        throw new InvalidOperationException($"Region directory is full ({FormatConstants.MaxRegionSlots} slots occupied).");
    }

    /// <summary>
    /// Removes the first region matching the specified type ID by zeroing out its slot.
    /// </summary>
    /// <param name="regionTypeId">Block type tag of the region to remove.</param>
    /// <returns>True if a matching region was found and removed; false otherwise.</returns>
    public bool RemoveRegion(uint regionTypeId)
    {
        int index = FindRegion(regionTypeId);
        if (index < 0) return false;
        _slots[index] = default;
        return true;
    }

    /// <summary>
    /// Removes the region at the specified slot index by zeroing it out.
    /// </summary>
    /// <param name="index">Slot index (0 to 126).</param>
    /// <returns>True if the slot was in use and was cleared; false if already free.</returns>
    public bool RemoveRegionAt(int index)
    {
        if ((uint)index >= (uint)FormatConstants.MaxRegionSlots)
            throw new ArgumentOutOfRangeException(nameof(index), $"Index must be 0..{FormatConstants.MaxRegionSlots - 1}.");

        if (_slots[index].IsFree) return false;
        _slots[index] = default;
        return true;
    }

    /// <summary>
    /// Finds the first slot containing a region with the specified type ID.
    /// </summary>
    /// <param name="regionTypeId">Block type tag to search for.</param>
    /// <returns>Slot index if found, or -1 if not found.</returns>
    public int FindRegion(uint regionTypeId)
    {
        for (int i = 0; i < FormatConstants.MaxRegionSlots; i++)
        {
            if (_slots[i].RegionTypeId == regionTypeId && _slots[i].IsActive)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Returns all non-free slots with their indices.
    /// </summary>
    public IReadOnlyList<(int Index, RegionPointer Pointer)> GetActiveRegions()
    {
        var result = new List<(int, RegionPointer)>();
        for (int i = 0; i < FormatConstants.MaxRegionSlots; i++)
        {
            if (_slots[i].IsActive)
                result.Add((i, _slots[i]));
        }
        return result;
    }

    /// <summary>Count of slots with the Active flag set.</summary>
    public int ActiveRegionCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < FormatConstants.MaxRegionSlots; i++)
            {
                if (_slots[i].IsActive) count++;
            }
            return count;
        }
    }

    /// <summary>Number of free slots remaining.</summary>
    public int FreeSlotCount => SlotCount - ActiveRegionCount;

    // ── Serialization ────────────────────────────────────────────────────

    /// <summary>
    /// Serializes the Region Directory into a 2-block buffer.
    /// Block 0: 127 slots x 32 bytes = 4064 bytes + zero-fill + RMAP trailer.
    /// Block 1: directory header (version, counts, block 0 hash) + zero-fill + RMAP trailer.
    /// </summary>
    /// <param name="buffer">Destination buffer, must be at least 2 * blockSize bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int totalSize = checked(FormatConstants.RegionDirectoryBlocks * blockSize);
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));

        // Clear the entire 2-block region
        buffer.Slice(0, totalSize).Clear();

        // ── Block 0: Write 127 slots ────────────────────────────────────
        var block0 = buffer.Slice(0, blockSize);
        for (int i = 0; i < FormatConstants.MaxRegionSlots; i++)
        {
            int offset = i * RegionPointer.SerializedSize;
            RegionPointer.Serialize(_slots[i], block0.Slice(offset));
        }
        // Remaining bytes up to blockSize-16 are already zero (reserved).
        // Write trailer for block 0
        UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.RMAP, Generation);

        // ── Block 1: Write directory header ─────────────────────────────
        var block1 = buffer.Slice(blockSize, blockSize);

        // Compute XxHash64 of block 0 payload (for cross-block verification)
        int payloadSize = blockSize - FormatConstants.UniversalBlockTrailerSize;
        var block0Payload = buffer.Slice(0, payloadSize);
        var block0Hash = System.IO.Hashing.XxHash64.HashToUInt64(block0Payload);

        // Header: version(2) + slotCount(2) + activeCount(2) + reserved(2) + block0Hash(8) = 16 bytes
        BinaryPrimitives.WriteUInt16LittleEndian(block1, DirectoryVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(block1.Slice(2), (ushort)FormatConstants.MaxRegionSlots);
        BinaryPrimitives.WriteUInt16LittleEndian(block1.Slice(4), (ushort)ActiveRegionCount);
        BinaryPrimitives.WriteUInt16LittleEndian(block1.Slice(6), 0); // reserved
        BinaryPrimitives.WriteUInt64LittleEndian(block1.Slice(8), block0Hash);

        // Write trailer for block 1
        UniversalBlockTrailer.Write(block1, blockSize, BlockTypeTags.RMAP, Generation);
    }

    /// <summary>
    /// Deserializes a Region Directory from a 2-block buffer.
    /// Verifies both block trailers before reading data.
    /// </summary>
    /// <param name="buffer">Source buffer, must be at least 2 * blockSize bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>The deserialized Region Directory.</returns>
    /// <exception cref="InvalidDataException">Block trailer verification failed.</exception>
    public static RegionDirectory Deserialize(ReadOnlySpan<byte> buffer, int blockSize)
    {
        int totalSize = checked(FormatConstants.RegionDirectoryBlocks * blockSize);
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));

        // Verify block 0 trailer
        var block0 = buffer.Slice(0, blockSize);
        if (!UniversalBlockTrailer.Verify(block0, blockSize))
            throw new InvalidDataException("Region Directory block 0 trailer verification failed.");

        // Read 127 slots from block 0
        var slots = new RegionPointer[FormatConstants.MaxRegionSlots];
        for (int i = 0; i < FormatConstants.MaxRegionSlots; i++)
        {
            int offset = i * RegionPointer.SerializedSize;
            slots[i] = RegionPointer.Deserialize(block0.Slice(offset));
        }

        // Verify block 1 trailer
        var block1 = buffer.Slice(blockSize, blockSize);
        if (!UniversalBlockTrailer.Verify(block1, blockSize))
            throw new InvalidDataException("Region Directory block 1 trailer verification failed.");

        // Read directory header from block 1
        var version = BinaryPrimitives.ReadUInt16LittleEndian(block1);
        if (version != DirectoryVersion)
            throw new InvalidDataException($"Unsupported Region Directory version {version} (expected {DirectoryVersion}).");

        // Read generation from block 0 trailer
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        var directory = new RegionDirectory(slots)
        {
            Generation = trailer.GenerationNumber,
        };

        return directory;
    }
}
