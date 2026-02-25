using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Result of claiming inode padding bytes for a new module's fields.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Online module addition - padding claim result (VDEF-09)")]
public readonly record struct PaddingClaimResult
{
    /// <summary>True if the padding claim succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if the claim failed; null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Updated inode layout descriptor with the new module field entry.</summary>
    public InodeLayoutDescriptor NewLayout { get; init; }

    /// <summary>Number of padding bytes claimed for the new module.</summary>
    public int BytesClaimed { get; init; }

    /// <summary>Padding bytes remaining after the claim.</summary>
    public int RemainingPadding { get; init; }

    /// <summary>Creates a successful claim result.</summary>
    public static PaddingClaimResult Succeeded(
        InodeLayoutDescriptor newLayout, int bytesClaimed, int remainingPadding) =>
        new()
        {
            Success = true,
            ErrorMessage = null,
            NewLayout = newLayout,
            BytesClaimed = bytesClaimed,
            RemainingPadding = remainingPadding,
        };

    /// <summary>Creates a failed claim result with an error message.</summary>
    public static PaddingClaimResult Failed(string errorMessage) =>
        new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            NewLayout = default,
            BytesClaimed = 0,
            RemainingPadding = 0,
        };
}

/// <summary>
/// Claims inode padding bytes for a new module's fields, enabling online module
/// addition without inode table migration. When the existing inode size has
/// sufficient padding (due to 64-byte alignment), a new module's fields can be
/// carved out of that padding by updating only the superblock metadata.
/// </summary>
/// <remarks>
/// <para>
/// Key design principle: <b>Inode data on disk does NOT change.</b> The bytes that
/// were previously padding (all zeros) are now interpreted as the new module's fields.
/// Since all module field initial values are zero (encryption key slot 0 = none,
/// replication generation 0 = initial, RAID shard 0 = unassigned, etc.), this is
/// correct by design. Only the <see cref="InodeLayoutDescriptor"/> in the superblock
/// is updated to reflect the new field mapping.
/// </para>
/// <para>
/// The claim process updates two superblock locations:
/// <list type="number">
///   <item>Block 2 (ExtendedMetadata): the serialized <see cref="InodeLayoutDescriptor"/></item>
///   <item>Block 0 (Primary Superblock): the module manifest bit</item>
/// </list>
/// Both writes are crash-safe via a minimal write-ahead approach (backup, write, flush, mark).
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Online module addition - inode padding claim (VDEF-09)")]
public sealed class InodePaddingClaim
{
    /// <summary>Marker byte indicating a WAL entry is pending.</summary>
    private const byte WalMarkerPending = 0x01;

    /// <summary>Marker byte indicating a WAL entry is committed.</summary>
    private const byte WalMarkerCommitted = 0x02;

    /// <summary>WAL header size: 4 bytes block number + 4 bytes data length + 1 byte marker.</summary>
    private const int WalEntryHeaderSize = 9;

    private readonly Stream _vdeStream;
    private readonly int _blockSize;

    /// <summary>
    /// Creates a new inode padding claim operator.
    /// </summary>
    /// <param name="vdeStream">Seekable read/write stream to the VDE file.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vdeStream"/> is null.</exception>
    /// <exception cref="ArgumentException">Stream is not seekable or writable, or block size is invalid.</exception>
    public InodePaddingClaim(Stream vdeStream, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(vdeStream);

        if (!vdeStream.CanSeek)
            throw new ArgumentException("VDE stream must be seekable.", nameof(vdeStream));
        if (!vdeStream.CanWrite)
            throw new ArgumentException("VDE stream must be writable.", nameof(vdeStream));
        if (!vdeStream.CanRead)
            throw new ArgumentException("VDE stream must be readable.", nameof(vdeStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentException(
                $"Block size must be between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.",
                nameof(blockSize));

        _vdeStream = vdeStream;
        _blockSize = blockSize;
    }

    /// <summary>
    /// Claims padding bytes for the specified module's inode fields. Updates the
    /// <see cref="InodeLayoutDescriptor"/> in the superblock and sets the module's
    /// manifest bit. No inode data is rewritten -- zero-filled padding bytes serve
    /// as valid initial state for all module fields (lazy initialization).
    /// </summary>
    /// <param name="module">The module to activate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PaddingClaimResult"/> indicating success or failure.</returns>
    public async Task<PaddingClaimResult> ClaimPaddingForModuleAsync(ModuleId module, CancellationToken ct)
    {
        // Step 1: Read the current superblock (block 0) to get module manifest
        var block0 = new byte[_blockSize];
        _vdeStream.Position = 0;
        await ReadFullyAsync(block0, ct).ConfigureAwait(false);

        var superblock = SuperblockV2.Deserialize(block0, _blockSize);
        uint currentManifest = superblock.ModuleManifest;

        // Step 2: Validate module is not already active
        if (ModuleRegistry.IsModuleActive(currentManifest, module))
            return PaddingClaimResult.Failed($"Module {module} is already active in the manifest.");

        // Step 3: Get module's inode field bytes
        var moduleDef = ModuleRegistry.GetModule(module);

        // Step 4: If module has no inode fields, only manifest update is needed
        if (moduleDef.InodeFieldBytes == 0)
        {
            // Just update the manifest bit -- no layout changes needed
            uint newManifest = currentManifest | (1u << (int)module);
            var currentLayout = InodeLayoutDescriptor.Create(currentManifest);
            await UpdateManifestInSuperblockAsync(block0, newManifest, ct).ConfigureAwait(false);

            return PaddingClaimResult.Succeeded(
                newLayout: currentLayout,
                bytesClaimed: 0,
                remainingPadding: currentLayout.PaddingBytes);
        }

        // Step 5: Calculate current layout and verify fit
        var sizeResult = InodeSizeCalculator.Calculate(currentManifest);
        if (!InodeSizeCalculator.CanAddModuleWithoutMigration(sizeResult.PaddingBytes, module))
        {
            return PaddingClaimResult.Failed(
                $"Module {module} requires {moduleDef.InodeFieldBytes} inode field bytes " +
                $"but only {sizeResult.PaddingBytes} padding bytes are available. " +
                "An inode table migration is required.");
        }

        // Step 6: Build updated layout descriptor
        var currentDescriptor = InodeLayoutDescriptor.Create(currentManifest);
        var newLayout = BuildUpdatedLayout(currentDescriptor, module);

        // Step 7: Read block 2 (ExtendedMetadata) for layout descriptor update
        var block2 = new byte[_blockSize];
        _vdeStream.Position = 2L * _blockSize;
        await ReadFullyAsync(block2, ct).ConfigureAwait(false);

        // Step 8: Write layout descriptor to block 2 with crash safety
        await WriteWithFsyncAsync(
            blockNumber: 2,
            oldData: block2,
            newDataWriter: buffer =>
            {
                // Serialize the new layout descriptor into the block
                // Layout descriptor is stored at the beginning of block 2
                InodeLayoutDescriptor.Serialize(in newLayout, buffer);
            },
            ct).ConfigureAwait(false);

        // Step 9: Update module manifest bit in superblock block 0
        uint updatedManifest = currentManifest | (1u << (int)module);
        await UpdateManifestInSuperblockAsync(block0, updatedManifest, ct).ConfigureAwait(false);

        return PaddingClaimResult.Succeeded(
            newLayout: newLayout,
            bytesClaimed: moduleDef.InodeFieldBytes,
            remainingPadding: newLayout.PaddingBytes);
    }

    /// <summary>
    /// Pure pre-flight check: determines whether the specified module's inode fields
    /// can be claimed from existing padding without migration. No I/O is performed.
    /// </summary>
    /// <param name="moduleManifest">Current 32-bit module manifest.</param>
    /// <param name="module">The module to check.</param>
    /// <returns>True if the module can be added via padding claim.</returns>
    public static bool CanClaimPadding(uint moduleManifest, ModuleId module)
    {
        if (ModuleRegistry.IsModuleActive(moduleManifest, module))
            return false;

        var moduleDef = ModuleRegistry.GetModule(module);
        if (moduleDef.InodeFieldBytes == 0)
            return true; // No inode fields needed

        var sizeResult = InodeSizeCalculator.Calculate(moduleManifest);
        return InodeSizeCalculator.CanAddModuleWithoutMigration(sizeResult.PaddingBytes, module);
    }

    /// <summary>
    /// Builds an updated <see cref="InodeLayoutDescriptor"/> with the new module's field
    /// entry added. Validates that the new field does not overlap existing fields.
    /// </summary>
    /// <param name="current">The current layout descriptor.</param>
    /// <param name="module">The module to add.</param>
    /// <returns>A new layout descriptor with the module's field entry appended.</returns>
    /// <exception cref="InvalidOperationException">
    /// The new field would overlap an existing field, or the module is already present.
    /// </exception>
    private static InodeLayoutDescriptor BuildUpdatedLayout(InodeLayoutDescriptor current, ModuleId module)
    {
        // Verify module is not already in the layout
        if (current.HasModule(module))
            throw new InvalidOperationException($"Module {module} already has a field entry in the layout.");

        var moduleDef = ModuleRegistry.GetModule(module);
        int fieldBytes = moduleDef.InodeFieldBytes;

        // Calculate the offset for the new field: right after all existing module fields
        // Existing module fields span from CoreFieldsEnd to CoreFieldsEnd + sum(existing field sizes)
        int existingModuleFieldBytes = 0;
        for (int i = 0; i < current.ModuleFields.Length; i++)
        {
            existingModuleFieldBytes += current.ModuleFields[i].FieldSize;
        }

        ushort newFieldOffset = (ushort)(current.CoreFieldsEnd + existingModuleFieldBytes);

        // Validate no overlap: new field must fit within what was previously padding
        int endOfNewField = newFieldOffset + fieldBytes;
        int endOfInode = current.InodeSize;
        if (endOfNewField > endOfInode)
        {
            throw new InvalidOperationException(
                $"New field for module {module} would extend past inode boundary " +
                $"(offset {newFieldOffset} + size {fieldBytes} = {endOfNewField}, inode size = {endOfInode}).");
        }

        // Build the new module field entry
        var newEntry = new ModuleFieldEntry(
            moduleId: (byte)module,
            fieldOffset: newFieldOffset,
            fieldSize: (ushort)fieldBytes,
            fieldVersion: 1,
            flags: ModuleFieldEntry.FlagActive);

        // Build new module fields array: existing entries + new entry, sorted by module ID
        var newFields = new ModuleFieldEntry[current.ModuleFieldCount + 1];
        int insertIndex = newFields.Length - 1; // Default: append at end
        bool inserted = false;

        for (int i = 0, j = 0; i < current.ModuleFields.Length; i++, j++)
        {
            if (!inserted && current.ModuleFields[i].ModuleId > (byte)module)
            {
                newFields[j] = newEntry;
                inserted = true;
                j++;
            }
            newFields[j] = current.ModuleFields[i];
        }
        if (!inserted)
        {
            newFields[^1] = newEntry;
        }

        // Padding decreases by the claimed bytes; inode size stays the same
        byte newPadding = (byte)(current.PaddingBytes - fieldBytes);

        return new InodeLayoutDescriptor(
            inodeSize: current.InodeSize, // Inode size does NOT change
            coreFieldsEnd: current.CoreFieldsEnd,
            moduleFieldCount: (byte)newFields.Length,
            paddingBytes: newPadding,
            reserved: 0,
            moduleFields: newFields);
    }

    /// <summary>
    /// Updates the module manifest in superblock block 0 with crash safety.
    /// </summary>
    private async Task UpdateManifestInSuperblockAsync(byte[] currentBlock0, uint newManifest, CancellationToken ct)
    {
        await WriteWithFsyncAsync(
            blockNumber: 0,
            oldData: currentBlock0,
            newDataWriter: buffer =>
            {
                // Copy current block 0 data
                currentBlock0.AsSpan().CopyTo(buffer);
                // Overwrite the module manifest at offset 0x20 (after magic + version info)
                BinaryPrimitives.WriteUInt32LittleEndian(
                    buffer.Slice(FormatConstants.MagicSignatureSize + FormatVersionInfo.SerializedSize),
                    newManifest);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Minimal crash-safe write: backs up the old block data to a WAL region in the
    /// metadata area, writes the new data, flushes, and writes a completion marker.
    /// </summary>
    /// <remarks>
    /// The WAL region uses the last block of the superblock mirror (block 7) for
    /// temporary backup storage. Recovery: if a pending marker is found without a
    /// committed marker, the old data is restored from the WAL entry.
    /// </remarks>
    private async Task WriteWithFsyncAsync(
        int blockNumber,
        byte[] oldData,
        Action<Span<byte>> newDataWriter,
        CancellationToken ct)
    {
        // WAL backup location: last block of superblock mirror group (block 7)
        // Each WAL entry: [4 bytes block#] [4 bytes length] [1 byte marker] [data...]
        long walBlockOffset = 7L * _blockSize;
        int walDataLength = Math.Min(oldData.Length, _blockSize);

        // Step 1: Write backup to WAL with pending marker
        var walEntry = new byte[WalEntryHeaderSize + walDataLength];
        BinaryPrimitives.WriteInt32LittleEndian(walEntry.AsSpan(0, 4), blockNumber);
        BinaryPrimitives.WriteInt32LittleEndian(walEntry.AsSpan(4, 4), walDataLength);
        walEntry[8] = WalMarkerPending;
        oldData.AsSpan(0, walDataLength).CopyTo(walEntry.AsSpan(WalEntryHeaderSize));

        _vdeStream.Position = walBlockOffset;
        await _vdeStream.WriteAsync(walEntry, ct).ConfigureAwait(false);
        await _vdeStream.FlushAsync(ct).ConfigureAwait(false);

        // Step 2: Write the new block data
        var newBlock = new byte[_blockSize];
        newDataWriter(newBlock);

        _vdeStream.Position = (long)blockNumber * _blockSize;
        await _vdeStream.WriteAsync(newBlock, ct).ConfigureAwait(false);
        await _vdeStream.FlushAsync(ct).ConfigureAwait(false);

        // Step 3: Write completion marker
        walEntry[8] = WalMarkerCommitted;
        _vdeStream.Position = walBlockOffset;
        await _vdeStream.WriteAsync(walEntry.AsMemory(0, WalEntryHeaderSize), ct).ConfigureAwait(false);
        await _vdeStream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads exactly buffer.Length bytes from the current stream position.
    /// </summary>
    private async Task ReadFullyAsync(byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await _vdeStream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(
                    $"Unexpected end of VDE stream at position {_vdeStream.Position}. " +
                    $"Expected {buffer.Length} bytes, got {totalRead}.");
            totalRead += read;
        }
    }
}
