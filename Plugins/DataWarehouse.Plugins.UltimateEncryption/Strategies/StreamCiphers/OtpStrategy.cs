using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Encryption;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.StreamCiphers;

/// <summary>
/// One-Time Pad (OTP) / Vernam Cipher encryption strategy.
///
/// THEORETICAL PROPERTIES:
/// - When implemented correctly with truly random keys, OTP provides information-theoretic security
/// - The only provably unbreakable encryption method (Shannon's perfect secrecy theorem)
/// - No computational assumptions required - secure even against infinite computational power
///
/// CRITICAL REQUIREMENTS FOR SECURITY:
/// 1. Key must be EXACTLY the same length as the plaintext
/// 2. Key must be truly random (not pseudorandom)
/// 3. Key must NEVER be reused (hence "One-Time")
/// 4. Key must be kept completely secret
/// 5. Secure key distribution is required (often the hardest part)
///
/// PRACTICAL LIMITATIONS:
/// - Key distribution problem: How to securely exchange keys as large as messages?
/// - Key storage: Requires storing massive amounts of key material
/// - Key generation: True randomness is difficult to obtain at scale
/// - No authentication: OTP provides confidentiality only, not integrity
/// - Impractical for most real-world scenarios
///
/// SECURITY WARNINGS:
/// - Key reuse completely breaks security (allows known-plaintext attacks)
/// - Pseudorandom keys reduce security to that of the PRNG
/// - Without authentication, vulnerable to known-plaintext and chosen-ciphertext attacks
/// - This implementation is for educational/experimental purposes
///
/// USE CASES:
/// - High-security diplomatic communications (with proper key distribution)
/// - Educational demonstrations of perfect secrecy
/// - Theoretical security research
/// - NOT recommended for general-purpose encryption (use AES-256-GCM instead)
/// </summary>
public sealed class OtpStrategy : EncryptionStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "otp-vernam";

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("otp.vernam.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("otp.vernam.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

    /// <inheritdoc/>
    public override string StrategyName => "One-Time Pad (Vernam Cipher)";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "One-Time Pad (OTP)",
        KeySizeBits = 0, // Variable - must equal plaintext length
        BlockSizeBytes = 1, // Stream cipher - operates byte-by-byte
        IvSizeBytes = 0, // No IV required
        TagSizeBytes = 0, // No authentication (confidentiality only)
        SecurityLevel = SecurityLevel.Experimental,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = false,
            IsStreamable = true,
            IsHardwareAcceleratable = true, // Simple XOR is very fast
            SupportsAead = false,
            SupportsParallelism = true, // XOR operations are fully parallelizable
            MinimumSecurityLevel = SecurityLevel.Experimental
        },
        Parameters = new Dictionary<string, object>
        {
            ["Operation"] = "XOR",
            ["KeyRequirement"] = "Must equal plaintext length",
            ["KeyReuse"] = "NEVER - Breaks security completely",
            ["Authentication"] = "None - Confidentiality only",
            ["TheoreticalSecurity"] = "Information-theoretic (perfect secrecy)",
            ["PracticalSecurity"] = "Depends entirely on key quality and distribution",
            ["Warning"] = "Experimental - Key management is extremely challenging"
        }
    };

    /// <summary>
    /// Validates that the key length exactly matches the plaintext length.
    /// This is a fundamental requirement for OTP security.
    /// </summary>
    private void ValidateKeyLength(byte[] data, byte[] key)
    {
        if (key.Length != data.Length)
        {
            throw new ArgumentException(
                $"OTP requires key length to exactly match data length. " +
                $"Data length: {data.Length} bytes, Key length: {key.Length} bytes. " +
                $"The key must be exactly {data.Length} bytes for this operation.",
                nameof(key));
        }
    }

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        // Validate fundamental OTP requirement
        ValidateKeyLength(plaintext, key);

        // OTP encryption: ciphertext = plaintext XOR key
        var ciphertext = new byte[plaintext.Length];

        for (int i = 0; i < plaintext.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ciphertext[i] = (byte)(plaintext[i] ^ key[i]);
        }

        return Task.FromResult(ciphertext);
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        // Validate fundamental OTP requirement
        ValidateKeyLength(ciphertext, key);

        // OTP decryption: plaintext = ciphertext XOR key
        // Note: For OTP, encryption and decryption are identical operations (XOR is its own inverse)
        var plaintext = new byte[ciphertext.Length];

        for (int i = 0; i < ciphertext.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plaintext[i] = (byte)(ciphertext[i] ^ key[i]);
        }

        return Task.FromResult(plaintext);
    }
}
