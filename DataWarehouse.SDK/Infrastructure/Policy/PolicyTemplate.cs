using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Represents a shareable, portable policy package that can be exported from one DataWarehouse instance
    /// and imported into another. Templates carry full metadata (author, version, description, compliance
    /// frameworks) to support community sharing and marketplace distribution.
    /// <para>
    /// A compliance officer can export a "HIPAA-Compliant-Standard" template from a certified environment
    /// and import it into any DataWarehouse instance, with automatic compatibility validation ensuring the
    /// target engine supports all required features.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Templates include an optional SHA-256 checksum for integrity verification during import.
    /// The <see cref="MinEngineVersion"/> field ensures templates are only imported into engine versions
    /// that support all features referenced by the contained policies.
    /// </remarks>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Policy marketplace (PADV-01)")]
    public sealed record PolicyTemplate
    {
        /// <summary>
        /// Unique identifier for this template (GUID format).
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Human-readable name for this template (e.g., "HIPAA-Compliant-Standard").
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Detailed description of what this template provides and its intended use case.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Identity of the template creator (individual, organization, or system identifier).
        /// </summary>
        public required string Author { get; init; }

        /// <summary>
        /// Semantic version of this template, independent of the engine version.
        /// Incremented when the template's policies are updated.
        /// </summary>
        public required Version TemplateVersion { get; init; }

        /// <summary>
        /// Minimum PolicyEngine version required to import this template.
        /// Templates referencing features introduced in newer engine versions set this
        /// to ensure compatibility (e.g., 6.0.0 for Phase 69 features).
        /// </summary>
        public required Version MinEngineVersion { get; init; }

        /// <summary>
        /// UTC timestamp when this template was first created.
        /// </summary>
        public DateTimeOffset CreatedAt { get; init; }

        /// <summary>
        /// UTC timestamp of the most recent update to this template, or null if never updated.
        /// </summary>
        public DateTimeOffset? UpdatedAt { get; init; }

        /// <summary>
        /// Searchable tags for categorization and discovery (e.g., "hipaa", "healthcare", "strict").
        /// </summary>
        public string[] Tags { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Compliance frameworks this template is designed to satisfy (e.g., "HIPAA", "SOC2", "GDPR", "PCI-DSS").
        /// Used for marketplace filtering and compliance reporting.
        /// </summary>
        public string[] TargetComplianceFrameworks { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The collection of feature policies contained in this template.
        /// These are the actual policy definitions that will be imported into the target system.
        /// </summary>
        public required IReadOnlyList<FeaturePolicy> Policies { get; init; }

        /// <summary>
        /// Optional operational profile preset bundled with this template.
        /// When present, importing the template also sets the active operational profile.
        /// </summary>
        public OperationalProfile? Profile { get; init; }

        /// <summary>
        /// SHA-256 hex digest of the serialized policies for tamper detection.
        /// Computed during export and verified during import to ensure template integrity.
        /// Null when checksum verification is not required.
        /// </summary>
        public string? Checksum { get; init; }
    }

    /// <summary>
    /// Result of a compatibility check between a <see cref="PolicyTemplate"/> and the current engine version.
    /// Indicates whether the template can be safely imported and provides details when it cannot.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Policy marketplace (PADV-01)")]
    public sealed record PolicyTemplateCompatibility
    {
        /// <summary>
        /// Whether the template is compatible with the current engine version and can be imported.
        /// </summary>
        public bool IsCompatible { get; init; }

        /// <summary>
        /// Human-readable explanation of why the template is incompatible, or null when compatible.
        /// </summary>
        public string? IncompatibilityReason { get; init; }

        /// <summary>
        /// The minimum engine version required by the template, provided for diagnostics when incompatible.
        /// </summary>
        public Version? RequiredVersion { get; init; }

        /// <summary>
        /// The current engine version, provided for diagnostics when incompatible.
        /// </summary>
        public Version? CurrentVersion { get; init; }
    }

    /// <summary>
    /// Result of a policy template import operation. Contains success status, counts of imported items,
    /// and any warnings generated during the import (e.g., skipped duplicate policies).
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Policy marketplace (PADV-01)")]
    public sealed record PolicyTemplateImportResult
    {
        /// <summary>
        /// Whether the import operation completed successfully.
        /// False when compatibility checks fail or a critical error occurs.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Number of individual feature policies that were imported into the target persistence backend.
        /// </summary>
        public int PoliciesImported { get; init; }

        /// <summary>
        /// Whether the template's optional operational profile was imported.
        /// </summary>
        public bool ProfileImported { get; init; }

        /// <summary>
        /// Non-fatal warnings generated during import (e.g., "Skipped existing policy for 'encryption' at VDE level").
        /// Empty when no warnings were generated.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Error message when the import failed, or null on success.
        /// </summary>
        public string? Error { get; init; }
    }
}
