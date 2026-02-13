using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using HierarchyEncryptionPluginBase = DataWarehouse.SDK.Contracts.Hierarchy.EncryptionPluginBase;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateEncryption;

/// <summary>
/// Ultimate Encryption Plugin - Comprehensive encryption solution consolidating all encryption strategies.
///
/// Implements 70+ encryption algorithms across categories:
/// - AES (all modes: GCM, CBC, CTR, CCM, OCB, SIV, GCM-SIV, XTS, Key Wrap)
/// - ChaCha/Salsa family (ChaCha20-Poly1305, XChaCha20, Salsa20)
/// - Block ciphers (Serpent, Twofish, Camellia, ARIA, SM4, SEED, Kuznyechik, Magma)
/// - Legacy ciphers (Blowfish, IDEA, CAST5/6, RC5/6, DES, 3DES)
/// - AEAD constructs (Deoxys, Ascon, AEGIS)
/// - Post-quantum encryption (ML-KEM/Kyber, NTRU, SABER, McEliece, FrodoKEM, BIKE, HQC)
/// - Post-quantum signatures (ML-DSA/Dilithium, SLH-DSA/SPHINCS+, Falcon)
/// - Hybrid encryption (Classical + Post-Quantum)
/// - Disk encryption modes (XTS, Adiantum, ESSIV)
/// - Format-Preserving Encryption (FF1, FF3-1)
/// - Homomorphic encryption (SEAL BFV/CKKS, TFHE, OpenFHE)
///
/// Features:
/// - Strategy pattern for algorithm extensibility
/// - Auto-discovery of strategies
/// - Hardware acceleration detection (AES-NI, AVX2)
/// - FIPS compliance validation
/// - Envelope encryption support
/// - Cipher cascade (multiple algorithms)
/// - Streaming encryption
/// - Audit logging
/// - Algorithm agility (re-encryption)
/// - Intelligence-aware cipher recommendations
/// - Threat assessment integration
/// </summary>
public sealed class UltimateEncryptionPlugin : HierarchyEncryptionPluginBase, IDisposable
{
    private readonly EncryptionStrategyRegistry _registry;
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private readonly object _statsLock = new();
    private bool _disposed;

    // Hardware acceleration flags
    private readonly bool _aesNiAvailable;
    private readonly bool _avx2Available;

    // Configuration
    private volatile string _defaultStrategyId = "aes-256-gcm";
    private volatile bool _fipsMode;
    private volatile bool _auditEnabled = true;

    // Statistics
    private long _totalEncryptions;
    private long _totalDecryptions;
    private long _totalBytesEncrypted;
    private long _totalBytesDecrypted;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.encryption.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Encryption";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SubCategory => "Encryption";

    /// <inheritdoc/>
    public override int QualityLevel => 100;

    /// <inheritdoc/>
    public override int DefaultPipelineOrder => 90;

    /// <inheritdoc/>
    public override bool AllowBypass => false;

    /// <inheritdoc/>
    public override IReadOnlyList<string> RequiredPrecedingStages => ["Compression"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> IncompatibleStages => [];

    /// <summary>Primary encryption algorithm name.</summary>
    public string Algorithm => _defaultStrategyId;

    /// <inheritdoc/>
    public override string AlgorithmId => _defaultStrategyId;

    /// <inheritdoc/>
    public override int KeySizeBytes => 32; // AES-256 default

    /// <inheritdoc/>
    public override int IvSizeBytes => 12; // GCM nonce default

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate encryption plugin providing 70+ encryption algorithms including AES-GCM, ChaCha20-Poly1305, " +
        "post-quantum cryptography (ML-KEM, ML-DSA), homomorphic encryption, and format-preserving encryption. " +
        "Supports FIPS compliance, hardware acceleration, envelope encryption, and cipher cascading.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags => [
        "encryption", "security", "cryptography", "aes", "chacha20", "post-quantum",
        "fips", "compliance", "envelope-encryption", "hardware-acceleration"
    ];

    /// <summary>
    /// Gets the encryption strategy registry.
    /// </summary>
    public IEncryptionStrategyRegistry Registry => _registry;

    /// <summary>
    /// Gets whether AES-NI hardware acceleration is available.
    /// </summary>
    public bool AesNiAvailable => _aesNiAvailable;

    /// <summary>
    /// Gets whether AVX2 is available for vectorized operations.
    /// </summary>
    public bool Avx2Available => _avx2Available;

    /// <summary>
    /// Gets or sets whether FIPS compliance mode is enabled.
    /// When enabled, only FIPS-approved algorithms are available.
    /// </summary>
    public bool FipsMode
    {
        get => _fipsMode;
        set => _fipsMode = value;
    }

    /// <summary>
    /// Initializes a new instance of the Ultimate Encryption plugin.
    /// </summary>
    public UltimateEncryptionPlugin()
    {
        _registry = new EncryptionStrategyRegistry();

        // Detect hardware acceleration
        _aesNiAvailable = System.Runtime.Intrinsics.X86.Aes.IsSupported;
        _avx2Available = Avx2.IsSupported;

        // Auto-discover and register strategies
        DiscoverAndRegisterStrategies();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        // Register knowledge and capabilities
        await RegisterAllKnowledgeAsync();

        response.Metadata["RegisteredStrategies"] = _registry.GetAllStrategies().Count.ToString();
        response.Metadata["AesNiAvailable"] = _aesNiAvailable.ToString();
        response.Metadata["Avx2Available"] = _avx2Available.ToString();
        response.Metadata["FipsMode"] = _fipsMode.ToString();
        response.Metadata["DefaultStrategy"] = _defaultStrategyId;

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "encryption.encrypt", DisplayName = "Encrypt", Description = "Encrypt data using selected strategy" },
            new() { Name = "encryption.decrypt", DisplayName = "Decrypt", Description = "Decrypt encrypted data" },
            new() { Name = "encryption.list-strategies", DisplayName = "List Strategies", Description = "List available encryption strategies" },
            new() { Name = "encryption.set-default", DisplayName = "Set Default", Description = "Set default encryption strategy" },
            new() { Name = "encryption.set-fips", DisplayName = "FIPS Mode", Description = "Enable/disable FIPS compliance mode" },
            new() { Name = "encryption.stats", DisplayName = "Statistics", Description = "Get encryption statistics" },
            new() { Name = "encryption.validate-fips", DisplayName = "Validate FIPS", Description = "Validate FIPS compliance of a strategy" },
            new() { Name = "encryption.cascade", DisplayName = "Cascade", Description = "Encrypt with multiple algorithms" },
            new() { Name = "encryption.reencrypt", DisplayName = "Re-encrypt", Description = "Re-encrypt data with a different algorithm" },
            new() { Name = "encryption.generate-key", DisplayName = "Generate Key", Description = "Generate encryption key" }
        ];
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<SDK.Contracts.RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            var capabilities = new List<SDK.Contracts.RegisteredCapability>();

            // Add base encryption capability
            capabilities.Add(new SDK.Contracts.RegisteredCapability
            {
                CapabilityId = $"{Id}.encrypt",
                DisplayName = $"{Name} - Encrypt",
                Description = "Encrypt data",
                Category = SDK.Contracts.CapabilityCategory.Encryption,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "encryption", "security", "crypto" }
            });

            capabilities.Add(new SDK.Contracts.RegisteredCapability
            {
                CapabilityId = $"{Id}.decrypt",
                DisplayName = $"{Name} - Decrypt",
                Description = "Decrypt data",
                Category = SDK.Contracts.CapabilityCategory.Encryption,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "encryption", "security", "crypto" }
            });

            // Auto-generate capabilities from strategy registry
            foreach (var strategy in _registry.GetAllStrategies())
            {
                var tags = new List<string> { "encryption", "strategy" };
                tags.Add(strategy.CipherInfo.SecurityLevel.ToString().ToLowerInvariant());
                if (strategy.CipherInfo.Capabilities.IsAuthenticated) tags.Add("aead");
                if (strategy.CipherInfo.Capabilities.IsStreamable) tags.Add("streaming");
                if (strategy.CipherInfo.Capabilities.IsHardwareAcceleratable) tags.Add("hardware-accelerated");

                capabilities.Add(new SDK.Contracts.RegisteredCapability
                {
                    CapabilityId = $"{Id}.strategy.{strategy.StrategyId}",
                    DisplayName = strategy.StrategyName,
                    Description = $"{strategy.CipherInfo.AlgorithmName} ({strategy.CipherInfo.KeySizeBits}-bit)",
                    Category = SDK.Contracts.CapabilityCategory.Encryption,
                    SubCategory = strategy.CipherInfo.SecurityLevel.ToString(),
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = tags.ToArray(),
                    Priority = (int)strategy.CipherInfo.SecurityLevel * 10,
                    Metadata = new Dictionary<string, object>
                    {
                        ["algorithm"] = strategy.CipherInfo.AlgorithmName,
                        ["keySizeBits"] = strategy.CipherInfo.KeySizeBits,
                        ["isAuthenticated"] = strategy.CipherInfo.Capabilities.IsAuthenticated,
                        ["securityLevel"] = strategy.CipherInfo.SecurityLevel.ToString()
                    },
                    SemanticDescription = $"Encrypt using {strategy.CipherInfo.AlgorithmName} with {strategy.CipherInfo.KeySizeBits}-bit key"
                });
            }

            return capabilities;
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<SDK.AI.KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<SDK.AI.KnowledgeObject>(base.GetStaticKnowledge());

        var strategies = _registry.GetAllStrategies();
        knowledge.Add(new SDK.AI.KnowledgeObject
        {
            Id = $"{Id}.strategies",
            Topic = "encryption.strategies",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = $"{strategies.Count} encryption strategies available",
            Payload = new Dictionary<string, object>
            {
                ["count"] = strategies.Count,
                ["algorithms"] = strategies.Select(s => s.CipherInfo.AlgorithmName).Distinct().ToArray(),
                ["aeadCount"] = strategies.Count(s => s.CipherInfo.Capabilities.IsAuthenticated),
                ["hardwareAccelerated"] = strategies.Count(s => s.CipherInfo.Capabilities.IsHardwareAcceleratable)
            },
            Tags = new[] { "encryption", "strategies", "summary" }
        });

        return knowledge;
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _registry.GetAllStrategies().Count;
        metadata["AesStrategies"] = GetStrategiesByPrefix("aes").Count;
        metadata["ChaChaStrategies"] = GetStrategiesByPrefix("chacha").Count;
        metadata["PostQuantumStrategies"] = GetStrategiesByPrefix("ml-").Count + GetStrategiesByPrefix("slh-").Count;
        metadata["FipsCompliantStrategies"] = _registry.GetFipsCompliantStrategies().Count;
        metadata["HardwareAcceleration"] = _aesNiAvailable ? "AES-NI" : "None";
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "encryption.encrypt" => HandleEncryptAsync(message),
            "encryption.decrypt" => HandleDecryptAsync(message),
            "encryption.list-strategies" => HandleListStrategiesAsync(message),
            "encryption.set-default" => HandleSetDefaultAsync(message),
            "encryption.set-fips" => HandleSetFipsAsync(message),
            "encryption.stats" => HandleStatsAsync(message),
            "encryption.validate-fips" => HandleValidateFipsAsync(message),
            "encryption.cascade" => HandleCascadeEncryptAsync(message),
            "encryption.reencrypt" => HandleReencryptAsync(message),
            "encryption.generate-key" => HandleGenerateKeyAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    /// <inheritdoc/>
    public override async Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Get strategy
        var strategyId = args.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = GetStrategyOrThrow(strategyId);

        // Read input
        using var inputMs = new MemoryStream();
        await input.CopyToAsync(inputMs);
        var plaintext = inputMs.ToArray();

        // Get key
        byte[] key;
        string? keyId = null;

        if (args.TryGetValue("key", out var keyObj) && keyObj is byte[] providedKey)
        {
            key = providedKey;
        }
        else if (args.TryGetValue("keyId", out var kidObj) && kidObj is string kid)
        {
            keyId = kid;
            // Key store integration - get key from key store
            key = await GetKeyFromKeyStoreAsync(kid, context, args);
        }
        else
        {
            // Generate a new key
            key = strategy.GenerateKey();
            keyId = Guid.NewGuid().ToString("N");
        }

        // Encrypt
        var aad = args.TryGetValue("associatedData", out var aadObj) && aadObj is byte[] associatedData
            ? associatedData : null;

        var ciphertext = await strategy.EncryptAsync(plaintext, key, aad);

        // Create payload
        var payload = new EncryptedPayload
        {
            AlgorithmId = strategy.StrategyId,
            Nonce = Array.Empty<byte>(), // Included in ciphertext
            Ciphertext = ciphertext,
            KeyId = keyId
        };

        // Update stats
        Interlocked.Increment(ref _totalEncryptions);
        Interlocked.Add(ref _totalBytesEncrypted, plaintext.Length);
        IncrementUsageStats(strategyId);

        if (_auditEnabled)
        {
            context.LogDebug($"Encrypted {plaintext.Length} bytes using {strategyId}");
        }

        // Clear sensitive data
        CryptographicOperations.ZeroMemory(plaintext);

        return new MemoryStream(payload.ToBytes());
    }

    /// <inheritdoc/>
    public override async Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Read payload
        using var inputMs = new MemoryStream();
        await stored.CopyToAsync(inputMs);
        var payloadBytes = inputMs.ToArray();

        var payload = EncryptedPayload.FromBytes(payloadBytes);

        // Get strategy
        var strategy = GetStrategyOrThrow(payload.AlgorithmId);

        // Get key
        byte[] key;

        if (args.TryGetValue("key", out var keyObj) && keyObj is byte[] providedKey)
        {
            key = providedKey;
        }
        else if (!string.IsNullOrEmpty(payload.KeyId))
        {
            key = await GetKeyFromKeyStoreAsync(payload.KeyId, context, args);
        }
        else
        {
            throw new CryptographicException("No decryption key provided and no key ID in payload");
        }

        // Decrypt
        var aad = args.TryGetValue("associatedData", out var aadObj) && aadObj is byte[] associatedData
            ? associatedData : null;

        var plaintext = await strategy.DecryptAsync(payload.Ciphertext, key, aad);

        // Update stats
        Interlocked.Increment(ref _totalDecryptions);
        Interlocked.Add(ref _totalBytesDecrypted, plaintext.Length);

        if (_auditEnabled)
        {
            context.LogDebug($"Decrypted {plaintext.Length} bytes using {payload.AlgorithmId}");
        }

        return new MemoryStream(plaintext);
    }

    #region Message Handlers

    private Task HandleEncryptAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
        {
            throw new ArgumentException("Missing or invalid 'data' parameter");
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = GetStrategyOrThrow(strategyId);

        byte[] key;
        if (message.Payload.TryGetValue("key", out var keyObj) && keyObj is byte[] providedKey)
        {
            key = providedKey;
        }
        else
        {
            key = strategy.GenerateKey();
            message.Payload["generatedKey"] = key;
        }

        var aad = message.Payload.TryGetValue("associatedData", out var aadObj) && aadObj is byte[] associatedData
            ? associatedData : null;

        var ciphertext = strategy.EncryptAsync(data, key, aad).GetAwaiter().GetResult();

        message.Payload["result"] = ciphertext;
        message.Payload["strategyId"] = strategyId;

        Interlocked.Increment(ref _totalEncryptions);
        Interlocked.Add(ref _totalBytesEncrypted, data.Length);
        IncrementUsageStats(strategyId);

        return Task.CompletedTask;
    }

    private Task HandleDecryptAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
        {
            throw new ArgumentException("Missing or invalid 'data' parameter");
        }

        if (!message.Payload.TryGetValue("key", out var keyObj) || keyObj is not byte[] key)
        {
            throw new ArgumentException("Missing or invalid 'key' parameter");
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = GetStrategyOrThrow(strategyId);

        var aad = message.Payload.TryGetValue("associatedData", out var aadObj) && aadObj is byte[] associatedData
            ? associatedData : null;

        var plaintext = strategy.DecryptAsync(data, key, aad).GetAwaiter().GetResult();

        message.Payload["result"] = plaintext;

        Interlocked.Increment(ref _totalDecryptions);
        Interlocked.Add(ref _totalBytesDecrypted, plaintext.Length);

        return Task.CompletedTask;
    }

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var strategies = _fipsMode
            ? _registry.GetFipsCompliantStrategies()
            : _registry.GetAllStrategies();

        var strategyList = strategies.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["name"] = s.StrategyName,
            ["algorithm"] = s.CipherInfo.AlgorithmName,
            ["keySizeBits"] = s.CipherInfo.KeySizeBits,
            ["securityLevel"] = s.CipherInfo.SecurityLevel.ToString(),
            ["isAuthenticated"] = s.CipherInfo.Capabilities.IsAuthenticated,
            ["isStreamable"] = s.CipherInfo.Capabilities.IsStreamable,
            ["supportsHardwareAcceleration"] = s.CipherInfo.Capabilities.IsHardwareAcceleratable
        }).ToList();

        message.Payload["strategies"] = strategyList;
        message.Payload["count"] = strategyList.Count;
        message.Payload["fipsMode"] = _fipsMode;

        return Task.CompletedTask;
    }

    private Task HandleSetDefaultAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"Strategy '{strategyId}' not found");

        if (_fipsMode)
        {
            var fipsResult = FipsComplianceValidator.Validate(strategy.CipherInfo);
            if (!fipsResult.IsCompliant)
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategyId}' is not FIPS compliant: {string.Join(", ", fipsResult.Violations)}");
            }
        }

        _defaultStrategyId = strategyId;
        message.Payload["success"] = true;
        message.Payload["defaultStrategy"] = strategyId;

        return Task.CompletedTask;
    }

    private Task HandleSetFipsAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("enabled", out var enabledObj))
        {
            throw new ArgumentException("Missing 'enabled' parameter");
        }

        var enabled = enabledObj is bool b ? b : bool.Parse(enabledObj.ToString()!);
        _fipsMode = enabled;

        if (enabled)
        {
            // Verify default strategy is FIPS compliant
            var defaultStrategy = _registry.GetStrategy(_defaultStrategyId);
            if (defaultStrategy != null)
            {
                var fipsResult = FipsComplianceValidator.Validate(defaultStrategy.CipherInfo);
                if (!fipsResult.IsCompliant)
                {
                    _defaultStrategyId = "aes-256-gcm"; // Fall back to FIPS-compliant default
                }
            }
        }

        message.Payload["fipsMode"] = _fipsMode;
        message.Payload["defaultStrategy"] = _defaultStrategyId;

        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        message.Payload["totalEncryptions"] = Interlocked.Read(ref _totalEncryptions);
        message.Payload["totalDecryptions"] = Interlocked.Read(ref _totalDecryptions);
        message.Payload["totalBytesEncrypted"] = Interlocked.Read(ref _totalBytesEncrypted);
        message.Payload["totalBytesDecrypted"] = Interlocked.Read(ref _totalBytesDecrypted);
        message.Payload["registeredStrategies"] = _registry.GetAllStrategies().Count;
        message.Payload["fipsMode"] = _fipsMode;
        message.Payload["aesNiAvailable"] = _aesNiAvailable;
        message.Payload["avx2Available"] = _avx2Available;

        var usageByStrategy = new Dictionary<string, long>(_usageStats);
        message.Payload["usageByStrategy"] = usageByStrategy;

        return Task.CompletedTask;
    }

    private Task HandleValidateFipsAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"Strategy '{strategyId}' not found");

        var result = FipsComplianceValidator.Validate(strategy.CipherInfo);

        message.Payload["isCompliant"] = result.IsCompliant;
        message.Payload["algorithmName"] = result.AlgorithmName;
        message.Payload["fipsVersion"] = result.FipsVersion;
        message.Payload["violations"] = result.Violations;

        return Task.CompletedTask;
    }

    private async Task HandleCascadeEncryptAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
        {
            throw new ArgumentException("Missing or invalid 'data' parameter");
        }

        if (!message.Payload.TryGetValue("strategyIds", out var sidsObj) || sidsObj is not IEnumerable<string> strategyIds)
        {
            throw new ArgumentException("Missing or invalid 'strategyIds' parameter");
        }

        var ids = strategyIds.ToList();
        if (ids.Count < 2)
        {
            throw new ArgumentException("Cascade encryption requires at least 2 strategies");
        }

        var currentData = data;
        var keys = new List<byte[]>();

        foreach (var strategyId in ids)
        {
            var strategy = GetStrategyOrThrow(strategyId);
            var key = strategy.GenerateKey();
            keys.Add(key);

            currentData = await strategy.EncryptAsync(currentData, key);

            Interlocked.Increment(ref _totalEncryptions);
            IncrementUsageStats(strategyId);
        }

        message.Payload["result"] = currentData;
        message.Payload["keys"] = keys;
        message.Payload["strategyIds"] = ids;

        Interlocked.Add(ref _totalBytesEncrypted, data.Length);
    }

    private async Task HandleReencryptAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
        {
            throw new ArgumentException("Missing or invalid 'data' parameter");
        }

        if (!message.Payload.TryGetValue("oldKey", out var oldKeyObj) || oldKeyObj is not byte[] oldKey)
        {
            throw new ArgumentException("Missing or invalid 'oldKey' parameter");
        }

        if (!message.Payload.TryGetValue("oldStrategyId", out var oldSidObj) || oldSidObj is not string oldStrategyId)
        {
            throw new ArgumentException("Missing or invalid 'oldStrategyId' parameter");
        }

        var newStrategyId = message.Payload.TryGetValue("newStrategyId", out var newSidObj) && newSidObj is string nsid
            ? nsid : _defaultStrategyId;

        // Decrypt with old strategy
        var oldStrategy = GetStrategyOrThrow(oldStrategyId);
        var plaintext = await oldStrategy.DecryptAsync(data, oldKey);

        // Encrypt with new strategy
        var newStrategy = GetStrategyOrThrow(newStrategyId);
        var newKey = message.Payload.TryGetValue("newKey", out var newKeyObj) && newKeyObj is byte[] nk
            ? nk : newStrategy.GenerateKey();

        var newCiphertext = await newStrategy.EncryptAsync(plaintext, newKey);

        // Clear sensitive data
        CryptographicOperations.ZeroMemory(plaintext);

        message.Payload["result"] = newCiphertext;
        message.Payload["newKey"] = newKey;
        message.Payload["newStrategyId"] = newStrategyId;

        Interlocked.Increment(ref _totalDecryptions);
        Interlocked.Increment(ref _totalEncryptions);
    }

    private Task HandleGenerateKeyAsync(PluginMessage message)
    {
        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = GetStrategyOrThrow(strategyId);
        var key = strategy.GenerateKey();

        message.Payload["key"] = key;
        message.Payload["keySizeBits"] = strategy.CipherInfo.KeySizeBits;
        message.Payload["strategyId"] = strategyId;

        return Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    private IEncryptionStrategy GetStrategyOrThrow(string strategyId)
    {
        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"Encryption strategy '{strategyId}' not found");

        if (_fipsMode)
        {
            var fipsResult = FipsComplianceValidator.Validate(strategy.CipherInfo);
            if (!fipsResult.IsCompliant)
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategyId}' is not FIPS compliant and FIPS mode is enabled");
            }
        }

        return strategy;
    }

    private List<IEncryptionStrategy> GetStrategiesByPrefix(string prefix)
    {
        return _registry.GetAllStrategies()
            .Where(s => s.StrategyId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void IncrementUsageStats(string strategyId)
    {
        _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
    }

    private void DiscoverAndRegisterStrategies()
    {
        // Auto-discover strategies in this assembly
        _registry.DiscoverStrategies(Assembly.GetExecutingAssembly());
    }

    private async Task<byte[]> GetKeyFromKeyStoreAsync(string keyId, IKernelContext context, Dictionary<string, object> args)
    {
        // Try to get key store from context
        var keyStore = context.GetPlugins<IPlugin>()
            .OfType<IKeyStore>()
            .FirstOrDefault();

        if (keyStore == null)
        {
            throw new InvalidOperationException("No key store available. Provide 'key' directly or configure a key store.");
        }

        // Get security context
        ISecurityContext securityContext;
        if (args.TryGetValue("securityContext", out var scObj) && scObj is ISecurityContext sc)
        {
            securityContext = sc;
        }
        else
        {
            securityContext = new DefaultSecurityContext();
        }

        return await keyStore.GetKeyAsync(keyId, securityContext);
    }

    #endregion

    #region Knowledge & Capability Registry Integration

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetConfigurationState()
    {
        return new Dictionary<string, object>
        {
            ["fipsMode"] = _fipsMode,
            ["auditEnabled"] = _auditEnabled,
            ["defaultStrategy"] = _defaultStrategyId,
            ["aesNiAvailable"] = _aesNiAvailable,
            ["avx2Available"] = _avx2Available,
            ["registeredStrategies"] = _registry.GetAllStrategies().Count
        };
    }

    /// <inheritdoc/>
    protected override KnowledgeObject? BuildStatisticsKnowledge()
    {
        return new KnowledgeObject
        {
            Id = $"{Id}.statistics.{Guid.NewGuid():N}",
            Topic = "plugin.statistics",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "metric",
            Description = $"Usage statistics for {Name}",
            Payload = new Dictionary<string, object>
            {
                ["totalEncryptions"] = Interlocked.Read(ref _totalEncryptions),
                ["totalDecryptions"] = Interlocked.Read(ref _totalDecryptions),
                ["totalBytesEncrypted"] = Interlocked.Read(ref _totalBytesEncrypted),
                ["totalBytesDecrypted"] = Interlocked.Read(ref _totalBytesDecrypted),
                ["registeredStrategies"] = _registry.GetAllStrategies().Count,
                ["fipsCompliantStrategies"] = _registry.GetFipsCompliantStrategies().Count,
                ["usageByStrategy"] = new Dictionary<string, long>(_usageStats)
            },
            Tags = new[] { "statistics", "encryption", "usage" }
        };
    }

    /// <inheritdoc/>
    protected override string[] GetCapabilityTags(PluginCapabilityDescriptor capability)
    {
        var tags = base.GetCapabilityTags(capability).ToList();
        tags.Add("encryption");
        if (_fipsMode) tags.Add("fips-mode-active");
        if (_aesNiAvailable) tags.Add("hardware-accelerated");
        return tags.ToArray();
    }

    #endregion

    #region Intelligence Integration

    /// <summary>
    /// Called when Intelligence becomes available - register encryption capabilities.
    /// </summary>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        // Register encryption capabilities with Intelligence
        if (MessageBus != null)
        {
            var strategies = _registry.GetAllStrategies();
            var algorithms = strategies.Select(s => s.CipherInfo.AlgorithmName).Distinct().ToArray();
            var aeadCount = strategies.Count(s => s.CipherInfo.Capabilities.IsAuthenticated);

            await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
            {
                Type = "capability.register",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["pluginName"] = Name,
                    ["pluginType"] = "encryption",
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["strategyCount"] = strategies.Count,
                        ["algorithms"] = algorithms,
                        ["aeadCount"] = aeadCount,
                        ["fipsCompliantCount"] = _registry.GetFipsCompliantStrategies().Count,
                        ["postQuantumCount"] = GetStrategiesByPrefix("ml-").Count + GetStrategiesByPrefix("slh-").Count,
                        ["hardwareAccelerated"] = _aesNiAvailable,
                        ["supportsCipherRecommendation"] = true,
                        ["supportsThreatAssessment"] = true
                    },
                    ["semanticDescription"] = SemanticDescription,
                    ["tags"] = SemanticTags
                }
            }, ct);

            // Subscribe to cipher recommendation requests
            SubscribeToCipherRecommendationRequests();
        }
    }

    /// <summary>
    /// Subscribes to Intelligence cipher recommendation requests.
    /// </summary>
    private void SubscribeToCipherRecommendationRequests()
    {
        if (MessageBus == null) return;

        MessageBus.Subscribe(IntelligenceTopics.RequestCipherRecommendation, async msg =>
        {
            if (msg.Payload.TryGetValue("contentType", out var ctObj) && ctObj is string contentType &&
                msg.Payload.TryGetValue("contentSize", out var csObj) && csObj is long contentSize)
            {
                var recommendation = RecommendCipherStrategy(contentType, contentSize, msg.Payload);

                await MessageBus.PublishAsync(IntelligenceTopics.RequestCipherRecommendationResponse, new PluginMessage
                {
                    Type = "cipher-recommendation.response",
                    CorrelationId = msg.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["algorithm"] = recommendation.Algorithm,
                        ["keySize"] = recommendation.KeySize,
                        ["mode"] = recommendation.Mode,
                        ["reasoning"] = recommendation.Reasoning,
                        ["confidence"] = recommendation.Confidence,
                        ["performanceImpact"] = recommendation.PerformanceImpact
                    }
                });
            }
        });
    }

    /// <summary>
    /// Recommends a cipher strategy based on content characteristics.
    /// </summary>
    private (string Algorithm, int KeySize, string Mode, string Reasoning, double Confidence, string PerformanceImpact)
        RecommendCipherStrategy(string contentType, long contentSize, Dictionary<string, object> context)
    {
        // Check FIPS requirements
        var requiresFips = context.TryGetValue("requiresFips", out var fipsObj) && fipsObj is true;
        var securityLevel = context.TryGetValue("securityLevel", out var slObj) && slObj is string sl ? sl : "Standard";

        // Post-quantum for high security
        if (securityLevel == "PostQuantum" || securityLevel == "Maximum")
        {
            return ("ML-KEM-1024", 256, "Hybrid-AES-GCM",
                "Post-quantum hybrid encryption recommended for maximum security against quantum threats",
                0.95, "High");
        }

        // Large files: prefer streaming-capable ciphers
        if (contentSize > 100 * 1024 * 1024) // > 100MB
        {
            return ("AES-256-GCM", 256, "GCM",
                "AES-256-GCM recommended for large files due to streaming capability and hardware acceleration",
                0.90, "Low");
        }

        // FIPS compliance
        if (requiresFips || _fipsMode)
        {
            return ("AES-256-GCM", 256, "GCM",
                "AES-256-GCM is FIPS 140-2/3 compliant and provides authenticated encryption",
                0.95, "Low");
        }

        // Sensitive content types
        if (contentType.Contains("medical") || contentType.Contains("health"))
        {
            return ("AES-256-GCM", 256, "GCM",
                "AES-256-GCM recommended for healthcare data - HIPAA compliant",
                0.92, "Low");
        }

        if (contentType.Contains("financial") || contentType.Contains("payment"))
        {
            return ("AES-256-GCM", 256, "GCM",
                "AES-256-GCM recommended for financial data - PCI-DSS compliant",
                0.92, "Low");
        }

        // Default recommendation
        return ("AES-256-GCM", 256, "GCM",
            "AES-256-GCM provides strong authenticated encryption with hardware acceleration",
            0.88, "Low");
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    #endregion

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _usageStats.Clear();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Default security context for internal operations.
    /// </summary>
    private sealed class DefaultSecurityContext : ISecurityContext
    {
        public string UserId => Environment.UserName;
        public string? TenantId => "default";
        public IEnumerable<string> Roles => ["user"];
        public bool IsSystemAdmin => false;
    }
}
