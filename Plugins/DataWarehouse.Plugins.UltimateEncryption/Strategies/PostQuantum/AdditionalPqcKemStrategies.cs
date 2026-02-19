using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Pqc.Crypto.Ntru;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.PostQuantum
{
    /// <summary>
    /// NTRU-HRSS-701 KEM encryption strategy with AES-256-GCM.
    ///
    /// HRSS (Hash-based Ring Signature Scheme) variant provides improved IND-CCA2 security
    /// with constant-time operations and side-channel resistance.
    ///
    /// Security Level: NIST Level 3
    /// KEM: NTRU-HRSS-701
    /// Symmetric Cipher: AES-256-GCM
    ///
    /// Use Case: Side-channel resistant post-quantum encryption.
    /// </summary>
    public sealed class NtruHrss701Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "NTRU-HRSS-701-AES-256-GCM",
            KeySizeBits = 256,
            BlockSizeBytes = 16,
            IvSizeBytes = 12,
            TagSizeBytes = 16,
            Capabilities = CipherCapabilities.AeadCipher with
            {
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["KemAlgorithm"] = "NTRU-HRSS-701",
                ["NistLevel"] = 3,
                ["Variant"] = "HRSS (constant-time)",
                ["Note"] = "HRSS variant with improved side-channel resistance"
            }
        };

        public override string StrategyId => "ntru-hrss-701";
        public override string StrategyName => "NTRU-HRSS-701 + AES-256-GCM (Side-channel Resistant)";

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ntru.hrss701.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ntru.hrss701.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public NtruHrss701Strategy()
        {
            _secureRandom = new SecureRandom();
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var recipientPublicKey = new NtruPublicKeyParameters(NtruParameters.NtruHrss701, key);
                var kemGenerator = new NtruKemGenerator(_secureRandom);
                var encapsulated = kemGenerator.GenerateEncapsulated(recipientPublicKey);

                var kemCiphertext = encapsulated.GetEncapsulation();
                var sharedSecret = encapsulated.GetSecret();

                try
                {
                    var aesKey = new byte[32];
                    Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                    var nonce = GenerateIv();
                    var tag = new byte[16];
                    var ciphertext = new byte[plaintext.Length];

                    using (var aesGcm = new AesGcm(aesKey, 16))
                    {
                        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
                    }

                    using var ms = new System.IO.MemoryStream();
                    using var writer = new System.IO.BinaryWriter(ms);
                    writer.Write(kemCiphertext.Length);
                    writer.Write(kemCiphertext);
                    writer.Write(nonce);
                    writer.Write(tag);
                    writer.Write(ciphertext);
                    return ms.ToArray();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sharedSecret);
                }
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                var kemCiphertextLength = reader.ReadInt32();
                var kemCiphertext = reader.ReadBytes(kemCiphertextLength);
                var nonce = reader.ReadBytes(12);
                var tag = reader.ReadBytes(16);
                var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

                var privateKeyParams = new NtruPrivateKeyParameters(NtruParameters.NtruHrss701, key);
                var kemExtractor = new NtruKemExtractor(privateKeyParams);
                var sharedSecret = kemExtractor.ExtractSecret(kemCiphertext);

                try
                {
                    var aesKey = new byte[32];
                    Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                    var plaintext = new byte[encryptedData.Length];
                    using (var aesGcm = new AesGcm(aesKey, 16))
                    {
                        aesGcm.Decrypt(nonce, encryptedData, tag, plaintext, associatedData);
                    }
                    return plaintext;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sharedSecret);
                }
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            var keyGenParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHrss701);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded();
        }

        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            var keyGenParams = new NtruKeyGenerationParameters(_secureRandom, NtruParameters.NtruHrss701);
            var keyPairGenerator = new NtruKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return (((NtruPublicKeyParameters)keyPair.Public).GetEncoded(),
                    ((NtruPrivateKeyParameters)keyPair.Private).GetEncoded());
        }
    }

    /// <summary>
    /// McEliece KEM strategy - Code-based post-quantum encryption.
    ///
    /// Classic McEliece is a code-based KEM using binary Goppa codes.
    /// It has the longest track record of any post-quantum candidate (1978).
    /// Note: BouncyCastle does not yet provide McEliece — this strategy provides
    /// a protocol abstraction that throws UnsupportedAlgorithmException until library support is added.
    ///
    /// Use Case: Ultra-conservative post-quantum encryption for long-term archival.
    /// </summary>
    public sealed class ClassicMcElieceStrategy : EncryptionStrategyBase
    {
        private const string UnavailableMessage =
            "Classic McEliece is not yet supported. BouncyCastle does not include Classic McEliece. " +
            "Use 'ml-kem-768' (NTRU-based) or 'ntru-hrss-701' as alternatives. " +
            "Classic McEliece support will be added when available in BouncyCastle.";

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "Classic-McEliece-6960119",
            KeySizeBits = 256,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "Classic-McEliece-6960119",
                ["Type"] = "KEM",
                ["NistLevel"] = 5,
                ["Status"] = "UNAVAILABLE",
                ["Reason"] = "Awaiting BouncyCastle Classic McEliece support",
                ["Alternative"] = "Use ml-kem-768 or ntru-hrss-701",
                ["PublicKeySize"] = 1047319,
                ["Note"] = "Very large public keys (~1MB) — designed for stationary use cases"
            }
        };

        public override string StrategyId => "classic-mceliece";
        public override string StrategyName => "Classic McEliece-6960119 (UNAVAILABLE)";

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
    /// BIKE (Bit Flipping Key Encapsulation) strategy.
    /// Code-based KEM using quasi-cyclic moderate-density parity-check codes.
    /// Status: Not available in BouncyCastle — protocol abstraction only.
    /// </summary>
    public sealed class BikeStrategy : EncryptionStrategyBase
    {
        private const string UnavailableMessage =
            "BIKE is not yet supported. BouncyCastle does not include BIKE. " +
            "Use 'ml-kem-768' (NTRU-based) as an alternative. " +
            "BIKE support will be added when available in BouncyCastle.";

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "BIKE-L3",
            KeySizeBits = 256,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "BIKE-L3",
                ["Type"] = "KEM",
                ["NistLevel"] = 3,
                ["Status"] = "UNAVAILABLE",
                ["Reason"] = "Awaiting BouncyCastle BIKE support",
                ["Alternative"] = "Use ml-kem-768 or ntru-hrss-701"
            }
        };

        public override string StrategyId => "bike-l3";
        public override string StrategyName => "BIKE Level 3 (UNAVAILABLE)";

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
    /// HQC (Hamming Quasi-Cyclic) KEM strategy.
    /// Code-based KEM using quasi-cyclic codes with decoding via Reed-Muller codes.
    /// Status: Not available in BouncyCastle — protocol abstraction only.
    /// </summary>
    public sealed class HqcStrategy : EncryptionStrategyBase
    {
        private const string UnavailableMessage =
            "HQC is not yet supported. BouncyCastle does not include HQC. " +
            "Use 'ml-kem-768' (NTRU-based) as an alternative. " +
            "HQC support will be added when available in BouncyCastle.";

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "HQC-256",
            KeySizeBits = 256,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "HQC-256",
                ["Type"] = "KEM",
                ["NistLevel"] = 5,
                ["Status"] = "UNAVAILABLE",
                ["Reason"] = "Awaiting BouncyCastle HQC support",
                ["Alternative"] = "Use ml-kem-1024 or ntru-hrss-701"
            }
        };

        public override string StrategyId => "hqc-256";
        public override string StrategyName => "HQC-256 (UNAVAILABLE)";

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
    /// FrodoKEM strategy - Lattice-based KEM using standard (unstructured) lattices.
    /// More conservative security assumptions than ring/module lattices (NTRU/Kyber).
    ///
    /// BouncyCastle provides FrodoKEM support via the Pqc.Crypto.Frodo namespace.
    /// </summary>
    public sealed class FrodoKemStrategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "FrodoKEM-976-AES-AES-256-GCM",
            KeySizeBits = 256,
            BlockSizeBytes = 16,
            IvSizeBytes = 12,
            TagSizeBytes = 16,
            Capabilities = CipherCapabilities.AeadCipher with
            {
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["KemAlgorithm"] = "FrodoKEM-976-AES",
                ["NistLevel"] = 3,
                ["LatticeType"] = "Standard (unstructured)",
                ["Note"] = "Conservative security — uses standard lattices, not ring lattices"
            }
        };

        public override string StrategyId => "frodokem-976";
        public override string StrategyName => "FrodoKEM-976-AES + AES-256-GCM (Conservative PQ)";

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("frodokem.976.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("frodokem.976.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public FrodoKemStrategy()
        {
            _secureRandom = new SecureRandom();
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                // FrodoKEM uses standard lattice assumptions for KEM
                // BouncyCastle FrodoKEM API - generate encapsulation from public key
                var kemGen = new Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoKEMGenerator(_secureRandom);
                var pubKeyParams = new Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoPublicKeyParameters(
                    Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoParameters.frodokem976aes, key);
                var encapsulated = kemGen.GenerateEncapsulated(pubKeyParams);

                var kemCiphertext = encapsulated.GetEncapsulation();
                var sharedSecret = encapsulated.GetSecret();

                try
                {
                    var aesKey = new byte[32];
                    Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                    var nonce = GenerateIv();
                    var tag = new byte[16];
                    var ct = new byte[plaintext.Length];

                    using (var aesGcm = new AesGcm(aesKey, 16))
                    {
                        aesGcm.Encrypt(nonce, plaintext, ct, tag, associatedData);
                    }

                    using var ms = new System.IO.MemoryStream();
                    using var writer = new System.IO.BinaryWriter(ms);
                    writer.Write(kemCiphertext.Length);
                    writer.Write(kemCiphertext);
                    writer.Write(nonce);
                    writer.Write(tag);
                    writer.Write(ct);
                    return ms.ToArray();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sharedSecret);
                }
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                var kemCiphertextLength = reader.ReadInt32();
                var kemCiphertext = reader.ReadBytes(kemCiphertextLength);
                var nonce = reader.ReadBytes(12);
                var tag = reader.ReadBytes(16);
                var encryptedData = reader.ReadBytes((int)(ms.Length - ms.Position));

                var privKeyParams = new Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoPrivateKeyParameters(
                    Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoParameters.frodokem976aes, key);
                var kemExtractor = new Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoKEMExtractor(privKeyParams);
                var sharedSecret = kemExtractor.ExtractSecret(kemCiphertext);

                try
                {
                    var aesKey = new byte[32];
                    Buffer.BlockCopy(sharedSecret, 0, aesKey, 0, Math.Min(sharedSecret.Length, 32));

                    var pt = new byte[encryptedData.Length];
                    using (var aesGcm = new AesGcm(aesKey, 16))
                    {
                        aesGcm.Decrypt(nonce, encryptedData, tag, pt, associatedData);
                    }
                    return pt;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(sharedSecret);
                }
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            var keyGenParams = new Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoKeyGenerationParameters(
                _secureRandom, Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoParameters.frodokem976aes);
            var keyPairGenerator = new Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return ((Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoPrivateKeyParameters)keyPair.Private).GetEncoded();
        }

        public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
        {
            var keyGenParams = new Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoKeyGenerationParameters(
                _secureRandom, Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoParameters.frodokem976aes);
            var keyPairGenerator = new Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return (((Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoPublicKeyParameters)keyPair.Public).GetEncoded(),
                    ((Org.BouncyCastle.Pqc.Crypto.Frodo.FrodoPrivateKeyParameters)keyPair.Private).GetEncoded());
        }
    }

    /// <summary>
    /// SABER KEM strategy - Module lattice-based KEM.
    /// Status: Not available in BouncyCastle — protocol abstraction only.
    /// SABER was not selected for NIST standardization; ML-KEM (Kyber/NTRU) is preferred.
    /// </summary>
    public sealed class SaberStrategy : EncryptionStrategyBase
    {
        private const string UnavailableMessage =
            "SABER was not selected for NIST PQC standardization. " +
            "Use 'ml-kem-768' (NTRU-based) which provides equivalent lattice-based security. " +
            "SABER will not be implemented as ML-KEM is the NIST standard.";

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "SABER-L3",
            KeySizeBits = 256,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 0,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = false,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "SABER-L3",
                ["Type"] = "KEM",
                ["NistLevel"] = 3,
                ["Status"] = "DEPRECATED",
                ["Reason"] = "Not selected for NIST PQC standardization",
                ["Alternative"] = "Use ml-kem-768 (NIST standard ML-KEM)"
            }
        };

        public override string StrategyId => "saber-l3";
        public override string StrategyName => "SABER Level 3 (DEPRECATED - Use ML-KEM)";

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
