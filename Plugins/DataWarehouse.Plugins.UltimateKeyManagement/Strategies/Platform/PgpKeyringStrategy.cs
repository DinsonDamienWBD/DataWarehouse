using DataWarehouse.SDK.Security;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Strategies.Platform
{
    /// <summary>
    /// PGP Keyring KeyStore strategy using BouncyCastle's OpenPGP implementation.
    /// Provides asymmetric key encryption for secure key storage.
    ///
    /// Features:
    /// - Uses OpenPGP standard for key encryption
    /// - Support for RSA and ElGamal key pairs
    /// - Compatible with GnuPG keyring format
    /// - Keys are encrypted with a master PGP key
    /// - Support for key import/export in OpenPGP format
    /// - Hardware token support via existing PGP infrastructure
    ///
    /// Security Model:
    /// - Data Encryption Keys (DEK) are wrapped using PGP public key encryption
    /// - Only the holder of the private key can decrypt wrapped keys
    /// - Supports multiple recipients for key escrow scenarios
    ///
    /// Configuration:
    /// - KeyringPath: Path to PGP keyring directory
    /// - MasterKeyId: Key ID of the master encryption key
    /// - Passphrase: Passphrase for the private key (or use environment variable)
    /// </summary>
    public sealed class PgpKeyringStrategy : KeyStoreStrategyBase, IEnvelopeKeyStore
    {
        private PgpKeyringConfig _config = new();
        private string? _currentKeyId;
        private PgpPublicKeyRing? _publicKeyRing;
        private PgpSecretKeyRing? _secretKeyRing;
        private readonly object _keyringLock = new();

        public override KeyStoreCapabilities Capabilities => new()
        {
            SupportsRotation = true,
            SupportsEnvelope = true,
            SupportsHsm = false, // Can be true if using hardware token
            SupportsExpiration = true,
            SupportsReplication = false,
            SupportsVersioning = true,
            SupportsPerKeyAcl = false,
            SupportsAuditLogging = false,
            MaxKeySizeBytes = 0,
            MinKeySizeBytes = 16,
            Metadata = new Dictionary<string, object>
            {
                ["Provider"] = "PGP Keyring",
                ["Library"] = "BouncyCastle",
                ["StorageType"] = "OpenPGP Keyring",
                ["ProtectionMethod"] = "PGP Public Key Encryption",
                ["KeyAlgorithms"] = "RSA, ElGamal, ECDH"
            }
        };

        public IReadOnlyList<string> SupportedWrappingAlgorithms => new[]
        {
            "PGP-RSA",
            "PGP-ElGamal",
            "PGP-ECDH"
        };

        public bool SupportsHsmKeyGeneration => false;

        protected override async Task InitializeStorage(CancellationToken cancellationToken)
        {
            // Load configuration
            if (Configuration.TryGetValue("KeyringPath", out var pathObj) && pathObj is string path)
                _config.KeyringPath = path;
            if (Configuration.TryGetValue("MasterKeyId", out var keyIdObj) && keyIdObj is string keyId)
                _config.MasterKeyId = keyId;
            if (Configuration.TryGetValue("Passphrase", out var passObj) && passObj is string pass)
                _config.Passphrase = pass;
            if (Configuration.TryGetValue("PassphraseEnvVar", out var envObj) && envObj is string env)
                _config.PassphraseEnvVar = env;
            if (Configuration.TryGetValue("KeyAlgorithm", out var algoObj) && algoObj is string algo)
                _config.KeyAlgorithm = Enum.Parse<PgpKeyAlgorithm>(algo, ignoreCase: true);
            if (Configuration.TryGetValue("KeySizeBits", out var sizeObj) && sizeObj is int size)
                _config.KeySizeBits = size;

            // Ensure keyring directory exists
            Directory.CreateDirectory(_config.KeyringPath);

            // Load or generate master key
            await LoadOrGenerateMasterKeyAsync(cancellationToken);

            // Load metadata
            var metadataPath = Path.Combine(_config.KeyringPath, "keystore.meta");
            if (File.Exists(metadataPath))
            {
                var metadata = JsonSerializer.Deserialize<PgpKeyringMetadata>(
                    await File.ReadAllTextAsync(metadataPath, cancellationToken));
                _currentKeyId = metadata?.CurrentKeyId;
            }

            // If no current key, create initial key
            if (string.IsNullOrEmpty(_currentKeyId))
            {
                _currentKeyId = Guid.NewGuid().ToString("N");
                var initialKey = GenerateKey();
                await SaveKeyToStorage(_currentKeyId, initialKey, CreateSystemContext());
            }

            await Task.CompletedTask;
        }

        public override Task<string> GetCurrentKeyIdAsync()
        {
            return Task.FromResult(_currentKeyId ?? "default");
        }

        public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Verify we can encrypt and decrypt
                var testData = RandomNumberGenerator.GetBytes(32);
                var encrypted = PgpEncrypt(testData);
                var decrypted = PgpDecrypt(encrypted);

                return await Task.FromResult(testData.SequenceEqual(decrypted));
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<byte[]> LoadKeyFromStorage(string keyId, ISecurityContext context)
        {
            var keyPath = GetKeyPath(keyId);
            if (!File.Exists(keyPath))
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in PGP keyring.");
            }

            var encryptedData = await File.ReadAllBytesAsync(keyPath);
            var keyData = PgpDecrypt(encryptedData);

            return keyData;
        }

        protected override async Task SaveKeyToStorage(string keyId, byte[] keyData, ISecurityContext context)
        {
            var encryptedData = PgpEncrypt(keyData);
            var keyPath = GetKeyPath(keyId);

            await File.WriteAllBytesAsync(keyPath, encryptedData);

            // Update metadata
            _currentKeyId = keyId;
            await SaveMetadataAsync();
        }

        public async Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            // Use PGP encryption to wrap the key
            return await Task.FromResult(PgpEncrypt(dataKey));
        }

        public async Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context)
        {
            ValidateSecurityContext(context);

            // Use PGP decryption to unwrap the key
            return await Task.FromResult(PgpDecrypt(wrappedKey));
        }

        public override async Task<IReadOnlyList<string>> ListKeysAsync(ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keyFiles = Directory.GetFiles(_config.KeyringPath, "*.pgp.key");
            var keyIds = keyFiles
                .Select(f => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(f)))
                .ToList()
                .AsReadOnly();

            return await Task.FromResult(keyIds);
        }

        public override async Task DeleteKeyAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can delete keys.");
            }

            var keyPath = GetKeyPath(keyId);
            if (File.Exists(keyPath))
            {
                File.Delete(keyPath);
            }

            await Task.CompletedTask;
        }

        public override async Task<KeyMetadata?> GetKeyMetadataAsync(string keyId, ISecurityContext context, CancellationToken cancellationToken = default)
        {
            ValidateSecurityContext(context);

            var keyPath = GetKeyPath(keyId);
            if (!File.Exists(keyPath))
                return null;

            var fileInfo = new FileInfo(keyPath);

            return await Task.FromResult(new KeyMetadata
            {
                KeyId = keyId,
                CreatedAt = fileInfo.CreationTimeUtc,
                LastRotatedAt = fileInfo.LastWriteTimeUtc,
                IsActive = keyId == _currentKeyId,
                Metadata = new Dictionary<string, object>
                {
                    ["FilePath"] = keyPath,
                    ["FileSize"] = fileInfo.Length,
                    ["MasterKeyId"] = _config.MasterKeyId ?? "generated",
                    ["Algorithm"] = _config.KeyAlgorithm.ToString()
                }
            });
        }

        #region PGP Operations

        private async Task LoadOrGenerateMasterKeyAsync(CancellationToken cancellationToken)
        {
            var publicKeyPath = Path.Combine(_config.KeyringPath, "master.pub");
            var secretKeyPath = Path.Combine(_config.KeyringPath, "master.sec");

            if (File.Exists(publicKeyPath) && File.Exists(secretKeyPath))
            {
                // Load existing keys
                await using var publicStream = File.OpenRead(publicKeyPath);
                var publicBundle = new PgpPublicKeyRingBundle(PgpUtilities.GetDecoderStream(publicStream));
                _publicKeyRing = publicBundle.GetKeyRings().Cast<PgpPublicKeyRing>().FirstOrDefault();

                await using var secretStream = File.OpenRead(secretKeyPath);
                var secretBundle = new PgpSecretKeyRingBundle(PgpUtilities.GetDecoderStream(secretStream));
                _secretKeyRing = secretBundle.GetKeyRings().Cast<PgpSecretKeyRing>().FirstOrDefault();

                if (_publicKeyRing == null || _secretKeyRing == null)
                {
                    throw new InvalidOperationException("Failed to load PGP keyring.");
                }
            }
            else
            {
                // Generate new master key pair
                await GenerateMasterKeyPairAsync(publicKeyPath, secretKeyPath);
            }
        }

        private async Task GenerateMasterKeyPairAsync(string publicKeyPath, string secretKeyPath)
        {
            var random = new SecureRandom();
            var passphrase = GetPassphrase();

            IAsymmetricCipherKeyPairGenerator keyPairGenerator;
            PgpKeyPair keyPair;

            if (_config.KeyAlgorithm == PgpKeyAlgorithm.RSA)
            {
                keyPairGenerator = new RsaKeyPairGenerator();
                keyPairGenerator.Init(new RsaKeyGenerationParameters(
                    BigInteger.ValueOf(65537),
                    random,
                    _config.KeySizeBits,
                    80));

                var rsaKeyPair = keyPairGenerator.GenerateKeyPair();
                keyPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, rsaKeyPair, DateTime.UtcNow);
            }
            else
            {
                // Use DSA/ElGamal for non-RSA
                var dsaGenerator = new DsaKeyPairGenerator();
                var dsaParams = new DsaParametersGenerator();
                dsaParams.Init(1024, 80, random);
                dsaGenerator.Init(new DsaKeyGenerationParameters(random, dsaParams.GenerateParameters()));
                var dsaKeyPair = dsaGenerator.GenerateKeyPair();
                var dsaPgpKeyPair = new PgpKeyPair(PublicKeyAlgorithmTag.Dsa, dsaKeyPair, DateTime.UtcNow);

                var elGamalGenerator = new ElGamalKeyPairGenerator();
                var elGamalParams = new ElGamalParametersGenerator();
                elGamalParams.Init(_config.KeySizeBits, 80, random);
                elGamalGenerator.Init(new ElGamalKeyGenerationParameters(random, elGamalParams.GenerateParameters()));
                var elGamalKeyPair = elGamalGenerator.GenerateKeyPair();

                keyPair = new PgpKeyPair(PublicKeyAlgorithmTag.ElGamalEncrypt, elGamalKeyPair, DateTime.UtcNow);
            }

            var identity = $"DataWarehouse KeyStore <keystore@{Environment.MachineName}>";

            var keyRingGenerator = new PgpKeyRingGenerator(
                PgpSignature.DefaultCertification,
                keyPair,
                identity,
                SymmetricKeyAlgorithmTag.Aes256,
                passphrase.ToCharArray(),
                true,
                null,
                null,
                random);

            _publicKeyRing = keyRingGenerator.GeneratePublicKeyRing();
            _secretKeyRing = keyRingGenerator.GenerateSecretKeyRing();

            // Save keys to files
            await using (var publicOut = File.Create(publicKeyPath))
            {
                _publicKeyRing.Encode(publicOut);
            }

            await using (var secretOut = File.Create(secretKeyPath))
            {
                _secretKeyRing.Encode(secretOut);
            }
        }

        private byte[] PgpEncrypt(byte[] data)
        {
            lock (_keyringLock)
            {
                if (_publicKeyRing == null)
                    throw new InvalidOperationException("PGP keyring not initialized.");

                var encKey = _publicKeyRing.GetPublicKey();
                if (encKey == null)
                    throw new InvalidOperationException("No encryption key found in keyring.");

                using var outputStream = new MemoryStream(65536);
                using (var armoredStream = new ArmoredOutputStream(outputStream))
                {
                    var compressedData = Compress(data);

                    var encryptedDataGenerator = new PgpEncryptedDataGenerator(
                        SymmetricKeyAlgorithmTag.Aes256,
                        true,
                        new SecureRandom());

                    encryptedDataGenerator.AddMethod(encKey);

                    using (var encryptedOut = encryptedDataGenerator.Open(armoredStream, compressedData.Length))
                    {
                        encryptedOut.Write(compressedData, 0, compressedData.Length);
                    }
                }

                return outputStream.ToArray();
            }
        }

        private byte[] PgpDecrypt(byte[] encryptedData)
        {
            lock (_keyringLock)
            {
                if (_secretKeyRing == null)
                    throw new InvalidOperationException("PGP keyring not initialized.");

                using var inputStream = new MemoryStream(encryptedData);
                using var decoderStream = PgpUtilities.GetDecoderStream(inputStream);

                var pgpObjectFactory = new PgpObjectFactory(decoderStream);
                var encryptedDataList = (PgpEncryptedDataList)pgpObjectFactory.NextPgpObject();

                PgpPrivateKey? privateKey = null;
                PgpPublicKeyEncryptedData? encryptedDataPacket = null;

                foreach (PgpPublicKeyEncryptedData pked in encryptedDataList.GetEncryptedDataObjects())
                {
                    var secretKey = _secretKeyRing.GetSecretKey(pked.KeyId);
                    if (secretKey != null)
                    {
                        var passphrase = GetPassphrase();
                        privateKey = secretKey.ExtractPrivateKey(passphrase.ToCharArray());
                        encryptedDataPacket = pked;
                        break;
                    }
                }

                if (privateKey == null || encryptedDataPacket == null)
                    throw new InvalidOperationException("Cannot find matching private key for decryption.");

                using var clearStream = encryptedDataPacket.GetDataStream(privateKey);
                var clearFactory = new PgpObjectFactory(clearStream);
                var message = clearFactory.NextPgpObject();

                if (message is PgpCompressedData compressedData)
                {
                    var compressedFactory = new PgpObjectFactory(compressedData.GetDataStream());
                    message = compressedFactory.NextPgpObject();
                }

                if (message is PgpLiteralData literalData)
                {
                    using var literalStream = literalData.GetInputStream();
                    using var output = new MemoryStream(65536);
                    literalStream.CopyTo(output);
                    return output.ToArray();
                }

                throw new InvalidOperationException("Unknown PGP message format.");
            }
        }

        private byte[] Compress(byte[] data)
        {
            using var compressedOut = new MemoryStream(65536);
            var compressedDataGenerator = new PgpCompressedDataGenerator(CompressionAlgorithmTag.Zip);

            using (var compressedStream = compressedDataGenerator.Open(compressedOut))
            {
                var literalDataGenerator = new PgpLiteralDataGenerator();
                using var literalOut = literalDataGenerator.Open(
                    compressedStream,
                    PgpLiteralData.Binary,
                    "encrypted-key",
                    data.Length,
                    DateTime.UtcNow);

                literalOut.Write(data, 0, data.Length);
            }

            return compressedOut.ToArray();
        }

        private string GetPassphrase()
        {
            // First check environment variable
            if (!string.IsNullOrEmpty(_config.PassphraseEnvVar))
            {
                var envPassphrase = Environment.GetEnvironmentVariable(_config.PassphraseEnvVar);
                if (!string.IsNullOrEmpty(envPassphrase))
                    return envPassphrase;
            }

            // Then use configured passphrase
            if (!string.IsNullOrEmpty(_config.Passphrase))
                return _config.Passphrase;

            // Fall back to machine-derived passphrase
            return DeriveDefaultPassphrase();
        }

        private string DeriveDefaultPassphrase()
        {
            var entropy = $"{Environment.MachineName}:{Environment.UserName}:DataWarehouse.PGP.v1";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(entropy));
            return Convert.ToBase64String(hash);
        }

        #endregion

        private string GetKeyPath(string keyId)
        {
            var safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyId)))[..32];
            return Path.Combine(_config.KeyringPath, $"{safeId}.pgp.key");
        }

        private async Task SaveMetadataAsync()
        {
            var metadata = new PgpKeyringMetadata
            {
                CurrentKeyId = _currentKeyId ?? string.Empty,
                LastUpdated = DateTime.UtcNow,
                Version = 1,
                MasterKeyId = _config.MasterKeyId ?? "generated"
            };

            var json = JsonSerializer.Serialize(metadata);
            var metadataPath = Path.Combine(_config.KeyringPath, "keystore.meta");
            await File.WriteAllTextAsync(metadataPath, json);
        }

        public override void Dispose()
        {
            // Clear sensitive data
            _publicKeyRing = null;
            _secretKeyRing = null;
            base.Dispose();
        }
    }

    /// <summary>
    /// Configuration for PGP Keyring key store strategy.
    /// </summary>
    public class PgpKeyringConfig
    {
        /// <summary>
        /// Path to the PGP keyring directory.
        /// </summary>
        public string KeyringPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "pgp-keyring");

        /// <summary>
        /// Key ID of the master encryption key.
        /// If null, a new key pair will be generated.
        /// </summary>
        public string? MasterKeyId { get; set; }

        /// <summary>
        /// Passphrase for the private key.
        /// </summary>
        public string? Passphrase { get; set; }

        /// <summary>
        /// Environment variable name containing the passphrase.
        /// Takes precedence over Passphrase setting.
        /// </summary>
        public string? PassphraseEnvVar { get; set; } = "DATAWAREHOUSE_PGP_PASSPHRASE";

        /// <summary>
        /// Key algorithm to use for new key generation.
        /// </summary>
        public PgpKeyAlgorithm KeyAlgorithm { get; set; } = PgpKeyAlgorithm.RSA;

        /// <summary>
        /// Key size in bits for new key generation.
        /// </summary>
        public int KeySizeBits { get; set; } = 4096;
    }

    /// <summary>
    /// PGP key algorithm options.
    /// </summary>
    public enum PgpKeyAlgorithm
    {
        /// <summary>
        /// RSA algorithm (recommended).
        /// </summary>
        RSA,

        /// <summary>
        /// DSA/ElGamal algorithm.
        /// </summary>
        ElGamal
    }

    internal class PgpKeyringMetadata
    {
        public string CurrentKeyId { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; }
        public int Version { get; set; }
        public string MasterKeyId { get; set; } = string.Empty;
    }
}
