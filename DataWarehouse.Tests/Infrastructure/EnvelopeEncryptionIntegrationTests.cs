using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Security;
using Xunit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Integration tests for envelope encryption verifying both Direct and Envelope
    /// key management modes work end-to-end through the SDK encryption infrastructure.
    /// Covers T5.1.2: Envelope mode integration tests.
    /// </summary>
    public class EnvelopeEncryptionIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        public EnvelopeEncryptionIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        #region Test Helpers

        /// <summary>
        /// In-memory IKeyStore implementation for testing Direct mode.
        /// Uses a ConcurrentDictionary to store AES-256 keys.
        /// </summary>
        private sealed class InMemoryKeyStore : IKeyStore
        {
            private readonly BoundedDictionary<string, byte[]> _keys = new BoundedDictionary<string, byte[]>(1000);
            private string _currentKeyId = "default-key";

            public Task<string> GetCurrentKeyIdAsync() => Task.FromResult(_currentKeyId);

            public byte[] GetKey(string keyId)
            {
                if (!_keys.TryGetValue(keyId, out var key))
                {
                    // Auto-generate a 256-bit key on first access
                    key = RandomNumberGenerator.GetBytes(32);
                    _keys[keyId] = key;
                }
                return key;
            }

            public Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context)
            {
                return Task.FromResult(GetKey(keyId));
            }

            public Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context)
            {
                var key = RandomNumberGenerator.GetBytes(32);
                _keys[keyId] = key;
                _currentKeyId = keyId;
                return Task.FromResult(key);
            }

            /// <summary>
            /// Stores a specific key for testing purposes.
            /// </summary>
            public void StoreKey(string keyId, byte[] keyData)
            {
                _keys[keyId] = keyData;
            }
        }

        /// <summary>
        /// In-memory IEnvelopeKeyStore implementation for testing Envelope mode.
        /// Uses AES-256-ECB for key wrapping (simplified for test use;
        /// production would use AES-KW or RSA-OAEP via HSM).
        /// </summary>
        private sealed class InMemoryEnvelopeKeyStore : IEnvelopeKeyStore
        {
            private readonly BoundedDictionary<string, byte[]> _keks = new BoundedDictionary<string, byte[]>(1000);
            private readonly BoundedDictionary<string, byte[]> _directKeys = new BoundedDictionary<string, byte[]>(1000);
            private string _currentKeyId = "default-kek";

            public IReadOnlyList<string> SupportedWrappingAlgorithms =>
                new[] { "AES-256-WRAP" };

            public bool SupportsHsmKeyGeneration => false;

            public Task<string> GetCurrentKeyIdAsync() => Task.FromResult(_currentKeyId);

            public byte[] GetKey(string keyId)
            {
                if (!_directKeys.TryGetValue(keyId, out var key))
                {
                    key = RandomNumberGenerator.GetBytes(32);
                    _directKeys[keyId] = key;
                }
                return key;
            }

            public Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context)
            {
                return Task.FromResult(GetKey(keyId));
            }

            public Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context)
            {
                var key = RandomNumberGenerator.GetBytes(32);
                _directKeys[keyId] = key;
                return Task.FromResult(key);
            }

            public Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
            {
                var kek = GetOrCreateKek(kekId);
                var wrapped = AesWrap(kek, dataKey);
                return Task.FromResult(wrapped);
            }

            public Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
            {
                if (!_keks.TryGetValue(kekId, out var kek))
                {
                    throw new CryptographicException(
                        $"KEK '{kekId}' not found. Cannot unwrap DEK.");
                }
                var unwrapped = AesUnwrap(kek, wrappedKey);
                return Task.FromResult(unwrapped);
            }

            /// <summary>
            /// Stores a specific KEK for testing purposes.
            /// </summary>
            public void StoreKek(string kekId, byte[] kekData)
            {
                _keks[kekId] = kekData;
            }

            /// <summary>
            /// Gets or auto-creates a KEK for a given ID.
            /// </summary>
            public byte[] GetOrCreateKek(string kekId)
            {
                return _keks.GetOrAdd(kekId, _ => RandomNumberGenerator.GetBytes(32));
            }

            /// <summary>
            /// AES-based key wrapping using AES-GCM for authenticated wrapping.
            /// Format: [nonce:12][ciphertext:keyLen][tag:16]
            /// </summary>
            private static byte[] AesWrap(byte[] kek, byte[] plainKey)
            {
                var nonce = RandomNumberGenerator.GetBytes(12);
                var ciphertext = new byte[plainKey.Length];
                var tag = new byte[16];

                using var aes = new AesGcm(kek, 16);
                aes.Encrypt(nonce, plainKey, ciphertext, tag);

                var result = new byte[12 + ciphertext.Length + 16];
                Buffer.BlockCopy(nonce, 0, result, 0, 12);
                Buffer.BlockCopy(ciphertext, 0, result, 12, ciphertext.Length);
                Buffer.BlockCopy(tag, 0, result, 12 + ciphertext.Length, 16);
                return result;
            }

            /// <summary>
            /// AES-based key unwrapping using AES-GCM.
            /// </summary>
            private static byte[] AesUnwrap(byte[] kek, byte[] wrapped)
            {
                if (wrapped.Length < 28) // 12 (nonce) + 0 (min) + 16 (tag)
                    throw new CryptographicException("Wrapped key data too short.");

                var nonce = new byte[12];
                var tag = new byte[16];
                var ciphertextLen = wrapped.Length - 12 - 16;
                var ciphertext = new byte[ciphertextLen];
                var plaintext = new byte[ciphertextLen];

                Buffer.BlockCopy(wrapped, 0, nonce, 0, 12);
                Buffer.BlockCopy(wrapped, 12, ciphertext, 0, ciphertextLen);
                Buffer.BlockCopy(wrapped, 12 + ciphertextLen, tag, 0, 16);

                using var aes = new AesGcm(kek, 16);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
                return plaintext;
            }
        }

        /// <summary>
        /// Simple test security context.
        /// </summary>
        private sealed class TestSecurityContext : ISecurityContext
        {
            public string UserId { get; init; } = "test-user";
            public string? TenantId { get; init; } = "test-tenant";
            public IEnumerable<string> Roles { get; init; } = new[] { "admin" };
            public bool IsSystemAdmin { get; init; } = true;
        }

        /// <summary>
        /// Concrete AES-256-GCM encryption strategy for end-to-end testing.
        /// This is a real encryption implementation, not a stub.
        /// </summary>
        private sealed class Aes256GcmTestStrategy : EncryptionStrategyBase
        {
            public override CipherInfo CipherInfo => new()
            {
                AlgorithmName = "AES-256-GCM",
                KeySizeBits = 256,
                BlockSizeBytes = 16,
                IvSizeBytes = 12,
                TagSizeBytes = 16,
                Capabilities = CipherCapabilities.AeadCipher,
                SecurityLevel = SecurityLevel.High
            };

            public override string StrategyId => "aes-256-gcm-test";
            public override string StrategyName => "AES-256-GCM Test";

            protected override Task<byte[]> EncryptCoreAsync(
                byte[] plaintext, byte[] key, byte[]? associatedData,
                CancellationToken cancellationToken)
            {
                var iv = GenerateIv();
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[16];

                using var aes = new AesGcm(key, 16);
                aes.Encrypt(iv, plaintext, ciphertext, tag, associatedData);

                return Task.FromResult(CombineIvAndCiphertext(iv, ciphertext, tag));
            }

            protected override Task<byte[]> DecryptCoreAsync(
                byte[] ciphertext, byte[] key, byte[]? associatedData,
                CancellationToken cancellationToken)
            {
                var (iv, encData, tag) = SplitCiphertext(ciphertext);
                var plaintext = new byte[encData.Length];

                using var aes = new AesGcm(key, 16);
                aes.Decrypt(iv, encData, tag!, plaintext, associatedData);

                return Task.FromResult(plaintext);
            }
        }

        #endregion

        #region Integration Tests

        [Fact]
        public async Task Direct_Mode_Encrypt_Decrypt_RoundTrip()
        {
            // Arrange
            var keyStore = new InMemoryKeyStore();
            var strategy = new Aes256GcmTestStrategy();
            var context = new TestSecurityContext();

            var originalData = Encoding.UTF8.GetBytes(
                "Sensitive data requiring encryption in Direct mode.");
            var keyId = "direct-key-1";
            var key = await keyStore.GetKeyAsync(keyId, context);

            _output.WriteLine($"Original data length: {originalData.Length} bytes");
            _output.WriteLine($"Key ID: {keyId}, Key length: {key.Length} bytes");

            // Act - Encrypt
            var encrypted = await strategy.EncryptAsync(originalData, key);

            // Build Direct mode metadata
            var metadata = new EncryptionMetadata
            {
                EncryptionPluginId = strategy.StrategyId,
                KeyMode = KeyManagementMode.Direct,
                KeyId = keyId,
                EncryptedBy = context.UserId
            };

            _output.WriteLine($"Encrypted data length: {encrypted.Length} bytes");
            _output.WriteLine($"Metadata mode: {metadata.KeyMode}");

            // Act - Decrypt using key from metadata
            var decryptKey = await keyStore.GetKeyAsync(metadata.KeyId!, context);
            var decrypted = await strategy.DecryptAsync(encrypted, decryptKey);

            // Assert
            Assert.Equal(originalData, decrypted);
            Assert.Equal(KeyManagementMode.Direct, metadata.KeyMode);
            Assert.Equal(keyId, metadata.KeyId);
            Assert.Null(metadata.WrappedDek);
            Assert.Null(metadata.KekId);

            _output.WriteLine("PASS: Direct mode round-trip verified.");
        }

        [Fact]
        public async Task Envelope_Mode_Encrypt_Decrypt_RoundTrip()
        {
            // Arrange
            var envelopeStore = new InMemoryEnvelopeKeyStore();
            var strategy = new Aes256GcmTestStrategy();
            var context = new TestSecurityContext();

            var originalData = Encoding.UTF8.GetBytes(
                "Sensitive data requiring envelope encryption with KEK wrapping.");
            var kekId = "kek-primary";

            // Generate a unique DEK for this encryption operation
            var dek = strategy.GenerateKey();
            _output.WriteLine($"Generated DEK: {dek.Length} bytes");
            _output.WriteLine($"KEK ID: {kekId}");

            // Act - Wrap DEK with KEK (envelope step 1)
            var wrappedDek = await envelopeStore.WrapKeyAsync(kekId, dek, context);
            _output.WriteLine($"Wrapped DEK: {wrappedDek.Length} bytes");

            // Act - Encrypt data with DEK (envelope step 2)
            var encrypted = await strategy.EncryptAsync(originalData, dek);

            // Build Envelope mode metadata
            var metadata = new EncryptionMetadata
            {
                EncryptionPluginId = strategy.StrategyId,
                KeyMode = KeyManagementMode.Envelope,
                WrappedDek = wrappedDek,
                KekId = kekId,
                EncryptedBy = context.UserId
            };

            _output.WriteLine($"Encrypted data length: {encrypted.Length} bytes");
            _output.WriteLine($"Metadata mode: {metadata.KeyMode}");

            // Act - Decrypt: unwrap DEK first, then decrypt data
            var unwrappedDek = await envelopeStore.UnwrapKeyAsync(
                metadata.KekId!, metadata.WrappedDek!, context);
            var decrypted = await strategy.DecryptAsync(encrypted, unwrappedDek);

            // Assert
            Assert.Equal(originalData, decrypted);
            Assert.Equal(dek, unwrappedDek);
            Assert.Equal(KeyManagementMode.Envelope, metadata.KeyMode);
            Assert.NotNull(metadata.WrappedDek);
            Assert.Equal(kekId, metadata.KekId);

            _output.WriteLine("PASS: Envelope mode round-trip verified.");
        }

        [Fact]
        public async Task Envelope_Mode_Metadata_Contains_WrappedDek()
        {
            // Arrange
            var envelopeStore = new InMemoryEnvelopeKeyStore();
            var strategy = new Aes256GcmTestStrategy();
            var context = new TestSecurityContext();

            var dek = strategy.GenerateKey();
            var kekId = "kek-metadata-test";

            // Act
            var wrappedDek = await envelopeStore.WrapKeyAsync(kekId, dek, context);

            var metadata = new EncryptionMetadata
            {
                EncryptionPluginId = strategy.StrategyId,
                KeyMode = KeyManagementMode.Envelope,
                WrappedDek = wrappedDek,
                KekId = kekId,
                KeyStorePluginId = "test-envelope-store",
                EncryptedBy = context.UserId,
                Version = 1
            };

            // Assert - metadata contains all required envelope fields
            Assert.Equal(KeyManagementMode.Envelope, metadata.KeyMode);
            Assert.NotNull(metadata.WrappedDek);
            Assert.NotEmpty(metadata.WrappedDek);
            Assert.Equal(kekId, metadata.KekId);
            Assert.Equal("test-envelope-store", metadata.KeyStorePluginId);
            Assert.Equal(strategy.StrategyId, metadata.EncryptionPluginId);
            Assert.Equal(context.UserId, metadata.EncryptedBy);
            Assert.Equal(1, metadata.Version);

            // Verify wrapped DEK is not the same as plain DEK
            Assert.NotEqual(dek, metadata.WrappedDek);
            Assert.True(metadata.WrappedDek.Length > 0);

            _output.WriteLine($"Wrapped DEK length: {metadata.WrappedDek.Length} bytes");
            _output.WriteLine($"Plain DEK length: {dek.Length} bytes");
            _output.WriteLine($"KEK ID in metadata: {metadata.KekId}");
            _output.WriteLine($"Key store plugin: {metadata.KeyStorePluginId}");
            _output.WriteLine("PASS: Envelope metadata verified with wrapped DEK and KEK ID.");
        }

        [Fact]
        public async Task Direct_And_Envelope_Produce_Different_Metadata()
        {
            // Arrange
            var directKeyStore = new InMemoryKeyStore();
            var envelopeStore = new InMemoryEnvelopeKeyStore();
            var strategy = new Aes256GcmTestStrategy();
            var context = new TestSecurityContext();

            var plaintext = Encoding.UTF8.GetBytes("Same data encrypted two ways.");

            // Act - Direct mode encryption
            var directKeyId = "direct-key-compare";
            var directKey = await directKeyStore.GetKeyAsync(directKeyId, context);
            var directEncrypted = await strategy.EncryptAsync(plaintext, directKey);

            var directMetadata = new EncryptionMetadata
            {
                EncryptionPluginId = strategy.StrategyId,
                KeyMode = KeyManagementMode.Direct,
                KeyId = directKeyId,
                EncryptedBy = context.UserId
            };

            // Act - Envelope mode encryption
            var kekId = "kek-compare";
            var dek = strategy.GenerateKey();
            var wrappedDek = await envelopeStore.WrapKeyAsync(kekId, dek, context);
            var envelopeEncrypted = await strategy.EncryptAsync(plaintext, dek);

            var envelopeMetadata = new EncryptionMetadata
            {
                EncryptionPluginId = strategy.StrategyId,
                KeyMode = KeyManagementMode.Envelope,
                WrappedDek = wrappedDek,
                KekId = kekId,
                EncryptedBy = context.UserId
            };

            // Assert - metadata structures are fundamentally different
            Assert.NotEqual(directMetadata.KeyMode, envelopeMetadata.KeyMode);

            // Direct has KeyId, not WrappedDek
            Assert.NotNull(directMetadata.KeyId);
            Assert.Null(directMetadata.WrappedDek);
            Assert.Null(directMetadata.KekId);

            // Envelope has WrappedDek and KekId, not KeyId
            Assert.Null(envelopeMetadata.KeyId);
            Assert.NotNull(envelopeMetadata.WrappedDek);
            Assert.NotNull(envelopeMetadata.KekId);

            // Both still use the same encryption plugin
            Assert.Equal(directMetadata.EncryptionPluginId, envelopeMetadata.EncryptionPluginId);

            // Ciphertexts are different (different keys, different IVs)
            Assert.NotEqual(directEncrypted, envelopeEncrypted);

            _output.WriteLine($"Direct metadata: KeyMode={directMetadata.KeyMode}, " +
                $"KeyId={directMetadata.KeyId}, WrappedDek=null");
            _output.WriteLine($"Envelope metadata: KeyMode={envelopeMetadata.KeyMode}, " +
                $"KekId={envelopeMetadata.KekId}, WrappedDek={envelopeMetadata.WrappedDek?.Length} bytes");
            _output.WriteLine("PASS: Direct and Envelope produce structurally different metadata.");
        }

        [Fact]
        public async Task Envelope_Mode_Key_Rotation_Without_Reencrypt()
        {
            // Arrange
            var envelopeStore = new InMemoryEnvelopeKeyStore();
            var strategy = new Aes256GcmTestStrategy();
            var context = new TestSecurityContext();

            var originalData = Encoding.UTF8.GetBytes(
                "Data that should survive KEK rotation without re-encryption.");

            // Step 1: Encrypt with original KEK
            var originalKekId = "kek-v1";
            var dek = strategy.GenerateKey();
            var wrappedDekV1 = await envelopeStore.WrapKeyAsync(originalKekId, dek, context);
            var encrypted = await strategy.EncryptAsync(originalData, dek);

            _output.WriteLine($"Original KEK: {originalKekId}");
            _output.WriteLine($"Wrapped DEK v1: {wrappedDekV1.Length} bytes");
            _output.WriteLine($"Encrypted data: {encrypted.Length} bytes");

            // Step 2: Unwrap DEK with old KEK
            var unwrappedDek = await envelopeStore.UnwrapKeyAsync(
                originalKekId, wrappedDekV1, context);

            // Step 3: Re-wrap the same DEK with a NEW KEK (rotation)
            var newKekId = "kek-v2";
            var wrappedDekV2 = await envelopeStore.WrapKeyAsync(newKekId, unwrappedDek, context);

            _output.WriteLine($"New KEK: {newKekId}");
            _output.WriteLine($"Wrapped DEK v2: {wrappedDekV2.Length} bytes");

            // Step 4: Verify new wrapped DEK works for decryption
            var dekFromNewKek = await envelopeStore.UnwrapKeyAsync(
                newKekId, wrappedDekV2, context);
            var decrypted = await strategy.DecryptAsync(encrypted, dekFromNewKek);

            Assert.Equal(originalData, decrypted);
            Assert.Equal(dek, dekFromNewKek);

            // Step 5: Verify old wrapped DEK with old KEK still works
            var dekFromOldKek = await envelopeStore.UnwrapKeyAsync(
                originalKekId, wrappedDekV1, context);
            var decryptedWithOldKek = await strategy.DecryptAsync(encrypted, dekFromOldKek);
            Assert.Equal(originalData, decryptedWithOldKek);

            // Step 6: Verify the two wrapped DEKs are different
            // (same DEK, different KEKs -> different wrapped forms)
            Assert.NotEqual(wrappedDekV1, wrappedDekV2);

            // The data itself was NEVER re-encrypted -- same ciphertext throughout
            _output.WriteLine("PASS: KEK rotation completed without re-encrypting data.");
            _output.WriteLine("  - Same ciphertext used before and after rotation");
            _output.WriteLine("  - Both old and new wrapped DEKs successfully decrypt");
        }

        [Fact]
        public async Task Invalid_KEK_Unwrap_Throws()
        {
            // Arrange
            var envelopeStore = new InMemoryEnvelopeKeyStore();
            var context = new TestSecurityContext();

            // Create a DEK and wrap it with KEK "kek-valid"
            var dek = RandomNumberGenerator.GetBytes(32);
            var validKekId = "kek-valid";
            var wrappedDek = await envelopeStore.WrapKeyAsync(validKekId, dek, context);

            _output.WriteLine($"DEK wrapped with '{validKekId}'");
            _output.WriteLine($"Wrapped DEK: {wrappedDek.Length} bytes");

            // Act & Assert - try to unwrap with a KEK ID that does not exist
            var invalidKekId = "kek-nonexistent";
            var ex = await Assert.ThrowsAsync<CryptographicException>(
                () => envelopeStore.UnwrapKeyAsync(invalidKekId, wrappedDek, context));

            _output.WriteLine($"Expected exception: {ex.GetType().Name}: {ex.Message}");
            Assert.Contains("kek-nonexistent", ex.Message);

            // Also test: unwrap with a DIFFERENT valid KEK (wrong key material)
            var wrongKekId = "kek-wrong";
            envelopeStore.StoreKek(wrongKekId, RandomNumberGenerator.GetBytes(32));

            // AES-GCM authentication will fail when wrong key is used
            await Assert.ThrowsAnyAsync<CryptographicException>(
                () => envelopeStore.UnwrapKeyAsync(wrongKekId, wrappedDek, context));

            _output.WriteLine("PASS: Invalid KEK unwrap correctly throws CryptographicException.");
        }

        #endregion

        #region EnvelopeHeader Serialization Tests

        [Fact]
        public void EnvelopeHeader_Serialize_Deserialize_RoundTrip()
        {
            // Arrange
            var header = new EnvelopeHeader
            {
                Version = EnvelopeHeader.CurrentVersion,
                KekId = "kek-header-test",
                KeyStorePluginId = "vault-plugin",
                WrappedDek = RandomNumberGenerator.GetBytes(60),
                WrappingAlgorithm = "AES-256-GCM",
                Iv = RandomNumberGenerator.GetBytes(12),
                EncryptionAlgorithm = "AES-256-GCM",
                EncryptionPluginId = "aes256gcm",
                EncryptedAtTicks = DateTime.UtcNow.Ticks,
                EncryptedBy = "test-user"
            };

            // Act
            var serialized = header.Serialize();
            var success = EnvelopeHeader.TryDeserialize(serialized, out var deserialized, out var headerLen);

            // Assert
            Assert.True(success);
            Assert.NotNull(deserialized);
            Assert.Equal(header.KekId, deserialized!.KekId);
            Assert.Equal(header.KeyStorePluginId, deserialized.KeyStorePluginId);
            Assert.Equal(header.WrappedDek, deserialized.WrappedDek);
            Assert.Equal(header.WrappingAlgorithm, deserialized.WrappingAlgorithm);
            Assert.Equal(header.EncryptionAlgorithm, deserialized.EncryptionAlgorithm);
            Assert.Equal(header.EncryptionPluginId, deserialized.EncryptionPluginId);
            Assert.Equal(header.EncryptedBy, deserialized.EncryptedBy);
            Assert.True(headerLen > 0);

            _output.WriteLine($"Header serialized to {serialized.Length} bytes, parsed {headerLen} bytes");
            _output.WriteLine("PASS: EnvelopeHeader round-trip serialization verified.");
        }

        [Fact]
        public void EnvelopeHeader_IsEnvelopeEncrypted_DetectsMagicBytes()
        {
            // Arrange
            var header = new EnvelopeHeader
            {
                KekId = "kek-detect-test",
                WrappedDek = new byte[] { 1, 2, 3, 4 }
            };
            var serialized = header.Serialize();

            var nonEnvelopeData = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

            // Assert
            Assert.True(EnvelopeHeader.IsEnvelopeEncrypted(serialized));
            Assert.False(EnvelopeHeader.IsEnvelopeEncrypted(nonEnvelopeData));
            Assert.False(EnvelopeHeader.IsEnvelopeEncrypted(Array.Empty<byte>()));

            _output.WriteLine("PASS: Magic byte detection works correctly.");
        }

        #endregion

        #region EncryptedPayload Envelope Tests

        [Fact]
        public void EncryptedPayload_Envelope_Mode_RoundTrip()
        {
            // Arrange
            var wrappedDek = RandomNumberGenerator.GetBytes(60);
            var payload = new EncryptedPayload
            {
                AlgorithmId = "aes-256-gcm",
                Version = 1,
                Nonce = RandomNumberGenerator.GetBytes(12),
                Ciphertext = RandomNumberGenerator.GetBytes(256),
                Tag = RandomNumberGenerator.GetBytes(16),
                KeyId = null,
                WrappedDek = wrappedDek,
                KekId = "kek-payload-test"
            };

            // Act
            var bytes = payload.ToBytes();
            var restored = EncryptedPayload.FromBytes(bytes);

            // Assert
            Assert.Equal(payload.AlgorithmId, restored.AlgorithmId);
            Assert.Equal(payload.Version, restored.Version);
            Assert.Equal(payload.Nonce, restored.Nonce);
            Assert.Equal(payload.Ciphertext, restored.Ciphertext);
            Assert.Equal(payload.Tag, restored.Tag);
            Assert.Equal(payload.WrappedDek, restored.WrappedDek);
            Assert.Equal(payload.KekId, restored.KekId);

            _output.WriteLine($"EncryptedPayload serialized to {bytes.Length} bytes");
            _output.WriteLine("PASS: EncryptedPayload envelope round-trip verified.");
        }

        #endregion

        #region Key Store Registry Tests

        [Fact]
        public void DefaultKeyStoreRegistry_RegisterAndRetrieve_Works()
        {
            // Arrange
            var registry = new DefaultKeyStoreRegistry();
            var directStore = new InMemoryKeyStore();
            var envelopeStore = new InMemoryEnvelopeKeyStore();

            // Act
            registry.Register("direct-plugin", directStore);
            registry.RegisterEnvelope("envelope-plugin", envelopeStore);

            // Assert
            var retrievedDirect = registry.GetKeyStore("direct-plugin");
            var retrievedEnvelope = registry.GetEnvelopeKeyStore("envelope-plugin");

            Assert.NotNull(retrievedDirect);
            Assert.NotNull(retrievedEnvelope);
            Assert.Same(directStore, retrievedDirect);
            Assert.Same(envelopeStore, retrievedEnvelope);

            Assert.Contains("direct-plugin", registry.GetRegisteredKeyStoreIds());
            Assert.Contains("envelope-plugin", registry.GetRegisteredEnvelopeKeyStoreIds());

            // Envelope store should also be registered as a regular key store
            Assert.NotNull(registry.GetKeyStore("envelope-plugin"));

            Assert.Null(registry.GetKeyStore("nonexistent"));
            Assert.Null(registry.GetEnvelopeKeyStore("nonexistent"));
            Assert.Null(registry.GetKeyStore(null));
            Assert.Null(registry.GetEnvelopeKeyStore(null));

            _output.WriteLine("PASS: DefaultKeyStoreRegistry register and retrieve verified.");
        }

        #endregion

        #region Encryption Strategy Statistics Tests

        [Fact]
        public async Task EncryptionStrategyBase_TracksStatistics()
        {
            // Arrange
            var strategy = new Aes256GcmTestStrategy();
            var key = strategy.GenerateKey();
            var data = Encoding.UTF8.GetBytes("Statistics tracking test data.");

            // Act
            var encrypted1 = await strategy.EncryptAsync(data, key);
            var encrypted2 = await strategy.EncryptAsync(data, key);
            var decrypted = await strategy.DecryptAsync(encrypted1, key);

            var stats = strategy.GetStatistics();

            // Assert
            Assert.Equal(2, stats.EncryptionCount);
            Assert.Equal(1, stats.DecryptionCount);
            Assert.True(stats.TotalBytesEncrypted > 0);
            Assert.True(stats.TotalBytesDecrypted > 0);
            Assert.Equal(0, stats.EncryptionFailures);
            Assert.Equal(0, stats.DecryptionFailures);

            _output.WriteLine($"Encryptions: {stats.EncryptionCount}");
            _output.WriteLine($"Decryptions: {stats.DecryptionCount}");
            _output.WriteLine($"Total bytes encrypted: {stats.TotalBytesEncrypted}");
            _output.WriteLine("PASS: Statistics tracking verified.");
        }

        #endregion
    }
}
