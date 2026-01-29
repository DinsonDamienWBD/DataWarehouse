// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using System.Security.Cryptography;
using HashAlgorithmType = DataWarehouse.SDK.Contracts.TamperProof.HashAlgorithmType;

namespace DataWarehouse.Plugins.Worm.Software;

/// <summary>
/// Software-enforced WORM (Write-Once-Read-Many) storage provider.
/// Implements immutable storage using file system with JSON metadata sidecars for retention and legal hold enforcement.
///
/// <para>
/// Storage structure:
/// - Data files: {basePath}/{objectId}.dat
/// - Metadata files: {basePath}/{objectId}.meta.json
/// </para>
///
/// <para>
/// Features:
/// - Retention period enforcement (reject deletion before expiry)
/// - Legal hold support (prevent deletion even after retention expires)
/// - Content integrity verification via SHA-256 hashing
/// - Audit trail with author and comment tracking
/// - Retention extension (can only extend, never shorten)
/// </para>
///
/// <para>
/// Limitations:
/// - Software enforcement can be bypassed with admin/filesystem access
/// - No hardware-level immutability guarantees
/// - Relies on file system permissions for additional protection
/// </para>
/// </summary>
public class SoftwareWormPlugin : WormStorageProviderPluginBase
{
    private readonly string _basePath;
    private readonly object _lockObject = new();

    /// <summary>
    /// Plugin identifier.
    /// </summary>
    public override string Id => "com.datawarehouse.worm.software";

    /// <summary>
    /// Plugin name.
    /// </summary>
    public override string Name => "Software WORM Provider";

    /// <summary>
    /// Plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <summary>
    /// WORM enforcement mode - Software-enforced.
    /// </summary>
    public override WormEnforcementMode EnforcementMode => WormEnforcementMode.Software;

    /// <summary>
    /// Initializes a new instance of the SoftwareWormPlugin.
    /// </summary>
    /// <param name="basePath">Base directory path for WORM storage. Defaults to ./worm-storage if not specified.</param>
    public SoftwareWormPlugin(string? basePath = null)
    {
        _basePath = basePath ?? Path.Combine(Directory.GetCurrentDirectory(), "worm-storage");

        // Ensure base directory exists
        if (!Directory.Exists(_basePath))
        {
            Directory.CreateDirectory(_basePath);
        }
    }

    /// <summary>
    /// Starts the WORM plugin. No initialization required for software enforcement.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public override Task StartAsync(CancellationToken ct)
    {
        // Verify base path is accessible
        if (!Directory.Exists(_basePath))
        {
            throw new InvalidOperationException($"WORM storage base path does not exist: {_basePath}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the WORM plugin. No cleanup required for software enforcement.
    /// </summary>
    public override Task StopAsync()
    {
        // No resources to clean up for file-based storage
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads data from WORM storage.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream containing the immutable data.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the object does not exist.</exception>
    public override async Task<Stream> ReadAsync(Guid objectId, CancellationToken ct = default)
    {
        var metadata = await GetMetadataInternalAsync(objectId, ct);
        if (metadata == null)
        {
            throw new KeyNotFoundException($"WORM object {objectId} does not exist.");
        }

        var dataPath = GetDataFilePath(objectId);
        if (!File.Exists(dataPath))
        {
            throw new KeyNotFoundException($"WORM object {objectId} data file not found at {dataPath}.");
        }

        // Return a read-only file stream
        return new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>
    /// Gets the current status of a WORM object including retention and legal holds.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Status information including existence, retention expiry, and active legal holds.</returns>
    public override async Task<WormObjectStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default)
    {
        var metadata = await GetMetadataInternalAsync(objectId, ct);

        if (metadata == null)
        {
            return new WormObjectStatus
            {
                Exists = false,
                ObjectId = objectId
            };
        }

        return new WormObjectStatus
        {
            Exists = true,
            ObjectId = objectId,
            RetentionExpiry = metadata.RetentionExpiry,
            LegalHolds = metadata.LegalHolds.Select(h => h.ToLegalHold()).ToList(),
            SizeBytes = metadata.SizeBytes,
            WrittenAt = metadata.WrittenAt,
            ContentHash = metadata.ContentHash,
            HardwareImmutabilityEnabled = false,
            StorageKey = metadata.DataFilePath
        };
    }

    /// <summary>
    /// Checks if a WORM object exists in storage.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the object exists, false otherwise.</returns>
    public override Task<bool> ExistsAsync(Guid objectId, CancellationToken ct = default)
    {
        var metadataPath = GetMetadataFilePath(objectId);
        var dataPath = GetDataFilePath(objectId);

        return Task.FromResult(File.Exists(metadataPath) && File.Exists(dataPath));
    }

    /// <summary>
    /// Gets all active legal holds for a WORM object.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of active legal holds, empty if none.</returns>
    public override async Task<IReadOnlyList<LegalHold>> GetLegalHoldsAsync(Guid objectId, CancellationToken ct = default)
    {
        var metadata = await GetMetadataInternalAsync(objectId, ct);

        if (metadata == null)
        {
            return Array.Empty<LegalHold>();
        }

        return metadata.LegalHolds.Select(h => h.ToLegalHold()).ToList();
    }

    /// <summary>
    /// Provider-specific write implementation.
    /// Writes data to file system with metadata sidecar for retention enforcement.
    /// </summary>
    protected override async Task<WormWriteResult> WriteInternalAsync(
        Guid objectId,
        Stream data,
        WormRetentionPolicy retention,
        WriteContext context,
        CancellationToken ct)
    {
        var writtenAt = DateTimeOffset.UtcNow;
        var retentionExpiry = writtenAt.Add(retention.RetentionPeriod);

        var dataPath = GetDataFilePath(objectId);
        var metadataPath = GetMetadataFilePath(objectId);

        // Use lock to ensure atomic write (prevent concurrent writes)
        lock (_lockObject)
        {
            if (File.Exists(dataPath) || File.Exists(metadataPath))
            {
                throw new InvalidOperationException($"WORM object {objectId} already exists. Cannot overwrite.");
            }
        }

        // Write data file
        long sizeBytes;
        string contentHash;

        await using (var fileStream = new FileStream(dataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // Compute hash while writing
            using var sha256 = SHA256.Create();
            var buffer = new byte[81920]; // 80KB buffer
            var hashStream = new CryptoStream(Stream.Null, sha256, CryptoStreamMode.Write);

            int bytesRead;
            sizeBytes = 0;

            while ((bytesRead = await data.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                await hashStream.WriteAsync(buffer, 0, bytesRead, ct);
                sizeBytes += bytesRead;
            }

            await hashStream.FlushFinalBlockAsync(ct);
            contentHash = Convert.ToHexString(sha256.Hash!);
        }

        // Set file to read-only as additional protection
        File.SetAttributes(dataPath, FileAttributes.ReadOnly);

        // Create metadata
        var metadata = WormMetadata.Create(
            objectId: objectId,
            writtenAt: writtenAt,
            retentionExpiry: retentionExpiry,
            contentHash: contentHash,
            hashAlgorithm: HashAlgorithmType.SHA256,
            sizeBytes: sizeBytes,
            author: context.Author,
            comment: context.Comment,
            dataFilePath: dataPath,
            providerMetadata: new Dictionary<string, string>
            {
                ["BasePath"] = _basePath,
                ["FileSystem"] = "Local"
            }
        );

        // Write metadata file
        var metadataJson = metadata.ToJson();
        await File.WriteAllTextAsync(metadataPath, metadataJson, ct);

        // Set metadata file to read-only
        File.SetAttributes(metadataPath, FileAttributes.ReadOnly);

        return new WormWriteResult
        {
            StorageKey = dataPath,
            ContentHash = contentHash,
            HashAlgorithm = HashAlgorithmType.SHA256,
            SizeBytes = sizeBytes,
            WrittenAt = writtenAt,
            RetentionExpiry = retentionExpiry,
            HardwareImmutabilityEnabled = false,
            ProviderMetadata = new Dictionary<string, string>
            {
                ["MetadataPath"] = metadataPath,
                ["EnforcementMode"] = "Software"
            }
        };
    }

    /// <summary>
    /// Provider-specific retention extension implementation.
    /// Updates retention metadata with new expiry date.
    /// </summary>
    protected override async Task ExtendRetentionInternalAsync(
        Guid objectId,
        DateTimeOffset newExpiry,
        CancellationToken ct)
    {
        var metadataPath = GetMetadataFilePath(objectId);

        // Read current metadata
        var metadata = await GetMetadataInternalAsync(objectId, ct);
        if (metadata == null)
        {
            throw new KeyNotFoundException($"WORM object {objectId} does not exist.");
        }

        // Remove read-only attribute temporarily
        File.SetAttributes(metadataPath, FileAttributes.Normal);

        try
        {
            // Create new metadata with extended retention
            var updatedMetadata = WormMetadata.Create(
                objectId: metadata.ObjectId,
                writtenAt: metadata.WrittenAt,
                retentionExpiry: newExpiry,
                contentHash: metadata.ContentHash,
                hashAlgorithm: metadata.HashAlgorithm,
                sizeBytes: metadata.SizeBytes,
                author: metadata.Author,
                comment: metadata.Comment,
                dataFilePath: metadata.DataFilePath,
                providerMetadata: metadata.ProviderMetadata
            );

            // Preserve legal holds
            updatedMetadata.LegalHolds.AddRange(metadata.LegalHolds);

            // Write updated metadata
            var metadataJson = updatedMetadata.ToJson();
            await File.WriteAllTextAsync(metadataPath, metadataJson, ct);
        }
        finally
        {
            // Restore read-only attribute
            File.SetAttributes(metadataPath, FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// Provider-specific legal hold placement implementation.
    /// Adds legal hold to metadata.
    /// </summary>
    protected override async Task PlaceLegalHoldInternalAsync(
        Guid objectId,
        string holdId,
        string reason,
        CancellationToken ct)
    {
        var metadataPath = GetMetadataFilePath(objectId);

        // Read current metadata
        var metadata = await GetMetadataInternalAsync(objectId, ct);
        if (metadata == null)
        {
            throw new KeyNotFoundException($"WORM object {objectId} does not exist.");
        }

        // Create new legal hold
        var hold = new LegalHoldRecord
        {
            HoldId = holdId,
            Reason = reason,
            PlacedAt = DateTimeOffset.UtcNow,
            PlacedBy = Environment.UserName, // In production, use authenticated principal
            CaseNumber = null,
            ExpiresAt = null,
            Metadata = null
        };

        // Remove read-only attribute temporarily
        File.SetAttributes(metadataPath, FileAttributes.Normal);

        try
        {
            // Add hold to metadata
            metadata.LegalHolds.Add(hold);

            // Write updated metadata
            var metadataJson = metadata.ToJson();
            await File.WriteAllTextAsync(metadataPath, metadataJson, ct);
        }
        finally
        {
            // Restore read-only attribute
            File.SetAttributes(metadataPath, FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// Provider-specific legal hold removal implementation.
    /// Removes legal hold from metadata.
    /// </summary>
    protected override async Task RemoveLegalHoldInternalAsync(
        Guid objectId,
        string holdId,
        CancellationToken ct)
    {
        var metadataPath = GetMetadataFilePath(objectId);

        // Read current metadata
        var metadata = await GetMetadataInternalAsync(objectId, ct);
        if (metadata == null)
        {
            throw new KeyNotFoundException($"WORM object {objectId} does not exist.");
        }

        // Remove read-only attribute temporarily
        File.SetAttributes(metadataPath, FileAttributes.Normal);

        try
        {
            // Remove hold from metadata
            var removed = metadata.LegalHolds.RemoveAll(h => h.HoldId == holdId);

            if (removed == 0)
            {
                throw new KeyNotFoundException($"Legal hold '{holdId}' not found on object {objectId}.");
            }

            // Write updated metadata
            var metadataJson = metadata.ToJson();
            await File.WriteAllTextAsync(metadataPath, metadataJson, ct);
        }
        finally
        {
            // Restore read-only attribute
            File.SetAttributes(metadataPath, FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// Gets metadata file path for an object.
    /// </summary>
    private string GetMetadataFilePath(Guid objectId)
    {
        return Path.Combine(_basePath, $"{objectId}.meta.json");
    }

    /// <summary>
    /// Gets data file path for an object.
    /// </summary>
    private string GetDataFilePath(Guid objectId)
    {
        return Path.Combine(_basePath, $"{objectId}.dat");
    }

    /// <summary>
    /// Reads metadata from file system.
    /// </summary>
    private async Task<WormMetadata?> GetMetadataInternalAsync(Guid objectId, CancellationToken ct)
    {
        var metadataPath = GetMetadataFilePath(objectId);

        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var metadataJson = await File.ReadAllTextAsync(metadataPath, ct);
            return WormMetadata.FromJson(metadataJson);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read WORM metadata for object {objectId}.", ex);
        }
    }

    /// <summary>
    /// Provides metadata about this WORM provider for diagnostics and AI agents.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["BasePath"] = _basePath;
        metadata["StorageFormat"] = "FileSystem+JSON";
        metadata["SoftwareEnforced"] = true;
        metadata["SupportsBypassWarning"] = true;
        return metadata;
    }
}
