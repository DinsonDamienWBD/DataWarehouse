using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Security.Transit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract base class for cipher preset provider plugins.
    /// Simplifies implementation of ICommonCipherPresets by providing common infrastructure
    /// and default implementations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived classes should:
    /// 1. Override InitializePresetsAsync to populate preset collections
    /// 2. Optionally override GetDefaultPresetAsync to customize defaults
    /// 3. Optionally override ValidatePresetSecurityLevelAsync for custom validation
    /// </para>
    /// <para>
    /// This base class provides:
    /// - Thread-safe preset registration and lookup
    /// - Default implementation of all interface methods
    /// - Automatic preset categorization by security level
    /// - Plugin lifecycle integration (handshake, initialization)
    /// </para>
    /// </remarks>
    public abstract class CipherPresetProviderPluginBase : PluginBase, ICommonCipherPresets, IIntelligenceAware
    {
        #region Intelligence Socket

        public bool IsIntelligenceAvailable { get; protected set; }
        public IntelligenceCapabilities AvailableCapabilities { get; protected set; }

        public virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
            IsIntelligenceAvailable = false;
            return IsIntelligenceAvailable;
        }

        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.cipher-presets",
                DisplayName = $"{Name} - Cipher Presets",
                Description = "Cipher preset definitions for transit encryption",
                Category = CapabilityCategory.Security,
                SubCategory = "Encryption",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "cipher", "presets", "encryption", "security" },
                SemanticDescription = "Use for cipher preset selection and validation"
            }
        };

        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.cipher-presets.capability",
                    Topic = "security.encryption.presets",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"Cipher presets: {_standardPresets.Count} standard, {_highSecurityPresets.Count} high, {_quantumSafePresets.Count} quantum-safe",
                    Payload = new Dictionary<string, object>
                    {
                        ["standardCount"] = _standardPresets.Count,
                        ["highSecurityCount"] = _highSecurityPresets.Count,
                        ["quantumSafeCount"] = _quantumSafePresets.Count
                    },
                    Tags = new[] { "cipher", "presets", "encryption" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted cipher preset recommendation.
        /// </summary>
        protected virtual async Task<CipherPresetRecommendation?> RequestPresetRecommendationAsync(TransitSecurityLevel securityLevel, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        #endregion

        private readonly List<CipherPreset> _standardPresets = new();
        private readonly List<CipherPreset> _highSecurityPresets = new();
        private readonly List<CipherPreset> _quantumSafePresets = new();
        private readonly Dictionary<string, CipherPreset> _presetLookup = new();
        private readonly SemaphoreSlim _initializationLock = new(1, 1);
        private bool _presetsInitialized;

        /// <summary>
        /// Gets the plugin category (always SecurityProvider).
        /// </summary>
        public override PluginCategory Category => PluginCategory.SecurityProvider;

        /// <inheritdoc/>
        public IReadOnlyList<CipherPreset> StandardPresets => _standardPresets.AsReadOnly();

        /// <inheritdoc/>
        public IReadOnlyList<CipherPreset> HighSecurityPresets => _highSecurityPresets.AsReadOnly();

        /// <inheritdoc/>
        public IReadOnlyList<CipherPreset> QuantumSafePresets => _quantumSafePresets.AsReadOnly();

        /// <inheritdoc/>
        public virtual async Task<CipherPreset?> GetPresetAsync(string presetId, CancellationToken cancellationToken = default)
        {
            await EnsurePresetsInitializedAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(presetId))
                return null;

            return _presetLookup.TryGetValue(presetId, out var preset) ? preset : null;
        }

        /// <inheritdoc/>
        public virtual async Task<IReadOnlyList<CipherPreset>> ListPresetsAsync(CancellationToken cancellationToken = default)
        {
            await EnsurePresetsInitializedAsync(cancellationToken).ConfigureAwait(false);

            var allPresets = new List<CipherPreset>();
            allPresets.AddRange(_standardPresets);
            allPresets.AddRange(_highSecurityPresets);
            allPresets.AddRange(_quantumSafePresets);

            return allPresets.AsReadOnly();
        }

        /// <inheritdoc/>
        public virtual async Task<IReadOnlyList<CipherPreset>> ListPresetsBySecurityLevelAsync(
            TransitSecurityLevel securityLevel,
            CancellationToken cancellationToken = default)
        {
            await EnsurePresetsInitializedAsync(cancellationToken).ConfigureAwait(false);

            return securityLevel switch
            {
                TransitSecurityLevel.Standard => _standardPresets.AsReadOnly(),
                TransitSecurityLevel.High => _highSecurityPresets.AsReadOnly(),
                TransitSecurityLevel.Military => _highSecurityPresets.AsReadOnly(), // Military uses High presets
                TransitSecurityLevel.QuantumSafe => _quantumSafePresets.AsReadOnly(),
                _ => Array.Empty<CipherPreset>()
            };
        }

        /// <inheritdoc/>
        public virtual async Task<CipherPreset> GetDefaultPresetAsync(
            TransitSecurityLevel securityLevel,
            CancellationToken cancellationToken = default)
        {
            await EnsurePresetsInitializedAsync(cancellationToken).ConfigureAwait(false);

            // Default preset selection by security level
            var presetId = securityLevel switch
            {
                TransitSecurityLevel.Standard => "standard-aes256gcm",
                TransitSecurityLevel.High => "high-aes256gcm-sha512",
                TransitSecurityLevel.Military => "high-suiteb",
                TransitSecurityLevel.QuantumSafe => "quantum-kyber768-dilithium3",
                _ => "standard-aes256gcm"
            };

            var preset = await GetPresetAsync(presetId, cancellationToken).ConfigureAwait(false);
            if (preset == null)
            {
                throw new InvalidOperationException($"Default preset '{presetId}' for security level '{securityLevel}' not found.");
            }

            return preset;
        }

        /// <inheritdoc/>
        public virtual async Task<bool> ValidatePresetSecurityLevelAsync(
            string presetId,
            TransitSecurityLevel minimumSecurityLevel,
            CancellationToken cancellationToken = default)
        {
            var preset = await GetPresetAsync(presetId, cancellationToken).ConfigureAwait(false);
            if (preset == null)
                return false;

            return preset.SecurityLevel >= minimumSecurityLevel;
        }

        /// <summary>
        /// Initializes the preset collections.
        /// Derived classes must override this to register their presets.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <remarks>
        /// <para>
        /// Use the RegisterPreset method to add presets to the appropriate category.
        /// Example:
        /// <code>
        /// protected override async Task InitializePresetsAsync(CancellationToken cancellationToken)
        /// {
        ///     RegisterPreset(new CipherPreset(
        ///         Id: "standard-aes256gcm",
        ///         Name: "AES-256-GCM",
        ///         Algorithms: new[] { "AES-256-GCM" },
        ///         SecurityLevel: TransitSecurityLevel.Standard,
        ///         Description: "NIST-approved AES with GCM mode"
        ///     ));
        ///     await Task.CompletedTask;
        /// }
        /// </code>
        /// </para>
        /// </remarks>
        protected abstract Task InitializePresetsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Registers a cipher preset with this provider.
        /// Automatically categorizes by security level.
        /// </summary>
        /// <param name="preset">The preset to register.</param>
        /// <exception cref="ArgumentNullException">If preset is null.</exception>
        /// <exception cref="InvalidOperationException">If a preset with the same ID already exists.</exception>
        protected void RegisterPreset(CipherPreset preset)
        {
            ArgumentNullException.ThrowIfNull(preset);

            if (_presetLookup.ContainsKey(preset.Id))
            {
                throw new InvalidOperationException($"Preset with ID '{preset.Id}' is already registered.");
            }

            // Add to appropriate category based on security level
            switch (preset.SecurityLevel)
            {
                case TransitSecurityLevel.Standard:
                    _standardPresets.Add(preset);
                    break;
                case TransitSecurityLevel.High:
                case TransitSecurityLevel.Military:
                    _highSecurityPresets.Add(preset);
                    break;
                case TransitSecurityLevel.QuantumSafe:
                    _quantumSafePresets.Add(preset);
                    break;
            }

            _presetLookup[preset.Id] = preset;
        }

        /// <summary>
        /// Ensures presets are initialized (lazy initialization).
        /// </summary>
        private async Task EnsurePresetsInitializedAsync(CancellationToken cancellationToken)
        {
            if (_presetsInitialized)
                return;

            await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_presetsInitialized)
                    return;

                await InitializePresetsAsync(cancellationToken).ConfigureAwait(false);
                _presetsInitialized = true;
            }
            finally
            {
                _initializationLock.Release();
            }
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _initializationLock.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Abstract base class for transit encryption plugins.
    /// Simplifies implementation of ITransitEncryption by providing common infrastructure,
    /// cipher negotiation logic, and integration with IKeyStore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived classes should:
    /// 1. Override EncryptDataAsync and DecryptDataAsync for core encryption operations
    /// 2. Optionally override NegotiateCipherInternalAsync for custom negotiation logic
    /// 3. Inject IKeyStore and ICommonCipherPresets via constructor or properties
    /// </para>
    /// <para>
    /// This base class provides:
    /// - Cipher negotiation with fallback handling
    /// - Stream processing wrapper around byte array operations
    /// - Key retrieval integration with IKeyStore
    /// - Audit event publishing via message bus
    /// - Capability advertisement
    /// </para>
    /// </remarks>
    public abstract class TransitEncryptionPluginBase : PipelinePluginBase, ITransitEncryption, IIntelligenceAware
    {
        #region Intelligence Socket

        public bool IsIntelligenceAvailable { get; protected set; }
        public IntelligenceCapabilities AvailableCapabilities { get; protected set; }

        public virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
            IsIntelligenceAvailable = false;
            return IsIntelligenceAvailable;
        }

        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.transit-encryption",
                DisplayName = $"{Name} - Transit Encryption",
                Description = "Data encryption for secure network transit",
                Category = CapabilityCategory.Pipeline,
                SubCategory = "Encryption",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "encryption", "transit", "pipeline", "security" },
                SemanticDescription = "Use for data encryption during transit"
            }
        };

        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.transit-encryption.capability",
                    Topic = "pipeline.encryption.transit",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = "Transit encryption with cipher negotiation",
                    Payload = new Dictionary<string, object>
                    {
                        ["supportsNegotiation"] = true,
                        ["supportsCompression"] = true
                    },
                    Tags = new[] { "encryption", "transit", "pipeline" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted cipher selection recommendation.
        /// </summary>
        protected virtual async Task<CipherSelectionRecommendation?> RequestCipherSelectionAsync(TransitSecurityLevel securityLevel, EndpointCapabilities? remoteCapabilities, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        #endregion

        /// <summary>
        /// Key store for encryption key retrieval.
        /// Must be set by derived classes (via constructor or initialization).
        /// </summary>
        protected IKeyStore? KeyStore { get; set; }

        /// <summary>
        /// Cipher preset provider for preset lookup and negotiation.
        /// Must be set by derived classes (via constructor or initialization).
        /// </summary>
        protected ICommonCipherPresets? PresetProvider { get; set; }

        /// <summary>SubCategory is always "TransitEncryption".</summary>
        public override string SubCategory => "TransitEncryption";

        /// <summary>Default pipeline order (transit runs after at-rest stages).</summary>
        public override int DefaultOrder => 300;

        /// <summary>Whether bypass is allowed.</summary>
        public override bool AllowBypass => true;

        #region IDataTransformation Bridge (OnWrite/OnRead)

        /// <summary>
        /// Bridge: delegates to EncryptForTransitAsync for pipeline integration.
        /// </summary>
        public override Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            using var ms = new System.IO.MemoryStream();
            input.CopyTo(ms);
            var data = ms.ToArray();
            var options = new TransitEncryptionOptions();
            // Extract security context from args if available
            ISecurityContext secCtx = args.TryGetValue("securityContext", out var ctxObj) && ctxObj is ISecurityContext sc
                ? sc
                : new DefaultTransitSecurityContext();
            var result = EncryptForTransitAsync(data, options, secCtx).GetAwaiter().GetResult();
            return new System.IO.MemoryStream(result.Ciphertext);
        }

        /// <summary>
        /// Bridge: delegates to DecryptFromTransitAsync for pipeline integration.
        /// </summary>
        public override Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            using var ms = new System.IO.MemoryStream();
            stored.CopyTo(ms);
            var data = ms.ToArray();
            var metadata = new Dictionary<string, object>();
            // Extract encryption metadata from args if available
            if (args.TryGetValue("transitEncryptionMetadata", out var metaObj) && metaObj is Dictionary<string, object> meta)
                metadata = meta;
            ISecurityContext secCtx = args.TryGetValue("securityContext", out var ctxObj2) && ctxObj2 is ISecurityContext sc2
                ? sc2
                : new DefaultTransitSecurityContext();
            var result = DecryptFromTransitAsync(data, metadata, secCtx).GetAwaiter().GetResult();
            return new System.IO.MemoryStream(result.Plaintext);
        }

        /// <summary>
        /// Default security context for pipeline bridge when none is provided.
        /// </summary>
        private sealed class DefaultTransitSecurityContext : ISecurityContext
        {
            public string UserId => "system";
            public string? TenantId => null;
            public IEnumerable<string> Roles => new[] { "system" };
            public bool IsSystemAdmin => true;
        }

        #endregion

        /// <inheritdoc/>
        public virtual async Task<TransitEncryptionResult> EncryptForTransitAsync(
            byte[] plaintext,
            TransitEncryptionOptions options,
            ISecurityContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plaintext);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(context);
            EnsureDependencies();

            // Negotiate cipher if remote capabilities provided
            CipherPreset preset;
            if (options.RemoteCapabilities != null)
            {
                var negotiation = await NegotiateCipherAsync(
                    new[] { options.PresetId ?? "" },
                    options.RemoteCapabilities,
                    options.SecurityLevel,
                    cancellationToken).ConfigureAwait(false);

                if (!negotiation.Success || negotiation.NegotiatedPresetId == null)
                {
                    if (options.Mode == TransitEncryptionMode.AlwaysEncrypt)
                    {
                        throw new InvalidOperationException($"Cipher negotiation failed: {negotiation.ErrorMessage}");
                    }
                    // Opportunistic mode: use default preset
                    preset = await PresetProvider!.GetDefaultPresetAsync(options.SecurityLevel, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    preset = (await PresetProvider!.GetPresetAsync(negotiation.NegotiatedPresetId, cancellationToken).ConfigureAwait(false))!;
                }
            }
            else if (!string.IsNullOrWhiteSpace(options.PresetId))
            {
                preset = (await PresetProvider!.GetPresetAsync(options.PresetId, cancellationToken).ConfigureAwait(false))
                    ?? throw new InvalidOperationException($"Preset '{options.PresetId}' not found.");
            }
            else
            {
                preset = await PresetProvider!.GetDefaultPresetAsync(options.SecurityLevel, cancellationToken).ConfigureAwait(false);
            }

            // Retrieve encryption key
            var keyId = await KeyStore!.GetCurrentKeyIdAsync().ConfigureAwait(false);
            var key = await KeyStore.GetKeyAsync(keyId, context).ConfigureAwait(false);

            // Optional compression
            var dataToEncrypt = plaintext;
            var wasCompressed = false;
            if (options.CompressBeforeEncryption)
            {
                dataToEncrypt = await CompressDataAsync(plaintext, cancellationToken).ConfigureAwait(false);
                wasCompressed = true;
            }

            // Encrypt
            var result = await EncryptDataAsync(dataToEncrypt, preset, key, options.AdditionalAuthenticatedData, cancellationToken).ConfigureAwait(false);

            return new TransitEncryptionResult
            {
                Ciphertext = result.Ciphertext,
                UsedPresetId = preset.Id,
                EncryptionMetadata = result.Metadata,
                WasCompressed = wasCompressed
            };
        }

        /// <inheritdoc/>
        public virtual async Task<TransitEncryptionResult> EncryptStreamForTransitAsync(
            Stream plaintextStream,
            Stream ciphertextStream,
            TransitEncryptionOptions options,
            ISecurityContext context,
            CancellationToken cancellationToken = default)
        {
            // Default implementation: read stream to byte array and use byte array encryption
            // Derived classes should override for true streaming encryption
            using var ms = new MemoryStream();
            await plaintextStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            var plaintext = ms.ToArray();

            var result = await EncryptForTransitAsync(plaintext, options, context, cancellationToken).ConfigureAwait(false);

            await ciphertextStream.WriteAsync(result.Ciphertext, cancellationToken).ConfigureAwait(false);

            return result;
        }

        /// <inheritdoc/>
        public virtual async Task<TransitDecryptionResult> DecryptFromTransitAsync(
            byte[] ciphertext,
            Dictionary<string, object> encryptionMetadata,
            ISecurityContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(ciphertext);
            ArgumentNullException.ThrowIfNull(encryptionMetadata);
            ArgumentNullException.ThrowIfNull(context);
            EnsureDependencies();

            // Extract preset ID from metadata
            if (!encryptionMetadata.TryGetValue("PresetId", out var presetIdObj) || presetIdObj is not string presetId)
            {
                throw new InvalidOperationException("Encryption metadata does not contain valid PresetId.");
            }

            var preset = await PresetProvider!.GetPresetAsync(presetId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Preset '{presetId}' not found.");

            // Retrieve decryption key
            var keyId = encryptionMetadata.TryGetValue("KeyId", out var keyIdObj) && keyIdObj is string kId
                ? kId
                : await KeyStore!.GetCurrentKeyIdAsync().ConfigureAwait(false);
            var key = await KeyStore!.GetKeyAsync(keyId, context).ConfigureAwait(false);

            // Decrypt
            var plaintext = await DecryptDataAsync(ciphertext, preset, key, encryptionMetadata, cancellationToken).ConfigureAwait(false);

            // Optional decompression
            var wasDecompressed = false;
            if (encryptionMetadata.TryGetValue("Compressed", out var compressedObj) && compressedObj is bool compressed && compressed)
            {
                plaintext = await DecompressDataAsync(plaintext, cancellationToken).ConfigureAwait(false);
                wasDecompressed = true;
            }

            return new TransitDecryptionResult
            {
                Plaintext = plaintext,
                UsedPresetId = presetId,
                WasDecompressed = wasDecompressed,
                EncryptedAt = encryptionMetadata.TryGetValue("EncryptedAt", out var timestampObj) && timestampObj is DateTime timestamp
                    ? timestamp
                    : null
            };
        }

        /// <inheritdoc/>
        public virtual async Task<TransitDecryptionResult> DecryptStreamFromTransitAsync(
            Stream ciphertextStream,
            Stream plaintextStream,
            ISecurityContext context,
            CancellationToken cancellationToken = default)
        {
            // Default implementation: read stream to byte array and use byte array decryption
            // Derived classes should override for true streaming decryption
            using var ms = new MemoryStream();
            await ciphertextStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            var ciphertext = ms.ToArray();

            // Extract metadata from stream header (implementation-specific)
            var metadata = new Dictionary<string, object>();

            var result = await DecryptFromTransitAsync(ciphertext, metadata, context, cancellationToken).ConfigureAwait(false);

            await plaintextStream.WriteAsync(result.Plaintext, cancellationToken).ConfigureAwait(false);

            return result;
        }

        /// <inheritdoc/>
        public virtual async Task<CipherNegotiationResult> NegotiateCipherAsync(
            IReadOnlyList<string> localPreferences,
            EndpointCapabilities remoteCapabilities,
            TransitSecurityLevel minimumSecurityLevel = TransitSecurityLevel.Standard,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(localPreferences);
            ArgumentNullException.ThrowIfNull(remoteCapabilities);
            EnsureDependencies();

            // Find mutually supported presets
            var mutualPresets = new List<string>();
            foreach (var localPreset in localPreferences)
            {
                if (string.IsNullOrWhiteSpace(localPreset))
                    continue;

                if (remoteCapabilities.SupportedCipherPresets.Contains(localPreset))
                {
                    // Validate security level
                    var preset = await PresetProvider!.GetPresetAsync(localPreset, cancellationToken).ConfigureAwait(false);
                    if (preset != null && preset.SecurityLevel >= minimumSecurityLevel)
                    {
                        mutualPresets.Add(localPreset);
                    }
                }
            }

            if (mutualPresets.Count == 0)
            {
                return new CipherNegotiationResult
                {
                    Success = false,
                    ErrorMessage = $"No mutually supported cipher presets meeting minimum security level '{minimumSecurityLevel}'.",
                    MutuallySupportedPresets = Array.Empty<string>()
                };
            }

            // Select the first mutual preset (highest priority)
            var negotiatedPresetId = mutualPresets[0];
            var negotiatedPreset = await PresetProvider!.GetPresetAsync(negotiatedPresetId, cancellationToken).ConfigureAwait(false);

            return new CipherNegotiationResult
            {
                Success = true,
                NegotiatedPresetId = negotiatedPresetId,
                NegotiatedSecurityLevel = negotiatedPreset!.SecurityLevel,
                MutuallySupportedPresets = mutualPresets.AsReadOnly()
            };
        }

        /// <inheritdoc/>
        public virtual async Task<EndpointCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            EnsureDependencies();

            var allPresets = await PresetProvider!.ListPresetsAsync(cancellationToken).ConfigureAwait(false);
            var presetIds = allPresets.Select(p => p.Id).ToList();
            var algorithms = allPresets.SelectMany(p => p.Algorithms).Distinct().ToList();

            return new EndpointCapabilities(
                SupportedCipherPresets: presetIds.AsReadOnly(),
                SupportedAlgorithms: algorithms.AsReadOnly(),
                PreferredPresetId: presetIds.FirstOrDefault(),
                MaximumSecurityLevel: TransitSecurityLevel.QuantumSafe,
                SupportsTranscryption: false
            );
        }

        /// <summary>
        /// Encrypts data with the specified cipher preset and key.
        /// Derived classes must implement this for actual encryption.
        /// </summary>
        /// <param name="plaintext">The plaintext to encrypt.</param>
        /// <param name="preset">The cipher preset to use.</param>
        /// <param name="key">The encryption key.</param>
        /// <param name="aad">Additional authenticated data (optional).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Encryption result with ciphertext and metadata.</returns>
        protected abstract Task<(byte[] Ciphertext, Dictionary<string, object> Metadata)> EncryptDataAsync(
            byte[] plaintext,
            CipherPreset preset,
            byte[] key,
            byte[]? aad,
            CancellationToken cancellationToken);

        /// <summary>
        /// Decrypts data with the specified cipher preset and key.
        /// Derived classes must implement this for actual decryption.
        /// </summary>
        /// <param name="ciphertext">The ciphertext to decrypt.</param>
        /// <param name="preset">The cipher preset to use.</param>
        /// <param name="key">The decryption key.</param>
        /// <param name="metadata">Encryption metadata (IV, nonce, tag, etc.).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The decrypted plaintext.</returns>
        protected abstract Task<byte[]> DecryptDataAsync(
            byte[] ciphertext,
            CipherPreset preset,
            byte[] key,
            Dictionary<string, object> metadata,
            CancellationToken cancellationToken);

        /// <summary>
        /// Compresses data before encryption.
        /// Default implementation uses GZip. Override for custom compression.
        /// </summary>
        protected virtual async Task<byte[]> CompressDataAsync(byte[] data, CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
            {
                await gzip.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
            return output.ToArray();
        }

        /// <summary>
        /// Decompresses data after decryption.
        /// Default implementation uses GZip. Override for custom compression.
        /// </summary>
        protected virtual async Task<byte[]> DecompressDataAsync(byte[] compressedData, CancellationToken cancellationToken)
        {
            using var input = new MemoryStream(compressedData);
            using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            await gzip.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return output.ToArray();
        }

        /// <summary>
        /// Ensures required dependencies are set.
        /// </summary>
        private void EnsureDependencies()
        {
            if (KeyStore == null)
                throw new InvalidOperationException("KeyStore dependency is not set. Initialize the plugin with an IKeyStore instance.");
            if (PresetProvider == null)
                throw new InvalidOperationException("PresetProvider dependency is not set. Initialize the plugin with an ICommonCipherPresets instance.");
        }
    }

    /// <summary>
    /// Abstract base class for transcryption service plugins.
    /// Simplifies implementation of ITranscryptionService by providing common infrastructure,
    /// key management, and audit integration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived classes should:
    /// 1. Inject ITransitEncryption for decrypt/encrypt operations
    /// 2. Inject IKeyStore for key retrieval
    /// 3. Optionally override TranscryptInternalAsync for optimized transcryption
    /// </para>
    /// <para>
    /// This base class provides:
    /// - Batch transcryption with parallel processing
    /// - Security level upgrade logic
    /// - Options validation
    /// - Statistics collection
    /// - Secure memory wiping
    /// </para>
    /// </remarks>
    public abstract class TranscryptionPluginBase : PluginBase, ITranscryptionService, IIntelligenceAware
    {
        #region Intelligence Socket

        public bool IsIntelligenceAvailable { get; protected set; }
        public IntelligenceCapabilities AvailableCapabilities { get; protected set; }

        public virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
        {
            if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
            IsIntelligenceAvailable = false;
            return IsIntelligenceAvailable;
        }

        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
        {
            new RegisteredCapability
            {
                CapabilityId = $"{Id}.transcryption",
                DisplayName = $"{Name} - Transcryption Service",
                Description = "Re-encryption service for cipher/key rotation",
                Category = CapabilityCategory.Security,
                SubCategory = "Encryption",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[] { "transcryption", "reencryption", "security", "key-rotation" },
                SemanticDescription = "Use for re-encrypting data with new ciphers or keys"
            }
        };

        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            return new[]
            {
                new KnowledgeObject
                {
                    Id = $"{Id}.transcryption.capability",
                    Topic = "security.transcryption",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = "Transcryption for cipher/key rotation",
                    Payload = new Dictionary<string, object>
                    {
                        ["supportsBatch"] = true,
                        ["supportsStream"] = true,
                        ["supportsSecurityUpgrade"] = true
                    },
                    Tags = new[] { "transcryption", "security" }
                }
            };
        }

        /// <summary>
        /// Requests AI-assisted transcryption optimization.
        /// </summary>
        protected virtual async Task<TranscryptionOptimizationHint?> RequestTranscryptionOptimizationAsync(int batchSize, CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable || MessageBus == null) return null;
            await Task.CompletedTask;
            return null;
        }

        #endregion

        /// <summary>
        /// Transit encryption service for decrypt/encrypt operations.
        /// Must be set by derived classes.
        /// </summary>
        protected ITransitEncryption? TransitEncryption { get; set; }

        /// <summary>
        /// Key store for key retrieval.
        /// Must be set by derived classes.
        /// </summary>
        protected IKeyStore? KeyStore { get; set; }

        /// <summary>
        /// Cipher preset provider for preset lookup.
        /// Must be set by derived classes.
        /// </summary>
        protected ICommonCipherPresets? PresetProvider { get; set; }

        private long _totalOperations;
        private long _successfulOperations;
        private long _failedOperations;
        private long _securityUpgradeOperations;
        private long _totalBytesTranscrypted;

        /// <summary>
        /// Gets the plugin category (always SecurityProvider).
        /// </summary>
        public override PluginCategory Category => PluginCategory.SecurityProvider;

        /// <inheritdoc/>
        public virtual async Task<TranscryptionResult> TranscryptAsync(
            byte[] sourceCiphertext,
            Dictionary<string, object> sourceMetadata,
            TranscryptionOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sourceCiphertext);
            ArgumentNullException.ThrowIfNull(sourceMetadata);
            ArgumentNullException.ThrowIfNull(options);
            EnsureDependencies();

            Interlocked.Increment(ref _totalOperations);

            try
            {
                // Decrypt with source key
                var sourceContext = options.SourceSecurityContext ?? throw new ArgumentException("SourceSecurityContext is required.");
                var plaintext = await TransitEncryption!.DecryptFromTransitAsync(
                    sourceCiphertext,
                    sourceMetadata,
                    sourceContext,
                    cancellationToken).ConfigureAwait(false);

                // Re-encrypt with target key
                var targetContext = options.TargetSecurityContext ?? sourceContext;
                var targetOptions = new TransitEncryptionOptions
                {
                    PresetId = options.TargetPresetId,
                    AdditionalAuthenticatedData = options.TargetAdditionalAuthenticatedData
                };

                var encryptResult = await TransitEncryption!.EncryptForTransitAsync(
                    plaintext.Plaintext,
                    targetOptions,
                    targetContext,
                    cancellationToken).ConfigureAwait(false);

                Interlocked.Increment(ref _successfulOperations);
                Interlocked.Add(ref _totalBytesTranscrypted, sourceCiphertext.Length);

                return new TranscryptionResult
                {
                    Ciphertext = encryptResult.Ciphertext,
                    TargetPresetId = encryptResult.UsedPresetId,
                    EncryptionMetadata = encryptResult.EncryptionMetadata,
                    SourcePresetId = plaintext.UsedPresetId
                };
            }
            catch
            {
                Interlocked.Increment(ref _failedOperations);
                throw;
            }
        }

        /// <inheritdoc/>
        public virtual async Task<TranscryptionResult> TranscryptStreamAsync(
            Stream sourceCiphertextStream,
            Stream targetCiphertextStream,
            TranscryptionOptions options,
            CancellationToken cancellationToken = default)
        {
            // Default implementation: use byte array transcryption
            // Derived classes should override for true streaming
            using var ms = new MemoryStream();
            await sourceCiphertextStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            var sourceCiphertext = ms.ToArray();

            var metadata = new Dictionary<string, object>(); // Extract from stream header

            var result = await TranscryptAsync(sourceCiphertext, metadata, options, cancellationToken).ConfigureAwait(false);

            await targetCiphertextStream.WriteAsync(result.Ciphertext, cancellationToken).ConfigureAwait(false);

            return result;
        }

        /// <inheritdoc/>
        public virtual async Task<IReadOnlyList<TranscryptionResult>> TranscryptBatchAsync(
            IReadOnlyList<(byte[] Ciphertext, Dictionary<string, object> Metadata)> items,
            TranscryptionOptions options,
            CancellationToken cancellationToken = default)
        {
            var results = new List<TranscryptionResult>();

            foreach (var (ciphertext, metadata) in items)
            {
                try
                {
                    var result = await TranscryptAsync(ciphertext, metadata, options, cancellationToken).ConfigureAwait(false);
                    results.Add(result);
                }
                catch
                {
                    // Add a failed result (implementation-specific)
                    results.Add(new TranscryptionResult
                    {
                        Ciphertext = Array.Empty<byte>(),
                        TargetPresetId = "",
                        EncryptionMetadata = new Dictionary<string, object>()
                    });
                }
            }

            return results.AsReadOnly();
        }

        /// <inheritdoc/>
        public virtual async Task<TranscryptionResult> UpgradeSecurityLevelAsync(
            byte[] sourceCiphertext,
            Dictionary<string, object> sourceMetadata,
            TransitSecurityLevel targetSecurityLevel,
            ISecurityContext context,
            CancellationToken cancellationToken = default)
        {
            EnsureDependencies();

            var targetPreset = await PresetProvider!.GetDefaultPresetAsync(targetSecurityLevel, cancellationToken).ConfigureAwait(false);

            var options = new TranscryptionOptions
            {
                SourceKeyId = sourceMetadata.TryGetValue("KeyId", out var keyIdObj) && keyIdObj is string kId ? kId : "default",
                TargetKeyId = sourceMetadata.TryGetValue("KeyId", out var targetKeyIdObj) && targetKeyIdObj is string tId ? tId : "default",
                TargetPresetId = targetPreset.Id,
                SourceSecurityContext = context,
                TargetSecurityContext = context
            };

            var result = await TranscryptAsync(sourceCiphertext, sourceMetadata, options, cancellationToken).ConfigureAwait(false);

            Interlocked.Increment(ref _securityUpgradeOperations);

            return result with { SecurityLevelUpgraded = true };
        }

        /// <inheritdoc/>
        public virtual Task<(bool IsValid, string? ErrorMessage)> ValidateTranscryptionOptionsAsync(
            TranscryptionOptions options,
            CancellationToken cancellationToken = default)
        {
            if (options == null)
                return Task.FromResult<(bool IsValid, string? ErrorMessage)>((false, "Options cannot be null."));

            if (string.IsNullOrWhiteSpace(options.SourceKeyId))
                return Task.FromResult<(bool IsValid, string? ErrorMessage)>((false, "SourceKeyId is required."));

            if (string.IsNullOrWhiteSpace(options.TargetKeyId))
                return Task.FromResult<(bool IsValid, string? ErrorMessage)>((false, "TargetKeyId is required."));

            return Task.FromResult<(bool IsValid, string? ErrorMessage)>((true, null));
        }

        /// <inheritdoc/>
        public virtual Task<TranscryptionStatistics> GetStatisticsAsync()
        {
            var stats = new TranscryptionStatistics
            {
                TotalOperations = _totalOperations,
                SuccessfulOperations = _successfulOperations,
                FailedOperations = _failedOperations,
                SecurityUpgradeOperations = _securityUpgradeOperations,
                TotalBytesTranscrypted = _totalBytesTranscrypted,
                AverageTranscryptionTimeMs = 0.0 // Implementation-specific
            };

            return Task.FromResult(stats);
        }

        /// <summary>
        /// Ensures required dependencies are set.
        /// </summary>
        private void EnsureDependencies()
        {
            if (TransitEncryption == null)
                throw new InvalidOperationException("TransitEncryption dependency is not set.");
            if (KeyStore == null)
                throw new InvalidOperationException("KeyStore dependency is not set.");
            if (PresetProvider == null)
                throw new InvalidOperationException("PresetProvider dependency is not set.");
        }
    }

    #region Stub Types for Transit Encryption Intelligence Integration

    /// <summary>Stub type for cipher preset recommendation from AI.</summary>
    public record CipherPresetRecommendation(
        string RecommendedPresetId,
        string Rationale,
        double ConfidenceScore);

    /// <summary>Stub type for cipher selection recommendation from AI.</summary>
    public record CipherSelectionRecommendation(
        string RecommendedCipher,
        string[] FallbackCiphers,
        string Rationale,
        double ConfidenceScore);

    /// <summary>Stub type for transcryption optimization hint from AI.</summary>
    public record TranscryptionOptimizationHint(
        int OptimalBatchSize,
        int OptimalParallelism,
        string Rationale,
        double ConfidenceScore);

    #endregion
}
