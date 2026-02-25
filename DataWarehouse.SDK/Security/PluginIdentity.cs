using System;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.SDK.Security;

/// <summary>
/// Provides cryptographic identity for plugins in distributed environments (VALID-05).
/// Each plugin instance generates an asymmetric key pair on initialization.
/// The public key is registered with the kernel for message authentication.
/// Other nodes can verify a plugin's messages using its registered public key.
/// </summary>
public sealed class PluginIdentity : IDisposable
{
    private readonly RSA _keyPair;
    private bool _disposed;

    /// <summary>
    /// Gets the plugin ID this identity belongs to.
    /// </summary>
    public string PluginId { get; }

    /// <summary>
    /// Gets the public key bytes (DER-encoded SubjectPublicKeyInfo).
    /// This is shared with other nodes for verification.
    /// </summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// Gets the public key as a Base64 string for serialization.
    /// </summary>
    public string PublicKeyBase64 => Convert.ToBase64String(PublicKey);

    /// <summary>
    /// Gets when this identity was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Creates a new plugin identity with a fresh RSA-2048 key pair.
    /// </summary>
    /// <param name="pluginId">The plugin ID this identity belongs to.</param>
    /// <exception cref="ArgumentException">If pluginId is null or whitespace.</exception>
    public PluginIdentity(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        PluginId = pluginId;
        _keyPair = RSA.Create(2048);
        PublicKey = _keyPair.ExportSubjectPublicKeyInfo();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Creates a plugin identity from an existing key pair (for persistence/restoration).
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <param name="pkcs8PrivateKey">The PKCS#8-encoded private key bytes.</param>
    public PluginIdentity(string pluginId, byte[] pkcs8PrivateKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(pkcs8PrivateKey);

        PluginId = pluginId;
        _keyPair = RSA.Create();
        _keyPair.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        PublicKey = _keyPair.ExportSubjectPublicKeyInfo();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Signs a message payload, producing a digital signature.
    /// </summary>
    /// <param name="data">The data to sign.</param>
    /// <returns>The RSA-SHA256 signature bytes.</returns>
    public byte[] Sign(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);

        return _keyPair.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    /// <summary>
    /// Signs a string message, producing a Base64-encoded signature.
    /// </summary>
    /// <param name="message">The message to sign.</param>
    /// <returns>Base64-encoded RSA-SHA256 signature.</returns>
    public string SignMessage(string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(message);

        var data = Encoding.UTF8.GetBytes(message);
        var signature = Sign(data);
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Verifies a signature against a public key.
    /// Use this on the receiving end to authenticate a plugin's message.
    /// </summary>
    /// <param name="data">The original data that was signed.</param>
    /// <param name="signature">The signature to verify.</param>
    /// <param name="publicKey">The signer's public key (DER-encoded SubjectPublicKeyInfo).</param>
    /// <returns>True if the signature is valid.</returns>
    public static bool VerifySignature(byte[] data, byte[] signature, byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(publicKey);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    /// <summary>
    /// Verifies a Base64-encoded signature on a string message.
    /// </summary>
    /// <param name="message">The original message.</param>
    /// <param name="signatureBase64">Base64-encoded signature.</param>
    /// <param name="publicKeyBase64">Base64-encoded public key.</param>
    /// <returns>True if the signature is valid.</returns>
    public static bool VerifyMessage(string message, string signatureBase64, string publicKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signatureBase64);
        ArgumentNullException.ThrowIfNull(publicKeyBase64);

        var data = Encoding.UTF8.GetBytes(message);
        var signature = Convert.FromBase64String(signatureBase64);
        var publicKey = Convert.FromBase64String(publicKeyBase64);
        return VerifySignature(data, signature, publicKey);
    }

    /// <summary>
    /// Exports the private key for secure storage (PKCS#8 format).
    /// SECURITY: Handle with extreme care. Wipe from memory after use.
    /// </summary>
    /// <returns>PKCS#8-encoded private key bytes.</returns>
    public byte[] ExportPrivateKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _keyPair.ExportPkcs8PrivateKey();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _keyPair.Dispose();
        _disposed = true;
    }
}
