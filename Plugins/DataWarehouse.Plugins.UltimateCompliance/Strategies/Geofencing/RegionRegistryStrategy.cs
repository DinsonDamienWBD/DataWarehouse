using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77.2: Region Registry Strategy
    /// Defines and manages geographic regions (EU, US, APAC, etc.) for sovereignty enforcement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Predefined regulatory regions (EU, EEA, APAC, LATAM, etc.)
    /// - Custom region definitions
    /// - Hierarchical region relationships (country in region in super-region)
    /// - Temporal validity (regions that change over time)
    /// - Treaty-based groupings (adequacy decisions, data sharing agreements)
    /// </para>
    /// </remarks>
    public sealed class RegionRegistryStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, RegionDefinition> _regions = new BoundedDictionary<string, RegionDefinition>(1000);
        private readonly BoundedDictionary<string, CountryInfo> _countries = new BoundedDictionary<string, CountryInfo>(1000);
        private readonly BoundedDictionary<string, List<string>> _countryToRegions = new BoundedDictionary<string, List<string>>(1000);
        private readonly BoundedDictionary<string, DataSharingAgreement> _agreements = new BoundedDictionary<string, DataSharingAgreement>(1000);

        /// <inheritdoc/>
        public override string StrategyId => "region-registry";

        /// <inheritdoc/>
        public override string StrategyName => "Region Registry";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Initialize predefined regions
            InitializePredefinedRegions();
            InitializeCountries();
            InitializeDataSharingAgreements();

            // Load custom regions from configuration
            if (configuration.TryGetValue("CustomRegions", out var customObj) &&
                customObj is IEnumerable<Dictionary<string, object>> customRegions)
            {
                foreach (var regionConfig in customRegions)
                {
                    var region = ParseRegionFromConfig(regionConfig);
                    if (region != null)
                    {
                        RegisterRegion(region);
                    }
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Registers a new region definition.
        /// </summary>
        public void RegisterRegion(RegionDefinition region)
        {
            _regions[region.RegionCode] = region;

            // Update country-to-region mappings
            foreach (var country in region.MemberCountries)
            {
                _countryToRegions.AddOrUpdate(
                    country.ToUpperInvariant(),
                    _ => new List<string> { region.RegionCode },
                    (_, list) =>
                    {
                        if (!list.Contains(region.RegionCode))
                            list.Add(region.RegionCode);
                        return list;
                    });
            }
        }

        /// <summary>
        /// Gets a region by its code.
        /// </summary>
        public RegionDefinition? GetRegion(string regionCode)
        {
            return _regions.TryGetValue(regionCode.ToUpperInvariant(), out var region) ? region : null;
        }

        /// <summary>
        /// Gets all regions containing a country.
        /// </summary>
        public IReadOnlyList<RegionDefinition> GetRegionsForCountry(string countryCode)
        {
            var code = countryCode.ToUpperInvariant();
            if (!_countryToRegions.TryGetValue(code, out var regionCodes))
            {
                return Array.Empty<RegionDefinition>();
            }

            return regionCodes
                .Select(rc => _regions.TryGetValue(rc, out var r) ? r : null)
                .Where(r => r != null)
                .Cast<RegionDefinition>()
                .ToList();
        }

        /// <summary>
        /// Checks if a country is a member of a region.
        /// </summary>
        public bool IsCountryInRegion(string countryCode, string regionCode)
        {
            var region = GetRegion(regionCode);
            if (region == null) return false;

            var code = countryCode.ToUpperInvariant();

            // Direct membership
            if (region.MemberCountries.Contains(code, StringComparer.OrdinalIgnoreCase))
                return true;

            // Check parent regions
            foreach (var parentCode in region.ParentRegions)
            {
                if (IsCountryInRegion(code, parentCode))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Gets all countries in a region (including nested regions).
        /// </summary>
        public IReadOnlySet<string> GetCountriesInRegion(string regionCode)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectCountriesRecursive(regionCode, result, new HashSet<string>());
            return result;
        }

        /// <summary>
        /// Checks if data transfer between regions is allowed based on agreements.
        /// </summary>
        public DataTransferAssessment AssessDataTransfer(string sourceCountry, string destCountry, string dataType)
        {
            var sourceRegions = GetRegionsForCountry(sourceCountry);
            var destRegions = GetRegionsForCountry(destCountry);

            // Same country is always allowed
            if (sourceCountry.Equals(destCountry, StringComparison.OrdinalIgnoreCase))
            {
                return new DataTransferAssessment
                {
                    IsAllowed = true,
                    Basis = "Same country",
                    RequiresAdditionalSafeguards = false,
                    ApplicableAgreements = Array.Empty<string>()
                };
            }

            // Check for common regions
            var commonRegions = sourceRegions
                .Select(r => r.RegionCode)
                .Intersect(destRegions.Select(r => r.RegionCode))
                .ToList();

            if (commonRegions.Count > 0)
            {
                return new DataTransferAssessment
                {
                    IsAllowed = true,
                    Basis = $"Common region membership: {string.Join(", ", commonRegions)}",
                    RequiresAdditionalSafeguards = false,
                    ApplicableAgreements = Array.Empty<string>()
                };
            }

            // Check data sharing agreements
            var applicableAgreements = FindApplicableAgreements(sourceCountry, destCountry, dataType);
            if (applicableAgreements.Count > 0)
            {
                var agreement = applicableAgreements.First();
                return new DataTransferAssessment
                {
                    IsAllowed = true,
                    Basis = $"Data sharing agreement: {agreement.AgreementName}",
                    RequiresAdditionalSafeguards = agreement.RequiresSafeguards,
                    SafeguardsRequired = agreement.RequiredSafeguards,
                    ApplicableAgreements = applicableAgreements.Select(a => a.AgreementName).ToList()
                };
            }

            // No basis found
            return new DataTransferAssessment
            {
                IsAllowed = false,
                Basis = "No legal basis for transfer",
                RequiresAdditionalSafeguards = true,
                SafeguardsRequired = new[] { "Standard Contractual Clauses", "Binding Corporate Rules", "Explicit Consent" },
                ApplicableAgreements = Array.Empty<string>()
            };
        }

        /// <summary>
        /// Gets country information.
        /// </summary>
        public CountryInfo? GetCountryInfo(string countryCode)
        {
            return _countries.TryGetValue(countryCode.ToUpperInvariant(), out var info) ? info : null;
        }

        /// <summary>
        /// Gets all registered regions.
        /// </summary>
        public IReadOnlyCollection<RegionDefinition> GetAllRegions() => _regions.Values.ToList();

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("region_registry.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            var source = context.SourceLocation?.ToUpperInvariant() ?? "";
            var dest = context.DestinationLocation?.ToUpperInvariant() ?? "";

            // Check if locations are valid
            if (!string.IsNullOrEmpty(source) && GetCountryInfo(source) == null && GetRegion(source) == null)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "REG-001",
                    Description = $"Unknown source location: {source}",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Register the location in the region registry or use a valid country code"
                });
            }

            if (!string.IsNullOrEmpty(dest) && GetCountryInfo(dest) == null && GetRegion(dest) == null)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "REG-002",
                    Description = $"Unknown destination location: {dest}",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Register the location in the region registry or use a valid country code"
                });
            }

            // Assess transfer if both locations provided
            if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(dest))
            {
                var assessment = AssessDataTransfer(source, dest, context.DataClassification);

                if (!assessment.IsAllowed)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "REG-003",
                        Description = $"No valid basis for data transfer from {source} to {dest}",
                        Severity = ViolationSeverity.High,
                        Remediation = $"Implement required safeguards: {string.Join(", ", assessment.SafeguardsRequired ?? Array.Empty<string>())}",
                        RegulatoryReference = "Cross-border transfer rules"
                    });
                }
                else if (assessment.RequiresAdditionalSafeguards)
                {
                    recommendations.Add($"Transfer allowed via {assessment.Basis}, but additional safeguards required: {string.Join(", ", assessment.SafeguardsRequired ?? Array.Empty<string>())}");
                }
            }

            // Check for regions with temporal validity
            foreach (var region in _regions.Values.Where(r => r.ValidUntil.HasValue))
            {
                if (region.ValidUntil!.Value < DateTime.UtcNow)
                {
                    recommendations.Add($"Region {region.RegionCode} validity has expired as of {region.ValidUntil.Value:d}. Review region membership.");
                }
                else if (region.ValidUntil.Value < DateTime.UtcNow.AddDays(90))
                {
                    recommendations.Add($"Region {region.RegionCode} validity expires on {region.ValidUntil.Value:d}. Plan for potential changes.");
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["RegisteredRegions"] = _regions.Count,
                    ["RegisteredCountries"] = _countries.Count,
                    ["DataSharingAgreements"] = _agreements.Count
                }
            });
        }

        private void CollectCountriesRecursive(string regionCode, HashSet<string> result, HashSet<string> visited)
        {
            if (visited.Contains(regionCode)) return;
            visited.Add(regionCode);

            if (!_regions.TryGetValue(regionCode.ToUpperInvariant(), out var region))
                return;

            foreach (var country in region.MemberCountries)
            {
                result.Add(country.ToUpperInvariant());
            }

            foreach (var childCode in region.ChildRegions)
            {
                CollectCountriesRecursive(childCode, result, visited);
            }
        }

        private List<DataSharingAgreement> FindApplicableAgreements(string source, string dest, string dataType)
        {
            var sourceRegions = GetRegionsForCountry(source).Select(r => r.RegionCode).ToHashSet();
            sourceRegions.Add(source.ToUpperInvariant());

            var destRegions = GetRegionsForCountry(dest).Select(r => r.RegionCode).ToHashSet();
            destRegions.Add(dest.ToUpperInvariant());

            return _agreements.Values
                .Where(a => a.IsActive &&
                           sourceRegions.Overlaps(a.ParticipatingEntities) &&
                           destRegions.Overlaps(a.ParticipatingEntities) &&
                           (a.CoveredDataTypes.Count == 0 || a.CoveredDataTypes.Contains(dataType, StringComparer.OrdinalIgnoreCase)))
                .ToList();
        }

        private void InitializePredefinedRegions()
        {
            // European Union
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "EU",
                RegionName = "European Union",
                RegionType = RegionType.RegulatoryBlock,
                MemberCountries = new List<string>
                {
                    "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
                    "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
                    "PL", "PT", "RO", "SK", "SI", "ES", "SE"
                },
                RegulatoryFrameworks = new List<string> { "GDPR" }
            });

            // European Economic Area
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "EEA",
                RegionName = "European Economic Area",
                RegionType = RegionType.RegulatoryBlock,
                MemberCountries = new List<string> { "IS", "LI", "NO" },
                ParentRegions = new List<string> { "EU" },
                RegulatoryFrameworks = new List<string> { "GDPR" }
            });

            // GDPR Adequate Countries
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "GDPR-ADEQUATE",
                RegionName = "GDPR Adequacy Countries",
                RegionType = RegionType.DataSharingGroup,
                MemberCountries = new List<string>
                {
                    "AD", "AR", "CA", "FO", "GG", "IL", "IM", "JP", "JE", "NZ",
                    "KR", "CH", "GB", "UY", "US"
                },
                ParentRegions = new List<string> { "EEA" },
                RegulatoryFrameworks = new List<string> { "GDPR" },
                Notes = "US adequacy via Data Privacy Framework"
            });

            // Five Eyes
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "FIVE-EYES",
                RegionName = "Five Eyes Alliance",
                RegionType = RegionType.IntelligenceAlliance,
                MemberCountries = new List<string> { "US", "GB", "CA", "AU", "NZ" }
            });

            // Asia-Pacific
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "APAC",
                RegionName = "Asia-Pacific",
                RegionType = RegionType.Geographic,
                MemberCountries = new List<string>
                {
                    "AU", "CN", "HK", "IN", "ID", "JP", "MY", "NZ", "PH", "SG",
                    "KR", "TW", "TH", "VN", "BD", "PK", "LK", "MM", "KH", "LA"
                }
            });

            // Latin America
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "LATAM",
                RegionName = "Latin America",
                RegionType = RegionType.Geographic,
                MemberCountries = new List<string>
                {
                    "AR", "BO", "BR", "CL", "CO", "CR", "CU", "DO", "EC", "SV",
                    "GT", "HN", "MX", "NI", "PA", "PY", "PE", "PR", "UY", "VE"
                }
            });

            // Middle East and North Africa
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "MENA",
                RegionName = "Middle East and North Africa",
                RegionType = RegionType.Geographic,
                MemberCountries = new List<string>
                {
                    "DZ", "BH", "EG", "IR", "IQ", "IL", "JO", "KW", "LB", "LY",
                    "MA", "OM", "PS", "QA", "SA", "SY", "TN", "AE", "YE"
                }
            });

            // North America
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "NA",
                RegionName = "North America",
                RegionType = RegionType.Geographic,
                MemberCountries = new List<string> { "US", "CA", "MX" }
            });

            // USMCA (Replaces NAFTA)
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "USMCA",
                RegionName = "United States-Mexico-Canada Agreement",
                RegionType = RegionType.TradeBlock,
                MemberCountries = new List<string> { "US", "CA", "MX" }
            });

            // ASEAN
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "ASEAN",
                RegionName = "Association of Southeast Asian Nations",
                RegionType = RegionType.RegulatoryBlock,
                MemberCountries = new List<string>
                {
                    "BN", "KH", "ID", "LA", "MY", "MM", "PH", "SG", "TH", "VN"
                }
            });

            // China Data Localization Zone
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "CN-LOCAL",
                RegionName = "China Data Localization Zone",
                RegionType = RegionType.RegulatoryBlock,
                MemberCountries = new List<string> { "CN" },
                RegulatoryFrameworks = new List<string> { "PIPL", "CSL", "DSL" },
                Notes = "Strict data localization requirements for personal and important data"
            });

            // Russia Data Localization
            RegisterRegion(new RegionDefinition
            {
                RegionCode = "RU-LOCAL",
                RegionName = "Russia Data Localization Zone",
                RegionType = RegionType.RegulatoryBlock,
                MemberCountries = new List<string> { "RU" },
                RegulatoryFrameworks = new List<string> { "FZ-242", "FZ-152" },
                Notes = "Personal data of Russian citizens must be stored in Russia"
            });
        }

        private void InitializeCountries()
        {
            // Initialize major countries with data protection info
            var countries = new[]
            {
                new CountryInfo { CountryCode = "US", CountryName = "United States", DataProtectionLaws = new[] { "CCPA", "HIPAA", "GLBA" }, HasAdequacyDecision = true },
                new CountryInfo { CountryCode = "GB", CountryName = "United Kingdom", DataProtectionLaws = new[] { "UK GDPR", "DPA 2018" }, HasAdequacyDecision = true },
                new CountryInfo { CountryCode = "DE", CountryName = "Germany", DataProtectionLaws = new[] { "GDPR", "BDSG" }, HasAdequacyDecision = true },
                new CountryInfo { CountryCode = "FR", CountryName = "France", DataProtectionLaws = new[] { "GDPR", "LIL" }, HasAdequacyDecision = true },
                new CountryInfo { CountryCode = "JP", CountryName = "Japan", DataProtectionLaws = new[] { "APPI" }, HasAdequacyDecision = true },
                new CountryInfo { CountryCode = "CN", CountryName = "China", DataProtectionLaws = new[] { "PIPL", "CSL", "DSL" }, HasAdequacyDecision = false, RequiresLocalStorage = true },
                new CountryInfo { CountryCode = "IN", CountryName = "India", DataProtectionLaws = new[] { "DPDP" }, HasAdequacyDecision = false },
                new CountryInfo { CountryCode = "BR", CountryName = "Brazil", DataProtectionLaws = new[] { "LGPD" }, HasAdequacyDecision = false },
                new CountryInfo { CountryCode = "AU", CountryName = "Australia", DataProtectionLaws = new[] { "Privacy Act" }, HasAdequacyDecision = false },
                new CountryInfo { CountryCode = "CA", CountryName = "Canada", DataProtectionLaws = new[] { "PIPEDA" }, HasAdequacyDecision = true },
                new CountryInfo { CountryCode = "KR", CountryName = "South Korea", DataProtectionLaws = new[] { "PIPA" }, HasAdequacyDecision = true },
                new CountryInfo { CountryCode = "SG", CountryName = "Singapore", DataProtectionLaws = new[] { "PDPA" }, HasAdequacyDecision = false },
                new CountryInfo { CountryCode = "RU", CountryName = "Russia", DataProtectionLaws = new[] { "FZ-152" }, HasAdequacyDecision = false, RequiresLocalStorage = true },
            };

            foreach (var country in countries)
            {
                _countries[country.CountryCode] = country;
            }
        }

        private void InitializeDataSharingAgreements()
        {
            // EU-US Data Privacy Framework
            _agreements["DPF"] = new DataSharingAgreement
            {
                AgreementId = "DPF",
                AgreementName = "EU-US Data Privacy Framework",
                ParticipatingEntities = new List<string> { "EU", "EEA", "US" },
                EffectiveDate = new DateTime(2023, 7, 10),
                IsActive = true,
                RequiresSafeguards = false,
                CoveredDataTypes = new List<string> { "personal" }
            };

            // APEC Cross-Border Privacy Rules
            _agreements["CBPR"] = new DataSharingAgreement
            {
                AgreementId = "CBPR",
                AgreementName = "APEC Cross-Border Privacy Rules",
                ParticipatingEntities = new List<string> { "US", "JP", "CA", "MX", "KR", "SG", "AU", "TW", "PH" },
                IsActive = true,
                RequiresSafeguards = true,
                RequiredSafeguards = new[] { "CBPR Certification" },
                CoveredDataTypes = new List<string>()
            };

            // UK Adequacy
            _agreements["UK-ADEQUACY"] = new DataSharingAgreement
            {
                AgreementId = "UK-ADEQUACY",
                AgreementName = "EU-UK Adequacy Decision",
                ParticipatingEntities = new List<string> { "EU", "EEA", "GB" },
                EffectiveDate = new DateTime(2021, 6, 28),
                IsActive = true,
                RequiresSafeguards = false,
                CoveredDataTypes = new List<string> { "personal" }
            };
        }

        private RegionDefinition? ParseRegionFromConfig(Dictionary<string, object> config)
        {
            try
            {
                return new RegionDefinition
                {
                    RegionCode = config["RegionCode"]?.ToString() ?? "",
                    RegionName = config["RegionName"]?.ToString() ?? "",
                    RegionType = Enum.TryParse<RegionType>(config.GetValueOrDefault("RegionType")?.ToString(), out var rt) ? rt : RegionType.Custom,
                    MemberCountries = (config.TryGetValue("MemberCountries", out var mc) && mc is IEnumerable<string> countries)
                        ? countries.ToList() : new List<string>(),
                    ParentRegions = (config.TryGetValue("ParentRegions", out var pr) && pr is IEnumerable<string> parents)
                        ? parents.ToList() : new List<string>(),
                    ChildRegions = (config.TryGetValue("ChildRegions", out var cr) && cr is IEnumerable<string> children)
                        ? children.ToList() : new List<string>()
                };
            }
            catch
            {
                return null;
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("region_registry.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("region_registry.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Defines a geographic or regulatory region.
    /// </summary>
    public sealed record RegionDefinition
    {
        public required string RegionCode { get; init; }
        public required string RegionName { get; init; }
        public RegionType RegionType { get; init; } = RegionType.Geographic;
        public List<string> MemberCountries { get; init; } = new();
        public List<string> ParentRegions { get; init; } = new();
        public List<string> ChildRegions { get; init; } = new();
        public List<string> RegulatoryFrameworks { get; init; } = new();
        public DateTime? ValidFrom { get; init; }
        public DateTime? ValidUntil { get; init; }
        public string? Notes { get; init; }
    }

    /// <summary>
    /// Type of region.
    /// </summary>
    public enum RegionType
    {
        Geographic,
        RegulatoryBlock,
        TradeBlock,
        DataSharingGroup,
        IntelligenceAlliance,
        Custom
    }

    /// <summary>
    /// Information about a country's data protection regime.
    /// </summary>
    public sealed record CountryInfo
    {
        public required string CountryCode { get; init; }
        public required string CountryName { get; init; }
        public string[] DataProtectionLaws { get; init; } = Array.Empty<string>();
        public bool HasAdequacyDecision { get; init; }
        public bool RequiresLocalStorage { get; init; }
    }

    /// <summary>
    /// Data sharing agreement between entities.
    /// </summary>
    public sealed record DataSharingAgreement
    {
        public required string AgreementId { get; init; }
        public required string AgreementName { get; init; }
        public List<string> ParticipatingEntities { get; init; } = new();
        public DateTime? EffectiveDate { get; init; }
        public DateTime? ExpirationDate { get; init; }
        public bool IsActive { get; init; }
        public bool RequiresSafeguards { get; init; }
        public string[] RequiredSafeguards { get; init; } = Array.Empty<string>();
        public List<string> CoveredDataTypes { get; init; } = new();
    }

    /// <summary>
    /// Result of data transfer assessment.
    /// </summary>
    public sealed record DataTransferAssessment
    {
        public required bool IsAllowed { get; init; }
        public required string Basis { get; init; }
        public required bool RequiresAdditionalSafeguards { get; init; }
        public string[]? SafeguardsRequired { get; init; }
        public IReadOnlyList<string> ApplicableAgreements { get; init; } = Array.Empty<string>();
    }
}
