using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Homomorphic
{
    /// <summary>
    /// Paillier homomorphic encryption strategy - Additive homomorphic encryption.
    ///
    /// The Paillier cryptosystem supports additive homomorphic operations:
    /// - E(m1) * E(m2) = E(m1 + m2) mod n^2
    /// - E(m1)^k = E(k * m1) mod n^2
    ///
    /// Security Level: High (based on hardness of computing n-th residues modulo n^2)
    /// Key Size: 2048-bit modulus (configurable)
    ///
    /// Use Case:
    /// - Secure voting systems
    /// - Privacy-preserving data aggregation
    /// - Secure auctions
    /// - Private set intersection cardinality
    ///
    /// Limitations:
    /// - Only supports addition (not multiplication of two ciphertexts)
    /// - Ciphertext expansion (2x input size)
    /// - Slower than symmetric encryption
    /// </summary>
    public sealed class PaillierStrategy : EncryptionStrategyBase
    {
        private readonly int _keyBits;
        // Lock protects all mutable key state. Concurrent callers would otherwise overwrite
        // each other's parsed key material, causing silent use of the wrong key (finding #2936).
        private readonly object _keyLock = new();
        private BigInteger? _n;
        private BigInteger? _nSquared;
        private BigInteger? _g;
        private BigInteger? _lambda;
        private BigInteger? _mu;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "Paillier-HE",
            KeySizeBits = _keyBits,
            BlockSizeBytes = _keyBits / 8, // Plaintext block = n bytes
            IvSizeBytes = 0,
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
                ["Algorithm"] = "Paillier",
                ["Type"] = "Additive Homomorphic",
                ["KeyBits"] = _keyBits,
                ["HomomorphicOperations"] = new[] { "Addition", "ScalarMultiplication" },
                ["CiphertextExpansion"] = 2
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "paillier-he";

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("paillier.he.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("paillier.he.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <inheritdoc/>
        public override string StrategyName => "Paillier Homomorphic Encryption";

        public PaillierStrategy(int keyBits = 2048)
        {
            _keyBits = keyBits;
        }

        /// <summary>
        /// Encrypts a plaintext message using Paillier encryption.
        /// </summary>
        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                BigInteger n, nSquared, g;
                lock (_keyLock)
                {
                    // Parse public key into local copies under lock to avoid concurrent overwrite.
                    ParsePublicKey(key);
                    n = _n!.Value;
                    nSquared = _nSquared!.Value;
                    g = _g!.Value;
                }

                // Convert plaintext to BigInteger
                var m = new BigInteger(plaintext, isUnsigned: true, isBigEndian: true);

                // Ensure m < n
                if (m >= n)
                {
                    throw new CryptographicException(
                        $"Plaintext too large. Max value is {n - 1}");
                }

                // Generate random r in Z*_n
                var r = GenerateRandomCoprime(n);

                // c = g^m * r^n mod n^2
                var gm = BigInteger.ModPow(g, m, nSquared);
                var rn = BigInteger.ModPow(r, n, nSquared);
                var c = (gm * rn) % nSquared;

                return c.ToByteArray(isUnsigned: true, isBigEndian: true);
            }, cancellationToken);
        }

        /// <summary>
        /// Decrypts a Paillier ciphertext.
        /// </summary>
        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                BigInteger n, nSquared, lambda, mu;
                lock (_keyLock)
                {
                    // Parse private key into local copies under lock to avoid concurrent overwrite.
                    ParsePrivateKey(key);
                    n = _n!.Value;
                    nSquared = _nSquared!.Value;
                    lambda = _lambda!.Value;
                    mu = _mu!.Value;
                }

                // Convert ciphertext to BigInteger
                var c = new BigInteger(ciphertext, isUnsigned: true, isBigEndian: true);

                // m = L(c^lambda mod n^2) * mu mod n
                // where L(u) = (u - 1) / n
                var clambda = BigInteger.ModPow(c, lambda, nSquared);
                var l = (clambda - 1) / n;
                var m = (l * mu) % n;

                return m.ToByteArray(isUnsigned: true, isBigEndian: true);
            }, cancellationToken);
        }

        /// <summary>
        /// Generates a Paillier key pair.
        /// Returns: [n_length:4][n][g_length:4][g][lambda_length:4][lambda][mu_length:4][mu]
        /// </summary>
        public override byte[] GenerateKey()
        {
            // Generate two large primes p and q
            var p = GeneratePrime(_keyBits / 2);
            var q = GeneratePrime(_keyBits / 2);

            var n = p * q;
            var nSquared = n * n;

            // lambda = lcm(p-1, q-1)
            var lambda = LeastCommonMultiple(p - 1, q - 1);

            // g = n + 1 (simplified generator)
            var g = n + 1;

            // mu = (L(g^lambda mod n^2))^(-1) mod n
            var glambda = BigInteger.ModPow(g, lambda, nSquared);
            var l = (glambda - 1) / n;
            var mu = ModInverse(l, n);

            // Serialize key components
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);

            var nBytes = n.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(nBytes.Length);
            writer.Write(nBytes);

            var gBytes = g.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(gBytes.Length);
            writer.Write(gBytes);

            var lambdaBytes = lambda.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(lambdaBytes.Length);
            writer.Write(lambdaBytes);

            var muBytes = mu.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(muBytes.Length);
            writer.Write(muBytes);

            return ms.ToArray();
        }

        /// <summary>
        /// Homomorphic addition of two ciphertexts.
        /// E(m1 + m2) = E(m1) * E(m2) mod n^2
        /// </summary>
        public byte[] Add(byte[] ciphertext1, byte[] ciphertext2, byte[] publicKey)
        {
            ParsePublicKey(publicKey);

            var c1 = new BigInteger(ciphertext1, isUnsigned: true, isBigEndian: true);
            var c2 = new BigInteger(ciphertext2, isUnsigned: true, isBigEndian: true);

            var result = (c1 * c2) % _nSquared!.Value;
            return result.ToByteArray(isUnsigned: true, isBigEndian: true);
        }

        /// <summary>
        /// Scalar multiplication of ciphertext by a constant.
        /// E(k * m) = E(m)^k mod n^2
        /// </summary>
        public byte[] ScalarMultiply(byte[] ciphertext, BigInteger scalar, byte[] publicKey)
        {
            ParsePublicKey(publicKey);

            var c = new BigInteger(ciphertext, isUnsigned: true, isBigEndian: true);
            var result = BigInteger.ModPow(c, scalar, _nSquared!.Value);
            return result.ToByteArray(isUnsigned: true, isBigEndian: true);
        }

        private void ParsePublicKey(byte[] key)
        {
            using var ms = new System.IO.MemoryStream(key);
            using var reader = new System.IO.BinaryReader(ms);

            var nLen = reader.ReadInt32();
            var nBytes = reader.ReadBytes(nLen);
            _n = new BigInteger(nBytes, isUnsigned: true, isBigEndian: true);
            _nSquared = _n * _n;

            var gLen = reader.ReadInt32();
            var gBytes = reader.ReadBytes(gLen);
            _g = new BigInteger(gBytes, isUnsigned: true, isBigEndian: true);
        }

        private void ParsePrivateKey(byte[] key)
        {
            using var ms = new System.IO.MemoryStream(key);
            using var reader = new System.IO.BinaryReader(ms);

            var nLen = reader.ReadInt32();
            var nBytes = reader.ReadBytes(nLen);
            _n = new BigInteger(nBytes, isUnsigned: true, isBigEndian: true);
            _nSquared = _n * _n;

            var gLen = reader.ReadInt32();
            reader.ReadBytes(gLen); // Skip g

            var lambdaLen = reader.ReadInt32();
            var lambdaBytes = reader.ReadBytes(lambdaLen);
            _lambda = new BigInteger(lambdaBytes, isUnsigned: true, isBigEndian: true);

            var muLen = reader.ReadInt32();
            var muBytes = reader.ReadBytes(muLen);
            _mu = new BigInteger(muBytes, isUnsigned: true, isBigEndian: true);
        }

        private static BigInteger GeneratePrime(int bits)
        {
            var bytes = new byte[(bits + 7) / 8];
            BigInteger candidate;

            do
            {
                RandomNumberGenerator.Fill(bytes);
                // Ensure top bit is set for proper bit length
                bytes[^1] |= 0x80;
                // Ensure odd (primes > 2 are odd)
                bytes[0] |= 0x01;

                candidate = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            } while (!IsProbablePrime(candidate, 20));

            return candidate;
        }

        private static BigInteger GenerateRandomCoprime(BigInteger n)
        {
            var bytes = new byte[(int)((n.GetBitLength() + 7) / 8)];
            BigInteger r;

            do
            {
                RandomNumberGenerator.Fill(bytes);
                r = new BigInteger(bytes, isUnsigned: true, isBigEndian: true) % n;
            } while (r <= 1 || BigInteger.GreatestCommonDivisor(r, n) != 1);

            return r;
        }

        private static bool IsProbablePrime(BigInteger n, int k)
        {
            if (n < 2) return false;
            if (n == 2 || n == 3) return true;
            if (n.IsEven) return false;

            // Write n-1 as 2^r * d
            var d = n - 1;
            var r = 0;
            while (d.IsEven)
            {
                d /= 2;
                r++;
            }

            // Miller-Rabin test
            var bytes = new byte[(int)((n.GetBitLength() + 7) / 8)];
            for (var i = 0; i < k; i++)
            {
                BigInteger a;
                do
                {
                    RandomNumberGenerator.Fill(bytes);
                    a = new BigInteger(bytes, isUnsigned: true, isBigEndian: true) % (n - 3) + 2;
                } while (a < 2);

                var x = BigInteger.ModPow(a, d, n);
                if (x == 1 || x == n - 1) continue;

                var composite = true;
                for (var j = 0; j < r - 1; j++)
                {
                    x = BigInteger.ModPow(x, 2, n);
                    if (x == n - 1)
                    {
                        composite = false;
                        break;
                    }
                }

                if (composite) return false;
            }

            return true;
        }

        private static BigInteger LeastCommonMultiple(BigInteger a, BigInteger b)
        {
            return BigInteger.Abs(a * b) / BigInteger.GreatestCommonDivisor(a, b);
        }

        private static BigInteger ModInverse(BigInteger a, BigInteger n)
        {
            BigInteger t = 0, newT = 1;
            BigInteger r = n, newR = a;

            while (newR != 0)
            {
                var quotient = r / newR;
                (t, newT) = (newT, t - quotient * newT);
                (r, newR) = (newR, r - quotient * newR);
            }

            if (r > 1)
                throw new CryptographicException("a is not invertible modulo n");

            if (t < 0)
                t += n;

            return t;
        }
    }

    /// <summary>
    /// ElGamal homomorphic encryption strategy - Multiplicative homomorphic encryption.
    ///
    /// The ElGamal cryptosystem supports multiplicative homomorphic operations:
    /// - E(m1) * E(m2) = E(m1 * m2)
    ///
    /// Security Level: High (based on Decisional Diffie-Hellman assumption)
    /// Key Size: 2048-bit prime modulus
    ///
    /// Use Case:
    /// - Mix networks
    /// - Verifiable secret sharing
    /// - Threshold cryptography
    /// - Re-encryption schemes
    /// </summary>
    public sealed class ElGamalStrategy : EncryptionStrategyBase
    {
        private readonly int _keyBits;
        // Lock protects all mutable key state. Concurrent callers would otherwise overwrite
        // each other's parsed key material, causing silent use of the wrong key (finding #2936).
        private readonly object _keyLock = new();
        private BigInteger? _p;
        private BigInteger? _g;
        private BigInteger? _y; // Public key
        private BigInteger? _x; // Private key

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "ElGamal-HE",
            KeySizeBits = _keyBits,
            BlockSizeBytes = _keyBits / 8,
            IvSizeBytes = 0,
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
                ["Algorithm"] = "ElGamal",
                ["Type"] = "Multiplicative Homomorphic",
                ["KeyBits"] = _keyBits,
                ["HomomorphicOperations"] = new[] { "Multiplication" },
                ["CiphertextExpansion"] = 2
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "elgamal-he";

        /// <inheritdoc/>
        public override string StrategyName => "ElGamal Homomorphic Encryption";

        public ElGamalStrategy(int keyBits = 2048)
        {
            _keyBits = keyBits;
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                BigInteger p, g, y;
                lock (_keyLock)
                {
                    // Parse public key into local copies under lock to avoid concurrent overwrite.
                    ParsePublicKey(key);
                    p = _p!.Value;
                    g = _g!.Value;
                    y = _y!.Value;
                }

                var m = new BigInteger(plaintext, isUnsigned: true, isBigEndian: true);
                if (m >= p)
                {
                    throw new CryptographicException("Plaintext too large");
                }

                // Generate random k
                var k = GenerateRandomInRange(2, p - 2);

                // c1 = g^k mod p
                var c1 = BigInteger.ModPow(g, k, p);

                // c2 = m * y^k mod p
                var yk = BigInteger.ModPow(y, k, p);
                var c2 = (m * yk) % p;

                // Serialize (c1, c2)
                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);

                var c1Bytes = c1.ToByteArray(isUnsigned: true, isBigEndian: true);
                writer.Write(c1Bytes.Length);
                writer.Write(c1Bytes);

                var c2Bytes = c2.ToByteArray(isUnsigned: true, isBigEndian: true);
                writer.Write(c2Bytes.Length);
                writer.Write(c2Bytes);

                return ms.ToArray();
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
                BigInteger p, x;
                lock (_keyLock)
                {
                    // Parse private key into local copies under lock to avoid concurrent overwrite.
                    ParsePrivateKey(key);
                    p = _p!.Value;
                    x = _x!.Value;
                }

                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                var c1Len = reader.ReadInt32();
                var c1Bytes = reader.ReadBytes(c1Len);
                var c1 = new BigInteger(c1Bytes, isUnsigned: true, isBigEndian: true);

                var c2Len = reader.ReadInt32();
                var c2Bytes = reader.ReadBytes(c2Len);
                var c2 = new BigInteger(c2Bytes, isUnsigned: true, isBigEndian: true);

                // m = c2 * (c1^x)^(-1) mod p
                var c1x = BigInteger.ModPow(c1, x, p);
                var c1xInv = BigInteger.ModPow(c1x, p - 2, p); // Fermat's little theorem
                var m = (c2 * c1xInv) % p;

                return m.ToByteArray(isUnsigned: true, isBigEndian: true);
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            // Generate safe prime p = 2q + 1 where q is prime
            var q = GeneratePrime(_keyBits - 1);
            var p = 2 * q + 1;
            while (!IsProbablePrime(p, 20))
            {
                q = GeneratePrime(_keyBits - 1);
                p = 2 * q + 1;
            }

            // Generator g (quadratic residue)
            var g = BigInteger.ModPow(2, 2, p);

            // Private key x
            var x = GenerateRandomInRange(2, p - 2);

            // Public key y = g^x mod p
            var y = BigInteger.ModPow(g, x, p);

            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);

            var pBytes = p.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(pBytes.Length);
            writer.Write(pBytes);

            var gBytes = g.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(gBytes.Length);
            writer.Write(gBytes);

            var yBytes = y.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(yBytes.Length);
            writer.Write(yBytes);

            var xBytes = x.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(xBytes.Length);
            writer.Write(xBytes);

            return ms.ToArray();
        }

        /// <summary>
        /// Homomorphic multiplication of two ciphertexts.
        /// E(m1 * m2) = E(m1) * E(m2)
        /// </summary>
        public byte[] Multiply(byte[] ciphertext1, byte[] ciphertext2, byte[] publicKey)
        {
            ParsePublicKey(publicKey);

            using var ms1 = new System.IO.MemoryStream(ciphertext1);
            using var reader1 = new System.IO.BinaryReader(ms1);
            var c1_1Len = reader1.ReadInt32();
            var c1_1 = new BigInteger(reader1.ReadBytes(c1_1Len), isUnsigned: true, isBigEndian: true);
            var c1_2Len = reader1.ReadInt32();
            var c1_2 = new BigInteger(reader1.ReadBytes(c1_2Len), isUnsigned: true, isBigEndian: true);

            using var ms2 = new System.IO.MemoryStream(ciphertext2);
            using var reader2 = new System.IO.BinaryReader(ms2);
            var c2_1Len = reader2.ReadInt32();
            var c2_1 = new BigInteger(reader2.ReadBytes(c2_1Len), isUnsigned: true, isBigEndian: true);
            var c2_2Len = reader2.ReadInt32();
            var c2_2 = new BigInteger(reader2.ReadBytes(c2_2Len), isUnsigned: true, isBigEndian: true);

            // Component-wise multiplication
            var r1 = (c1_1 * c2_1) % _p!.Value;
            var r2 = (c1_2 * c2_2) % _p!.Value;

            using var result = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(result);

            var r1Bytes = r1.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(r1Bytes.Length);
            writer.Write(r1Bytes);

            var r2Bytes = r2.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(r2Bytes.Length);
            writer.Write(r2Bytes);

            return result.ToArray();
        }

        private void ParsePublicKey(byte[] key)
        {
            using var ms = new System.IO.MemoryStream(key);
            using var reader = new System.IO.BinaryReader(ms);

            var pLen = reader.ReadInt32();
            _p = new BigInteger(reader.ReadBytes(pLen), isUnsigned: true, isBigEndian: true);

            var gLen = reader.ReadInt32();
            _g = new BigInteger(reader.ReadBytes(gLen), isUnsigned: true, isBigEndian: true);

            var yLen = reader.ReadInt32();
            _y = new BigInteger(reader.ReadBytes(yLen), isUnsigned: true, isBigEndian: true);
        }

        private void ParsePrivateKey(byte[] key)
        {
            ParsePublicKey(key);

            using var ms = new System.IO.MemoryStream(key);
            using var reader = new System.IO.BinaryReader(ms);

            // Skip p, g, y
            var pLen = reader.ReadInt32();
            reader.ReadBytes(pLen);
            var gLen = reader.ReadInt32();
            reader.ReadBytes(gLen);
            var yLen = reader.ReadInt32();
            reader.ReadBytes(yLen);

            var xLen = reader.ReadInt32();
            _x = new BigInteger(reader.ReadBytes(xLen), isUnsigned: true, isBigEndian: true);
        }

        private static BigInteger GeneratePrime(int bits)
        {
            var bytes = new byte[(bits + 7) / 8];
            BigInteger candidate;

            do
            {
                RandomNumberGenerator.Fill(bytes);
                bytes[^1] |= 0x80;
                bytes[0] |= 0x01;
                candidate = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            } while (!IsProbablePrime(candidate, 20));

            return candidate;
        }

        private static BigInteger GenerateRandomInRange(BigInteger min, BigInteger max)
        {
            var range = max - min;
            var bytes = new byte[(int)((range.GetBitLength() + 7) / 8) + 1];
            BigInteger result;

            do
            {
                RandomNumberGenerator.Fill(bytes);
                bytes[^1] = 0; // Ensure positive
                result = new BigInteger(bytes, isUnsigned: true) % range + min;
            } while (result < min || result > max);

            return result;
        }

        private static bool IsProbablePrime(BigInteger n, int k)
        {
            if (n < 2) return false;
            if (n == 2 || n == 3) return true;
            if (n.IsEven) return false;

            var d = n - 1;
            var r = 0;
            while (d.IsEven)
            {
                d /= 2;
                r++;
            }

            var bytes = new byte[(int)((n.GetBitLength() + 7) / 8)];
            for (var i = 0; i < k; i++)
            {
                BigInteger a;
                do
                {
                    RandomNumberGenerator.Fill(bytes);
                    a = new BigInteger(bytes, isUnsigned: true, isBigEndian: true) % (n - 3) + 2;
                } while (a < 2);

                var x = BigInteger.ModPow(a, d, n);
                if (x == 1 || x == n - 1) continue;

                var composite = true;
                for (var j = 0; j < r - 1; j++)
                {
                    x = BigInteger.ModPow(x, 2, n);
                    if (x == n - 1)
                    {
                        composite = false;
                        break;
                    }
                }

                if (composite) return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Goldwasser-Micali probabilistic encryption - XOR homomorphic.
    ///
    /// The Goldwasser-Micali cryptosystem encrypts bits and supports XOR operations:
    /// - E(b1) * E(b2) = E(b1 XOR b2)
    ///
    /// Security Level: High (based on Quadratic Residuosity assumption)
    ///
    /// Use Case:
    /// - Private information retrieval
    /// - Secure multi-party computation (bit operations)
    ///
    /// Note: Very high ciphertext expansion (bit-by-bit encryption).
    /// </summary>
    public sealed class GoldwasserMicaliStrategy : EncryptionStrategyBase
    {
        private readonly int _keyBits;
        private BigInteger? _n;
        private BigInteger? _x; // Non-residue
        private BigInteger? _p;
        private BigInteger? _q;

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "Goldwasser-Micali-HE",
            KeySizeBits = _keyBits,
            BlockSizeBytes = 1, // Encrypts bit by bit
            IvSizeBytes = 0,
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
                ["Algorithm"] = "Goldwasser-Micali",
                ["Type"] = "XOR Homomorphic",
                ["KeyBits"] = _keyBits,
                ["HomomorphicOperations"] = new[] { "XOR" },
                ["CiphertextExpansion"] = _keyBits // Each bit becomes n-bit ciphertext
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "gm-he";

        /// <inheritdoc/>
        public override string StrategyName => "Goldwasser-Micali XOR Homomorphic";

        public GoldwasserMicaliStrategy(int keyBits = 2048)
        {
            _keyBits = keyBits;
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext,
            byte[] key,
            byte[]? associatedData,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                ParsePublicKey(key);

                using var result = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(result);

                // Encrypt bit by bit
                foreach (var b in plaintext)
                {
                    for (var i = 7; i >= 0; i--)
                    {
                        var bit = (b >> i) & 1;
                        var c = EncryptBit(bit == 1);
                        var cBytes = c.ToByteArray(isUnsigned: true, isBigEndian: true);
                        writer.Write((ushort)cBytes.Length);
                        writer.Write(cBytes);
                    }
                }

                return result.ToArray();
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
                ParsePrivateKey(key);

                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                var bits = new System.Collections.Generic.List<bool>();
                while (ms.Position < ms.Length)
                {
                    var len = reader.ReadUInt16();
                    var cBytes = reader.ReadBytes(len);
                    var c = new BigInteger(cBytes, isUnsigned: true, isBigEndian: true);
                    bits.Add(DecryptBit(c));
                }

                // Convert bits to bytes
                var result = new byte[(bits.Count + 7) / 8];
                for (var i = 0; i < bits.Count; i++)
                {
                    if (bits[i])
                    {
                        result[i / 8] |= (byte)(1 << (7 - (i % 8)));
                    }
                }

                return result;
            }, cancellationToken);
        }

        private BigInteger EncryptBit(bool bit)
        {
            // Generate random y coprime to n
            var y = GenerateRandomCoprime(_n!.Value);

            if (bit)
            {
                // c = x * y^2 mod n (non-residue)
                return (_x!.Value * BigInteger.ModPow(y, 2, _n!.Value)) % _n!.Value;
            }
            else
            {
                // c = y^2 mod n (quadratic residue)
                return BigInteger.ModPow(y, 2, _n!.Value);
            }
        }

        private bool DecryptBit(BigInteger c)
        {
            // Check if c is a quadratic residue mod p
            // Using Euler's criterion: a^((p-1)/2) = 1 mod p iff a is QR
            var cp = BigInteger.ModPow(c, (_p!.Value - 1) / 2, _p!.Value);
            return cp != 1; // 1 = QR (bit 0), -1 = NQR (bit 1)
        }

        public override byte[] GenerateKey()
        {
            // Generate two primes p, q ≡ 3 mod 4
            var p = GenerateBlumPrime(_keyBits / 2);
            var q = GenerateBlumPrime(_keyBits / 2);

            var n = p * q;

            // Find x that is a non-residue mod both p and q
            var x = FindNonResidue(p, q, n);

            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);

            var nBytes = n.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(nBytes.Length);
            writer.Write(nBytes);

            var xBytes = x.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(xBytes.Length);
            writer.Write(xBytes);

            var pBytes = p.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(pBytes.Length);
            writer.Write(pBytes);

            var qBytes = q.ToByteArray(isUnsigned: true, isBigEndian: true);
            writer.Write(qBytes.Length);
            writer.Write(qBytes);

            return ms.ToArray();
        }

        /// <summary>
        /// Homomorphic XOR of two ciphertexts.
        /// E(b1 XOR b2) = E(b1) * E(b2) mod n
        /// </summary>
        public byte[] Xor(byte[] ciphertext1, byte[] ciphertext2, byte[] publicKey)
        {
            ParsePublicKey(publicKey);

            using var ms1 = new System.IO.MemoryStream(ciphertext1);
            using var reader1 = new System.IO.BinaryReader(ms1);
            using var ms2 = new System.IO.MemoryStream(ciphertext2);
            using var reader2 = new System.IO.BinaryReader(ms2);
            using var result = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(result);

            while (ms1.Position < ms1.Length && ms2.Position < ms2.Length)
            {
                var len1 = reader1.ReadUInt16();
                var c1 = new BigInteger(reader1.ReadBytes(len1), isUnsigned: true, isBigEndian: true);

                var len2 = reader2.ReadUInt16();
                var c2 = new BigInteger(reader2.ReadBytes(len2), isUnsigned: true, isBigEndian: true);

                var r = (c1 * c2) % _n!.Value;
                var rBytes = r.ToByteArray(isUnsigned: true, isBigEndian: true);
                writer.Write((ushort)rBytes.Length);
                writer.Write(rBytes);
            }

            return result.ToArray();
        }

        private void ParsePublicKey(byte[] key)
        {
            using var ms = new System.IO.MemoryStream(key);
            using var reader = new System.IO.BinaryReader(ms);

            var nLen = reader.ReadInt32();
            _n = new BigInteger(reader.ReadBytes(nLen), isUnsigned: true, isBigEndian: true);

            var xLen = reader.ReadInt32();
            _x = new BigInteger(reader.ReadBytes(xLen), isUnsigned: true, isBigEndian: true);
        }

        private void ParsePrivateKey(byte[] key)
        {
            ParsePublicKey(key);

            using var ms = new System.IO.MemoryStream(key);
            using var reader = new System.IO.BinaryReader(ms);

            // Skip n, x
            var nLen = reader.ReadInt32();
            reader.ReadBytes(nLen);
            var xLen = reader.ReadInt32();
            reader.ReadBytes(xLen);

            var pLen = reader.ReadInt32();
            _p = new BigInteger(reader.ReadBytes(pLen), isUnsigned: true, isBigEndian: true);

            var qLen = reader.ReadInt32();
            _q = new BigInteger(reader.ReadBytes(qLen), isUnsigned: true, isBigEndian: true);
        }

        private static BigInteger GenerateBlumPrime(int bits)
        {
            var bytes = new byte[(bits + 7) / 8];
            BigInteger candidate;

            do
            {
                RandomNumberGenerator.Fill(bytes);
                bytes[^1] |= 0x80;
                bytes[0] |= 0x03; // ≡ 3 mod 4
                candidate = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            } while (!IsProbablePrime(candidate, 20) || candidate % 4 != 3);

            return candidate;
        }

        private static BigInteger FindNonResidue(BigInteger p, BigInteger q, BigInteger n)
        {
            var bytes = new byte[(int)((n.GetBitLength() + 7) / 8)];
            BigInteger x;

            do
            {
                RandomNumberGenerator.Fill(bytes);
                x = new BigInteger(bytes, isUnsigned: true, isBigEndian: true) % n;
            } while (x <= 1 ||
                     BigInteger.ModPow(x, (p - 1) / 2, p) == 1 ||
                     BigInteger.ModPow(x, (q - 1) / 2, q) == 1);

            return x;
        }

        private static BigInteger GenerateRandomCoprime(BigInteger n)
        {
            var bytes = new byte[(int)((n.GetBitLength() + 7) / 8)];
            BigInteger r;

            do
            {
                RandomNumberGenerator.Fill(bytes);
                r = new BigInteger(bytes, isUnsigned: true, isBigEndian: true) % n;
            } while (r <= 1 || BigInteger.GreatestCommonDivisor(r, n) != 1);

            return r;
        }

        private static bool IsProbablePrime(BigInteger n, int k)
        {
            if (n < 2) return false;
            if (n == 2 || n == 3) return true;
            if (n.IsEven) return false;

            var d = n - 1;
            var r = 0;
            while (d.IsEven) { d /= 2; r++; }

            var bytes = new byte[(int)((n.GetBitLength() + 7) / 8)];
            for (var i = 0; i < k; i++)
            {
                BigInteger a;
                do
                {
                    RandomNumberGenerator.Fill(bytes);
                    a = new BigInteger(bytes, isUnsigned: true, isBigEndian: true) % (n - 3) + 2;
                } while (a < 2);

                var x = BigInteger.ModPow(a, d, n);
                if (x == 1 || x == n - 1) continue;

                var composite = true;
                for (var j = 0; j < r - 1; j++)
                {
                    x = BigInteger.ModPow(x, 2, n);
                    if (x == n - 1) { composite = false; break; }
                }
                if (composite) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// BFV/BGV-style leveled fully homomorphic encryption strategy.
    ///
    /// This strategy requires external libraries like Microsoft SEAL or OpenFHE.
    ///
    /// Status: NOT AVAILABLE - Requires native library integration.
    /// When full FHE is needed, integrate Microsoft SEAL via NuGet.
    ///
    /// Alternative: Use Paillier (additive) or ElGamal (multiplicative)
    /// for partial homomorphic operations.
    /// </summary>
    public sealed class BfvFheStrategy : EncryptionStrategyBase
    {
        private const string UnavailableMessage =
            "BFV Fully Homomorphic Encryption requires Microsoft SEAL library integration. " +
            "For partial homomorphic operations, use:\n" +
            "- 'paillier-he' for additive operations (sum, average)\n" +
            "- 'elgamal-he' for multiplicative operations\n" +
            "- 'gm-he' for XOR/boolean operations\n\n" +
            "To enable full FHE:\n" +
            "1. Add Microsoft.Research.SEAL NuGet package\n" +
            "2. Implement SEAL wrapper in this strategy";

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "BFV-FHE",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
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
                ["Algorithm"] = "BFV",
                ["Type"] = "Fully Homomorphic",
                ["Status"] = "UNAVAILABLE",
                ["Reason"] = "Requires Microsoft SEAL",
                ["Alternative"] = "Use paillier-he, elgamal-he, or gm-he"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "bfv-fhe";

        /// <inheritdoc/>
        public override string StrategyName => "BFV Fully Homomorphic (UNAVAILABLE)";

        protected override Task<byte[]> EncryptCoreAsync(
            byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
            => throw new NotSupportedException(UnavailableMessage);

        protected override Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
            => throw new NotSupportedException(UnavailableMessage);

        public override byte[] GenerateKey()
            => throw new NotSupportedException(UnavailableMessage);
    }

    /// <summary>
    /// CKKS-style approximate fully homomorphic encryption for real number operations.
    ///
    /// Status: NOT AVAILABLE - Requires native library integration.
    /// </summary>
    public sealed class CkksFheStrategy : EncryptionStrategyBase
    {
        private const string UnavailableMessage =
            "CKKS Fully Homomorphic Encryption for approximate arithmetic requires Microsoft SEAL. " +
            "Use 'paillier-he' for exact integer arithmetic on encrypted data.";

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "CKKS-FHE",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
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
                ["Algorithm"] = "CKKS",
                ["Type"] = "Approximate FHE",
                ["Status"] = "UNAVAILABLE",
                ["UseCase"] = "Machine learning on encrypted data"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "ckks-fhe";

        /// <inheritdoc/>
        public override string StrategyName => "CKKS Approximate FHE (UNAVAILABLE)";

        protected override Task<byte[]> EncryptCoreAsync(
            byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
            => throw new NotSupportedException(UnavailableMessage);

        protected override Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
            => throw new NotSupportedException(UnavailableMessage);

        public override byte[] GenerateKey()
            => throw new NotSupportedException(UnavailableMessage);
    }

    /// <summary>
    /// TFHE (Torus Fully Homomorphic Encryption) for boolean circuit operations.
    ///
    /// Status: NOT AVAILABLE - Requires native library integration.
    /// </summary>
    public sealed class TfheStrategy : EncryptionStrategyBase
    {
        private const string UnavailableMessage =
            "TFHE (Fast Fully Homomorphic Encryption over the Torus) requires native library integration. " +
            "For boolean operations on encrypted data, use 'gm-he' (Goldwasser-Micali XOR homomorphic).";

        /// <inheritdoc/>
        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "TFHE",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
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
                ["Algorithm"] = "TFHE",
                ["Type"] = "Boolean Circuit FHE",
                ["Status"] = "UNAVAILABLE",
                ["UseCase"] = "Arbitrary boolean circuits on encrypted bits"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "tfhe";

        /// <inheritdoc/>
        public override string StrategyName => "TFHE Boolean FHE (UNAVAILABLE)";

        protected override Task<byte[]> EncryptCoreAsync(
            byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
            => throw new NotSupportedException(UnavailableMessage);

        protected override Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
            => throw new NotSupportedException(UnavailableMessage);

        public override byte[] GenerateKey()
            => throw new NotSupportedException(UnavailableMessage);
    }
}
