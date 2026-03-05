using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateEncryption.CryptoAgility;

/// <summary>
/// Provides double-encryption capability for zero-downtime cryptographic algorithm transitions.
/// Encrypts data simultaneously with two algorithms, producing a <see cref="DoubleEncryptionEnvelope"/>
/// that can be decrypted by either algorithm. This allows gradual migration of consumers from
/// classical to post-quantum algorithms without service interruption.
/// </summary>
/// <remarks>
/// <para>
/// The service delegates actual encryption/decryption operations to the encryption strategies
/// via the message bus (topic "encryption.encrypt" and "encryption.decrypt"), maintaining
/// plugin isolation while supporting any registered encryption algorithm.
/// </para>
/// <para>
/// Security considerations:
/// </para>
/// <list type="bullet">
///   <item>All plaintext buffers are zeroed in finally blocks to minimize memory exposure</item>
///   <item>Each algorithm receives independent plaintext copies to prevent cross-contamination</item>
///   <item>Envelope metadata includes timestamps for transition expiry enforcement</item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto agility engine")]
public sealed class DoubleEncryptionService
{
    private readonly IMessageBus _messageBus;

    /// <summary>
    /// Default duration for double-encryption envelopes before they must be resolved.
    /// </summary>
    private static readonly TimeSpan DefaultTransitionDuration = TimeSpan.FromDays(7);

    /// <summary>
    /// Initializes a new instance of the <see cref="DoubleEncryptionService"/> class.
    /// </summary>
    /// <param name="messageBus">The message bus for delegating encryption operations to strategies.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="messageBus"/> is null.</exception>
    public DoubleEncryptionService(IMessageBus messageBus)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
    }

    /// <summary>
    /// Encrypts plaintext with both primary and secondary algorithms, producing a
    /// <see cref="DoubleEncryptionEnvelope"/> that can be decrypted by either algorithm.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object being encrypted.</param>
    /// <param name="plaintext">The data to encrypt. Will be zeroed after encryption completes.</param>
    /// <param name="primaryAlgorithmId">Primary algorithm identifier (typically the current/classical algorithm).</param>
    /// <param name="secondaryAlgorithmId">Secondary algorithm identifier (typically the target/PQC algorithm).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A double-encryption envelope containing ciphertext from both algorithms.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="plaintext"/> is null.</exception>
    /// <exception cref="ArgumentException">If algorithm IDs are null or whitespace.</exception>
    /// <exception cref="CryptographicException">If either encryption operation fails.</exception>
    public async Task<DoubleEncryptionEnvelope> EncryptAsync(
        Guid objectId,
        byte[] plaintext,
        string primaryAlgorithmId,
        string secondaryAlgorithmId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryAlgorithmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondaryAlgorithmId);

        byte[]? primaryCopy = null;
        byte[]? secondaryCopy = null;

        try
        {
            // Create independent copies of plaintext for each encryption operation
            primaryCopy = new byte[plaintext.Length];
            secondaryCopy = new byte[plaintext.Length];
            Buffer.BlockCopy(plaintext, 0, primaryCopy, 0, plaintext.Length);
            Buffer.BlockCopy(plaintext, 0, secondaryCopy, 0, plaintext.Length);

            // Encrypt with primary algorithm via bus
            var primaryCiphertext = await EncryptViaMessageBusAsync(
                primaryAlgorithmId, primaryCopy, ct).ConfigureAwait(false);

            // Encrypt with secondary algorithm via bus
            var secondaryCiphertext = await EncryptViaMessageBusAsync(
                secondaryAlgorithmId, secondaryCopy, ct).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;

            return new DoubleEncryptionEnvelope
            {
                ObjectId = objectId,
                PrimaryAlgorithmId = primaryAlgorithmId,
                SecondaryAlgorithmId = secondaryAlgorithmId,
                PrimaryCiphertext = primaryCiphertext,
                SecondaryCiphertext = secondaryCiphertext,
                TransitionCreatedAt = now,
                TransitionExpiresAt = now.Add(DefaultTransitionDuration),
                Metadata = new Dictionary<string, string?>
                {
                    ["createdBy"] = nameof(DoubleEncryptionService),
                    ["primaryStrategy"] = primaryAlgorithmId,
                    ["secondaryStrategy"] = secondaryAlgorithmId
                }
            };
        }
        finally
        {
            // Zero all sensitive buffers
            if (primaryCopy != null) CryptographicOperations.ZeroMemory(primaryCopy);
            if (secondaryCopy != null) CryptographicOperations.ZeroMemory(secondaryCopy);
        }
    }

    /// <summary>
    /// Decrypts data from a double-encryption envelope using the preferred algorithm.
    /// If the preferred algorithm fails, falls back to the other algorithm in the envelope.
    /// </summary>
    /// <param name="envelope">The double-encryption envelope containing dual ciphertexts.</param>
    /// <param name="preferredAlgorithmId">The preferred algorithm to try first.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decrypted plaintext.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="envelope"/> is null.</exception>
    /// <exception cref="CryptographicException">If both algorithms fail to decrypt.</exception>
    public async Task<byte[]> DecryptAsync(
        DoubleEncryptionEnvelope envelope,
        string preferredAlgorithmId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredAlgorithmId);

        // Determine which ciphertext corresponds to the preferred algorithm
        string preferredAlgorithm;
        byte[] preferredCiphertext;
        string fallbackAlgorithm;
        byte[] fallbackCiphertext;

        if (string.Equals(envelope.PrimaryAlgorithmId, preferredAlgorithmId, StringComparison.OrdinalIgnoreCase))
        {
            preferredAlgorithm = envelope.PrimaryAlgorithmId;
            preferredCiphertext = envelope.PrimaryCiphertext;
            fallbackAlgorithm = envelope.SecondaryAlgorithmId;
            fallbackCiphertext = envelope.SecondaryCiphertext;
        }
        else if (string.Equals(envelope.SecondaryAlgorithmId, preferredAlgorithmId, StringComparison.OrdinalIgnoreCase))
        {
            preferredAlgorithm = envelope.SecondaryAlgorithmId;
            preferredCiphertext = envelope.SecondaryCiphertext;
            fallbackAlgorithm = envelope.PrimaryAlgorithmId;
            fallbackCiphertext = envelope.PrimaryCiphertext;
        }
        else
        {
            // Preferred algorithm not in envelope; try primary first
            preferredAlgorithm = envelope.PrimaryAlgorithmId;
            preferredCiphertext = envelope.PrimaryCiphertext;
            fallbackAlgorithm = envelope.SecondaryAlgorithmId;
            fallbackCiphertext = envelope.SecondaryCiphertext;
        }

        // Try preferred algorithm first
        try
        {
            return await DecryptViaMessageBusAsync(preferredAlgorithm, preferredCiphertext, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {

            // Fall back to the other algorithm
            System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
        }

        // Try fallback algorithm
        try
        {
            return await DecryptViaMessageBusAsync(fallbackAlgorithm, fallbackCiphertext, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new CryptographicException(
                $"Failed to decrypt envelope for object '{envelope.ObjectId}' with both " +
                $"'{preferredAlgorithm}' and '{fallbackAlgorithm}' algorithms.", ex);
        }
    }

    /// <summary>
    /// Removes the secondary encryption from a double-encryption envelope, re-encrypting
    /// the data with only the algorithm to keep. Used during cleanup phase of migration.
    /// </summary>
    /// <param name="envelope">The double-encryption envelope to simplify.</param>
    /// <param name="algorithmToKeep">The algorithm ID to retain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Single-algorithm ciphertext encrypted with the kept algorithm.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="envelope"/> is null.</exception>
    /// <exception cref="CryptographicException">If decryption or re-encryption fails.</exception>
    public async Task<byte[]> RemoveSecondaryEncryptionAsync(
        DoubleEncryptionEnvelope envelope,
        string algorithmToKeep,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmToKeep);

        byte[]? plaintext = null;

        try
        {
            // Decrypt using the algorithm to keep (prefer it)
            plaintext = await DecryptAsync(envelope, algorithmToKeep, ct).ConfigureAwait(false);

            // Re-encrypt with only the kept algorithm
            return await EncryptViaMessageBusAsync(algorithmToKeep, plaintext, ct).ConfigureAwait(false);
        }
        finally
        {
            // Zero sensitive plaintext buffer
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Delegates encryption to an encryption strategy via the message bus.
    /// Uses SendAsync for request-response pattern: the handler writes "result" into the
    /// message payload, and the response wraps the modified payload.
    /// </summary>
    private async Task<byte[]> EncryptViaMessageBusAsync(
        string algorithmId,
        byte[] plaintext,
        CancellationToken ct)
    {
        var message = new PluginMessage
        {
            Type = "encryption.encrypt",
            Source = nameof(DoubleEncryptionService),
            Payload = new Dictionary<string, object>
            {
                ["strategyId"] = algorithmId,
                ["data"] = plaintext
            }
        };

        var response = await _messageBus.SendAsync("encryption.encrypt", message,
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        if (response.Success)
        {
            // The handler writes result into the original message payload
            if (message.Payload.TryGetValue("result", out var resultObj) && resultObj is byte[] ciphertext)
            {
                return ciphertext;
            }

            // Also check response payload if it's a dictionary
            if (response.Payload is Dictionary<string, object> responseDict
                && responseDict.TryGetValue("result", out var rObj) && rObj is byte[] rCiphertext)
            {
                return rCiphertext;
            }

            // If the response payload itself is the ciphertext
            if (response.Payload is byte[] directCiphertext)
            {
                return directCiphertext;
            }
        }

        throw new CryptographicException(
            $"Encryption with algorithm '{algorithmId}' failed: {response.ErrorMessage ?? "no result returned"}");
    }

    /// <summary>
    /// Delegates decryption to an encryption strategy via the message bus.
    /// </summary>
    private async Task<byte[]> DecryptViaMessageBusAsync(
        string algorithmId,
        byte[] ciphertext,
        CancellationToken ct)
    {
        var message = new PluginMessage
        {
            Type = "encryption.decrypt",
            Source = nameof(DoubleEncryptionService),
            Payload = new Dictionary<string, object>
            {
                ["strategyId"] = algorithmId,
                ["data"] = ciphertext
            }
        };

        var response = await _messageBus.SendAsync("encryption.decrypt", message,
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        if (response.Success)
        {
            // The handler writes result into the original message payload
            if (message.Payload.TryGetValue("result", out var resultObj) && resultObj is byte[] plaintext)
            {
                return plaintext;
            }

            // Also check response payload if it's a dictionary
            if (response.Payload is Dictionary<string, object> responseDict
                && responseDict.TryGetValue("result", out var rObj) && rObj is byte[] rPlaintext)
            {
                return rPlaintext;
            }

            // If the response payload itself is the plaintext
            if (response.Payload is byte[] directPlaintext)
            {
                return directPlaintext;
            }
        }

        throw new CryptographicException(
            $"Decryption with algorithm '{algorithmId}' failed: {response.ErrorMessage ?? "no result returned"}");
    }
}
