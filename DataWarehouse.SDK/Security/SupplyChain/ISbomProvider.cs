using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Security.SupplyChain
{
    /// <summary>
    /// Supported SBOM output formats for supply chain transparency.
    /// </summary>
    public enum SbomFormat
    {
        /// <summary>CycloneDX 1.5 JSON format (OWASP standard).</summary>
        CycloneDX_1_5_Json,

        /// <summary>CycloneDX 1.5 XML format.</summary>
        CycloneDX_1_5_Xml,

        /// <summary>SPDX 2.3 JSON format (Linux Foundation standard).</summary>
        SPDX_2_3_Json,

        /// <summary>SPDX 2.3 Tag-Value format.</summary>
        SPDX_2_3_TagValue
    }

    /// <summary>
    /// Options controlling SBOM generation behavior.
    /// </summary>
    public sealed class SbomOptions
    {
        /// <summary>Whether to include transitive dependencies. Default true.</summary>
        public bool IncludeTransitive { get; set; } = true;

        /// <summary>Whether to compute file hashes (SHA-256) for each component. Default true.</summary>
        public bool IncludeHashes { get; set; } = true;

        /// <summary>Whether to include license information when available. Default true.</summary>
        public bool IncludeLicenses { get; set; } = true;

        /// <summary>Creator tool name embedded in the SBOM metadata.</summary>
        public string CreatorTool { get; set; } = "DataWarehouse SBOM Generator";

        /// <summary>Filter to only include components matching these namespace prefixes. Null = all.</summary>
        public IReadOnlyList<string>? NamespaceFilter { get; set; }
    }

    /// <summary>
    /// Represents a generated SBOM document.
    /// </summary>
    public sealed class SbomDocument
    {
        /// <summary>The format this document was generated in.</summary>
        public SbomFormat Format { get; init; }

        /// <summary>The serialized SBOM content (JSON, XML, or tag-value text).</summary>
        public string Content { get; init; } = string.Empty;

        /// <summary>When this SBOM was generated.</summary>
        public DateTimeOffset GeneratedAt { get; init; }

        /// <summary>Total number of components included in the SBOM.</summary>
        public int ComponentCount { get; init; }

        /// <summary>Number of known vulnerabilities found during generation (0 if not scanned).</summary>
        public int VulnerabilityCount { get; init; }

        /// <summary>Individual component entries for programmatic access.</summary>
        public IReadOnlyList<SbomComponent> Components { get; init; } = Array.Empty<SbomComponent>();
    }

    /// <summary>
    /// Represents a single component (assembly/package) in an SBOM.
    /// </summary>
    public sealed class SbomComponent
    {
        /// <summary>Component name (assembly or package name).</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Component version string.</summary>
        public string Version { get; init; } = string.Empty;

        /// <summary>Component type: "library", "framework", "application".</summary>
        public string ComponentType { get; init; } = "library";

        /// <summary>Package URL (purl) identifier, e.g. pkg:nuget/Newtonsoft.Json@13.0.1.</summary>
        public string? PackageUrl { get; init; }

        /// <summary>SHA-256 hash of the assembly file, if computed.</summary>
        public string? Sha256Hash { get; init; }

        /// <summary>File path of the assembly on disk.</summary>
        public string? FilePath { get; init; }

        /// <summary>License identifier (SPDX expression) if known.</summary>
        public string? LicenseId { get; init; }

        /// <summary>Names of assemblies this component directly depends on.</summary>
        public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

        /// <summary>Whether this is a DataWarehouse-owned component vs third-party.</summary>
        public bool IsFirstParty { get; init; }
    }

    /// <summary>
    /// Interface for generating Software Bill of Materials (SBOM) documents.
    /// Supports CycloneDX 1.5 and SPDX 2.3 formats for supply chain security compliance
    /// (SLSA, SOC2, FedRAMP).
    /// </summary>
    public interface ISbomProvider
    {
        /// <summary>
        /// Generate an SBOM document in the specified format from runtime assembly information.
        /// </summary>
        /// <param name="format">The SBOM format to produce.</param>
        /// <param name="options">Optional generation options. Null uses defaults.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A fully serialized SBOM document.</returns>
        Task<SbomDocument> GenerateAsync(
            SbomFormat format,
            SbomOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Get the list of discovered components without generating a full SBOM document.
        /// Useful for quick inventory checks.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>All discovered runtime components.</returns>
        Task<IReadOnlyList<SbomComponent>> DiscoverComponentsAsync(CancellationToken ct = default);

        /// <summary>
        /// Get the supported SBOM formats.
        /// </summary>
        IReadOnlyList<SbomFormat> SupportedFormats { get; }
    }
}
