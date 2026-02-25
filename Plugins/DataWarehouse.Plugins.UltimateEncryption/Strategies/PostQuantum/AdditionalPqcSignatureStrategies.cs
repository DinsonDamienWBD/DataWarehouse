using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.PostQuantum
{
    /// <summary>
    /// ML-DSA-44 (CRYSTALS-Dilithium-2) digital signature strategy.
    /// Security Level: NIST Level 2 (128-bit classical security).
    /// Fastest Dilithium variant with smallest signatures.
    /// Use Case: Performance-sensitive quantum-safe signing.
    /// </summary>
    public sealed class MlDsa44Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "ML-DSA-44-Sign",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 2420,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "ML-DSA-44",
                ["Type"] = "Signature",
                ["NistLevel"] = 2,
                ["PublicKeySize"] = 1312,
                ["PrivateKeySize"] = 2560,
                ["SignatureSize"] = 2420
            }
        };

        public override string StrategyId => "ml-dsa-44";
        public override string StrategyName => "ML-DSA-44 (Dilithium-2) Signature";

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ml.dsa.44.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ml.dsa.44.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public MlDsa44Strategy()
        {
            _secureRandom = new SecureRandom();
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                AsymmetricCipherKeyPair keyPair;

                if (key.Length > 0)
                {
                    var privateKeyParams = MLDsaPrivateKeyParameters.FromEncoding(
                        MLDsaParameters.ml_dsa_44, key);
                    keyPair = new AsymmetricCipherKeyPair(null, privateKeyParams);
                }
                else
                {
                    var keyGenParams = new MLDsaKeyGenerationParameters(_secureRandom, MLDsaParameters.ml_dsa_44);
                    var keyPairGenerator = new MLDsaKeyPairGenerator();
                    keyPairGenerator.Init(keyGenParams);
                    keyPair = keyPairGenerator.GenerateKeyPair();
                }

                var signer = new MLDsaSigner(MLDsaParameters.ml_dsa_44, true);
                signer.Init(true, keyPair.Private);
                signer.BlockUpdate(plaintext, 0, plaintext.Length);
                var signature = signer.GenerateSignature();

                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);
                writer.Write(signature.Length);
                writer.Write(signature);
                writer.Write(plaintext);
                return ms.ToArray();
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                var signatureLength = reader.ReadInt32();
                var signature = reader.ReadBytes(signatureLength);
                var data = reader.ReadBytes((int)(ms.Length - ms.Position));

                var publicKeyParams = MLDsaPublicKeyParameters.FromEncoding(MLDsaParameters.ml_dsa_44, key);
                var verifier = new MLDsaSigner(MLDsaParameters.ml_dsa_44, true);
                verifier.Init(false, publicKeyParams);
                verifier.BlockUpdate(data, 0, data.Length);

                if (!verifier.VerifySignature(signature))
                    throw new CryptographicException("ML-DSA-44 signature verification failed.");

                return data;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            var keyGenParams = new MLDsaKeyGenerationParameters(_secureRandom, MLDsaParameters.ml_dsa_44);
            var keyPairGenerator = new MLDsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return ((MLDsaPrivateKeyParameters)keyPair.Private).GetEncoded();
        }
    }

    /// <summary>
    /// ML-DSA-87 (CRYSTALS-Dilithium-5) digital signature strategy.
    /// Security Level: NIST Level 5 (256-bit classical security).
    /// Maximum security Dilithium variant for classified/sensitive data.
    /// Use Case: Maximum assurance quantum-safe signing for government/military.
    /// </summary>
    public sealed class MlDsa87Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "ML-DSA-87-Sign",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 4627,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "ML-DSA-87",
                ["Type"] = "Signature",
                ["NistLevel"] = 5,
                ["PublicKeySize"] = 2592,
                ["PrivateKeySize"] = 4896,
                ["SignatureSize"] = 4627
            }
        };

        public override string StrategyId => "ml-dsa-87";
        public override string StrategyName => "ML-DSA-87 (Dilithium-5) Signature";

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ml.dsa.87.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ml.dsa.87.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public MlDsa87Strategy()
        {
            _secureRandom = new SecureRandom();
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                AsymmetricCipherKeyPair keyPair;

                if (key.Length > 0)
                {
                    var privateKeyParams = MLDsaPrivateKeyParameters.FromEncoding(
                        MLDsaParameters.ml_dsa_87, key);
                    keyPair = new AsymmetricCipherKeyPair(null, privateKeyParams);
                }
                else
                {
                    var keyGenParams = new MLDsaKeyGenerationParameters(_secureRandom, MLDsaParameters.ml_dsa_87);
                    var keyPairGenerator = new MLDsaKeyPairGenerator();
                    keyPairGenerator.Init(keyGenParams);
                    keyPair = keyPairGenerator.GenerateKeyPair();
                }

                var signer = new MLDsaSigner(MLDsaParameters.ml_dsa_87, true);
                signer.Init(true, keyPair.Private);
                signer.BlockUpdate(plaintext, 0, plaintext.Length);
                var signature = signer.GenerateSignature();

                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);
                writer.Write(signature.Length);
                writer.Write(signature);
                writer.Write(plaintext);
                return ms.ToArray();
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                var signatureLength = reader.ReadInt32();
                var signature = reader.ReadBytes(signatureLength);
                var data = reader.ReadBytes((int)(ms.Length - ms.Position));

                var publicKeyParams = MLDsaPublicKeyParameters.FromEncoding(MLDsaParameters.ml_dsa_87, key);
                var verifier = new MLDsaSigner(MLDsaParameters.ml_dsa_87, true);
                verifier.Init(false, publicKeyParams);
                verifier.BlockUpdate(data, 0, data.Length);

                if (!verifier.VerifySignature(signature))
                    throw new CryptographicException("ML-DSA-87 signature verification failed.");

                return data;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            var keyGenParams = new MLDsaKeyGenerationParameters(_secureRandom, MLDsaParameters.ml_dsa_87);
            var keyPairGenerator = new MLDsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return ((MLDsaPrivateKeyParameters)keyPair.Private).GetEncoded();
        }
    }

    /// <summary>
    /// SLH-DSA-SHA2-128f (SPHINCS+ with SHA-256, fast variant) signature strategy.
    /// Uses SHA-256 instead of SHAKE-128 for environments requiring FIPS-approved hash.
    /// Use Case: FIPS-compliant quantum-safe signatures.
    /// </summary>
    public sealed class SlhDsaSha2Strategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "SLH-DSA-SHA2-128f",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 17088,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "SLH-DSA-SHA2-128f",
                ["Type"] = "Signature",
                ["HashFunction"] = "SHA-256",
                ["Variant"] = "Fast",
                ["FipsCompliant"] = true
            }
        };

        public override string StrategyId => "slh-dsa-sha2-128f";
        public override string StrategyName => "SLH-DSA SHA2-128f (SPHINCS+ SHA-256)";

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("slh.dsa.sha2.128f.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("slh.dsa.sha2.128f.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public SlhDsaSha2Strategy()
        {
            _secureRandom = new SecureRandom();
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                AsymmetricCipherKeyPair keyPair;

                if (key.Length > 0)
                {
                    var privateKeyParams = SlhDsaPrivateKeyParameters.FromEncoding(
                        SlhDsaParameters.slh_dsa_sha2_128f, key);
                    keyPair = new AsymmetricCipherKeyPair(null, privateKeyParams);
                }
                else
                {
                    var keyGenParams = new SlhDsaKeyGenerationParameters(
                        _secureRandom, SlhDsaParameters.slh_dsa_sha2_128f);
                    var keyPairGenerator = new SlhDsaKeyPairGenerator();
                    keyPairGenerator.Init(keyGenParams);
                    keyPair = keyPairGenerator.GenerateKeyPair();
                }

                var signer = new SlhDsaSigner(SlhDsaParameters.slh_dsa_sha2_128f, true);
                signer.Init(true, keyPair.Private);
                signer.BlockUpdate(plaintext, 0, plaintext.Length);
                var signature = signer.GenerateSignature();

                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);
                writer.Write(signature.Length);
                writer.Write(signature);
                writer.Write(plaintext);
                return ms.ToArray();
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                var signatureLength = reader.ReadInt32();
                var signature = reader.ReadBytes(signatureLength);
                var data = reader.ReadBytes((int)(ms.Length - ms.Position));

                var publicKeyParams = SlhDsaPublicKeyParameters.FromEncoding(
                    SlhDsaParameters.slh_dsa_sha2_128f, key);
                var verifier = new SlhDsaSigner(SlhDsaParameters.slh_dsa_sha2_128f, true);
                verifier.Init(false, publicKeyParams);
                verifier.BlockUpdate(data, 0, data.Length);

                if (!verifier.VerifySignature(signature))
                    throw new CryptographicException("SLH-DSA-SHA2-128f signature verification failed.");

                return data;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            var keyGenParams = new SlhDsaKeyGenerationParameters(
                _secureRandom, SlhDsaParameters.slh_dsa_sha2_128f);
            var keyPairGenerator = new SlhDsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return ((SlhDsaPrivateKeyParameters)keyPair.Private).GetEncoded();
        }
    }

    /// <summary>
    /// SLH-DSA-SHAKE-256f (SPHINCS+ with SHAKE-256, fast variant) signature strategy.
    /// NIST Level 5 hash-based signatures for maximum assurance.
    /// Use Case: Maximum security quantum-safe signatures for classified data.
    /// </summary>
    public sealed class SlhDsaShake256fStrategy : EncryptionStrategyBase
    {
        private readonly SecureRandom _secureRandom;

        public override CipherInfo CipherInfo => new()
        {
            AlgorithmName = "SLH-DSA-SHAKE-256f",
            KeySizeBits = 0,
            BlockSizeBytes = 0,
            IvSizeBytes = 0,
            TagSizeBytes = 49856,
            Capabilities = new CipherCapabilities
            {
                IsAuthenticated = true,
                IsStreamable = false,
                IsHardwareAcceleratable = false,
                SupportsAead = false,
                SupportsParallelism = false,
                MinimumSecurityLevel = SecurityLevel.QuantumSafe
            },
            SecurityLevel = SecurityLevel.QuantumSafe,
            Parameters = new Dictionary<string, object>
            {
                ["Algorithm"] = "SLH-DSA-SHAKE-256f",
                ["Type"] = "Signature",
                ["HashFunction"] = "SHAKE-256",
                ["Variant"] = "Fast",
                ["NistLevel"] = 5
            }
        };

        public override string StrategyId => "slh-dsa-shake-256f";
        public override string StrategyName => "SLH-DSA SHAKE-256f (SPHINCS+ Level 5)";

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("slh.dsa.shake.256f.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("slh.dsa.shake.256f.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

        public SlhDsaShake256fStrategy()
        {
            _secureRandom = new SecureRandom();
        }

        protected override async Task<byte[]> EncryptCoreAsync(
            byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                AsymmetricCipherKeyPair keyPair;

                if (key.Length > 0)
                {
                    var privateKeyParams = SlhDsaPrivateKeyParameters.FromEncoding(
                        SlhDsaParameters.slh_dsa_shake_256f, key);
                    keyPair = new AsymmetricCipherKeyPair(null, privateKeyParams);
                }
                else
                {
                    var keyGenParams = new SlhDsaKeyGenerationParameters(
                        _secureRandom, SlhDsaParameters.slh_dsa_shake_256f);
                    var keyPairGenerator = new SlhDsaKeyPairGenerator();
                    keyPairGenerator.Init(keyGenParams);
                    keyPair = keyPairGenerator.GenerateKeyPair();
                }

                var signer = new SlhDsaSigner(SlhDsaParameters.slh_dsa_shake_256f, true);
                signer.Init(true, keyPair.Private);
                signer.BlockUpdate(plaintext, 0, plaintext.Length);
                var signature = signer.GenerateSignature();

                using var ms = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(ms);
                writer.Write(signature.Length);
                writer.Write(signature);
                writer.Write(plaintext);
                return ms.ToArray();
            }, cancellationToken);
        }

        protected override async Task<byte[]> DecryptCoreAsync(
            byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var ms = new System.IO.MemoryStream(ciphertext);
                using var reader = new System.IO.BinaryReader(ms);

                var signatureLength = reader.ReadInt32();
                var signature = reader.ReadBytes(signatureLength);
                var data = reader.ReadBytes((int)(ms.Length - ms.Position));

                var publicKeyParams = SlhDsaPublicKeyParameters.FromEncoding(
                    SlhDsaParameters.slh_dsa_shake_256f, key);
                var verifier = new SlhDsaSigner(SlhDsaParameters.slh_dsa_shake_256f, true);
                verifier.Init(false, publicKeyParams);
                verifier.BlockUpdate(data, 0, data.Length);

                if (!verifier.VerifySignature(signature))
                    throw new CryptographicException("SLH-DSA-SHAKE-256f signature verification failed.");

                return data;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            var keyGenParams = new SlhDsaKeyGenerationParameters(
                _secureRandom, SlhDsaParameters.slh_dsa_shake_256f);
            var keyPairGenerator = new SlhDsaKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return ((SlhDsaPrivateKeyParameters)keyPair.Private).GetEncoded();
        }
    }
}
