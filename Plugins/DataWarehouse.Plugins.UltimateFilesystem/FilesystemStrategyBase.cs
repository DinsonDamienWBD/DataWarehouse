using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateFilesystem;

/// <summary>
/// Defines the category of filesystem strategy.
/// </summary>
public enum FilesystemStrategyCategory
{
    /// <summary>Auto-detection of filesystem type.</summary>
    Detection,
    /// <summary>Low-level I/O driver.</summary>
    Driver,
    /// <summary>Block layer abstraction.</summary>
    Block,
    /// <summary>File format support.</summary>
    Format,
    /// <summary>Caching layer.</summary>
    Cache,
    /// <summary>Quota and space management.</summary>
    Quota,
    /// <summary>Virtual filesystem.</summary>
    Virtual,
    /// <summary>Container filesystem.</summary>
    Container
}

/// <summary>
/// Represents filesystem metadata.
/// </summary>
public sealed record FilesystemMetadata
{
    /// <summary>Filesystem type name.</summary>
    public required string FilesystemType { get; init; }
    /// <summary>Total capacity in bytes.</summary>
    public long TotalBytes { get; init; }
    /// <summary>Available space in bytes.</summary>
    public long AvailableBytes { get; init; }
    /// <summary>Used space in bytes.</summary>
    public long UsedBytes { get; init; }
    /// <summary>Block size in bytes.</summary>
    public int BlockSize { get; init; } = 4096;
    /// <summary>Whether read-only.</summary>
    public bool IsReadOnly { get; init; }
    /// <summary>Whether supports sparse files.</summary>
    public bool SupportsSparse { get; init; }
    /// <summary>Whether supports compression.</summary>
    public bool SupportsCompression { get; init; }
    /// <summary>Whether supports encryption.</summary>
    public bool SupportsEncryption { get; init; }
    /// <summary>Whether supports deduplication.</summary>
    public bool SupportsDeduplication { get; init; }
    /// <summary>Whether supports snapshots.</summary>
    public bool SupportsSnapshots { get; init; }
    /// <summary>Mount point path.</summary>
    public string? MountPoint { get; init; }
    /// <summary>Detection timestamp.</summary>
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Represents block I/O options.
/// </summary>
public sealed record BlockIoOptions
{
    /// <summary>Use direct I/O (bypass page cache).</summary>
    public bool DirectIo { get; init; }
    /// <summary>Use asynchronous I/O.</summary>
    public bool AsyncIo { get; init; }
    /// <summary>Buffer size for I/O operations.</summary>
    public int BufferSize { get; init; } = 64 * 1024;
    /// <summary>I/O priority (0-7).</summary>
    public int Priority { get; init; } = 4;
    /// <summary>Enable write-through caching.</summary>
    public bool WriteThrough { get; init; }
    /// <summary>Enable read-ahead.</summary>
    public bool ReadAhead { get; init; } = true;
    /// <summary>Maximum concurrent I/O operations.</summary>
    public int MaxConcurrentOps { get; init; } = 32;
}

/// <summary>
/// Capabilities of a filesystem strategy.
/// </summary>
public sealed record FilesystemStrategyCapabilities
{
    /// <summary>Whether supports direct I/O.</summary>
    public required bool SupportsDirectIo { get; init; }
    /// <summary>Whether supports async I/O.</summary>
    public required bool SupportsAsyncIo { get; init; }
    /// <summary>Whether supports memory-mapped files.</summary>
    public required bool SupportsMmap { get; init; }
    /// <summary>Whether supports kernel bypass.</summary>
    public required bool SupportsKernelBypass { get; init; }
    /// <summary>Whether supports vectored I/O.</summary>
    public required bool SupportsVectoredIo { get; init; }
    /// <summary>Whether supports sparse files.</summary>
    public required bool SupportsSparse { get; init; }
    /// <summary>Whether auto-detects filesystem type.</summary>
    public required bool SupportsAutoDetect { get; init; }
    /// <summary>Maximum file size supported.</summary>
    public long MaxFileSize { get; init; } = long.MaxValue;
}

/// <summary>
/// Interface for filesystem strategies.
/// </summary>
public interface IFilesystemStrategy
{
    /// <summary>Unique identifier.</summary>
    string StrategyId { get; }
    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }
    /// <summary>Category of this strategy.</summary>
    FilesystemStrategyCategory Category { get; }
    /// <summary>Capabilities of this strategy.</summary>
    FilesystemStrategyCapabilities Capabilities { get; }
    /// <summary>Semantic description for AI discovery.</summary>
    string SemanticDescription { get; }
    /// <summary>Tags for categorization.</summary>
    string[] Tags { get; }
    /// <summary>Detects filesystem at path.</summary>
    Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    /// <summary>Reads a block of data.</summary>
    Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    /// <summary>Writes a block of data.</summary>
    Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    /// <summary>Gets filesystem metadata.</summary>
    Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
    /// <summary>Initializes the strategy.</summary>
    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>Disposes of the strategy resources.</summary>
    Task DisposeAsync();
}

/// <summary>
/// Abstract base class for filesystem strategies.
/// Inherits lifecycle, counters, health caching, and dispose from StrategyBase.
/// </summary>
public abstract class FilesystemStrategyBase : StrategyBase, IFilesystemStrategy
{
    /// <inheritdoc/>
    public abstract override string StrategyId { get; }
    /// <inheritdoc/>
    public abstract string DisplayName { get; }
    /// <inheritdoc/>
    public override string Name => DisplayName;
    /// <inheritdoc/>
    public abstract FilesystemStrategyCategory Category { get; }
    /// <inheritdoc/>
    public abstract FilesystemStrategyCapabilities Capabilities { get; }
    /// <inheritdoc/>
    public abstract string SemanticDescription { get; }
    /// <inheritdoc/>
    public abstract string[] Tags { get; }

    /// <inheritdoc/>
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        await InitializeCoreAsync(cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        await DisposeCoreAsync();
    }

    /// <summary>Explicit implementation for IFilesystemStrategy.DisposeAsync() (Task vs ValueTask).</summary>
    Task IFilesystemStrategy.DisposeAsync() => ShutdownAsync();

    /// <summary>Core initialization logic.</summary>
    protected virtual Task InitializeCoreAsync(CancellationToken ct) => Task.CompletedTask;
    /// <summary>Core disposal logic.</summary>
    protected virtual Task DisposeCoreAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public abstract Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    /// <inheritdoc/>
    public abstract Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    /// <inheritdoc/>
    public abstract Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    /// <inheritdoc/>
    public abstract Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Validates that a user-supplied path does not contain path traversal sequences
    /// ("../", "..\\", null bytes) or UNC prefix that could escape the expected root.
    /// Throws <see cref="ArgumentException"/> if the path is invalid.
    /// </summary>
    protected static void ValidatePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains('\0'))
            throw new ArgumentException("Path must not contain null bytes.", nameof(path));
        if (path.Contains("..") && (path.Contains("../") || path.Contains("..\\") || path.EndsWith("..")))
            throw new ArgumentException($"Path traversal sequences ('..') are not allowed: {path}", nameof(path));
        if (path.StartsWith("\\\\") || path.StartsWith("//"))
            throw new ArgumentException($"UNC paths are not permitted: {path}", nameof(path));
    }
}

/// <summary>
/// Thread-safe registry for filesystem strategies.
/// </summary>
public sealed class FilesystemStrategyRegistry
{
    private readonly BoundedDictionary<string, IFilesystemStrategy> _strategies = new BoundedDictionary<string, IFilesystemStrategy>(1000);

    /// <summary>Registers a strategy.</summary>
    public void Register(IFilesystemStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>Unregisters a strategy by ID.</summary>
    public bool Unregister(string strategyId) => _strategies.TryRemove(strategyId, out _);

    /// <summary>Gets a strategy by ID.</summary>
    public IFilesystemStrategy? Get(string strategyId) =>
        _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;

    /// <summary>Gets all registered strategies.</summary>
    public IReadOnlyCollection<IFilesystemStrategy> GetAll() => _strategies.Values.ToList().AsReadOnly();

    /// <summary>Gets strategies by category.</summary>
    public IReadOnlyCollection<IFilesystemStrategy> GetByCategory(FilesystemStrategyCategory category) =>
        _strategies.Values.Where(s => s.Category == category).OrderBy(s => s.DisplayName).ToList().AsReadOnly();

    /// <summary>Gets the count of registered strategies.</summary>
    public int Count => _strategies.Count;

    /// <summary>Auto-discovers and registers strategies from assemblies.</summary>
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies)
    {
        var strategyType = typeof(IFilesystemStrategy);
        int discovered = 0;

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && strategyType.IsAssignableFrom(t));

                foreach (var type in types)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is IFilesystemStrategy strategy)
                        {
                            Register(strategy);
                            discovered++;
                        }
                    }
                    catch { /* Skip types that cannot be instantiated */ }
                }
            }
            catch { /* Skip assemblies that cannot be scanned */ }
        }

        return discovered;
    }
}
