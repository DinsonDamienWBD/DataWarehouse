using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Pqc.Crypto.Crystals.Dilithium;
using Org.BouncyCastle.Pqc.Crypto.SphincsPlus;

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
                    var privateKeyParams = new DilithiumPrivateKeyParameters(
                        DilithiumParameters.Dilithium2, key, null);
                    keyPair = new AsymmetricCipherKeyPair(null, privateKeyParams);
                }
                else
                {
                    var keyGenParams = new DilithiumKeyGenerationParameters(_secureRandom, DilithiumParameters.Dilithium2);
                    var keyPairGenerator = new DilithiumKeyPairGenerator();
                    keyPairGenerator.Init(keyGenParams);
                    keyPair = keyPairGenerator.GenerateKeyPair();
                }

                var signer = new DilithiumSigner();
                signer.Init(true, keyPair.Private);
                var signature = signer.GenerateSignature(plaintext);

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

                var publicKeyParams = new DilithiumPublicKeyParameters(DilithiumParameters.Dilithium2, key);
                var verifier = new DilithiumSigner();
                verifier.Init(false, publicKeyParams);

                if (!verifier.VerifySignature(data, signature))
                    throw new CryptographicException("ML-DSA-44 signature verification failed.");

                return data;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            var keyGenParams = new DilithiumKeyGenerationParameters(_secureRandom, DilithiumParameters.Dilithium2);
            var keyPairGenerator = new DilithiumKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return ((DilithiumPrivateKeyParameters)keyPair.Private).GetEncoded();
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
                    var privateKeyParams = new DilithiumPrivateKeyParameters(
                        DilithiumParameters.Dilithium5, key, null);
                    keyPair = new AsymmetricCipherKeyPair(null, privateKeyParams);
                }
                else
                {
                    var keyGenParams = new DilithiumKeyGenerationParameters(_secureRandom, DilithiumParameters.Dilithium5);
                    var keyPairGenerator = new DilithiumKeyPairGenerator();
                    keyPairGenerator.Init(keyGenParams);
                    keyPair = keyPairGenerator.GenerateKeyPair();
                }

                var signer = new DilithiumSigner();
                signer.Init(true, keyPair.Private);
                var signature = signer.GenerateSignature(plaintext);

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

                var publicKeyParams = new DilithiumPublicKeyParameters(DilithiumParameters.Dilithium5, key);
                var verifier = new DilithiumSigner();
                verifier.Init(false, publicKeyParams);

                if (!verifier.VerifySignature(data, signature))
                    throw new CryptographicException("ML-DSA-87 signature verification failed.");

                return data;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            var keyGenParams = new DilithiumKeyGenerationParameters(_secureRandom, DilithiumParameters.Dilithium5);
            var keyPairGenerator = new DilithiumKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return ((DilithiumPrivateKeyParameters)keyPair.Private).GetEncoded();
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
                    var privateKeyParams = new SphincsPlusPrivateKeyParameters(
                        SphincsPlusParameters.sha2_128f, key);
                    keyPair = new AsymmetricCipherKeyPair(null, privateKeyParams);
                }
                else
                {
                    var keyGenParams = new SphincsPlusKeyGenerationParameters(
                        _secureRandom, SphincsPlusParameters.sha2_128f);
                    var keyPairGenerator = new SphincsPlusKeyPairGenerator();
                    keyPairGenerator.Init(keyGenParams);
                    keyPair = keyPairGenerator.GenerateKeyPair();
                }

                var signer = new SphincsPlusSigner();
                signer.Init(true, keyPair.Private);
                var signature = signer.GenerateSignature(plaintext);

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

                var publicKeyParams = new SphincsPlusPublicKeyParameters(
                    SphincsPlusParameters.sha2_128f, key);
                var verifier = new SphincsPlusSigner();
                verifier.Init(false, publicKeyParams);

                if (!verifier.VerifySignature(data, signature))
                    throw new CryptographicException("SLH-DSA-SHA2-128f signature verification failed.");

                return data;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            var keyGenParams = new SphincsPlusKeyGenerationParameters(
                _secureRandom, SphincsPlusParameters.sha2_128f);
            var keyPairGenerator = new SphincsPlusKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return ((SphincsPlusPrivateKeyParameters)keyPair.Private).GetEncoded();
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
                    var privateKeyParams = new SphincsPlusPrivateKeyParameters(
                        SphincsPlusParameters.shake_256f, key);
                    keyPair = new AsymmetricCipherKeyPair(null, privateKeyParams);
                }
                else
                {
                    var keyGenParams = new SphincsPlusKeyGenerationParameters(
                        _secureRandom, SphincsPlusParameters.shake_256f);
                    var keyPairGenerator = new SphincsPlusKeyPairGenerator();
                    keyPairGenerator.Init(keyGenParams);
                    keyPair = keyPairGenerator.GenerateKeyPair();
                }

                var signer = new SphincsPlusSigner();
                signer.Init(true, keyPair.Private);
                var signature = signer.GenerateSignature(plaintext);

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

                var publicKeyParams = new SphincsPlusPublicKeyParameters(
                    SphincsPlusParameters.shake_256f, key);
                var verifier = new SphincsPlusSigner();
                verifier.Init(false, publicKeyParams);

                if (!verifier.VerifySignature(data, signature))
                    throw new CryptographicException("SLH-DSA-SHAKE-256f signature verification failed.");

                return data;
            }, cancellationToken);
        }

        public override byte[] GenerateKey()
        {
            var keyGenParams = new SphincsPlusKeyGenerationParameters(
                _secureRandom, SphincsPlusParameters.shake_256f);
            var keyPairGenerator = new SphincsPlusKeyPairGenerator();
            keyPairGenerator.Init(keyGenParams);
            var keyPair = keyPairGenerator.GenerateKeyPair();
            return ((SphincsPlusPrivateKeyParameters)keyPair.Private).GetEncoded();
        }
    }
}
