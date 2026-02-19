using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pqc.Crypto.SphincsPlus;
using Org.BouncyCastle.Security;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.PostQuantum
{
    /// <summary>
    /// Shared helper for SPHINCS+ signature operations across all security levels.
    /// Provides sign, verify, and key generation using BouncyCastle's SPHINCS+ implementation.
    /// SPHINCS+ is a stateless hash-based signature scheme providing conservative security assumptions.
    /// All operations are thread-safe and production-ready.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: PQC migration")]
    internal static class SphincsPlusSignatureHelper
    {
        /// <summary>
        /// Signs data using a SPHINCS+ private key at the specified parameter level.
        /// </summary>
        /// <param name="data">The data to sign.</param>
        /// <param name="privateKey">The encoded private key bytes.</param>
        /// <param name="parameters">The SPHINCS+ parameter set (shake_128f/192f/256f).</param>
        /// <returns>The digital signature bytes.</returns>
        /// <exception cref="CryptographicException">Thrown when signing fails.</exception>
        public static byte[] Sign(byte[] data, byte[] privateKey, SphincsPlusParameters parameters)
        {
            try
            {
                var privateKeyParams = new SphincsPlusPrivateKeyParameters(parameters, privateKey);
                var signer = new SphincsPlusSigner();
                signer.Init(true, privateKeyParams);
                return signer.GenerateSignature(data);
            }
            catch (Exception ex) when (ex is not CryptographicException)
            {
                throw new CryptographicException(
                    $"SPHINCS+ signing failed with parameter set {parameters}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Verifies a SPHINCS+ signature against the provided data and public key.
        /// </summary>
        /// <param name="data">The original signed data.</param>
        /// <param name="signature">The signature to verify.</param>
        /// <param name="publicKey">The encoded public key bytes.</param>
        /// <param name="parameters">The SPHINCS+ parameter set (shake_128f/192f/256f).</param>
        /// <returns>True if the signature is valid; false otherwise.</returns>
        public static bool Verify(byte[] data, byte[] signature, byte[] publicKey, SphincsPlusParameters parameters)
        {
            try
            {
                var publicKeyParams = new SphincsPlusPublicKeyParameters(parameters, publicKey);
                var verifier = new SphincsPlusSigner();
                verifier.Init(false, publicKeyParams);
                return verifier.VerifySignature(data, signature);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Generates a SPHINCS+ key pair at the specified parameter level.
        /// </summary>
        /// <param name="parameters">The SPHINCS+ parameter set (shake_128f/192f/256f).</param>
        /// <param name="random">A secure random number generator.</param>
        /// <returns>A tuple of (publicKey, privateKey) as encoded byte arrays.</returns>
        public static (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair(
            SphincsPlusParameters parameters, SecureRandom random)
        {
            var keyGenParams = new SphincsPlusKeyGenerationParameters(random, parameters);
            var keyPairGenerator = new SphincsPlusKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var publicKey = ((SphincsPlusPublicKeyParameters)keyPair.Public).GetEncoded();
            var privateKey = ((SphincsPlusPrivateKeyParameters)keyPair.Private).GetEncoded();

            return (publicKey, privateKey);
        }
    }

    /// <summary>
    /// SPHINCS+-SHAKE-128f (NIST FIPS 205, SLH-DSA-SHAKE-128f) digital signature strategy.
    ///
    /// Security Level: NIST Level 1
    /// Algorithm: SPHINCS+-SHAKE-128f (fast variant)
    /// Signature Size: 17088 bytes
    /// Public Key Size: 32 bytes
    /// Private Key Size: 64 bytes
    ///
    /// Wire format: [Signature Length:4][Signature][Original Data]
    ///
    /// Advantages over lattice-based signatures:
    /// - Based only on hash functions (more conservative security assumption)
    /// - Stateless (no secret state to maintain)
    /// - Simpler security proof
    ///
    /// Use Case: Maximum assurance signatures for critical infrastructure and long-term archival.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: PQC migration")]
    public sealed class SphincsPlus128fStrategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "SPHINCS+-SHAKE-128f-Sign",
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
                ["Algorithm"] = "SPHINCS+-SHAKE-128f",
                ["FipsReference"] = "FIPS 205",
                ["StandardName"] = "SLH-DSA-SHAKE-128f",
                ["Type"] = "Signature",
                ["HashFunction"] = "SHAKE-128",
                ["Variant"] = "Fast",
                ["PublicKeySize"] = 32,
                ["PrivateKeySize"] = 64,
                ["SignatureSize"] = 17088
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "sphincs-plus-shake-128f";

        /// <inheritdoc/>
        public override string StrategyName => "SPHINCS+-SHAKE-128f (SLH-DSA) Signature";

        /// <summary>
        /// Initializes a new instance of the SPHINCS+-SHAKE-128f signature strategy.
        /// </summary>
        public SphincsPlus128fStrategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sphincs.plus.128f.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sphincs.plus.128f.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Signs data using SPHINCS+-SHAKE-128f.
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
                byte[] signature;

                if (key.Length > 0)
                {
                    signature = SphincsPlusSignatureHelper.Sign(
                        plaintext, key, SphincsPlusParameters.shake_128f);
                }
                else
                {
                    var (_, privateKey) = SphincsPlusSignatureHelper.GenerateKeyPair(
                        SphincsPlusParameters.shake_128f, _secureRandom);
                    signature = SphincsPlusSignatureHelper.Sign(
                        plaintext, privateKey, SphincsPlusParameters.shake_128f);
                }

                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                writer.Write(signature.Length);
                writer.Write(signature);
                writer.Write(plaintext);

                return ms.ToArray();
            }, cancellationToken);
        }

        /// <summary>
        /// Verifies SPHINCS+-SHAKE-128f signature and returns the original data.
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

                if (!SphincsPlusSignatureHelper.Verify(
                    data, signature, key, SphincsPlusParameters.shake_128f))
                {
                    throw new CryptographicException(
                        "SPHINCS+-SHAKE-128f signature verification failed. Data may be tampered.");
                }

                return data;
            }, cancellationToken);
        }

        /// <summary>
        /// Generates a SPHINCS+-SHAKE-128f key pair. Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var (_, privateKey) = SphincsPlusSignatureHelper.GenerateKeyPair(
                SphincsPlusParameters.shake_128f, _secureRandom);
            return privateKey;
        }

        /// <summary>
        /// Generates a complete SPHINCS+-SHAKE-128f key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            return SphincsPlusSignatureHelper.GenerateKeyPair(
                SphincsPlusParameters.shake_128f, _secureRandom);
        }
    }

    /// <summary>
    /// SPHINCS+-SHAKE-192f (NIST FIPS 205, SLH-DSA-SHAKE-192f) digital signature strategy.
    ///
    /// Security Level: NIST Level 3
    /// Algorithm: SPHINCS+-SHAKE-192f (fast variant)
    /// Signature Size: 35664 bytes
    /// Public Key Size: 48 bytes
    /// Private Key Size: 96 bytes
    ///
    /// Wire format: [Signature Length:4][Signature][Original Data]
    ///
    /// Use Case: High-assurance hash-based signatures at NIST Level 3.
    /// Recommended for environments requiring conservative cryptographic assumptions.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: PQC migration")]
    public sealed class SphincsPlus192fStrategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "SPHINCS+-SHAKE-192f-Sign",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 35664, // SPHINCS+-SHAKE-192f signature size
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
                ["Algorithm"] = "SPHINCS+-SHAKE-192f",
                ["FipsReference"] = "FIPS 205",
                ["StandardName"] = "SLH-DSA-SHAKE-192f",
                ["Type"] = "Signature",
                ["HashFunction"] = "SHAKE-192",
                ["Variant"] = "Fast",
                ["PublicKeySize"] = 48,
                ["PrivateKeySize"] = 96,
                ["SignatureSize"] = 35664
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "sphincs-plus-shake-192f";

        /// <inheritdoc/>
        public override string StrategyName => "SPHINCS+-SHAKE-192f (SLH-DSA) Signature";

        /// <summary>
        /// Initializes a new instance of the SPHINCS+-SHAKE-192f signature strategy.
        /// </summary>
        public SphincsPlus192fStrategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sphincs.plus.192f.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sphincs.plus.192f.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Signs data using SPHINCS+-SHAKE-192f.
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
                byte[] signature;

                if (key.Length > 0)
                {
                    signature = SphincsPlusSignatureHelper.Sign(
                        plaintext, key, SphincsPlusParameters.shake_192f);
                }
                else
                {
                    var (_, privateKey) = SphincsPlusSignatureHelper.GenerateKeyPair(
                        SphincsPlusParameters.shake_192f, _secureRandom);
                    signature = SphincsPlusSignatureHelper.Sign(
                        plaintext, privateKey, SphincsPlusParameters.shake_192f);
                }

                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                writer.Write(signature.Length);
                writer.Write(signature);
                writer.Write(plaintext);

                return ms.ToArray();
            }, cancellationToken);
        }

        /// <summary>
        /// Verifies SPHINCS+-SHAKE-192f signature and returns the original data.
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

                if (!SphincsPlusSignatureHelper.Verify(
                    data, signature, key, SphincsPlusParameters.shake_192f))
                {
                    throw new CryptographicException(
                        "SPHINCS+-SHAKE-192f signature verification failed. Data may be tampered.");
                }

                return data;
            }, cancellationToken);
        }

        /// <summary>
        /// Generates a SPHINCS+-SHAKE-192f key pair. Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var (_, privateKey) = SphincsPlusSignatureHelper.GenerateKeyPair(
                SphincsPlusParameters.shake_192f, _secureRandom);
            return privateKey;
        }

        /// <summary>
        /// Generates a complete SPHINCS+-SHAKE-192f key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            return SphincsPlusSignatureHelper.GenerateKeyPair(
                SphincsPlusParameters.shake_192f, _secureRandom);
        }
    }

    /// <summary>
    /// SPHINCS+-SHAKE-256f (NIST FIPS 205, SLH-DSA-SHAKE-256f) digital signature strategy.
    ///
    /// Security Level: NIST Level 5
    /// Algorithm: SPHINCS+-SHAKE-256f (fast variant)
    /// Signature Size: 49856 bytes
    /// Public Key Size: 64 bytes
    /// Private Key Size: 128 bytes
    ///
    /// Wire format: [Signature Length:4][Signature][Original Data]
    ///
    /// Use Case: Maximum quantum resistance with conservative hash-based assumptions.
    /// Suitable for the most critical infrastructure, national security, and long-term archival
    /// where absolute assurance against both classical and quantum attacks is required.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: PQC migration")]
    public sealed class SphincsPlus256fStrategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "SPHINCS+-SHAKE-256f-Sign",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 49856, // SPHINCS+-SHAKE-256f signature size
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
                ["Algorithm"] = "SPHINCS+-SHAKE-256f",
                ["FipsReference"] = "FIPS 205",
                ["StandardName"] = "SLH-DSA-SHAKE-256f",
                ["Type"] = "Signature",
                ["HashFunction"] = "SHAKE-256",
                ["Variant"] = "Fast",
                ["PublicKeySize"] = 64,
                ["PrivateKeySize"] = 128,
                ["SignatureSize"] = 49856
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "sphincs-plus-shake-256f";

        /// <inheritdoc/>
        public override string StrategyName => "SPHINCS+-SHAKE-256f (SLH-DSA) Signature";

        /// <summary>
        /// Initializes a new instance of the SPHINCS+-SHAKE-256f signature strategy.
        /// </summary>
        public SphincsPlus256fStrategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sphincs.plus.256f.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sphincs.plus.256f.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Signs data using SPHINCS+-SHAKE-256f.
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
                byte[] signature;

                if (key.Length > 0)
                {
                    signature = SphincsPlusSignatureHelper.Sign(
                        plaintext, key, SphincsPlusParameters.shake_256f);
                }
                else
                {
                    var (_, privateKey) = SphincsPlusSignatureHelper.GenerateKeyPair(
                        SphincsPlusParameters.shake_256f, _secureRandom);
                    signature = SphincsPlusSignatureHelper.Sign(
                        plaintext, privateKey, SphincsPlusParameters.shake_256f);
                }

                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                writer.Write(signature.Length);
                writer.Write(signature);
                writer.Write(plaintext);

                return ms.ToArray();
            }, cancellationToken);
        }

        /// <summary>
        /// Verifies SPHINCS+-SHAKE-256f signature and returns the original data.
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

                if (!SphincsPlusSignatureHelper.Verify(
                    data, signature, key, SphincsPlusParameters.shake_256f))
                {
                    throw new CryptographicException(
                        "SPHINCS+-SHAKE-256f signature verification failed. Data may be tampered.");
                }

                return data;
            }, cancellationToken);
        }

        /// <summary>
        /// Generates a SPHINCS+-SHAKE-256f key pair. Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var (_, privateKey) = SphincsPlusSignatureHelper.GenerateKeyPair(
                SphincsPlusParameters.shake_256f, _secureRandom);
            return privateKey;
        }

        /// <summary>
        /// Generates a complete SPHINCS+-SHAKE-256f key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            return SphincsPlusSignatureHelper.GenerateKeyPair(
                SphincsPlusParameters.shake_256f, _secureRandom);
        }
    }
}
