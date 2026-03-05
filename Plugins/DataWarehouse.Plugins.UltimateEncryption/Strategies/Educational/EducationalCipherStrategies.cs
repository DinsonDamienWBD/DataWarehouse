using DataWarehouse.SDK.Contracts.Encryption;
using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Educational
{
    /// <summary>
    /// EDUCATIONAL ONLY - Classic Caesar cipher with configurable shift.
    /// This is NOT secure and should NEVER be used for real encryption.
    /// Provided solely for educational purposes to demonstrate basic cipher concepts.
    /// Use AES-256-GCM for any real security requirements.
    /// </summary>
    public sealed class CaesarCipherStrategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 8; // Single byte for shift value (0-255)
        private const int IvSizeBytes = 0; // Caesar cipher doesn't use IV

        /// <inheritdoc/>
        public override string StrategyId => "caesar";

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("caesar.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("caesar.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <inheritdoc/>
        public override string StrategyName => "Caesar Cipher (EDUCATIONAL ONLY - NOT SECURE)";

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "Caesar Cipher",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = 1,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = 0,
            SecurityLevel = SecurityLevel.Experimental,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = true,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = true,
                MinimumSecurityLevel = SecurityLevel.Experimental
            },
            Parameters = new Dictionary<string, object>
            {
                ["Warning"] = "EDUCATIONAL ONLY - Completely insecure substitution cipher from ancient Rome",
                ["Description"] = "Simple shift cipher that replaces each byte with another byte shifted by key value",
                ["BrokenBy"] = "Frequency analysis, brute force (256 possible keys)"
            }
        };

        /// <inheritdoc/>
        protected override Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            // LOW-2960: trivial byte loop — no Task.Run overhead needed.
            var shift = key[0];
            var ciphertext = new byte[plaintext.Length];
            for (int i = 0; i < plaintext.Length; i++)
                ciphertext[i] = (byte)((plaintext[i] + shift) % 256);
            return Task.FromResult(ciphertext);
        }

        /// <inheritdoc/>
        protected override Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            // LOW-2960: trivial byte loop — no Task.Run overhead needed.
            var shift = key[0];
            var plaintext = new byte[ciphertext.Length];
            for (int i = 0; i < ciphertext.Length; i++)
                plaintext[i] = (byte)((ciphertext[i] - shift + 256) % 256);
            return Task.FromResult(plaintext);
        }

        /// <inheritdoc/>
        public override byte[] GenerateKey()
        {
            // Generate a random shift value (1-255, avoiding 0 which would be no encryption)
            var shift = RandomNumberGenerator.GetBytes(1)[0];
            if (shift == 0) shift = 1;
            return new byte[] { shift };
        }

        /// <inheritdoc/>
        public override byte[] GenerateIv()
        {
            // Caesar cipher doesn't use IV
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// EDUCATIONAL ONLY - Simple XOR cipher.
    /// This is NOT secure and should NEVER be used for real encryption.
    /// XOR ciphers can be broken with known-plaintext attacks and statistical analysis.
    /// Provided solely for educational purposes to demonstrate symmetric encryption basics.
    /// Use AES-256-GCM for any real security requirements.
    /// </summary>
    public sealed class XorCipherStrategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 256; // 32 bytes for better demonstration
        private const int IvSizeBytes = 0; // Simple XOR doesn't use IV (though it should!)

        /// <inheritdoc/>
        public override string StrategyId => "xor";

        /// <inheritdoc/>
        public override string StrategyName => "XOR Cipher (EDUCATIONAL ONLY - NOT SECURE)";

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "XOR Cipher",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = 1,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = 0,
            SecurityLevel = SecurityLevel.Experimental,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = true,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = true,
                MinimumSecurityLevel = SecurityLevel.Experimental
            },
            Parameters = new Dictionary<string, object>
            {
                ["Warning"] = "EDUCATIONAL ONLY - Vulnerable to known-plaintext attacks and pattern analysis",
                ["Description"] = "XORs each plaintext byte with corresponding key byte (repeating key as needed)",
                ["BrokenBy"] = "Known-plaintext attack, crib-dragging, frequency analysis if key is short"
            }
        };

        /// <inheritdoc/>
        protected override Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            // LOW-2960: trivial byte loop — no Task.Run overhead needed.
            var ciphertext = new byte[plaintext.Length];
            for (int i = 0; i < plaintext.Length; i++)
                ciphertext[i] = (byte)(plaintext[i] ^ key[i % key.Length]);
            return Task.FromResult(ciphertext);
        }

        /// <inheritdoc/>
        protected override Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            // LOW-2960: XOR is symmetric; trivial byte loop — no Task.Run overhead needed.
            var plaintext = new byte[ciphertext.Length];
            for (int i = 0; i < ciphertext.Length; i++)
                plaintext[i] = (byte)(ciphertext[i] ^ key[i % key.Length]);
            return Task.FromResult(plaintext);
        }

        /// <inheritdoc/>
        public override byte[] GenerateIv()
        {
            // Simple XOR doesn't use IV (this is one of its many weaknesses!)
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// EDUCATIONAL ONLY - Vigenère cipher (polyalphabetic substitution).
    /// This is NOT secure and should NEVER be used for real encryption.
    /// The Vigenère cipher was broken in the 19th century using the Kasiski examination.
    /// Provided solely for educational purposes to demonstrate polyalphabetic ciphers.
    /// Use AES-256-GCM for any real security requirements.
    /// </summary>
    public sealed class VigenereCipherStrategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 128; // 16 bytes for keyword
        private const int IvSizeBytes = 0; // Vigenère doesn't use IV

        /// <inheritdoc/>
        public override string StrategyId => "vigenere";

        /// <inheritdoc/>
        public override string StrategyName => "Vigenère Cipher (EDUCATIONAL ONLY - NOT SECURE)";

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "Vigenère Cipher",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = 1,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = 0,
            SecurityLevel = SecurityLevel.Experimental,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = true,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = true,
                MinimumSecurityLevel = SecurityLevel.Experimental
            },
            Parameters = new Dictionary<string, object>
            {
                ["Warning"] = "EDUCATIONAL ONLY - Broken by Kasiski examination and frequency analysis",
                ["Description"] = "Polyalphabetic substitution using repeating keyword - each position uses different Caesar shift",
                ["BrokenBy"] = "Kasiski examination, Index of Coincidence, Friedman test",
                ["HistoricalNote"] = "Considered unbreakable for 300+ years until Charles Babbage broke it in 1854"
            }
        };

        /// <inheritdoc/>
        protected override Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            // LOW-2960: trivial byte loop — no Task.Run overhead needed.
            var ciphertext = new byte[plaintext.Length];
            for (int i = 0; i < plaintext.Length; i++)
            {
                var shift = key[i % key.Length];
                ciphertext[i] = (byte)((plaintext[i] + shift) % 256);
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
            // LOW-2960: trivial byte loop — no Task.Run overhead needed.
            var plaintext = new byte[ciphertext.Length];
            for (int i = 0; i < ciphertext.Length; i++)
            {
                var shift = key[i % key.Length];
                plaintext[i] = (byte)((ciphertext[i] - shift + 256) % 256);
            }
            return Task.FromResult(plaintext);
        }

        /// <inheritdoc/>
        public override byte[] GenerateIv()
        {
            // Vigenère cipher doesn't use IV
            return Array.Empty<byte>();
        }
    }
}
