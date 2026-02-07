using DataWarehouse.SDK.Security;
using HidSharp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hardware
{
    /// <summary>
    /// Ledger Hardware Wallet KeyStore strategy using HID communication via APDU.
    /// Ledger devices are hardware wallets that can be used for secure key management:
    /// - Secure Element (SE) for private key storage
    /// - PIN protection with anti-hammering
    /// - BIP32/BIP39 hierarchical deterministic keys
    /// - Custom app support for key derivation
    ///
    /// Key Derivation:
    /// Uses BIP32 derivation paths to derive keys deterministically.
    /// Path format: m/44'/purpose'/account'/change/index
    /// For DataWarehouse: m/44'/1234567'/account'/0/keyIndex
    ///
    /// Communication:
    /// APDU commands over USB HID with transport framing.
    /// Supports Ledger Nano S, Nano X, and Nano S Plus.
    ///
    /// Requirements:
    /// - Ledger device with custom app or Bitcoin/Ethereum app for key derivation
    /// - Device must be unlocked and app open
    /// </summary>
    public sealed class LedgerStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        // Ledger USB IDs
        private const int LedgerVendorId = 0x2c97;
        private static readonly int[] LedgerProductIds = { 0x0001, 0x0011, 0x0015, 0x0004, 0x4011, 0x5011 };

        // APDU Constants
        private const byte ClaLedger = 0xE0;
        private const byte InsGetPublicKey = 0x02;
        private const byte InsSignHash = 0x04;
        private const byte InsDeriveKey = 0x06;
        private const byte InsGetAppInfo = 0x01;

        // HID Transport
        private const int HidPacketSize = 64;
        private const int ApduTagInit = 0x05;
        private const int ApduChannelId = 0x0101;

        private HidDevice? _device;
        private HidStream? _stream;
        private LedgerConfig _config = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _deviceLock = new(1, 1);
        private readonly Dictionary<string, LedgerKeyDerivation> _derivedKeys = new();
        private ushort _sequenceNumber;
        private bool _disposed;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = false,
            SupportsEnvelope = true,
            SupportsHsm = true,
            SupportsExpiration = false,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 32,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "Ledger",
                ["Manufacturer"] = "Ledger SAS",
                ["Interface"] = "HID APDU",
                ["SecureElement"] = true,
                ["SupportsBip32"] = true,
                ["SupportsBip39"] = true,
                ["AntiHammering"] = true
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "ECDH-secp256k1",
            "ECDH-P256"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration
            if (Configuration.TryGetValue("DerivationPath", out var pathObj) && pathObj is string path)
                _config.DerivationPath = path;
            if (Configuration.TryGetValue("AppName", out var appObj) && appObj is string app)
                _config.AppName = app;
            if (Configuration.TryGetValue("RequireConfirmation", out var confirmObj) && confirmObj is bool confirm)
                _config.RequireConfirmation = confirm;
            if (Configuration.TryGetValue("KeyStoragePath", out var storagePath) && storagePath is string storage)
                _config.KeyStoragePath = storage;

            await Task.Run(ConnectDevice, cancellationToken);
            await LoadDerivedKeys();
        }

        private void ConnectDevice()
        {
            var devices = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID == LedgerVendorId &&
                           LedgerProductIds.Contains(d.ProductID));

            if (!devices.Any())
            {
                throw new InvalidOperationException("No Ledger device found.");
            }

            _device = devices.First();

            try
            {
                _stream = _device.Open();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to open Ledger device. Ensure it's unlocked and not in use.", ex);
            }

            // Verify device is responsive
            var appInfo = GetAppInfo();
            if (string.IsNullOrEmpty(appInfo))
            {
                throw new InvalidOperationException(
                    "Ledger device not responding. Ensure app is open.");
            }
        }

        private string? GetAppInfo()
        {
            try
            {
                var apdu = BuildApdu(ClaLedger, InsGetAppInfo, 0x00, 0x00, null);
                var response = ExchangeApdu(apdu);

                if (response.Length >= 2)
                {
                    var sw = (response[^2] << 8) | response[^1];
                    if (sw == 0x9000 && response.Length > 2)
                    {
                        // Parse app name from response
                        var nameLen = response[1];
                        return Encoding.ASCII.GetString(response, 2, nameLen);
                    }
                }

                return null;
            }
            catch
            {
                return null;
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

                var appInfo = GetAppInfo();
                return !string.IsNullOrEmpty(appInfo);
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
                // Check if we have a cached derivation
                if (!_derivedKeys.TryGetValue(keyId, out var derivation))
                {
                    // Derive new key
                    derivation = DeriveKey(keyId);
                    _derivedKeys[keyId] = derivation;
                    await PersistDerivedKeys();
                }

                return derivation.DerivedKey;
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            // Ledger uses deterministic derivation, so we just store the derivation parameters
            await _deviceLock.WaitAsync();
            try
            {
                var derivation = DeriveKey(keyId);
                _derivedKeys[keyId] = derivation;
                _currentKeyId = keyId;
                await PersistDerivedKeys();
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        /// <summary>
        /// Derives a key using BIP32 derivation path.
        /// </summary>
        private LedgerKeyDerivation DeriveKey(string keyId)
        {
            // Build derivation path
            // Format: m/44'/1234567'/keyIndex'/0/0
            var keyIndex = Math.Abs(keyId.GetHashCode()) % 1000000;
            var derivationPath = $"{_config.DerivationPath.TrimEnd('/')}/{keyIndex}'/0/0";

            // Parse path to BIP32 format
            var pathElements = ParseDerivationPath(derivationPath);

            // Get public key via APDU
            var pathData = EncodePath(pathElements);
            var apdu = BuildApdu(ClaLedger, InsGetPublicKey,
                _config.RequireConfirmation ? (byte)0x01 : (byte)0x00,
                0x00, pathData);

            var response = ExchangeApdu(apdu);
            ValidateResponse(response);

            // Parse public key (compressed format: 33 bytes)
            var pubKeyLen = response[0];
            var publicKey = new byte[pubKeyLen];
            Array.Copy(response, 1, publicKey, 0, pubKeyLen);

            // Derive symmetric key from public key using HKDF
            var derivedKey = new byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, publicKey,
                derivedKey, Encoding.UTF8.GetBytes(keyId),
                Encoding.UTF8.GetBytes("DataWarehouse.Ledger"));

            return new LedgerKeyDerivation
            {
                KeyId = keyId,
                DerivationPath = derivationPath,
                PublicKey = publicKey,
                DerivedKey = derivedKey,
                CreatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Gets the public key for a derivation path.
        /// </summary>
        public async Task<byte[]> GetPublicKeyAsync(string derivationPath, bool requireConfirmation = false)
        {
            await _deviceLock.WaitAsync();
            try
            {
                var pathElements = ParseDerivationPath(derivationPath);
                var pathData = EncodePath(pathElements);

                var apdu = BuildApdu(ClaLedger, InsGetPublicKey,
                    requireConfirmation ? (byte)0x01 : (byte)0x00,
                    0x00, pathData);

                var response = ExchangeApdu(apdu);
                ValidateResponse(response);

                var pubKeyLen = response[0];
                var publicKey = new byte[pubKeyLen];
                Array.Copy(response, 1, publicKey, 0, pubKeyLen);

                return publicKey;
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        /// <summary>
        /// Signs a hash using the Ledger device.
        /// </summary>
        public async Task<byte[]> SignHashAsync(string derivationPath, byte[] hash)
        {
            if (hash.Length != 32)
            {
                throw new ArgumentException("Hash must be 32 bytes.", nameof(hash));
            }

            await _deviceLock.WaitAsync();
            try
            {
                var pathElements = ParseDerivationPath(derivationPath);
                var pathData = EncodePath(pathElements);

                // Combine path and hash
                var data = new byte[pathData.Length + hash.Length];
                pathData.CopyTo(data, 0);
                hash.CopyTo(data, pathData.Length);

                var apdu = BuildApdu(ClaLedger, InsSignHash, 0x00, 0x00, data);

                var response = ExchangeApdu(apdu);
                ValidateResponse(response);

                // Signature is DER encoded
                var sigLen = response.Length - 2; // Remove status word
                var signature = new byte[sigLen];
                Array.Copy(response, 0, signature, 0, sigLen);

                return signature;
            }
            finally
            {
                _deviceLock.Release();
            }
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _deviceLock.WaitAsync();
            try
            {
                if (!_derivedKeys.TryGetValue(kekId, out var kekDerivation))
                {
                    kekDerivation = DeriveKey(kekId);
                    _derivedKeys[kekId] = kekDerivation;
                }

                // Wrap using derived key with AES-GCM
                var nonce = RandomNumberGenerator.GetBytes(12);
                var tag = new byte[16];
                var ciphertext = new byte[dataKey.Length];

                using var aes = new AesGcm(kekDerivation.DerivedKey, 16);
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
                if (wrappedKey.Length < 29)
                {
                    throw new ArgumentException("Wrapped key too short.", nameof(wrappedKey));
                }

                if (!_derivedKeys.TryGetValue(kekId, out var kekDerivation))
                {
                    kekDerivation = DeriveKey(kekId);
                    _derivedKeys[kekId] = kekDerivation;
                }

                // Parse wrapped data
                var nonce = wrappedKey.AsSpan(0, 12).ToArray();
                var ciphertext = wrappedKey.AsSpan(12, wrappedKey.Length - 28).ToArray();
                var tag = wrappedKey.AsSpan(wrappedKey.Length - 16).ToArray();

                var plaintext = new byte[ciphertext.Length];

                using var aes = new AesGcm(kekDerivation.DerivedKey, 16);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);

                return plaintext;
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
            return _derivedKeys.Keys.ToList().AsReadOnly();
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete Ledger keys.");
            }

            await _deviceLock.WaitAsync(cancellationToken);
            try
            {
                // Remove local derivation cache (device keys are deterministic and can't be deleted)
                if (_derivedKeys.Remove(keyId))
                {
                    await PersistDerivedKeys();
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

            if (!_derivedKeys.TryGetValue(keyId, out var derivation))
            {
                return null;
            }

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = derivation.CreatedAt,
                IsActive = keyId == _currentKeyId,
                KeySizeBytes = derivation.DerivedKey.Length,
                Metadata = new Dictionary<string, object>
                {
                    ["Backend"] = "Ledger BIP32",
                    ["DerivationPath"] = derivation.DerivationPath,
                    ["PublicKeyHash"] = Convert.ToHexString(SHA256.HashData(derivation.PublicKey)[..8])
                }
            });
        }

        #region APDU Communication

        private byte[] BuildApdu(byte cla, byte ins, byte p1, byte p2, byte[]? data)
        {
            if (data == null || data.Length == 0)
            {
                return new byte[] { cla, ins, p1, p2, 0x00 };
            }

            var apdu = new byte[5 + data.Length];
            apdu[0] = cla;
            apdu[1] = ins;
            apdu[2] = p1;
            apdu[3] = p2;
            apdu[4] = (byte)data.Length;
            data.CopyTo(apdu, 5);

            return apdu;
        }

        private byte[] ExchangeApdu(byte[] apdu)
        {
            // Frame APDU for HID transport
            var packets = FrameApdu(apdu);

            // Send all packets
            foreach (var packet in packets)
            {
                _stream!.Write(packet);
            }

            // Receive response
            return ReceiveResponse();
        }

        private List<byte[]> FrameApdu(byte[] apdu)
        {
            var packets = new List<byte[]>();
            var offset = 0;
            var sequence = _sequenceNumber++;

            while (offset < apdu.Length)
            {
                var packet = new byte[HidPacketSize];
                var dataOffset = 0;

                if (offset == 0)
                {
                    // First packet header
                    packet[dataOffset++] = 0x00; // Report ID
                    packet[dataOffset++] = (byte)(ApduChannelId >> 8);
                    packet[dataOffset++] = (byte)(ApduChannelId & 0xFF);
                    packet[dataOffset++] = ApduTagInit;
                    packet[dataOffset++] = (byte)(sequence >> 8);
                    packet[dataOffset++] = (byte)(sequence & 0xFF);
                    packet[dataOffset++] = (byte)(apdu.Length >> 8);
                    packet[dataOffset++] = (byte)(apdu.Length & 0xFF);
                }
                else
                {
                    // Continuation packet header
                    packet[dataOffset++] = 0x00;
                    packet[dataOffset++] = (byte)(ApduChannelId >> 8);
                    packet[dataOffset++] = (byte)(ApduChannelId & 0xFF);
                    packet[dataOffset++] = (byte)(packets.Count - 1);
                    packet[dataOffset++] = (byte)(sequence >> 8);
                    packet[dataOffset++] = (byte)(sequence & 0xFF);
                }

                var dataLen = Math.Min(apdu.Length - offset, HidPacketSize - dataOffset);
                Array.Copy(apdu, offset, packet, dataOffset, dataLen);
                offset += dataLen;

                packets.Add(packet);
            }

            return packets;
        }

        private byte[] ReceiveResponse()
        {
            var response = new List<byte>();
            var expectedLen = 0;
            var packetIndex = 0;

            while (true)
            {
                var packet = new byte[HidPacketSize];
                var bytesRead = _stream!.Read(packet, 0, packet.Length);

                if (bytesRead == 0)
                {
                    throw new InvalidOperationException("No response from Ledger device.");
                }

                var dataOffset = 0;

                if (packetIndex == 0)
                {
                    // First packet
                    dataOffset = 7; // Skip header
                    expectedLen = (packet[5] << 8) | packet[6];
                }
                else
                {
                    dataOffset = 5;
                }

                var dataLen = Math.Min(expectedLen - response.Count, bytesRead - dataOffset);
                for (var i = 0; i < dataLen; i++)
                {
                    response.Add(packet[dataOffset + i]);
                }

                packetIndex++;

                if (response.Count >= expectedLen)
                {
                    break;
                }
            }

            return response.ToArray();
        }

        private void ValidateResponse(byte[] response)
        {
            if (response.Length < 2)
            {
                throw new InvalidOperationException("Invalid response from Ledger.");
            }

            var sw = (response[^2] << 8) | response[^1];

            if (sw != 0x9000)
            {
                var message = sw switch
                {
                    0x6985 => "User rejected the operation.",
                    0x6986 => "Command not allowed. Ensure correct app is open.",
                    0x6A80 => "Invalid data.",
                    0x6A82 => "File not found.",
                    0x6B00 => "Incorrect parameters.",
                    0x6D00 => "Instruction not supported.",
                    0x6E00 => "Class not supported.",
                    0x6F00 => "Unknown error.",
                    _ => $"Ledger error: 0x{sw:X4}"
                };

                throw new InvalidOperationException(message);
            }
        }

        #endregion

        #region BIP32 Path Handling

        private uint[] ParseDerivationPath(string path)
        {
            var elements = new List<uint>();

            foreach (var part in path.Split('/'))
            {
                if (string.IsNullOrEmpty(part) || part == "m")
                    continue;

                var hardened = part.EndsWith("'") || part.EndsWith("h");
                var value = uint.Parse(hardened ? part.TrimEnd('\'', 'h') : part);

                if (hardened)
                {
                    value |= 0x80000000; // Hardened derivation
                }

                elements.Add(value);
            }

            return elements.ToArray();
        }

        private byte[] EncodePath(uint[] path)
        {
            var data = new byte[1 + path.Length * 4];
            data[0] = (byte)path.Length;

            for (var i = 0; i < path.Length; i++)
            {
                var offset = 1 + i * 4;
                data[offset] = (byte)(path[i] >> 24);
                data[offset + 1] = (byte)(path[i] >> 16);
                data[offset + 2] = (byte)(path[i] >> 8);
                data[offset + 3] = (byte)(path[i] & 0xFF);
            }

            return data;
        }

        #endregion

        private async Task LoadDerivedKeys()
        {
            var path = GetKeyStoragePath();
            if (!File.Exists(path))
                return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var stored = JsonSerializer.Deserialize<Dictionary<string, LedgerKeyDerivationDto>>(json);

                if (stored != null)
                {
                    foreach (var kvp in stored)
                    {
                        _derivedKeys[kvp.Key] = kvp.Value.ToDerivation();
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        private async Task PersistDerivedKeys()
        {
            var path = GetKeyStoragePath();
            var dir = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var toStore = _derivedKeys.ToDictionary(
                kvp => kvp.Key,
                kvp => LedgerKeyDerivationDto.FromDerivation(kvp.Value));

            var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        private string GetKeyStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.KeyStoragePath))
                return _config.KeyStoragePath;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "ledger-keys.json");
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
    /// Configuration for Ledger key store strategy.
    /// </summary>
    public class LedgerConfig
    {
        /// <summary>
        /// Base BIP32 derivation path.
        /// Default: m/44'/1234567' (DataWarehouse purpose)
        /// </summary>
        public string DerivationPath { get; set; } = "m/44'/1234567'";

        /// <summary>
        /// Expected app name on Ledger.
        /// </summary>
        public string AppName { get; set; } = "Bitcoin";

        /// <summary>
        /// Require on-device confirmation for key derivation.
        /// </summary>
        public bool RequireConfirmation { get; set; } = false;

        /// <summary>
        /// Path to store derived key metadata.
        /// </summary>
        public string? KeyStoragePath { get; set; }
    }

    /// <summary>
    /// Represents a derived key from Ledger.
    /// </summary>
    public class LedgerKeyDerivation
    {
        public string KeyId { get; set; } = string.Empty;
        public string DerivationPath { get; set; } = string.Empty;
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
        public byte[] DerivedKey { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; }
    }

    internal class LedgerKeyDerivationDto
    {
        public string KeyId { get; set; } = string.Empty;
        public string DerivationPath { get; set; } = string.Empty;
        public string PublicKey { get; set; } = string.Empty;
        public string DerivedKey { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        public static LedgerKeyDerivationDto FromDerivation(LedgerKeyDerivation d) => new()
        {
            KeyId = d.KeyId,
            DerivationPath = d.DerivationPath,
            PublicKey = Convert.ToBase64String(d.PublicKey),
            DerivedKey = Convert.ToBase64String(d.DerivedKey),
            CreatedAt = d.CreatedAt
        };

        public LedgerKeyDerivation ToDerivation() => new()
        {
            KeyId = KeyId,
            DerivationPath = DerivationPath,
            PublicKey = Convert.FromBase64String(PublicKey),
            DerivedKey = Convert.FromBase64String(DerivedKey),
            CreatedAt = CreatedAt
        };
    }
}
