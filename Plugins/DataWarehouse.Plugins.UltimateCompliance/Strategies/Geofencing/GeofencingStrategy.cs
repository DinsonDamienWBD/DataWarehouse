using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// Geofencing strategy for data sovereignty enforcement.
    /// Ensures data remains within authorized geographic boundaries based on regulatory requirements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Geofencing enforces:
    /// - Data residency: Data must be stored in specific regions
    /// - Data localization: Processing must occur in specific jurisdictions
    /// - Cross-border transfer restrictions: Data cannot leave certain regions
    /// - Sovereignty requirements: Data must remain under specific legal jurisdictions
    /// </para>
    /// <para>
    /// Regulatory support:
    /// - GDPR: EU data protection and transfer restrictions
    /// - China PIPL: Chinese data localization requirements
    /// - Russia Data Localization: Russian personal data residency
    /// - Brazil LGPD: Brazilian data protection
    /// - India PDPB: Indian data localization
    /// - Australia Privacy Act: Australian data handling
    /// </para>
    /// </remarks>
    public sealed class GeofencingStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, GeofenceZone> _zones = new BoundedDictionary<string, GeofenceZone>(1000);
        private readonly BoundedDictionary<string, DataClassificationPolicy> _policies = new BoundedDictionary<string, DataClassificationPolicy>(1000);
        private readonly BoundedDictionary<string, List<string>> _dataResidencyRequirements = new BoundedDictionary<string, List<string>>(1000);

        // Regulatory zone mappings
        private static readonly Dictionary<string, HashSet<string>> _regulatoryZones = new()
        {
            ["EU"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
                "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
                "PL", "PT", "RO", "SK", "SI", "ES", "SE"
            },
            ["EEA"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
                "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
                "PL", "PT", "RO", "SK", "SI", "ES", "SE", "IS", "LI", "NO"
            },
            ["GDPR-ADEQUATE"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // EU/EEA plus adequacy decisions
                "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
                "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
                "PL", "PT", "RO", "SK", "SI", "ES", "SE", "IS", "LI", "NO",
                // Adequacy decisions
                "AD", "AR", "CA", "FO", "GG", "IL", "IM", "JP", "JE", "NZ",
                "KR", "CH", "GB", "UY", "US" // US with DPF
            },
            ["FIVE-EYES"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "US", "GB", "CA", "AU", "NZ"
            },
            ["APAC"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AU", "CN", "HK", "IN", "ID", "JP", "MY", "NZ", "PH", "SG",
                "KR", "TW", "TH", "VN"
            }
        };

        /// <inheritdoc/>
        public override string StrategyId => "geofencing";

        /// <inheritdoc/>
        public override string StrategyName => "Data Sovereignty Geofencing";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load zone configurations
            if (configuration.TryGetValue("Zones", out var zonesObj) &&
                zonesObj is IEnumerable<Dictionary<string, object>> zoneConfigs)
            {
                foreach (var config in zoneConfigs)
                {
                    var zone = ParseZoneFromConfig(config);
                    if (zone != null)
                    {
                        _zones[zone.Id] = zone;
                    }
                }
            }

            // Load data classification policies
            if (configuration.TryGetValue("Policies", out var policiesObj) &&
                policiesObj is IEnumerable<Dictionary<string, object>> policyConfigs)
            {
                foreach (var config in policyConfigs)
                {
                    var policy = ParsePolicyFromConfig(config);
                    if (policy != null)
                    {
                        _policies[policy.DataClassification] = policy;
                    }
                }
            }

            // Initialize default policies if none provided
            if (!_policies.Any())
            {
                InitializeDefaultPolicies();
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Defines a geofence zone.
        /// </summary>
        public void DefineZone(GeofenceZone zone)
        {
            _zones[zone.Id] = zone;
        }

        /// <summary>
        /// Defines a data classification policy.
        /// </summary>
        public void DefinePolicy(DataClassificationPolicy policy)
        {
            _policies[policy.DataClassification] = policy;
        }

        /// <summary>
        /// Sets data residency requirements for a resource.
        /// </summary>
        public void SetResidencyRequirement(string resourceId, IEnumerable<string> allowedRegions)
        {
            _dataResidencyRequirements[resourceId] = allowedRegions.ToList();
        }

        /// <summary>
        /// Checks if a location is within a specified regulatory zone.
        /// </summary>
        public bool IsInRegulatoryZone(string countryCode, string zoneName)
        {
            if (_regulatoryZones.TryGetValue(zoneName, out var countries))
            {
                return countries.Contains(countryCode);
            }

            // Check custom zones
            if (_zones.TryGetValue(zoneName, out var customZone))
            {
                return customZone.Countries.Contains(countryCode, StringComparer.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("geofencing.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Get applicable policy
            var policy = GetPolicyForClassification(context.DataClassification);

            // Check data residency
            if (context.DestinationLocation != null)
            {
                var residencyResult = CheckDataResidency(context, policy);
                violations.AddRange(residencyResult.Violations);
                recommendations.AddRange(residencyResult.Recommendations);
            }

            // Check cross-border transfer
            if (context.SourceLocation != null && context.DestinationLocation != null &&
                !context.SourceLocation.Equals(context.DestinationLocation, StringComparison.OrdinalIgnoreCase))
            {
                var transferResult = CheckCrossBorderTransfer(context, policy);
                violations.AddRange(transferResult.Violations);
                recommendations.AddRange(transferResult.Recommendations);
            }

            // Check resource-specific requirements
            if (context.ResourceId != null)
            {
                var resourceResult = CheckResourceRequirements(context);
                violations.AddRange(resourceResult.Violations);
                recommendations.AddRange(resourceResult.Recommendations);
            }

            // Check regulatory zone compliance
            var zoneResult = CheckRegulatoryZoneCompliance(context, policy);
            violations.AddRange(zoneResult.Violations);
            recommendations.AddRange(zoneResult.Recommendations);

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        hasHighViolations ? ComplianceStatus.NonCompliant :
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
                    ["SourceLocation"] = context.SourceLocation ?? "unknown",
                    ["DestinationLocation"] = context.DestinationLocation ?? "unknown",
                    ["DataClassification"] = context.DataClassification,
                    ["PolicyApplied"] = policy?.DataClassification ?? "default",
                    ["ViolationCount"] = violations.Count
                }
            });
        }

        private DataClassificationPolicy? GetPolicyForClassification(string classification)
        {
            if (_policies.TryGetValue(classification, out var policy))
                return policy;

            // Try to find a matching policy by prefix
            foreach (var (key, value) in _policies)
            {
                if (classification.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    return value;
            }

            // Return default policy
            return _policies.TryGetValue("default", out var defaultPolicy) ? defaultPolicy : null;
        }

        private (List<ComplianceViolation> Violations, List<string> Recommendations) CheckDataResidency(
            ComplianceContext context, DataClassificationPolicy? policy)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (policy == null)
                return (violations, recommendations);

            var destination = context.DestinationLocation?.ToUpperInvariant();
            if (string.IsNullOrEmpty(destination))
                return (violations, recommendations);

            // Check allowed regions
            if (policy.AllowedRegions.Any())
            {
                bool isAllowed = false;

                foreach (var region in policy.AllowedRegions)
                {
                    if (region.Equals(destination, StringComparison.OrdinalIgnoreCase))
                    {
                        isAllowed = true;
                        break;
                    }

                    // Check if it's a regulatory zone
                    if (IsInRegulatoryZone(destination, region))
                    {
                        isAllowed = true;
                        break;
                    }
                }

                if (!isAllowed)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GEO-001",
                        Description = $"Data cannot be stored in {destination}. Allowed regions: {string.Join(", ", policy.AllowedRegions)}",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = $"Store data in one of the allowed regions: {string.Join(", ", policy.AllowedRegions)}",
                        RegulatoryReference = policy.RegulatoryReferences.FirstOrDefault()
                    });
                }
            }

            // Check prohibited regions
            foreach (var prohibited in policy.ProhibitedRegions)
            {
                if (prohibited.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
                    IsInRegulatoryZone(destination, prohibited))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GEO-002",
                        Description = $"Data storage is prohibited in region: {destination}",
                        Severity = ViolationSeverity.Critical,
                        AffectedResource = context.ResourceId,
                        Remediation = "Remove data from prohibited region immediately",
                        RegulatoryReference = policy.RegulatoryReferences.FirstOrDefault()
                    });
                }
            }

            return (violations, recommendations);
        }

        private (List<ComplianceViolation> Violations, List<string> Recommendations) CheckCrossBorderTransfer(
            ComplianceContext context, DataClassificationPolicy? policy)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (policy == null)
                return (violations, recommendations);

            var source = context.SourceLocation?.ToUpperInvariant() ?? "";
            var destination = context.DestinationLocation?.ToUpperInvariant() ?? "";

            // Check if transfer is allowed
            if (!policy.AllowCrossBorderTransfer)
            {
                if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GEO-003",
                        Description = $"Cross-border transfer not allowed for {context.DataClassification} data",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Process data in the same region as the source",
                        RegulatoryReference = policy.RegulatoryReferences.FirstOrDefault()
                    });
                }

                return (violations, recommendations);
            }

            // Check if SCCs or adequate safeguards are required
            bool sourceInEU = IsInRegulatoryZone(source, "EU");
            bool destinationInEU = IsInRegulatoryZone(destination, "EU");
            bool destinationAdequate = IsInRegulatoryZone(destination, "GDPR-ADEQUATE");

            if (sourceInEU && !destinationInEU && !destinationAdequate)
            {
                if (!policy.HasAdequacyDecision && !policy.HasStandardContractualClauses)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GEO-004",
                        Description = $"Transfer from EU to {destination} requires legal basis (adequacy decision, SCCs, or BCRs)",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Implement Standard Contractual Clauses or obtain derogation",
                        RegulatoryReference = "GDPR Article 46"
                    });
                }
                else
                {
                    recommendations.Add($"Document legal basis for transfer to {destination}");
                }
            }

            // Check for specific regulatory requirements
            if (source == "CN" || destination == "CN")
            {
                recommendations.Add("China PIPL may require security assessment for cross-border transfer");
            }

            if (source == "RU" && destination != "RU")
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "GEO-005",
                    Description = "Russian personal data may need to be primarily stored in Russia",
                    Severity = ViolationSeverity.Medium,
                    AffectedResource = context.ResourceId,
                    Remediation = "Ensure primary copy of Russian citizen data remains in Russia",
                    RegulatoryReference = "Russian Federal Law No. 242-FZ"
                });
            }

            return (violations, recommendations);
        }

        private (List<ComplianceViolation> Violations, List<string> Recommendations) CheckResourceRequirements(
            ComplianceContext context)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (context.ResourceId == null)
                return (violations, recommendations);

            if (_dataResidencyRequirements.TryGetValue(context.ResourceId, out var requirements))
            {
                var destination = context.DestinationLocation?.ToUpperInvariant();
                if (!string.IsNullOrEmpty(destination) && !requirements.Contains(destination, StringComparer.OrdinalIgnoreCase))
                {
                    // Check if any requirement is a zone that includes the destination
                    bool satisfiesRequirement = requirements.Any(r => IsInRegulatoryZone(destination, r));

                    if (!satisfiesRequirement)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "GEO-006",
                            Description = $"Resource {context.ResourceId} must remain in: {string.Join(", ", requirements)}",
                            Severity = ViolationSeverity.High,
                            AffectedResource = context.ResourceId,
                            Remediation = $"Move data to allowed region: {string.Join(", ", requirements)}"
                        });
                    }
                }
            }

            return (violations, recommendations);
        }

        private (List<ComplianceViolation> Violations, List<string> Recommendations) CheckRegulatoryZoneCompliance(
            ComplianceContext context, DataClassificationPolicy? policy)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (policy == null)
                return (violations, recommendations);

            var destination = context.DestinationLocation?.ToUpperInvariant();
            if (string.IsNullOrEmpty(destination))
                return (violations, recommendations);

            // Check required zones
            foreach (var requiredZone in policy.RequiredRegulatoryZones)
            {
                if (!IsInRegulatoryZone(destination, requiredZone))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GEO-007",
                        Description = $"Data must be in regulatory zone: {requiredZone}",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = $"Store data in a country within the {requiredZone} zone"
                    });
                }
            }

            // Check prohibited zones
            foreach (var prohibitedZone in policy.ProhibitedRegulatoryZones)
            {
                if (IsInRegulatoryZone(destination, prohibitedZone))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GEO-008",
                        Description = $"Data cannot be in regulatory zone: {prohibitedZone}",
                        Severity = ViolationSeverity.Critical,
                        AffectedResource = context.ResourceId,
                        Remediation = $"Remove data from all countries in the {prohibitedZone} zone"
                    });
                }
            }

            return (violations, recommendations);
        }

        private void InitializeDefaultPolicies()
        {
            // GDPR-compliant policy for EU personal data
            _policies["personal-eu"] = new DataClassificationPolicy
            {
                DataClassification = "personal-eu",
                AllowedRegions = new List<string> { "EU", "EEA", "GDPR-ADEQUATE" },
                ProhibitedRegions = new List<string>(),
                RequiredRegulatoryZones = new List<string> { "GDPR-ADEQUATE" },
                ProhibitedRegulatoryZones = new List<string>(),
                AllowCrossBorderTransfer = true,
                HasAdequacyDecision = false,
                HasStandardContractualClauses = false,
                RegulatoryReferences = new List<string> { "GDPR Article 44-49" }
            };

            // Sensitive personal data
            _policies["sensitive-personal"] = new DataClassificationPolicy
            {
                DataClassification = "sensitive-personal",
                AllowedRegions = new List<string> { "EU", "EEA" },
                ProhibitedRegions = new List<string>(),
                RequiredRegulatoryZones = new List<string> { "EU" },
                ProhibitedRegulatoryZones = new List<string>(),
                AllowCrossBorderTransfer = false,
                HasAdequacyDecision = false,
                HasStandardContractualClauses = false,
                RegulatoryReferences = new List<string> { "GDPR Article 9" }
            };

            // Default permissive policy
            _policies["default"] = new DataClassificationPolicy
            {
                DataClassification = "default",
                AllowedRegions = new List<string>(),
                ProhibitedRegions = new List<string>(),
                RequiredRegulatoryZones = new List<string>(),
                ProhibitedRegulatoryZones = new List<string>(),
                AllowCrossBorderTransfer = true,
                HasAdequacyDecision = true,
                HasStandardContractualClauses = true,
                RegulatoryReferences = new List<string>()
            };
        }

        private GeofenceZone? ParseZoneFromConfig(Dictionary<string, object> config)
        {
            try
            {
                return new GeofenceZone
                {
                    Id = config["Id"]?.ToString() ?? Guid.NewGuid().ToString(),
                    Name = config["Name"]?.ToString() ?? "Unnamed Zone",
                    Countries = (config.TryGetValue("Countries", out var c) && c is IEnumerable<string> countries)
                        ? countries.ToList()
                        : new List<string>()
                };
            }
            catch
            {
                return null;
            }
        }

        private DataClassificationPolicy? ParsePolicyFromConfig(Dictionary<string, object> config)
        {
            try
            {
                return new DataClassificationPolicy
                {
                    DataClassification = config["DataClassification"]?.ToString() ?? "default",
                    AllowedRegions = (config.TryGetValue("AllowedRegions", out var ar) && ar is IEnumerable<string> allowed)
                        ? allowed.ToList() : new List<string>(),
                    ProhibitedRegions = (config.TryGetValue("ProhibitedRegions", out var pr) && pr is IEnumerable<string> prohibited)
                        ? prohibited.ToList() : new List<string>(),
                    RequiredRegulatoryZones = (config.TryGetValue("RequiredZones", out var rz) && rz is IEnumerable<string> required)
                        ? required.ToList() : new List<string>(),
                    ProhibitedRegulatoryZones = (config.TryGetValue("ProhibitedZones", out var pz) && pz is IEnumerable<string> prohibitedZones)
                        ? prohibitedZones.ToList() : new List<string>(),
                    AllowCrossBorderTransfer = !config.TryGetValue("AllowCrossBorder", out var acb) || acb is true,
                    RegulatoryReferences = (config.TryGetValue("RegulatoryReferences", out var rr) && rr is IEnumerable<string> refs)
                        ? refs.ToList() : new List<string>()
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
            IncrementCounter("geofencing.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("geofencing.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Represents a geofence zone.
    /// </summary>
    public record GeofenceZone
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public List<string> Countries { get; init; } = new();
        public string? Description { get; init; }
    }

    /// <summary>
    /// Policy for a data classification defining geographic restrictions.
    /// </summary>
    public record DataClassificationPolicy
    {
        public required string DataClassification { get; init; }
        public List<string> AllowedRegions { get; init; } = new();
        public List<string> ProhibitedRegions { get; init; } = new();
        public List<string> RequiredRegulatoryZones { get; init; } = new();
        public List<string> ProhibitedRegulatoryZones { get; init; } = new();
        public bool AllowCrossBorderTransfer { get; init; }
        public bool HasAdequacyDecision { get; init; }
        public bool HasStandardContractualClauses { get; init; }
        public List<string> RegulatoryReferences { get; init; } = new();
    }
}
