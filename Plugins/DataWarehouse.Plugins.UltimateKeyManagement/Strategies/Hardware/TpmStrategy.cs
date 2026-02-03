using DataWarehouse.SDK.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tpm2Lib;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hardware
{
    /// <summary>
    /// TPM 2.0 Hardware KeyStore strategy using the Microsoft.TSS (Tpm2Lib) library.
    /// Provides secure key storage using Trusted Platform Module hardware.
    ///
    /// Features:
    /// - Key sealing to PCR (Platform Configuration Register) state
    /// - Key unsealing with PCR validation
    /// - Hardware-bound key protection
    /// - Storage hierarchy key management
    /// - Support for Windows TBS and Linux /dev/tpm0
    ///
    /// Key operations use TPM's storage hierarchy where keys are sealed to the
    /// current PCR state and can only be unsealed when the platform is in the
    /// same configuration (secure boot state, firmware measurements, etc.).
    ///
    /// Requirements:
    /// - TPM 2.0 hardware
    /// - Windows: TPM Base Services (TBS) enabled
    /// - Linux: /dev/tpm0 or /dev/tpmrm0 accessible
    /// </summary>
    public sealed class TpmStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private Tpm2? _tpm;
        private Tpm2Device? _tpmDevice;
        private TpmHandle? _srkHandle;
        private TpmConfig _config = new();
        private string _currentKeyId = "default";
        private readonly Dictionary<string, byte[]> _sealedKeyBlobs = new();
        private readonly SemaphoreSlim _tpmLock = new(1, 1);
        private bool _disposed;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = true,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = false,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 128, // TPM seal limit
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "TPM 2.0",
                ["Standard"] = "TCG TPM 2.0",
                ["SupportsSealing"] = true,
                ["SupportsPcrBinding"] = true,
                ["Platform"] = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows TBS" : "Linux TPM"
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "TPM-RSA-OAEP",
            "TPM-AES-CFB"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration
            if (Configuration.TryGetValue("DevicePath", out var deviceObj) && deviceObj is string device)
                _config.DevicePath = device;
            if (Configuration.TryGetValue("PcrSelection", out var pcrObj) && pcrObj is int[] pcrs)
                _config.PcrSelection = pcrs;
            if (Configuration.TryGetValue("AuthValue", out var authObj) && authObj is string auth)
                _config.AuthValue = auth;
            if (Configuration.TryGetValue("KeyStoragePath", out var storagePath) && storagePath is string path)
                _config.KeyStoragePath = path;

            await Task.Run(() => InitializeTpm(), cancellationToken);

            // Load existing sealed keys from storage
            LoadSealedKeyBlobs();
        }

        private void InitializeTpm()
        {
            // Create TPM device based on platform
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _tpmDevice = new TbsDevice();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Use device path for Linux
                var devicePath = string.IsNullOrEmpty(_config.DevicePath)
                    ? "/dev/tpmrm0"
                    : _config.DevicePath;
                _tpmDevice = new LinuxTpmDevice(devicePath);
            }
            else
            {
                throw new PlatformNotSupportedException("TPM is only supported on Windows and Linux.");
            }

            _tpmDevice.Connect();
            _tpm = new Tpm2(_tpmDevice);

            // Get or create the Storage Root Key (SRK)
            _srkHandle = GetOrCreateSrk();
        }

        private TpmHandle GetOrCreateSrk()
        {
            // Try to read existing SRK from persistent handle 0x81000001 (standard SRK handle)
            var srkPersistentHandle = new TpmHandle(0x81000001);

            try
            {
                // Check if SRK already exists
                _tpm!.ReadPublic(srkPersistentHandle, out _, out _);
                return srkPersistentHandle;
            }
            catch
            {
                // SRK doesn't exist, create it
                return CreateSrk(srkPersistentHandle);
            }
        }

        private TpmHandle CreateSrk(TpmHandle persistentHandle)
        {
            // Define SRK template (RSA 2048, storage key)
            var srkTemplate = new TpmPublic(
                TpmAlgId.Sha256,
                ObjectAttr.Restricted | ObjectAttr.Decrypt |
                ObjectAttr.FixedTPM | ObjectAttr.FixedParent |
                ObjectAttr.NoDA | ObjectAttr.SensitiveDataOrigin | ObjectAttr.UserWithAuth,
                null,
                new RsaParms(
                    new SymDefObject(TpmAlgId.Aes, 128, TpmAlgId.Cfb),
                    new NullAsymScheme(),
                    2048,
                    0),
                new Tpm2bPublicKeyRsa());

            // Create the SRK under the storage hierarchy
            var srkCreationData = _tpm!.CreatePrimary(
                TpmRh.Owner,
                new SensitiveCreate(null, null),
                srkTemplate,
                null,
                new PcrSelection[0],
                out TpmPublic srkPublic,
                out CreationData _,
                out byte[] _,
                out TkCreation _);

            // Make it persistent
            _tpm.EvictControl(TpmRh.Owner, srkCreationData, persistentHandle);
            _tpm.FlushContext(srkCreationData);

            return persistentHandle;
        }

        public override async Task<string> GetCurrentKeyIdAsync()
        {
            return await Task.FromResult(_currentKeyId);
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            await _tpmLock.WaitAsync(cancellationToken);
            try
            {
                if (_tpm == null || _disposed)
                    return false;

                // Try to get TPM properties
                _tpm.GetCapability(Cap.TpmProperties, (uint)Pt.FamilyIndicator, 1, out ICapabilitiesUnion caps);
                return caps != null;
            }
            catch
            {
                return false;
            }
            finally
            {
                _tpmLock.Release();
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _tpmLock.WaitAsync();
            try
            {
                if (!_sealedKeyBlobs.TryGetValue(keyId, out var sealedBlob))
                {
                    throw new KeyNotFoundException($"Key '{keyId}' not found in TPM storage.");
                }

                // Unseal the key
                return UnsealKey(sealedBlob);
            }
            finally
            {
                _tpmLock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _tpmLock.WaitAsync();
            try
            {
                // Seal the key to current PCR state
                var sealedBlob = SealKey(keyData);

                _sealedKeyBlobs[keyId] = sealedBlob;
                _currentKeyId = keyId;

                // Persist sealed blobs to file storage
                PersistSealedKeyBlobs();
            }
            finally
            {
                _tpmLock.Release();
            }
        }

        private byte[] SealKey(byte[] data)
        {
            // Create sealing object template (keyedhash for sealing)
            var sealTemplate = new TpmPublic(
                TpmAlgId.Sha256,
                ObjectAttr.FixedTPM | ObjectAttr.FixedParent | ObjectAttr.NoDA | ObjectAttr.UserWithAuth,
                null,
                new KeyedhashParms(new NullSchemeKeyedhash()),
                new Tpm2bDigestKeyedhash());

            // Get PCR selection for sealing policy
            var pcrSelection = CreatePcrSelection();

            // Read current PCR values
            _tpm!.PcrRead(pcrSelection, out _, out Tpm2bDigest[] pcrValues);

            // Start a trial policy session to compute the policy digest
            var trialSession = _tpm.StartAuthSession(
                TpmRh.Null, TpmRh.Null,
                Globs.GetRandomBytes(16),
                null,
                TpmSe.Trial,
                new SymDef(),
                TpmAlgId.Sha256,
                out _);

            try
            {
                // Compute the concatenated PCR digest
                var pcrDigest = ComputePcrDigest(pcrValues);

                // Add PCR policy
                _tpm.PolicyPCR(trialSession, pcrDigest, pcrSelection);

                // Get the policy digest
                var policyDigest = _tpm.PolicyGetDigest(trialSession);

                // Update template with policy
                sealTemplate = new TpmPublic(
                    TpmAlgId.Sha256,
                    ObjectAttr.FixedTPM | ObjectAttr.FixedParent | ObjectAttr.NoDA | ObjectAttr.UserWithAuth,
                    policyDigest,
                    new KeyedhashParms(new NullSchemeKeyedhash()),
                    new Tpm2bDigestKeyedhash());

                // Create sensitive data with the key
                var authValue = string.IsNullOrEmpty(_config.AuthValue)
                    ? null
                    : Encoding.UTF8.GetBytes(_config.AuthValue);
                var sensitive = new SensitiveCreate(authValue, data);

                // Create the sealed object
                var sealedPrivate = _tpm.Create(
                    _srkHandle!,
                    sensitive,
                    sealTemplate,
                    null,
                    new PcrSelection[0],
                    out TpmPublic sealedPublic,
                    out CreationData _,
                    out byte[] _,
                    out TkCreation _);

                // Serialize the sealed object (public + private)
                using var ms = new MemoryStream();
                using var writer = new BinaryWriter(ms);

                var publicBytes = sealedPublic.GetTpmRepresentation();
                var privateBytes = sealedPrivate.GetTpmRepresentation();

                writer.Write(publicBytes.Length);
                writer.Write(publicBytes);
                writer.Write(privateBytes.Length);
                writer.Write(privateBytes);

                return ms.ToArray();
            }
            finally
            {
                _tpm.FlushContext(trialSession);
            }
        }

        private byte[] UnsealKey(byte[] sealedBlob)
        {
            // Deserialize sealed object
            using var ms = new MemoryStream(sealedBlob);
            using var reader = new BinaryReader(ms);

            var publicLen = reader.ReadInt32();
            var publicBytes = reader.ReadBytes(publicLen);
            var privateLen = reader.ReadInt32();
            var privateBytes = reader.ReadBytes(privateLen);

            var sealedPublic = Marshaller.FromTpmRepresentation<TpmPublic>(publicBytes);
            var sealedPrivate = Marshaller.FromTpmRepresentation<TpmPrivate>(privateBytes);

            // Load the sealed object
            var loadedHandle = _tpm!.Load(_srkHandle!, sealedPrivate, sealedPublic);

            try
            {
                // Start policy session
                var policySession = _tpm.StartAuthSession(
                    TpmRh.Null, TpmRh.Null,
                    Globs.GetRandomBytes(16),
                    null,
                    TpmSe.Policy,
                    new SymDef(),
                    TpmAlgId.Sha256,
                    out _);

                try
                {
                    // Get PCR selection
                    var pcrSelection = CreatePcrSelection();

                    // Read current PCR values
                    _tpm.PcrRead(pcrSelection, out _, out Tpm2bDigest[] pcrValues);

                    // Compute PCR digest
                    var pcrDigest = ComputePcrDigest(pcrValues);

                    // Satisfy the PCR policy
                    _tpm.PolicyPCR(policySession, pcrDigest, pcrSelection);

                    // Set auth for unsealing
                    var authValue = string.IsNullOrEmpty(_config.AuthValue)
                        ? Array.Empty<byte>()
                        : Encoding.UTF8.GetBytes(_config.AuthValue);

                    loadedHandle.Auth = authValue;

                    // Unseal using policy session
                    var authSession = new AuthSession(policySession);
                    var unsealedData = _tpm[authSession].Unseal(loadedHandle);
                    return unsealedData;
                }
                finally
                {
                    _tpm.FlushContext(policySession);
                }
            }
            finally
            {
                _tpm.FlushContext(loadedHandle);
            }
        }

        private byte[] ComputePcrDigest(Tpm2bDigest[] pcrValues)
        {
            // Concatenate all PCR values and hash them
            using var ms = new MemoryStream();
            foreach (var pcr in pcrValues)
            {
                ms.Write(pcr.buffer, 0, pcr.buffer.Length);
            }

            return SHA256.HashData(ms.ToArray());
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _tpmLock.WaitAsync();
            try
            {
                // Load the KEK from storage
                if (!_sealedKeyBlobs.TryGetValue(kekId, out var kekBlob))
                {
                    throw new KeyNotFoundException($"KEK '{kekId}' not found in TPM storage.");
                }

                // For TPM, we use the SRK to wrap data directly
                // The wrapped key can only be unwrapped with the same SRK

                // Create a random IV
                var iv = Globs.GetRandomBytes(16);

                // Use TPM to encrypt the data key using EncryptDecrypt2
                var encrypted = _tpm!.EncryptDecrypt2(
                    _srkHandle!,
                    dataKey,
                    0, // decrypt = false (encrypt)
                    TpmAlgId.Cfb,
                    iv,
                    out byte[] ivOut);

                // Combine IV and encrypted data
                var result = new byte[iv.Length + encrypted.Length];
                Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
                Buffer.BlockCopy(encrypted, 0, result, iv.Length, encrypted.Length);

                return result;
            }
            finally
            {
                _tpmLock.Release();
            }
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _tpmLock.WaitAsync();
            try
            {
                if (wrappedKey.Length < 17)
                {
                    throw new ArgumentException("Wrapped key data is too short.", nameof(wrappedKey));
                }

                // Extract IV and encrypted data
                var iv = new byte[16];
                Buffer.BlockCopy(wrappedKey, 0, iv, 0, 16);

                var encrypted = new byte[wrappedKey.Length - 16];
                Buffer.BlockCopy(wrappedKey, 16, encrypted, 0, encrypted.Length);

                // Use TPM to decrypt
                var decrypted = _tpm!.EncryptDecrypt2(
                    _srkHandle!,
                    encrypted,
                    1, // decrypt = true
                    TpmAlgId.Cfb,
                    iv,
                    out _);

                return decrypted;
            }
            finally
            {
                _tpmLock.Release();
            }
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);
            await Task.CompletedTask;
            return _sealedKeyBlobs.Keys.ToList().AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete TPM keys.");
            }

            await _tpmLock.WaitAsync(cancellationToken);
            try
            {
                if (_sealedKeyBlobs.Remove(keyId))
                {
                    PersistSealedKeyBlobs();
                }
            }
            finally
            {
                _tpmLock.Release();
            }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!_sealedKeyBlobs.ContainsKey(keyId))
            {
                return null;
            }

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = DateTime.UtcNow,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["Backend"] = "TPM 2.0",
                    ["SealedToPcr"] = _config.PcrSelection,
                    ["StorageHandle"] = _srkHandle?.handle ?? 0
                }
            });
        }

        private PcrSelection[] CreatePcrSelection()
        {
            if (_config.PcrSelection.Length == 0)
            {
                // Default: PCR 0, 2, 4, 7 (BIOS, option ROMs, boot manager, secure boot)
                return new PcrSelection[]
                {
                    new PcrSelection(TpmAlgId.Sha256, new uint[] { 0, 2, 4, 7 })
                };
            }

            return new PcrSelection[]
            {
                new PcrSelection(TpmAlgId.Sha256, _config.PcrSelection.Select(p => (uint)p).ToArray())
            };
        }

        private void LoadSealedKeyBlobs()
        {
            var storagePath = GetKeyStoragePath();
            if (!File.Exists(storagePath))
                return;

            try
            {
                var json = File.ReadAllText(storagePath);
                var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (stored != null)
                {
                    foreach (var kvp in stored)
                    {
                        _sealedKeyBlobs[kvp.Key] = Convert.FromBase64String(kvp.Value);
                    }
                }
            }
            catch
            {
                // Ignore errors loading existing keys
            }
        }

        private void PersistSealedKeyBlobs()
        {
            var storagePath = GetKeyStoragePath();
            var dir = Path.GetDirectoryName(storagePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var toStore = _sealedKeyBlobs.ToDictionary(
                kvp => kvp.Key,
                kvp => Convert.ToBase64String(kvp.Value));

            var json = JsonSerializer.Serialize(toStore);
            File.WriteAllText(storagePath, json);
        }

        private string GetKeyStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.KeyStoragePath))
                return _config.KeyStoragePath;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "tpm-keys.json");
        }

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _tpmLock.Wait();
            try
            {
                _tpm?.Dispose();
                _tpmDevice?.Dispose();
            }
            finally
            {
                _tpmLock.Release();
                _tpmLock.Dispose();
            }

            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for TPM 2.0 key store strategy.
    /// </summary>
    public class TpmConfig
    {
        /// <summary>
        /// Device path for Linux TPM device (e.g., "/dev/tpmrm0").
        /// Ignored on Windows where TBS is used.
        /// </summary>
        public string? DevicePath { get; set; }

        /// <summary>
        /// PCR indices to bind keys to. Default: 0, 2, 4, 7.
        /// Keys sealed to these PCRs can only be unsealed when PCR values match.
        /// </summary>
        public int[] PcrSelection { get; set; } = { 0, 2, 4, 7 };

        /// <summary>
        /// Authorization value for sealed keys.
        /// </summary>
        public string? AuthValue { get; set; }

        /// <summary>
        /// Path to store sealed key blobs. Defaults to local app data.
        /// </summary>
        public string? KeyStoragePath { get; set; }
    }
}
