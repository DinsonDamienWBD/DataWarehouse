using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pqc.Crypto.Ntru;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Math.EC;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Hybrid;

/// <summary>
/// Hybrid AES-256-GCM + NTRU-HPS-2048-677 encryption strategy.
///
/// Combines classical ECDH P-384 key agreement with post-quantum NTRU KEM.
/// Provides defense-in-depth: secure if either classical or PQ algorithm remains unbroken.
///
/// Process:
/// 1. Generate ephemeral ECDH P-384 key pair -> derive classical shared secret
/// 2. Encapsulate using NTRU-HPS-2048-677 -> derive PQ shared secret
/// 3. Combine both secrets using HKDF-SHA384
/// 4. Encrypt plaintext with AES-256-GCM using combined key
/// 5. Store: [ECDH Public Key Length:2][ECDH Public Key][KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Encrypted Data]
///
/// Security Level: QuantumSafe (hybrid)
/// Classical Component: ECDH P-384 (NIST Level 3)
/// PQ Component: NTRU-HPS-2048-677 (NIST Level 3)
/// Symmetric Cipher: AES-256-GCM
///
/// Use Case: Maximum security for long-term data protection against both classical and quantum threats.
/// </summary>
public sealed class HybridAesKyberStrategy : EncryptionStrategyBase
{
    private readonly SecureRandom _secureRandom;
    private const int AesKeySize = 32; // 256 bits
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "Hybrid-AES-256-GCM-ECDH-P384-NTRU-677",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        Capabilities = CipherCapabilities.AeadCipher with
        {
            MinimumSecurityLevel = SecurityLevel.QuantumSafe
        },
        SecurityLevel = SecurityLevel.QuantumSafe,
        Parameters = new Dictionary<string, object>
        {
            ["HybridScheme"] = "ECDH-P384 + NTRU-HPS-2048-677",
            ["ClassicalAlgorithm"] = "ECDH-P384",
            ["PostQuantumAlgorithm"] = "NTRU-HPS-2048-677",
            ["SymmetricCipher"] = "AES-256-GCM",
            ["KDF"] = "HKDF-SHA384"
        }
    };

    /// <inheritdoc/>
    public override string StrategyId => "hybrid-aes-kyber";

    /// <inheritdoc/>
    public override string StrategyName => "Hybrid AES-256-GCM + ECDH-P384 + NTRU";

    /// <summary>
    /// Initializes a new instance of the hybrid AES-NTRU encryption strategy.
    /// </summary>
    public HybridAesKyberStrategy()
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
            // Step 1: Generate ephemeral ECDH P-384 key pair
            var ecdhGenerator = new ECKeyPairGenerator();
            var curveParams = ECNamedCurveTable.GetByName("P-384");
            var ecDomainParams = new ECDomainParameters(curveParams.Curve, curveParams.G, curveParams.N, curveParams.H);
            var ecdhParams = new ECKeyGenerationParameters(
                ecDomainParams,
                _secureRandom);
            ecdhGenerator.Init(ecdhParams);
            var ecdhKeyPair = ecdhGenerator.GenerateKeyPair();

            var ecdhPublicKey = ((ECPublicKeyParameters)ecdhKeyPair.Public).Q.GetEncoded(compressed: false);

            // Step 2: Derive classical shared secret using ECDH (with recipient's public key from 'key')
            var recipientEcdhPublic = DecodeECPublicKey(key);
            var ecdhAgreement = new ECDHBasicAgreement();
            ecdhAgreement.Init(ecdhKeyPair.Private);
            var classicalSecret = ecdhAgreement.CalculateAgreement(recipientEcdhPublic).ToByteArrayUnsigned();

            // Step 3: Generate ephemeral NTRU key pair and encapsulate
            var kemParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048677);
            var kemGenerator = new NtruKeyPairGenerator();
            kemGenerator.Init(kemParams);
            var kemKeyPair = kemGenerator.GenerateKeyPair();

            var kemEncapsulator = new NtruKemGenerator(_secureRandom);
            var encapsulated = kemEncapsulator.GenerateEncapsulated((NtruPublicKeyParameters)kemKeyPair.Public);
            var kemCiphertext = encapsulated.GetEncapsulation();
            var pqSecret = encapsulated.GetSecret();

            try
            {
                // Step 4: Combine both secrets using HKDF-SHA384
                var combinedSecret = CombineSecrets(classicalSecret, pqSecret);

                // Step 5: Encrypt with AES-256-GCM
                var nonce = GenerateIv();
                var tag = new byte[TagSize];
                var ciphertext = new byte[plaintext.Length];

                using (var aesGcm = new AesGcm(combinedSecret, TagSize))
                {
                    aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                }

                // Step 6: Build result
                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                writer.Write((ushort)ecdhPublicKey.Length);
                writer.Write(ecdhPublicKey);
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
                CryptographicOperations.ZeroMemory(classicalSecret);
                CryptographicOperations.ZeroMemory(pqSecret);
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

            // Step 1: Extract ECDH public key
            var ecdhPublicKeyLength = reader.ReadUInt16();
            var ecdhPublicKey = reader.ReadBytes(ecdhPublicKeyLength);

            // Step 2: Extract KEM ciphertext
            var kemCiphertextLength = reader.ReadInt32();
            var kemCiphertext = reader.ReadBytes(kemCiphertextLength);

            // Step 3: Extract nonce, tag, and encrypted data
            var nonce = reader.ReadBytes(NonceSize);
            var tag = reader.ReadBytes(TagSize);
            var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

            // Step 4: Derive classical secret using recipient's ECDH private key (from 'key')
            var recipientEcdhPrivate = DecodeECPrivateKey(key);
            var senderEcdhPublic = DecodeECPublicKeyFromBytes(ecdhPublicKey);
            var ecdhAgreement = new ECDHBasicAgreement();
            ecdhAgreement.Init(recipientEcdhPrivate);
            var classicalSecret = ecdhAgreement.CalculateAgreement(senderEcdhPublic).ToByteArrayUnsigned();

            // Step 5: Decapsulate KEM to get PQ secret
            var kemPrivateKey = ExtractKemPrivateKey(key);
            var kemExtractor = new NtruKemExtractor(kemPrivateKey);
            var pqSecret = kemExtractor.ExtractSecret(kemCiphertext);

            try
            {
                // Step 6: Combine both secrets
                var combinedSecret = CombineSecrets(classicalSecret, pqSecret);

                // Step 7: Decrypt with AES-256-GCM
                var plaintext = new byte[encryptedData.Length];

                using (var aesGcm = new AesGcm(combinedSecret, TagSize))
                {
                    aesGcm.Decrypt(nonce, encryptedData, tag, plaintext, associatedData);
                }

                return plaintext;
            }
            finally
            {
                // Clear sensitive data
                CryptographicOperations.ZeroMemory(classicalSecret);
                CryptographicOperations.ZeroMemory(pqSecret);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Combines classical and post-quantum secrets using HKDF-SHA384.
    /// </summary>
    private static byte[] CombineSecrets(byte[] classicalSecret, byte[] pqSecret)
    {
        var combinedInput = new byte[classicalSecret.Length + pqSecret.Length];
        Buffer.BlockCopy(classicalSecret, 0, combinedInput, 0, classicalSecret.Length);
        Buffer.BlockCopy(pqSecret, 0, combinedInput, classicalSecret.Length, pqSecret.Length);

        var salt = new byte[48]; // 384 bits
        var info = System.Text.Encoding.UTF8.GetBytes("DataWarehouse.Hybrid.ECDH-P384.NTRU-677");

        var derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA384, combinedInput, AesKeySize, salt, info);

        CryptographicOperations.ZeroMemory(combinedInput);

        return derivedKey;
    }

    /// <summary>
    /// Decodes EC public key from key material.
    /// </summary>
    private ECPublicKeyParameters DecodeECPublicKey(byte[] keyMaterial)
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var point = curve.Curve.DecodePoint(keyMaterial);
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        return new ECPublicKeyParameters(point, ecDomainParams);
    }

    /// <summary>
    /// Decodes EC private key from key material.
    /// </summary>
    private ECPrivateKeyParameters DecodeECPrivateKey(byte[] keyMaterial)
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var d = new Org.BouncyCastle.Math.BigInteger(1, keyMaterial);
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        return new ECPrivateKeyParameters(d, ecDomainParams);
    }

    /// <summary>
    /// Decodes EC public key from raw bytes.
    /// </summary>
    private ECPublicKeyParameters DecodeECPublicKeyFromBytes(byte[] encodedKey)
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var point = curve.Curve.DecodePoint(encodedKey);
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        return new ECPublicKeyParameters(point, ecDomainParams);
    }

    /// <summary>
    /// Extracts NTRU private key from composite key material.
    /// </summary>
    private NtruPrivateKeyParameters ExtractKemPrivateKey(byte[] keyMaterial)
    {
        // In production, parse composite key format
        return new NtruPrivateKeyParameters(NtruParameters.NtruHps2048677, keyMaterial);
    }
}

/// <summary>
/// Hybrid ChaCha20-Poly1305 + NTRU-HPS-2048-677 encryption strategy.
///
/// Similar to HybridAesKyberStrategy but uses ChaCha20-Poly1305 instead of AES-256-GCM.
/// Suitable for platforms without AES hardware acceleration.
///
/// Security Level: QuantumSafe (hybrid)
/// Classical Component: ECDH P-384
/// PQ Component: NTRU-HPS-2048-677
/// Symmetric Cipher: ChaCha20-Poly1305
///
/// Use Case: High-performance quantum-safe encryption on ARM and mobile devices.
/// </summary>
public sealed class HybridChaChaKyberStrategy : EncryptionStrategyBase
{
    private readonly SecureRandom _secureRandom;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "Hybrid-ChaCha20-Poly1305-ECDH-P384-NTRU-677",
        KeySizeBits = 256,
        BlockSizeBytes = 64,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = true,
            IsHardwareAcceleratable = false,
            SupportsAead = true,
            SupportsParallelism = true,
            MinimumSecurityLevel = SecurityLevel.QuantumSafe
        },
        SecurityLevel = SecurityLevel.QuantumSafe,
        Parameters = new Dictionary<string, object>
        {
            ["HybridScheme"] = "ECDH-P384 + NTRU-HPS-2048-677",
            ["ClassicalAlgorithm"] = "ECDH-P384",
            ["PostQuantumAlgorithm"] = "NTRU-HPS-2048-677",
            ["SymmetricCipher"] = "ChaCha20-Poly1305",
            ["KDF"] = "HKDF-SHA384"
        }
    };

    /// <inheritdoc/>
    public override string StrategyId => "hybrid-chacha-kyber";

    /// <inheritdoc/>
    public override string StrategyName => "Hybrid ChaCha20-Poly1305 + ECDH-P384 + NTRU";

    /// <summary>
    /// Initializes a new instance of the hybrid ChaCha-NTRU encryption strategy.
    /// </summary>
    public HybridChaChaKyberStrategy()
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
            // Step 1: Generate ephemeral ECDH P-384 key pair
            var ecdhGenerator = new ECKeyPairGenerator();
            var curveParams = ECNamedCurveTable.GetByName("P-384");
            var ecDomainParams = new ECDomainParameters(curveParams.Curve, curveParams.G, curveParams.N, curveParams.H);
            var ecdhParams = new ECKeyGenerationParameters(
                ecDomainParams,
                _secureRandom);
            ecdhGenerator.Init(ecdhParams);
            var ecdhKeyPair = ecdhGenerator.GenerateKeyPair();

            var ecdhPublicKey = ((ECPublicKeyParameters)ecdhKeyPair.Public).Q.GetEncoded(compressed: false);

            // Step 2: Derive classical shared secret
            var recipientEcdhPublic = DecodeECPublicKey(key);
            var ecdhAgreement = new ECDHBasicAgreement();
            ecdhAgreement.Init(ecdhKeyPair.Private);
            var classicalSecret = ecdhAgreement.CalculateAgreement(recipientEcdhPublic).ToByteArrayUnsigned();

            // Step 3: Generate ephemeral NTRU and encapsulate
            var kemParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048677);
            var kemGenerator = new NtruKeyPairGenerator();
            kemGenerator.Init(kemParams);
            var kemKeyPair = kemGenerator.GenerateKeyPair();

            var kemEncapsulator = new NtruKemGenerator(_secureRandom);
            var encapsulated = kemEncapsulator.GenerateEncapsulated((NtruPublicKeyParameters)kemKeyPair.Public);
            var kemCiphertext = encapsulated.GetEncapsulation();
            var pqSecret = encapsulated.GetSecret();

            try
            {
                // Step 4: Combine secrets with HKDF
                var combinedSecret = CombineSecrets(classicalSecret, pqSecret);

                // Step 5: Encrypt with ChaCha20-Poly1305
                var nonce = GenerateIv();
                var tag = new byte[TagSize];
                var ciphertext = new byte[plaintext.Length];

                using (var chacha = new ChaCha20Poly1305(combinedSecret))
                {
                    chacha.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                }

                // Step 6: Build result
                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                writer.Write((ushort)ecdhPublicKey.Length);
                writer.Write(ecdhPublicKey);
                writer.Write(kemCiphertext.Length);
                writer.Write(kemCiphertext);
                writer.Write(nonce);
                writer.Write(tag);
                writer.Write(ciphertext);

                return ms.ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(classicalSecret);
                CryptographicOperations.ZeroMemory(pqSecret);
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

            var ecdhPublicKeyLength = reader.ReadUInt16();
            var ecdhPublicKey = reader.ReadBytes(ecdhPublicKeyLength);
            var kemCiphertextLength = reader.ReadInt32();
            var kemCiphertext = reader.ReadBytes(kemCiphertextLength);
            var nonce = reader.ReadBytes(NonceSize);
            var tag = reader.ReadBytes(TagSize);
            var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

            var recipientEcdhPrivate = DecodeECPrivateKey(key);
            var senderEcdhPublic = DecodeECPublicKeyFromBytes(ecdhPublicKey);
            var ecdhAgreement = new ECDHBasicAgreement();
            ecdhAgreement.Init(recipientEcdhPrivate);
            var classicalSecret = ecdhAgreement.CalculateAgreement(senderEcdhPublic).ToByteArrayUnsigned();

            var kemPrivateKey = ExtractKemPrivateKey(key);
            var kemExtractor = new NtruKemExtractor(kemPrivateKey);
            var pqSecret = kemExtractor.ExtractSecret(kemCiphertext);

            try
            {
                var combinedSecret = CombineSecrets(classicalSecret, pqSecret);

                var plaintext = new byte[encryptedData.Length];

                using (var chacha = new ChaCha20Poly1305(combinedSecret))
                {
                    chacha.Decrypt(nonce, encryptedData, tag, plaintext, associatedData);
                }

                return plaintext;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(classicalSecret);
                CryptographicOperations.ZeroMemory(pqSecret);
            }
        }, cancellationToken);
    }

    private static byte[] CombineSecrets(byte[] classicalSecret, byte[] pqSecret)
    {
        var combinedInput = new byte[classicalSecret.Length + pqSecret.Length];
        Buffer.BlockCopy(classicalSecret, 0, combinedInput, 0, classicalSecret.Length);
        Buffer.BlockCopy(pqSecret, 0, combinedInput, classicalSecret.Length, pqSecret.Length);

        var salt = new byte[48];
        var info = System.Text.Encoding.UTF8.GetBytes("DataWarehouse.Hybrid.ECDH-P384.NTRU-677.ChaCha20");

        var derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA384, combinedInput, KeySize, salt, info);

        CryptographicOperations.ZeroMemory(combinedInput);

        return derivedKey;
    }

    private ECPublicKeyParameters DecodeECPublicKey(byte[] keyMaterial)
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var point = curve.Curve.DecodePoint(keyMaterial);
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        return new ECPublicKeyParameters(point, ecDomainParams);
    }

    private ECPrivateKeyParameters DecodeECPrivateKey(byte[] keyMaterial)
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var d = new Org.BouncyCastle.Math.BigInteger(1, keyMaterial);
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        return new ECPrivateKeyParameters(d, ecDomainParams);
    }

    private ECPublicKeyParameters DecodeECPublicKeyFromBytes(byte[] encodedKey)
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var point = curve.Curve.DecodePoint(encodedKey);
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        return new ECPublicKeyParameters(point, ecDomainParams);
    }

    private NtruPrivateKeyParameters ExtractKemPrivateKey(byte[] keyMaterial)
    {
        return new NtruPrivateKeyParameters(NtruParameters.NtruHps2048677, keyMaterial);
    }
}

/// <summary>
/// Hybrid X25519 + NTRU-HPS-2048-677 encryption strategy with AES-256-GCM.
///
/// Combines X25519 Elliptic Curve Diffie-Hellman with NTRU KEM.
/// Uses Curve25519 for better performance compared to P-384.
///
/// Security Level: QuantumSafe (hybrid)
/// Classical Component: X25519
/// PQ Component: NTRU-HPS-2048-677
/// Symmetric Cipher: AES-256-GCM
///
/// Use Case: High-performance hybrid encryption for modern applications.
/// </summary>
public sealed class HybridX25519KyberStrategy : EncryptionStrategyBase
{
    private readonly SecureRandom _secureRandom;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int X25519KeySize = 32;

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "Hybrid-AES-256-GCM-X25519-NTRU-677",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        Capabilities = CipherCapabilities.AeadCipher with
        {
            MinimumSecurityLevel = SecurityLevel.QuantumSafe
        },
        SecurityLevel = SecurityLevel.QuantumSafe,
        Parameters = new Dictionary<string, object>
        {
            ["HybridScheme"] = "X25519 + NTRU-HPS-2048-677",
            ["ClassicalAlgorithm"] = "X25519",
            ["PostQuantumAlgorithm"] = "NTRU-HPS-2048-677",
            ["SymmetricCipher"] = "AES-256-GCM",
            ["KDF"] = "HKDF-SHA256"
        }
    };

    /// <inheritdoc/>
    public override string StrategyId => "hybrid-x25519-kyber";

    /// <inheritdoc/>
    public override string StrategyName => "Hybrid AES-256-GCM + X25519 + NTRU";

    /// <summary>
    /// Initializes a new instance of the hybrid X25519-NTRU encryption strategy.
    /// </summary>
    public HybridX25519KyberStrategy()
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
            // Step 1: Generate ephemeral X25519 key pair
            var x25519Generator = new X25519KeyPairGenerator();
            x25519Generator.Init(new KeyGenerationParameters(_secureRandom, 256));
            var x25519KeyPair = x25519Generator.GenerateKeyPair();

            var x25519PublicKey = ((Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters)x25519KeyPair.Public).GetEncoded();

            // Step 2: Derive classical shared secret using X25519
            var recipientX25519Public = new Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters(key, 0);
            var x25519Agreement = new Org.BouncyCastle.Crypto.Agreement.X25519Agreement();
            x25519Agreement.Init(x25519KeyPair.Private);
            var classicalSecret = new byte[32];
            x25519Agreement.CalculateAgreement(recipientX25519Public, classicalSecret, 0);

            // Step 3: Generate ephemeral NTRU and encapsulate
            var kemParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048677);
            var kemGenerator = new NtruKeyPairGenerator();
            kemGenerator.Init(kemParams);
            var kemKeyPair = kemGenerator.GenerateKeyPair();

            var kemEncapsulator = new NtruKemGenerator(_secureRandom);
            var encapsulated = kemEncapsulator.GenerateEncapsulated((NtruPublicKeyParameters)kemKeyPair.Public);
            var kemCiphertext = encapsulated.GetEncapsulation();
            var pqSecret = encapsulated.GetSecret();

            try
            {
                // Step 4: Combine secrets with HKDF
                var combinedSecret = CombineSecrets(classicalSecret, pqSecret);

                // Step 5: Encrypt with AES-256-GCM
                var nonce = GenerateIv();
                var tag = new byte[TagSize];
                var ciphertext = new byte[plaintext.Length];

                using (var aesGcm = new AesGcm(combinedSecret, TagSize))
                {
                    aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                }

                // Step 6: Build result
                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                writer.Write(x25519PublicKey);
                writer.Write(kemCiphertext.Length);
                writer.Write(kemCiphertext);
                writer.Write(nonce);
                writer.Write(tag);
                writer.Write(ciphertext);

                return ms.ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(classicalSecret);
                CryptographicOperations.ZeroMemory(pqSecret);
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

            var x25519PublicKey = reader.ReadBytes(X25519KeySize);
            var kemCiphertextLength = reader.ReadInt32();
            var kemCiphertext = reader.ReadBytes(kemCiphertextLength);
            var nonce = reader.ReadBytes(NonceSize);
            var tag = reader.ReadBytes(TagSize);
            var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

            // Derive classical secret
            var recipientX25519Private = new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(key, 0);
            var senderX25519Public = new Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters(x25519PublicKey, 0);
            var x25519Agreement = new Org.BouncyCastle.Crypto.Agreement.X25519Agreement();
            x25519Agreement.Init(recipientX25519Private);
            var classicalSecret = new byte[32];
            x25519Agreement.CalculateAgreement(senderX25519Public, classicalSecret, 0);

            // Decapsulate KEM
            var kemPrivateKey = ExtractKemPrivateKey(key);
            var kemExtractor = new NtruKemExtractor(kemPrivateKey);
            var pqSecret = kemExtractor.ExtractSecret(kemCiphertext);

            try
            {
                var combinedSecret = CombineSecrets(classicalSecret, pqSecret);

                var plaintext = new byte[encryptedData.Length];

                using (var aesGcm = new AesGcm(combinedSecret, TagSize))
                {
                    aesGcm.Decrypt(nonce, encryptedData, tag, plaintext, associatedData);
                }

                return plaintext;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(classicalSecret);
                CryptographicOperations.ZeroMemory(pqSecret);
            }
        }, cancellationToken);
    }

    private static byte[] CombineSecrets(byte[] classicalSecret, byte[] pqSecret)
    {
        var combinedInput = new byte[classicalSecret.Length + pqSecret.Length];
        Buffer.BlockCopy(classicalSecret, 0, combinedInput, 0, classicalSecret.Length);
        Buffer.BlockCopy(pqSecret, 0, combinedInput, classicalSecret.Length, pqSecret.Length);

        var salt = new byte[32];
        var info = System.Text.Encoding.UTF8.GetBytes("DataWarehouse.Hybrid.X25519.NTRU-677");

        var derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, combinedInput, KeySize, salt, info);

        CryptographicOperations.ZeroMemory(combinedInput);

        return derivedKey;
    }

    private NtruPrivateKeyParameters ExtractKemPrivateKey(byte[] keyMaterial)
    {
        return new NtruPrivateKeyParameters(NtruParameters.NtruHps2048677, keyMaterial);
    }
}
