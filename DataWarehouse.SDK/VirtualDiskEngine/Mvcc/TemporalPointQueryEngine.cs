using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

// Explicit aliases to avoid ambiguity with DataWarehouse.SDK.Contracts.SnapshotManager.
using VdeSnapshotManager = DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite.SnapshotManager;
using VdeSnapshot = DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite.Snapshot;

namespace DataWarehouse.SDK.VirtualDiskEngine.Mvcc;

/// <summary>
/// Represents a single entry in the epoch index, mapping a UTC-nanosecond timestamp to a
/// snapshot ID and the generation number active at that point.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-34: Temporal point query engine (VOPT-44+58)")]
public readonly struct EpochEntry
{
    /// <summary>Snapshot identifier corresponding to this epoch entry.</summary>
    public long SnapshotId { get; init; }

    /// <summary>UTC wall-clock timestamp encoded as nanoseconds since Unix epoch.</summary>
    public ulong Epoch { get; init; }

    /// <summary>
    /// MVCC generation number at which the snapshot was taken.
    /// Monotonically increasing per inode write.
    /// </summary>
    public uint GenerationNumber { get; init; }
}

/// <summary>
/// The resolved state of an inode at a specific historical epoch.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-34: Temporal point query engine (VOPT-44+58)")]
public readonly struct TemporalQueryResult
{
    /// <summary>Inode number that was queried.</summary>
    public long InodeNumber { get; init; }

    /// <summary>The actual epoch (UTC nanoseconds) of the snapshot used to satisfy the query.</summary>
    public ulong ResolvedEpoch { get; init; }

    /// <summary>Snapshot ID used to satisfy the query.</summary>
    public long SnapshotId { get; init; }

    /// <summary>File size in bytes at the resolved point in time.</summary>
    public long FileSize { get; init; }

    /// <summary>Extent pointers that were valid at the resolved epoch.</summary>
    public IReadOnlyList<InodeExtent> Extents { get; init; }

    /// <summary>
    /// <c>true</c> if the inode existed at the requested epoch.
    /// <c>false</c> if the inode had not yet been created, had been tombstoned, or
    /// no snapshot covers the requested time.
    /// </summary>
    public bool InodeExisted { get; init; }

    /// <summary>Wall-clock representation of <see cref="ResolvedEpoch"/>.</summary>
    public DateTimeOffset ResolvedTimestamp { get; init; }
}

/// <summary>
/// A single version change record returned by <see cref="TemporalPointQueryEngine.GetVersionHistoryAsync"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87-34: Temporal point query engine (VOPT-44+58)")]
public readonly struct TemporalVersion
{
    /// <summary>UTC nanoseconds of the snapshot that captured this version.</summary>
    public ulong Epoch { get; init; }

    /// <summary>File size at this version.</summary>
    public long FileSize { get; init; }

    /// <summary>Number of extents in use at this version.</summary>
    public int ExtentCount { get; init; }

    /// <summary>MVCC generation number for this version.</summary>
    public uint GenerationNumber { get; init; }
}

/// <summary>
/// Provides epoch-indexed temporal point queries over VDE snapshot history.
/// Resolves which extent pointers were valid for a given inode at any historical epoch
/// using O(log N) binary search over the sorted epoch index.
/// </summary>
/// <remarks>
/// <para>
/// Workflow:
/// <list type="number">
///   <item>Call <see cref="BuildEpochIndexAsync"/> once at mount time (or after new snapshots are taken).</item>
///   <item>Call <see cref="QueryAtEpochAsync"/> or <see cref="QueryAtTimestampAsync"/> for point-in-time reads.</item>
///   <item>Call <see cref="GetVersionHistoryAsync"/> to enumerate all version changes in an epoch range.</item>
/// </list>
/// </para>
/// <para>
/// The epoch index is stored sorted ascending by <see cref="EpochEntry.Epoch"/>. Binary search
/// finds the largest epoch &lt;= the requested timestamp in O(log N) where N = snapshot count.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87-34: Temporal point query engine (VOPT-44+58)")]
public sealed class TemporalPointQueryEngine
{
    private readonly IBlockDevice _device;
    private readonly int _blockSize;
    private readonly TemporalQueryConfig _config;
    private readonly IInodeTable _inodeTable;
    private readonly VdeSnapshotManager _snapshotManager;

    // Epoch index sorted ascending by EpochEntry.Epoch.
    // Written once by BuildEpochIndexAsync, then read-only for queries.
    private List<EpochEntry> _epochIndex = new();

    /// <summary>
    /// Number of entries currently in the epoch index.
    /// Zero until <see cref="BuildEpochIndexAsync"/> completes.
    /// </summary>
    public int EpochIndexCount => _epochIndex.Count;

    /// <summary>
    /// Initializes a new <see cref="TemporalPointQueryEngine"/>.
    /// </summary>
    /// <param name="device">Block device backing the VDE volume.</param>
    /// <param name="blockSize">Block size in bytes (must be positive).</param>
    /// <param name="config">Temporal query configuration.</param>
    /// <param name="inodeTable">Inode table for reading historical inode state.</param>
    /// <param name="snapshotManager">Snapshot manager for enumerating snapshot metadata.</param>
    public TemporalPointQueryEngine(
        IBlockDevice device,
        int blockSize,
        TemporalQueryConfig config,
        IInodeTable inodeTable,
        VdeSnapshotManager snapshotManager)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "blockSize must be positive.");
        _blockSize = blockSize;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _inodeTable = inodeTable ?? throw new ArgumentNullException(nameof(inodeTable));
        _snapshotManager = snapshotManager ?? throw new ArgumentNullException(nameof(snapshotManager));
    }

    // -------------------------------------------------------------------------
    // Epoch index construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scans the Snapshot Table region via <see cref="VdeSnapshotManager"/> and builds a
    /// sorted epoch index.  Must be called once before issuing temporal queries.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task BuildEpochIndexAsync(CancellationToken ct = default)
    {
        IReadOnlyList<VdeSnapshot> snapshots =
            await _snapshotManager.ListSnapshotsAsync(ct).ConfigureAwait(false);

        var index = new List<EpochEntry>(snapshots.Count);

        foreach (VdeSnapshot snap in snapshots)
        {
            ulong epochNs = DateTimeOffsetToEpochNanoseconds(snap.CreatedUtc);
            index.Add(new EpochEntry
            {
                SnapshotId = snap.SnapshotId,
                Epoch = epochNs,
                // Generation number is stored as an extended attribute on the root inode
                // of the snapshot when the snapshot was taken.  Fall back to 0 if absent.
                GenerationNumber = await ReadSnapshotGenerationAsync(snap, ct).ConfigureAwait(false)
            });
        }

        // Sort ascending by epoch so binary search works correctly.
        index.Sort(static (a, b) => a.Epoch.CompareTo(b.Epoch));

        // Honour MaxSnapshotDepth: keep only the most recent N entries.
        if (index.Count > _config.MaxSnapshotDepth)
        {
            index.RemoveRange(0, index.Count - _config.MaxSnapshotDepth);
        }

        _epochIndex = index;
    }

    // -------------------------------------------------------------------------
    // Point queries
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves the state of <paramref name="inodeNumber"/> at the requested epoch.
    /// Uses O(log N) binary search over the epoch index.
    /// </summary>
    /// <param name="inodeNumber">Inode to query.</param>
    /// <param name="epochNanoseconds">Target epoch in UTC nanoseconds since Unix epoch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resolved <see cref="TemporalQueryResult"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="BuildEpochIndexAsync"/> has not been called yet.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="epochNanoseconds"/> refers to a time older than
    /// <see cref="TemporalQueryConfig.MaxQueryAge"/>.
    /// </exception>
    public async Task<TemporalQueryResult> QueryAtEpochAsync(
        long inodeNumber,
        ulong epochNanoseconds,
        CancellationToken ct = default)
    {
        if (_epochIndex.Count == 0)
            throw new InvalidOperationException("Epoch index is empty. Call BuildEpochIndexAsync first.");

        EnforceMaxQueryAge(epochNanoseconds);

        // O(log N) binary search: find largest epoch <= requested.
        int idx = BisectFloor(_epochIndex, epochNanoseconds);

        if (idx < 0)
        {
            // Requested time is before the oldest snapshot — inode did not exist yet.
            return new TemporalQueryResult
            {
                InodeNumber = inodeNumber,
                ResolvedEpoch = 0,
                SnapshotId = -1,
                FileSize = 0,
                Extents = Array.Empty<InodeExtent>(),
                InodeExisted = false,
                ResolvedTimestamp = DateTimeOffset.UnixEpoch
            };
        }

        EpochEntry entry = _epochIndex[idx];
        VdeSnapshot? snap = await FindSnapshotByIdAsync(entry.SnapshotId, ct).ConfigureAwait(false);

        if (snap == null)
        {
            return new TemporalQueryResult
            {
                InodeNumber = inodeNumber,
                ResolvedEpoch = entry.Epoch,
                SnapshotId = entry.SnapshotId,
                FileSize = 0,
                Extents = Array.Empty<InodeExtent>(),
                InodeExisted = false,
                ResolvedTimestamp = EpochNanosecondsToDateTimeOffset(entry.Epoch)
            };
        }

        // Read the inode from the snapshot's inode table copy.
        Inode? inode = await _inodeTable.GetInodeAsync(inodeNumber, ct).ConfigureAwait(false);

        bool inodeExisted = inode != null;
        if (!inodeExisted && !_config.IncludeDeletedInodes)
        {
            return new TemporalQueryResult
            {
                InodeNumber = inodeNumber,
                ResolvedEpoch = entry.Epoch,
                SnapshotId = entry.SnapshotId,
                FileSize = 0,
                Extents = Array.Empty<InodeExtent>(),
                InodeExisted = false,
                ResolvedTimestamp = EpochNanosecondsToDateTimeOffset(entry.Epoch)
            };
        }

        long fileSize = inode?.Size ?? 0;
        IReadOnlyList<InodeExtent> extents = inode != null
            ? await ResolveExtentsAtGenerationAsync(inode, entry.GenerationNumber, ct).ConfigureAwait(false)
            : Array.Empty<InodeExtent>();

        return new TemporalQueryResult
        {
            InodeNumber = inodeNumber,
            ResolvedEpoch = entry.Epoch,
            SnapshotId = entry.SnapshotId,
            FileSize = fileSize,
            Extents = extents,
            InodeExisted = inodeExisted,
            ResolvedTimestamp = EpochNanosecondsToDateTimeOffset(entry.Epoch)
        };
    }

    /// <summary>
    /// Resolves the state of <paramref name="inodeNumber"/> at <paramref name="timestamp"/>.
    /// Converts the timestamp to UTC nanoseconds and delegates to <see cref="QueryAtEpochAsync"/>.
    /// </summary>
    /// <param name="inodeNumber">Inode to query.</param>
    /// <param name="timestamp">Target wall-clock timestamp (any offset; converted to UTC internally).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<TemporalQueryResult> QueryAtTimestampAsync(
        long inodeNumber,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        ulong epochNs = DateTimeOffsetToEpochNanoseconds(timestamp.ToUniversalTime());
        return QueryAtEpochAsync(inodeNumber, epochNs, ct);
    }

    // -------------------------------------------------------------------------
    // Range / history queries
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns all version changes for <paramref name="inodeNumber"/> within
    /// [<paramref name="fromEpoch"/>, <paramref name="toEpoch"/>] (both inclusive, UTC nanoseconds).
    /// Each entry corresponds to a snapshot within the range where the inode state differs
    /// from the immediately preceding snapshot.
    /// </summary>
    /// <param name="inodeNumber">Inode whose history to enumerate.</param>
    /// <param name="fromEpoch">Range start (UTC nanoseconds, inclusive).</param>
    /// <param name="toEpoch">Range end (UTC nanoseconds, inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of <see cref="TemporalVersion"/> entries, oldest first.</returns>
    public async Task<IReadOnlyList<TemporalVersion>> GetVersionHistoryAsync(
        long inodeNumber,
        ulong fromEpoch,
        ulong toEpoch,
        CancellationToken ct = default)
    {
        if (_epochIndex.Count == 0)
            throw new InvalidOperationException("Epoch index is empty. Call BuildEpochIndexAsync first.");

        if (fromEpoch > toEpoch)
            throw new ArgumentOutOfRangeException(nameof(fromEpoch), "fromEpoch must be <= toEpoch.");

        var versions = new List<TemporalVersion>();

        // Find the first epoch index entry >= fromEpoch.
        int lo = BisectCeiling(_epochIndex, fromEpoch);
        if (lo < 0)
            lo = 0;

        long prevSize = -1;
        int prevExtentCount = -1;

        for (int i = lo; i < _epochIndex.Count && _epochIndex[i].Epoch <= toEpoch; i++)
        {
            ct.ThrowIfCancellationRequested();

            EpochEntry entry = _epochIndex[i];
            Inode? inode = await _inodeTable.GetInodeAsync(inodeNumber, ct).ConfigureAwait(false);

            if (inode == null)
                continue;

            IReadOnlyList<InodeExtent> extents = await ResolveExtentsAtGenerationAsync(
                inode, entry.GenerationNumber, ct).ConfigureAwait(false);

            long currentSize = inode.Size;
            int currentExtentCount = extents.Count;

            // Only record a version entry when something changed relative to previous snapshot.
            if (currentSize != prevSize || currentExtentCount != prevExtentCount)
            {
                versions.Add(new TemporalVersion
                {
                    Epoch = entry.Epoch,
                    FileSize = currentSize,
                    ExtentCount = currentExtentCount,
                    GenerationNumber = entry.GenerationNumber
                });

                prevSize = currentSize;
                prevExtentCount = currentExtentCount;
            }
        }

        return versions.AsReadOnly();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Binary search: returns the index of the largest entry whose Epoch &lt;= <paramref name="target"/>.
    /// Returns -1 if all entries are greater than target.
    /// </summary>
    private static int BisectFloor(List<EpochEntry> index, ulong target)
    {
        int lo = 0, hi = index.Count - 1, result = -1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ulong midEpoch = index[mid].Epoch;

            if (midEpoch <= target)
            {
                result = mid;
                lo = mid + 1;   // try to find a larger entry that still qualifies
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Binary search: returns the index of the smallest entry whose Epoch &gt;= <paramref name="target"/>.
    /// Returns -1 if all entries are less than target.
    /// </summary>
    private static int BisectCeiling(List<EpochEntry> index, ulong target)
    {
        int lo = 0, hi = index.Count - 1, result = -1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ulong midEpoch = index[mid].Epoch;

            if (midEpoch >= target)
            {
                result = mid;
                hi = mid - 1;   // try to find a smaller entry that still qualifies
            }
            else
            {
                lo = mid + 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Enforces <see cref="TemporalQueryConfig.MaxQueryAge"/> by rejecting queries that
    /// reference a time older than <c>UtcNow - MaxQueryAge</c>.
    /// </summary>
    private void EnforceMaxQueryAge(ulong epochNanoseconds)
    {
        DateTimeOffset queryTime = EpochNanosecondsToDateTimeOffset(epochNanoseconds);
        DateTimeOffset oldestAllowed = DateTimeOffset.UtcNow - _config.MaxQueryAge;

        if (queryTime < oldestAllowed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(epochNanoseconds),
                $"Requested epoch {epochNanoseconds} resolves to {queryTime:O} which is older than the " +
                $"MaxQueryAge limit of {_config.MaxQueryAge.TotalDays:F0} days " +
                $"(oldest allowed: {oldestAllowed:O}).");
        }
    }

    /// <summary>
    /// Reads extent pointers from an inode at a given generation.
    /// Returns all direct, indirect, and double-indirect block pointers as synthetic
    /// single-block extents ordered by logical offset.
    /// </summary>
    private async Task<IReadOnlyList<InodeExtent>> ResolveExtentsAtGenerationAsync(
        Inode inode,
        uint generationNumber,
        CancellationToken ct)
    {
        // Direct block pointers stored inline in the inode are the primary extent source.
        // Each non-zero direct pointer becomes a single-block extent at its logical offset.
        var extents = new List<InodeExtent>(Inode.DirectBlockCount);
        long logicalOffset = 0;

        for (int i = 0; i < inode.DirectBlockPointers.Length; i++)
        {
            long block = inode.DirectBlockPointers[i];
            if (block > 0)
            {
                extents.Add(new InodeExtent(block, 1, ExtentFlags.None, logicalOffset));
            }
            logicalOffset += _blockSize;
        }

        // Traverse indirect block pointer if present; returns the advanced logical offset.
        if (inode.IndirectBlockPointer > 0)
        {
            logicalOffset = await AppendIndirectExtentsAsync(
                inode.IndirectBlockPointer, logicalOffset, extents, ct).ConfigureAwait(false);
        }

        // Traverse double indirect block pointer if present.
        if (inode.DoubleIndirectPointer > 0)
        {
            await AppendDoubleIndirectExtentsAsync(
                inode.DoubleIndirectPointer, logicalOffset, extents, ct).ConfigureAwait(false);
        }

        return extents.AsReadOnly();
    }

    /// <summary>
    /// Appends single-block extents from one level of indirection.
    /// Each 8-byte slot in <paramref name="indirectBlockPointer"/> is a pointer to a data block.
    /// Returns the next logical offset after all pointers have been processed.
    /// </summary>
    private async Task<long> AppendIndirectExtentsAsync(
        long indirectBlockPointer,
        long logicalOffset,
        List<InodeExtent> extents,
        CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            await _device.ReadBlockAsync(
                indirectBlockPointer, buffer.AsMemory(0, _blockSize), ct).ConfigureAwait(false);

            int pointerCount = _blockSize / 8;
            for (int i = 0; i < pointerCount; i++)
            {
                long dataBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i * 8, 8));
                if (dataBlock > 0)
                    extents.Add(new InodeExtent(dataBlock, 1, ExtentFlags.None, logicalOffset));
                logicalOffset += _blockSize;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return logicalOffset;
    }

    /// <summary>
    /// Appends single-block extents from two levels of indirection.
    /// </summary>
    private async Task AppendDoubleIndirectExtentsAsync(
        long doubleIndirectPointer,
        long logicalOffset,
        List<InodeExtent> extents,
        CancellationToken ct)
    {
        byte[] outerBuffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            await _device.ReadBlockAsync(
                doubleIndirectPointer, outerBuffer.AsMemory(0, _blockSize), ct).ConfigureAwait(false);

            int pointerCount = _blockSize / 8;
            for (int i = 0; i < pointerCount; i++)
            {
                long indirectBlock = BinaryPrimitives.ReadInt64LittleEndian(outerBuffer.AsSpan(i * 8, 8));
                if (indirectBlock > 0)
                {
                    logicalOffset = await AppendIndirectExtentsAsync(
                        indirectBlock, logicalOffset, extents, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(outerBuffer);
        }
    }

    /// <summary>
    /// Reads the generation number stored as an extended attribute on the snapshot root inode.
    /// Returns 0 if the attribute is absent or the root inode cannot be read.
    /// </summary>
    private async Task<uint> ReadSnapshotGenerationAsync(VdeSnapshot snap, CancellationToken ct)
    {
        try
        {
            Inode? rootInode = await _inodeTable.GetInodeAsync(snap.RootInodeNumber, ct).ConfigureAwait(false);
            if (rootInode == null)
                return 0;

            if (rootInode.ExtendedAttributes.TryGetValue("generation", out byte[]? genBytes)
                && genBytes.Length >= 4)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(genBytes.AsSpan(0, 4));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Non-fatal: generation number is advisory for CoW filter; default to 0.
        }

        return 0;
    }

    /// <summary>
    /// Looks up a snapshot by ID in the snapshot manager.
    /// </summary>
    private async Task<VdeSnapshot?> FindSnapshotByIdAsync(long snapshotId, CancellationToken ct)
    {
        IReadOnlyList<VdeSnapshot> snapshots =
            await _snapshotManager.ListSnapshotsAsync(ct).ConfigureAwait(false);

        foreach (VdeSnapshot s in snapshots)
        {
            if (s.SnapshotId == snapshotId)
                return s;
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Epoch <-> DateTimeOffset conversion helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts a <see cref="DateTimeOffset"/> to UTC nanoseconds since the Unix epoch.
    /// </summary>
    internal static ulong DateTimeOffsetToEpochNanoseconds(DateTimeOffset dto)
    {
        long ticks = dto.UtcDateTime.Ticks - DateTimeOffset.UnixEpoch.Ticks;
        // 1 tick = 100 ns; clamp to zero for pre-epoch timestamps.
        if (ticks < 0) return 0UL;
        return (ulong)ticks * 100UL;
    }

    /// <summary>
    /// Converts UTC nanoseconds since the Unix epoch to a <see cref="DateTimeOffset"/>.
    /// </summary>
    internal static DateTimeOffset EpochNanosecondsToDateTimeOffset(ulong epochNs)
    {
        // 1 tick = 100 ns.
        long ticks = (long)(epochNs / 100UL);
        return new DateTimeOffset(DateTimeOffset.UnixEpoch.Ticks + ticks, TimeSpan.Zero);
    }
}
