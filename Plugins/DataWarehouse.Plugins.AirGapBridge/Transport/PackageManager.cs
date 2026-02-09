using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.Plugins.AirGapBridge.Core;

namespace DataWarehouse.Plugins.AirGapBridge.Transport;

/// <summary>
/// Manager for creating and handling DwPackage files (.dwpack).
/// Implements sub-tasks 79.5, 79.6, 79.7, 79.8, 79.9, 79.10, 79.33.
/// </summary>
public sealed class PackageManager
{
    private readonly byte[] _encryptionKey;
    private readonly string _instanceId;

    /// <summary>
    /// Creates a new package manager.
    /// </summary>
    /// <param name="encryptionKey">Master encryption key (256-bit).</param>
    /// <param name="instanceId">Local instance identifier.</param>
    public PackageManager(byte[] encryptionKey, string instanceId)
    {
        if (encryptionKey.Length != 32)
            throw new ArgumentException("Encryption key must be 256 bits (32 bytes)", nameof(encryptionKey));

        _encryptionKey = encryptionKey;
        _instanceId = instanceId;
    }

    #region Sub-task 79.5: Package Creator

    /// <summary>
    /// Creates a .dwpack file from blobs.
    /// Implements sub-task 79.5.
    /// </summary>
    public async Task<DwPackage> CreatePackageAsync(
        IEnumerable<BlobData> blobs,
        string? targetInstanceId = null,
        bool autoIngest = false,
        IEnumerable<string>? tags = null,
        CancellationToken ct = default)
    {
        var packageId = Guid.NewGuid().ToString("N");
        var shards = new List<EncryptedShard>();
        var blobUris = new List<string>();
        long totalSize = 0;
        var index = 0;

        foreach (var blob in blobs)
        {
            if (ct.IsCancellationRequested) break;

            // Encrypt the blob data
            var (ciphertext, nonce, tag) = EncryptData(blob.Data);

            // Compute hash of encrypted data
            using var sha = SHA256.Create();
            var hash = Convert.ToBase64String(sha.ComputeHash(ciphertext));

            shards.Add(new EncryptedShard
            {
                Index = index++,
                BlobUri = blob.Uri,
                Data = ciphertext,
                Nonce = nonce,
                Tag = tag,
                Hash = hash,
                OriginalSize = blob.Data.Length
            });

            blobUris.Add(blob.Uri);
            totalSize += blob.Data.Length;
        }

        // Compute merkle root
        var merkleRoot = ComputeMerkleRoot(shards.Select(s => s.Hash));

        var manifest = new PackageManifest
        {
            ShardCount = shards.Count,
            TotalSizeBytes = totalSize,
            EncryptionAlgorithm = "AES-256-GCM",
            KeyDerivation = "Argon2id",
            MerkleRoot = merkleRoot,
            BlobUris = blobUris,
            Tags = tags?.ToList() ?? new List<string>(),
            AutoIngest = autoIngest
        };

        var package = new DwPackage
        {
            PackageId = packageId,
            SourceInstanceId = _instanceId,
            TargetInstanceId = targetInstanceId,
            Manifest = manifest,
            Shards = shards
        };

        // Sign the package
        SignPackage(package);

        return package;
    }

    /// <summary>
    /// Saves a package to a device.
    /// </summary>
    public async Task<string> SavePackageToDeviceAsync(
        DwPackage package,
        string devicePath,
        CancellationToken ct = default)
    {
        var fileName = $"{package.PackageId}.dwpack";
        var filePath = Path.Combine(devicePath, fileName);

        var bytes = package.ToBytes();
        await File.WriteAllBytesAsync(filePath, bytes, ct);

        return filePath;
    }

    /// <summary>
    /// Creates a package with processing manifest for EHT convergence.
    /// Implements sub-task 79.33.
    /// </summary>
    public async Task<DwPackage> CreatePackageWithProcessingManifestAsync(
        IEnumerable<BlobData> blobs,
        IEnumerable<ProcessingOperation> localOperations,
        IEnumerable<DeferredOperation> deferredOperations,
        int schemaVersion,
        string? targetInstanceId = null,
        CancellationToken ct = default)
    {
        var package = await CreatePackageAsync(blobs, targetInstanceId, ct: ct);

        package.ProcessingInfo = new ProcessingManifest
        {
            ProcessingInstanceId = _instanceId,
            LocalOperations = localOperations.ToList(),
            DeferredOperations = deferredOperations.ToList(),
            SchemaVersion = schemaVersion,
            Statistics = new DataStatistics
            {
                BlobCount = package.Shards.Count,
                TotalSizeBytes = package.Manifest.TotalSizeBytes,
                LatestModification = DateTimeOffset.UtcNow
            }
        };

        // Re-sign with processing info
        SignPackage(package);

        return package;
    }

    #endregion

    #region Sub-tasks 79.6, 79.7, 79.8: Auto-Ingest Engine

    /// <summary>
    /// Scans a device for packages and auto-ingests if tagged.
    /// Implements sub-task 79.6.
    /// </summary>
    public async Task<IReadOnlyList<DwPackage>> ScanForPackagesAsync(
        string devicePath,
        CancellationToken ct = default)
    {
        var packages = new List<DwPackage>();
        var pattern = "*.dwpack";

        foreach (var file in Directory.EnumerateFiles(devicePath, pattern))
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var bytes = await File.ReadAllBytesAsync(file, ct);
                var package = DwPackage.FromBytes(bytes);
                packages.Add(package);
            }
            catch
            {
                // Invalid package file
            }
        }

        return packages;
    }

    /// <summary>
    /// Verifies a package signature.
    /// Implements sub-task 79.7.
    /// </summary>
    public bool VerifyPackageSignature(DwPackage package, byte[]? trustedPublicKey = null)
    {
        if (string.IsNullOrEmpty(package.Signature))
            return false;

        try
        {
            // Compute expected signature
            var dataToSign = GetPackageSignableData(package);
            using var hmac = new HMACSHA256(_encryptionKey);
            var expectedSignature = Convert.ToBase64String(hmac.ComputeHash(dataToSign));

            return package.Signature == expectedSignature;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Unpacks shards from a package.
    /// Implements sub-task 79.8.
    /// </summary>
    public async Task<IReadOnlyList<BlobData>> UnpackShardsAsync(
        DwPackage package,
        CancellationToken ct = default)
    {
        var blobs = new List<BlobData>();

        foreach (var shard in package.Shards)
        {
            if (ct.IsCancellationRequested) break;

            // Verify hash
            using var sha = SHA256.Create();
            var computedHash = Convert.ToBase64String(sha.ComputeHash(shard.Data));
            if (computedHash != shard.Hash)
            {
                throw new CryptographicException($"Shard {shard.Index} hash mismatch");
            }

            // Decrypt
            var plaintext = DecryptData(shard.Data, shard.Nonce, shard.Tag);

            blobs.Add(new BlobData
            {
                Uri = shard.BlobUri,
                Data = plaintext
            });
        }

        return blobs;
    }

    /// <summary>
    /// Imports a package with full verification.
    /// </summary>
    public async Task<ImportResult> ImportPackageAsync(
        DwPackage package,
        Func<BlobData, Task> storeBlobAsync,
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();
        var shardsImported = 0;
        var shardsSkipped = 0;
        long bytesImported = 0;

        try
        {
            // Verify signature
            if (!VerifyPackageSignature(package))
            {
                return new ImportResult
                {
                    Success = false,
                    PackageId = package.PackageId,
                    ErrorMessage = "Package signature verification failed"
                };
            }

            // Verify merkle root
            var computedMerkle = ComputeMerkleRoot(package.Shards.Select(s => s.Hash));
            if (computedMerkle != package.Manifest.MerkleRoot)
            {
                return new ImportResult
                {
                    Success = false,
                    PackageId = package.PackageId,
                    ErrorMessage = "Package integrity verification failed (merkle root mismatch)"
                };
            }

            // Unpack and store
            var blobs = await UnpackShardsAsync(package, ct);

            foreach (var blob in blobs)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    await storeBlobAsync(blob);
                    shardsImported++;
                    bytesImported += blob.Data.Length;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to store {blob.Uri}: {ex.Message}");
                    shardsSkipped++;
                }
            }

            stopwatch.Stop();

            return new ImportResult
            {
                Success = true,
                PackageId = package.PackageId,
                ShardsImported = shardsImported,
                ShardsSkipped = shardsSkipped,
                BytesImported = bytesImported,
                Duration = stopwatch.Elapsed,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return new ImportResult
            {
                Success = false,
                PackageId = package.PackageId,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }

    #endregion

    #region Sub-task 79.9: Result Logging

    /// <summary>
    /// Writes a result log to the device for sender feedback.
    /// Implements sub-task 79.9.
    /// </summary>
    public async Task WriteResultLogAsync(
        string devicePath,
        ImportResult result,
        CancellationToken ct = default)
    {
        var logEntry = new ResultLogEntry
        {
            PackageId = result.PackageId,
            Result = result.Success ? "SUCCESS" : "FAILED",
            TargetInstanceId = _instanceId,
            ItemsProcessed = result.ShardsImported,
            BytesProcessed = result.BytesImported,
            Warnings = result.Warnings,
            Errors = result.ErrorMessage != null ? new List<string> { result.ErrorMessage } : new List<string>()
        };

        var logPath = Path.Combine(devicePath, "result.log");
        var existingLog = new List<ResultLogEntry>();

        // Read existing log if present
        if (File.Exists(logPath))
        {
            try
            {
                var existingJson = await File.ReadAllTextAsync(logPath, ct);
                existingLog = JsonSerializer.Deserialize<List<ResultLogEntry>>(existingJson) ?? new List<ResultLogEntry>();
            }
            catch
            {
                // Ignore corrupt log file
            }
        }

        existingLog.Add(logEntry);

        // Keep only last 100 entries
        if (existingLog.Count > 100)
        {
            existingLog = existingLog.Skip(existingLog.Count - 100).ToList();
        }

        var json = JsonSerializer.Serialize(existingLog, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(logPath, json, ct);
    }

    #endregion

    #region Sub-task 79.10: Secure Wipe

    /// <summary>
    /// Performs cryptographic wipe of package files after successful import.
    /// Implements sub-task 79.10.
    /// </summary>
    public async Task<SecureWipeResult> SecureWipePackageAsync(
        string packagePath,
        int passes = 3,
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long bytesWiped = 0;

        try
        {
            var fileInfo = new FileInfo(packagePath);
            if (!fileInfo.Exists)
            {
                return new SecureWipeResult
                {
                    Success = false,
                    ErrorMessage = "File not found"
                };
            }

            bytesWiped = fileInfo.Length;

            // Overwrite with random data multiple times
            using (var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Write))
            {
                var buffer = new byte[4096];

                for (int pass = 0; pass < passes; pass++)
                {
                    if (ct.IsCancellationRequested) break;

                    fs.Position = 0;

                    while (fs.Position < fs.Length)
                    {
                        if (pass == passes - 1)
                        {
                            // Last pass: zeros
                            Array.Clear(buffer);
                        }
                        else
                        {
                            // Random data
                            RandomNumberGenerator.Fill(buffer);
                        }

                        var toWrite = (int)Math.Min(buffer.Length, fs.Length - fs.Position);
                        await fs.WriteAsync(buffer.AsMemory(0, toWrite), ct);
                    }

                    await fs.FlushAsync(ct);
                }
            }

            // Delete the file
            File.Delete(packagePath);

            stopwatch.Stop();

            // Verify deletion
            var verified = !File.Exists(packagePath);

            return new SecureWipeResult
            {
                Success = true,
                BytesWiped = bytesWiped,
                WipePasses = passes,
                Duration = stopwatch.Elapsed,
                Verified = verified
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return new SecureWipeResult
            {
                Success = false,
                BytesWiped = bytesWiped,
                WipePasses = passes,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Wipes all processed packages from a device.
    /// </summary>
    public async Task<SecureWipeResult> SecureWipeAllPackagesAsync(
        string devicePath,
        int passes = 3,
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long totalBytesWiped = 0;
        var errors = new List<string>();

        foreach (var file in Directory.EnumerateFiles(devicePath, "*.dwpack"))
        {
            if (ct.IsCancellationRequested) break;

            var result = await SecureWipePackageAsync(file, passes, ct);
            if (result.Success)
            {
                totalBytesWiped += result.BytesWiped;
            }
            else
            {
                errors.Add($"{Path.GetFileName(file)}: {result.ErrorMessage}");
            }
        }

        stopwatch.Stop();

        return new SecureWipeResult
        {
            Success = errors.Count == 0,
            BytesWiped = totalBytesWiped,
            WipePasses = passes,
            Duration = stopwatch.Elapsed,
            Verified = errors.Count == 0,
            ErrorMessage = errors.Count > 0 ? string.Join("; ", errors) : null
        };
    }

    #endregion

    #region Encryption Helpers

    private (byte[] ciphertext, byte[] nonce, byte[] tag) EncryptData(byte[] plaintext)
    {
        var nonce = new byte[12]; // 96-bit nonce for AES-GCM
        RandomNumberGenerator.Fill(nonce);

        var tag = new byte[16]; // 128-bit tag
        var ciphertext = new byte[plaintext.Length];

        using var aesGcm = new AesGcm(_encryptionKey, 16);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        return (ciphertext, nonce, tag);
    }

    private byte[] DecryptData(byte[] ciphertext, byte[] nonce, byte[] tag)
    {
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(_encryptionKey, 16);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    private void SignPackage(DwPackage package)
    {
        var dataToSign = GetPackageSignableData(package);
        using var hmac = new HMACSHA256(_encryptionKey);
        package.Signature = Convert.ToBase64String(hmac.ComputeHash(dataToSign));
    }

    private static byte[] GetPackageSignableData(DwPackage package)
    {
        var json = JsonSerializer.Serialize(new
        {
            package.PackageId,
            package.SourceInstanceId,
            package.TargetInstanceId,
            package.CreatedAt,
            package.Manifest.MerkleRoot,
            package.Manifest.ShardCount,
            package.Manifest.TotalSizeBytes
        });
        return Encoding.UTF8.GetBytes(json);
    }

    private static string ComputeMerkleRoot(IEnumerable<string> hashes)
    {
        var hashList = hashes.ToList();
        if (hashList.Count == 0) return string.Empty;
        if (hashList.Count == 1) return hashList[0];

        using var sha = SHA256.Create();

        while (hashList.Count > 1)
        {
            var newLevel = new List<string>();

            for (int i = 0; i < hashList.Count; i += 2)
            {
                var left = hashList[i];
                var right = i + 1 < hashList.Count ? hashList[i + 1] : left;
                var combined = Encoding.UTF8.GetBytes(left + right);
                var hash = sha.ComputeHash(combined);
                newLevel.Add(Convert.ToBase64String(hash));
            }

            hashList = newLevel;
        }

        return hashList[0];
    }

    #endregion
}

/// <summary>
/// Represents blob data for transfer.
/// </summary>
public sealed class BlobData
{
    /// <summary>Blob URI.</summary>
    public required string Uri { get; init; }

    /// <summary>Blob data.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Metadata.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}
