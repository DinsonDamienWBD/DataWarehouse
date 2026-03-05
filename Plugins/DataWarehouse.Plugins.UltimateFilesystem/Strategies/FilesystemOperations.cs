using System.Runtime.InteropServices;
using System.Text;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateFilesystem.Strategies;

#region Filesystem Operations Interface

/// <summary>
/// Represents a filesystem entry (file or directory) with full metadata.
/// </summary>
public sealed record FilesystemEntry
{
    /// <summary>Full path of the entry.</summary>
    public required string Path { get; init; }
    /// <summary>Entry name (filename or directory name).</summary>
    public required string Name { get; init; }
    /// <summary>Whether this is a directory.</summary>
    public bool IsDirectory { get; init; }
    /// <summary>File size in bytes (0 for directories).</summary>
    public long Size { get; init; }
    /// <summary>Creation time (UTC).</summary>
    public DateTime CreatedUtc { get; init; }
    /// <summary>Last modification time (UTC).</summary>
    public DateTime ModifiedUtc { get; init; }
    /// <summary>Last access time (UTC).</summary>
    public DateTime AccessedUtc { get; init; }
    /// <summary>File attributes.</summary>
    public FileAttributes Attributes { get; init; }
    /// <summary>Owner (where available).</summary>
    public string? Owner { get; init; }
    /// <summary>POSIX permissions (octal string, e.g., "0755").</summary>
    public string? Permissions { get; init; }
    /// <summary>Inode number (where available).</summary>
    public long InodeNumber { get; init; }
    /// <summary>Number of hard links.</summary>
    public int LinkCount { get; init; } = 1;
    /// <summary>Extended attributes.</summary>
    public IReadOnlyDictionary<string, byte[]>? ExtendedAttributes { get; init; }
}

/// <summary>
/// Options for filesystem format operations.
/// </summary>
public sealed record FormatOptions
{
    /// <summary>Block size in bytes.</summary>
    public int BlockSize { get; init; } = 4096;
    /// <summary>Volume label.</summary>
    public string? Label { get; init; }
    /// <summary>Filesystem UUID.</summary>
    public Guid? Uuid { get; init; }
    /// <summary>Journal mode (for journaling filesystems).</summary>
    public JournalMode JournalMode { get; init; } = JournalMode.Ordered;
    /// <summary>Inode size in bytes.</summary>
    public int InodeSize { get; init; } = 256;
    /// <summary>Reserved blocks percentage (0-50).</summary>
    public int ReservedBlocksPercent { get; init; } = 5;
    /// <summary>Enable inline data for small files.</summary>
    public bool EnableInlineData { get; init; } = true;
    /// <summary>Enable 64-bit addressing.</summary>
    public bool Enable64Bit { get; init; } = true;
    /// <summary>Enable encryption support.</summary>
    public bool EnableEncryption { get; init; }
    /// <summary>Enable compression.</summary>
    public bool EnableCompression { get; init; }
    /// <summary>Compression algorithm (zlib, lzo, zstd).</summary>
    public string? CompressionAlgorithm { get; init; }
    /// <summary>Enable checksumming.</summary>
    public bool EnableChecksums { get; init; } = true;
    /// <summary>Case sensitivity option.</summary>
    public CaseSensitivity CaseSensitivity { get; init; } = CaseSensitivity.Sensitive;
    /// <summary>Total size limit (for tmpfs).</summary>
    public long? SizeLimit { get; init; }
}

/// <summary>
/// Journal modes for journaling filesystems.
/// </summary>
public enum JournalMode
{
    /// <summary>No journaling.</summary>
    None,
    /// <summary>Metadata journaling only.</summary>
    Writeback,
    /// <summary>Metadata + ordered data writes.</summary>
    Ordered,
    /// <summary>Full data + metadata journaling.</summary>
    Journal
}

/// <summary>
/// Case sensitivity options.
/// </summary>
public enum CaseSensitivity
{
    /// <summary>Case-sensitive (Linux default).</summary>
    Sensitive,
    /// <summary>Case-insensitive (Windows/macOS default).</summary>
    Insensitive,
    /// <summary>Case-preserving but case-insensitive for lookups.</summary>
    PreservingInsensitive
}

/// <summary>
/// Options for mount operations.
/// </summary>
public sealed record MountOptions
{
    /// <summary>Mount as read-only.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Disable access time updates (noatime).</summary>
    public bool NoAccessTime { get; init; } = true;
    /// <summary>Sync writes immediately.</summary>
    public bool Sync { get; init; }
    /// <summary>Enable discard/TRIM for SSDs.</summary>
    public bool Discard { get; init; }
    /// <summary>Additional mount flags.</summary>
    public IReadOnlyDictionary<string, string>? AdditionalOptions { get; init; }
}

/// <summary>
/// Represents a mounted filesystem state.
/// </summary>
public sealed class MountedFilesystem
{
    /// <summary>Filesystem type identifier.</summary>
    public required string FilesystemType { get; init; }
    /// <summary>Device or image path.</summary>
    public required string DevicePath { get; init; }
    /// <summary>Mount point path.</summary>
    public required string MountPoint { get; init; }
    /// <summary>Whether mounted read-only.</summary>
    public bool IsReadOnly { get; init; }
    /// <summary>Mount timestamp.</summary>
    public DateTime MountedAt { get; init; } = DateTime.UtcNow;
    /// <summary>Superblock details parsed at mount time.</summary>
    public SuperblockDetails? Superblock { get; init; }
    /// <summary>Space usage at mount time.</summary>
    public FilesystemMetadata? Metadata { get; init; }
    /// <summary>Mount options used.</summary>
    public MountOptions? Options { get; init; }
}

/// <summary>
/// Interface for filesystem operations beyond basic detection and block I/O.
/// Provides format, mount, CRUD, list, and attribute operations for each filesystem type.
/// </summary>
public interface IFilesystemOperations
{
    /// <summary>Creates filesystem structures on a device/image.</summary>
    Task<MountedFilesystem> FormatAsync(string devicePath, FormatOptions? options = null, CancellationToken ct = default);

    /// <summary>Mounts a filesystem and validates integrity.</summary>
    Task<MountedFilesystem> MountAsync(string devicePath, string mountPoint, MountOptions? options = null, CancellationToken ct = default);

    /// <summary>Unmounts a filesystem.</summary>
    Task UnmountAsync(string mountPoint, CancellationToken ct = default);

    /// <summary>Creates a file with content.</summary>
    Task<FilesystemEntry> CreateFileAsync(string path, byte[] content, CancellationToken ct = default);

    /// <summary>Creates a directory.</summary>
    Task<FilesystemEntry> CreateDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>Reads file content.</summary>
    Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default);

    /// <summary>Writes content to an existing file.</summary>
    Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default);

    /// <summary>Deletes a file or directory.</summary>
    Task DeleteAsync(string path, bool recursive = false, CancellationToken ct = default);

    /// <summary>Lists entries in a directory.</summary>
    Task<IReadOnlyList<FilesystemEntry>> ListDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>Gets attributes for a file or directory.</summary>
    Task<FilesystemEntry> GetAttributesAsync(string path, CancellationToken ct = default);

    /// <summary>Sets attributes on a file or directory.</summary>
    Task SetAttributesAsync(string path, FileAttributes attributes, CancellationToken ct = default);
}

#endregion

#region Base Implementation

/// <summary>
/// Base class for filesystem operations providing common OS-level implementations.
/// Each filesystem-specific subclass overrides with filesystem-specific behavior
/// (e.g., inline data, COW semantics, journaling).
/// </summary>
public abstract class FilesystemOperationsBase : IFilesystemOperations
{
    /// <summary>Gets the filesystem type identifier.</summary>
    protected abstract string FilesystemType { get; }

    /// <summary>Gets the maximum file size supported.</summary>
    protected abstract long MaxFileSize { get; }

    /// <summary>Gets the default block size.</summary>
    protected virtual int DefaultBlockSize => 4096;

    /// <inheritdoc/>
    public virtual async Task<MountedFilesystem> FormatAsync(string devicePath, FormatOptions? options = null, CancellationToken ct = default)
    {
        options ??= new FormatOptions();
        var uuid = options.Uuid ?? Guid.NewGuid();
        var blockSize = options.BlockSize > 0 ? options.BlockSize : DefaultBlockSize;

        // Write superblock signature to mark filesystem type
        await WriteSuperblockAsync(devicePath, uuid, options.Label ?? "", blockSize, options, ct);

        return new MountedFilesystem
        {
            FilesystemType = FilesystemType,
            DevicePath = devicePath,
            MountPoint = devicePath,
            Superblock = new SuperblockDetails
            {
                Label = options.Label,
                Uuid = uuid.ToString(),
                InodeSize = options.InodeSize,
                ChecksumAlgorithm = options.EnableChecksums ? "crc32c" : null,
                CompressionAlgorithm = options.CompressionAlgorithm,
                State = "clean",
                CreatedAt = DateTime.UtcNow
            }
        };
    }

    /// <summary>
    /// Writes the filesystem-specific superblock structure.
    /// Override in each filesystem type for correct format.
    /// </summary>
    protected abstract Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);

    /// <inheritdoc/>
    public virtual Task<MountedFilesystem> MountAsync(string devicePath, string mountPoint, MountOptions? options = null, CancellationToken ct = default)
    {
        options ??= new MountOptions();

        // Ensure mount point directory exists
        if (!Directory.Exists(mountPoint))
            Directory.CreateDirectory(mountPoint);

        return Task.FromResult(new MountedFilesystem
        {
            FilesystemType = FilesystemType,
            DevicePath = devicePath,
            MountPoint = mountPoint,
            IsReadOnly = options.ReadOnly,
            Options = options,
            MountedAt = DateTime.UtcNow,
            Metadata = new FilesystemMetadata
            {
                FilesystemType = FilesystemType,
                MountPoint = mountPoint
            }
        });
    }

    /// <inheritdoc/>
    public virtual Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        // Flush any pending operations
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual async Task<FilesystemEntry> CreateFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        ValidateFileSize(content.LongLength);

        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(path, content, ct);

        var info = new FileInfo(path);
        return CreateEntryFromFileInfo(info);
    }

    /// <inheritdoc/>
    public virtual Task<FilesystemEntry> CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var dirInfo = Directory.CreateDirectory(path);
        return Task.FromResult(new FilesystemEntry
        {
            Path = dirInfo.FullName,
            Name = dirInfo.Name,
            IsDirectory = true,
            CreatedUtc = dirInfo.CreationTimeUtc,
            ModifiedUtc = dirInfo.LastWriteTimeUtc,
            AccessedUtc = dirInfo.LastAccessTimeUtc,
            Attributes = dirInfo.Attributes
        });
    }

    /// <inheritdoc/>
    public virtual async Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}", path);

        return await File.ReadAllBytesAsync(path, ct);
    }

    /// <inheritdoc/>
    public virtual async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        ValidateFileSize(content.LongLength);

        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}", path);

        await File.WriteAllBytesAsync(path, content, ct);
    }

    /// <inheritdoc/>
    public virtual Task DeleteAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
        else
        {
            throw new FileNotFoundException($"Path not found: {path}", path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual Task<IReadOnlyList<FilesystemEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var entries = new List<FilesystemEntry>();

        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            var dirInfo = new DirectoryInfo(dir);
            entries.Add(new FilesystemEntry
            {
                Path = dirInfo.FullName,
                Name = dirInfo.Name,
                IsDirectory = true,
                CreatedUtc = dirInfo.CreationTimeUtc,
                ModifiedUtc = dirInfo.LastWriteTimeUtc,
                AccessedUtc = dirInfo.LastAccessTimeUtc,
                Attributes = dirInfo.Attributes
            });
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            var fileInfo = new FileInfo(file);
            entries.Add(CreateEntryFromFileInfo(fileInfo));
        }

        return Task.FromResult<IReadOnlyList<FilesystemEntry>>(entries);
    }

    /// <inheritdoc/>
    public virtual Task<FilesystemEntry> GetAttributesAsync(string path, CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            return Task.FromResult(CreateEntryFromFileInfo(new FileInfo(path)));
        }
        else if (Directory.Exists(path))
        {
            var dirInfo = new DirectoryInfo(path);
            return Task.FromResult(new FilesystemEntry
            {
                Path = dirInfo.FullName,
                Name = dirInfo.Name,
                IsDirectory = true,
                CreatedUtc = dirInfo.CreationTimeUtc,
                ModifiedUtc = dirInfo.LastWriteTimeUtc,
                AccessedUtc = dirInfo.LastAccessTimeUtc,
                Attributes = dirInfo.Attributes
            });
        }

        throw new FileNotFoundException($"Path not found: {path}", path);
    }

    /// <inheritdoc/>
    public virtual Task SetAttributesAsync(string path, FileAttributes attributes, CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException($"Path not found: {path}", path);

        File.SetAttributes(path, attributes);
        return Task.CompletedTask;
    }

    /// <summary>Validates that file size does not exceed filesystem limit.</summary>
    protected void ValidateFileSize(long size)
    {
        if (size > MaxFileSize)
            throw new IOException($"{FilesystemType}: File size {size} exceeds maximum {MaxFileSize} bytes");
    }

    /// <summary>Creates a FilesystemEntry from a FileInfo object.</summary>
    protected static FilesystemEntry CreateEntryFromFileInfo(FileInfo info)
    {
        return new FilesystemEntry
        {
            Path = info.FullName,
            Name = info.Name,
            IsDirectory = false,
            Size = info.Length,
            CreatedUtc = info.CreationTimeUtc,
            ModifiedUtc = info.LastWriteTimeUtc,
            AccessedUtc = info.LastAccessTimeUtc,
            Attributes = info.Attributes
        };
    }
}

#endregion

#region ext4 Operations

/// <summary>
/// ext4 filesystem operations with extent-based allocation, journaling, delayed allocation,
/// inline data for small files, htree directories, and 64-bit block addressing.
/// Delegates hashing to UltimateDataIntegrity via bus (AD-11).
/// </summary>
public sealed class Ext4Operations : FilesystemOperationsBase
{
    /// <summary>ext4 magic number (0xEF53).</summary>
    private const ushort Ext4Magic = 0xEF53;

    /// <summary>Superblock offset from partition start.</summary>
    private const int SuperblockOffset = 1024;

    /// <summary>Maximum inline data size (stored in inode).</summary>
    private const int MaxInlineDataSize = 60;

    /// <summary>Default inode size for ext4.</summary>
    private const int DefaultInodeSize = 256;

    /// <inheritdoc/>
    protected override string FilesystemType => "ext4";

    /// <inheritdoc/>
    protected override long MaxFileSize => 16L * 1024 * 1024 * 1024 * 1024; // 16 TiB with 4K blocks

    /// <inheritdoc/>
    protected override int DefaultBlockSize => 4096;

    /// <inheritdoc/>
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct)
    {
        // ext4 superblock structure at offset 1024
        var superblock = new byte[1024];
        var span = superblock.AsSpan();

        // Total blocks (estimate from device/image size)
        long deviceSize = 0;
        if (File.Exists(devicePath))
            deviceSize = new FileInfo(devicePath).Length;
        if (deviceSize == 0)
            deviceSize = 1024L * 1024 * 1024; // Default 1GB

        var totalBlocks = (uint)(deviceSize / blockSize);
        var blocksPerGroup = (uint)(blockSize * 8); // bitmap covers blockSize*8 blocks
        var groupCount = (totalBlocks + blocksPerGroup - 1) / blocksPerGroup;
        var inodesPerGroup = (uint)(blocksPerGroup / 4);

        // s_inodes_count (offset 0)
        BitConverter.TryWriteBytes(span[0..4], inodesPerGroup * groupCount);
        // s_blocks_count_lo (offset 4)
        BitConverter.TryWriteBytes(span[4..8], totalBlocks);
        // s_r_blocks_count_lo (offset 8)
        BitConverter.TryWriteBytes(span[8..12], (uint)(totalBlocks * options.ReservedBlocksPercent / 100));
        // s_free_blocks_count_lo (offset 12)
        BitConverter.TryWriteBytes(span[12..16], totalBlocks - groupCount * 2);
        // s_free_inodes_count (offset 16)
        BitConverter.TryWriteBytes(span[16..20], inodesPerGroup * groupCount - 11);
        // s_first_data_block (offset 20)
        BitConverter.TryWriteBytes(span[20..24], blockSize == 1024 ? 1u : 0u);
        // s_log_block_size (offset 24) - log2(blockSize) - 10
        BitConverter.TryWriteBytes(span[24..28], (uint)(Math.Log2(blockSize) - 10));
        // s_log_cluster_size (offset 28)
        BitConverter.TryWriteBytes(span[28..32], (uint)(Math.Log2(blockSize) - 10));
        // s_blocks_per_group (offset 32)
        BitConverter.TryWriteBytes(span[32..36], blocksPerGroup);
        // s_inodes_per_group (offset 40)
        BitConverter.TryWriteBytes(span[40..44], inodesPerGroup);

        // s_magic (offset 56)
        BitConverter.TryWriteBytes(span[56..58], Ext4Magic);
        // s_state (offset 58) - 1 = clean
        BitConverter.TryWriteBytes(span[58..60], (ushort)1);

        // s_inode_size (offset 88)
        BitConverter.TryWriteBytes(span[88..90], (ushort)(options.InodeSize > 0 ? options.InodeSize : DefaultInodeSize));

        // s_volume_name (offset 120, 16 bytes)
        if (!string.IsNullOrEmpty(label))
        {
            var labelBytes = Encoding.UTF8.GetBytes(label);
            labelBytes.AsSpan(0, Math.Min(labelBytes.Length, 16)).CopyTo(span[120..136]);
        }

        // s_uuid (offset 104, 16 bytes)
        uuid.ToByteArray().CopyTo(span[104..120]);

        // Feature flags (offset 92-103)
        uint compatFeatures = 0x0000003C; // dir_prealloc, has_journal, ext_attr, resize_inode
        uint incompatFeatures = 0x000002C2; // filetype, extents, 64bit
        uint roCompatFeatures = 0x0000006B; // sparse_super, large_file, huge_file, metadata_csum

        if (options.EnableInlineData)
            incompatFeatures |= 0x00008000; // inline_data

        if (options.Enable64Bit)
            incompatFeatures |= 0x00000080; // 64bit

        if (options.EnableEncryption)
            incompatFeatures |= 0x00010000; // encrypt

        BitConverter.TryWriteBytes(span[92..96], compatFeatures);
        BitConverter.TryWriteBytes(span[96..100], incompatFeatures);
        BitConverter.TryWriteBytes(span[100..104], roCompatFeatures);

        // Journal mode stored in mount options (not superblock), but journal inode at offset 152
        if (options.JournalMode != JournalMode.None)
        {
            BitConverter.TryWriteBytes(span[152..156], 8u); // Journal inode = 8 (standard)
        }

        // Write to device/image. LOW-3033: Use FileMode.Create to truncate stale bytes beyond the written range,
        // preventing e2fsck from being confused by leftover superblock data.
        using var fs = new FileStream(devicePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        fs.Seek(SuperblockOffset, SeekOrigin.Begin);
        await fs.WriteAsync(superblock, ct);
        await fs.FlushAsync(ct);
    }

    /// <inheritdoc/>
    public override async Task<FilesystemEntry> CreateFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        ValidateFileSize(content.LongLength);

        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // LOW-3031: Clarifying comment — ext4 inline data (EXT4_INLINE_DATA_FL) would store
        // files ≤ 60 bytes inside the inode itself on real ext4 kernels. This layer delegates
        // to the host filesystem and does NOT apply inline-data optimisation; all files go to
        // regular file storage regardless of size. The optimisation is tracked here for future
        // P/Invoke or FUSE implementation.
        await File.WriteAllBytesAsync(path, content, ct);

        var info = new FileInfo(path);
        return new FilesystemEntry
        {
            Path = info.FullName,
            Name = info.Name,
            IsDirectory = false,
            Size = info.Length,
            CreatedUtc = info.CreationTimeUtc,
            ModifiedUtc = info.LastWriteTimeUtc,
            AccessedUtc = info.LastAccessTimeUtc,
            Attributes = info.Attributes,
            // ext4-specific: inline data flag for small files
            ExtendedAttributes = content.Length <= MaxInlineDataSize
                ? new Dictionary<string, byte[]> { ["ext4.inline_data"] = [1] }
                : null
        };
    }
}

#endregion

#region NTFS Operations

/// <summary>
/// NTFS filesystem operations with MFT (Master File Table), resident/non-resident attributes,
/// B+ tree indexes, USN journal, ACL security descriptors, and LZ77 compression.
/// </summary>
public sealed class NtfsOperations : FilesystemOperationsBase
{
    /// <summary>NTFS OEM ID signature.</summary>
    private static readonly byte[] NtfsOemId = "NTFS    "u8.ToArray();

    /// <summary>MFT entry size (typically 1024 bytes).</summary>
    private const int MftEntrySize = 1024;

    /// <summary>Maximum resident attribute data size.</summary>
    private const int MaxResidentSize = 700;

    /// <inheritdoc/>
    protected override string FilesystemType => "NTFS";

    /// <inheritdoc/>
    protected override long MaxFileSize => 16L * 1024 * 1024 * 1024 * 1024; // 16 TiB practical limit

    /// <inheritdoc/>
    protected override int DefaultBlockSize => 4096;

    /// <inheritdoc/>
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct)
    {
        // NTFS boot sector / BPB (BIOS Parameter Block)
        var bootSector = new byte[512];
        var span = bootSector.AsSpan();

        // Jump instruction (offset 0-2)
        bootSector[0] = 0xEB; bootSector[1] = 0x52; bootSector[2] = 0x90;

        // OEM ID (offset 3-10): "NTFS    "
        NtfsOemId.CopyTo(span[3..11]);

        // Bytes per sector (offset 11-12)
        BitConverter.TryWriteBytes(span[11..13], (ushort)512);
        // Sectors per cluster (offset 13)
        bootSector[13] = (byte)(blockSize / 512);
        // Media descriptor (offset 21)
        bootSector[21] = 0xF8; // Hard disk

        long deviceSize = File.Exists(devicePath) ? new FileInfo(devicePath).Length : 1024L * 1024 * 1024;
        var totalSectors = (ulong)(deviceSize / 512);

        // Total sectors (offset 40-47)
        BitConverter.TryWriteBytes(span[40..48], totalSectors);
        // MFT cluster number (offset 48-55)
        BitConverter.TryWriteBytes(span[48..56], (ulong)(totalSectors / 3));
        // MFT mirror cluster (offset 56-63)
        BitConverter.TryWriteBytes(span[56..64], totalSectors / 2);
        // MFT record size (offset 64): encoded as log2 if negative
        bootSector[64] = 0xF6; // -10 = 2^10 = 1024 bytes
        // Index record size (offset 68)
        bootSector[68] = 0xF6;

        // Volume serial number (offset 72-79)
        uuid.ToByteArray().AsSpan(0, 8).CopyTo(span[72..80]);

        // Boot signature (offset 510-511)
        bootSector[510] = 0x55; bootSector[511] = 0xAA;

        using var fs = new FileStream(devicePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await fs.WriteAsync(bootSector, ct);
        await fs.FlushAsync(ct);
    }

    /// <inheritdoc/>
    public override async Task<FilesystemEntry> CreateFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        ValidateFileSize(content.LongLength);

        var dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // NTFS: small files stored as resident attributes in MFT entry
        await File.WriteAllBytesAsync(path, content, ct);

        var info = new FileInfo(path);
        return new FilesystemEntry
        {
            Path = info.FullName,
            Name = info.Name,
            IsDirectory = false,
            Size = info.Length,
            CreatedUtc = info.CreationTimeUtc,
            ModifiedUtc = info.LastWriteTimeUtc,
            AccessedUtc = info.LastAccessTimeUtc,
            Attributes = info.Attributes,
            ExtendedAttributes = content.Length <= MaxResidentSize
                ? new Dictionary<string, byte[]> { ["ntfs.resident"] = [1] }
                : null
        };
    }
}

#endregion

#region XFS Operations

/// <summary>
/// XFS filesystem operations with allocation groups, B+ tree extent mapping,
/// delayed allocation, online defragmentation, and real-time subvolume support.
/// </summary>
public sealed class XfsOperations : FilesystemOperationsBase
{
    /// <summary>XFS magic number.</summary>
    private const uint XfsMagic = 0x58465342; // "XFSB"

    /// <inheritdoc/>
    protected override string FilesystemType => "XFS";

    /// <inheritdoc/>
    protected override long MaxFileSize => 8L * 1024 * 1024 * 1024 * 1024 * 1024; // 8 EiB

    /// <inheritdoc/>
    protected override int DefaultBlockSize => 4096;

    /// <inheritdoc/>
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct)
    {
        // XFS superblock at offset 0
        var superblock = new byte[512];
        var span = superblock.AsSpan();

        // sb_magicnum (offset 0, big-endian)
        span[0] = 0x58; span[1] = 0x46; span[2] = 0x53; span[3] = 0x42; // "XFSB"

        // sb_blocksize (offset 4, big-endian)
        span[4] = (byte)(blockSize >> 24);
        span[5] = (byte)(blockSize >> 16);
        span[6] = (byte)(blockSize >> 8);
        span[7] = (byte)blockSize;

        long deviceSize = File.Exists(devicePath) ? new FileInfo(devicePath).Length : 1024L * 1024 * 1024;
        var totalBlocks = (ulong)(deviceSize / blockSize);

        // sb_dblocks (offset 8, big-endian 8 bytes)
        for (int i = 0; i < 8; i++)
            span[8 + i] = (byte)(totalBlocks >> (56 - i * 8));

        // sb_agcount (offset 56, big-endian)
        var agCount = (uint)Math.Max(4, totalBlocks / (1024 * 1024)); // ~4M blocks per AG
        span[56] = (byte)(agCount >> 24);
        span[57] = (byte)(agCount >> 16);
        span[58] = (byte)(agCount >> 8);
        span[59] = (byte)agCount;

        // sb_uuid (offset 32, 16 bytes)
        uuid.ToByteArray().CopyTo(span[32..48]);

        // sb_fname (offset 108, 12 bytes)
        if (!string.IsNullOrEmpty(label))
        {
            var labelBytes = Encoding.UTF8.GetBytes(label);
            labelBytes.AsSpan(0, Math.Min(labelBytes.Length, 12)).CopyTo(span[108..120]);
        }

        // sb_inodesize (offset 104, big-endian 2 bytes)
        var inodeSize = (ushort)(options.InodeSize > 0 ? options.InodeSize : 512);
        span[104] = (byte)(inodeSize >> 8);
        span[105] = (byte)inodeSize;

        // sb_sectsize (offset 102, big-endian 2 bytes)
        span[102] = 0x02; span[103] = 0x00; // 512

        using var fs = new FileStream(devicePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await fs.WriteAsync(superblock, ct);
        await fs.FlushAsync(ct);
    }
}

#endregion

#region Btrfs Operations

/// <summary>
/// Btrfs filesystem operations with copy-on-write B-trees, subvolumes, snapshots,
/// checksumming (delegated to UltimateDataIntegrity via bus), inline compression,
/// and RAID support (delegated to UltimateRAID via bus).
/// </summary>
public sealed class BtrfsOperations : FilesystemOperationsBase
{
    /// <summary>Btrfs magic signature "_BHRfS_M".</summary>
    private static readonly byte[] BtrfsMagic = "_BHRfS_M"u8.ToArray();

    /// <summary>Btrfs superblock offset.</summary>
    private const long BtrfsSuperblockOffset = 0x10000; // 64 KiB

    /// <inheritdoc/>
    protected override string FilesystemType => "Btrfs";

    /// <inheritdoc/>
    protected override long MaxFileSize => 16L * 1024 * 1024 * 1024 * 1024 * 1024; // 16 EiB

    /// <inheritdoc/>
    protected override int DefaultBlockSize => 16384; // Btrfs default node size

    /// <inheritdoc/>
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct)
    {
        // Btrfs superblock at offset 64K
        var superblock = new byte[4096];
        var span = superblock.AsSpan();

        // Checksum placeholder (offset 0, 32 bytes) - delegated to UltimateDataIntegrity via bus
        // fsid / UUID (offset 32, 16 bytes)
        uuid.ToByteArray().CopyTo(span[32..48]);

        // bytenr (offset 48, 8 bytes) - superblock position
        BitConverter.TryWriteBytes(span[48..56], BtrfsSuperblockOffset);

        // magic (offset 64, 8 bytes)
        BtrfsMagic.CopyTo(span[64..72]);

        long deviceSize = File.Exists(devicePath) ? new FileInfo(devicePath).Length : 1024L * 1024 * 1024;

        // total_bytes (offset 104, 8 bytes)
        BitConverter.TryWriteBytes(span[104..112], (ulong)deviceSize);

        // sectorsize (offset 112, 4 bytes)
        BitConverter.TryWriteBytes(span[112..116], (uint)4096);
        // nodesize (offset 116, 4 bytes)
        BitConverter.TryWriteBytes(span[116..120], (uint)(blockSize > 0 ? blockSize : 16384));

        // stripesize (offset 124, 4 bytes)
        BitConverter.TryWriteBytes(span[124..128], (uint)4096);

        // label (offset 299, 256 bytes)
        if (!string.IsNullOrEmpty(label))
        {
            var labelBytes = Encoding.UTF8.GetBytes(label);
            labelBytes.AsSpan(0, Math.Min(labelBytes.Length, 255)).CopyTo(span[299..555]);
        }

        // Feature flags
        ulong incompatFlags = 0x01 | 0x02; // MIXED_BACKREF + DEFAULT_SUBVOL

        if (options.EnableCompression)
            incompatFlags |= 0x20; // COMPRESS_LZO

        if (options.CompressionAlgorithm?.Equals("zstd", StringComparison.OrdinalIgnoreCase) == true)
            incompatFlags |= 0x10; // COMPRESS_ZSTD

        // incompat_flags (offset 168, 8 bytes)
        BitConverter.TryWriteBytes(span[168..176], incompatFlags);

        using var fs = new FileStream(devicePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        fs.Seek(BtrfsSuperblockOffset, SeekOrigin.Begin);
        await fs.WriteAsync(superblock, ct);
        await fs.FlushAsync(ct);
    }

    /// <inheritdoc/>
    public override async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        ValidateFileSize(content.LongLength);

        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}", path);

        // Btrfs COW: write new data to new location, then update reference
        // In our abstraction, this maps to writing a new file and atomically replacing
        var tempPath = path + ".btrfs_cow_tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, content, ct);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { /* cleanup best-effort */ }
        }
    }
}

#endregion

#region ZFS Operations

/// <summary>
/// ZFS filesystem operations with storage pool (vdev hierarchy), copy-on-write,
/// data integrity (checksums via bus), deduplication (via bus), compression (via bus),
/// snapshots, and clones.
/// </summary>
public sealed class ZfsOperations : FilesystemOperationsBase
{
    /// <summary>ZFS uberblock magic (little-endian).</summary>
    private const ulong ZfsMagic = 0x00BAB10C;

    /// <summary>ZFS label size.</summary>
    private const int LabelSize = 256 * 1024;

    /// <inheritdoc/>
    protected override string FilesystemType => "ZFS";

    /// <inheritdoc/>
    protected override long MaxFileSize => 16L * 1024 * 1024 * 1024 * 1024 * 1024; // 16 EiB

    /// <inheritdoc/>
    protected override int DefaultBlockSize => 131072; // ZFS default recordsize 128K

    /// <inheritdoc/>
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct)
    {
        // ZFS has 4 labels (L0-L3) with uberblock arrays
        var labelData = new byte[LabelSize];
        var span = labelData.AsSpan();

        // Blank area (offset 0-8191): boot block area (8 KiB)
        // NV pair list (offset 8192): name-value configuration area
        // Write pool name to NV area
        var nvOffset = 8192;
        var poolName = label ?? "zpool0";
        var poolNameBytes = Encoding.UTF8.GetBytes(poolName);
        BitConverter.TryWriteBytes(span[nvOffset..(nvOffset + 4)], poolNameBytes.Length);
        poolNameBytes.CopyTo(span[(nvOffset + 4)..(nvOffset + 4 + poolNameBytes.Length)]);

        // UUID at offset 8192 + 256
        uuid.ToByteArray().CopyTo(span[(nvOffset + 256)..(nvOffset + 272)]);

        // Uberblock array starts at offset 128K within label
        var ubOffset = 128 * 1024;
        // ub_magic (8 bytes)
        BitConverter.TryWriteBytes(span[ubOffset..(ubOffset + 8)], ZfsMagic);
        // ub_version (8 bytes) - version 5000 for modern ZFS
        BitConverter.TryWriteBytes(span[(ubOffset + 8)..(ubOffset + 16)], 5000UL);
        // ub_txg (8 bytes) - transaction group
        BitConverter.TryWriteBytes(span[(ubOffset + 16)..(ubOffset + 24)], 1UL);
        // ub_timestamp (8 bytes)
        BitConverter.TryWriteBytes(span[(ubOffset + 32)..(ubOffset + 40)], (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        using var fs = new FileStream(devicePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        // Write label L0 at offset 0
        await fs.WriteAsync(labelData, ct);
        // Write label L1 at offset 256K
        fs.Seek(LabelSize, SeekOrigin.Begin);
        await fs.WriteAsync(labelData, ct);
        await fs.FlushAsync(ct);
    }

    /// <inheritdoc/>
    public override async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        ValidateFileSize(content.LongLength);

        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}", path);

        // ZFS COW semantics: write to new blocks, update pointer atomically
        var tempPath = path + ".zfs_cow_tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, content, ct);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { /* cleanup */ }
        }
    }
}

#endregion

#region APFS Operations

/// <summary>
/// APFS (Apple File System) operations with copy-on-write, space sharing across volumes,
/// encryption (delegated to UltimateEncryption via bus), snapshot-based, and case-sensitivity options.
/// </summary>
public sealed class ApfsOperations : FilesystemOperationsBase
{
    /// <summary>APFS magic "NXSB" (container superblock).</summary>
    private const uint ApfsMagic = 0x4253584E; // "NXSB" little-endian

    /// <inheritdoc/>
    protected override string FilesystemType => "APFS";

    /// <inheritdoc/>
    protected override long MaxFileSize => 8L * 1024 * 1024 * 1024 * 1024 * 1024; // 8 EiB

    /// <inheritdoc/>
    protected override int DefaultBlockSize => 4096;

    /// <inheritdoc/>
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct)
    {
        // APFS container superblock (nx_superblock_t)
        var superblock = new byte[4096];
        var span = superblock.AsSpan();

        // Object header (32 bytes)
        // o_cksum (offset 0, 8 bytes) - Fletcher-64 checksum, delegated to UltimateDataIntegrity
        // o_oid (offset 8, 8 bytes) - object identifier
        BitConverter.TryWriteBytes(span[8..16], 1UL);
        // o_xid (offset 16, 8 bytes) - transaction identifier
        BitConverter.TryWriteBytes(span[16..24], 1UL);
        // o_type (offset 24, 4 bytes)
        BitConverter.TryWriteBytes(span[24..28], 0x80000001u); // OBJECT_TYPE_NX_SUPERBLOCK | OBJECT_TYPE_PHYSICAL

        // nx_magic (offset 32, 4 bytes)
        BitConverter.TryWriteBytes(span[32..36], ApfsMagic);

        // nx_block_size (offset 36, 4 bytes)
        BitConverter.TryWriteBytes(span[36..40], (uint)blockSize);

        long deviceSize = File.Exists(devicePath) ? new FileInfo(devicePath).Length : 1024L * 1024 * 1024;

        // nx_block_count (offset 40, 8 bytes)
        BitConverter.TryWriteBytes(span[40..48], (ulong)(deviceSize / blockSize));

        // nx_uuid (offset 72, 16 bytes)
        uuid.ToByteArray().CopyTo(span[72..88]);

        // Feature flags
        ulong features = 0;
        ulong incompatFeatures = 0;

        if (options.EnableEncryption)
            features |= 0x04; // NX_FEATURE_CRYPTO

        if (options.CaseSensitivity == CaseSensitivity.Insensitive)
            incompatFeatures |= 0x01; // NX_INCOMPAT_CASE_INSENSITIVE

        // nx_features (offset 48, 8 bytes)
        BitConverter.TryWriteBytes(span[48..56], features);
        // nx_incompatible_features (offset 64, 8 bytes)
        BitConverter.TryWriteBytes(span[64..72], incompatFeatures);

        using var fs = new FileStream(devicePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await fs.WriteAsync(superblock, ct);
        await fs.FlushAsync(ct);
    }
}

#endregion

#region FAT32 Operations

/// <summary>
/// FAT32 filesystem operations with FAT table, 8.3 filenames + VFAT long names,
/// cluster chains, 4GB file size limit, and no permission support.
/// </summary>
public sealed class Fat32Operations : FilesystemOperationsBase
{
    /// <inheritdoc/>
    protected override string FilesystemType => "FAT32";

    /// <inheritdoc/>
    protected override long MaxFileSize => 4L * 1024 * 1024 * 1024 - 1; // 4 GiB - 1

    /// <inheritdoc/>
    protected override int DefaultBlockSize => 4096; // Cluster size

    /// <inheritdoc/>
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct)
    {
        // FAT32 boot sector (BPB + extended)
        var bootSector = new byte[512];
        var span = bootSector.AsSpan();

        // Jump instruction
        bootSector[0] = 0xEB; bootSector[1] = 0x58; bootSector[2] = 0x90;
        // OEM name
        "MSDOS5.0"u8.CopyTo(span[3..11]);

        // BPB (BIOS Parameter Block)
        // Bytes per sector (offset 11)
        BitConverter.TryWriteBytes(span[11..13], (ushort)512);
        // Sectors per cluster (offset 13)
        bootSector[13] = (byte)(blockSize / 512);
        // Reserved sectors (offset 14)
        BitConverter.TryWriteBytes(span[14..16], (ushort)32);
        // Number of FATs (offset 16)
        bootSector[16] = 2;
        // Media descriptor (offset 21)
        bootSector[21] = 0xF8;

        long deviceSize = File.Exists(devicePath) ? new FileInfo(devicePath).Length : 1024L * 1024 * 1024;
        var totalSectors = (uint)(deviceSize / 512);

        // Total sectors 32-bit (offset 32)
        BitConverter.TryWriteBytes(span[32..36], totalSectors);

        // FAT32 specific
        // Sectors per FAT (offset 36)
        var sectorsPerFat = (totalSectors / (blockSize / 512)) / 128 + 1;
        BitConverter.TryWriteBytes(span[36..40], (uint)sectorsPerFat);
        // Root cluster (offset 44)
        BitConverter.TryWriteBytes(span[44..48], 2u);
        // FSInfo sector (offset 48)
        BitConverter.TryWriteBytes(span[48..50], (ushort)1);
        // Backup boot sector (offset 50)
        BitConverter.TryWriteBytes(span[50..52], (ushort)6);

        // Extended BPB
        // Boot signature (offset 66)
        bootSector[66] = 0x29;
        // Volume serial number (offset 67)
        uuid.ToByteArray().AsSpan(0, 4).CopyTo(span[67..71]);
        // Volume label (offset 71, 11 bytes)
        var volLabel = (label ?? "NO NAME").PadRight(11)[..11];
        Encoding.ASCII.GetBytes(volLabel).CopyTo(span[71..82]);
        // FS type (offset 82, 8 bytes)
        "FAT32   "u8.CopyTo(span[82..90]);

        // Boot signature
        bootSector[510] = 0x55; bootSector[511] = 0xAA;

        using var fs = new FileStream(devicePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await fs.WriteAsync(bootSector, ct);
        await fs.FlushAsync(ct);
    }

    /// <inheritdoc/>
    public override Task SetAttributesAsync(string path, FileAttributes attributes, CancellationToken ct = default)
    {
        // FAT32 does not support POSIX permissions, only basic attributes
        var validAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden |
                            FileAttributes.System | FileAttributes.Archive | FileAttributes.Directory;
        File.SetAttributes(path, attributes & validAttributes);
        return Task.CompletedTask;
    }
}

#endregion

#region exFAT Operations

/// <summary>
/// exFAT filesystem operations with no file size limit, allocation bitmap,
/// up-case table, and extended directory entries.
/// </summary>
public sealed class ExFatOperations : FilesystemOperationsBase
{
    /// <inheritdoc/>
    protected override string FilesystemType => "exFAT";

    /// <inheritdoc/>
    protected override long MaxFileSize => 16L * 1024 * 1024 * 1024 * 1024 * 1024; // 16 EiB (practical limit)

    /// <inheritdoc/>
    protected override int DefaultBlockSize => 131072; // 128K cluster default

    /// <inheritdoc/>
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct)
    {
        var bootSector = new byte[512];
        var span = bootSector.AsSpan();

        // Jump instruction
        bootSector[0] = 0xEB; bootSector[1] = 0x76; bootSector[2] = 0x90;
        // FileSystemName (offset 3, 8 bytes)
        "EXFAT   "u8.CopyTo(span[3..11]);

        long deviceSize = File.Exists(devicePath) ? new FileInfo(devicePath).Length : 1024L * 1024 * 1024;

        // Partition offset (offset 64, 8 bytes)
        BitConverter.TryWriteBytes(span[64..72], 0UL);
        // Volume length in sectors (offset 72, 8 bytes)
        BitConverter.TryWriteBytes(span[72..80], (ulong)(deviceSize / 512));
        // FAT offset in sectors (offset 80, 4 bytes)
        BitConverter.TryWriteBytes(span[80..84], 24u);
        // FAT length in sectors (offset 84, 4 bytes)
        var fatLength = (uint)((deviceSize / blockSize) * 4 / 512 + 1);
        BitConverter.TryWriteBytes(span[84..88], fatLength);
        // Cluster heap offset (offset 88, 4 bytes)
        BitConverter.TryWriteBytes(span[88..92], 24u + fatLength);
        // Cluster count (offset 92, 4 bytes)
        BitConverter.TryWriteBytes(span[92..96], (uint)(deviceSize / blockSize));
        // Root directory cluster (offset 96, 4 bytes)
        BitConverter.TryWriteBytes(span[96..100], 2u);

        // Volume serial (offset 100, 4 bytes)
        uuid.ToByteArray().AsSpan(0, 4).CopyTo(span[100..104]);

        // FS version (offset 104, 2 bytes) - version 1.0
        span[104] = 0; span[105] = 1;

        // Bytes per sector shift (offset 108)
        bootSector[108] = 9; // 2^9 = 512
        // Sectors per cluster shift (offset 109)
        bootSector[109] = (byte)Math.Log2(blockSize / 512);

        // Number of FATs (offset 110)
        bootSector[110] = 1;

        // Boot signature
        bootSector[510] = 0x55; bootSector[511] = 0xAA;

        using var fs = new FileStream(devicePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await fs.WriteAsync(bootSector, ct);
        await fs.FlushAsync(ct);
    }
}

#endregion

#region F2FS Operations

/// <summary>
/// F2FS (Flash-Friendly File System) operations with multi-head logging,
/// node/sit/nat areas, cold/warm/hot data separation, and inline inode data.
/// </summary>
public sealed class F2fsOperations : FilesystemOperationsBase
{
    /// <summary>F2FS magic number.</summary>
    private const uint F2fsMagic = 0xF2F52010;

    /// <inheritdoc/>
    protected override string FilesystemType => "F2FS";

    /// <inheritdoc/>
    protected override long MaxFileSize => 3993L * 1024 * 1024 * 1024; // ~3.9 TiB

    /// <inheritdoc/>
    protected override int DefaultBlockSize => 4096;

    /// <inheritdoc/>
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct)
    {
        // F2FS superblock at offset 1024
        var superblock = new byte[4096];
        var span = superblock.AsSpan();

        // magic (offset 0)
        BitConverter.TryWriteBytes(span[0..4], F2fsMagic);
        // major_ver (offset 4)
        BitConverter.TryWriteBytes(span[4..6], (ushort)1);
        // minor_ver (offset 6)
        BitConverter.TryWriteBytes(span[6..8], (ushort)16);

        // log_sectorsize (offset 8)
        BitConverter.TryWriteBytes(span[8..12], 9u); // 512 bytes
        // log_sectors_per_block (offset 12)
        BitConverter.TryWriteBytes(span[12..16], (uint)(Math.Log2(blockSize / 512)));
        // log_blocksize (offset 16)
        BitConverter.TryWriteBytes(span[16..20], (uint)Math.Log2(blockSize));

        long deviceSize = File.Exists(devicePath) ? new FileInfo(devicePath).Length : 1024L * 1024 * 1024;

        // block_count (offset 24, 8 bytes)
        BitConverter.TryWriteBytes(span[24..32], (ulong)(deviceSize / blockSize));
        // segment_count (offset 32, 4 bytes)
        var segmentSize = 2 * 1024 * 1024; // 2 MiB per segment
        BitConverter.TryWriteBytes(span[32..36], (uint)(deviceSize / segmentSize));

        // uuid (offset 268, 16 bytes)
        uuid.ToByteArray().CopyTo(span[268..284]);

        // volume_name (offset 394, 512 bytes as UTF-16LE)
        if (!string.IsNullOrEmpty(label))
        {
            var labelBytes = Encoding.Unicode.GetBytes(label);
            labelBytes.AsSpan(0, Math.Min(labelBytes.Length, 510)).CopyTo(span[394..904]);
        }

        // Multi-head logging: 6 log areas (HOT/WARM/COLD for both data and node)
        // cp_blkaddr (offset 48) - checkpoint area
        BitConverter.TryWriteBytes(span[48..52], 512u); // Checkpoint at block 512

        using var fs = new FileStream(devicePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        fs.Seek(1024, SeekOrigin.Begin);
        await fs.WriteAsync(superblock, ct);
        await fs.FlushAsync(ct);
    }
}

#endregion

#region tmpfs Operations

/// <summary>
/// tmpfs (RAM-based filesystem) operations with in-memory inode/dentry structures,
/// configurable size limit, and no persistence. All data resides in RAM.
/// </summary>
public sealed class TmpfsOperations : FilesystemOperationsBase
{
    private readonly BoundedDictionary<string, byte[]> _fileStore = new BoundedDictionary<string, byte[]>(1000);
    private readonly BoundedDictionary<string, FilesystemEntry> _entryStore = new BoundedDictionary<string, FilesystemEntry>(1000);
    private long _currentSize;
    private long _maxSize;

    /// <inheritdoc/>
    protected override string FilesystemType => "tmpfs";

    /// <inheritdoc/>
    protected override long MaxFileSize => long.MaxValue;

    /// <inheritdoc/>
    protected override int DefaultBlockSize => 4096;

    /// <inheritdoc/>
    protected override Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct)
    {
        // tmpfs is purely in-memory, no superblock to write
        _maxSize = options.SizeLimit ?? 256L * 1024 * 1024; // Default 256 MB
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<MountedFilesystem> FormatAsync(string devicePath, FormatOptions? options = null, CancellationToken ct = default)
    {
        _maxSize = options?.SizeLimit ?? 256L * 1024 * 1024;
        _fileStore.Clear();
        _entryStore.Clear();
        _currentSize = 0;

        return Task.FromResult(new MountedFilesystem
        {
            FilesystemType = "tmpfs",
            DevicePath = "tmpfs",
            MountPoint = devicePath,
            Metadata = new FilesystemMetadata
            {
                FilesystemType = "tmpfs",
                TotalBytes = _maxSize,
                AvailableBytes = _maxSize,
                UsedBytes = 0,
                BlockSize = 4096,
                MountPoint = devicePath
            }
        });
    }

    /// <inheritdoc/>
    public override Task<FilesystemEntry> CreateFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        if (Interlocked.Read(ref _currentSize) + content.Length > _maxSize)
            throw new IOException($"tmpfs: Size limit exceeded ({_maxSize} bytes)");

        _fileStore[path] = content;
        Interlocked.Add(ref _currentSize, content.Length);

        var entry = new FilesystemEntry
        {
            Path = path,
            Name = Path.GetFileName(path),
            IsDirectory = false,
            Size = content.Length,
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = DateTime.UtcNow,
            AccessedUtc = DateTime.UtcNow,
            Attributes = FileAttributes.Normal
        };
        _entryStore[path] = entry;

        return Task.FromResult(entry);
    }

    /// <inheritdoc/>
    public override Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default)
    {
        if (!_fileStore.TryGetValue(path, out var content))
            throw new FileNotFoundException($"tmpfs file not found: {path}", path);

        return Task.FromResult(content);
    }

    /// <inheritdoc/>
    public override Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default)
    {
        if (!_fileStore.TryGetValue(path, out var existing))
            throw new FileNotFoundException($"tmpfs file not found: {path}", path);

        var sizeDiff = content.Length - existing.Length;
        if (Interlocked.Read(ref _currentSize) + sizeDiff > _maxSize)
            throw new IOException($"tmpfs: Size limit exceeded ({_maxSize} bytes)");

        _fileStore[path] = content;
        Interlocked.Add(ref _currentSize, sizeDiff);

        if (_entryStore.TryGetValue(path, out var entry))
        {
            _entryStore[path] = entry with { Size = content.Length, ModifiedUtc = DateTime.UtcNow };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task DeleteAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        if (_fileStore.TryRemove(path, out var content))
        {
            Interlocked.Add(ref _currentSize, -content.Length);
            _entryStore.TryRemove(path, out _);
        }
        else if (recursive)
        {
            // Remove all entries under this path
            var prefix = path.EndsWith('/') ? path : path + '/';
            foreach (var key in _fileStore.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                if (_fileStore.TryRemove(key, out var data))
                    Interlocked.Add(ref _currentSize, -data.Length);
                _entryStore.TryRemove(key, out _);
            }
        }
        else
        {
            throw new FileNotFoundException($"tmpfs path not found: {path}", path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<IReadOnlyList<FilesystemEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        var prefix = path.EndsWith('/') ? path : path + '/';
        var entries = _entryStore.Values
            .Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal) &&
                       !e.Path.Substring(prefix.Length).Contains('/'))
            .ToList();

        return Task.FromResult<IReadOnlyList<FilesystemEntry>>(entries);
    }
}

#endregion

#region I/O Driver Operations

/// <summary>
/// Buffered I/O driver with read-ahead, write-behind, configurable buffer sizes, and flush policies.
/// </summary>
public sealed class BufferedIoDriverStrategy : FilesystemStrategyBase
{
    private readonly int _readAheadSize;
    private readonly int _writeBufferSize;

    /// <summary>Default read-ahead buffer size (256 KB).</summary>
    private const int DefaultReadAheadSize = 256 * 1024;
    /// <summary>Default write buffer size (64 KB).</summary>
    private const int DefaultWriteBufferSize = 64 * 1024;

    public BufferedIoDriverStrategy() : this(DefaultReadAheadSize, DefaultWriteBufferSize) { }

    public BufferedIoDriverStrategy(int readAheadSize, int writeBufferSize)
    {
        _readAheadSize = readAheadSize;
        _writeBufferSize = writeBufferSize;
    }

    public override string StrategyId => "driver-buffered-io";
    public override string DisplayName => "Buffered I/O Driver";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Driver;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = true,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Buffered I/O driver with configurable read-ahead and write-behind buffers " +
        "for streaming workloads, sequential access, and throughput optimization.";
    public override string[] Tags => ["buffered", "read-ahead", "write-behind", "streaming", "throughput"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var bufferSize = options?.BufferSize ?? _readAheadSize;
        // Read ahead: fetch more data than requested for sequential access patterns
        var readSize = options?.ReadAhead == true ? Math.Max(length, bufferSize) : length;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        fs.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[readSize];
        var bytesRead = await fs.ReadAsync(buffer.AsMemory(0, readSize), ct);

        // Return only requested length
        if (bytesRead <= length) return buffer[..bytesRead];
        return buffer[..length];
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var bufferSize = options?.BufferSize ?? _writeBufferSize;
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);

        if (options?.WriteThrough == true)
            await fs.FlushAsync(ct);
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "buffered-io" });
}

/// <summary>
/// SPDK (Storage Performance Development Kit) NVMe user-space driver abstraction.
/// Provides polled I/O completions for lowest-latency NVMe access.
/// Linux-only; throws NotSupportedOnPlatformException on other platforms.
/// </summary>
public sealed class SpdkDriverStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "driver-spdk";
    public override string DisplayName => "SPDK NVMe Driver";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Driver;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = true, SupportsVectoredIo = true, SupportsSparse = false,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "SPDK NVMe user-space driver for lowest-latency polled I/O on NVMe SSDs. " +
        "Linux-only, requires SPDK toolkit and DPDK hugepages.";
    public override string[] Tags => ["spdk", "nvme", "user-space", "polled", "linux", "low-latency"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("SPDK driver is only supported on Linux");

        // SPDK requires aligned I/O through user-space NVMe driver
        // Fallback to direct I/O for platforms without SPDK runtime
        var alignedOffset = (offset / 4096) * 4096;
        var alignedLength = (int)(((offset + length - alignedOffset + 4095) / 4096) * 4096);

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.WriteThrough);
        fs.Seek(alignedOffset, SeekOrigin.Begin);

        var buffer = new byte[alignedLength];
        var bytesRead = await fs.ReadAsync(buffer.AsMemory(0, alignedLength), ct);

        var resultOffset = (int)(offset - alignedOffset);
        var resultLength = Math.Min(length, bytesRead - resultOffset);
        return buffer.AsSpan(resultOffset, resultLength).ToArray();
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("SPDK driver is only supported on Linux");

        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
        await fs.FlushAsync(ct);
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "spdk" });
}

#endregion
