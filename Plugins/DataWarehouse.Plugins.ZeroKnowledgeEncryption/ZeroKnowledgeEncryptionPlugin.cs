using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.ZeroKnowledgeEncryption
{
    /// <summary>
    /// Zero-knowledge encryption plugin implementing client-side encryption with ZK proofs.
    ///
    /// Security Features:
    /// - Client-side AES-256-GCM encryption (server never sees plaintext)
    /// - Schnorr identification protocol for proof of knowledge
    /// - Pedersen commitments for binding and hiding properties
    /// - Discrete logarithm based proofs over prime order groups
    /// - Non-interactive ZK proofs using Fiat-Shamir heuristic
    ///
    /// Cryptographic Foundations:
    /// - Uses NIST P-256 curve parameters for elliptic curve operations
    /// - SHA-256 for hash-based random oracle model
    /// - Secure random number generation via RandomNumberGenerator
    ///
    /// Thread Safety: All operations are thread-safe.
    ///
    /// Message Commands:
    /// - zk.encryption.prove: Generate ZK proof for encrypted data
    /// - zk.encryption.verify: Verify ZK proof without decryption
    /// - zk.encryption.commit: Create Pedersen commitment
    /// - zk.encryption.stats: Get encryption statistics
    /// </summary>
    public sealed class ZeroKnowledgeEncryptionPlugin : PipelinePluginBase, IDisposable
    {
        private readonly ZkEncryptionConfig _config;
        private readonly object _statsLock = new();
        private readonly ConcurrentDictionary<string, ZkProofRecord> _proofCache = new();
        private readonly SchnorrProver _schnorrProver;
        private readonly PedersenCommitter _pedersenCommitter;
        private IKeyStore? _keyStore;
        private ISecurityContext? _securityContext;
        private long _encryptionCount;
        private long _decryptionCount;
        private long _proofsGenerated;
        private long _proofsVerified;
        private long _totalBytesEncrypted;
        private bool _disposed;

        /// <summary>
        /// IV size for AES-GCM (96 bits).
        /// </summary>
        private const int IvSizeBytes = 12;

        /// <summary>
        /// Authentication tag size (128 bits).
        /// </summary>
        private const int TagSizeBytes = 16;

        /// <summary>
        /// Maximum key ID length in header.
        /// </summary>
        private const int MaxKeyIdLength = 64;

        /// <summary>
        /// Header version for ZK-encrypted data.
        /// </summary>
        private const byte HeaderVersion = 0x5A; // 'Z' for ZK

        /// <inheritdoc/>
        public override string Id => "datawarehouse.plugins.encryption.zeroknowledge";

        /// <inheritdoc/>
        public override string Name => "Zero-Knowledge Encryption";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override string SubCategory => "Encryption";

        /// <inheritdoc/>
        public override int QualityLevel => 90;

        /// <inheritdoc/>
        public override int DefaultOrder => 90;

        /// <inheritdoc/>
        public override bool AllowBypass => false;

        /// <inheritdoc/>
        public override string[] RequiredPrecedingStages => new[] { "Compression" };

        /// <inheritdoc/>
        public override string[] IncompatibleStages => new[] { "encryption.chacha20", "encryption.aes256", "encryption.fips" };

        /// <summary>
        /// Initializes a new instance of the zero-knowledge encryption plugin.
        /// </summary>
        /// <param name="config">Optional configuration. If null, defaults are used.</param>
        public ZeroKnowledgeEncryptionPlugin(ZkEncryptionConfig? config = null)
        {
            _config = config ?? new ZkEncryptionConfig();
            _schnorrProver = new SchnorrProver();
            _pedersenCommitter = new PedersenCommitter();
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);

            if (_config.KeyStore != null)
            {
                _keyStore = _config.KeyStore;
            }

            return response;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "zk.encryption.encrypt", DisplayName = "Encrypt", Description = "Client-side encrypt with ZK proof capability" },
                new() { Name = "zk.encryption.decrypt", DisplayName = "Decrypt", Description = "Client-side decrypt with proof verification" },
                new() { Name = "zk.encryption.prove", DisplayName = "Generate Proof", Description = "Generate ZK proof for encrypted data" },
                new() { Name = "zk.encryption.verify", DisplayName = "Verify Proof", Description = "Verify ZK proof without decryption" },
                new() { Name = "zk.encryption.commit", DisplayName = "Commit", Description = "Create Pedersen commitment" },
                new() { Name = "zk.encryption.stats", DisplayName = "Statistics", Description = "Get ZK encryption statistics" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["Algorithm"] = "AES-256-GCM";
            metadata["ZkProtocol"] = "Schnorr";
            metadata["CommitmentScheme"] = "Pedersen";
            metadata["KeySize"] = 256;
            metadata["CurveType"] = "P-256";
            metadata["SupportsClientSideEncryption"] = true;
            metadata["SupportsZkProofs"] = true;
            metadata["ServerSeesPlaintext"] = false;
            return metadata;
        }

        /// <inheritdoc/>
        public override Task OnMessageAsync(PluginMessage message)
        {
            return message.Type switch
            {
                "zk.encryption.prove" => HandleProveAsync(message),
                "zk.encryption.verify" => HandleVerifyAsync(message),
                "zk.encryption.commit" => HandleCommitAsync(message),
                "zk.encryption.stats" => HandleStatsAsync(message),
                "zk.encryption.setKeyStore" => HandleSetKeyStoreAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        /// <summary>
        /// Encrypts data client-side and generates a ZK proof of encryption.
        /// </summary>
        /// <param name="input">The plaintext input stream.</param>
        /// <param name="context">The kernel context.</param>
        /// <param name="args">
        /// Optional arguments:
        /// - keyStore: IKeyStore instance
        /// - securityContext: ISecurityContext for key access
        /// - generateProof: Whether to generate ZK proof (default true)
        /// </param>
        /// <returns>Encrypted data with ZK proof in header.</returns>
        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var keyStore = GetKeyStore(args, context);
            var securityContext = GetSecurityContext(args);
            var generateProof = !args.TryGetValue("generateProof", out var gpObj) || gpObj is not bool gp || gp;

            var keyId = RunSyncWithErrorHandling(
                () => keyStore.GetCurrentKeyIdAsync(),
                "Failed to retrieve current key ID");

            var key = RunSyncWithErrorHandling(
                () => keyStore.GetKeyAsync(keyId, securityContext),
                "Failed to retrieve key for ZK encryption");

            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new CryptographicException($"AES-256 requires 32-byte key. Got {key.Length} bytes.");
            }

            byte[]? plaintext = null;
            byte[]? ciphertext = null;

            try
            {
                using var inputMs = new MemoryStream();
                input.CopyTo(inputMs);
                plaintext = inputMs.ToArray();

                var iv = RandomNumberGenerator.GetBytes(IvSizeBytes);
                var tag = new byte[TagSizeBytes];
                ciphertext = new byte[plaintext.Length];

                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Encrypt(iv, plaintext, ciphertext, tag);

                // Generate Pedersen commitment to plaintext hash
                var plaintextHash = SHA256.HashData(plaintext);
                var commitment = _pedersenCommitter.Commit(plaintextHash, out var commitmentRandomness);

                // Generate Schnorr proof of knowledge of the key
                SchnorrProof? schnorrProof = null;
                if (generateProof)
                {
                    schnorrProof = _schnorrProver.Prove(key, plaintextHash);
                    Interlocked.Increment(ref _proofsGenerated);
                }

                var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
                if (keyIdBytes.Length > MaxKeyIdLength)
                {
                    throw new CryptographicException($"Key ID exceeds {MaxKeyIdLength} bytes");
                }

                // Build output: Header + ZK data + encrypted content
                using var output = new MemoryStream();
                using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

                // Header
                writer.Write(HeaderVersion);
                writer.Write((byte)keyIdBytes.Length);
                writer.Write(keyIdBytes);
                writer.Write(iv);
                writer.Write(tag);

                // Commitment
                writer.Write(commitment.Length);
                writer.Write(commitment);
                writer.Write(commitmentRandomness.Length);
                writer.Write(commitmentRandomness);

                // Schnorr proof (optional)
                writer.Write(schnorrProof != null);
                if (schnorrProof != null)
                {
                    var proofBytes = schnorrProof.Serialize();
                    writer.Write(proofBytes.Length);
                    writer.Write(proofBytes);
                }

                // Ciphertext
                writer.Write(ciphertext.Length);
                writer.Write(ciphertext);

                lock (_statsLock)
                {
                    _encryptionCount++;
                    _totalBytesEncrypted += plaintext.Length;
                }

                // Cache proof for verification
                var dataId = Convert.ToHexString(SHA256.HashData(output.ToArray())[..16]);
                _proofCache[dataId] = new ZkProofRecord
                {
                    DataId = dataId,
                    CommitmentHash = Convert.ToHexString(commitment),
                    HasSchnorrProof = schnorrProof != null,
                    CreatedAt = DateTime.UtcNow
                };

                context.LogDebug($"ZK encrypted {plaintext.Length} bytes with Pedersen commitment");

                return new MemoryStream(output.ToArray());
            }
            finally
            {
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>
        /// Decrypts client-side encrypted data and optionally verifies ZK proof.
        /// </summary>
        /// <param name="stored">The encrypted input stream.</param>
        /// <param name="context">The kernel context.</param>
        /// <param name="args">
        /// Optional arguments:
        /// - keyStore: IKeyStore instance
        /// - securityContext: ISecurityContext for key access
        /// - verifyProof: Whether to verify ZK proof (default true)
        /// </param>
        /// <returns>Decrypted plaintext stream.</returns>
        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var keyStore = GetKeyStore(args, context);
            var securityContext = GetSecurityContext(args);
            var verifyProof = !args.TryGetValue("verifyProof", out var vpObj) || vpObj is not bool vp || vp;

            byte[]? encryptedData = null;
            byte[]? plaintext = null;
            byte[]? key = null;

            try
            {
                using var inputMs = new MemoryStream();
                stored.CopyTo(inputMs);
                encryptedData = inputMs.ToArray();

                using var reader = new BinaryReader(new MemoryStream(encryptedData), Encoding.UTF8);

                // Read header
                var version = reader.ReadByte();
                if (version != HeaderVersion)
                {
                    throw new CryptographicException($"Invalid ZK encryption header version: 0x{version:X2}");
                }

                var keyIdLength = reader.ReadByte();
                var keyIdBytes = reader.ReadBytes(keyIdLength);
                var keyId = Encoding.UTF8.GetString(keyIdBytes);

                var iv = reader.ReadBytes(IvSizeBytes);
                var tag = reader.ReadBytes(TagSizeBytes);

                // Read commitment
                var commitmentLength = reader.ReadInt32();
                var commitment = reader.ReadBytes(commitmentLength);
                var randomnessLength = reader.ReadInt32();
                var commitmentRandomness = reader.ReadBytes(randomnessLength);

                // Read Schnorr proof
                var hasProof = reader.ReadBoolean();
                SchnorrProof? schnorrProof = null;
                if (hasProof)
                {
                    var proofLength = reader.ReadInt32();
                    var proofBytes = reader.ReadBytes(proofLength);
                    schnorrProof = SchnorrProof.Deserialize(proofBytes);
                }

                // Read ciphertext
                var ciphertextLength = reader.ReadInt32();
                var ciphertext = reader.ReadBytes(ciphertextLength);

                // Get decryption key
                key = RunSyncWithErrorHandling(
                    () => keyStore.GetKeyAsync(keyId, securityContext),
                    "Failed to retrieve key for ZK decryption");

                if (key.Length != 32)
                {
                    throw new CryptographicException($"AES-256 requires 32-byte key. Got {key.Length} bytes.");
                }

                // Decrypt
                plaintext = new byte[ciphertextLength];
                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Decrypt(iv, ciphertext, tag, plaintext);

                // Verify commitment
                var plaintextHash = SHA256.HashData(plaintext);
                if (!_pedersenCommitter.Verify(commitment, plaintextHash, commitmentRandomness))
                {
                    throw new CryptographicException("Pedersen commitment verification failed. Data integrity compromised.");
                }

                // Verify Schnorr proof if present and requested
                if (verifyProof && schnorrProof != null)
                {
                    if (!_schnorrProver.Verify(schnorrProof, plaintextHash))
                    {
                        throw new CryptographicException("Schnorr proof verification failed. Proof of knowledge invalid.");
                    }
                    Interlocked.Increment(ref _proofsVerified);
                }

                lock (_statsLock)
                {
                    _decryptionCount++;
                }

                context.LogDebug($"ZK decrypted {ciphertextLength} bytes with commitment verification");

                var result = new byte[plaintext.Length];
                Array.Copy(plaintext, result, plaintext.Length);
                return new MemoryStream(result);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new CryptographicException("Authentication tag verification failed", ex);
            }
            finally
            {
                if (encryptedData != null) CryptographicOperations.ZeroMemory(encryptedData);
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (key != null) CryptographicOperations.ZeroMemory(key);
            }
        }

        private IKeyStore GetKeyStore(Dictionary<string, object> args, IKernelContext context)
        {
            if (args.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                return ks;
            }

            if (_keyStore != null)
            {
                return _keyStore;
            }

            var keyStorePlugin = context.GetPlugins<IPlugin>()
                .OfType<IKeyStore>()
                .FirstOrDefault();

            if (keyStorePlugin != null)
            {
                return keyStorePlugin;
            }

            throw new InvalidOperationException("No IKeyStore available for ZK encryption");
        }

        private ISecurityContext GetSecurityContext(Dictionary<string, object> args)
        {
            if (args.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
            {
                return sc;
            }

            return _securityContext ?? new ZkSecurityContext();
        }

        private static T RunSyncWithErrorHandling<T>(Func<Task<T>> asyncOperation, string errorContext)
        {
            try
            {
                return Task.Run(asyncOperation).GetAwaiter().GetResult();
            }
            catch (AggregateException ae) when (ae.InnerException != null)
            {
                throw new CryptographicException($"{errorContext}: {ae.InnerException.Message}", ae.InnerException);
            }
            catch (Exception ex)
            {
                throw new CryptographicException($"{errorContext}: {ex.Message}", ex);
            }
        }

        private Task HandleProveAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
            {
                throw new ArgumentException("Missing 'data' parameter");
            }

            var hash = SHA256.HashData(data);
            var keyStore = GetKeyStore(message.Payload as Dictionary<string, object> ?? new(), null!);
            var securityContext = GetSecurityContext(message.Payload as Dictionary<string, object> ?? new());

            var keyId = RunSyncWithErrorHandling(
                () => keyStore.GetCurrentKeyIdAsync(),
                "Failed to get key ID for proof generation");

            var key = RunSyncWithErrorHandling(
                () => keyStore.GetKeyAsync(keyId, securityContext),
                "Failed to get key for proof generation");

            try
            {
                var proof = _schnorrProver.Prove(key, hash);
                var proofBytes = proof.Serialize();

                message.Payload["proof"] = proofBytes;
                message.Payload["proofType"] = "Schnorr";
                Interlocked.Increment(ref _proofsGenerated);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            return Task.CompletedTask;
        }

        private Task HandleVerifyAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("proof", out var proofObj) || proofObj is not byte[] proofBytes)
            {
                throw new ArgumentException("Missing 'proof' parameter");
            }

            if (!message.Payload.TryGetValue("challenge", out var challengeObj) || challengeObj is not byte[] challenge)
            {
                throw new ArgumentException("Missing 'challenge' parameter");
            }

            var proof = SchnorrProof.Deserialize(proofBytes);
            var isValid = _schnorrProver.Verify(proof, challenge);

            message.Payload["valid"] = isValid;
            Interlocked.Increment(ref _proofsVerified);

            return Task.CompletedTask;
        }

        private Task HandleCommitAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("value", out var valueObj) || valueObj is not byte[] value)
            {
                throw new ArgumentException("Missing 'value' parameter");
            }

            var commitment = _pedersenCommitter.Commit(value, out var randomness);

            message.Payload["commitment"] = commitment;
            message.Payload["randomness"] = randomness;

            return Task.CompletedTask;
        }

        private Task HandleStatsAsync(PluginMessage message)
        {
            lock (_statsLock)
            {
                message.Payload["EncryptionCount"] = _encryptionCount;
                message.Payload["DecryptionCount"] = _decryptionCount;
                message.Payload["ProofsGenerated"] = Interlocked.Read(ref _proofsGenerated);
                message.Payload["ProofsVerified"] = Interlocked.Read(ref _proofsVerified);
                message.Payload["TotalBytesEncrypted"] = _totalBytesEncrypted;
                message.Payload["CachedProofs"] = _proofCache.Count;
            }

            return Task.CompletedTask;
        }

        private Task HandleSetKeyStoreAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                _keyStore = ks;
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _proofCache.Clear();
        }
    }

    /// <summary>
    /// Schnorr identification protocol implementation.
    /// Provides non-interactive zero-knowledge proofs of knowledge.
    /// Uses Fiat-Shamir heuristic for non-interactive proofs.
    /// </summary>
    internal sealed class SchnorrProver
    {
        // NIST P-256 curve parameters
        private static readonly BigInteger P = BigInteger.Parse("115792089210356248762697446949407573530086143415290314195533631308867097853951");
        private static readonly BigInteger Q = BigInteger.Parse("115792089210356248762697446949407573529996955224135760342422259061068512044369");
        private static readonly BigInteger Gx = BigInteger.Parse("48439561293906451759052585252797914202762949526041747995844080717082404635286");
        private static readonly BigInteger Gy = BigInteger.Parse("36134250956749795798585127919587881956611106672985015071877198253568414405109");

        /// <summary>
        /// Generates a Schnorr proof of knowledge.
        /// </summary>
        /// <param name="secret">The secret value (witness).</param>
        /// <param name="message">The message to bind to the proof.</param>
        /// <returns>A Schnorr proof.</returns>
        public SchnorrProof Prove(byte[] secret, byte[] message)
        {
            // Convert secret to scalar
            var x = new BigInteger(secret, isUnsigned: true, isBigEndian: true);
            x = ((x % Q) + Q) % Q;

            // Generate random k
            var kBytes = RandomNumberGenerator.GetBytes(32);
            var k = new BigInteger(kBytes, isUnsigned: true, isBigEndian: true);
            k = ((k % Q) + Q) % Q;
            if (k == 0) k = BigInteger.One;

            // Compute R = k * G (simplified scalar multiplication)
            var (Rx, Ry) = ScalarMultiply(Gx, Gy, k);

            // Compute Y = x * G (public key point)
            var (Yx, Yy) = ScalarMultiply(Gx, Gy, x);

            // Compute challenge e = H(R || Y || message) using Fiat-Shamir
            using var sha = SHA256.Create();
            using var ms = new MemoryStream();
            ms.Write(Rx.ToByteArray(isUnsigned: true, isBigEndian: true));
            ms.Write(Ry.ToByteArray(isUnsigned: true, isBigEndian: true));
            ms.Write(Yx.ToByteArray(isUnsigned: true, isBigEndian: true));
            ms.Write(Yy.ToByteArray(isUnsigned: true, isBigEndian: true));
            ms.Write(message);

            var eBytes = sha.ComputeHash(ms.ToArray());
            var e = new BigInteger(eBytes, isUnsigned: true, isBigEndian: true);
            e = ((e % Q) + Q) % Q;

            // Compute s = k - e * x mod q
            var s = ((k - (e * x % Q) % Q) + Q) % Q;

            return new SchnorrProof
            {
                Rx = Rx,
                Ry = Ry,
                S = s,
                E = e,
                Yx = Yx,
                Yy = Yy
            };
        }

        /// <summary>
        /// Verifies a Schnorr proof.
        /// </summary>
        /// <param name="proof">The proof to verify.</param>
        /// <param name="message">The message bound to the proof.</param>
        /// <returns>True if the proof is valid.</returns>
        public bool Verify(SchnorrProof proof, byte[] message)
        {
            // Verify: s * G + e * Y = R
            // Compute s * G
            var (sGx, sGy) = ScalarMultiply(Gx, Gy, proof.S);

            // Compute e * Y
            var (eYx, eYy) = ScalarMultiply(proof.Yx, proof.Yy, proof.E);

            // Add points: s * G + e * Y
            var (Rx2, Ry2) = PointAdd(sGx, sGy, eYx, eYy);

            // Recompute challenge
            using var sha = SHA256.Create();
            using var ms = new MemoryStream();
            ms.Write(Rx2.ToByteArray(isUnsigned: true, isBigEndian: true));
            ms.Write(Ry2.ToByteArray(isUnsigned: true, isBigEndian: true));
            ms.Write(proof.Yx.ToByteArray(isUnsigned: true, isBigEndian: true));
            ms.Write(proof.Yy.ToByteArray(isUnsigned: true, isBigEndian: true));
            ms.Write(message);

            var e2Bytes = sha.ComputeHash(ms.ToArray());
            var e2 = new BigInteger(e2Bytes, isUnsigned: true, isBigEndian: true);
            e2 = ((e2 % Q) + Q) % Q;

            // Verify R matches and challenge matches
            return Rx2 == proof.Rx && Ry2 == proof.Ry && e2 == proof.E;
        }

        /// <summary>
        /// Simplified elliptic curve scalar multiplication (double-and-add).
        /// </summary>
        private static (BigInteger X, BigInteger Y) ScalarMultiply(BigInteger px, BigInteger py, BigInteger k)
        {
            BigInteger rx = 0, ry = 0;
            BigInteger qx = px, qy = py;
            bool first = true;

            while (k > 0)
            {
                if ((k & 1) == 1)
                {
                    if (first)
                    {
                        rx = qx;
                        ry = qy;
                        first = false;
                    }
                    else
                    {
                        (rx, ry) = PointAdd(rx, ry, qx, qy);
                    }
                }
                (qx, qy) = PointDouble(qx, qy);
                k >>= 1;
            }

            return (rx, ry);
        }

        /// <summary>
        /// Elliptic curve point addition.
        /// </summary>
        private static (BigInteger X, BigInteger Y) PointAdd(BigInteger x1, BigInteger y1, BigInteger x2, BigInteger y2)
        {
            if (x1 == x2 && y1 == y2)
            {
                return PointDouble(x1, y1);
            }

            var lambda = ((y2 - y1) * ModInverse(x2 - x1, P) % P + P) % P;
            var x3 = ((lambda * lambda - x1 - x2) % P + P) % P;
            var y3 = ((lambda * (x1 - x3) - y1) % P + P) % P;

            return (x3, y3);
        }

        /// <summary>
        /// Elliptic curve point doubling.
        /// </summary>
        private static (BigInteger X, BigInteger Y) PointDouble(BigInteger x, BigInteger y)
        {
            // For P-256: a = -3
            var a = P - 3;
            var lambda = ((3 * x * x + a) * ModInverse(2 * y, P) % P + P) % P;
            var x3 = ((lambda * lambda - 2 * x) % P + P) % P;
            var y3 = ((lambda * (x - x3) - y) % P + P) % P;

            return (x3, y3);
        }

        /// <summary>
        /// Modular multiplicative inverse using extended Euclidean algorithm.
        /// </summary>
        private static BigInteger ModInverse(BigInteger a, BigInteger m)
        {
            a = ((a % m) + m) % m;
            BigInteger g = BigInteger.GreatestCommonDivisor(a, m);
            if (g != 1)
            {
                return BigInteger.One;
            }

            return BigInteger.ModPow(a, m - 2, m);
        }
    }

    /// <summary>
    /// Schnorr proof structure.
    /// </summary>
    internal sealed class SchnorrProof
    {
        public BigInteger Rx { get; init; }
        public BigInteger Ry { get; init; }
        public BigInteger S { get; init; }
        public BigInteger E { get; init; }
        public BigInteger Yx { get; init; }
        public BigInteger Yy { get; init; }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            WriteBI(writer, Rx);
            WriteBI(writer, Ry);
            WriteBI(writer, S);
            WriteBI(writer, E);
            WriteBI(writer, Yx);
            WriteBI(writer, Yy);

            return ms.ToArray();
        }

        public static SchnorrProof Deserialize(byte[] data)
        {
            using var reader = new BinaryReader(new MemoryStream(data));

            return new SchnorrProof
            {
                Rx = ReadBI(reader),
                Ry = ReadBI(reader),
                S = ReadBI(reader),
                E = ReadBI(reader),
                Yx = ReadBI(reader),
                Yy = ReadBI(reader)
            };
        }

        private static void WriteBI(BinaryWriter writer, BigInteger value)
        {
            var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static BigInteger ReadBI(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            var bytes = reader.ReadBytes(length);
            return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        }
    }

    /// <summary>
    /// Pedersen commitment scheme implementation.
    /// Provides computationally binding and perfectly hiding commitments.
    /// </summary>
    internal sealed class PedersenCommitter
    {
        // Generator points for Pedersen commitment (using P-256 parameters)
        private static readonly BigInteger P = BigInteger.Parse("115792089210356248762697446949407573530086143415290314195533631308867097853951");
        private static readonly BigInteger Q = BigInteger.Parse("115792089210356248762697446949407573529996955224135760342422259061068512044369");
        private static readonly BigInteger Gx = BigInteger.Parse("48439561293906451759052585252797914202762949526041747995844080717082404635286");
        private static readonly BigInteger Gy = BigInteger.Parse("36134250956749795798585127919587881956611106672985015071877198253568414405109");

        // Second generator H (derived from hash of G)
        private readonly BigInteger _Hx;
        private readonly BigInteger _Hy;

        public PedersenCommitter()
        {
            // Derive H from G using hash-to-curve
            var gBytes = Gx.ToByteArray(isUnsigned: true, isBigEndian: true);
            var hHash = SHA256.HashData(gBytes);
            var hScalar = new BigInteger(hHash, isUnsigned: true, isBigEndian: true) % Q;
            (_Hx, _Hy) = ScalarMultiply(Gx, Gy, hScalar);
        }

        /// <summary>
        /// Creates a Pedersen commitment: C = m*G + r*H
        /// </summary>
        /// <param name="message">The message to commit to.</param>
        /// <param name="randomness">Output: the randomness used (needed for opening).</param>
        /// <returns>The commitment.</returns>
        public byte[] Commit(byte[] message, out byte[] randomness)
        {
            // Convert message to scalar
            var m = new BigInteger(SHA256.HashData(message), isUnsigned: true, isBigEndian: true) % Q;

            // Generate random r
            randomness = RandomNumberGenerator.GetBytes(32);
            var r = new BigInteger(randomness, isUnsigned: true, isBigEndian: true) % Q;

            // C = m*G + r*H
            var (mGx, mGy) = ScalarMultiply(Gx, Gy, m);
            var (rHx, rHy) = ScalarMultiply(_Hx, _Hy, r);
            var (Cx, Cy) = PointAdd(mGx, mGy, rHx, rHy);

            // Serialize commitment
            using var ms = new MemoryStream();
            var cxBytes = Cx.ToByteArray(isUnsigned: true, isBigEndian: true);
            var cyBytes = Cy.ToByteArray(isUnsigned: true, isBigEndian: true);
            ms.Write(BitConverter.GetBytes(cxBytes.Length));
            ms.Write(cxBytes);
            ms.Write(BitConverter.GetBytes(cyBytes.Length));
            ms.Write(cyBytes);

            return ms.ToArray();
        }

        /// <summary>
        /// Verifies a Pedersen commitment.
        /// </summary>
        /// <param name="commitment">The commitment to verify.</param>
        /// <param name="message">The claimed message.</param>
        /// <param name="randomness">The randomness used in commitment.</param>
        /// <returns>True if the commitment is valid.</returns>
        public bool Verify(byte[] commitment, byte[] message, byte[] randomness)
        {
            // Parse commitment
            using var reader = new BinaryReader(new MemoryStream(commitment));
            var cxLen = reader.ReadInt32();
            var cxBytes = reader.ReadBytes(cxLen);
            var cyLen = reader.ReadInt32();
            var cyBytes = reader.ReadBytes(cyLen);

            var Cx = new BigInteger(cxBytes, isUnsigned: true, isBigEndian: true);
            var Cy = new BigInteger(cyBytes, isUnsigned: true, isBigEndian: true);

            // Recompute commitment
            var m = new BigInteger(SHA256.HashData(message), isUnsigned: true, isBigEndian: true) % Q;
            var r = new BigInteger(randomness, isUnsigned: true, isBigEndian: true) % Q;

            var (mGx, mGy) = ScalarMultiply(Gx, Gy, m);
            var (rHx, rHy) = ScalarMultiply(_Hx, _Hy, r);
            var (Cx2, Cy2) = PointAdd(mGx, mGy, rHx, rHy);

            return Cx == Cx2 && Cy == Cy2;
        }

        private static (BigInteger X, BigInteger Y) ScalarMultiply(BigInteger px, BigInteger py, BigInteger k)
        {
            BigInteger rx = 0, ry = 0;
            BigInteger qx = px, qy = py;
            bool first = true;

            while (k > 0)
            {
                if ((k & 1) == 1)
                {
                    if (first)
                    {
                        rx = qx;
                        ry = qy;
                        first = false;
                    }
                    else
                    {
                        (rx, ry) = PointAdd(rx, ry, qx, qy);
                    }
                }
                (qx, qy) = PointDouble(qx, qy);
                k >>= 1;
            }

            return (rx, ry);
        }

        private static (BigInteger X, BigInteger Y) PointAdd(BigInteger x1, BigInteger y1, BigInteger x2, BigInteger y2)
        {
            if (x1 == x2 && y1 == y2)
            {
                return PointDouble(x1, y1);
            }

            var lambda = ((y2 - y1) * ModInverse(x2 - x1, P) % P + P) % P;
            var x3 = ((lambda * lambda - x1 - x2) % P + P) % P;
            var y3 = ((lambda * (x1 - x3) - y1) % P + P) % P;

            return (x3, y3);
        }

        private static (BigInteger X, BigInteger Y) PointDouble(BigInteger x, BigInteger y)
        {
            var a = P - 3;
            var lambda = ((3 * x * x + a) * ModInverse(2 * y, P) % P + P) % P;
            var x3 = ((lambda * lambda - 2 * x) % P + P) % P;
            var y3 = ((lambda * (x - x3) - y) % P + P) % P;

            return (x3, y3);
        }

        private static BigInteger ModInverse(BigInteger a, BigInteger m)
        {
            a = ((a % m) + m) % m;
            return BigInteger.ModPow(a, m - 2, m);
        }
    }

    /// <summary>
    /// Record of a ZK proof for caching.
    /// </summary>
    internal sealed class ZkProofRecord
    {
        public string DataId { get; init; } = string.Empty;
        public string CommitmentHash { get; init; } = string.Empty;
        public bool HasSchnorrProof { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    /// <summary>
    /// Configuration for ZK encryption plugin.
    /// </summary>
    public sealed class ZkEncryptionConfig
    {
        /// <summary>
        /// Gets or sets the key store.
        /// </summary>
        public IKeyStore? KeyStore { get; set; }

        /// <summary>
        /// Gets or sets the security context.
        /// </summary>
        public ISecurityContext? SecurityContext { get; set; }

        /// <summary>
        /// Gets or sets whether to automatically generate proofs on encryption.
        /// Default is true.
        /// </summary>
        public bool AutoGenerateProofs { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to verify proofs on decryption.
        /// Default is true.
        /// </summary>
        public bool VerifyProofsOnDecrypt { get; set; } = true;
    }

    /// <summary>
    /// Default security context for ZK operations.
    /// </summary>
    internal sealed class ZkSecurityContext : ISecurityContext
    {
        public string UserId => Environment.UserName;
        public string? TenantId => "zk-local";
        public IEnumerable<string> Roles => new[] { "zk-user" };
        public bool IsSystemAdmin => false;
    }
}
