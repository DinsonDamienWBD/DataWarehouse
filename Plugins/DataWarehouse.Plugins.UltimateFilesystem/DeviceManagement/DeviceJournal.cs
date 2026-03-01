using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

/// <summary>
/// Type of device lifecycle event recorded in the journal.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device lifecycle journal (BMDV-10)")]
public enum JournalEntryType : byte
{
    /// <summary>A new pool is being created.</summary>
    PoolCreate = 0,
    /// <summary>A pool is being deleted.</summary>
    PoolDelete = 1,
    /// <summary>A device is being added to a pool.</summary>
    DeviceAdd = 2,
    /// <summary>A device is being removed from a pool.</summary>
    DeviceRemove = 3,
    /// <summary>A rebuild operation is starting.</summary>
    RebuildStart = 4,
    /// <summary>A rebuild operation has completed.</summary>
    RebuildComplete = 5,
    /// <summary>A device has come online.</summary>
    DeviceOnline = 6,
    /// <summary>A device has gone offline.</summary>
    DeviceOffline = 7
}

/// <summary>
/// Phase of a journal entry in the intent/commit/rollback lifecycle.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device lifecycle journal (BMDV-10)")]
public enum JournalEntryPhase : byte
{
    /// <summary>Operation has been declared but not yet completed.</summary>
    Intent = 0,
    /// <summary>Operation completed successfully.</summary>
    Committed = 1,
    /// <summary>Operation was rolled back.</summary>
    RolledBack = 2
}

/// <summary>
/// A single journal entry recording a device lifecycle event with its phase.
/// </summary>
/// <param name="SequenceNumber">Monotonically increasing sequence number.</param>
/// <param name="Type">The type of lifecycle event.</param>
/// <param name="PoolId">The pool affected by this event.</param>
/// <param name="DeviceId">The device involved (null for pool-level events).</param>
/// <param name="TimestampUtc">UTC timestamp when the entry was created.</param>
/// <param name="Phase">Current phase of the entry (Intent/Committed/RolledBack).</param>
/// <param name="Payload">Operation-specific data (e.g., serialized PoolMemberDescriptor for DeviceAdd).</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device lifecycle journal (BMDV-10)")]
public sealed record JournalEntry(
    long SequenceNumber,
    JournalEntryType Type,
    Guid PoolId,
    string? DeviceId,
    DateTime TimestampUtc,
    JournalEntryPhase Phase,
    byte[] Payload);

/// <summary>
/// Write-ahead journal for device lifecycle operations. Before any pool mutation
/// (add device, remove device, create pool, delete pool), an intent record is journaled.
/// After the mutation completes, a completion record is written. On crash recovery,
/// incomplete intents are replayed or rolled back.
/// </summary>
/// <remarks>
/// <para>
/// Journal storage uses reserved sectors on pool member devices. Block 0 is pool metadata
/// (from 90-03). Blocks 1-8 (32KB on 4K devices) form the journal area.
/// </para>
/// <para>
/// Each entry is fixed 256 bytes with CRC32 integrity. Maximum 128 entries per journal area
/// (128 * 256 = 32KB). The journal operates as a circular buffer: when full, oldest
/// committed/rolled-back entries are overwritten.
/// </para>
/// <para>
/// Thread-safe via SemaphoreSlim for write serialization.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device lifecycle journal (BMDV-10)")]
public sealed class DeviceJournal : IAsyncDisposable
{
    /// <summary>Size of each journal entry in bytes.</summary>
    private const int EntrySize = 256;

    /// <summary>Maximum number of entries in the journal area.</summary>
    private const int MaxEntries = 128;

    /// <summary>Total journal area size in bytes (128 * 256 = 32KB).</summary>
    private const int JournalAreaSize = MaxEntries * EntrySize;

    /// <summary>Starting block for journal area (block 1, after pool metadata at block 0).</summary>
    private const int JournalStartBlock = 1;

    /// <summary>Maximum device ID length in bytes (UTF-8).</summary>
    private const int MaxDeviceIdBytes = 64;

    /// <summary>Maximum payload length in bytes.</summary>
    private const int MaxPayloadBytes = 148;

    /// <summary>Offset constants within a 256-byte entry.</summary>
    private const int OffsetSequenceNumber = 0;   // 8 bytes (int64 LE)
    private const int OffsetEntryType = 8;         // 1 byte
    private const int OffsetPhase = 9;             // 1 byte
    private const int OffsetPoolId = 10;           // 16 bytes (Guid)
    private const int OffsetTimestamp = 26;         // 8 bytes (int64 LE, ticks)
    private const int OffsetDeviceIdLength = 34;   // 2 bytes (int16 LE)
    private const int OffsetDeviceId = 36;         // max 64 bytes
    private const int OffsetPayloadLength = 100;   // 2 bytes (int16 LE)
    private const int OffsetPayload = 102;         // max 148 bytes
    private const int OffsetCrc32 = 250;           // 4 bytes
    // Bytes 254-255: reserved (zero)

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _nextSequenceNumber;
    private bool _disposed;

    /// <summary>
    /// Writes an intent entry to the journal area of the specified device.
    /// </summary>
    /// <param name="type">The type of lifecycle event.</param>
    /// <param name="poolId">The pool affected by this event.</param>
    /// <param name="deviceId">The device involved (null for pool-level events).</param>
    /// <param name="payload">Operation-specific data.</param>
    /// <param name="journalDevice">The device to write the journal entry to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The sequence number of the written entry.</returns>
    public async Task<long> WriteIntentAsync(
        JournalEntryType type,
        Guid poolId,
        string? deviceId,
        byte[]? payload,
        IPhysicalBlockDevice journalDevice,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(journalDevice);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sequenceNumber = Interlocked.Increment(ref _nextSequenceNumber);
            var entry = CreateEntry(sequenceNumber, type, JournalEntryPhase.Intent, poolId, deviceId, payload);

            await WriteEntryToDeviceAsync(entry, journalDevice, ct).ConfigureAwait(false);

            return sequenceNumber;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Marks an existing journal entry as committed.
    /// </summary>
    /// <param name="sequenceNumber">The sequence number of the entry to commit.</param>
    /// <param name="journalDevice">The device containing the journal.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CommitAsync(
        long sequenceNumber,
        IPhysicalBlockDevice journalDevice,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(journalDevice);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await UpdateEntryPhaseAsync(sequenceNumber, JournalEntryPhase.Committed, journalDevice, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Marks an existing journal entry as rolled back.
    /// </summary>
    /// <param name="sequenceNumber">The sequence number of the entry to roll back.</param>
    /// <param name="journalDevice">The device containing the journal.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RollbackAsync(
        long sequenceNumber,
        IPhysicalBlockDevice journalDevice,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(journalDevice);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await UpdateEntryPhaseAsync(sequenceNumber, JournalEntryPhase.RolledBack, journalDevice, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads all valid journal entries from the journal area of the specified device.
    /// </summary>
    /// <param name="journalDevice">The device to read the journal from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All valid journal entries ordered by sequence number.</returns>
    public async Task<IReadOnlyList<JournalEntry>> ReadJournalAsync(
        IPhysicalBlockDevice journalDevice,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(journalDevice);

        var journalData = await ReadJournalAreaAsync(journalDevice, ct).ConfigureAwait(false);
        var entries = new List<JournalEntry>();

        for (var i = 0; i < MaxEntries; i++)
        {
            var offset = i * EntrySize;
            var entrySpan = journalData.AsSpan(offset, EntrySize);

            var entry = DeserializeEntry(entrySpan);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        entries.Sort((a, b) => a.SequenceNumber.CompareTo(b.SequenceNumber));

        // Update next sequence number based on max found.
        // P2-2975: Use a CompareExchange loop so we only advance _nextSequenceNumber forward.
        // A plain Read→check→Exchange is a TOCTOU race: a concurrent writer could increment
        // the counter between our Read and Exchange, and Exchange would regress it to maxSeq.
        if (entries.Count > 0)
        {
            var maxSeq = entries[entries.Count - 1].SequenceNumber;
            long current;
            do
            {
                current = Interlocked.Read(ref _nextSequenceNumber);
                if (maxSeq < current) break; // Already at or past this value — nothing to do.
            }
            while (Interlocked.CompareExchange(ref _nextSequenceNumber, maxSeq, current) != current);
        }

        return entries.AsReadOnly();
    }

    /// <summary>
    /// Gets all uncommitted intent entries from the journal (crash recovery candidates).
    /// </summary>
    /// <param name="journalDevice">The device to read the journal from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Journal entries with Phase=Intent that were not committed or rolled back.</returns>
    public async Task<IReadOnlyList<JournalEntry>> GetUncommittedIntentsAsync(
        IPhysicalBlockDevice journalDevice,
        CancellationToken ct = default)
    {
        var allEntries = await ReadJournalAsync(journalDevice, ct).ConfigureAwait(false);
        var uncommitted = new List<JournalEntry>();

        foreach (var entry in allEntries)
        {
            if (entry.Phase == JournalEntryPhase.Intent)
            {
                uncommitted.Add(entry);
            }
        }

        return uncommitted.AsReadOnly();
    }

    /// <inheritdoc/>
    /// <summary>
    /// Initializes the journal area on a device by writing a zeroed 32KB block
    /// (blocks 1-8). This marks the journal as empty and valid for use.
    /// Called during first-time pool creation to ensure a clean journal state.
    /// </summary>
    public async Task InitializeJournalAreaAsync(IPhysicalBlockDevice device, CancellationToken ct)
    {
        ThrowIfDisposed();
        var zeroedJournal = new byte[JournalAreaSize]; // zero-initialized by CLR
        await WriteJournalAreaAsync(zeroedJournal, device, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _writeLock.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    // ========================================================================
    // Internal helpers
    // ========================================================================

    /// <summary>
    /// Creates the raw 256-byte entry buffer for a journal entry.
    /// </summary>
    private static byte[] CreateEntry(
        long sequenceNumber,
        JournalEntryType type,
        JournalEntryPhase phase,
        Guid poolId,
        string? deviceId,
        byte[]? payload)
    {
        var buffer = new byte[EntrySize];

        // Sequence number (bytes 0-7)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(OffsetSequenceNumber), sequenceNumber);

        // Entry type (byte 8)
        buffer[OffsetEntryType] = (byte)type;

        // Phase (byte 9)
        buffer[OffsetPhase] = (byte)phase;

        // Pool ID (bytes 10-25)
        if (!poolId.TryWriteBytes(buffer.AsSpan(OffsetPoolId, 16)))
        {
            poolId.ToByteArray().CopyTo(buffer, OffsetPoolId);
        }

        // Timestamp (bytes 26-33)
        BinaryPrimitives.WriteInt64LittleEndian(
            buffer.AsSpan(OffsetTimestamp),
            DateTime.UtcNow.Ticks);

        // Device ID (bytes 34-99)
        if (!string.IsNullOrEmpty(deviceId))
        {
            var deviceIdBytes = Encoding.UTF8.GetBytes(deviceId);
            var deviceIdLen = Math.Min(deviceIdBytes.Length, MaxDeviceIdBytes);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(OffsetDeviceIdLength), (short)deviceIdLen);
            Array.Copy(deviceIdBytes, 0, buffer, OffsetDeviceId, deviceIdLen);
        }
        else
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(OffsetDeviceIdLength), 0);
        }

        // Payload (bytes 100-249)
        if (payload != null && payload.Length > 0)
        {
            var payloadLen = Math.Min(payload.Length, MaxPayloadBytes);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(OffsetPayloadLength), (short)payloadLen);
            Array.Copy(payload, 0, buffer, OffsetPayload, payloadLen);
        }
        else
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(OffsetPayloadLength), 0);
        }

        // CRC32 of bytes 0-249 (bytes 250-253)
        var crc = Crc32.Hash(buffer.AsSpan(0, OffsetCrc32));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OffsetCrc32), BinaryPrimitives.ReadUInt32LittleEndian(crc));

        // Bytes 254-255: reserved (already zero)

        return buffer;
    }

    /// <summary>
    /// Deserializes a journal entry from a 256-byte span. Returns null if the entry
    /// is empty (all zeros) or has an invalid CRC32.
    /// </summary>
    private static JournalEntry? DeserializeEntry(ReadOnlySpan<byte> data)
    {
        if (data.Length < EntrySize)
        {
            return null;
        }

        // Check if entry is empty (all zeros in sequence number area)
        var seqNum = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(OffsetSequenceNumber));
        if (seqNum == 0)
        {
            return null;
        }

        // Verify CRC32
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(OffsetCrc32));
        var computedCrcBytes = Crc32.Hash(data.Slice(0, OffsetCrc32));
        var computedCrc = BinaryPrimitives.ReadUInt32LittleEndian(computedCrcBytes);
        if (storedCrc != computedCrc)
        {
            return null;
        }

        var type = (JournalEntryType)data[OffsetEntryType];
        var phase = (JournalEntryPhase)data[OffsetPhase];

        var poolIdBytes = data.Slice(OffsetPoolId, 16).ToArray();
        var poolId = new Guid(poolIdBytes);

        var timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(OffsetTimestamp));
        var timestamp = new DateTime(timestampTicks, DateTimeKind.Utc);

        var deviceIdLen = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(OffsetDeviceIdLength));
        string? deviceId = null;
        if (deviceIdLen > 0)
        {
            deviceId = Encoding.UTF8.GetString(data.Slice(OffsetDeviceId, deviceIdLen));
        }

        var payloadLen = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(OffsetPayloadLength));
        var payload = payloadLen > 0
            ? data.Slice(OffsetPayload, payloadLen).ToArray()
            : Array.Empty<byte>();

        return new JournalEntry(seqNum, type, poolId, deviceId, timestamp, phase, payload);
    }

    /// <summary>
    /// Writes a serialized entry to the appropriate slot in the journal area on the device.
    /// Uses circular buffer semantics: when full, overwrites oldest committed/rolled-back entries.
    /// </summary>
    private async Task WriteEntryToDeviceAsync(
        byte[] entryBuffer,
        IPhysicalBlockDevice device,
        CancellationToken ct)
    {
        var journalData = await ReadJournalAreaAsync(device, ct).ConfigureAwait(false);

        // Find the slot to write to: first empty slot, or oldest committed/rolled-back
        var targetSlot = FindWriteSlot(journalData);
        var offset = targetSlot * EntrySize;
        Array.Copy(entryBuffer, 0, journalData, offset, EntrySize);

        await WriteJournalAreaAsync(journalData, device, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the phase of an existing entry (Intent -> Committed or Intent -> RolledBack).
    /// </summary>
    private async Task UpdateEntryPhaseAsync(
        long sequenceNumber,
        JournalEntryPhase newPhase,
        IPhysicalBlockDevice device,
        CancellationToken ct)
    {
        var journalData = await ReadJournalAreaAsync(device, ct).ConfigureAwait(false);

        for (var i = 0; i < MaxEntries; i++)
        {
            var offset = i * EntrySize;
            var seqNum = BinaryPrimitives.ReadInt64LittleEndian(journalData.AsSpan(offset + OffsetSequenceNumber));

            if (seqNum == sequenceNumber)
            {
                // Update phase byte
                journalData[offset + OffsetPhase] = (byte)newPhase;

                // Recompute CRC32
                var crcBytes = Crc32.Hash(journalData.AsSpan(offset, OffsetCrc32));
                var crc = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);
                BinaryPrimitives.WriteUInt32LittleEndian(journalData.AsSpan(offset + OffsetCrc32), crc);

                await WriteJournalAreaAsync(journalData, device, ct).ConfigureAwait(false);
                return;
            }
        }

        // Entry not found — it was evicted from the circular buffer before commit/rollback.
        // Silently succeeding here would leave a dangling Intent on recovery.
        throw new InvalidOperationException(
            $"Journal entry {sequenceNumber} not found in journal area — " +
            $"it may have been evicted by the circular buffer before the phase transition. " +
            $"Recovery must treat this transaction as uncommitted and roll it back.");
    }

    /// <summary>
    /// Finds the best slot to write a new entry. Prefers empty slots,
    /// then overwrites the oldest committed/rolled-back entry.
    /// </summary>
    private static int FindWriteSlot(byte[] journalData)
    {
        var oldestCompletedSlot = -1;
        var oldestCompletedSeq = long.MaxValue;

        for (var i = 0; i < MaxEntries; i++)
        {
            var offset = i * EntrySize;
            var seqNum = BinaryPrimitives.ReadInt64LittleEndian(journalData.AsSpan(offset + OffsetSequenceNumber));

            // Empty slot
            if (seqNum == 0)
            {
                return i;
            }

            // Track oldest committed/rolled-back for circular overwrite
            var phase = (JournalEntryPhase)journalData[offset + OffsetPhase];
            if (phase is JournalEntryPhase.Committed or JournalEntryPhase.RolledBack)
            {
                if (seqNum < oldestCompletedSeq)
                {
                    oldestCompletedSeq = seqNum;
                    oldestCompletedSlot = i;
                }
            }
        }

        // If no empty slots, overwrite oldest completed entry
        return oldestCompletedSlot >= 0 ? oldestCompletedSlot : 0;
    }

    /// <summary>
    /// Reads the entire 32KB journal area (blocks 1-8) from the device.
    /// </summary>
    private static async Task<byte[]> ReadJournalAreaAsync(
        IPhysicalBlockDevice device,
        CancellationToken ct)
    {
        var journalData = new byte[JournalAreaSize];
        var blockSize = device.BlockSize;

        // Calculate which blocks cover the journal area
        var journalStartByteOffset = (long)JournalStartBlock * blockSize;
        var blocksNeeded = (JournalAreaSize + blockSize - 1) / blockSize;

        var bytesRead = 0;
        for (var i = 0; i < blocksNeeded && bytesRead < JournalAreaSize; i++)
        {
            var blockBuffer = new byte[blockSize];
            await device.ReadBlockAsync(JournalStartBlock + i, blockBuffer, ct).ConfigureAwait(false);

            var bytesToCopy = Math.Min(blockSize, JournalAreaSize - bytesRead);
            Array.Copy(blockBuffer, 0, journalData, bytesRead, bytesToCopy);
            bytesRead += bytesToCopy;
        }

        return journalData;
    }

    /// <summary>
    /// Writes the entire 32KB journal area back to the device (blocks 1-8).
    /// </summary>
    private static async Task WriteJournalAreaAsync(
        byte[] journalData,
        IPhysicalBlockDevice device,
        CancellationToken ct)
    {
        var blockSize = device.BlockSize;
        var blocksNeeded = (JournalAreaSize + blockSize - 1) / blockSize;

        var bytesWritten = 0;
        for (var i = 0; i < blocksNeeded && bytesWritten < JournalAreaSize; i++)
        {
            var blockBuffer = new byte[blockSize];
            var bytesToCopy = Math.Min(blockSize, JournalAreaSize - bytesWritten);
            Array.Copy(journalData, bytesWritten, blockBuffer, 0, bytesToCopy);

            await device.WriteBlockAsync(JournalStartBlock + i, blockBuffer, ct).ConfigureAwait(false);
            bytesWritten += bytesToCopy;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
