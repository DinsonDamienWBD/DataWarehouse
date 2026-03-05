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
/// AES-128-GCM transit encryption strategy.
/// Provides high-performance authenticated encryption with lower security margin.
/// Suitable for internal network transfers where speed is prioritized over maximum security.
/// </summary>
public sealed class Aes128GcmTransitStrategy : TransitEncryptionPluginBase
{
    private const int KeySize = 16; // 128 bits
    private const int NonceSize = 12; // 96 bits (recommended for GCM)
    private const int TagSize = 16; // 128 bits
    private const int ChunkSize = 64 * 1024; // 64KB chunks for streaming

    /// <inheritdoc/>
    public override string Id => "transit-aes128gcm";

    /// <inheritdoc/>
    public override string Name => "AES-128-GCM Transit Encryption";

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
            throw new ArgumentException($"Key must be {KeySize} bytes for AES-128-GCM", nameof(key));
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
            ["Algorithm"] = "AES-128-GCM",
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
            throw new ArgumentException($"Key must be {KeySize} bytes for AES-128-GCM", nameof(key));
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
        // This allows the decryptor to detect truncation attacks where the stream is
        // cut short before all chunks are delivered (#2995).
        await ciphertextStream.WriteAsync(BitConverter.GetBytes(0), cancellationToken);

        // Secure memory cleanup
        CryptographicOperations.ZeroMemory(key);

        var metadata = new Dictionary<string, object>
        {
            ["PresetId"] = options.PresetId ?? "standard-aes128gcm",
            ["Algorithm"] = "AES-128-GCM",
            ["StreamingMode"] = true,
            ["ChunkSize"] = ChunkSize,
            ["TotalBytes"] = totalBytes,
            ["ChunkCount"] = chunkCounter,
            ["EncryptedAt"] = DateTime.UtcNow
        };

        return new TransitEncryptionResult
        {
            Ciphertext = Array.Empty<byte>(), // Data written to stream
            UsedPresetId = options.PresetId ?? "standard-aes128gcm",
            EncryptionMetadata = metadata,
            WasCompressed = false
        };
    }

    /// <summary>
    /// Decrypts a stream written by <see cref="EncryptStreamForTransitAsync"/>.
    /// Reads the master nonce from the stream header, then decrypts each chunk in sequence.
    /// LOW-3010: previously absent — streams encrypted via the chunk-based path could not be decrypted.
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

        if (KeyStore is null)
            throw new InvalidOperationException("KeyStore has not been initialized. Call InitializeAsync before streaming decryption.");

        // Read master nonce from stream header
        var masterNonce = new byte[NonceSize];
        var nonceBytesRead = await ciphertextStream.ReadAsync(masterNonce, cancellationToken).ConfigureAwait(false);
        if (nonceBytesRead != NonceSize)
            throw new CryptographicException($"Stream header truncated: expected {NonceSize}-byte nonce, got {nonceBytesRead} bytes.");

        var keyId = await KeyStore.GetCurrentKeyIdAsync().ConfigureAwait(false);
        var key = await KeyStore.GetKeyAsync(keyId, context).ConfigureAwait(false);

        if (key.Length != KeySize)
            throw new CryptographicException($"Key size mismatch. Expected {KeySize} bytes, got {key.Length}.");

        // Decrypt format (mirrors EncryptStreamForTransitAsync):
        //   12-byte master nonce (already read above)
        //   Per chunk: [4-byte LE plaintext-size][ciphertext(plaintext-size bytes)][16-byte tag]
        //   Sentinel: [4-byte LE zero]
        var lenBytes = new byte[4];
        var buffer = new byte[ChunkSize + TagSize];
        long totalBytes = 0;
        ulong chunkCounter = 0;

        using var aesGcm = new AesGcm(key, TagSize);

        while (true)
        {
            // Read chunk plaintext-size prefix (4 bytes, LE)
            var lenRead = await ReadExactAsync(ciphertextStream, lenBytes, cancellationToken).ConfigureAwait(false);
            if (lenRead == 0) break; // clean end of stream (shouldn't happen before sentinel)
            if (lenRead != 4) throw new CryptographicException("Truncated chunk length prefix.");

            var plainSize = BitConverter.ToInt32(lenBytes, 0);
            if (plainSize == 0) break; // end-of-stream sentinel
            if (plainSize < 0 || plainSize > ChunkSize)
                throw new CryptographicException($"Invalid chunk plaintext size {plainSize}.");

            var totalChunkBytes = plainSize + TagSize;
            var actualRead = await ReadExactAsync(ciphertextStream, buffer.AsMemory(0, totalChunkBytes), cancellationToken).ConfigureAwait(false);
            if (actualRead != totalChunkBytes) throw new CryptographicException("Truncated chunk data.");

            var chunkNonce = DeriveChunkNonce(masterNonce, chunkCounter++);
            var cipherChunk = buffer.AsSpan(0, plainSize);
            var tag = buffer.AsSpan(plainSize, TagSize);
            var plainChunk = new byte[plainSize];

            aesGcm.Decrypt(chunkNonce, cipherChunk, tag, plainChunk);

            await plaintextStream.WriteAsync(plainChunk, cancellationToken).ConfigureAwait(false);
            totalBytes += plainSize;
        }

        CryptographicOperations.ZeroMemory(key);

        return new TransitDecryptionResult
        {
            Plaintext = Array.Empty<byte>(), // Data written to stream
            UsedPresetId = "standard-aes128gcm",
            WasDecompressed = false
        };
    }

    private static async Task<int> ReadExactAsync(System.IO.Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.Slice(offset), ct).ConfigureAwait(false);
            if (read == 0) return offset; // EOF
            offset += read;
        }
        return offset;
    }

    /// <summary>
    /// Derives a chunk-specific nonce from master nonce and chunk counter.
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
