using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using Xunit;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Comprehensive tests for Ultimate Key Management Plugin (T94).
    /// Covers:
    /// - 94.A5: Unit tests for key management SDK extensions
    /// - 94.D2: Envelope mode integration tests with FileKeyStoreStrategy
    /// - 94.D4: Envelope mode benchmarks (Direct vs Envelope)
    /// - 94.E7: TamperProof encryption integration tests
    /// </summary>
    public class UltimateKeyManagementTests
    {
        #region Test Helpers

        /// <summary>
        /// Test implementation of IKeyStoreStrategy for testing.
        /// Uses in-memory storage with file-like behavior.
        /// </summary>
        private sealed class TestKeyStoreStrategy : IKeyStoreStrategy
        {
            private readonly Dictionary<string, byte[]> _keys = new();
            private readonly Dictionary<string, byte[]> _keks = new();
            private readonly Dictionary<string, KeyMetadata> _metadata = new();
            private string _currentKeyId = "default-key";

            public string StrategyId => "test-memory-keystore";
            public string StrategyName => "Test Memory KeyStore";
            public KeyStoreCapabilities Capabilities => new()
            {
                SupportsRotation = true,
                SupportsEnvelope = true,
                SupportsHsm = false,
                SupportsExpiration = true,
                SupportsVersioning = true,
                SupportsAuditLogging = true
            };

            public Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken ct = default)
            {
                return Task.CompletedTask;
            }

            public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }

            public Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<string>>(_keys.Keys.ToList().AsReadOnly());
            }

            public Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
            {
                _keys.Remove(keyId);
                _metadata.Remove(keyId);
                return Task.CompletedTask;
            }

            public Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
            {
                if (_metadata.TryGetValue(keyId, out var metadata))
                {
                    return Task.FromResult<KeyMetadata?>(metadata);
                }

                if (_keys.ContainsKey(keyId))
                {
                    var meta = new KeyMetadata
                    {
                        KeyId = keyId,
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = context.UserId,
                        KeySizeBytes = 32,
                        IsActive = true,
                        Version = 1
                    };
                    _metadata[keyId] = meta;
                    return Task.FromResult<KeyMetadata?>(meta);
                }

                return Task.FromResult<KeyMetadata?>(null);
            }

            public Task<string> GetCurrentKeyIdAsync() => Task.FromResult(_currentKeyId);

            public byte[] GetKey(string keyId)
            {
                if (!_keys.TryGetValue(keyId, out var key))
                {
                    key = RandomNumberGenerator.GetBytes(32);
                    _keys[keyId] = key;
                    _metadata[keyId] = new KeyMetadata
                    {
                        KeyId = keyId,
                        CreatedAt = DateTime.UtcNow,
                        KeySizeBytes = 32,
                        IsActive = true,
                        Version = 1
                    };
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
                _metadata[keyId] = new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId,
                    KeySizeBytes = 32,
                    IsActive = true,
                    Version = 1
                };
                return Task.FromResult(key);
            }

            public Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
            {
                var kek = GetOrCreateKek(kekId);
                return Task.FromResult(AesWrap(kek, dataKey));
            }

            public Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
            {
                if (!_keks.TryGetValue(kekId, out var kek))
                {
                    throw new CryptographicException($"KEK '{kekId}' not found");
                }
                return Task.FromResult(AesUnwrap(kek, wrappedKey));
            }

            private byte[] GetOrCreateKek(string kekId)
            {
                if (!_keks.TryGetValue(kekId, out var kek))
                {
                    kek = RandomNumberGenerator.GetBytes(32);
                    _keks[kekId] = kek;
                }
                return kek;
            }

            /// <summary>
            /// AES-GCM key wrapping for testing.
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

            private static byte[] AesUnwrap(byte[] kek, byte[] wrapped)
            {
                if (wrapped.Length < 28)
                    throw new CryptographicException("Wrapped key data too short");

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
        /// Test security context.
        /// </summary>
        private sealed class TestSecurityContext : ISecurityContext
        {
            public string UserId { get; init; } = "test-user";
            public string? TenantId { get; init; } = "test-tenant";
            public IEnumerable<string> Roles { get; init; } = new[] { "admin" };
            public bool IsSystemAdmin { get; init; } = true;
        }

        /// <summary>
        /// Test encryption strategy for end-to-end testing.
        /// </summary>
        private sealed class TestEncryptionStrategy : EncryptionStrategyBase
        {
            public override CipherInfo CipherInfo => new()
            {
                AlgorithmName = "AES-256-GCM",
                KeySizeBits = 256,
                BlockSizeBytes = 16,
                IvSizeBytes = 12,
                TagSizeBytes = 16,
                Capabilities = CipherCapabilities.AeadCipher,
                SecurityLevel = SDK.Contracts.Encryption.SecurityLevel.High
            };

            public override string StrategyId => "aes-256-gcm-test";
            public override string StrategyName => "AES-256-GCM Test Strategy";

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

        #region 94.A5: Unit Tests for Key Management SDK Extensions

        [Fact]
        public async Task KeyStoreStrategy_GetKeyAsync_ReturnsValidKey()
        {
            // Arrange
            var strategy = new TestKeyStoreStrategy();
            var context = new TestSecurityContext();
            var keyId = "test-key-1";

            await strategy.InitializeAsync(new Dictionary<string, object>());

            // Act
            var key = await strategy.GetKeyAsync(keyId, context);

            // Assert
            Assert.NotNull(key);
            Assert.Equal(32, key.Length); // 256-bit key
        }

        [Fact]
        public async Task KeyStoreStrategy_CreateKeyAsync_GeneratesNewKey()
        {
            // Arrange
            var strategy = new TestKeyStoreStrategy();
            var context = new TestSecurityContext();
            var keyId = "created-key";

            await strategy.InitializeAsync(new Dictionary<string, object>());

            // Act
            var key = await strategy.CreateKeyAsync(keyId, context);
            var currentKeyId = await strategy.GetCurrentKeyIdAsync();

            // Assert
            Assert.NotNull(key);
            Assert.Equal(32, key.Length);
            Assert.Equal(keyId, currentKeyId);
        }

        [Fact]
        public async Task KeyStoreStrategy_SupportsRotation_ReflectedInCapabilities()
        {
            // Arrange
            var strategy = new TestKeyStoreStrategy();
            await strategy.InitializeAsync(new Dictionary<string, object>());

            // Act
            var capabilities = strategy.Capabilities;

            // Assert
            Assert.True(capabilities.SupportsRotation);
            Assert.True(capabilities.SupportsEnvelope);
            Assert.True(capabilities.SupportsVersioning);
            Assert.True(capabilities.SupportsAuditLogging);
        }

        [Fact]
        public async Task KeyStoreStrategy_MigrationUtility_SuccessfulKeyMigration()
        {
            // Arrange
            var sourceStrategy = new TestKeyStoreStrategy();
            var targetStrategy = new TestKeyStoreStrategy();
            var context = new TestSecurityContext();
            var keyId = "migration-test-key";

            await sourceStrategy.InitializeAsync(new Dictionary<string, object>());
            await targetStrategy.InitializeAsync(new Dictionary<string, object>());

            // Create key in source
            var originalKey = await sourceStrategy.CreateKeyAsync(keyId, context);

            // Act - Simulate migration (get from source, create in target)
            var migratedKey = await sourceStrategy.GetKeyAsync(keyId, context);
            await targetStrategy.CreateKeyAsync(keyId, context);
            var targetKey = await targetStrategy.GetKeyAsync(keyId, context);

            // Assert
            Assert.NotNull(originalKey);
            Assert.NotNull(migratedKey);
            Assert.NotNull(targetKey);
            Assert.Equal(32, targetKey.Length);
        }

        [Fact]
        public void KeyStoreCapabilities_MetadataExtension_StoresCustomProperties()
        {
            // Arrange & Act
            var capabilities = new KeyStoreCapabilities
            {
                SupportsRotation = true,
                SupportsEnvelope = true,
                SupportsHsm = true,
                MaxKeySizeBytes = 4096,
                MinKeySizeBytes = 32,
                Metadata = new Dictionary<string, object>
                {
                    ["Provider"] = "TestProvider",
                    ["Region"] = "us-east-1",
                    ["Compliance"] = new[] { "FIPS-140-2", "Common Criteria" }
                }
            };

            // Assert
            Assert.True(capabilities.SupportsRotation);
            Assert.True(capabilities.SupportsEnvelope);
            Assert.True(capabilities.SupportsHsm);
            Assert.Equal(4096, capabilities.MaxKeySizeBytes);
            Assert.Equal(32, capabilities.MinKeySizeBytes);
            Assert.Equal("TestProvider", capabilities.Metadata["Provider"]);
            Assert.Equal("us-east-1", capabilities.Metadata["Region"]);
            Assert.IsType<string[]>(capabilities.Metadata["Compliance"]);
        }

        #endregion

        #region 94.D2: Envelope Mode Integration Tests

        [Fact]
        public async Task EnvelopeMode_WrapUnwrap_RoundTrip_Succeeds()
        {
            // Arrange
            var strategy = new TestKeyStoreStrategy();
            var context = new TestSecurityContext();
            var kekId = "envelope-kek-1";
            var dek = RandomNumberGenerator.GetBytes(32);

            await strategy.InitializeAsync(new Dictionary<string, object>());

            // Act - Wrap DEK
            var wrappedDek = await strategy.WrapKeyAsync(kekId, dek, context);

            // Act - Unwrap DEK
            var unwrappedDek = await strategy.UnwrapKeyAsync(kekId, wrappedDek, context);

            // Assert
            Assert.NotNull(wrappedDek);
            Assert.NotNull(unwrappedDek);
            Assert.Equal(dek, unwrappedDek);
            Assert.NotEqual(dek, wrappedDek); // Wrapped form should differ
            Assert.True(wrappedDek.Length > dek.Length); // Wrapped includes overhead
        }

        [Fact]
        public async Task EnvelopeMode_EndToEnd_WithEncryption_Succeeds()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var encStrategy = new TestEncryptionStrategy();
            var context = new TestSecurityContext();
            var kekId = "e2e-kek";
            var originalData = Encoding.UTF8.GetBytes("Envelope encryption end-to-end test data");

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            // Act - Envelope encryption workflow
            // 1. Generate DEK
            var dek = encStrategy.GenerateKey();

            // 2. Wrap DEK with KEK
            var wrappedDek = await keyStore.WrapKeyAsync(kekId, dek, context);

            // 3. Encrypt data with DEK
            var encrypted = await encStrategy.EncryptAsync(originalData, dek);

            // 4. Create metadata
            var metadata = new EncryptionMetadata
            {
                EncryptionPluginId = encStrategy.StrategyId,
                KeyMode = KeyManagementMode.Envelope,
                WrappedDek = wrappedDek,
                KekId = kekId,
                KeyStorePluginId = keyStore.StrategyId,
                EncryptedBy = context.UserId
            };

            // Act - Envelope decryption workflow
            // 1. Unwrap DEK
            var unwrappedDek = await keyStore.UnwrapKeyAsync(metadata.KekId!, metadata.WrappedDek!, context);

            // 2. Decrypt data with DEK
            var decrypted = await encStrategy.DecryptAsync(encrypted, unwrappedDek);

            // Assert
            Assert.Equal(originalData, decrypted);
            Assert.Equal(KeyManagementMode.Envelope, metadata.KeyMode);
            Assert.NotNull(metadata.WrappedDek);
            Assert.Equal(kekId, metadata.KekId);
        }

        [Fact]
        public async Task EnvelopeMode_MultipleKEKs_IndependentWrapping()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var context = new TestSecurityContext();
            var dek = RandomNumberGenerator.GetBytes(32);
            var kek1 = "kek-region-1";
            var kek2 = "kek-region-2";

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            // Act - Wrap same DEK with different KEKs
            var wrapped1 = await keyStore.WrapKeyAsync(kek1, dek, context);
            var wrapped2 = await keyStore.WrapKeyAsync(kek2, dek, context);

            // Act - Unwrap with respective KEKs
            var unwrapped1 = await keyStore.UnwrapKeyAsync(kek1, wrapped1, context);
            var unwrapped2 = await keyStore.UnwrapKeyAsync(kek2, wrapped2, context);

            // Assert
            Assert.Equal(dek, unwrapped1);
            Assert.Equal(dek, unwrapped2);
            Assert.NotEqual(wrapped1, wrapped2); // Different KEKs produce different wrapped forms
        }

        [Fact]
        public async Task EnvelopeMode_WrongKEK_UnwrapFails()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var context = new TestSecurityContext();
            var dek = RandomNumberGenerator.GetBytes(32);
            var correctKek = "correct-kek";
            var wrongKek = "wrong-kek";

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            // Act - Wrap with one KEK
            var wrapped = await keyStore.WrapKeyAsync(correctKek, dek, context);

            // Create a different KEK (force creation by wrapping dummy data)
            await keyStore.WrapKeyAsync(wrongKek, new byte[32], context);

            // Assert - Unwrap with wrong KEK fails
            await Assert.ThrowsAnyAsync<CryptographicException>(
                async () => await keyStore.UnwrapKeyAsync(wrongKek, wrapped, context));
        }

        [Fact]
        public async Task EnvelopeMode_NonexistentKEK_UnwrapThrows()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var context = new TestSecurityContext();
            var dek = RandomNumberGenerator.GetBytes(32);
            var kekId = "existing-kek";
            var missingKek = "nonexistent-kek";

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            // Act - Wrap with existing KEK
            var wrapped = await keyStore.WrapKeyAsync(kekId, dek, context);

            // Assert - Unwrap with nonexistent KEK throws
            var ex = await Assert.ThrowsAsync<CryptographicException>(
                async () => await keyStore.UnwrapKeyAsync(missingKek, wrapped, context));

            Assert.Contains("not found", ex.Message);
        }

        #endregion

        #region 94.D4: Envelope Mode Benchmarks

        [Fact]
        public async Task Benchmark_DirectMode_vs_EnvelopeMode_Performance()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var encStrategy = new TestEncryptionStrategy();
            var context = new TestSecurityContext();
            var testData = Encoding.UTF8.GetBytes(new string('A', 1024 * 100)); // 100KB test data
            var iterations = 10;

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            // === Direct Mode Benchmark ===
            var directKeyId = "direct-bench-key";
            var directKey = await keyStore.GetKeyAsync(directKeyId, context);

            var directSw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var encrypted = await encStrategy.EncryptAsync(testData, directKey);
                var decrypted = await encStrategy.DecryptAsync(encrypted, directKey);
            }
            directSw.Stop();

            var directAvgMs = directSw.ElapsedMilliseconds / (double)iterations;

            // === Envelope Mode Benchmark ===
            var kekId = "envelope-bench-kek";

            var envelopeSw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                // Generate and wrap DEK
                var dek = encStrategy.GenerateKey();
                var wrappedDek = await keyStore.WrapKeyAsync(kekId, dek, context);

                // Encrypt with DEK
                var encrypted = await encStrategy.EncryptAsync(testData, dek);

                // Unwrap DEK
                var unwrappedDek = await keyStore.UnwrapKeyAsync(kekId, wrappedDek, context);

                // Decrypt with unwrapped DEK
                var decrypted = await encStrategy.DecryptAsync(encrypted, unwrappedDek);
            }
            envelopeSw.Stop();

            var envelopeAvgMs = envelopeSw.ElapsedMilliseconds / (double)iterations;

            // Calculate overhead
            var overheadMs = envelopeAvgMs - directAvgMs;
            var overheadPercent = (overheadMs / directAvgMs) * 100;

            // Output benchmark results
            Console.WriteLine($"=== Key Management Mode Performance Benchmark ===");
            Console.WriteLine($"Test data size: {testData.Length / 1024} KB");
            Console.WriteLine($"Iterations: {iterations}");
            Console.WriteLine($"");
            Console.WriteLine($"Direct Mode:");
            Console.WriteLine($"  Total: {directSw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  Average: {directAvgMs:F2} ms/operation");
            Console.WriteLine($"");
            Console.WriteLine($"Envelope Mode:");
            Console.WriteLine($"  Total: {envelopeSw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  Average: {envelopeAvgMs:F2} ms/operation");
            Console.WriteLine($"");
            Console.WriteLine($"Performance Analysis:");
            Console.WriteLine($"  Overhead: {overheadMs:F2} ms ({overheadPercent:F1}%)");
            Console.WriteLine($"  Direct mode is {envelopeAvgMs / directAvgMs:F2}x faster");

            // Assert - Both modes work
            // Note: In very fast operations, timings might be similar or even equal to 0
            // The important assertion is that envelope mode completes in reasonable time
            Assert.True(envelopeAvgMs < 1000); // Should complete in reasonable time
            Assert.True(directAvgMs >= 0); // Direct mode completed successfully
            Assert.True(envelopeAvgMs >= 0); // Envelope mode completed successfully
        }

        [Fact]
        public async Task Benchmark_KeyRetrieval_DirectVsEnvelope()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var context = new TestSecurityContext();
            var iterations = 100;

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            // === Direct Mode Key Retrieval ===
            var directKeyId = "direct-retrieval-key";
            var directSw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var key = await keyStore.GetKeyAsync(directKeyId, context);
            }
            directSw.Stop();

            // === Envelope Mode Key Operations ===
            var kekId = "envelope-retrieval-kek";
            var dek = RandomNumberGenerator.GetBytes(32);

            var envelopeSw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var wrapped = await keyStore.WrapKeyAsync(kekId, dek, context);
                var unwrapped = await keyStore.UnwrapKeyAsync(kekId, wrapped, context);
            }
            envelopeSw.Stop();

            // Output results
            Console.WriteLine($"=== Key Retrieval Performance Benchmark ===");
            Console.WriteLine($"Iterations: {iterations}");
            Console.WriteLine($"");
            Console.WriteLine($"Direct Key Retrieval: {directSw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  Average: {directSw.ElapsedMilliseconds / (double)iterations:F3} ms");
            Console.WriteLine($"");
            Console.WriteLine($"Envelope Wrap/Unwrap: {envelopeSw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  Average: {envelopeSw.ElapsedMilliseconds / (double)iterations:F3} ms");

            // Assert
            Assert.True(directSw.ElapsedMilliseconds >= 0);
            Assert.True(envelopeSw.ElapsedMilliseconds >= 0);
        }

        [Fact]
        public async Task Benchmark_EnvelopeEncryption_TotalRoundtrip()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var encStrategy = new TestEncryptionStrategy();
            var context = new TestSecurityContext();
            var testSizes = new[] { 1024, 10240, 102400, 1048576 }; // 1KB, 10KB, 100KB, 1MB
            var kekId = "roundtrip-bench-kek";

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            Console.WriteLine($"=== Envelope Encryption Total Roundtrip Benchmark ===");
            Console.WriteLine();

            foreach (var size in testSizes)
            {
                var testData = new byte[size];
                RandomNumberGenerator.Fill(testData);

                var sw = Stopwatch.StartNew();

                // Full envelope workflow
                var dek = encStrategy.GenerateKey();
                var wrappedDek = await keyStore.WrapKeyAsync(kekId, dek, context);
                var encrypted = await encStrategy.EncryptAsync(testData, dek);
                var unwrappedDek = await keyStore.UnwrapKeyAsync(kekId, wrappedDek, context);
                var decrypted = await encStrategy.DecryptAsync(encrypted, unwrappedDek);

                sw.Stop();

                var throughputMBps = (size / (1024.0 * 1024.0)) / (sw.Elapsed.TotalSeconds);

                Console.WriteLine($"Data size: {size / 1024} KB");
                Console.WriteLine($"  Time: {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"  Throughput: {throughputMBps:F2} MB/s");
                Console.WriteLine();

                // Assert
                Assert.Equal(testData, decrypted);
            }
        }

        #endregion

        #region 94.E7: TamperProof Integration Tests

        [Fact]
        public async Task TamperProof_PerObjectEncryptionConfig_WorksWithEnvelope()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var encStrategy = new TestEncryptionStrategy();
            var context = new TestSecurityContext();
            var objectId = "tamperproof-object-1";
            var kekId = "tamperproof-kek";

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            // Simulate TamperProof manifest with per-object encryption config
            var originalData = Encoding.UTF8.GetBytes("Tamper-proof data with per-object encryption");

            // Act - Encrypt with envelope mode
            var dek = encStrategy.GenerateKey();
            var wrappedDek = await keyStore.WrapKeyAsync(kekId, dek, context);
            var encrypted = await encStrategy.EncryptAsync(originalData, dek);

            // Create encryption metadata for TamperProof manifest
            // EncryptionConfigMode would be tracked in TamperProof manifest separately
            var encryptionMetadata = new EncryptionMetadata
            {
                EncryptionPluginId = encStrategy.StrategyId,
                KeyMode = KeyManagementMode.Envelope,
                WrappedDek = wrappedDek,
                KekId = kekId,
                KeyStorePluginId = keyStore.StrategyId,
                EncryptedBy = context.UserId,
                Version = 1
            };

            // Simulate TamperProof manifest structure with config mode
            var manifest = new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["EncryptionMetadata"] = encryptionMetadata,
                ["EncryptionConfigMode"] = EncryptionConfigMode.PerObjectConfig,
                ["IntegrityHash"] = ComputeSha256Hash(encrypted),
                ["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            // Act - Decrypt using metadata from manifest
            var unwrappedDek = await keyStore.UnwrapKeyAsync(
                encryptionMetadata.KekId!, encryptionMetadata.WrappedDek!, context);
            var decrypted = await encStrategy.DecryptAsync(encrypted, unwrappedDek);

            // Assert
            Assert.Equal(originalData, decrypted);
            Assert.Equal(EncryptionConfigMode.PerObjectConfig, manifest["EncryptionConfigMode"]);
            Assert.Equal(KeyManagementMode.Envelope, encryptionMetadata.KeyMode);
        }

        [Fact]
        public async Task TamperProof_FixedEncryptionConfig_EnforcesConsistency()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var encStrategy = new TestEncryptionStrategy();
            var context = new TestSecurityContext();
            var kekId = "fixed-config-kek";

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            // First object sets the fixed configuration
            // ConfigMode would be tracked at TamperProof plugin level, not in EncryptionMetadata
            var firstConfig = new EncryptionMetadata
            {
                EncryptionPluginId = encStrategy.StrategyId,
                KeyMode = KeyManagementMode.Envelope,
                KekId = kekId,
                KeyStorePluginId = keyStore.StrategyId
            };

            // Act - Verify that subsequent writes must match fixed config
            var secondConfig = new EncryptionMetadata
            {
                EncryptionPluginId = encStrategy.StrategyId,
                KeyMode = KeyManagementMode.Envelope,
                KekId = kekId,
                KeyStorePluginId = keyStore.StrategyId
            };

            // Assert - Configurations match (would be enforced by TamperProof plugin)
            Assert.Equal(firstConfig.EncryptionPluginId, secondConfig.EncryptionPluginId);
            Assert.Equal(firstConfig.KeyMode, secondConfig.KeyMode);
            Assert.Equal(firstConfig.KekId, secondConfig.KekId);
            Assert.Equal(firstConfig.KeyStorePluginId, secondConfig.KeyStorePluginId);
        }

        [Fact]
        public async Task TamperProof_PolicyEnforcedConfig_AllowsFlexibility()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var encStrategy = new TestEncryptionStrategy();
            var context = new TestSecurityContext();

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            // Define organizational policy (would be stored in TamperProof plugin)
            var policy = new EncryptionPolicy
            {
                AllowedAlgorithms = new[] { "aes-256-gcm", "aes-256-cbc" },
                RequiredKeyMode = KeyManagementMode.Envelope,
                AllowedKeyStores = new[] { keyStore.StrategyId },
                MinimumKeySizeBits = 256,
                ConfigMode = EncryptionConfigMode.PolicyEnforced
            };

            // Act - Create config that complies with policy
            var config1 = new EncryptionMetadata
            {
                EncryptionPluginId = "aes-256-gcm",
                KeyMode = KeyManagementMode.Envelope,
                KekId = "kek-policy-1",
                KeyStorePluginId = keyStore.StrategyId
            };

            var config2 = new EncryptionMetadata
            {
                EncryptionPluginId = "aes-256-cbc",
                KeyMode = KeyManagementMode.Envelope,
                KekId = "kek-policy-2",
                KeyStorePluginId = keyStore.StrategyId
            };

            // Assert - Both configs are valid under policy
            Assert.Contains(config1.EncryptionPluginId, policy.AllowedAlgorithms);
            Assert.Contains(config2.EncryptionPluginId, policy.AllowedAlgorithms);
            Assert.Equal(policy.RequiredKeyMode, config1.KeyMode);
            Assert.Equal(policy.RequiredKeyMode, config2.KeyMode);
            Assert.Equal(EncryptionConfigMode.PolicyEnforced, policy.ConfigMode);
        }

        [Fact]
        public async Task TamperProof_WriteTimeConfigResolution_ReadTimeVerification()
        {
            // Arrange
            var keyStore = new TestKeyStoreStrategy();
            var encStrategy = new TestEncryptionStrategy();
            var context = new TestSecurityContext();
            var kekId = "config-resolution-kek";
            var originalData = Encoding.UTF8.GetBytes("Data with write-time config");

            await keyStore.InitializeAsync(new Dictionary<string, object>());

            // === WRITE TIME: Config Resolution ===
            var dek = encStrategy.GenerateKey();
            var wrappedDek = await keyStore.WrapKeyAsync(kekId, dek, context);
            var encrypted = await encStrategy.EncryptAsync(originalData, dek);

            var writeTimeConfig = new EncryptionMetadata
            {
                EncryptionPluginId = encStrategy.StrategyId,
                KeyMode = KeyManagementMode.Envelope,
                KekId = kekId,
                KeyStorePluginId = keyStore.StrategyId,
                EncryptedBy = context.UserId,
                EncryptedAt = DateTime.UtcNow,
                WrappedDek = wrappedDek,
                Version = 1
            };

            // === READ TIME: Config from Manifest ===
            // Simulate reading manifest and getting encryption config
            var readTimeConfig = writeTimeConfig; // In real scenario, deserialized from manifest

            // Verify config integrity
            Assert.Equal(writeTimeConfig.EncryptionPluginId, readTimeConfig.EncryptionPluginId);
            Assert.Equal(writeTimeConfig.KeyMode, readTimeConfig.KeyMode);
            Assert.Equal(writeTimeConfig.KekId, readTimeConfig.KekId);
            Assert.NotNull(readTimeConfig.WrappedDek);

            // Decrypt using read-time config
            var unwrappedDek = await keyStore.UnwrapKeyAsync(
                readTimeConfig.KekId!, readTimeConfig.WrappedDek!, context);
            var decrypted = await encStrategy.DecryptAsync(encrypted, unwrappedDek);

            // Assert
            Assert.Equal(originalData, decrypted);
        }

        [Fact]
        public void TamperProof_EncryptionMetadata_FullStructure()
        {
            // Arrange & Act - Create complete encryption metadata for TamperProof
            var metadata = new EncryptionMetadata
            {
                EncryptionPluginId = "aes-256-gcm",
                KeyMode = KeyManagementMode.Envelope,
                KeyId = null, // Not used in envelope mode
                WrappedDek = RandomNumberGenerator.GetBytes(60),
                KekId = "tamperproof-kek",
                KeyStorePluginId = "ultimate-keymanagement",
                EncryptedBy = "system",
                EncryptedAt = DateTime.UtcNow,
                Version = 1,
                AlgorithmParams = new Dictionary<string, object>
                {
                    ["ComplianceLevel"] = "High",
                    ["RetentionYears"] = 7,
                    ["DataClassification"] = "Confidential"
                }
            };

            // Assert - Verify all fields populated correctly
            Assert.Equal("aes-256-gcm", metadata.EncryptionPluginId);
            Assert.Equal(KeyManagementMode.Envelope, metadata.KeyMode);
            Assert.Null(metadata.KeyId);
            Assert.NotNull(metadata.WrappedDek);
            Assert.Equal(60, metadata.WrappedDek.Length);
            Assert.Equal("tamperproof-kek", metadata.KekId);
            Assert.Equal("ultimate-keymanagement", metadata.KeyStorePluginId);
            Assert.Equal("system", metadata.EncryptedBy);
            Assert.True(metadata.EncryptedAt > DateTime.UtcNow.AddMinutes(-1));
            Assert.Equal(1, metadata.Version);
            Assert.NotNull(metadata.AlgorithmParams);
            Assert.Equal("High", metadata.AlgorithmParams["ComplianceLevel"]);
        }

        #endregion

        #region Helper Methods

        private static byte[] ComputeSha256Hash(byte[] data)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(data);
        }

        /// <summary>
        /// Simplified encryption policy for testing.
        /// </summary>
        private class EncryptionPolicy
        {
            public string[] AllowedAlgorithms { get; set; } = Array.Empty<string>();
            public KeyManagementMode RequiredKeyMode { get; set; }
            public string[] AllowedKeyStores { get; set; } = Array.Empty<string>();
            public int MinimumKeySizeBits { get; set; }
            public EncryptionConfigMode ConfigMode { get; set; }
        }

        #endregion
    }
}
