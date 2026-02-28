using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.SDK.Security.SupplyChain
{
    /// <summary>
    /// Runtime SBOM generator that discovers loaded assemblies and their dependencies,
    /// producing CycloneDX 1.5 and SPDX 2.3 documents without external NuGet dependencies.
    /// Uses AppDomain reflection and *.deps.json parsing for comprehensive component discovery.
    /// </summary>
    public sealed class SbomGenerator : ISbomProvider
    {
        private readonly ILogger<SbomGenerator>? _logger;
        private readonly SemaphoreSlim _cacheLock = new(1, 1);
        private IReadOnlyList<SbomComponent>? _cachedComponents;
        private int _cachedAssemblyCount;
        private string _cachedAssemblyHash = string.Empty;

        /// <inheritdoc />
        public IReadOnlyList<SbomFormat> SupportedFormats { get; } = new[]
        {
            SbomFormat.CycloneDX_1_5_Json,
            SbomFormat.CycloneDX_1_5_Xml,
            SbomFormat.SPDX_2_3_Json,
            SbomFormat.SPDX_2_3_TagValue
        };

        /// <summary>
        /// Creates a new SBOM generator instance.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostic output.</param>
        public SbomGenerator(ILogger<SbomGenerator>? logger = null)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<SbomDocument> GenerateAsync(
            SbomFormat format,
            SbomOptions? options = null,
            CancellationToken ct = default)
        {
            options ??= new SbomOptions();
            var components = await DiscoverComponentsAsync(ct).ConfigureAwait(false);

            if (!options.IncludeHashes)
            {
                // Strip hashes if not requested
                components = components.Select(c => new SbomComponent
                {
                    Name = c.Name,
                    Version = c.Version,
                    ComponentType = c.ComponentType,
                    PackageUrl = c.PackageUrl,
                    Sha256Hash = null,
                    FilePath = c.FilePath,
                    LicenseId = options.IncludeLicenses ? c.LicenseId : null,
                    Dependencies = options.IncludeTransitive ? c.Dependencies : Array.Empty<string>(),
                    IsFirstParty = c.IsFirstParty
                }).ToList();
            }

            if (options.NamespaceFilter is { Count: > 0 } filter)
            {
                components = components
                    .Where(c => filter.Any(ns => c.Name.StartsWith(ns, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            var content = format switch
            {
                SbomFormat.CycloneDX_1_5_Json => GenerateCycloneDxJson(components, options),
                SbomFormat.CycloneDX_1_5_Xml => GenerateCycloneDxXml(components, options),
                SbomFormat.SPDX_2_3_Json => GenerateSpdxJson(components, options),
                SbomFormat.SPDX_2_3_TagValue => GenerateSpdxTagValue(components, options),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported SBOM format")
            };

            _logger?.LogInformation(
                "Generated {Format} SBOM with {ComponentCount} components",
                format, components.Count);

            return new SbomDocument
            {
                Format = format,
                Content = content,
                GeneratedAt = DateTimeOffset.UtcNow,
                ComponentCount = components.Count,
                VulnerabilityCount = 0,
                Components = components
            };
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SbomComponent>> DiscoverComponentsAsync(CancellationToken ct = default)
        {
            await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var currentCount = assemblies.Length;
                var currentHash = ComputeAssemblyListHash(assemblies);

                if (_cachedComponents != null &&
                    _cachedAssemblyCount == currentCount &&
                    _cachedAssemblyHash == currentHash)
                {
                    return _cachedComponents;
                }

                var components = DiscoverComponents(assemblies);
                _cachedComponents = components;
                _cachedAssemblyCount = currentCount;
                _cachedAssemblyHash = currentHash;

                _logger?.LogDebug("Discovered {Count} components from {AssemblyCount} loaded assemblies",
                    components.Count, currentCount);

                return components;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        private IReadOnlyList<SbomComponent> DiscoverComponents(Assembly[] assemblies)
        {
            var depsJsonPackages = ParseDepsJsonFiles();
            // Use a concurrent bag for parallel-safe collection (finding P2-599).
            var bag = new System.Collections.Concurrent.ConcurrentBag<SbomComponent>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount)
            };

            Parallel.ForEach(assemblies, parallelOptions, assembly =>
            {
                if (assembly.IsDynamic) return;
                var component = BuildComponent(assembly, depsJsonPackages);
                if (component != null)
                    bag.Add(component);
            });

            return bag.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private SbomComponent? BuildComponent(Assembly assembly, Dictionary<string, DepsJsonPackageInfo> depsJsonPackages)
        {
            if (assembly.IsDynamic) return null;

            var name = assembly.GetName();
            if (name.Name == null) return null;

            // Skip core framework assemblies (System.*, Microsoft.Extensions.* are kept as they're NuGet packages)
            if (IsFrameworkAssembly(name.Name)) return null;

            var isFirstParty = name.Name.StartsWith("DataWarehouse.", StringComparison.OrdinalIgnoreCase);
            string? sha256 = null;
            string? filePath = null;

            try
            {
                if (!string.IsNullOrEmpty(assembly.Location) && File.Exists(assembly.Location))
                {
                    filePath = assembly.Location;
                    sha256 = ComputeFileSha256(assembly.Location);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogTrace(ex, "Could not compute hash for {Assembly}", name.Name);
            }

            // Resolve package URL from deps.json data or assembly name
            var packageUrl = ResolvePackageUrl(name.Name, name.Version?.ToString() ?? "0.0.0", depsJsonPackages);

            // Get direct dependencies
            var dependencies = new List<string>();
            try
            {
                var refs = assembly.GetReferencedAssemblies();
                foreach (var refAsm in refs)
                {
                    if (refAsm.Name != null && !IsFrameworkAssembly(refAsm.Name))
                        dependencies.Add(refAsm.Name);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogTrace(ex, "Could not enumerate references for {Assembly}", name.Name);
            }

            // Try to get license from deps.json metadata
            string? licenseId = null;
            if (depsJsonPackages.TryGetValue(name.Name, out var pkgInfo))
                licenseId = pkgInfo.LicenseId;

            return new SbomComponent
            {
                Name = name.Name,
                Version = name.Version?.ToString() ?? "0.0.0",
                ComponentType = isFirstParty ? "application" : "library",
                PackageUrl = packageUrl,
                Sha256Hash = sha256,
                FilePath = filePath,
                LicenseId = licenseId,
                Dependencies = dependencies,
                IsFirstParty = isFirstParty
            };
        }

        private static bool IsFrameworkAssembly(string name)
        {
            // Skip BCL and runtime assemblies; keep Microsoft.Extensions.* (NuGet packages)
            if (name.StartsWith("System.", StringComparison.Ordinal) && !name.Contains("Text.Json"))
                return true;
            if (name == "System" || name == "mscorlib" || name == "netstandard")
                return true;
            if (name.StartsWith("Microsoft.CSharp", StringComparison.Ordinal))
                return true;
            if (name.StartsWith("Microsoft.Win32", StringComparison.Ordinal))
                return true;
            if (name.StartsWith("Microsoft.VisualBasic", StringComparison.Ordinal))
                return true;
            // Keep Microsoft.Extensions.* — they are NuGet packages
            if (name.StartsWith("Microsoft.", StringComparison.Ordinal) &&
                !name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
                return true;

            return false;
        }

        private static string ComputeAssemblyListHash(Assembly[] assemblies)
        {
            using var sha = SHA256.Create();
            var names = assemblies
                .Where(a => !a.IsDynamic)
                .Select(a => a.GetName().FullName)
                .OrderBy(n => n, StringComparer.Ordinal);

            var combined = string.Join("|", names);
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
            return Convert.ToHexString(hash);
        }

        private static string ComputeFileSha256(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string ResolvePackageUrl(string name, string version,
            Dictionary<string, DepsJsonPackageInfo> depsPackages)
        {
            if (depsPackages.TryGetValue(name, out var pkg))
            {
                return $"pkg:nuget/{pkg.PackageName}@{pkg.Version}";
            }

            return $"pkg:nuget/{name}@{version}";
        }

        // ──────────────────────────────────────────────────────────────
        // deps.json parsing for NuGet package metadata
        // ──────────────────────────────────────────────────────────────

        private Dictionary<string, DepsJsonPackageInfo> ParseDepsJsonFiles()
        {
            var result = new Dictionary<string, DepsJsonPackageInfo>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var depsFiles = Directory.GetFiles(baseDir, "*.deps.json", SearchOption.TopDirectoryOnly);

                foreach (var depsFile in depsFiles)
                {
                    try
                    {
                        ParseSingleDepsJson(depsFile, result);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogTrace(ex, "Failed to parse deps.json: {File}", depsFile);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogTrace(ex, "Failed to enumerate deps.json files");
            }

            return result;
        }

        private static void ParseSingleDepsJson(string filePath, Dictionary<string, DepsJsonPackageInfo> result)
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("libraries", out var libraries))
                return;

            foreach (var lib in libraries.EnumerateObject())
            {
                // Key format: "PackageName/Version"
                var parts = lib.Name.Split('/');
                if (parts.Length != 2) continue;

                var packageName = parts[0];
                var version = parts[1];

                var libType = lib.Value.TryGetProperty("type", out var typeEl)
                    ? typeEl.GetString() ?? "unknown"
                    : "unknown";

                // Only include "package" type (NuGet packages)
                if (!string.Equals(libType, "package", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? licenseId = null;
                if (lib.Value.TryGetProperty("licenseExpression", out var licEl))
                {
                    licenseId = licEl.GetString();
                }

                // Map assembly names that might differ from package names
                var assemblyName = packageName.Replace(".", string.Empty) switch
                {
                    _ => packageName // Default: package name is the assembly name
                };

                result.TryAdd(packageName, new DepsJsonPackageInfo
                {
                    PackageName = packageName,
                    Version = version,
                    LicenseId = licenseId
                });
            }
        }

        // ──────────────────────────────────────────────────────────────
        // CycloneDX 1.5 JSON generation
        // ──────────────────────────────────────────────────────────────

        private static string GenerateCycloneDxJson(IReadOnlyList<SbomComponent> components, SbomOptions options)
        {
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            writer.WriteString("bomFormat", "CycloneDX");
            writer.WriteString("specVersion", "1.5");
            writer.WriteNumber("version", 1);
            writer.WriteString("serialNumber", $"urn:uuid:{Guid.NewGuid()}");

            // Metadata
            writer.WriteStartObject("metadata");
            writer.WriteString("timestamp", DateTimeOffset.UtcNow.ToString("o"));

            writer.WriteStartArray("tools");
            writer.WriteStartObject();
            writer.WriteString("vendor", "DataWarehouse");
            writer.WriteString("name", options.CreatorTool);
            writer.WriteString("version", typeof(SbomGenerator).Assembly.GetName().Version?.ToString() ?? "1.0.0");
            writer.WriteEndObject();
            writer.WriteEndArray();

            // Root component
            writer.WriteStartObject("component");
            writer.WriteString("type", "application");
            writer.WriteString("name", "DataWarehouse");
            writer.WriteString("version",
                typeof(SbomGenerator).Assembly.GetName().Version?.ToString() ?? "1.0.0");
            writer.WriteString("bom-ref", "datawarehouse-root");
            writer.WriteEndObject();

            writer.WriteEndObject(); // metadata

            // Components array
            writer.WriteStartArray("components");
            foreach (var component in components)
            {
                writer.WriteStartObject();
                writer.WriteString("type", component.ComponentType);
                writer.WriteString("name", component.Name);
                writer.WriteString("version", component.Version);
                writer.WriteString("bom-ref", $"pkg:{component.Name}@{component.Version}");

                if (component.PackageUrl != null)
                {
                    writer.WriteString("purl", component.PackageUrl);
                }

                if (options.IncludeHashes && component.Sha256Hash != null)
                {
                    writer.WriteStartArray("hashes");
                    writer.WriteStartObject();
                    writer.WriteString("alg", "SHA-256");
                    writer.WriteString("content", component.Sha256Hash);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }

                if (options.IncludeLicenses && component.LicenseId != null)
                {
                    writer.WriteStartArray("licenses");
                    writer.WriteStartObject();
                    writer.WriteStartObject("license");
                    writer.WriteString("id", component.LicenseId);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }
            writer.WriteEndArray(); // components

            // Dependencies
            writer.WriteStartArray("dependencies");

            // Root depends on all first-party components
            writer.WriteStartObject();
            writer.WriteString("ref", "datawarehouse-root");
            writer.WriteStartArray("dependsOn");
            foreach (var component in components.Where(c => c.IsFirstParty))
            {
                writer.WriteStringValue($"pkg:{component.Name}@{component.Version}");
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            // Individual component dependencies
            foreach (var component in components)
            {
                if (component.Dependencies.Count == 0)
                    continue;

                writer.WriteStartObject();
                writer.WriteString("ref", $"pkg:{component.Name}@{component.Version}");
                writer.WriteStartArray("dependsOn");
                foreach (var dep in component.Dependencies)
                {
                    var resolved = components.FirstOrDefault(c =>
                        string.Equals(c.Name, dep, StringComparison.OrdinalIgnoreCase));
                    if (resolved != null)
                    {
                        writer.WriteStringValue($"pkg:{resolved.Name}@{resolved.Version}");
                    }
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); // dependencies

            writer.WriteEndObject(); // root
            writer.Flush();

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        // ──────────────────────────────────────────────────────────────
        // CycloneDX 1.5 XML generation
        // ──────────────────────────────────────────────────────────────

        private static string GenerateCycloneDxXml(IReadOnlyList<SbomComponent> components, SbomOptions options)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<bom xmlns=\"http://cyclonedx.org/schema/bom/1.5\" version=\"1\"");
            sb.AppendLine($"     serialNumber=\"urn:uuid:{Guid.NewGuid()}\">");

            // Metadata
            sb.AppendLine("  <metadata>");
            sb.AppendLine($"    <timestamp>{DateTimeOffset.UtcNow:o}</timestamp>");
            sb.AppendLine("    <tools>");
            sb.AppendLine("      <tool>");
            sb.AppendLine("        <vendor>DataWarehouse</vendor>");
            sb.AppendLine($"        <name>{EscapeXml(options.CreatorTool)}</name>");
            sb.AppendLine($"        <version>{typeof(SbomGenerator).Assembly.GetName().Version?.ToString() ?? "1.0.0"}</version>");
            sb.AppendLine("      </tool>");
            sb.AppendLine("    </tools>");
            sb.AppendLine("    <component type=\"application\">");
            sb.AppendLine("      <name>DataWarehouse</name>");
            sb.AppendLine($"      <version>{typeof(SbomGenerator).Assembly.GetName().Version?.ToString() ?? "1.0.0"}</version>");
            sb.AppendLine("      <bom-ref>datawarehouse-root</bom-ref>");
            sb.AppendLine("    </component>");
            sb.AppendLine("  </metadata>");

            // Components
            sb.AppendLine("  <components>");
            foreach (var component in components)
            {
                sb.AppendLine($"    <component type=\"{component.ComponentType}\">");
                sb.AppendLine($"      <name>{EscapeXml(component.Name)}</name>");
                sb.AppendLine($"      <version>{EscapeXml(component.Version)}</version>");
                sb.AppendLine($"      <bom-ref>pkg:{EscapeXml(component.Name)}@{EscapeXml(component.Version)}</bom-ref>");

                if (component.PackageUrl != null)
                    sb.AppendLine($"      <purl>{EscapeXml(component.PackageUrl)}</purl>");

                if (options.IncludeHashes && component.Sha256Hash != null)
                {
                    sb.AppendLine("      <hashes>");
                    sb.AppendLine($"        <hash alg=\"SHA-256\">{component.Sha256Hash}</hash>");
                    sb.AppendLine("      </hashes>");
                }

                if (options.IncludeLicenses && component.LicenseId != null)
                {
                    sb.AppendLine("      <licenses>");
                    sb.AppendLine("        <license>");
                    sb.AppendLine($"          <id>{EscapeXml(component.LicenseId)}</id>");
                    sb.AppendLine("        </license>");
                    sb.AppendLine("      </licenses>");
                }

                sb.AppendLine("    </component>");
            }
            sb.AppendLine("  </components>");

            // Dependencies
            sb.AppendLine("  <dependencies>");
            sb.AppendLine("    <dependency ref=\"datawarehouse-root\">");
            foreach (var component in components.Where(c => c.IsFirstParty))
            {
                sb.AppendLine($"      <dependency ref=\"pkg:{EscapeXml(component.Name)}@{EscapeXml(component.Version)}\" />");
            }
            sb.AppendLine("    </dependency>");

            foreach (var component in components.Where(c => c.Dependencies.Count > 0))
            {
                sb.AppendLine($"    <dependency ref=\"pkg:{EscapeXml(component.Name)}@{EscapeXml(component.Version)}\">");
                foreach (var dep in component.Dependencies)
                {
                    var resolved = components.FirstOrDefault(c =>
                        string.Equals(c.Name, dep, StringComparison.OrdinalIgnoreCase));
                    if (resolved != null)
                    {
                        sb.AppendLine($"      <dependency ref=\"pkg:{EscapeXml(resolved.Name)}@{EscapeXml(resolved.Version)}\" />");
                    }
                }
                sb.AppendLine("    </dependency>");
            }
            sb.AppendLine("  </dependencies>");

            sb.AppendLine("</bom>");
            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────
        // SPDX 2.3 JSON generation
        // ──────────────────────────────────────────────────────────────

        private static string GenerateSpdxJson(IReadOnlyList<SbomComponent> components, SbomOptions options)
        {
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            writer.WriteString("spdxVersion", "SPDX-2.3");
            writer.WriteString("dataLicense", "CC0-1.0");
            writer.WriteString("SPDXID", "SPDXRef-DOCUMENT");
            writer.WriteString("name", "DataWarehouse-SBOM");
            writer.WriteString("documentNamespace",
                $"https://datawarehouse.local/spdx/{Guid.NewGuid()}");

            // Creation info
            writer.WriteStartObject("creationInfo");
            writer.WriteString("created", DateTimeOffset.UtcNow.ToString("o"));
            writer.WriteStartArray("creators");
            writer.WriteStringValue($"Tool: {options.CreatorTool}");
            writer.WriteStringValue("Organization: DataWarehouse");
            writer.WriteEndArray();
            writer.WriteString("licenseListVersion", "3.22");
            writer.WriteEndObject();

            // Document describes the root package
            writer.WriteStartArray("documentDescribes");
            writer.WriteStringValue("SPDXRef-RootPackage");
            writer.WriteEndArray();

            // Packages
            writer.WriteStartArray("packages");

            // Root package
            writer.WriteStartObject();
            writer.WriteString("SPDXID", "SPDXRef-RootPackage");
            writer.WriteString("name", "DataWarehouse");
            writer.WriteString("versionInfo",
                typeof(SbomGenerator).Assembly.GetName().Version?.ToString() ?? "1.0.0");
            writer.WriteString("downloadLocation", "NOASSERTION");
            writer.WriteBoolean("filesAnalyzed", false);
            writer.WriteString("supplier", "Organization: DataWarehouse");
            writer.WriteEndObject();

            // Component packages
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                writer.WriteStartObject();
                writer.WriteString("SPDXID", $"SPDXRef-Package-{SanitizeSpdxId(component.Name)}");
                writer.WriteString("name", component.Name);
                writer.WriteString("versionInfo", component.Version);
                writer.WriteString("downloadLocation", "NOASSERTION");
                writer.WriteBoolean("filesAnalyzed", false);

                if (component.PackageUrl != null)
                {
                    writer.WriteStartArray("externalRefs");
                    writer.WriteStartObject();
                    writer.WriteString("referenceCategory", "PACKAGE-MANAGER");
                    writer.WriteString("referenceType", "purl");
                    writer.WriteString("referenceLocator", component.PackageUrl);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }

                if (options.IncludeHashes && component.Sha256Hash != null)
                {
                    writer.WriteStartArray("checksums");
                    writer.WriteStartObject();
                    writer.WriteString("algorithm", "SHA256");
                    writer.WriteString("checksumValue", component.Sha256Hash);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }

                if (options.IncludeLicenses && component.LicenseId != null)
                {
                    writer.WriteString("licenseConcluded", component.LicenseId);
                    writer.WriteString("licenseDeclared", component.LicenseId);
                }
                else
                {
                    writer.WriteString("licenseConcluded", "NOASSERTION");
                    writer.WriteString("licenseDeclared", "NOASSERTION");
                }

                writer.WriteString("copyrightText", "NOASSERTION");

                writer.WriteEndObject();
            }
            writer.WriteEndArray(); // packages

            // Relationships
            writer.WriteStartArray("relationships");

            // Root DESCRIBES relationship
            writer.WriteStartObject();
            writer.WriteString("spdxElementId", "SPDXRef-DOCUMENT");
            writer.WriteString("relatedSpdxElement", "SPDXRef-RootPackage");
            writer.WriteString("relationshipType", "DESCRIBES");
            writer.WriteEndObject();

            // Root DEPENDS_ON each first-party component
            foreach (var component in components.Where(c => c.IsFirstParty))
            {
                writer.WriteStartObject();
                writer.WriteString("spdxElementId", "SPDXRef-RootPackage");
                writer.WriteString("relatedSpdxElement",
                    $"SPDXRef-Package-{SanitizeSpdxId(component.Name)}");
                writer.WriteString("relationshipType", "DEPENDS_ON");
                writer.WriteEndObject();
            }

            // Component-level DEPENDS_ON
            foreach (var component in components)
            {
                foreach (var dep in component.Dependencies)
                {
                    var resolved = components.FirstOrDefault(c =>
                        string.Equals(c.Name, dep, StringComparison.OrdinalIgnoreCase));
                    if (resolved != null)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("spdxElementId",
                            $"SPDXRef-Package-{SanitizeSpdxId(component.Name)}");
                        writer.WriteString("relatedSpdxElement",
                            $"SPDXRef-Package-{SanitizeSpdxId(resolved.Name)}");
                        writer.WriteString("relationshipType", "DEPENDS_ON");
                        writer.WriteEndObject();
                    }
                }
            }
            writer.WriteEndArray(); // relationships

            writer.WriteEndObject(); // root
            writer.Flush();

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        // ──────────────────────────────────────────────────────────────
        // SPDX 2.3 Tag-Value generation
        // ──────────────────────────────────────────────────────────────

        private static string GenerateSpdxTagValue(IReadOnlyList<SbomComponent> components, SbomOptions options)
        {
            var sb = new StringBuilder();

            sb.AppendLine("SPDXVersion: SPDX-2.3");
            sb.AppendLine("DataLicense: CC0-1.0");
            sb.AppendLine("SPDXID: SPDXRef-DOCUMENT");
            sb.AppendLine("DocumentName: DataWarehouse-SBOM");
            sb.AppendLine($"DocumentNamespace: https://datawarehouse.local/spdx/{Guid.NewGuid()}");
            sb.AppendLine($"Creator: Tool: {options.CreatorTool}");
            sb.AppendLine("Creator: Organization: DataWarehouse");
            sb.AppendLine($"Created: {DateTimeOffset.UtcNow:o}");
            sb.AppendLine("LicenseListVersion: 3.22");
            sb.AppendLine();

            // Root package
            sb.AppendLine("##### Package: DataWarehouse");
            sb.AppendLine();
            sb.AppendLine("PackageName: DataWarehouse");
            sb.AppendLine("SPDXID: SPDXRef-RootPackage");
            sb.AppendLine($"PackageVersion: {typeof(SbomGenerator).Assembly.GetName().Version?.ToString() ?? "1.0.0"}");
            sb.AppendLine("PackageDownloadLocation: NOASSERTION");
            sb.AppendLine("FilesAnalyzed: false");
            sb.AppendLine("PackageSupplier: Organization: DataWarehouse");
            sb.AppendLine();

            // Component packages
            foreach (var component in components)
            {
                sb.AppendLine($"##### Package: {component.Name}");
                sb.AppendLine();
                sb.AppendLine($"PackageName: {component.Name}");
                sb.AppendLine($"SPDXID: SPDXRef-Package-{SanitizeSpdxId(component.Name)}");
                sb.AppendLine($"PackageVersion: {component.Version}");
                sb.AppendLine("PackageDownloadLocation: NOASSERTION");
                sb.AppendLine("FilesAnalyzed: false");

                if (component.PackageUrl != null)
                    sb.AppendLine($"ExternalRef: PACKAGE-MANAGER purl {component.PackageUrl}");

                if (options.IncludeHashes && component.Sha256Hash != null)
                    sb.AppendLine($"PackageChecksum: SHA256: {component.Sha256Hash}");

                if (options.IncludeLicenses && component.LicenseId != null)
                {
                    sb.AppendLine($"PackageLicenseConcluded: {component.LicenseId}");
                    sb.AppendLine($"PackageLicenseDeclared: {component.LicenseId}");
                }
                else
                {
                    sb.AppendLine("PackageLicenseConcluded: NOASSERTION");
                    sb.AppendLine("PackageLicenseDeclared: NOASSERTION");
                }

                sb.AppendLine("PackageCopyrightText: NOASSERTION");
                sb.AppendLine();
            }

            // Relationships
            sb.AppendLine("##### Relationships");
            sb.AppendLine();
            sb.AppendLine("Relationship: SPDXRef-DOCUMENT DESCRIBES SPDXRef-RootPackage");

            foreach (var component in components.Where(c => c.IsFirstParty))
            {
                sb.AppendLine(
                    $"Relationship: SPDXRef-RootPackage DEPENDS_ON SPDXRef-Package-{SanitizeSpdxId(component.Name)}");
            }

            foreach (var component in components)
            {
                foreach (var dep in component.Dependencies)
                {
                    var resolved = components.FirstOrDefault(c =>
                        string.Equals(c.Name, dep, StringComparison.OrdinalIgnoreCase));
                    if (resolved != null)
                    {
                        sb.AppendLine(
                            $"Relationship: SPDXRef-Package-{SanitizeSpdxId(component.Name)} DEPENDS_ON SPDXRef-Package-{SanitizeSpdxId(resolved.Name)}");
                    }
                }
            }

            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────

        private static string SanitizeSpdxId(string name)
        {
            // SPDX identifiers can only contain letters, numbers, '.', and '-'
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '-');
            }
            return sb.ToString();
        }

        private static string EscapeXml(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        /// <summary>
        /// Internal record for deps.json package metadata.
        /// </summary>
        private sealed class DepsJsonPackageInfo
        {
            public string PackageName { get; init; } = string.Empty;
            public string Version { get; init; } = string.Empty;
            public string? LicenseId { get; init; }
        }
    }
}
