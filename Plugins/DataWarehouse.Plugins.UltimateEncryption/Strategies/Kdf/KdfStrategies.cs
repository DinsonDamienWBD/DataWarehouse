using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using Konscious.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Kdf
{
    /// <summary>
    /// Argon2id key derivation strategy - Winner of Password Hashing Competition.
    ///
    /// Argon2id is a hybrid of Argon2i (side-channel resistant) and Argon2d (GPU resistant).
    /// It provides the best of both worlds: resistance against timing attacks AND
    /// resistance against GPU/ASIC cracking.
    ///
    /// Security Level: High
    /// Use Case: Password hashing, key derivation from passwords
    ///
    /// Parameters:
    /// - Memory: 64 MB (65536 KB)
    /// - Iterations: 3
    /// - Parallelism: 4
    ///
    /// Note: This is NOT an encryption strategy - it's a key derivation function.
    /// Use it to derive encryption keys from passwords.
    /// </summary>
    public sealed class Argon2idKdfStrategy : EncryptionStrategyBase
    {
        private readonly int _memoryKb;
        private readonly int _iterations;
        private readonly int _parallelism;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "Argon2id-KDF",
            KeySizeBits = 256, // Default output key size
            BlockSizeBytes = 0,
            IvSizeBytes = 16, // Salt size
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = true,
                MinimumSecurityLevel = SecurityLevel.High
            },
            SecurityLevel = SecurityLevel.High,
            Parameters = new Dictionary<string, object>
            {
                ["Type"] = "Argon2id",
                ["MemoryKb"] = _memoryKb,
                ["Iterations"] = _iterations,
                ["Parallelism"] = _parallelism,
                ["OutputLength"] = 32
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "argon2id-kdf";

        /// <inheritdoc/>
        public override string StrategyName => "Argon2id Key Derivation Function";

        public Argon2idKdfStrategy(int memoryKb = 65536, int iterations = 3, int parallelism = 4)
        {
            _memoryKb = memoryKb;
            _iterations = iterations;
            _parallelism = parallelism;
        }

        /// <summary>
        /// Derives a key from a password using Argon2id.
        /// Input: password (plaintext), key (salt), associatedData (optional additional data)
        /// Output: [Salt:16][DerivedKey:32]
        /// </summary>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // plaintext is the password, key is the salt (or generate if empty)
                var password = plaintext;
                var salt = key.Length >= 16 ? key : GenerateSalt(16);

                using var argon2 = new Argon2id(password);
                argon2.Salt = salt;
                argon2.MemorySize = _memoryKb;
                argon2.Iterations = _iterations;
                argon2.DegreeOfParallelism = _parallelism;

                if (associatedData != null && associatedData.Length > 0)
                {
                    argon2.AssociatedData = associatedData;
                }

                var derivedKey = argon2.GetBytes(32);

                // Return salt + derived key
                var result = new byte[salt.Length + derivedKey.Length];
                Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
                Buffer.BlockCopy(derivedKey, 0, result, salt.Length, derivedKey.Length);

                return result;
            }, cancellationToken);
        }

        /// <summary>
        /// Verifies a password against a stored hash.
        /// Input: password (ciphertext param), stored hash+salt (key param)
        /// Output: password if verification succeeds, throws if fails
        /// </summary>
        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // ciphertext is the password to verify
                // key is the stored [Salt:16][DerivedKey:32]
                if (key.Length < 48)
                    throw new CryptographicException("Invalid stored hash format");

                var salt = new byte[16];
                var storedHash = new byte[32];
                Buffer.BlockCopy(key, 0, salt, 0, 16);
                Buffer.BlockCopy(key, 16, storedHash, 0, 32);

                using var argon2 = new Argon2id(ciphertext);
                argon2.Salt = salt;
                argon2.MemorySize = _memoryKb;
                argon2.Iterations = _iterations;
                argon2.DegreeOfParallelism = _parallelism;

                if (associatedData != null && associatedData.Length > 0)
                {
                    argon2.AssociatedData = associatedData;
                }

                var computedHash = argon2.GetBytes(32);

                if (!CryptographicOperations.FixedTimeEquals(computedHash, storedHash))
                {
                    throw new CryptographicException("Password verification failed");
                }

                return ciphertext; // Return password if verification succeeds
            }, cancellationToken);
        }

        /// <summary>
        /// Generates a random salt for Argon2id.
        /// </summary>
        public override byte[] GenerateKey()
        {
            return GenerateSalt(16);
        }

        private static byte[] GenerateSalt(int length)
        {
            return RandomNumberGenerator.GetBytes(length);
        }
    }

    /// <summary>
    /// Argon2i key derivation strategy - Side-channel resistant variant.
    ///
    /// Argon2i is designed to be resistant against side-channel attacks.
    /// It uses data-independent memory access patterns, making it suitable
    /// for environments where timing attacks are a concern.
    ///
    /// Security Level: High
    /// Use Case: Key derivation in environments with side-channel attack risk
    /// </summary>
    public sealed class Argon2iKdfStrategy : EncryptionStrategyBase
    {
        private readonly int _memoryKb;
        private readonly int _iterations;
        private readonly int _parallelism;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "Argon2i-KDF",
            KeySizeBits = 256,
            BlockSizeBytes = 0,
            IvSizeBytes = 16,
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = true,
                MinimumSecurityLevel = SecurityLevel.High
            },
            SecurityLevel = SecurityLevel.High,
            Parameters = new Dictionary<string, object>
            {
                ["Type"] = "Argon2i",
                ["MemoryKb"] = _memoryKb,
                ["Iterations"] = _iterations,
                ["Parallelism"] = _parallelism
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "argon2i-kdf";

        /// <inheritdoc/>
        public override string StrategyName => "Argon2i Key Derivation Function";

        public Argon2iKdfStrategy(int memoryKb = 65536, int iterations = 3, int parallelism = 4)
        {
            _memoryKb = memoryKb;
            _iterations = iterations;
            _parallelism = parallelism;
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var password = plaintext;
                var salt = key.Length >= 16 ? key : RandomNumberGenerator.GetBytes(16);

                using var argon2 = new Argon2i(password);
                argon2.Salt = salt;
                argon2.MemorySize = _memoryKb;
                argon2.Iterations = _iterations;
                argon2.DegreeOfParallelism = _parallelism;

                if (associatedData != null && associatedData.Length > 0)
                {
                    argon2.AssociatedData = associatedData;
                }

                var derivedKey = argon2.GetBytes(32);

                var result = new byte[salt.Length + derivedKey.Length];
                Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
                Buffer.BlockCopy(derivedKey, 0, result, salt.Length, derivedKey.Length);

                return result;
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
                if (key.Length < 48)
                    throw new CryptographicException("Invalid stored hash format");

                var salt = new byte[16];
                var storedHash = new byte[32];
                Buffer.BlockCopy(key, 0, salt, 0, 16);
                Buffer.BlockCopy(key, 16, storedHash, 0, 32);

                using var argon2 = new Argon2i(ciphertext);
                argon2.Salt = salt;
                argon2.MemorySize = _memoryKb;
                argon2.Iterations = _iterations;
                argon2.DegreeOfParallelism = _parallelism;

                if (associatedData != null && associatedData.Length > 0)
                {
                    argon2.AssociatedData = associatedData;
                }

                var computedHash = argon2.GetBytes(32);

                if (!CryptographicOperations.FixedTimeEquals(computedHash, storedHash))
                {
                    throw new CryptographicException("Password verification failed");
                }

                return ciphertext;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            return RandomNumberGenerator.GetBytes(16);
        }
    }

    /// <summary>
    /// Argon2d key derivation strategy - GPU/ASIC resistant variant.
    ///
    /// Argon2d uses data-dependent memory access, making it highly resistant
    /// to GPU and ASIC attacks. However, it may be vulnerable to side-channel
    /// attacks in certain environments.
    ///
    /// Security Level: High
    /// Use Case: Server-side password hashing where side-channels are not a concern
    /// </summary>
    public sealed class Argon2dKdfStrategy : EncryptionStrategyBase
    {
        private readonly int _memoryKb;
        private readonly int _iterations;
        private readonly int _parallelism;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "Argon2d-KDF",
            KeySizeBits = 256,
            BlockSizeBytes = 0,
            IvSizeBytes = 16,
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = true,
                MinimumSecurityLevel = SecurityLevel.High
            },
            SecurityLevel = SecurityLevel.High,
            Parameters = new Dictionary<string, object>
            {
                ["Type"] = "Argon2d",
                ["MemoryKb"] = _memoryKb,
                ["Iterations"] = _iterations,
                ["Parallelism"] = _parallelism
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "argon2d-kdf";

        /// <inheritdoc/>
        public override string StrategyName => "Argon2d Key Derivation Function";

        public Argon2dKdfStrategy(int memoryKb = 65536, int iterations = 3, int parallelism = 4)
        {
            _memoryKb = memoryKb;
            _iterations = iterations;
            _parallelism = parallelism;
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var password = plaintext;
                var salt = key.Length >= 16 ? key : RandomNumberGenerator.GetBytes(16);

                using var argon2 = new Argon2d(password);
                argon2.Salt = salt;
                argon2.MemorySize = _memoryKb;
                argon2.Iterations = _iterations;
                argon2.DegreeOfParallelism = _parallelism;

                if (associatedData != null && associatedData.Length > 0)
                {
                    argon2.AssociatedData = associatedData;
                }

                var derivedKey = argon2.GetBytes(32);

                var result = new byte[salt.Length + derivedKey.Length];
                Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
                Buffer.BlockCopy(derivedKey, 0, result, salt.Length, derivedKey.Length);

                return result;
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
                if (key.Length < 48)
                    throw new CryptographicException("Invalid stored hash format");

                var salt = new byte[16];
                var storedHash = new byte[32];
                Buffer.BlockCopy(key, 0, salt, 0, 16);
                Buffer.BlockCopy(key, 16, storedHash, 0, 32);

                using var argon2 = new Argon2d(ciphertext);
                argon2.Salt = salt;
                argon2.MemorySize = _memoryKb;
                argon2.Iterations = _iterations;
                argon2.DegreeOfParallelism = _parallelism;

                if (associatedData != null && associatedData.Length > 0)
                {
                    argon2.AssociatedData = associatedData;
                }

                var computedHash = argon2.GetBytes(32);

                if (!CryptographicOperations.FixedTimeEquals(computedHash, storedHash))
                {
                    throw new CryptographicException("Password verification failed");
                }

                return ciphertext;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            return RandomNumberGenerator.GetBytes(16);
        }
    }

    /// <summary>
    /// scrypt key derivation strategy - Memory-hard KDF.
    ///
    /// scrypt is a password-based key derivation function designed to be
    /// computationally intensive and memory-hard, making it resistant to
    /// custom hardware attacks (ASICs/FPGAs).
    ///
    /// Security Level: High
    /// Algorithm: scrypt
    /// Parameters:
    /// - N (CPU/memory cost): 2^14 = 16384
    /// - r (block size): 8
    /// - p (parallelization): 1
    ///
    /// Use Case: Password storage, key derivation where memory-hardness is required
    /// </summary>
    public sealed class ScryptKdfStrategy : EncryptionStrategyBase
    {
        private readonly int _n; // CPU/memory cost parameter
        private readonly int _r; // Block size
        private readonly int _p; // Parallelization parameter

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "scrypt-KDF",
            KeySizeBits = 256,
            BlockSizeBytes = 0,
            IvSizeBytes = 32, // Salt size
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.High
            },
            SecurityLevel = SecurityLevel.High,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "scrypt",
                ["N"] = _n,
                ["r"] = _r,
                ["p"] = _p,
                ["OutputLength"] = 32
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "scrypt-kdf";

        /// <inheritdoc/>
        public override string StrategyName => "scrypt Key Derivation Function";

        public ScryptKdfStrategy(int n = 16384, int r = 8, int p = 1)
        {
            _n = n;
            _r = r;
            _p = p;
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var password = plaintext;
                var salt = key.Length >= 32 ? key : RandomNumberGenerator.GetBytes(32);

                var derivedKey = SCrypt.Generate(password, salt, _n, _r, _p, 32);

                var result = new byte[salt.Length + derivedKey.Length];
                Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
                Buffer.BlockCopy(derivedKey, 0, result, salt.Length, derivedKey.Length);

                return result;
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
                if (key.Length < 64)
                    throw new CryptographicException("Invalid stored hash format");

                var salt = new byte[32];
                var storedHash = new byte[32];
                Buffer.BlockCopy(key, 0, salt, 0, 32);
                Buffer.BlockCopy(key, 32, storedHash, 0, 32);

                var computedHash = SCrypt.Generate(ciphertext, salt, _n, _r, _p, 32);

                if (!CryptographicOperations.FixedTimeEquals(computedHash, storedHash))
                {
                    throw new CryptographicException("Password verification failed");
                }

                return ciphertext;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            return RandomNumberGenerator.GetBytes(32);
        }
    }

    /// <summary>
    /// bcrypt key derivation strategy - Blowfish-based password hashing.
    ///
    /// bcrypt is a password hashing function based on the Blowfish cipher.
    /// It includes a salt and a cost factor that makes brute-force attacks
    /// computationally expensive.
    ///
    /// Security Level: High
    /// Algorithm: bcrypt
    /// Cost Factor: 12 (2^12 iterations)
    ///
    /// Use Case: Password storage, legacy system compatibility
    /// Note: For new systems, prefer Argon2id.
    /// </summary>
    public sealed class BcryptKdfStrategy : EncryptionStrategyBase
    {
        private readonly int _cost;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "bcrypt-KDF",
            KeySizeBits = 184, // bcrypt produces 23 bytes (184 bits) + 16 byte salt
            BlockSizeBytes = 0,
            IvSizeBytes = 16, // Salt size
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.High
            },
            SecurityLevel = SecurityLevel.High,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "bcrypt",
                ["Cost"] = _cost,
                ["SaltSize"] = 16,
                ["HashSize"] = 23
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "bcrypt-kdf";

        /// <inheritdoc/>
        public override string StrategyName => "bcrypt Key Derivation Function";

        public BcryptKdfStrategy(int cost = 12)
        {
            _cost = cost;
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // bcrypt via BouncyCastle
                var password = plaintext;
                var salt = key.Length >= 16 ? key[..16] : RandomNumberGenerator.GetBytes(16);

                // OpenBSD bcrypt format - produces 24 bytes but last byte is always 0
                var hash = BCrypt.Generate(password, salt, _cost);

                // Return salt + hash
                var result = new byte[salt.Length + hash.Length];
                Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
                Buffer.BlockCopy(hash, 0, result, salt.Length, hash.Length);

                return result;
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
                // key contains [salt:16][hash:24]
                if (key.Length < 40)
                    throw new CryptographicException("Invalid stored hash format");

                var salt = new byte[16];
                var storedHash = new byte[24];
                Buffer.BlockCopy(key, 0, salt, 0, 16);
                Buffer.BlockCopy(key, 16, storedHash, 0, 24);

                var computedHash = BCrypt.Generate(ciphertext, salt, _cost);

                if (!CryptographicOperations.FixedTimeEquals(computedHash, storedHash))
                {
                    throw new CryptographicException("Password verification failed");
                }

                return ciphertext;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            return RandomNumberGenerator.GetBytes(16);
        }
    }

    /// <summary>
    /// HKDF-SHA256 key derivation strategy - HMAC-based Extract-and-Expand KDF.
    ///
    /// HKDF is a key derivation function based on HMAC. It is suitable for
    /// deriving keys from existing high-entropy key material (not passwords).
    ///
    /// Security Level: High
    /// Algorithm: HKDF-SHA256 (RFC 5869)
    ///
    /// Use Case: Key derivation from Diffie-Hellman shared secrets, expanding keys
    /// Note: Do NOT use for password hashing - use Argon2id instead.
    /// </summary>
    public sealed class HkdfSha256Strategy : EncryptionStrategyBase
    {
        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "HKDF-SHA256",
            KeySizeBits = 256,
            BlockSizeBytes = 0,
            IvSizeBytes = 32, // Salt size
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = true,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.High
            },
            SecurityLevel = SecurityLevel.High,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "HKDF-SHA256",
                ["HashFunction"] = "SHA256",
                ["OutputLength"] = 32
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "hkdf-sha256";

        /// <inheritdoc/>
        public override string StrategyName => "HKDF-SHA256 Key Derivation";

        protected override Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            // plaintext = input key material
            // key = salt
            // associatedData = info
            var salt = key.Length > 0 ? key : new byte[32];
            var info = associatedData ?? Array.Empty<byte>();

            var derivedKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                plaintext,
                32,
                salt,
                info);

            // Return salt + derived key
            var result = new byte[salt.Length + derivedKey.Length];
            Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
            Buffer.BlockCopy(derivedKey, 0, result, salt.Length, derivedKey.Length);

            return Task.FromResult(result);
        }

        protected override Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            // HKDF is deterministic - same inputs produce same output
            // ciphertext = input key material
            // key = expected [salt:32][derivedKey:32]
            if (key.Length < 64)
                throw new CryptographicException("Invalid key format");

            var salt = new byte[32];
            var expectedKey = new byte[32];
            Buffer.BlockCopy(key, 0, salt, 0, 32);
            Buffer.BlockCopy(key, 32, expectedKey, 0, 32);

            var info = associatedData ?? Array.Empty<byte>();
            var computedKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ciphertext,
                32,
                salt,
                info);

            if (!CryptographicOperations.FixedTimeEquals(computedKey, expectedKey))
            {
                throw new CryptographicException("Key derivation mismatch");
            }

            return Task.FromResult(computedKey);
        }

        public override byte[] GenerateKey()
        {
            return RandomNumberGenerator.GetBytes(32);
        }
    }

    /// <summary>
    /// HKDF-SHA512 key derivation strategy - Higher security variant.
    ///
    /// Same as HKDF-SHA256 but uses SHA-512 for higher security margin.
    /// Suitable for deriving longer keys or when SHA-512 is required.
    ///
    /// Security Level: High
    /// Algorithm: HKDF-SHA512 (RFC 5869)
    /// </summary>
    public sealed class HkdfSha512Strategy : EncryptionStrategyBase
    {
        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "HKDF-SHA512",
            KeySizeBits = 512,
            BlockSizeBytes = 0,
            IvSizeBytes = 64, // Salt size
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = true,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.High
            },
            SecurityLevel = SecurityLevel.High,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "HKDF-SHA512",
                ["HashFunction"] = "SHA512",
                ["OutputLength"] = 64
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "hkdf-sha512";

        /// <inheritdoc/>
        public override string StrategyName => "HKDF-SHA512 Key Derivation";

        protected override Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            var salt = key.Length > 0 ? key : new byte[64];
            var info = associatedData ?? Array.Empty<byte>();

            var derivedKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA512,
                plaintext,
                64,
                salt,
                info);

            var result = new byte[salt.Length + derivedKey.Length];
            Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
            Buffer.BlockCopy(derivedKey, 0, result, salt.Length, derivedKey.Length);

            return Task.FromResult(result);
        }

        protected override Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            if (key.Length < 128)
                throw new CryptographicException("Invalid key format");

            var salt = new byte[64];
            var expectedKey = new byte[64];
            Buffer.BlockCopy(key, 0, salt, 0, 64);
            Buffer.BlockCopy(key, 64, expectedKey, 0, 64);

            var info = associatedData ?? Array.Empty<byte>();
            var computedKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA512,
                ciphertext,
                64,
                salt,
                info);

            if (!CryptographicOperations.FixedTimeEquals(computedKey, expectedKey))
            {
                throw new CryptographicException("Key derivation mismatch");
            }

            return Task.FromResult(computedKey);
        }

        public override byte[] GenerateKey()
        {
            return RandomNumberGenerator.GetBytes(64);
        }
    }

    /// <summary>
    /// PBKDF2-SHA256 key derivation strategy - Password-Based KDF.
    ///
    /// PBKDF2 is a widely-deployed password-based key derivation function.
    /// While Argon2id is preferred for new systems, PBKDF2 is still acceptable
    /// for FIPS compliance and legacy system compatibility.
    ///
    /// Security Level: Standard
    /// Algorithm: PBKDF2-HMAC-SHA256
    /// Iterations: 600,000 (OWASP 2023 recommendation)
    ///
    /// Use Case: FIPS compliance, legacy system compatibility
    /// </summary>
    public sealed class Pbkdf2Sha256Strategy : EncryptionStrategyBase
    {
        private readonly int _iterations;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "PBKDF2-SHA256",
            KeySizeBits = 256,
            BlockSizeBytes = 0,
            IvSizeBytes = 32, // Salt size
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = true,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.Standard
            },
            SecurityLevel = SecurityLevel.Standard,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "PBKDF2-HMAC-SHA256",
                ["Iterations"] = _iterations,
                ["OutputLength"] = 32
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "pbkdf2-sha256";

        /// <inheritdoc/>
        public override string StrategyName => "PBKDF2-SHA256 Key Derivation";

        public Pbkdf2Sha256Strategy(int iterations = 600000)
        {
            _iterations = iterations;
        }

        protected override Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            var password = plaintext;
            var salt = key.Length >= 32 ? key : RandomNumberGenerator.GetBytes(32);

            var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                _iterations,
                HashAlgorithmName.SHA256,
                32);

            var result = new byte[salt.Length + derivedKey.Length];
            Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
            Buffer.BlockCopy(derivedKey, 0, result, salt.Length, derivedKey.Length);

            return Task.FromResult(result);
        }

        protected override Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            if (key.Length < 64)
                throw new CryptographicException("Invalid stored hash format");

            var salt = new byte[32];
            var storedHash = new byte[32];
            Buffer.BlockCopy(key, 0, salt, 0, 32);
            Buffer.BlockCopy(key, 32, storedHash, 0, 32);

            var password = ciphertext;
            var computedHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                _iterations,
                HashAlgorithmName.SHA256,
                32);

            if (!CryptographicOperations.FixedTimeEquals(computedHash, storedHash))
            {
                throw new CryptographicException("Password verification failed");
            }

            return Task.FromResult(ciphertext);
        }

        public override byte[] GenerateKey()
        {
            return RandomNumberGenerator.GetBytes(32);
        }
    }

    /// <summary>
    /// PBKDF2-SHA512 key derivation strategy - Higher security variant.
    ///
    /// Same as PBKDF2-SHA256 but uses SHA-512 for higher security margin.
    ///
    /// Security Level: High
    /// Algorithm: PBKDF2-HMAC-SHA512
    /// Iterations: 210,000 (OWASP 2023 recommendation for SHA-512)
    /// </summary>
    public sealed class Pbkdf2Sha512Strategy : EncryptionStrategyBase
    {
        private readonly int _iterations;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "PBKDF2-SHA512",
            KeySizeBits = 512,
            BlockSizeBytes = 0,
            IvSizeBytes = 64, // Salt size
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = true,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.High
            },
            SecurityLevel = SecurityLevel.High,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "PBKDF2-HMAC-SHA512",
                ["Iterations"] = _iterations,
                ["OutputLength"] = 64
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "pbkdf2-sha512";

        /// <inheritdoc/>
        public override string StrategyName => "PBKDF2-SHA512 Key Derivation";

        public Pbkdf2Sha512Strategy(int iterations = 210000)
        {
            _iterations = iterations;
        }

        protected override Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            var password = plaintext;
            var salt = key.Length >= 64 ? key : RandomNumberGenerator.GetBytes(64);

            var derivedKey = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                _iterations,
                HashAlgorithmName.SHA512,
                64);

            var result = new byte[salt.Length + derivedKey.Length];
            Buffer.BlockCopy(salt, 0, result, 0, salt.Length);
            Buffer.BlockCopy(derivedKey, 0, result, salt.Length, derivedKey.Length);

            return Task.FromResult(result);
        }

        protected override Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            if (key.Length < 128)
                throw new CryptographicException("Invalid stored hash format");

            var salt = new byte[64];
            var storedHash = new byte[64];
            Buffer.BlockCopy(key, 0, salt, 0, 64);
            Buffer.BlockCopy(key, 64, storedHash, 0, 64);

            var password = ciphertext;
            var computedHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                _iterations,
                HashAlgorithmName.SHA512,
                64);

            if (!CryptographicOperations.FixedTimeEquals(computedHash, storedHash))
            {
                throw new CryptographicException("Password verification failed");
            }

            return Task.FromResult(ciphertext);
        }

        public override byte[] GenerateKey()
        {
            return RandomNumberGenerator.GetBytes(64);
        }
    }
}
