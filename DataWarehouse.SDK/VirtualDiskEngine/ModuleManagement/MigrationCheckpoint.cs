using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// Phase of a background inode migration operation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Migration phase enum (OMA-03)")]
public enum MigrationPhase : byte
{
    /// <summary>Allocating blocks for the new inode table.</summary>
    Allocating = 0,

    /// <summary>Copying inodes from old to new table with new layout.</summary>
    CopyingInodes = 1,

    /// <summary>Atomically swapping region pointers from old to new inode table.</summary>
    SwappingRegions = 2,

    /// <summary>Migration completed successfully.</summary>
    Complete = 3,

    /// <summary>Rolling back a failed or cancelled migration.</summary>
    RollingBack = 4,
}

/// <summary>
/// Persistent checkpoint data for a background inode migration. Serialized to a
/// reserved block on disk so migration can resume after crash without data loss.
/// </summary>
/// <remarks>
/// Wire format (148 bytes):
/// <code>
///   [0..16)   MigrationId          (Guid)
///   [16..17)  TargetModule         (byte = ModuleId)
///   [17..21)  OriginalManifest     (uint)
///   [21..25)  TargetManifest       (uint)
///   [25..33)  TotalInodes          (long)
///   [33..41)  MigratedInodes       (long)
///   [41..49)  NewInodeTableStartBlock  (long)
///   [49..57)  NewInodeTableBlockCount  (long)
///   [57..65)  OldInodeTableStartBlock  (long)
///   [65..73)  OldInodeTableBlockCount  (long)
///   [73..81)  StartedUtc           (long, ticks)
///   [81..89)  LastCheckpointUtc    (long, ticks)
///   [89]      Phase                (byte = MigrationPhase)
///   [90..98)  XxHash64 checksum    (ulong)
/// </code>
/// Total: 98 bytes of useful data.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Migration checkpoint data (OMA-03)")]
public readonly record struct CheckpointData
{
    /// <summary>Serialized size of checkpoint data excluding checksum.</summary>
    internal const int PayloadSize = 90;

    /// <summary>Serialized size including XxHash64 checksum.</summary>
    internal const int SerializedSize = 98;

    /// <summary>Unique ID for this migration operation.</summary>
    public Guid MigrationId { get; init; }

    /// <summary>Module being added.</summary>
    public ModuleId TargetModule { get; init; }

    /// <summary>Module manifest before migration (for rollback).</summary>
    public uint OriginalManifest { get; init; }

    /// <summary>Module manifest after migration.</summary>
    public uint TargetManifest { get; init; }

    /// <summary>Total inodes to migrate.</summary>
    public long TotalInodes { get; init; }

    /// <summary>Inodes successfully migrated so far.</summary>
    public long MigratedInodes { get; init; }

    /// <summary>Where the new inode table region starts.</summary>
    public long NewInodeTableStartBlock { get; init; }

    /// <summary>Size of new inode table region in blocks.</summary>
    public long NewInodeTableBlockCount { get; init; }

    /// <summary>Original inode table location (for rollback).</summary>
    public long OldInodeTableStartBlock { get; init; }

    /// <summary>Original inode table size in blocks.</summary>
    public long OldInodeTableBlockCount { get; init; }

    /// <summary>When migration began.</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>When last checkpoint was saved.</summary>
    public DateTimeOffset LastCheckpointUtc { get; init; }

    /// <summary>Current migration phase.</summary>
    public MigrationPhase Phase { get; init; }
}

/// <summary>
/// Persists migration progress checkpoints to a reserved block on disk.
/// Uses XxHash64 checksum for integrity verification and <see cref="UniversalBlockTrailer"/>
/// with tag MCHK for block-level validation.
///
/// On crash recovery, <see cref="LoadAsync"/> verifies both the trailer and the payload
/// checksum before returning data. Invalid or empty blocks return null (no pending migration).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Migration checkpoint persistence (OMA-03)")]
public sealed class MigrationCheckpoint
{
    private readonly Stream _vdeStream;
    private readonly int _blockSize;
    private readonly long _checkpointBlock;

    /// <summary>Custom block type tag for migration checkpoint ("MCHK" big-endian).</summary>
    private const uint MchkTag = 0x4D43484B;

    /// <summary>
    /// Creates a new MigrationCheckpoint writer/reader.
    /// </summary>
    /// <param name="vdeStream">The VDE stream for read/write operations.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="checkpointBlock">Reserved block number for checkpoint data.</param>
    public MigrationCheckpoint(Stream vdeStream, int blockSize, long checkpointBlock)
    {
        _vdeStream = vdeStream ?? throw new ArgumentNullException(nameof(vdeStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        _blockSize = blockSize;
        _checkpointBlock = checkpointBlock;
    }

    /// <summary>
    /// Saves checkpoint data to the reserved block. Serializes using BinaryPrimitives,
    /// appends XxHash64 checksum, and writes with UniversalBlockTrailer (tag: MCHK).
    /// </summary>
    /// <param name="data">Checkpoint data to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SaveAsync(CheckpointData data, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            Array.Clear(buffer, 0, _blockSize);
            var span = buffer.AsSpan(0, _blockSize);

            // Serialize payload
            SerializePayload(data, span);

            // XxHash64 checksum of payload bytes [0..PayloadSize)
            ulong checksum = XxHash64.HashToUInt64(span[..CheckpointData.PayloadSize]);
            BinaryPrimitives.WriteUInt64LittleEndian(span[CheckpointData.PayloadSize..], checksum);

            // Write block trailer
            UniversalBlockTrailer.Write(span, _blockSize, MchkTag, 1);

            // Write to disk
            long offset = _checkpointBlock * _blockSize;
            _vdeStream.Seek(offset, SeekOrigin.Begin);
            await _vdeStream.WriteAsync(buffer.AsMemory(0, _blockSize), ct);
            await _vdeStream.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Loads checkpoint data from the reserved block. Verifies the trailer and XxHash64
    /// checksum before returning data. Returns null if no valid checkpoint exists.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The checkpoint data if valid, or null if block is empty/invalid.</returns>
    public async Task<CheckpointData?> LoadAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            long offset = _checkpointBlock * _blockSize;
            _vdeStream.Seek(offset, SeekOrigin.Begin);

            int totalRead = 0;
            while (totalRead < _blockSize)
            {
                int bytesRead = await _vdeStream.ReadAsync(
                    buffer.AsMemory(totalRead, _blockSize - totalRead), ct);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            if (totalRead < _blockSize)
                return null;

            var span = buffer.AsSpan(0, _blockSize);

            // Verify block trailer
            if (!UniversalBlockTrailer.Verify(span, _blockSize))
                return null;

            // Verify XxHash64 checksum
            ulong storedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(
                span[CheckpointData.PayloadSize..]);
            ulong computedChecksum = XxHash64.HashToUInt64(span[..CheckpointData.PayloadSize]);
            if (storedChecksum != computedChecksum)
                return null;

            // Check if all payload bytes are zero (no checkpoint saved)
            bool allZero = true;
            for (int i = 0; i < CheckpointData.PayloadSize; i++)
            {
                if (span[i] != 0)
                {
                    allZero = false;
                    break;
                }
            }
            if (allZero)
                return null;

            return DeserializePayload(span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Clears the checkpoint block by zeroing out all data.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task ClearAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            Array.Clear(buffer, 0, _blockSize);

            long offset = _checkpointBlock * _blockSize;
            _vdeStream.Seek(offset, SeekOrigin.Begin);
            await _vdeStream.WriteAsync(buffer.AsMemory(0, _blockSize), ct);
            await _vdeStream.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Serializes checkpoint data payload to the buffer at offset 0.
    /// </summary>
    private static void SerializePayload(CheckpointData data, Span<byte> buffer)
    {
        int offset = 0;

        // MigrationId (16 bytes)
        data.MigrationId.TryWriteBytes(buffer[offset..]);
        offset += 16;

        // TargetModule (1 byte)
        buffer[offset++] = (byte)data.TargetModule;

        // OriginalManifest (4 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[offset..], data.OriginalManifest);
        offset += 4;

        // TargetManifest (4 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[offset..], data.TargetManifest);
        offset += 4;

        // TotalInodes (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], data.TotalInodes);
        offset += 8;

        // MigratedInodes (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], data.MigratedInodes);
        offset += 8;

        // NewInodeTableStartBlock (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], data.NewInodeTableStartBlock);
        offset += 8;

        // NewInodeTableBlockCount (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], data.NewInodeTableBlockCount);
        offset += 8;

        // OldInodeTableStartBlock (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], data.OldInodeTableStartBlock);
        offset += 8;

        // OldInodeTableBlockCount (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], data.OldInodeTableBlockCount);
        offset += 8;

        // StartedUtc (8 bytes, ticks)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], data.StartedUtc.UtcTicks);
        offset += 8;

        // LastCheckpointUtc (8 bytes, ticks)
        BinaryPrimitives.WriteInt64LittleEndian(buffer[offset..], data.LastCheckpointUtc.UtcTicks);
        offset += 8;

        // Phase (1 byte)
        buffer[offset] = (byte)data.Phase;
    }

    /// <summary>
    /// Deserializes checkpoint data payload from the buffer at offset 0.
    /// </summary>
    private static CheckpointData DeserializePayload(ReadOnlySpan<byte> buffer)
    {
        int offset = 0;

        var migrationId = new Guid(buffer[offset..(offset + 16)]);
        offset += 16;

        var targetModule = (ModuleId)buffer[offset++];

        var originalManifest = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
        offset += 4;

        var targetManifest = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
        offset += 4;

        var totalInodes = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        var migratedInodes = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        var newInodeTableStartBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        var newInodeTableBlockCount = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        var oldInodeTableStartBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        var oldInodeTableBlockCount = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        var startedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        var lastCheckpointUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer[offset..]);
        offset += 8;

        var phase = (MigrationPhase)buffer[offset];

        return new CheckpointData
        {
            MigrationId = migrationId,
            TargetModule = targetModule,
            OriginalManifest = originalManifest,
            TargetManifest = targetManifest,
            TotalInodes = totalInodes,
            MigratedInodes = migratedInodes,
            NewInodeTableStartBlock = newInodeTableStartBlock,
            NewInodeTableBlockCount = newInodeTableBlockCount,
            OldInodeTableStartBlock = oldInodeTableStartBlock,
            OldInodeTableBlockCount = oldInodeTableBlockCount,
            StartedUtc = new DateTimeOffset(startedUtcTicks, TimeSpan.Zero),
            LastCheckpointUtc = new DateTimeOffset(lastCheckpointUtcTicks, TimeSpan.Zero),
            Phase = phase,
        };
    }
}
