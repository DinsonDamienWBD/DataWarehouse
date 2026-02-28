using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Offline;

/// <summary>
/// Air-Gap Transfer Strategy -- encrypted offline transfer via removable media.
/// Implements the "Mule" transport pattern: encrypted .dwpack containers for sneakernet transfer
/// with signature verification, shard-level integrity, and secure wipe capabilities.
/// </summary>
/// <remarks>
/// Merged from DataWarehouse.Plugins.AirGapBridge (Phase 65.5-12 consolidation).
/// Consolidates the Transport/PackageManager, Core/AirGapTypes, and Security subsystems
/// into the UltimateDataTransit offline strategies.
/// </remarks>
public sealed class AirGapTransferStrategy
{
    private readonly byte[] _encryptionKey;
    private readonly string _instanceId;

    /// <summary>
    /// Creates a new AirGapTransferStrategy.
    /// </summary>
    /// <param name="encryptionKey">Master encryption key (256-bit).</param>
    /// <param name="instanceId">Local instance identifier.</param>
    public AirGapTransferStrategy(byte[] encryptionKey, string instanceId)
    {
        if (encryptionKey.Length != 32)
            throw new ArgumentException("Encryption key must be 256 bits (32 bytes)", nameof(encryptionKey));

        _encryptionKey = encryptionKey;
        _instanceId = instanceId;
    }

    /// <summary>
    /// Creates a .dwpack package from blobs for air-gap transfer.
    /// </summary>
    public async Task<AirGapPackage> CreatePackageAsync(
        IEnumerable<AirGapBlobData> blobs,
        string? targetInstanceId = null,
        bool autoIngest = false,
        IEnumerable<string>? tags = null,
        CancellationToken ct = default)
    {
        var packageId = Guid.NewGuid().ToString("N");
        var shards = new List<AirGapEncryptedShard>();
        var blobUris = new List<string>();
        long totalSize = 0;
        var index = 0;

        foreach (var blob in blobs)
        {
            if (ct.IsCancellationRequested) break;

            var (ciphertext, nonce, tag) = EncryptData(blob.Data);

            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(ciphertext);
            var hash = Convert.ToBase64String(hashBytes);

            shards.Add(new AirGapEncryptedShard
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

        var merkleRoot = ComputeMerkleRoot(shards.Select(s => s.Hash));

        var manifest = new AirGapPackageManifest
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

        var package = new AirGapPackage
        {
            PackageId = packageId,
            SourceInstanceId = _instanceId,
            TargetInstanceId = targetInstanceId,
            Manifest = manifest,
            Shards = shards
        };

        SignPackage(package);

        return await Task.FromResult(package);
    }

    /// <summary>
    /// Verifies a package signature.
    /// </summary>
    public bool VerifyPackageSignature(AirGapPackage package)
    {
        if (string.IsNullOrEmpty(package.Signature))
            return false;

        try
        {
            var dataToSign = GetPackageSignableData(package);
            using var hmac = new HMACSHA256(_encryptionKey);
            var computedMac = hmac.ComputeHash(dataToSign);
            var expectedMac = Convert.FromBase64String(package.Signature);

            return CryptographicOperations.FixedTimeEquals(computedMac, expectedMac);
        }
        catch (FormatException)
        {
            // Invalid Base64 in signature
            return false;
        }
        catch (CryptographicException)
        {
            // Crypto operation failed
            return false;
        }
        // Let other exceptions (ArgumentNullException, etc.) propagate
    }

    /// <summary>
    /// Unpacks and decrypts shards from a package.
    /// </summary>
    public IReadOnlyList<AirGapBlobData> UnpackShards(AirGapPackage package)
    {
        var blobs = new List<AirGapBlobData>();

        foreach (var shard in package.Shards)
        {
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(shard.Data);
            var computedHash = Convert.ToBase64String(hashBytes);
            if (computedHash != shard.Hash)
            {
                throw new CryptographicException($"Shard {shard.Index} hash mismatch");
            }

            var plaintext = DecryptData(shard.Data, shard.Nonce, shard.Tag);

            blobs.Add(new AirGapBlobData
            {
                Uri = shard.BlobUri,
                Data = plaintext
            });
        }

        return blobs;
    }

    /// <summary>
    /// Performs cryptographic wipe of a package file.
    /// </summary>
    public async Task<AirGapSecureWipeResult> SecureWipePackageAsync(
        string packagePath, int passes = 3, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long bytesWiped = 0;

        try
        {
            var fileInfo = new FileInfo(packagePath);
            if (!fileInfo.Exists)
            {
                return new AirGapSecureWipeResult { Success = false, ErrorMessage = "File not found" };
            }

            bytesWiped = fileInfo.Length;

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
                            Array.Clear(buffer);
                        else
                            RandomNumberGenerator.Fill(buffer);

                        var toWrite = (int)Math.Min(buffer.Length, fs.Length - fs.Position);
                        await fs.WriteAsync(buffer.AsMemory(0, toWrite), ct);
                    }

                    await fs.FlushAsync(ct);
                }
            }

            File.Delete(packagePath);
            stopwatch.Stop();

            return new AirGapSecureWipeResult
            {
                Success = true,
                BytesWiped = bytesWiped,
                WipePasses = passes,
                Duration = stopwatch.Elapsed,
                Verified = !File.Exists(packagePath)
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new AirGapSecureWipeResult
            {
                Success = false,
                BytesWiped = bytesWiped,
                WipePasses = passes,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message
            };
        }
    }

    #region Encryption Helpers

    private (byte[] ciphertext, byte[] nonce, byte[] tag) EncryptData(byte[] plaintext)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var tag = new byte[16];
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

    private void SignPackage(AirGapPackage package)
    {
        var dataToSign = GetPackageSignableData(package);
        using var hmac = new HMACSHA256(_encryptionKey);
        var hmacBytes = hmac.ComputeHash(dataToSign);
        package.Signature = Convert.ToBase64String(hmacBytes);
    }

    private static byte[] GetPackageSignableData(AirGapPackage package)
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

#region Air-Gap Types

/// <summary>
/// Mode of operation for air-gap bridge devices.
/// </summary>
public enum AirGapMode
{
    /// <summary>Transport mode - encrypted blob container for sneakernet transfer.</summary>
    Transport,

    /// <summary>Storage extension mode - acts as a capacity tier.</summary>
    StorageExtension,

    /// <summary>Pocket instance mode - full portable DataWarehouse.</summary>
    PocketInstance
}

/// <summary>
/// Type of removable media detected.
/// </summary>
public enum AirGapMediaType
{
    Usb, SdCard, NvMe, Sata, NetworkDrive, Optical, Unknown
}

/// <summary>
/// Security level for air-gap authentication.
/// </summary>
public enum AirGapSecurityLevel
{
    None, Password, Keyfile, HardwareKey, MultiFactorAuth
}

/// <summary>
/// A DataWarehouse package (.dwpack) for encrypted air-gap transport.
/// </summary>
public sealed class AirGapPackage
{
    public int Version { get; init; } = 1;
    public required string PackageId { get; init; }
    public required string SourceInstanceId { get; init; }
    public string? TargetInstanceId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required AirGapPackageManifest Manifest { get; init; }
    public List<AirGapEncryptedShard> Shards { get; init; } = new();
    public string? Signature { get; set; }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = false });

    public static AirGapPackage FromBytes(byte[] data) =>
        JsonSerializer.Deserialize<AirGapPackage>(data)
            ?? throw new InvalidOperationException("Failed to deserialize AirGapPackage");
}

/// <summary>
/// Manifest for an AirGapPackage.
/// </summary>
public sealed class AirGapPackageManifest
{
    public int ShardCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public string EncryptionAlgorithm { get; init; } = "AES-256-GCM";
    public string KeyDerivation { get; init; } = "Argon2id";
    public string? MerkleRoot { get; init; }
    public List<string> BlobUris { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    public bool AutoIngest { get; init; }
}

/// <summary>
/// An encrypted shard within a package.
/// </summary>
public sealed class AirGapEncryptedShard
{
    public int Index { get; init; }
    public required string BlobUri { get; init; }
    public required byte[] Data { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] Tag { get; init; }
    public required string Hash { get; init; }
    public long OriginalSize { get; init; }
}

/// <summary>
/// Represents blob data for air-gap transfer.
/// </summary>
public sealed class AirGapBlobData
{
    public required string Uri { get; init; }
    public required byte[] Data { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Result of a secure wipe operation.
/// </summary>
public sealed class AirGapSecureWipeResult
{
    public bool Success { get; init; }
    public long BytesWiped { get; init; }
    public int WipePasses { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Verified { get; init; }
    public string? ErrorMessage { get; init; }
}

#endregion
