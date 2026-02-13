using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace DataWarehouse.SDK.Security
{
    /// <summary>
    /// Registry for cryptographic algorithm selection.
    /// Provides algorithm agility -- no hardcoded algorithm choices in SDK code.
    /// All algorithm selection goes through this registry, enabling runtime configuration.
    /// </summary>
    /// <remarks>
    /// Encryption algorithm agility already exists via IEncryptionStrategy/IEncryptionStrategyRegistry.
    /// This registry extends agility to hash algorithms and HMAC algorithms.
    /// </remarks>
    public interface ICryptographicAlgorithmRegistry
    {
        /// <summary>
        /// Gets the default hash algorithm name (e.g., SHA256, SHA384, SHA512).
        /// </summary>
        HashAlgorithmName DefaultHashAlgorithm { get; }

        /// <summary>
        /// Gets the default HMAC algorithm name.
        /// </summary>
        HashAlgorithmName DefaultHmacAlgorithm { get; }

        /// <summary>
        /// Gets all registered hash algorithm names.
        /// </summary>
        IReadOnlyList<HashAlgorithmName> SupportedHashAlgorithms { get; }

        /// <summary>
        /// Gets all registered HMAC algorithm names.
        /// </summary>
        IReadOnlyList<HashAlgorithmName> SupportedHmacAlgorithms { get; }

        /// <summary>
        /// Creates a hash algorithm instance by name.
        /// </summary>
        /// <param name="algorithmName">Algorithm name (e.g., SHA256).</param>
        /// <returns>A disposable hash algorithm instance.</returns>
        /// <exception cref="NotSupportedException">If the algorithm is not registered.</exception>
        HashAlgorithm CreateHashAlgorithm(HashAlgorithmName algorithmName);

        /// <summary>
        /// Creates an HMAC instance by algorithm name.
        /// </summary>
        /// <param name="algorithmName">HMAC algorithm name (e.g., SHA256 for HMAC-SHA256).</param>
        /// <param name="key">The HMAC key.</param>
        /// <returns>A disposable HMAC instance.</returns>
        /// <exception cref="NotSupportedException">If the algorithm is not registered.</exception>
        HMAC CreateHmac(HashAlgorithmName algorithmName, byte[] key);

        /// <summary>
        /// Computes a hash using the specified algorithm.
        /// Uses one-shot static APIs where available for zero allocation.
        /// </summary>
        /// <param name="algorithmName">Algorithm name.</param>
        /// <param name="data">Data to hash.</param>
        /// <returns>Hash bytes.</returns>
        byte[] ComputeHash(HashAlgorithmName algorithmName, ReadOnlySpan<byte> data);

        /// <summary>
        /// Computes an HMAC using the specified algorithm.
        /// Uses one-shot static APIs where available for zero allocation.
        /// </summary>
        /// <param name="algorithmName">HMAC algorithm name.</param>
        /// <param name="key">The HMAC key.</param>
        /// <param name="data">Data to authenticate.</param>
        /// <returns>HMAC bytes.</returns>
        byte[] ComputeHmac(HashAlgorithmName algorithmName, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data);

        /// <summary>
        /// Checks whether the specified algorithm is FIPS 140-3 approved.
        /// </summary>
        bool IsFipsApproved(HashAlgorithmName algorithmName);
    }

    /// <summary>
    /// Default implementation using .NET BCL cryptographic primitives.
    /// All algorithms are FIPS 140-3 compliant (System.Security.Cryptography).
    /// </summary>
    public class DefaultCryptographicAlgorithmRegistry : ICryptographicAlgorithmRegistry
    {
        /// <inheritdoc/>
        public HashAlgorithmName DefaultHashAlgorithm => HashAlgorithmName.SHA256;

        /// <inheritdoc/>
        public HashAlgorithmName DefaultHmacAlgorithm => HashAlgorithmName.SHA256;

        /// <inheritdoc/>
        public IReadOnlyList<HashAlgorithmName> SupportedHashAlgorithms { get; } = new[]
        {
            HashAlgorithmName.SHA256,
            HashAlgorithmName.SHA384,
            HashAlgorithmName.SHA512
        };

        /// <inheritdoc/>
        public IReadOnlyList<HashAlgorithmName> SupportedHmacAlgorithms { get; } = new[]
        {
            HashAlgorithmName.SHA256,
            HashAlgorithmName.SHA384,
            HashAlgorithmName.SHA512
        };

        /// <inheritdoc/>
        public HashAlgorithm CreateHashAlgorithm(HashAlgorithmName algorithmName)
        {
            if (algorithmName == HashAlgorithmName.SHA256) return SHA256.Create();
            if (algorithmName == HashAlgorithmName.SHA384) return SHA384.Create();
            if (algorithmName == HashAlgorithmName.SHA512) return SHA512.Create();
            throw new NotSupportedException($"Hash algorithm '{algorithmName.Name}' is not supported. Use SHA256, SHA384, or SHA512.");
        }

        /// <inheritdoc/>
        public HMAC CreateHmac(HashAlgorithmName algorithmName, byte[] key)
        {
            if (algorithmName == HashAlgorithmName.SHA256) return new HMACSHA256(key);
            if (algorithmName == HashAlgorithmName.SHA384) return new HMACSHA384(key);
            if (algorithmName == HashAlgorithmName.SHA512) return new HMACSHA512(key);
            throw new NotSupportedException($"HMAC algorithm '{algorithmName.Name}' is not supported. Use SHA256, SHA384, or SHA512.");
        }

        /// <inheritdoc/>
        public byte[] ComputeHash(HashAlgorithmName algorithmName, ReadOnlySpan<byte> data)
        {
            if (algorithmName == HashAlgorithmName.SHA256) return SHA256.HashData(data);
            if (algorithmName == HashAlgorithmName.SHA384) return SHA384.HashData(data);
            if (algorithmName == HashAlgorithmName.SHA512) return SHA512.HashData(data);
            throw new NotSupportedException($"Hash algorithm '{algorithmName.Name}' is not supported.");
        }

        /// <inheritdoc/>
        public byte[] ComputeHmac(HashAlgorithmName algorithmName, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
        {
            if (algorithmName == HashAlgorithmName.SHA256) return HMACSHA256.HashData(key, data);
            if (algorithmName == HashAlgorithmName.SHA384) return HMACSHA384.HashData(key, data);
            if (algorithmName == HashAlgorithmName.SHA512) return HMACSHA512.HashData(key, data);
            throw new NotSupportedException($"HMAC algorithm '{algorithmName.Name}' is not supported.");
        }

        /// <inheritdoc/>
        public bool IsFipsApproved(HashAlgorithmName algorithmName)
        {
            // All .NET BCL hash algorithms are FIPS 140-3 approved
            return algorithmName == HashAlgorithmName.SHA256
                || algorithmName == HashAlgorithmName.SHA384
                || algorithmName == HashAlgorithmName.SHA512;
        }
    }
}
