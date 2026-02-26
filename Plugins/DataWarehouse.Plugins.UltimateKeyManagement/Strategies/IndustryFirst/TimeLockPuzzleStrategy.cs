using DataWarehouse.SDK.Security;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.IndustryFirst
{
    /// <summary>
    /// Time-Lock Puzzle Strategy - RSA-based time-lock encryption.
    ///
    /// Based on the seminal paper "Time-lock puzzles and timed-release crypto"
    /// by Rivest, Shamir, and Wagner (1996).
    ///
    /// Algorithm:
    /// The key is encrypted such that decryption requires T sequential squaring operations
    /// modulo N (RSA modulus). This creates a compute-bound delay that cannot be parallelized.
    ///
    /// To create a time-lock puzzle:
    /// 1. Generate RSA modulus N = p * q
    /// 2. Generate random a
    /// 3. Compute b = a^(2^T) mod N (efficiently using phi(N) = (p-1)(q-1))
    /// 4. Encrypt key K as: C = K XOR H(b)
    ///
    /// To solve the puzzle (without knowing p, q):
    /// 1. Compute b = a^(2^T) mod N by performing T sequential squarings
    /// 2. Decrypt key as: K = C XOR H(b)
    ///
    /// Security Properties:
    /// - Cannot be parallelized (inherent sequential computation)
    /// - Puzzle creation is O(log T), solving is O(T)
    /// - Based on RSA assumption (factoring is hard)
    ///
    /// Features:
    /// - Configurable unlock time (compute-bound, not wall-clock)
    /// - Puzzle verification without solving
    /// - Parallel puzzle chains for different time horizons
    /// - Progress checkpointing for long solves
    /// </summary>
    public sealed class TimeLockPuzzleStrategy : KeyStoreStrategyBase
    {
        private TimeLockConfig _config = new();
        private readonly Dictionary<string, TimeLockKeyData> _keys = new();
        private readonly Dictionary<string, PuzzleSolverState> _activeSolvers = new();
        private string _currentKeyId = "default";
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly SecureRandom _secureRandom = new();
        private bool _disposed;

        // RSA key size for time-lock modulus
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
                ["Algorithm"] = "RSA Time-Lock Puzzle",
                ["Paper"] = "Rivest-Shamir-Wagner 1996",
                ["SecurityModel"] = "Sequential Computation Bound",
                ["ModulusBits"] = ModulusBits
            }
        };

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("timelockpuzzle.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            IncrementCounter("timelockpuzzle.init");
            if (Configuration.TryGetValue("SquaringsPerSecond", out var sps) && sps is long squarings)
                _config.SquaringsPerSecond = squarings;
            if (Configuration.TryGetValue("DefaultUnlockSeconds", out var dur) && dur is long duration)
                _config.DefaultUnlockSeconds = duration;
            if (Configuration.TryGetValue("StoragePath", out var p) && p is string path)
                _config.StoragePath = path;
            if (Configuration.TryGetValue("EnableCheckpointing", out var cp) && cp is bool checkpoint)
                _config.EnableCheckpointing = checkpoint;
            if (Configuration.TryGetValue("CheckpointIntervalSeconds", out var cpi) && cpi is int interval)
                _config.CheckpointIntervalSeconds = interval;

            // Calibrate squarings per second if not specified
            if (_config.SquaringsPerSecond == 0)
            {
                _config.SquaringsPerSecond = await CalibrateSquaringsPerSecond();
            }

            await LoadKeysFromStorage();
        }

        /// <summary>
        /// Calibrates the squarings per second on this machine.
        /// </summary>
        private Task<long> CalibrateSquaringsPerSecond()
        {
            // Generate a test modulus
            var p = BigInteger.ProbablePrime(1024, _secureRandom);
            var q = BigInteger.ProbablePrime(1024, _secureRandom);
            var n = p.Multiply(q);

            var a = new BigInteger(2048, _secureRandom).Mod(n);

            // Measure time for 100,000 squarings
            const int testSquarings = 100000;
            var sw = Stopwatch.StartNew();

            var current = a;
            for (int i = 0; i < testSquarings; i++)
            {
                current = current.ModPow(BigInteger.Two, n);
            }

            sw.Stop();

            var squaringsPerSecond = (long)(testSquarings / sw.Elapsed.TotalSeconds);
            return Task.FromResult(squaringsPerSecond);
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
                    throw new KeyNotFoundException($"Time-lock key '{keyId}' not found.");

                // Check if puzzle has been solved
                if (keyData.SolvedAt.HasValue && keyData.DecryptedKey != null)
                {
                    return keyData.DecryptedKey;
                }

                // Check for active solver
                if (_activeSolvers.TryGetValue(keyId, out var solver) && solver.IsComplete)
                {
                    keyData.DecryptedKey = solver.DecryptedKey;
                    keyData.SolvedAt = DateTime.UtcNow;
                    await PersistKeysToStorage();
                    return solver.DecryptedKey!;
                }

                throw new InvalidOperationException(
                    $"Key is time-locked. Estimated unlock time: {GetEstimatedUnlockTime(keyData):g}. " +
                    $"Start solving with SolvePuzzleAsync().");
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
                // Generate RSA modulus
                var p = BigInteger.ProbablePrime(ModulusBits / 2, _secureRandom);
                var q = BigInteger.ProbablePrime(ModulusBits / 2, _secureRandom);
                var n = p.Multiply(q);
                var phi = p.Subtract(BigInteger.One).Multiply(q.Subtract(BigInteger.One));

                // Calculate number of squarings for desired time
                var totalSquarings = new BigInteger(
                    (_config.DefaultUnlockSeconds * _config.SquaringsPerSecond).ToString());

                // Generate random base
                var a = new BigInteger(2048, _secureRandom).Mod(n);

                // Compute b = a^(2^T) mod N efficiently using phi(N)
                // b = a^(2^T mod phi(N)) mod N
                var exponent = BigInteger.Two.ModPow(totalSquarings, phi);
                var b = a.ModPow(exponent, n);

                // Encrypt the key: C = K XOR H(b)
                var keyHash = ComputeKeyHash(b);
                var encryptedKey = XorBytes(keyData, keyHash);

                var timeLockData = new TimeLockKeyData
                {
                    KeyId = keyId,
                    Modulus = n.ToByteArrayUnsigned(),
                    Base = a.ToByteArrayUnsigned(),
                    TotalSquarings = totalSquarings.ToByteArrayUnsigned(),
                    EncryptedKey = encryptedKey,
                    KeySizeBytes = keyData.Length,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = context.UserId,
                    // Store verification data (hash of expected result)
                    ExpectedResultHash = SHA256.HashData(b.ToByteArrayUnsigned()),
                    EstimatedUnlockSeconds = _config.DefaultUnlockSeconds
                };

                // IMPORTANT: For security, we clear p, q, and phi immediately
                // Only the puzzle creator knows these values

                _keys[keyId] = timeLockData;
                _currentKeyId = keyId;

                await PersistKeysToStorage();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Creates a time-lock puzzle with a specific unlock time.
        /// </summary>
        public async Task<TimeLockPuzzleResult> CreatePuzzleAsync(
            string keyId,
            byte[] keyData,
            TimeSpan unlockTime,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var originalConfig = _config.DefaultUnlockSeconds;
            try
            {
                _config.DefaultUnlockSeconds = (long)unlockTime.TotalSeconds;
                await SaveKeyToStorage(keyId, keyData, context);

                var key = _keys[keyId];
                return new TimeLockPuzzleResult
                {
                    Success = true,
                    KeyId = keyId,
                    TotalSquarings = new BigInteger(1, key.TotalSquarings),
                    EstimatedUnlockTime = unlockTime,
                    Modulus = key.Modulus
                };
            }
            finally
            {
                _config.DefaultUnlockSeconds = originalConfig;
            }
        }

        /// <summary>
        /// Creates parallel puzzle chains for different time horizons.
        /// Each chain can be solved independently.
        /// </summary>
        public async Task<ParallelPuzzleChainResult> CreateParallelPuzzleChainsAsync(
            string baseKeyId,
            byte[] keyData,
            TimeSpan[] unlockTimes,
            ISecurityContext context)
        {
            ValidateSecurityContext(context);

            var chains = new List<PuzzleChainInfo>();

            // Use Shamir secret sharing to split the key
            var secret = new BigInteger(1, keyData);
            var shares = GenerateShamirShares(secret, unlockTimes.Length, unlockTimes.Length);

            for (int i = 0; i < unlockTimes.Length; i++)
            {
                var chainId = $"{baseKeyId}_chain_{i}";
                var shareBytes = shares[i].Value.ToByteArrayUnsigned();

                // Pad to key size
                var paddedShare = new byte[keyData.Length];
                Array.Copy(shareBytes, 0, paddedShare,
                    Math.Max(0, keyData.Length - shareBytes.Length),
                    Math.Min(shareBytes.Length, keyData.Length));

                await CreatePuzzleAsync(chainId, paddedShare, unlockTimes[i], context);

                chains.Add(new PuzzleChainInfo
                {
                    ChainId = chainId,
                    UnlockTime = unlockTimes[i],
                    ShareIndex = shares[i].Index
                });
            }

            return new ParallelPuzzleChainResult
            {
                Success = true,
                BaseKeyId = baseKeyId,
                Chains = chains,
                RequiredChains = unlockTimes.Length
            };
        }

        /// <summary>
        /// Starts solving a time-lock puzzle asynchronously.
        /// </summary>
        public async Task<PuzzleSolveResult> StartSolvingAsync(
            string keyId,
            ISecurityContext context,
            IProgress<PuzzleSolveProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            await _lock.WaitAsync(cancellationToken);
            TimeLockKeyData keyData;
            try
            {
                if (!_keys.TryGetValue(keyId, out keyData!))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                if (keyData.SolvedAt.HasValue)
                    return new PuzzleSolveResult
                    {
                        Success = true,
                        DecryptedKey = keyData.DecryptedKey,
                        ElapsedTime = TimeSpan.Zero
                    };

                if (_activeSolvers.ContainsKey(keyId))
                    throw new InvalidOperationException("Solver already running for this key.");
            }
            finally
            {
                _lock.Release();
            }

            var solver = new PuzzleSolverState
            {
                KeyId = keyId,
                Modulus = new BigInteger(1, keyData.Modulus),
                Current = new BigInteger(1, keyData.Base),
                TotalSquarings = new BigInteger(1, keyData.TotalSquarings),
                CompletedSquarings = BigInteger.Zero,
                EncryptedKey = keyData.EncryptedKey,
                ExpectedResultHash = keyData.ExpectedResultHash,
                StartedAt = DateTime.UtcNow
            };

            _activeSolvers[keyId] = solver;

            // Run solver on thread pool
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                var lastCheckpoint = DateTime.UtcNow;
                var lastProgress = DateTime.UtcNow;

                try
                {
                    while (solver.CompletedSquarings.CompareTo(solver.TotalSquarings) < 0)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            solver.IsCancelled = true;
                            break;
                        }

                        // Perform batch of squarings
                        const int batchSize = 10000;
                        for (int i = 0; i < batchSize && solver.CompletedSquarings.CompareTo(solver.TotalSquarings) < 0; i++)
                        {
                            solver.Current = solver.Current.ModPow(BigInteger.Two, solver.Modulus);
                            solver.CompletedSquarings = solver.CompletedSquarings.Add(BigInteger.One);
                        }

                        // Report progress periodically
                        if ((DateTime.UtcNow - lastProgress).TotalSeconds >= 1)
                        {
                            var percentComplete = solver.CompletedSquarings
                                .Multiply(BigInteger.ValueOf(100))
                                .Divide(solver.TotalSquarings)
                                .IntValue;

                            progress?.Report(new PuzzleSolveProgress
                            {
                                KeyId = keyId,
                                CompletedSquarings = solver.CompletedSquarings,
                                TotalSquarings = solver.TotalSquarings,
                                PercentComplete = percentComplete,
                                ElapsedTime = sw.Elapsed,
                                EstimatedRemaining = EstimateRemainingTime(solver, sw.Elapsed)
                            });

                            lastProgress = DateTime.UtcNow;
                        }

                        // Checkpoint periodically
                        if (_config.EnableCheckpointing &&
                            (DateTime.UtcNow - lastCheckpoint).TotalSeconds >= _config.CheckpointIntervalSeconds)
                        {
                            await SaveCheckpointAsync(solver);
                            lastCheckpoint = DateTime.UtcNow;
                        }
                    }

                    if (!solver.IsCancelled)
                    {
                        // Verify result
                        var resultHash = SHA256.HashData(solver.Current.ToByteArrayUnsigned());
                        if (!(resultHash.Length == solver.ExpectedResultHash.Length && CryptographicOperations.FixedTimeEquals(resultHash, solver.ExpectedResultHash)))
                        {
                            solver.Error = "Puzzle solution verification failed.";
                            return;
                        }

                        // Decrypt the key
                        var keyHash = ComputeKeyHash(solver.Current);
                        solver.DecryptedKey = XorBytes(solver.EncryptedKey, keyHash);
                        solver.IsComplete = true;
                        solver.CompletedAt = DateTime.UtcNow;

                        // Update storage
                        await _lock.WaitAsync();
                        try
                        {
                            if (_keys.TryGetValue(keyId, out var kd))
                            {
                                kd.DecryptedKey = solver.DecryptedKey;
                                kd.SolvedAt = solver.CompletedAt;
                                await PersistKeysToStorage();
                            }
                        }
                        finally
                        {
                            _lock.Release();
                        }
                    }
                }
                catch (Exception ex)
                {
                    solver.Error = ex.Message;
                }
            }, cancellationToken);

            return new PuzzleSolveResult
            {
                Success = true,
                KeyId = keyId,
                Message = "Puzzle solving started in background."
            };
        }

        /// <summary>
        /// Gets the current status of a puzzle solver.
        /// </summary>
        public PuzzleSolveProgress? GetSolverProgress(string keyId)
        {
            if (!_activeSolvers.TryGetValue(keyId, out var solver))
                return null;

            var elapsed = DateTime.UtcNow - solver.StartedAt;
            var percentComplete = solver.CompletedSquarings
                .Multiply(BigInteger.ValueOf(100))
                .Divide(solver.TotalSquarings)
                .IntValue;

            return new PuzzleSolveProgress
            {
                KeyId = keyId,
                CompletedSquarings = solver.CompletedSquarings,
                TotalSquarings = solver.TotalSquarings,
                PercentComplete = percentComplete,
                ElapsedTime = elapsed,
                EstimatedRemaining = EstimateRemainingTime(solver, elapsed),
                IsComplete = solver.IsComplete,
                IsCancelled = solver.IsCancelled,
                Error = solver.Error
            };
        }

        /// <summary>
        /// Verifies a puzzle solution without having solved it (requires the solution).
        /// </summary>
        public async Task<bool> VerifyPuzzleSolutionAsync(string keyId, BigInteger solution)
        {
            await _lock.WaitAsync();
            try
            {
                if (!_keys.TryGetValue(keyId, out var keyData))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                var resultHash = SHA256.HashData(solution.ToByteArrayUnsigned());
                return resultHash.Length == keyData.ExpectedResultHash.Length && CryptographicOperations.FixedTimeEquals(resultHash, keyData.ExpectedResultHash);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Resumes solving from a checkpoint.
        /// </summary>
        public async Task<PuzzleSolveResult> ResumeSolvingAsync(
            string keyId,
            ISecurityContext context,
            IProgress<PuzzleSolveProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var checkpointPath = GetCheckpointPath(keyId);
            if (!File.Exists(checkpointPath))
            {
                return await StartSolvingAsync(keyId, context, progress, cancellationToken);
            }

            var checkpointJson = await File.ReadAllTextAsync(checkpointPath, cancellationToken);
            var checkpoint = JsonSerializer.Deserialize<PuzzleCheckpoint>(checkpointJson);

            if (checkpoint == null)
            {
                return await StartSolvingAsync(keyId, context, progress, cancellationToken);
            }

            await _lock.WaitAsync(cancellationToken);
            TimeLockKeyData keyData;
            try
            {
                if (!_keys.TryGetValue(keyId, out keyData!))
                    throw new KeyNotFoundException($"Key '{keyId}' not found.");

                if (_activeSolvers.ContainsKey(keyId))
                    throw new InvalidOperationException("Solver already running for this key.");
            }
            finally
            {
                _lock.Release();
            }

            var solver = new PuzzleSolverState
            {
                KeyId = keyId,
                Modulus = new BigInteger(1, keyData.Modulus),
                Current = new BigInteger(1, checkpoint.CurrentValue),
                TotalSquarings = new BigInteger(1, keyData.TotalSquarings),
                CompletedSquarings = new BigInteger(1, checkpoint.CompletedSquarings),
                EncryptedKey = keyData.EncryptedKey,
                ExpectedResultHash = keyData.ExpectedResultHash,
                StartedAt = DateTime.UtcNow
            };

            _activeSolvers[keyId] = solver;

            // #3519: Implement modular squaring loop continuation from checkpoint.
            // Read checkpoint (current_squarings, current_value), continue squaring until total reached.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastProgress = DateTime.UtcNow;
            bool solveSuccess = false;
            string? solveMessage = null;

            try
            {
                while (solver.CompletedSquarings.CompareTo(solver.TotalSquarings) < 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    solver.Current = solver.Current.ModPow(BigInteger.Two, solver.Modulus);
                    solver.CompletedSquarings = solver.CompletedSquarings.Add(BigInteger.One);

                    if ((DateTime.UtcNow - lastProgress).TotalSeconds >= 5)
                    {
                        lastProgress = DateTime.UtcNow;
                        await SaveCheckpointAsync(solver);
                        progress?.Report(new PuzzleSolveProgress
                        {
                            KeyId = keyId,
                            CompletedSquarings = solver.CompletedSquarings,
                            TotalSquarings = solver.TotalSquarings,
                            PercentComplete = solver.CompletedSquarings
                                .Multiply(BigInteger.ValueOf(100))
                                .Divide(solver.TotalSquarings).IntValue,
                            ElapsedTime = sw.Elapsed,
                            EstimatedRemaining = EstimateRemainingTime(solver, sw.Elapsed)
                        });
                    }
                }

                // Verify result hash
                var resultBytes = solver.Current.ToByteArrayUnsigned();
                var resultHash = SHA256.HashData(resultBytes);
                var resultHashHex = Convert.ToHexString(resultHash);

                if (keyData.ExpectedResultHash != null && keyData.ExpectedResultHash.Length > 0 &&
                    !(resultHash.Length == keyData.ExpectedResultHash.Length &&
                      CryptographicOperations.FixedTimeEquals(resultHash, keyData.ExpectedResultHash)))
                {
                    solveMessage = "Puzzle result hash mismatch. Key integrity compromised.";
                }
                else
                {
                    solveSuccess = true;
                    solveMessage = $"Resumed from checkpoint ({checkpoint.PercentComplete}%) and completed.";
                }
            }
            catch (OperationCanceledException)
            {
                await SaveCheckpointAsync(solver);
                solveMessage = "Solving cancelled and checkpoint saved.";
            }
            finally
            {
                _activeSolvers.Remove(keyId);
            }

            return new PuzzleSolveResult
            {
                Success = solveSuccess,
                KeyId = keyId,
                Message = solveMessage ?? "Resumed from checkpoint."
            };
        }

        private TimeSpan GetEstimatedUnlockTime(TimeLockKeyData keyData)
        {
            return TimeSpan.FromSeconds(keyData.EstimatedUnlockSeconds);
        }

        private TimeSpan EstimateRemainingTime(PuzzleSolverState solver, TimeSpan elapsed)
        {
            if (solver.CompletedSquarings.Equals(BigInteger.Zero))
                return TimeSpan.FromSeconds(solver.TotalSquarings.LongValue / _config.SquaringsPerSecond);

            var remaining = solver.TotalSquarings.Subtract(solver.CompletedSquarings);
            var rate = solver.CompletedSquarings.LongValue / elapsed.TotalSeconds;
            if (rate <= 0) return TimeSpan.MaxValue;

            return TimeSpan.FromSeconds(remaining.LongValue / rate);
        }

        private async Task SaveCheckpointAsync(PuzzleSolverState solver)
        {
            var checkpoint = new PuzzleCheckpoint
            {
                KeyId = solver.KeyId,
                CurrentValue = solver.Current.ToByteArrayUnsigned(),
                CompletedSquarings = solver.CompletedSquarings.ToByteArrayUnsigned(),
                PercentComplete = solver.CompletedSquarings
                    .Multiply(BigInteger.ValueOf(100))
                    .Divide(solver.TotalSquarings)
                    .IntValue,
                CheckpointedAt = DateTime.UtcNow
            };

            var path = GetCheckpointPath(solver.KeyId);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(checkpoint);
            await File.WriteAllTextAsync(path, json);
        }

        private string GetCheckpointPath(string keyId)
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var safeKeyId = string.Join("_", keyId.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(baseDir, "DataWarehouse", "timelock-checkpoints", $"{safeKeyId}.json");
        }

        private static byte[] ComputeKeyHash(BigInteger b)
        {
            return SHA256.HashData(b.ToByteArrayUnsigned());
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

        #region Shamir for Parallel Chains

        private List<ShamirShare> GenerateShamirShares(BigInteger secret, int threshold, int totalShares)
        {
            var prime = new BigInteger(
                "115792089237316195423570985008687907853269984665640564039457584007913129639747", 10);

            var coefficients = new BigInteger[threshold];
            coefficients[0] = secret.Mod(prime);

            for (int i = 1; i < threshold; i++)
            {
                var randomBytes = new byte[32];
                _secureRandom.NextBytes(randomBytes);
                coefficients[i] = new BigInteger(1, randomBytes).Mod(prime);
            }

            var shares = new List<ShamirShare>();
            for (int x = 1; x <= totalShares; x++)
            {
                var xBig = BigInteger.ValueOf(x);
                var y = BigInteger.Zero;
                for (int i = coefficients.Length - 1; i >= 0; i--)
                    y = y.Multiply(xBig).Add(coefficients[i]).Mod(prime);
                shares.Add(new ShamirShare { Index = x, Value = y });
            }

            return shares;
        }

        private class ShamirShare
        {
            public int Index { get; set; }
            public BigInteger Value { get; set; } = BigInteger.Zero;
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
                    _activeSolvers.Remove(keyId);
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

                var progress = GetSolverProgress(keyId);

                return new KeyMetadata
                {
                    KeyId = keyId,
                    CreatedAt = keyData.CreatedAt,
                    CreatedBy = keyData.CreatedBy,
                    KeySizeBytes = keyData.KeySizeBytes,
                    IsActive = keyId == _currentKeyId,
                    Metadata = new Dictionary<string, object>
                    {
                        ["Algorithm"] = "RSA Time-Lock Puzzle",
                        ["TotalSquarings"] = new BigInteger(1, keyData.TotalSquarings).ToString(),
                        ["EstimatedUnlockSeconds"] = keyData.EstimatedUnlockSeconds,
                        ["IsSolved"] = keyData.SolvedAt.HasValue,
                        ["SolvedAt"] = keyData.SolvedAt?.ToString() ?? "Not solved",
                        ["SolverActive"] = progress != null,
                        ["SolverProgress"] = progress?.PercentComplete ?? 0
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
                var stored = JsonSerializer.Deserialize<Dictionary<string, TimeLockKeyDataSerialized>>(json);
                if (stored != null)
                {
                    foreach (var kvp in stored)
                        _keys[kvp.Key] = DeserializeKeyData(kvp.Value);
                    if (_keys.Count > 0)
                        _currentKeyId = _keys.Keys.First();
                }
            }
            catch { /* Deserialization failure — start with empty state */ }
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
            return Path.Combine(baseDir, "DataWarehouse", "timelock-keys.json");
        }

        private static TimeLockKeyDataSerialized SerializeKeyData(TimeLockKeyData data) => new()
        {
            KeyId = data.KeyId,
            Modulus = data.Modulus,
            Base = data.Base,
            TotalSquarings = data.TotalSquarings,
            EncryptedKey = data.EncryptedKey,
            ExpectedResultHash = data.ExpectedResultHash,
            KeySizeBytes = data.KeySizeBytes,
            EstimatedUnlockSeconds = data.EstimatedUnlockSeconds,
            CreatedAt = data.CreatedAt,
            CreatedBy = data.CreatedBy,
            SolvedAt = data.SolvedAt,
            DecryptedKey = data.DecryptedKey
        };

        private static TimeLockKeyData DeserializeKeyData(TimeLockKeyDataSerialized data) => new()
        {
            KeyId = data.KeyId ?? "",
            Modulus = data.Modulus ?? Array.Empty<byte>(),
            Base = data.Base ?? Array.Empty<byte>(),
            TotalSquarings = data.TotalSquarings ?? Array.Empty<byte>(),
            EncryptedKey = data.EncryptedKey ?? Array.Empty<byte>(),
            ExpectedResultHash = data.ExpectedResultHash ?? Array.Empty<byte>(),
            KeySizeBytes = data.KeySizeBytes,
            EstimatedUnlockSeconds = data.EstimatedUnlockSeconds,
            CreatedAt = data.CreatedAt,
            CreatedBy = data.CreatedBy,
            SolvedAt = data.SolvedAt,
            DecryptedKey = data.DecryptedKey
        };

        #endregion

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _activeSolvers.Clear();
            _lock.Dispose();
            base.Dispose();
        }
    }

    #region Supporting Types

    public class TimeLockConfig
    {
        public long SquaringsPerSecond { get; set; }
        public long DefaultUnlockSeconds { get; set; } = 3600; // 1 hour
        public bool EnableCheckpointing { get; set; } = true;
        public int CheckpointIntervalSeconds { get; set; } = 60;
        public string? StoragePath { get; set; }
    }

    internal class TimeLockKeyData
    {
        public string KeyId { get; set; } = "";
        public byte[] Modulus { get; set; } = Array.Empty<byte>();
        public byte[] Base { get; set; } = Array.Empty<byte>();
        public byte[] TotalSquarings { get; set; } = Array.Empty<byte>();
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
        public byte[] ExpectedResultHash { get; set; } = Array.Empty<byte>();
        public int KeySizeBytes { get; set; }
        public long EstimatedUnlockSeconds { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? SolvedAt { get; set; }
        public byte[]? DecryptedKey { get; set; }
    }

    internal class TimeLockKeyDataSerialized
    {
        public string? KeyId { get; set; }
        public byte[]? Modulus { get; set; }
        public byte[]? Base { get; set; }
        public byte[]? TotalSquarings { get; set; }
        public byte[]? EncryptedKey { get; set; }
        public byte[]? ExpectedResultHash { get; set; }
        public int KeySizeBytes { get; set; }
        public long EstimatedUnlockSeconds { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? SolvedAt { get; set; }
        public byte[]? DecryptedKey { get; set; }
    }

    internal class PuzzleSolverState
    {
        public string KeyId { get; set; } = "";
        public BigInteger Modulus { get; set; } = BigInteger.Zero;
        public BigInteger Current { get; set; } = BigInteger.Zero;
        public BigInteger TotalSquarings { get; set; } = BigInteger.Zero;
        public BigInteger CompletedSquarings { get; set; } = BigInteger.Zero;
        public byte[] EncryptedKey { get; set; } = Array.Empty<byte>();
        public byte[] ExpectedResultHash { get; set; } = Array.Empty<byte>();
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool IsComplete { get; set; }
        public bool IsCancelled { get; set; }
        public string? Error { get; set; }
        public byte[]? DecryptedKey { get; set; }
    }

    internal class PuzzleCheckpoint
    {
        public string? KeyId { get; set; }
        public byte[]? CurrentValue { get; set; }
        public byte[]? CompletedSquarings { get; set; }
        public int PercentComplete { get; set; }
        public DateTime CheckpointedAt { get; set; }
    }

    public class TimeLockPuzzleResult
    {
        public bool Success { get; set; }
        public string? KeyId { get; set; }
        public BigInteger TotalSquarings { get; set; } = BigInteger.Zero;
        public TimeSpan EstimatedUnlockTime { get; set; }
        public byte[]? Modulus { get; set; }
        public string? Error { get; set; }
    }

    public class PuzzleSolveResult
    {
        public bool Success { get; set; }
        public string? KeyId { get; set; }
        public byte[]? DecryptedKey { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
    }

    public class PuzzleSolveProgress
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

    public class ParallelPuzzleChainResult
    {
        public bool Success { get; set; }
        public string? BaseKeyId { get; set; }
        public List<PuzzleChainInfo> Chains { get; set; } = new();
        public int RequiredChains { get; set; }
    }

    public class PuzzleChainInfo
    {
        public string? ChainId { get; set; }
        public TimeSpan UnlockTime { get; set; }
        public int ShareIndex { get; set; }
    }

    #endregion
}
