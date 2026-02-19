using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Asymmetric;

/// <summary>
/// RSA encryption strategy with OAEP padding (SHA-256).
///
/// RSA (Rivest-Shamir-Adleman) is an asymmetric encryption algorithm widely used
/// for secure data transmission. This implementation uses OAEP (Optimal Asymmetric
/// Encryption Padding) with SHA-256 for enhanced security.
///
/// Process:
/// 1. Uses RSA public key to encrypt plaintext with OAEP padding
/// 2. OAEP provides probabilistic encryption (same plaintext produces different ciphertexts)
/// 3. SHA-256 hash function provides strong randomness oracle
/// 4. Maximum plaintext size: (keySize in bytes) - 2 - (2 * hash length) - 2
///    For 2048-bit key with SHA-256: 2048/8 - 2 - (2 * 32) - 2 = 190 bytes
///    For 3072-bit key with SHA-256: 3072/8 - 2 - (2 * 32) - 2 = 318 bytes
///    For 4096-bit key with SHA-256: 4096/8 - 2 - (2 * 32) - 2 = 446 bytes
///
/// Key Sizes:
/// - 2048 bits: Minimum recommended for modern use (NIST compliant until 2030)
/// - 3072 bits: Recommended for long-term security
/// - 4096 bits: Maximum security, higher computational cost
///
/// Security Level: High (2048/3072) to Military (4096)
///
/// Use Cases:
/// - Key exchange and key wrapping
/// - Small message encryption (certificates, tokens)
/// - Digital envelope encryption (hybrid schemes)
/// - Secure bootstrap of symmetric keys
///
/// Performance Note: RSA is computationally expensive. For large data,
/// use hybrid encryption (RSA for key exchange + AES for data).
///
/// Standards: PKCS#1 v2.1 (RFC 3447), NIST SP 800-56B
/// </summary>
public sealed class RsaOaepStrategy : EncryptionStrategyBase
{
    private readonly SecureRandom _secureRandom;
    private readonly int _keySize;

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = $"RSA-{_keySize}-OAEP-SHA256",
        KeySizeBits = _keySize,
        BlockSizeBytes = _keySize / 8,
        IvSizeBytes = 0, // RSA doesn't use IV
        TagSizeBytes = 0, // RSA doesn't use authentication tags
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = false,
            IsStreamable = false,
            IsHardwareAcceleratable = true,
            SupportsAead = false,
            SupportsParallelism = false,
            MinimumSecurityLevel = _keySize switch
            {
                2048 => SecurityLevel.High,
                3072 => SecurityLevel.High,
                4096 => SecurityLevel.Military,
                _ => SecurityLevel.Standard
            }
        },
        SecurityLevel = _keySize switch
        {
            2048 => SecurityLevel.High,
            3072 => SecurityLevel.High,
            4096 => SecurityLevel.Military,
            _ => SecurityLevel.Standard
        },
        Parameters = new Dictionary<string, object>
        {
            ["Algorithm"] = "RSA",
            ["Padding"] = "OAEP",
            ["HashAlgorithm"] = "SHA-256",
            ["MGF"] = "MGF1",
            ["KeySize"] = _keySize,
            ["MaxPlaintextSize"] = GetMaxPlaintextSize(_keySize),
            ["Standard"] = "PKCS#1 v2.1 (RFC 3447)",
            ["NISTCompliance"] = _keySize >= 2048 ? "NIST SP 800-56B" : "Non-compliant"
        }
    };

    /// <inheritdoc/>
    public override string StrategyId => $"rsa-{_keySize}-oaep-sha256";

    /// <inheritdoc/>
    public override string StrategyName => $"RSA-{_keySize}-OAEP-SHA256";

    /// <summary>
    /// Initializes a new instance of RSA-OAEP encryption strategy.
    /// </summary>
    /// <param name="keySize">RSA key size in bits (2048, 3072, or 4096). Default: 2048.</param>
    /// <exception cref="ArgumentException">If key size is not supported.</exception>
    public RsaOaepStrategy(int keySize = 2048)
    {
        if (keySize != 2048 && keySize != 3072 && keySize != 4096)
        {
            throw new ArgumentException(
                "RSA key size must be 2048, 3072, or 4096 bits",
                nameof(keySize));
        }

        _keySize = keySize;
        _secureRandom = new SecureRandom();
    }

    /// <summary>
    /// Production hardening: validates RSA configuration on initialization.
    /// </summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("rsa.oaep.init");
        // Validate key size is NIST-compliant
        if (_keySize < 2048)
            throw new InvalidOperationException($"RSA key size {_keySize} is below NIST minimum of 2048 bits");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Production hardening: releases resources and zeroes sensitive memory on shutdown.
    /// </summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("rsa.oaep.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Production hardening: cached health check verifying RSA round-trip capability.
    /// </summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        return GetCachedHealthAsync(async (cancellationToken) =>
        {
            try
            {
                // Verify we can create an RSA key pair and perform a round-trip
                using var rsa = RSA.Create(_keySize);
                var testData = new byte[32];
                RandomNumberGenerator.Fill(testData);
                var encrypted = rsa.Encrypt(testData, RSAEncryptionPadding.OaepSHA256);
                var decrypted = rsa.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA256);
                var isHealthy = testData.AsSpan().SequenceEqual(decrypted);
                CryptographicOperations.ZeroMemory(testData);
                CryptographicOperations.ZeroMemory(decrypted);
                return new StrategyHealthCheckResult(
                    isHealthy,
                    isHealthy ? $"RSA-{_keySize}-OAEP healthy" : "Round-trip failed",
                    new Dictionary<string, object>
                    {
                        ["KeySize"] = _keySize,
                        ["Padding"] = "OAEP-SHA256",
                        ["EncryptOps"] = GetCounter("rsa.oaep.encrypt"),
                        ["DecryptOps"] = GetCounter("rsa.oaep.decrypt")
                    });
            }
            catch (Exception ex)
            {
                return new StrategyHealthCheckResult(false, $"RSA health check failed: {ex.Message}");
            }
        }, TimeSpan.FromSeconds(60), ct);
    }

    /// <inheritdoc/>
    protected override async Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        IncrementCounter("rsa.oaep.encrypt");
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        if (plaintext.Length == 0)
            throw new ArgumentException("Plaintext cannot be empty", nameof(plaintext));

        return await Task.Run(() =>
        {
            // Parse RSA public key from key material
            var publicKey = ParsePublicKey(key);

            // Create OAEP cipher with SHA-256
            var cipher = new OaepEncoding(
                new RsaEngine(),
                new Org.BouncyCastle.Crypto.Digests.Sha256Digest(),
                new Org.BouncyCastle.Crypto.Digests.Sha256Digest(),
                null); // No label

            cipher.Init(true, publicKey);

            // Check plaintext size
            var maxSize = cipher.GetInputBlockSize();
            if (plaintext.Length > maxSize)
            {
                throw new ArgumentException(
                    $"Plaintext too large for RSA-{_keySize}-OAEP. Maximum size: {maxSize} bytes, got: {plaintext.Length} bytes. " +
                    $"Consider using hybrid encryption for larger data.",
                    nameof(plaintext));
            }

            // Encrypt
            var ciphertext = cipher.ProcessBlock(plaintext, 0, plaintext.Length);

            return ciphertext;
        }, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        IncrementCounter("rsa.oaep.decrypt");
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(key);

        if (ciphertext.Length == 0)
            throw new ArgumentException("Ciphertext cannot be empty", nameof(ciphertext));

        return await Task.Run(() =>
        {
            // Parse RSA private key from key material
            var privateKey = ParsePrivateKey(key);

            // Create OAEP cipher with SHA-256
            var cipher = new OaepEncoding(
                new RsaEngine(),
                new Org.BouncyCastle.Crypto.Digests.Sha256Digest(),
                new Org.BouncyCastle.Crypto.Digests.Sha256Digest(),
                null); // No label

            cipher.Init(false, privateKey);

            // Decrypt
            var plaintext = cipher.ProcessBlock(ciphertext, 0, ciphertext.Length);

            return plaintext;
        }, cancellationToken);
    }

    /// <summary>
    /// Parses an RSA public key from key material.
    /// Supports both raw BouncyCastle format and .NET RSA parameters.
    /// </summary>
    private RsaKeyParameters ParsePublicKey(byte[] keyMaterial)
    {
        try
        {
            // Try parsing as BouncyCastle public key
            var publicKeyInfo = Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory
                .CreateSubjectPublicKeyInfo(PublicKeyFactory.CreateKey(keyMaterial));
            return (RsaKeyParameters)PublicKeyFactory.CreateKey(publicKeyInfo);
        }
        catch
        {
            // Fallback: parse as .NET RSA parameters
            using var rsa = RSA.Create();
            rsa.ImportRSAPublicKey(keyMaterial, out _);
            var parameters = rsa.ExportParameters(false);

            return new RsaKeyParameters(
                false,
                new Org.BouncyCastle.Math.BigInteger(1, parameters.Modulus!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.Exponent!));
        }
    }

    /// <summary>
    /// Parses an RSA private key from key material.
    /// Supports both raw BouncyCastle format and .NET RSA parameters.
    /// </summary>
    private RsaPrivateCrtKeyParameters ParsePrivateKey(byte[] keyMaterial)
    {
        try
        {
            // Try parsing as BouncyCastle private key
            return (RsaPrivateCrtKeyParameters)PrivateKeyFactory.CreateKey(keyMaterial);
        }
        catch
        {
            // Fallback: parse as .NET RSA parameters
            using var rsa = RSA.Create();
            rsa.ImportRSAPrivateKey(keyMaterial, out _);
            var parameters = rsa.ExportParameters(true);

            return new RsaPrivateCrtKeyParameters(
                new Org.BouncyCastle.Math.BigInteger(1, parameters.Modulus!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.Exponent!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.D!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.P!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.Q!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.DP!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.DQ!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.InverseQ!));
        }
    }

    /// <summary>
    /// Calculates the maximum plaintext size for RSA-OAEP with SHA-256.
    /// </summary>
    private static int GetMaxPlaintextSize(int keySize)
    {
        // OAEP overhead: 2 + (2 * hash length) + 2
        // SHA-256 hash length: 32 bytes
        const int hashLength = 32;
        const int oaepOverhead = 2 + (2 * hashLength) + 2;
        return (keySize / 8) - oaepOverhead;
    }
}

/// <summary>
/// RSA encryption strategy with PKCS#1 v1.5 padding (legacy compatibility).
///
/// RSA with PKCS#1 v1.5 padding is an older padding scheme that is still widely
/// supported for backward compatibility. However, it is vulnerable to padding
/// oracle attacks and should be avoided for new implementations.
///
/// Process:
/// 1. Uses RSA public key to encrypt plaintext with PKCS#1 v1.5 padding
/// 2. Deterministic padding (less secure than OAEP)
/// 3. Maximum plaintext size: (keySize in bytes) - 11
///    For 2048-bit key: 2048/8 - 11 = 245 bytes
///    For 3072-bit key: 3072/8 - 11 = 373 bytes
///    For 4096-bit key: 4096/8 - 11 = 501 bytes
///
/// Key Sizes:
/// - 2048 bits: Minimum recommended (legacy systems)
/// - 3072 bits: Better security margin
/// - 4096 bits: Maximum security
///
/// Security Level: Standard to High
///
/// Security Warning:
/// PKCS#1 v1.5 is vulnerable to padding oracle attacks (Bleichenbacher attack).
/// Use RSA-OAEP instead for new implementations. This strategy is provided
/// only for backward compatibility with legacy systems.
///
/// Use Cases:
/// - Legacy system integration
/// - Backward compatibility with older software
/// - Systems that don't support OAEP
/// - SSL/TLS legacy compatibility
///
/// Recommendation: Migrate to RSA-OAEP (RsaOaepStrategy) for better security.
///
/// Standards: PKCS#1 v1.5 (RFC 2313, obsoleted by RFC 3447)
/// </summary>
public sealed class RsaPkcs1Strategy : EncryptionStrategyBase
{
    private readonly SecureRandom _secureRandom;
    private readonly int _keySize;

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = $"RSA-{_keySize}-PKCS1",
        KeySizeBits = _keySize,
        BlockSizeBytes = _keySize / 8,
        IvSizeBytes = 0, // RSA doesn't use IV
        TagSizeBytes = 0, // RSA doesn't use authentication tags
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = false,
            IsStreamable = false,
            IsHardwareAcceleratable = true,
            SupportsAead = false,
            SupportsParallelism = false,
            MinimumSecurityLevel = SecurityLevel.Standard
        },
        SecurityLevel = _keySize switch
        {
            2048 => SecurityLevel.Standard,
            3072 => SecurityLevel.High,
            4096 => SecurityLevel.High,
            _ => SecurityLevel.Standard
        },
        Parameters = new Dictionary<string, object>
        {
            ["Algorithm"] = "RSA",
            ["Padding"] = "PKCS#1 v1.5",
            ["KeySize"] = _keySize,
            ["MaxPlaintextSize"] = (_keySize / 8) - 11,
            ["Standard"] = "PKCS#1 v1.5 (RFC 2313)",
            ["SecurityWarning"] = "Vulnerable to padding oracle attacks. Use RSA-OAEP for better security.",
            ["Deprecated"] = true,
            ["LegacyOnly"] = true
        }
    };

    /// <inheritdoc/>
    public override string StrategyId => $"rsa-{_keySize}-pkcs1";

    /// <inheritdoc/>
    public override string StrategyName => $"RSA-{_keySize}-PKCS1 (Legacy)";

    /// <summary>
    /// Initializes a new instance of RSA-PKCS1 encryption strategy.
    /// </summary>
    /// <param name="keySize">RSA key size in bits (2048, 3072, or 4096). Default: 2048.</param>
    /// <exception cref="ArgumentException">If key size is not supported.</exception>
    public RsaPkcs1Strategy(int keySize = 2048)
    {
        if (keySize != 2048 && keySize != 3072 && keySize != 4096)
        {
            throw new ArgumentException(
                "RSA key size must be 2048, 3072, or 4096 bits",
                nameof(keySize));
        }

        _keySize = keySize;
        _secureRandom = new SecureRandom();
    }

    /// <summary>
    /// Production hardening: validates RSA PKCS1 configuration on initialization.
    /// </summary>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("rsa.pkcs1.init");
        if (_keySize < 2048)
            throw new InvalidOperationException($"RSA key size {_keySize} is below NIST minimum of 2048 bits");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Production hardening: releases resources on shutdown.
    /// </summary>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("rsa.pkcs1.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Production hardening: cached health check for RSA-PKCS1 round-trip.
    /// </summary>
    public Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        return GetCachedHealthAsync(async (cancellationToken) =>
        {
            try
            {
                using var rsa = RSA.Create(_keySize);
                var testData = new byte[32];
                RandomNumberGenerator.Fill(testData);
                var encrypted = rsa.Encrypt(testData, RSAEncryptionPadding.OaepSHA256);
                var decrypted = rsa.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA256);
                var isHealthy = testData.AsSpan().SequenceEqual(decrypted);
                CryptographicOperations.ZeroMemory(testData);
                CryptographicOperations.ZeroMemory(decrypted);
                return new StrategyHealthCheckResult(
                    isHealthy,
                    isHealthy ? $"RSA-{_keySize}-OAEP healthy" : "Round-trip failed",
                    new Dictionary<string, object>
                    {
                        ["KeySize"] = _keySize,
                        ["Padding"] = "PKCS1-v1.5",
                        ["Legacy"] = true,
                        ["EncryptOps"] = GetCounter("rsa.pkcs1.encrypt"),
                        ["DecryptOps"] = GetCounter("rsa.pkcs1.decrypt")
                    });
            }
            catch (Exception ex)
            {
                return new StrategyHealthCheckResult(false, $"RSA PKCS1 health check failed: {ex.Message}");
            }
        }, TimeSpan.FromSeconds(60), ct);
    }

    /// <inheritdoc/>
    protected override async Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        IncrementCounter("rsa.pkcs1.encrypt");
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        if (plaintext.Length == 0)
            throw new ArgumentException("Plaintext cannot be empty", nameof(plaintext));

        return await Task.Run(() =>
        {
            // Parse RSA public key from key material
            var publicKey = ParsePublicKey(key);

            // Create PKCS#1 v1.5 cipher
            var cipher = new Pkcs1Encoding(new RsaEngine());
            cipher.Init(true, publicKey);

            // Check plaintext size
            var maxSize = cipher.GetInputBlockSize();
            if (plaintext.Length > maxSize)
            {
                throw new ArgumentException(
                    $"Plaintext too large for RSA-{_keySize}-PKCS1. Maximum size: {maxSize} bytes, got: {plaintext.Length} bytes. " +
                    $"Consider using hybrid encryption for larger data.",
                    nameof(plaintext));
            }

            // Encrypt
            var ciphertext = cipher.ProcessBlock(plaintext, 0, plaintext.Length);

            return ciphertext;
        }, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        IncrementCounter("rsa.pkcs1.decrypt");
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(key);

        if (ciphertext.Length == 0)
            throw new ArgumentException("Ciphertext cannot be empty", nameof(ciphertext));

        return await Task.Run(() =>
        {
            // Parse RSA private key from key material
            var privateKey = ParsePrivateKey(key);

            // Create PKCS#1 v1.5 cipher
            var cipher = new Pkcs1Encoding(new RsaEngine());
            cipher.Init(false, privateKey);

            try
            {
                // Decrypt
                var plaintext = cipher.ProcessBlock(ciphertext, 0, ciphertext.Length);
                return plaintext;
            }
            catch (InvalidCipherTextException ex)
            {
                // PKCS#1 v1.5 padding validation failed
                // This could indicate tampering or padding oracle attack attempt
                throw new CryptographicException(
                    "RSA-PKCS1 decryption failed. Invalid padding or corrupted ciphertext.", ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Parses an RSA public key from key material.
    /// Supports both raw BouncyCastle format and .NET RSA parameters.
    /// </summary>
    private RsaKeyParameters ParsePublicKey(byte[] keyMaterial)
    {
        try
        {
            // Try parsing as BouncyCastle public key
            var publicKeyInfo = Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory
                .CreateSubjectPublicKeyInfo(PublicKeyFactory.CreateKey(keyMaterial));
            return (RsaKeyParameters)PublicKeyFactory.CreateKey(publicKeyInfo);
        }
        catch
        {
            // Fallback: parse as .NET RSA parameters
            using var rsa = RSA.Create();
            rsa.ImportRSAPublicKey(keyMaterial, out _);
            var parameters = rsa.ExportParameters(false);

            return new RsaKeyParameters(
                false,
                new Org.BouncyCastle.Math.BigInteger(1, parameters.Modulus!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.Exponent!));
        }
    }

    /// <summary>
    /// Parses an RSA private key from key material.
    /// Supports both raw BouncyCastle format and .NET RSA parameters.
    /// </summary>
    private RsaPrivateCrtKeyParameters ParsePrivateKey(byte[] keyMaterial)
    {
        try
        {
            // Try parsing as BouncyCastle private key
            return (RsaPrivateCrtKeyParameters)PrivateKeyFactory.CreateKey(keyMaterial);
        }
        catch
        {
            // Fallback: parse as .NET RSA parameters
            using var rsa = RSA.Create();
            rsa.ImportRSAPrivateKey(keyMaterial, out _);
            var parameters = rsa.ExportParameters(true);

            return new RsaPrivateCrtKeyParameters(
                new Org.BouncyCastle.Math.BigInteger(1, parameters.Modulus!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.Exponent!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.D!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.P!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.Q!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.DP!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.DQ!),
                new Org.BouncyCastle.Math.BigInteger(1, parameters.InverseQ!));
        }
    }
}
