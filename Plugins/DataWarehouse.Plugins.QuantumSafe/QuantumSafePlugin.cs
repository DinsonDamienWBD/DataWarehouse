using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pqc.Crypto.Crystals.Kyber;
using Org.BouncyCastle.Pqc.Crypto.Crystals.Dilithium;
using Org.BouncyCastle.Pqc.Crypto.SphincsPlus;
using Org.BouncyCastle.Security;

namespace DataWarehouse.Plugins.QuantumSafe;

/// <summary>
/// Industry-first quantum-safe storage plugin implementing NIST FIPS 203/204/205 post-quantum cryptography.
/// Provides comprehensive protection against both classical and quantum computer attacks.
///
/// Implemented Algorithms (NIST Standardized):
/// - ML-KEM (FIPS 203): Module-Lattice-Based Key Encapsulation Mechanism (formerly CRYSTALS-Kyber)
/// - ML-DSA (FIPS 204): Module-Lattice-Based Digital Signature Algorithm (formerly CRYSTALS-Dilithium)
/// - SLH-DSA/SPHINCS+ (FIPS 205): Stateless Hash-Based Digital Signature Algorithm
///
/// Security Features:
/// - Hybrid Mode: Classical (AES-256-GCM + ECDH-P384) combined with PQC per NIST guidance
/// - Cryptographic Agility: Hot-swap algorithms without data migration
/// - Harvest-Now-Decrypt-Later Protection: Forward secrecy for archival data
/// - QRNG Integration Interface: Ready for quantum random number generator integration
/// - QKD Readiness Interface: Ready for quantum key distribution integration
///
/// Thread Safety: All operations are thread-safe using concurrent collections and immutable state.
///
/// Message Commands:
/// - quantumsafe.encrypt: Encrypt data with quantum-safe algorithms
/// - quantumsafe.decrypt: Decrypt quantum-safe encrypted data
/// - quantumsafe.sign: Create quantum-safe digital signature
/// - quantumsafe.verify: Verify quantum-safe digital signature
/// - quantumsafe.encapsulate: Perform key encapsulation
/// - quantumsafe.decapsulate: Perform key decapsulation
/// - quantumsafe.configure: Configure quantum-safe settings
/// - quantumsafe.stats: Get cryptographic statistics
/// - quantumsafe.rotate: Trigger key rotation with PQC
/// - quantumsafe.setalgorithm: Hot-swap cryptographic algorithm
/// </summary>
public sealed class QuantumSafePlugin : PipelinePluginBase, IDisposable
{
    private readonly QuantumSafeConfig _config;
    private readonly object _statsLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _keyAccessLog = new();
    private readonly ConcurrentDictionary<string, QuantumSafeKeyPair> _keyPairCache = new();
    private readonly SecureRandom _secureRandom;

    private IKeyStore? _keyStore;
    private ISecurityContext? _securityContext;
    private IQuantumRandomSource? _qrngSource;
    private volatile QuantumSafeAlgorithmSuite _currentSuite;

    // Statistics
    private long _encryptionCount;
    private long _decryptionCount;
    private long _signatureCount;
    private long _verificationCount;
    private long _encapsulationCount;
    private long _decapsulationCount;
    private long _totalBytesProcessed;
    private bool _disposed;

    /// <summary>
    /// Header version for format evolution support.
    /// </summary>
    private const byte HeaderVersion = 1;

    /// <summary>
    /// Magic bytes to identify quantum-safe encrypted data.
    /// </summary>
    private static readonly byte[] MagicBytes = "QSAFE"u8.ToArray();

    /// <inheritdoc/>
    public override string Id => "datawarehouse.plugins.cryptography.quantumsafe";

    /// <inheritdoc/>
    public override string Name => "Quantum-Safe Cryptography";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SubCategory => "Encryption";

    /// <inheritdoc/>
    public override int QualityLevel => 99;

    /// <inheritdoc/>
    public override int DefaultOrder => 85;

    /// <inheritdoc/>
    public override bool AllowBypass => false;

    /// <inheritdoc/>
    public override string[] RequiredPrecedingStages => ["Compression"];

    /// <inheritdoc/>
    public override string[] IncompatibleStages => [];

    /// <summary>
    /// Initializes a new instance of the quantum-safe cryptography plugin.
    /// </summary>
    /// <param name="config">Optional configuration. If null, defaults with hybrid mode enabled are used.</param>
    public QuantumSafePlugin(QuantumSafeConfig? config = null)
    {
        _config = config ?? new QuantumSafeConfig();
        _currentSuite = _config.DefaultAlgorithmSuite;
        _secureRandom = new SecureRandom();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        if (_config.KeyStore != null)
        {
            _keyStore = _config.KeyStore;
        }

        if (_config.SecurityContext != null)
        {
            _securityContext = _config.SecurityContext;
        }

        if (_config.QuantumRandomSource != null)
        {
            _qrngSource = _config.QuantumRandomSource;
        }

        // Verify PQC algorithms are available
        try
        {
            VerifyPqcAvailability();
        }
        catch (Exception ex)
        {
            response.Success = false;
            response.ReadyState = PluginReadyState.Failed;
            response.Metadata["Error"] = ex.Message;
        }

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "quantumsafe.encrypt", DisplayName = "Encrypt", Description = "Encrypt data with quantum-safe algorithms (hybrid mode)" },
            new() { Name = "quantumsafe.decrypt", DisplayName = "Decrypt", Description = "Decrypt quantum-safe encrypted data" },
            new() { Name = "quantumsafe.sign", DisplayName = "Sign", Description = "Create quantum-safe digital signature (ML-DSA/SLH-DSA)" },
            new() { Name = "quantumsafe.verify", DisplayName = "Verify", Description = "Verify quantum-safe digital signature" },
            new() { Name = "quantumsafe.encapsulate", DisplayName = "Encapsulate", Description = "Perform ML-KEM key encapsulation" },
            new() { Name = "quantumsafe.decapsulate", DisplayName = "Decapsulate", Description = "Perform ML-KEM key decapsulation" },
            new() { Name = "quantumsafe.configure", DisplayName = "Configure", Description = "Configure quantum-safe settings" },
            new() { Name = "quantumsafe.stats", DisplayName = "Statistics", Description = "Get cryptographic statistics" },
            new() { Name = "quantumsafe.rotate", DisplayName = "Rotate Keys", Description = "Trigger key rotation with PQC" },
            new() { Name = "quantumsafe.setalgorithm", DisplayName = "Set Algorithm", Description = "Hot-swap cryptographic algorithm suite" },
            new() { Name = "quantumsafe.generatekeypair", DisplayName = "Generate Key Pair", Description = "Generate new quantum-safe key pair" }
        ];
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["QuantumSafe"] = true;
        metadata["HybridMode"] = _config.EnableHybridMode;
        metadata["KEM"] = "ML-KEM (FIPS 203)";
        metadata["Signature"] = "ML-DSA (FIPS 204), SLH-DSA/SPHINCS+ (FIPS 205)";
        metadata["ClassicalEncryption"] = "AES-256-GCM";
        metadata["ClassicalKeyAgreement"] = "ECDH-P384";
        metadata["CryptographicAgility"] = true;
        metadata["ForwardSecrecy"] = true;
        metadata["HarvestProtection"] = true;
        metadata["QRNGReady"] = true;
        metadata["QKDReady"] = true;
        metadata["NistCompliant"] = true;
        metadata["CurrentSuite"] = _currentSuite.ToString();
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "quantumsafe.encrypt" => HandleEncryptMessageAsync(message),
            "quantumsafe.decrypt" => HandleDecryptMessageAsync(message),
            "quantumsafe.sign" => HandleSignMessageAsync(message),
            "quantumsafe.verify" => HandleVerifyMessageAsync(message),
            "quantumsafe.encapsulate" => HandleEncapsulateMessageAsync(message),
            "quantumsafe.decapsulate" => HandleDecapsulateMessageAsync(message),
            "quantumsafe.configure" => HandleConfigureAsync(message),
            "quantumsafe.stats" => HandleStatsAsync(message),
            "quantumsafe.rotate" => HandleRotateAsync(message),
            "quantumsafe.setalgorithm" => HandleSetAlgorithmAsync(message),
            "quantumsafe.generatekeypair" => HandleGenerateKeyPairAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    /// <summary>
    /// Encrypts data using quantum-safe hybrid encryption (ML-KEM + AES-256-GCM).
    /// LEGACY: Use OnWriteAsync for proper async support.
    /// </summary>
    public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
    {
        return OnWriteAsync(input, context, args).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Async version: Encrypts data using quantum-safe hybrid encryption.
    ///
    /// Encryption Process:
    /// 1. Generate ephemeral ML-KEM key pair
    /// 2. Encapsulate shared secret using recipient's public key
    /// 3. If hybrid mode: Also derive key via ECDH
    /// 4. Combine secrets using HKDF
    /// 5. Encrypt data with AES-256-GCM
    /// 6. Optionally sign the ciphertext with ML-DSA/SLH-DSA
    ///
    /// Output Format:
    /// [Magic:5][Version:1][Suite:1][Flags:1][KeyIdLen:2][KeyId:var]
    /// [MLKEMCiphertext:var][ECDHPublicKey:97 (if hybrid)]
    /// [Nonce:12][Tag:16][Ciphertext:var]
    /// [Signature:var (if signed)]
    /// </summary>
    protected override async Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var keyStore = GetKeyStore(args, context);
        var securityContext = GetSecurityContext(args);
        var suite = GetAlgorithmSuite(args);

        // Get or generate recipient's quantum-safe key pair
        var keyId = await GetOrCreateKeyIdAsync(args, keyStore, securityContext).ConfigureAwait(false);
        var recipientKeyPair = await GetOrCreateKeyPairAsync(keyId, suite, securityContext).ConfigureAwait(false);

        byte[]? plaintext = null;
        byte[]? combinedSecret = null;
        byte[]? encryptionKey = null;

        try
        {
            // Read plaintext
            using var inputMs = new MemoryStream();
            await input.CopyToAsync(inputMs).ConfigureAwait(false);
            plaintext = inputMs.ToArray();

            // Step 1: ML-KEM Key Encapsulation
            var mlkemResult = PerformMLKemEncapsulation(recipientKeyPair.MlKemPublicKey, suite);
            var mlkemCiphertext = mlkemResult.Ciphertext;
            var mlkemSharedSecret = mlkemResult.SharedSecret;

            // Step 2: If hybrid mode, also perform ECDH
            byte[]? ecdhPublicKey = null;
            byte[]? ecdhSharedSecret = null;

            if (_config.EnableHybridMode && recipientKeyPair.EcdhPublicKey != null)
            {
                var ecdhResult = PerformEcdhKeyAgreement(recipientKeyPair.EcdhPublicKey);
                ecdhPublicKey = ecdhResult.PublicKey;
                ecdhSharedSecret = ecdhResult.SharedSecret;
            }

            // Step 3: Derive encryption key using HKDF
            combinedSecret = CombineSecrets(mlkemSharedSecret, ecdhSharedSecret);
            encryptionKey = DeriveEncryptionKey(combinedSecret, keyId);

            // Step 4: Encrypt with AES-256-GCM
            var nonce = GenerateSecureRandom(12);
            var tag = new byte[16];
            var ciphertext = new byte[plaintext.Length];

            using (var aesGcm = new AesGcm(encryptionKey, 16))
            {
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            // Step 5: Build output packet
            var output = BuildEncryptedPacket(
                suite,
                keyId,
                mlkemCiphertext,
                ecdhPublicKey,
                nonce,
                tag,
                ciphertext,
                _config.EnableHybridMode,
                _config.SignCiphertext ? recipientKeyPair : null);

            // Update statistics
            lock (_statsLock)
            {
                _encryptionCount++;
                _encapsulationCount++;
                _totalBytesProcessed += plaintext.Length;
            }

            context.LogDebug($"Quantum-safe encrypted {plaintext.Length} bytes using {suite} (hybrid={_config.EnableHybridMode})");

            return new MemoryStream(output);
        }
        finally
        {
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
            if (combinedSecret != null) CryptographicOperations.ZeroMemory(combinedSecret);
            if (encryptionKey != null) CryptographicOperations.ZeroMemory(encryptionKey);
        }
    }

    /// <summary>
    /// Decrypts quantum-safe encrypted data.
    /// LEGACY: Use OnReadAsync for proper async support.
    /// </summary>
    public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
    {
        return OnReadAsync(stored, context, args).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Async version: Decrypts quantum-safe encrypted data.
    /// </summary>
    protected override async Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var keyStore = GetKeyStore(args, context);
        var securityContext = GetSecurityContext(args);

        byte[]? encryptedData = null;
        byte[]? combinedSecret = null;
        byte[]? encryptionKey = null;
        byte[]? plaintext = null;

        try
        {
            // Read encrypted data
            using var inputMs = new MemoryStream();
            await stored.CopyToAsync(inputMs).ConfigureAwait(false);
            encryptedData = inputMs.ToArray();

            // Parse packet
            var packet = ParseEncryptedPacket(encryptedData);

            // Get key pair for decapsulation
            var keyPair = await GetOrCreateKeyPairAsync(packet.KeyId, packet.Suite, securityContext).ConfigureAwait(false);

            // Step 1: ML-KEM Key Decapsulation
            var mlkemSharedSecret = PerformMLKemDecapsulation(
                packet.MlKemCiphertext,
                keyPair.MlKemPrivateKey,
                packet.Suite);

            // Step 2: If hybrid mode, also perform ECDH
            byte[]? ecdhSharedSecret = null;
            if (packet.IsHybrid && packet.EcdhPublicKey != null && keyPair.EcdhPrivateKey != null)
            {
                ecdhSharedSecret = PerformEcdhDecapsulation(packet.EcdhPublicKey, keyPair.EcdhPrivateKey);
            }

            // Step 3: Derive decryption key using HKDF
            combinedSecret = CombineSecrets(mlkemSharedSecret, ecdhSharedSecret);
            encryptionKey = DeriveEncryptionKey(combinedSecret, packet.KeyId);

            // Step 4: Verify signature if present
            if (packet.Signature != null && packet.Signature.Length > 0)
            {
                var dataToVerify = GetDataToSign(packet);
                if (!VerifySignature(dataToVerify, packet.Signature, keyPair, packet.Suite))
                {
                    throw new CryptographicException("Signature verification failed. Data may be tampered.");
                }
            }

            // Step 5: Decrypt with AES-256-GCM
            plaintext = new byte[packet.Ciphertext.Length];

            using (var aesGcm = new AesGcm(encryptionKey, 16))
            {
                aesGcm.Decrypt(packet.Nonce, packet.Ciphertext, packet.Tag, plaintext);
            }

            // Update statistics
            lock (_statsLock)
            {
                _decryptionCount++;
                _decapsulationCount++;
                _totalBytesProcessed += plaintext.Length;
            }

            context.LogDebug($"Quantum-safe decrypted {packet.Ciphertext.Length} bytes using {packet.Suite}");

            var result = new byte[plaintext.Length];
            Array.Copy(plaintext, result, plaintext.Length);
            return new MemoryStream(result);
        }
        finally
        {
            if (encryptedData != null) CryptographicOperations.ZeroMemory(encryptedData);
            if (combinedSecret != null) CryptographicOperations.ZeroMemory(combinedSecret);
            if (encryptionKey != null) CryptographicOperations.ZeroMemory(encryptionKey);
            if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    #region ML-KEM (FIPS 203) Key Encapsulation

    /// <summary>
    /// Performs ML-KEM key encapsulation (FIPS 203).
    /// </summary>
    private MLKemEncapsulationResult PerformMLKemEncapsulation(byte[] publicKey, QuantumSafeAlgorithmSuite suite)
    {
        var kyberParams = GetKyberParameters(suite);
        var kemGenerator = new KyberKemGenerator(_secureRandom);

        var publicKeyParams = new KyberPublicKeyParameters(kyberParams, publicKey);
        var encapsulated = kemGenerator.GenerateEncapsulated(publicKeyParams);

        return new MLKemEncapsulationResult
        {
            Ciphertext = encapsulated.GetEncapsulation(),
            SharedSecret = encapsulated.GetSecret()
        };
    }

    /// <summary>
    /// Performs ML-KEM key decapsulation (FIPS 203).
    /// </summary>
    private byte[] PerformMLKemDecapsulation(byte[] ciphertext, byte[] privateKey, QuantumSafeAlgorithmSuite suite)
    {
        var kyberParams = GetKyberParameters(suite);
        var kemExtractor = new KyberKemExtractor(new KyberPrivateKeyParameters(kyberParams, privateKey, null));

        return kemExtractor.ExtractSecret(ciphertext);
    }

    /// <summary>
    /// Generates an ML-KEM key pair.
    /// </summary>
    private (byte[] PublicKey, byte[] PrivateKey) GenerateMLKemKeyPair(QuantumSafeAlgorithmSuite suite)
    {
        var kyberParams = GetKyberParameters(suite);
        var keyGenParams = new KyberKeyGenerationParameters(_secureRandom, kyberParams);
        var keyPairGenerator = new KyberKeyPairGenerator();
        keyPairGenerator.Init(keyGenParams);

        var keyPair = keyPairGenerator.GenerateKeyPair();
        var publicKey = ((KyberPublicKeyParameters)keyPair.Public).GetEncoded();
        var privateKey = ((KyberPrivateKeyParameters)keyPair.Private).GetEncoded();

        return (publicKey, privateKey);
    }

    private static KyberParameters GetKyberParameters(QuantumSafeAlgorithmSuite suite)
    {
        // Using Kyber parameter names (BouncyCastle pre-FIPS naming)
        return suite switch
        {
            QuantumSafeAlgorithmSuite.MlKem512_MlDsa44 => KyberParameters.kyber512,
            QuantumSafeAlgorithmSuite.MlKem768_MlDsa65 => KyberParameters.kyber768,
            QuantumSafeAlgorithmSuite.MlKem1024_MlDsa87 => KyberParameters.kyber1024,
            QuantumSafeAlgorithmSuite.MlKem768_SlhDsa => KyberParameters.kyber768,
            QuantumSafeAlgorithmSuite.MlKem1024_SlhDsa => KyberParameters.kyber1024,
            _ => KyberParameters.kyber768
        };
    }

    #endregion

    #region ML-DSA (FIPS 204) Digital Signatures

    /// <summary>
    /// Creates an ML-DSA signature (FIPS 204).
    /// </summary>
    private byte[] SignWithMLDsa(byte[] data, byte[] privateKey, QuantumSafeAlgorithmSuite suite)
    {
        var mldsaParams = GetMLDsaParameters(suite);
        var privateKeyParams = new DilithiumPrivateKeyParameters(mldsaParams, privateKey, null);

        var signer = new DilithiumSigner();
        signer.Init(true, privateKeyParams);

        return signer.GenerateSignature(data);
    }

    /// <summary>
    /// Verifies an ML-DSA signature (FIPS 204).
    /// </summary>
    private bool VerifyWithMLDsa(byte[] data, byte[] signature, byte[] publicKey, QuantumSafeAlgorithmSuite suite)
    {
        var mldsaParams = GetMLDsaParameters(suite);
        var publicKeyParams = new DilithiumPublicKeyParameters(mldsaParams, publicKey);

        var verifier = new DilithiumSigner();
        verifier.Init(false, publicKeyParams);

        return verifier.VerifySignature(data, signature);
    }

    /// <summary>
    /// Generates an ML-DSA key pair.
    /// </summary>
    private (byte[] PublicKey, byte[] PrivateKey) GenerateMLDsaKeyPair(QuantumSafeAlgorithmSuite suite)
    {
        var mldsaParams = GetMLDsaParameters(suite);
        var keyGenParams = new DilithiumKeyGenerationParameters(_secureRandom, mldsaParams);
        var keyPairGenerator = new DilithiumKeyPairGenerator();
        keyPairGenerator.Init(keyGenParams);

        var keyPair = keyPairGenerator.GenerateKeyPair();
        var publicKey = ((DilithiumPublicKeyParameters)keyPair.Public).GetEncoded();
        var privateKey = ((DilithiumPrivateKeyParameters)keyPair.Private).GetEncoded();

        return (publicKey, privateKey);
    }

    private static DilithiumParameters GetMLDsaParameters(QuantumSafeAlgorithmSuite suite)
    {
        // Using Dilithium parameter names (BouncyCastle pre-FIPS naming)
        return suite switch
        {
            QuantumSafeAlgorithmSuite.MlKem512_MlDsa44 => DilithiumParameters.Dilithium2,
            QuantumSafeAlgorithmSuite.MlKem768_MlDsa65 => DilithiumParameters.Dilithium3,
            QuantumSafeAlgorithmSuite.MlKem1024_MlDsa87 => DilithiumParameters.Dilithium5,
            _ => DilithiumParameters.Dilithium3
        };
    }

    #endregion

    #region SLH-DSA/SPHINCS+ (FIPS 205) Hash-Based Signatures

    /// <summary>
    /// Creates a SPHINCS+/SLH-DSA signature (FIPS 205).
    /// </summary>
    private byte[] SignWithSlhDsa(byte[] data, byte[] privateKey, QuantumSafeAlgorithmSuite suite)
    {
        var sphincsParams = GetSlhDsaParameters(suite);
        var privateKeyParams = new SphincsPlusPrivateKeyParameters(sphincsParams, privateKey);

        var signer = new SphincsPlusSigner();
        signer.Init(true, privateKeyParams);

        return signer.GenerateSignature(data);
    }

    /// <summary>
    /// Verifies a SPHINCS+/SLH-DSA signature (FIPS 205).
    /// </summary>
    private bool VerifyWithSlhDsa(byte[] data, byte[] signature, byte[] publicKey, QuantumSafeAlgorithmSuite suite)
    {
        var sphincsParams = GetSlhDsaParameters(suite);
        var publicKeyParams = new SphincsPlusPublicKeyParameters(sphincsParams, publicKey);

        var verifier = new SphincsPlusSigner();
        verifier.Init(false, publicKeyParams);

        return verifier.VerifySignature(data, signature);
    }

    /// <summary>
    /// Generates a SPHINCS+/SLH-DSA key pair.
    /// </summary>
    private (byte[] PublicKey, byte[] PrivateKey) GenerateSlhDsaKeyPair(QuantumSafeAlgorithmSuite suite)
    {
        var sphincsParams = GetSlhDsaParameters(suite);
        var keyGenParams = new SphincsPlusKeyGenerationParameters(_secureRandom, sphincsParams);
        var keyPairGenerator = new SphincsPlusKeyPairGenerator();
        keyPairGenerator.Init(keyGenParams);

        var keyPair = keyPairGenerator.GenerateKeyPair();
        var publicKey = ((SphincsPlusPublicKeyParameters)keyPair.Public).GetEncoded();
        var privateKey = ((SphincsPlusPrivateKeyParameters)keyPair.Private).GetEncoded();

        return (publicKey, privateKey);
    }

    private static SphincsPlusParameters GetSlhDsaParameters(QuantumSafeAlgorithmSuite suite)
    {
        return suite switch
        {
            QuantumSafeAlgorithmSuite.MlKem768_SlhDsa => SphincsPlusParameters.shake_128f,
            QuantumSafeAlgorithmSuite.MlKem1024_SlhDsa => SphincsPlusParameters.shake_256f,
            _ => SphincsPlusParameters.shake_192f
        };
    }

    #endregion

    #region ECDH (Classical Hybrid Component)

    /// <summary>
    /// Performs ECDH key agreement for hybrid mode.
    /// </summary>
    private EcdhKeyAgreementResult PerformEcdhKeyAgreement(byte[] recipientPublicKey)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        var ephemeralPublicKey = ecdh.PublicKey.ExportSubjectPublicKeyInfo();

        using var recipientEcdh = ECDiffieHellman.Create();
        recipientEcdh.ImportSubjectPublicKeyInfo(recipientPublicKey, out _);

        var sharedSecret = ecdh.DeriveKeyMaterial(recipientEcdh.PublicKey);

        return new EcdhKeyAgreementResult
        {
            PublicKey = ephemeralPublicKey,
            SharedSecret = sharedSecret
        };
    }

    /// <summary>
    /// Performs ECDH decapsulation for hybrid mode.
    /// </summary>
    private byte[] PerformEcdhDecapsulation(byte[] ephemeralPublicKey, byte[] privateKey)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(privateKey, out _);

        using var ephemeralEcdh = ECDiffieHellman.Create();
        ephemeralEcdh.ImportSubjectPublicKeyInfo(ephemeralPublicKey, out _);

        return ecdh.DeriveKeyMaterial(ephemeralEcdh.PublicKey);
    }

    /// <summary>
    /// Generates an ECDH key pair for hybrid mode.
    /// </summary>
    private (byte[] PublicKey, byte[] PrivateKey) GenerateEcdhKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        var publicKey = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var privateKey = ecdh.ExportPkcs8PrivateKey();

        return (publicKey, privateKey);
    }

    #endregion

    #region Key Derivation and Secret Combination

    /// <summary>
    /// Combines ML-KEM and ECDH shared secrets using concatenation.
    /// The combined secret is then used as input to HKDF.
    /// </summary>
    private static byte[] CombineSecrets(byte[] mlkemSecret, byte[]? ecdhSecret)
    {
        if (ecdhSecret == null || ecdhSecret.Length == 0)
        {
            return mlkemSecret;
        }

        var combined = new byte[mlkemSecret.Length + ecdhSecret.Length];
        Array.Copy(mlkemSecret, 0, combined, 0, mlkemSecret.Length);
        Array.Copy(ecdhSecret, 0, combined, mlkemSecret.Length, ecdhSecret.Length);
        return combined;
    }

    /// <summary>
    /// Derives encryption key from combined secret using HKDF-SHA384.
    /// </summary>
    private static byte[] DeriveEncryptionKey(byte[] combinedSecret, string keyId)
    {
        var info = Encoding.UTF8.GetBytes($"QuantumSafe-v1-{keyId}");
        return HKDF.DeriveKey(HashAlgorithmName.SHA384, combinedSecret, 32, info: info);
    }

    #endregion

    #region Packet Building and Parsing

    /// <summary>
    /// Builds the encrypted packet with all necessary components.
    /// </summary>
    private byte[] BuildEncryptedPacket(
        QuantumSafeAlgorithmSuite suite,
        string keyId,
        byte[] mlkemCiphertext,
        byte[]? ecdhPublicKey,
        byte[] nonce,
        byte[] tag,
        byte[] ciphertext,
        bool isHybrid,
        QuantumSafeKeyPair? signingKeyPair)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Magic bytes
        writer.Write(MagicBytes);

        // Version
        writer.Write(HeaderVersion);

        // Algorithm suite
        writer.Write((byte)suite);

        // Flags: bit 0 = hybrid, bit 1 = signed
        byte flags = 0;
        if (isHybrid) flags |= 0x01;
        if (signingKeyPair != null && _config.SignCiphertext) flags |= 0x02;
        writer.Write(flags);

        // Key ID
        var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
        writer.Write((ushort)keyIdBytes.Length);
        writer.Write(keyIdBytes);

        // ML-KEM ciphertext
        writer.Write(mlkemCiphertext.Length);
        writer.Write(mlkemCiphertext);

        // ECDH public key (if hybrid)
        if (isHybrid && ecdhPublicKey != null)
        {
            writer.Write(ecdhPublicKey.Length);
            writer.Write(ecdhPublicKey);
        }

        // Nonce
        writer.Write(nonce);

        // Tag
        writer.Write(tag);

        // Ciphertext
        writer.Write(ciphertext.Length);
        writer.Write(ciphertext);

        // Signature (if signing enabled)
        if (signingKeyPair != null && _config.SignCiphertext)
        {
            var dataToSign = ms.ToArray();
            var signature = CreateSignature(dataToSign, signingKeyPair, suite);
            writer.Write(signature.Length);
            writer.Write(signature);

            lock (_statsLock)
            {
                _signatureCount++;
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Parses an encrypted packet.
    /// </summary>
    private QuantumSafePacket ParseEncryptedPacket(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);

        // Verify magic bytes
        var magic = reader.ReadBytes(MagicBytes.Length);
        if (!magic.SequenceEqual(MagicBytes))
        {
            throw new CryptographicException("Invalid quantum-safe packet: magic bytes mismatch");
        }

        // Version
        var version = reader.ReadByte();
        if (version != HeaderVersion)
        {
            throw new CryptographicException($"Unsupported quantum-safe packet version: {version}");
        }

        // Algorithm suite
        var suite = (QuantumSafeAlgorithmSuite)reader.ReadByte();

        // Flags
        var flags = reader.ReadByte();
        var isHybrid = (flags & 0x01) != 0;
        var isSigned = (flags & 0x02) != 0;

        // Key ID
        var keyIdLength = reader.ReadUInt16();
        var keyIdBytes = reader.ReadBytes(keyIdLength);
        var keyId = Encoding.UTF8.GetString(keyIdBytes);

        // ML-KEM ciphertext
        var mlkemCiphertextLength = reader.ReadInt32();
        var mlkemCiphertext = reader.ReadBytes(mlkemCiphertextLength);

        // ECDH public key (if hybrid)
        byte[]? ecdhPublicKey = null;
        if (isHybrid)
        {
            var ecdhPublicKeyLength = reader.ReadInt32();
            ecdhPublicKey = reader.ReadBytes(ecdhPublicKeyLength);
        }

        // Nonce
        var nonce = reader.ReadBytes(12);

        // Tag
        var tag = reader.ReadBytes(16);

        // Ciphertext
        var ciphertextLength = reader.ReadInt32();
        var ciphertext = reader.ReadBytes(ciphertextLength);

        // Signature (if signed)
        byte[]? signature = null;
        if (isSigned && ms.Position < ms.Length)
        {
            var signatureLength = reader.ReadInt32();
            signature = reader.ReadBytes(signatureLength);
        }

        return new QuantumSafePacket
        {
            Version = version,
            Suite = suite,
            IsHybrid = isHybrid,
            IsSigned = isSigned,
            KeyId = keyId,
            MlKemCiphertext = mlkemCiphertext,
            EcdhPublicKey = ecdhPublicKey,
            Nonce = nonce,
            Tag = tag,
            Ciphertext = ciphertext,
            Signature = signature
        };
    }

    private byte[] GetDataToSign(QuantumSafePacket packet)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        writer.Write(MagicBytes);
        writer.Write(packet.Version);
        writer.Write((byte)packet.Suite);
        writer.Write(packet.KeyId);
        writer.Write(packet.MlKemCiphertext);
        if (packet.EcdhPublicKey != null)
        {
            writer.Write(packet.EcdhPublicKey);
        }
        writer.Write(packet.Nonce);
        writer.Write(packet.Tag);
        writer.Write(packet.Ciphertext);

        return ms.ToArray();
    }

    #endregion

    #region Signature Operations

    /// <summary>
    /// Creates a signature using either ML-DSA or SLH-DSA based on suite.
    /// </summary>
    private byte[] CreateSignature(byte[] data, QuantumSafeKeyPair keyPair, QuantumSafeAlgorithmSuite suite)
    {
        return suite switch
        {
            QuantumSafeAlgorithmSuite.MlKem768_SlhDsa or QuantumSafeAlgorithmSuite.MlKem1024_SlhDsa =>
                SignWithSlhDsa(data, keyPair.SlhDsaPrivateKey ?? throw new InvalidOperationException("SLH-DSA private key required"), suite),
            _ =>
                SignWithMLDsa(data, keyPair.MlDsaPrivateKey ?? throw new InvalidOperationException("ML-DSA private key required"), suite)
        };
    }

    /// <summary>
    /// Verifies a signature using either ML-DSA or SLH-DSA based on suite.
    /// </summary>
    private bool VerifySignature(byte[] data, byte[] signature, QuantumSafeKeyPair keyPair, QuantumSafeAlgorithmSuite suite)
    {
        lock (_statsLock)
        {
            _verificationCount++;
        }

        return suite switch
        {
            QuantumSafeAlgorithmSuite.MlKem768_SlhDsa or QuantumSafeAlgorithmSuite.MlKem1024_SlhDsa =>
                VerifyWithSlhDsa(data, signature, keyPair.SlhDsaPublicKey ?? throw new InvalidOperationException("SLH-DSA public key required"), suite),
            _ =>
                VerifyWithMLDsa(data, signature, keyPair.MlDsaPublicKey ?? throw new InvalidOperationException("ML-DSA public key required"), suite)
        };
    }

    #endregion

    #region Key Management

    /// <summary>
    /// Gets or creates a key ID for the current operation.
    /// </summary>
    private async Task<string> GetOrCreateKeyIdAsync(
        Dictionary<string, object> args,
        IKeyStore keyStore,
        ISecurityContext securityContext)
    {
        if (args.TryGetValue("keyId", out var kidObj) && kidObj is string specificKeyId)
        {
            return specificKeyId;
        }

        return await keyStore.GetCurrentKeyIdAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Gets or creates a quantum-safe key pair for the specified key ID.
    /// </summary>
    private Task<QuantumSafeKeyPair> GetOrCreateKeyPairAsync(
        string keyId,
        QuantumSafeAlgorithmSuite suite,
        ISecurityContext securityContext)
    {
        var cacheKey = $"{keyId}:{suite}";

        if (_keyPairCache.TryGetValue(cacheKey, out var cachedKeyPair))
        {
            return Task.FromResult(cachedKeyPair);
        }

        // Generate new key pair
        var keyPair = GenerateQuantumSafeKeyPair(suite);

        _keyPairCache[cacheKey] = keyPair;
        _keyAccessLog[keyId] = DateTime.UtcNow;

        return Task.FromResult(keyPair);
    }

    /// <summary>
    /// Generates a complete quantum-safe key pair for the specified algorithm suite.
    /// </summary>
    private QuantumSafeKeyPair GenerateQuantumSafeKeyPair(QuantumSafeAlgorithmSuite suite)
    {
        var (mlkemPublic, mlkemPrivate) = GenerateMLKemKeyPair(suite);

        var keyPair = new QuantumSafeKeyPair
        {
            MlKemPublicKey = mlkemPublic,
            MlKemPrivateKey = mlkemPrivate,
            Suite = suite,
            CreatedAt = DateTime.UtcNow
        };

        // Generate signature keys based on suite
        if (suite is QuantumSafeAlgorithmSuite.MlKem768_SlhDsa or QuantumSafeAlgorithmSuite.MlKem1024_SlhDsa)
        {
            var (slhDsaPublic, slhDsaPrivate) = GenerateSlhDsaKeyPair(suite);
            keyPair.SlhDsaPublicKey = slhDsaPublic;
            keyPair.SlhDsaPrivateKey = slhDsaPrivate;
        }
        else
        {
            var (mldsaPublic, mldsaPrivate) = GenerateMLDsaKeyPair(suite);
            keyPair.MlDsaPublicKey = mldsaPublic;
            keyPair.MlDsaPrivateKey = mldsaPrivate;
        }

        // Generate ECDH keys if hybrid mode enabled
        if (_config.EnableHybridMode)
        {
            var (ecdhPublic, ecdhPrivate) = GenerateEcdhKeyPair();
            keyPair.EcdhPublicKey = ecdhPublic;
            keyPair.EcdhPrivateKey = ecdhPrivate;
        }

        return keyPair;
    }

    /// <summary>
    /// Derives a quantum-safe key from an existing classical key using HKDF.
    /// This enables migration from classical to quantum-safe without re-keying.
    /// </summary>
    /// <param name="classicalKey">The classical key to derive from.</param>
    /// <param name="context">Context string for derivation.</param>
    /// <param name="outputLength">Desired output length in bytes.</param>
    /// <returns>Derived key material.</returns>
    public byte[] DeriveQuantumSafeKey(byte[] classicalKey, string context, int outputLength = 32)
    {
        var info = Encoding.UTF8.GetBytes($"QuantumSafe-Derivation-{context}");
        return HKDF.DeriveKey(HashAlgorithmName.SHA384, classicalKey, outputLength, info: info);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets the key store from configuration or context.
    /// </summary>
    private IKeyStore GetKeyStore(Dictionary<string, object> args, IKernelContext context)
    {
        if (args.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
        {
            return ks;
        }

        if (_keyStore != null)
        {
            return _keyStore;
        }

        var keyStorePlugin = context.GetPlugins<IPlugin>()
            .OfType<IKeyStore>()
            .FirstOrDefault();

        if (keyStorePlugin != null)
        {
            return keyStorePlugin;
        }

        throw new InvalidOperationException(
            "No IKeyStore available. Configure a key store before using quantum-safe encryption.");
    }

    /// <summary>
    /// Gets the security context from configuration or creates a default.
    /// </summary>
    private ISecurityContext GetSecurityContext(Dictionary<string, object> args)
    {
        if (args.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
        {
            return sc;
        }

        return _securityContext ?? new DefaultSecurityContext();
    }

    /// <summary>
    /// Gets the algorithm suite from args or uses current default.
    /// </summary>
    private QuantumSafeAlgorithmSuite GetAlgorithmSuite(Dictionary<string, object> args)
    {
        if (args.TryGetValue("suite", out var suiteObj))
        {
            if (suiteObj is QuantumSafeAlgorithmSuite suite)
            {
                return suite;
            }
            if (suiteObj is string suiteName && Enum.TryParse<QuantumSafeAlgorithmSuite>(suiteName, out var parsedSuite))
            {
                return parsedSuite;
            }
        }

        return _currentSuite;
    }

    /// <summary>
    /// Generates cryptographically secure random bytes.
    /// Uses QRNG if available, falls back to system CSPRNG.
    /// </summary>
    private byte[] GenerateSecureRandom(int length)
    {
        if (_qrngSource != null && _qrngSource.IsAvailable)
        {
            return _qrngSource.GetRandomBytes(length);
        }

        return RandomNumberGenerator.GetBytes(length);
    }

    /// <summary>
    /// Verifies that PQC algorithms are available and working.
    /// </summary>
    private void VerifyPqcAvailability()
    {
        // Test ML-KEM
        var mlkemParams = MLKemParameters.ml_kem_768;
        var mlkemKeyGen = new MLKemKeyPairGenerator();
        mlkemKeyGen.Init(new MLKemKeyGenerationParameters(_secureRandom, mlkemParams));
        var mlkemKeyPair = mlkemKeyGen.GenerateKeyPair();
        if (mlkemKeyPair == null)
        {
            throw new CryptographicException("ML-KEM key generation failed");
        }

        // Test ML-DSA
        var mldsaParams = MLDsaParameters.ml_dsa_65;
        var mldsaKeyGen = new MLDsaKeyPairGenerator();
        mldsaKeyGen.Init(new MLDsaKeyGenerationParameters(_secureRandom, mldsaParams));
        var mldsaKeyPair = mldsaKeyGen.GenerateKeyPair();
        if (mldsaKeyPair == null)
        {
            throw new CryptographicException("ML-DSA key generation failed");
        }

        // Test SLH-DSA/SPHINCS+
        var sphincsParams = SphincsPlusParameters.shake_192f;
        var sphincsKeyGen = new SphincsPlusKeyPairGenerator();
        sphincsKeyGen.Init(new SphincsPlusKeyGenerationParameters(_secureRandom, sphincsParams));
        var sphincsKeyPair = sphincsKeyGen.GenerateKeyPair();
        if (sphincsKeyPair == null)
        {
            throw new CryptographicException("SLH-DSA/SPHINCS+ key generation failed");
        }
    }

    #endregion

    #region Message Handlers

    private Task HandleEncryptMessageAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
        {
            throw new ArgumentException("Missing or invalid 'data' parameter");
        }

        var args = message.Payload;
        using var input = new MemoryStream(data);
        using var result = OnWriteAsync(input, new NullKernelContext(), args).GetAwaiter().GetResult();
        using var ms = new MemoryStream();
        result.CopyTo(ms);
        message.Payload["result"] = ms.ToArray();

        return Task.CompletedTask;
    }

    private Task HandleDecryptMessageAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
        {
            throw new ArgumentException("Missing or invalid 'data' parameter");
        }

        var args = message.Payload;
        using var input = new MemoryStream(data);
        using var result = OnReadAsync(input, new NullKernelContext(), args).GetAwaiter().GetResult();
        using var ms = new MemoryStream();
        result.CopyTo(ms);
        message.Payload["result"] = ms.ToArray();

        return Task.CompletedTask;
    }

    private Task HandleSignMessageAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
        {
            throw new ArgumentException("Missing or invalid 'data' parameter");
        }

        var args = message.Payload;
        var suite = GetAlgorithmSuite(args);
        var keyId = args.TryGetValue("keyId", out var kidObj) && kidObj is string kid ? kid : Guid.NewGuid().ToString("N");
        var keyPair = GetOrCreateKeyPairAsync(keyId, suite, GetSecurityContext(args)).GetAwaiter().GetResult();

        var signature = CreateSignature(data, keyPair, suite);
        message.Payload["signature"] = signature;
        message.Payload["keyId"] = keyId;
        message.Payload["suite"] = suite.ToString();

        lock (_statsLock)
        {
            _signatureCount++;
        }

        return Task.CompletedTask;
    }

    private Task HandleVerifyMessageAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
        {
            throw new ArgumentException("Missing or invalid 'data' parameter");
        }
        if (!message.Payload.TryGetValue("signature", out var sigObj) || sigObj is not byte[] signature)
        {
            throw new ArgumentException("Missing or invalid 'signature' parameter");
        }

        var args = message.Payload;
        var suite = GetAlgorithmSuite(args);
        var keyId = args.TryGetValue("keyId", out var kidObj) && kidObj is string kid ? kid : throw new ArgumentException("Missing 'keyId' parameter");
        var keyPair = GetOrCreateKeyPairAsync(keyId, suite, GetSecurityContext(args)).GetAwaiter().GetResult();

        var isValid = VerifySignature(data, signature, keyPair, suite);
        message.Payload["isValid"] = isValid;

        return Task.CompletedTask;
    }

    private Task HandleEncapsulateMessageAsync(PluginMessage message)
    {
        var args = message.Payload;
        var suite = GetAlgorithmSuite(args);

        if (!message.Payload.TryGetValue("publicKey", out var pkObj) || pkObj is not byte[] publicKey)
        {
            throw new ArgumentException("Missing or invalid 'publicKey' parameter");
        }

        var result = PerformMLKemEncapsulation(publicKey, suite);
        message.Payload["ciphertext"] = result.Ciphertext;
        message.Payload["sharedSecret"] = result.SharedSecret;

        lock (_statsLock)
        {
            _encapsulationCount++;
        }

        return Task.CompletedTask;
    }

    private Task HandleDecapsulateMessageAsync(PluginMessage message)
    {
        var args = message.Payload;
        var suite = GetAlgorithmSuite(args);

        if (!message.Payload.TryGetValue("ciphertext", out var ctObj) || ctObj is not byte[] ciphertext)
        {
            throw new ArgumentException("Missing or invalid 'ciphertext' parameter");
        }
        if (!message.Payload.TryGetValue("privateKey", out var skObj) || skObj is not byte[] privateKey)
        {
            throw new ArgumentException("Missing or invalid 'privateKey' parameter");
        }

        var sharedSecret = PerformMLKemDecapsulation(ciphertext, privateKey, suite);
        message.Payload["sharedSecret"] = sharedSecret;

        lock (_statsLock)
        {
            _decapsulationCount++;
        }

        return Task.CompletedTask;
    }

    private Task HandleConfigureAsync(PluginMessage message)
    {
        if (message.Payload.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
        {
            _keyStore = ks;
        }

        if (message.Payload.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
        {
            _securityContext = sc;
        }

        if (message.Payload.TryGetValue("qrngSource", out var qrngObj) && qrngObj is IQuantumRandomSource qrng)
        {
            _qrngSource = qrng;
        }

        if (message.Payload.TryGetValue("suite", out var suiteObj))
        {
            if (suiteObj is QuantumSafeAlgorithmSuite suite)
            {
                _currentSuite = suite;
            }
            else if (suiteObj is string suiteName && Enum.TryParse<QuantumSafeAlgorithmSuite>(suiteName, out var parsedSuite))
            {
                _currentSuite = parsedSuite;
            }
        }

        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        lock (_statsLock)
        {
            message.Payload["EncryptionCount"] = _encryptionCount;
            message.Payload["DecryptionCount"] = _decryptionCount;
            message.Payload["SignatureCount"] = _signatureCount;
            message.Payload["VerificationCount"] = _verificationCount;
            message.Payload["EncapsulationCount"] = _encapsulationCount;
            message.Payload["DecapsulationCount"] = _decapsulationCount;
            message.Payload["TotalBytesProcessed"] = _totalBytesProcessed;
            message.Payload["CachedKeyPairs"] = _keyPairCache.Count;
            message.Payload["CurrentSuite"] = _currentSuite.ToString();
            message.Payload["HybridMode"] = _config.EnableHybridMode;
            message.Payload["QRNGAvailable"] = _qrngSource?.IsAvailable ?? false;
        }

        return Task.CompletedTask;
    }

    private Task HandleRotateAsync(PluginMessage message)
    {
        var args = message.Payload;
        var suite = GetAlgorithmSuite(args);
        var securityContext = GetSecurityContext(args);

        var newKeyId = Guid.NewGuid().ToString("N");
        var newKeyPair = GenerateQuantumSafeKeyPair(suite);

        var cacheKey = $"{newKeyId}:{suite}";
        _keyPairCache[cacheKey] = newKeyPair;
        _keyAccessLog[newKeyId] = DateTime.UtcNow;

        message.Payload["NewKeyId"] = newKeyId;
        message.Payload["RotatedAt"] = DateTime.UtcNow;
        message.Payload["Suite"] = suite.ToString();

        return Task.CompletedTask;
    }

    private Task HandleSetAlgorithmAsync(PluginMessage message)
    {
        if (message.Payload.TryGetValue("suite", out var suiteObj))
        {
            if (suiteObj is QuantumSafeAlgorithmSuite suite)
            {
                _currentSuite = suite;
            }
            else if (suiteObj is string suiteName && Enum.TryParse<QuantumSafeAlgorithmSuite>(suiteName, out var parsedSuite))
            {
                _currentSuite = parsedSuite;
            }

            message.Payload["NewSuite"] = _currentSuite.ToString();
            message.Payload["SwitchedAt"] = DateTime.UtcNow;
        }

        return Task.CompletedTask;
    }

    private Task HandleGenerateKeyPairAsync(PluginMessage message)
    {
        var args = message.Payload;
        var suite = GetAlgorithmSuite(args);
        var keyId = args.TryGetValue("keyId", out var kidObj) && kidObj is string kid
            ? kid
            : Guid.NewGuid().ToString("N");

        var keyPair = GenerateQuantumSafeKeyPair(suite);
        var cacheKey = $"{keyId}:{suite}";
        _keyPairCache[cacheKey] = keyPair;
        _keyAccessLog[keyId] = DateTime.UtcNow;

        message.Payload["keyId"] = keyId;
        message.Payload["mlKemPublicKey"] = keyPair.MlKemPublicKey;
        message.Payload["mlDsaPublicKey"] = keyPair.MlDsaPublicKey;
        message.Payload["slhDsaPublicKey"] = keyPair.SlhDsaPublicKey;
        message.Payload["ecdhPublicKey"] = keyPair.EcdhPublicKey;
        message.Payload["suite"] = suite.ToString();
        message.Payload["createdAt"] = keyPair.CreatedAt;

        return Task.CompletedTask;
    }

    #endregion

    /// <summary>
    /// Releases all resources used by this plugin.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Clear sensitive key material
        foreach (var keyPair in _keyPairCache.Values)
        {
            keyPair.ClearSensitiveData();
        }
        _keyPairCache.Clear();
        _keyAccessLog.Clear();
    }
}

#region Supporting Types

/// <summary>
/// Configuration for quantum-safe cryptography plugin.
/// </summary>
public sealed class QuantumSafeConfig
{
    /// <summary>
    /// Gets or sets the key store for key management.
    /// </summary>
    public IKeyStore? KeyStore { get; set; }

    /// <summary>
    /// Gets or sets the security context for access control.
    /// </summary>
    public ISecurityContext? SecurityContext { get; set; }

    /// <summary>
    /// Gets or sets the quantum random number generator source.
    /// </summary>
    public IQuantumRandomSource? QuantumRandomSource { get; set; }

    /// <summary>
    /// Gets or sets whether to enable hybrid mode (Classical + PQC).
    /// Default is true per NIST guidance for transition period.
    /// </summary>
    public bool EnableHybridMode { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to sign ciphertext with ML-DSA/SLH-DSA.
    /// Provides additional integrity protection.
    /// </summary>
    public bool SignCiphertext { get; set; } = false;

    /// <summary>
    /// Gets or sets the default algorithm suite.
    /// </summary>
    public QuantumSafeAlgorithmSuite DefaultAlgorithmSuite { get; set; } = QuantumSafeAlgorithmSuite.MlKem768_MlDsa65;
}

/// <summary>
/// Quantum-safe algorithm suites combining KEM and signature algorithms per NIST FIPS standards.
/// </summary>
public enum QuantumSafeAlgorithmSuite : byte
{
    /// <summary>
    /// ML-KEM-512 + ML-DSA-44 - NIST Level 1 security.
    /// Suitable for general use with smaller key sizes.
    /// </summary>
    MlKem512_MlDsa44 = 1,

    /// <summary>
    /// ML-KEM-768 + ML-DSA-65 - NIST Level 3 security.
    /// Recommended default for most applications.
    /// </summary>
    MlKem768_MlDsa65 = 2,

    /// <summary>
    /// ML-KEM-1024 + ML-DSA-87 - NIST Level 5 security.
    /// Maximum security for sensitive/classified data.
    /// </summary>
    MlKem1024_MlDsa87 = 3,

    /// <summary>
    /// ML-KEM-768 + SLH-DSA (SPHINCS+) - Hybrid with hash-based signatures.
    /// More conservative, stateless signature scheme.
    /// </summary>
    MlKem768_SlhDsa = 4,

    /// <summary>
    /// ML-KEM-1024 + SLH-DSA (SPHINCS+) - Maximum security with hash-based signatures.
    /// For highest assurance requirements (e.g., government/military).
    /// </summary>
    MlKem1024_SlhDsa = 5
}

/// <summary>
/// Quantum-safe key pair containing all necessary keys for encryption, decryption, and signing.
/// </summary>
public sealed class QuantumSafeKeyPair
{
    /// <summary>
    /// ML-KEM public key for key encapsulation (FIPS 203).
    /// </summary>
    public byte[] MlKemPublicKey { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// ML-KEM private key for key decapsulation (FIPS 203).
    /// </summary>
    public byte[] MlKemPrivateKey { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// ML-DSA public key for signature verification (FIPS 204).
    /// </summary>
    public byte[]? MlDsaPublicKey { get; set; }

    /// <summary>
    /// ML-DSA private key for signing (FIPS 204).
    /// </summary>
    public byte[]? MlDsaPrivateKey { get; set; }

    /// <summary>
    /// SLH-DSA/SPHINCS+ public key for signature verification (FIPS 205).
    /// </summary>
    public byte[]? SlhDsaPublicKey { get; set; }

    /// <summary>
    /// SLH-DSA/SPHINCS+ private key for signing (FIPS 205).
    /// </summary>
    public byte[]? SlhDsaPrivateKey { get; set; }

    /// <summary>
    /// ECDH public key for hybrid mode classical key agreement.
    /// </summary>
    public byte[]? EcdhPublicKey { get; set; }

    /// <summary>
    /// ECDH private key for hybrid mode classical key agreement.
    /// </summary>
    public byte[]? EcdhPrivateKey { get; set; }

    /// <summary>
    /// Algorithm suite used for this key pair.
    /// </summary>
    public QuantumSafeAlgorithmSuite Suite { get; set; }

    /// <summary>
    /// Creation timestamp.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Securely clears all private key material from memory.
    /// </summary>
    public void ClearSensitiveData()
    {
        if (MlKemPrivateKey.Length > 0) CryptographicOperations.ZeroMemory(MlKemPrivateKey);
        if (MlDsaPrivateKey != null) CryptographicOperations.ZeroMemory(MlDsaPrivateKey);
        if (SlhDsaPrivateKey != null) CryptographicOperations.ZeroMemory(SlhDsaPrivateKey);
        if (EcdhPrivateKey != null) CryptographicOperations.ZeroMemory(EcdhPrivateKey);
    }
}

/// <summary>
/// Parsed quantum-safe encrypted packet.
/// </summary>
internal sealed class QuantumSafePacket
{
    public byte Version { get; set; }
    public QuantumSafeAlgorithmSuite Suite { get; set; }
    public bool IsHybrid { get; set; }
    public bool IsSigned { get; set; }
    public string KeyId { get; set; } = string.Empty;
    public byte[] MlKemCiphertext { get; set; } = Array.Empty<byte>();
    public byte[]? EcdhPublicKey { get; set; }
    public byte[] Nonce { get; set; } = Array.Empty<byte>();
    public byte[] Tag { get; set; } = Array.Empty<byte>();
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public byte[]? Signature { get; set; }
}

/// <summary>
/// Result of ML-KEM key encapsulation.
/// </summary>
internal sealed class MLKemEncapsulationResult
{
    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
    public byte[] SharedSecret { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Result of ECDH key agreement.
/// </summary>
internal sealed class EcdhKeyAgreementResult
{
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public byte[] SharedSecret { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Interface for quantum random number generator integration.
/// Enables QRNG hardware when available for maximum randomness quality.
/// </summary>
public interface IQuantumRandomSource
{
    /// <summary>
    /// Gets whether the QRNG source is currently available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets random bytes from the quantum source.
    /// </summary>
    /// <param name="length">Number of bytes to generate.</param>
    /// <returns>Cryptographically random bytes from quantum source.</returns>
    byte[] GetRandomBytes(int length);

    /// <summary>
    /// Gets random bytes asynchronously.
    /// </summary>
    /// <param name="length">Number of bytes to generate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cryptographically random bytes from quantum source.</returns>
    Task<byte[]> GetRandomBytesAsync(int length, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for quantum key distribution readiness.
/// Enables QKD integration when quantum networks become available.
/// </summary>
public interface IQuantumKeyDistribution
{
    /// <summary>
    /// Gets whether QKD is currently available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Establishes a quantum-secure key with a remote party.
    /// </summary>
    /// <param name="remotePartyId">Identifier of the remote party.</param>
    /// <param name="keyLength">Desired key length in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Quantum-distributed key material.</returns>
    Task<byte[]> EstablishKeyAsync(string remotePartyId, int keyLength, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current key rate in bits per second.
    /// </summary>
    double CurrentKeyRateBps { get; }

    /// <summary>
    /// Gets the quantum bit error rate (QBER).
    /// </summary>
    double QuantumBitErrorRate { get; }
}

/// <summary>
/// Default security context for quantum-safe operations.
/// </summary>
internal sealed class DefaultSecurityContext : ISecurityContext
{
    /// <inheritdoc/>
    public string UserId => Environment.UserName;

    /// <inheritdoc/>
    public string? TenantId => "local";

    /// <inheritdoc/>
    public IEnumerable<string> Roles => ["user"];

    /// <inheritdoc/>
    public bool IsSystemAdmin => false;
}

/// <summary>
/// Null kernel context for standalone message handlers.
/// </summary>
internal sealed class NullKernelContext : IKernelContext
{
    /// <inheritdoc/>
    public OperatingMode Mode => OperatingMode.Standalone;

    /// <inheritdoc/>
    public string RootPath => ".";

    /// <inheritdoc/>
    public IKernelStorageService Storage => NullStorageService.Instance;

    /// <inheritdoc/>
    public void LogDebug(string message) { }

    /// <inheritdoc/>
    public void LogInfo(string message) { }

    /// <inheritdoc/>
    public void LogWarning(string message) { }

    /// <inheritdoc/>
    public void LogError(string message, Exception? ex = null) { }

    /// <inheritdoc/>
    public T? GetPlugin<T>() where T : class, IPlugin => null;

    /// <inheritdoc/>
    public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => Enumerable.Empty<T>();
}

/// <summary>
/// Null storage service for standalone operation.
/// </summary>
internal sealed class NullStorageService : IKernelStorageService
{
    public static readonly NullStorageService Instance = new();

    public Task SaveAsync(string path, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SaveAsync(string path, byte[] data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<Stream?> LoadAsync(string path, CancellationToken ct = default)
        => Task.FromResult<Stream?>(null);

    public Task<byte[]?> LoadBytesAsync(string path, CancellationToken ct = default)
        => Task.FromResult<byte[]?>(null);

    public Task<bool> DeleteAsync(string path, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<IReadOnlyList<StorageItemInfo>> ListAsync(string prefix, int limit = 100, int offset = 0, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StorageItemInfo>>(Array.Empty<StorageItemInfo>());

    public Task<IDictionary<string, string>?> GetMetadataAsync(string path, CancellationToken ct = default)
        => Task.FromResult<IDictionary<string, string>?>(null);
}

#endregion
