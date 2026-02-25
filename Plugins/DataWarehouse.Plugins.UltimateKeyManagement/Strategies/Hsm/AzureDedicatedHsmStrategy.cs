using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hsm
{
    /// <summary>
    /// Azure Dedicated HSM / Managed HSM KeyStore strategy using Azure.Security.KeyVault.Keys SDK.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for Azure Managed HSM integration.
    ///
    /// Supported features:
    /// - Azure Managed HSM (FIPS 140-2 Level 3 validated)
    /// - Azure Dedicated HSM (Thales Luna Network HSM 7)
    /// - Key generation inside HSM (keys never leave HSM boundary)
    /// - RSA and EC key operations
    /// - AES-256-GCM key wrapping/unwrapping
    /// - Multi-region replication support
    /// - Role-based access control (RBAC)
    /// - Soft-delete and purge protection
    ///
    /// Configuration:
    /// - ManagedHsmUrl: Azure Managed HSM URL (e.g., "https://{hsm-name}.managedhsm.azure.net")
    /// - TenantId: Azure AD tenant ID
    /// - ClientId: Application (client) ID with HSM access
    /// - ClientSecret: Client secret for authentication
    /// - DefaultKeyName: Default key name for operations (default: "datawarehouse-master")
    /// - KeyType: Key type for new keys (RSA-HSM, EC-HSM, oct-HSM)
    /// - KeySize: Key size in bits (2048, 3072, 4096 for RSA; 256, 384, 521 for EC)
    /// </summary>
    public sealed class AzureDedicatedHsmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private AzureDedicatedHsmConfig _config = new();
        private KeyClient? _keyClient;
        private CryptographyClient? _cryptoClient;
        private string? _currentKeyId;
        private KeyVaultKey? _currentKey;
        private readonly SemaphoreSlim _keyLock = new(1, 1);

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = true,
            SupportsExpiration = true,
            SupportsReplication = true,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Azure Managed HSM",
                ["Cloud"] = "Microsoft Azure",
                ["Compliance"] = "FIPS 140-2 Level 3",
                ["AuthMethod"] = "Azure AD OAuth2 / Managed Identity"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("azurededicatedhsm.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "RSA-OAEP-256",
            "RSA-OAEP",
            "RSA1_5",
            "A256KW",
            "A192KW",
            "A128KW"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("azurededicatedhsm.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("ManagedHsmUrl", out var hsmUrlObj) && hsmUrlObj is string hsmUrl)
                _config.ManagedHsmUrl = hsmUrl;
            if (Configuration.TryGetValue("TenantId", out var tenantIdObj) && tenantIdObj is string tenantId)
                _config.TenantId = tenantId;
            if (Configuration.TryGetValue("ClientId", out var clientIdObj) && clientIdObj is string clientId)
                _config.ClientId = clientId;
            if (Configuration.TryGetValue("ClientSecret", out var clientSecretObj) && clientSecretObj is string clientSecret)
                _config.ClientSecret = clientSecret;
            if (Configuration.TryGetValue("DefaultKeyName", out var keyNameObj) && keyNameObj is string keyName)
                _config.DefaultKeyName = keyName;
            if (Configuration.TryGetValue("KeyType", out var keyTypeObj) && keyTypeObj is string keyType)
                _config.KeyType = keyType;
            if (Configuration.TryGetValue("KeySize", out var keySizeObj))
            {
                if (keySizeObj is int keySize) _config.KeySize = keySize;
                else if (keySizeObj is long keySizeLong) _config.KeySize = (int)keySizeLong;
            }
            if (Configuration.TryGetValue("UseManagedIdentity", out var useMiObj) && useMiObj is bool useMi)
                _config.UseManagedIdentity = useMi;

            // Validate configuration
            if (string.IsNullOrEmpty(_config.ManagedHsmUrl))
                throw new InvalidOperationException("ManagedHsmUrl is required for Azure Dedicated HSM strategy");

            // Initialize Azure credential
            Azure.Core.TokenCredential credential;
            if (_config.UseManagedIdentity)
            {
                // Use Managed Identity (for Azure-hosted applications)
                credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    TenantId = _config.TenantId
                });
            }
            else
            {
                // Use client credentials (service principal)
                if (string.IsNullOrEmpty(_config.TenantId))
                    throw new InvalidOperationException("TenantId is required when not using Managed Identity");
                if (string.IsNullOrEmpty(_config.ClientId))
                    throw new InvalidOperationException("ClientId is required when not using Managed Identity");
                if (string.IsNullOrEmpty(_config.ClientSecret))
                    throw new InvalidOperationException("ClientSecret is required when not using Managed Identity");

                credential = new ClientSecretCredential(_config.TenantId, _config.ClientId, _config.ClientSecret);
            }

            // Initialize Key Vault Key client for Managed HSM
            _keyClient = new KeyClient(new Uri(_config.ManagedHsmUrl), credential);

            // Validate connection by checking if default key exists or can be created
            await EnsureDefaultKeyAsync(cancellationToken);

            _currentKeyId = _config.DefaultKeyName;
        }

        private async Task EnsureDefaultKeyAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Try to get existing key
                var response = await _keyClient!.GetKeyAsync(_config.DefaultKeyName, cancellationToken: cancellationToken);
                _currentKey = response.Value;
                InitializeCryptoClient();
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Key doesn't exist, create it
                await CreateHsmKeyAsync(_config.DefaultKeyName, cancellationToken);
            }
        }

        private async Task CreateHsmKeyAsync(string keyName, CancellationToken cancellationToken)
        {
            var keyType = _config.KeyType.ToUpperInvariant() switch
            {
                "RSA-HSM" or "RSA" => KeyType.RsaHsm,
                "EC-HSM" or "EC" => KeyType.EcHsm,
                "OCT-HSM" or "OCT" or "AES" => KeyType.OctHsm,
                _ => KeyType.RsaHsm
            };

            KeyVaultKey key;

            if (keyType == KeyType.RsaHsm)
            {
                var options = new CreateRsaKeyOptions(keyName, hardwareProtected: true)
                {
                    KeySize = _config.KeySize,
                    KeyOperations =
                    {
                        KeyOperation.Encrypt,
                        KeyOperation.Decrypt,
                        KeyOperation.WrapKey,
                        KeyOperation.UnwrapKey,
                        KeyOperation.Sign,
                        KeyOperation.Verify
                    }
                };
                key = await _keyClient!.CreateRsaKeyAsync(options, cancellationToken);
            }
            else if (keyType == KeyType.EcHsm)
            {
                var curveName = _config.KeySize switch
                {
                    256 => KeyCurveName.P256,
                    384 => KeyCurveName.P384,
                    521 => KeyCurveName.P521,
                    _ => KeyCurveName.P256
                };

                var options = new CreateEcKeyOptions(keyName, hardwareProtected: true)
                {
                    CurveName = curveName,
                    KeyOperations =
                    {
                        KeyOperation.Sign,
                        KeyOperation.Verify
                    }
                };
                key = await _keyClient!.CreateEcKeyAsync(options, cancellationToken);
            }
            else // OctHsm (AES)
            {
                var options = new CreateOctKeyOptions(keyName, hardwareProtected: true)
                {
                    KeySize = _config.KeySize,
                    KeyOperations =
                    {
                        KeyOperation.Encrypt,
                        KeyOperation.Decrypt,
                        KeyOperation.WrapKey,
                        KeyOperation.UnwrapKey
                    }
                };
                key = await _keyClient!.CreateOctKeyAsync(options, cancellationToken);
            }

            _currentKey = key;
            InitializeCryptoClient();
        }

        private void InitializeCryptoClient()
        {
            if (_currentKey != null)
            {
                Azure.Core.TokenCredential credential;
                if (_config.UseManagedIdentity)
                {
                    credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                    {
                        TenantId = _config.TenantId
                    });
                }
                else
                {
                    credential = new ClientSecretCredential(_config.TenantId, _config.ClientId, _config.ClientSecret);
                }

                _cryptoClient = new CryptographyClient(_currentKey.Id, credential);
            }
        }

        private CryptographyClient GetCryptoClientForKey(string keyId)
        {
            Azure.Core.TokenCredential credential;
            if (_config.UseManagedIdentity)
            {
                credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    TenantId = _config.TenantId
                });
            }
            else
            {
                credential = new ClientSecretCredential(_config.TenantId, _config.ClientId, _config.ClientSecret);
            }

            var keyUri = new Uri($"{_config.ManagedHsmUrl}/keys/{keyId}");
            return new CryptographyClient(keyUri, credential);
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? _config.DefaultKeyName);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // List keys to verify connectivity
                await foreach (var key in _keyClient!.GetPropertiesOfKeysAsync(cancellationToken))
                {
                    // Just need to successfully enumerate - return true immediately
                    return true;
                }
                return true; // Empty list is also valid
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            // Azure Managed HSM keys don't leave the HSM in plaintext
            // For symmetric keys (oct-HSM), we can export if the key policy allows
            // For asymmetric keys (RSA-HSM, EC-HSM), only public key is exportable

            var response = await _keyClient!.GetKeyAsync(keyId);
            var key = response.Value;

            if (key.KeyType == KeyType.OctHsm)
            {
                // Symmetric keys - cannot be exported from HSM
                // Return a reference/handle that can be used with WrapKey/UnwrapKey
                throw new InvalidOperationException(
                    $"Key '{keyId}' is an HSM-protected symmetric key and cannot be exported. " +
                    "Use envelope encryption with WrapKeyAsync/UnwrapKeyAsync instead.");
            }

            // For asymmetric keys, return the public key (DER encoded)
            if (key.Key.N != null) // RSA
            {
                using var rsa = key.Key.ToRSA();
                return rsa.ExportSubjectPublicKeyInfo();
            }
            else if (key.Key.X != null && key.Key.Y != null) // EC
            {
                using var ecdsa = key.Key.ToECDsa();
                return ecdsa.ExportSubjectPublicKeyInfo();
            }

            throw new InvalidOperationException($"Unsupported key type for key '{keyId}'");
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            // For Managed HSM, we generate keys inside the HSM rather than importing
            await _keyLock.WaitAsync();
            try
            {
                // Create new HSM key
                await CreateHsmKeyAsync(keyId, CancellationToken.None);
                _currentKeyId = keyId;
            }
            finally
            {
                _keyLock.Release();
            }
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var cryptoClient = GetCryptoClientForKey(kekId);

            // Get key info to determine appropriate wrapping algorithm
            var keyResponse = await _keyClient!.GetKeyAsync(kekId);
            var key = keyResponse.Value;

            KeyWrapAlgorithm algorithm;
            if (key.KeyType == KeyType.RsaHsm)
            {
                algorithm = KeyWrapAlgorithm.RsaOaep256;
            }
            else if (key.KeyType == KeyType.OctHsm)
            {
                algorithm = KeyWrapAlgorithm.A256KW;
            }
            else
            {
                throw new InvalidOperationException($"Key type {key.KeyType} does not support key wrapping");
            }

            var result = await cryptoClient.WrapKeyAsync(algorithm, dataKey);
            return result.EncryptedKey;
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var cryptoClient = GetCryptoClientForKey(kekId);

            // Get key info to determine appropriate unwrapping algorithm
            var keyResponse = await _keyClient!.GetKeyAsync(kekId);
            var key = keyResponse.Value;

            KeyWrapAlgorithm algorithm;
            if (key.KeyType == KeyType.RsaHsm)
            {
                algorithm = KeyWrapAlgorithm.RsaOaep256;
            }
            else if (key.KeyType == KeyType.OctHsm)
            {
                algorithm = KeyWrapAlgorithm.A256KW;
            }
            else
            {
                throw new InvalidOperationException($"Key type {key.KeyType} does not support key unwrapping");
            }

            var result = await cryptoClient.UnwrapKeyAsync(algorithm, wrappedKey);
            return result.Key;
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keys = new List<string>();

            await foreach (var keyProperties in _keyClient!.GetPropertiesOfKeysAsync(cancellationToken))
            {
                if (keyProperties.Enabled == true)
                {
                    keys.Add(keyProperties.Name);
                }
            }

            return keys.AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            // Azure Managed HSM uses soft-delete by default
            // Keys are recoverable for a period after deletion
            var operation = await _keyClient!.StartDeleteKeyAsync(keyId, cancellationToken);

            // Wait for deletion to complete
            await operation.WaitForCompletionAsync(cancellationToken);
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var response = await _keyClient!.GetKeyAsync(keyId, cancellationToken: cancellationToken);
                var key = response.Value;

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = key.Properties.CreatedOn?.UtcDateTime ?? DateTime.UtcNow,
                    LastRotatedAt = key.Properties.UpdatedOn?.UtcDateTime,
                    ExpiresAt = key.Properties.ExpiresOn?.UtcDateTime,
                    Version = 1, // Could parse from key.Properties.Version
                    IsActive = key.Properties.Enabled == true && keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Backend"] = "Azure Managed HSM",
                        ["ManagedHsmUrl"] = _config.ManagedHsmUrl,
                        ["KeyType"] = key.KeyType.ToString(),
                        ["KeyId"] = key.Id.ToString(),
                        ["Version"] = key.Properties.Version,
                        ["Recoverable"] = key.Properties.RecoveryLevel,
                        ["HsmPlatform"] = key.Properties.HsmPlatform ?? "ManagedHsm"
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Rotates the specified key by creating a new version in the HSM.
        /// </summary>
        public async Task<string> RotateKeyAsync(string keyId, CancellationToken cancellationToken = default)
        {
            var rotatedKey = await _keyClient!.RotateKeyAsync(keyId, cancellationToken);
            return rotatedKey.Value.Properties.Version;
        }

        /// <summary>
        /// Gets all versions of a key.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetKeyVersionsAsync(string keyId, CancellationToken cancellationToken = default)
        {
            var versions = new List<string>();

            await foreach (var keyProperties in _keyClient!.GetPropertiesOfKeyVersionsAsync(keyId, cancellationToken))
            {
                versions.Add(keyProperties.Version);
            }

            return versions.AsReadOnly();
        }

        /// <summary>
        /// Backs up a key for disaster recovery.
        /// The backup is encrypted and can only be restored to an HSM in the same security domain.
        /// </summary>
        public async Task<byte[]> BackupKeyAsync(string keyId, CancellationToken cancellationToken = default)
        {
            var backup = await _keyClient!.BackupKeyAsync(keyId, cancellationToken);
            return backup.Value;
        }

        /// <summary>
        /// Restores a key from backup.
        /// </summary>
        public async Task<string> RestoreKeyAsync(byte[] backup, CancellationToken cancellationToken = default)
        {
            var restored = await _keyClient!.RestoreKeyBackupAsync(backup, cancellationToken);
            return restored.Value.Name;
        }

        public override void Dispose()
        {
            _keyLock?.Dispose();
            // KeyClient and CryptographyClient don't implement IDisposable
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Azure Dedicated HSM / Managed HSM key store strategy.
    /// </summary>
    public class AzureDedicatedHsmConfig
    {
        public string ManagedHsmUrl { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string DefaultKeyName { get; set; } = "datawarehouse-master";
        public string KeyType { get; set; } = "RSA-HSM";
        public int KeySize { get; set; } = 2048;
        public bool UseManagedIdentity { get; set; } = false;
    }
}
