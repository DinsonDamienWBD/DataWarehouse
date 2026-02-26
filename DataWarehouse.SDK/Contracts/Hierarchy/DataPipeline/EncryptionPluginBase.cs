using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Security;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for encryption pipeline plugins. Provides key management infrastructure,
/// envelope encryption, DEK generation, statistics tracking, and audit logging.
/// All encryption plugins in the v3.0 hierarchy MUST extend this class.
/// </summary>
public abstract class EncryptionPluginBase : DataTransformationPluginBase
{
    /// <inheritdoc/>
    public override string SubCategory => "Encryption";

    #region Algorithm Properties

    /// <summary>Key size in bytes (e.g., 32 for AES-256).</summary>
    public abstract int KeySizeBytes { get; }

    /// <summary>IV/nonce size in bytes.</summary>
    public abstract int IvSizeBytes { get; }

    /// <summary>Authentication tag size in bytes (0 if non-AEAD).</summary>
    public virtual int TagSizeBytes => 0;

    /// <summary>Algorithm identifier (e.g., "AES-256-GCM").</summary>
    public abstract string AlgorithmId { get; }

    #endregion

    #region AI Hooks

    /// <inheritdoc/>
    /// <remarks>Default returns <see cref="AlgorithmId"/> for encryption-specific selection.</remarks>
    protected override Task<string> SelectOptimalAlgorithmAsync(Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(AlgorithmId);

    /// <summary>AI hook: Evaluate key strength.</summary>
    protected virtual Task<int> EvaluateKeyStrengthAsync(byte[] keyMaterial, CancellationToken ct = default)
        => Task.FromResult(100);

    /// <summary>AI hook: Detect encryption anomalies.</summary>
    protected virtual Task<bool> DetectEncryptionAnomalyAsync(Dictionary<string, object> operationContext, CancellationToken ct = default)
        => Task.FromResult(false);

    #endregion

    #region Typed Encryption Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="IEncryptionStrategy"/> instances.
    /// IEncryptionStrategy does not inherit IStrategy, so it uses its own dedicated registry
    /// rather than the PluginBase IStrategy registry.
    /// </summary>
    private StrategyRegistry<IEncryptionStrategy>? _encryptionStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_encryptionStrategyRegistry"/>.</summary>
    private readonly object _encryptionRegistryLock = new();

    /// <summary>
    /// Gets the typed encryption strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<IEncryptionStrategy> EncryptionStrategyRegistry
    {
        get
        {
            if (_encryptionStrategyRegistry is not null) return _encryptionStrategyRegistry;
            lock (_encryptionRegistryLock)
            {
                _encryptionStrategyRegistry ??= new StrategyRegistry<IEncryptionStrategy>(s => s.StrategyId);
            }
            return _encryptionStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers an encryption strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The encryption strategy to register.</param>
    protected void RegisterEncryptionStrategy(IEncryptionStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        EncryptionStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the optimal encryption strategy, using AI-driven algorithm
    /// selection when no explicit strategy is specified. Routes through the typed
    /// <see cref="EncryptionStrategyRegistry"/> with CommandIdentity ACL enforcement.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use AI selection via SelectOptimalAlgorithmAsync).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="dataContext">Context about the data for AI algorithm selection.</param>
    /// <param name="operation">The operation to execute on the resolved encryption strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchEncryptionStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<IEncryptionStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        string strategyId;
        if (!string.IsNullOrEmpty(explicitStrategyId))
        {
            strategyId = explicitStrategyId;
        }
        else
        {
            // Delegate to AI hook -- SelectOptimalAlgorithmAsync is active code here
            strategyId = await SelectOptimalAlgorithmAsync(
                dataContext ?? new Dictionary<string, object>(), ct).ConfigureAwait(false);
        }

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use encryption strategy '{strategyId}'.");
        }

        var strategy = EncryptionStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Encryption strategy '{strategyId}' is not registered. " +
                $"Call RegisterEncryptionStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Encrypts data using the specified or default encryption strategy.
    /// Resolves the strategy from the typed <see cref="EncryptionStrategyRegistry"/> with
    /// optional CommandIdentity ACL, falls back to <see cref="SelectOptimalAlgorithmAsync"/>
    /// for AI-driven algorithm selection when no strategy is specified.
    /// </summary>
    /// <param name="plaintext">The data to encrypt.</param>
    /// <param name="key">The encryption key.</param>
    /// <param name="strategyId">Optional strategy ID. When null, AI selection applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="associatedData">Optional associated data for AEAD ciphers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Encrypted ciphertext.</returns>
    protected async Task<byte[]> EncryptAsync(
        byte[] plaintext,
        byte[] key,
        string? strategyId = null,
        CommandIdentity? identity = null,
        byte[]? associatedData = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        return await DispatchEncryptionStrategyAsync<byte[]>(
            strategyId,
            identity,
            new Dictionary<string, object>
            {
                ["operation"] = "encrypt",
                ["dataSize"] = plaintext.Length,
                ["hasAssociatedData"] = associatedData != null
            },
            async strategy =>
            {
                var result = await strategy.EncryptAsync(plaintext, key, associatedData, ct)
                    .ConfigureAwait(false);
                UpdateEncryptionStats(plaintext.Length);
                return result;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Decrypts data using the specified or default encryption strategy.
    /// Resolves the strategy from the typed <see cref="EncryptionStrategyRegistry"/> with
    /// optional CommandIdentity ACL.
    /// </summary>
    /// <param name="ciphertext">The encrypted data to decrypt.</param>
    /// <param name="key">The decryption key.</param>
    /// <param name="strategyId">Optional strategy ID. When null, AI selection applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="associatedData">Optional associated data for AEAD verification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Decrypted plaintext.</returns>
    protected async Task<byte[]> DecryptAsync(
        byte[] ciphertext,
        byte[] key,
        string? strategyId = null,
        CommandIdentity? identity = null,
        byte[]? associatedData = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(key);

        return await DispatchEncryptionStrategyAsync<byte[]>(
            strategyId,
            identity,
            new Dictionary<string, object>
            {
                ["operation"] = "decrypt",
                ["dataSize"] = ciphertext.Length
            },
            async strategy =>
            {
                var result = await strategy.DecryptAsync(ciphertext, key, associatedData, ct)
                    .ConfigureAwait(false);
                UpdateDecryptionStats(result.Length);
                return result;
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="AlgorithmId"/> to route encryption-specific dispatches to the correct algorithm.</remarks>
    protected override string? GetDefaultStrategyId() => AlgorithmId;

    #endregion

    #region Key Store Registry

    /// <summary>
    /// Default key store (fallback when no user preference or explicit override).
    /// </summary>
    protected IKeyStore? DefaultKeyStore;

    /// <summary>
    /// Default key management mode.
    /// </summary>
    protected KeyManagementMode DefaultKeyManagementMode = KeyManagementMode.Direct;

    /// <summary>
    /// Default envelope key store for Envelope mode.
    /// </summary>
    protected IEnvelopeKeyStore? DefaultEnvelopeKeyStore;

    /// <summary>
    /// Default KEK key ID for Envelope mode.
    /// </summary>
    protected string? DefaultKekKeyId;

    /// <summary>
    /// Per-user configuration provider (optional, for multi-tenant).
    /// </summary>
    protected IKeyManagementConfigProvider? ConfigProvider;

    /// <summary>
    /// Key store registry for resolving plugin IDs.
    /// </summary>
    protected IKeyStoreRegistry? KeyStoreRegistry;

    /// <summary>
    /// Registers a key store for use by this encryption plugin.
    /// </summary>
    /// <param name="name">Logical name for this key store.</param>
    /// <param name="store">The key store instance.</param>
    protected virtual void RegisterKeyStore(string name, IKeyStore store)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(store);
        KeyStoreRegistry?.Register(name, store);
    }

    /// <summary>
    /// Gets a key store by name from the registry.
    /// </summary>
    /// <param name="name">The key store name.</param>
    /// <returns>The key store instance, or null if not found.</returns>
    protected virtual IKeyStore? GetKeyStore(string name)
    {
        return KeyStoreRegistry?.GetKeyStore(name);
    }

    #endregion

    #region Encryption Statistics

    /// <summary>
    /// Lock for thread-safe statistics updates.
    /// </summary>
    protected readonly object StatsLock = new();

    /// <summary>Total encryption operations performed.</summary>
    protected long EncryptionCount;

    /// <summary>Total decryption operations performed.</summary>
    protected long DecryptionCount;

    /// <summary>Total bytes encrypted.</summary>
    protected long TotalBytesEncrypted;

    /// <summary>Total bytes decrypted.</summary>
    protected long TotalBytesDecrypted;

    /// <summary>Total key rotations performed.</summary>
    protected long KeyRotationCount;

    /// <summary>
    /// Key access audit log (tracks when each key was last used).
    /// Bounded to <see cref="MaxKeyAccessLogSize"/> entries.
    /// </summary>
    protected readonly BoundedDictionary<string, DateTime> KeyAccessLog = new BoundedDictionary<string, DateTime>(1000);

    /// <summary>
    /// Maximum number of entries in the key access log.
    /// Override in derived classes to customize. Default: 10,000.
    /// </summary>
    protected virtual int MaxKeyAccessLogSize => 10_000;

    /// <summary>
    /// Records a key access event in the bounded key access log.
    /// Uses oldest-first eviction when the log is full.
    /// </summary>
    protected void RecordKeyAccess(string keyId)
    {
        if (KeyAccessLog.Count >= MaxKeyAccessLogSize && !KeyAccessLog.ContainsKey(keyId))
        {
            var oldest = KeyAccessLog.OrderBy(kvp => kvp.Value).FirstOrDefault();
            if (oldest.Key != null)
            {
                KeyAccessLog.TryRemove(oldest.Key, out _);
            }
        }
        KeyAccessLog[keyId] = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates encryption statistics after a successful encryption operation.
    /// Thread-safe via <see cref="StatsLock"/>.
    /// </summary>
    protected virtual void UpdateEncryptionStats(long bytesProcessed)
    {
        lock (StatsLock)
        {
            EncryptionCount++;
            TotalBytesEncrypted += bytesProcessed;
        }
    }

    /// <summary>
    /// Updates decryption statistics after a successful decryption operation.
    /// Thread-safe via <see cref="StatsLock"/>.
    /// </summary>
    protected virtual void UpdateDecryptionStats(long bytesProcessed)
    {
        lock (StatsLock)
        {
            DecryptionCount++;
            TotalBytesDecrypted += bytesProcessed;
        }
    }

    /// <summary>
    /// Gets a snapshot of encryption statistics including operation counts,
    /// byte totals, unique keys used, and last key access time.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current encryption statistics.</returns>
    protected virtual Task<EncryptionStatistics> GetEncryptionStatisticsAsync(CancellationToken ct = default)
    {
        lock (StatsLock)
        {
            return Task.FromResult(new EncryptionStatistics
            {
                EncryptionCount = EncryptionCount,
                DecryptionCount = DecryptionCount,
                TotalBytesEncrypted = TotalBytesEncrypted,
                TotalBytesDecrypted = TotalBytesDecrypted,
                UniqueKeysUsed = KeyAccessLog.Count,
                LastKeyAccess = KeyAccessLog.Values.DefaultIfEmpty().Max()
            });
        }
    }

    #endregion

    #region DEK Generation

    /// <summary>
    /// Generates a cryptographically secure Data Encryption Key (DEK) for the configured algorithm.
    /// Uses <see cref="RandomNumberGenerator"/> for CSPRNG-based key generation.
    /// </summary>
    /// <param name="keySizeBytes">Key size in bytes. Defaults to <see cref="KeySizeBytes"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new cryptographically random key of the specified size.</returns>
    protected virtual Task<byte[]> GenerateDataEncryptionKeyAsync(int? keySizeBytes = null, CancellationToken ct = default)
    {
        var size = keySizeBytes ?? KeySizeBytes;
        var dek = new byte[size];
        RandomNumberGenerator.Fill(dek);
        return Task.FromResult(dek);
    }

    /// <summary>
    /// Generates a random IV/nonce for this encryption algorithm.
    /// Override if algorithm requires specific IV generation logic.
    /// </summary>
    protected virtual byte[] GenerateIv()
    {
        var iv = new byte[IvSizeBytes];
        RandomNumberGenerator.Fill(iv);
        return iv;
    }

    #endregion

    #region Envelope Encryption

    /// <summary>
    /// Encrypts data using envelope encryption: generates a random DEK, encrypts data with DEK,
    /// wraps DEK with KEK via the envelope key store.
    /// </summary>
    /// <param name="data">The plaintext data stream to encrypt.</param>
    /// <param name="kekKeyId">The Key Encryption Key identifier in the envelope key store.</param>
    /// <param name="securityContext">Security context for ACL validation.</param>
    /// <param name="metadata">Optional additional metadata for the envelope header.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The encrypted data stream with envelope header prepended.</returns>
    protected virtual async Task<Stream> EncryptWithEnvelopeAsync(
        Stream data,
        string kekKeyId,
        ISecurityContext securityContext,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        if (DefaultEnvelopeKeyStore == null)
            throw new InvalidOperationException("No envelope key store configured. Call SetDefaultEnvelopeKeyStore first.");

        // Generate random DEK
        var dek = await GenerateDataEncryptionKeyAsync(ct: ct).ConfigureAwait(false);

        try
        {
            // Wrap DEK with KEK via HSM
            var wrappedDek = await DefaultEnvelopeKeyStore.WrapKeyAsync(kekKeyId, dek, securityContext).ConfigureAwait(false);

            // Generate IV
            var iv = GenerateIv();

            // Create envelope header
            var envelope = new EnvelopeHeader
            {
                KekId = kekKeyId,
                KeyStorePluginId = "",
                WrappedDek = wrappedDek,
                Iv = iv,
                EncryptionAlgorithm = AlgorithmId,
                EncryptionPluginId = Id,
                EncryptedAtTicks = DateTime.UtcNow.Ticks,
                EncryptedBy = securityContext.UserId,
                Metadata = metadata ?? new Dictionary<string, string>()
            };

            // Encrypt data with DEK
            var encryptedData = await EncryptCoreAsync(data, dek, iv).ConfigureAwait(false);

            // Prepend envelope header to encrypted data
            var headerBytes = envelope.Serialize();
            var encryptedLength = encryptedData.CanSeek ? encryptedData.Length : 0;
            var result = new MemoryStream(headerBytes.Length + (int)Math.Min(encryptedLength, int.MaxValue));
            await result.WriteAsync(headerBytes, ct).ConfigureAwait(false);
            await encryptedData.CopyToAsync(result, ct).ConfigureAwait(false);
            result.Position = 0;

            // Log key access and update stats
            RecordKeyAccess($"envelope:{kekKeyId}");
            UpdateEncryptionStats(data.Length);
            await LogCryptoOperationAsync("EncryptWithEnvelope", AlgorithmId, "Success",
                new Dictionary<string, object> { ["kekKeyId"] = kekKeyId, ["bytesEncrypted"] = data.Length }, ct).ConfigureAwait(false);

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Decrypts data that was encrypted using envelope encryption.
    /// Reads the envelope header, unwraps DEK via KEK, and decrypts the payload.
    /// </summary>
    /// <param name="encryptedData">The encrypted data stream with envelope header.</param>
    /// <param name="securityContext">Security context for ACL validation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decrypted plaintext data stream.</returns>
    protected virtual async Task<Stream> DecryptWithEnvelopeAsync(
        Stream encryptedData,
        ISecurityContext securityContext,
        CancellationToken ct = default)
    {
        // Read and parse envelope header (pooled buffer to reduce GC pressure)
        var headerBuffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var bytesRead = await encryptedData.ReadAsync(headerBuffer, 0, 4096, ct).ConfigureAwait(false);
            encryptedData.Position = 0;

            if (!EnvelopeHeader.TryDeserialize(headerBuffer, out var envelope, out var headerLength) || envelope == null)
                throw new InvalidOperationException("Failed to parse envelope header from encrypted data.");

            // Skip header for decryption
            encryptedData.Position = headerLength;

            // Resolve envelope key store
            var envelopeKeyStore = DefaultEnvelopeKeyStore
                ?? (KeyStoreRegistry?.GetEnvelopeKeyStore(envelope.KeyStorePluginId))
                ?? throw new InvalidOperationException($"Cannot find envelope key store '{envelope.KeyStorePluginId}' to unwrap DEK.");

            // Unwrap DEK
            var dek = await envelopeKeyStore.UnwrapKeyAsync(envelope.KekId, envelope.WrappedDek, securityContext).ConfigureAwait(false);

            try
            {
                // Decrypt data with DEK
                var (decryptedData, _) = await DecryptCoreAsync(encryptedData, dek, envelope.Iv).ConfigureAwait(false);

                // Log key access and update stats
                RecordKeyAccess($"envelope:{envelope.KekId}");
                UpdateDecryptionStats(encryptedData.Length);
                await LogCryptoOperationAsync("DecryptWithEnvelope", AlgorithmId, "Success",
                    new Dictionary<string, object> { ["kekKeyId"] = envelope.KekId }, ct).ConfigureAwait(false);

                return decryptedData;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer, clearArray: true);
        }
    }

    #endregion

    #region Configuration Resolution

    /// <summary>
    /// Resolves key management configuration for this operation.
    /// Priority: 1. Explicit args, 2. User preferences, 3. Plugin defaults.
    /// </summary>
    protected virtual async Task<ResolvedKeyManagementConfig> ResolveConfigAsync(
        Dictionary<string, object> args,
        ISecurityContext context)
    {
        if (TryGetConfigFromArgs(args, out var argsConfig))
            return argsConfig;

        if (ConfigProvider != null)
        {
            var userConfig = await ConfigProvider.GetConfigAsync(context).ConfigureAwait(false);
            if (userConfig != null)
                return ResolveFromUserConfig(userConfig);
        }

        return new ResolvedKeyManagementConfig
        {
            Mode = DefaultKeyManagementMode,
            KeyStore = DefaultKeyStore,
            EnvelopeKeyStore = DefaultEnvelopeKeyStore,
            KekKeyId = DefaultKekKeyId
        };
    }

    /// <summary>
    /// Attempts to extract configuration from operation arguments.
    /// </summary>
    protected virtual bool TryGetConfigFromArgs(Dictionary<string, object> args, out ResolvedKeyManagementConfig config)
    {
        config = default!;

        if (!args.TryGetValue("keyManagementMode", out var modeObj))
            return false;

        var mode = modeObj switch
        {
            KeyManagementMode m => m,
            string s when Enum.TryParse<KeyManagementMode>(s, true, out var parsed) => parsed,
            _ => KeyManagementMode.Direct
        };

        IKeyStore? keyStore = null;
        IEnvelopeKeyStore? envelopeKeyStore = null;
        string? keyStorePluginId = null;
        string? envelopeKeyStorePluginId = null;
        string? kekKeyId = null;
        string? keyId = null;

        if (args.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
            keyStore = ks;
        else if (args.TryGetValue("keyStorePluginId", out var kspObj) && kspObj is string ksp)
        {
            keyStorePluginId = ksp;
            keyStore = KeyStoreRegistry?.GetKeyStore(ksp);
        }

        if (args.TryGetValue("envelopeKeyStore", out var eksObj) && eksObj is IEnvelopeKeyStore eks)
            envelopeKeyStore = eks;
        else if (args.TryGetValue("envelopeKeyStorePluginId", out var ekspObj) && ekspObj is string eksp)
        {
            envelopeKeyStorePluginId = eksp;
            envelopeKeyStore = KeyStoreRegistry?.GetEnvelopeKeyStore(eksp);
        }

        if (args.TryGetValue("kekKeyId", out var kekObj) && kekObj is string kek)
            kekKeyId = kek;

        if (args.TryGetValue("keyId", out var kidObj) && kidObj is string kid)
            keyId = kid;

        config = new ResolvedKeyManagementConfig
        {
            Mode = mode,
            KeyStore = keyStore ?? DefaultKeyStore,
            KeyId = keyId,
            EnvelopeKeyStore = envelopeKeyStore ?? DefaultEnvelopeKeyStore,
            KekKeyId = kekKeyId ?? DefaultKekKeyId,
            KeyStorePluginId = keyStorePluginId,
            EnvelopeKeyStorePluginId = envelopeKeyStorePluginId
        };

        return true;
    }

    /// <summary>
    /// Resolves configuration from user preferences.
    /// </summary>
    protected virtual ResolvedKeyManagementConfig ResolveFromUserConfig(KeyManagementConfig userConfig)
    {
        return new ResolvedKeyManagementConfig
        {
            Mode = userConfig.Mode,
            KeyStore = userConfig.KeyStore ?? KeyStoreRegistry?.GetKeyStore(userConfig.KeyStorePluginId),
            KeyId = userConfig.KeyId,
            EnvelopeKeyStore = userConfig.EnvelopeKeyStore ?? KeyStoreRegistry?.GetEnvelopeKeyStore(userConfig.EnvelopeKeyStorePluginId),
            KekKeyId = userConfig.KekKeyId,
            KeyStorePluginId = userConfig.KeyStorePluginId,
            EnvelopeKeyStorePluginId = userConfig.EnvelopeKeyStorePluginId
        };
    }

    #endregion

    #region Key Management

    /// <summary>
    /// Gets a key for encryption based on resolved configuration.
    /// For Direct mode: retrieves key from key store.
    /// For Envelope mode: generates random DEK, wraps with KEK.
    /// </summary>
    protected virtual async Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetKeyForEncryptionAsync(
        ResolvedKeyManagementConfig config,
        ISecurityContext context)
    {
        if (config.Mode == KeyManagementMode.Envelope)
            return await GetEnvelopeKeyForEncryptionAsync(config, context).ConfigureAwait(false);
        else
            return await GetDirectKeyForEncryptionAsync(config, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a key for Direct mode encryption.
    /// </summary>
    protected virtual async Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetDirectKeyForEncryptionAsync(
        ResolvedKeyManagementConfig config,
        ISecurityContext context)
    {
        if (config.KeyStore == null)
            throw new InvalidOperationException("No key store configured for Direct mode encryption.");

        var keyId = config.KeyId ?? await config.KeyStore.GetCurrentKeyIdAsync().ConfigureAwait(false);
        var key = await config.KeyStore.GetKeyAsync(keyId, context).ConfigureAwait(false);

        RecordKeyAccess(keyId);
        return (key, keyId, null);
    }

    /// <summary>
    /// Gets a key for Envelope mode encryption.
    /// Generates a random DEK and wraps it with the KEK.
    /// </summary>
    protected virtual async Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetEnvelopeKeyForEncryptionAsync(
        ResolvedKeyManagementConfig config,
        ISecurityContext context)
    {
        if (config.EnvelopeKeyStore == null)
            throw new InvalidOperationException("No envelope key store configured for Envelope mode encryption.");
        if (string.IsNullOrEmpty(config.KekKeyId))
            throw new InvalidOperationException("No KEK key ID configured for Envelope mode encryption.");

        var dek = await GenerateDataEncryptionKeyAsync(ct: default).ConfigureAwait(false);
        var wrappedDek = await config.EnvelopeKeyStore.WrapKeyAsync(config.KekKeyId, dek, context).ConfigureAwait(false);
        var iv = GenerateIv();

        var envelope = new EnvelopeHeader
        {
            KekId = config.KekKeyId,
            KeyStorePluginId = config.EnvelopeKeyStorePluginId ?? "",
            WrappedDek = wrappedDek,
            Iv = iv,
            EncryptionAlgorithm = AlgorithmId,
            EncryptionPluginId = Id,
            EncryptedAtTicks = DateTime.UtcNow.Ticks,
            EncryptedBy = context.UserId
        };

        RecordKeyAccess($"envelope:{config.KekKeyId}");
        return (dek, $"envelope:{Guid.NewGuid():N}", envelope);
    }

    /// <summary>
    /// Gets a key for decryption based on stored metadata.
    /// </summary>
    protected virtual async Task<byte[]> GetKeyForDecryptionAsync(
        EnvelopeHeader? envelope,
        string? keyId,
        ResolvedKeyManagementConfig config,
        ISecurityContext context)
    {
        if (envelope != null)
        {
            var envelopeKeyStore = config.EnvelopeKeyStore
                ?? KeyStoreRegistry?.GetEnvelopeKeyStore(envelope.KeyStorePluginId)
                ?? throw new InvalidOperationException($"Cannot find envelope key store '{envelope.KeyStorePluginId}' to unwrap DEK.");

            var dek = await envelopeKeyStore.UnwrapKeyAsync(envelope.KekId, envelope.WrappedDek, context).ConfigureAwait(false);
            RecordKeyAccess($"envelope:{envelope.KekId}");
            return dek;
        }
        else
        {
            if (string.IsNullOrEmpty(keyId))
                throw new InvalidOperationException("Key ID required for Direct mode decryption.");

            var keyStore = config.KeyStore
                ?? DefaultKeyStore
                ?? throw new InvalidOperationException("No key store configured for Direct mode decryption.");

            var key = await keyStore.GetKeyAsync(keyId, context).ConfigureAwait(false);
            RecordKeyAccess(keyId);
            return key;
        }
    }

    #endregion

    #region Security Context Resolution

    /// <summary>
    /// Gets the security context from operation arguments or falls back to system context.
    /// </summary>
    protected virtual ISecurityContext GetSecurityContext(Dictionary<string, object> args)
    {
        if (args.TryGetValue("securityContext", out var ctxObj) && ctxObj is ISecurityContext secCtx)
            return secCtx;

        return new DefaultSecurityContext();
    }

    /// <summary>
    /// Default security context for when none is provided.
    /// </summary>
    protected class DefaultSecurityContext : ISecurityContext
    {
        /// <inheritdoc/>
        public string UserId => "system";
        /// <inheritdoc/>
        public string? TenantId => null;
        /// <inheritdoc/>
        public IEnumerable<string> Roles => new[] { "system" };
        /// <inheritdoc/>
        public bool IsSystemAdmin => false;
    }

    #endregion

    #region Native Key Access

    /// <summary>
    /// Retrieves a key as a <see cref="NativeKeyHandle"/> for secure zero-copy crypto operations.
    /// Delegates to <see cref="IKeyStore.GetKeyNativeAsync"/> on the default key store.
    /// The caller MUST dispose the returned handle to trigger secure wipe.
    /// </summary>
    /// <param name="keyId">The key identifier.</param>
    /// <param name="context">Security context for ACL validation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="NativeKeyHandle"/> containing the key material in unmanaged memory.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no key store is configured.</exception>
    protected virtual Task<NativeKeyHandle> GetKeyNativeAsync(string keyId, ISecurityContext context, CancellationToken ct = default)
    {
        var keyStore = DefaultKeyStore
            ?? throw new InvalidOperationException("No key store configured. Call SetDefaultKeyStore first.");
        return keyStore.GetKeyNativeAsync(keyId, context, ct);
    }

    #endregion

    #region Core Crypto Methods (Algorithm-Specific)

    /// <summary>
    /// Performs the core encryption operation.
    /// Override in derived classes to provide algorithm-specific encryption.
    /// Default throws NotSupportedException -- plugins that use envelope/key management
    /// features must implement this method.
    /// </summary>
    /// <param name="input">The plaintext input stream.</param>
    /// <param name="key">The encryption key.</param>
    /// <param name="iv">The initialization vector.</param>
    /// <returns>The encrypted stream.</returns>
    protected virtual Task<Stream> EncryptCoreAsync(Stream input, byte[] key, byte[] iv)
        => throw new NotSupportedException($"EncryptCoreAsync not implemented by {GetType().Name}. Override to provide algorithm-specific encryption.");

    /// <summary>
    /// Performs the core decryption operation.
    /// Override in derived classes to provide algorithm-specific decryption.
    /// Default throws NotSupportedException -- plugins that use envelope/key management
    /// features must implement this method.
    /// </summary>
    /// <param name="input">The ciphertext input stream.</param>
    /// <param name="key">The decryption key.</param>
    /// <param name="iv">The initialization vector (null if embedded in ciphertext).</param>
    /// <returns>The decrypted stream and authentication tag (if applicable).</returns>
    protected virtual Task<(Stream data, byte[]? tag)> DecryptCoreAsync(Stream input, byte[] key, byte[]? iv)
        => throw new NotSupportedException($"DecryptCoreAsync not implemented by {GetType().Name}. Override to provide algorithm-specific decryption.");

    #endregion

    #region Audit Logging

    /// <summary>
    /// Logs a cryptographic operation for audit and compliance purposes.
    /// Publishes to the message bus if available.
    /// </summary>
    /// <param name="operation">The operation performed (e.g., "Encrypt", "Decrypt", "KeyRotation").</param>
    /// <param name="algorithm">The algorithm used.</param>
    /// <param name="outcome">The outcome (e.g., "Success", "Failed").</param>
    /// <param name="metadata">Additional metadata about the operation.</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual Task LogCryptoOperationAsync(
        string operation,
        string algorithm,
        string outcome,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        // Default implementation: no-op. Override to integrate with audit logging infrastructure.
        // Plugins with MessageBus access should publish to "crypto.audit.{operation}" topics.
        return Task.CompletedTask;
    }

    #endregion

    #region Configuration Methods

    /// <summary>
    /// Configures the default key store for Direct mode.
    /// </summary>
    public virtual void SetDefaultKeyStore(IKeyStore keyStore)
    {
        DefaultKeyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    /// <summary>
    /// Configures the default envelope key store for Envelope mode.
    /// </summary>
    public virtual void SetDefaultEnvelopeKeyStore(IEnvelopeKeyStore envelopeKeyStore, string kekKeyId)
    {
        DefaultEnvelopeKeyStore = envelopeKeyStore ?? throw new ArgumentNullException(nameof(envelopeKeyStore));
        DefaultKekKeyId = kekKeyId ?? throw new ArgumentNullException(nameof(kekKeyId));
    }

    /// <summary>
    /// Sets the default key management mode.
    /// </summary>
    public virtual void SetDefaultMode(KeyManagementMode mode)
    {
        DefaultKeyManagementMode = mode;
    }

    /// <summary>
    /// Sets the per-user configuration provider.
    /// </summary>
    public virtual void SetConfigProvider(IKeyManagementConfigProvider provider)
    {
        ConfigProvider = provider;
    }

    /// <summary>
    /// Sets the key store registry.
    /// </summary>
    public virtual void SetKeyStoreRegistry(IKeyStoreRegistry registry)
    {
        KeyStoreRegistry = registry;
    }

    #endregion

    #region Metadata

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["AlgorithmId"] = AlgorithmId;
        metadata["KeySizeBytes"] = KeySizeBytes;
        metadata["IvSizeBytes"] = IvSizeBytes;
        metadata["TagSizeBytes"] = TagSizeBytes;
        metadata["SupportsEnvelopeMode"] = true;
        metadata["SupportsDirectMode"] = true;
        metadata["SupportsPerUserConfig"] = true;
        return metadata;
    }

    #endregion

    #region IDisposable

    private bool _disposed;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            KeyAccessLog.Clear();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    #endregion
}
