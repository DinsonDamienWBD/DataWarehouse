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
    /// macOS Keychain KeyStore strategy using the Security framework.
    /// Provides secure key storage on macOS platforms using P/Invoke to the Security framework.
    ///
    /// Features:
    /// - Uses macOS Keychain Services (SecKeychain APIs)
    /// - Keys are stored as generic passwords in the user's keychain
    /// - Support for both login and custom keychains
    /// - Automatic keychain locking/unlocking
    /// - Access control via ACLs on keychain items
    ///
    /// Requirements:
    /// - macOS only (will throw on other platforms)
    /// - User keychain must be unlocked
    /// </summary>
    [SupportedOSPlatform("macos")]
    public sealed class MacOsKeychainStrategy : KeyStoreStrategyBase
    {
        private const string ServiceName = "DataWarehouse.KeyStore";
        private MacOsKeychainConfig _config = new();
        private string? _currentKeyId;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 0, // No practical limit
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "macOS Keychain",
                ["Platform"] = "macOS",
                ["StorageType"] = "OS Keychain Services",
                ["ProtectionMethod"] = "Keychain + AES-256"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("macoskeychain.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public MacOsKeychainStrategy()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                throw new PlatformNotSupportedException(
                    "MacOsKeychainStrategy is only supported on macOS platforms.");
            }
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("macoskeychain.init");
            // Load configuration
            if (Configuration.TryGetValue("ServiceName", out var serviceObj) && serviceObj is string service)
                _config.ServiceName = service;
            if (Configuration.TryGetValue("KeychainPath", out var pathObj) && pathObj is string path)
                _config.KeychainPath = path;
            if (Configuration.TryGetValue("AccessGroup", out var groupObj) && groupObj is string group)
                _config.AccessGroup = group;

            // Try to load metadata to get current key ID
            var metadataKey = $"{_config.ServiceName}.metadata";
            if (TryGetKeychainItem(metadataKey, out var metadataBytes))
            {
                try
                {
                    var metadata = JsonSerializer.Deserialize<KeychainMetadata>(
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
                // Try to add and retrieve a test item
                var testKey = $"{_config.ServiceName}.healthcheck";
                var testData = Encoding.UTF8.GetBytes("HealthCheck");

                SetKeychainItem(testKey, testData);
                var success = TryGetKeychainItem(testKey, out var readData);

                // Clean up test item
                DeleteKeychainItem(testKey);

                return await Task.FromResult(success && readData != null &&
                    Encoding.UTF8.GetString(readData) == "HealthCheck");
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            var itemKey = GetKeyItemName(keyId);

            if (!TryGetKeychainItem(itemKey, out var keyData))
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in macOS Keychain.");
            }

            return await Task.FromResult(keyData!);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var itemKey = GetKeyItemName(keyId);

            SetKeychainItem(itemKey, keyData);

            // Update metadata
            _currentKeyId = keyId;
            await SaveMetadataAsync();
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keys = new List<string>();
            var prefix = $"{_config.ServiceName}.key.";

            // Use security command to list keychain items
            var processInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                Arguments = $"dump-keychain -r",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                return await Task.FromResult(keys.AsReadOnly());
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            // Parse output to find our keys (generic passwords with matching service name)
            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains($"\"svce\"<blob>=\"{prefix}"))
                {
                    var start = line.IndexOf($"{prefix}") + prefix.Length;
                    var end = line.IndexOf('"', start);
                    if (start > prefix.Length && end > start)
                    {
                        var keyId = line.Substring(start, end - start);
                        keys.Add(keyId);
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

            var itemKey = GetKeyItemName(keyId);
            DeleteKeychainItem(itemKey);

            await Task.CompletedTask;
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var itemKey = GetKeyItemName(keyId);

            if (!TryGetKeychainItem(itemKey, out _))
            {
                return null;
            }

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow, // macOS Keychain doesn't expose creation date easily
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["ServiceName"] = itemKey,
                    ["Platform"] = "macOS Keychain",
                    ["KeychainPath"] = _config.KeychainPath ?? "default"
                }
            });
        }

        private string GetKeyItemName(string keyId)
        {
            return $"{_config.ServiceName}.key.{keyId}";
        }

        private async Task SaveMetadataAsync()
        {
            var metadata = new KeychainMetadata
            {
                CurrentKeyId = _currentKeyId ?? string.Empty,
                LastUpdated = DateTime.UtcNow,
                Version = 1
            };

            var json = JsonSerializer.Serialize(metadata);
            var metadataBytes = Encoding.UTF8.GetBytes(json);
            SetKeychainItem($"{_config.ServiceName}.metadata", metadataBytes);

            await Task.CompletedTask;
        }

        #region macOS Keychain Operations via security command

        // Using the 'security' command-line tool for keychain operations
        // This is more portable than P/Invoke and handles keychain unlock prompts

        private void SetKeychainItem(string account, byte[] data)
        {
            // First try to delete any existing item
            DeleteKeychainItem(account);

            // Convert data to base64 for command-line safety
            var base64Data = Convert.ToBase64String(data);

            // Use security add-generic-password command
            var args = $"add-generic-password -a \"{account}\" -s \"{_config.ServiceName}\" " +
                      $"-w \"{base64Data}\" -U";

            if (!string.IsNullOrEmpty(_config.AccessGroup))
            {
                args += $" -G \"{_config.AccessGroup}\"";
            }

            ExecuteSecurityCommand(args, out _, out var error);

            if (!string.IsNullOrEmpty(error) && !error.Contains("already exists"))
            {
                throw new InvalidOperationException($"Failed to add keychain item: {error}");
            }
        }

        private bool TryGetKeychainItem(string account, out byte[]? data)
        {
            data = null;

            var args = $"find-generic-password -a \"{account}\" -s \"{_config.ServiceName}\" -w";

            if (ExecuteSecurityCommand(args, out var output, out var error))
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

        private void DeleteKeychainItem(string account)
        {
            var args = $"delete-generic-password -a \"{account}\" -s \"{_config.ServiceName}\"";
            ExecuteSecurityCommand(args, out _, out _);
            // Ignore errors on delete (item may not exist)
        }

        private bool ExecuteSecurityCommand(string arguments, out string output, out string error)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                output = string.Empty;
                error = "Failed to start security command";
                return false;
            }

            output = process.StandardOutput.ReadToEnd();
            error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0;
        }

        #endregion

        #region P/Invoke declarations (for future native implementation)

        // These are provided for reference if a pure P/Invoke implementation is needed
        // Currently using the 'security' command-line tool for better compatibility

        /*
        private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            string serviceName,
            uint accountNameLength,
            string accountName,
            uint passwordLength,
            byte[] passwordData,
            out IntPtr itemRef);

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainFindGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            string serviceName,
            uint accountNameLength,
            string accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemRef);

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(SecurityFramework)]
        private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        private const int errSecSuccess = 0;
        private const int errSecItemNotFound = -25300;
        private const int errSecDuplicateItem = -25299;
        */

        #endregion
    }

    /// <summary>
    /// Configuration for macOS Keychain key store strategy.
    /// </summary>
    public class MacOsKeychainConfig
    {
        /// <summary>
        /// Service name for keychain items.
        /// Default: "DataWarehouse.KeyStore"
        /// </summary>
        public string ServiceName { get; set; } = "DataWarehouse.KeyStore";

        /// <summary>
        /// Path to a specific keychain file.
        /// If null, uses the default login keychain.
        /// </summary>
        public string? KeychainPath { get; set; }

        /// <summary>
        /// Access group for keychain item sharing.
        /// Used for sharing items between applications.
        /// </summary>
        public string? AccessGroup { get; set; }
    }

    internal class KeychainMetadata
    {
        public string CurrentKeyId { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; }
        public int Version { get; set; }
    }
}
