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
    /// Extends EncryptionPluginBase for composable key management with Direct and Envelope modes.
    ///
    /// Security Features:
    /// - Client-side AES-256-GCM encryption (server never sees plaintext)
    /// - Schnorr identification protocol for proof of knowledge
    /// - Pedersen commitments for binding and hiding properties
    /// - Discrete logarithm based proofs over prime order groups
    /// - Non-interactive ZK proofs using Fiat-Shamir heuristic
    /// - Composable key management: Direct mode (IKeyStore) or Envelope mode (IEnvelopeKeyStore)
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
    public sealed class ZeroKnowledgeEncryptionPlugin : EncryptionPluginBase
    {
        private readonly ZkEncryptionConfig _config;
        private readonly ConcurrentDictionary<string, ZkProofRecord> _proofCache = new();
        private readonly SchnorrProver _schnorrProver;
        private readonly PedersenCommitter _pedersenCommitter;
        private long _proofsGenerated;
        private long _proofsVerified;

        /// <summary>
        /// Maximum key ID length in header (for legacy format).
        /// </summary>
        private const int MaxKeyIdLength = 64;

        /// <summary>
        /// Header version for ZK-encrypted data (for legacy format).
        /// </summary>
        private const byte LegacyHeaderVersion = 0x5A; // 'Z' for ZK

        #region Abstract Property Overrides

        /// <inheritdoc/>
        protected override int KeySizeBytes => 32; // 256 bits

        /// <inheritdoc/>
        protected override int IvSizeBytes => 12; // 96 bits for GCM

        /// <inheritdoc/>
        protected override int TagSizeBytes => 16; // 128-bit tag

        /// <inheritdoc/>
        protected override string AlgorithmId => "ZK-AES-256-GCM";

        #endregion

        #region Plugin Identity

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

        #endregion

        /// <summary>
        /// Initializes a new instance of the zero-knowledge encryption plugin.
        /// </summary>
        /// <param name="config">Optional configuration. If null, defaults are used.</param>
        public ZeroKnowledgeEncryptionPlugin(ZkEncryptionConfig? config = null)
        {
            _config = config ?? new ZkEncryptionConfig();
            _schnorrProver = new SchnorrProver();
            _pedersenCommitter = new PedersenCommitter();

            // Set defaults from config (backward compatibility)
            if (_config.KeyStore != null)
            {
                DefaultKeyStore = _config.KeyStore;
            }
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
            metadata["Algorithm"] = AlgorithmId;
            metadata["ZkProtocol"] = "Schnorr";
            metadata["CommitmentScheme"] = "Pedersen";
            metadata["KeySize"] = KeySizeBytes * 8;
            metadata["IVSize"] = IvSizeBytes * 8;
            metadata["TagSize"] = TagSizeBytes * 8;
            metadata["CurveType"] = "P-256";
            metadata["SupportsClientSideEncryption"] = true;
            metadata["SupportsZkProofs"] = true;
            metadata["ServerSeesPlaintext"] = false;
            metadata["SupportsKeyRotation"] = true;
            metadata["SupportedModes"] = new[] { "Direct", "Envelope" };
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
                "zk.encryption.configure" => HandleConfigureAsync(message),
                _ => base.OnMessageAsync(message)
            };
        }

        #region Core Encryption/Decryption (Algorithm-Specific)

        /// <summary>
        /// Performs ZK-AES-256-GCM encryption with Schnorr proof generation and Pedersen commitment.
        /// Base class handles key resolution, config resolution, and statistics.
        /// </summary>
        /// <param name="input">The plaintext input stream.</param>
        /// <param name="key">The encryption key (provided by base class).</param>
        /// <param name="iv">The initialization vector (provided by base class).</param>
        /// <param name="context">The kernel context for logging.</param>
        /// <returns>A stream containing ZK proof data + [IV:12][Tag:16][Ciphertext].</returns>
        /// <exception cref="CryptographicException">Thrown on encryption failure.</exception>
        protected override async Task<Stream> EncryptCoreAsync(Stream input, byte[] key, byte[] iv, IKernelContext context)
        {
            byte[]? plaintext = null;
            byte[]? ciphertext = null;

            try
            {
                // Read all input data
                using var inputMs = new MemoryStream();
                await input.CopyToAsync(inputMs);
                plaintext = inputMs.ToArray();

                var tag = new byte[TagSizeBytes];
                ciphertext = new byte[plaintext.Length];

                // Perform AES-256-GCM encryption
                using var aesGcm = new AesGcm(key, TagSizeBytes);
                aesGcm.Encrypt(iv, plaintext, ciphertext, tag);

                // Generate Pedersen commitment to plaintext hash
                var plaintextHash = SHA256.HashData(plaintext);
                var commitment = _pedersenCommitter.Commit(plaintextHash, out var commitmentRandomness);

                // Generate Schnorr proof of knowledge of the key
                var generateProof = _config.AutoGenerateProofs;
                SchnorrProof? schnorrProof = null;
                if (generateProof)
                {
                    schnorrProof = _schnorrProver.Prove(key, plaintextHash);
                    Interlocked.Increment(ref _proofsGenerated);
                }

                // Build output: Commitment + Proof + [IV:12][Tag:16][Ciphertext]
                // Note: Key info is now stored in EncryptionMetadata by base class
                using var output = new MemoryStream();
                using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

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

                // Write IV (12 bytes)
                writer.Write(iv);

                // Write authentication tag (16 bytes)
                writer.Write(tag);

                // Write ciphertext
                writer.Write(ciphertext.Length);
                writer.Write(ciphertext);

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
                // Security: Clear sensitive data from memory
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
            }
        }

        /// <summary>
        /// Performs ZK-AES-256-GCM decryption with Schnorr proof and Pedersen commitment verification.
        /// Base class handles key resolution, config resolution, and statistics.
        /// Supports both new format with commitment/proof and legacy format.
        /// </summary>
        /// <param name="input">The encrypted input stream.</param>
        /// <param name="key">The decryption key (provided by base class).</param>
        /// <param name="iv">The initialization vector (null if embedded in ciphertext).</param>
        /// <param name="context">The kernel context for logging.</param>
        /// <returns>The decrypted stream and authentication tag.</returns>
        /// <exception cref="CryptographicException">
        /// Thrown on decryption failure, commitment verification failure, or proof verification failure.
        /// </exception>
        protected override async Task<(Stream data, byte[]? tag)> DecryptCoreAsync(Stream input, byte[] key, byte[]? iv, IKernelContext context)
        {
            byte[]? encryptedData = null;
            byte[]? plaintext = null;

            try
            {
                // Read all encrypted data
                using var inputMs = new MemoryStream();
                await input.CopyToAsync(inputMs);
                encryptedData = inputMs.ToArray();

                // Check if this is legacy format (has key ID header)
                var isLegacyFormat = IsLegacyFormat(encryptedData);

                using var reader = new BinaryReader(new MemoryStream(encryptedData), Encoding.UTF8);

                if (isLegacyFormat)
                {
                    // Legacy format: [HeaderVersion:1][KeyIdLen:1][KeyId:variable][IV:12][Tag:16][Commitment][Proof][Ciphertext]
                    var version = reader.ReadByte();
                    if (version != LegacyHeaderVersion)
                    {
                        throw new CryptographicException($"Invalid ZK encryption header version: 0x{version:X2}");
                    }

                    var keyIdLength = reader.ReadByte();
                    var keyIdBytes = reader.ReadBytes(keyIdLength);
                    // keyId already resolved by base class, skip it

                    // Read IV from legacy format
                    iv = reader.ReadBytes(IvSizeBytes);
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
                    var verifyProof = _config.VerifyProofsOnDecrypt;
                    if (verifyProof && schnorrProof != null)
                    {
                        if (!_schnorrProver.Verify(schnorrProof, plaintextHash))
                        {
                            throw new CryptographicException("Schnorr proof verification failed. Proof of knowledge invalid.");
                        }
                        Interlocked.Increment(ref _proofsVerified);
                    }

                    context.LogDebug($"ZK decrypted {ciphertextLength} bytes (legacy format) with commitment verification");

                    var result = new byte[plaintext.Length];
                    Array.Copy(plaintext, result, plaintext.Length);
                    return (new MemoryStream(result), tag);
                }
                else
                {
                    // New format: [Commitment][Proof][IV:12][Tag:16][Ciphertext]
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

                    // If IV not provided by base class, read from data
                    if (iv == null)
                    {
                        iv = reader.ReadBytes(IvSizeBytes);
                    }
                    else
                    {
                        // IV provided by base class (from metadata), skip in data
                        reader.ReadBytes(IvSizeBytes);
                    }

                    // Read authentication tag
                    var tag = reader.ReadBytes(TagSizeBytes);

                    // Read ciphertext
                    var ciphertextLength = reader.ReadInt32();
                    var ciphertext = reader.ReadBytes(ciphertextLength);

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
                    var verifyProof = _config.VerifyProofsOnDecrypt;
                    if (verifyProof && schnorrProof != null)
                    {
                        if (!_schnorrProver.Verify(schnorrProof, plaintextHash))
                        {
                            throw new CryptographicException("Schnorr proof verification failed. Proof of knowledge invalid.");
                        }
                        Interlocked.Increment(ref _proofsVerified);
                    }

                    context.LogDebug($"ZK decrypted {ciphertextLength} bytes with commitment verification");

                    var result = new byte[plaintext.Length];
                    Array.Copy(plaintext, result, plaintext.Length);
                    return (new MemoryStream(result), tag);
                }
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new CryptographicException("Authentication tag verification failed. Data may be corrupted or tampered with.", ex);
            }
            finally
            {
                // Security: Clear sensitive data from memory
                if (encryptedData != null) CryptographicOperations.ZeroMemory(encryptedData);
                if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        /// <summary>
        /// Checks if the encrypted data uses the legacy format with key ID header.
        /// Legacy format: [HeaderVersion:1][KeyIdLen:1][KeyId:variable]...
        /// </summary>
        private bool IsLegacyFormat(byte[] data)
        {
            if (data.Length < 2)
                return false;

            // Check for legacy header version (0x5A = 'Z' for ZK)
            var version = data[0];
            if (version != LegacyHeaderVersion)
                return false;

            // Check for valid key ID length
            var keyIdLength = data[1];
            return keyIdLength > 0 && keyIdLength <= MaxKeyIdLength;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets key store for ZK-specific message operations.
        /// Falls back to DefaultKeyStore (from base class).
        /// </summary>
        private IKeyStore GetKeyStoreForMessage(Dictionary<string, object> payload)
        {
            if (payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                return ks;
            }

            if (DefaultKeyStore != null)
            {
                return DefaultKeyStore;
            }

            throw new InvalidOperationException("No IKeyStore available for ZK encryption");
        }

        /// <summary>
        /// Gets security context from message payload.
        /// </summary>
        private ISecurityContext GetSecurityContext(Dictionary<string, object> payload)
        {
            if (payload.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
            {
                return sc;
            }

            return new ZkSecurityContext();
        }

        #endregion

        #region Message Handlers

        private async Task HandleProveAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
            {
                throw new ArgumentException("Missing 'data' parameter");
            }

            var hash = SHA256.HashData(data);
            var keyStore = GetKeyStoreForMessage(message.Payload);
            var securityContext = GetSecurityContext(message.Payload);

            var keyId = await keyStore.GetCurrentKeyIdAsync();
            var key = await keyStore.GetKeyAsync(keyId, securityContext);

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
            // Use base class statistics
            var stats = GetStatistics();

            message.Payload["EncryptionCount"] = stats.EncryptionCount;
            message.Payload["DecryptionCount"] = stats.DecryptionCount;
            message.Payload["TotalBytesEncrypted"] = stats.TotalBytesEncrypted;
            message.Payload["TotalBytesDecrypted"] = stats.TotalBytesDecrypted;
            message.Payload["UniqueKeysUsed"] = stats.UniqueKeysUsed;
            message.Payload["Algorithm"] = AlgorithmId;
            message.Payload["ProofsGenerated"] = Interlocked.Read(ref _proofsGenerated);
            message.Payload["ProofsVerified"] = Interlocked.Read(ref _proofsVerified);
            message.Payload["CachedProofs"] = _proofCache.Count;

            return Task.CompletedTask;
        }

        private Task HandleConfigureAsync(PluginMessage message)
        {
            // Use base class configuration methods
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                SetDefaultKeyStore(ks);
            }

            if (message.Payload.TryGetValue("envelopeKeyStore", out var eksObj) && eksObj is IEnvelopeKeyStore eks &&
                message.Payload.TryGetValue("kekKeyId", out var kekObj) && kekObj is string kek)
            {
                SetDefaultEnvelopeKeyStore(eks, kek);
            }

            if (message.Payload.TryGetValue("mode", out var modeObj))
            {
                if (modeObj is KeyManagementMode mode)
                {
                    SetDefaultMode(mode);
                }
                else if (modeObj is string modeStr && Enum.TryParse<KeyManagementMode>(modeStr, true, out var parsedMode))
                {
                    SetDefaultMode(parsedMode);
                }
            }

            return Task.CompletedTask;
        }

        private Task HandleSetKeyStoreAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            {
                SetDefaultKeyStore(ks);
            }
            return Task.CompletedTask;
        }

        #endregion
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
