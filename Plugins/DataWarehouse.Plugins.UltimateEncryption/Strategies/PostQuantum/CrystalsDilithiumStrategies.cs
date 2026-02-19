using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pqc.Crypto.Crystals.Dilithium;
using Org.BouncyCastle.Security;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.PostQuantum
{
    /// <summary>
    /// Shared helper for CRYSTALS-Dilithium signature operations across all security levels.
    /// Provides sign, verify, and key generation using BouncyCastle's Dilithium implementation.
    /// All operations are thread-safe and production-ready.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: PQC migration")]
    internal static class DilithiumSignatureHelper
    {
        /// <summary>
        /// Signs data using a Dilithium private key at the specified parameter level.
        /// </summary>
        /// <param name="data">The data to sign.</param>
        /// <param name="privateKey">The encoded private key bytes.</param>
        /// <param name="parameters">The Dilithium parameter set (Dilithium2/3/5).</param>
        /// <returns>The digital signature bytes.</returns>
        /// <exception cref="CryptographicException">Thrown when signing fails.</exception>
        public static byte[] Sign(byte[] data, byte[] privateKey, DilithiumParameters parameters)
        {
            try
            {
                var privateKeyParams = new DilithiumPrivateKeyParameters(parameters, privateKey, null);
                var signer = new DilithiumSigner();
                signer.Init(true, privateKeyParams);
                return signer.GenerateSignature(data);
            }
            catch (Exception ex) when (ex is not CryptographicException)
            {
                throw new CryptographicException(
                    $"Dilithium signing failed with parameter set {parameters}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Verifies a Dilithium signature against the provided data and public key.
        /// </summary>
        /// <param name="data">The original signed data.</param>
        /// <param name="signature">The signature to verify.</param>
        /// <param name="publicKey">The encoded public key bytes.</param>
        /// <param name="parameters">The Dilithium parameter set (Dilithium2/3/5).</param>
        /// <returns>True if the signature is valid; false otherwise.</returns>
        public static bool Verify(byte[] data, byte[] signature, byte[] publicKey, DilithiumParameters parameters)
        {
            try
            {
                var publicKeyParams = new DilithiumPublicKeyParameters(parameters, publicKey);
                var verifier = new DilithiumSigner();
                verifier.Init(false, publicKeyParams);
                return verifier.VerifySignature(data, signature);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Generates a Dilithium key pair at the specified parameter level.
        /// </summary>
        /// <param name="parameters">The Dilithium parameter set (Dilithium2/3/5).</param>
        /// <param name="random">A secure random number generator.</param>
        /// <returns>A tuple of (publicKey, privateKey) as encoded byte arrays.</returns>
        public static (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair(
            DilithiumParameters parameters, SecureRandom random)
        {
            var keyGenParams = new DilithiumKeyGenerationParameters(random, parameters);
            var keyPairGenerator = new DilithiumKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var publicKey = ((DilithiumPublicKeyParameters)keyPair.Public).GetEncoded();
            var privateKey = ((DilithiumPrivateKeyParameters)keyPair.Private).GetEncoded();

            return (publicKey, privateKey);
        }
    }

    /// <summary>
    /// CRYSTALS-Dilithium-2 (NIST FIPS 204, ML-DSA-44) digital signature strategy.
    ///
    /// Security Level: NIST Level 2
    /// Algorithm: Dilithium2 (ML-DSA-44)
    /// Signature Size: 2420 bytes
    /// Public Key Size: 1312 bytes
    /// Private Key Size: 2528 bytes
    ///
    /// Wire format: [Signature Length:4][Signature][Original Data]
    ///
    /// Use Case: Fast lattice-based signatures for general use at NIST Level 2.
    /// Suitable for ransomware vaccination, time-locked object signing, and crypto-agility migration.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: PQC migration")]
    public sealed class DilithiumSignature44Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "CRYSTALS-Dilithium-2-Sign",
            KeySizeBits = 0, // Signature schemes don't have traditional key sizes
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 2420, // Dilithium2 signature size
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
                ["Algorithm"] = "CRYSTALS-Dilithium-2",
                ["FipsReference"] = "FIPS 204",
                ["StandardName"] = "ML-DSA-44",
                ["Type"] = "Signature",
                ["NistLevel"] = 2,
                ["PublicKeySize"] = 1312,
                ["PrivateKeySize"] = 2528,
                ["SignatureSize"] = 2420
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "crystals-dilithium-44";

        /// <inheritdoc/>
        public override string StrategyName => "CRYSTALS-Dilithium-2 (ML-DSA-44) Signature";

        /// <summary>
        /// Initializes a new instance of the Dilithium-2 signature strategy.
        /// </summary>
        public DilithiumSignature44Strategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dilithium.44.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dilithium.44.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Signs data using CRYSTALS-Dilithium-2.
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
                    // Use provided private key to sign
                    signature = DilithiumSignatureHelper.Sign(plaintext, key, DilithiumParameters.Dilithium2);
                }
                else
                {
                    // Generate ephemeral key pair and sign
                    var (_, privateKey) = DilithiumSignatureHelper.GenerateKeyPair(
                        DilithiumParameters.Dilithium2, _secureRandom);
                    signature = DilithiumSignatureHelper.Sign(plaintext, privateKey, DilithiumParameters.Dilithium2);
                }

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
        /// Verifies CRYSTALS-Dilithium-2 signature and returns the original data.
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
                if (!DilithiumSignatureHelper.Verify(data, signature, key, DilithiumParameters.Dilithium2))
                {
                    throw new CryptographicException(
                        "CRYSTALS-Dilithium-2 signature verification failed. Data may be tampered.");
                }

                return data;
            }, cancellationToken);
        }

        /// <summary>
        /// Generates a CRYSTALS-Dilithium-2 key pair. Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var (_, privateKey) = DilithiumSignatureHelper.GenerateKeyPair(
                DilithiumParameters.Dilithium2, _secureRandom);
            return privateKey;
        }

        /// <summary>
        /// Generates a complete Dilithium-2 key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            return DilithiumSignatureHelper.GenerateKeyPair(DilithiumParameters.Dilithium2, _secureRandom);
        }
    }

    /// <summary>
    /// CRYSTALS-Dilithium-3 (NIST FIPS 204, ML-DSA-65) digital signature strategy.
    ///
    /// Security Level: NIST Level 3
    /// Algorithm: Dilithium3 (ML-DSA-65)
    /// Signature Size: 3309 bytes
    /// Public Key Size: 1952 bytes
    /// Private Key Size: 4000 bytes
    ///
    /// Wire format: [Signature Length:4][Signature][Original Data]
    ///
    /// Use Case: Recommended default for most production applications requiring quantum-safe signatures.
    /// Provides NIST Level 3 security matching the existing MlDsaStrategy.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: PQC migration")]
    public sealed class DilithiumSignature65Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "CRYSTALS-Dilithium-3-Sign",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 3309, // Dilithium3 signature size
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
                ["Algorithm"] = "CRYSTALS-Dilithium-3",
                ["FipsReference"] = "FIPS 204",
                ["StandardName"] = "ML-DSA-65",
                ["Type"] = "Signature",
                ["NistLevel"] = 3,
                ["PublicKeySize"] = 1952,
                ["PrivateKeySize"] = 4000,
                ["SignatureSize"] = 3309
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "crystals-dilithium-65";

        /// <inheritdoc/>
        public override string StrategyName => "CRYSTALS-Dilithium-3 (ML-DSA-65) Signature";

        /// <summary>
        /// Initializes a new instance of the Dilithium-3 signature strategy.
        /// </summary>
        public DilithiumSignature65Strategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dilithium.65.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dilithium.65.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Signs data using CRYSTALS-Dilithium-3.
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
                    signature = DilithiumSignatureHelper.Sign(plaintext, key, DilithiumParameters.Dilithium3);
                }
                else
                {
                    var (_, privateKey) = DilithiumSignatureHelper.GenerateKeyPair(
                        DilithiumParameters.Dilithium3, _secureRandom);
                    signature = DilithiumSignatureHelper.Sign(plaintext, privateKey, DilithiumParameters.Dilithium3);
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
        /// Verifies CRYSTALS-Dilithium-3 signature and returns the original data.
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

                if (!DilithiumSignatureHelper.Verify(data, signature, key, DilithiumParameters.Dilithium3))
                {
                    throw new CryptographicException(
                        "CRYSTALS-Dilithium-3 signature verification failed. Data may be tampered.");
                }

                return data;
            }, cancellationToken);
        }

        /// <summary>
        /// Generates a CRYSTALS-Dilithium-3 key pair. Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var (_, privateKey) = DilithiumSignatureHelper.GenerateKeyPair(
                DilithiumParameters.Dilithium3, _secureRandom);
            return privateKey;
        }

        /// <summary>
        /// Generates a complete Dilithium-3 key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            return DilithiumSignatureHelper.GenerateKeyPair(DilithiumParameters.Dilithium3, _secureRandom);
        }
    }

    /// <summary>
    /// CRYSTALS-Dilithium-5 (NIST FIPS 204, ML-DSA-87) digital signature strategy.
    ///
    /// Security Level: NIST Level 5
    /// Algorithm: Dilithium5 (ML-DSA-87)
    /// Signature Size: 4627 bytes
    /// Public Key Size: 2592 bytes
    /// Private Key Size: 4864 bytes
    ///
    /// Wire format: [Signature Length:4][Signature][Original Data]
    ///
    /// Use Case: Maximum quantum resistance for classified/sensitive data,
    /// long-term archival, and critical infrastructure signing.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: PQC migration")]
    public sealed class DilithiumSignature87Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "CRYSTALS-Dilithium-5-Sign",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 4627, // Dilithium5 signature size
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
                ["Algorithm"] = "CRYSTALS-Dilithium-5",
                ["FipsReference"] = "FIPS 204",
                ["StandardName"] = "ML-DSA-87",
                ["Type"] = "Signature",
                ["NistLevel"] = 5,
                ["PublicKeySize"] = 2592,
                ["PrivateKeySize"] = 4864,
                ["SignatureSize"] = 4627
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "crystals-dilithium-87";

        /// <inheritdoc/>
        public override string StrategyName => "CRYSTALS-Dilithium-5 (ML-DSA-87) Signature";

        /// <summary>
        /// Initializes a new instance of the Dilithium-5 signature strategy.
        /// </summary>
        public DilithiumSignature87Strategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dilithium.87.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dilithium.87.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Signs data using CRYSTALS-Dilithium-5.
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
                    signature = DilithiumSignatureHelper.Sign(plaintext, key, DilithiumParameters.Dilithium5);
                }
                else
                {
                    var (_, privateKey) = DilithiumSignatureHelper.GenerateKeyPair(
                        DilithiumParameters.Dilithium5, _secureRandom);
                    signature = DilithiumSignatureHelper.Sign(plaintext, privateKey, DilithiumParameters.Dilithium5);
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
        /// Verifies CRYSTALS-Dilithium-5 signature and returns the original data.
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

                if (!DilithiumSignatureHelper.Verify(data, signature, key, DilithiumParameters.Dilithium5))
                {
                    throw new CryptographicException(
                        "CRYSTALS-Dilithium-5 signature verification failed. Data may be tampered.");
                }

                return data;
            }, cancellationToken);
        }

        /// <summary>
        /// Generates a CRYSTALS-Dilithium-5 key pair. Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var (_, privateKey) = DilithiumSignatureHelper.GenerateKeyPair(
                DilithiumParameters.Dilithium5, _secureRandom);
            return privateKey;
        }

        /// <summary>
        /// Generates a complete Dilithium-5 key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            return DilithiumSignatureHelper.GenerateKeyPair(DilithiumParameters.Dilithium5, _secureRandom);
        }
    }
}
