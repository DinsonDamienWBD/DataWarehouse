using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Provides import/export operations for policy templates, enabling community sharing of policy
    /// configurations across DataWarehouse deployments. The marketplace handles template serialization,
    /// integrity verification via SHA-256 checksums, and version compatibility validation.
    /// <para>
    /// <b>Export workflow:</b> Read all policies from an <see cref="IPolicyPersistence"/> backend,
    /// package them into a <see cref="PolicyTemplate"/> with metadata, serialize to portable JSON bytes.
    /// </para>
    /// <para>
    /// <b>Import workflow:</b> Deserialize JSON bytes, verify checksum integrity, validate engine version
    /// compatibility, then write policies into the target <see cref="IPolicyPersistence"/> backend.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <example>
    /// <code>
    /// // Export from one instance
    /// var marketplace = new PolicyMarketplace();
    /// var template = await marketplace.ExportTemplateAsync(persistence, "My HIPAA Profile",
    ///     "Strict HIPAA compliance policies", "admin@org.com", new Version(1, 0, 0));
    /// byte[] bytes = marketplace.SerializeTemplate(template);
    ///
    /// // Import into another instance
    /// var result = await marketplace.ImportFromBytesAsync(targetPersistence, bytes);
    /// if (!result.Success) Console.WriteLine(result.Error);
    /// </code>
    /// </example>
    /// </remarks>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Policy marketplace (PADV-01)")]
    public sealed class PolicyMarketplace
    {
        private static readonly JsonSerializerOptions s_templateOptions = CreateTemplateOptions();

        private readonly Version _currentEngineVersion;

        /// <summary>
        /// Initializes a new <see cref="PolicyMarketplace"/> instance with the specified engine version
        /// used for compatibility checking during import operations.
        /// </summary>
        /// <param name="currentEngineVersion">
        /// The running PolicyEngine version. Templates requiring a higher version will be rejected during import.
        /// Defaults to 6.0.0 if null.
        /// </param>
        public PolicyMarketplace(Version? currentEngineVersion = null)
        {
            _currentEngineVersion = currentEngineVersion ?? new Version(6, 0, 0);
        }

        /// <summary>
        /// Exports all policies and the active profile from the specified persistence backend into a
        /// portable <see cref="PolicyTemplate"/> with full metadata and integrity checksum.
        /// </summary>
        /// <param name="persistence">The persistence backend to export policies from.</param>
        /// <param name="name">Human-readable name for the template.</param>
        /// <param name="description">Description of the template's purpose and intended use.</param>
        /// <param name="author">Identity of the template creator.</param>
        /// <param name="templateVersion">Semantic version for this template release.</param>
        /// <param name="tags">Optional searchable tags for categorization.</param>
        /// <param name="targetFrameworks">Optional compliance frameworks this template targets.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A fully populated <see cref="PolicyTemplate"/> ready for serialization and sharing.</returns>
        /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
        public async Task<PolicyTemplate> ExportTemplateAsync(
            IPolicyPersistence persistence,
            string name,
            string description,
            string author,
            Version templateVersion,
            string[]? tags = null,
            string[]? targetFrameworks = null,
            CancellationToken ct = default)
        {
            if (persistence == null) throw new ArgumentNullException(nameof(persistence));
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (description == null) throw new ArgumentNullException(nameof(description));
            if (author == null) throw new ArgumentNullException(nameof(author));
            if (templateVersion == null) throw new ArgumentNullException(nameof(templateVersion));

            var allPolicies = await persistence.LoadAllAsync(ct).ConfigureAwait(false);
            var profile = await persistence.LoadProfileAsync(ct).ConfigureAwait(false);

            var policies = new List<FeaturePolicy>(allPolicies.Count);
            for (int i = 0; i < allPolicies.Count; i++)
            {
                policies.Add(allPolicies[i].Policy);
            }

            // Compute checksum from serialized policies for integrity verification
            byte[] serializedPolicies = PolicySerializationHelper.SerializePolicies(allPolicies);
            string checksum = ComputeSha256Hex(serializedPolicies);

            return new PolicyTemplate
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = name,
                Description = description,
                Author = author,
                TemplateVersion = templateVersion,
                MinEngineVersion = _currentEngineVersion,
                CreatedAt = DateTimeOffset.UtcNow,
                Tags = tags ?? Array.Empty<string>(),
                TargetComplianceFrameworks = targetFrameworks ?? Array.Empty<string>(),
                Policies = policies,
                Profile = profile,
                Checksum = checksum
            };
        }

        /// <summary>
        /// Serializes a <see cref="PolicyTemplate"/> to a JSON byte array suitable for storage or transmission.
        /// The output is human-readable (indented) with string-based enum serialization.
        /// </summary>
        /// <param name="template">The template to serialize.</param>
        /// <returns>UTF-8 JSON byte array representing the template.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="template"/> is null.</exception>
        public byte[] SerializeTemplate(PolicyTemplate template)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            return JsonSerializer.SerializeToUtf8Bytes(template, s_templateOptions);
        }

        /// <summary>
        /// Deserializes a JSON byte array to a <see cref="PolicyTemplate"/>, validating the integrity
        /// checksum if one is present in the template.
        /// </summary>
        /// <param name="data">UTF-8 JSON byte array to deserialize.</param>
        /// <returns>The deserialized <see cref="PolicyTemplate"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when deserialization produces null or when the checksum does not match the serialized policies.
        /// </exception>
        public PolicyTemplate DeserializeTemplate(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            var template = JsonSerializer.Deserialize<PolicyTemplate>(data, s_templateOptions)
                ?? throw new InvalidOperationException("Deserialization of PolicyTemplate produced null result.");

            // Validate checksum if present
            if (template.Checksum != null && template.Policies.Count > 0)
            {
                var tuples = PoliciesToTuples(template.Policies);
                byte[] serialized = PolicySerializationHelper.SerializePolicies(tuples);
                string computed = ComputeSha256Hex(serialized);

                if (!string.Equals(computed, template.Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Template integrity check failed: checksum mismatch");
                }
            }

            return template;
        }

        /// <summary>
        /// Checks whether the specified template is compatible with the current engine version.
        /// </summary>
        /// <param name="template">The template to check compatibility for.</param>
        /// <returns>A <see cref="PolicyTemplateCompatibility"/> indicating whether the template can be imported.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="template"/> is null.</exception>
        public PolicyTemplateCompatibility CheckCompatibility(PolicyTemplate template)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));

            if (template.MinEngineVersion > _currentEngineVersion)
            {
                return new PolicyTemplateCompatibility
                {
                    IsCompatible = false,
                    IncompatibilityReason = $"Template requires engine version {template.MinEngineVersion} but current version is {_currentEngineVersion}",
                    RequiredVersion = template.MinEngineVersion,
                    CurrentVersion = _currentEngineVersion
                };
            }

            if (template.Policies.Count == 0)
            {
                return new PolicyTemplateCompatibility
                {
                    IsCompatible = false,
                    IncompatibilityReason = "Template contains no policies"
                };
            }

            return new PolicyTemplateCompatibility
            {
                IsCompatible = true
            };
        }

        /// <summary>
        /// Imports a policy template into the specified persistence backend with optional overwrite control.
        /// Runs compatibility checks before importing. Skipped policies (when not overwriting duplicates)
        /// are reported as warnings.
        /// </summary>
        /// <param name="persistence">The target persistence backend to import into.</param>
        /// <param name="template">The template to import.</param>
        /// <param name="overwriteExisting">
        /// When true, existing policies with the same feature ID and level are overwritten.
        /// When false, existing policies are skipped and reported as warnings.
        /// </param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A <see cref="PolicyTemplateImportResult"/> with import statistics and any warnings.</returns>
        /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
        public async Task<PolicyTemplateImportResult> ImportTemplateAsync(
            IPolicyPersistence persistence,
            PolicyTemplate template,
            bool overwriteExisting = false,
            CancellationToken ct = default)
        {
            if (persistence == null) throw new ArgumentNullException(nameof(persistence));
            if (template == null) throw new ArgumentNullException(nameof(template));

            var compatibility = CheckCompatibility(template);
            if (!compatibility.IsCompatible)
            {
                return new PolicyTemplateImportResult
                {
                    Success = false,
                    Error = compatibility.IncompatibilityReason
                };
            }

            // Build lookup of existing policies when not overwriting
            HashSet<string>? existingKeys = null;
            if (!overwriteExisting)
            {
                var existing = await persistence.LoadAllAsync(ct).ConfigureAwait(false);
                existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < existing.Count; i++)
                {
                    existingKeys.Add($"{existing[i].FeatureId}:{existing[i].Level}");
                }
            }

            var warnings = new List<string>();
            int imported = 0;

            for (int i = 0; i < template.Policies.Count; i++)
            {
                var policy = template.Policies[i];
                string key = $"{policy.FeatureId}:{policy.Level}";

                if (!overwriteExisting && existingKeys != null && existingKeys.Contains(key))
                {
                    warnings.Add($"Skipped existing policy for '{policy.FeatureId}' at {policy.Level} level");
                    continue;
                }

                await persistence.SaveAsync(policy.FeatureId, policy.Level, "/", policy, ct).ConfigureAwait(false);
                imported++;
            }

            bool profileImported = false;
            if (template.Profile != null)
            {
                await persistence.SaveProfileAsync(template.Profile, ct).ConfigureAwait(false);
                profileImported = true;
            }

            return new PolicyTemplateImportResult
            {
                Success = true,
                PoliciesImported = imported,
                ProfileImported = profileImported,
                Warnings = warnings
            };
        }

        /// <summary>
        /// Convenience method that deserializes, validates, and imports a template from raw JSON bytes in a single call.
        /// Combines <see cref="DeserializeTemplate"/>, <see cref="CheckCompatibility"/>, and <see cref="ImportTemplateAsync"/>.
        /// </summary>
        /// <param name="persistence">The target persistence backend to import into.</param>
        /// <param name="templateData">UTF-8 JSON byte array containing the serialized template.</param>
        /// <param name="overwriteExisting">Whether to overwrite existing policies with the same key.</param>
        /// <param name="ct">Cancellation token for cooperative cancellation.</param>
        /// <returns>A <see cref="PolicyTemplateImportResult"/> with import statistics and any warnings.</returns>
        /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when template deserialization or integrity check fails.</exception>
        public async Task<PolicyTemplateImportResult> ImportFromBytesAsync(
            IPolicyPersistence persistence,
            byte[] templateData,
            bool overwriteExisting = false,
            CancellationToken ct = default)
        {
            if (persistence == null) throw new ArgumentNullException(nameof(persistence));
            if (templateData == null) throw new ArgumentNullException(nameof(templateData));

            var template = DeserializeTemplate(templateData);
            return await ImportTemplateAsync(persistence, template, overwriteExisting, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a built-in HIPAA compliance template with strict encryption, restrictive compression,
        /// and high replication. Suitable for healthcare environments requiring HIPAA compliance.
        /// </summary>
        /// <returns>A pre-configured <see cref="PolicyTemplate"/> targeting HIPAA compliance.</returns>
        /// <remarks>
        /// <list type="bullet">
        /// <item>Encryption: IntensityLevel=90, Cascade=Enforce, AI=ManualOnly</item>
        /// <item>Compression: IntensityLevel=70, Cascade=MostRestrictive, AI=Suggest</item>
        /// <item>Replication: IntensityLevel=80, Cascade=MostRestrictive</item>
        /// <item>Profile: OperationalProfile.Strict()</item>
        /// </list>
        /// </remarks>
        public static PolicyTemplate HipaaTemplate() => new()
        {
            Id = "00000000-0000-0000-0000-000000000001",
            Name = "HIPAA-Compliant-Standard",
            Description = "Strict encryption and replication policies for HIPAA-regulated healthcare environments. " +
                          "Enforces encryption at all levels, restricts AI autonomy, and ensures high data redundancy.",
            Author = "DataWarehouse Built-in",
            TemplateVersion = new Version(1, 0, 0),
            MinEngineVersion = new Version(6, 0, 0),
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Tags = new[] { "hipaa", "healthcare", "strict", "compliance" },
            TargetComplianceFrameworks = new[] { "HIPAA" },
            Policies = new FeaturePolicy[]
            {
                new()
                {
                    FeatureId = "encryption",
                    Level = PolicyLevel.VDE,
                    IntensityLevel = 90,
                    Cascade = CascadeStrategy.Enforce,
                    AiAutonomy = AiAutonomyLevel.ManualOnly
                },
                new()
                {
                    FeatureId = "compression",
                    Level = PolicyLevel.Object,
                    IntensityLevel = 70,
                    Cascade = CascadeStrategy.MostRestrictive,
                    AiAutonomy = AiAutonomyLevel.Suggest
                },
                new()
                {
                    FeatureId = "replication",
                    Level = PolicyLevel.Container,
                    IntensityLevel = 80,
                    Cascade = CascadeStrategy.MostRestrictive,
                    AiAutonomy = AiAutonomyLevel.Suggest
                }
            },
            Profile = OperationalProfile.Strict()
        };

        /// <summary>
        /// Creates a built-in GDPR compliance template with strong encryption and standard compression.
        /// Suitable for European environments requiring General Data Protection Regulation compliance.
        /// </summary>
        /// <returns>A pre-configured <see cref="PolicyTemplate"/> targeting GDPR compliance.</returns>
        /// <remarks>
        /// <list type="bullet">
        /// <item>Encryption: IntensityLevel=80, Cascade=Enforce</item>
        /// <item>Compression: IntensityLevel=50, Cascade=Inherit</item>
        /// <item>Replication: IntensityLevel=50, Cascade=MostRestrictive</item>
        /// </list>
        /// </remarks>
        public static PolicyTemplate GdprTemplate() => new()
        {
            Id = "00000000-0000-0000-0000-000000000002",
            Name = "GDPR-Standard",
            Description = "Strong encryption with standard compression for GDPR-regulated European environments. " +
                          "Enforces encryption and provides moderate replication for data durability.",
            Author = "DataWarehouse Built-in",
            TemplateVersion = new Version(1, 0, 0),
            MinEngineVersion = new Version(6, 0, 0),
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Tags = new[] { "gdpr", "europe", "privacy", "compliance" },
            TargetComplianceFrameworks = new[] { "GDPR" },
            Policies = new FeaturePolicy[]
            {
                new()
                {
                    FeatureId = "encryption",
                    Level = PolicyLevel.VDE,
                    IntensityLevel = 80,
                    Cascade = CascadeStrategy.Enforce,
                    AiAutonomy = AiAutonomyLevel.SuggestExplain
                },
                new()
                {
                    FeatureId = "compression",
                    Level = PolicyLevel.Object,
                    IntensityLevel = 50,
                    Cascade = CascadeStrategy.Inherit,
                    AiAutonomy = AiAutonomyLevel.AutoNotify
                },
                new()
                {
                    FeatureId = "replication",
                    Level = PolicyLevel.Container,
                    IntensityLevel = 50,
                    Cascade = CascadeStrategy.MostRestrictive,
                    AiAutonomy = AiAutonomyLevel.SuggestExplain
                }
            }
        };

        /// <summary>
        /// Creates a built-in high-performance template with minimal overhead. Suitable for
        /// throughput-sensitive workloads where security requirements are relaxed.
        /// </summary>
        /// <returns>A pre-configured <see cref="PolicyTemplate"/> optimized for speed.</returns>
        /// <remarks>
        /// <list type="bullet">
        /// <item>Encryption: IntensityLevel=30</item>
        /// <item>Compression: IntensityLevel=30</item>
        /// <item>Replication: IntensityLevel=20</item>
        /// <item>Profile: OperationalProfile.Speed()</item>
        /// </list>
        /// </remarks>
        public static PolicyTemplate HighPerformanceTemplate() => new()
        {
            Id = "00000000-0000-0000-0000-000000000003",
            Name = "High-Performance",
            Description = "Lightweight encryption and compression for throughput-sensitive workloads. " +
                          "Minimal overhead with AI fully autonomous for maximum speed.",
            Author = "DataWarehouse Built-in",
            TemplateVersion = new Version(1, 0, 0),
            MinEngineVersion = new Version(6, 0, 0),
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Tags = new[] { "performance", "speed", "throughput", "low-latency" },
            TargetComplianceFrameworks = Array.Empty<string>(),
            Policies = new FeaturePolicy[]
            {
                new()
                {
                    FeatureId = "encryption",
                    Level = PolicyLevel.VDE,
                    IntensityLevel = 30,
                    Cascade = CascadeStrategy.Inherit,
                    AiAutonomy = AiAutonomyLevel.AutoSilent
                },
                new()
                {
                    FeatureId = "compression",
                    Level = PolicyLevel.Object,
                    IntensityLevel = 30,
                    Cascade = CascadeStrategy.Inherit,
                    AiAutonomy = AiAutonomyLevel.AutoSilent
                },
                new()
                {
                    FeatureId = "replication",
                    Level = PolicyLevel.Container,
                    IntensityLevel = 20,
                    Cascade = CascadeStrategy.Inherit,
                    AiAutonomy = AiAutonomyLevel.AutoNotify
                }
            },
            Profile = OperationalProfile.Speed()
        };

        /// <summary>
        /// Converts a list of <see cref="FeaturePolicy"/> objects to the tuple format used by
        /// <see cref="PolicySerializationHelper.SerializePolicies"/>. Uses "/" as the default path.
        /// </summary>
        private static IReadOnlyList<(string FeatureId, PolicyLevel Level, string Path, FeaturePolicy Policy)> PoliciesToTuples(
            IReadOnlyList<FeaturePolicy> policies)
        {
            var tuples = new List<(string, PolicyLevel, string, FeaturePolicy)>(policies.Count);
            for (int i = 0; i < policies.Count; i++)
            {
                var p = policies[i];
                if (p is null)
                    throw new InvalidOperationException(
                        $"PolicyTemplate.Policies contains a null entry at index {i}. Templates must not contain null policy items.");
                tuples.Add((p.FeatureId, p.Level, "/", p));
            }
            return tuples;
        }

        /// <summary>
        /// Computes the SHA-256 hash of the specified data and returns it as a lowercase hexadecimal string.
        /// </summary>
        private static string ComputeSha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(data);

            // Convert to lowercase hex without allocations beyond the result string
            var chars = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                byte b = hash[i];
                chars[i * 2] = GetHexChar(b >> 4);
                chars[i * 2 + 1] = GetHexChar(b & 0x0F);
            }
            return new string(chars);
        }

        /// <summary>
        /// Returns the lowercase hexadecimal character for a nibble value (0-15).
        /// </summary>
        private static char GetHexChar(int nibble) =>
            (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);

        /// <summary>
        /// Creates JSON serializer options configured for policy template serialization.
        /// Uses indented output for human readability and string-based enum conversion.
        /// </summary>
        private static JsonSerializerOptions CreateTemplateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            options.Converters.Add(new VersionJsonConverter());

            return options;
        }

        /// <summary>
        /// Custom JSON converter for <see cref="Version"/> objects, serializing them as simple strings (e.g., "6.0.0").
        /// Required because <see cref="System.Text.Json"/> does not natively support <see cref="Version"/> serialization.
        /// </summary>
        private sealed class VersionJsonConverter : JsonConverter<Version>
        {
            /// <inheritdoc />
            public override Version Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                string? value = reader.GetString();
                if (value == null)
                    throw new JsonException("Version value cannot be null.");
                return Version.Parse(value);
            }

            /// <inheritdoc />
            public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString());
            }
        }
    }
}
