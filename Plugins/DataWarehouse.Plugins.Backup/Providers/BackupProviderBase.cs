using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.Backup.Providers;

/// <summary>
/// Abstract base class for backup providers that implements common functionality
/// such as logging, filtering, event handling, and disposal patterns.
/// </summary>
public abstract class BackupProviderBase : IBackupProvider, IAsyncDisposable
{
    /// <summary>Logger instance for the provider.</summary>
    protected readonly ILogger? _logger;

    /// <summary>Lock for thread-safe backup operations.</summary>
    protected readonly SemaphoreSlim _backupLock = new(1, 1);

    /// <summary>Indicates whether the provider has been disposed.</summary>
    protected volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupProviderBase"/> class.
    /// </summary>
    /// <param name="logger">Optional logger instance.</param>
    protected BackupProviderBase(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the unique identifier for this backup provider.
    /// </summary>
    public abstract string ProviderId { get; }

    /// <summary>
    /// Gets the display name of this backup provider.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Gets the type of backup this provider performs.
    /// </summary>
    public abstract BackupProviderType ProviderType { get; }

    /// <summary>
    /// Gets a value indicating whether the provider is actively monitoring for changes.
    /// </summary>
    public virtual bool IsMonitoring => false;

    /// <summary>
    /// Occurs when backup progress changes.
    /// </summary>
    public event EventHandler<BackupProgressEventArgs>? ProgressChanged;

    /// <summary>
    /// Occurs when a backup operation completes.
    /// </summary>
    public event EventHandler<BackupCompletedEventArgs>? BackupCompleted;

    /// <summary>
    /// Initializes the backup provider.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public abstract Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Performs a full backup of the specified paths.
    /// </summary>
    /// <param name="paths">The paths to back up.</param>
    /// <param name="options">Optional backup options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation with the backup result.</returns>
    public abstract Task<BackupResult> PerformFullBackupAsync(
        IEnumerable<string> paths,
        BackupOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Performs an incremental backup based on changes since the last backup.
    /// </summary>
    /// <param name="options">Optional backup options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation with the backup result.</returns>
    public abstract Task<BackupResult> PerformIncrementalBackupAsync(
        BackupOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Restores files from a backup.
    /// </summary>
    /// <param name="backupId">The backup identifier to restore from.</param>
    /// <param name="targetPath">Optional target path for restore. If null, restores to original location.</param>
    /// <param name="pointInTime">Optional point-in-time to restore to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation with the restore result.</returns>
    public abstract Task<RestoreResult> RestoreAsync(
        string backupId,
        string? targetPath = null,
        DateTime? pointInTime = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets statistics about the backup provider's current state.
    /// </summary>
    /// <returns>Backup statistics.</returns>
    public abstract BackupStatistics GetStatistics();

    /// <summary>
    /// Gets metadata for a specific backup.
    /// </summary>
    /// <param name="backupId">The backup identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation with the backup metadata, or null if not found.</returns>
    public abstract Task<BackupMetadata?> GetBackupMetadataAsync(string backupId, CancellationToken ct = default);

    /// <summary>
    /// Lists all available backups.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of backup metadata.</returns>
    public abstract IAsyncEnumerable<BackupMetadata> ListBackupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Determines whether a file should be included in the backup based on the provided options.
    /// </summary>
    /// <param name="path">The file path to evaluate.</param>
    /// <param name="options">The backup options containing include/exclude patterns.</param>
    /// <returns>True if the file should be backed up; otherwise, false.</returns>
    protected virtual bool ShouldBackupFile(string path, BackupOptions? options)
    {
        if (options == null) return true;

        var fileName = Path.GetFileName(path);

        // Check exclude patterns first
        if (options.ExcludePatterns?.Length > 0)
        {
            foreach (var pattern in options.ExcludePatterns)
            {
                if (MatchesPattern(fileName, pattern))
                {
                    _logger?.LogDebug("Excluding file {Path} matching exclude pattern: {Pattern}", path, pattern);
                    return false;
                }
            }
        }

        // Check include patterns if specified
        if (options.IncludePatterns?.Length > 0)
        {
            var matched = false;
            foreach (var pattern in options.IncludePatterns)
            {
                if (MatchesPattern(fileName, pattern))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                _logger?.LogDebug("Excluding file {Path} not matching any include pattern", path);
                return false;
            }
        }

        // Check file size limit
        if (options.MaxFileSizeBytes > 0)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length > options.MaxFileSizeBytes)
                {
                    _logger?.LogDebug("Excluding file {Path} exceeding size limit: {Size} > {Limit}",
                        path, info.Length, options.MaxFileSizeBytes);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Filter evaluation failed for path: {Path}", path);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Matches a string against a wildcard pattern.
    /// </summary>
    /// <param name="input">The input string to match.</param>
    /// <param name="pattern">The wildcard pattern (* and ? supported).</param>
    /// <returns>True if the input matches the pattern; otherwise, false.</returns>
    protected static bool MatchesPattern(string input, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Computes a SHA256 checksum for a file stream.
    /// </summary>
    /// <param name="stream">The stream to compute the checksum for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation with the hex-encoded checksum.</returns>
    protected static async Task<string> ComputeChecksumAsync(Stream stream, CancellationToken ct)
    {
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes a SHA256 hash for a block of data.
    /// </summary>
    /// <param name="data">The data to hash.</param>
    /// <returns>The hex-encoded hash.</returns>
    protected static string ComputeBlockHash(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    /// <summary>
    /// Converts a file path to a relative path format suitable for backup storage.
    /// </summary>
    /// <param name="filePath">The file path to convert.</param>
    /// <returns>The relative path with forward slashes.</returns>
    protected static string GetRelativePath(string filePath)
    {
        return filePath.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>
    /// Raises the <see cref="ProgressChanged"/> event.
    /// </summary>
    /// <param name="backupId">The backup identifier.</param>
    /// <param name="processedFiles">The number of files processed.</param>
    /// <param name="totalFiles">The total number of files to process.</param>
    /// <param name="processedBytes">The number of bytes processed.</param>
    /// <param name="totalBytes">The total number of bytes to process.</param>
    protected virtual void RaiseProgressChanged(string backupId, long processedFiles, long totalFiles, long processedBytes, long totalBytes)
    {
        ProgressChanged?.Invoke(this, new BackupProgressEventArgs
        {
            BackupId = backupId,
            ProcessedFiles = processedFiles,
            TotalFiles = totalFiles,
            ProcessedBytes = processedBytes,
            TotalBytes = totalBytes
        });
    }

    /// <summary>
    /// Raises the <see cref="BackupCompleted"/> event.
    /// </summary>
    /// <param name="result">The backup result.</param>
    protected virtual void RaiseBackupCompleted(BackupResult result)
    {
        BackupCompleted?.Invoke(this, new BackupCompletedEventArgs { Result = result });
    }

    /// <summary>
    /// Throws an exception if the provider has been disposed.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the provider has been disposed.</exception>
    protected void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    /// <summary>
    /// Loads state from a JSON file in the state path.
    /// </summary>
    /// <typeparam name="T">The type of state to load.</typeparam>
    /// <param name="fileName">The name of the state file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The loaded state, or null if the file doesn't exist.</returns>
    protected async Task<T?> LoadStateAsync<T>(string fileName, CancellationToken ct = default) where T : class
    {
        var stateFile = Path.Combine(GetStatePath(), fileName);
        if (!File.Exists(stateFile))
            return null;

        var json = await File.ReadAllTextAsync(stateFile, ct);
        return JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>
    /// Saves state to a JSON file in the state path.
    /// </summary>
    /// <typeparam name="T">The type of state to save.</typeparam>
    /// <param name="state">The state to save.</param>
    /// <param name="fileName">The name of the state file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected async Task SaveStateAsync<T>(T state, string fileName, CancellationToken ct = default) where T : class
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        var stateFile = Path.Combine(GetStatePath(), fileName);
        await File.WriteAllTextAsync(stateFile, json, ct);
    }

    /// <summary>
    /// Gets the state path for this provider. Must be overridden by derived classes
    /// that use state persistence.
    /// </summary>
    /// <returns>The state path directory.</returns>
    protected virtual string GetStatePath()
    {
        throw new NotImplementedException("Derived class must override GetStatePath() to use state persistence methods.");
    }

    /// <summary>
    /// Performs a backup loop over a collection of file paths, invoking a callback for each file.
    /// This method handles common iteration patterns including directory enumeration, filtering,
    /// progress tracking, and error handling.
    /// </summary>
    /// <param name="paths">The paths to back up (files or directories).</param>
    /// <param name="options">Optional backup options for filtering.</param>
    /// <param name="backupId">The backup identifier for progress reporting.</param>
    /// <param name="result">The backup result to populate with statistics.</param>
    /// <param name="fileProcessor">Async callback to process each file. Returns bytes processed.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected async Task PerformBackupLoopAsync(
        IEnumerable<string> paths,
        BackupOptions? options,
        string backupId,
        BackupResult result,
        Func<string, CancellationToken, Task<long>> fileProcessor,
        CancellationToken ct = default)
    {
        // Enumerate all files
        var allFiles = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                allFiles.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
            else if (File.Exists(path))
                allFiles.Add(path);
        }

        result.TotalFiles = allFiles.Count;
        var processedFiles = 0;

        // Process each file
        foreach (var filePath in allFiles)
        {
            ct.ThrowIfCancellationRequested();

            if (!ShouldBackupFile(filePath, options))
                continue;

            try
            {
                var bytesProcessed = await fileProcessor(filePath, ct);
                result.TotalBytes += bytesProcessed;
                processedFiles++;

                RaiseProgressChanged(backupId, processedFiles, result.TotalFiles, result.TotalBytes, 0);
            }
            catch (Exception ex)
            {
                result.Errors.Add(new BackupError { FilePath = filePath, Message = ex.Message });
            }
        }
    }

    /// <summary>
    /// Disposes the backup provider asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous dispose operation.</returns>
    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        await DisposeAsyncCore().ConfigureAwait(false);
        _backupLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Performs additional cleanup operations specific to derived classes.
    /// </summary>
    /// <returns>A task representing the asynchronous cleanup operation.</returns>
    protected virtual ValueTask DisposeAsyncCore()
    {
        return ValueTask.CompletedTask;
    }
}
