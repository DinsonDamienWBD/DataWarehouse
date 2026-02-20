using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Storage.Fabric;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UniversalFabric.S3Server;

/// <summary>
/// Manages S3 bucket lifecycle, metadata, and backend mappings.
/// </summary>
/// <remarks>
/// <para>
/// Each bucket is mapped to a storage backend via the <see cref="IBackendRegistry"/>.
/// Buckets without an explicit backend mapping use the default backend (first registered).
/// </para>
/// <para>
/// Bucket names are validated per the S3 naming specification: 3-63 characters,
/// lowercase letters, numbers, and hyphens only, must start with a letter or number.
/// </para>
/// <para>
/// Bucket metadata persists to disk as JSON, surviving server restarts.
/// All operations are thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </para>
/// </remarks>
public sealed class S3BucketManager
{
    private readonly BoundedDictionary<string, S3BucketInfo> _buckets = new BoundedDictionary<string, S3BucketInfo>(1000);
    private readonly IBackendRegistry _registry;
    private readonly string _storagePath;
    private readonly object _persistLock = new();

    /// <summary>
    /// Initializes a new bucket manager backed by the specified backend registry.
    /// </summary>
    /// <param name="registry">The backend registry for resolving backend IDs.</param>
    /// <param name="storagePath">The file path for persisting bucket metadata as JSON.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="registry"/> or <paramref name="storagePath"/> is null.
    /// </exception>
    public S3BucketManager(IBackendRegistry registry, string storagePath)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        if (string.IsNullOrWhiteSpace(storagePath))
        {
            throw new ArgumentNullException(nameof(storagePath));
        }

        _storagePath = storagePath;
        LoadFromDisk();
    }

    /// <summary>
    /// Creates a new S3 bucket with optional backend mapping and region.
    /// </summary>
    /// <param name="name">The bucket name (must pass S3 naming validation).</param>
    /// <param name="backendId">Optional backend ID to map the bucket to. If null, uses the default backend.</param>
    /// <param name="region">Optional region for the bucket (e.g., "us-east-1").</param>
    /// <returns>The newly created bucket info.</returns>
    /// <exception cref="ArgumentException">Thrown when the bucket name is invalid or already exists.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the specified backend ID is not registered.</exception>
    public S3BucketInfo CreateBucket(string name, string? backendId = null, string? region = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!IsValidBucketName(name))
        {
            throw new ArgumentException($"Invalid bucket name: '{name}'. Bucket names must be 3-63 characters, " +
                "contain only lowercase letters, numbers, and hyphens, and must start and end with a letter or number.", nameof(name));
        }

        if (_buckets.ContainsKey(name))
        {
            throw new ArgumentException($"Bucket '{name}' already exists.", nameof(name));
        }

        // Validate backend ID if provided
        if (backendId is not null && _registry.GetById(backendId) is null)
        {
            throw new InvalidOperationException($"Backend '{backendId}' is not registered.");
        }

        var bucketInfo = new S3BucketInfo
        {
            Name = name,
            CreationDate = DateTime.UtcNow,
            BackendId = backendId,
            Region = region ?? "us-east-1",
            VersioningEnabled = false,
            ObjectCount = 0,
            TotalSizeBytes = 0
        };

        if (!_buckets.TryAdd(name, bucketInfo))
        {
            throw new ArgumentException($"Bucket '{name}' already exists (concurrent creation).", nameof(name));
        }

        PersistToDisk();
        return bucketInfo;
    }

    /// <summary>
    /// Deletes a bucket. The bucket must be empty (ObjectCount == 0).
    /// </summary>
    /// <param name="name">The name of the bucket to delete.</param>
    /// <exception cref="ArgumentException">Thrown when the bucket does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the bucket is not empty.</exception>
    public void DeleteBucket(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_buckets.TryGetValue(name, out var bucket))
        {
            throw new ArgumentException($"Bucket '{name}' does not exist.", nameof(name));
        }

        if (bucket.ObjectCount > 0)
        {
            throw new InvalidOperationException(
                $"Cannot delete bucket '{name}': bucket is not empty ({bucket.ObjectCount} objects remaining).");
        }

        _buckets.TryRemove(name, out _);
        PersistToDisk();
    }

    /// <summary>
    /// Checks whether a bucket with the given name exists.
    /// </summary>
    /// <param name="name">The bucket name to check.</param>
    /// <returns>True if the bucket exists; false otherwise.</returns>
    public bool BucketExists(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _buckets.ContainsKey(name);
    }

    /// <summary>
    /// Lists all buckets.
    /// </summary>
    /// <returns>A read-only list of all bucket info records.</returns>
    public IReadOnlyList<S3BucketInfo> ListBuckets()
    {
        return _buckets.Values.OrderBy(b => b.Name, StringComparer.Ordinal).ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets information about a specific bucket.
    /// </summary>
    /// <param name="name">The bucket name.</param>
    /// <returns>The bucket info, or null if not found.</returns>
    public S3BucketInfo? GetBucket(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _buckets.TryGetValue(name, out var bucket) ? bucket : null;
    }

    /// <summary>
    /// Gets the backend ID mapped to a bucket.
    /// </summary>
    /// <param name="bucketName">The bucket name.</param>
    /// <returns>The backend ID, or null if the bucket does not exist or has no explicit mapping.</returns>
    public string? GetBackendId(string bucketName)
    {
        ArgumentNullException.ThrowIfNull(bucketName);
        return _buckets.TryGetValue(bucketName, out var bucket) ? bucket.BackendId : null;
    }

    /// <summary>
    /// Updates the object count and total size for a bucket (called after object operations).
    /// </summary>
    /// <param name="bucketName">The bucket name.</param>
    /// <param name="objectCountDelta">Change in object count (positive for adds, negative for deletes).</param>
    /// <param name="sizeDelta">Change in total size bytes (positive for adds, negative for deletes).</param>
    public void UpdateBucketStats(string bucketName, long objectCountDelta, long sizeDelta)
    {
        ArgumentNullException.ThrowIfNull(bucketName);

        if (_buckets.TryGetValue(bucketName, out var bucket))
        {
            var updated = bucket with
            {
                ObjectCount = Math.Max(0, bucket.ObjectCount + objectCountDelta),
                TotalSizeBytes = Math.Max(0, bucket.TotalSizeBytes + sizeDelta)
            };
            _buckets[bucketName] = updated;
            PersistToDisk();
        }
    }

    /// <summary>
    /// Enables or disables versioning for a bucket.
    /// </summary>
    /// <param name="bucketName">The bucket name.</param>
    /// <param name="enabled">Whether versioning should be enabled.</param>
    public void SetVersioning(string bucketName, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(bucketName);

        if (_buckets.TryGetValue(bucketName, out var bucket))
        {
            _buckets[bucketName] = bucket with { VersioningEnabled = enabled };
            PersistToDisk();
        }
    }

    /// <summary>
    /// Gets the total number of managed buckets.
    /// </summary>
    public int Count => _buckets.Count;

    /// <summary>
    /// Validates a bucket name per the S3 naming specification.
    /// Rules:
    /// - 3-63 characters long
    /// - Only lowercase letters, numbers, and hyphens
    /// - Must start with a letter or number
    /// - Must end with a letter or number
    /// - No consecutive periods
    /// - No period adjacent to hyphen
    /// - Must not be formatted as an IP address (e.g., 192.168.1.1)
    /// </summary>
    /// <param name="name">The bucket name to validate.</param>
    /// <returns>True if the name is valid per S3 naming rules.</returns>
    public static bool IsValidBucketName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // Length check: 3-63 characters
        if (name.Length < 3 || name.Length > 63)
        {
            return false;
        }

        // Must start with letter or number
        if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        // Must end with letter or number
        if (!char.IsAsciiLetterLower(name[^1]) && !char.IsAsciiDigit(name[^1]))
        {
            return false;
        }

        // Only lowercase letters, numbers, hyphens, and periods allowed
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-' && c != '.')
            {
                return false;
            }
        }

        // No consecutive periods
        if (name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        // No period adjacent to hyphen
        if (name.Contains(".-", StringComparison.Ordinal) || name.Contains("-.", StringComparison.Ordinal))
        {
            return false;
        }

        // Must not be formatted as an IP address
        if (Regex.IsMatch(name, @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$"))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Persists bucket metadata to the JSON file on disk.
    /// </summary>
    private void PersistToDisk()
    {
        lock (_persistLock)
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var entries = _buckets.Values.Select(b => new BucketEntry
            {
                Name = b.Name,
                CreationDate = b.CreationDate,
                BackendId = b.BackendId,
                Region = b.Region,
                VersioningEnabled = b.VersioningEnabled,
                ObjectCount = b.ObjectCount,
                TotalSizeBytes = b.TotalSizeBytes
            }).ToList();

            var json = JsonSerializer.Serialize(entries, BucketJsonContext.Default.ListBucketEntry);

            // Write atomically via temp file
            var tempPath = _storagePath + ".tmp";
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
    }

    /// <summary>
    /// Loads bucket metadata from the JSON file on disk.
    /// </summary>
    private void LoadFromDisk()
    {
        if (!File.Exists(_storagePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_storagePath, Encoding.UTF8);
            var entries = JsonSerializer.Deserialize(json, BucketJsonContext.Default.ListBucketEntry);

            if (entries is not null)
            {
                foreach (var entry in entries)
                {
                    var info = new S3BucketInfo
                    {
                        Name = entry.Name,
                        CreationDate = entry.CreationDate,
                        BackendId = entry.BackendId,
                        Region = entry.Region,
                        VersioningEnabled = entry.VersioningEnabled,
                        ObjectCount = entry.ObjectCount,
                        TotalSizeBytes = entry.TotalSizeBytes
                    };
                    _buckets[entry.Name] = info;
                }
            }
        }
        catch (JsonException)
        {
            // Corrupted file -- start fresh rather than crash
        }
    }

    /// <summary>
    /// Internal serialization model for bucket persistence.
    /// </summary>
    internal sealed class BucketEntry
    {
        public required string Name { get; set; }
        public DateTime CreationDate { get; set; }
        public string? BackendId { get; set; }
        public string? Region { get; set; }
        public bool VersioningEnabled { get; set; }
        public long ObjectCount { get; set; }
        public long TotalSizeBytes { get; set; }
    }
}

/// <summary>
/// Represents metadata and configuration for an S3 bucket.
/// </summary>
public record S3BucketInfo
{
    /// <summary>The bucket name.</summary>
    public required string Name { get; init; }

    /// <summary>When the bucket was created (UTC).</summary>
    public required DateTime CreationDate { get; init; }

    /// <summary>The backend ID this bucket is mapped to, or null for default backend.</summary>
    public string? BackendId { get; init; }

    /// <summary>The region for the bucket (e.g., "us-east-1").</summary>
    public string? Region { get; init; }

    /// <summary>Whether versioning is enabled for this bucket.</summary>
    public bool VersioningEnabled { get; init; }

    /// <summary>The number of objects currently in the bucket.</summary>
    public long ObjectCount { get; init; }

    /// <summary>The total size of all objects in the bucket in bytes.</summary>
    public long TotalSizeBytes { get; init; }
}

/// <summary>
/// Source-generated JSON serialization context for bucket persistence.
/// </summary>
[JsonSerializable(typeof(List<S3BucketManager.BucketEntry>))]
internal partial class BucketJsonContext : JsonSerializerContext
{
}
