using DataWarehouse.SDK.Security;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.DevCiCd
{
    /// <summary>
    /// age encryption (FiloSottile/age) KeyStore strategy using the age CLI.
    /// Implements IKeyStoreStrategy using age for modern, simple file encryption.
    ///
    /// Features:
    /// - X25519 public key encryption (default)
    /// - Passphrase-based encryption (scrypt)
    /// - SSH key support (ssh-rsa, ssh-ed25519)
    /// - Multiple recipient encryption
    /// - Identity file management
    /// - Plugin support (age plugins like age-plugin-yubikey)
    ///
    /// Prerequisites:
    /// - age CLI must be installed (https://github.com/FiloSottile/age)
    /// - age-keygen for identity generation
    ///
    /// Configuration:
    /// - KeyStorePath: Directory for encrypted key files
    /// - AgePath: Path to age binary (default: "age")
    /// - AgeKeygenPath: Path to age-keygen binary (default: "age-keygen")
    /// - IdentityFile: Path to identity file for decryption
    /// - IdentityEnvVar: Environment variable containing identity (AGE_IDENTITY)
    /// - PassphraseEnvVar: Environment variable for passphrase-based encryption
    /// - Recipients: List of public keys/SSH keys for encryption
    /// - RecipientsFile: Path to file containing recipient public keys
    /// </summary>
    public sealed class AgeStrategy : KeyStoreStrategyBase
    {
        private AgeConfig _config = new();
        private readonly ConcurrentDictionary<string, byte[]> _keyCache = new();
        private string? _currentKeyId;
        private readonly SemaphoreSlim _processLock = new(1, 1);
        private string? _identityContent;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,  // Unless using age-plugin-yubikey
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = false,
            SupportsPerKeyAcl = true,  // Multiple recipients
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["StorageType"] = "AgeEncryption",
                ["Backend"] = "age CLI (FiloSottile)",
                ["Platform"] = "Cross-Platform",
                ["Algorithms"] = new[] { "X25519-ChaCha20-Poly1305", "scrypt" },
                ["KeyTypes"] = new[] { "X25519", "SSH-RSA", "SSH-ED25519" },
                ["IdealFor"] = "DevOps, SOPS integration, SSH-native workflows"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("age.shutdown");
            _keyCache.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("age.init");
            // Load configuration
            if (Configuration.TryGetValue("KeyStorePath", out var keyStorePathObj) && keyStorePathObj is string keyStorePath)
                _config.KeyStorePath = keyStorePath;
            if (Configuration.TryGetValue("AgePath", out var agePathObj) && agePathObj is string agePath)
                _config.AgePath = agePath;
            if (Configuration.TryGetValue("AgeKeygenPath", out var keygenPathObj) && keygenPathObj is string keygenPath)
                _config.AgeKeygenPath = keygenPath;
            if (Configuration.TryGetValue("IdentityFile", out var identityFileObj) && identityFileObj is string identityFile)
                _config.IdentityFile = identityFile;
            if (Configuration.TryGetValue("IdentityEnvVar", out var identityEnvObj) && identityEnvObj is string identityEnv)
                _config.IdentityEnvVar = identityEnv;
            if (Configuration.TryGetValue("PassphraseEnvVar", out var passphraseEnvObj) && passphraseEnvObj is string passphraseEnv)
                _config.PassphraseEnvVar = passphraseEnv;
            if (Configuration.TryGetValue("Recipients", out var recipientsObj) && recipientsObj is List<string> recipients)
                _config.Recipients = recipients;
            if (Configuration.TryGetValue("RecipientsFile", out var recipientsFileObj) && recipientsFileObj is string recipientsFile)
                _config.RecipientsFile = recipientsFile;
            if (Configuration.TryGetValue("AutoGenerateIdentity", out var autoGenObj) && autoGenObj is bool autoGen)
                _config.AutoGenerateIdentity = autoGen;

            // Verify age is installed
            await VerifyAgeInstallationAsync(cancellationToken);

            // Load identity from environment or file
            await LoadIdentityAsync(cancellationToken);

            // Ensure key store directory exists
            Directory.CreateDirectory(_config.KeyStorePath);

            // Auto-generate identity if configured and none exists
            if (_config.AutoGenerateIdentity && string.IsNullOrEmpty(_identityContent) && string.IsNullOrEmpty(_config.IdentityFile))
            {
                var generatedIdentity = await GenerateIdentityAsync(cancellationToken);
                _config.IdentityFile = generatedIdentity.IdentityFile;
                _identityContent = generatedIdentity.PrivateKey;
                _config.Recipients.Add(generatedIdentity.PublicKey);
            }

            _currentKeyId = "default";
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await ExecuteAgeAsync("--version", cancellationToken);
                if (result.ExitCode != 0)
                    return false;

                // Verify we have decryption capability
                return !string.IsNullOrEmpty(_identityContent) ||
                       !string.IsNullOrEmpty(_config.IdentityFile) ||
                       !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(_config.PassphraseEnvVar));
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            // Check cache first
            if (_keyCache.TryGetValue(keyId, out var cached))
                return cached;

            var encryptedFilePath = GetEncryptedFilePath(keyId);

            if (!File.Exists(encryptedFilePath))
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in age key store at '{encryptedFilePath}'.");
            }

            // Decrypt using age
            var decryptedData = await DecryptFileAsync(encryptedFilePath, context);

            // Parse the decrypted JSON
            var keyData = JsonSerializer.Deserialize<AgeKeyData>(decryptedData);
            if (keyData?.Key == null)
            {
                throw new InvalidOperationException($"Invalid key format for key '{keyId}'.");
            }

            var keyBytes = Convert.FromBase64String(keyData.Key);
            _keyCache[keyId] = keyBytes;

            return keyBytes;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var encryptedFilePath = GetEncryptedFilePath(keyId);

            // Ensure directory exists
            var directory = Path.GetDirectoryName(encryptedFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create key data object
            var keyDataObj = new AgeKeyData
            {
                Key = Convert.ToBase64String(keyData),
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = context.UserId,
                Algorithm = "AES-256"
            };

            var json = JsonSerializer.Serialize(keyDataObj, new JsonSerializerOptions { WriteIndented = true });

            // Encrypt and save
            await EncryptAndSaveAsync(encryptedFilePath, json, context);

            // Update cache
            _keyCache[keyId] = keyData;
            _currentKeyId = keyId;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!Directory.Exists(_config.KeyStorePath))
                return Array.Empty<string>();

            var keyFiles = Directory.GetFiles(_config.KeyStorePath, "*.age", SearchOption.AllDirectories);
            var keyIds = keyFiles
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToList()
                .AsReadOnly();

            return await Task.FromResult(keyIds);
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            var encryptedFilePath = GetEncryptedFilePath(keyId);
            if (File.Exists(encryptedFilePath))
            {
                // Secure delete: overwrite with random data before deleting
                var fileInfo = new FileInfo(encryptedFilePath);
                var randomData = RandomNumberGenerator.GetBytes((int)fileInfo.Length);
                await File.WriteAllBytesAsync(encryptedFilePath, randomData, cancellationToken);
                File.Delete(encryptedFilePath);
                _keyCache.TryRemove(keyId, out _);
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var encryptedFilePath = GetEncryptedFilePath(keyId);
            if (!File.Exists(encryptedFilePath))
                return null;

            var fileInfo = new FileInfo(encryptedFilePath);

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = fileInfo.CreationTimeUtc,
                LastRotatedAt = fileInfo.LastWriteTimeUtc,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["FilePath"] = encryptedFilePath,
                    ["FileSize"] = fileInfo.Length,
                    ["Backend"] = "age (FiloSottile)",
                    ["Encryption"] = "X25519-ChaCha20-Poly1305"
                }
            });
        }

        #region age Operations

        private async Task VerifyAgeInstallationAsync(CancellationToken cancellationToken)
        {
            var result = await ExecuteAgeAsync("--version", cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"age is not installed or not accessible at '{_config.AgePath}'. " +
                    "Please install age: https://github.com/FiloSottile/age");
            }
        }

        private async Task LoadIdentityAsync(CancellationToken cancellationToken)
        {
            // Try environment variable first
            var identityEnv = Environment.GetEnvironmentVariable(_config.IdentityEnvVar);
            if (!string.IsNullOrEmpty(identityEnv))
            {
                _identityContent = identityEnv;
                return;
            }

            // Try identity file
            if (!string.IsNullOrEmpty(_config.IdentityFile) && File.Exists(_config.IdentityFile))
            {
                _identityContent = await File.ReadAllTextAsync(_config.IdentityFile, cancellationToken);
            }
        }

        /// <summary>
        /// Generates a new age identity (key pair).
        /// </summary>
        public async Task<AgeIdentity> GenerateIdentityAsync(CancellationToken cancellationToken = default)
        {
            var result = await ExecuteAgeKeygenAsync("", cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to generate age identity: {result.Error}");
            }

            // age-keygen outputs both public key (as comment) and private key
            var output = result.Output ?? "";
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            string? publicKey = null;
            string? privateKey = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("# public key:"))
                {
                    publicKey = line.Substring("# public key:".Length).Trim();
                }
                else if (line.StartsWith("AGE-SECRET-KEY-"))
                {
                    privateKey = line.Trim();
                }
            }

            if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(privateKey))
            {
                throw new InvalidOperationException("Failed to parse age-keygen output.");
            }

            // Save identity file
            var identityPath = Path.Combine(_config.KeyStorePath, "identity.txt");
            await File.WriteAllTextAsync(identityPath, $"# public key: {publicKey}\n{privateKey}\n", cancellationToken);

            // Set restrictive permissions (Unix-style - best effort on Windows)
            try
            {
                var fileInfo = new FileInfo(identityPath);
                fileInfo.Attributes = FileAttributes.Hidden | FileAttributes.ReadOnly;
            }
            catch
            {
                // Ignore permission errors on Windows
            }

            return new AgeIdentity
            {
                PublicKey = publicKey,
                PrivateKey = privateKey,
                IdentityFile = identityPath
            };
        }

        private async Task<string> DecryptFileAsync(string encryptedFilePath, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            string arguments;
            string? input = null;

            // Check for passphrase-based decryption
            var passphrase = Environment.GetEnvironmentVariable(_config.PassphraseEnvVar);
            if (!string.IsNullOrEmpty(passphrase))
            {
                // Use passphrase decryption
                arguments = $"-d --passphrase \"{encryptedFilePath}\"";
                input = passphrase;
            }
            else if (!string.IsNullOrEmpty(_identityContent))
            {
                // Write identity to temp file for decryption
                var tempIdentityFile = Path.GetTempFileName();
                try
                {
                    await File.WriteAllTextAsync(tempIdentityFile, _identityContent, cancellationToken);
                    arguments = $"-d -i \"{tempIdentityFile}\" \"{encryptedFilePath}\"";

                    var result = await ExecuteAgeAsync(arguments, cancellationToken);
                    if (result.ExitCode != 0)
                    {
                        throw new CryptographicException($"Failed to decrypt key file: {result.Error}");
                    }
                    return result.Output ?? "";
                }
                finally
                {
                    SecureDeleteFile(tempIdentityFile);
                }
            }
            else if (!string.IsNullOrEmpty(_config.IdentityFile))
            {
                arguments = $"-d -i \"{_config.IdentityFile}\" \"{encryptedFilePath}\"";
            }
            else
            {
                throw new InvalidOperationException("No decryption identity or passphrase available.");
            }

            var decryptResult = await ExecuteAgeAsync(arguments, cancellationToken, input);
            if (decryptResult.ExitCode != 0)
            {
                throw new CryptographicException($"Failed to decrypt key file: {decryptResult.Error}");
            }

            return decryptResult.Output ?? "";
        }

        private async Task EncryptAndSaveAsync(string encryptedFilePath, string plaintext, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            var tempPlaintextFile = Path.GetTempFileName();
            var tempEncryptedFile = Path.GetTempFileName();

            try
            {
                await File.WriteAllTextAsync(tempPlaintextFile, plaintext, cancellationToken);

                // Build recipient arguments
                var recipientArgs = new StringBuilder();

                // Check for passphrase-based encryption
                var passphrase = Environment.GetEnvironmentVariable(_config.PassphraseEnvVar);
                if (!string.IsNullOrEmpty(passphrase))
                {
                    recipientArgs.Append("-p ");
                }
                else
                {
                    // Add individual recipients
                    foreach (var recipient in _config.Recipients)
                    {
                        if (recipient.StartsWith("age1"))
                        {
                            recipientArgs.Append($"-r {recipient} ");
                        }
                        else if (recipient.StartsWith("ssh-"))
                        {
                            recipientArgs.Append($"-R {recipient} ");
                        }
                    }

                    // Add recipients file if specified
                    if (!string.IsNullOrEmpty(_config.RecipientsFile) && File.Exists(_config.RecipientsFile))
                    {
                        recipientArgs.Append($"-R \"{_config.RecipientsFile}\" ");
                    }
                }

                if (recipientArgs.Length == 0)
                {
                    throw new InvalidOperationException("No recipients specified for encryption. Add recipients to configuration.");
                }

                var arguments = $"-e {recipientArgs}-o \"{tempEncryptedFile}\" \"{tempPlaintextFile}\"";

                var result = await ExecuteAgeAsync(arguments, cancellationToken, passphrase);
                if (result.ExitCode != 0)
                {
                    throw new CryptographicException($"Failed to encrypt key: {result.Error}");
                }

                // Move encrypted file to final location
                if (File.Exists(encryptedFilePath))
                {
                    File.Delete(encryptedFilePath);
                }
                File.Move(tempEncryptedFile, encryptedFilePath);
            }
            finally
            {
                SecureDeleteFile(tempPlaintextFile);
                if (File.Exists(tempEncryptedFile))
                {
                    SecureDeleteFile(tempEncryptedFile);
                }
            }
        }

        /// <summary>
        /// Adds a recipient public key for encryption.
        /// </summary>
        public void AddRecipient(string publicKey)
        {
            if (!_config.Recipients.Contains(publicKey))
            {
                _config.Recipients.Add(publicKey);
            }
        }

        /// <summary>
        /// Gets the public key from the current identity.
        /// </summary>
        public async Task<string?> GetPublicKeyAsync(CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrEmpty(_identityContent))
            {
                var tempIdentityFile = Path.GetTempFileName();
                try
                {
                    await File.WriteAllTextAsync(tempIdentityFile, _identityContent, cancellationToken);
                    var result = await ExecuteAgeKeygenAsync($"-y \"{tempIdentityFile}\"", cancellationToken);
                    return result.ExitCode == 0 ? result.Output?.Trim() : null;
                }
                finally
                {
                    SecureDeleteFile(tempIdentityFile);
                }
            }

            if (!string.IsNullOrEmpty(_config.IdentityFile) && File.Exists(_config.IdentityFile))
            {
                var result = await ExecuteAgeKeygenAsync($"-y \"{_config.IdentityFile}\"", cancellationToken);
                return result.ExitCode == 0 ? result.Output?.Trim() : null;
            }

            return null;
        }

        #endregion

        #region Process Execution

        private async Task<ProcessResult> ExecuteAgeAsync(string arguments, CancellationToken cancellationToken = default, string? stdin = null)
        {
            return await ExecuteProcessAsync(_config.AgePath, arguments, stdin, cancellationToken);
        }

        private async Task<ProcessResult> ExecuteAgeKeygenAsync(string arguments, CancellationToken cancellationToken = default)
        {
            return await ExecuteProcessAsync(_config.AgeKeygenPath, arguments, null, cancellationToken);
        }

        private async Task<ProcessResult> ExecuteProcessAsync(string program, string arguments, string? stdin, CancellationToken cancellationToken)
        {
            await _processLock.WaitAsync(cancellationToken);
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = program,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = stdin != null,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!string.IsNullOrEmpty(stdin))
                {
                    await process.StandardInput.WriteLineAsync(stdin);
                    process.StandardInput.Close();
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    throw;
                }

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    Output = outputBuilder.ToString().Trim(),
                    Error = errorBuilder.ToString().Trim()
                };
            }
            finally
            {
                _processLock.Release();
            }
        }

        private static void SecureDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    var randomData = RandomNumberGenerator.GetBytes((int)Math.Min(fileInfo.Length, 1024 * 1024));
                    File.WriteAllBytes(filePath, randomData);
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Best effort secure deletion
                try { File.Delete(filePath); } catch { /* Best-effort cleanup — failure is non-fatal */ }
            }
        }

        #endregion

        private string GetEncryptedFilePath(string keyId)
        {
            // Sanitize keyId to prevent path traversal
            var safeKeyId = Regex.Replace(keyId, @"[^a-zA-Z0-9_-]", "_");
            return Path.Combine(_config.KeyStorePath, $"{safeKeyId}.age");
        }

        public override void Dispose()
        {
            // Clear sensitive data
            _identityContent = null;
            _processLock.Dispose();
            base.Dispose();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Configuration for age encryption key store strategy.
    /// </summary>
    public class AgeConfig
    {
        /// <summary>
        /// Directory for storing encrypted key files.
        /// </summary>
        public string KeyStorePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "age-keystore");

        /// <summary>
        /// Path to the age binary.
        /// </summary>
        public string AgePath { get; set; } = "age";

        /// <summary>
        /// Path to the age-keygen binary.
        /// </summary>
        public string AgeKeygenPath { get; set; } = "age-keygen";

        /// <summary>
        /// Path to the identity file for decryption.
        /// </summary>
        public string? IdentityFile { get; set; }

        /// <summary>
        /// Environment variable containing the age identity (secret key).
        /// </summary>
        public string IdentityEnvVar { get; set; } = "AGE_IDENTITY";

        /// <summary>
        /// Environment variable for passphrase-based encryption/decryption.
        /// </summary>
        public string PassphraseEnvVar { get; set; } = "AGE_PASSPHRASE";

        /// <summary>
        /// List of recipient public keys for encryption.
        /// Supports: age1... (native), ssh-rsa, ssh-ed25519
        /// </summary>
        public List<string> Recipients { get; set; } = new();

        /// <summary>
        /// Path to a file containing recipient public keys (one per line).
        /// </summary>
        public string? RecipientsFile { get; set; }

        /// <summary>
        /// Automatically generate an identity if none exists.
        /// </summary>
        public bool AutoGenerateIdentity { get; set; } = false;
    }

    /// <summary>
    /// Key data structure stored in age-encrypted files.
    /// </summary>
    internal class AgeKeyData
    {
        public string Key { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string Algorithm { get; set; } = "AES-256";
    }

    /// <summary>
    /// Represents an age identity (key pair).
    /// </summary>
    public class AgeIdentity
    {
        /// <summary>
        /// The public key (age1...).
        /// </summary>
        public string PublicKey { get; set; } = string.Empty;

        /// <summary>
        /// The private key (AGE-SECRET-KEY-...).
        /// </summary>
        public string PrivateKey { get; set; } = string.Empty;

        /// <summary>
        /// Path to the identity file.
        /// </summary>
        public string IdentityFile { get; set; } = string.Empty;
    }

    #endregion
}
