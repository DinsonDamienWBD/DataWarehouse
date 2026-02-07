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
/// Serpent-256 in GCM mode transit encryption strategy.
/// Maximum security cipher with government/military grade protection.
/// Wider security margin than AES (32 rounds vs 14) but slower due to no hardware acceleration.
/// Suitable for classified data and high-security deployments.
/// </summary>
public sealed class SerpentGcmTransitStrategy : TransitEncryptionPluginBase
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 12; // 96 bits (GCM standard)
    private const int TagSize = 16; // 128 bits
    private const int BlockSize = 16; // Serpent block size

    /// <inheritdoc/>
    public override string Id => "transit-serpent256gcm";

    /// <inheritdoc/>
    public override string Name => "Serpent-256-GCM Transit Encryption";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

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
            throw new ArgumentException($"Key must be {KeySize} bytes for Serpent-256-GCM", nameof(key));
        }

        // Generate random nonce
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        // Serpent-256 in GCM mode
        // Note: .NET does not have native Serpent support
        // This implementation uses a software-based Serpent cipher
        // Production code would use BouncyCastle or similar library

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        // Encrypt using Serpent-GCM (simplified - would use proper Serpent implementation)
        EncryptSerpentGcm(plaintext, key, nonce, aad, ciphertext, tag);

        // Combine nonce + ciphertext + tag
        var combined = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + ciphertext.Length, TagSize);

        // Build metadata
        var metadata = new Dictionary<string, object>
        {
            ["PresetId"] = preset.Id,
            ["Algorithm"] = "Serpent-256-GCM",
            ["NonceSize"] = NonceSize,
            ["TagSize"] = TagSize,
            ["KeySize"] = KeySize,
            ["Rounds"] = 32,
            ["SecurityLevel"] = "Military",
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
            throw new ArgumentException($"Key must be {KeySize} bytes for Serpent-256-GCM", nameof(key));
        }

        // Validate minimum size
        var minSize = NonceSize + TagSize;
        if (ciphertext.Length < minSize)
        {
            throw new ArgumentException(
                $"Ciphertext too short. Expected at least {minSize} bytes, got {ciphertext.Length}",
                nameof(ciphertext));
        }

        // Extract nonce, encrypted data, and tag
        var nonce = new byte[NonceSize];
        var encryptedDataLength = ciphertext.Length - NonceSize - TagSize;
        var encryptedData = new byte[encryptedDataLength];
        var tag = new byte[TagSize];

        Buffer.BlockCopy(ciphertext, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, NonceSize, encryptedData, 0, encryptedDataLength);
        Buffer.BlockCopy(ciphertext, NonceSize + encryptedDataLength, tag, 0, TagSize);

        // Extract AAD from metadata if present
        byte[]? aad = null;
        if (metadata.TryGetValue("AAD", out var aadObj) && aadObj is byte[] aadBytes)
        {
            aad = aadBytes;
        }

        // Decrypt
        var plaintext = new byte[encryptedDataLength];
        DecryptSerpentGcm(encryptedData, key, nonce, tag, aad, plaintext);

        return await Task.FromResult(plaintext);
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
        // Use BouncyCastle's SerpentEngine with GCM mode
        var serpentEngine = new SerpentEngine();
        var gcmCipher = new GcmBlockCipher(serpentEngine);

        // Initialize for encryption with 128-bit tag
        var keyParams = new KeyParameter(key);
        var gcmParams = new AeadParameters(keyParams, TagSize * 8, nonce, aad);
        gcmCipher.Init(true, gcmParams);

        // Process all input data
        var outputBuffer = new byte[gcmCipher.GetOutputSize(plaintext.Length)];
        var len = gcmCipher.ProcessBytes(plaintext, 0, plaintext.Length, outputBuffer, 0);
        len += gcmCipher.DoFinal(outputBuffer, len);

        // Extract ciphertext and tag from output
        // GCM output is: [ciphertext][tag]
        Buffer.BlockCopy(outputBuffer, 0, ciphertext, 0, plaintext.Length);
        Buffer.BlockCopy(outputBuffer, plaintext.Length, tag, 0, TagSize);
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
        // Use BouncyCastle's SerpentEngine with GCM mode
        var serpentEngine = new SerpentEngine();
        var gcmCipher = new GcmBlockCipher(serpentEngine);

        // Initialize for decryption with 128-bit tag
        var keyParams = new KeyParameter(key);
        var gcmParams = new AeadParameters(keyParams, TagSize * 8, nonce, aad);
        gcmCipher.Init(false, gcmParams);

        // Combine ciphertext and tag for decryption
        // GCM expects: [ciphertext][tag]
        var inputBuffer = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, inputBuffer, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, inputBuffer, ciphertext.Length, tag.Length);

        // Process and verify
        var outputBuffer = new byte[gcmCipher.GetOutputSize(inputBuffer.Length)];
        var len = gcmCipher.ProcessBytes(inputBuffer, 0, inputBuffer.Length, outputBuffer, 0);
        len += gcmCipher.DoFinal(outputBuffer, len); // This verifies the tag and throws if invalid

        // Copy plaintext
        Buffer.BlockCopy(outputBuffer, 0, plaintext, 0, plaintext.Length);
    }

    /// <inheritdoc/>
    public override async Task<EndpointCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = new EndpointCapabilities(
            SupportedCipherPresets: new List<string>
            {
                "military-serpent256gcm",
                "maximum-serpent256gcm"
            }.AsReadOnly(),
            SupportedAlgorithms: new List<string>
            {
                "Serpent-256-GCM",
                "Serpent-256"
            }.AsReadOnly(),
            PreferredPresetId: "military-serpent256gcm",
            MaximumSecurityLevel: TransitSecurityLevel.Military,
            SupportsTranscryption: true,
            Metadata: new Dictionary<string, object>
            {
                ["SecurityLevel"] = "Military",
                ["Rounds"] = 32,
                ["HardwareAccelerated"] = false,
                ["Type"] = "AEAD-Military",
                ["Note"] = "Software implementation - slower than AES but wider security margin"
            }.AsReadOnly()
        );

        return await Task.FromResult(capabilities);
    }
}
