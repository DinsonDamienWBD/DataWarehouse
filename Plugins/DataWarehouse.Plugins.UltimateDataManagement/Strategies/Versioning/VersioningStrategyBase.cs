using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Versioning;

/// <summary>
/// Metadata for a specific version of data.
/// </summary>
public sealed record VersionMetadata
{
    /// <summary>
    /// Author/creator of this version.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Commit message or description.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Custom key-value properties.
    /// </summary>
    public Dictionary<string, string>? Properties { get; init; }

    /// <summary>
    /// Parent version ID (for branching).
    /// </summary>
    public string? ParentVersionId { get; init; }

    /// <summary>
    /// Branch name (if applicable).
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Tags applied to this version.
    /// </summary>
    public string[]? Tags { get; init; }
}

/// <summary>
/// Information about a specific version.
/// </summary>
public sealed record VersionInfo
{
    /// <summary>
    /// Unique version identifier.
    /// </summary>
    public required string VersionId { get; init; }

    /// <summary>
    /// Object ID this version belongs to.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Sequential version number (if applicable).
    /// </summary>
    public long VersionNumber { get; init; }

    /// <summary>
    /// SHA-256 hash of the content.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Size of the content in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Timestamp when this version was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Metadata associated with this version.
    /// </summary>
    public VersionMetadata? Metadata { get; init; }

    /// <summary>
    /// Whether this is the current/latest version.
    /// </summary>
    public bool IsCurrent { get; init; }

    /// <summary>
    /// Whether this version is deleted (soft delete).
    /// </summary>
    public bool IsDeleted { get; init; }
}

/// <summary>
/// Result of a version comparison.
/// </summary>
public sealed class VersionDiff
{
    /// <summary>
    /// Source version ID.
    /// </summary>
    public required string FromVersionId { get; init; }

    /// <summary>
    /// Target version ID.
    /// </summary>
    public required string ToVersionId { get; init; }

    /// <summary>
    /// Size difference in bytes.
    /// </summary>
    public long SizeDifference { get; init; }

    /// <summary>
    /// Whether content is identical.
    /// </summary>
    public bool IsIdentical { get; init; }

    /// <summary>
    /// Delta/patch bytes (if applicable).
    /// </summary>
    public byte[]? DeltaBytes { get; init; }

    /// <summary>
    /// Summary of changes.
    /// </summary>
    public string? Summary { get; init; }
}

/// <summary>
/// Options for version listing.
/// </summary>
public sealed class VersionListOptions
{
    /// <summary>
    /// Maximum number of versions to return.
    /// </summary>
    public int MaxResults { get; init; } = 100;

    /// <summary>
    /// Include deleted versions.
    /// </summary>
    public bool IncludeDeleted { get; init; }

    /// <summary>
    /// Filter by branch name.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Filter by date range (from).
    /// </summary>
    public DateTime? FromDate { get; init; }

    /// <summary>
    /// Filter by date range (to).
    /// </summary>
    public DateTime? ToDate { get; init; }
}

/// <summary>
/// Interface for versioning strategies.
/// </summary>
public interface IVersioningStrategy : IDataManagementStrategy
{
    /// <summary>
    /// Creates a new version of the specified object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="data">Data stream for the new version.</param>
    /// <param name="metadata">Version metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Information about the created version.</returns>
    Task<VersionInfo> CreateVersionAsync(string objectId, Stream data, VersionMetadata? metadata, CancellationToken ct = default);

    /// <summary>
    /// Gets a specific version's data as a stream.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="versionId">Version identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Data stream for the version.</returns>
    Task<Stream> GetVersionAsync(string objectId, string versionId, CancellationToken ct = default);

    /// <summary>
    /// Lists all versions of an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="options">Listing options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of version information.</returns>
    Task<IEnumerable<VersionInfo>> ListVersionsAsync(string objectId, VersionListOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes a specific version.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="versionId">Version identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if version was deleted.</returns>
    Task<bool> DeleteVersionAsync(string objectId, string versionId, CancellationToken ct = default);

    /// <summary>
    /// Gets the current/latest version info.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current version information, or null if no versions exist.</returns>
    Task<VersionInfo?> GetCurrentVersionAsync(string objectId, CancellationToken ct = default);

    /// <summary>
    /// Restores a specific version as the current version.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="versionId">Version identifier to restore.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>New version info created from the restoration.</returns>
    Task<VersionInfo> RestoreVersionAsync(string objectId, string versionId, CancellationToken ct = default);

    /// <summary>
    /// Computes the difference between two versions.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="fromVersionId">Source version.</param>
    /// <param name="toVersionId">Target version.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Diff information.</returns>
    Task<VersionDiff> DiffVersionsAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct = default);

    /// <summary>
    /// Gets the total count of versions for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Version count.</returns>
    Task<long> GetVersionCountAsync(string objectId, CancellationToken ct = default);
}

/// <summary>
/// Abstract base class for versioning strategies.
/// Provides common functionality for version management implementations.
/// </summary>
public abstract class VersioningStrategyBase : DataManagementStrategyBase, IVersioningStrategy
{
    /// <inheritdoc/>
    public override DataManagementCategory Category => DataManagementCategory.Versioning;

    /// <inheritdoc/>
    public async Task<VersionInfo> CreateVersionAsync(string objectId, Stream data, VersionMetadata? metadata, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(data);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await CreateVersionCoreAsync(objectId, data, metadata ?? new VersionMetadata(), ct);
            sw.Stop();
            RecordWrite(result.SizeBytes, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Stream> GetVersionAsync(string objectId, string versionId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await GetVersionCoreAsync(objectId, versionId, ct);
            sw.Stop();
            RecordRead(result.Length, sw.Elapsed.TotalMilliseconds, hit: true);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<VersionInfo>> ListVersionsAsync(string objectId, VersionListOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await ListVersionsCoreAsync(objectId, options ?? new VersionListOptions(), ct);
            sw.Stop();
            RecordRead(0, sw.Elapsed.TotalMilliseconds, hit: true);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteVersionAsync(string objectId, string versionId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await DeleteVersionCoreAsync(objectId, versionId, ct);
            sw.Stop();
            RecordDelete(sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<VersionInfo?> GetCurrentVersionAsync(string objectId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return await GetCurrentVersionCoreAsync(objectId, ct);
    }

    /// <inheritdoc/>
    public async Task<VersionInfo> RestoreVersionAsync(string objectId, string versionId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RestoreVersionCoreAsync(objectId, versionId, ct);
            sw.Stop();
            RecordWrite(result.SizeBytes, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<VersionDiff> DiffVersionsAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromVersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toVersionId);

        return await DiffVersionsCoreAsync(objectId, fromVersionId, toVersionId, ct);
    }

    /// <inheritdoc/>
    public async Task<long> GetVersionCountAsync(string objectId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return await GetVersionCountCoreAsync(objectId, ct);
    }

    /// <summary>
    /// Core implementation for creating a new version.
    /// </summary>
    protected abstract Task<VersionInfo> CreateVersionCoreAsync(string objectId, Stream data, VersionMetadata metadata, CancellationToken ct);

    /// <summary>
    /// Core implementation for getting version data.
    /// </summary>
    protected abstract Task<Stream> GetVersionCoreAsync(string objectId, string versionId, CancellationToken ct);

    /// <summary>
    /// Core implementation for listing versions.
    /// </summary>
    protected abstract Task<IEnumerable<VersionInfo>> ListVersionsCoreAsync(string objectId, VersionListOptions options, CancellationToken ct);

    /// <summary>
    /// Core implementation for deleting a version.
    /// </summary>
    protected abstract Task<bool> DeleteVersionCoreAsync(string objectId, string versionId, CancellationToken ct);

    /// <summary>
    /// Core implementation for getting current version.
    /// </summary>
    protected abstract Task<VersionInfo?> GetCurrentVersionCoreAsync(string objectId, CancellationToken ct);

    /// <summary>
    /// Core implementation for restoring a version.
    /// </summary>
    protected abstract Task<VersionInfo> RestoreVersionCoreAsync(string objectId, string versionId, CancellationToken ct);

    /// <summary>
    /// Core implementation for computing version diff.
    /// </summary>
    protected abstract Task<VersionDiff> DiffVersionsCoreAsync(string objectId, string fromVersionId, string toVersionId, CancellationToken ct);

    /// <summary>
    /// Core implementation for getting version count.
    /// </summary>
    protected abstract Task<long> GetVersionCountCoreAsync(string objectId, CancellationToken ct);

    /// <summary>
    /// Computes SHA-256 hash of stream content.
    /// </summary>
    /// <param name="stream">Stream to hash.</param>
    /// <returns>Hex-encoded hash string.</returns>
    protected static string ComputeHash(Stream stream)
    {
        var position = stream.Position;
        try
        {
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = position;
        }
    }

    /// <summary>
    /// Computes SHA-256 hash of byte array.
    /// </summary>
    /// <param name="data">Data to hash.</param>
    /// <returns>Hex-encoded hash string.</returns>
    protected static string ComputeHash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a unique version ID.
    /// </summary>
    /// <returns>Unique identifier.</returns>
    protected static string GenerateVersionId()
    {
        return Guid.NewGuid().ToString("N")[..12];
    }
}
