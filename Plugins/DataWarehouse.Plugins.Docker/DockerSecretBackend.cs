using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.Docker
{
    /// <summary>
    /// Docker Secret Backend implementation for storing Docker secrets in DataWarehouse.
    /// Provides secure storage with encryption at rest for Docker Swarm secrets.
    ///
    /// Features:
    /// - AES-256-GCM encryption at rest
    /// - Secure key derivation using PBKDF2
    /// - Atomic secret updates
    /// - Secret versioning support
    /// - Access auditing
    /// - Secret rotation support
    /// - Label-based organization
    /// - Size limits and validation
    /// </summary>
    internal sealed class DockerSecretBackend : IDisposable
    {
        private readonly DockerPluginConfig _config;
        private readonly ConcurrentDictionary<string, SecretMetadata> _secretIndex = new();
        private readonly SemaphoreSlim _secretLock = new(1, 1);
        private readonly string _metadataPath;
        private readonly byte[] _masterKey;
        private bool _disposed;

        private const int MaxSecretSize = 500 * 1024; // 500KB max per Docker spec
        private const int KeySize = 32; // 256 bits
        private const int NonceSize = 12; // 96 bits for GCM
        private const int TagSize = 16; // 128 bits GCM tag
        private const int SaltSize = 32; // 256 bits
        private const int Pbkdf2Iterations = 100000;

        /// <summary>
        /// Initializes the Docker Secret Backend.
        /// </summary>
        /// <param name="config">Plugin configuration.</param>
        public DockerSecretBackend(DockerPluginConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _metadataPath = Path.Combine(_config.SecretBasePath, ".metadata");

            // Derive master key from configuration or generate new one
            _masterKey = DeriveOrGenerateMasterKey();
        }

        /// <summary>
        /// Initializes the secret backend, loading existing secret metadata.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task InitializeAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(_config.SecretBasePath);
            Directory.CreateDirectory(_metadataPath);

            // Set restrictive permissions on secret directory (Unix-like systems)
            SetSecureDirectoryPermissions(_config.SecretBasePath);

            await LoadSecretIndexAsync(ct);
        }

        /// <summary>
        /// Gracefully shuts down the secret backend.
        /// </summary>
        public async Task ShutdownAsync()
        {
            await PersistSecretIndexAsync();
        }

        /// <summary>
        /// Stores a secret with encryption.
        /// </summary>
        /// <param name="secretId">Unique identifier for the secret.</param>
        /// <param name="data">Secret data to store.</param>
        /// <param name="labels">Optional labels for organization.</param>
        /// <exception cref="ArgumentException">Thrown when secret ID or data is invalid.</exception>
        /// <exception cref="InvalidOperationException">Thrown when secret exceeds size limit.</exception>
        public async Task StoreSecretAsync(
            string secretId,
            byte[] data,
            Dictionary<string, string>? labels = null)
        {
            ValidateSecretId(secretId);
            ValidateSecretData(data);

            await _secretLock.WaitAsync();
            try
            {
                var secretPath = GetSecretPath(secretId);
                var now = DateTime.UtcNow;

                // Check if this is an update
                var isUpdate = _secretIndex.ContainsKey(secretId);
                var version = isUpdate
                    ? _secretIndex[secretId].Version + 1
                    : 1;

                // Encrypt the secret
                var encryptedData = EncryptSecret(data);

                // Atomic write
                var tempPath = secretPath + ".tmp";
                await File.WriteAllBytesAsync(tempPath, encryptedData);
                File.Move(tempPath, secretPath, overwrite: true);

                // Update metadata
                var metadata = new SecretMetadata
                {
                    SecretId = secretId,
                    CreatedAt = isUpdate ? _secretIndex[secretId].CreatedAt : now,
                    UpdatedAt = now,
                    Version = version,
                    Size = data.Length,
                    EncryptedSize = encryptedData.Length,
                    Labels = labels ?? new Dictionary<string, string>(),
                    Hash = ComputeSecretHash(data)
                };

                _secretIndex[secretId] = metadata;
                await PersistSecretMetadataAsync(metadata);

                // Audit log
                LogSecretAccess(secretId, isUpdate ? "updated" : "created");
            }
            finally
            {
                _secretLock.Release();
            }
        }

        /// <summary>
        /// Retrieves a secret by ID.
        /// </summary>
        /// <param name="secretId">Secret identifier.</param>
        /// <returns>Decrypted secret data, or null if not found.</returns>
        public async Task<byte[]?> GetSecretAsync(string secretId)
        {
            ValidateSecretId(secretId);

            if (!_secretIndex.ContainsKey(secretId))
            {
                return null;
            }

            var secretPath = GetSecretPath(secretId);
            if (!File.Exists(secretPath))
            {
                // Index out of sync, remove stale entry
                _secretIndex.TryRemove(secretId, out _);
                return null;
            }

            var encryptedData = await File.ReadAllBytesAsync(secretPath);
            var decryptedData = DecryptSecret(encryptedData);

            // Update access time
            if (_secretIndex.TryGetValue(secretId, out var metadata))
            {
                metadata.LastAccessedAt = DateTime.UtcNow;
                metadata.AccessCount++;
            }

            // Audit log
            LogSecretAccess(secretId, "accessed");

            return decryptedData;
        }

        /// <summary>
        /// Removes a secret by ID.
        /// </summary>
        /// <param name="secretId">Secret identifier to remove.</param>
        /// <returns>True if removed, false if not found.</returns>
        public async Task<bool> RemoveSecretAsync(string secretId)
        {
            ValidateSecretId(secretId);

            await _secretLock.WaitAsync();
            try
            {
                if (!_secretIndex.TryRemove(secretId, out _))
                {
                    return false;
                }

                var secretPath = GetSecretPath(secretId);
                if (File.Exists(secretPath))
                {
                    // Secure delete: overwrite with zeros before deletion
                    await SecureDeleteFileAsync(secretPath);
                }

                // Remove metadata file
                var metadataFile = GetMetadataFilePath(secretId);
                if (File.Exists(metadataFile))
                {
                    File.Delete(metadataFile);
                }

                // Audit log
                LogSecretAccess(secretId, "deleted");

                return true;
            }
            finally
            {
                _secretLock.Release();
            }
        }

        /// <summary>
        /// Lists all secrets with their metadata.
        /// </summary>
        /// <returns>Collection of secret metadata (without actual secret data).</returns>
        public Task<IReadOnlyList<SecretMetadata>> ListSecretsAsync()
        {
            return Task.FromResult<IReadOnlyList<SecretMetadata>>(_secretIndex.Values.ToList());
        }

        /// <summary>
        /// Gets the health status of the secret backend.
        /// </summary>
        /// <returns>Health status string.</returns>
        public string GetHealthStatus()
        {
            try
            {
                if (!Directory.Exists(_config.SecretBasePath))
                {
                    return "unhealthy: secret base path does not exist";
                }

                // Check write access
                var testFile = Path.Combine(_config.SecretBasePath, ".health_check");
                var testData = Encoding.UTF8.GetBytes("health_check");
                var encrypted = EncryptSecret(testData);
                File.WriteAllBytes(testFile, encrypted);
                var decrypted = DecryptSecret(File.ReadAllBytes(testFile));
                File.Delete(testFile);

                if (!testData.SequenceEqual(decrypted))
                {
                    return "unhealthy: encryption/decryption verification failed";
                }

                return $"healthy: {_secretIndex.Count} secrets stored";
            }
            catch (Exception ex)
            {
                return $"unhealthy: {ex.Message}";
            }
        }

        /// <summary>
        /// Disposes resources used by the secret backend.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                // Securely clear master key from memory
                CryptographicOperations.ZeroMemory(_masterKey);
                _secretLock.Dispose();
                _disposed = true;
            }
        }

        #region Private Methods

        private string GetSecretPath(string secretId)
        {
            var safeId = SanitizeSecretId(secretId);
            return Path.Combine(_config.SecretBasePath, $"{safeId}.secret");
        }

        private string GetMetadataFilePath(string secretId)
        {
            var safeId = SanitizeSecretId(secretId);
            return Path.Combine(_metadataPath, $"{safeId}.json");
        }

        private static string SanitizeSecretId(string secretId)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new StringBuilder(secretId.Length);
            foreach (var c in secretId)
            {
                sanitized.Append(invalid.Contains(c) ? '_' : c);
            }
            return sanitized.ToString();
        }

        private static void ValidateSecretId(string secretId)
        {
            if (string.IsNullOrWhiteSpace(secretId))
            {
                throw new ArgumentException("Secret ID cannot be empty", nameof(secretId));
            }

            if (secretId.Length > 255)
            {
                throw new ArgumentException("Secret ID too long (max 255 characters)", nameof(secretId));
            }

            // Check for path traversal attempts
            if (secretId.Contains("..") || secretId.Contains('/') || secretId.Contains('\\'))
            {
                throw new ArgumentException("Secret ID contains invalid characters", nameof(secretId));
            }
        }

        private static void ValidateSecretData(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new ArgumentException("Secret data cannot be empty", nameof(data));
            }

            if (data.Length > MaxSecretSize)
            {
                throw new InvalidOperationException($"Secret exceeds maximum size of {MaxSecretSize} bytes");
            }
        }

        private byte[] DeriveOrGenerateMasterKey()
        {
            var keyFile = Path.Combine(_config.SecretBasePath, ".master.key");

            if (File.Exists(keyFile))
            {
                // Load existing key material and derive key
                var keyMaterial = File.ReadAllBytes(keyFile);
                if (keyMaterial.Length >= SaltSize)
                {
                    var salt = keyMaterial[..SaltSize];
                    var passphrase = _config.SecretEncryptionKey ?? GetMachineIdentifier();
                    return DeriveKey(passphrase, salt);
                }
            }

            // Generate new key material
            var newSalt = RandomNumberGenerator.GetBytes(SaltSize);

            // Ensure directory exists with secure permissions
            Directory.CreateDirectory(_config.SecretBasePath);
            SetSecureDirectoryPermissions(_config.SecretBasePath);

            // Store salt securely
            File.WriteAllBytes(keyFile, newSalt);
            SetSecureFilePermissions(keyFile);

            var newPassphrase = _config.SecretEncryptionKey ?? GetMachineIdentifier();
            return DeriveKey(newPassphrase, newSalt);
        }

        private static byte[] DeriveKey(string passphrase, byte[] salt)
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passphrase,
                salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                KeySize);
        }

        private static string GetMachineIdentifier()
        {
            // Use machine-specific identifier as fallback passphrase
            var machineName = Environment.MachineName;
            var userName = Environment.UserName;
            return $"dw-docker-{machineName}-{userName}";
        }

        private byte[] EncryptSecret(byte[] plaintext)
        {
            // Generate random nonce
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);

            // Prepare output buffer: nonce + ciphertext + tag
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(_masterKey, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // Combine: nonce || ciphertext || tag
            var result = new byte[NonceSize + ciphertext.Length + TagSize];
            Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
            Buffer.BlockCopy(ciphertext, 0, result, NonceSize, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, NonceSize + ciphertext.Length, TagSize);

            return result;
        }

        private byte[] DecryptSecret(byte[] encrypted)
        {
            if (encrypted.Length < NonceSize + TagSize)
            {
                throw new CryptographicException("Invalid encrypted data format");
            }

            // Extract components
            var nonce = encrypted[..NonceSize];
            var ciphertext = encrypted[NonceSize..^TagSize];
            var tag = encrypted[^TagSize..];

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(_masterKey, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }

        private static string ComputeSecretHash(byte[] data)
        {
            var hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private async Task LoadSecretIndexAsync(CancellationToken ct)
        {
            if (!Directory.Exists(_metadataPath))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(_metadataPath, "*.json"))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file, ct);
                    var metadata = JsonSerializer.Deserialize<SecretMetadata>(json);
                    if (metadata != null && !string.IsNullOrEmpty(metadata.SecretId))
                    {
                        // Verify secret file exists
                        var secretPath = GetSecretPath(metadata.SecretId);
                        if (File.Exists(secretPath))
                        {
                            _secretIndex[metadata.SecretId] = metadata;
                        }
                        else
                        {
                            // Clean up orphaned metadata
                            File.Delete(file);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed metadata files
                }
            }
        }

        private async Task PersistSecretMetadataAsync(SecretMetadata metadata)
        {
            var metadataFile = GetMetadataFilePath(metadata.SecretId);
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });

            // Atomic write
            var tempFile = metadataFile + ".tmp";
            await File.WriteAllTextAsync(tempFile, json);
            File.Move(tempFile, metadataFile, overwrite: true);
        }

        private async Task PersistSecretIndexAsync()
        {
            foreach (var metadata in _secretIndex.Values)
            {
                await PersistSecretMetadataAsync(metadata);
            }
        }

        private static async Task SecureDeleteFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var size = fileInfo.Length;

            // Overwrite with zeros
            var zeros = new byte[Math.Min(size, 4096)];
            await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write))
            {
                var remaining = size;
                while (remaining > 0)
                {
                    var toWrite = (int)Math.Min(remaining, zeros.Length);
                    await fs.WriteAsync(zeros.AsMemory(0, toWrite));
                    remaining -= toWrite;
                }
            }

            // Delete the file
            File.Delete(filePath);
        }

        private static void SetSecureDirectoryPermissions(string path)
        {
            // Set restrictive permissions (owner-only on Unix-like systems)
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    // chmod 700
                    File.SetUnixFileMode(path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch
                {
                    // Permissions may not be supported on all filesystems
                }
            }
        }

        private static void SetSecureFilePermissions(string path)
        {
            // Set restrictive permissions (owner-only on Unix-like systems)
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    // chmod 600
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // Permissions may not be supported on all filesystems
                }
            }
        }

        private void LogSecretAccess(string secretId, string action)
        {
            // Audit logging - in production, this would integrate with proper audit system
            var logEntry = new
            {
                Timestamp = DateTime.UtcNow,
                SecretId = secretId,
                Action = action,
                User = Environment.UserName,
                Machine = Environment.MachineName
            };

            var auditPath = Path.Combine(_config.SecretBasePath, "audit.log");
            var logLine = JsonSerializer.Serialize(logEntry) + Environment.NewLine;

            lock (_secretLock)
            {
                File.AppendAllText(auditPath, logLine);
            }
        }

        #endregion
    }

    /// <summary>
    /// Metadata for a stored secret.
    /// </summary>
    public sealed class SecretMetadata
    {
        /// <summary>
        /// Unique identifier for the secret.
        /// </summary>
        public string SecretId { get; init; } = string.Empty;

        /// <summary>
        /// When the secret was first created.
        /// </summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>
        /// When the secret was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; init; }

        /// <summary>
        /// When the secret was last accessed.
        /// </summary>
        public DateTime? LastAccessedAt { get; set; }

        /// <summary>
        /// Version number (incremented on updates).
        /// </summary>
        public int Version { get; init; }

        /// <summary>
        /// Original size of the secret data in bytes.
        /// </summary>
        public int Size { get; init; }

        /// <summary>
        /// Size of the encrypted secret data in bytes.
        /// </summary>
        public int EncryptedSize { get; init; }

        /// <summary>
        /// SHA-256 hash of the secret data for integrity verification.
        /// </summary>
        public string Hash { get; init; } = string.Empty;

        /// <summary>
        /// User-defined labels for organization.
        /// </summary>
        public Dictionary<string, string> Labels { get; init; } = new();

        /// <summary>
        /// Number of times this secret has been accessed.
        /// </summary>
        public long AccessCount { get; set; }
    }
}
