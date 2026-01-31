namespace DataWarehouse.SDK.Security
{
    /// <summary>
    /// Determines how encryption keys are managed.
    /// User-configurable option for all encryption plugins.
    /// </summary>
    public enum KeyManagementMode
    {
        /// <summary>
        /// Direct mode (DEFAULT): Key is retrieved directly from any IKeyStore.
        /// Works with: FileKeyStorePlugin, VaultKeyStorePlugin, KeyRotationPlugin, etc.
        /// The same key is used for all operations with the same key ID.
        /// </summary>
        Direct,

        /// <summary>
        /// Envelope mode: A unique Data Encryption Key (DEK) is generated per object,
        /// wrapped by a Key Encryption Key (KEK) via HSM, and stored in the ciphertext header.
        /// Requires IEnvelopeKeyStore implementation (e.g., VaultKeyStorePlugin with HSM backend).
        /// Provides additional security: even if wrapped DEK is compromised, KEK in HSM protects actual data.
        /// </summary>
        Envelope
    }

    /// <summary>
    /// Determines how encryption configuration is managed for a storage context.
    /// Applies to BOTH tamper-proof storage (manifest) and standalone encryption (ciphertext header).
    /// </summary>
    public enum EncryptionConfigMode
    {
        /// <summary>
        /// DEFAULT: Each object stores its own EncryptionMetadata.
        /// - Tamper-proof: EncryptionMetadata stored in TamperProofManifest
        /// - Standalone: EncryptionMetadata stored in ciphertext header
        /// Use case: Multi-tenant deployments, mixed compliance requirements.
        /// </summary>
        PerObjectConfig,

        /// <summary>
        /// All objects MUST use the same encryption configuration.
        /// Configuration is sealed after first write - cannot be changed.
        /// Any write with different config will be rejected.
        /// Use case: Single-tenant deployments, strict compliance (all data same encryption).
        /// </summary>
        FixedConfig,

        /// <summary>
        /// Per-object configuration allowed, but must satisfy tenant/org policy.
        /// Policy defines: allowed encryption algorithms, required key modes, allowed key stores.
        /// Writes that violate policy are rejected with detailed error.
        /// Use case: Enterprise with compliance rules but per-user flexibility within bounds.
        /// </summary>
        PolicyEnforced
    }

    /// <summary>
    /// Core interface for key management operations.
    /// All key store plugins must implement this interface.
    /// </summary>
    public interface IKeyStore
    {
        /// <summary>
        /// Gets the current active key ID for encryption operations.
        /// Returns the most recently created or rotated key.
        /// </summary>
        /// <returns>The current key identifier.</returns>
        Task<string> GetCurrentKeyIdAsync();

        /// <summary>
        /// Synchronously retrieves a key by ID.
        /// LEGACY: Prefer GetKeyAsync for new implementations.
        /// </summary>
        /// <param name="keyId">The key identifier.</param>
        /// <returns>The key bytes.</returns>
        byte[] GetKey(string keyId);

        /// <summary>
        /// Asynchronously retrieves or creates an encryption key for the specified ID.
        /// Performs strict ACL checks against the provided security context.
        /// </summary>
        /// <param name="keyId">The key identifier.</param>
        /// <param name="context">Security context for ACL validation.</param>
        /// <returns>The key bytes.</returns>
        Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context);

        /// <summary>
        /// Creates or rotates a key with the specified ID.
        /// Requires appropriate permissions in the security context.
        /// </summary>
        /// <param name="keyId">The key identifier.</param>
        /// <param name="context">Security context for ACL validation.</param>
        /// <returns>The newly created key bytes.</returns>
        Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context);
    }

    /// <summary>
    /// Extended key store interface that supports envelope encryption operations.
    /// Required for KeyManagementMode.Envelope.
    /// Implementations typically wrap HSM backends (Azure Key Vault, AWS KMS, HashiCorp Vault Transit).
    /// </summary>
    public interface IEnvelopeKeyStore : IKeyStore
    {
        /// <summary>
        /// Wraps a Data Encryption Key (DEK) with a Key Encryption Key (KEK).
        /// The KEK never leaves the HSM - only the wrapped result is returned.
        /// </summary>
        /// <param name="kekId">The Key Encryption Key identifier in the HSM.</param>
        /// <param name="dataKey">The plaintext Data Encryption Key to wrap.</param>
        /// <param name="context">Security context for ACL validation.</param>
        /// <returns>The wrapped (encrypted) DEK bytes.</returns>
        Task<byte[]> WrapKeyAsync(string kekId, byte[] dataKey, ISecurityContext context);

        /// <summary>
        /// Unwraps a previously wrapped DEK using the KEK in the HSM.
        /// The KEK never leaves the HSM - decryption happens inside the HSM.
        /// </summary>
        /// <param name="kekId">The Key Encryption Key identifier in the HSM.</param>
        /// <param name="wrappedKey">The wrapped DEK bytes to unwrap.</param>
        /// <param name="context">Security context for ACL validation.</param>
        /// <returns>The unwrapped (plaintext) DEK bytes.</returns>
        Task<byte[]> UnwrapKeyAsync(string kekId, byte[] wrappedKey, ISecurityContext context);

        /// <summary>
        /// Gets the list of supported wrapping algorithms for this key store.
        /// Common algorithms: AES-256-GCM, RSA-OAEP-256, etc.
        /// </summary>
        IReadOnlyList<string> SupportedWrappingAlgorithms { get; }

        /// <summary>
        /// Gets whether this envelope key store supports key generation inside the HSM.
        /// If true, keys can be generated that never leave the HSM boundary.
        /// </summary>
        bool SupportsHsmKeyGeneration { get; }
    }

    /// <summary>
    /// Registry for resolving key store plugin IDs to instances.
    /// Used by IKeyManagementConfigProvider to resolve stored plugin IDs.
    /// Typically populated during kernel initialization.
    /// </summary>
    public interface IKeyStoreRegistry
    {
        /// <summary>
        /// Registers a key store plugin with its ID.
        /// </summary>
        /// <param name="pluginId">The unique plugin identifier.</param>
        /// <param name="keyStore">The key store instance.</param>
        void Register(string pluginId, IKeyStore keyStore);

        /// <summary>
        /// Registers an envelope-capable key store plugin.
        /// </summary>
        /// <param name="pluginId">The unique plugin identifier.</param>
        /// <param name="envelopeKeyStore">The envelope key store instance.</param>
        void RegisterEnvelope(string pluginId, IEnvelopeKeyStore envelopeKeyStore);

        /// <summary>
        /// Gets a key store by plugin ID.
        /// </summary>
        /// <param name="pluginId">The plugin identifier, or null to return null.</param>
        /// <returns>The key store instance, or null if not found.</returns>
        IKeyStore? GetKeyStore(string? pluginId);

        /// <summary>
        /// Gets an envelope key store by plugin ID.
        /// </summary>
        /// <param name="pluginId">The plugin identifier, or null to return null.</param>
        /// <returns>The envelope key store instance, or null if not found.</returns>
        IEnvelopeKeyStore? GetEnvelopeKeyStore(string? pluginId);

        /// <summary>
        /// Gets all registered key store plugin IDs.
        /// </summary>
        IReadOnlyList<string> GetRegisteredKeyStoreIds();

        /// <summary>
        /// Gets all registered envelope key store plugin IDs.
        /// </summary>
        IReadOnlyList<string> GetRegisteredEnvelopeKeyStoreIds();
    }

    /// <summary>
    /// Resolves key management configuration per-user/per-tenant.
    /// Implement this to store user preferences in database, config files, etc.
    /// </summary>
    public interface IKeyManagementConfigProvider
    {
        /// <summary>
        /// Gets key management configuration for a user/tenant.
        /// Returns null if no preferences stored (use defaults).
        /// </summary>
        /// <param name="context">The security context identifying the user/tenant.</param>
        /// <returns>The configuration, or null to use defaults.</returns>
        Task<KeyManagementConfig?> GetConfigAsync(ISecurityContext context);

        /// <summary>
        /// Saves user preferences for key management.
        /// </summary>
        /// <param name="context">The security context identifying the user/tenant.</param>
        /// <param name="config">The configuration to save.</param>
        Task SaveConfigAsync(ISecurityContext context, KeyManagementConfig config);

        /// <summary>
        /// Deletes saved configuration for a user/tenant.
        /// </summary>
        /// <param name="context">The security context identifying the user/tenant.</param>
        /// <returns>True if configuration was deleted, false if none existed.</returns>
        Task<bool> DeleteConfigAsync(ISecurityContext context);
    }

    /// <summary>
    /// User-specific key management configuration.
    /// Stored per-user/per-tenant to enable different encryption settings for different users.
    /// </summary>
    public record KeyManagementConfig
    {
        /// <summary>
        /// Key management mode: Direct or Envelope.
        /// </summary>
        public KeyManagementMode Mode { get; init; } = KeyManagementMode.Direct;

        /// <summary>
        /// Key store plugin ID for Direct mode.
        /// Used to resolve the key store from IKeyStoreRegistry.
        /// </summary>
        public string? KeyStorePluginId { get; init; }

        /// <summary>
        /// Key store instance for Direct mode (alternative to plugin ID).
        /// Takes precedence over KeyStorePluginId if both are set.
        /// </summary>
        public IKeyStore? KeyStore { get; init; }

        /// <summary>
        /// Specific key ID to use (optional).
        /// If not set, the key store's current key is used.
        /// </summary>
        public string? KeyId { get; init; }

        /// <summary>
        /// Envelope key store plugin ID for Envelope mode.
        /// Used to resolve the envelope key store from IKeyStoreRegistry.
        /// </summary>
        public string? EnvelopeKeyStorePluginId { get; init; }

        /// <summary>
        /// Envelope key store instance for Envelope mode (alternative to plugin ID).
        /// Takes precedence over EnvelopeKeyStorePluginId if both are set.
        /// </summary>
        public IEnvelopeKeyStore? EnvelopeKeyStore { get; init; }

        /// <summary>
        /// Key Encryption Key (KEK) identifier for Envelope mode.
        /// Must be a valid key ID in the HSM.
        /// </summary>
        public string? KekKeyId { get; init; }

        /// <summary>
        /// Preferred encryption algorithm/plugin (for systems with multiple).
        /// E.g., "aes256gcm", "chacha20poly1305".
        /// </summary>
        public string? PreferredEncryptionPluginId { get; init; }

        /// <summary>
        /// Additional configuration options.
        /// </summary>
        public Dictionary<string, object>? Options { get; init; }
    }

    /// <summary>
    /// Resolved configuration for a single operation (after applying priority rules).
    /// This is the internal representation used by EncryptionPluginBase.
    /// </summary>
    public record ResolvedKeyManagementConfig
    {
        /// <summary>
        /// The resolved key management mode.
        /// </summary>
        public KeyManagementMode Mode { get; init; }

        /// <summary>
        /// The resolved key store for Direct mode.
        /// </summary>
        public IKeyStore? KeyStore { get; init; }

        /// <summary>
        /// The resolved key ID for Direct mode.
        /// </summary>
        public string? KeyId { get; init; }

        /// <summary>
        /// The resolved envelope key store for Envelope mode.
        /// </summary>
        public IEnvelopeKeyStore? EnvelopeKeyStore { get; init; }

        /// <summary>
        /// The resolved KEK key ID for Envelope mode.
        /// </summary>
        public string? KekKeyId { get; init; }

        /// <summary>
        /// The key store plugin ID (for metadata storage).
        /// </summary>
        public string? KeyStorePluginId { get; init; }

        /// <summary>
        /// The envelope key store plugin ID (for metadata storage).
        /// </summary>
        public string? EnvelopeKeyStorePluginId { get; init; }
    }

    /// <summary>
    /// Metadata stored with encrypted data to enable decryption.
    /// Stored in TamperProofManifest.EncryptionMetadata or embedded in ciphertext header.
    /// </summary>
    public record EncryptionMetadata
    {
        /// <summary>
        /// Encryption plugin ID used (e.g., "aes256gcm", "chacha20poly1305", "twofish256").
        /// </summary>
        public string EncryptionPluginId { get; init; } = "";

        /// <summary>
        /// Key management mode used: Direct or Envelope.
        /// </summary>
        public KeyManagementMode KeyMode { get; init; }

        /// <summary>
        /// For Direct mode: Key ID in the key store.
        /// </summary>
        public string? KeyId { get; init; }

        /// <summary>
        /// For Envelope mode: Wrapped DEK (encrypted by HSM).
        /// </summary>
        public byte[]? WrappedDek { get; init; }

        /// <summary>
        /// For Envelope mode: KEK identifier in HSM.
        /// </summary>
        public string? KekId { get; init; }

        /// <summary>
        /// Key store plugin ID used (for verification/routing on read).
        /// </summary>
        public string? KeyStorePluginId { get; init; }

        /// <summary>
        /// Algorithm-specific parameters (IV, nonce, tag location, AAD, etc.).
        /// </summary>
        public Dictionary<string, object> AlgorithmParams { get; init; } = new();

        /// <summary>
        /// Timestamp when encryption was performed (UTC).
        /// </summary>
        public DateTime EncryptedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// User/tenant who encrypted (for audit trail).
        /// </summary>
        public string? EncryptedBy { get; init; }

        /// <summary>
        /// Optional encryption algorithm version for forward compatibility.
        /// </summary>
        public int Version { get; init; } = 1;
    }

    /// <summary>
    /// Envelope header stored at the beginning of encrypted data in Envelope mode.
    /// Contains the wrapped DEK and metadata needed for decryption.
    /// </summary>
    public class EnvelopeHeader
    {
        /// <summary>
        /// Magic bytes to identify envelope-encrypted data: "DWENV" (0x44 0x57 0x45 0x4E 0x56).
        /// </summary>
        public static readonly byte[] MagicBytes = { 0x44, 0x57, 0x45, 0x4E, 0x56 };

        /// <summary>
        /// Current envelope header version.
        /// </summary>
        public const int CurrentVersion = 1;

        /// <summary>
        /// Header version for compatibility.
        /// </summary>
        public int Version { get; set; } = CurrentVersion;

        /// <summary>
        /// KEK identifier used to wrap the DEK.
        /// </summary>
        public string KekId { get; set; } = "";

        /// <summary>
        /// Key store plugin ID that holds the KEK.
        /// </summary>
        public string KeyStorePluginId { get; set; } = "";

        /// <summary>
        /// The wrapped (encrypted) Data Encryption Key.
        /// </summary>
        public byte[] WrappedDek { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Wrapping algorithm used (e.g., "AES-256-GCM", "RSA-OAEP-256").
        /// </summary>
        public string WrappingAlgorithm { get; set; } = "AES-256-GCM";

        /// <summary>
        /// Initialization vector used for encryption.
        /// </summary>
        public byte[] Iv { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Encryption algorithm used for the payload.
        /// </summary>
        public string EncryptionAlgorithm { get; set; } = "";

        /// <summary>
        /// Encryption plugin ID.
        /// </summary>
        public string EncryptionPluginId { get; set; } = "";

        /// <summary>
        /// Timestamp when encrypted (UTC ticks).
        /// </summary>
        public long EncryptedAtTicks { get; set; }

        /// <summary>
        /// User who encrypted (for audit).
        /// </summary>
        public string? EncryptedBy { get; set; }

        /// <summary>
        /// Additional metadata as key-value pairs.
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>
        /// Serializes the header to bytes for storage.
        /// Format: MagicBytes(5) + Version(4) + HeaderLength(4) + HeaderJson(variable)
        /// </summary>
        public byte[] Serialize()
        {
            var json = System.Text.Json.JsonSerializer.Serialize(this);
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(MagicBytes);
            writer.Write(Version);
            writer.Write(jsonBytes.Length);
            writer.Write(jsonBytes);

            return ms.ToArray();
        }

        /// <summary>
        /// Deserializes a header from bytes.
        /// </summary>
        /// <param name="data">The byte array starting with the header.</param>
        /// <param name="header">The deserialized header.</param>
        /// <param name="headerLength">The total length of the header in bytes.</param>
        /// <returns>True if successfully deserialized.</returns>
        public static bool TryDeserialize(byte[] data, out EnvelopeHeader? header, out int headerLength)
        {
            header = null;
            headerLength = 0;

            if (data.Length < MagicBytes.Length + 8) // Magic + Version + Length
                return false;

            // Check magic bytes
            for (int i = 0; i < MagicBytes.Length; i++)
            {
                if (data[i] != MagicBytes[i])
                    return false;
            }

            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            reader.ReadBytes(MagicBytes.Length); // Skip magic
            var version = reader.ReadInt32();
            var jsonLength = reader.ReadInt32();

            if (data.Length < MagicBytes.Length + 8 + jsonLength)
                return false;

            var jsonBytes = reader.ReadBytes(jsonLength);
            var json = System.Text.Encoding.UTF8.GetString(jsonBytes);

            header = System.Text.Json.JsonSerializer.Deserialize<EnvelopeHeader>(json);
            headerLength = MagicBytes.Length + 8 + jsonLength;

            return header != null;
        }

        /// <summary>
        /// Checks if the data starts with an envelope header.
        /// </summary>
        public static bool IsEnvelopeEncrypted(byte[] data)
        {
            if (data.Length < MagicBytes.Length)
                return false;

            for (int i = 0; i < MagicBytes.Length; i++)
            {
                if (data[i] != MagicBytes[i])
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Checks if the stream starts with an envelope header (does not consume the stream).
        /// </summary>
        public static async Task<bool> IsEnvelopeEncryptedAsync(Stream stream)
        {
            if (!stream.CanSeek)
                return false;

            var originalPosition = stream.Position;
            var buffer = new byte[MagicBytes.Length];
            var bytesRead = await stream.ReadAsync(buffer, 0, MagicBytes.Length);
            stream.Position = originalPosition;

            if (bytesRead < MagicBytes.Length)
                return false;

            for (int i = 0; i < MagicBytes.Length; i++)
            {
                if (buffer[i] != MagicBytes[i])
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Default in-memory implementation of IKeyStoreRegistry.
    /// Thread-safe and suitable for single-process deployments.
    /// For distributed deployments, implement a custom registry backed by distributed cache.
    /// </summary>
    public class DefaultKeyStoreRegistry : IKeyStoreRegistry
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IKeyStore> _keyStores = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IEnvelopeKeyStore> _envelopeKeyStores = new();

        /// <inheritdoc/>
        public void Register(string pluginId, IKeyStore keyStore)
        {
            ArgumentNullException.ThrowIfNull(pluginId);
            ArgumentNullException.ThrowIfNull(keyStore);
            _keyStores[pluginId] = keyStore;

            // If it's also an envelope key store, register there too
            if (keyStore is IEnvelopeKeyStore envelopeKeyStore)
            {
                _envelopeKeyStores[pluginId] = envelopeKeyStore;
            }
        }

        /// <inheritdoc/>
        public void RegisterEnvelope(string pluginId, IEnvelopeKeyStore envelopeKeyStore)
        {
            ArgumentNullException.ThrowIfNull(pluginId);
            ArgumentNullException.ThrowIfNull(envelopeKeyStore);
            _envelopeKeyStores[pluginId] = envelopeKeyStore;
            _keyStores[pluginId] = envelopeKeyStore; // Also register as regular key store
        }

        /// <inheritdoc/>
        public IKeyStore? GetKeyStore(string? pluginId)
        {
            if (string.IsNullOrEmpty(pluginId))
                return null;

            return _keyStores.TryGetValue(pluginId, out var keyStore) ? keyStore : null;
        }

        /// <inheritdoc/>
        public IEnvelopeKeyStore? GetEnvelopeKeyStore(string? pluginId)
        {
            if (string.IsNullOrEmpty(pluginId))
                return null;

            return _envelopeKeyStores.TryGetValue(pluginId, out var keyStore) ? keyStore : null;
        }

        /// <inheritdoc/>
        public IReadOnlyList<string> GetRegisteredKeyStoreIds()
        {
            return _keyStores.Keys.ToList().AsReadOnly();
        }

        /// <inheritdoc/>
        public IReadOnlyList<string> GetRegisteredEnvelopeKeyStoreIds()
        {
            return _envelopeKeyStores.Keys.ToList().AsReadOnly();
        }

        /// <summary>
        /// Removes a key store registration.
        /// </summary>
        public bool Unregister(string pluginId)
        {
            var removed = _keyStores.TryRemove(pluginId, out _);
            _envelopeKeyStores.TryRemove(pluginId, out _);
            return removed;
        }

        /// <summary>
        /// Clears all registrations.
        /// </summary>
        public void Clear()
        {
            _keyStores.Clear();
            _envelopeKeyStores.Clear();
        }
    }

    /// <summary>
    /// In-memory implementation of IKeyManagementConfigProvider for testing.
    /// Thread-safe. For production, implement a database-backed provider.
    /// </summary>
    public class InMemoryKeyManagementConfigProvider : IKeyManagementConfigProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, KeyManagementConfig> _configs = new();

        /// <inheritdoc/>
        public Task<KeyManagementConfig?> GetConfigAsync(ISecurityContext context)
        {
            var key = GetConfigKey(context);
            _configs.TryGetValue(key, out var config);
            return Task.FromResult(config);
        }

        /// <inheritdoc/>
        public Task SaveConfigAsync(ISecurityContext context, KeyManagementConfig config)
        {
            var key = GetConfigKey(context);
            _configs[key] = config;
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<bool> DeleteConfigAsync(ISecurityContext context)
        {
            var key = GetConfigKey(context);
            return Task.FromResult(_configs.TryRemove(key, out _));
        }

        private static string GetConfigKey(ISecurityContext context)
        {
            // Use tenant ID if available, otherwise user ID
            return context.TenantId ?? context.UserId;
        }
    }

    /// <summary>
    /// Policy for validating encryption configurations in PolicyEnforced mode.
    /// </summary>
    public class EncryptionPolicy
    {
        /// <summary>
        /// Allowed key management modes. Empty means all modes allowed.
        /// </summary>
        public KeyManagementMode[] AllowedModes { get; init; } = Array.Empty<KeyManagementMode>();

        /// <summary>
        /// Allowed encryption plugin IDs. Empty means all plugins allowed.
        /// </summary>
        public string[] AllowedEncryptionPlugins { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Allowed key store plugin IDs. Empty means all key stores allowed.
        /// </summary>
        public string[] AllowedKeyStores { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Whether HSM-backed KEK is required for Envelope mode.
        /// </summary>
        public bool RequireHsmBackedKek { get; init; }

        /// <summary>
        /// Minimum key size in bits (0 = no minimum).
        /// </summary>
        public int MinimumKeySizeBits { get; init; }

        /// <summary>
        /// Validates a configuration against this policy.
        /// </summary>
        /// <param name="config">The configuration to validate.</param>
        /// <param name="encryptionPluginId">The encryption plugin ID being used.</param>
        /// <returns>Validation result with success flag and error message if failed.</returns>
        public (bool IsValid, string? ErrorMessage) Validate(ResolvedKeyManagementConfig config, string encryptionPluginId)
        {
            // Check allowed modes
            if (AllowedModes.Length > 0 && !AllowedModes.Contains(config.Mode))
            {
                return (false, $"Key management mode '{config.Mode}' is not allowed by policy. Allowed: {string.Join(", ", AllowedModes)}");
            }

            // Check allowed encryption plugins
            if (AllowedEncryptionPlugins.Length > 0 && !AllowedEncryptionPlugins.Contains(encryptionPluginId))
            {
                return (false, $"Encryption plugin '{encryptionPluginId}' is not allowed by policy. Allowed: {string.Join(", ", AllowedEncryptionPlugins)}");
            }

            // Check allowed key stores
            var keyStoreId = config.Mode == KeyManagementMode.Envelope
                ? config.EnvelopeKeyStorePluginId
                : config.KeyStorePluginId;

            if (AllowedKeyStores.Length > 0 && !string.IsNullOrEmpty(keyStoreId) && !AllowedKeyStores.Contains(keyStoreId))
            {
                return (false, $"Key store '{keyStoreId}' is not allowed by policy. Allowed: {string.Join(", ", AllowedKeyStores)}");
            }

            // Check HSM requirement for Envelope mode
            if (config.Mode == KeyManagementMode.Envelope && RequireHsmBackedKek)
            {
                if (config.EnvelopeKeyStore == null || !config.EnvelopeKeyStore.SupportsHsmKeyGeneration)
                {
                    return (false, "Policy requires HSM-backed KEK for Envelope mode, but the envelope key store does not support HSM key generation.");
                }
            }

            return (true, null);
        }
    }
}
