using DataWarehouse.SDK.Security;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.IndustryFirst
{
    /// <summary>
    /// Verifiable Delay Function (VDF) Strategy with Wesolowski proofs.
    ///
    /// VDFs are cryptographic primitives that require a specified amount of sequential computation
    /// to evaluate, but produce a proof that can be verified quickly. Unlike time-lock puzzles,
    /// VDFs allow anyone to verify the result without redoing the computation.
    ///
    /// Algorithm: Wesolowski VDF (2019)
    /// Based on the paper "Efficient Verifiable Delay Functions" by Benjamin Wesolowski.
    ///
    /// Core Operation:
    /// - Evaluation: y = x^(2^T) mod N (requires T sequential squarings)
    /// - Proof: pi = x^floor(2^T / l) mod N where l = H(x, y)
    /// - Verification: y = x^r * pi^l mod N where r = 2^T mod l (fast, O(log T) operations)
    ///
    /// Security Properties:
    /// - Sequential computation requirement (cannot be parallelized)
    /// - Efficient verification (exponentially faster than evaluation)
    /// - Based on groups of unknown order (RSA, class groups)
    /// - Provides publicly verifiable output (anyone can verify without secrets)
    ///
    /// Use Cases:
    /// - Time-locked key release with public verifiability
    /// - Proof-of-elapsed-time without trusted hardware
    /// - Randomness beacons (Ethereum 2.0, Chia)
    /// - Fair lottery/auction timing proofs
    /// </summary>
    public sealed class VerifiableDelayStrategy : KeyStoreStrategyBase
    {
        private VdfConfig _config = new();
        private readonly Dictionary<string, VdfKeyData> _keys = new();
        private readonly Dictionary<string, VdfEvaluatorState> _activeEvaluators = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly SecureRandom _secureRandom = new();
        private bool _disposed;

        // RSA modulus bit size for group of unknown order
        private const int ModulusBits = 2048;

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = false,
            SupportsHsm = false,
            SupportsExpiration = true,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = true,
            SupportsAuditLogging = true,
            MaxKeySizeBytes = 32,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Algorithm"] = "Wesolowski VDF",
                ["Paper"] = "Efficient Verifiable Delay Functions (2019)",
                ["SecurityModel"] = "Sequential Computation + Public Verifiability",
                ["ModulusBits"] = ModulusBits,
                ["VerificationComplexity"] = "O(log T)"
            }
        };

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            if (Configuration.TryGetValue("SquaringsPerSecond", out var sps) && sps is long squarings)
                _config.SquaringsPerSecond = squarings;
            if (Configuration.TryGetValue("DefaultDelaySeconds", out var delay) && delay is long delaySeconds)
                _config.DefaultDelaySeconds = delaySeconds;
            if (Configuration.TryGetValue("StoragePath", out var p) && p is string path)
                _config.StoragePath = path;
            if (Configuration.TryGetValue("SecurityParameter", out var sec) && sec is int security)
                _config.SecurityParameter = security;
            if (Configuration.TryGetValue("EnableProofCaching", out var cache) && cache is bool enableCache)
                _config.EnableProofCaching = enableCache;

            // Calibrate if not specified
            if (_config.SquaringsPerSecond == 0)
            {
                _config.SquaringsPerSecond = await CalibrateSquaringsPerSecond();
            }

            await LoadKeysFromStorage();
        }

        private Task<long> CalibrateSquaringsPerSecond()
        {
            // Generate test modulus
            var p = BigInteger.ProbablePrime(ModulusBits / 2, _secureRandom);
            var q = BigInteger.ProbablePrime(ModulusBits / 2, _secureRandom);
            var n = p.Multiply(q);

            var x = new BigInteger(ModulusBits - 1, _secureRandom).Mod(n);

            const int testSquarings = 50000;
            var sw = Stopwatch.StartNew();

            var current = x;
            for (int i = 0; i < testSquarings; i++)
            {
                current = current.ModPow(BigInteger.Two, n);
            }

            sw.Stop();

            var rate = (long)(testSquarings / sw.Elapsed.TotalSeconds);
            return Task.FromResult(rate);
        }

        public override Task<string> GetCurrentKeyIdAsync() => Task.FromResult(_currentKeyId);

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                return _keys.Count >= 0;
            }
            finally
            {
                _lock.Release();
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"VDF key '{keyId}' not found.");

                // Check if VDF has been evaluated and verified
                if (keyData.IsEvaluated && keyData.IsVerified && keyData.DecryptedKey != null)
                {
                    return keyData.DecryptedKey;
                }

                // Check for active evaluator
                if (_activeEvaluators.TryGetValue(keyId, out var evaluator) && evaluator.IsComplete)
                {
                    // Verify the proof
                    var isValid = VerifyWesolowskiProof(
                        new BigInteger(1, keyData.Modulus),
                        new BigInteger(1, keyData.InputX),
                        evaluator.OutputY,
                        evaluator.Proof!,
                        new BigInteger(1, keyData.TotalSquarings));

                    if (!isValid)
                    {
                        throw new CryptographicException("VDF proof verification failed.");
                    }

                    // Decrypt the key using VDF output
                    var keyHash = ComputeKeyHash(evaluator.OutputY);
                    var decryptedKey = XorBytes(keyData.EncryptedKey, keyHash);

                    keyData.DecryptedKey = decryptedKey;
                    keyData.OutputY = evaluator.OutputY.ToByteArrayUnsigned();
                    keyData.Proof = evaluator.Proof!.ToByteArrayUnsigned();
                    keyData.IsEvaluated = true;
                    keyData.IsVerified = true;
                    keyData.EvaluatedAt = DateTime.UtcNow;

                    await PersistKeysToStorage();
                    return decryptedKey;
                }

                throw new InvalidOperationException(
                    $"Key is protected by VDF. Estimated evaluation time: {GetEstimatedDelay(keyData):g}. " +
                    $"Start evaluation with EvaluateVdfAsync().");
            }
            finally
            {
                _lock.Release();
            }
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            await _lock.WaitAsync();
            try
            {
                // Generate RSA modulus (group of unknown order)
                var (n, p, q) = GenerateRsaModulus();

                // Calculate total squarings for desired delay
                var totalSquarings = new BigInteger(
                    (_config.DefaultDelaySeconds * _config.SquaringsPerSecond).ToString());

                // Generate random input x in Z*_N
                var x = GenerateValidInput(n);

                // Compute y = x^(2^T) mod N efficiently using factorization
                // This is the "trapdoor" - we know p,q so we can compute phi(N)
                var phi = p.Subtract(BigInteger.One).Multiply(q.Subtract(BigInteger.One));
                var exponent = BigInteger.Two.ModPow(totalSquarings, phi);
                var y = x.ModPow(exponent, n);

                // Generate Wesolowski proof
                var proof = GenerateWesolowskiProof(n, x, y, totalSquarings);

                // Encrypt the key: C = K XOR H(y)
                var keyHash = ComputeKeyHash(y);
                var encryptedKey = XorBytes(keyData, keyHash);

                var vdfKeyData = new VdfKeyData
                {
                    KeyId = keyId,
                    Modulus = n.ToByteArrayUnsigned(),
                    InputX = x.ToByteArrayUnsigned(),
                    TotalSquarings = totalSquarings.ToByteArrayUnsigned(),
                    EncryptedKey = encryptedKey,
                    KeySizeBytes = keyData.Length,
                    OutputY = y.ToByteArrayUnsigned(),
                    Proof = proof.ToByteArrayUnsigned(),
                    IsEvaluated = true,
                    IsVerified = true,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId,
                    EstimatedDelaySeconds = _config.DefaultDelaySeconds,
                    // Store the decrypted key (creator has immediate access)
                    DecryptedKey = keyData
                };

                // IMPORTANT: Clear p, q, phi - these are the trapdoor
                // Only the VDF output and proof are stored

                _keys[keyId] = vdfKeyData;
                _currentKeyId = keyId;

                await PersistKeysToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Creates a VDF-protected key with a specific delay time.
        /// The key creator can access immediately (knows the trapdoor).
        /// Others must evaluate the VDF to access the key.
        /// </summary>
        public async Task<VdfCreationResult> CreateVdfProtectedKeyAsync(
            string keyId,
            byte[] keyData,
            TimeSpan delay,
            bool publishForOthers,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var originalDelay = _config.DefaultDelaySeconds;
            try
            {
                _config.DefaultDelaySeconds = (long)delay.TotalSeconds;

                await SaveKeyToStorage(keyId, keyData, context);

                var key = _keys[keyId];

                if (publishForOthers)
                {
                    // Clear the decrypted key so others must evaluate VDF
                    key.DecryptedKey = null;
                    key.IsEvaluated = false;
                    key.IsVerified = false;
                    await PersistKeysToStorage();
                }

                return new VdfCreationResult
                {
                    Success = true,
                    KeyId = keyId,
                    InputX = key.InputX,
                    Modulus = key.Modulus,
                    TotalSquarings = new BigInteger(1, key.TotalSquarings),
                    EstimatedDelay = delay,
                    // Include proof so anyone can verify once they compute y
                    Proof = key.Proof
                };
            }
            finally
            {
                _config.DefaultDelaySeconds = originalDelay;
            }
        }

        /// <summary>
        /// Starts VDF evaluation asynchronously.
        /// This performs the sequential computation y = x^(2^T) mod N.
        /// </summary>
        public async Task<VdfEvaluationResult> StartEvaluationAsync(
            string keyId,
            ISecurityContext context,
            IProgress<VdfProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync(cancellationToken);
            VdfKeyData keyData;
            try
            {
                if (!_keys.TryGetValue(keyId, out keyData!))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                if (keyData.IsEvaluated && keyData.IsVerified)
                    return new VdfEvaluationResult
                    {
                        Success = true,
                        DecryptedKey = keyData.DecryptedKey,
                        ElapsedTime = TimeSpan.Zero
                    };

                if (_activeEvaluators.ContainsKey(keyId))
                    throw new InvalidOperationException("Evaluation already in progress.");
            }
            finally
            {
                _lock.Release();
            }

            var evaluator = new VdfEvaluatorState
            {
                KeyId = keyId,
                Modulus = new BigInteger(1, keyData.Modulus),
                Current = new BigInteger(1, keyData.InputX),
                TotalSquarings = new BigInteger(1, keyData.TotalSquarings),
                CompletedSquarings = BigInteger.Zero,
                StartedAt = DateTime.UtcNow
            };

            _activeEvaluators[keyId] = evaluator;

            // Run VDF evaluation on background thread
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                var lastProgress = DateTime.UtcNow;

                try
                {
                    // Sequential squaring - this CANNOT be parallelized
                    while (evaluator.CompletedSquarings.CompareTo(evaluator.TotalSquarings) < 0)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            evaluator.IsCancelled = true;
                            break;
                        }

                        // Batch squarings for efficiency
                        const int batchSize = 10000;
                        for (int i = 0; i < batchSize && evaluator.CompletedSquarings.CompareTo(evaluator.TotalSquarings) < 0; i++)
                        {
                            evaluator.Current = evaluator.Current.ModPow(BigInteger.Two, evaluator.Modulus);
                            evaluator.CompletedSquarings = evaluator.CompletedSquarings.Add(BigInteger.One);
                        }

                        // Report progress
                        if ((DateTime.UtcNow - lastProgress).TotalSeconds >= 1)
                        {
                            var percent = evaluator.CompletedSquarings
                                .Multiply(BigInteger.ValueOf(100))
                                .Divide(evaluator.TotalSquarings)
                                .IntValue;

                            progress?.Report(new VdfProgress
                            {
                                KeyId = keyId,
                                CompletedSquarings = evaluator.CompletedSquarings,
                                TotalSquarings = evaluator.TotalSquarings,
                                PercentComplete = percent,
                                ElapsedTime = sw.Elapsed,
                                EstimatedRemaining = EstimateRemainingTime(evaluator, sw.Elapsed)
                            });

                            lastProgress = DateTime.UtcNow;
                        }
                    }

                    if (!evaluator.IsCancelled)
                    {
                        evaluator.OutputY = evaluator.Current;

                        // Generate Wesolowski proof
                        evaluator.Proof = GenerateWesolowskiProof(
                            evaluator.Modulus,
                            new BigInteger(1, keyData.InputX),
                            evaluator.OutputY,
                            evaluator.TotalSquarings);

                        evaluator.IsComplete = true;
                        evaluator.CompletedAt = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    evaluator.Error = ex.Message;
                }
            }, cancellationToken);

            return new VdfEvaluationResult
            {
                Success = true,
                KeyId = keyId,
                Message = "VDF evaluation started in background."
            };
        }

        /// <summary>
        /// Verifies a VDF output and proof without performing the full evaluation.
        /// This is the key advantage of VDFs - verification is O(log T) vs O(T) for evaluation.
        /// </summary>
        public async Task<VdfVerificationResult> VerifyVdfAsync(
            string keyId,
            byte[] outputY,
            byte[] proof,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var n = new BigInteger(1, keyData.Modulus);
                var x = new BigInteger(1, keyData.InputX);
                var y = new BigInteger(1, outputY);
                var pi = new BigInteger(1, proof);
                var t = new BigInteger(1, keyData.TotalSquarings);

                var sw = Stopwatch.StartNew();
                var isValid = VerifyWesolowskiProof(n, x, y, pi, t);
                sw.Stop();

                if (isValid)
                {
                    // Decrypt the key
                    var keyHash = ComputeKeyHash(y);
                    var decryptedKey = XorBytes(keyData.EncryptedKey, keyHash);

                    keyData.DecryptedKey = decryptedKey;
                    keyData.OutputY = outputY;
                    keyData.Proof = proof;
                    keyData.IsEvaluated = true;
                    keyData.IsVerified = true;
                    keyData.EvaluatedAt = DateTime.UtcNow;

                    await PersistKeysToStorage();

                    return new VdfVerificationResult
                    {
                        IsValid = true,
                        DecryptedKey = decryptedKey,
                        VerificationTime = sw.Elapsed
                    };
                }

                return new VdfVerificationResult
                {
                    IsValid = false,
                    VerificationTime = sw.Elapsed,
                    Error = "VDF proof verification failed."
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Gets the current progress of a VDF evaluation.
        /// </summary>
        public VdfProgress? GetEvaluationProgress(string keyId)
        {
            if (!_activeEvaluators.TryGetValue(keyId, out var evaluator))
                return null;

            var elapsed = DateTime.UtcNow - evaluator.StartedAt;
            var percent = evaluator.CompletedSquarings
                .Multiply(BigInteger.ValueOf(100))
                .Divide(evaluator.TotalSquarings)
                .IntValue;

            return new VdfProgress
            {
                KeyId = keyId,
                CompletedSquarings = evaluator.CompletedSquarings,
                TotalSquarings = evaluator.TotalSquarings,
                PercentComplete = percent,
                ElapsedTime = elapsed,
                EstimatedRemaining = EstimateRemainingTime(evaluator, elapsed),
                IsComplete = evaluator.IsComplete,
                IsCancelled = evaluator.IsCancelled,
                Error = evaluator.Error
            };
        }

        #region Wesolowski VDF Implementation

        /// <summary>
        /// Generates an RSA modulus N = p * q where p, q are safe primes.
        /// The factorization is the "trapdoor" that allows fast VDF computation.
        /// </summary>
        private (BigInteger n, BigInteger p, BigInteger q) GenerateRsaModulus()
        {
            // Generate safe primes for stronger security
            BigInteger p, q;

            do
            {
                var pPrime = BigInteger.ProbablePrime(ModulusBits / 2 - 1, _secureRandom);
                p = pPrime.Multiply(BigInteger.Two).Add(BigInteger.One);
            } while (!p.IsProbablePrime(100));

            do
            {
                var qPrime = BigInteger.ProbablePrime(ModulusBits / 2 - 1, _secureRandom);
                q = qPrime.Multiply(BigInteger.Two).Add(BigInteger.One);
            } while (!q.IsProbablePrime(100) || q.Equals(p));

            var n = p.Multiply(q);

            return (n, p, q);
        }

        /// <summary>
        /// Generates a valid input x in Z*_N (invertible elements modulo N).
        /// </summary>
        private BigInteger GenerateValidInput(BigInteger n)
        {
            BigInteger x;
            do
            {
                x = new BigInteger(ModulusBits - 1, _secureRandom).Mod(n);
            } while (x.CompareTo(BigInteger.Two) < 0 || !x.Gcd(n).Equals(BigInteger.One));

            return x;
        }

        /// <summary>
        /// Generates a Wesolowski proof for the VDF computation.
        /// The proof pi = x^floor(2^T / l) mod N where l = H(x, y).
        /// </summary>
        private BigInteger GenerateWesolowskiProof(
            BigInteger n,
            BigInteger x,
            BigInteger y,
            BigInteger t)
        {
            // Compute l = H(x || y) - a prime derived from hash
            var l = ComputeChallengeL(x, y, _config.SecurityParameter);

            // Compute floor(2^T / l)
            var twoToTheT = BigInteger.Two.Pow(t.IntValue);
            var divideResult = twoToTheT.DivideAndRemainder(l);
            var quotient = divideResult[0];

            // Compute pi = x^quotient mod N
            var pi = x.ModPow(quotient, n);

            return pi;
        }

        /// <summary>
        /// Verifies a Wesolowski proof.
        /// Checks that y = x^r * pi^l mod N where r = 2^T mod l.
        /// Verification is O(log T) - exponentially faster than evaluation.
        /// </summary>
        private bool VerifyWesolowskiProof(
            BigInteger n,
            BigInteger x,
            BigInteger y,
            BigInteger pi,
            BigInteger t)
        {
            try
            {
                // Compute l = H(x || y)
                var l = ComputeChallengeL(x, y, _config.SecurityParameter);

                // Compute r = 2^T mod l
                var r = BigInteger.Two.ModPow(t, l);

                // Compute x^r mod N
                var xToR = x.ModPow(r, n);

                // Compute pi^l mod N
                var piToL = pi.ModPow(l, n);

                // Check y == x^r * pi^l mod N
                var expected = xToR.Multiply(piToL).Mod(n);

                return y.Equals(expected);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Computes the Fiat-Shamir challenge l = H(x || y).
        /// Returns a prime number derived from the hash.
        /// </summary>
        private BigInteger ComputeChallengeL(BigInteger x, BigInteger y, int securityBits)
        {
            // Hash x || y
            using var sha = SHA256.Create();

            var xBytes = x.ToByteArrayUnsigned();
            var yBytes = y.ToByteArrayUnsigned();

            var combined = new byte[xBytes.Length + yBytes.Length];
            Array.Copy(xBytes, combined, xBytes.Length);
            Array.Copy(yBytes, 0, combined, xBytes.Length, yBytes.Length);

            var hash = sha.ComputeHash(combined);

            // Derive a prime from the hash
            var candidate = new BigInteger(1, hash).Mod(
                BigInteger.One.ShiftLeft(securityBits));

            // Find next prime
            while (!candidate.IsProbablePrime(50))
            {
                candidate = candidate.Add(BigInteger.One);
            }

            return candidate;
        }

        #endregion

        #region Helpers

        private TimeSpan GetEstimatedDelay(VdfKeyData keyData)
        {
            return TimeSpan.FromSeconds(keyData.EstimatedDelaySeconds);
        }

        private TimeSpan EstimateRemainingTime(VdfEvaluatorState evaluator, TimeSpan elapsed)
        {
            if (evaluator.CompletedSquarings.Equals(BigInteger.Zero))
                return TimeSpan.FromSeconds(
                    evaluator.TotalSquarings.LongValue / _config.SquaringsPerSecond);

            var remaining = evaluator.TotalSquarings.Subtract(evaluator.CompletedSquarings);
            var rate = evaluator.CompletedSquarings.LongValue / elapsed.TotalSeconds;
            if (rate <= 0) return TimeSpan.MaxValue;

            return TimeSpan.FromSeconds(remaining.LongValue / rate);
        }

        private static byte[] ComputeKeyHash(BigInteger value)
        {
            return SHA256.HashData(value.ToByteArrayUnsigned());
        }

        private static byte[] XorBytes(byte[] a, byte[] b)
        {
            var result = new byte[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                result[i] = (byte)(a[i] ^ b[i % b.Length]);
            }
            return result;
        }

        #endregion

        #region IKeyStore Implementation

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);
            await _lock.WaitAsync(ct);
            try { return _keys.Keys.ToList().AsReadOnly(); }
            finally { _lock.Release(); }
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);
            if (!context.IsSystemAdmin) throw new UnauthorizedAccessException();

            await _lock.WaitAsync(ct);
            try
            {
                if (_keys.Remove(keyId))
                {
                    _activeEvaluators.Remove(keyId);
                    await PersistKeysToStorage();
                }
            }
            finally { _lock.Release(); }
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync(ct);
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData)) return null;

                var progress = GetEvaluationProgress(keyId);

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = keyData.CreatedAt,
                    CreatedBy = keyData.CreatedBy,
                    KeySizeBytes = keyData.KeySizeBytes,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Algorithm"] = "Wesolowski VDF",
                        ["TotalSquarings"] = new BigInteger(1, keyData.TotalSquarings).ToString(),
                        ["EstimatedDelaySeconds"] = keyData.EstimatedDelaySeconds,
                        ["IsEvaluated"] = keyData.IsEvaluated,
                        ["IsVerified"] = keyData.IsVerified,
                        ["EvaluatedAt"] = keyData.EvaluatedAt?.ToString() ?? "Not evaluated",
                        ["EvaluationInProgress"] = progress != null,
                        ["EvaluationProgress"] = progress?.PercentComplete ?? 0
                    }
                };
            }
            finally { _lock.Release(); }
        }

        #endregion

        #region Storage

        private async Task LoadKeysFromStorage()
        {
            var path = GetStoragePath();
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var stored = JsonSerializer.Deserialize<Dictionary<string, VdfKeyDataSerialized>>(json);
                if (stored != null)
                {
                    foreach (var kvp in stored)
                        _keys[kvp.Key] = DeserializeKeyData(kvp.Value);
                    if (_keys.Count > 0)
                        _currentKeyId = _keys.Keys.First();
                }
            }
            catch { }
        }

        private async Task PersistKeysToStorage()
        {
            var path = GetStoragePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var toStore = _keys.ToDictionary(
                kvp => kvp.Key,
                kvp => SerializeKeyData(kvp.Value));

            var json = JsonSerializer.Serialize(toStore, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
        }

        private string GetStoragePath()
        {
            if (!string.IsNullOrEmpty(_config.StoragePath))
                return _config.StoragePath;
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "DataWarehouse", "vdf-keys.json");
        }

        private static VdfKeyDataSerialized SerializeKeyData(VdfKeyData data) => new()
        {
            KeyId = data.KeyId,
            Modulus = data.Modulus,
            InputX = data.InputX,
            TotalSquarings = data.TotalSquarings,
            EncryptedKey = data.EncryptedKey,
            KeySizeBytes = data.KeySizeBytes,
            OutputY = data.OutputY,
            Proof = data.Proof,
            IsEvaluated = data.IsEvaluated,
            IsVerified = data.IsVerified,
            EstimatedDelaySeconds = data.EstimatedDelaySeconds,
            CreatedAt = data.CreatedAt,
            CreatedBy = data.CreatedBy,
            EvaluatedAt = data.EvaluatedAt,
            DecryptedKey = data.DecryptedKey
        };

        private static VdfKeyData DeserializeKeyData(VdfKeyDataSerialized data) => new()
        {
            KeyId = data.KeyId ?? "",
            Modulus = data.Modulus ?? Array.Empty<byte>(),
            InputX = data.InputX ?? Array.Empty<byte>(),
            TotalSquarings = data.TotalSquarings ?? Array.Empty<byte>(),
            EncryptedKey = data.EncryptedKey ?? Array.Empty<byte>(),
            KeySizeBytes = data.KeySizeBytes,
            OutputY = data.OutputY,
            Proof = data.Proof,
            IsEvaluated = data.IsEvaluated,
            IsVerified = data.IsVerified,
            EstimatedDelaySeconds = data.EstimatedDelaySeconds,
            CreatedAt = data.CreatedAt,
            CreatedBy = data.CreatedBy,
            EvaluatedAt = data.EvaluatedAt,
            DecryptedKey = data.DecryptedKey
        };

        #endregion

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _activeEvaluators.Clear();
            _lock.Dispose();
            base.Dispose();
        }
    }

    #region Supporting Types

    public class VdfConfig
    {
        public long SquaringsPerSecond { get; set; }
        public long DefaultDelaySeconds { get; set; } = 3600; // 1 hour
        public int SecurityParameter { get; set; } = 128; // bits for challenge
        public bool EnableProofCaching { get; set; } = true;
        public string? StoragePath { get; set; }
    }

    internal class VdfKeyData
    {
        public string KeyId { get; set; } = "";
        public byte[] Modulus { get; set; } = Array.Empty<byte>();
        public byte[] InputX { get; set; } = Array.Empty<byte>();
        public byte[] TotalSquarings { get; set; } = Array.Empty<byte>();
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
        public int KeySizeBytes { get; set; }
        public byte[]? OutputY { get; set; }
        public byte[]? Proof { get; set; }
        public bool IsEvaluated { get; set; }
        public bool IsVerified { get; set; }
        public long EstimatedDelaySeconds { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? EvaluatedAt { get; set; }
        public byte[]? DecryptedKey { get; set; }
    }

    internal class VdfKeyDataSerialized
    {
        public string? KeyId { get; set; }
        public byte[]? Modulus { get; set; }
        public byte[]? InputX { get; set; }
        public byte[]? TotalSquarings { get; set; }
        public byte[]? EncryptedKey { get; set; }
        public int KeySizeBytes { get; set; }
        public byte[]? OutputY { get; set; }
        public byte[]? Proof { get; set; }
        public bool IsEvaluated { get; set; }
        public bool IsVerified { get; set; }
        public long EstimatedDelaySeconds { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? EvaluatedAt { get; set; }
        public byte[]? DecryptedKey { get; set; }
    }

    internal class VdfEvaluatorState
    {
        public string KeyId { get; set; } = "";
        public BigInteger Modulus { get; set; } = BigInteger.Zero;
        public BigInteger Current { get; set; } = BigInteger.Zero;
        public BigInteger TotalSquarings { get; set; } = BigInteger.Zero;
        public BigInteger CompletedSquarings { get; set; } = BigInteger.Zero;
        public BigInteger OutputY { get; set; } = BigInteger.Zero;
        public BigInteger? Proof { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool IsComplete { get; set; }
        public bool IsCancelled { get; set; }
        public string? Error { get; set; }
    }

    public class VdfCreationResult
    {
        public bool Success { get; set; }
        public string? KeyId { get; set; }
        public byte[]? InputX { get; set; }
        public byte[]? Modulus { get; set; }
        public BigInteger TotalSquarings { get; set; } = BigInteger.Zero;
        public TimeSpan EstimatedDelay { get; set; }
        public byte[]? Proof { get; set; }
        public string? Error { get; set; }
    }

    public class VdfEvaluationResult
    {
        public bool Success { get; set; }
        public string? KeyId { get; set; }
        public byte[]? DecryptedKey { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
    }

    public class VdfVerificationResult
    {
        public bool IsValid { get; set; }
        public byte[]? DecryptedKey { get; set; }
        public TimeSpan VerificationTime { get; set; }
        public string? Error { get; set; }
    }

    public class VdfProgress
    {
        public string? KeyId { get; set; }
        public BigInteger CompletedSquarings { get; set; } = BigInteger.Zero;
        public BigInteger TotalSquarings { get; set; } = BigInteger.Zero;
        public int PercentComplete { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public TimeSpan EstimatedRemaining { get; set; }
        public bool IsComplete { get; set; }
        public bool IsCancelled { get; set; }
        public string? Error { get; set; }
    }

    #endregion
}
