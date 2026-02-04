using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Legacy
{
    /// <summary>
    /// WARNING: LEGACY CIPHER - Blowfish-448 with CBC mode and HMAC-SHA256.
    /// Blowfish is deprecated for new systems. Use AES-256-GCM for new implementations.
    /// Provided for compatibility with legacy encrypted data only.
    /// </summary>
    public sealed class BlowfishStrategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 448;
        private const int BlockSizeBytes = 8;
        private const int IvSizeBytes = 8;
        private const int HmacSizeBytes = 32; // SHA-256

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "Blowfish-448-CBC-HMAC",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = BlockSizeBytes,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = HmacSizeBytes,
            SecurityLevel = SecurityLevel.Standard,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.Standard
            },
            Parameters = new Dictionary<string, object>
            {
                ["Mode"] = "CBC",
                ["Padding"] = "PKCS7",
                ["MacAlgorithm"] = "HMAC-SHA256",
                ["Warning"] = "Legacy cipher - deprecated for new systems"
            }
        };

        public override string StrategyId => "blowfish-448-cbc";
        public override string StrategyName => "Blowfish-448 CBC with HMAC (Legacy)";

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // Generate IV
                var iv = GenerateIv();

                // Split key: 448 bits for Blowfish, 256 bits for HMAC
                var encKey = new byte[KeySizeBits / 8];
                var macKey = new byte[32];
                Buffer.BlockCopy(key, 0, encKey, 0, encKey.Length);

                using var hmac = new HMACSHA256(key);
                macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                // Encrypt with Blowfish-CBC
                var engine = new BlowfishEngine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(encKey);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(true, keyParamWithIv);

                var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
                var outputLen = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
                outputLen += cipher.DoFinal(ciphertext, outputLen);

                // Trim to actual size
                if (outputLen < ciphertext.Length)
                {
                    Array.Resize(ref ciphertext, outputLen);
                }

                // Compute HMAC over IV + ciphertext + associated data
                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(ciphertext, 0, ciphertext.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var tag = hmacEngine.Hash!;

                return CombineIvAndCiphertext(iv, ciphertext, tag);
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

                // Split key: 448 bits for Blowfish, 256 bits for HMAC
                var encKey = new byte[KeySizeBits / 8];
                var macKey = new byte[32];
                Buffer.BlockCopy(key, 0, encKey, 0, encKey.Length);

                using var hmac = new HMACSHA256(key);
                macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                // Verify HMAC
                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(encryptedData, 0, encryptedData.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var computedTag = hmacEngine.Hash!;

                if (!CryptographicOperations.FixedTimeEquals(tag!, computedTag))
                {
                    throw new CryptographicException("Authentication tag verification failed");
                }

                // Decrypt with Blowfish-CBC
                var engine = new BlowfishEngine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(encKey);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(false, keyParamWithIv);

                var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
                var outputLen = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);
                outputLen += cipher.DoFinal(plaintext, outputLen);

                // Trim to actual size
                if (outputLen < plaintext.Length)
                {
                    Array.Resize(ref plaintext, outputLen);
                }

                return plaintext;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            // Generate 448 bits for Blowfish + 256 bits for HMAC = 704 bits total
            // But we'll derive HMAC key from the main key, so just 448 bits
            return RandomNumberGenerator.GetBytes(KeySizeBits / 8);
        }
    }

    /// <summary>
    /// WARNING: LEGACY CIPHER - IDEA-128 with CBC mode and HMAC-SHA256.
    /// IDEA is deprecated and was used in legacy PGP implementations.
    /// Provided for compatibility with legacy encrypted data only.
    /// Use AES-256-GCM for new implementations.
    /// </summary>
    public sealed class IdeaStrategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 128;
        private const int BlockSizeBytes = 8;
        private const int IvSizeBytes = 8;
        private const int HmacSizeBytes = 32; // SHA-256

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "IDEA-128-CBC-HMAC",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = BlockSizeBytes,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = HmacSizeBytes,
            SecurityLevel = SecurityLevel.Standard,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.Standard
            },
            Parameters = new Dictionary<string, object>
            {
                ["Mode"] = "CBC",
                ["Padding"] = "PKCS7",
                ["MacAlgorithm"] = "HMAC-SHA256",
                ["Warning"] = "Legacy PGP cipher - deprecated for new systems"
            }
        };

        public override string StrategyId => "idea-128-cbc";
        public override string StrategyName => "IDEA-128 CBC with HMAC (Legacy PGP)";

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var iv = GenerateIv();

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                var engine = new IdeaEngine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(true, keyParamWithIv);

                var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
                var outputLen = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
                outputLen += cipher.DoFinal(ciphertext, outputLen);

                if (outputLen < ciphertext.Length)
                {
                    Array.Resize(ref ciphertext, outputLen);
                }

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(ciphertext, 0, ciphertext.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var tag = hmacEngine.Hash!;

                return CombineIvAndCiphertext(iv, ciphertext, tag);
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(encryptedData, 0, encryptedData.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var computedTag = hmacEngine.Hash!;

                if (!CryptographicOperations.FixedTimeEquals(tag!, computedTag))
                {
                    throw new CryptographicException("Authentication tag verification failed");
                }

                var engine = new IdeaEngine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(false, keyParamWithIv);

                var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
                var outputLen = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);
                outputLen += cipher.DoFinal(plaintext, outputLen);

                if (outputLen < plaintext.Length)
                {
                    Array.Resize(ref plaintext, outputLen);
                }

                return plaintext;
            }, cancellationToken);
        }
    }

    /// <summary>
    /// WARNING: LEGACY CIPHER - CAST5 (CAST-128) with CBC mode and HMAC-SHA256.
    /// CAST5 is deprecated and was used in OpenPGP implementations.
    /// Provided for compatibility with legacy encrypted data only.
    /// Use AES-256-GCM for new implementations.
    /// </summary>
    public sealed class Cast5Strategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 128;
        private const int BlockSizeBytes = 8;
        private const int IvSizeBytes = 8;
        private const int HmacSizeBytes = 32;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "CAST5-128-CBC-HMAC",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = BlockSizeBytes,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = HmacSizeBytes,
            SecurityLevel = SecurityLevel.Standard,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.Standard
            },
            Parameters = new Dictionary<string, object>
            {
                ["Mode"] = "CBC",
                ["Padding"] = "PKCS7",
                ["MacAlgorithm"] = "HMAC-SHA256",
                ["Warning"] = "Legacy OpenPGP cipher - deprecated for new systems"
            }
        };

        public override string StrategyId => "cast5-128-cbc";
        public override string StrategyName => "CAST5-128 CBC with HMAC (Legacy OpenPGP)";

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var iv = GenerateIv();

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                var engine = new Cast5Engine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(true, keyParamWithIv);

                var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
                var outputLen = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
                outputLen += cipher.DoFinal(ciphertext, outputLen);

                if (outputLen < ciphertext.Length)
                {
                    Array.Resize(ref ciphertext, outputLen);
                }

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(ciphertext, 0, ciphertext.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var tag = hmacEngine.Hash!;

                return CombineIvAndCiphertext(iv, ciphertext, tag);
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(encryptedData, 0, encryptedData.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var computedTag = hmacEngine.Hash!;

                if (!CryptographicOperations.FixedTimeEquals(tag!, computedTag))
                {
                    throw new CryptographicException("Authentication tag verification failed");
                }

                var engine = new Cast5Engine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(false, keyParamWithIv);

                var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
                var outputLen = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);
                outputLen += cipher.DoFinal(plaintext, outputLen);

                if (outputLen < plaintext.Length)
                {
                    Array.Resize(ref plaintext, outputLen);
                }

                return plaintext;
            }, cancellationToken);
        }
    }

    /// <summary>
    /// WARNING: LEGACY CIPHER - CAST6 (CAST-256) with CBC mode and HMAC-SHA256.
    /// CAST6 is less common than AES and should only be used for legacy compatibility.
    /// Use AES-256-GCM for new implementations.
    /// </summary>
    public sealed class Cast6Strategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 256;
        private const int BlockSizeBytes = 16;
        private const int IvSizeBytes = 16;
        private const int HmacSizeBytes = 32;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "CAST6-256-CBC-HMAC",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = BlockSizeBytes,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = HmacSizeBytes,
            SecurityLevel = SecurityLevel.Standard,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.Standard
            },
            Parameters = new Dictionary<string, object>
            {
                ["Mode"] = "CBC",
                ["Padding"] = "PKCS7",
                ["MacAlgorithm"] = "HMAC-SHA256",
                ["Warning"] = "Legacy cipher - prefer AES-256-GCM"
            }
        };

        public override string StrategyId => "cast6-256-cbc";
        public override string StrategyName => "CAST6-256 CBC with HMAC (Legacy)";

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var iv = GenerateIv();

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                var engine = new Cast6Engine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(true, keyParamWithIv);

                var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
                var outputLen = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
                outputLen += cipher.DoFinal(ciphertext, outputLen);

                if (outputLen < ciphertext.Length)
                {
                    Array.Resize(ref ciphertext, outputLen);
                }

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(ciphertext, 0, ciphertext.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var tag = hmacEngine.Hash!;

                return CombineIvAndCiphertext(iv, ciphertext, tag);
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(encryptedData, 0, encryptedData.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var computedTag = hmacEngine.Hash!;

                if (!CryptographicOperations.FixedTimeEquals(tag!, computedTag))
                {
                    throw new CryptographicException("Authentication tag verification failed");
                }

                var engine = new Cast6Engine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(false, keyParamWithIv);

                var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
                var outputLen = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);
                outputLen += cipher.DoFinal(plaintext, outputLen);

                if (outputLen < plaintext.Length)
                {
                    Array.Resize(ref plaintext, outputLen);
                }

                return plaintext;
            }, cancellationToken);
        }
    }

    /// <summary>
    /// WARNING: LEGACY CIPHER - RC5-256 with 12 rounds, CBC mode and HMAC-SHA256.
    /// RC5 is patented and deprecated. Use AES-256-GCM for new implementations.
    /// Provided for compatibility with legacy encrypted data only.
    /// </summary>
    public sealed class Rc5Strategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 256;
        private const int BlockSizeBytes = 8;
        private const int IvSizeBytes = 8;
        private const int HmacSizeBytes = 32;
        private const int Rounds = 12;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "RC5-256-CBC-HMAC",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = BlockSizeBytes,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = HmacSizeBytes,
            SecurityLevel = SecurityLevel.Standard,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.Standard
            },
            Parameters = new Dictionary<string, object>
            {
                ["Mode"] = "CBC",
                ["Padding"] = "PKCS7",
                ["MacAlgorithm"] = "HMAC-SHA256",
                ["Rounds"] = Rounds,
                ["Warning"] = "Legacy patented cipher - deprecated for new systems"
            }
        };

        public override string StrategyId => "rc5-256-cbc";
        public override string StrategyName => "RC5-256 CBC with HMAC (Legacy)";

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var iv = GenerateIv();

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                var engine = new RC532Engine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(true, keyParamWithIv);

                var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
                var outputLen = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
                outputLen += cipher.DoFinal(ciphertext, outputLen);

                if (outputLen < ciphertext.Length)
                {
                    Array.Resize(ref ciphertext, outputLen);
                }

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(ciphertext, 0, ciphertext.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var tag = hmacEngine.Hash!;

                return CombineIvAndCiphertext(iv, ciphertext, tag);
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(encryptedData, 0, encryptedData.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var computedTag = hmacEngine.Hash!;

                if (!CryptographicOperations.FixedTimeEquals(tag!, computedTag))
                {
                    throw new CryptographicException("Authentication tag verification failed");
                }

                var engine = new RC532Engine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(false, keyParamWithIv);

                var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
                var outputLen = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);
                outputLen += cipher.DoFinal(plaintext, outputLen);

                if (outputLen < plaintext.Length)
                {
                    Array.Resize(ref plaintext, outputLen);
                }

                return plaintext;
            }, cancellationToken);
        }
    }

    /// <summary>
    /// WARNING: LEGACY CIPHER - RC6-256 with 20 rounds, CBC mode and HMAC-SHA256.
    /// RC6 is an AES finalist but less tested than AES. Use AES-256-GCM for new implementations.
    /// Provided for compatibility with legacy encrypted data only.
    /// </summary>
    public sealed class Rc6Strategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 256;
        private const int BlockSizeBytes = 16;
        private const int IvSizeBytes = 16;
        private const int HmacSizeBytes = 32;
        private const int Rounds = 20;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "RC6-256-CBC-HMAC",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = BlockSizeBytes,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = HmacSizeBytes,
            SecurityLevel = SecurityLevel.Standard,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.Standard
            },
            Parameters = new Dictionary<string, object>
            {
                ["Mode"] = "CBC",
                ["Padding"] = "PKCS7",
                ["MacAlgorithm"] = "HMAC-SHA256",
                ["Rounds"] = Rounds,
                ["Warning"] = "Legacy AES finalist - prefer AES-256-GCM"
            }
        };

        public override string StrategyId => "rc6-256-cbc";
        public override string StrategyName => "RC6-256 CBC with HMAC (Legacy)";

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var iv = GenerateIv();

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                var engine = new RC6Engine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(true, keyParamWithIv);

                var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
                var outputLen = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
                outputLen += cipher.DoFinal(ciphertext, outputLen);

                if (outputLen < ciphertext.Length)
                {
                    Array.Resize(ref ciphertext, outputLen);
                }

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(ciphertext, 0, ciphertext.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var tag = hmacEngine.Hash!;

                return CombineIvAndCiphertext(iv, ciphertext, tag);
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(encryptedData, 0, encryptedData.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var computedTag = hmacEngine.Hash!;

                if (!CryptographicOperations.FixedTimeEquals(tag!, computedTag))
                {
                    throw new CryptographicException("Authentication tag verification failed");
                }

                var engine = new RC6Engine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(false, keyParamWithIv);

                var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
                var outputLen = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);
                outputLen += cipher.DoFinal(plaintext, outputLen);

                if (outputLen < plaintext.Length)
                {
                    Array.Resize(ref plaintext, outputLen);
                }

                return plaintext;
            }, cancellationToken);
        }
    }

    /// <summary>
    /// DANGER: NOT SECURE - DES-64 with CBC mode and HMAC-SHA256.
    /// DES has a 56-bit effective key size and is completely broken by modern standards.
    /// FOR RESEARCH AND LEGACY COMPATIBILITY ONLY - NEVER USE IN PRODUCTION.
    /// Use AES-256-GCM for any real security requirements.
    /// </summary>
    public sealed class DesStrategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 64; // 56-bit effective key + 8 parity bits
        private const int BlockSizeBytes = 8;
        private const int IvSizeBytes = 8;
        private const int HmacSizeBytes = 32;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "DES-64-CBC-HMAC",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = BlockSizeBytes,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = HmacSizeBytes,
            SecurityLevel = SecurityLevel.Experimental,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.Experimental
            },
            Parameters = new Dictionary<string, object>
            {
                ["Mode"] = "CBC",
                ["Padding"] = "PKCS7",
                ["MacAlgorithm"] = "HMAC-SHA256",
                ["Warning"] = "INSECURE: DES is completely broken - for research/legacy only"
            }
        };

        public override string StrategyId => "des-64-cbc";
        public override string StrategyName => "DES-64 CBC with HMAC (INSECURE - Experimental Only)";

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var iv = GenerateIv();

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                var engine = new DesEngine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(true, keyParamWithIv);

                var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
                var outputLen = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
                outputLen += cipher.DoFinal(ciphertext, outputLen);

                if (outputLen < ciphertext.Length)
                {
                    Array.Resize(ref ciphertext, outputLen);
                }

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(ciphertext, 0, ciphertext.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var tag = hmacEngine.Hash!;

                return CombineIvAndCiphertext(iv, ciphertext, tag);
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(encryptedData, 0, encryptedData.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var computedTag = hmacEngine.Hash!;

                if (!CryptographicOperations.FixedTimeEquals(tag!, computedTag))
                {
                    throw new CryptographicException("Authentication tag verification failed");
                }

                var engine = new DesEngine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(false, keyParamWithIv);

                var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
                var outputLen = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);
                outputLen += cipher.DoFinal(plaintext, outputLen);

                if (outputLen < plaintext.Length)
                {
                    Array.Resize(ref plaintext, outputLen);
                }

                return plaintext;
            }, cancellationToken);
        }
    }

    /// <summary>
    /// WARNING: DEPRECATED - 3DES-168 with CBC mode and HMAC-SHA256.
    /// Triple DES is deprecated by NIST (SP 800-131A) and should only be used for legacy compatibility.
    /// Use AES-256-GCM for new implementations.
    /// </summary>
    public sealed class TripleDesStrategy : EncryptionStrategyBase
    {
        private const int KeySizeBits = 192; // 168-bit effective key + 24 parity bits
        private const int BlockSizeBytes = 8;
        private const int IvSizeBytes = 8;
        private const int HmacSizeBytes = 32;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "3DES-168-CBC-HMAC",
            KeySizeBits = KeySizeBits,
            BlockSizeBytes = BlockSizeBytes,
            IvSizeBytes = IvSizeBytes,
            TagSizeBytes = HmacSizeBytes,
            SecurityLevel = SecurityLevel.Standard,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.Standard
            },
            Parameters = new Dictionary<string, object>
            {
                ["Mode"] = "CBC",
                ["Padding"] = "PKCS7",
                ["MacAlgorithm"] = "HMAC-SHA256",
                ["Warning"] = "DEPRECATED by NIST - use only for legacy compatibility"
            }
        };

        public override string StrategyId => "3des-168-cbc";
        public override string StrategyName => "3DES-168 CBC with HMAC (Deprecated)";

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var iv = GenerateIv();

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                var engine = new DesEdeEngine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(true, keyParamWithIv);

                var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
                var outputLen = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
                outputLen += cipher.DoFinal(ciphertext, outputLen);

                if (outputLen < ciphertext.Length)
                {
                    Array.Resize(ref ciphertext, outputLen);
                }

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(ciphertext, 0, ciphertext.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var tag = hmacEngine.Hash!;

                return CombineIvAndCiphertext(iv, ciphertext, tag);
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

                using var hmac = new HMACSHA256(key);
                var macKey = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes("mac-key-derivation"));

                using var hmacEngine = new HMACSHA256(macKey);
                hmacEngine.TransformBlock(iv, 0, iv.Length, null, 0);
                hmacEngine.TransformBlock(encryptedData, 0, encryptedData.Length, null, 0);
                if (associatedData != null && associatedData.Length > 0)
                {
                    hmacEngine.TransformBlock(associatedData, 0, associatedData.Length, null, 0);
                }
                hmacEngine.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var computedTag = hmacEngine.Hash!;

                if (!CryptographicOperations.FixedTimeEquals(tag!, computedTag))
                {
                    throw new CryptographicException("Authentication tag verification failed");
                }

                var engine = new DesEdeEngine();
                var blockCipher = new CbcBlockCipher(engine);
                var cipher = new PaddedBufferedBlockCipher(blockCipher, new Pkcs7Padding());

                var keyParam = new KeyParameter(key);
                var keyParamWithIv = new ParametersWithIV(keyParam, iv);
                cipher.Init(false, keyParamWithIv);

                var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
                var outputLen = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);
                outputLen += cipher.DoFinal(plaintext, outputLen);

                if (outputLen < plaintext.Length)
                {
                    Array.Resize(ref plaintext, outputLen);
                }

                return plaintext;
            }, cancellationToken);
        }
    }
}
