using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Security;
using Xunit;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Performance benchmarks comparing Direct vs Envelope encryption modes.
    /// Measures encryption/decryption throughput and key wrap/unwrap overhead
    /// across multiple data sizes (1KB, 10KB, 100KB, 1MB).
    /// Covers T5.1.4: Envelope mode benchmarks.
    /// </summary>
    public class EnvelopeEncryptionBenchmarks
    {
        private readonly ITestOutputHelper _output;

        private static readonly int[] DataSizes = { 1024, 10240, 102400, 1048576 };
        private static readonly string[] DataSizeLabels = { "1KB", "10KB", "100KB", "1MB" };
        private const int WarmupIterations = 10;
        private const int BenchmarkIterations = 200;

        public EnvelopeEncryptionBenchmarks(ITestOutputHelper output)
        {
            _output = output;
        }

        #region Test Helpers (shared with integration tests)

        private sealed class BenchmarkKeyStore : IKeyStore
        {
            private readonly ConcurrentDictionary<string, byte[]> _keys = new();

            public Task<string> GetCurrentKeyIdAsync() => Task.FromResult("benchmark-key");

            public byte[] GetKey(string keyId)
            {
                return _keys.GetOrAdd(keyId, _ => RandomNumberGenerator.GetBytes(32));
            }

            public Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context)
            {
                return Task.FromResult(GetKey(keyId));
            }

            public Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context)
            {
                var key = RandomNumberGenerator.GetBytes(32);
                _keys[keyId] = key;
                return Task.FromResult(key);
            }
        }

        private sealed class BenchmarkEnvelopeKeyStore : IEnvelopeKeyStore
        {
            private readonly ConcurrentDictionary<string, byte[]> _keks = new();

            public IReadOnlyList<string> SupportedWrappingAlgorithms => new[] { "AES-256-WRAP" };
            public bool SupportsHsmKeyGeneration => false;

            public Task<string> GetCurrentKeyIdAsync() => Task.FromResult("benchmark-kek");

            public byte[] GetKey(string keyId)
            {
                return _keks.GetOrAdd(keyId, _ => RandomNumberGenerator.GetBytes(32));
            }

            public Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context)
            {
                return Task.FromResult(GetKey(keyId));
            }

            public Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context)
            {
                var key = RandomNumberGenerator.GetBytes(32);
                _keks[keyId] = key;
                return Task.FromResult(key);
            }

            public Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
            {
                var kek = GetKey(kekId);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var ciphertext = new byte[dataKey.Length];
                var tag = new byte[16];

                using var aes = new AesGcm(kek, 16);
                aes.Encrypt(nonce, dataKey, ciphertext, tag);

                var result = new byte[12 + ciphertext.Length + 16];
                Buffer.BlockCopy(nonce, 0, result, 0, 12);
                Buffer.BlockCopy(ciphertext, 0, result, 12, ciphertext.Length);
                Buffer.BlockCopy(tag, 0, result, 12 + ciphertext.Length, 16);
                return Task.FromResult(result);
            }

            public Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
            {
                var kek = GetKey(kekId);
                var nonce = new byte[12];
                var tag = new byte[16];
                var ciphertextLen = wrappedKey.Length - 12 - 16;
                var ciphertext = new byte[ciphertextLen];
                var plaintext = new byte[ciphertextLen];

                Buffer.BlockCopy(wrappedKey, 0, nonce, 0, 12);
                Buffer.BlockCopy(wrappedKey, 12, ciphertext, 0, ciphertextLen);
                Buffer.BlockCopy(wrappedKey, 12 + ciphertextLen, tag, 0, 16);

                using var aes = new AesGcm(kek, 16);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
                return Task.FromResult(plaintext);
            }
        }

        private sealed class BenchmarkSecurityContext : ISecurityContext
        {
            public string UserId => "benchmark-user";
            public string? TenantId => "benchmark-tenant";
            public IEnumerable<string> Roles => new[] { "admin" };
            public bool IsSystemAdmin => true;
        }

        private sealed class Aes256GcmBenchmarkStrategy : EncryptionStrategyBase
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

            public override string StrategyId => "aes-256-gcm-bench";
            public override string StrategyName => "AES-256-GCM Benchmark";

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

        #region Benchmark Methods

        private record BenchmarkResult(
            string Label,
            int DataSizeBytes,
            int Iterations,
            double TotalMs,
            double AvgMs,
            double MinMs,
            double MaxMs,
            double ThroughputMBps);

        private static BenchmarkResult RunBenchmark(
            string label, int dataSize, int iterations,
            Func<Task> operation)
        {
            // Warmup
            for (int i = 0; i < WarmupIterations; i++)
            {
                operation().GetAwaiter().GetResult();
            }

            var timings = new double[iterations];
            var sw = new Stopwatch();

            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                operation().GetAwaiter().GetResult();
                sw.Stop();
                timings[i] = sw.Elapsed.TotalMilliseconds;
            }

            var totalMs = 0.0;
            var minMs = double.MaxValue;
            var maxMs = double.MinValue;

            for (int i = 0; i < timings.Length; i++)
            {
                totalMs += timings[i];
                if (timings[i] < minMs) minMs = timings[i];
                if (timings[i] > maxMs) maxMs = timings[i];
            }

            var avgMs = totalMs / iterations;
            var throughputMBps = (dataSize / (1024.0 * 1024.0)) / (avgMs / 1000.0);

            return new BenchmarkResult(
                label, dataSize, iterations,
                totalMs, avgMs, minMs, maxMs, throughputMBps);
        }

        private void PrintResult(BenchmarkResult r)
        {
            _output.WriteLine(
                $"  {r.Label,-45} | Avg: {r.AvgMs,8:F3}ms | " +
                $"Min: {r.MinMs,8:F3}ms | Max: {r.MaxMs,8:F3}ms | " +
                $"Throughput: {r.ThroughputMBps,8:F1} MB/s");
        }

        #endregion

        #region Benchmark Tests

        [Fact]
        public async Task Benchmark_Direct_Mode_Encryption()
        {
            var strategy = new Aes256GcmBenchmarkStrategy();
            var keyStore = new BenchmarkKeyStore();
            var context = new BenchmarkSecurityContext();
            var key = await keyStore.GetKeyAsync("bench-key", context);

            _output.WriteLine("=== Direct Mode Encryption Benchmark ===");
            _output.WriteLine($"Iterations: {BenchmarkIterations}, Warmup: {WarmupIterations}");
            _output.WriteLine(new string('-', 100));

            for (int s = 0; s < DataSizes.Length; s++)
            {
                var data = RandomNumberGenerator.GetBytes(DataSizes[s]);
                var result = RunBenchmark(
                    $"Direct Encrypt {DataSizeLabels[s]}",
                    DataSizes[s],
                    BenchmarkIterations,
                    () => strategy.EncryptAsync(data, key));

                PrintResult(result);

                // Sanity check: encryption produces output
                var encrypted = await strategy.EncryptAsync(data, key);
                Assert.True(encrypted.Length >= data.Length);
            }

            _output.WriteLine("PASS: Direct mode encryption benchmark complete.");
        }

        [Fact]
        public async Task Benchmark_Envelope_Mode_Encryption()
        {
            var strategy = new Aes256GcmBenchmarkStrategy();
            var envelopeStore = new BenchmarkEnvelopeKeyStore();
            var context = new BenchmarkSecurityContext();
            var kekId = "bench-kek";

            _output.WriteLine("=== Envelope Mode Encryption Benchmark ===");
            _output.WriteLine($"Iterations: {BenchmarkIterations}, Warmup: {WarmupIterations}");
            _output.WriteLine("(Includes DEK generation + key wrapping + encryption)");
            _output.WriteLine(new string('-', 100));

            for (int s = 0; s < DataSizes.Length; s++)
            {
                var data = RandomNumberGenerator.GetBytes(DataSizes[s]);
                var result = RunBenchmark(
                    $"Envelope Encrypt {DataSizeLabels[s]}",
                    DataSizes[s],
                    BenchmarkIterations,
                    async () =>
                    {
                        // Full envelope flow: generate DEK, wrap, encrypt
                        var dek = strategy.GenerateKey();
                        await envelopeStore.WrapKeyAsync(kekId, dek, context);
                        await strategy.EncryptAsync(data, dek);
                    });

                PrintResult(result);
            }

            _output.WriteLine("PASS: Envelope mode encryption benchmark complete.");
        }

        [Fact]
        public async Task Benchmark_Direct_Mode_Decryption()
        {
            var strategy = new Aes256GcmBenchmarkStrategy();
            var keyStore = new BenchmarkKeyStore();
            var context = new BenchmarkSecurityContext();
            var key = await keyStore.GetKeyAsync("bench-key", context);

            _output.WriteLine("=== Direct Mode Decryption Benchmark ===");
            _output.WriteLine($"Iterations: {BenchmarkIterations}, Warmup: {WarmupIterations}");
            _output.WriteLine(new string('-', 100));

            for (int s = 0; s < DataSizes.Length; s++)
            {
                var data = RandomNumberGenerator.GetBytes(DataSizes[s]);
                var encrypted = await strategy.EncryptAsync(data, key);

                var result = RunBenchmark(
                    $"Direct Decrypt {DataSizeLabels[s]}",
                    DataSizes[s],
                    BenchmarkIterations,
                    () => strategy.DecryptAsync(encrypted, key));

                PrintResult(result);

                // Verify decryption is correct
                var decrypted = await strategy.DecryptAsync(encrypted, key);
                Assert.Equal(data, decrypted);
            }

            _output.WriteLine("PASS: Direct mode decryption benchmark complete.");
        }

        [Fact]
        public async Task Benchmark_Envelope_Mode_Decryption()
        {
            var strategy = new Aes256GcmBenchmarkStrategy();
            var envelopeStore = new BenchmarkEnvelopeKeyStore();
            var context = new BenchmarkSecurityContext();
            var kekId = "bench-kek";

            _output.WriteLine("=== Envelope Mode Decryption Benchmark ===");
            _output.WriteLine($"Iterations: {BenchmarkIterations}, Warmup: {WarmupIterations}");
            _output.WriteLine("(Includes key unwrapping + decryption)");
            _output.WriteLine(new string('-', 100));

            for (int s = 0; s < DataSizes.Length; s++)
            {
                var data = RandomNumberGenerator.GetBytes(DataSizes[s]);
                var dek = strategy.GenerateKey();
                var wrappedDek = await envelopeStore.WrapKeyAsync(kekId, dek, context);
                var encrypted = await strategy.EncryptAsync(data, dek);

                var result = RunBenchmark(
                    $"Envelope Decrypt {DataSizeLabels[s]}",
                    DataSizes[s],
                    BenchmarkIterations,
                    async () =>
                    {
                        // Full envelope decrypt flow: unwrap DEK, decrypt
                        var unwrappedDek = await envelopeStore.UnwrapKeyAsync(kekId, wrappedDek, context);
                        await strategy.DecryptAsync(encrypted, unwrappedDek);
                    });

                PrintResult(result);

                // Verify decryption is correct
                var unwrapped = await envelopeStore.UnwrapKeyAsync(kekId, wrappedDek, context);
                var decrypted = await strategy.DecryptAsync(encrypted, unwrapped);
                Assert.Equal(data, decrypted);
            }

            _output.WriteLine("PASS: Envelope mode decryption benchmark complete.");
        }

        [Fact]
        public async Task Benchmark_Key_Wrap_Unwrap_Overhead()
        {
            var envelopeStore = new BenchmarkEnvelopeKeyStore();
            var context = new BenchmarkSecurityContext();
            var kekId = "bench-kek-overhead";
            var dek = RandomNumberGenerator.GetBytes(32);

            _output.WriteLine("=== Key Wrap/Unwrap Overhead Benchmark ===");
            _output.WriteLine($"Iterations: {BenchmarkIterations * 5}, Warmup: {WarmupIterations}");
            _output.WriteLine(new string('-', 100));

            // Benchmark WrapKeyAsync
            var wrapResult = RunBenchmark(
                "WrapKeyAsync (32-byte DEK)",
                32,
                BenchmarkIterations * 5,
                () => envelopeStore.WrapKeyAsync(kekId, dek, context));
            PrintResult(wrapResult);

            // Benchmark UnwrapKeyAsync
            var wrappedDek = await envelopeStore.WrapKeyAsync(kekId, dek, context);
            var unwrapResult = RunBenchmark(
                "UnwrapKeyAsync (32-byte DEK)",
                32,
                BenchmarkIterations * 5,
                () => envelopeStore.UnwrapKeyAsync(kekId, wrappedDek, context));
            PrintResult(unwrapResult);

            // Benchmark DEK generation
            var strategy = new Aes256GcmBenchmarkStrategy();
            var genResult = RunBenchmark(
                "GenerateKey (256-bit DEK)",
                32,
                BenchmarkIterations * 5,
                () =>
                {
                    strategy.GenerateKey();
                    return Task.CompletedTask;
                });
            PrintResult(genResult);

            // Combined wrap + unwrap round-trip
            var roundTripResult = RunBenchmark(
                "Wrap + Unwrap round-trip",
                32,
                BenchmarkIterations * 5,
                async () =>
                {
                    var wrapped = await envelopeStore.WrapKeyAsync(kekId, dek, context);
                    await envelopeStore.UnwrapKeyAsync(kekId, wrapped, context);
                });
            PrintResult(roundTripResult);

            _output.WriteLine("");
            _output.WriteLine($"Key wrap avg: {wrapResult.AvgMs:F4}ms");
            _output.WriteLine($"Key unwrap avg: {unwrapResult.AvgMs:F4}ms");
            _output.WriteLine($"Key generation avg: {genResult.AvgMs:F4}ms");
            _output.WriteLine($"Wrap+Unwrap round-trip avg: {roundTripResult.AvgMs:F4}ms");
            _output.WriteLine("PASS: Key wrap/unwrap overhead benchmark complete.");
        }

        [Fact]
        public async Task Compare_Direct_Vs_Envelope_Overhead()
        {
            var strategy = new Aes256GcmBenchmarkStrategy();
            var keyStore = new BenchmarkKeyStore();
            var envelopeStore = new BenchmarkEnvelopeKeyStore();
            var context = new BenchmarkSecurityContext();
            var directKey = await keyStore.GetKeyAsync("bench-key", context);
            var kekId = "bench-kek-compare";

            _output.WriteLine("=== Direct vs Envelope Overhead Comparison ===");
            _output.WriteLine($"Iterations: {BenchmarkIterations}, Warmup: {WarmupIterations}");
            _output.WriteLine(new string('-', 100));
            _output.WriteLine($"{"Size",-8} | {"Direct Enc (ms)",-16} | {"Envelope Enc (ms)",-18} | {"Overhead %",-12} | {"Direct Dec (ms)",-16} | {"Envelope Dec (ms)",-18} | {"Overhead %",-12}");
            _output.WriteLine(new string('-', 100));

            for (int s = 0; s < DataSizes.Length; s++)
            {
                var data = RandomNumberGenerator.GetBytes(DataSizes[s]);

                // Direct mode encryption
                var directEncResult = RunBenchmark(
                    $"Direct Enc {DataSizeLabels[s]}",
                    DataSizes[s],
                    BenchmarkIterations,
                    () => strategy.EncryptAsync(data, directKey));

                // Envelope mode encryption (DEK gen + wrap + encrypt)
                var envelopeEncResult = RunBenchmark(
                    $"Envelope Enc {DataSizeLabels[s]}",
                    DataSizes[s],
                    BenchmarkIterations,
                    async () =>
                    {
                        var dek = strategy.GenerateKey();
                        await envelopeStore.WrapKeyAsync(kekId, dek, context);
                        await strategy.EncryptAsync(data, dek);
                    });

                // Prepare data for decryption benchmarks
                var encryptedDirect = await strategy.EncryptAsync(data, directKey);
                var dekForDecrypt = strategy.GenerateKey();
                var wrappedDek = await envelopeStore.WrapKeyAsync(kekId, dekForDecrypt, context);
                var encryptedEnvelope = await strategy.EncryptAsync(data, dekForDecrypt);

                // Direct mode decryption
                var directDecResult = RunBenchmark(
                    $"Direct Dec {DataSizeLabels[s]}",
                    DataSizes[s],
                    BenchmarkIterations,
                    () => strategy.DecryptAsync(encryptedDirect, directKey));

                // Envelope mode decryption (unwrap + decrypt)
                var envelopeDecResult = RunBenchmark(
                    $"Envelope Dec {DataSizeLabels[s]}",
                    DataSizes[s],
                    BenchmarkIterations,
                    async () =>
                    {
                        var unwrapped = await envelopeStore.UnwrapKeyAsync(kekId, wrappedDek, context);
                        await strategy.DecryptAsync(encryptedEnvelope, unwrapped);
                    });

                var encOverhead = directEncResult.AvgMs > 0
                    ? ((envelopeEncResult.AvgMs - directEncResult.AvgMs) / directEncResult.AvgMs) * 100
                    : 0;
                var decOverhead = directDecResult.AvgMs > 0
                    ? ((envelopeDecResult.AvgMs - directDecResult.AvgMs) / directDecResult.AvgMs) * 100
                    : 0;

                _output.WriteLine(
                    $"{DataSizeLabels[s],-8} | {directEncResult.AvgMs,14:F3}ms | " +
                    $"{envelopeEncResult.AvgMs,16:F3}ms | {encOverhead,9:F1}%   | " +
                    $"{directDecResult.AvgMs,14:F3}ms | " +
                    $"{envelopeDecResult.AvgMs,16:F3}ms | {decOverhead,9:F1}%");
            }

            _output.WriteLine(new string('-', 100));
            _output.WriteLine("");
            _output.WriteLine("Note: Envelope overhead comes from DEK generation + AES-GCM key wrap/unwrap.");
            _output.WriteLine("For large payloads, the overhead percentage decreases since data encryption");
            _output.WriteLine("dominates total time (same AES-GCM algorithm used in both modes).");
            _output.WriteLine("");

            // Verify both modes produce correct results
            var verifyData = RandomNumberGenerator.GetBytes(1024);
            var verifyEncDirect = await strategy.EncryptAsync(verifyData, directKey);
            var verifyDecDirect = await strategy.DecryptAsync(verifyEncDirect, directKey);
            Assert.Equal(verifyData, verifyDecDirect);

            var verifyDek = strategy.GenerateKey();
            var verifyWrapped = await envelopeStore.WrapKeyAsync(kekId, verifyDek, context);
            var verifyEncEnvelope = await strategy.EncryptAsync(verifyData, verifyDek);
            var verifyUnwrapped = await envelopeStore.UnwrapKeyAsync(kekId, verifyWrapped, context);
            var verifyDecEnvelope = await strategy.DecryptAsync(verifyEncEnvelope, verifyUnwrapped);
            Assert.Equal(verifyData, verifyDecEnvelope);

            _output.WriteLine("PASS: Direct vs Envelope overhead comparison complete. Both modes verified correct.");
        }

        #endregion
    }
}
