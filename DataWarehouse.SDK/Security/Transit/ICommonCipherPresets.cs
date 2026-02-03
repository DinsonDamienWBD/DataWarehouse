namespace DataWarehouse.SDK.Security.Transit
{
    /// <summary>
    /// Interface for cipher preset providers.
    /// Implementations provide predefined cipher configurations for transit encryption,
    /// categorized by security level and use case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cipher presets simplify transit encryption configuration by providing well-tested
    /// combinations of algorithms, key sizes, and security parameters.
    /// </para>
    /// <para>
    /// Standard implementations should provide:
    /// - StandardPresets: Common algorithms (AES-256-GCM, ChaCha20-Poly1305) for general use
    /// - HighSecurityPresets: Enhanced security with additional integrity protection (AES-256-GCM + HMAC-SHA512)
    /// - QuantumSafePresets: Post-quantum cryptography (Kyber + Dilithium hybrid)
    /// </para>
    /// <para>
    /// Plugins can extend presets for specific compliance requirements (FIPS 140-2, Suite B, etc.).
    /// </para>
    /// </remarks>
    public interface ICommonCipherPresets
    {
        /// <summary>
        /// Gets standard cipher presets for general-purpose transit encryption.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Standard presets typically include:
        /// - "standard-aes256gcm": AES-256-GCM (NIST approved, high performance)
        /// - "standard-chacha20poly1305": ChaCha20-Poly1305 (optimized for software implementations)
        /// - "standard-aes128gcm": AES-128-GCM (legacy compatibility, lower security)
        /// </para>
        /// <para>
        /// These presets are suitable for most applications and provide FIPS 140-2 Level 1 compliance.
        /// </para>
        /// </remarks>
        /// <returns>Read-only list of standard cipher presets.</returns>
        IReadOnlyList<CipherPreset> StandardPresets { get; }

        /// <summary>
        /// Gets high-security cipher presets for sensitive data and compliance requirements.
        /// </summary>
        /// <remarks>
        /// <para>
        /// High-security presets typically include:
        /// - "high-aes256gcm-sha512": AES-256-GCM with HMAC-SHA512 for dual-layer integrity
        /// - "high-aes256gcm-fips": AES-256-GCM with FIPS 140-2 Level 3 certified module
        /// - "high-suiteb": NSA Suite B algorithms (AES-256-GCM + ECDH P-384 + ECDSA P-384)
        /// </para>
        /// <para>
        /// These presets are suitable for:
        /// - Financial transactions (PCI-DSS)
        /// - Healthcare data (HIPAA)
        /// - Government classified data (up to SECRET level)
        /// - Defense contractors (CMMC Level 3+)
        /// </para>
        /// </remarks>
        /// <returns>Read-only list of high-security cipher presets.</returns>
        IReadOnlyList<CipherPreset> HighSecurityPresets { get; }

        /// <summary>
        /// Gets quantum-safe cipher presets using post-quantum cryptography.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Quantum-safe presets typically include:
        /// - "quantum-kyber1024-dilithium5": NIST PQC winners (highest security)
        /// - "quantum-kyber768-dilithium3": Balanced quantum resistance and performance
        /// - "quantum-hybrid-aes256gcm-kyber768": Hybrid classical + post-quantum
        /// </para>
        /// <para>
        /// These presets provide protection against future quantum computer attacks and are suitable for:
        /// - Long-term sensitive data (retention >10 years)
        /// - Forward-looking compliance requirements
        /// - Government TOP SECRET / SCI data
        /// - Critical infrastructure protection
        /// </para>
        /// <para>
        /// Note: Post-quantum algorithms have larger key sizes and slower performance than classical algorithms.
        /// </para>
        /// </remarks>
        /// <returns>Read-only list of quantum-safe cipher presets.</returns>
        IReadOnlyList<CipherPreset> QuantumSafePresets { get; }

        /// <summary>
        /// Gets a cipher preset by its unique identifier.
        /// </summary>
        /// <param name="presetId">The unique preset identifier (e.g., "standard-aes256gcm").</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// The requested cipher preset, or null if no preset exists with the specified ID.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method searches across all preset categories (Standard, HighSecurity, QuantumSafe)
        /// and returns the first match. Preset IDs must be unique across all categories.
        /// </para>
        /// <para>
        /// Use this method during cipher negotiation to validate endpoint-requested presets.
        /// </para>
        /// </remarks>
        Task<CipherPreset?> GetPresetAsync(string presetId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists all available cipher presets across all security levels.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Read-only list of all cipher presets, ordered by security level (Standard, High, QuantumSafe).
        /// </returns>
        /// <remarks>
        /// <para>
        /// Use this method for:
        /// - Endpoint capability advertisement during handshake
        /// - UI/CLI tools showing available encryption options
        /// - Compliance auditing and reporting
        /// </para>
        /// </remarks>
        Task<IReadOnlyList<CipherPreset>> ListPresetsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists cipher presets filtered by security level.
        /// </summary>
        /// <param name="securityLevel">The security level to filter by.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// Read-only list of cipher presets matching the specified security level.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Use this method to find appropriate presets for a given compliance requirement:
        /// - Standard: General-purpose applications
        /// - High: Financial, healthcare, government unclassified
        /// - Military: Government classified (SECRET level)
        /// - QuantumSafe: Long-term sensitive data, TOP SECRET
        /// </para>
        /// </remarks>
        Task<IReadOnlyList<CipherPreset>> ListPresetsBySecurityLevelAsync(
            TransitSecurityLevel securityLevel,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the default cipher preset for a given security level.
        /// </summary>
        /// <param name="securityLevel">The security level.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// The recommended default preset for the security level.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Default presets by security level:
        /// - Standard: "standard-aes256gcm" (AES-256-GCM)
        /// - High: "high-aes256gcm-sha512" (AES-256-GCM + HMAC-SHA512)
        /// - Military: "high-suiteb" (NSA Suite B)
        /// - QuantumSafe: "quantum-kyber768-dilithium3" (NIST PQC balanced)
        /// </para>
        /// <para>
        /// Use this method when no specific preset is requested but a security level is specified.
        /// </para>
        /// </remarks>
        Task<CipherPreset> GetDefaultPresetAsync(
            TransitSecurityLevel securityLevel,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates whether a cipher preset meets minimum security requirements.
        /// </summary>
        /// <param name="presetId">The preset ID to validate.</param>
        /// <param name="minimumSecurityLevel">The minimum required security level.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// True if the preset meets or exceeds the minimum security level, false otherwise.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Use this method to enforce security policies during cipher negotiation.
        /// For example, a policy might require High security level or above for financial data.
        /// </para>
        /// </remarks>
        Task<bool> ValidatePresetSecurityLevelAsync(
            string presetId,
            TransitSecurityLevel minimumSecurityLevel,
            CancellationToken cancellationToken = default);
    }
}
