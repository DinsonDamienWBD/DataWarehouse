using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts.Transit;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Offline;

/// <summary>
/// Store-and-forward transit strategy for offline/sneakernet data transfers.
/// Packages data with a JSON manifest and per-entry SHA-256 integrity hashes for air-gap transfer.
/// On ingest, validates manifest integrity and verifies every entry hash before accepting data.
/// </summary>
/// <remarks>
/// <para>
/// This strategy supports two modes:
/// <list type="bullet">
/// <item><description><b>Package mode (source side):</b> Takes data from a stream or source endpoint and packages
/// it into a directory structure with a <c>manifest.json</c> containing SHA-256 hashes for each entry.</description></item>
/// <item><description><b>Ingest mode (destination side):</b> Reads a previously created package, verifies the
/// manifest integrity hash and all per-entry hashes, then copies verified data to the final destination.</description></item>
/// </list>
/// </para>
/// <para>
/// Package directory structure:
/// <code>
/// {packageId}/
///   manifest.json       -- ForwardPackage serialized as JSON
///   data/
///     {entry1.RelativePath}
///     {entry2.RelativePath}
///     ...
/// </code>
/// </para>
/// <para>
/// Integrity verification uses streaming SHA-256 computation via <see cref="IncrementalHash"/>
/// to handle large files without loading them entirely into memory.
/// </para>
/// </remarks>
internal sealed class StoreAndForwardStrategy : DataTransitStrategyBase
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, string> _activePackages = new();

    /// <inheritdoc/>
    public override string StrategyId => "transit-store-and-forward";

    /// <inheritdoc/>
    public override string Name => "Store-and-Forward Transfer";

    /// <inheritdoc/>
    public override TransitCapabilities Capabilities => new()
    {
        SupportsResumable = true,
        SupportsStreaming = false,
        SupportsDelta = false,
        SupportsMultiPath = false,
        SupportsP2P = false,
        SupportsOffline = true,
        SupportsCompression = true,
        SupportsEncryption = true,
        MaxTransferSizeBytes = long.MaxValue,
        SupportedProtocols = ["file", "offline", "sneakernet"]
    };

    /// <summary>
    /// Checks whether this strategy can handle the specified endpoint.
    /// Returns true if the endpoint URI scheme is "file" or "offline", or if the URI local path exists as a directory.
    /// </summary>
    /// <param name="endpoint">The target endpoint to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the strategy can handle the endpoint; false otherwise.</returns>
    public override Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default)
    {
        var scheme = endpoint.Uri.Scheme.ToLowerInvariant();
        if (scheme is "file" or "offline" or "sneakernet")
        {
            return Task.FromResult(true);
        }

        // Check if the path exists as a directory (existing package)
        if (endpoint.Uri.IsFile && Directory.Exists(endpoint.Uri.LocalPath))
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Executes a store-and-forward transfer. Operates in two modes:
    /// <list type="bullet">
    /// <item><description><b>Ingest mode:</b> If the source points to an existing package directory,
    /// verifies manifest and entry integrity, then copies data to the destination.</description></item>
    /// <item><description><b>Package mode:</b> Creates a new package at the destination with manifest
    /// and SHA-256 integrity hashes for each data entry.</description></item>
    /// </list>
    /// </summary>
    /// <param name="request">The transfer request containing source, destination, and data stream.</param>
    /// <param name="progress">Optional progress reporter for tracking transfer status.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the transfer operation.</returns>
    public override async Task<TransitResult> TransferAsync(
        TransitRequest request,
        IProgress<TransitProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IncrementActiveTransfers();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Determine mode: ingest vs package
            var sourcePath = GetLocalPath(request.Source.Uri);
            if (sourcePath != null && Directory.Exists(sourcePath) && File.Exists(Path.Combine(sourcePath, "manifest.json")))
            {
                // Ingest mode: source is an existing package
                return await IngestPackageAsync(request, sourcePath, progress, ct);
            }

            // Package mode: create new package at destination
            return await CreatePackageAsync(request, progress, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordTransferFailure();
            return new TransitResult
            {
                TransferId = request.TransferId,
                Success = false,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed,
                StrategyUsed = StrategyId
            };
        }
        finally
        {
            DecrementActiveTransfers();
            stopwatch.Stop();
        }
    }

    /// <summary>
    /// Creates a store-and-forward package at the destination path.
    /// Copies data from the request stream into a structured directory with manifest and integrity hashes.
    /// </summary>
    /// <param name="request">The transfer request.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the package creation.</returns>
    private async Task<TransitResult> CreatePackageAsync(
        TransitRequest request,
        IProgress<TransitProgress>? progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var packageId = Guid.NewGuid().ToString("N");
        var transferId = request.TransferId;

        // Determine output path from destination URI
        var outputBasePath = GetLocalPath(request.Destination.Uri)
            ?? throw new InvalidOperationException(
                $"Cannot determine local path from destination URI: {request.Destination.Uri}");

        var packageDir = Path.Combine(outputBasePath, packageId);
        var dataDir = Path.Combine(packageDir, "data");
        Directory.CreateDirectory(dataDir);

        _activePackages[transferId] = packageDir;

        try
        {
            var entries = new List<PackageEntry>();
            long totalBytes = 0;

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Packaging",
                PercentComplete = 0
            });

            if (request.DataStream != null)
            {
                // Single stream mode: write as payload.bin
                var payloadPath = Path.Combine(dataDir, "payload.bin");
                var (size, hash) = await CopyStreamWithHashAsync(request.DataStream, payloadPath, ct);

                entries.Add(new PackageEntry
                {
                    RelativePath = "payload.bin",
                    SizeBytes = size,
                    Sha256Hash = hash,
                    ModifiedAt = DateTime.UtcNow
                });

                totalBytes = size;

                progress?.Report(new TransitProgress
                {
                    TransferId = transferId,
                    BytesTransferred = totalBytes,
                    TotalBytes = request.SizeBytes > 0 ? request.SizeBytes : totalBytes,
                    PercentComplete = 50,
                    CurrentPhase = "Packaging"
                });
            }
            else if (request.Source.Uri.IsFile)
            {
                // Source is a local directory: copy all files
                var sourceDir = request.Source.Uri.LocalPath;
                if (Directory.Exists(sourceDir))
                {
                    var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                    var filesProcessed = 0;

                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();

                        var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                        var destPath = Path.Combine(dataDir, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                        await using var sourceStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                        var (size, hash) = await CopyStreamWithHashAsync(sourceStream, destPath, ct);

                        entries.Add(new PackageEntry
                        {
                            RelativePath = relativePath,
                            SizeBytes = size,
                            Sha256Hash = hash,
                            ModifiedAt = File.GetLastWriteTimeUtc(file)
                        });

                        totalBytes += size;
                        filesProcessed++;

                        progress?.Report(new TransitProgress
                        {
                            TransferId = transferId,
                            BytesTransferred = totalBytes,
                            TotalBytes = request.SizeBytes > 0 ? request.SizeBytes : totalBytes,
                            PercentComplete = files.Length > 0 ? (double)filesProcessed / files.Length * 50 : 50,
                            CurrentPhase = $"Packaging ({filesProcessed}/{files.Length} files)"
                        });
                    }
                }
            }

            // Build the manifest
            var manifest = new ForwardPackage
            {
                PackageId = packageId,
                TransferId = transferId,
                CreatedAt = DateTime.UtcNow,
                SourceDescription = request.Source.Uri.ToString(),
                DestinationDescription = request.Destination.Uri.ToString(),
                TotalSizeBytes = totalBytes,
                Entries = entries
            };

            // Compute manifest hash: serialize without ManifestHash, compute SHA-256
            manifest = manifest with { ManifestHash = ComputeManifestHash(manifest) };

            // Write manifest.json
            var manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            await File.WriteAllTextAsync(Path.Combine(packageDir, "manifest.json"), manifestJson, ct);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalBytes,
                TotalBytes = totalBytes,
                PercentComplete = 100,
                CurrentPhase = "Package complete"
            });

            stopwatch.Stop();
            RecordTransferSuccess(totalBytes);

            return new TransitResult
            {
                TransferId = transferId,
                Success = true,
                BytesTransferred = totalBytes,
                Duration = stopwatch.Elapsed,
                StrategyUsed = StrategyId,
                Metadata = new Dictionary<string, string>
                {
                    ["packageId"] = packageId,
                    ["outputPath"] = packageDir,
                    ["entryCount"] = entries.Count.ToString(),
                    ["manifestHash"] = manifest.ManifestHash ?? string.Empty
                }
            };
        }
        finally
        {
            _activePackages.TryRemove(transferId, out _);
        }
    }

    /// <summary>
    /// Ingests a previously created store-and-forward package by verifying integrity and copying data.
    /// Validates the manifest hash and all per-entry SHA-256 hashes before accepting data.
    /// </summary>
    /// <param name="request">The transfer request.</param>
    /// <param name="packagePath">The local path to the package directory.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the ingest operation.</returns>
    private async Task<TransitResult> IngestPackageAsync(
        TransitRequest request,
        string packagePath,
        IProgress<TransitProgress>? progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var transferId = request.TransferId;

        progress?.Report(new TransitProgress
        {
            TransferId = transferId,
            CurrentPhase = "Reading manifest",
            PercentComplete = 0
        });

        // Read and deserialize manifest
        var manifestPath = Path.Combine(packagePath, "manifest.json");
        var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize<ForwardPackage>(manifestJson, ManifestJsonOptions);

        if (manifest == null)
        {
            RecordTransferFailure();
            return new TransitResult
            {
                TransferId = transferId,
                Success = false,
                ErrorMessage = "Failed to deserialize manifest.json - file may be corrupted",
                Duration = stopwatch.Elapsed,
                StrategyUsed = StrategyId
            };
        }

        // Verify manifest integrity
        progress?.Report(new TransitProgress
        {
            TransferId = transferId,
            CurrentPhase = "Verifying manifest integrity",
            PercentComplete = 5
        });

        var expectedManifestHash = manifest.ManifestHash;
        var computedManifestHash = ComputeManifestHash(manifest);

        if (!string.Equals(expectedManifestHash, computedManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            RecordTransferFailure();
            return new TransitResult
            {
                TransferId = transferId,
                Success = false,
                ErrorMessage = "Manifest integrity check failed - package may be tampered",
                Duration = stopwatch.Elapsed,
                StrategyUsed = StrategyId
            };
        }

        // Verify each entry's SHA-256 hash
        var dataDir = Path.Combine(packagePath, "data");
        var entriesVerified = 0;
        long totalBytesVerified = 0;

        foreach (var entry in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var entryPath = Path.Combine(dataDir, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(entryPath))
            {
                RecordTransferFailure();
                return new TransitResult
                {
                    TransferId = transferId,
                    Success = false,
                    ErrorMessage = $"Missing entry file: {entry.RelativePath}",
                    Duration = stopwatch.Elapsed,
                    StrategyUsed = StrategyId
                };
            }

            var computedHash = await ComputeFileHashAsync(entryPath, ct);
            if (!string.Equals(entry.Sha256Hash, computedHash, StringComparison.OrdinalIgnoreCase))
            {
                RecordTransferFailure();
                return new TransitResult
                {
                    TransferId = transferId,
                    Success = false,
                    ErrorMessage = $"Integrity check failed for entry: {entry.RelativePath} (expected: {entry.Sha256Hash}, actual: {computedHash})",
                    Duration = stopwatch.Elapsed,
                    StrategyUsed = StrategyId
                };
            }

            entriesVerified++;
            totalBytesVerified += entry.SizeBytes;

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalBytesVerified,
                TotalBytes = manifest.TotalSizeBytes,
                PercentComplete = manifest.Entries.Count > 0
                    ? 10 + (double)entriesVerified / manifest.Entries.Count * 40
                    : 50,
                CurrentPhase = $"Verifying ({entriesVerified}/{manifest.Entries.Count} entries)"
            });
        }

        // Copy verified data to final destination
        var destinationPath = GetLocalPath(request.Destination.Uri);
        long totalBytesCopied = 0;

        if (destinationPath != null)
        {
            Directory.CreateDirectory(destinationPath);
            var entriesCopied = 0;

            foreach (var entry in manifest.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var sourcePath = Path.Combine(dataDir, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var destFilePath = Path.Combine(destinationPath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);

                await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var destStream = new FileStream(destFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await sourceStream.CopyToAsync(destStream, ct);

                totalBytesCopied += entry.SizeBytes;
                entriesCopied++;

                progress?.Report(new TransitProgress
                {
                    TransferId = transferId,
                    BytesTransferred = totalBytesCopied,
                    TotalBytes = manifest.TotalSizeBytes,
                    PercentComplete = manifest.Entries.Count > 0
                        ? 50 + (double)entriesCopied / manifest.Entries.Count * 50
                        : 100,
                    CurrentPhase = $"Copying ({entriesCopied}/{manifest.Entries.Count} entries)"
                });
            }
        }

        stopwatch.Stop();
        RecordTransferSuccess(totalBytesCopied);

        return new TransitResult
        {
            TransferId = transferId,
            Success = true,
            BytesTransferred = totalBytesCopied,
            Duration = stopwatch.Elapsed,
            StrategyUsed = StrategyId,
            Metadata = new Dictionary<string, string>
            {
                ["packageId"] = manifest.PackageId,
                ["entriesVerified"] = entriesVerified.ToString(),
                ["manifestHash"] = manifest.ManifestHash ?? string.Empty,
                ["sourcePackagePath"] = packagePath
            }
        };
    }

    /// <summary>
    /// Copies a stream to a file while computing the SHA-256 hash using incremental hashing.
    /// Uses streaming computation to handle large files without loading them entirely into memory.
    /// </summary>
    /// <param name="source">The source stream to read from.</param>
    /// <param name="destinationPath">The file path to write to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of (bytes written, SHA-256 hash hex string).</returns>
    private static async Task<(long Size, string Hash)> CopyStreamWithHashAsync(
        Stream source, string destinationPath, CancellationToken ct)
    {
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalBytes = 0;
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            incrementalHash.AppendData(buffer, 0, bytesRead);
            await destStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalBytes += bytesRead;
        }

        var hashBytes = incrementalHash.GetHashAndReset();
        var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

        return (totalBytes, hashHex);
    }

    /// <summary>
    /// Computes the SHA-256 hash of a file using streaming incremental hash computation.
    /// Reads the file in chunks to avoid loading the entire file into memory.
    /// </summary>
    /// <param name="filePath">The path to the file to hash.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The SHA-256 hash as a lowercase hex string.</returns>
    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);

        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            incrementalHash.AppendData(buffer, 0, bytesRead);
        }

        var hashBytes = incrementalHash.GetHashAndReset();
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the integrity hash of a manifest by serializing without the ManifestHash field
    /// and computing SHA-256 of the resulting JSON.
    /// </summary>
    /// <param name="manifest">The manifest to compute the hash for.</param>
    /// <returns>The SHA-256 hash of the manifest JSON as a lowercase hex string.</returns>
    private static string ComputeManifestHash(ForwardPackage manifest)
    {
        // Serialize manifest with ManifestHash excluded (set to null)
        var manifestForHashing = manifest with { ManifestHash = null };
        var json = JsonSerializer.Serialize(manifestForHashing, ManifestJsonOptions);
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        var hashBytes = SHA256.HashData(jsonBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Extracts a local file system path from a URI.
    /// Handles file:// URIs and direct local paths.
    /// </summary>
    /// <param name="uri">The URI to extract the path from.</param>
    /// <returns>The local path if extractable; null otherwise.</returns>
    private static string? GetLocalPath(Uri uri)
    {
        if (uri.IsFile)
        {
            return uri.LocalPath;
        }

        if (uri.Scheme is "offline" or "sneakernet")
        {
            // Use the path portion as a local directory path
            var path = uri.AbsolutePath.TrimStart('/');
            return string.IsNullOrEmpty(path) ? null : path;
        }

        return null;
    }
}

/// <summary>
/// Represents a store-and-forward package containing data entries and integrity metadata
/// for offline/air-gap transfer.
/// </summary>
internal sealed record ForwardPackage
{
    /// <summary>
    /// Unique identifier for this package.
    /// </summary>
    public string PackageId { get; init; } = string.Empty;

    /// <summary>
    /// The transfer ID associated with this package.
    /// </summary>
    public string TransferId { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp when the package was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Description of the source endpoint.
    /// </summary>
    public string SourceDescription { get; init; } = string.Empty;

    /// <summary>
    /// Description of the destination endpoint.
    /// </summary>
    public string DestinationDescription { get; init; } = string.Empty;

    /// <summary>
    /// Total size in bytes of all data entries in the package.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// The list of data entries in this package.
    /// </summary>
    public List<PackageEntry> Entries { get; init; } = [];

    /// <summary>
    /// SHA-256 hash of the serialized manifest (excluding this field) for tamper detection.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManifestHash { get; init; }
}

/// <summary>
/// Represents a single data entry within a store-and-forward package.
/// Contains the file path, size, SHA-256 hash, and modification timestamp for integrity verification.
/// </summary>
internal sealed record PackageEntry
{
    /// <summary>
    /// Relative path of the entry within the package data directory.
    /// Uses forward slash as separator regardless of platform.
    /// </summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>
    /// Size of the entry file in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// SHA-256 hash of the entry file content as a lowercase hex string.
    /// </summary>
    public string Sha256Hash { get; init; } = string.Empty;

    /// <summary>
    /// UTC timestamp of when the entry was last modified.
    /// </summary>
    public DateTime ModifiedAt { get; init; }
}
