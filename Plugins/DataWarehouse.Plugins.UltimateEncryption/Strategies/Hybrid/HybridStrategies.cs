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
/// PQ Component: NTRU-HPS-2048-677 (NIST Level 3, aligns with FIPS 203 ML-KEM transition path)
/// Symmetric Cipher: AES-256-GCM
///
/// Use Case: Maximum security for long-term data protection against both classical and quantum threats.
///
/// Migration: Consider migrating to <see cref="X25519Kyber768Strategy"/> (hybrid-x25519-kyber768)
/// for TLS-compatible wire format and standardized FIPS 203 alignment.
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
            ["KDF"] = "HKDF-SHA384",
            ["FipsReference"] = "FIPS 203 (PQ component)",
            ["MigrationTarget"] = "hybrid-x25519-kyber768"
        }
    };

    /// <inheritdoc/>
    public override string StrategyId => "hybrid-aes-kyber";

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("hybrid.aes.kyber.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("hybrid.aes.kyber.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

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

            // Step 2: Parse composite public key: [ECDH Public Length:2][ECDH Public][NTRU Public Length:4][NTRU Public]
            if (key.Length < 2)
                throw new ArgumentException(
                    "Key must be a composite public key. Use GenerateCompositeKeyPair() to obtain the correct public key.", nameof(key));
            var ecdhPublicKeyLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(0, 2));
            if (key.Length < 2 + ecdhPublicKeyLen + 4)
                throw new ArgumentException("Composite public key is truncated (ECDH section).", nameof(key));
            var ecdhPublicKeyBytes = new byte[ecdhPublicKeyLen];
            Buffer.BlockCopy(key, 2, ecdhPublicKeyBytes, 0, ecdhPublicKeyLen);
            var ntruPublicKeyLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(key.AsSpan(2 + ecdhPublicKeyLen, 4));
            if (key.Length < 2 + ecdhPublicKeyLen + 4 + ntruPublicKeyLen)
                throw new ArgumentException("Composite public key is truncated (NTRU section).", nameof(key));
            var ntruPublicKeyBytes = new byte[ntruPublicKeyLen];
            Buffer.BlockCopy(key, 2 + ecdhPublicKeyLen + 4, ntruPublicKeyBytes, 0, ntruPublicKeyLen);

            // Derive classical shared secret using ECDH (with recipient's ECDH public key)
            var recipientEcdhPublic = DecodeECPublicKeyFromBytes(ecdhPublicKeyBytes);
            var ecdhAgreement = new ECDHBasicAgreement();
            ecdhAgreement.Init(ecdhKeyPair.Private);
            var classicalSecret = ecdhAgreement.CalculateAgreement(recipientEcdhPublic).ToByteArrayUnsigned();

            // Step 3: Encapsulate against recipient's NTRU public key
            var recipientNtruPublic = NtruPublicKeyParameters.FromEncoding(NtruParameters.NtruHps2048677, ntruPublicKeyBytes);
            var kemEncapsulator = new NtruKemGenerator(_secureRandom);
            var encapsulated = kemEncapsulator.GenerateEncapsulated(recipientNtruPublic);
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

            // Step 4: Parse composite private key: [ECDH Private Length:2][ECDH Private][NTRU Private:rest]
            if (key.Length < 2)
                throw new CryptographicException("Composite private key is too short.");
            var ecdhPrivKeyLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(0, 2));
            if (key.Length < 2 + ecdhPrivKeyLen)
                throw new CryptographicException("Composite private key is truncated (ECDH section).");
            var ecdhPrivKeyBytes = new byte[ecdhPrivKeyLen];
            Buffer.BlockCopy(key, 2, ecdhPrivKeyBytes, 0, ecdhPrivKeyLen);
            var ntruPrivKeyBytes = new byte[key.Length - 2 - ecdhPrivKeyLen];
            Buffer.BlockCopy(key, 2 + ecdhPrivKeyLen, ntruPrivKeyBytes, 0, ntruPrivKeyBytes.Length);

            // Derive classical secret using recipient's ECDH private key
            var recipientEcdhPrivate = DecodeECPrivateKey(ecdhPrivKeyBytes);
            var senderEcdhPublic = DecodeECPublicKeyFromBytes(ecdhPublicKey);
            var ecdhAgreement = new ECDHBasicAgreement();
            ecdhAgreement.Init(recipientEcdhPrivate);
            var classicalSecret = ecdhAgreement.CalculateAgreement(senderEcdhPublic).ToByteArrayUnsigned();

            // Step 5: Decapsulate KEM to get PQ secret using recipient's NTRU private key
            var kemPrivateKey = NtruPrivateKeyParameters.FromEncoding(NtruParameters.NtruHps2048677, ntruPrivKeyBytes);
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

        var info = System.Text.Encoding.UTF8.GetBytes("DataWarehouse.Hybrid.ECDH-P384.NTRU-677");
        // Use SHA-384 of the protocol info label as a fixed, meaningful domain-separation
        // salt rather than all-zero bytes. An all-zero salt of the same length as the hash
        // is equivalent to HKDF's documented default (RFC 5869 §2.2), providing no domain
        // separation. The hash of the info label creates a non-trivial, deterministic, and
        // publicly known salt appropriate for a hybrid KEM context.
        var salt = System.Security.Cryptography.SHA384.HashData(info);

        var derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA384, combinedInput, AesKeySize, salt, info);

        CryptographicOperations.ZeroMemory(combinedInput);

        return derivedKey;
    }

    /// <summary>
    /// Generates a composite key pair for AES-ECDH-NTRU hybrid operations.
    /// Returns (compositePublicKey, compositePrivateKey) where:
    /// - compositePublicKey: [ECDH Public Length:2][ECDH Public][NTRU Public Length:4][NTRU Public]
    ///   (used as the 'key' parameter in EncryptAsync)
    /// - compositePrivateKey: [ECDH Private Length:2][ECDH Private][NTRU Private:rest]
    ///   (used as the 'key' parameter in DecryptAsync)
    /// </summary>
    public (byte[] PublicKey, byte[] CompositePrivateKey) GenerateCompositeKeyPair()
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        var ecdhGenerator = new ECKeyPairGenerator();
        ecdhGenerator.Init(new ECKeyGenerationParameters(ecDomainParams, _secureRandom));
        var ecdhKeyPair = ecdhGenerator.GenerateKeyPair();

        var ecdhPublicBytes = ((ECPublicKeyParameters)ecdhKeyPair.Public).Q.GetEncoded(compressed: false);
        var ecdhPrivateBytes = ((ECPrivateKeyParameters)ecdhKeyPair.Private).D.ToByteArrayUnsigned();

        var kemParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048677);
        var kemGenerator = new NtruKeyPairGenerator();
        kemGenerator.Init(kemParams);
        var kemKeyPair = kemGenerator.GenerateKeyPair();

        var ntruPublicBytes = ((NtruPublicKeyParameters)kemKeyPair.Public).GetEncoded();
        var ntruPrivateBytes = ((NtruPrivateKeyParameters)kemKeyPair.Private).GetEncoded();

        // Build composite public key: [ECDH Public Length:2][ECDH Public][NTRU Public Length:4][NTRU Public]
        var pubLenBytes = new byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(pubLenBytes, (ushort)ecdhPublicBytes.Length);
        var ntruPubLenBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ntruPubLenBytes, ntruPublicBytes.Length);
        var compositePublicKey = new byte[2 + ecdhPublicBytes.Length + 4 + ntruPublicBytes.Length];
        int pos = 0;
        Buffer.BlockCopy(pubLenBytes, 0, compositePublicKey, pos, 2); pos += 2;
        Buffer.BlockCopy(ecdhPublicBytes, 0, compositePublicKey, pos, ecdhPublicBytes.Length); pos += ecdhPublicBytes.Length;
        Buffer.BlockCopy(ntruPubLenBytes, 0, compositePublicKey, pos, 4); pos += 4;
        Buffer.BlockCopy(ntruPublicBytes, 0, compositePublicKey, pos, ntruPublicBytes.Length);

        // Build composite private key: [ECDH Private Length:2][ECDH Private][NTRU Private:rest]
        var privLenBytes = new byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(privLenBytes, (ushort)ecdhPrivateBytes.Length);
        var compositePrivateKey = new byte[2 + ecdhPrivateBytes.Length + ntruPrivateBytes.Length];
        int ppos = 0;
        Buffer.BlockCopy(privLenBytes, 0, compositePrivateKey, ppos, 2); ppos += 2;
        Buffer.BlockCopy(ecdhPrivateBytes, 0, compositePrivateKey, ppos, ecdhPrivateBytes.Length); ppos += ecdhPrivateBytes.Length;
        Buffer.BlockCopy(ntruPrivateBytes, 0, compositePrivateKey, ppos, ntruPrivateBytes.Length);

        return (compositePublicKey, compositePrivateKey);
    }

    /// <summary>
    /// Decodes EC private key from raw big-endian bytes.
    /// </summary>
    private static ECPrivateKeyParameters DecodeECPrivateKey(byte[] keyMaterial)
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var d = new Org.BouncyCastle.Math.BigInteger(1, keyMaterial);
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        return new ECPrivateKeyParameters(d, ecDomainParams);
    }

    /// <summary>
    /// Decodes EC public key from raw encoded bytes.
    /// </summary>
    private static ECPublicKeyParameters DecodeECPublicKeyFromBytes(byte[] encodedKey)
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var point = curve.Curve.DecodePoint(encodedKey);
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        return new ECPublicKeyParameters(point, ecDomainParams);
    }
}

/// <summary>
/// Hybrid ChaCha20-Poly1305 + NTRU-HPS-2048-677 encryption strategy.
///
/// Similar to HybridAesKyberStrategy but uses ChaCha20-Poly1305 instead of AES-256-GCM.
/// Suitable for platforms without AES hardware acceleration (AES-NI).
///
/// Security Level: QuantumSafe (hybrid)
/// Classical Component: ECDH P-384
/// PQ Component: NTRU-HPS-2048-677 (aligns with FIPS 203 ML-KEM transition path)
/// Symmetric Cipher: ChaCha20-Poly1305
///
/// Use Case: High-performance quantum-safe encryption on ARM and mobile devices
/// where AES-NI hardware acceleration is unavailable.
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
            ["KDF"] = "HKDF-SHA384",
            ["FipsReference"] = "FIPS 203 (PQ component)",
            ["Advantage"] = "No AES-NI required, ARM/mobile optimized"
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

            // Step 2: Parse composite public key: [ECDH Public Length:2][ECDH Public][NTRU Public Length:4][NTRU Public]
            if (key.Length < 2)
                throw new ArgumentException(
                    "Key must be a composite public key. Use GenerateCompositeKeyPair() to obtain the correct public key.", nameof(key));
            var ecdhPublicKeyLen2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(0, 2));
            if (key.Length < 2 + ecdhPublicKeyLen2 + 4)
                throw new ArgumentException("Composite public key is truncated (ECDH section).", nameof(key));
            var ecdhPublicKeyBytes2 = new byte[ecdhPublicKeyLen2];
            Buffer.BlockCopy(key, 2, ecdhPublicKeyBytes2, 0, ecdhPublicKeyLen2);
            var ntruPublicKeyLen2 = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(key.AsSpan(2 + ecdhPublicKeyLen2, 4));
            if (key.Length < 2 + ecdhPublicKeyLen2 + 4 + ntruPublicKeyLen2)
                throw new ArgumentException("Composite public key is truncated (NTRU section).", nameof(key));
            var ntruPublicKeyBytes2 = new byte[ntruPublicKeyLen2];
            Buffer.BlockCopy(key, 2 + ecdhPublicKeyLen2 + 4, ntruPublicKeyBytes2, 0, ntruPublicKeyLen2);

            // Derive classical shared secret using recipient's ECDH public key
            var recipientEcdhPublic = DecodeECPublicKeyFromBytes(ecdhPublicKeyBytes2);
            var ecdhAgreement = new ECDHBasicAgreement();
            ecdhAgreement.Init(ecdhKeyPair.Private);
            var classicalSecret = ecdhAgreement.CalculateAgreement(recipientEcdhPublic).ToByteArrayUnsigned();

            // Step 3: Encapsulate against recipient's NTRU public key
            var recipientNtruPublic2 = NtruPublicKeyParameters.FromEncoding(NtruParameters.NtruHps2048677, ntruPublicKeyBytes2);
            var kemEncapsulator = new NtruKemGenerator(_secureRandom);
            var encapsulated = kemEncapsulator.GenerateEncapsulated(recipientNtruPublic2);
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

            // Parse composite private key: [ECDH Private Length:2][ECDH Private][NTRU Private:rest]
            if (key.Length < 2)
                throw new CryptographicException("Composite private key is too short.");
            var ecdhPrivKeyLen2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(key.AsSpan(0, 2));
            if (key.Length < 2 + ecdhPrivKeyLen2)
                throw new CryptographicException("Composite private key is truncated (ECDH section).");
            var ecdhPrivKeyBytes2 = new byte[ecdhPrivKeyLen2];
            Buffer.BlockCopy(key, 2, ecdhPrivKeyBytes2, 0, ecdhPrivKeyLen2);
            var ntruPrivKeyBytes2 = new byte[key.Length - 2 - ecdhPrivKeyLen2];
            Buffer.BlockCopy(key, 2 + ecdhPrivKeyLen2, ntruPrivKeyBytes2, 0, ntruPrivKeyBytes2.Length);

            var recipientEcdhPrivate = DecodeECPrivateKey(ecdhPrivKeyBytes2);
            var senderEcdhPublic = DecodeECPublicKeyFromBytes(ecdhPublicKey);
            var ecdhAgreement = new ECDHBasicAgreement();
            ecdhAgreement.Init(recipientEcdhPrivate);
            var classicalSecret = ecdhAgreement.CalculateAgreement(senderEcdhPublic).ToByteArrayUnsigned();

            var kemPrivateKey = NtruPrivateKeyParameters.FromEncoding(NtruParameters.NtruHps2048677, ntruPrivKeyBytes2);
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

        var info = System.Text.Encoding.UTF8.GetBytes("DataWarehouse.Hybrid.ECDH-P384.NTRU-677.ChaCha20");
        // Use SHA-384 of the info label as a non-trivial domain-separation salt instead of
        // all-zero bytes (finding #2938). All-zero salt provides no domain separation benefit.
        var salt = System.Security.Cryptography.SHA384.HashData(info);

        var derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA384, combinedInput, KeySize, salt, info);

        CryptographicOperations.ZeroMemory(combinedInput);

        return derivedKey;
    }

    /// <summary>
    /// Generates a composite key pair for ChaCha-ECDH-NTRU hybrid operations.
    /// Returns (compositePublicKey, compositePrivateKey) using the same format as
    /// <see cref="HybridAesKyberStrategy.GenerateCompositeKeyPair"/>.
    /// </summary>
    public (byte[] PublicKey, byte[] CompositePrivateKey) GenerateCompositeKeyPair()
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        var ecdhGenerator = new ECKeyPairGenerator();
        ecdhGenerator.Init(new ECKeyGenerationParameters(ecDomainParams, _secureRandom));
        var ecdhKeyPair = ecdhGenerator.GenerateKeyPair();

        var ecdhPublicBytes = ((ECPublicKeyParameters)ecdhKeyPair.Public).Q.GetEncoded(compressed: false);
        var ecdhPrivateBytes = ((ECPrivateKeyParameters)ecdhKeyPair.Private).D.ToByteArrayUnsigned();

        var kemParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048677);
        var kemGenerator = new NtruKeyPairGenerator();
        kemGenerator.Init(kemParams);
        var kemKeyPair = kemGenerator.GenerateKeyPair();

        var ntruPublicBytes = ((NtruPublicKeyParameters)kemKeyPair.Public).GetEncoded();
        var ntruPrivateBytes = ((NtruPrivateKeyParameters)kemKeyPair.Private).GetEncoded();

        var pubLenBytes = new byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(pubLenBytes, (ushort)ecdhPublicBytes.Length);
        var ntruPubLenBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ntruPubLenBytes, ntruPublicBytes.Length);
        var compositePublicKey = new byte[2 + ecdhPublicBytes.Length + 4 + ntruPublicBytes.Length];
        int pos = 0;
        Buffer.BlockCopy(pubLenBytes, 0, compositePublicKey, pos, 2); pos += 2;
        Buffer.BlockCopy(ecdhPublicBytes, 0, compositePublicKey, pos, ecdhPublicBytes.Length); pos += ecdhPublicBytes.Length;
        Buffer.BlockCopy(ntruPubLenBytes, 0, compositePublicKey, pos, 4); pos += 4;
        Buffer.BlockCopy(ntruPublicBytes, 0, compositePublicKey, pos, ntruPublicBytes.Length);

        var privLenBytes = new byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(privLenBytes, (ushort)ecdhPrivateBytes.Length);
        var compositePrivateKey = new byte[2 + ecdhPrivateBytes.Length + ntruPrivateBytes.Length];
        int ppos = 0;
        Buffer.BlockCopy(privLenBytes, 0, compositePrivateKey, ppos, 2); ppos += 2;
        Buffer.BlockCopy(ecdhPrivateBytes, 0, compositePrivateKey, ppos, ecdhPrivateBytes.Length); ppos += ecdhPrivateBytes.Length;
        Buffer.BlockCopy(ntruPrivateBytes, 0, compositePrivateKey, ppos, ntruPrivateBytes.Length);

        return (compositePublicKey, compositePrivateKey);
    }

    private static ECPrivateKeyParameters DecodeECPrivateKey(byte[] keyMaterial)
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var d = new Org.BouncyCastle.Math.BigInteger(1, keyMaterial);
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        return new ECPrivateKeyParameters(d, ecDomainParams);
    }

    private static ECPublicKeyParameters DecodeECPublicKeyFromBytes(byte[] encodedKey)
    {
        var curve = ECNamedCurveTable.GetByName("P-384");
        var point = curve.Curve.DecodePoint(encodedKey);
        var ecDomainParams = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        return new ECPublicKeyParameters(point, ecDomainParams);
    }
}

/// <summary>
/// Hybrid X25519 + NTRU-HPS-2048-677 encryption strategy with AES-256-GCM.
///
/// Combines X25519 Elliptic Curve Diffie-Hellman with NTRU KEM.
/// Uses Curve25519 for better performance compared to P-384.
///
/// NOTE: This strategy uses NTRU-HPS-2048-677 as the PQ component, NOT standard
/// CRYSTALS-Kyber-768. For the dedicated TLS-compatible X25519+Kyber768 hybrid
/// with FIPS 203 alignment, use <see cref="X25519Kyber768Strategy"/> instead.
///
/// Security Level: QuantumSafe (hybrid)
/// Classical Component: X25519
/// PQ Component: NTRU-HPS-2048-677 (aligns with FIPS 203 ML-KEM transition path)
/// Symmetric Cipher: AES-256-GCM
///
/// Use Case: High-performance hybrid encryption for modern applications.
/// Migration target: hybrid-x25519-kyber768 (X25519Kyber768Strategy)
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
            ["KDF"] = "HKDF-SHA256",
            ["FipsReference"] = "FIPS 203 (PQ component)",
            ["TlsCompatible"] = false,
            ["MigrationTarget"] = "hybrid-x25519-kyber768"
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

            // Step 2: Parse composite public key: [X25519 Public:32][NTRU Public Length:4][NTRU Public:rest]
            if (key.Length < X25519KeySize + 4)
                throw new ArgumentException(
                    "Key must be a composite public key: [X25519 Public:32][NTRU Public Length:4][NTRU Public:rest]. " +
                    "Use GenerateCompositeKeyPair() to obtain the correct public key.", nameof(key));
            var ntruPubLen3 = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(key.AsSpan(X25519KeySize, 4));
            if (key.Length < X25519KeySize + 4 + ntruPubLen3)
                throw new ArgumentException("Composite public key is truncated (NTRU section).", nameof(key));
            var ntruPublicKeyBytes3 = new byte[ntruPubLen3];
            Buffer.BlockCopy(key, X25519KeySize + 4, ntruPublicKeyBytes3, 0, ntruPubLen3);

            // Derive classical shared secret using X25519 with recipient's X25519 public key
            var recipientX25519Public = new Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters(key, 0);
            var x25519Agreement = new Org.BouncyCastle.Crypto.Agreement.X25519Agreement();
            x25519Agreement.Init(x25519KeyPair.Private);
            var classicalSecret = new byte[32];
            x25519Agreement.CalculateAgreement(recipientX25519Public, classicalSecret, 0);

            // Step 3: Encapsulate against recipient's NTRU public key
            var recipientNtruPublic3 = NtruPublicKeyParameters.FromEncoding(NtruParameters.NtruHps2048677, ntruPublicKeyBytes3);
            var kemEncapsulator = new NtruKemGenerator(_secureRandom);
            var encapsulated = kemEncapsulator.GenerateEncapsulated(recipientNtruPublic3);
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

            // Parse composite private key: [X25519 Private:32][NTRU Private:rest]
            if (key.Length < X25519KeySize)
                throw new CryptographicException("Composite private key is too short.");
            var x25519PrivKeyBytes3 = new byte[X25519KeySize];
            Buffer.BlockCopy(key, 0, x25519PrivKeyBytes3, 0, X25519KeySize);
            var ntruPrivKeyBytes3 = new byte[key.Length - X25519KeySize];
            Buffer.BlockCopy(key, X25519KeySize, ntruPrivKeyBytes3, 0, ntruPrivKeyBytes3.Length);

            // Derive classical secret using recipient's X25519 private key
            var recipientX25519Private = new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(x25519PrivKeyBytes3, 0);
            var senderX25519Public = new Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters(x25519PublicKey, 0);
            var x25519Agreement = new Org.BouncyCastle.Crypto.Agreement.X25519Agreement();
            x25519Agreement.Init(recipientX25519Private);
            var classicalSecret = new byte[32];
            x25519Agreement.CalculateAgreement(senderX25519Public, classicalSecret, 0);

            // Decapsulate KEM using recipient's NTRU private key
            var kemPrivateKey = NtruPrivateKeyParameters.FromEncoding(NtruParameters.NtruHps2048677, ntruPrivKeyBytes3);
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

        var info = System.Text.Encoding.UTF8.GetBytes("DataWarehouse.Hybrid.X25519.NTRU-677");
        // Use SHA-256 of the info label as a non-trivial domain-separation salt instead of
        // all-zero bytes (finding #2938). All-zero salt provides no domain separation benefit.
        var salt = System.Security.Cryptography.SHA256.HashData(info);

        var derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, combinedInput, KeySize, salt, info);

        CryptographicOperations.ZeroMemory(combinedInput);

        return derivedKey;
    }

    /// <summary>
    /// Generates a composite key pair for X25519+NTRU hybrid operations.
    /// Returns (compositePublicKey, compositePrivateKey) where:
    /// - compositePublicKey: [X25519 Public:32][NTRU Public Length:4][NTRU Public:rest]
    ///   (used as the 'key' parameter in EncryptAsync)
    /// - compositePrivateKey: [X25519 Private:32][NTRU Private:rest]
    ///   (used as the 'key' parameter in DecryptAsync)
    /// </summary>
    public (byte[] PublicKey, byte[] CompositePrivateKey) GenerateCompositeKeyPair()
    {
        var x25519Generator = new X25519KeyPairGenerator();
        x25519Generator.Init(new KeyGenerationParameters(_secureRandom, 256));
        var x25519KeyPair = x25519Generator.GenerateKeyPair();

        var x25519PublicBytes = ((Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters)x25519KeyPair.Public).GetEncoded();
        var x25519PrivateBytes = ((Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters)x25519KeyPair.Private).GetEncoded();

        var kemParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048677);
        var kemGenerator = new NtruKeyPairGenerator();
        kemGenerator.Init(kemParams);
        var kemKeyPair = kemGenerator.GenerateKeyPair();

        var ntruPublicBytes = ((NtruPublicKeyParameters)kemKeyPair.Public).GetEncoded();
        var ntruPrivateBytes = ((NtruPrivateKeyParameters)kemKeyPair.Private).GetEncoded();

        var ntruPubLenBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ntruPubLenBytes, ntruPublicBytes.Length);
        var compositePublicKey = new byte[x25519PublicBytes.Length + 4 + ntruPublicBytes.Length];
        int pos = 0;
        Buffer.BlockCopy(x25519PublicBytes, 0, compositePublicKey, pos, x25519PublicBytes.Length); pos += x25519PublicBytes.Length;
        Buffer.BlockCopy(ntruPubLenBytes, 0, compositePublicKey, pos, 4); pos += 4;
        Buffer.BlockCopy(ntruPublicBytes, 0, compositePublicKey, pos, ntruPublicBytes.Length);

        var compositePrivateKey = new byte[x25519PrivateBytes.Length + ntruPrivateBytes.Length];
        Buffer.BlockCopy(x25519PrivateBytes, 0, compositePrivateKey, 0, x25519PrivateBytes.Length);
        Buffer.BlockCopy(ntruPrivateBytes, 0, compositePrivateKey, x25519PrivateBytes.Length, ntruPrivateBytes.Length);

        return (compositePublicKey, compositePrivateKey);
    }
}
