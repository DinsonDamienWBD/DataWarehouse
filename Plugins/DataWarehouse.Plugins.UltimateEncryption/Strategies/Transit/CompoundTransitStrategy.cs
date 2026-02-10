using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Security.Transit;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Transit;

/// <summary>
/// Compound transit encryption strategy that chains two ciphers.
/// Encrypts with primary cipher, then wraps with secondary cipher for maximum security.
/// Suitable for maximum security deployments where defense-in-depth is required.
/// Example: AES-256-GCM + Serpent-256-GCM cascade.
/// </summary>
public sealed class CompoundTransitStrategy : TransitEncryptionPluginBase
{
    private const int KeySize = 64; // 32 bytes per cipher (2 x 256-bit keys)
    private const int NonceSize = 24; // 12 bytes per cipher
    private const int TagSize = 32; // 16 bytes per cipher

    /// <inheritdoc/>
    public override string Id => "transit-compound-aes-serpent";

    /// <inheritdoc/>
    public override string Name => "Compound Transit Encryption (AES + Serpent)";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <summary>
    /// Primary cipher algorithm name. Default is AES-256-GCM.
    /// </summary>
    public string PrimaryCipher { get; set; } = "AES-256-GCM";

    /// <summary>
    /// Secondary cipher algorithm name. Default is Serpent-256-GCM.
    /// </summary>
    public string SecondaryCipher { get; set; } = "Serpent-256-GCM";

    /// <inheritdoc/>
    protected override async Task<(byte[] Ciphertext, Dictionary<string, object> Metadata)> EncryptDataAsync(
        byte[] plaintext,
        CipherPreset preset,
        byte[] key,
        byte[]? aad,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != KeySize)
        {
            throw new ArgumentException(
                $"Key must be {KeySize} bytes for compound encryption (32 bytes per cipher)",
                nameof(key));
        }

        // Split key into two keys
        var primaryKey = new byte[32];
        var secondaryKey = new byte[32];
        Buffer.BlockCopy(key, 0, primaryKey, 0, 32);
        Buffer.BlockCopy(key, 32, secondaryKey, 0, 32);

        // Generate nonces for both layers
        var primaryNonce = new byte[12];
        var secondaryNonce = new byte[12];
        RandomNumberGenerator.Fill(primaryNonce);
        RandomNumberGenerator.Fill(secondaryNonce);

        // Step 1: Encrypt with primary cipher (AES-256-GCM)
        var primaryCiphertext = new byte[plaintext.Length];
        var primaryTag = new byte[16];

        using (var aesGcm = new AesGcm(primaryKey, 16))
        {
            aesGcm.Encrypt(primaryNonce, plaintext, primaryCiphertext, primaryTag, aad);
        }

        // Combine primary result: nonce + ciphertext + tag
        var primaryResult = new byte[12 + primaryCiphertext.Length + 16];
        Buffer.BlockCopy(primaryNonce, 0, primaryResult, 0, 12);
        Buffer.BlockCopy(primaryCiphertext, 0, primaryResult, 12, primaryCiphertext.Length);
        Buffer.BlockCopy(primaryTag, 0, primaryResult, 12 + primaryCiphertext.Length, 16);

        // Step 2: Encrypt the result with secondary cipher (Serpent-256-GCM using BouncyCastle)
        var secondaryCiphertext = new byte[primaryResult.Length];
        var secondaryTag = new byte[16];

        EncryptSerpentGcm(primaryResult, secondaryKey, secondaryNonce, null, secondaryCiphertext, secondaryTag);

        // Secure memory cleanup
        CryptographicOperations.ZeroMemory(primaryKey);
        CryptographicOperations.ZeroMemory(secondaryKey);

        // Combine final result: secondary nonce + secondary ciphertext + secondary tag
        var combined = new byte[12 + secondaryCiphertext.Length + 16];
        Buffer.BlockCopy(secondaryNonce, 0, combined, 0, 12);
        Buffer.BlockCopy(secondaryCiphertext, 0, combined, 12, secondaryCiphertext.Length);
        Buffer.BlockCopy(secondaryTag, 0, combined, 12 + secondaryCiphertext.Length, 16);

        // Build metadata
        var metadata = new Dictionary<string, object>
        {
            ["PresetId"] = preset.Id,
            ["Algorithm"] = "Compound",
            ["PrimaryCipher"] = PrimaryCipher,
            ["SecondaryCipher"] = SecondaryCipher,
            ["Layers"] = 2,
            ["KeySize"] = KeySize,
            ["SecurityLevel"] = "Maximum",
            ["Compressed"] = false,
            ["EncryptedAt"] = DateTime.UtcNow
        };

        return await Task.FromResult((combined, metadata));
    }

    /// <inheritdoc/>
    protected override async Task<byte[]> DecryptDataAsync(
        byte[] ciphertext,
        CipherPreset preset,
        byte[] key,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(metadata);

        if (key.Length != KeySize)
        {
            throw new ArgumentException(
                $"Key must be {KeySize} bytes for compound decryption",
                nameof(key));
        }

        // Validate minimum size
        var minSize = 12 + 16 + 12 + 16; // Two layers of nonce + tag
        if (ciphertext.Length < minSize)
        {
            throw new ArgumentException(
                $"Ciphertext too short. Expected at least {minSize} bytes, got {ciphertext.Length}",
                nameof(ciphertext));
        }

        // Split key
        var primaryKey = new byte[32];
        var secondaryKey = new byte[32];
        Buffer.BlockCopy(key, 0, primaryKey, 0, 32);
        Buffer.BlockCopy(key, 32, secondaryKey, 0, 32);

        // Step 1: Decrypt outer layer (secondary cipher)
        // Extract secondary nonce, ciphertext, and tag
        var secondaryNonce = new byte[12];
        var secondaryEncryptedLength = ciphertext.Length - 12 - 16;
        var secondaryEncrypted = new byte[secondaryEncryptedLength];
        var secondaryTag = new byte[16];

        Buffer.BlockCopy(ciphertext, 0, secondaryNonce, 0, 12);
        Buffer.BlockCopy(ciphertext, 12, secondaryEncrypted, 0, secondaryEncryptedLength);
        Buffer.BlockCopy(ciphertext, 12 + secondaryEncryptedLength, secondaryTag, 0, 16);

        var primaryLayerData = new byte[secondaryEncryptedLength];

        DecryptSerpentGcm(secondaryEncrypted, secondaryKey, secondaryNonce, secondaryTag, null, primaryLayerData);

        // Step 2: Decrypt inner layer (primary cipher)
        // Extract primary nonce, ciphertext, and tag
        var primaryNonce = new byte[12];
        var primaryEncryptedLength = primaryLayerData.Length - 12 - 16;
        var primaryEncrypted = new byte[primaryEncryptedLength];
        var primaryTag = new byte[16];

        Buffer.BlockCopy(primaryLayerData, 0, primaryNonce, 0, 12);
        Buffer.BlockCopy(primaryLayerData, 12, primaryEncrypted, 0, primaryEncryptedLength);
        Buffer.BlockCopy(primaryLayerData, 12 + primaryEncryptedLength, primaryTag, 0, 16);

        // Extract AAD from metadata if present
        byte[]? aad = null;
        if (metadata.TryGetValue("AAD", out var aadObj) && aadObj is byte[] aadBytes)
        {
            aad = aadBytes;
        }

        var plaintext = new byte[primaryEncryptedLength];

        using (var primaryAes = new AesGcm(primaryKey, 16))
        {
            primaryAes.Decrypt(primaryNonce, primaryEncrypted, primaryTag, plaintext, aad);
        }

        // Secure memory cleanup
        CryptographicOperations.ZeroMemory(primaryKey);
        CryptographicOperations.ZeroMemory(secondaryKey);
        CryptographicOperations.ZeroMemory(primaryLayerData);

        return await Task.FromResult(plaintext);
    }

    /// <inheritdoc/>
    public override async Task<EndpointCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = new EndpointCapabilities(
            SupportedCipherPresets: new List<string>
            {
                "maximum-compound",
                "military-compound"
            }.AsReadOnly(),
            SupportedAlgorithms: new List<string>
            {
                "AES-256-GCM",
                "Serpent-256-GCM",
                "Compound-AES-Serpent"
            }.AsReadOnly(),
            PreferredPresetId: "maximum-compound",
            MaximumSecurityLevel: TransitSecurityLevel.Military,
            SupportsTranscryption: true,
            Metadata: new Dictionary<string, object>
            {
                ["SecurityLevel"] = "Maximum",
                ["Layers"] = 2,
                ["DefenseInDepth"] = true,
                ["Type"] = "Compound-AEAD",
                ["Note"] = "Two-layer encryption: AES-256-GCM + Serpent-256-GCM"
            }.AsReadOnly()
        );

        return await Task.FromResult(capabilities);
    }

    /// <summary>
    /// Configures the cipher algorithms for the compound encryption.
    /// </summary>
    /// <param name="primaryCipher">Primary (inner) cipher algorithm name.</param>
    /// <param name="secondaryCipher">Secondary (outer) cipher algorithm name.</param>
    public void ConfigureCiphers(string primaryCipher, string secondaryCipher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryCipher);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondaryCipher);

        PrimaryCipher = primaryCipher;
        SecondaryCipher = secondaryCipher;
    }

    /// <summary>
    /// Gets information about the configured cipher cascade.
    /// </summary>
    public Dictionary<string, object> GetCascadeInfo()
    {
        return new Dictionary<string, object>
        {
            ["Strategy"] = "Compound",
            ["PrimaryCipher"] = PrimaryCipher,
            ["SecondaryCipher"] = SecondaryCipher,
            ["Layers"] = 2,
            ["KeySize"] = KeySize,
            ["TotalOverhead"] = NonceSize + TagSize,
            ["SecurityLevel"] = "Maximum"
        };
    }

    /// <summary>
    /// Encrypts data using Serpent-256 in GCM mode using BouncyCastle.
    /// </summary>
    private static void EncryptSerpentGcm(
        byte[] plaintext,
        byte[] key,
        byte[] nonce,
        byte[]? aad,
        byte[] ciphertext,
        byte[] tag)
    {
        var serpentEngine = new SerpentEngine();
        var gcmCipher = new GcmBlockCipher(serpentEngine);

        var keyParams = new KeyParameter(key);
        var gcmParams = new AeadParameters(keyParams, 128, nonce, aad);
        gcmCipher.Init(true, gcmParams);

        var outputBuffer = new byte[gcmCipher.GetOutputSize(plaintext.Length)];
        var len = gcmCipher.ProcessBytes(plaintext, 0, plaintext.Length, outputBuffer, 0);
        len += gcmCipher.DoFinal(outputBuffer, len);

        Buffer.BlockCopy(outputBuffer, 0, ciphertext, 0, plaintext.Length);
        Buffer.BlockCopy(outputBuffer, plaintext.Length, tag, 0, 16);
    }

    /// <summary>
    /// Decrypts data using Serpent-256 in GCM mode using BouncyCastle.
    /// </summary>
    private static void DecryptSerpentGcm(
        byte[] ciphertext,
        byte[] key,
        byte[] nonce,
        byte[] tag,
        byte[]? aad,
        byte[] plaintext)
    {
        var serpentEngine = new SerpentEngine();
        var gcmCipher = new GcmBlockCipher(serpentEngine);

        var keyParams = new KeyParameter(key);
        var gcmParams = new AeadParameters(keyParams, 128, nonce, aad);
        gcmCipher.Init(false, gcmParams);

        var inputBuffer = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, inputBuffer, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, inputBuffer, ciphertext.Length, tag.Length);

        var outputBuffer = new byte[gcmCipher.GetOutputSize(inputBuffer.Length)];
        var len = gcmCipher.ProcessBytes(inputBuffer, 0, inputBuffer.Length, outputBuffer, 0);
        len += gcmCipher.DoFinal(outputBuffer, len);

        Buffer.BlockCopy(outputBuffer, 0, plaintext, 0, plaintext.Length);
    }
}
