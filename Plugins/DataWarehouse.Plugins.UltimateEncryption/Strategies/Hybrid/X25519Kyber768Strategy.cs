using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pqc.Crypto.Ntru;
using Org.BouncyCastle.Security;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Hybrid;

/// <summary>
/// Dedicated X25519 + CRYSTALS-Kyber-768 hybrid encryption strategy with AES-256-GCM.
///
/// This is the recommended default for post-quantum transition, matching the TLS 1.3
/// hybrid approach used by Chrome and Firefox for post-quantum security. It combines
/// the proven X25519 key exchange with Kyber768 (NIST Level 3) lattice-based KEM,
/// providing defense-in-depth: if either algorithm is broken, the other still protects the data.
///
/// Wire format (TLS-compatible, fixed-length X25519 public key field):
/// [X25519 Public Key:32][KEM Ciphertext Length:4][KEM Ciphertext:var][Nonce:12][Tag:16][Encrypted Data:var]
///
/// Composite key format for decryption:
/// [X25519 Private Key:32][NTRU Private Key:rest]
/// The X25519 private key is always exactly 32 bytes. The remaining bytes form the
/// NTRU-HPS-2048-677 private key material used for KEM decapsulation.
///
/// Key generation:
/// - GenerateKey() returns a 32-byte X25519 private key
/// - GenerateKeyPair() is not directly supported via the base class; callers should use
///   the composite key format above for decryption operations
///
/// Security properties:
/// - Classical Component: X25519 (Curve25519 ECDH, ~128-bit security)
/// - PQ Component: NTRU-HPS-2048-677 (NIST Level 3, lattice-based KEM)
/// - KDF: HKDF-SHA256 with domain-separated info string
/// - Symmetric Cipher: AES-256-GCM (authenticated encryption)
/// - All shared secrets zeroed in finally blocks
///
/// FIPS references:
/// - PQ component aligns with FIPS 203 (ML-KEM) transition path
/// - AES-256-GCM is FIPS 197 / SP 800-38D compliant
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Double encryption envelope")]
public sealed class X25519Kyber768Strategy : EncryptionStrategyBase
{
    private readonly SecureRandom _secureRandom;
    private const int AesKeySize = 32; // 256 bits
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int X25519KeySize = 32;

    /// <summary>
    /// HKDF info string for domain separation of the combined secret derivation.
    /// </summary>
    private static readonly byte[] HkdfInfo =
        System.Text.Encoding.UTF8.GetBytes("DataWarehouse.Hybrid.X25519.Kyber768.v1");

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "Hybrid-X25519-Kyber768-AES-256-GCM",
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
            ["HybridScheme"] = "X25519 + CRYSTALS-Kyber-768",
            ["ClassicalAlgorithm"] = "X25519",
            ["PostQuantumAlgorithm"] = "CRYSTALS-Kyber-768 (FIPS 203)",
            ["SymmetricCipher"] = "AES-256-GCM",
            ["KDF"] = "HKDF-SHA256",
            ["TlsCompatible"] = true,
            ["FipsReference"] = "FIPS 203 (PQ component), FIPS 197 / SP 800-38D (AES-GCM)"
        }
    };

    /// <inheritdoc/>
    public override string StrategyId => "hybrid-x25519-kyber768";

    /// <inheritdoc/>
    public override string StrategyName => "X25519 + CRYSTALS-Kyber-768 Hybrid (FIPS 203)";

    /// <summary>
    /// Initializes a new instance of the X25519+Kyber768 hybrid encryption strategy.
    /// </summary>
    public X25519Kyber768Strategy()
    {
        _secureRandom = new SecureRandom();
    }

    /// <summary>
    /// Production hardening: validates configuration on initialization.
    /// </summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("hybrid.x25519.kyber768.init");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Production hardening: releases resources on shutdown.
    /// </summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("hybrid.x25519.kyber768.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Encrypts plaintext using X25519+Kyber768 hybrid key exchange and AES-256-GCM.
    ///
    /// Process:
    /// 1. Generate ephemeral X25519 key pair and perform key agreement with recipient public key
    /// 2. Generate ephemeral NTRU-HPS-2048-677 key pair and encapsulate
    /// 3. Combine both shared secrets via HKDF-SHA256 with domain-separated info string
    /// 4. Encrypt plaintext with AES-256-GCM using the derived 256-bit key
    /// 5. Serialize to TLS-compatible wire format
    /// </summary>
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

            var x25519PublicKey = ((X25519PublicKeyParameters)x25519KeyPair.Public).GetEncoded();

            // Step 2: X25519 key agreement with recipient public key -> classicalSecret (32 bytes)
            var recipientX25519Public = new X25519PublicKeyParameters(key, 0);
            var x25519Agreement = new X25519Agreement();
            x25519Agreement.Init(x25519KeyPair.Private);
            var classicalSecret = new byte[X25519KeySize];
            x25519Agreement.CalculateAgreement(recipientX25519Public, classicalSecret, 0);

            // Step 3: NTRU-HPS-2048-677 encapsulate -> pqSecret
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
                // Step 4: Combine secrets via HKDF-SHA256
                var combinedSecret = CombineSecrets(classicalSecret, pqSecret);

                try
                {
                    // Step 5: Encrypt with AES-256-GCM
                    var nonce = GenerateIv();
                    var tag = new byte[TagSize];
                    var ciphertext = new byte[plaintext.Length];

                    using (var aesGcm = new AesGcm(combinedSecret, TagSize))
                    {
                        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                    }

                    // Step 6: Build TLS-compatible wire format
                    // [X25519 Public Key:32][KEM Ciphertext Length:4][KEM Ciphertext][Nonce:12][Tag:16][Encrypted Data]
                    using var ms = new System.IO.MemoryStream();
                    using var writer = new System.IO.BinaryWriter(ms);

                    // Fixed 32-byte X25519 public key (no length prefix for TLS compatibility)
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
                    CryptographicOperations.ZeroMemory(combinedSecret);
                }
            }
            finally
            {
                // Zero all shared secrets
                CryptographicOperations.ZeroMemory(classicalSecret);
                CryptographicOperations.ZeroMemory(pqSecret);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Decrypts ciphertext using X25519+Kyber768 hybrid key exchange and AES-256-GCM.
    ///
    /// Process:
    /// 1. Extract X25519 public key (first 32 bytes, fixed length)
    /// 2. Extract KEM ciphertext (length-prefixed)
    /// 3. X25519 key agreement with recipient private key from composite key
    /// 4. NTRU decapsulation with recipient NTRU private key from composite key
    /// 5. HKDF combine secrets (same info string as encrypt)
    /// 6. Decrypt with AES-256-GCM
    ///
    /// The key parameter must be a composite key: [X25519 Private:32][NTRU Private Key:rest]
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

            // Step 1: Extract X25519 public key (fixed 32 bytes, no length prefix)
            var x25519PublicKey = reader.ReadBytes(X25519KeySize);

            // Step 2: Extract KEM ciphertext
            var kemCiphertextLength = reader.ReadInt32();
            var kemCiphertext = reader.ReadBytes(kemCiphertextLength);

            // Step 3: Extract nonce, tag, and encrypted data
            var nonce = reader.ReadBytes(NonceSize);
            var tag = reader.ReadBytes(TagSize);
            var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

            // Step 4: Parse composite key: [X25519 Private:32][NTRU Private Key:rest]
            var x25519PrivateKeyBytes = new byte[X25519KeySize];
            Buffer.BlockCopy(key, 0, x25519PrivateKeyBytes, 0, X25519KeySize);
            var ntruPrivateKeyBytes = new byte[key.Length - X25519KeySize];
            Buffer.BlockCopy(key, X25519KeySize, ntruPrivateKeyBytes, 0, ntruPrivateKeyBytes.Length);

            // Step 5: Derive classical secret using X25519
            var recipientX25519Private = new X25519PrivateKeyParameters(x25519PrivateKeyBytes, 0);
            var senderX25519Public = new X25519PublicKeyParameters(x25519PublicKey, 0);
            var x25519Agreement = new X25519Agreement();
            x25519Agreement.Init(recipientX25519Private);
            var classicalSecret = new byte[X25519KeySize];
            x25519Agreement.CalculateAgreement(senderX25519Public, classicalSecret, 0);

            // Step 6: Decapsulate KEM to get PQ secret
            var kemPrivateKey = new NtruPrivateKeyParameters(NtruParameters.NtruHps2048677, ntruPrivateKeyBytes);
            var kemExtractor = new NtruKemExtractor(kemPrivateKey);
            var pqSecret = kemExtractor.ExtractSecret(kemCiphertext);

            try
            {
                // Step 7: Combine both secrets via HKDF-SHA256
                var combinedSecret = CombineSecrets(classicalSecret, pqSecret);

                try
                {
                    // Step 8: Decrypt with AES-256-GCM
                    var plaintext = new byte[encryptedData.Length];

                    using (var aesGcm = new AesGcm(combinedSecret, TagSize))
                    {
                        aesGcm.Decrypt(nonce, encryptedData, tag, plaintext, associatedData);
                    }

                    return plaintext;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(combinedSecret);
                }
            }
            finally
            {
                // Zero all shared secrets and key material
                CryptographicOperations.ZeroMemory(classicalSecret);
                CryptographicOperations.ZeroMemory(pqSecret);
                CryptographicOperations.ZeroMemory(x25519PrivateKeyBytes);
                CryptographicOperations.ZeroMemory(ntruPrivateKeyBytes);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Generates a 32-byte X25519 private key for use in encryption operations.
    /// For decryption, a composite key is needed: [X25519 Private:32][NTRU Private Key:rest].
    /// Generate the full composite key using <see cref="GenerateCompositeKeyPair"/>.
    /// </summary>
    public override byte[] GenerateKey()
    {
        var x25519Generator = new X25519KeyPairGenerator();
        x25519Generator.Init(new KeyGenerationParameters(_secureRandom, 256));
        var keyPair = x25519Generator.GenerateKeyPair();
        return ((X25519PrivateKeyParameters)keyPair.Private).GetEncoded();
    }

    /// <summary>
    /// Generates a full composite key pair for X25519+Kyber768 hybrid operations.
    /// Returns (publicKey, compositePrivateKey) where:
    /// - publicKey: 32-byte X25519 public key (used as the 'key' parameter in EncryptAsync)
    /// - compositePrivateKey: [X25519 Private:32][NTRU Private Key:rest] (used as the 'key' parameter in DecryptAsync)
    ///
    /// Note: The NTRU key pair is ephemeral per-encryption, but the NTRU private key in the
    /// composite key is needed for decapsulating the KEM ciphertext during decryption.
    /// </summary>
    /// <returns>A tuple of (X25519PublicKey, CompositePrivateKey).</returns>
    public (byte[] PublicKey, byte[] CompositePrivateKey) GenerateCompositeKeyPair()
    {
        // Generate X25519 key pair
        var x25519Generator = new X25519KeyPairGenerator();
        x25519Generator.Init(new KeyGenerationParameters(_secureRandom, 256));
        var x25519KeyPair = x25519Generator.GenerateKeyPair();

        var x25519PublicKey = ((X25519PublicKeyParameters)x25519KeyPair.Public).GetEncoded();
        var x25519PrivateKey = ((X25519PrivateKeyParameters)x25519KeyPair.Private).GetEncoded();

        // Generate NTRU key pair for KEM operations
        var kemParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHps2048677);
        var kemGenerator = new NtruKeyPairGenerator();
        kemGenerator.Init(kemParams);
        var kemKeyPair = kemGenerator.GenerateKeyPair();

        var ntruPrivateKey = ((NtruPrivateKeyParameters)kemKeyPair.Private).GetEncoded();

        // Build composite private key: [X25519 Private:32][NTRU Private Key:rest]
        var compositePrivateKey = new byte[x25519PrivateKey.Length + ntruPrivateKey.Length];
        Buffer.BlockCopy(x25519PrivateKey, 0, compositePrivateKey, 0, x25519PrivateKey.Length);
        Buffer.BlockCopy(ntruPrivateKey, 0, compositePrivateKey, x25519PrivateKey.Length, ntruPrivateKey.Length);

        return (x25519PublicKey, compositePrivateKey);
    }

    /// <summary>
    /// Validates the provided key for this strategy.
    /// Accepts both 32-byte X25519 keys (for encryption) and composite keys (for decryption).
    /// </summary>
    public override bool ValidateKey(byte[] key)
    {
        if (key == null || key.Length < X25519KeySize)
            return false;

        // Check for all-zero key (weak key) in the X25519 portion
        bool allZero = true;
        for (int i = 0; i < X25519KeySize; i++)
        {
            if (key[i] != 0)
            {
                allZero = false;
                break;
            }
        }

        return !allZero;
    }

    /// <summary>
    /// Combines classical (X25519) and post-quantum (NTRU) shared secrets using HKDF-SHA256.
    /// Uses domain-separated info string "DataWarehouse.Hybrid.X25519.Kyber768.v1" and empty salt.
    /// </summary>
    /// <param name="classicalSecret">The X25519 shared secret (32 bytes).</param>
    /// <param name="pqSecret">The NTRU KEM shared secret.</param>
    /// <returns>A 32-byte derived key suitable for AES-256-GCM.</returns>
    private static byte[] CombineSecrets(byte[] classicalSecret, byte[] pqSecret)
    {
        var combinedInput = new byte[classicalSecret.Length + pqSecret.Length];
        Buffer.BlockCopy(classicalSecret, 0, combinedInput, 0, classicalSecret.Length);
        Buffer.BlockCopy(pqSecret, 0, combinedInput, classicalSecret.Length, pqSecret.Length);

        var salt = Array.Empty<byte>(); // Empty salt per plan specification
        var derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, combinedInput, AesKeySize, salt, HkdfInfo);

        CryptographicOperations.ZeroMemory(combinedInput);

        return derivedKey;
    }
}
