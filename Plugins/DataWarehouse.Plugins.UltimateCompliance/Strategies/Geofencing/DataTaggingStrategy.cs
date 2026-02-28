using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77.3: Data Tagging Strategy
    /// Tags objects with sovereignty requirements for compliance enforcement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Sovereignty tag assignment and management
    /// - Inheritance rules (container tags apply to contents)
    /// - Tag validation and conflict detection
    /// - Immutable audit trail of tag changes
    /// - Integration with classification systems
    /// </para>
    /// </remarks>
    public sealed class DataTaggingStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, SovereigntyTag> _tags = new BoundedDictionary<string, SovereigntyTag>(1000);
        private readonly BoundedDictionary<string, List<TagHistory>> _tagHistory = new BoundedDictionary<string, List<TagHistory>>(1000);
        private readonly BoundedDictionary<string, TagTemplate> _templates = new BoundedDictionary<string, TagTemplate>(1000);
        private readonly BoundedDictionary<string, List<string>> _inheritanceRules = new BoundedDictionary<string, List<string>>(1000);

        /// <inheritdoc/>
        public override string StrategyId => "data-tagging";

        /// <inheritdoc/>
        public override string StrategyName => "Data Tagging";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            InitializeDefaultTemplates();

            // Load custom templates
            if (configuration.TryGetValue("Templates", out var templatesObj) &&
                templatesObj is IEnumerable<Dictionary<string, object>> templates)
            {
                foreach (var templateConfig in templates)
                {
                    var template = ParseTemplateFromConfig(templateConfig);
                    if (template != null)
                    {
                        _templates[template.TemplateId] = template;
                    }
                }
            }

            // Load inheritance rules
            if (configuration.TryGetValue("InheritanceRules", out var rulesObj) &&
                rulesObj is Dictionary<string, IEnumerable<string>> rules)
            {
                foreach (var (parent, children) in rules)
                {
                    _inheritanceRules[parent] = children.ToList();
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Applies a sovereignty tag to a resource.
        /// </summary>
        public TaggingResult ApplyTag(string resourceId, SovereigntyTag tag, string appliedBy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceId, nameof(resourceId));
            ArgumentNullException.ThrowIfNull(tag, nameof(tag));

            // Validate the tag
            var validation = ValidateTag(tag);
            if (!validation.IsValid)
            {
                return new TaggingResult
                {
                    Success = false,
                    ResourceId = resourceId,
                    ErrorMessage = validation.ErrorMessage,
                    Conflicts = validation.Conflicts
                };
            }

            // Check for existing tag conflicts
            if (_tags.TryGetValue(resourceId, out var existingTag))
            {
                var conflicts = DetectConflicts(existingTag, tag);
                if (conflicts.Count > 0)
                {
                    return new TaggingResult
                    {
                        Success = false,
                        ResourceId = resourceId,
                        ErrorMessage = "Tag conflicts detected with existing tag",
                        Conflicts = conflicts
                    };
                }
            }

            // Apply the tag
            var appliedTag = tag with
            {
                AppliedAt = DateTime.UtcNow,
                AppliedBy = appliedBy,
                TagId = GenerateTagId(resourceId, tag)
            };

            _tags[resourceId] = appliedTag;

            // Record history
            RecordTagHistory(resourceId, appliedTag, TagOperation.Applied);

            // Apply inheritance
            if (_inheritanceRules.TryGetValue(resourceId, out var children))
            {
                foreach (var childId in children)
                {
                    ApplyInheritedTag(childId, appliedTag, appliedBy);
                }
            }

            return new TaggingResult
            {
                Success = true,
                ResourceId = resourceId,
                AppliedTag = appliedTag
            };
        }

        /// <summary>
        /// Removes a sovereignty tag from a resource.
        /// </summary>
        public TaggingResult RemoveTag(string resourceId, string removedBy, string reason)
        {
            if (!_tags.TryRemove(resourceId, out var removedTag))
            {
                return new TaggingResult
                {
                    Success = false,
                    ResourceId = resourceId,
                    ErrorMessage = "No tag found for resource"
                };
            }

            RecordTagHistory(resourceId, removedTag, TagOperation.Removed, reason);

            return new TaggingResult
            {
                Success = true,
                ResourceId = resourceId,
                RemovedTag = removedTag
            };
        }

        /// <summary>
        /// Gets the sovereignty tag for a resource.
        /// </summary>
        public SovereigntyTag? GetTag(string resourceId)
        {
            return _tags.TryGetValue(resourceId, out var tag) ? tag : null;
        }

        /// <summary>
        /// Gets effective tag considering inheritance.
        /// </summary>
        public SovereigntyTag? GetEffectiveTag(string resourceId, string? parentResourceId = null)
        {
            // Direct tag takes precedence
            if (_tags.TryGetValue(resourceId, out var directTag))
            {
                return directTag;
            }

            // Check parent inheritance
            if (!string.IsNullOrEmpty(parentResourceId) && _tags.TryGetValue(parentResourceId, out var parentTag))
            {
                return parentTag with { IsInherited = true, InheritedFrom = parentResourceId };
            }

            return null;
        }

        /// <summary>
        /// Applies a tag from a template.
        /// </summary>
        public TaggingResult ApplyTemplate(string resourceId, string templateId, string appliedBy, Dictionary<string, string>? parameters = null)
        {
            if (!_templates.TryGetValue(templateId, out var template))
            {
                return new TaggingResult
                {
                    Success = false,
                    ResourceId = resourceId,
                    ErrorMessage = $"Template not found: {templateId}"
                };
            }

            var tag = new SovereigntyTag
            {
                AllowedRegions = template.DefaultAllowedRegions.ToList(),
                ProhibitedRegions = template.DefaultProhibitedRegions.ToList(),
                DataClassification = parameters?.GetValueOrDefault("classification") ?? template.DefaultClassification,
                RetentionPolicy = template.DefaultRetentionPolicy,
                EncryptionRequired = template.RequiresEncryption,
                CrossBorderAllowed = template.AllowsCrossBorder,
                LegalBasis = template.DefaultLegalBasis,
                ExpirationDate = template.DefaultExpirationDays.HasValue
                    ? DateTime.UtcNow.AddDays(template.DefaultExpirationDays.Value)
                    : null,
                CustomAttributes = parameters?.Where(p => !p.Key.StartsWith("_"))
                    .ToDictionary(p => p.Key, p => (object)p.Value) ?? new Dictionary<string, object>()
            };

            return ApplyTag(resourceId, tag, appliedBy);
        }

        /// <summary>
        /// Gets tag history for a resource.
        /// </summary>
        public IReadOnlyList<TagHistory> GetTagHistory(string resourceId)
        {
            return _tagHistory.TryGetValue(resourceId, out var history)
                ? history.AsReadOnly()
                : Array.Empty<TagHistory>();
        }

        /// <summary>
        /// Validates a resource operation against its tag.
        /// </summary>
        public TagValidationResult ValidateOperation(string resourceId, string operation, string targetLocation)
        {
            var tag = GetTag(resourceId);
            if (tag == null)
            {
                return new TagValidationResult
                {
                    IsAllowed = true,
                    Reason = "No sovereignty tag applied"
                };
            }

            // Check expiration
            if (tag.ExpirationDate.HasValue && tag.ExpirationDate.Value < DateTime.UtcNow)
            {
                return new TagValidationResult
                {
                    IsAllowed = false,
                    Reason = "Tag has expired",
                    RequiredAction = "Renew or remove the sovereignty tag"
                };
            }

            // Check allowed regions
            if (tag.AllowedRegions.Count > 0 &&
                !tag.AllowedRegions.Contains(targetLocation, StringComparer.OrdinalIgnoreCase))
            {
                return new TagValidationResult
                {
                    IsAllowed = false,
                    Reason = $"Target location {targetLocation} not in allowed regions: {string.Join(", ", tag.AllowedRegions)}",
                    RequiredAction = "Choose a location from the allowed regions"
                };
            }

            // Check prohibited regions
            if (tag.ProhibitedRegions.Contains(targetLocation, StringComparer.OrdinalIgnoreCase))
            {
                return new TagValidationResult
                {
                    IsAllowed = false,
                    Reason = $"Target location {targetLocation} is prohibited",
                    RequiredAction = "Cannot perform operation to this location"
                };
            }

            // Check cross-border operations
            if (operation.Contains("transfer", StringComparison.OrdinalIgnoreCase) ||
                operation.Contains("replicate", StringComparison.OrdinalIgnoreCase))
            {
                if (!tag.CrossBorderAllowed)
                {
                    return new TagValidationResult
                    {
                        IsAllowed = false,
                        Reason = "Cross-border operations not allowed for this data",
                        RequiredAction = "Keep data within original jurisdiction"
                    };
                }
            }

            return new TagValidationResult
            {
                IsAllowed = true,
                Reason = "Operation complies with sovereignty tag"
            };
        }

        /// <summary>
        /// Gets all tagged resources matching criteria.
        /// </summary>
        public IReadOnlyList<(string ResourceId, SovereigntyTag Tag)> QueryTags(
            string? region = null,
            string? classification = null,
            bool? crossBorderAllowed = null)
        {
            return _tags
                .Where(kvp =>
                    (region == null || kvp.Value.AllowedRegions.Contains(region, StringComparer.OrdinalIgnoreCase)) &&
                    (classification == null || kvp.Value.DataClassification.Equals(classification, StringComparison.OrdinalIgnoreCase)) &&
                    (crossBorderAllowed == null || kvp.Value.CrossBorderAllowed == crossBorderAllowed))
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("data_tagging.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if resource has required tag
            if (context.ResourceId != null)
            {
                var tag = GetEffectiveTag(context.ResourceId);

                if (tag == null)
                {
                    if (RequiresTag(context.DataClassification))
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "TAG-001",
                            Description = $"Resource {context.ResourceId} with classification {context.DataClassification} requires sovereignty tag",
                            Severity = ViolationSeverity.High,
                            AffectedResource = context.ResourceId,
                            Remediation = "Apply appropriate sovereignty tag using a predefined template"
                        });
                    }
                    else
                    {
                        recommendations.Add($"Consider applying sovereignty tag to resource {context.ResourceId}");
                    }
                }
                else
                {
                    // Validate operation against tag
                    if (!string.IsNullOrEmpty(context.DestinationLocation))
                    {
                        var validation = ValidateOperation(context.ResourceId, context.OperationType, context.DestinationLocation);
                        if (!validation.IsAllowed)
                        {
                            violations.Add(new ComplianceViolation
                            {
                                Code = "TAG-002",
                                Description = validation.Reason,
                                Severity = ViolationSeverity.Critical,
                                AffectedResource = context.ResourceId,
                                Remediation = validation.RequiredAction
                            });
                        }
                    }

                    // Check tag expiration
                    if (tag.ExpirationDate.HasValue)
                    {
                        var daysUntilExpiry = (tag.ExpirationDate.Value - DateTime.UtcNow).TotalDays;
                        if (daysUntilExpiry < 0)
                        {
                            violations.Add(new ComplianceViolation
                            {
                                Code = "TAG-003",
                                Description = "Sovereignty tag has expired",
                                Severity = ViolationSeverity.High,
                                AffectedResource = context.ResourceId,
                                Remediation = "Renew the sovereignty tag"
                            });
                        }
                        else if (daysUntilExpiry < 30)
                        {
                            recommendations.Add($"Tag expires in {daysUntilExpiry:F0} days. Plan for renewal.");
                        }
                    }

                    // Check encryption requirement
                    if (tag.EncryptionRequired)
                    {
                        if (!context.Attributes.TryGetValue("IsEncrypted", out var encryptedObj) || encryptedObj is not true)
                        {
                            violations.Add(new ComplianceViolation
                            {
                                Code = "TAG-004",
                                Description = "Sovereignty tag requires encryption but data is not encrypted",
                                Severity = ViolationSeverity.High,
                                AffectedResource = context.ResourceId,
                                Remediation = "Encrypt the data before storage or transfer"
                            });
                        }
                    }
                }
            }

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
                    ["TotalTaggedResources"] = _tags.Count,
                    ["TemplatesAvailable"] = _templates.Count
                }
            });
        }

        private (bool IsValid, string? ErrorMessage, List<string> Conflicts) ValidateTag(SovereigntyTag tag)
        {
            var conflicts = new List<string>();

            // Check for region conflicts
            var overlap = tag.AllowedRegions.Intersect(tag.ProhibitedRegions, StringComparer.OrdinalIgnoreCase).ToList();
            if (overlap.Count > 0)
            {
                conflicts.Add($"Regions in both allowed and prohibited: {string.Join(", ", overlap)}");
            }

            // Validate classification
            if (string.IsNullOrWhiteSpace(tag.DataClassification))
            {
                return (false, "Data classification is required", conflicts);
            }

            if (conflicts.Count > 0)
            {
                return (false, "Tag has conflicts", conflicts);
            }

            return (true, null, conflicts);
        }

        private List<string> DetectConflicts(SovereigntyTag existing, SovereigntyTag newTag)
        {
            var conflicts = new List<string>();

            // Check if new tag is more restrictive in prohibited regions
            var newProhibited = newTag.ProhibitedRegions.Except(existing.ProhibitedRegions, StringComparer.OrdinalIgnoreCase).ToList();
            if (newProhibited.Count > 0)
            {
                // Check if any allowed regions would become prohibited
                var conflicting = existing.AllowedRegions.Intersect(newProhibited, StringComparer.OrdinalIgnoreCase).ToList();
                if (conflicting.Count > 0)
                {
                    conflicts.Add($"New tag prohibits currently allowed regions: {string.Join(", ", conflicting)}");
                }
            }

            return conflicts;
        }

        private void ApplyInheritedTag(string childId, SovereigntyTag parentTag, string appliedBy)
        {
            var inheritedTag = parentTag with
            {
                IsInherited = true,
                InheritedFrom = parentTag.TagId,
                AppliedAt = DateTime.UtcNow,
                AppliedBy = $"{appliedBy} (inherited)",
                TagId = GenerateTagId(childId, parentTag)
            };

            _tags[childId] = inheritedTag;
            RecordTagHistory(childId, inheritedTag, TagOperation.Inherited);
        }

        private void RecordTagHistory(string resourceId, SovereigntyTag tag, TagOperation operation, string? reason = null)
        {
            var history = new TagHistory
            {
                ResourceId = resourceId,
                TagId = tag.TagId ?? "",
                Operation = operation,
                Timestamp = DateTime.UtcNow,
                PerformedBy = tag.AppliedBy ?? "system",
                Reason = reason,
                TagSnapshot = JsonSerializer.Serialize(tag)
            };

            _tagHistory.AddOrUpdate(
                resourceId,
                _ => new List<TagHistory> { history },
                (_, list) =>
                {
                    list.Add(history);
                    return list;
                });
        }

        private static string GenerateTagId(string resourceId, SovereigntyTag tag)
        {
            var input = $"{resourceId}:{tag.DataClassification}:{DateTime.UtcNow.Ticks}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash)[..16];
        }

        private bool RequiresTag(string classification)
        {
            var sensitiveClassifications = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "personal", "pii", "phi", "financial", "confidential", "secret", "top-secret",
                "personal-eu", "sensitive-personal", "health", "genetic", "biometric"
            };

            return sensitiveClassifications.Any(sc => classification.Contains(sc, StringComparison.OrdinalIgnoreCase));
        }

        private void InitializeDefaultTemplates()
        {
            _templates["eu-gdpr"] = new TagTemplate
            {
                TemplateId = "eu-gdpr",
                TemplateName = "EU GDPR Personal Data",
                DefaultAllowedRegions = new List<string> { "EU", "EEA", "GDPR-ADEQUATE" },
                DefaultProhibitedRegions = new List<string>(),
                DefaultClassification = "personal-eu",
                DefaultLegalBasis = "GDPR Article 6",
                RequiresEncryption = true,
                AllowsCrossBorder = true,
                DefaultRetentionPolicy = "7-years"
            };

            _templates["us-hipaa"] = new TagTemplate
            {
                TemplateId = "us-hipaa",
                TemplateName = "US HIPAA Health Data",
                DefaultAllowedRegions = new List<string> { "US" },
                DefaultProhibitedRegions = new List<string>(),
                DefaultClassification = "phi",
                DefaultLegalBasis = "HIPAA",
                RequiresEncryption = true,
                AllowsCrossBorder = false,
                DefaultRetentionPolicy = "6-years"
            };

            _templates["china-pipl"] = new TagTemplate
            {
                TemplateId = "china-pipl",
                TemplateName = "China PIPL Personal Data",
                DefaultAllowedRegions = new List<string> { "CN" },
                DefaultProhibitedRegions = new List<string>(),
                DefaultClassification = "personal-cn",
                DefaultLegalBasis = "PIPL",
                RequiresEncryption = true,
                AllowsCrossBorder = false,
                DefaultRetentionPolicy = "indefinite"
            };

            _templates["russia-local"] = new TagTemplate
            {
                TemplateId = "russia-local",
                TemplateName = "Russia Data Localization",
                DefaultAllowedRegions = new List<string> { "RU" },
                DefaultProhibitedRegions = new List<string>(),
                DefaultClassification = "personal-ru",
                DefaultLegalBasis = "FZ-152",
                RequiresEncryption = true,
                AllowsCrossBorder = false,
                DefaultRetentionPolicy = "indefinite"
            };

            _templates["financial-global"] = new TagTemplate
            {
                TemplateId = "financial-global",
                TemplateName = "Financial Data",
                DefaultAllowedRegions = new List<string>(),
                DefaultProhibitedRegions = new List<string>(),
                DefaultClassification = "financial",
                RequiresEncryption = true,
                AllowsCrossBorder = true,
                DefaultRetentionPolicy = "10-years"
            };
        }

        private TagTemplate? ParseTemplateFromConfig(Dictionary<string, object> config)
        {
            var templateId = config.GetValueOrDefault("TemplateId")?.ToString() ?? "";
            var templateName = config.GetValueOrDefault("TemplateName")?.ToString() ?? "";

            // Log and skip invalid templates instead of silently returning null (finding 1430)
            if (string.IsNullOrWhiteSpace(templateId))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[DataTaggingStrategy] WARNING: Skipping tag template with missing TemplateId.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(templateName))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DataTaggingStrategy] WARNING: Skipping tag template '{templateId}' with missing TemplateName.");
                return null;
            }

            return new TagTemplate
            {
                TemplateId = templateId,
                TemplateName = templateName,
                DefaultAllowedRegions = (config.TryGetValue("AllowedRegions", out var ar) && ar is IEnumerable<string> allowed)
                    ? allowed.ToList() : new List<string>(),
                DefaultProhibitedRegions = (config.TryGetValue("ProhibitedRegions", out var pr) && pr is IEnumerable<string> prohibited)
                    ? prohibited.ToList() : new List<string>(),
                DefaultClassification = config.GetValueOrDefault("Classification")?.ToString() ?? "general",
                RequiresEncryption = config.TryGetValue("RequiresEncryption", out var enc) && enc is true,
                AllowsCrossBorder = !config.TryGetValue("AllowsCrossBorder", out var cb) || cb is true
            };
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("data_tagging.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("data_tagging.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Sovereignty tag attached to data resources.
    /// </summary>
    public sealed record SovereigntyTag
    {
        public string? TagId { get; init; }
        public List<string> AllowedRegions { get; init; } = new();
        public List<string> ProhibitedRegions { get; init; } = new();
        public required string DataClassification { get; init; }
        public string? RetentionPolicy { get; init; }
        public bool EncryptionRequired { get; init; }
        public bool CrossBorderAllowed { get; init; }
        public string? LegalBasis { get; init; }
        public DateTime? ExpirationDate { get; init; }
        public DateTime? AppliedAt { get; init; }
        public string? AppliedBy { get; init; }
        public bool IsInherited { get; init; }
        public string? InheritedFrom { get; init; }
        public Dictionary<string, object> CustomAttributes { get; init; } = new();
    }

    /// <summary>
    /// Result of a tagging operation.
    /// </summary>
    public sealed record TaggingResult
    {
        public required bool Success { get; init; }
        public required string ResourceId { get; init; }
        public SovereigntyTag? AppliedTag { get; init; }
        public SovereigntyTag? RemovedTag { get; init; }
        public string? ErrorMessage { get; init; }
        public List<string> Conflicts { get; init; } = new();
    }

    /// <summary>
    /// Result of tag validation for an operation.
    /// </summary>
    public sealed record TagValidationResult
    {
        public required bool IsAllowed { get; init; }
        public required string Reason { get; init; }
        public string? RequiredAction { get; init; }
    }

    /// <summary>
    /// Historical record of tag changes.
    /// </summary>
    public sealed record TagHistory
    {
        public required string ResourceId { get; init; }
        public required string TagId { get; init; }
        public required TagOperation Operation { get; init; }
        public required DateTime Timestamp { get; init; }
        public required string PerformedBy { get; init; }
        public string? Reason { get; init; }
        public required string TagSnapshot { get; init; }
    }

    /// <summary>
    /// Tag operation type.
    /// </summary>
    public enum TagOperation
    {
        Applied,
        Removed,
        Modified,
        Inherited,
        Expired
    }

    /// <summary>
    /// Template for creating sovereignty tags.
    /// </summary>
    public sealed record TagTemplate
    {
        public required string TemplateId { get; init; }
        public required string TemplateName { get; init; }
        public List<string> DefaultAllowedRegions { get; init; } = new();
        public List<string> DefaultProhibitedRegions { get; init; } = new();
        public required string DefaultClassification { get; init; }
        public string? DefaultLegalBasis { get; init; }
        public string? DefaultRetentionPolicy { get; init; }
        public int? DefaultExpirationDays { get; init; }
        public bool RequiresEncryption { get; init; }
        public bool AllowsCrossBorder { get; init; }
    }
}
