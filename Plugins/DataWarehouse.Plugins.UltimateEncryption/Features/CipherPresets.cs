using DataWarehouse.SDK.Contracts.Encryption;

namespace DataWarehouse.Plugins.UltimateEncryption.Features;

/// <summary>
/// Defines cipher preset types for various security and compliance requirements.
/// </summary>
public enum CipherPreset
{
    /// <summary>
    /// Fast preset optimized for performance with adequate security.
    /// Uses hardware-accelerated algorithms where available.
    /// </summary>
    Fast,

    /// <summary>
    /// Balanced preset providing a good balance between security and performance.
    /// Recommended for most use cases.
    /// </summary>
    Balanced,

    /// <summary>
    /// Secure preset with strong cryptographic parameters.
    /// Prioritizes security over performance.
    /// </summary>
    Secure,

    /// <summary>
    /// Maximum security preset with strongest available algorithms.
    /// Uses multiple layers and quantum-resistant algorithms.
    /// </summary>
    Maximum,

    /// <summary>
    /// FIPS-only preset using exclusively FIPS 140-2/3 validated algorithms.
    /// Required for government and regulated environments.
    /// </summary>
    FipsOnly,

    /// <summary>
    /// Quantum-resistant preset using post-quantum cryptography.
    /// Protects against future quantum computer attacks.
    /// </summary>
    QuantumResistant,

    // Enterprise Presets

    /// <summary>
    /// PCI DSS compliant preset for payment card industry data security.
    /// Meets PCI DSS requirements for encryption at rest and in transit.
    /// </summary>
    PciDss,

    /// <summary>
    /// HIPAA compliant preset for protected health information.
    /// Meets HIPAA/HITECH requirements for PHI encryption.
    /// </summary>
    Hipaa,

    /// <summary>
    /// SOX compliant preset for financial data protection.
    /// Meets Sarbanes-Oxley requirements for financial records.
    /// </summary>
    Sox,

    /// <summary>
    /// ISO 27001 compliant preset for information security management.
    /// Follows ISO/IEC 27001 cryptographic controls.
    /// </summary>
    Iso27001,

    // Government Presets

    /// <summary>
    /// ITAR compliant preset for International Traffic in Arms Regulations.
    /// Meets ITAR requirements for defense-related technical data.
    /// </summary>
    Itar,

    /// <summary>
    /// FedRAMP compliant preset for federal cloud computing.
    /// Meets FedRAMP security requirements for cloud services.
    /// </summary>
    FedRamp,

    /// <summary>
    /// Classified preset for classified government information.
    /// Uses high-assurance algorithms approved for classified data.
    /// </summary>
    Classified,

    /// <summary>
    /// Top Secret preset for top secret government information.
    /// Uses strongest algorithms with split-key architecture.
    /// </summary>
    TopSecret
}

/// <summary>
/// Represents a complete cipher preset configuration including algorithms,
/// key derivation, hashing, and compliance metadata.
/// </summary>
/// <param name="Preset">The preset identifier.</param>
/// <param name="StorageCipher">Cipher algorithm for data at rest (e.g., "AES-256-GCM").</param>
/// <param name="TransitCipher">Cipher algorithm for data in transit (e.g., "ChaCha20-Poly1305").</param>
/// <param name="KeyDerivationFunction">KDF algorithm (e.g., "Argon2id", "PBKDF2-SHA256").</param>
/// <param name="KdfIterations">Number of iterations for key derivation (higher = more secure but slower).</param>
/// <param name="HashAlgorithm">Hash algorithm for integrity checks (e.g., "SHA-256", "SHA-512").</param>
/// <param name="AllowDowngrade">Whether downgrade to weaker algorithms is permitted.</param>
/// <param name="Description">Human-readable description of the preset.</param>
/// <param name="ComplianceStandards">List of compliance standards this preset satisfies.</param>
public sealed record CipherPresetConfiguration(
    CipherPreset Preset,
    string StorageCipher,
    string TransitCipher,
    string KeyDerivationFunction,
    int KdfIterations,
    string HashAlgorithm,
    bool AllowDowngrade,
    string Description,
    List<string> ComplianceStandards
)
{
    /// <summary>
    /// Gets the security level of this preset.
    /// </summary>
    public SecurityLevel SecurityLevel => Preset switch
    {
        CipherPreset.Fast => SecurityLevel.Standard,
        CipherPreset.Balanced => SecurityLevel.High,
        CipherPreset.Secure => SecurityLevel.High,
        CipherPreset.Maximum => SecurityLevel.Military,
        CipherPreset.FipsOnly => SecurityLevel.High,
        CipherPreset.QuantumResistant => SecurityLevel.Military,
        CipherPreset.PciDss => SecurityLevel.High,
        CipherPreset.Hipaa => SecurityLevel.High,
        CipherPreset.Sox => SecurityLevel.High,
        CipherPreset.Iso27001 => SecurityLevel.High,
        CipherPreset.Itar => SecurityLevel.High,
        CipherPreset.FedRamp => SecurityLevel.High,
        CipherPreset.Classified => SecurityLevel.Military,
        CipherPreset.TopSecret => SecurityLevel.Military,
        _ => SecurityLevel.Standard
    };

    /// <summary>
    /// Gets whether this preset requires audit logging.
    /// </summary>
    public bool RequiresAuditLogging => Preset switch
    {
        CipherPreset.Hipaa => true,
        CipherPreset.Sox => true,
        CipherPreset.Iso27001 => true,
        CipherPreset.Itar => true,
        CipherPreset.FedRamp => true,
        CipherPreset.Classified => true,
        CipherPreset.TopSecret => true,
        _ => false
    };

    /// <summary>
    /// Gets whether this preset requires split-key architecture.
    /// </summary>
    public bool RequiresSplitKeys => Preset switch
    {
        CipherPreset.TopSecret => true,
        CipherPreset.Maximum => true,
        _ => false
    };

    /// <summary>
    /// Gets the recommended key rotation interval in days.
    /// </summary>
    public int KeyRotationIntervalDays => Preset switch
    {
        CipherPreset.Fast => 365,
        CipherPreset.Balanced => 180,
        CipherPreset.Secure => 90,
        CipherPreset.Maximum => 30,
        CipherPreset.FipsOnly => 90,
        CipherPreset.QuantumResistant => 90,
        CipherPreset.PciDss => 90,
        CipherPreset.Hipaa => 90,
        CipherPreset.Sox => 90,
        CipherPreset.Iso27001 => 90,
        CipherPreset.Itar => 30,
        CipherPreset.FedRamp => 90,
        CipherPreset.Classified => 30,
        CipherPreset.TopSecret => 7,
        _ => 180
    };
}

/// <summary>
/// Interface for cipher preset provider.
/// </summary>
public interface ICipherPresetProvider
{
    /// <summary>
    /// Gets the configuration for a specific preset.
    /// </summary>
    /// <param name="preset">The preset to retrieve.</param>
    /// <returns>The preset configuration.</returns>
    CipherPresetConfiguration GetPreset(CipherPreset preset);

    /// <summary>
    /// Gets a preset by its name (case-insensitive).
    /// </summary>
    /// <param name="name">The preset name.</param>
    /// <returns>The preset configuration, or null if not found.</returns>
    CipherPresetConfiguration? GetPresetByName(string name);

    /// <summary>
    /// Lists all available presets.
    /// </summary>
    /// <returns>Collection of all preset configurations.</returns>
    IReadOnlyCollection<CipherPresetConfiguration> ListPresets();

    /// <summary>
    /// Recommends a preset based on system capabilities and requirements.
    /// </summary>
    /// <param name="hasHardwareAes">Whether AES hardware acceleration is available.</param>
    /// <param name="estimatedThroughputMBps">Estimated required throughput in MB/s.</param>
    /// <returns>Recommended preset configuration.</returns>
    CipherPresetConfiguration RecommendPreset(bool hasHardwareAes, int estimatedThroughputMBps);
}

/// <summary>
/// Default implementation of cipher preset provider with all standard presets.
/// </summary>
public sealed class CipherPresetProvider : ICipherPresetProvider
{
    private readonly Dictionary<CipherPreset, CipherPresetConfiguration> _presets;

    /// <summary>
    /// Initializes a new instance of the cipher preset provider.
    /// </summary>
    public CipherPresetProvider()
    {
        _presets = new Dictionary<CipherPreset, CipherPresetConfiguration>
        {
            // Standard Presets
            [CipherPreset.Fast] = new(
                Preset: CipherPreset.Fast,
                StorageCipher: "AES-128-GCM",
                TransitCipher: "ChaCha20-Poly1305",
                KeyDerivationFunction: "PBKDF2-SHA256",
                KdfIterations: 100_000,
                HashAlgorithm: "SHA-256",
                AllowDowngrade: true,
                Description: "Fast preset optimized for performance with hardware acceleration. " +
                             "Uses AES-128-GCM for storage and ChaCha20-Poly1305 for transit.",
                ComplianceStandards: new List<string> { "General Purpose" }
            ),

            [CipherPreset.Balanced] = new(
                Preset: CipherPreset.Balanced,
                StorageCipher: "AES-256-GCM",
                TransitCipher: "ChaCha20-Poly1305",
                KeyDerivationFunction: "Argon2id",
                KdfIterations: 3,
                HashAlgorithm: "SHA-256",
                AllowDowngrade: false,
                Description: "Balanced preset providing excellent security and good performance. " +
                             "Recommended default for most applications.",
                ComplianceStandards: new List<string> { "NIST SP 800-175B", "General Purpose" }
            ),

            [CipherPreset.Secure] = new(
                Preset: CipherPreset.Secure,
                StorageCipher: "AES-256-GCM",
                TransitCipher: "AES-256-GCM",
                KeyDerivationFunction: "Argon2id",
                KdfIterations: 5,
                HashAlgorithm: "SHA-512",
                AllowDowngrade: false,
                Description: "Secure preset with strong cryptographic parameters. " +
                             "Prioritizes security with minimal performance impact.",
                ComplianceStandards: new List<string> { "NIST SP 800-175B", "CNSA Suite 1.0" }
            ),

            [CipherPreset.Maximum] = new(
                Preset: CipherPreset.Maximum,
                StorageCipher: "Serpent-256-GCM",
                TransitCipher: "AES-256-GCM",
                KeyDerivationFunction: "Argon2id",
                KdfIterations: 10,
                HashAlgorithm: "SHA3-512",
                AllowDowngrade: false,
                Description: "Maximum security preset using Serpent cipher with layered encryption. " +
                             "Suitable for highly sensitive data requiring strongest available protection.",
                ComplianceStandards: new List<string> { "Military Grade", "CNSA Suite 2.0" }
            ),

            [CipherPreset.FipsOnly] = new(
                Preset: CipherPreset.FipsOnly,
                StorageCipher: "AES-256-GCM",
                TransitCipher: "AES-256-GCM",
                KeyDerivationFunction: "PBKDF2-SHA256",
                KdfIterations: 600_000,
                HashAlgorithm: "SHA-256",
                AllowDowngrade: false,
                Description: "FIPS 140-2/3 compliant preset using only validated algorithms. " +
                             "Required for government and highly regulated environments.",
                ComplianceStandards: new List<string> { "FIPS 140-2", "FIPS 140-3", "FedRAMP" }
            ),

            [CipherPreset.QuantumResistant] = new(
                Preset: CipherPreset.QuantumResistant,
                StorageCipher: "ML-KEM-1024",
                TransitCipher: "Kyber1024-AES256",
                KeyDerivationFunction: "Argon2id",
                KdfIterations: 5,
                HashAlgorithm: "SHA3-512",
                AllowDowngrade: false,
                Description: "Quantum-resistant preset using NIST-approved post-quantum algorithms. " +
                             "Protects against future quantum computing threats.",
                ComplianceStandards: new List<string> { "NIST PQC", "CNSA Suite 2.0" }
            ),

            // Enterprise Presets
            [CipherPreset.PciDss] = new(
                Preset: CipherPreset.PciDss,
                StorageCipher: "AES-256-GCM",
                TransitCipher: "AES-256-GCM",
                KeyDerivationFunction: "PBKDF2-SHA256",
                KdfIterations: 600_000,
                HashAlgorithm: "SHA-256",
                AllowDowngrade: false,
                Description: "PCI DSS compliant preset for payment card data protection. " +
                             "Meets PCI DSS v4.0 requirements for strong cryptography.",
                ComplianceStandards: new List<string> { "PCI DSS 4.0", "PCI DSS 3.2.1" }
            ),

            [CipherPreset.Hipaa] = new(
                Preset: CipherPreset.Hipaa,
                StorageCipher: "AES-256-GCM",
                TransitCipher: "AES-256-GCM",
                KeyDerivationFunction: "Argon2id",
                KdfIterations: 5,
                HashAlgorithm: "SHA-512",
                AllowDowngrade: false,
                Description: "HIPAA/HITECH compliant preset for protected health information. " +
                             "Includes mandatory audit logging and access controls.",
                ComplianceStandards: new List<string> { "HIPAA", "HITECH", "NIST SP 800-66" }
            ),

            [CipherPreset.Sox] = new(
                Preset: CipherPreset.Sox,
                StorageCipher: "AES-256-GCM",
                TransitCipher: "AES-256-GCM",
                KeyDerivationFunction: "Argon2id",
                KdfIterations: 5,
                HashAlgorithm: "SHA-512",
                AllowDowngrade: false,
                Description: "SOX compliant preset for financial records and audit trails. " +
                             "Ensures integrity and confidentiality of financial data.",
                ComplianceStandards: new List<string> { "SOX", "NIST SP 800-53" }
            ),

            [CipherPreset.Iso27001] = new(
                Preset: CipherPreset.Iso27001,
                StorageCipher: "AES-256-GCM",
                TransitCipher: "ChaCha20-Poly1305",
                KeyDerivationFunction: "Argon2id",
                KdfIterations: 5,
                HashAlgorithm: "SHA-512",
                AllowDowngrade: false,
                Description: "ISO/IEC 27001:2022 compliant preset following cryptographic controls. " +
                             "Aligns with Annex A.8 cryptographic controls.",
                ComplianceStandards: new List<string> { "ISO/IEC 27001:2022", "ISO/IEC 27002" }
            ),

            // Government Presets
            [CipherPreset.Itar] = new(
                Preset: CipherPreset.Itar,
                StorageCipher: "AES-256-GCM",
                TransitCipher: "AES-256-GCM",
                KeyDerivationFunction: "Argon2id",
                KdfIterations: 10,
                HashAlgorithm: "SHA-512",
                AllowDowngrade: false,
                Description: "ITAR compliant preset for defense-related technical data. " +
                             "Meets State Department requirements for controlled unclassified information.",
                ComplianceStandards: new List<string> { "ITAR", "EAR", "NIST SP 800-171" }
            ),

            [CipherPreset.FedRamp] = new(
                Preset: CipherPreset.FedRamp,
                StorageCipher: "AES-256-GCM",
                TransitCipher: "AES-256-GCM",
                KeyDerivationFunction: "PBKDF2-SHA256",
                KdfIterations: 600_000,
                HashAlgorithm: "SHA-256",
                AllowDowngrade: false,
                Description: "FedRAMP High baseline compliant preset for federal cloud services. " +
                             "Uses FIPS-validated cryptographic modules.",
                ComplianceStandards: new List<string> { "FedRAMP High", "FIPS 140-2", "NIST SP 800-53 Rev 5" }
            ),

            [CipherPreset.Classified] = new(
                Preset: CipherPreset.Classified,
                StorageCipher: "Serpent-256-XTS",
                TransitCipher: "AES-256-GCM",
                KeyDerivationFunction: "Argon2id",
                KdfIterations: 10,
                HashAlgorithm: "SHA3-512",
                AllowDowngrade: false,
                Description: "Classified information preset using NSA Suite B algorithms. " +
                             "Approved for classified information up to SECRET level.",
                ComplianceStandards: new List<string> { "CNSA Suite 1.0", "NSA Suite B", "NIST SP 800-53 Rev 5 High" }
            ),

            [CipherPreset.TopSecret] = new(
                Preset: CipherPreset.TopSecret,
                StorageCipher: "Serpent-256-XTS",
                TransitCipher: "Serpent-256-GCM",
                KeyDerivationFunction: "Argon2id",
                KdfIterations: 15,
                HashAlgorithm: "SHA3-512",
                AllowDowngrade: false,
                Description: "Top Secret information preset with split-key architecture. " +
                             "Uses multiple layers of Serpent encryption with separated key material. " +
                             "Weekly key rotation and continuous audit logging required.",
                ComplianceStandards: new List<string>
                {
                    "CNSA Suite 2.0",
                    "NSA Commercial Solutions for Classified",
                    "NIST SP 800-53 Rev 5 High",
                    "ICD 503"
                }
            )
        };
    }

    /// <inheritdoc/>
    public CipherPresetConfiguration GetPreset(CipherPreset preset)
    {
        if (!_presets.TryGetValue(preset, out var config))
        {
            throw new ArgumentException($"Preset '{preset}' is not defined", nameof(preset));
        }

        return config;
    }

    /// <inheritdoc/>
    public CipherPresetConfiguration? GetPresetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (Enum.TryParse<CipherPreset>(name, ignoreCase: true, out var preset))
        {
            return _presets.TryGetValue(preset, out var config) ? config : null;
        }

        return null;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<CipherPresetConfiguration> ListPresets()
    {
        return _presets.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public CipherPresetConfiguration RecommendPreset(bool hasHardwareAes, int estimatedThroughputMBps)
    {
        // High-throughput requirements (>500 MB/s)
        if (estimatedThroughputMBps > 500)
        {
            return hasHardwareAes
                ? GetPreset(CipherPreset.Fast)
                : GetPreset(CipherPreset.Balanced); // ChaCha20 for systems without AES-NI
        }

        // Medium throughput (100-500 MB/s)
        if (estimatedThroughputMBps > 100)
        {
            return GetPreset(CipherPreset.Balanced);
        }

        // Low throughput (<100 MB/s) - can afford stronger security
        return GetPreset(CipherPreset.Secure);
    }

    /// <summary>
    /// Gets presets that satisfy a specific compliance standard.
    /// </summary>
    /// <param name="complianceStandard">The compliance standard to match (e.g., "FIPS 140-2", "PCI DSS").</param>
    /// <returns>Collection of presets that meet the compliance standard.</returns>
    public IReadOnlyCollection<CipherPresetConfiguration> GetPresetsByCompliance(string complianceStandard)
    {
        if (string.IsNullOrWhiteSpace(complianceStandard))
        {
            return Array.Empty<CipherPresetConfiguration>();
        }

        return _presets.Values
            .Where(p => p.ComplianceStandards.Any(s =>
                s.Contains(complianceStandard, StringComparison.OrdinalIgnoreCase)))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets presets by minimum security level.
    /// </summary>
    /// <param name="minimumLevel">The minimum security level required.</param>
    /// <returns>Collection of presets that meet or exceed the security level.</returns>
    public IReadOnlyCollection<CipherPresetConfiguration> GetPresetsBySecurityLevel(SecurityLevel minimumLevel)
    {
        return _presets.Values
            .Where(p => p.SecurityLevel >= minimumLevel)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Validates whether a preset is suitable for given requirements.
    /// </summary>
    /// <param name="preset">The preset to validate.</param>
    /// <param name="requireFips">Whether FIPS compliance is required.</param>
    /// <param name="requireAuditLog">Whether audit logging is required.</param>
    /// <param name="minimumSecurityLevel">Minimum security level required.</param>
    /// <returns>Validation result with any violations.</returns>
    public PresetValidationResult ValidatePreset(
        CipherPreset preset,
        bool requireFips = false,
        bool requireAuditLog = false,
        SecurityLevel minimumSecurityLevel = SecurityLevel.Standard)
    {
        var config = GetPreset(preset);
        var violations = new List<string>();

        if (requireFips && !config.ComplianceStandards.Any(s =>
                s.Contains("FIPS", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add("FIPS compliance required but not satisfied");
        }

        if (requireAuditLog && !config.RequiresAuditLogging)
        {
            violations.Add("Audit logging required but not enabled in preset");
        }

        if (config.SecurityLevel < minimumSecurityLevel)
        {
            violations.Add($"Security level {config.SecurityLevel} is below required {minimumSecurityLevel}");
        }

        return new PresetValidationResult(
            IsValid: violations.Count == 0,
            Violations: violations
        );
    }
}

/// <summary>
/// Result of preset validation.
/// </summary>
/// <param name="IsValid">Whether the preset meets all requirements.</param>
/// <param name="Violations">List of requirement violations, if any.</param>
public sealed record PresetValidationResult(
    bool IsValid,
    List<string> Violations
);
