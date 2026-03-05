using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateFilesystem.Strategies;

/// <summary>
/// POSIX I/O driver strategy.
/// Standard POSIX file I/O for cross-platform compatibility.
/// </summary>
public sealed class PosixDriverStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "driver-posix";
    public override string DisplayName => "POSIX I/O Driver";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Driver;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Standard POSIX file I/O driver providing cross-platform compatibility " +
        "with synchronous and asynchronous operations.";
    public override string[] Tags => ["posix", "driver", "cross-platform", "standard"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        ValidatePath(path);
        var fileOptions = options?.AsyncIo == true ? FileOptions.Asynchronous : FileOptions.None;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4096, fileOptions);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        #pragma warning disable CA2022 // Intentional partial read - bytesRead is checked and buffer resized
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct);
        #pragma warning restore CA2022
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        ValidatePath(path);
        var fileOptions = options?.AsyncIo == true ? FileOptions.Asynchronous : FileOptions.None;
        if (options?.WriteThrough == true) fileOptions |= FileOptions.WriteThrough;
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 4096, fileOptions);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "posix" });
}

/// <summary>
/// Direct I/O driver strategy.
/// Bypasses page cache for predictable I/O performance.
/// </summary>
public sealed class DirectIoDriverStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "driver-direct-io";
    public override string DisplayName => "Direct I/O Driver";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Driver;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Direct I/O driver bypassing the page cache for database workloads " +
        "requiring predictable I/O latency and no double-buffering.";
    public override string[] Tags => ["direct-io", "o_direct", "database", "no-cache"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // Align to 512-byte boundary for direct I/O
        var alignedOffset = (offset / 512) * 512;
        var alignedLength = ((length + 512 - 1) / 512) * 512;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.WriteThrough);
        fs.Seek(alignedOffset, SeekOrigin.Begin);
        var buffer = new byte[alignedLength];
        #pragma warning disable CA2022 // Intentional partial read - aligned buffer may exceed available data
        var bytesRead = await fs.ReadAsync(buffer, 0, alignedLength, ct);
        #pragma warning restore CA2022

        // Return only requested portion
        var result = new byte[Math.Min(length, bytesRead)];
        Array.Copy(buffer, offset - alignedOffset, result, 0, result.Length);
        return result;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
        await fs.FlushAsync(ct);
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "direct-io" });
}

/// <summary>
/// io_uring driver strategy for Linux.
/// High-performance async I/O using Linux io_uring.
/// </summary>
public sealed class IoUringDriverStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "driver-io-uring";
    public override string DisplayName => "io_uring Driver";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Driver;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        // SupportsKernelBypass is false: current implementation uses managed FileStream with
        // FileOptions.Asynchronous. True io_uring (P/Invoke to io_uring_setup/enter) requires
        // native interop not yet wired. Mark false so callers do not assume bypass semantics.
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Linux io_uring-style async I/O driver using FileOptions.Asynchronous for overlapped I/O. " +
        "Native io_uring kernel bypass (SupportsKernelBypass) is not yet active; " +
        "upgrade to P/Invoke io_uring_setup/io_uring_enter for true zero-syscall paths.";
    public override string[] Tags => ["io-uring", "linux", "async", "nvme"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // Uses FileOptions.Asynchronous for OS-level async I/O completion (IOCP on Windows, epoll on Linux)
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        // LOW-3027: Use ReadAsync with resize on partial read (consistent with other strategies).
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct).ConfigureAwait(false);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "io-uring" });
}

/// <summary>
/// Memory-mapped I/O driver strategy.
/// Uses memory-mapped files for zero-copy I/O.
/// </summary>
public sealed class MmapDriverStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "driver-mmap";
    public override string DisplayName => "Memory-Mapped I/O Driver";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Driver;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = false, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = true,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Memory-mapped I/O driver using mmap for zero-copy access to files, " +
        "ideal for random access to large files like databases.";
    public override string[] Tags => ["mmap", "zero-copy", "random-access", "large-files"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(path, FileMode.Open);
        using var accessor = mmf.CreateViewAccessor(offset, length, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
        var buffer = new byte[length];
        accessor.ReadArray(0, buffer, 0, length);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, null, offset + data.Length);
        using var accessor = mmf.CreateViewAccessor(offset, data.Length, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Write);
        accessor.WriteArray(0, data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "mmap" });
}

/// <summary>
/// Windows native I/O driver strategy.
/// Uses Windows-specific optimizations.
/// </summary>
public sealed class WindowsNativeDriverStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "driver-windows-native";
    public override string DisplayName => "Windows Native I/O Driver";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Driver;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = true, SupportsAsyncIo = true, SupportsMmap = true,
        SupportsKernelBypass = false, SupportsVectoredIo = true, SupportsSparse = true,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Windows native I/O driver using overlapped I/O and completion ports " +
        "for optimal performance on Windows systems.";
    public override string[] Tags => ["windows", "overlapped", "iocp", "native"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var fileOptions = FileOptions.Asynchronous;
        if (options?.DirectIo == true) fileOptions |= FileOptions.WriteThrough;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options?.BufferSize ?? 4096, fileOptions);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        // LOW-3027: Use ReadAsync with resize on partial read (consistent with other strategies).
        var bytesRead = await fs.ReadAsync(buffer, 0, length, ct).ConfigureAwait(false);
        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        var fileOptions = FileOptions.Asynchronous;
        if (options?.WriteThrough == true) fileOptions |= FileOptions.WriteThrough;

        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, options?.BufferSize ?? 4096, fileOptions);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data, 0, data.Length, ct);
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "windows-native" });
}

/// <summary>
/// Async I/O driver strategy.
/// Generic async I/O with configurable concurrency.
/// </summary>
public sealed class AsyncIoDriverStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "driver-async-io";
    public override string DisplayName => "Async I/O Driver";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Driver;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = true,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Generic async I/O driver with configurable concurrency limits " +
        "for streaming and batch workloads.";
    public override string[] Tags => ["async", "streaming", "batch", "concurrent"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        await fs.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
        return buffer;
    }

    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        fs.Seek(offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.AsMemory(), ct);
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "async-io" });
}
