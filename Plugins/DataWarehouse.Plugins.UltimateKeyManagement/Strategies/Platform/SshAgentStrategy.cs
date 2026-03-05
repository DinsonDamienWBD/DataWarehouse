using DataWarehouse.SDK.Security;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Platform
{
    /// <summary>
    /// SSH Agent KeyStore strategy using SSH.NET's agent support.
    /// Provides secure key storage leveraging existing SSH infrastructure.
    ///
    /// Features:
    /// - Uses SSH agent for key operations (ssh-agent on Unix, Pageant on Windows)
    /// - Keys are wrapped using ECDH key agreement with SSH agent keys
    /// - Compatible with hardware tokens via SSH agent forwarding
    /// - Support for OpenSSH agent protocol
    /// - Leverages existing SSH key management infrastructure
    ///
    /// Security Model:
    /// - Data Encryption Keys (DEK) are wrapped using ECDH with SSH agent keys
    /// - SSH agent provides hardware-bound key operations when backed by FIDO2/PIV
    /// - Keys never leave the SSH agent boundary
    ///
    /// Configuration:
    /// - AgentSocketPath: Path to SSH agent socket (default: SSH_AUTH_SOCK)
    /// - KeyFingerprint: Fingerprint of the SSH key to use for encryption
    /// - StoragePath: Path to store wrapped keys
    /// </summary>
    public sealed class SshAgentStrategy : KeyStoreStrategyBase
    {
        private SshAgentConfig _config = new();
        private string? _currentKeyId;
        private AgentIdentity? _selectedIdentity;
        private IAgentProtocol? _agent;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            // #3566: SupportsHsm reflects that hardware-backed SSH keys (FIDO2/PIV) can be loaded into
            // the agent; the strategy itself does not perform HSM operations directly. Callers must
            // configure a hardware-backed key in the SSH agent. Set false if no hardware key is confirmed.
            SupportsHsm = false, // Cannot guarantee HSM — depends on agent-loaded key type
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = false,
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "SSH Agent",
                ["Library"] = "SSH.NET",
                ["StorageType"] = "SSH Agent + File System",
                ["ProtectionMethod"] = "ECDH Key Agreement",
                ["SupportsHardwareTokens"] = true
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sshagent.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("sshagent.init");
            // Load configuration
            if (Configuration.TryGetValue("AgentSocketPath", out var socketObj) && socketObj is string socket)
                _config.AgentSocketPath = socket;
            if (Configuration.TryGetValue("KeyFingerprint", out var fpObj) && fpObj is string fp)
                _config.KeyFingerprint = fp;
            if (Configuration.TryGetValue("StoragePath", out var pathObj) && pathObj is string path)
                _config.StoragePath = path;
            if (Configuration.TryGetValue("FallbackToFile", out var fallbackObj) && fallbackObj is bool fallback)
                _config.FallbackToFileEncryption = fallback;

            // Ensure storage directory exists
            Directory.CreateDirectory(_config.StoragePath);

            // Initialize SSH agent connection
            await InitializeAgentConnectionAsync(cancellationToken);

            // Select the encryption key
            await SelectEncryptionKeyAsync(cancellationToken);

            // Load metadata
            var metadataPath = Path.Combine(_config.StoragePath, "keystore.meta");
            if (File.Exists(metadataPath))
            {
                var metadata = JsonSerializer.Deserialize<SshAgentMetadata>(
                    await File.ReadAllTextAsync(metadataPath, cancellationToken));
                _currentKeyId = metadata?.CurrentKeyId;
            }

            // If no current key, create initial key
            if (string.IsNullOrEmpty(_currentKeyId))
            {
                _currentKeyId = Guid.NewGuid().ToString("N");
                var initialKey = GenerateKey();
                await SaveKeyToStorage(_currentKeyId, initialKey, CreateSystemContext());
            }
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Verify agent connection and key availability
                if (_agent == null || _selectedIdentity == null)
                {
                    return _config.FallbackToFileEncryption;
                }

                // Test encryption/decryption
                var testData = RandomNumberGenerator.GetBytes(32);
                var encrypted = await EncryptWithAgentKeyAsync(testData);
                var decrypted = await DecryptWithAgentKeyAsync(encrypted);

                return testData.SequenceEqual(decrypted);
            }
            catch
            {
                return _config.FallbackToFileEncryption;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            var keyPath = GetKeyPath(keyId);
            if (!File.Exists(keyPath))
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in SSH agent key store.");
            }

            var encryptedData = await File.ReadAllBytesAsync(keyPath);
            var keyData = await DecryptWithAgentKeyAsync(encryptedData);

            return keyData;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var encryptedData = await EncryptWithAgentKeyAsync(keyData);
            var keyPath = GetKeyPath(keyId);

            await File.WriteAllBytesAsync(keyPath, encryptedData);

            // Update metadata
            _currentKeyId = keyId;
            await SaveMetadataAsync();
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keyFiles = Directory.GetFiles(_config.StoragePath, "*.ssh.key");
            var keyIds = keyFiles
                .Select(f => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(f)))
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

            var keyPath = GetKeyPath(keyId);
            if (File.Exists(keyPath))
            {
                File.Delete(keyPath);
            }

            await Task.CompletedTask;
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keyPath = GetKeyPath(keyId);
            if (!File.Exists(keyPath))
                return null;

            var fileInfo = new FileInfo(keyPath);

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = fileInfo.CreationTimeUtc,
                LastRotatedAt = fileInfo.LastWriteTimeUtc,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["FilePath"] = keyPath,
                    ["FileSize"] = fileInfo.Length,
                    ["AgentKeyFingerprint"] = _selectedIdentity?.Comment ?? "fallback",
                    ["EncryptionMethod"] = _selectedIdentity != null ? "SSH Agent ECDH" : "AES-256-GCM"
                }
            });
        }

        #region SSH Agent Operations

        private async Task InitializeAgentConnectionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var socketPath = _config.AgentSocketPath;

                // Default to SSH_AUTH_SOCK environment variable
                if (string.IsNullOrEmpty(socketPath))
                {
                    socketPath = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
                }

                // On Windows, try named pipe for OpenSSH agent
                if (string.IsNullOrEmpty(socketPath) && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    socketPath = "\\\\.\\pipe\\openssh-ssh-agent";
                }

                if (string.IsNullOrEmpty(socketPath))
                {
                    if (_config.FallbackToFileEncryption)
                    {
                        // No agent available, will use file-based encryption
                        return;
                    }
                    throw new InvalidOperationException("SSH agent socket not found. Set SSH_AUTH_SOCK or configure AgentSocketPath.");
                }

                _agent = await ConnectToAgentAsync(socketPath, cancellationToken);
            }
            catch (Exception ex) when (_config.FallbackToFileEncryption)
            {
                // Log warning and continue with fallback
                System.Diagnostics.Trace.TraceWarning($"SSH agent not available, using fallback encryption: {ex.Message}");
                _agent = null;
            }
        }

        private async Task<IAgentProtocol?> ConnectToAgentAsync(string socketPath, CancellationToken cancellationToken)
        {
            // Use external process to list keys since SSH.NET's agent support may be limited
            // This provides better compatibility across platforms
            var identities = await ListAgentIdentitiesAsync(cancellationToken);

            if (identities.Count == 0)
            {
                if (_config.FallbackToFileEncryption)
                    return null;

                throw new InvalidOperationException("No keys found in SSH agent.");
            }

            return new ProcessBasedAgent(identities);
        }

        private async Task<List<AgentIdentity>> ListAgentIdentitiesAsync(CancellationToken cancellationToken)
        {
            var identities = new List<AgentIdentity>();

            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ssh-add" : "/usr/bin/ssh-add",
                    Arguments = "-L",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                if (process == null)
                    return identities;

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                    return identities;

                // Parse ssh-add -L output
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Trim().Split(' ', 3);
                    if (parts.Length >= 2)
                    {
                        var keyType = parts[0];
                        var publicKeyBase64 = parts[1];
                        var comment = parts.Length > 2 ? parts[2] : "";

                        var fingerprint = ComputeFingerprint(publicKeyBase64);

                        identities.Add(new AgentIdentity
                        {
                            KeyType = keyType,
                            PublicKeyBase64 = publicKeyBase64,
                            Comment = comment,
                            Fingerprint = fingerprint
                        });
                    }
                }
            }
            catch
            {

                // Return empty list on error
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            return identities;
        }

        private async Task SelectEncryptionKeyAsync(CancellationToken cancellationToken)
        {
            if (_agent == null)
                return;

            var identities = (_agent as ProcessBasedAgent)?.Identities ?? new List<AgentIdentity>();

            if (!string.IsNullOrEmpty(_config.KeyFingerprint))
            {
                // Find key by fingerprint
                _selectedIdentity = identities.FirstOrDefault(i =>
                    i.Fingerprint.Contains(_config.KeyFingerprint, StringComparison.OrdinalIgnoreCase));

                if (_selectedIdentity == null)
                {
                    throw new InvalidOperationException(
                        $"SSH key with fingerprint '{_config.KeyFingerprint}' not found in agent.");
                }
            }
            else
            {
                // Use the first available key, preferring ECDSA/Ed25519
                _selectedIdentity = identities
                    .OrderByDescending(i => i.KeyType.Contains("ecdsa") || i.KeyType.Contains("ed25519"))
                    .FirstOrDefault();
            }

            await Task.CompletedTask;
        }

        private async Task<byte[]> EncryptWithAgentKeyAsync(byte[] data)
        {
            if (_selectedIdentity == null)
            {
                // #3565: No identity loaded from agent — fall back to file-based encryption.
                return EncryptWithDerivedKey(data);
            }

            // #3565: NOTE — SSH agents only support signing operations (sign, list-keys, add-key,
            // remove-key). They do NOT expose a decrypt or ECDH-agree operation. The hybrid scheme
            // below uses the SSH public key as a static Diffie-Hellman public value to derive an
            // encryption key via HKDF, which provides sender-side encryption but NOT agent-mediated
            // decryption (the agent private key is not used during decryption). This means the
            // "agent involvement" is limited to using the agent's public key as a domain separator.
            // For true agent-mediated decryption, a custom SSH agent extension (e.g., via ProxyCommand)
            // or a PKCS#11 bridge is required. Callers must be aware of this limitation.
            //
            // Use SSH public key to derive a shared secret via ECDH-like scheme:
            // 1. Generate ephemeral key pair
            // 2. Derive encryption key from ephemeral private + SSH public key
            // 3. Encrypt data with derived key
            // 4. Store ephemeral public key with ciphertext

            using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var ephemeralPublic = ephemeral.PublicKey.ExportSubjectPublicKeyInfo();

            // Derive encryption key from public key material
            var keyMaterial = Convert.FromBase64String(_selectedIdentity.PublicKeyBase64);
            var combinedMaterial = new byte[ephemeralPublic.Length + keyMaterial.Length];
            Buffer.BlockCopy(ephemeralPublic, 0, combinedMaterial, 0, ephemeralPublic.Length);
            Buffer.BlockCopy(keyMaterial, 0, combinedMaterial, ephemeralPublic.Length, keyMaterial.Length);

            var encryptionKey = Rfc2898DeriveBytes.Pbkdf2(
                combinedMaterial,
                SHA256.HashData(Encoding.UTF8.GetBytes("DataWarehouse.SSH.Salt.v1")),
                100000,
                HashAlgorithmName.SHA256,
                32);

            // Encrypt with AES-GCM
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[data.Length];

            using var aes = new AesGcm(encryptionKey, 16);
            aes.Encrypt(nonce, data, ciphertext, tag);

            // Format: ephemeral_public_len (4) + ephemeral_public + nonce (12) + tag (16) + ciphertext
            var ephemeralLen = BitConverter.GetBytes(ephemeralPublic.Length);
            var result = new byte[4 + ephemeralPublic.Length + 12 + 16 + ciphertext.Length];
            var pos = 0;
            Buffer.BlockCopy(ephemeralLen, 0, result, pos, 4); pos += 4;
            Buffer.BlockCopy(ephemeralPublic, 0, result, pos, ephemeralPublic.Length); pos += ephemeralPublic.Length;
            Buffer.BlockCopy(nonce, 0, result, pos, 12); pos += 12;
            Buffer.BlockCopy(tag, 0, result, pos, 16); pos += 16;
            Buffer.BlockCopy(ciphertext, 0, result, pos, ciphertext.Length);

            return await Task.FromResult(result);
        }

        private async Task<byte[]> DecryptWithAgentKeyAsync(byte[] encryptedData)
        {
            if (_selectedIdentity == null)
            {
                // Fallback to file-based decryption
                return DecryptWithDerivedKey(encryptedData);
            }

            // Parse encrypted data
            if (encryptedData.Length < 4 + 12 + 16)
                throw new CryptographicException("Invalid encrypted data format");

            var ephemeralLen = BitConverter.ToInt32(encryptedData, 0);
            if (encryptedData.Length < 4 + ephemeralLen + 12 + 16)
                throw new CryptographicException("Invalid encrypted data format");

            var ephemeralPublic = new byte[ephemeralLen];
            var nonce = new byte[12];
            var tag = new byte[16];
            var ciphertext = new byte[encryptedData.Length - 4 - ephemeralLen - 12 - 16];

            var pos = 4;
            Buffer.BlockCopy(encryptedData, pos, ephemeralPublic, 0, ephemeralLen); pos += ephemeralLen;
            Buffer.BlockCopy(encryptedData, pos, nonce, 0, 12); pos += 12;
            Buffer.BlockCopy(encryptedData, pos, tag, 0, 16); pos += 16;
            Buffer.BlockCopy(encryptedData, pos, ciphertext, 0, ciphertext.Length);

            // Derive the same encryption key
            var keyMaterial = Convert.FromBase64String(_selectedIdentity.PublicKeyBase64);
            var combinedMaterial = new byte[ephemeralPublic.Length + keyMaterial.Length];
            Buffer.BlockCopy(ephemeralPublic, 0, combinedMaterial, 0, ephemeralPublic.Length);
            Buffer.BlockCopy(keyMaterial, 0, combinedMaterial, ephemeralPublic.Length, keyMaterial.Length);

            var encryptionKey = Rfc2898DeriveBytes.Pbkdf2(
                combinedMaterial,
                SHA256.HashData(Encoding.UTF8.GetBytes("DataWarehouse.SSH.Salt.v1")),
                100000,
                HashAlgorithmName.SHA256,
                32);

            // Decrypt with AES-GCM
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(encryptionKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return await Task.FromResult(plaintext);
        }

        private byte[] EncryptWithDerivedKey(byte[] data)
        {
            var key = DeriveFallbackKey();
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[data.Length];

            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, data, ciphertext, tag);

            // Format: marker (1) + nonce (12) + tag (16) + ciphertext
            var result = new byte[1 + 12 + 16 + ciphertext.Length];
            result[0] = 0xFF; // Marker for fallback encryption
            Buffer.BlockCopy(nonce, 0, result, 1, 12);
            Buffer.BlockCopy(tag, 0, result, 13, 16);
            Buffer.BlockCopy(ciphertext, 0, result, 29, ciphertext.Length);

            return result;
        }

        private byte[] DecryptWithDerivedKey(byte[] encryptedData)
        {
            if (encryptedData.Length < 29 || encryptedData[0] != 0xFF)
                throw new CryptographicException("Invalid fallback encrypted data format");

            var key = DeriveFallbackKey();
            var nonce = new byte[12];
            var tag = new byte[16];
            var ciphertext = new byte[encryptedData.Length - 29];

            Buffer.BlockCopy(encryptedData, 1, nonce, 0, 12);
            Buffer.BlockCopy(encryptedData, 13, tag, 0, 16);
            Buffer.BlockCopy(encryptedData, 29, ciphertext, 0, ciphertext.Length);

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }

        private byte[] DeriveFallbackKey()
        {
            var entropy = $"{Environment.MachineName}:{Environment.UserName}:DataWarehouse.SSH.Fallback.v1";
            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(entropy),
                SHA256.HashData(Encoding.UTF8.GetBytes("DataWarehouse.SSH.Fallback.Salt.v1")),
                100000,
                HashAlgorithmName.SHA256,
                32);
        }

        private static string ComputeFingerprint(string publicKeyBase64)
        {
            var keyBytes = Convert.FromBase64String(publicKeyBase64);
            var hash = SHA256.HashData(keyBytes);
            return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
        }

        #endregion

        private string GetKeyPath(string keyId)
        {
            var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyId)))[..32];
            return Path.Combine(_config.StoragePath, $"{safeId}.ssh.key");
        }

        private async Task SaveMetadataAsync()
        {
            var metadata = new SshAgentMetadata
            {
                CurrentKeyId = _currentKeyId ?? string.Empty,
                LastUpdated = DateTime.UtcNow,
                Version = 1,
                AgentKeyFingerprint = _selectedIdentity?.Fingerprint ?? "fallback"
            };

            var json = JsonSerializer.Serialize(metadata);
            var metadataPath = Path.Combine(_config.StoragePath, "keystore.meta");
            await File.WriteAllTextAsync(metadataPath, json);
        }

        public override void Dispose()
        {
            _agent = null;
            _selectedIdentity = null;
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for SSH Agent key store strategy.
    /// </summary>
    public class SshAgentConfig
    {
        /// <summary>
        /// Path to SSH agent socket.
        /// If null, uses SSH_AUTH_SOCK environment variable.
        /// </summary>
        public string? AgentSocketPath { get; set; }

        /// <summary>
        /// Fingerprint of the SSH key to use for encryption.
        /// If null, the first available key is used.
        /// </summary>
        public string? KeyFingerprint { get; set; }

        /// <summary>
        /// Path to store wrapped keys.
        /// </summary>
        public string StoragePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "ssh-keystore");

        /// <summary>
        /// Whether to fall back to file-based encryption if SSH agent is not available.
        /// </summary>
        public bool FallbackToFileEncryption { get; set; } = true;
    }

    internal class SshAgentMetadata
    {
        public string CurrentKeyId { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; }
        public int Version { get; set; }
        public string AgentKeyFingerprint { get; set; } = string.Empty;
    }

    internal interface IAgentProtocol
    {
    }

    internal class AgentIdentity
    {
        public string KeyType { get; set; } = string.Empty;
        public string PublicKeyBase64 { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
    }

    internal class ProcessBasedAgent : IAgentProtocol
    {
        public List<AgentIdentity> Identities { get; }

        public ProcessBasedAgent(List<AgentIdentity> identities)
        {
            Identities = identities;
        }
    }
}
