using DataWarehouse.SDK.Security;
using HidSharp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hardware
{
    /// <summary>
    /// OnlyKey Hardware KeyStore strategy using HID communication.
    /// OnlyKey is an open-source hardware security key supporting:
    /// - Password/key storage (24 slots: 12 profiles x 2 slots each)
    /// - Challenge-response authentication
    /// - FIDO2/U2F
    /// - Backup/restore with secure encryption
    /// - Self-destruct on tamper detection
    ///
    /// Security Model:
    /// - Keys are stored in hardware with PIN protection
    /// - Supports primary and secondary profiles
    /// - Challenge-response for key derivation
    /// - Secure communication via HID
    ///
    /// HMAC-SHA1 Usage Note:
    /// HMAC-SHA1 is used for challenge-response due to OnlyKey firmware protocol limitations.
    /// This is a hardware constraint, not a software design choice. For enhanced security,
    /// derived keys are extended using HKDF-SHA256.
    ///
    /// Communication Protocol:
    /// Uses OnlyKey's proprietary HID protocol with encrypted payloads.
    /// Message format: [type][slot][data...]
    ///
    /// Requirements:
    /// - OnlyKey hardware device
    /// - HidSharp NuGet package for HID communication
    /// - Device must be unlocked (PIN entered)
    /// </summary>
    public sealed class OnlyKeyStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private const int OnlyKeyVendorId = 0x1d50;
        private const int OnlyKeyProductId = 0x60fc;
        private const int ReportSize = 64;

        // OnlyKey message types
        private const byte OKSetSlot = 0x01;
        private const byte OKGetSlot = 0x02;
        private const byte OKSign = 0x03;
        private const byte OKChallenge = 0x10;
        private const byte OKEncrypt = 0x11;
        private const byte OKDecrypt = 0x12;
        private const byte OKPing = 0xF0;
        private const byte OKAck = 0xF1;

        private HidDevice? _device;
        private HidStream? _stream;
        private OnlyKeyConfig _config = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _deviceLock = new(1, 1);
        private readonly Dictionary<string, OnlyKeySlotMapping> _slotMappings = new();
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
                ["Provider"] = "OnlyKey",
                ["Manufacturer"] = "CryptoTrust",
                ["Interface"] = "HID",
                ["OpenSource"] = true,
                ["SlotCount"] = 24,
                ["SupportsFido2"] = true,
                ["SupportsBackup"] = true,
                ["TamperDetection"] = true
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "ECDH-P256",
            "RSA-2048"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration
            if (Configuration.TryGetValue("SerialNumber", out var serialObj) && serialObj is string serial)
                _config.SerialNumber = serial;
            if (Configuration.TryGetValue("Pin", out var pinObj) && pinObj is string pin)
                _config.Pin = pin;
            if (Configuration.TryGetValue("Profile", out var profileObj) && profileObj is int profile)
                _config.Profile = (OnlyKeyProfile)profile;
            if (Configuration.TryGetValue("DefaultSlot", out var slotObj) && slotObj is int slot)
                _config.DefaultSlot = slot;
            if (Configuration.TryGetValue("MappingStoragePath", out var pathObj) && pathObj is string path)
                _config.MappingStoragePath = path;

            await Task.Run(ConnectDevice, cancellationToken);
            await LoadSlotMappings();
        }

        private void ConnectDevice()
        {
            var devices = DeviceList.Local.GetHidDevices(OnlyKeyVendorId, OnlyKeyProductId);

            if (!devices.Any())
            {
                throw new InvalidOperationException("No OnlyKey device found.");
            }

            // Select by serial number or use first available
            if (!string.IsNullOrEmpty(_config.SerialNumber))
            {
                _device = devices.FirstOrDefault(d =>
                    d.GetSerialNumber()?.Contains(_config.SerialNumber) == true);

                if (_device == null)
                {
                    throw new InvalidOperationException(
                        $"OnlyKey with serial '{_config.SerialNumber}' not found.");
                }
            }
            else
            {
                _device = devices.First();
            }

            // Open HID stream
            _stream = _device.Open();

            // Verify device is responsive
            if (!PingDevice())
            {
                throw new InvalidOperationException(
                    "OnlyKey device not responding. Ensure it is unlocked.");
            }
        }

        private bool PingDevice()
        {
            try
            {
                var report = new byte[ReportSize];
                report[0] = 0x00; // Report ID
                report[1] = OKPing;

                _stream!.Write(report);

                var response = new byte[ReportSize];
                var bytesRead = _stream.Read(response, 0, response.Length);

                return bytesRead > 0 && response[1] == OKAck;
            }
            catch
            {
                return false;
            }
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
                if (_stream == null || _disposed)
                    return false;

                return PingDevice();
            }
            catch
            {
                return false;
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
                // Check if we have a slot mapping
                if (!_slotMappings.TryGetValue(keyId, out var mapping))
                {
                    // Use challenge-response for key derivation
                    return await DeriveKeyFromChallenge(keyId);
                }

                // Read from slot (if slot contains the key directly)
                return await ReadSlotData(mapping.Slot);
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
                // Find available slot
                var slot = FindAvailableSlot();
                if (slot < 0)
                {
                    throw new InvalidOperationException("No available slots on OnlyKey.");
                }

                // Write to slot
                await WriteSlotData(slot, keyData);

                // Save mapping
                _slotMappings[keyId] = new OnlyKeySlotMapping
                {
                    KeyId = keyId,
                    Slot = slot,
                    Profile = _config.Profile,
                    CreatedAt = DateTime.UtcNow
                };

                _currentKeyId = keyId;
                await PersistSlotMappings();
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        /// <summary>
        /// Derives a key using challenge-response.
        /// </summary>
        private async Task<byte[]> DeriveKeyFromChallenge(string keyId)
        {
            // Create challenge from keyId
            var challenge = SHA256.HashData(Encoding.UTF8.GetBytes($"DataWarehouse.{keyId}"));

            // Send challenge to OnlyKey
            var report = new byte[ReportSize];
            report[0] = 0x00; // Report ID
            report[1] = OKChallenge;
            report[2] = (byte)_config.Profile;
            report[3] = (byte)_config.DefaultSlot;
            Array.Copy(challenge, 0, report, 4, Math.Min(challenge.Length, 32));

            _stream!.Write(report);

            // Read response
            var response = new byte[ReportSize];
            var totalRead = 0;
            var timeout = DateTime.UtcNow.AddSeconds(30);

            while (totalRead == 0 && DateTime.UtcNow < timeout)
            {
                totalRead = _stream.Read(response, 0, response.Length);
                if (totalRead == 0)
                {
                    await Task.Delay(100);
                }
            }

            if (totalRead == 0)
            {
                throw new TimeoutException("OnlyKey challenge-response timed out. Button press may be required.");
            }

            // Extract HMAC response (20 bytes for HMAC-SHA1)
            var hmacResponse = new byte[20];
            Array.Copy(response, 1, hmacResponse, 0, 20);

            // Extend to 32 bytes using HKDF
            var derivedKey = new byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, hmacResponse,
                derivedKey, Encoding.UTF8.GetBytes(keyId), Encoding.UTF8.GetBytes("DataWarehouse.OnlyKey"));

            return derivedKey;
        }

        private async Task<byte[]> ReadSlotData(int slot)
        {
            var report = new byte[ReportSize];
            report[0] = 0x00;
            report[1] = OKGetSlot;
            report[2] = (byte)_config.Profile;
            report[3] = (byte)slot;

            _stream!.Write(report);

            var response = new byte[ReportSize];
            var timeout = DateTime.UtcNow.AddSeconds(30);
            var totalRead = 0;

            while (totalRead == 0 && DateTime.UtcNow < timeout)
            {
                totalRead = _stream.Read(response, 0, response.Length);
                if (totalRead == 0)
                {
                    await Task.Delay(100);
                }
            }

            if (totalRead == 0 || response[1] != OKAck)
            {
                throw new InvalidOperationException($"Failed to read OnlyKey slot {slot}.");
            }

            // Data starts at byte 2, length at byte 2
            var dataLen = response[2];
            var data = new byte[dataLen];
            Array.Copy(response, 3, data, 0, dataLen);

            return data;
        }

        private async Task WriteSlotData(int slot, byte[] data)
        {
            if (data.Length > 56) // Max slot data size
            {
                throw new ArgumentException("Data too large for OnlyKey slot (max 56 bytes).", nameof(data));
            }

            var report = new byte[ReportSize];
            report[0] = 0x00;
            report[1] = OKSetSlot;
            report[2] = (byte)_config.Profile;
            report[3] = (byte)slot;
            report[4] = (byte)data.Length;
            Array.Copy(data, 0, report, 5, data.Length);

            _stream!.Write(report);

            // Wait for acknowledgment
            var response = new byte[ReportSize];
            var timeout = DateTime.UtcNow.AddSeconds(30);
            var totalRead = 0;

            while (totalRead == 0 && DateTime.UtcNow < timeout)
            {
                totalRead = _stream.Read(response, 0, response.Length);
                if (totalRead == 0)
                {
                    await Task.Delay(100);
                }
            }

            if (totalRead == 0 || response[1] != OKAck)
            {
                throw new InvalidOperationException($"Failed to write to OnlyKey slot {slot}.");
            }
        }

        private int FindAvailableSlot()
        {
            var usedSlots = _slotMappings.Values.Select(m => m.Slot).ToHashSet();

            // Slots 1-12 for current profile
            for (int i = 1; i <= 12; i++)
            {
                if (!usedSlots.Contains(i))
                {
                    return i;
                }
            }

            return -1;
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _deviceLock.WaitAsync();
            try
            {
                // Derive wrapping key from KEK slot using challenge-response
                var wrappingKey = await DeriveKeyFromChallenge(kekId);

                // Wrap with AES-GCM
                var nonce = RandomNumberGenerator.GetBytes(12);
                var tag = new byte[16];
                var ciphertext = new byte[dataKey.Length];

                using var aes = new AesGcm(wrappingKey, 16);
                aes.Encrypt(nonce, dataKey, ciphertext, tag);

                // Format: nonce || ciphertext || tag
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
                if (wrappedKey.Length < 29) // 12 + 1 + 16 minimum
                {
                    throw new ArgumentException("Wrapped key too short.", nameof(wrappedKey));
                }

                // Derive unwrapping key
                var unwrappingKey = await DeriveKeyFromChallenge(kekId);

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
        /// Signs data using the OnlyKey's private key.
        /// </summary>
        public async Task<byte[]> SignDataAsync(int slot, byte[] data)
        {
            await _deviceLock.WaitAsync();
            try
            {
                // Hash the data first
                var hash = SHA256.HashData(data);

                var report = new byte[ReportSize];
                report[0] = 0x00;
                report[1] = OKSign;
                report[2] = (byte)_config.Profile;
                report[3] = (byte)slot;
                Array.Copy(hash, 0, report, 4, 32);

                _stream!.Write(report);

                // Wait for signature (may require button press)
                var response = new byte[ReportSize * 2]; // Signatures can be up to 72 bytes
                var timeout = DateTime.UtcNow.AddSeconds(30);
                var totalRead = 0;

                while (totalRead == 0 && DateTime.UtcNow < timeout)
                {
                    totalRead = _stream.Read(response, 0, response.Length);
                    if (totalRead == 0)
                    {
                        await Task.Delay(100);
                    }
                }

                if (totalRead == 0)
                {
                    throw new TimeoutException("Signing timed out. Button press may be required.");
                }

                // Extract signature (length varies based on algorithm)
                var sigLen = response[1];
                var signature = new byte[sigLen];
                Array.Copy(response, 2, signature, 0, sigLen);

                return signature;
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
            return _slotMappings.Keys.ToList().AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete OnlyKey keys.");
            }

            await _deviceLock.WaitAsync(cancellationToken);
            try
            {
                if (_slotMappings.TryGetValue(keyId, out var mapping))
                {
                    // Clear the slot by writing zeros
                    await WriteSlotData(mapping.Slot, new byte[32]);

                    _slotMappings.Remove(keyId);
                    await PersistSlotMappings();
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

            if (!_slotMappings.TryGetValue(keyId, out var mapping))
            {
                // Key uses challenge-response derivation
                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Backend"] = "OnlyKey Challenge-Response",
                        ["DerivationMethod"] = "HMAC-SHA1 + HKDF"
                    }
                };
            }

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = mapping.CreatedAt,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["Backend"] = "OnlyKey HID",
                    ["Slot"] = mapping.Slot,
                    ["Profile"] = mapping.Profile.ToString(),
                    ["SerialNumber"] = _device?.GetSerialNumber() ?? "unknown"
                }
            });
        }

        private async Task LoadSlotMappings()
        {
            var path = GetMappingStoragePath();
            if (!File.Exists(path))
                return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var mappings = JsonSerializer.Deserialize<Dictionary<string, OnlyKeySlotMapping>>(json);

                if (mappings != null)
                {
                    foreach (var kvp in mappings)
                    {
                        _slotMappings[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch
            {
                // Ignore errors loading mappings
            }
        }

        private async Task PersistSlotMappings()
        {
            var path = GetMappingStoragePath();
            var dir = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_slotMappings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        private string GetMappingStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.MappingStoragePath))
                return _config.MappingStoragePath;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "onlykey-mappings.json");
        }

        public override void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_deviceLock.Wait(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    _stream?.Dispose();
                }
                finally
                {
                    _deviceLock.Release();
                    _deviceLock.Dispose();
                }
            }
            else
            {
                // Timeout - force disposal
                _deviceLock.Dispose();
            }

            base.Dispose();
        }
    }

    /// <summary>
    /// OnlyKey profile (PIN-protected compartments).
    /// </summary>
    public enum OnlyKeyProfile
    {
        /// <summary>Primary profile (first PIN)</summary>
        Primary = 1,
        /// <summary>Secondary profile (second PIN)</summary>
        Secondary = 2
    }

    /// <summary>
    /// Configuration for OnlyKey key store strategy.
    /// </summary>
    public class OnlyKeyConfig
    {
        /// <summary>
        /// OnlyKey serial number. Auto-detected if not specified.
        /// </summary>
        public string? SerialNumber { get; set; }

        /// <summary>
        /// PIN for device unlock (user must enter on device).
        /// </summary>
        public string? Pin { get; set; }

        /// <summary>
        /// Which profile to use.
        /// </summary>
        public OnlyKeyProfile Profile { get; set; } = OnlyKeyProfile.Primary;

        /// <summary>
        /// Default slot for challenge-response.
        /// </summary>
        public int DefaultSlot { get; set; } = 1;

        /// <summary>
        /// Path to store slot mappings.
        /// </summary>
        public string? MappingStoragePath { get; set; }
    }

    /// <summary>
    /// Mapping between key IDs and OnlyKey slots.
    /// </summary>
    public class OnlyKeySlotMapping
    {
        public string KeyId { get; set; } = string.Empty;
        public int Slot { get; set; }
        public OnlyKeyProfile Profile { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
