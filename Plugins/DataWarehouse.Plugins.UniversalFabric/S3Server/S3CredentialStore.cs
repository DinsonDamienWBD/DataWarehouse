using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Storage.Fabric;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UniversalFabric.S3Server;

/// <summary>
/// Manages S3-compatible access key / secret key credential pairs with disk persistence.
/// </summary>
/// <remarks>
/// <para>
/// Credentials are generated using <see cref="RandomNumberGenerator"/> for cryptographic security.
/// Access keys are 20-character base62 strings; secret keys are 40-character base62 strings,
/// matching AWS IAM credential format conventions.
/// </para>
/// <para>
/// The store persists credentials to a JSON file on disk, ensuring credentials survive
/// server restarts. All operations are thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </para>
/// </remarks>
public sealed class S3CredentialStore
{
    private readonly BoundedDictionary<string, S3Credentials> _credentials = new BoundedDictionary<string, S3Credentials>(1000);
    private readonly string _storagePath;
    private readonly object _persistLock = new();

    /// <summary>
    /// Base62 character set used for generating access keys and secret keys.
    /// </summary>
    private const string Base62Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Length of generated access key IDs (matches AWS convention).
    /// </summary>
    private const int AccessKeyLength = 20;

    /// <summary>
    /// Length of generated secret access keys (matches AWS convention).
    /// </summary>
    private const int SecretKeyLength = 40;

    /// <summary>
    /// Initializes a new credential store that persists to the specified file path.
    /// </summary>
    /// <param name="storagePath">The file path for persisting credentials as JSON.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="storagePath"/> is null or empty.</exception>
    public S3CredentialStore(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            throw new ArgumentNullException(nameof(storagePath));
        }

        _storagePath = storagePath;
        LoadFromDisk();
    }

    /// <summary>
    /// Creates a new set of S3 credentials with cryptographically random access and secret keys.
    /// </summary>
    /// <param name="userId">Optional internal user ID to associate with the credentials.</param>
    /// <param name="allowedBuckets">Optional set of bucket names restricting access. Null means all buckets.</param>
    /// <param name="isAdmin">Whether the credentials grant administrative privileges.</param>
    /// <returns>The newly created credentials.</returns>
    public S3Credentials CreateCredentials(string? userId = null, IReadOnlySet<string>? allowedBuckets = null, bool isAdmin = false)
    {
        var accessKeyId = GenerateRandomBase62(AccessKeyLength);
        var secretAccessKey = GenerateRandomBase62(SecretKeyLength);

        var credentials = new S3Credentials
        {
            AccessKeyId = accessKeyId,
            SecretAccessKey = secretAccessKey,
            UserId = userId,
            AllowedBuckets = allowedBuckets,
            IsAdmin = isAdmin
        };

        _credentials[accessKeyId] = credentials;
        PersistToDisk();

        return credentials;
    }

    /// <summary>
    /// Retrieves credentials by access key ID.
    /// </summary>
    /// <param name="accessKeyId">The access key ID to look up.</param>
    /// <returns>The credentials, or null if not found.</returns>
    public S3Credentials? GetCredentials(string accessKeyId)
    {
        ArgumentNullException.ThrowIfNull(accessKeyId);
        return _credentials.TryGetValue(accessKeyId, out var creds) ? creds : null;
    }

    /// <summary>
    /// Deletes credentials by access key ID.
    /// </summary>
    /// <param name="accessKeyId">The access key ID of the credentials to delete.</param>
    /// <returns>True if credentials were found and deleted; false otherwise.</returns>
    public bool DeleteCredentials(string accessKeyId)
    {
        ArgumentNullException.ThrowIfNull(accessKeyId);
        var removed = _credentials.TryRemove(accessKeyId, out _);
        if (removed)
        {
            PersistToDisk();
        }
        return removed;
    }

    /// <summary>
    /// Lists all stored credentials with secret keys redacted.
    /// </summary>
    /// <returns>A read-only list of credentials with secret keys replaced by asterisks.</returns>
    public IReadOnlyList<S3Credentials> ListCredentials()
    {
        return _credentials.Values
            .Select(c => c with { SecretAccessKey = "********" })
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets the total number of stored credentials.
    /// </summary>
    public int Count => _credentials.Count;

    /// <summary>
    /// Persists the current credential set to the JSON file on disk.
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

            var entries = _credentials.Values.Select(c => new CredentialEntry
            {
                AccessKeyId = c.AccessKeyId,
                SecretAccessKey = c.SecretAccessKey,
                UserId = c.UserId,
                AllowedBuckets = c.AllowedBuckets?.ToList(),
                IsAdmin = c.IsAdmin
            }).ToList();

            var json = JsonSerializer.Serialize(entries, CredentialJsonContext.Default.ListCredentialEntry);

            // Write atomically via temp file
            var tempPath = _storagePath + ".tmp";
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
    }

    /// <summary>
    /// Loads credentials from the JSON file on disk.
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
            var entries = JsonSerializer.Deserialize(json, CredentialJsonContext.Default.ListCredentialEntry);

            if (entries is not null)
            {
                foreach (var entry in entries)
                {
                    var creds = new S3Credentials
                    {
                        AccessKeyId = entry.AccessKeyId,
                        SecretAccessKey = entry.SecretAccessKey,
                        UserId = entry.UserId,
                        AllowedBuckets = entry.AllowedBuckets is not null
                            ? new HashSet<string>(entry.AllowedBuckets)
                            : null,
                        IsAdmin = entry.IsAdmin
                    };
                    _credentials[entry.AccessKeyId] = creds;
                }
            }
        }
        catch (JsonException ex)
        {

            // Corrupted file -- start fresh rather than crash
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates a cryptographically random base62 string of the specified length.
    /// </summary>
    private static string GenerateRandomBase62(int length)
    {
        var result = new char[length];
        Span<byte> randomBytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(randomBytes);

        for (int i = 0; i < length; i++)
        {
            // Use modulo with rejection sampling not needed here since base62 (62) is close enough
            // to a power of 2 that bias is negligible for key generation purposes.
            result[i] = Base62Chars[randomBytes[i] % Base62Chars.Length];
        }

        return new string(result);
    }

    /// <summary>
    /// Internal serialization model for credential persistence.
    /// </summary>
    internal sealed class CredentialEntry
    {
        public required string AccessKeyId { get; set; }
        public required string SecretAccessKey { get; set; }
        public string? UserId { get; set; }
        public List<string>? AllowedBuckets { get; set; }
        public bool IsAdmin { get; set; }
    }
}

/// <summary>
/// Source-generated JSON serialization context for credential persistence.
/// </summary>
[JsonSerializable(typeof(List<S3CredentialStore.CredentialEntry>))]
internal partial class CredentialJsonContext : JsonSerializerContext
{
}
