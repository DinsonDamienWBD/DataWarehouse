using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Operations;

/// <summary>
/// Operation Journal Region (OPJR) — crash-recoverable state store for long-running online
/// operations in the DWVD v2.1 format (VOPT-74).
///
/// The OPJR region consists of:
///   1. A 64-byte region header (magic, counters, timestamp).
///   2. A sequence of 128-byte <see cref="OperationEntry"/> records.
///
/// All header fields and entry fields are serialised into OPJR-tagged blocks with
/// <see cref="UniversalBlockTrailer"/> for crash-safety.  The magic constant
/// <c>0x4F504A524E4C0000</c> encodes the ASCII string "OPJRNL\0\0".
///
/// Crash-recovery workflow:
///   After a power failure the engine calls <see cref="GetOperationsRequiringRecovery"/> to
///   find operations in the <see cref="OperationState.InProgress"/> state and resumes them
///   from the last recorded <see cref="OperationEntry.CompletedUnits"/> checkpoint.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: OPJR operation journal (VOPT-74)")]
public sealed class OperationJournalRegion
{
    // ── Header constants ────────────────────────────────────────────────────

    /// <summary>Size of the OPJR region header in bytes.</summary>
    public const int HeaderSize = 64;

    /// <summary>
    /// Magic number stored at the start of the header.
    /// Encodes "OPJRNL\0\0" as a big-endian uint64.
    /// </summary>
    public const ulong RegionMagic = 0x4F504A524E4C0000UL;

    /// <summary>
    /// Block type tag for OPJR blocks ("OPJR" = 0x4F504A52).
    /// Matches <see cref="BlockTypeTags.OPJR"/>.
    /// </summary>
    public const uint BlockTypeTag = BlockTypeTags.OPJR;

    // ── Header field offsets ────────────────────────────────────────────────
    // +0x00  Magic               ulong  8
    // +0x08  ActiveOperations    uint   4
    // +0x0C  TotalOperations     uint   4
    // +0x10  OldestActiveId      ulong  8
    // +0x18  NewestId            ulong  8
    // +0x20  LastCheckpointUtc   ulong  8  (UTC nanoseconds)
    // +0x28  Reserved            24 bytes
    // Total = 64 bytes

    // ── Internal state ──────────────────────────────────────────────────────

    private readonly List<OperationEntry> _entries;
    private ulong _nextOperationId;
    private ulong _lastCheckpointUtcNanos;
    private uint _activeOperations;
    private uint _totalOperations;
    private ulong _oldestActiveId;
    private ulong _newestId;

    // ── Public header properties ────────────────────────────────────────────

    /// <summary>Count of entries that have not yet reached a terminal state.</summary>
    public uint ActiveOperations => _activeOperations;

    /// <summary>Total number of operation entries ever created (including completed ones).</summary>
    public uint TotalOperations => _totalOperations;

    /// <summary>
    /// Lowest <see cref="OperationEntry.OperationId"/> that is still in a non-terminal state,
    /// or 0 if there are no active operations.
    /// </summary>
    public ulong OldestActiveId => _oldestActiveId;

    /// <summary>Highest <see cref="OperationEntry.OperationId"/> that has been recorded.</summary>
    public ulong NewestId => _newestId;

    /// <summary>
    /// UTC nanoseconds (Unix epoch) of the most recent <see cref="CheckpointProgress"/> call.
    /// 0 if no checkpoint has been written yet.
    /// </summary>
    public ulong LastCheckpointUtc => _lastCheckpointUtcNanos;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    /// <summary>Creates an empty OPJR region (for a freshly formatted volume).</summary>
    public OperationJournalRegion()
    {
        _entries = new List<OperationEntry>();
        _nextOperationId = 1;
        _lastCheckpointUtcNanos = 0;
        _activeOperations = 0;
        _totalOperations = 0;
        _oldestActiveId = 0;
        _newestId = 0;
    }

    private OperationJournalRegion(
        List<OperationEntry> entries,
        ulong nextOperationId,
        ulong lastCheckpointUtcNanos,
        uint activeOperations,
        uint totalOperations,
        ulong oldestActiveId,
        ulong newestId)
    {
        _entries = entries;
        _nextOperationId = nextOperationId;
        _lastCheckpointUtcNanos = lastCheckpointUtcNanos;
        _activeOperations = activeOperations;
        _totalOperations = totalOperations;
        _oldestActiveId = oldestActiveId;
        _newestId = newestId;
    }

    // ── Operation management ────────────────────────────────────────────────

    /// <summary>
    /// Allocates a new operation entry in the <see cref="OperationState.Queued"/> state.
    /// </summary>
    /// <param name="type">The type of operation to track.</param>
    /// <param name="sourceStart">Start block of the source region.</param>
    /// <param name="targetStart">Start block of the target region (0 if not applicable).</param>
    /// <param name="totalUnits">Total work units (e.g. blocks to process).</param>
    /// <param name="payload">Optional operation-specific payload (up to 32 bytes).</param>
    /// <returns>The assigned <see cref="OperationEntry.OperationId"/>.</returns>
    public ulong CreateOperation(
        OperationType type,
        ulong sourceStart,
        ulong targetStart,
        ulong totalUnits,
        byte[]? payload = null)
    {
        var id = _nextOperationId++;
        var nowTicks = (ulong)DateTime.UtcNow.Ticks;

        var entry = new OperationEntry(
            operationId: id,
            type: type,
            state: OperationState.Queued,
            flags: OperationFlags.Pausable | OperationFlags.Cancellable,
            progressPercent: 0,
            startUtcTicks: nowTicks,
            lastUpdateUtcTicks: nowTicks,
            estimatedEndTicks: 0,
            sourceRegionStart: sourceStart,
            targetRegionStart: targetStart,
            totalUnits: totalUnits,
            completedUnits: 0,
            checkpointData: 0,
            operationPayload: payload);

        _entries.Add(entry);
        _totalOperations++;
        _activeOperations++;

        if (_oldestActiveId == 0)
            _oldestActiveId = id;

        if (id > _newestId)
            _newestId = id;

        return id;
    }

    /// <summary>
    /// Transitions an operation to a new state, enforcing the state machine.
    /// Transitions out of terminal states (<see cref="OperationState.Completed"/>,
    /// <see cref="OperationState.Failed"/>, <see cref="OperationState.Cancelled"/>) are rejected.
    /// </summary>
    /// <param name="operationId">The operation to transition.</param>
    /// <param name="newState">The desired new state.</param>
    /// <returns>
    /// <see langword="true"/> if the transition was applied;
    /// <see langword="false"/> if the operation was not found or the transition is invalid.
    /// </returns>
    public bool TransitionState(ulong operationId, OperationState newState)
    {
        int index = FindIndex(operationId);
        if (index < 0)
            return false;

        var current = _entries[index];

        // No outgoing transitions from terminal states.
        if (OperationStateHelper.IsTerminal(current.State))
            return false;

        // A no-op transition is considered valid but makes no change.
        if (current.State == newState)
            return true;

        bool wasActive = !OperationStateHelper.IsTerminal(current.State);
        bool willBeActive = !OperationStateHelper.IsTerminal(newState);

        var updated = new OperationEntry(
            operationId: current.OperationId,
            type: current.Type,
            state: newState,
            flags: current.Flags,
            progressPercent: current.ProgressPercent,
            startUtcTicks: current.StartUtcTicks,
            lastUpdateUtcTicks: (ulong)DateTime.UtcNow.Ticks,
            estimatedEndTicks: current.EstimatedEndTicks,
            sourceRegionStart: current.SourceRegionStart,
            targetRegionStart: current.TargetRegionStart,
            totalUnits: current.TotalUnits,
            completedUnits: current.CompletedUnits,
            checkpointData: current.CheckpointData,
            operationPayload: current.OperationPayload);

        _entries[index] = updated;

        if (wasActive && !willBeActive)
        {
            if (_activeOperations > 0)
                _activeOperations--;

            RefreshOldestActiveId();
        }

        return true;
    }

    /// <summary>
    /// Updates progress for an operation and records a crash-recovery checkpoint.
    /// </summary>
    /// <param name="operationId">The operation to update.</param>
    /// <param name="completedUnits">Total units completed so far (cumulative).</param>
    /// <param name="checkpointData">Operation-specific checkpoint value.</param>
    /// <exception cref="KeyNotFoundException">Operation not found.</exception>
    public void CheckpointProgress(ulong operationId, ulong completedUnits, ulong checkpointData)
    {
        int index = FindIndex(operationId);
        if (index < 0)
            throw new KeyNotFoundException($"Operation {operationId} not found in OPJR.");

        var current = _entries[index];
        var nowTicks = (ulong)DateTime.UtcNow.Ticks;

        uint progress = current.TotalUnits > 0
            ? (uint)Math.Min(1_000_000UL, completedUnits * 1_000_000UL / current.TotalUnits)
            : 0u;

        var updated = new OperationEntry(
            operationId: current.OperationId,
            type: current.Type,
            state: current.State,
            flags: current.Flags,
            progressPercent: progress,
            startUtcTicks: current.StartUtcTicks,
            lastUpdateUtcTicks: nowTicks,
            estimatedEndTicks: current.EstimatedEndTicks,
            sourceRegionStart: current.SourceRegionStart,
            targetRegionStart: current.TargetRegionStart,
            totalUnits: current.TotalUnits,
            completedUnits: completedUnits,
            checkpointData: checkpointData,
            operationPayload: current.OperationPayload);

        _entries[index] = updated;

        // Record checkpoint UTC as nanoseconds (ticks * 100).
        _lastCheckpointUtcNanos = nowTicks * 100UL;
    }

    /// <summary>
    /// Returns the operation entry for the given <paramref name="operationId"/>, or
    /// <see langword="null"/> if not found.
    /// </summary>
    public OperationEntry? GetOperation(ulong operationId)
    {
        int index = FindIndex(operationId);
        return index >= 0 ? _entries[index] : null;
    }

    /// <summary>
    /// Returns all entries that have not yet reached a terminal state.
    /// </summary>
    public IReadOnlyList<OperationEntry> GetActiveOperations() =>
        _entries.Where(e => !OperationStateHelper.IsTerminal(e.State)).ToList();

    /// <summary>
    /// Returns entries in the <see cref="OperationState.InProgress"/> state.
    /// These are operations that were running when the engine crashed and need to be
    /// resumed from their last <see cref="OperationEntry.CompletedUnits"/> checkpoint.
    /// </summary>
    public IReadOnlyList<OperationEntry> GetOperationsRequiringRecovery() =>
        _entries.Where(e => e.State == OperationState.InProgress).ToList();

    // ── Serialisation ────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the number of <paramref name="blockSize"/>-byte blocks required to serialise
    /// the current header and all entries.
    /// </summary>
    /// <param name="blockSize">Block size in bytes (must be &gt; <see cref="UniversalBlockTrailer.Size"/>).</param>
    /// <returns>Block count (at least 1).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Block size is too small.</exception>
    public int RequiredBlocks(int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockSize, UniversalBlockTrailer.Size);

        int payloadPerBlock = blockSize - UniversalBlockTrailer.Size;
        long totalBytes = HeaderSize + (long)_entries.Count * OperationEntry.Size;
        return (int)((totalBytes + payloadPerBlock - 1) / payloadPerBlock);
    }

    /// <summary>
    /// Serialises the OPJR region (header + all entries) into <paramref name="buffer"/>.
    /// Each block receives an <see cref="UniversalBlockTrailer"/> tagged with
    /// <see cref="BlockTypeTag"/>.
    /// </summary>
    /// <param name="buffer">Output buffer.  Must be at least
    /// <c><see cref="RequiredBlocks"/>(blockSize) * blockSize</c> bytes.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <exception cref="ArgumentException">Buffer too small or block size invalid.</exception>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockSize, UniversalBlockTrailer.Size);

        int needed = RequiredBlocks(blockSize) * blockSize;
        if (buffer.Length < needed)
            throw new ArgumentException(
                $"Buffer must be at least {needed} bytes for blockSize={blockSize}.", nameof(buffer));

        int payloadPerBlock = blockSize - UniversalBlockTrailer.Size;

        // Build a contiguous payload stream: header + entries.
        byte[] payload = BuildPayloadStream();

        int written = 0;
        int payloadOffset = 0;
        uint generation = 1;

        while (payloadOffset < payload.Length)
        {
            int blockStart = written;
            var blockSpan = buffer.Slice(blockStart, blockSize);
            blockSpan.Clear();

            int chunkLen = Math.Min(payloadPerBlock, payload.Length - payloadOffset);
            payload.AsSpan(payloadOffset, chunkLen).CopyTo(blockSpan);

            UniversalBlockTrailer.Write(blockSpan, blockSize, BlockTypeTag, generation++);

            payloadOffset += chunkLen;
            written += blockSize;
        }
    }

    /// <summary>
    /// Reads an <see cref="OperationJournalRegion"/> from <paramref name="buffer"/>.
    /// Validates the region magic and all block trailers.
    /// </summary>
    /// <param name="buffer">Buffer containing the serialised region.</param>
    /// <param name="blockSize">Block size in bytes used during serialisation.</param>
    /// <param name="blockCount">Number of blocks to read.</param>
    /// <returns>A fully populated <see cref="OperationJournalRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Magic mismatch or checksum failure.</exception>
    public static OperationJournalRegion Deserialize(
        ReadOnlySpan<byte> buffer,
        int blockSize,
        int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockSize, UniversalBlockTrailer.Size);

        int totalBytes = blockCount * blockSize;
        if (buffer.Length < totalBytes)
            throw new ArgumentException(
                $"Buffer must be at least {totalBytes} bytes.", nameof(buffer));

        int payloadPerBlock = blockSize - UniversalBlockTrailer.Size;

        // Reassemble contiguous payload.
        byte[] payload = new byte[(long)blockCount * payloadPerBlock];
        int payloadOffset = 0;

        for (int i = 0; i < blockCount; i++)
        {
            var blockSpan = buffer.Slice(i * blockSize, blockSize);

            if (!UniversalBlockTrailer.Verify(blockSpan, blockSize))
                throw new InvalidDataException(
                    $"OPJR block {i}: checksum verification failed.");

            blockSpan.Slice(0, payloadPerBlock).CopyTo(payload.AsSpan(payloadOffset));
            payloadOffset += payloadPerBlock;
        }

        // Parse header.
        var ps = payload.AsSpan();

        var magic = BinaryPrimitives.ReadUInt64BigEndian(ps.Slice(0x00));
        if (magic != RegionMagic)
            throw new InvalidDataException(
                $"OPJR magic mismatch: expected 0x{RegionMagic:X16}, got 0x{magic:X16}.");

        var activeOps = BinaryPrimitives.ReadUInt32LittleEndian(ps.Slice(0x08));
        var totalOps = BinaryPrimitives.ReadUInt32LittleEndian(ps.Slice(0x0C));
        var oldestActive = BinaryPrimitives.ReadUInt64LittleEndian(ps.Slice(0x10));
        var newestId = BinaryPrimitives.ReadUInt64LittleEndian(ps.Slice(0x18));
        var lastCheckpointUtc = BinaryPrimitives.ReadUInt64LittleEndian(ps.Slice(0x20));

        // Parse entries.
        var entries = new List<OperationEntry>();
        int entryOffset = HeaderSize;
        int entryCount = (int)totalOps;

        for (int i = 0; i < entryCount && entryOffset + OperationEntry.Size <= payload.Length; i++)
        {
            var entry = OperationEntry.ReadFrom(ps.Slice(entryOffset, OperationEntry.Size));
            entries.Add(entry);
            entryOffset += OperationEntry.Size;
        }

        return new OperationJournalRegion(
            entries: entries,
            nextOperationId: newestId + 1,
            lastCheckpointUtcNanos: lastCheckpointUtc,
            activeOperations: activeOps,
            totalOperations: totalOps,
            oldestActiveId: oldestActive,
            newestId: newestId);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private int FindIndex(ulong operationId)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].OperationId == operationId)
                return i;
        }
        return -1;
    }

    private void RefreshOldestActiveId()
    {
        ulong oldest = 0;
        foreach (var e in _entries)
        {
            if (!OperationStateHelper.IsTerminal(e.State))
            {
                if (oldest == 0 || e.OperationId < oldest)
                    oldest = e.OperationId;
            }
        }
        _oldestActiveId = oldest;
    }

    private byte[] BuildPayloadStream()
    {
        // Header: 64 bytes + entries: count * 128 bytes.
        int entryBytes = _entries.Count * OperationEntry.Size;
        byte[] payload = new byte[HeaderSize + entryBytes];
        var span = payload.AsSpan();

        // Write header.
        BinaryPrimitives.WriteUInt64BigEndian(span.Slice(0x00), RegionMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x08), _activeOperations);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0x0C), _totalOperations);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(0x10), _oldestActiveId);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(0x18), _newestId);
        BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(0x20), _lastCheckpointUtcNanos);
        // +0x28: 24 reserved bytes already zeroed.

        // Write entries.
        int offset = HeaderSize;
        foreach (var entry in _entries)
        {
            entry.WriteTo(span.Slice(offset, OperationEntry.Size));
            offset += OperationEntry.Size;
        }

        return payload;
    }
}
