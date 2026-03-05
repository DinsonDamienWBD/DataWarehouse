using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.CopyOnWrite;

/// <summary>
/// Result of a single <see cref="InstantCloneEngine.CloneInodeAsync"/> call.
/// </summary>
/// <param name="ClonedInodeNumber">Block number that stores the newly allocated clone inode.</param>
/// <param name="ExtentsShared">Number of extents shared between source and clone (all given SharedCow flag).</param>
/// <param name="BlocksShared">Total physical blocks shared between source and clone.</param>
/// <param name="LogicalBytesCloned">Logical data bytes covered by all shared extents.</param>
/// <param name="ChildrenCloned">Number of child inodes recursively cloned (directory deep clones only).</param>
/// <param name="Duration">Wall-clock duration of the clone operation.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VOPT-47 instant clone result")]
public readonly record struct CloneResult(
    long ClonedInodeNumber,
    int ExtentsShared,
    long BlocksShared,
    long LogicalBytesCloned,
    int ChildrenCloned,
    TimeSpan Duration);

/// <summary>
/// Result of a <see cref="InstantCloneEngine.SplitOnWriteAsync"/> call.
/// </summary>
/// <param name="OldStartBlock">The original (shared) extent start block.</param>
/// <param name="NewStartBlock">The newly allocated (private) extent start block.</param>
/// <param name="BlockCount">Number of blocks in the split extent.</param>
/// <param name="OldBlocksFreed">
/// <see langword="true"/> when the old shared blocks' reference count dropped to zero and
/// they were released back to the allocator.
/// </param>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VOPT-47 CoW split result")]
public readonly record struct CowSplitResult(
    long OldStartBlock,
    long NewStartBlock,
    int BlockCount,
    bool OldBlocksFreed);

/// <summary>
/// Provides O(metadata) instant cloning of files or entire directory trees using copy-on-write
/// semantics. Only inode metadata and extent pointer arrays are duplicated; data blocks are
/// shared lazily via the <see cref="ExtentFlags.SharedCow"/> flag and copied on first write.
/// </summary>
/// <remarks>
/// <para>
/// Clone algorithm (per inode):
/// <list type="number">
///   <item>Read the source <see cref="ExtendedInode512"/> from its dedicated inode block.</item>
///   <item>Allocate a new inode block for the clone.</item>
///   <item>Copy inode metadata (size, type, permissions/xattrs if options allow).</item>
///   <item>Copy all extent pointers verbatim; set <see cref="ExtentFlags.SharedCow"/> on ALL
///         extents in BOTH source AND clone.</item>
///   <item>Increment the reference count for every shared block range via
///         <see cref="ExtentAwareCowManager"/>.</item>
///   <item>Persist both updated inodes and add a directory entry for the clone.</item>
///   <item>If <see cref="CloneOptions.DeepClone"/> is <see langword="true"/> and the source is
///         a directory, recursively clone all children.</item>
/// </list>
/// </para>
/// <para>
/// Write-time CoW split algorithm (per modified extent):
/// <list type="number">
///   <item>Check <see cref="ExtentFlags.SharedCow"/>; if not set, no split needed.</item>
///   <item>Allocate new blocks equal to the extent's <see cref="InodeExtent.BlockCount"/>.</item>
///   <item>Copy data from the shared blocks to the new private blocks.</item>
///   <item>Apply the pending write to the private copy.</item>
///   <item>Update the extent pointer on the writing inode: new start block, clear
///         <see cref="ExtentFlags.SharedCow"/>.</item>
///   <item>Decrement the reference count on the old blocks; free them when count reaches zero.</item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VOPT-47 instant clone engine")]
public sealed class InstantCloneEngine
{
    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly IInodeTable _inodeTable;
    private readonly int _blockSize;
    private readonly ExtentAwareCowManager _cowManager;

    /// <summary>
    /// Creates a new <see cref="InstantCloneEngine"/>.
    /// </summary>
    /// <param name="device">Block device for reading and writing inode and data blocks.</param>
    /// <param name="allocator">Block allocator used to allocate inode blocks and new data blocks.</param>
    /// <param name="inodeTable">
    /// Inode table used for directory entry management (adding the clone entry to the
    /// destination parent directory) and for directory child enumeration during deep clones.
    /// </param>
    /// <param name="blockSize">Block size in bytes (e.g., 4096). Must be positive.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="device"/>, <paramref name="allocator"/>, or
    /// <paramref name="inodeTable"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="blockSize"/> is less than or equal to zero.
    /// </exception>
    public InstantCloneEngine(
        IBlockDevice device,
        IBlockAllocator allocator,
        IInodeTable inodeTable,
        int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _inodeTable = inodeTable ?? throw new ArgumentNullException(nameof(inodeTable));
        _blockSize = blockSize > 0 ? blockSize
            : throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "Block size must be positive.");

        // Re-use extent-aware CoW for reference counting; no WAL needed here because
        // InstantCloneEngine callers are expected to wrap operations in their own WAL transaction.
        // We pass a null WAL guard by using the in-memory ConcurrentDictionary path of
        // ExtentAwareCowManager directly — it doesn't touch the WAL on IncrementRef/DecrementRef.
        // NOTE: ExtentAwareCowManager does require a WAL for CreateSnapshotAsync / CopyOnWriteAsync;
        //       only the ref-count helpers (IncrementRef / DecrementRef) are WAL-free.
        _cowManager = new ExtentAwareCowManager(device, allocator, NullWal.Instance, blockSize);
    }

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Performs an O(metadata) clone of the source inode. Only extent pointers are copied;
    /// data blocks are shared via <see cref="ExtentFlags.SharedCow"/> and copied lazily on
    /// first write.
    /// </summary>
    /// <param name="sourceInodeNumber">
    /// Block number that stores the source <see cref="ExtendedInode512"/> on device.
    /// </param>
    /// <param name="destinationParentInode">
    /// Inode number of the directory that will receive the new clone entry.
    /// </param>
    /// <param name="destinationName">File-system name for the clone entry.</param>
    /// <param name="options">Cloning options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CloneResult"/> describing what was cloned.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="destinationName"/> or <paramref name="options"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="destinationName"/> is empty or whitespace.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the source inode block cannot be read or contains no valid data.
    /// </exception>
    public async Task<CloneResult> CloneInodeAsync(
        long sourceInodeNumber,
        long destinationParentInode,
        string destinationName,
        CloneOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);

        var sw = Stopwatch.StartNew();

        // Step 1: Read source inode
        ExtendedInode512 sourceInode = await ReadExtendedInodeAsync(sourceInodeNumber, ct);

        // Step 2: Allocate a new inode block for the clone
        long cloneInodeNumber = _allocator.AllocateBlock(ct);

        // Step 3: Build clone inode — copy metadata according to options
        long nowTicks = DateTimeOffset.UtcNow.Ticks;
        long nowNs = nowTicks * 100; // ticks → nanoseconds

        var cloneInode = new ExtendedInode512
        {
            InodeNumber = cloneInodeNumber,
            Type = sourceInode.Type,
            Flags = sourceInode.Flags,
            Size = sourceInode.Size,
            AllocatedSize = sourceInode.AllocatedSize,
            ExtentCount = sourceInode.ExtentCount,
            IndirectExtentBlock = sourceInode.IndirectExtentBlock,
            DoubleIndirectBlock = sourceInode.DoubleIndirectBlock,
            // Timestamps
            CreatedUtc = options.PreserveTimestamps ? sourceInode.CreatedUtc : nowTicks,
            ModifiedUtc = options.PreserveTimestamps ? sourceInode.ModifiedUtc : nowTicks,
            AccessedUtc = options.PreserveTimestamps ? sourceInode.AccessedUtc : nowTicks,
            ChangedUtc = options.PreserveTimestamps ? sourceInode.ChangedUtc : nowTicks,
            CreatedNs = options.PreserveTimestamps ? sourceInode.CreatedNs : nowNs,
            ModifiedNs = options.PreserveTimestamps ? sourceInode.ModifiedNs : nowNs,
            AccessedNs = options.PreserveTimestamps ? sourceInode.AccessedNs : nowNs,
            // Permissions
            Permissions = options.ClonePermissions ? sourceInode.Permissions : sourceInode.Permissions,
            OwnerId = options.ClonePermissions ? sourceInode.OwnerId : Guid.Empty,
            LinkCount = 1, // New clone starts with a single link
            // Extended attributes (inline area)
            CompressionDictionaryRef = sourceInode.CompressionDictionaryRef,
            PerObjectEncryptionIv = new byte[ExtendedInode512.EncryptionIvSize],
            ReplicationVector = new byte[ExtendedInode512.ReplicationVectorSize],
        };

        // Copy inline xattr area if requested
        if (options.CloneExtendedAttributes)
        {
            cloneInode.SetInlineXattrs(sourceInode.GetInlineXattrs());
            cloneInode.ExtendedAttributeBlock = sourceInode.ExtendedAttributeBlock;
        }

        // Step 4 & 5: Copy all extent pointers; set SharedCow on both source and clone
        int extentsShared = 0;
        long blocksShared = 0L;
        long logicalBytes = 0L;

        for (int i = 0; i < FormatConstants.MaxExtentsPerInode; i++)
        {
            InodeExtent srcExtent = sourceInode.Extents[i];
            if (srcExtent.IsEmpty)
            {
                cloneInode.Extents[i] = srcExtent;
                continue;
            }

            // Ensure SharedCow is set on BOTH source and clone extents
            var sharedExtent = new InodeExtent(
                srcExtent.StartBlock,
                srcExtent.BlockCount,
                srcExtent.Flags | ExtentFlags.SharedCow,
                srcExtent.LogicalOffset);

            sourceInode.Extents[i] = sharedExtent;
            cloneInode.Extents[i] = sharedExtent;

            // Step 6: Increment block reference counts for shared blocks
            _cowManager.IncrementRef(sharedExtent);

            extentsShared++;
            blocksShared += srcExtent.BlockCount;
            logicalBytes += (long)srcExtent.BlockCount * _blockSize;
        }

        // Step 7: Persist updated source inode (SharedCow flags updated) and new clone inode
        await WriteExtendedInodeAsync(sourceInodeNumber, sourceInode, ct);
        await WriteExtendedInodeAsync(cloneInodeNumber, cloneInode, ct);

        // Step 8: Add directory entry for the clone in the destination parent directory
        var dirEntry = new DirectoryEntry
        {
            InodeNumber = cloneInodeNumber,
            Type = sourceInode.Type,
            Name = destinationName
        };
        await _inodeTable.AddDirectoryEntryAsync(destinationParentInode, dirEntry, ct);

        int childrenCloned = 0;

        // Step 9: Deep clone if requested and source is a directory
        if (options.DeepClone && sourceInode.Type == InodeType.Directory)
        {
            childrenCloned = await DeepCloneDirectoryChildrenAsync(
                sourceInodeNumber, cloneInodeNumber, options, ct);
        }

        sw.Stop();

        return new CloneResult(
            ClonedInodeNumber: cloneInodeNumber,
            ExtentsShared: extentsShared,
            BlocksShared: blocksShared,
            LogicalBytesCloned: logicalBytes,
            ChildrenCloned: childrenCloned,
            Duration: sw.Elapsed);
    }

    /// <summary>
    /// Performs a write-time copy-on-write split for a shared extent belonging to a cloned inode.
    /// The caller is responsible for updating the inode's extent table after this call returns
    /// with the new private extent.
    /// </summary>
    /// <param name="inodeNumber">
    /// Block number that stores the <see cref="ExtendedInode512"/> being written to.
    /// </param>
    /// <param name="extentIndex">
    /// Zero-based index into the inode's <see cref="ExtendedInode512.Extents"/> array
    /// identifying the extent to split.
    /// </param>
    /// <param name="writeOffsetInExtent">Byte offset within the extent at which the write begins.</param>
    /// <param name="writeLength">Number of bytes the write will cover.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CowSplitResult"/> describing the split.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="extentIndex"/> is out of the valid range, or when
    /// <paramref name="writeOffsetInExtent"/> or <paramref name="writeLength"/> is negative.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the inode block cannot be read, or the targeted extent is empty.
    /// </exception>
    public async Task<CowSplitResult> SplitOnWriteAsync(
        long inodeNumber,
        int extentIndex,
        long writeOffsetInExtent,
        int writeLength,
        CancellationToken ct = default)
    {
        if (extentIndex < 0 || extentIndex >= FormatConstants.MaxExtentsPerInode)
            throw new ArgumentOutOfRangeException(nameof(extentIndex), extentIndex,
                $"Extent index must be in [0, {FormatConstants.MaxExtentsPerInode - 1}].");
        if (writeOffsetInExtent < 0)
            throw new ArgumentOutOfRangeException(nameof(writeOffsetInExtent), writeOffsetInExtent,
                "Write offset must be non-negative.");
        if (writeLength < 0)
            throw new ArgumentOutOfRangeException(nameof(writeLength), writeLength,
                "Write length must be non-negative.");

        // Step 1: Read the inode and get the target extent
        ExtendedInode512 inode = await ReadExtendedInodeAsync(inodeNumber, ct);
        InodeExtent extent = inode.Extents[extentIndex];

        if (extent.IsEmpty)
            throw new InvalidOperationException(
                $"Extent at index {extentIndex} in inode block {inodeNumber} is empty.");

        // Step 2: If not SharedCow, no split needed
        if (!extent.IsShared)
        {
            return new CowSplitResult(
                OldStartBlock: extent.StartBlock,
                NewStartBlock: extent.StartBlock,
                BlockCount: extent.BlockCount,
                OldBlocksFreed: false);
        }

        // Step 3: Allocate new private blocks for the entire extent
        long[] newBlocks = _allocator.AllocateExtent(extent.BlockCount, ct);
        long newStartBlock = newBlocks[0];

        // Step 4: Copy data from shared blocks to new private blocks
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            for (int i = 0; i < extent.BlockCount; i++)
            {
                long srcBlock = extent.StartBlock + i;
                await _device.ReadBlockAsync(srcBlock, buffer.AsMemory(0, _blockSize), ct);
                await _device.WriteBlockAsync(newBlocks[i], buffer.AsMemory(0, _blockSize), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Step 5: Decrement reference count on old blocks; determine if they are now free
        _cowManager.DecrementRef(extent);
        int oldRefCount = _cowManager.GetRefCount(extent);
        bool oldBlocksFreed = oldRefCount <= 0;

        if (oldBlocksFreed)
        {
            _allocator.FreeExtent(extent.StartBlock, extent.BlockCount);
        }

        // Step 6: Update the extent pointer on the inode — new start block, clear SharedCow
        var privateExtent = new InodeExtent(
            newStartBlock,
            extent.BlockCount,
            extent.Flags & ~ExtentFlags.SharedCow,
            extent.LogicalOffset);

        inode.Extents[extentIndex] = privateExtent;

        // Persist the updated inode
        await WriteExtendedInodeAsync(inodeNumber, inode, ct);

        return new CowSplitResult(
            OldStartBlock: extent.StartBlock,
            NewStartBlock: newStartBlock,
            BlockCount: extent.BlockCount,
            OldBlocksFreed: oldBlocksFreed);
    }

    // ── Internal helpers ────────────────────────────────────────────────

    /// <summary>
    /// Reads an <see cref="ExtendedInode512"/> from the block identified by
    /// <paramref name="inodeBlockNumber"/>.
    /// </summary>
    private async Task<ExtendedInode512> ReadExtendedInodeAsync(long inodeBlockNumber, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            await _device.ReadBlockAsync(inodeBlockNumber, buffer.AsMemory(0, _blockSize), ct);
            return ExtendedInode512.Deserialize(buffer.AsSpan(0, ExtendedInode512.SerializedSize));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes an <see cref="ExtendedInode512"/> to the block identified by
    /// <paramref name="inodeBlockNumber"/>. Zeroes the remainder of the block.
    /// </summary>
    private async Task WriteExtendedInodeAsync(long inodeBlockNumber, ExtendedInode512 inode, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            Array.Clear(buffer, 0, _blockSize);
            ExtendedInode512.SerializeToSpan(inode, buffer.AsSpan(0, ExtendedInode512.SerializedSize));
            await _device.WriteBlockAsync(inodeBlockNumber, buffer.AsMemory(0, _blockSize), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Recursively clones all child inodes of a source directory into the clone directory.
    /// </summary>
    /// <returns>Total number of child inodes cloned.</returns>
    private async Task<int> DeepCloneDirectoryChildrenAsync(
        long sourceInodeNumber,
        long cloneInodeNumber,
        CloneOptions options,
        CancellationToken ct)
    {
        IReadOnlyList<DirectoryEntry> entries =
            await _inodeTable.ReadDirectoryAsync(sourceInodeNumber, ct);

        int count = 0;
        foreach (DirectoryEntry entry in entries)
        {
            // Skip navigation entries
            if (entry.Name is "." or "..") continue;

            CloneResult childResult = await CloneInodeAsync(
                entry.InodeNumber,
                cloneInodeNumber,
                entry.Name,
                options,
                ct);

            // Each cloned child counts as 1 direct child plus its own recursive children
            count += 1 + childResult.ChildrenCloned;
        }

        return count;
    }

    // ── NullWal inner type ──────────────────────────────────────────────

    /// <summary>
    /// Minimal no-op WAL implementation used internally so that
    /// <see cref="ExtentAwareCowManager"/> can be constructed without a real WAL.
    /// Only <c>IncrementRef</c> and <c>DecrementRef</c> are called on the
    /// cow manager from <see cref="InstantCloneEngine"/>; neither touches the WAL.
    /// </summary>
    private sealed class NullWal : Journal.IWriteAheadLog
    {
        public static readonly NullWal Instance = new();

        private NullWal() { }

        public long CurrentSequenceNumber => 0L;
        public long WalSizeBlocks => 0L;
        public double WalUtilization => 0.0;
        public bool NeedsRecovery => false;

        public Task<Journal.WalTransaction> BeginTransactionAsync(CancellationToken ct = default)
            => Task.FromResult(new Journal.WalTransaction(0, this));

        public Task AppendEntryAsync(Journal.JournalEntry entry, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task FlushAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<System.Collections.Generic.IReadOnlyList<Journal.JournalEntry>> ReplayAsync(CancellationToken ct = default)
            => Task.FromResult<System.Collections.Generic.IReadOnlyList<Journal.JournalEntry>>(
                Array.Empty<Journal.JournalEntry>());

        public Task CheckpointAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
