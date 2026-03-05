using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Security.Transit;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Transit;

/// <summary>
/// AES-256-GCM transit encryption strategy.
/// Provides fastest authenticated encryption when AES-NI hardware acceleration is available.
/// Suitable for high-throughput data transfers with strong security.
/// </summary>
public sealed class AesGcmTransitStrategy : TransitEncryptionPluginBase
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 12; // 96 bits (recommended for GCM)
    private const int TagSize = 16; // 128 bits
    private const int ChunkSize = 64 * 1024; // 64KB chunks for streaming

    /// <inheritdoc/>
    public override string Id => "transit-aes256gcm";

    /// <inheritdoc/>
    public override string Name => "AES-256-GCM Transit Encryption";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    protected override Task<(byte[] Ciphertext, Dictionary<string, object> Metadata)> EncryptDataAsync(
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
            throw new ArgumentException($"Key must be {KeySize} bytes for AES-256-GCM", nameof(key));
        }

        // Generate random nonce
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        // Prepare buffers
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        // Encrypt with AES-GCM
        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        // Combine nonce + ciphertext + tag
        var combined = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + ciphertext.Length, TagSize);

        // Build metadata
        var metadata = new Dictionary<string, object>
        {
            ["PresetId"] = preset.Id,
            ["Algorithm"] = "AES-256-GCM",
            ["NonceSize"] = NonceSize,
            ["TagSize"] = TagSize,
            ["KeySize"] = KeySize,
            ["Compressed"] = false,
            ["EncryptedAt"] = DateTime.UtcNow
        };

        return Task.FromResult((combined, metadata));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptDataAsync(
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
            throw new ArgumentException($"Key must be {KeySize} bytes for AES-256-GCM", nameof(key));
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

        // Decrypt
        var plaintext = new byte[encryptedDataLength];
        using var aesGcm = new AesGcm(key, TagSize);

        // Extract AAD from metadata if present
        byte[]? aad = null;
        if (metadata.TryGetValue("AAD", out var aadObj) && aadObj is byte[] aadBytes)
        {
            aad = aadBytes;
        }

        aesGcm.Decrypt(nonce, encryptedData, tag, plaintext, aad);

        return Task.FromResult(plaintext);
    }

    /// <inheritdoc/>
    public override async Task<TransitEncryptionResult> EncryptStreamForTransitAsync(
        System.IO.Stream plaintextStream,
        System.IO.Stream ciphertextStream,
        TransitEncryptionOptions options,
        ISecurityContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintextStream);
        ArgumentNullException.ThrowIfNull(ciphertextStream);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        // For streaming, encrypt in chunks
        // Each chunk gets its own nonce derived from a master nonce + counter
        var masterNonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(masterNonce);

        // Write master nonce to stream header
        await ciphertextStream.WriteAsync(masterNonce, cancellationToken);

        if (KeyStore is null)
            throw new InvalidOperationException("KeyStore has not been initialized. Call InitializeAsync before streaming encryption (#3000).");

        var keyId = await KeyStore.GetCurrentKeyIdAsync();
        var key = await KeyStore.GetKeyAsync(keyId, context);

        if (key.Length != KeySize)
        {
            throw new InvalidOperationException($"Key size mismatch. Expected {KeySize} bytes");
        }

        var buffer = new byte[ChunkSize];
        long totalBytes = 0;
        ulong chunkCounter = 0;

        using var aesGcm = new AesGcm(key, TagSize);

        while (true)
        {
            var bytesRead = await plaintextStream.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken);
            if (bytesRead == 0) break;

            totalBytes += bytesRead;

            // Derive chunk nonce from master nonce + counter
            var chunkNonce = DeriveChunkNonce(masterNonce, chunkCounter++);

            // Encrypt chunk
            var plainChunk = buffer.AsSpan(0, bytesRead);
            var cipherChunk = new byte[bytesRead];
            var tag = new byte[TagSize];

            aesGcm.Encrypt(chunkNonce, plainChunk, cipherChunk, tag, options.AdditionalAuthenticatedData);

            // Write chunk size (4 bytes) + cipher chunk + tag
            var chunkSizeBytes = BitConverter.GetBytes(bytesRead);
            await ciphertextStream.WriteAsync(chunkSizeBytes, cancellationToken);
            await ciphertextStream.WriteAsync(cipherChunk, cancellationToken);
            await ciphertextStream.WriteAsync(tag, cancellationToken);
        }

        // Write end-of-stream sentinel: chunk size of 0 with no data or tag.
        // Decryptors must verify this sentinel is present to detect truncation attacks (#2995).
        await ciphertextStream.WriteAsync(BitConverter.GetBytes(0), cancellationToken);

        // Secure memory cleanup
        CryptographicOperations.ZeroMemory(key);

        var metadata = new Dictionary<string, object>
        {
            ["PresetId"] = options.PresetId ?? "standard-aes256gcm",
            ["Algorithm"] = "AES-256-GCM",
            ["StreamingMode"] = true,
            ["ChunkSize"] = ChunkSize,
            ["TotalBytes"] = totalBytes,
            ["ChunkCount"] = chunkCounter,
            ["EncryptedAt"] = DateTime.UtcNow
        };

        return new TransitEncryptionResult
        {
            Ciphertext = Array.Empty<byte>(), // Data written to stream
            UsedPresetId = options.PresetId ?? "standard-aes256gcm",
            EncryptionMetadata = metadata,
            WasCompressed = false
        };
    }

    /// <summary>
    /// Decrypts a stream written by <see cref="EncryptStreamForTransitAsync"/> — LOW-3010.
    /// </summary>
    public override async Task<TransitDecryptionResult> DecryptStreamFromTransitAsync(
        System.IO.Stream ciphertextStream,
        System.IO.Stream plaintextStream,
        ISecurityContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertextStream);
        ArgumentNullException.ThrowIfNull(plaintextStream);
        ArgumentNullException.ThrowIfNull(context);
        if (KeyStore is null) throw new InvalidOperationException("KeyStore not initialized.");

        var masterNonce = new byte[NonceSize];
        if (await ReadExactAsync(ciphertextStream, masterNonce, cancellationToken) != NonceSize)
            throw new System.Security.Cryptography.CryptographicException("Stream header truncated.");

        var keyId = await KeyStore.GetCurrentKeyIdAsync().ConfigureAwait(false);
        var key = await KeyStore.GetKeyAsync(keyId, context).ConfigureAwait(false);
        if (key.Length != KeySize) throw new System.Security.Cryptography.CryptographicException("Key size mismatch.");

        var lenBytes = new byte[4];
        var buffer = new byte[ChunkSize + TagSize];
        long totalBytes = 0;
        ulong chunkCounter = 0;

        using var aesGcm = new AesGcm(key, TagSize);
        while (true)
        {
            if (await ReadExactAsync(ciphertextStream, lenBytes, cancellationToken) != 4)
                throw new System.Security.Cryptography.CryptographicException("Truncated chunk length.");
            var plainSize = BitConverter.ToInt32(lenBytes, 0);
            if (plainSize == 0) break;
            if (plainSize < 0 || plainSize > ChunkSize) throw new System.Security.Cryptography.CryptographicException($"Invalid chunk size {plainSize}.");
            if (await ReadExactAsync(ciphertextStream, buffer.AsMemory(0, plainSize + TagSize), cancellationToken) != plainSize + TagSize)
                throw new System.Security.Cryptography.CryptographicException("Truncated chunk data.");
            var plainChunk = new byte[plainSize];
            aesGcm.Decrypt(DeriveChunkNonce(masterNonce, chunkCounter++), buffer.AsSpan(0, plainSize), buffer.AsSpan(plainSize, TagSize), plainChunk);
            await plaintextStream.WriteAsync(plainChunk, cancellationToken).ConfigureAwait(false);
            totalBytes += plainSize;
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        return new TransitDecryptionResult { Plaintext = Array.Empty<byte>(), UsedPresetId = "standard-aes256gcm", WasDecompressed = false };
    }

    private static async Task<int> ReadExactAsync(System.IO.Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length) { var r = await stream.ReadAsync(buffer.Slice(offset), ct).ConfigureAwait(false); if (r == 0) return offset; offset += r; }
        return offset;
    }

    /// <summary>
    /// Derives a chunk-specific nonce from master nonce and chunk counter.
    /// Uses XOR with counter to maintain nonce uniqueness.
    /// </summary>
    private static byte[] DeriveChunkNonce(byte[] masterNonce, ulong counter)
    {
        var chunkNonce = new byte[NonceSize];
        Buffer.BlockCopy(masterNonce, 0, chunkNonce, 0, NonceSize);

        // XOR the last 8 bytes with the counter
        var counterBytes = BitConverter.GetBytes(counter);
        for (int i = 0; i < 8 && i < NonceSize; i++)
        {
            chunkNonce[NonceSize - 8 + i] ^= counterBytes[i];
        }

        return chunkNonce;
    }
}
