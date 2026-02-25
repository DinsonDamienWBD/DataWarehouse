// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Features;

/// <summary>
/// Integrates encryption configuration with TamperProof storage manifests.
/// Handles write-time config resolution/storage and read-time config extraction.
/// </summary>
/// <remarks>
/// Phase E TamperProof Integration:
/// - E1: EncryptionMetadata structure for TamperProofManifest
/// - E2: Write-time config resolution and storage in manifest
/// - E3: Read-time config extraction from manifest (ignore current preferences)
/// </remarks>
public sealed class TamperProofEncryptionIntegration
{
    private readonly IKeyStoreRegistry? _keyStoreRegistry;
    private readonly IKeyManagementConfigProvider? _configProvider;
    private IEncryptionConfigMode _configMode;

    /// <summary>
    /// Creates a new TamperProof encryption integration instance.
    /// </summary>
    /// <param name="keyStoreRegistry">Optional registry for resolving key stores.</param>
    /// <param name="configProvider">Optional provider for user/tenant config preferences.</param>
    /// <param name="configMode">The encryption config mode to use (defaults to PerObjectConfig).</param>
    public TamperProofEncryptionIntegration(
        IKeyStoreRegistry? keyStoreRegistry = null,
        IKeyManagementConfigProvider? configProvider = null,
        EncryptionConfigMode configMode = EncryptionConfigMode.PerObjectConfig)
    {
        _keyStoreRegistry = keyStoreRegistry;
        _configProvider = configProvider;
        _configMode = EncryptionConfigModeFactory.Create(configMode);
    }

    /// <summary>
    /// Gets or sets the active encryption config mode.
    /// </summary>
    public IEncryptionConfigMode ConfigMode
    {
        get => _configMode;
        set => _configMode = value ?? throw new ArgumentNullException(nameof(value));
    }

    #region E2: Write-Time Config Resolution and Storage

    /// <summary>
    /// Resolves encryption configuration for a write operation and creates manifest metadata.
    /// This is called during the write pipeline BEFORE encryption occurs.
    /// </summary>
    /// <param name="context">Security context for the write operation.</param>
    /// <param name="preferredConfig">Optional preferred configuration from caller.</param>
    /// <param name="encryptionPluginId">The encryption plugin that will be used.</param>
    /// <param name="existingManifest">Optional existing manifest (for corrections/updates).</param>
    /// <param name="policy">Optional policy for PolicyEnforced mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved configuration ready for storage in manifest.</returns>
    /// <exception cref="InvalidOperationException">If config mode validation fails.</exception>
    public async Task<WriteTimeEncryptionConfig> ResolveWriteConfigAsync(
        ISecurityContext context,
        KeyManagementConfig? preferredConfig,
        string encryptionPluginId,
        TamperProofManifest? existingManifest = null,
        EncryptionPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Get the base configuration (from preference or provider)
        var baseConfig = preferredConfig;
        if (baseConfig == null && _configProvider != null)
        {
            baseConfig = await _configProvider.GetConfigAsync(context);
        }

        // Step 2: Resolve to concrete instances
        var resolvedConfig = await ResolveKeyManagementConfigAsync(
            baseConfig,
            context,
            cancellationToken);

        // Step 3: Build the EncryptionMetadata that will be stored in the manifest
        var encryptionMetadata = BuildEncryptionMetadata(
            resolvedConfig,
            encryptionPluginId,
            context);

        // Step 4: Extract existing config from manifest (if updating)
        var existingEncryptionMetadata = existingManifest?.GetEncryptionMetadata();

        // Step 5: Validate against config mode rules
        var validation = _configMode.CanWriteWithConfig(
            encryptionMetadata,
            existingEncryptionMetadata,
            policy);

        if (!validation.IsAllowed)
        {
            throw new InvalidOperationException(
                $"Write rejected by encryption config mode '{_configMode.Id}': {validation.Error}");
        }

        // Step 6: Handle FixedConfig sealing
        if (_configMode is FixedConfigMode fixedMode && !fixedMode.IsConfigurationSealed)
        {
            fixedMode.TrySeal(encryptionMetadata);
        }

        return new WriteTimeEncryptionConfig
        {
            ResolvedConfig = resolvedConfig,
            EncryptionMetadata = encryptionMetadata,
            ConfigMode = _configMode.Mode,
            Policy = policy
        };
    }

    /// <summary>
    /// Creates UserMetadata dictionary entries for storing in the manifest.
    /// Call this after ResolveWriteConfigAsync to get the metadata to store.
    /// </summary>
    /// <param name="writeConfig">The resolved write-time configuration.</param>
    /// <param name="existingUserMetadata">Optional existing user metadata to merge with.</param>
    /// <returns>UserMetadata dictionary ready for manifest creation.</returns>
    public Dictionary<string, object> CreateManifestUserMetadata(
        WriteTimeEncryptionConfig writeConfig,
        Dictionary<string, object>? existingUserMetadata = null)
    {
        return TamperProofManifestExtensions.CreateUserMetadataWithEncryption(
            writeConfig.EncryptionMetadata,
            writeConfig.ConfigMode,
            existingUserMetadata);
    }

    #endregion

    #region E3: Read-Time Config Extraction

    /// <summary>
    /// Extracts encryption configuration from a manifest for decryption.
    /// This ignores current user preferences and uses the stored configuration.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to read from.</param>
    /// <param name="context">Security context for key retrieval authorization.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolved configuration for decryption.</returns>
    /// <exception cref="InvalidOperationException">If manifest lacks encryption metadata.</exception>
    public async Task<ReadTimeEncryptionConfig> ExtractReadConfigAsync(
        TamperProofManifest manifest,
        ISecurityContext context,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Validate manifest has encryption metadata
        var validationErrors = manifest.ValidateEncryptionMetadata();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Manifest encryption metadata validation failed: {string.Join("; ", validationErrors)}");
        }

        // Step 2: Extract the stored encryption metadata
        var encryptionMetadata = manifest.GetRequiredEncryptionMetadata();

        // Step 3: Handle version migration if needed
        var migratedMetadata = manifest.MigrateEncryptionMetadataIfNeeded();
        if (migratedMetadata != null)
        {
            encryptionMetadata = migratedMetadata;
        }

        // Step 4: Resolve key stores from metadata
        var resolvedConfig = await ResolveFromEncryptionMetadataAsync(
            encryptionMetadata,
            context,
            cancellationToken);

        // Step 5: Get config mode from manifest (informational)
        var configMode = manifest.GetEncryptionConfigMode() ?? EncryptionConfigMode.PerObjectConfig;

        return new ReadTimeEncryptionConfig
        {
            ResolvedConfig = resolvedConfig,
            EncryptionMetadata = encryptionMetadata,
            ConfigMode = configMode,
            ManifestVersion = manifest.GetEncryptionMetadataVersion(),
            ObjectId = manifest.ObjectId,
            ObjectVersion = manifest.Version
        };
    }

    /// <summary>
    /// Checks if a manifest contains decryptable content.
    /// </summary>
    /// <param name="manifest">The TamperProofManifest to check.</param>
    /// <returns>True if the manifest indicates encrypted content.</returns>
    public bool IsEncryptedContent(TamperProofManifest manifest)
    {
        return manifest.IsContentEncrypted();
    }

    #endregion

    #region Resolution Helpers

    /// <summary>
    /// Resolves a KeyManagementConfig to concrete key store instances.
    /// </summary>
    private async Task<ResolvedKeyManagementConfig> ResolveKeyManagementConfigAsync(
        KeyManagementConfig? config,
        ISecurityContext context,
        CancellationToken cancellationToken)
    {
        if (config == null)
        {
            // Return default configuration
            return new ResolvedKeyManagementConfig
            {
                Mode = KeyManagementMode.Direct,
                KeyStore = _keyStoreRegistry?.GetKeyStore(_keyStoreRegistry.GetRegisteredKeyStoreIds().FirstOrDefault()),
                KeyId = null
            };
        }

        var resolvedKeyStore = config.KeyStore;
        var resolvedEnvelopeKeyStore = config.EnvelopeKeyStore;

        // Resolve from registry if needed
        if (resolvedKeyStore == null && !string.IsNullOrEmpty(config.KeyStorePluginId))
        {
            resolvedKeyStore = _keyStoreRegistry?.GetKeyStore(config.KeyStorePluginId);
        }

        if (resolvedEnvelopeKeyStore == null && !string.IsNullOrEmpty(config.EnvelopeKeyStorePluginId))
        {
            resolvedEnvelopeKeyStore = _keyStoreRegistry?.GetEnvelopeKeyStore(config.EnvelopeKeyStorePluginId);
        }

        // Get current key ID if not specified
        var keyId = config.KeyId;
        if (string.IsNullOrEmpty(keyId) && resolvedKeyStore != null)
        {
            keyId = await resolvedKeyStore.GetCurrentKeyIdAsync();
        }

        return new ResolvedKeyManagementConfig
        {
            Mode = config.Mode,
            KeyStore = resolvedKeyStore,
            KeyId = keyId,
            EnvelopeKeyStore = resolvedEnvelopeKeyStore,
            KekKeyId = config.KekKeyId,
            KeyStorePluginId = config.KeyStorePluginId,
            EnvelopeKeyStorePluginId = config.EnvelopeKeyStorePluginId
        };
    }

    /// <summary>
    /// Resolves key stores from stored encryption metadata (for read operations).
    /// </summary>
    private Task<ResolvedKeyManagementConfig> ResolveFromEncryptionMetadataAsync(
        EncryptionMetadata metadata,
        ISecurityContext context,
        CancellationToken cancellationToken)
    {
        IKeyStore? keyStore = null;
        IEnvelopeKeyStore? envelopeKeyStore = null;

        // Resolve key store from stored plugin ID
        if (!string.IsNullOrEmpty(metadata.KeyStorePluginId))
        {
            keyStore = _keyStoreRegistry?.GetKeyStore(metadata.KeyStorePluginId);

            if (metadata.KeyMode == KeyManagementMode.Envelope)
            {
                envelopeKeyStore = _keyStoreRegistry?.GetEnvelopeKeyStore(metadata.KeyStorePluginId);
            }
        }

        var resolved = new ResolvedKeyManagementConfig
        {
            Mode = metadata.KeyMode,
            KeyStore = keyStore,
            KeyId = metadata.KeyId,
            EnvelopeKeyStore = envelopeKeyStore,
            KekKeyId = metadata.KekId,
            KeyStorePluginId = metadata.KeyStorePluginId,
            EnvelopeKeyStorePluginId = metadata.KeyStorePluginId // For envelope mode
        };

        return Task.FromResult(resolved);
    }

    /// <summary>
    /// Builds EncryptionMetadata from resolved configuration.
    /// </summary>
    private static EncryptionMetadata BuildEncryptionMetadata(
        ResolvedKeyManagementConfig config,
        string encryptionPluginId,
        ISecurityContext context)
    {
        return new EncryptionMetadata
        {
            EncryptionPluginId = encryptionPluginId,
            KeyMode = config.Mode,
            KeyId = config.KeyId,
            KekId = config.KekKeyId,
            KeyStorePluginId = config.Mode == KeyManagementMode.Envelope
                ? config.EnvelopeKeyStorePluginId
                : config.KeyStorePluginId,
            EncryptedAt = DateTime.UtcNow,
            EncryptedBy = context.UserId,
            Version = 1
        };
    }

    #endregion
}

/// <summary>
/// Configuration resolved at write time, ready for storage in manifest.
/// </summary>
public sealed class WriteTimeEncryptionConfig
{
    /// <summary>
    /// Resolved key management configuration with concrete instances.
    /// </summary>
    public required ResolvedKeyManagementConfig ResolvedConfig { get; init; }

    /// <summary>
    /// Encryption metadata to store in the manifest.
    /// </summary>
    public required EncryptionMetadata EncryptionMetadata { get; init; }

    /// <summary>
    /// The config mode being used.
    /// </summary>
    public required EncryptionConfigMode ConfigMode { get; init; }

    /// <summary>
    /// Optional policy that was applied (for PolicyEnforced mode).
    /// </summary>
    public EncryptionPolicy? Policy { get; init; }
}

/// <summary>
/// Configuration extracted at read time from manifest.
/// </summary>
public sealed class ReadTimeEncryptionConfig
{
    /// <summary>
    /// Resolved key management configuration for decryption.
    /// </summary>
    public required ResolvedKeyManagementConfig ResolvedConfig { get; init; }

    /// <summary>
    /// Encryption metadata extracted from manifest.
    /// </summary>
    public required EncryptionMetadata EncryptionMetadata { get; init; }

    /// <summary>
    /// The config mode that was used when the object was written.
    /// </summary>
    public required EncryptionConfigMode ConfigMode { get; init; }

    /// <summary>
    /// Version of the encryption metadata format.
    /// </summary>
    public required int ManifestVersion { get; init; }

    /// <summary>
    /// The object ID from the manifest.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// The object version from the manifest.
    /// </summary>
    public required int ObjectVersion { get; init; }
}

/// <summary>
/// Builder for creating TamperProofEncryptionIntegration instances.
/// </summary>
public sealed class TamperProofEncryptionIntegrationBuilder
{
    private IKeyStoreRegistry? _keyStoreRegistry;
    private IKeyManagementConfigProvider? _configProvider;
    private EncryptionConfigMode _configMode = EncryptionConfigMode.PerObjectConfig;
    private EncryptionPolicy? _policy;

    /// <summary>
    /// Sets the key store registry for resolving key stores.
    /// </summary>
    public TamperProofEncryptionIntegrationBuilder WithKeyStoreRegistry(IKeyStoreRegistry registry)
    {
        _keyStoreRegistry = registry;
        return this;
    }

    /// <summary>
    /// Sets the config provider for user/tenant preferences.
    /// </summary>
    public TamperProofEncryptionIntegrationBuilder WithConfigProvider(IKeyManagementConfigProvider provider)
    {
        _configProvider = provider;
        return this;
    }

    /// <summary>
    /// Sets the encryption config mode to use.
    /// </summary>
    public TamperProofEncryptionIntegrationBuilder WithConfigMode(EncryptionConfigMode mode)
    {
        _configMode = mode;
        return this;
    }

    /// <summary>
    /// Sets the encryption policy (for PolicyEnforced mode).
    /// </summary>
    public TamperProofEncryptionIntegrationBuilder WithPolicy(EncryptionPolicy policy)
    {
        _policy = policy;
        return this;
    }

    /// <summary>
    /// Builds the TamperProofEncryptionIntegration instance.
    /// </summary>
    public TamperProofEncryptionIntegration Build()
    {
        var integration = new TamperProofEncryptionIntegration(
            _keyStoreRegistry,
            _configProvider,
            _configMode);

        // Set policy if using PolicyEnforced mode
        if (_policy != null && integration.ConfigMode is PolicyEnforcedConfigMode policyMode)
        {
            policyMode.Policy = _policy;
        }

        return integration;
    }
}
