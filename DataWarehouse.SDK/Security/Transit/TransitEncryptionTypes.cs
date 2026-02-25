namespace DataWarehouse.SDK.Security.Transit
{
    /// <summary>
    /// Represents a cipher preset configuration for transit encryption.
    /// Provides predefined combinations of encryption algorithms and security settings.
    /// </summary>
    /// <param name="Id">Unique identifier for the preset (e.g., "standard-aes256gcm").</param>
    /// <param name="Name">Human-readable name of the preset.</param>
    /// <param name="Algorithms">List of algorithms included in this preset (e.g., ["AES-256-GCM", "HMAC-SHA256"]).</param>
    /// <param name="SecurityLevel">Security level classification of this preset.</param>
    /// <param name="Description">Detailed description of the preset and its use cases.</param>
    /// <param name="Metadata">Additional metadata about the preset (performance characteristics, compliance certifications, etc.).</param>
    public record CipherPreset(
        string Id,
        string Name,
        IReadOnlyList<string> Algorithms,
        TransitSecurityLevel SecurityLevel,
        string Description = "",
        IReadOnlyDictionary<string, object>? Metadata = null
    );

    /// <summary>
    /// Defines the mode of operation for transit encryption.
    /// Controls when and how encryption is applied during data transfer.
    /// </summary>
    public enum TransitEncryptionMode
    {
        /// <summary>
        /// Always encrypt data during transit, regardless of transport security.
        /// Provides defense-in-depth even over TLS/HTTPS connections.
        /// </summary>
        AlwaysEncrypt,

        /// <summary>
        /// Opportunistic encryption: encrypt only when endpoint supports it.
        /// Falls back to transport-level security (TLS) if endpoint doesn't support transit encryption.
        /// </summary>
        Opportunistic,

        /// <summary>
        /// No transit encryption - rely on transport-level security only (TLS/HTTPS).
        /// Use only in trusted networks or when performance is critical.
        /// </summary>
        None
    }

    /// <summary>
    /// Represents the capabilities of a remote endpoint for transit encryption negotiation.
    /// Used during cipher negotiation to select mutually supported algorithms.
    /// </summary>
    /// <param name="SupportedCipherPresets">List of cipher preset IDs supported by the endpoint.</param>
    /// <param name="SupportedAlgorithms">List of individual algorithm names supported (e.g., "AES-256-GCM", "ChaCha20-Poly1305").</param>
    /// <param name="PreferredPresetId">The endpoint's preferred cipher preset ID, if any.</param>
    /// <param name="MaximumSecurityLevel">Maximum security level the endpoint can handle (may impact performance).</param>
    /// <param name="SupportsTranscryption">Whether the endpoint supports transcryption (re-encryption during transit).</param>
    /// <param name="Metadata">Additional capability metadata (protocol version, extensions, etc.).</param>
    public record EndpointCapabilities(
        IReadOnlyList<string> SupportedCipherPresets,
        IReadOnlyList<string> SupportedAlgorithms,
        string? PreferredPresetId = null,
        TransitSecurityLevel MaximumSecurityLevel = TransitSecurityLevel.High,
        bool SupportsTranscryption = false,
        IReadOnlyDictionary<string, object>? Metadata = null
    );

    /// <summary>
    /// Classification of transit encryption security levels.
    /// Higher levels provide stronger security but may impact performance.
    /// </summary>
    public enum TransitSecurityLevel
    {
        /// <summary>
        /// Standard security: AES-256-GCM or ChaCha20-Poly1305.
        /// Suitable for most applications and compliance requirements.
        /// </summary>
        Standard = 0,

        /// <summary>
        /// High security: AES-256-GCM with HMAC-SHA512 for additional integrity protection.
        /// Suitable for financial, healthcare, and sensitive government data.
        /// </summary>
        High = 1,

        /// <summary>
        /// Military-grade security: AES-256-GCM with Suite B algorithms.
        /// Suitable for classified government communications and defense contractors.
        /// Requires FIPS 140-2 Level 3+ certified cryptographic modules.
        /// </summary>
        Military = 2,

        /// <summary>
        /// Quantum-safe security: Hybrid post-quantum cryptography (Kyber + Dilithium).
        /// Future-proof against quantum computer attacks.
        /// Suitable for long-term sensitive data and forward-looking compliance.
        /// </summary>
        QuantumSafe = 3
    }

    /// <summary>
    /// Options for configuring transit encryption operations.
    /// </summary>
    public record TransitEncryptionOptions
    {
        /// <summary>
        /// The cipher preset ID to use for encryption.
        /// If null, the default preset for the security level will be selected.
        /// </summary>
        public string? PresetId { get; init; }

        /// <summary>
        /// The desired security level for this operation.
        /// Used to select an appropriate preset if PresetId is not specified.
        /// </summary>
        public TransitSecurityLevel SecurityLevel { get; init; } = TransitSecurityLevel.Standard;

        /// <summary>
        /// Encryption mode for this operation.
        /// </summary>
        public TransitEncryptionMode Mode { get; init; } = TransitEncryptionMode.AlwaysEncrypt;

        /// <summary>
        /// Remote endpoint capabilities for cipher negotiation.
        /// If provided, the best mutually supported cipher will be selected.
        /// </summary>
        public EndpointCapabilities? RemoteCapabilities { get; init; }

        /// <summary>
        /// Additional authenticated data (AAD) to include in encryption.
        /// Not encrypted but authenticated as part of the ciphertext.
        /// Useful for including metadata like request IDs, timestamps, etc.
        /// </summary>
        public byte[]? AdditionalAuthenticatedData { get; init; }

        /// <summary>
        /// Whether to compress data before encryption.
        /// Can improve performance for large payloads but may leak information about plaintext size.
        /// </summary>
        public bool CompressBeforeEncryption { get; init; } = false;

        /// <summary>
        /// Additional options for specific algorithms or plugins.
        /// </summary>
        public Dictionary<string, object>? ExtendedOptions { get; init; }
    }

    /// <summary>
    /// Result of a transit encryption operation.
    /// </summary>
    public record TransitEncryptionResult
    {
        /// <summary>
        /// The encrypted ciphertext.
        /// </summary>
        public required byte[] Ciphertext { get; init; }

        /// <summary>
        /// The cipher preset ID that was actually used for encryption.
        /// May differ from requested if negotiation occurred.
        /// </summary>
        public required string UsedPresetId { get; init; }

        /// <summary>
        /// Metadata about the encryption operation (IV, nonce, tag location, etc.).
        /// Needed for decryption.
        /// </summary>
        public required Dictionary<string, object> EncryptionMetadata { get; init; }

        /// <summary>
        /// Timestamp when encryption was performed (UTC).
        /// </summary>
        public DateTime EncryptedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Whether compression was applied before encryption.
        /// </summary>
        public bool WasCompressed { get; init; }
    }

    /// <summary>
    /// Result of a transit decryption operation.
    /// </summary>
    public record TransitDecryptionResult
    {
        /// <summary>
        /// The decrypted plaintext.
        /// </summary>
        public required byte[] Plaintext { get; init; }

        /// <summary>
        /// The cipher preset ID that was used for decryption.
        /// </summary>
        public required string UsedPresetId { get; init; }

        /// <summary>
        /// Whether the data was decompressed after decryption.
        /// </summary>
        public bool WasDecompressed { get; init; }

        /// <summary>
        /// Original encryption timestamp (UTC).
        /// </summary>
        public DateTime? EncryptedAt { get; init; }
    }

    /// <summary>
    /// Options for transcryption operations (re-encryption during transit).
    /// </summary>
    public record TranscryptionOptions
    {
        /// <summary>
        /// Source key ID for decryption (Key A).
        /// </summary>
        public required string SourceKeyId { get; init; }

        /// <summary>
        /// Target key ID for re-encryption (Key B).
        /// </summary>
        public required string TargetKeyId { get; init; }

        /// <summary>
        /// Source cipher preset ID for decryption.
        /// If null, will be determined from ciphertext metadata.
        /// </summary>
        public string? SourcePresetId { get; init; }

        /// <summary>
        /// Target cipher preset ID for re-encryption.
        /// If null, will use the same preset as source.
        /// </summary>
        public string? TargetPresetId { get; init; }

        /// <summary>
        /// Whether to verify integrity before transcryption.
        /// Recommended for security-critical operations.
        /// </summary>
        public bool VerifyIntegrity { get; init; } = true;

        /// <summary>
        /// Additional authenticated data (AAD) for the target encryption.
        /// </summary>
        public byte[]? TargetAdditionalAuthenticatedData { get; init; }

        /// <summary>
        /// Security context for accessing source key.
        /// </summary>
        public ISecurityContext? SourceSecurityContext { get; init; }

        /// <summary>
        /// Security context for accessing target key.
        /// </summary>
        public ISecurityContext? TargetSecurityContext { get; init; }
    }

    /// <summary>
    /// Result of a transcryption operation.
    /// </summary>
    public record TranscryptionResult
    {
        /// <summary>
        /// The re-encrypted ciphertext.
        /// </summary>
        public required byte[] Ciphertext { get; init; }

        /// <summary>
        /// The target cipher preset ID that was used for re-encryption.
        /// </summary>
        public required string TargetPresetId { get; init; }

        /// <summary>
        /// Metadata about the new encryption operation.
        /// </summary>
        public required Dictionary<string, object> EncryptionMetadata { get; init; }

        /// <summary>
        /// Timestamp when transcryption was performed (UTC).
        /// </summary>
        public DateTime TranscryptedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Whether the operation upgraded the security level.
        /// </summary>
        public bool SecurityLevelUpgraded { get; init; }

        /// <summary>
        /// The source preset ID that was used for decryption.
        /// </summary>
        public string? SourcePresetId { get; init; }
    }

    /// <summary>
    /// Result of cipher negotiation between endpoints.
    /// </summary>
    public record CipherNegotiationResult
    {
        /// <summary>
        /// The negotiated cipher preset ID.
        /// Null if no mutually supported cipher was found.
        /// </summary>
        public string? NegotiatedPresetId { get; init; }

        /// <summary>
        /// Whether negotiation was successful.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// The negotiated security level.
        /// </summary>
        public TransitSecurityLevel NegotiatedSecurityLevel { get; init; }

        /// <summary>
        /// List of mutually supported cipher preset IDs (in preference order).
        /// </summary>
        public IReadOnlyList<string> MutuallySupportedPresets { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Error message if negotiation failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Metadata about the negotiation process.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }
}
