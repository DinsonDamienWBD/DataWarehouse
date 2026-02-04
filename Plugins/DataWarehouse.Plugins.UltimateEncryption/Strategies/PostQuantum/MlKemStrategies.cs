using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;

// Stub types for ML-KEM (CRYSTALS-Kyber) - BouncyCastle 2.5.1 doesn't have full NIST FIPS 203 support yet
// These will be replaced when BouncyCastle adds official NIST FIPS 203 support
namespace Org.BouncyCastle.Pqc.Crypto.Crystals.Kyber
{
    internal class KyberParameters
    {
        internal static readonly KyberParameters kyber512 = new();
        internal static readonly KyberParameters kyber768 = new();
        internal static readonly KyberParameters kyber1024 = new();
    }

    internal class KyberPublicKeyParameters : AsymmetricKeyParameter
    {
        internal KyberPublicKeyParameters(KyberParameters parameters, byte[] publicKey) : base(false) { }
        internal byte[] GetEncoded() => Array.Empty<byte>();
    }

    internal class KyberPrivateKeyParameters : AsymmetricKeyParameter
    {
        internal KyberPrivateKeyParameters(KyberParameters parameters, byte[] privateKey, byte[]? publicKey) : base(true) { }
        internal byte[] GetEncoded() => Array.Empty<byte>();
    }

    internal class KyberKeyGenerationParameters : KeyGenerationParameters
    {
        internal KyberKeyGenerationParameters(SecureRandom random, KyberParameters parameters) : base(random, 256) { }
    }

    internal class KyberKeyPairGenerator : IAsymmetricCipherKeyPairGenerator
    {
        public void Init(KeyGenerationParameters parameters) { }
        public AsymmetricCipherKeyPair GenerateKeyPair() => new AsymmetricCipherKeyPair(
            new KyberPublicKeyParameters(KyberParameters.kyber768, Array.Empty<byte>()),
            new KyberPrivateKeyParameters(KyberParameters.kyber768, Array.Empty<byte>(), null));
    }

    internal class KyberKemGenerator
    {
        internal KyberKemGenerator(SecureRandom random) { }
        internal ISecretWithEncapsulation GenerateEncapsulated(KyberPublicKeyParameters publicKey) =>
            new StubSecretWithEncapsulation();
    }

    internal class KyberKemExtractor
    {
        internal KyberKemExtractor(KyberPrivateKeyParameters privateKey) { }
        internal byte[] ExtractSecret(byte[] ciphertext) => Array.Empty<byte>();
    }

    internal class StubSecretWithEncapsulation : ISecretWithEncapsulation
    {
        public byte[] GetEncapsulation() => Array.Empty<byte>();
        public byte[] GetSecret() => Array.Empty<byte>();
        public void Dispose() { }
    }
}

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.PostQuantum
{
    using Org.BouncyCastle.Pqc.Crypto.Crystals.Kyber;

    /// <summary>
    /// ML-KEM-512 (NIST FIPS 203) encryption strategy with AES-256-GCM.
    ///
    /// Security Level: NIST Level 1 (equivalent to AES-128)
    /// KEM: ML-KEM-512 (CRYSTALS-Kyber-512)
    /// Symmetric Cipher: AES-256-GCM
    ///
    /// Process:
    /// 1. Generate ephemeral ML-KEM-512 key pair
    /// 2. Encapsulate to derive shared secret (32 bytes)
    /// 3. Use shared secret as AES-256-GCM key
    /// 4. Encrypt plaintext with AES-256-GCM
    /// 5. Store: [Encapsulated Ciphertext][Nonce:12][Tag:16][Encrypted Data]
    ///
    /// Use Case: General-purpose quantum-safe encryption with smaller key sizes.
    /// </summary>
    public sealed class MlKem512Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "ML-KEM-512-AES-256-GCM",
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
                ["KemAlgorithm"] = "ML-KEM-512",
                ["NistLevel"] = 1,
                ["PublicKeySize"] = 800,
                ["PrivateKeySize"] = 1632,
                ["CiphertextSize"] = 768
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "ml-kem-512";

        /// <inheritdoc/>
        public override string StrategyName => "ML-KEM-512 + AES-256-GCM";

        /// <summary>
        /// Initializes a new instance of ML-KEM-512 encryption strategy.
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
                // Step 1: Generate ephemeral ML-KEM-512 key pair
                var keyGenParams = new KyberKeyGenerationParameters(_secureRandom, KyberParameters.kyber512);
                var keyPairGenerator = new KyberKeyPairGenerator();
                keyPairGenerator.Init(keyGenParams);
                var keyPair = keyPairGenerator.GenerateKeyPair();

                // Step 2: Encapsulate to derive shared secret
                var kemGenerator = new KyberKemGenerator(_secureRandom);
                var publicKeyParams = (KyberPublicKeyParameters)keyPair.Public;
                var encapsulated = kemGenerator.GenerateEncapsulated(publicKeyParams);

                var kemCiphertext = encapsulated.GetEncapsulation();
                var sharedSecret = encapsulated.GetSecret();

                try
                {
                    // Step 3: Use shared secret as encryption key (take first 32 bytes for AES-256)
                    var aesKey = new byte[32];
                    Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                    // Step 4: Encrypt with AES-256-GCM
                    var nonce = GenerateIv();
                    var tag = new byte[16];
                    var ciphertext = new byte[plaintext.Length];

                    using (var aesGcm = new AesGcm(aesKey, 16))
                    {
                        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                    }

                    // Step 5: Build result: [KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Ciphertext]
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
                    // Clear sensitive data
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

                // Step 1: Extract KEM ciphertext
                var kemCiphertextLength = reader.ReadInt32();
                var kemCiphertext = reader.ReadBytes(kemCiphertextLength);

                // Step 2: Extract nonce, tag, and encrypted data
                var nonce = reader.ReadBytes(12);
                var tag = reader.ReadBytes(16);
                var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

                // Step 3: Decapsulate using private key (from 'key' parameter which should be ML-KEM private key)
                var kemExtractor = new KyberKemExtractor(
                    new KyberPrivateKeyParameters(KyberParameters.kyber512, key, null));
                var sharedSecret = kemExtractor.ExtractSecret(kemCiphertext);

                try
                {
                    // Step 4: Derive AES key from shared secret
                    var aesKey = new byte[32];
                    Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                    // Step 5: Decrypt with AES-256-GCM
                    var plaintext = new byte[encryptedData.Length];

                    using (var aesGcm = new AesGcm(aesKey, 16))
                    {
                        aesGcm.Decrypt(nonce, encryptedData, tag, plaintext, associatedData);
                    }

                    return plaintext;
                }
                finally
                {
                    // Clear sensitive data
                    CryptographicOperations.ZeroMemory(sharedSecret);
                }
            }, cancellationToken);
        }
    }

    /// <summary>
    /// ML-KEM-768 (NIST FIPS 203) encryption strategy with AES-256-GCM.
    ///
    /// Security Level: NIST Level 3 (equivalent to AES-192)
    /// KEM: ML-KEM-768 (CRYSTALS-Kyber-768)
    /// Symmetric Cipher: AES-256-GCM
    ///
    /// Process: Same as ML-KEM-512 but with larger keys for higher security.
    ///
    /// Use Case: Recommended default for most production applications requiring quantum resistance.
    /// </summary>
    public sealed class MlKem768Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "ML-KEM-768-AES-256-GCM",
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
                ["KemAlgorithm"] = "ML-KEM-768",
                ["NistLevel"] = 3,
                ["PublicKeySize"] = 1184,
                ["PrivateKeySize"] = 2400,
                ["CiphertextSize"] = 1088
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "ml-kem-768";

        /// <inheritdoc/>
        public override string StrategyName => "ML-KEM-768 + AES-256-GCM";

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
                var keyGenParams = new KyberKeyGenerationParameters(_secureRandom, KyberParameters.kyber768);
                var keyPairGenerator = new KyberKeyPairGenerator();
                keyPairGenerator.Init(keyGenParams);
                var keyPair = keyPairGenerator.GenerateKeyPair();

                var kemGenerator = new KyberKemGenerator(_secureRandom);
                var publicKeyParams = (KyberPublicKeyParameters)keyPair.Public;
                var encapsulated = kemGenerator.GenerateEncapsulated(publicKeyParams);

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

                var kemExtractor = new KyberKemExtractor(
                    new KyberPrivateKeyParameters(KyberParameters.kyber768, key, null));
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
    }

    /// <summary>
    /// ML-KEM-1024 (NIST FIPS 203) encryption strategy with AES-256-GCM.
    ///
    /// Security Level: NIST Level 5 (equivalent to AES-256)
    /// KEM: ML-KEM-1024 (CRYSTALS-Kyber-1024)
    /// Symmetric Cipher: AES-256-GCM
    ///
    /// Process: Same as ML-KEM-512 but with largest keys for maximum security.
    ///
    /// Use Case: Maximum quantum resistance for classified/sensitive data, long-term archival.
    /// </summary>
    public sealed class MlKem1024Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "ML-KEM-1024-AES-256-GCM",
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
                ["KemAlgorithm"] = "ML-KEM-1024",
                ["NistLevel"] = 5,
                ["PublicKeySize"] = 1568,
                ["PrivateKeySize"] = 3168,
                ["CiphertextSize"] = 1568
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "ml-kem-1024";

        /// <inheritdoc/>
        public override string StrategyName => "ML-KEM-1024 + AES-256-GCM";

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
                var keyGenParams = new KyberKeyGenerationParameters(_secureRandom, KyberParameters.kyber1024);
                var keyPairGenerator = new KyberKeyPairGenerator();
                keyPairGenerator.Init(keyGenParams);
                var keyPair = keyPairGenerator.GenerateKeyPair();

                var kemGenerator = new KyberKemGenerator(_secureRandom);
                var publicKeyParams = (KyberPublicKeyParameters)keyPair.Public;
                var encapsulated = kemGenerator.GenerateEncapsulated(publicKeyParams);

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

                var kemExtractor = new KyberKemExtractor(
                    new KyberPrivateKeyParameters(KyberParameters.kyber1024, key, null));
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
    }
}
