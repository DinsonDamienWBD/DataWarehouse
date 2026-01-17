using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.Encryption
{
    /// <summary>
    /// AES-256-GCM encryption plugin for DataWarehouse pipeline.
    /// Extends PipelinePluginBase for bidirectional stream transformation.
    /// Uses IKeyStore for key management with automatic IV generation.
    ///
    /// Security features:
    /// - AES-256-GCM authenticated encryption
    /// - Automatic IV generation (96-bit for GCM)
    /// - Key ID stored with ciphertext for key rotation support
    /// - Authentication tag verification on decryption
    ///
    /// Message Commands:
    /// - encryption.aes.configure: Configure encryption settings
    /// - encryption.aes.rotate: Trigger key rotation
    /// - encryption.aes.stats: Get encryption statistics
    /// </summary>
    public sealed class AesEncryptionPlugin : PipelinePluginBase
    {
        private readonly AesEncryptionConfig _config;
        private readonly object _statsLock = new();
        private IKeyStore? _keyStore;
        private ISecurityContext? _securityContext;
        private long _encryptionCount;
        private long _decryptionCount;
        private long _totalBytesEncrypted;

        private const int IvSize = 12;
        private const int TagSize = 16;
        private const int KeyIdSize = 32;
        private const int HeaderSize = IvSize + TagSize + KeyIdSize + 4;

        public override string Id => "datawarehouse.plugins.encryption.aes256";
        public override string Name => "AES-256 Encryption";
        public override string Version => "1.0.0";
        public override string SubCategory => "Encryption";
        public override int QualityLevel => 85;
        public override int DefaultOrder => 90;
        public override bool AllowBypass => false;

        public override string[] RequiredPrecedingStages => ["Compression"];

        public AesEncryptionPlugin(AesEncryptionConfig? config = null)
        {
            _config = config ?? new AesEncryptionConfig();
        }

        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);

            if (_config.KeyStore != null)
            {
                _keyStore = _config.KeyStore;
            }

            return response;
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "encryption.aes.configure", DisplayName = "Configure", Description = "Configure AES encryption settings" },
                new() { Name = "encryption.aes.rotate", DisplayName = "Rotate Key", Description = "Trigger key rotation" },
                new() { Name = "encryption.aes.stats", DisplayName = "Statistics", Description = "Get encryption statistics" },
                new() { Name = "encryption.aes.encrypt", DisplayName = "Encrypt", Description = "Encrypt data using AES-256-GCM" },
                new() { Name = "encryption.aes.decrypt", DisplayName = "Decrypt", Description = "Decrypt AES-256-GCM encrypted data" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "AES-256-GCM";
            metadata["KeySize"] = 256;
            metadata["IVSize"] = IvSize * 8;
            metadata["TagSize"] = TagSize * 8;
            metadata["SupportsKeyRotation"] = true;
            metadata["SupportsStreaming"] = true;
            metadata["RequiresKeyStore"] = true;
            return metadata;
        }

        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "encryption.aes.configure" => HandleConfigureAsync(message),
                "encryption.aes.rotate" => HandleRotateAsync(message),
                "encryption.aes.stats" => HandleStatsAsync(message),
                "encryption.aes.setKeyStore" => HandleSetKeyStoreAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            var keyStore = GetKeyStore(args, context);
            var securityContext = GetSecurityContext(args);

            var keyId = keyStore.GetCurrentKeyIdAsync().GetAwaiter().GetResult();
            var key = keyStore.GetKeyAsync(keyId, securityContext).GetAwaiter().GetResult();

            if (key.Length != 32)
                throw new CryptographicException("AES-256 requires a 256-bit (32-byte) key");

            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var plaintext = inputMs.ToArray();

            var ciphertext = Encrypt(plaintext, key, keyId);

            lock (_statsLock)
            {
                _encryptionCount++;
                _totalBytesEncrypted += plaintext.Length;
            }

            context.LogDebug($"AES-256-GCM encrypted {plaintext.Length} bytes with key {keyId[..8]}...");

            return new MemoryStream(ciphertext);
        }

        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            var keyStore = GetKeyStore(args, context);
            var securityContext = GetSecurityContext(args);

            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs);
            var ciphertext = inputMs.ToArray();

            if (ciphertext.Length < HeaderSize)
                throw new CryptographicException("Ciphertext too short to contain header");

            var keyIdLength = BitConverter.ToInt32(ciphertext, 0);
            if (keyIdLength <= 0 || keyIdLength > KeyIdSize)
                throw new CryptographicException("Invalid key ID length in header");

            var keyId = System.Text.Encoding.UTF8.GetString(ciphertext, 4, keyIdLength).TrimEnd('\0');
            var key = keyStore.GetKeyAsync(keyId, securityContext).GetAwaiter().GetResult();

            if (key.Length != 32)
                throw new CryptographicException("AES-256 requires a 256-bit (32-byte) key");

            var plaintext = Decrypt(ciphertext, key);

            lock (_statsLock)
            {
                _decryptionCount++;
            }

            context.LogDebug($"AES-256-GCM decrypted {ciphertext.Length} bytes with key {keyId[..Math.Min(8, keyId.Length)]}...");

            return new MemoryStream(plaintext);
        }

        private byte[] Encrypt(byte[] plaintext, byte[] key, string keyId)
        {
            var iv = RandomNumberGenerator.GetBytes(IvSize);
            var tag = new byte[TagSize];
            var ciphertext = new byte[plaintext.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(iv, plaintext, ciphertext, tag);

            var keyIdBytes = new byte[KeyIdSize];
            var keyIdUtf8 = System.Text.Encoding.UTF8.GetBytes(keyId);
            Array.Copy(keyIdUtf8, keyIdBytes, Math.Min(keyIdUtf8.Length, KeyIdSize));

            var result = new byte[4 + KeyIdSize + IvSize + TagSize + ciphertext.Length];
            var pos = 0;

            BitConverter.GetBytes(keyIdUtf8.Length).CopyTo(result, pos); pos += 4;
            keyIdBytes.CopyTo(result, pos); pos += KeyIdSize;
            iv.CopyTo(result, pos); pos += IvSize;
            tag.CopyTo(result, pos); pos += TagSize;
            ciphertext.CopyTo(result, pos);

            return result;
        }

        private byte[] Decrypt(byte[] encryptedData, byte[] key)
        {
            var pos = 0;

            var keyIdLength = BitConverter.ToInt32(encryptedData, pos); pos += 4;
            pos += KeyIdSize;

            var iv = new byte[IvSize];
            Array.Copy(encryptedData, pos, iv, 0, IvSize); pos += IvSize;

            var tag = new byte[TagSize];
            Array.Copy(encryptedData, pos, tag, 0, TagSize); pos += TagSize;

            var ciphertext = new byte[encryptedData.Length - pos];
            Array.Copy(encryptedData, pos, ciphertext, 0, ciphertext.Length);

            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(iv, ciphertext, tag, plaintext);

            return plaintext;
        }

        private IKeyStore GetKeyStore(Dictionary<string, object> args, IKernelContext context)
        {
            if (args.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
                return ks;

            if (_keyStore != null)
                return _keyStore;

            var keyStorePlugin = context.GetPlugin<IKeyStore>();
            if (keyStorePlugin != null)
                return keyStorePlugin;

            throw new InvalidOperationException("No IKeyStore available. Configure a key store before using encryption.");
        }

        private ISecurityContext GetSecurityContext(Dictionary<string, object> args)
        {
            if (args.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
                return sc;

            return _securityContext ?? new DefaultSecurityContext();
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
                _keyStore = ks;

            if (message.Payload.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
                _securityContext = sc;

            return Task.CompletedTask;
        }

        private async Task HandleRotateAsync(PluginMessage message)
        {
            if (_keyStore == null)
                throw new InvalidOperationException("No key store configured");

            var context = GetSecurityContext(message.Payload as Dictionary<string, object> ?? new());
            var newKeyId = Guid.NewGuid().ToString("N");
            await _keyStore.CreateKeyAsync(newKeyId, context);
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                var stats = new Dictionary<string, object>
                {
                    ["EncryptionCount"] = _encryptionCount,
                    ["DecryptionCount"] = _decryptionCount,
                    ["TotalBytesEncrypted"] = _totalBytesEncrypted
                };
            }
            return Task.CompletedTask;
        }

        private Task HandleSetKeyStoreAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
                _keyStore = ks;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// ChaCha20-Poly1305 encryption plugin for high-performance encryption.
    /// Alternative to AES-GCM, often faster on systems without AES-NI.
    /// </summary>
    public sealed class ChaCha20EncryptionPlugin : PipelinePluginBase
    {
        private readonly ChaCha20Config _config;
        private IKeyStore? _keyStore;
        private ISecurityContext? _securityContext;

        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int KeyIdSize = 32;

        public override string Id => "datawarehouse.plugins.encryption.chacha20";
        public override string Name => "ChaCha20-Poly1305 Encryption";
        public override string Version => "1.0.0";
        public override string SubCategory => "Encryption";
        public override int QualityLevel => 80;
        public override int DefaultOrder => 90;
        public override bool AllowBypass => false;

        public override string[] IncompatibleStages => ["encryption.aes256"];

        public ChaCha20EncryptionPlugin(ChaCha20Config? config = null)
        {
            _config = config ?? new ChaCha20Config();
        }

        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "encryption.chacha20.configure", DisplayName = "Configure", Description = "Configure ChaCha20 encryption settings" },
                new() { Name = "encryption.chacha20.encrypt", DisplayName = "Encrypt", Description = "Encrypt data using ChaCha20-Poly1305" },
                new() { Name = "encryption.chacha20.decrypt", DisplayName = "Decrypt", Description = "Decrypt ChaCha20-Poly1305 encrypted data" }
            ];
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "ChaCha20-Poly1305";
            metadata["KeySize"] = 256;
            metadata["NonceSize"] = NonceSize * 8;
            metadata["TagSize"] = TagSize * 8;
            metadata["SupportsKeyRotation"] = true;
            metadata["RequiresKeyStore"] = true;
            return metadata;
        }

        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "encryption.chacha20.configure" => HandleConfigureAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            var keyStore = GetKeyStore(args, context);
            var securityContext = GetSecurityContext(args);

            var keyId = keyStore.GetCurrentKeyIdAsync().GetAwaiter().GetResult();
            var key = keyStore.GetKeyAsync(keyId, securityContext).GetAwaiter().GetResult();

            if (key.Length != 32)
                throw new CryptographicException("ChaCha20-Poly1305 requires a 256-bit (32-byte) key");

            using var inputMs = new MemoryStream();
            input.CopyTo(inputMs);
            var plaintext = inputMs.ToArray();

            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var tag = new byte[TagSize];
            var ciphertext = new byte[plaintext.Length];

            using var chacha = new ChaCha20Poly1305(key);
            chacha.Encrypt(nonce, plaintext, ciphertext, tag);

            var keyIdBytes = new byte[KeyIdSize];
            var keyIdUtf8 = System.Text.Encoding.UTF8.GetBytes(keyId);
            Array.Copy(keyIdUtf8, keyIdBytes, Math.Min(keyIdUtf8.Length, KeyIdSize));

            var result = new byte[4 + KeyIdSize + NonceSize + TagSize + ciphertext.Length];
            var pos = 0;

            BitConverter.GetBytes(keyIdUtf8.Length).CopyTo(result, pos); pos += 4;
            keyIdBytes.CopyTo(result, pos); pos += KeyIdSize;
            nonce.CopyTo(result, pos); pos += NonceSize;
            tag.CopyTo(result, pos); pos += TagSize;
            ciphertext.CopyTo(result, pos);

            context.LogDebug($"ChaCha20-Poly1305 encrypted {plaintext.Length} bytes");

            return new MemoryStream(result);
        }

        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            var keyStore = GetKeyStore(args, context);
            var securityContext = GetSecurityContext(args);

            using var inputMs = new MemoryStream();
            stored.CopyTo(inputMs);
            var encryptedData = inputMs.ToArray();

            var pos = 0;
            var keyIdLength = BitConverter.ToInt32(encryptedData, pos); pos += 4;
            var keyId = System.Text.Encoding.UTF8.GetString(encryptedData, pos, keyIdLength).TrimEnd('\0');
            pos += KeyIdSize;

            var key = keyStore.GetKeyAsync(keyId, securityContext).GetAwaiter().GetResult();

            var nonce = new byte[NonceSize];
            Array.Copy(encryptedData, pos, nonce, 0, NonceSize); pos += NonceSize;

            var tag = new byte[TagSize];
            Array.Copy(encryptedData, pos, tag, 0, TagSize); pos += TagSize;

            var ciphertext = new byte[encryptedData.Length - pos];
            Array.Copy(encryptedData, pos, ciphertext, 0, ciphertext.Length);

            var plaintext = new byte[ciphertext.Length];

            using var chacha = new ChaCha20Poly1305(key);
            chacha.Decrypt(nonce, ciphertext, tag, plaintext);

            context.LogDebug($"ChaCha20-Poly1305 decrypted {ciphertext.Length} bytes");

            return new MemoryStream(plaintext);
        }

        private IKeyStore GetKeyStore(Dictionary<string, object> args, IKernelContext context)
        {
            if (args.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
                return ks;
            if (_keyStore != null)
                return _keyStore;
            var keyStorePlugin = context.GetPlugin<IKeyStore>();
            if (keyStorePlugin != null)
                return keyStorePlugin;
            throw new InvalidOperationException("No IKeyStore available");
        }

        private ISecurityContext GetSecurityContext(Dictionary<string, object> args)
        {
            if (args.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
                return sc;
            return _securityContext ?? new DefaultSecurityContext();
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
                _keyStore = ks;
            if (message.Payload.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
                _securityContext = sc;
            return Task.CompletedTask;
        }
    }

    public class AesEncryptionConfig
    {
        public IKeyStore? KeyStore { get; set; }
        public ISecurityContext? SecurityContext { get; set; }
    }

    public class ChaCha20Config
    {
        public IKeyStore? KeyStore { get; set; }
    }

    internal class DefaultSecurityContext : ISecurityContext
    {
        public string UserId => Environment.UserName;
        public string? TenantId => "local";
        public IEnumerable<string> Roles => ["user"];
        public bool IsSystemAdmin => false;
    }
}
