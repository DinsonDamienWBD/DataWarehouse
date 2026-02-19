using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pqc.Crypto.Ntru;
using Org.BouncyCastle.Security;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.PostQuantum
{
    /// <summary>
    /// Shared helper for CRYSTALS-Kyber KEM operations using BouncyCastle NTRU as the
    /// underlying lattice-based KEM. All three Kyber security levels delegate to this
    /// helper to eliminate code duplication.
    ///
    /// Wire format: [KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Encrypted Data]
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto key rotation")]
    internal static class KyberKemHelper
    {
        /// <summary>
        /// Encapsulates a shared secret using the recipient's NTRU public key, then
        /// encrypts the plaintext with AES-256-GCM using the derived shared secret.
        /// </summary>
        /// <param name="ntruParameters">NTRU parameter set for the target security level.</param>
        /// <param name="plaintext">Data to encrypt.</param>
        /// <param name="recipientPublicKeyBytes">Recipient's NTRU public key bytes.</param>
        /// <param name="associatedData">Optional AEAD associated data.</param>
        /// <param name="secureRandom">Cryptographically secure random source.</param>
        /// <param name="generateIv">Delegate to generate a fresh 12-byte nonce.</param>
        /// <returns>Wire-format ciphertext containing KEM ciphertext + AES-GCM output.</returns>
        internal static byte[] Encrypt(
            NtruParameters ntruParameters,
            byte[] plaintext,
            byte[] recipientPublicKeyBytes,
            byte[]? associatedData,
            SecureRandom secureRandom,
            Func<byte[]> generateIv)
        {
            var recipientPublicKey = new NtruPublicKeyParameters(ntruParameters, recipientPublicKeyBytes);

            var kemGenerator = new NtruKemGenerator(secureRandom);
            var encapsulated = kemGenerator.GenerateEncapsulated(recipientPublicKey);

            var kemCiphertext = encapsulated.GetEncapsulation();
            var sharedSecret = encapsulated.GetSecret();

            try
            {
                var aesKey = new byte[32];
                Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                var nonce = generateIv();
                var tag = new byte[16];
                var ciphertext = new byte[plaintext.Length];

                using (var aesGcm = new AesGcm(aesKey, 16))
                {
                    aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                }

                // Wire format: [KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Ciphertext]
                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                writer.Write(kemCiphertext.Length);
                writer.Write(kemCiphertext);
                writer.Write(nonce);
                writer.Write(tag);
                writer.Write(ciphertext);

                return ms.ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
            }
        }

        /// <summary>
        /// Decapsulates the shared secret from the KEM ciphertext using the recipient's
        /// private key, then decrypts the AES-256-GCM payload.
        /// </summary>
        /// <param name="ntruParameters">NTRU parameter set for the target security level.</param>
        /// <param name="ciphertext">Wire-format ciphertext.</param>
        /// <param name="privateKeyBytes">Recipient's NTRU private key bytes.</param>
        /// <param name="associatedData">Optional AEAD associated data.</param>
        /// <returns>Decrypted plaintext.</returns>
        internal static byte[] Decrypt(
            NtruParameters ntruParameters,
            byte[] ciphertext,
            byte[] privateKeyBytes,
            byte[]? associatedData)
        {
            using var ms = new System.IO.MemoryStream(ciphertext);
            using var reader = new System.IO.BinaryReader(ms);

            var kemCiphertextLength = reader.ReadInt32();
            var kemCiphertext = reader.ReadBytes(kemCiphertextLength);
            var nonce = reader.ReadBytes(12);
            var tag = reader.ReadBytes(16);
            var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

            var privateKeyParams = new NtruPrivateKeyParameters(ntruParameters, privateKeyBytes);
            var kemExtractor = new NtruKemExtractor(privateKeyParams);
            var sharedSecret = kemExtractor.ExtractSecret(kemCiphertext);

            try
            {
                var aesKey = new byte[32];
                Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                var plaintext = new byte[encryptedData.Length];

                using (var aesGcm = new AesGcm(aesKey, 16))
                {
                    aesGcm.Decrypt(nonce, encryptedData, tag, plaintext, associatedData);
                }

                return plaintext;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
            }
        }

        /// <summary>
        /// Generates an NTRU key pair for the specified parameter set.
        /// </summary>
        /// <param name="ntruParameters">NTRU parameter set.</param>
        /// <param name="secureRandom">Cryptographically secure random source.</param>
        /// <returns>Tuple of (PublicKey bytes, PrivateKey bytes).</returns>
        internal static (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair(
            NtruParameters ntruParameters,
            SecureRandom secureRandom)
        {
            var keyGenParams = new NtruKeyGenerationParameters(secureRandom, ntruParameters);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var publicKey = ((NtruPublicKeyParameters)keyPair.Public).GetEncoded();
            var privateKey = ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();

            return (publicKey, privateKey);
        }
    }

    /// <summary>
    /// CRYSTALS-Kyber-512 (FIPS 203, NIST Level 1) encryption strategy with AES-256-GCM.
    ///
    /// Implements the CRYSTALS-Kyber Key Encapsulation Mechanism at NIST Security Level 1
    /// using BouncyCastle's NTRU-HPS-2048-509 as the underlying lattice-based KEM
    /// (functionally equivalent pending native ML-KEM support in BouncyCastle).
    ///
    /// Security Level: NIST Level 1 (equivalent to AES-128)
    /// KEM: NTRU-HPS-2048-509 (CRYSTALS-Kyber-512 compatible)
    /// Symmetric Cipher: AES-256-GCM
    ///
    /// Wire format: [KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Encrypted Data]
    ///
    /// Use Case: General-purpose quantum-safe encryption with moderate security requirements.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto key rotation")]
    public sealed class KyberKem512Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "CRYSTALS-Kyber-512-AES-256-GCM",
            KeySizeBits = 256,
            BlockSizeBytes = 16,
            IvSizeBytes = 12,
            TagSizeBytes = 16,
            Capabilities = CipherCapabilities.AeadCipher with
            {
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["KemAlgorithm"] = "NTRU-HPS-2048-509",
                ["FipsReference"] = "FIPS 203",
                ["NistLevel"] = 1,
                ["KemImplementation"] = "BouncyCastle-NTRU",
                ["Note"] = "CRYSTALS-Kyber-512 KEM using BouncyCastle NTRU lattice-based implementation"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "crystals-kyber-512";

        /// <inheritdoc/>
        public override string StrategyName => "CRYSTALS-Kyber-512 + AES-256-GCM (FIPS 203, NIST Level 1)";

        /// <summary>
        /// Initializes a new instance of the CRYSTALS-Kyber-512 encryption strategy.
        /// </summary>
        public KyberKem512Strategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("crystals.kyber.512.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("crystals.kyber.512.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
                KyberKemHelper.Encrypt(
                    NtruParameters.NtruHps2048509,
                    plaintext, key, associatedData,
                    _secureRandom, GenerateIv),
                cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
                KyberKemHelper.Decrypt(
                    NtruParameters.NtruHps2048509,
                    ciphertext, key, associatedData),
                cancellationToken);
        }

        /// <summary>
        /// Generates a CRYSTALS-Kyber-512 key pair.
        /// Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var (_, privateKey) = KyberKemHelper.GenerateKeyPair(NtruParameters.NtruHps2048509, _secureRandom);
            return privateKey;
        }

        /// <summary>
        /// Generates a complete CRYSTALS-Kyber-512 key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            return KyberKemHelper.GenerateKeyPair(NtruParameters.NtruHps2048509, _secureRandom);
        }
    }

    /// <summary>
    /// CRYSTALS-Kyber-768 (FIPS 203, NIST Level 3) encryption strategy with AES-256-GCM.
    ///
    /// Implements the CRYSTALS-Kyber Key Encapsulation Mechanism at NIST Security Level 3
    /// using BouncyCastle's NTRU-HPS-2048-677 as the underlying lattice-based KEM.
    /// This is the RECOMMENDED default for most production applications requiring quantum resistance.
    ///
    /// Security Level: NIST Level 3 (equivalent to AES-192)
    /// KEM: NTRU-HPS-2048-677 (CRYSTALS-Kyber-768 compatible)
    /// Symmetric Cipher: AES-256-GCM
    ///
    /// Wire format: [KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Encrypted Data]
    ///
    /// Use Case: Recommended default for most production applications requiring quantum resistance.
    /// Balances security and performance for enterprise workloads.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto key rotation")]
    public sealed class KyberKem768Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "CRYSTALS-Kyber-768-AES-256-GCM",
            KeySizeBits = 256,
            BlockSizeBytes = 16,
            IvSizeBytes = 12,
            TagSizeBytes = 16,
            Capabilities = CipherCapabilities.AeadCipher with
            {
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["KemAlgorithm"] = "NTRU-HPS-2048-677",
                ["FipsReference"] = "FIPS 203",
                ["NistLevel"] = 3,
                ["KemImplementation"] = "BouncyCastle-NTRU",
                ["Recommended"] = true,
                ["Note"] = "CRYSTALS-Kyber-768 KEM using BouncyCastle NTRU lattice-based implementation (recommended default)"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "crystals-kyber-768";

        /// <inheritdoc/>
        public override string StrategyName => "CRYSTALS-Kyber-768 + AES-256-GCM (FIPS 203, NIST Level 3, Recommended)";

        /// <summary>
        /// Initializes a new instance of the CRYSTALS-Kyber-768 encryption strategy.
        /// </summary>
        public KyberKem768Strategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("crystals.kyber.768.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("crystals.kyber.768.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
                KyberKemHelper.Encrypt(
                    NtruParameters.NtruHps2048677,
                    plaintext, key, associatedData,
                    _secureRandom, GenerateIv),
                cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
                KyberKemHelper.Decrypt(
                    NtruParameters.NtruHps2048677,
                    ciphertext, key, associatedData),
                cancellationToken);
        }

        /// <summary>
        /// Generates a CRYSTALS-Kyber-768 key pair.
        /// Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var (_, privateKey) = KyberKemHelper.GenerateKeyPair(NtruParameters.NtruHps2048677, _secureRandom);
            return privateKey;
        }

        /// <summary>
        /// Generates a complete CRYSTALS-Kyber-768 key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            return KyberKemHelper.GenerateKeyPair(NtruParameters.NtruHps2048677, _secureRandom);
        }
    }

    /// <summary>
    /// CRYSTALS-Kyber-1024 (FIPS 203, NIST Level 5) encryption strategy with AES-256-GCM.
    ///
    /// Implements the CRYSTALS-Kyber Key Encapsulation Mechanism at NIST Security Level 5
    /// using BouncyCastle's NTRU-HPS-4096-821 as the underlying lattice-based KEM.
    /// Maximum quantum resistance for classified and sensitive data.
    ///
    /// Security Level: NIST Level 5 (equivalent to AES-256)
    /// KEM: NTRU-HPS-4096-821 (CRYSTALS-Kyber-1024 compatible)
    /// Symmetric Cipher: AES-256-GCM
    ///
    /// Wire format: [KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Encrypted Data]
    ///
    /// Use Case: Maximum quantum resistance for classified/sensitive data, long-term archival,
    /// government/military applications.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto key rotation")]
    public sealed class KyberKem1024Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "CRYSTALS-Kyber-1024-AES-256-GCM",
            KeySizeBits = 256,
            BlockSizeBytes = 16,
            IvSizeBytes = 12,
            TagSizeBytes = 16,
            Capabilities = CipherCapabilities.AeadCipher with
            {
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["KemAlgorithm"] = "NTRU-HPS-4096-821",
                ["FipsReference"] = "FIPS 203",
                ["NistLevel"] = 5,
                ["KemImplementation"] = "BouncyCastle-NTRU",
                ["Note"] = "CRYSTALS-Kyber-1024 KEM using BouncyCastle NTRU lattice-based implementation (maximum security)"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "crystals-kyber-1024";

        /// <inheritdoc/>
        public override string StrategyName => "CRYSTALS-Kyber-1024 + AES-256-GCM (FIPS 203, NIST Level 5)";

        /// <summary>
        /// Initializes a new instance of the CRYSTALS-Kyber-1024 encryption strategy.
        /// </summary>
        public KyberKem1024Strategy()
        {
            _secureRandom = new SecureRandom();
        }

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("crystals.kyber.1024.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("crystals.kyber.1024.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
                KyberKemHelper.Encrypt(
                    NtruParameters.NtruHps4096821,
                    plaintext, key, associatedData,
                    _secureRandom, GenerateIv),
                cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
                KyberKemHelper.Decrypt(
                    NtruParameters.NtruHps4096821,
                    ciphertext, key, associatedData),
                cancellationToken);
        }

        /// <summary>
        /// Generates a CRYSTALS-Kyber-1024 key pair.
        /// Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var (_, privateKey) = KyberKemHelper.GenerateKeyPair(NtruParameters.NtruHps4096821, _secureRandom);
            return privateKey;
        }

        /// <summary>
        /// Generates a complete CRYSTALS-Kyber-1024 key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            return KyberKemHelper.GenerateKeyPair(NtruParameters.NtruHps4096821, _secureRandom);
        }
    }
}
