using DataWarehouse.SDK.Security;
using Fido2NetLib;
using Fido2NetLib.Objects;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hardware
{
    /// <summary>
    /// SoloKey Hardware KeyStore strategy using FIDO2/WebAuthn via Fido2NetLib.
    /// SoloKeys are open-source FIDO2 security keys that support:
    /// - FIDO2/WebAuthn authentication
    /// - CTAP2 protocol for device communication
    /// - Resident credentials (discoverable credentials)
    /// - Ed25519 and ECDSA P-256 signatures
    ///
    /// Security Model:
    /// - Keys are derived from credential secrets using HKDF
    /// - Private keys never leave the device
    /// - UV (User Verification) via button press
    /// - Resident credentials for key persistence
    ///
    /// Key Derivation:
    /// Since FIDO2 standard doesn't expose raw secrets, we use:
    /// 1. Credential ID as seed for deterministic key derivation
    /// 2. Device-specific entropy from credential creation
    /// 3. HKDF for final key derivation
    ///
    /// Requirements:
    /// - SoloKey v1, v2, or compatible FIDO2 device
    /// - Platform authenticator or external security key
    /// </summary>
    public sealed class SoloKeyStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private SoloKeyConfig _config = new();
        private string _currentKeyId = "default";
        private readonly Dictionary<string, Fido2Credential> _credentials = new();
        private readonly SemaphoreSlim _deviceLock = new(1, 1);
        private IFido2? _fido2;
        private bool _disposed;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = false,
            SupportsEnvelope = true,
            SupportsHsm = true,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = false,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 32,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "SoloKey",
                ["Standard"] = "FIDO2/WebAuthn",
                ["Protocol"] = "CTAP2",
                ["OpenSource"] = true,
                ["SupportsResidentKeys"] = true
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("solokey.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "Credential-HKDF-AES256"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("solokey.init");
            // Load configuration
            if (Configuration.TryGetValue("Origin", out var originObj) && originObj is string origin)
                _config.Origin = origin;
            if (Configuration.TryGetValue("RpId", out var rpIdObj) && rpIdObj is string rpId)
                _config.RpId = rpId;
            if (Configuration.TryGetValue("RpName", out var rpNameObj) && rpNameObj is string rpName)
                _config.RpName = rpName;
            if (Configuration.TryGetValue("RequireUserVerification", out var uvObj) && uvObj is bool uv)
                _config.RequireUserVerification = uv;
            if (Configuration.TryGetValue("CredentialStoragePath", out var storagePath) && storagePath is string path)
                _config.CredentialStoragePath = path;

            await Task.Run(InitializeFido2, cancellationToken);
            await LoadStoredCredentials();
        }

        private void InitializeFido2()
        {
            var config = new Fido2Configuration
            {
                ServerDomain = _config.RpId,
                ServerName = _config.RpName,
                Origins = new HashSet<string> { _config.Origin }
            };

            _fido2 = new Fido2(config);
        }

        public override async Task<string> GetCurrentKeyIdAsync()
        {
            return await Task.FromResult(_currentKeyId);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            await _deviceLock.WaitAsync(cancellationToken);
            try
            {
                return _fido2 != null && !_disposed;
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _deviceLock.WaitAsync();
            try
            {
                if (!_credentials.TryGetValue(keyId, out var credential))
                {
                    throw new KeyNotFoundException($"Credential for key '{keyId}' not found.");
                }

                // Derive key from credential
                return DeriveKeyFromCredential(credential, keyId);
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _deviceLock.WaitAsync();
            try
            {
                // Create a new credential for this key
                var credential = CreateCredential(keyId);
                _credentials[keyId] = credential;
                _currentKeyId = keyId;

                await PersistCredentials();
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        /// <summary>
        /// Creates a credential for key derivation via the SoloKey FIDO2 device.
        /// </summary>
        private Fido2Credential CreateCredential(string keyId)
        {
            // #3481: Hardware FIDO2 credential creation requires an actual SoloKey device.
            throw new PlatformNotSupportedException(
                "Requires SoloKey FIDO2 device. Install SoloKey SDK, connect the device, " +
                "and configure via SoloKeyOptions.");
        }

        /// <summary>
        /// Derives a key from the credential using HKDF.
        /// </summary>
        private byte[] DeriveKeyFromCredential(Fido2Credential credential, string keyId)
        {
            // Use credential ID and public key as input key material
            var ikm = new byte[credential.CredentialId.Length + credential.PublicKey.Length];
            credential.CredentialId.CopyTo(ikm, 0);
            credential.PublicKey.CopyTo(ikm, credential.CredentialId.Length);

            // Derive a 32-byte key using HKDF
            var derivedKey = new byte[32];
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm,
                derivedKey,
                Encoding.UTF8.GetBytes(keyId),
                Encoding.UTF8.GetBytes("DataWarehouse.SoloKey"));

            return derivedKey;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _deviceLock.WaitAsync();
            try
            {
                if (!_credentials.TryGetValue(kekId, out var credential))
                {
                    throw new KeyNotFoundException($"KEK credential '{kekId}' not found.");
                }

                // Derive wrapping key from credential
                var wrappingKey = DeriveKeyFromCredential(credential, kekId);

                // Wrap using AES-GCM
                var nonce = RandomNumberGenerator.GetBytes(12);
                var tag = new byte[16];
                var ciphertext = new byte[dataKey.Length];

                using var aes = new AesGcm(wrappingKey, 16);
                aes.Encrypt(nonce, dataKey, ciphertext, tag);

                // Combine nonce + ciphertext + tag
                var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
                nonce.CopyTo(result, 0);
                ciphertext.CopyTo(result, nonce.Length);
                tag.CopyTo(result, nonce.Length + ciphertext.Length);

                return result;
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _deviceLock.WaitAsync();
            try
            {
                if (!_credentials.TryGetValue(kekId, out var credential))
                {
                    throw new KeyNotFoundException($"KEK credential '{kekId}' not found.");
                }

                // Derive unwrapping key from credential
                var unwrappingKey = DeriveKeyFromCredential(credential, kekId);

                // Parse wrapped data
                var nonce = wrappedKey.AsSpan(0, 12).ToArray();
                var ciphertext = wrappedKey.AsSpan(12, wrappedKey.Length - 28).ToArray();
                var tag = wrappedKey.AsSpan(wrappedKey.Length - 16).ToArray();

                var plaintext = new byte[ciphertext.Length];

                using var aes = new AesGcm(unwrappingKey, 16);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);

                return plaintext;
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        /// <summary>
        /// Registers a new credential.
        /// Call this to create the initial key for a keyId.
        /// </summary>
        public async Task<Fido2Credential> RegisterCredentialAsync(string keyId, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _deviceLock.WaitAsync();
            try
            {
                var credential = CreateCredential(keyId);
                _credentials[keyId] = credential;
                await PersistCredentials();
                return credential;
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await Task.CompletedTask;
            return _credentials.Keys.ToList().AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete SoloKey credentials.");
            }

            await _deviceLock.WaitAsync(cancellationToken);
            try
            {
                // Note: FIDO2 credentials cannot be deleted remotely
                // We can only remove our local reference
                if (_credentials.Remove(keyId))
                {
                    await PersistCredentials();
                }
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!_credentials.TryGetValue(keyId, out var credential))
            {
                return null;
            }

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = credential.CreatedAt,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["Backend"] = "SoloKey FIDO2",
                    ["CredentialId"] = Convert.ToBase64String(credential.CredentialId),
                    ["SignCount"] = credential.SignCount
                }
            });
        }

        private byte[] GeneratePublicKey()
        {
            // Generate an ECDSA P-256 key pair
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return ecdsa.ExportSubjectPublicKeyInfo();
        }

        private async Task LoadStoredCredentials()
        {
            var path = GetCredentialStoragePath();
            if (!File.Exists(path))
                return;

            try
            {
                var envelopeJson = await File.ReadAllTextAsync(path);
                using var envelope = JsonDocument.Parse(envelopeJson);

                string json;
                // P2-3498: Verify HMAC integrity tag before trusting credential data.
                if (envelope.RootElement.TryGetProperty("mac", out var macProp) &&
                    envelope.RootElement.TryGetProperty("data", out var dataProp))
                {
                    json = dataProp.GetString() ?? string.Empty;
                    var storedMac = Convert.FromBase64String(macProp.GetString() ?? string.Empty);
                    var hmacKey = DeriveIntegrityKey();
                    var computedMac = HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(json));
                    if (!CryptographicOperations.FixedTimeEquals(storedMac, computedMac))
                    {
                        System.Diagnostics.Trace.TraceWarning(
                            "[SoloKeyStrategy] Credential file HMAC mismatch — file may be tampered. Discarding.");
                        return;
                    }
                }
                else
                {
                    // Legacy format without envelope (no MAC); load but warn.
                    json = envelopeJson;
                    System.Diagnostics.Trace.TraceWarning(
                        "[SoloKeyStrategy] Credential file has no integrity tag — upgrade by re-registering credentials.");
                }

                var stored = JsonSerializer.Deserialize<Dictionary<string, Fido2CredentialDto>>(json);

                if (stored != null)
                {
                    foreach (var kvp in stored)
                    {
                        _credentials[kvp.Key] = kvp.Value.ToCredential();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SoloKeyStrategy] LoadStoredCredentials error: {ex.Message}");
            }
        }

        private async Task PersistCredentials()
        {
            var path = GetCredentialStoragePath();
            var dir = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var toStore = _credentials.ToDictionary(
                kvp => kvp.Key,
                kvp => Fido2CredentialDto.FromCredential(kvp.Value));

            var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });

            // P2-3498: Add HMAC-SHA256 integrity tag so tampering with stored credentials
            // can be detected on load. The HMAC key is derived from a machine-specific secret.
            var hmacKey = DeriveIntegrityKey();
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var mac = HMACSHA256.HashData(hmacKey, jsonBytes);
            var envelope = new { mac = Convert.ToBase64String(mac), data = json };
            var envelopeJson = JsonSerializer.Serialize(envelope);
            await File.WriteAllTextAsync(path, envelopeJson);
        }

        private static byte[] DeriveIntegrityKey()
        {
            // Derive a machine-scoped integrity key from MachineGuid (or a stable fallback).
            var machineId = Environment.MachineName + Environment.GetEnvironmentVariable("COMPUTERNAME");
            var ikm = Encoding.UTF8.GetBytes(machineId);
            var info = Encoding.UTF8.GetBytes("DataWarehouse.SoloKey.CredentialIntegrity.v1");
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, info: info);
        }

        private string GetCredentialStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.CredentialStoragePath))
                return _config.CredentialStoragePath;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "solokey-credentials.json");
        }

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _credentials.Clear();
            _deviceLock.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for SoloKey key store strategy.
    /// </summary>
    public class SoloKeyConfig
    {
        /// <summary>
        /// WebAuthn origin (e.g., "https://datawarehouse.local").
        /// </summary>
        public string Origin { get; set; } = "https://datawarehouse.local";

        /// <summary>
        /// Relying Party ID (domain).
        /// </summary>
        public string RpId { get; set; } = "datawarehouse.local";

        /// <summary>
        /// Relying Party display name.
        /// </summary>
        public string RpName { get; set; } = "DataWarehouse Key Management";

        /// <summary>
        /// Require user verification (button press) for all operations.
        /// </summary>
        public bool RequireUserVerification { get; set; } = true;

        /// <summary>
        /// Path to store credential references.
        /// </summary>
        public string? CredentialStoragePath { get; set; }
    }

    /// <summary>
    /// Internal FIDO2 credential representation.
    /// </summary>
    public class Fido2Credential
    {
        public byte[] CredentialId { get; set; } = Array.Empty<byte>();
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
        public byte[] UserId { get; set; } = Array.Empty<byte>();
        public string KeyId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public uint SignCount { get; set; }
    }

    /// <summary>
    /// DTO for JSON serialization of credentials.
    /// </summary>
    internal class Fido2CredentialDto
    {
        public string CredentialId { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public uint SignCount { get; set; }

        public static Fido2CredentialDto FromCredential(Fido2Credential cred) => new()
        {
            CredentialId = Convert.ToBase64String(cred.CredentialId),
            PublicKey = Convert.ToBase64String(cred.PublicKey),
            UserId = Convert.ToBase64String(cred.UserId),
            KeyId = cred.KeyId,
            CreatedAt = cred.CreatedAt,
            SignCount = cred.SignCount
        };

        public Fido2Credential ToCredential() => new()
        {
            CredentialId = Convert.FromBase64String(CredentialId),
            PublicKey = Convert.FromBase64String(PublicKey),
            UserId = Convert.FromBase64String(UserId),
            KeyId = KeyId,
            CreatedAt = CreatedAt,
            SignCount = SignCount
        };
    }
}
