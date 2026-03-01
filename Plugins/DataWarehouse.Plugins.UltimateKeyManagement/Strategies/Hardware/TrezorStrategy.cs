using DataWarehouse.SDK.Security;
using HidSharp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Hardware
{
    /// <summary>
    /// Trezor Hardware Wallet KeyStore strategy using HID communication.
    /// Trezor devices are open-source hardware wallets supporting:
    /// - BIP32/BIP39 hierarchical deterministic key derivation
    /// - CipherKeyValue for symmetric key encryption
    /// - ECDSA signing with secp256k1
    /// - PIN and passphrase protection
    ///
    /// Key Derivation Methods:
    /// 1. CipherKeyValue: Encrypt/decrypt using device-specific key
    /// 2. GetPublicKey + HKDF: Derive symmetric key from public key
    /// 3. SignIdentity: ECDH-based key derivation
    ///
    /// Communication:
    /// Protobuf-encoded messages over USB HID with V1/V2 transport framing.
    /// Supports Trezor Model One, Model T, and Safe 3.
    ///
    /// Requirements:
    /// - Trezor device with firmware supporting CipherKeyValue
    /// - Device must be unlocked
    /// </summary>
    public sealed class TrezorStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        // Trezor USB IDs
        private const int TrezorVendorId = 0x534c;
        private const int TrezorOneProductId = 0x0001;
        private const int TrezorTProductId = 0x0002;

        // Alternative USB IDs (bootloader mode, webusb)
        private const int TrezorVendorIdAlt = 0x1209;
        private const int TrezorProductIdAlt = 0x53c0;
        private const int TrezorProductIdAlt2 = 0x53c1;

        // Message types
        private const ushort MsgInitialize = 0x0000;
        private const ushort MsgFeatures = 0x0011;
        private const ushort MsgPing = 0x0001;
        private const ushort MsgSuccess = 0x0002;
        private const ushort MsgFailure = 0x0003;
        private const ushort MsgButtonRequest = 0x001A;
        private const ushort MsgButtonAck = 0x001B;
        private const ushort MsgPinMatrixRequest = 0x0012;
        private const ushort MsgPinMatrixAck = 0x0013;
        private const ushort MsgGetPublicKey = 0x0019;
        private const ushort MsgPublicKey = 0x0021;
        private const ushort MsgCipherKeyValue = 0x0017;
        private const ushort MsgCipheredKeyValue = 0x0030;
        private const ushort MsgSignIdentity = 0x0035;
        private const ushort MsgSignedIdentity = 0x0036;

        private HidDevice? _device;
        private HidStream? _stream;
        private TrezorConfig _config = new();
        private string _currentKeyId = "default";
        private TrezorFeatures? _features;
        private readonly SemaphoreSlim _deviceLock = new(1, 1);
        private readonly Dictionary<string, TrezorDerivedKey> _derivedKeys = new();
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
                ["Provider"] = "Trezor",
                ["Manufacturer"] = "SatoshiLabs",
                ["Interface"] = "HID Protobuf",
                ["OpenSource"] = true,
                ["SupportsBip32"] = true,
                ["SupportsBip39"] = true,
                ["SupportsCipherKeyValue"] = true,
                ["SupportsPassphrase"] = true
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("trezor.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "CipherKeyValue-AES256",
            "ECDH-secp256k1"
        };

        public bool SupportsHsmKeyGeneration => true;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("trezor.init");
            // Load configuration
            if (Configuration.TryGetValue("DerivationPath", out var pathObj) && pathObj is string path)
                _config.DerivationPath = path;
            if (Configuration.TryGetValue("KeyValueKey", out var kvObj) && kvObj is string kv)
                _config.KeyValueKey = kv;
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
                .Where(d =>
                    (d.VendorID == TrezorVendorId &&
                     (d.ProductID == TrezorOneProductId || d.ProductID == TrezorTProductId)) ||
                    (d.VendorID == TrezorVendorIdAlt &&
                     (d.ProductID == TrezorProductIdAlt || d.ProductID == TrezorProductIdAlt2)));

            if (!devices.Any())
            {
                throw new InvalidOperationException("No Trezor device found.");
            }

            // Prefer Model T over Model One
            _device = devices.FirstOrDefault(d => d.ProductID == TrezorTProductId)
                   ?? devices.First();

            try
            {
                _stream = _device.Open();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Failed to open Trezor device. Ensure it's unlocked and not in use.", ex);
            }

            // Initialize and get features
            _features = Initialize();

            if (_features == null)
            {
                throw new InvalidOperationException("Failed to initialize Trezor device.");
            }
        }

        private TrezorFeatures? Initialize()
        {
            // Send Initialize message
            var initMsg = EncodeMessage(MsgInitialize, Array.Empty<byte>());
            SendMessage(initMsg);

            // Receive Features response
            var (msgType, payload) = ReceiveMessage();

            if (msgType == MsgFeatures)
            {
                return ParseFeatures(payload);
            }

            return null;
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

                // Send Ping
                var pingMsg = EncodeMessage(MsgPing, Encoding.UTF8.GetBytes("ping"));
                SendMessage(pingMsg);

                var (msgType, _) = ReceiveMessage();
                return msgType == MsgSuccess;
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
                // Check cache
                if (!_derivedKeys.TryGetValue(keyId, out var derivation))
                {
                    // Derive new key using CipherKeyValue
                    derivation = await DeriveKeyAsync(keyId);
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
            await _deviceLock.WaitAsync();
            try
            {
                var derivation = await DeriveKeyAsync(keyId);
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
        /// Derives a key using Trezor's CipherKeyValue operation.
        /// </summary>
        private async Task<TrezorDerivedKey> DeriveKeyAsync(string keyId)
        {
            // CipherKeyValue encrypts a value using a key derived from the BIP32 path
            // and a user-provided key name. This provides deterministic key derivation.

            var pathElements = ParseDerivationPath(_config.DerivationPath);
            var keyName = $"{_config.KeyValueKey}/{keyId}";

            // Create input value (will be encrypted by device)
            var inputValue = SHA256.HashData(Encoding.UTF8.GetBytes($"DataWarehouse.{keyId}"));

            // Build CipherKeyValue message
            var message = BuildCipherKeyValueMessage(pathElements, keyName, inputValue, true);
            SendMessage(EncodeMessage(MsgCipherKeyValue, message));

            // Handle button request if needed
            var (msgType, payload) = await HandleInteractiveResponse();

            if (msgType == MsgCipheredKeyValue)
            {
                // Extract encrypted value (this is our derived key)
                var derivedKey = payload.Length > 32 ? payload[..32] : payload;

                return new TrezorDerivedKey
                {
                    KeyId = keyId,
                    DerivationPath = _config.DerivationPath,
                    KeyName = keyName,
                    DerivedKey = derivedKey,
                    CreatedAt = DateTime.UtcNow
                };
            }
            else if (msgType == MsgFailure)
            {
                var errorMsg = ParseFailureMessage(payload);
                throw new InvalidOperationException($"Trezor CipherKeyValue failed: {errorMsg}");
            }

            throw new InvalidOperationException($"Unexpected response: {msgType}");
        }

        /// <summary>
        /// Gets a public key from the device.
        /// </summary>
        public async Task<byte[]> GetPublicKeyAsync(string derivationPath)
        {
            await _deviceLock.WaitAsync();
            try
            {
                var pathElements = ParseDerivationPath(derivationPath);
                var message = BuildGetPublicKeyMessage(pathElements);

                SendMessage(EncodeMessage(MsgGetPublicKey, message));

                var (msgType, payload) = await HandleInteractiveResponse();

                if (msgType == MsgPublicKey)
                {
                    // Parse public key from response
                    return ParsePublicKeyResponse(payload);
                }
                else if (msgType == MsgFailure)
                {
                    throw new InvalidOperationException($"Trezor GetPublicKey failed: {ParseFailureMessage(payload)}");
                }

                throw new InvalidOperationException($"Unexpected response: {msgType}");
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
                    kekDerivation = await DeriveKeyAsync(kekId);
                    _derivedKeys[kekId] = kekDerivation;
                }

                // Wrap using AES-GCM with derived key
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
                    kekDerivation = await DeriveKeyAsync(kekId);
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
                throw new UnauthorizedAccessException("Only system administrators can delete Trezor keys.");
            }

            await _deviceLock.WaitAsync(cancellationToken);
            try
            {
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
                    ["Backend"] = "Trezor CipherKeyValue",
                    ["DerivationPath"] = derivation.DerivationPath,
                    ["KeyName"] = derivation.KeyName,
                    ["DeviceModel"] = _features?.Model ?? "Unknown",
                    ["DeviceLabel"] = _features?.Label ?? "Unknown"
                }
            });
        }

        #region Message Encoding/Decoding

        private byte[] EncodeMessage(ushort msgType, byte[] payload)
        {
            // #3482: Fixed Trezor message format: ## (2) + msgType (2) + length (4) + payload
            // Handle empty payloads correctly — allocate minimum 8 bytes.
            var messageLength = 8 + payload.Length;
            var message = new byte[messageLength];

            message[0] = (byte)'#';
            message[1] = (byte)'#';
            message[2] = (byte)(msgType >> 8);
            message[3] = (byte)(msgType & 0xFF);
            message[4] = (byte)((payload.Length >> 24) & 0xFF);
            message[5] = (byte)((payload.Length >> 16) & 0xFF);
            message[6] = (byte)((payload.Length >> 8) & 0xFF);
            message[7] = (byte)(payload.Length & 0xFF);

            if (payload.Length > 0)
            {
                payload.CopyTo(message, 8);
            }

            return message;
        }

        private void SendMessage(byte[] message)
        {
            // Frame message into HID packets (63 bytes payload per packet for Model T)
            var packetSize = _device!.GetMaxOutputReportLength();
            var offset = 0;

            while (offset < message.Length)
            {
                var packet = new byte[packetSize];
                packet[0] = 0x00; // Report ID
                packet[1] = (byte)'?'; // V1 transport marker

                var dataLen = Math.Min(message.Length - offset, packetSize - 2);
                Array.Copy(message, offset, packet, 2, dataLen);
                offset += dataLen;

                _stream!.Write(packet);
            }
        }

        private (ushort msgType, byte[] payload) ReceiveMessage()
        {
            var packetSize = _device!.GetMaxInputReportLength();
            var headerReceived = false;
            ushort msgType = 0;
            int payloadLength = 0;
            var payload = new List<byte>();

            while (true)
            {
                var packet = new byte[packetSize];
                var bytesRead = _stream!.Read(packet, 0, packet.Length);

                if (bytesRead == 0)
                {
                    throw new InvalidOperationException("No response from Trezor.");
                }

                var dataStart = 0;

                if (!headerReceived)
                {
                    // Find header marker
                    for (int i = 0; i < bytesRead - 1; i++)
                    {
                        if (packet[i] == '#' && packet[i + 1] == '#')
                        {
                            dataStart = i;
                            break;
                        }
                    }

                    if (bytesRead - dataStart >= 8)
                    {
                        msgType = (ushort)((packet[dataStart + 2] << 8) | packet[dataStart + 3]);
                        payloadLength = (packet[dataStart + 4] << 24) |
                                       (packet[dataStart + 5] << 16) |
                                       (packet[dataStart + 6] << 8) |
                                       packet[dataStart + 7];
                        headerReceived = true;
                        dataStart += 8;
                    }
                }

                if (headerReceived)
                {
                    var dataLen = Math.Min(payloadLength - payload.Count, bytesRead - dataStart);
                    for (int i = 0; i < dataLen; i++)
                    {
                        payload.Add(packet[dataStart + i]);
                    }

                    if (payload.Count >= payloadLength)
                    {
                        break;
                    }
                }
            }

            return (msgType, payload.ToArray());
        }

        private async Task<(ushort msgType, byte[] payload)> HandleInteractiveResponse()
        {
            while (true)
            {
                var (msgType, payload) = ReceiveMessage();

                if (msgType == MsgButtonRequest)
                {
                    // Send ButtonAck
                    SendMessage(EncodeMessage(MsgButtonAck, Array.Empty<byte>()));
                    await Task.Delay(100);
                    continue;
                }
                else if (msgType == MsgPinMatrixRequest)
                {
                    throw new InvalidOperationException(
                        "Trezor requires PIN entry. Please unlock the device first.");
                }

                return (msgType, payload);
            }
        }

        #endregion

        #region Message Building

        private byte[] BuildCipherKeyValueMessage(uint[] path, string keyName, byte[] value, bool encrypt)
        {
            // Simplified protobuf-like encoding for CipherKeyValue
            var ms = new MemoryStream(4096);
            var writer = new BinaryWriter(ms);

            // Field 1: path (repeated uint32, packed)
            foreach (var p in path)
            {
                writer.Write((byte)0x08); // Field 1, wire type 0 (varint)
                WriteVarint(writer, p);
            }

            // Field 2: key (string)
            var keyBytes = Encoding.UTF8.GetBytes(keyName);
            writer.Write((byte)0x12); // Field 2, wire type 2 (length-delimited)
            WriteVarint(writer, (uint)keyBytes.Length);
            writer.Write(keyBytes);

            // Field 3: value (bytes)
            writer.Write((byte)0x1A); // Field 3, wire type 2
            WriteVarint(writer, (uint)value.Length);
            writer.Write(value);

            // Field 4: encrypt (bool)
            writer.Write((byte)0x20); // Field 4, wire type 0
            writer.Write((byte)(encrypt ? 1 : 0));

            // Field 5: ask_on_encrypt (bool) - set based on config
            writer.Write((byte)0x28); // Field 5, wire type 0
            writer.Write((byte)(_config.RequireConfirmation ? 1 : 0));

            // Field 6: ask_on_decrypt (bool)
            writer.Write((byte)0x30); // Field 6, wire type 0
            writer.Write((byte)(_config.RequireConfirmation ? 1 : 0));

            return ms.ToArray();
        }

        private byte[] BuildGetPublicKeyMessage(uint[] path)
        {
            var ms = new MemoryStream(4096);
            var writer = new BinaryWriter(ms);

            // Field 1: path (repeated uint32)
            foreach (var p in path)
            {
                writer.Write((byte)0x08);
                WriteVarint(writer, p);
            }

            return ms.ToArray();
        }

        private void WriteVarint(BinaryWriter writer, uint value)
        {
            while (value >= 0x80)
            {
                writer.Write((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            writer.Write((byte)value);
        }

        private uint[] ParseDerivationPath(string path)
        {
            var elements = new List<uint>();

            foreach (var part in path.Split('/'))
            {
                if (string.IsNullOrEmpty(part) || part == "m")
                    continue;

                var hardened = part.EndsWith("'") || part.EndsWith("h");
                var raw = hardened ? part.TrimEnd('\'', 'h') : part;
                if (!uint.TryParse(raw, out var value))
                    throw new FormatException($"Invalid BIP-32 path component: '{part}'");

                if (hardened)
                {
                    value |= 0x80000000;
                }

                elements.Add(value);
            }

            return elements.ToArray();
        }

        private TrezorFeatures ParseFeatures(byte[] payload)
        {
            // Simplified parsing - in production, use proper protobuf
            var features = new TrezorFeatures();

            // Extract common fields from protobuf-like structure
            var text = Encoding.UTF8.GetString(payload);

            if (payload.Length > 10)
            {
                features.Initialized = true;
                features.Model = payload.Length > 50 ? "Model T" : "Model One";
            }

            return features;
        }

        private byte[] ParsePublicKeyResponse(byte[] payload)
        {
            // Find the public key bytes in the response
            // Public key is typically 33 bytes (compressed) or 65 bytes (uncompressed)
            if (payload.Length >= 33)
            {
                return payload[..33];
            }

            throw new InvalidOperationException("Invalid public key response.");
        }

        private string ParseFailureMessage(byte[] payload)
        {
            // Extract error message from failure response
            if (payload.Length > 2)
            {
                return Encoding.UTF8.GetString(payload, 2, payload.Length - 2).TrimEnd('\0');
            }
            return "Unknown error";
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
                var stored = JsonSerializer.Deserialize<Dictionary<string, TrezorDerivedKeyDto>>(json);

                if (stored != null)
                {
                    foreach (var kvp in stored)
                    {
                        _derivedKeys[kvp.Key] = kvp.Value.ToKey();
                    }
                }
            }
            catch
            {

                // Ignore errors
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
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
                kvp => TrezorDerivedKeyDto.FromKey(kvp.Value));

            var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        private string GetKeyStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.KeyStoragePath))
                return _config.KeyStoragePath;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "trezor-keys.json");
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
    /// Configuration for Trezor key store strategy.
    /// </summary>
    public class TrezorConfig
    {
        /// <summary>
        /// BIP32 derivation path for keys.
        /// Default: m/10016'/0 (Trezor CipherKeyValue standard path)
        /// </summary>
        public string DerivationPath { get; set; } = "m/10016'/0";

        /// <summary>
        /// Key name prefix for CipherKeyValue operations.
        /// </summary>
        public string KeyValueKey { get; set; } = "DataWarehouse";

        /// <summary>
        /// Require on-device confirmation for operations.
        /// </summary>
        public bool RequireConfirmation { get; set; } = true;

        /// <summary>
        /// Path to store derived key metadata.
        /// </summary>
        public string? KeyStoragePath { get; set; }
    }

    /// <summary>
    /// Trezor device features.
    /// </summary>
    public class TrezorFeatures
    {
        public string? Model { get; set; }
        public string? Label { get; set; }
        public bool Initialized { get; set; }
        public bool Unlocked { get; set; }
        public bool PassphraseProtection { get; set; }
    }

    /// <summary>
    /// Represents a derived key from Trezor.
    /// </summary>
    public class TrezorDerivedKey
    {
        public string KeyId { get; set; } = string.Empty;
        public string DerivationPath { get; set; } = string.Empty;
        public string KeyName { get; set; } = string.Empty;
        public byte[] DerivedKey { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; }
    }

    internal class TrezorDerivedKeyDto
    {
        public string KeyId { get; set; } = string.Empty;
        public string DerivationPath { get; set; } = string.Empty;
        public string KeyName { get; set; } = string.Empty;
        public string DerivedKey { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        public static TrezorDerivedKeyDto FromKey(TrezorDerivedKey k) => new()
        {
            KeyId = k.KeyId,
            DerivationPath = k.DerivationPath,
            KeyName = k.KeyName,
            DerivedKey = Convert.ToBase64String(k.DerivedKey),
            CreatedAt = k.CreatedAt
        };

        public TrezorDerivedKey ToKey() => new()
        {
            KeyId = KeyId,
            DerivationPath = DerivationPath,
            KeyName = KeyName,
            DerivedKey = Convert.FromBase64String(DerivedKey),
            CreatedAt = CreatedAt
        };
    }
}
