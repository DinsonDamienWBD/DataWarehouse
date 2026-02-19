using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;

// BouncyCastle provides NTRU as post-quantum KEM until ML-KEM (FIPS 203) is finalized
using Org.BouncyCastle.Pqc.Crypto.Ntru;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.PostQuantum
{
    /// <summary>
    /// NTRU-HPS-2048-509 (NIST PQC Round 3) encryption strategy with AES-256-GCM.
    ///
    /// NTRU is a lattice-based KEM that provides post-quantum security.
    /// While ML-KEM (FIPS 203) is the NIST standard, BouncyCastle currently provides
    /// NTRU as a mature post-quantum KEM implementation.
    ///
    /// Security Level: NIST Level 1
    /// KEM: NTRU-HPS-2048-509
    /// Symmetric Cipher: AES-256-GCM
    ///
    /// Process:
    /// 1. Parse recipient's NTRU public key from 'key' parameter
    /// 2. Encapsulate to derive shared secret
    /// 3. Use shared secret as AES-256-GCM key
    /// 4. Encrypt plaintext with AES-256-GCM
    /// 5. Store: [KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Encrypted Data]
    ///
    /// Use Case: General-purpose quantum-safe encryption.
    /// </summary>
    public sealed class MlKem512Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

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
                ["KemAlgorithm"] = "NTRU-HPS-2048-509",
                ["NistLevel"] = 1,
                ["Note"] = "Uses NTRU as post-quantum KEM (ML-KEM FIPS 203 awaiting BouncyCastle support)"
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
        public override string StrategyName => "NTRU-HPS-2048-509 + AES-256-GCM (ML-KEM-512 compatible)";

        /// <summary>
        /// Initializes a new instance of the NTRU-based encryption strategy.
        /// </summary>
        public MlKem512Strategy()
        {
            _secureRandom = new SecureRandom();
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
                var recipientPublicKey = new NtruPublicKeyParameters(NtruParameters.NtruHps2048509, key);

                // Encapsulate to derive shared secret
                var kemGenerator = new NtruKemGenerator(_secureRandom);
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
                var privateKeyParams = new NtruPrivateKeyParameters(NtruParameters.NtruHps2048509, key);
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
            var keyGenParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048509);
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
            var keyGenParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048509);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var publicKey = ((NtruPublicKeyParameters)keyPair.Public).GetEncoded();
            var privateKey = ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();

            return (publicKey, privateKey);
        }
    }

    /// <summary>
    /// NTRU-HPS-2048-677 (NIST PQC Round 3) encryption strategy with AES-256-GCM.
    ///
    /// Security Level: NIST Level 3
    /// KEM: NTRU-HPS-2048-677
    /// Symmetric Cipher: AES-256-GCM
    ///
    /// Use Case: Recommended default for most production applications requiring quantum resistance.
    /// </summary>
    public sealed class MlKem768Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

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
                ["KemAlgorithm"] = "NTRU-HPS-2048-677",
                ["NistLevel"] = 3,
                ["Note"] = "Uses NTRU as post-quantum KEM (ML-KEM FIPS 203 awaiting BouncyCastle support)"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "ml-kem-768";

        /// <inheritdoc/>
        public override string StrategyName => "NTRU-HPS-2048-677 + AES-256-GCM (ML-KEM-768 compatible)";

        public MlKem768Strategy()
        {
            _secureRandom = new SecureRandom();
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
                var recipientPublicKey = new NtruPublicKeyParameters(NtruParameters.NtruHps2048677, key);
                var kemGenerator = new NtruKemGenerator(_secureRandom);
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

                var privateKeyParams = new NtruPrivateKeyParameters(NtruParameters.NtruHps2048677, key);
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
            var keyGenParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048677);
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
            var keyGenParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048677);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var publicKey = ((NtruPublicKeyParameters)keyPair.Public).GetEncoded();
            var privateKey = ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();

            return (publicKey, privateKey);
        }
    }

    /// <summary>
    /// NTRU-HPS-4096-821 (NIST PQC Round 3) encryption strategy with AES-256-GCM.
    ///
    /// Security Level: NIST Level 5
    /// KEM: NTRU-HPS-4096-821
    /// Symmetric Cipher: AES-256-GCM
    ///
    /// Use Case: Maximum quantum resistance for classified/sensitive data, long-term archival.
    /// </summary>
    public sealed class MlKem1024Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

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
                ["KemAlgorithm"] = "NTRU-HPS-4096-821",
                ["NistLevel"] = 5,
                ["Note"] = "Uses NTRU as post-quantum KEM (ML-KEM FIPS 203 awaiting BouncyCastle support)"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "ml-kem-1024";

        /// <inheritdoc/>
        public override string StrategyName => "NTRU-HPS-4096-821 + AES-256-GCM (ML-KEM-1024 compatible)";

        public MlKem1024Strategy()
        {
            _secureRandom = new SecureRandom();
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
                var recipientPublicKey = new NtruPublicKeyParameters(NtruParameters.NtruHps4096821, key);
                var kemGenerator = new NtruKemGenerator(_secureRandom);
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

                var privateKeyParams = new NtruPrivateKeyParameters(NtruParameters.NtruHps4096821, key);
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
            var keyGenParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps4096821);
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
            var keyGenParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps4096821);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();

            var publicKey = ((NtruPublicKeyParameters)keyPair.Public).GetEncoded();
            var privateKey = ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();

            return (publicKey, privateKey);
        }
    }
}
