using DataWarehouse.SDK.Security;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Platform
{
    /// <summary>
    /// Linux Secret Service KeyStore strategy using D-Bus org.freedesktop.secrets API.
    /// Provides secure key storage on Linux platforms using the FreeDesktop Secret Service.
    ///
    /// Features:
    /// - Uses org.freedesktop.secrets D-Bus API (libsecret compatible)
    /// - Compatible with GNOME Keyring, KWallet, and other Secret Service implementations
    /// - Keys stored as secrets with custom attributes
    /// - Supports multiple collections
    /// - Automatic session management
    ///
    /// Requirements:
    /// - Linux only (will throw on other platforms)
    /// - Secret Service provider must be running (gnome-keyring, kwallet, etc.)
    /// - D-Bus session bus must be available
    ///
    /// Uses the 'secret-tool' command-line utility for portability.
    /// For production deployments with high throughput, consider using Tmds.DBus NuGet package
    /// for direct D-Bus communication.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class LinuxSecretServiceStrategy : KeyStoreStrategyBase
    {
        private const string DefaultLabel = "DataWarehouse.KeyStore";
        private const string AttributeApplication = "application";
        private const string AttributeKeyId = "keyid";
        private const string AttributeKeyType = "keytype";

        private LinuxSecretServiceConfig _config = new();
        private string? _currentKeyId;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = false,
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 0, // No practical limit
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Linux Secret Service",
                ["Platform"] = "Linux",
                ["StorageType"] = "FreeDesktop Secret Service",
                ["ProtectionMethod"] = "D-Bus Secret Service + AES-256",
                ["Backend"] = "gnome-keyring/kwallet"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("linuxsecretservice.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public LinuxSecretServiceStrategy()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                throw new PlatformNotSupportedException(
                    "LinuxSecretServiceStrategy is only supported on Linux platforms.");
            }
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("linuxsecretservice.init");
            // Load configuration
            if (Configuration.TryGetValue("ApplicationName", out var appObj) && appObj is string app)
                _config.ApplicationName = app;
            if (Configuration.TryGetValue("CollectionName", out var collObj) && collObj is string coll)
                _config.CollectionName = coll;
            if (Configuration.TryGetValue("UseEncryption", out var encObj) && encObj is bool enc)
                _config.UseAdditionalEncryption = enc;

            // Verify secret-tool is available
            if (!IsSecretToolAvailable())
            {
                throw new InvalidOperationException(
                    "secret-tool command not found. Please install libsecret-tools " +
                    "(apt install libsecret-tools or dnf install libsecret).");
            }

            // Try to load metadata to get current key ID
            if (TryGetSecret("metadata", out var metadataBytes))
            {
                try
                {
                    var metadata = JsonSerializer.Deserialize<SecretServiceMetadata>(
                        Encoding.UTF8.GetString(metadataBytes!));
                    _currentKeyId = metadata?.CurrentKeyId;
                }
                catch
                {
                    _currentKeyId = null;
                }
            }

            // If no current key, create initial key
            if (string.IsNullOrEmpty(_currentKeyId))
            {
                _currentKeyId = Guid.NewGuid().ToString("N");
                var initialKey = GenerateKey();
                await SaveKeyToStorage(_currentKeyId, initialKey, CreateSystemContext());
            }

            await Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Try to store and retrieve a test secret
                var testData = "HealthCheck-" + Guid.NewGuid().ToString("N");

                StoreSecret("healthcheck", Encoding.UTF8.GetBytes(testData), "Health check secret");
                var success = TryGetSecret("healthcheck", out var readData);

                // Clean up test secret
                DeleteSecret("healthcheck");

                return await Task.FromResult(success && readData != null &&
                    Encoding.UTF8.GetString(readData) == testData);
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            if (!TryGetSecret(keyId, out var keyData))
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in Linux Secret Service.");
            }

            // If additional encryption is enabled, decrypt the key
            if (_config.UseAdditionalEncryption)
            {
                keyData = DecryptWithMachineKey(keyData!);
            }

            return await Task.FromResult(keyData!);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            // If additional encryption is enabled, encrypt the key
            var dataToStore = _config.UseAdditionalEncryption
                ? EncryptWithMachineKey(keyData)
                : keyData;

            StoreSecret(keyId, dataToStore, $"DataWarehouse encryption key: {keyId}");

            // Update metadata
            _currentKeyId = keyId;
            await SaveMetadataAsync();
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keys = new List<string>();

            // P2-3572: Use ArgumentList to prevent injection.
            // Use secret-tool search to find all keys with our application attribute
            var argList = new[] { "search", "--all", AttributeApplication, _config.ApplicationName, AttributeKeyType, "key" };

            if (ExecuteSecretTool(argList, out var output, out _))
            {
                // Parse output to extract key IDs
                // Output format: [/org/freedesktop/secrets/collection/login/xxx]
                // attribute.keyid = <keyid>
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains($"{AttributeKeyId} = "))
                    {
                        var start = line.IndexOf(" = ") + 3;
                        if (start > 3)
                        {
                            var keyId = line.Substring(start).Trim();
                            if (!string.IsNullOrEmpty(keyId) && keyId != "metadata")
                            {
                                keys.Add(keyId);
                            }
                        }
                    }
                }
            }

            return await Task.FromResult(keys.AsReadOnly());
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            DeleteSecret(keyId);

            await Task.CompletedTask;
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!TryGetSecret(keyId, out _))
            {
                return null;
            }

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow, // Secret Service doesn't expose creation date easily
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["Application"] = _config.ApplicationName,
                    ["Collection"] = _config.CollectionName,
                    ["Platform"] = "Linux Secret Service"
                }
            });
        }

        private async Task SaveMetadataAsync()
        {
            var metadata = new SecretServiceMetadata
            {
                CurrentKeyId = _currentKeyId ?? string.Empty,
                LastUpdated = DateTime.UtcNow,
                Version = 1
            };

            var json = JsonSerializer.Serialize(metadata);
            var metadataBytes = Encoding.UTF8.GetBytes(json);
            StoreSecret("metadata", metadataBytes, "DataWarehouse KeyStore metadata");

            await Task.CompletedTask;
        }

        private bool IsSecretToolAvailable()
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "secret-tool",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        #region Secret Service Operations via secret-tool

        private void StoreSecret(string keyId, byte[] data, string label)
        {
            // P2-3572: Use ArgumentList (not Arguments string) to prevent shell metacharacter injection.
            // secret-tool store --label=<label> attribute1 value1 attribute2 value2 ...
            // Password is read from stdin
            var argList = new List<string> { "store", $"--label={label}" };
            if (!string.IsNullOrEmpty(_config.CollectionName) && _config.CollectionName != "default")
                argList.Add($"--collection={_config.CollectionName}");
            argList.AddRange(new[] { AttributeApplication, _config.ApplicationName, AttributeKeyId, keyId, AttributeKeyType, "key" });

            // Convert data to base64 for safe stdin transfer
            var base64Data = Convert.ToBase64String(data);

            ExecuteSecretToolWithInput(argList, base64Data, out var error);

            if (!string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException($"Failed to store secret: {error}");
            }
        }

        private bool TryGetSecret(string keyId, out byte[]? data)
        {
            data = null;

            // P2-3572: Use ArgumentList to prevent injection.
            // secret-tool lookup attribute1 value1 attribute2 value2 ...
            var argList = new[] { "lookup", AttributeApplication, _config.ApplicationName, AttributeKeyId, keyId };

            if (ExecuteSecretTool(argList, out var output, out _))
            {
                if (!string.IsNullOrEmpty(output))
                {
                    try
                    {
                        // Output is base64-encoded
                        data = Convert.FromBase64String(output.Trim());
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        private void DeleteSecret(string keyId)
        {
            // P2-3572: Use ArgumentList to prevent injection.
            // secret-tool clear attribute1 value1 attribute2 value2 ...
            var argList = new[] { "clear", AttributeApplication, _config.ApplicationName, AttributeKeyId, keyId };
            ExecuteSecretTool(argList, out _, out _);
            // Ignore errors on delete (secret may not exist)
        }

        private bool ExecuteSecretTool(IEnumerable<string> arguments, out string output, out string error)
        {
            // P2-3572: Use ArgumentList instead of Arguments string to prevent shell metacharacter injection.
            var processInfo = new ProcessStartInfo
            {
                FileName = "secret-tool",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in arguments)
                processInfo.ArgumentList.Add(arg);

            // Ensure D-Bus session is available
            if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") == null)
            {
                processInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/" +
                    Environment.GetEnvironmentVariable("UID") + "/bus";
            }

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                output = string.Empty;
                error = "Failed to start secret-tool";
                return false;
            }

            output = process.StandardOutput.ReadToEnd();
            error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0;
        }

        private void ExecuteSecretToolWithInput(IEnumerable<string> arguments, string input, out string error)
        {
            // P2-3572: Use ArgumentList to prevent shell metacharacter injection.
            var processInfo = new ProcessStartInfo
            {
                FileName = "secret-tool",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in arguments)
                processInfo.ArgumentList.Add(arg);

            // Ensure D-Bus session is available
            if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") == null)
            {
                processInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/" +
                    Environment.GetEnvironmentVariable("UID") + "/bus";
            }

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                error = "Failed to start secret-tool";
                return;
            }

            // Write input to stdin
            process.StandardInput.WriteLine(input);
            process.StandardInput.Close();

            error = process.StandardError.ReadToEnd();
            process.WaitForExit();
        }

        #endregion

        #region Additional Encryption Layer

        // Machine-specific encryption for additional security
        private byte[] EncryptWithMachineKey(byte[] data)
        {
            var key = DeriveMachineKey();
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[data.Length];

            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, data, ciphertext, tag);

            // Format: nonce (12) + tag (16) + ciphertext
            var result = new byte[12 + 16 + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, 12);
            Buffer.BlockCopy(tag, 0, result, 12, 16);
            Buffer.BlockCopy(ciphertext, 0, result, 28, ciphertext.Length);

            return result;
        }

        private byte[] DecryptWithMachineKey(byte[] encryptedData)
        {
            if (encryptedData.Length < 28)
                throw new CryptographicException("Invalid encrypted data format");

            var key = DeriveMachineKey();
            var nonce = new byte[12];
            var tag = new byte[16];
            var ciphertext = new byte[encryptedData.Length - 28];

            Buffer.BlockCopy(encryptedData, 0, nonce, 0, 12);
            Buffer.BlockCopy(encryptedData, 12, tag, 0, 16);
            Buffer.BlockCopy(encryptedData, 28, ciphertext, 0, ciphertext.Length);

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }

        private byte[] DeriveMachineKey()
        {
            // Use machine-specific information to derive a key
            var entropyParts = new List<string>
            {
                Environment.MachineName,
                Environment.UserName,
                _config.ApplicationName
            };

            // Add machine-id if available (Linux specific)
            try
            {
                if (File.Exists("/etc/machine-id"))
                {
                    entropyParts.Add(File.ReadAllText("/etc/machine-id").Trim());
                }
            }
            catch
            {

                // Ignore if machine-id is not accessible
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            var combinedEntropy = string.Join("|", entropyParts);
            var entropyBytes = Encoding.UTF8.GetBytes(combinedEntropy);

            return Rfc2898DeriveBytes.Pbkdf2(
                entropyBytes,
                SHA256.HashData(Encoding.UTF8.GetBytes("DataWarehouse.Linux.SecretService.Salt.v1")),
                100000,
                HashAlgorithmName.SHA256,
                32);
        }

        #endregion
    }

    /// <summary>
    /// Configuration for Linux Secret Service key store strategy.
    /// </summary>
    public class LinuxSecretServiceConfig
    {
        /// <summary>
        /// Application name used for secret attributes.
        /// Default: "DataWarehouse.KeyStore"
        /// </summary>
        public string ApplicationName { get; set; } = "DataWarehouse.KeyStore";

        /// <summary>
        /// Collection name in Secret Service.
        /// Use "default" or "login" for the default collection, or specify a custom collection.
        /// </summary>
        public string CollectionName { get; set; } = "login";

        /// <summary>
        /// Whether to apply additional machine-specific encryption.
        /// Provides defense in depth even if Secret Service is compromised.
        /// </summary>
        public bool UseAdditionalEncryption { get; set; } = true;
    }

    internal class SecretServiceMetadata
    {
        public string CurrentKeyId { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; }
        public int Version { get; set; }
    }
}
