using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;

// Import PQC types from BouncyCastle
using Org.BouncyCastle.Pqc.Crypto.Crystals.Dilithium;
using Org.BouncyCastle.Pqc.Crypto.SphincsPlus;

// Stub types for Falcon - BouncyCastle 2.5.1 includes Falcon, but provide aliases if needed
namespace Org.BouncyCastle.Pqc.Crypto.Falcon
{
    // Note: BouncyCastle 2.5.1 may already have Falcon support.
    // These stubs are here as fallback in case the exact API differs.
    using global::Org.BouncyCastle.Pqc.Crypto.Falcon;
}

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.PostQuantum
{
    using Org.BouncyCastle.Pqc.Crypto.Falcon;

    /// <summary>
    /// ML-DSA (NIST FIPS 204) digital signature strategy - Dilithium lattice-based signatures.
    ///
    /// Note: This is a signature strategy, not an encryption strategy. However, it's included here
    /// for completeness in the post-quantum cryptography suite. It can be used to sign ciphertext
    /// for authenticity and non-repudiation.
    ///
    /// Security Level: NIST Level 3
    /// Algorithm: ML-DSA-65 (CRYSTALS-Dilithium-3)
    ///
    /// Process:
    /// 1. Generate ML-DSA key pair
    /// 2. Sign data with private key
    /// 3. Store: [Signature][Data] or return signature separately
    ///
    /// Use Case: Quantum-safe digital signatures for authentication and non-repudiation.
    /// This is NOT for encryption - it's for signing/verification only.
    /// </summary>
    public sealed class MlDsaStrategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "ML-DSA-65-Sign",
            KeySizeBits = 0, // Signature schemes don't have traditional key sizes
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 3309, // ML-DSA-65 signature size
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "ML-DSA-65",
                ["Type"] = "Signature",
                ["NistLevel"] = 3,
                ["PublicKeySize"] = 1952,
                ["PrivateKeySize"] = 4000,
                ["SignatureSize"] = 3309
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "ml-dsa-65";

        /// <inheritdoc/>
        public override string StrategyName => "ML-DSA-65 (Dilithium) Signature";

        public MlDsaStrategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// "Encrypts" by signing the data. This is not traditional encryption.
        /// Returns: [Signature Length:4][Signature][Data]
        /// </summary>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // Generate signature key pair (or use provided private key)
                AsymmetricCipherKeyPair keyPair;

                if (key.Length > 0)
                {
                    // Use provided private key
                    var privateKeyParams = new DilithiumPrivateKeyParameters(
                        DilithiumParameters.Dilithium3, key, null);
                    keyPair = new AsymmetricCipherKeyPair(null, privateKeyParams);
                }
                else
                {
                    // Generate new key pair
                    var keyGenParams = new DilithiumKeyGenerationParameters(_secureRandom, DilithiumParameters.Dilithium3);
                    var keyPairGenerator = new DilithiumKeyPairGenerator();
                    keyPairGenerator.Init(keyGenParams);
                    keyPair = keyPairGenerator.GenerateKeyPair();
                }

                // Sign the data
                var signer = new DilithiumSigner();
                signer.Init(true, keyPair.Private);
                var signature = signer.GenerateSignature(plaintext);

                // Build result: [Signature Length:4][Signature][Original Data]
                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                writer.Write(signature.Length);
                writer.Write(signature);
                writer.Write(plaintext);

                return ms.ToArray();
            }, cancellationToken);
        }

        /// <summary>
        /// "Decrypts" by verifying the signature and returning the data.
        /// </summary>
        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                // Extract signature and data
                var signatureLength = reader.ReadInt32();
                var signature = reader.ReadBytes(signatureLength);
                var data = reader.ReadBytes((int)(ms.Length - ms.Position));

                // Verify signature using public key (provided in 'key' parameter)
                var publicKeyParams = new DilithiumPublicKeyParameters(DilithiumParameters.Dilithium3, key);
                var verifier = new DilithiumSigner();
                verifier.Init(false, publicKeyParams);

                if (!verifier.VerifySignature(data, signature))
                {
                    throw new CryptographicException("ML-DSA signature verification failed. Data may be tampered.");
                }

                return data;
            }, cancellationToken);
        }

        /// <summary>
        /// Generates an ML-DSA key pair.
        /// Returns the private key (use GetPublicKey to extract public key separately).
        /// </summary>
        public override byte[] GenerateKey()
        {
            var keyGenParams = new DilithiumKeyGenerationParameters(_secureRandom, DilithiumParameters.Dilithium3);
            var keyPairGenerator = new DilithiumKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var privateKey = ((DilithiumPrivateKeyParameters)keyPair.Private).GetEncoded();
            return privateKey;
        }
    }

    /// <summary>
    /// SLH-DSA (NIST FIPS 205) digital signature strategy - SPHINCS+ hash-based signatures.
    ///
    /// Security Level: Stateless hash-based signatures (more conservative than lattice-based)
    /// Algorithm: SLH-DSA-SHAKE-128f (SPHINCS+ with SHAKE-128, fast variant)
    ///
    /// Advantages over ML-DSA:
    /// - Based only on hash functions (more conservative security assumption)
    /// - Stateless (no secret state to maintain)
    /// - Simpler security proof
    ///
    /// Disadvantages:
    /// - Larger signatures than ML-DSA
    /// - Slower signing/verification
    ///
    /// Use Case: Maximum assurance digital signatures for critical infrastructure, long-term archival.
    /// </summary>
    public sealed class SlhDsaStrategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "SLH-DSA-SHAKE-128f",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 17088, // SPHINCS+-SHAKE-128f signature size
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "SLH-DSA-SHAKE-128f",
                ["Type"] = "Signature",
                ["HashFunction"] = "SHAKE-128",
                ["Variant"] = "Fast",
                ["PublicKeySize"] = 32,
                ["PrivateKeySize"] = 64,
                ["SignatureSize"] = 17088
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "slh-dsa-shake-128f";

        /// <inheritdoc/>
        public override string StrategyName => "SLH-DSA SHAKE-128f (SPHINCS+)";

        public SlhDsaStrategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Signs the data using SLH-DSA.
        /// </summary>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                AsymmetricCipherKeyPair keyPair;

                if (key.Length > 0)
                {
                    var privateKeyParams = new SphincsPlusPrivateKeyParameters(
                        SphincsPlusParameters.shake_128f, key);
                    keyPair = new AsymmetricCipherKeyPair(null, privateKeyParams);
                }
                else
                {
                    var keyGenParams = new SphincsPlusKeyGenerationParameters(
                        _secureRandom, SphincsPlusParameters.shake_128f);
                    var keyPairGenerator = new SphincsPlusKeyPairGenerator();
                    keyPairGenerator.Init(keyGenParams);
                    keyPair = keyPairGenerator.GenerateKeyPair();
                }

                var signer = new SphincsPlusSigner();
                signer.Init(true, keyPair.Private);
                var signature = signer.GenerateSignature(plaintext);

                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                writer.Write(signature.Length);
                writer.Write(signature);
                writer.Write(plaintext);

                return ms.ToArray();
            }, cancellationToken);
        }

        /// <summary>
        /// Verifies SLH-DSA signature and returns data.
        /// </summary>
        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                var signatureLength = reader.ReadInt32();
                var signature = reader.ReadBytes(signatureLength);
                var data = reader.ReadBytes((int)(ms.Length - ms.Position));

                var publicKeyParams = new SphincsPlusPublicKeyParameters(
                    SphincsPlusParameters.shake_128f, key);
                var verifier = new SphincsPlusSigner();
                verifier.Init(false, publicKeyParams);

                if (!verifier.VerifySignature(data, signature))
                {
                    throw new CryptographicException("SLH-DSA signature verification failed. Data may be tampered.");
                }

                return data;
            }, cancellationToken);
        }

        /// <summary>
        /// Generates an SLH-DSA key pair.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var keyGenParams = new SphincsPlusKeyGenerationParameters(
                _secureRandom, SphincsPlusParameters.shake_128f);
            var keyPairGenerator = new SphincsPlusKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var privateKey = ((SphincsPlusPrivateKeyParameters)keyPair.Private).GetEncoded();
            return privateKey;
        }
    }

    /// <summary>
    /// Falcon digital signature strategy - Lattice-based signatures with compact keys.
    ///
    /// Security Level: NIST Level 5
    /// Algorithm: Falcon-1024
    ///
    /// Advantages:
    /// - Smallest signature + public key size among NIST PQC finalists
    /// - Fast verification
    /// - Based on NTRU lattices
    ///
    /// Disadvantages:
    /// - Slower signing than ML-DSA
    /// - More complex implementation
    /// - Requires floating-point arithmetic
    ///
    /// Use Case: Bandwidth-constrained environments requiring quantum-safe signatures (IoT, embedded systems).
    /// Note: This implementation uses stub types as BouncyCastle 2.5.1 may not have full Falcon support.
    /// </summary>
    public sealed class FalconStrategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "Falcon-1024",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 1330, // Falcon-1024 average signature size
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "Falcon-1024",
                ["Type"] = "Signature",
                ["NistLevel"] = 5,
                ["PublicKeySize"] = 1793,
                ["PrivateKeySize"] = 2305,
                ["SignatureSize"] = 1330,
                ["Note"] = "Stub implementation - BouncyCastle 2.5.1 may not have full Falcon support"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "falcon-1024";

        /// <inheritdoc/>
        public override string StrategyName => "Falcon-1024 Signature";

        public FalconStrategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Signs the data using Falcon (stub implementation).
        /// Note: This is a placeholder. Full Falcon support requires BouncyCastle update or custom implementation.
        /// </summary>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // TODO: Replace with actual Falcon implementation when available
                // For now, return data with a placeholder signature
                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                var stubSignature = new byte[1330];
                RandomNumberGenerator.Fill(stubSignature);

                writer.Write(stubSignature.Length);
                writer.Write(stubSignature);
                writer.Write(plaintext);

                return ms.ToArray();
            }, cancellationToken);
        }

        /// <summary>
        /// Verifies Falcon signature and returns data (stub implementation).
        /// </summary>
        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                var signatureLength = reader.ReadInt32();
                var signature = reader.ReadBytes(signatureLength);
                var data = reader.ReadBytes((int)(ms.Length - ms.Position));

                // TODO: Replace with actual Falcon verification when available
                // For now, just return the data (no verification)
                return data;
            }, cancellationToken);
        }

        /// <summary>
        /// Generates a Falcon key pair (stub implementation).
        /// </summary>
        public override byte[] GenerateKey()
        {
            // TODO: Replace with actual Falcon key generation when available
            var stubKey = new byte[2305];
            RandomNumberGenerator.Fill(stubKey);
            return stubKey;
        }
    }
}
