using DataWarehouse.SDK.Security;
using Google.Api.Gax;
using Google.Cloud.Kms.V1;
using Google.Protobuf;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hsm
{
    /// <summary>
    /// Google Cloud HSM KeyStore strategy using Google.Cloud.Kms.V1 SDK.
    /// Implements IKeyStoreStrategy and IEnvelopeKeyStore for GCP Cloud HSM integration.
    ///
    /// Supported features:
    /// - Google Cloud HSM (FIPS 140-2 Level 3 validated)
    /// - Key generation inside HSM (keys never leave HSM boundary)
    /// - Symmetric encryption (AES-256-GCM)
    /// - Asymmetric encryption (RSA-OAEP, ECDSA)
    /// - Key wrapping/unwrapping operations
    /// - Automatic key rotation with configurable schedule
    /// - Multi-region key replication
    /// - IAM-based access control
    /// - Cloud Audit Logs integration
    ///
    /// Configuration:
    /// - ProjectId: GCP project ID
    /// - Location: GCP location (e.g., "us-central1", "global")
    /// - KeyRing: Key ring name
    /// - KeyName: Key name within the key ring
    /// - ServiceAccountJson: Service account JSON for authentication (optional, uses ADC if not provided)
    /// - ProtectionLevel: HSM or SOFTWARE (default: HSM)
    /// - Algorithm: Encryption algorithm (default: GOOGLE_SYMMETRIC_ENCRYPTION)
    /// </summary>
    public sealed class GcpCloudHsmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private GcpCloudHsmConfig _config = new();
        private KeyManagementServiceClient? _kmsClient;
        private string? _currentKeyId;
        private CryptoKeyVersionName? _currentKeyVersion;

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
                ["Provider"] = "Google Cloud HSM",
                ["Cloud"] = "Google Cloud Platform",
                ["Compliance"] = "FIPS 140-2 Level 3",
                ["AuthMethod"] = "Service Account / Application Default Credentials"
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("gcpcloudhsm.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "GOOGLE_SYMMETRIC_ENCRYPTION",
            "RSA_DECRYPT_OAEP_2048_SHA256",
            "RSA_DECRYPT_OAEP_3072_SHA256",
            "RSA_DECRYPT_OAEP_4096_SHA256",
            "RSA_DECRYPT_OAEP_4096_SHA512"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("gcpcloudhsm.init");
            // Load configuration from Configuration dictionary
            if (Configuration.TryGetValue("ProjectId", out var projectIdObj) && projectIdObj is string projectId)
                _config.ProjectId = projectId;
            if (Configuration.TryGetValue("Location", out var locationObj) && locationObj is string location)
                _config.Location = location;
            if (Configuration.TryGetValue("KeyRing", out var keyRingObj) && keyRingObj is string keyRing)
                _config.KeyRing = keyRing;
            if (Configuration.TryGetValue("KeyName", out var keyNameObj) && keyNameObj is string keyName)
                _config.KeyName = keyName;
            if (Configuration.TryGetValue("ServiceAccountJson", out var saJsonObj) && saJsonObj is string saJson)
                _config.ServiceAccountJson = saJson;
            if (Configuration.TryGetValue("ProtectionLevel", out var protLevelObj) && protLevelObj is string protLevel)
                _config.ProtectionLevel = protLevel;
            if (Configuration.TryGetValue("Algorithm", out var algoObj) && algoObj is string algo)
                _config.Algorithm = algo;

            // Validate configuration
            if (string.IsNullOrEmpty(_config.ProjectId))
                throw new InvalidOperationException("ProjectId is required for GCP Cloud HSM strategy");
            if (string.IsNullOrEmpty(_config.Location))
                throw new InvalidOperationException("Location is required for GCP Cloud HSM strategy");
            if (string.IsNullOrEmpty(_config.KeyRing))
                throw new InvalidOperationException("KeyRing is required for GCP Cloud HSM strategy");
            if (string.IsNullOrEmpty(_config.KeyName))
                throw new InvalidOperationException("KeyName is required for GCP Cloud HSM strategy");

            // Initialize KMS client
            if (!string.IsNullOrEmpty(_config.ServiceAccountJson))
            {
                // Use explicit service account credentials
                var builder = new KeyManagementServiceClientBuilder
                {
                    JsonCredentials = _config.ServiceAccountJson
                };
                _kmsClient = await builder.BuildAsync(cancellationToken);
            }
            else
            {
                // Use Application Default Credentials (ADC)
                _kmsClient = await KeyManagementServiceClient.CreateAsync(cancellationToken);
            }

            // Ensure key ring and key exist
            await EnsureKeyRingExistsAsync(cancellationToken);
            await EnsureKeyExistsAsync(cancellationToken);

            _currentKeyId = GetKeyResourceName();
        }

        private async Task EnsureKeyRingExistsAsync(CancellationToken cancellationToken)
        {
            var keyRingName = KeyRingName.FromProjectLocationKeyRing(
                _config.ProjectId,
                _config.Location,
                _config.KeyRing);

            try
            {
                await _kmsClient!.GetKeyRingAsync(keyRingName, cancellationToken);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                // Create key ring using string parent
                var parent = $"projects/{_config.ProjectId}/locations/{_config.Location}";
                var keyRing = new KeyRing();

                await _kmsClient!.CreateKeyRingAsync(parent, _config.KeyRing, keyRing, cancellationToken);
            }
        }

        private async Task EnsureKeyExistsAsync(CancellationToken cancellationToken)
        {
            var cryptoKeyName = CryptoKeyName.FromProjectLocationKeyRingCryptoKey(
                _config.ProjectId,
                _config.Location,
                _config.KeyRing,
                _config.KeyName);

            try
            {
                var key = await _kmsClient!.GetCryptoKeyAsync(cryptoKeyName, cancellationToken);
                _currentKeyVersion = CryptoKeyVersionName.Parse(key.Primary.Name);
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
            {
                // Create HSM key
                await CreateHsmKeyAsync(_config.KeyName, cancellationToken);
            }
        }

        private async Task CreateHsmKeyAsync(string keyName, CancellationToken cancellationToken)
        {
            var keyRingName = KeyRingName.FromProjectLocationKeyRing(
                _config.ProjectId,
                _config.Location,
                _config.KeyRing);

            var protectionLevel = _config.ProtectionLevel.ToUpperInvariant() switch
            {
                "HSM" => ProtectionLevel.Hsm,
                "SOFTWARE" => ProtectionLevel.Software,
                "EXTERNAL" => ProtectionLevel.External,
                "EXTERNAL_VPC" => ProtectionLevel.ExternalVpc,
                _ => ProtectionLevel.Hsm
            };

            var algorithm = _config.Algorithm.ToUpperInvariant() switch
            {
                "GOOGLE_SYMMETRIC_ENCRYPTION" => CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.GoogleSymmetricEncryption,
                "RSA_DECRYPT_OAEP_2048_SHA256" => CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaDecryptOaep2048Sha256,
                "RSA_DECRYPT_OAEP_3072_SHA256" => CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaDecryptOaep3072Sha256,
                "RSA_DECRYPT_OAEP_4096_SHA256" => CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaDecryptOaep4096Sha256,
                "RSA_DECRYPT_OAEP_4096_SHA512" => CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaDecryptOaep4096Sha512,
                "EC_SIGN_P256_SHA256" => CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.EcSignP256Sha256,
                "EC_SIGN_P384_SHA384" => CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.EcSignP384Sha384,
                _ => CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.GoogleSymmetricEncryption
            };

            var purpose = algorithm == CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.GoogleSymmetricEncryption
                ? CryptoKey.Types.CryptoKeyPurpose.EncryptDecrypt
                : algorithm.ToString().Contains("SIGN")
                    ? CryptoKey.Types.CryptoKeyPurpose.AsymmetricSign
                    : CryptoKey.Types.CryptoKeyPurpose.AsymmetricDecrypt;

            var cryptoKey = new CryptoKey
            {
                Purpose = purpose,
                VersionTemplate = new CryptoKeyVersionTemplate
                {
                    ProtectionLevel = protectionLevel,
                    Algorithm = algorithm
                },
                Labels =
                {
                    ["managed-by"] = "datawarehouse",
                    ["protection-level"] = protectionLevel.ToString().ToLowerInvariant()
                }
            };

            var createdKey = await _kmsClient!.CreateCryptoKeyAsync(keyRingName, keyName, cryptoKey, cancellationToken);
            _currentKeyVersion = CryptoKeyVersionName.Parse(createdKey.Primary.Name);
        }

        private string GetKeyResourceName()
        {
            return $"projects/{_config.ProjectId}/locations/{_config.Location}/keyRings/{_config.KeyRing}/cryptoKeys/{_config.KeyName}";
        }

        private CryptoKeyName GetCryptoKeyName(string? keyId = null)
        {
            if (string.IsNullOrEmpty(keyId) || keyId == GetKeyResourceName())
            {
                return CryptoKeyName.FromProjectLocationKeyRingCryptoKey(
                    _config.ProjectId,
                    _config.Location,
                    _config.KeyRing,
                    _config.KeyName);
            }

            // Parse key ID if it's a full resource name
            if (keyId.StartsWith("projects/"))
            {
                return CryptoKeyName.Parse(keyId);
            }

            // Otherwise, assume it's just a key name in the same key ring
            return CryptoKeyName.FromProjectLocationKeyRingCryptoKey(
                _config.ProjectId,
                _config.Location,
                _config.KeyRing,
                keyId);
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? GetKeyResourceName());
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var cryptoKeyName = GetCryptoKeyName();
                var key = await _kmsClient!.GetCryptoKeyAsync(cryptoKeyName, cancellationToken);
                return key.Primary.State == CryptoKeyVersion.Types.CryptoKeyVersionState.Enabled;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            // GCP Cloud HSM keys don't leave the HSM in plaintext
            // For symmetric keys, we cannot export the key material
            // For asymmetric keys, we can export the public key

            var cryptoKeyName = GetCryptoKeyName(keyId);
            var key = await _kmsClient!.GetCryptoKeyAsync(cryptoKeyName);

            if (key.Purpose == CryptoKey.Types.CryptoKeyPurpose.EncryptDecrypt)
            {
                // Symmetric key - cannot be exported
                throw new InvalidOperationException(
                    $"Key '{keyId}' is an HSM-protected symmetric key and cannot be exported. " +
                    "Use envelope encryption with WrapKeyAsync/UnwrapKeyAsync instead.");
            }

            // Get the public key for asymmetric keys
            var keyVersionName = CryptoKeyVersionName.Parse(key.Primary.Name);
            var publicKey = await _kmsClient.GetPublicKeyAsync(keyVersionName);

            return Encoding.UTF8.GetBytes(publicKey.Pem);
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            // For Cloud HSM, we generate keys inside the HSM
            await CreateHsmKeyAsync(keyId, CancellationToken.None);
            _currentKeyId = GetKeyResourceName().Replace(_config.KeyName, keyId);
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var cryptoKeyName = GetCryptoKeyName(kekId);

            // For symmetric keys, use encrypt
            // For asymmetric keys, get the public key and wrap locally
            var key = await _kmsClient!.GetCryptoKeyAsync(cryptoKeyName);

            if (key.Purpose == CryptoKey.Types.CryptoKeyPurpose.EncryptDecrypt)
            {
                // Use Cloud KMS encrypt operation
                var response = await _kmsClient.EncryptAsync(cryptoKeyName, ByteString.CopyFrom(dataKey));
                return response.Ciphertext.ToByteArray();
            }
            else if (key.Purpose == CryptoKey.Types.CryptoKeyPurpose.AsymmetricDecrypt)
            {
                // Get public key and wrap locally with RSA-OAEP
                var keyVersionName = CryptoKeyVersionName.Parse(key.Primary.Name);
                var publicKey = await _kmsClient.GetPublicKeyAsync(keyVersionName);

                using var rsa = RSA.Create();
                rsa.ImportFromPem(publicKey.Pem);

                var padding = key.Primary.Algorithm switch
                {
                    CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaDecryptOaep2048Sha256 or
                    CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaDecryptOaep3072Sha256 or
                    CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaDecryptOaep4096Sha256 =>
                        RSAEncryptionPadding.OaepSHA256,
                    CryptoKeyVersion.Types.CryptoKeyVersionAlgorithm.RsaDecryptOaep4096Sha512 =>
                        RSAEncryptionPadding.OaepSHA512,
                    _ => RSAEncryptionPadding.OaepSHA256
                };

                return rsa.Encrypt(dataKey, padding);
            }

            throw new InvalidOperationException($"Key '{kekId}' does not support key wrapping");
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var cryptoKeyName = GetCryptoKeyName(kekId);
            var key = await _kmsClient!.GetCryptoKeyAsync(cryptoKeyName);

            if (key.Purpose == CryptoKey.Types.CryptoKeyPurpose.EncryptDecrypt)
            {
                // Use Cloud KMS decrypt operation
                var response = await _kmsClient.DecryptAsync(cryptoKeyName, ByteString.CopyFrom(wrappedKey));
                return response.Plaintext.ToByteArray();
            }
            else if (key.Purpose == CryptoKey.Types.CryptoKeyPurpose.AsymmetricDecrypt)
            {
                // Use asymmetric decrypt in Cloud KMS (private key never leaves HSM)
                var keyVersionName = CryptoKeyVersionName.Parse(key.Primary.Name);

                var response = await _kmsClient.AsymmetricDecryptAsync(keyVersionName, ByteString.CopyFrom(wrappedKey));
                return response.Plaintext.ToByteArray();
            }

            throw new InvalidOperationException($"Key '{kekId}' does not support key unwrapping");
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keyRingName = KeyRingName.FromProjectLocationKeyRing(
                _config.ProjectId,
                _config.Location,
                _config.KeyRing);

            var keys = new List<string>();

            var pagedKeys = _kmsClient!.ListCryptoKeysAsync(keyRingName);

            await foreach (var key in pagedKeys.WithCancellation(cancellationToken))
            {
                if (key.Primary?.State == CryptoKeyVersion.Types.CryptoKeyVersionState.Enabled)
                {
                    keys.Add(key.CryptoKeyName.CryptoKeyId);
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

            var cryptoKeyName = GetCryptoKeyName(keyId);
            var key = await _kmsClient!.GetCryptoKeyAsync(cryptoKeyName, cancellationToken);

            // GCP KMS doesn't allow direct key deletion
            // Instead, we schedule the primary version for destruction
            var keyVersionName = CryptoKeyVersionName.Parse(key.Primary.Name);

            await _kmsClient.DestroyCryptoKeyVersionAsync(keyVersionName, cancellationToken);
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            try
            {
                var cryptoKeyName = GetCryptoKeyName(keyId);
                var key = await _kmsClient!.GetCryptoKeyAsync(cryptoKeyName, cancellationToken);

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = key.CreateTime?.ToDateTime() ?? DateTime.UtcNow,
                    LastRotatedAt = key.NextRotationTime?.ToDateTime(),
                    IsActive = key.Primary?.State == CryptoKeyVersion.Types.CryptoKeyVersionState.Enabled &&
                              keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Backend"] = "Google Cloud HSM",
                        ["ProjectId"] = _config.ProjectId,
                        ["Location"] = _config.Location,
                        ["KeyRing"] = _config.KeyRing,
                        ["Purpose"] = key.Purpose.ToString(),
                        ["Algorithm"] = key.Primary?.Algorithm.ToString() ?? "",
                        ["ProtectionLevel"] = key.Primary?.ProtectionLevel.ToString() ?? "",
                        ["PrimaryState"] = key.Primary?.State.ToString() ?? "",
                        ["RotationPeriod"] = key.RotationPeriod?.ToString() ?? "None"
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sets automatic key rotation schedule.
        /// </summary>
        public async Task SetRotationScheduleAsync(string keyId, TimeSpan rotationPeriod, CancellationToken cancellationToken = default)
        {
            var cryptoKeyName = GetCryptoKeyName(keyId);
            var key = await _kmsClient!.GetCryptoKeyAsync(cryptoKeyName, cancellationToken);

            key.RotationPeriod = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(rotationPeriod);
            key.NextRotationTime = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
                DateTime.UtcNow.Add(rotationPeriod));

            var updateMask = new Google.Protobuf.WellKnownTypes.FieldMask
            {
                Paths = { "rotation_period", "next_rotation_time" }
            };

            await _kmsClient.UpdateCryptoKeyAsync(key, updateMask, cancellationToken);
        }

        /// <summary>
        /// Manually rotates the key (creates a new primary version).
        /// </summary>
        public async Task<string> RotateKeyAsync(string keyId, CancellationToken cancellationToken = default)
        {
            var cryptoKeyName = GetCryptoKeyName(keyId);
            var key = await _kmsClient!.GetCryptoKeyAsync(cryptoKeyName, cancellationToken);

            // Create a new key version
            var keyVersion = new CryptoKeyVersion();
            var newVersion = await _kmsClient.CreateCryptoKeyVersionAsync(cryptoKeyName, keyVersion, cancellationToken);

            // Wait for the new version to be enabled
            // Note: In production, you might want to implement proper polling
            await Task.Delay(1000, cancellationToken);

            // Update the primary version
            var updateKey = new CryptoKey
            {
                Name = key.Name,
                Primary = newVersion
            };

            var updateMask = new Google.Protobuf.WellKnownTypes.FieldMask
            {
                Paths = { "primary" }
            };

            var updatedKey = await _kmsClient.UpdateCryptoKeyAsync(updateKey, updateMask, cancellationToken);
            _currentKeyVersion = CryptoKeyVersionName.Parse(updatedKey.Primary.Name);

            return newVersion.Name;
        }

        /// <summary>
        /// Gets all versions of a key.
        /// </summary>
        public async Task<IReadOnlyList<CryptoKeyVersionInfo>> GetKeyVersionsAsync(string keyId, CancellationToken cancellationToken = default)
        {
            var cryptoKeyName = GetCryptoKeyName(keyId);
            var versions = new List<CryptoKeyVersionInfo>();

            var pagedVersions = _kmsClient!.ListCryptoKeyVersionsAsync(cryptoKeyName);

            await foreach (var version in pagedVersions.WithCancellation(cancellationToken))
            {
                versions.Add(new CryptoKeyVersionInfo
                {
                    Name = version.Name,
                    State = version.State.ToString(),
                    Algorithm = version.Algorithm.ToString(),
                    ProtectionLevel = version.ProtectionLevel.ToString(),
                    CreateTime = version.CreateTime?.ToDateTime(),
                    DestroyTime = version.DestroyTime?.ToDateTime(),
                    DestroyEventTime = version.DestroyEventTime?.ToDateTime()
                });
            }

            return versions.AsReadOnly();
        }

        public override void Dispose()
        {
            // KmsClient doesn't require explicit disposal
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for Google Cloud HSM key store strategy.
    /// </summary>
    public class GcpCloudHsmConfig
    {
        public string ProjectId { get; set; } = string.Empty;
        public string Location { get; set; } = "us-central1";
        public string KeyRing { get; set; } = string.Empty;
        public string KeyName { get; set; } = "datawarehouse-master";
        public string ServiceAccountJson { get; set; } = string.Empty;
        public string ProtectionLevel { get; set; } = "HSM";
        public string Algorithm { get; set; } = "GOOGLE_SYMMETRIC_ENCRYPTION";
    }

    /// <summary>
    /// Information about a crypto key version.
    /// </summary>
    public class CryptoKeyVersionInfo
    {
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Algorithm { get; set; } = string.Empty;
        public string ProtectionLevel { get; set; } = string.Empty;
        public DateTime? CreateTime { get; set; }
        public DateTime? DestroyTime { get; set; }
        public DateTime? DestroyEventTime { get; set; }
    }
}
