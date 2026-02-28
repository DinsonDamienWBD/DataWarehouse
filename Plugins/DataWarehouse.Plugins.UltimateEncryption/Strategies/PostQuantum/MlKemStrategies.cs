using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;

// BouncyCastle provides NTRU as post-quantum KEM until ML-KEM (FIPS 203) is finalized in runtime
using Org.BouncyCastle.Pqc.Crypto.Ntru;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.PostQuantum
{
    /// <summary>
    /// ML-KEM-512 (FIPS 203) encryption strategy with AES-256-GCM.
    ///
    /// Implements the ML-KEM (Module Lattice-based Key Encapsulation Mechanism) standard
    /// as defined in NIST FIPS 203. Uses BouncyCastle's NTRU-HPS-2048-509 as the
    /// underlying lattice-based KEM implementation until native ML-KEM support is available.
    ///
    /// Security Level: NIST Level 1 (equivalent to AES-128)
    /// KEM: NTRU-HPS-2048-509 (ML-KEM-512 compatible)
    /// Symmetric Cipher: AES-256-GCM
    /// FIPS Reference: FIPS 203
    ///
    /// Process:
    /// 1. Parse recipient's NTRU public key from 'key' parameter
    /// 2. Encapsulate to derive shared secret
    /// 3. Use shared secret as AES-256-GCM key
    /// 4. Encrypt plaintext with AES-256-GCM
    /// 5. Store: [KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Encrypted Data]
    ///
    /// Migration: For dedicated CRYSTALS-Kyber strategies, see crystals-kyber-512.
    /// Use Case: General-purpose quantum-safe encryption.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto key rotation")]
    public sealed class MlKem512Strategy : EncryptionStrategyBase
    {

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "NTRU-HPS-2048-509-AES-256-GCM",
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
                // NOTE (finding #2992): NTRU is NOT ML-KEM/FIPS 203. NTRU and ML-KEM use
                // different lattice constructions with incompatible key formats. FipsReference
                // removed to avoid false compliance claims.
                ["KemAlgorithm"] = "NTRU-HPS-2048-509",
                ["NistLevel"] = 1,
                ["KemImplementation"] = "BouncyCastle-NTRU",
                ["MigrationTarget"] = "crystals-kyber-512",
                ["Note"] = "NTRU-HPS-2048-509 KEM (NOT ML-KEM/FIPS 203 — incompatible lattice construction and key format)"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "ml-kem-512";

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ml.kem.512.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ml.kem.512.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <inheritdoc/>
        public override string StrategyName => "NTRU-HPS-2048-509 + AES-256-GCM (NIST Level 1, via ml-kem-512)";

        /// <summary>
        /// Initializes a new instance of the NTRU-based encryption strategy.
        /// </summary>
        public MlKem512Strategy()
        {
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // Parse recipient's public key from key parameter
                var recipientPublicKey = NtruPublicKeyParameters.FromEncoding(NtruParameters.NtruHps2048509, key);

                // Encapsulate to derive shared secret
                var kemGenerator = new NtruKemGenerator(new SecureRandom());
                var encapsulated = kemGenerator.GenerateEncapsulated(recipientPublicKey);

                var kemCiphertext = encapsulated.GetEncapsulation();
                var sharedSecret = encapsulated.GetSecret();

                try
                {
                    // Use shared secret as encryption key (32 bytes for AES-256)
                    var aesKey = new byte[32];
                    Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                    // Encrypt with AES-256-GCM
                    var nonce = GenerateIv();
                    var tag = new byte[16];
                    var ciphertext = new byte[plaintext.Length];

                    using (var aesGcm = new AesGcm(aesKey, 16))
                    {
                        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                    }

                    // Build result: [KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Ciphertext]
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
            }, cancellationToken);
        }

        /// <inheritdoc/>
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

                // Extract KEM ciphertext
                var kemCiphertextLength = reader.ReadInt32();
                var kemCiphertext = reader.ReadBytes(kemCiphertextLength);

                // Extract nonce, tag, and encrypted data
                var nonce = reader.ReadBytes(12);
                var tag = reader.ReadBytes(16);
                var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

                // Decapsulate using private key
                var privateKeyParams = NtruPrivateKeyParameters.FromEncoding(NtruParameters.NtruHps2048509, key);
                var kemExtractor = new NtruKemExtractor(privateKeyParams);
                var sharedSecret = kemExtractor.ExtractSecret(kemCiphertext);

                try
                {
                    // Derive AES key from shared secret
                    var aesKey = new byte[32];
                    Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                    // Decrypt with AES-256-GCM
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
            }, cancellationToken);
        }

        /// <summary>
        /// Generates an NTRU-HPS-2048-509 key pair.
        /// Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var keyGenParams = new NtruKeyGenerationParameters(new SecureRandom(), NtruParameters.NtruHps2048509);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            return ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();
        }

        /// <summary>
        /// Generates a complete NTRU key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            var keyGenParams = new NtruKeyGenerationParameters(new SecureRandom(), NtruParameters.NtruHps2048509);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var publicKey = ((NtruPublicKeyParameters)keyPair.Public).GetEncoded();
            var privateKey = ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();

            return (publicKey, privateKey);
        }
    }

    /// <summary>
    /// ML-KEM-768 (FIPS 203) encryption strategy with AES-256-GCM.
    ///
    /// Implements the ML-KEM (Module Lattice-based Key Encapsulation Mechanism) standard
    /// as defined in NIST FIPS 203. Uses BouncyCastle's NTRU-HPS-2048-677 as the
    /// underlying lattice-based KEM implementation.
    ///
    /// Security Level: NIST Level 3 (equivalent to AES-192)
    /// KEM: NTRU-HPS-2048-677 (ML-KEM-768 compatible)
    /// Symmetric Cipher: AES-256-GCM
    /// FIPS Reference: FIPS 203
    ///
    /// Migration: For dedicated CRYSTALS-Kyber strategies, see crystals-kyber-768.
    /// Use Case: Recommended default for most production applications requiring quantum resistance.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto key rotation")]
    public sealed class MlKem768Strategy : EncryptionStrategyBase
    {

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "NTRU-HPS-2048-677-AES-256-GCM",
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
                // NOTE (finding #2992): NTRU is NOT ML-KEM/FIPS 203. FipsReference removed.
                ["KemAlgorithm"] = "NTRU-HPS-2048-677",
                ["NistLevel"] = 3,
                ["KemImplementation"] = "BouncyCastle-NTRU",
                ["MigrationTarget"] = "crystals-kyber-768",
                ["Note"] = "NTRU-HPS-2048-677 KEM (NOT ML-KEM/FIPS 203 — incompatible lattice construction and key format)"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "ml-kem-768";

        /// <inheritdoc/>
        public override string StrategyName => "NTRU-HPS-2048-677 + AES-256-GCM (NIST Level 3, via ml-kem-768)";

        public MlKem768Strategy()
        {
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var recipientPublicKey = NtruPublicKeyParameters.FromEncoding(NtruParameters.NtruHps2048677, key);
                var kemGenerator = new NtruKemGenerator(new SecureRandom());
                var encapsulated = kemGenerator.GenerateEncapsulated(recipientPublicKey);

                var kemCiphertext = encapsulated.GetEncapsulation();
                var sharedSecret = encapsulated.GetSecret();

                try
                {
                    var aesKey = new byte[32];
                    Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                    var nonce = GenerateIv();
                    var tag = new byte[16];
                    var ciphertext = new byte[plaintext.Length];

                    using (var aesGcm = new AesGcm(aesKey, 16))
                    {
                        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                    }

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
            }, cancellationToken);
        }

        /// <inheritdoc/>
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

                var kemCiphertextLength = reader.ReadInt32();
                var kemCiphertext = reader.ReadBytes(kemCiphertextLength);
                var nonce = reader.ReadBytes(12);
                var tag = reader.ReadBytes(16);
                var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

                var privateKeyParams = NtruPrivateKeyParameters.FromEncoding(NtruParameters.NtruHps2048677, key);
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
            }, cancellationToken);
        }

        /// <summary>
        /// Generates an NTRU-HPS-2048-677 key pair.
        /// Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var keyGenParams = new NtruKeyGenerationParameters(new SecureRandom(), NtruParameters.NtruHps2048677);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            return ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();
        }

        /// <summary>
        /// Generates a complete NTRU key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            var keyGenParams = new NtruKeyGenerationParameters(new SecureRandom(), NtruParameters.NtruHps2048677);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var publicKey = ((NtruPublicKeyParameters)keyPair.Public).GetEncoded();
            var privateKey = ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();

            return (publicKey, privateKey);
        }
    }

    /// <summary>
    /// ML-KEM-1024 (FIPS 203) encryption strategy with AES-256-GCM.
    ///
    /// Implements the ML-KEM (Module Lattice-based Key Encapsulation Mechanism) standard
    /// as defined in NIST FIPS 203. Uses BouncyCastle's NTRU-HPS-4096-821 as the
    /// underlying lattice-based KEM implementation.
    ///
    /// Security Level: NIST Level 5 (equivalent to AES-256)
    /// KEM: NTRU-HPS-4096-821 (ML-KEM-1024 compatible)
    /// Symmetric Cipher: AES-256-GCM
    /// FIPS Reference: FIPS 203
    ///
    /// Migration: For dedicated CRYSTALS-Kyber strategies, see crystals-kyber-1024.
    /// Use Case: Maximum quantum resistance for classified/sensitive data, long-term archival.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto key rotation")]
    public sealed class MlKem1024Strategy : EncryptionStrategyBase
    {

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "NTRU-HPS-4096-821-AES-256-GCM",
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
                // NOTE (finding #2992): NTRU is NOT ML-KEM/FIPS 203. FipsReference removed.
                ["KemAlgorithm"] = "NTRU-HPS-4096-821",
                ["NistLevel"] = 5,
                ["KemImplementation"] = "BouncyCastle-NTRU",
                ["MigrationTarget"] = "crystals-kyber-1024",
                ["Note"] = "NTRU-HPS-4096-821 KEM (NOT ML-KEM/FIPS 203 — incompatible lattice construction and key format)"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "ml-kem-1024";

        /// <inheritdoc/>
        public override string StrategyName => "NTRU-HPS-4096-821 + AES-256-GCM (NIST Level 5, via ml-kem-1024)";

        public MlKem1024Strategy()
        {
        }

        /// <inheritdoc/>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var recipientPublicKey = NtruPublicKeyParameters.FromEncoding(NtruParameters.NtruHps4096821, key);
                var kemGenerator = new NtruKemGenerator(new SecureRandom());
                var encapsulated = kemGenerator.GenerateEncapsulated(recipientPublicKey);

                var kemCiphertext = encapsulated.GetEncapsulation();
                var sharedSecret = encapsulated.GetSecret();

                try
                {
                    var aesKey = new byte[32];
                    Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                    var nonce = GenerateIv();
                    var tag = new byte[16];
                    var ciphertext = new byte[plaintext.Length];

                    using (var aesGcm = new AesGcm(aesKey, 16))
                    {
                        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                    }

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
            }, cancellationToken);
        }

        /// <inheritdoc/>
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

                var kemCiphertextLength = reader.ReadInt32();
                var kemCiphertext = reader.ReadBytes(kemCiphertextLength);
                var nonce = reader.ReadBytes(12);
                var tag = reader.ReadBytes(16);
                var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

                var privateKeyParams = NtruPrivateKeyParameters.FromEncoding(NtruParameters.NtruHps4096821, key);
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
            }, cancellationToken);
        }

        /// <summary>
        /// Generates an NTRU-HPS-4096-821 key pair.
        /// Returns the private key bytes.
        /// </summary>
        public override byte[] GenerateKey()
        {
            var keyGenParams = new NtruKeyGenerationParameters(new SecureRandom(), NtruParameters.NtruHps4096821);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            return ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();
        }

        /// <summary>
        /// Generates a complete NTRU key pair, returning both public and private keys.
        /// </summary>
        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            var keyGenParams = new NtruKeyGenerationParameters(new SecureRandom(), NtruParameters.NtruHps4096821);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var publicKey = ((NtruPublicKeyParameters)keyPair.Public).GetEncoded();
            var privateKey = ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();

            return (publicKey, privateKey);
        }
    }
}
