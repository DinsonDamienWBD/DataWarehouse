using DataWarehouse.SDK.Security;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Platform
{
    /// <summary>
    /// Windows Credential Manager KeyStore strategy using native DPAPI and Credential Manager APIs.
    /// Provides secure key storage on Windows platforms using P/Invoke to Advapi32.dll.
    ///
    /// Features:
    /// - Uses Windows Credential Manager (CredRead/CredWrite/CredDelete)
    /// - Keys are protected by user's Windows login credentials
    /// - Supports Generic credentials with binary data
    /// - Automatic DPAPI protection for additional security
    /// - Key versioning through credential metadata
    ///
    /// Requirements:
    /// - Windows OS only (will throw on other platforms)
    /// - User must be logged in (credentials tied to user session)
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsCredManagerStrategy : KeyStoreStrategyBase
    {
        private const string CredentialPrefix = "DataWarehouse.KeyStore";
        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;
        private const int CRED_PERSIST_ENTERPRISE = 3;

        private WindowsCredManagerConfig _config = new();
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
            MaxKeySizeBytes = 2560, // Windows Credential Manager limit for CredentialBlob
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Windows Credential Manager",
                ["Platform"] = "Windows",
                ["StorageType"] = "OS Credential Store",
                ["ProtectionMethod"] = "DPAPI + Credential Manager"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("windowscredmanager.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public WindowsCredManagerStrategy()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException(
                    "WindowsCredManagerStrategy is only supported on Windows platforms.");
            }
        }

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("windowscredmanager.init");
            // Load configuration
            if (Configuration.TryGetValue("CredentialPrefix", out var prefixObj) && prefixObj is string prefix)
                _config.CredentialPrefix = prefix;
            if (Configuration.TryGetValue("PersistenceLevel", out var persistObj) && persistObj is string persist)
                _config.PersistenceLevel = Enum.Parse<CredentialPersistence>(persist, ignoreCase: true);
            if (Configuration.TryGetValue("UseEntropy", out var entropyObj) && entropyObj is bool useEntropy)
                _config.UseAdditionalEntropy = useEntropy;
            if (Configuration.TryGetValue("EntropyKey", out var entropyKeyObj) && entropyKeyObj is string entropyKey)
                _config.EntropyKey = entropyKey;

            // Try to load metadata to get current key ID
            var metadataCredName = GetMetadataCredentialName();
            if (TryReadCredential(metadataCredName, out var metadataBytes))
            {
                try
                {
                    var metadata = JsonSerializer.Deserialize<CredManagerMetadata>(
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
                // Try to write and read a test credential
                var testCredName = $"{_config.CredentialPrefix}.HealthCheck";
                var testData = Encoding.UTF8.GetBytes("HealthCheck");

                WriteCredential(testCredName, testData, "Health check credential");
                var success = TryReadCredential(testCredName, out var readData);

                // Clean up test credential
                DeleteCredential(testCredName);

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
            var credName = GetKeyCredentialName(keyId);

            if (!TryReadCredential(credName, out var encryptedKey))
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in Windows Credential Manager.");
            }

            // Unprotect the key data (reverse DPAPI protection we applied during save)
            var keyData = UnprotectData(encryptedKey!);

            return await Task.FromResult(keyData);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var credName = GetKeyCredentialName(keyId);

            // Protect the key data with DPAPI before storing
            var protectedData = ProtectData(keyData);

            WriteCredential(credName, protectedData, $"DataWarehouse encryption key: {keyId}");

            // Update metadata
            _currentKeyId = keyId;
            await SaveMetadataAsync();
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keys = new List<string>();
            var prefix = $"{_config.CredentialPrefix}.Key.";

            // Enumerate credentials using CredEnumerate
            if (CredEnumerateW(prefix + "*", 0, out var count, out var credentials))
            {
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var credPtr = Marshal.ReadIntPtr(credentials, i * IntPtr.Size);
                        var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                        if (cred.TargetName != null && cred.TargetName.StartsWith(prefix))
                        {
                            var keyId = cred.TargetName.Substring(prefix.Length);
                            keys.Add(keyId);
                        }
                    }
                }
                finally
                {
                    CredFree(credentials);
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

            var credName = GetKeyCredentialName(keyId);
            DeleteCredential(credName);

            await Task.CompletedTask;
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var credName = GetKeyCredentialName(keyId);

            if (!TryGetCredentialMetadata(credName, out var lastWritten))
            {
                return null;
            }

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = lastWritten,
                LastRotatedAt = lastWritten,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["CredentialName"] = credName,
                    ["Platform"] = "Windows Credential Manager",
                    ["PersistenceLevel"] = _config.PersistenceLevel.ToString()
                }
            });
        }

        private string GetKeyCredentialName(string keyId)
        {
            return $"{_config.CredentialPrefix}.Key.{keyId}";
        }

        private string GetMetadataCredentialName()
        {
            return $"{_config.CredentialPrefix}.Metadata";
        }

        private async Task SaveMetadataAsync()
        {
            var metadata = new CredManagerMetadata
            {
                CurrentKeyId = _currentKeyId ?? string.Empty,
                LastUpdated = DateTime.UtcNow,
                Version = 1
            };

            var json = JsonSerializer.Serialize(metadata);
            var metadataBytes = Encoding.UTF8.GetBytes(json);
            WriteCredential(GetMetadataCredentialName(), metadataBytes, "DataWarehouse KeyStore metadata");

            await Task.CompletedTask;
        }

        private byte[] ProtectData(byte[] data)
        {
            byte[]? entropy = _config.UseAdditionalEntropy
                ? SHA256.HashData(Encoding.UTF8.GetBytes(_config.EntropyKey ?? "DataWarehouse.DefaultEntropy"))
                : null;

            return ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
        }

        private byte[] UnprotectData(byte[] protectedData)
        {
            byte[]? entropy = _config.UseAdditionalEntropy
                ? SHA256.HashData(Encoding.UTF8.GetBytes(_config.EntropyKey ?? "DataWarehouse.DefaultEntropy"))
                : null;

            return ProtectedData.Unprotect(protectedData, entropy, DataProtectionScope.CurrentUser);
        }

        #region Windows Credential Manager P/Invoke

        private void WriteCredential(string targetName, byte[] credentialBlob, string comment)
        {
            var credential = new CREDENTIAL
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = targetName,
                Comment = comment,
                CredentialBlobSize = (uint)credentialBlob.Length,
                Persist = (uint)(_config.PersistenceLevel == CredentialPersistence.Enterprise
                    ? CRED_PERSIST_ENTERPRISE
                    : CRED_PERSIST_LOCAL_MACHINE),
                UserName = Environment.UserName
            };

            var blobPtr = Marshal.AllocHGlobal(credentialBlob.Length);
            try
            {
                Marshal.Copy(credentialBlob, 0, blobPtr, credentialBlob.Length);
                credential.CredentialBlob = blobPtr;

                if (!CredWriteW(ref credential, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(
                        $"Failed to write credential '{targetName}'. Win32 error: {error}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(blobPtr);
            }
        }

        private bool TryReadCredential(string targetName, out byte[]? credentialBlob)
        {
            credentialBlob = null;

            if (!CredReadW(targetName, CRED_TYPE_GENERIC, 0, out var credentialPtr))
            {
                return false;
            }

            try
            {
                var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                if (credential.CredentialBlobSize > 0 && credential.CredentialBlob != IntPtr.Zero)
                {
                    credentialBlob = new byte[credential.CredentialBlobSize];
                    Marshal.Copy(credential.CredentialBlob, credentialBlob, 0, (int)credential.CredentialBlobSize);
                    return true;
                }
                return false;
            }
            finally
            {
                CredFree(credentialPtr);
            }
        }

        private bool TryGetCredentialMetadata(string targetName, out DateTime lastWritten)
        {
            lastWritten = DateTime.MinValue;

            if (!CredReadW(targetName, CRED_TYPE_GENERIC, 0, out var credentialPtr))
            {
                return false;
            }

            try
            {
                var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                lastWritten = DateTime.FromFileTimeUtc(credential.LastWritten);
                return true;
            }
            finally
            {
                CredFree(credentialPtr);
            }
        }

        private void DeleteCredential(string targetName)
        {
            if (!CredDeleteW(targetName, CRED_TYPE_GENERIC, 0))
            {
                var error = Marshal.GetLastWin32Error();
                // Ignore "not found" errors (error 1168)
                if (error != 1168)
                {
                    throw new InvalidOperationException(
                        $"Failed to delete credential '{targetName}'. Win32 error: {error}");
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? TargetName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredReadW(string targetName, uint type, uint reservedFlag, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredDeleteW(string targetName, uint type, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredEnumerateW(string filter, uint flags, out int count, out IntPtr credentials);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        #endregion
    }

    /// <summary>
    /// Configuration for Windows Credential Manager key store strategy.
    /// </summary>
    public class WindowsCredManagerConfig
    {
        /// <summary>
        /// Prefix for all credential names in Credential Manager.
        /// Default: "DataWarehouse.KeyStore"
        /// </summary>
        public string CredentialPrefix { get; set; } = "DataWarehouse.KeyStore";

        /// <summary>
        /// Credential persistence level.
        /// LocalMachine: Persists for all sessions on this computer.
        /// Enterprise: Roams with user profile (domain environments).
        /// </summary>
        public CredentialPersistence PersistenceLevel { get; set; } = CredentialPersistence.LocalMachine;

        /// <summary>
        /// Whether to use additional entropy for DPAPI protection.
        /// </summary>
        public bool UseAdditionalEntropy { get; set; } = true;

        /// <summary>
        /// Custom entropy key for DPAPI protection.
        /// </summary>
        public string? EntropyKey { get; set; }
    }

    /// <summary>
    /// Credential persistence level for Windows Credential Manager.
    /// </summary>
    public enum CredentialPersistence
    {
        /// <summary>
        /// Credential persists for all sessions on this computer.
        /// </summary>
        LocalMachine,

        /// <summary>
        /// Credential roams with user profile (domain environments).
        /// </summary>
        Enterprise
    }

    internal class CredManagerMetadata
    {
        public string CurrentKeyId { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; }
        public int Version { get; set; }
    }
}
