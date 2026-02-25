using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Retention;

/// <summary>
/// Compliance-driven retention strategy supporting GDPR, HIPAA, SOX, and custom policies.
/// Enforces regulatory requirements for data retention and deletion.
/// </summary>
/// <remarks>
/// Features:
/// - Pre-built compliance policies (GDPR, HIPAA, SOX, PCI-DSS)
/// - Custom policy definition
/// - Policy conflict resolution
/// - Audit trail for compliance
/// - Geographic policy enforcement
/// </remarks>
public sealed class PolicyBasedRetentionStrategy : RetentionStrategyBase
{
    private readonly BoundedDictionary<string, CompliancePolicy> _policies = new BoundedDictionary<string, CompliancePolicy>(1000);
    private readonly BoundedDictionary<string, PolicyAssignment> _assignments = new BoundedDictionary<string, PolicyAssignment>(1000);
    private readonly List<PolicyAuditEntry> _auditLog = new();
    private readonly object _auditLock = new();

    /// <summary>
    /// Initializes with default compliance policies.
    /// </summary>
    public PolicyBasedRetentionStrategy()
    {
        // Register built-in compliance policies
        RegisterBuiltInPolicies();
    }

    /// <inheritdoc/>
    public override string StrategyId => "retention.policybased";

    /// <inheritdoc/>
    public override string DisplayName => "Policy-Based Retention";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 50_000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Compliance-driven retention with support for GDPR, HIPAA, SOX, and PCI-DSS. " +
        "Automatically applies regulatory requirements based on data classification and geography. " +
        "Maintains audit trail for compliance verification.";

    /// <inheritdoc/>
    public override string[] Tags => ["retention", "compliance", "gdpr", "hipaa", "sox", "pci-dss", "regulatory"];

    private void RegisterBuiltInPolicies()
    {
        // GDPR - EU General Data Protection Regulation
        RegisterPolicy(new CompliancePolicy
        {
            PolicyId = "gdpr",
            Name = "GDPR Compliance",
            Description = "EU General Data Protection Regulation requirements",
            MinRetention = TimeSpan.Zero,
            MaxRetention = TimeSpan.FromDays(365 * 3), // 3 years max for personal data
            RequiresConsent = true,
            AllowsDeletion = true,
            RequiresDeletionOnRequest = true,
            ApplicableRegions = new[] { "EU", "EEA", "UK" },
            DataCategories = new[] { "personal", "pii", "sensitive" },
            Priority = 100
        });

        // HIPAA - Health Insurance Portability and Accountability Act
        RegisterPolicy(new CompliancePolicy
        {
            PolicyId = "hipaa",
            Name = "HIPAA Compliance",
            Description = "US Health Insurance Portability and Accountability Act",
            MinRetention = TimeSpan.FromDays(365 * 6), // 6 years minimum
            MaxRetention = null, // No maximum
            RequiresConsent = false,
            AllowsDeletion = false, // Cannot delete during retention
            RequiresDeletionOnRequest = false,
            ApplicableRegions = new[] { "US" },
            DataCategories = new[] { "phi", "health", "medical" },
            Priority = 200
        });

        // SOX - Sarbanes-Oxley Act
        RegisterPolicy(new CompliancePolicy
        {
            PolicyId = "sox",
            Name = "SOX Compliance",
            Description = "Sarbanes-Oxley Act financial record retention",
            MinRetention = TimeSpan.FromDays(365 * 7), // 7 years minimum
            MaxRetention = null,
            RequiresConsent = false,
            AllowsDeletion = false,
            RequiresDeletionOnRequest = false,
            ApplicableRegions = new[] { "US" },
            DataCategories = new[] { "financial", "audit", "accounting" },
            Priority = 150
        });

        // PCI-DSS - Payment Card Industry Data Security Standard
        RegisterPolicy(new CompliancePolicy
        {
            PolicyId = "pci-dss",
            Name = "PCI-DSS Compliance",
            Description = "Payment Card Industry Data Security Standard",
            MinRetention = TimeSpan.FromDays(365), // 1 year minimum for logs
            MaxRetention = TimeSpan.FromDays(365 * 3), // Minimize storage of cardholder data
            RequiresConsent = false,
            AllowsDeletion = true,
            RequiresDeletionOnRequest = false,
            ApplicableRegions = null, // Global
            DataCategories = new[] { "payment", "cardholder", "pan" },
            Priority = 180
        });
    }

    /// <summary>
    /// Registers a custom compliance policy.
    /// </summary>
    /// <param name="policy">Policy to register.</param>
    public void RegisterPolicy(CompliancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.PolicyId);
        _policies[policy.PolicyId] = policy;
    }

    /// <summary>
    /// Assigns a policy to objects matching criteria.
    /// </summary>
    /// <param name="assignment">Policy assignment.</param>
    public void AssignPolicy(PolicyAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var key = $"{assignment.PolicyId}:{assignment.PathPattern ?? "*"}";
        _assignments[key] = assignment;
    }

    /// <inheritdoc/>
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Find applicable policies
        var applicablePolicies = GetApplicablePolicies(data);

        if (applicablePolicies.Count == 0)
        {
            return Task.FromResult(RetentionDecision.Retain(
                "No compliance policies apply - default retention",
                DateTime.UtcNow.AddDays(30)));
        }

        // Resolve policy conflicts - most restrictive wins
        var resolvedPolicy = ResolvePolicyConflicts(applicablePolicies);
        var decision = ApplyPolicy(data, resolvedPolicy);

        // Audit log
        LogPolicyApplication(data, resolvedPolicy, decision);

        return Task.FromResult(decision);
    }

    private List<CompliancePolicy> GetApplicablePolicies(DataObject data)
    {
        var applicable = new List<CompliancePolicy>();

        foreach (var policy in _policies.Values)
        {
            if (PolicyApplies(data, policy))
            {
                applicable.Add(policy);
            }
        }

        // Also check explicit assignments
        foreach (var assignment in _assignments.Values)
        {
            if (AssignmentMatches(data, assignment) && _policies.TryGetValue(assignment.PolicyId, out var policy))
            {
                if (!applicable.Contains(policy))
                {
                    applicable.Add(policy);
                }
            }
        }

        return applicable;
    }

    private bool PolicyApplies(DataObject data, CompliancePolicy policy)
    {
        // Check region
        if (policy.ApplicableRegions != null && policy.ApplicableRegions.Length > 0)
        {
            var region = GetDataRegion(data);
            if (!policy.ApplicableRegions.Contains(region))
            {
                return false;
            }
        }

        // Check data category
        if (policy.DataCategories != null && policy.DataCategories.Length > 0)
        {
            var categories = GetDataCategories(data);
            if (!policy.DataCategories.Intersect(categories).Any())
            {
                return false;
            }
        }

        return true;
    }

    private bool AssignmentMatches(DataObject data, PolicyAssignment assignment)
    {
        if (!string.IsNullOrEmpty(assignment.PathPattern))
        {
            var regex = new Regex(assignment.PathPattern.Replace("*", ".*"), RegexOptions.IgnoreCase);
            if (data.Path == null || !regex.IsMatch(data.Path))
            {
                return false;
            }
        }

        if (assignment.ContentTypes != null && assignment.ContentTypes.Length > 0)
        {
            if (data.ContentType == null || !assignment.ContentTypes.Contains(data.ContentType))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(assignment.TenantId) && data.TenantId != assignment.TenantId)
        {
            return false;
        }

        return true;
    }

    private CompliancePolicy ResolvePolicyConflicts(List<CompliancePolicy> policies)
    {
        // Sort by priority (higher = more important)
        var sorted = policies.OrderByDescending(p => p.Priority).ToList();

        // Create merged policy taking most restrictive values
        var merged = new CompliancePolicy
        {
            PolicyId = "merged",
            Name = "Merged Policy",
            Description = string.Join(" + ", sorted.Select(p => p.Name)),
            MinRetention = sorted.Max(p => p.MinRetention ?? TimeSpan.Zero),
            MaxRetention = sorted.Where(p => p.MaxRetention.HasValue)
                                 .Select(p => p.MaxRetention!.Value)
                                 .DefaultIfEmpty(TimeSpan.MaxValue)
                                 .Min(),
            RequiresConsent = sorted.Any(p => p.RequiresConsent),
            AllowsDeletion = sorted.All(p => p.AllowsDeletion),
            RequiresDeletionOnRequest = sorted.Any(p => p.RequiresDeletionOnRequest),
            Priority = sorted.Max(p => p.Priority)
        };

        return merged;
    }

    private RetentionDecision ApplyPolicy(DataObject data, CompliancePolicy policy)
    {
        var age = data.Age;

        // Check minimum retention
        if (policy.MinRetention.HasValue && age < policy.MinRetention.Value)
        {
            var minDate = data.CreatedAt + policy.MinRetention.Value;
            return new RetentionDecision
            {
                Action = RetentionAction.Retain,
                Reason = $"Minimum retention not met ({policy.Name})",
                NextEvaluationDate = minDate,
                PolicyName = policy.Name
            };
        }

        // Check maximum retention
        if (policy.MaxRetention.HasValue && age > policy.MaxRetention.Value)
        {
            if (policy.AllowsDeletion)
            {
                return new RetentionDecision
                {
                    Action = RetentionAction.Delete,
                    Reason = $"Maximum retention exceeded ({policy.Name})",
                    PolicyName = policy.Name
                };
            }
            else
            {
                return new RetentionDecision
                {
                    Action = RetentionAction.Archive,
                    Reason = $"Maximum retention exceeded but deletion not allowed ({policy.Name})",
                    PolicyName = policy.Name
                };
            }
        }

        // Within retention window
        var nextEval = policy.MaxRetention.HasValue
            ? data.CreatedAt + policy.MaxRetention.Value
            : DateTime.UtcNow.AddDays(90);

        return new RetentionDecision
        {
            Action = RetentionAction.Retain,
            Reason = $"Within policy retention window ({policy.Name})",
            NextEvaluationDate = nextEval,
            PolicyName = policy.Name
        };
    }

    private static string GetDataRegion(DataObject data)
    {
        if (data.Metadata?.TryGetValue("region", out var region) == true)
        {
            return region?.ToString() ?? "UNKNOWN";
        }

        if (data.Metadata?.TryGetValue("country", out var country) == true)
        {
            return MapCountryToRegion(country?.ToString() ?? "");
        }

        return "UNKNOWN";
    }

    private static string MapCountryToRegion(string country)
    {
        var euCountries = new[] { "DE", "FR", "IT", "ES", "NL", "BE", "AT", "PL", "SE", "DK", "FI", "IE", "PT", "GR" };
        var eeaCountries = new[] { "NO", "IS", "LI" };

        country = country.ToUpperInvariant();

        if (euCountries.Contains(country)) return "EU";
        if (eeaCountries.Contains(country)) return "EEA";
        if (country == "GB" || country == "UK") return "UK";
        if (country == "US") return "US";

        return "OTHER";
    }

    private static string[] GetDataCategories(DataObject data)
    {
        var categories = new List<string>();

        if (data.Tags != null)
        {
            categories.AddRange(data.Tags.Select(t => t.ToLowerInvariant()));
        }

        if (data.Metadata?.TryGetValue("category", out var cat) == true)
        {
            categories.Add(cat?.ToString()?.ToLowerInvariant() ?? "");
        }

        if (data.Metadata?.TryGetValue("dataType", out var dt) == true)
        {
            categories.Add(dt?.ToString()?.ToLowerInvariant() ?? "");
        }

        return categories.Where(c => !string.IsNullOrEmpty(c)).Distinct().ToArray();
    }

    private void LogPolicyApplication(DataObject data, CompliancePolicy policy, RetentionDecision decision)
    {
        lock (_auditLock)
        {
            _auditLog.Add(new PolicyAuditEntry
            {
                Timestamp = DateTime.UtcNow,
                ObjectId = data.ObjectId,
                PolicyId = policy.PolicyId,
                PolicyName = policy.Name,
                Decision = decision.Action,
                Reason = decision.Reason
            });

            // Keep only last 10000 entries
            while (_auditLog.Count > 10000)
            {
                _auditLog.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Gets the audit log entries.
    /// </summary>
    /// <param name="limit">Maximum entries to return.</param>
    /// <returns>Recent audit entries.</returns>
    public IReadOnlyList<PolicyAuditEntry> GetAuditLog(int limit = 100)
    {
        lock (_auditLock)
        {
            return _auditLog.TakeLast(limit).ToList().AsReadOnly();
        }
    }
}

/// <summary>
/// Represents a compliance policy.
/// </summary>
public sealed class CompliancePolicy
{
    /// <summary>
    /// Unique policy identifier.
    /// </summary>
    public required string PolicyId { get; init; }

    /// <summary>
    /// Human-readable policy name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Policy description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Minimum retention period required.
    /// </summary>
    public TimeSpan? MinRetention { get; init; }

    /// <summary>
    /// Maximum retention period allowed.
    /// </summary>
    public TimeSpan? MaxRetention { get; init; }

    /// <summary>
    /// Whether the policy requires user consent.
    /// </summary>
    public bool RequiresConsent { get; init; }

    /// <summary>
    /// Whether deletion is allowed under this policy.
    /// </summary>
    public bool AllowsDeletion { get; init; } = true;

    /// <summary>
    /// Whether deletion is required on user request (e.g., GDPR right to erasure).
    /// </summary>
    public bool RequiresDeletionOnRequest { get; init; }

    /// <summary>
    /// Regions where this policy applies.
    /// </summary>
    public string[]? ApplicableRegions { get; init; }

    /// <summary>
    /// Data categories this policy applies to.
    /// </summary>
    public string[]? DataCategories { get; init; }

    /// <summary>
    /// Priority for conflict resolution (higher = more important).
    /// </summary>
    public int Priority { get; init; }
}

/// <summary>
/// Assignment of a policy to specific data.
/// </summary>
public sealed class PolicyAssignment
{
    /// <summary>
    /// Policy ID to assign.
    /// </summary>
    public required string PolicyId { get; init; }

    /// <summary>
    /// Path pattern to match (supports wildcards).
    /// </summary>
    public string? PathPattern { get; init; }

    /// <summary>
    /// Content types to match.
    /// </summary>
    public string[]? ContentTypes { get; init; }

    /// <summary>
    /// Tenant ID to scope to.
    /// </summary>
    public string? TenantId { get; init; }
}

/// <summary>
/// Audit log entry for policy application.
/// </summary>
public sealed class PolicyAuditEntry
{
    /// <summary>
    /// When the policy was applied.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Object that was evaluated.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Policy that was applied.
    /// </summary>
    public required string PolicyId { get; init; }

    /// <summary>
    /// Policy name.
    /// </summary>
    public required string PolicyName { get; init; }

    /// <summary>
    /// Decision made.
    /// </summary>
    public required RetentionAction Decision { get; init; }

    /// <summary>
    /// Reason for decision.
    /// </summary>
    public required string Reason { get; init; }
}
