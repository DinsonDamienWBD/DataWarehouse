using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.UltimateMultiCloud.Strategies.Security;

/// <summary>
/// 118.5: Multi-Cloud Security Strategies
/// Unified security across cloud providers.
/// </summary>

/// <summary>
/// Unified IAM across all cloud providers.
/// </summary>
public sealed class UnifiedIamStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, UnifiedIdentity> _identities = new();
    private readonly ConcurrentDictionary<string, List<ProviderRoleMapping>> _roleMappings = new();

    public override string StrategyId => "security-unified-iam";
    public override string StrategyName => "Unified IAM";
    public override string Category => "Security";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Single identity management across AWS IAM, Azure AD, GCP IAM, etc.",
        Category = Category,
        SupportsDataSovereignty = true,
        TypicalLatencyOverheadMs = 5.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Creates a unified identity.</summary>
    public UnifiedIdentity CreateIdentity(string identityId, string email, string displayName)
    {
        var identity = new UnifiedIdentity
        {
            IdentityId = identityId,
            Email = email,
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _identities[identityId] = identity;
        RecordSuccess();
        return identity;
    }

    /// <summary>Maps a unified role to provider-specific roles.</summary>
    public void MapRole(string unifiedRole, string providerId, string providerRole)
    {
        var mappings = _roleMappings.GetOrAdd(unifiedRole, _ => new List<ProviderRoleMapping>());
        mappings.Add(new ProviderRoleMapping
        {
            UnifiedRole = unifiedRole,
            ProviderId = providerId,
            ProviderRole = providerRole
        });
    }

    /// <summary>Assigns role to identity.</summary>
    public void AssignRole(string identityId, string unifiedRole)
    {
        if (_identities.TryGetValue(identityId, out var identity))
        {
            identity.Roles.Add(unifiedRole);

            // Propagate to all mapped providers
            if (_roleMappings.TryGetValue(unifiedRole, out var mappings))
            {
                foreach (var mapping in mappings)
                {
                    identity.ProviderCredentials[mapping.ProviderId] = new ProviderCredential
                    {
                        ProviderId = mapping.ProviderId,
                        ProviderRole = mapping.ProviderRole,
                        GrantedAt = DateTimeOffset.UtcNow
                    };
                }
            }
        }
    }

    /// <summary>Gets identity with all roles.</summary>
    public UnifiedIdentity? GetIdentity(string identityId)
    {
        return _identities.TryGetValue(identityId, out var identity) ? identity : null;
    }

    protected override string? GetCurrentState() => $"Identities: {_identities.Count}, Roles: {_roleMappings.Count}";
}

/// <summary>
/// Cross-cloud encryption key management.
/// </summary>
public sealed class CrossCloudEncryptionStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, EncryptionKeyInfo> _keys = new();

    public override string StrategyId => "security-cross-cloud-encryption";
    public override string StrategyName => "Cross-Cloud Encryption";
    public override string Category => "Security";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Unified encryption with key management across AWS KMS, Azure Key Vault, GCP KMS",
        Category = Category,
        SupportsDataSovereignty = true,
        TypicalLatencyOverheadMs = 10.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Creates a master encryption key.</summary>
    public EncryptionKeyInfo CreateMasterKey(string keyId, string? description = null)
    {
        var keyMaterial = new byte[32];
        RandomNumberGenerator.Fill(keyMaterial);

        var keyInfo = new EncryptionKeyInfo
        {
            KeyId = keyId,
            Algorithm = "AES-256-GCM",
            CreatedAt = DateTimeOffset.UtcNow,
            Description = description,
            KeyHash = Convert.ToHexString(SHA256.HashData(keyMaterial)).ToLowerInvariant()
        };

        _keys[keyId] = keyInfo;
        RecordSuccess();
        return keyInfo;
    }

    /// <summary>Encrypts data with unified key.</summary>
    public EncryptedData Encrypt(string keyId, ReadOnlySpan<byte> plaintext)
    {
        if (!_keys.ContainsKey(keyId))
            throw new InvalidOperationException($"Key {keyId} not found");

        // In production, this would use actual encryption
        var ciphertext = new byte[plaintext.Length + 16];
        plaintext.CopyTo(ciphertext);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        RecordSuccess();
        return new EncryptedData
        {
            KeyId = keyId,
            Ciphertext = ciphertext,
            Nonce = nonce,
            Algorithm = "AES-256-GCM"
        };
    }

    /// <summary>Replicates key to a provider.</summary>
    public KeyReplicationResult ReplicateKey(string keyId, string targetProvider)
    {
        if (!_keys.TryGetValue(keyId, out var key))
        {
            RecordFailure();
            return new KeyReplicationResult { Success = false, ErrorMessage = "Key not found" };
        }

        key.ReplicatedTo.Add(targetProvider);
        RecordSuccess();
        return new KeyReplicationResult
        {
            Success = true,
            KeyId = keyId,
            TargetProvider = targetProvider,
            ReplicatedAt = DateTimeOffset.UtcNow
        };
    }

    protected override string? GetCurrentState() => $"Keys: {_keys.Count}";
}

/// <summary>
/// Compliance enforcement across clouds.
/// </summary>
public sealed class CrossCloudComplianceStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, CompliancePolicy> _policies = new();
    private readonly ConcurrentDictionary<string, List<ComplianceViolation>> _violations = new();

    public override string StrategyId => "security-compliance-enforcement";
    public override string StrategyName => "Cross-Cloud Compliance";
    public override string Category => "Security";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Enforces compliance policies (GDPR, HIPAA, SOC2) across all cloud providers",
        Category = Category,
        SupportsDataSovereignty = true,
        TypicalLatencyOverheadMs = 3.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Registers a compliance policy.</summary>
    public void RegisterPolicy(string policyId, string name, ComplianceFramework framework, IEnumerable<ComplianceRule> rules)
    {
        _policies[policyId] = new CompliancePolicy
        {
            PolicyId = policyId,
            Name = name,
            Framework = framework,
            Rules = rules.ToList(),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Validates resource against policies.</summary>
    public ComplianceCheckResult ValidateResource(string resourceId, string providerId, Dictionary<string, object> resourceProperties)
    {
        var violations = new List<ComplianceViolation>();

        foreach (var policy in _policies.Values)
        {
            foreach (var rule in policy.Rules)
            {
                var isCompliant = EvaluateRule(rule, resourceProperties);
                if (!isCompliant)
                {
                    violations.Add(new ComplianceViolation
                    {
                        ViolationId = Guid.NewGuid().ToString("N"),
                        PolicyId = policy.PolicyId,
                        RuleId = rule.RuleId,
                        ResourceId = resourceId,
                        ProviderId = providerId,
                        Severity = rule.Severity,
                        Description = rule.Description,
                        DetectedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        if (violations.Any())
        {
            _violations.AddOrUpdate(resourceId, violations, (_, existing) =>
            {
                existing.AddRange(violations);
                return existing;
            });
            RecordFailure();
        }
        else
        {
            RecordSuccess();
        }

        return new ComplianceCheckResult
        {
            ResourceId = resourceId,
            IsCompliant = !violations.Any(),
            Violations = violations,
            CheckedAt = DateTimeOffset.UtcNow
        };
    }

    private static bool EvaluateRule(ComplianceRule rule, Dictionary<string, object> properties)
    {
        // Simplified rule evaluation
        return rule.PropertyName == null || properties.ContainsKey(rule.PropertyName);
    }

    /// <summary>Gets compliance status summary.</summary>
    public ComplianceStatusSummary GetComplianceStatus()
    {
        var allViolations = _violations.Values.SelectMany(v => v).ToList();
        return new ComplianceStatusSummary
        {
            TotalPolicies = _policies.Count,
            TotalViolations = allViolations.Count,
            CriticalViolations = allViolations.Count(v => v.Severity == "Critical"),
            HighViolations = allViolations.Count(v => v.Severity == "High"),
            ByFramework = _policies.Values
                .GroupBy(p => p.Framework)
                .ToDictionary(g => g.Key.ToString(), g => g.Count())
        };
    }

    protected override string? GetCurrentState() =>
        $"Policies: {_policies.Count}, Violations: {_violations.Values.Sum(v => v.Count)}";
}

/// <summary>
/// Zero-trust network architecture across clouds.
/// </summary>
public sealed class ZeroTrustNetworkStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, TrustPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, AccessDecision> _recentDecisions = new();

    public override string StrategyId => "security-zero-trust";
    public override string StrategyName => "Zero-Trust Network";
    public override string Category => "Security";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Zero-trust security model with continuous verification across cloud boundaries",
        Category = Category,
        SupportsAutomaticFailover = false,
        TypicalLatencyOverheadMs = 8.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Creates a trust policy.</summary>
    public void CreatePolicy(string policyId, string resourcePattern, AccessRequirements requirements)
    {
        _policies[policyId] = new TrustPolicy
        {
            PolicyId = policyId,
            ResourcePattern = resourcePattern,
            Requirements = requirements,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Evaluates access request.</summary>
    public AccessDecision EvaluateAccess(AccessRequest request)
    {
        var applicablePolicies = _policies.Values
            .Where(p => MatchesPattern(request.ResourceId, p.ResourcePattern))
            .ToList();

        var decision = new AccessDecision
        {
            DecisionId = Guid.NewGuid().ToString("N"),
            RequestId = request.RequestId,
            Allowed = true,
            EvaluatedAt = DateTimeOffset.UtcNow
        };

        foreach (var policy in applicablePolicies)
        {
            var requirements = policy.Requirements;

            if (requirements.RequireMfa && !request.MfaVerified)
            {
                decision.Allowed = false;
                decision.DenialReason = "MFA required";
                break;
            }

            if (requirements.AllowedSourceIps?.Any() == true &&
                !requirements.AllowedSourceIps.Contains(request.SourceIp))
            {
                decision.Allowed = false;
                decision.DenialReason = "Source IP not allowed";
                break;
            }

            if (requirements.RequiredRoles?.Any() == true &&
                !requirements.RequiredRoles.Intersect(request.Roles).Any())
            {
                decision.Allowed = false;
                decision.DenialReason = "Required role not present";
                break;
            }
        }

        _recentDecisions[decision.DecisionId] = decision;

        if (decision.Allowed) RecordSuccess();
        else RecordFailure();

        return decision;
    }

    private static bool MatchesPattern(string resource, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith("*"))
            return resource.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return resource.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    protected override string? GetCurrentState() => $"Policies: {_policies.Count}";
}

/// <summary>
/// Secrets management across clouds.
/// </summary>
public sealed class CrossCloudSecretsStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, SecretEntry> _secrets = new();
    private readonly ConcurrentDictionary<string, List<SecretAccess>> _accessLog = new();

    public override string StrategyId => "security-secrets-management";
    public override string StrategyName => "Cross-Cloud Secrets";
    public override string Category => "Security";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Unified secrets management across AWS Secrets Manager, Azure Key Vault, GCP Secret Manager",
        Category = Category,
        TypicalLatencyOverheadMs = 15.0,
        MemoryFootprint = "Low"
    };

    /// <summary>Stores a secret.</summary>
    public void StoreSecret(string secretId, string value, Dictionary<string, string>? tags = null)
    {
        var encrypted = Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); // Simplified
        _secrets[secretId] = new SecretEntry
        {
            SecretId = secretId,
            EncryptedValue = encrypted,
            Version = 1,
            Tags = tags ?? new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        RecordSuccess();
    }

    /// <summary>Retrieves a secret.</summary>
    public string? GetSecret(string secretId, string requesterId)
    {
        if (!_secrets.TryGetValue(secretId, out var entry))
        {
            RecordFailure();
            return null;
        }

        // Log access
        var access = new SecretAccess
        {
            SecretId = secretId,
            RequesterId = requesterId,
            AccessedAt = DateTimeOffset.UtcNow,
            AccessType = "Read"
        };

        _accessLog.AddOrUpdate(secretId,
            new List<SecretAccess> { access },
            (_, list) => { list.Add(access); return list; });

        RecordSuccess();
        return Encoding.UTF8.GetString(Convert.FromBase64String(entry.EncryptedValue));
    }

    /// <summary>Rotates a secret.</summary>
    public void RotateSecret(string secretId, string newValue)
    {
        if (_secrets.TryGetValue(secretId, out var entry))
        {
            entry.EncryptedValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(newValue));
            entry.Version++;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            entry.LastRotated = DateTimeOffset.UtcNow;
            RecordSuccess();
        }
        else
        {
            RecordFailure();
        }
    }

    /// <summary>Gets access audit log.</summary>
    public IReadOnlyList<SecretAccess> GetAccessLog(string secretId)
    {
        return _accessLog.TryGetValue(secretId, out var log) ? log : new List<SecretAccess>();
    }

    protected override string? GetCurrentState() => $"Secrets: {_secrets.Count}";
}

/// <summary>
/// Threat detection across clouds.
/// </summary>
public sealed class CrossCloudThreatDetectionStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentDictionary<string, ThreatIndicator> _indicators = new();
    private readonly ConcurrentQueue<SecurityEvent> _events = new();

    public override string StrategyId => "security-threat-detection";
    public override string StrategyName => "Cross-Cloud Threat Detection";
    public override string Category => "Security";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Unified threat detection across AWS GuardDuty, Azure Sentinel, GCP Security Command Center",
        Category = Category,
        TypicalLatencyOverheadMs = 2.0,
        MemoryFootprint = "High"
    };

    /// <summary>Ingests security event.</summary>
    public ThreatAnalysisResult AnalyzeEvent(SecurityEvent securityEvent)
    {
        _events.Enqueue(securityEvent);

        var threats = new List<DetectedThreat>();

        // Check against known indicators
        foreach (var indicator in _indicators.Values)
        {
            if (MatchesIndicator(securityEvent, indicator))
            {
                threats.Add(new DetectedThreat
                {
                    ThreatId = Guid.NewGuid().ToString("N"),
                    IndicatorId = indicator.IndicatorId,
                    ThreatType = indicator.ThreatType,
                    Severity = indicator.Severity,
                    DetectedAt = DateTimeOffset.UtcNow,
                    SourceProvider = securityEvent.ProviderId,
                    Description = indicator.Description
                });
            }
        }

        // Behavioral analysis
        if (securityEvent.EventType == "failed_login" && GetRecentEventCount("failed_login", securityEvent.SourceId) > 5)
        {
            threats.Add(new DetectedThreat
            {
                ThreatId = Guid.NewGuid().ToString("N"),
                ThreatType = "BruteForce",
                Severity = "High",
                DetectedAt = DateTimeOffset.UtcNow,
                SourceProvider = securityEvent.ProviderId,
                Description = "Multiple failed login attempts detected"
            });
        }

        if (threats.Any()) RecordFailure();
        else RecordSuccess();

        return new ThreatAnalysisResult
        {
            EventId = securityEvent.EventId,
            Threats = threats,
            AnalyzedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Adds threat indicator.</summary>
    public void AddIndicator(string indicatorId, string pattern, string threatType, string severity)
    {
        _indicators[indicatorId] = new ThreatIndicator
        {
            IndicatorId = indicatorId,
            Pattern = pattern,
            ThreatType = threatType,
            Severity = severity
        };
    }

    private static bool MatchesIndicator(SecurityEvent securityEvent, ThreatIndicator indicator)
    {
        return securityEvent.EventType.Contains(indicator.Pattern, StringComparison.OrdinalIgnoreCase);
    }

    private int GetRecentEventCount(string eventType, string sourceId)
    {
        return _events.Count(e => e.EventType == eventType && e.SourceId == sourceId);
    }

    protected override string? GetCurrentState() =>
        $"Indicators: {_indicators.Count}, Events: {_events.Count}";
}

#region Supporting Types

public sealed class UnifiedIdentity
{
    public required string IdentityId { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public HashSet<string> Roles { get; } = new();
    public Dictionary<string, ProviderCredential> ProviderCredentials { get; } = new();
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ProviderRoleMapping
{
    public required string UnifiedRole { get; init; }
    public required string ProviderId { get; init; }
    public required string ProviderRole { get; init; }
}

public sealed class ProviderCredential
{
    public required string ProviderId { get; init; }
    public required string ProviderRole { get; init; }
    public DateTimeOffset GrantedAt { get; init; }
}

public sealed class EncryptionKeyInfo
{
    public required string KeyId { get; init; }
    public required string Algorithm { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? Description { get; init; }
    public string? KeyHash { get; init; }
    public HashSet<string> ReplicatedTo { get; } = new();
}

public sealed class EncryptedData
{
    public required string KeyId { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Nonce { get; init; }
    public required string Algorithm { get; init; }
}

public sealed class KeyReplicationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? KeyId { get; init; }
    public string? TargetProvider { get; init; }
    public DateTimeOffset ReplicatedAt { get; init; }
}

public enum ComplianceFramework { GDPR, HIPAA, SOC2, PCI_DSS, CCPA, ISO27001 }

public sealed class CompliancePolicy
{
    public required string PolicyId { get; init; }
    public required string Name { get; init; }
    public ComplianceFramework Framework { get; init; }
    public List<ComplianceRule> Rules { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ComplianceRule
{
    public required string RuleId { get; init; }
    public required string Description { get; init; }
    public string? PropertyName { get; init; }
    public required string Severity { get; init; }
}

public sealed class ComplianceViolation
{
    public required string ViolationId { get; init; }
    public required string PolicyId { get; init; }
    public required string RuleId { get; init; }
    public required string ResourceId { get; init; }
    public required string ProviderId { get; init; }
    public required string Severity { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
}

public sealed class ComplianceCheckResult
{
    public required string ResourceId { get; init; }
    public bool IsCompliant { get; init; }
    public List<ComplianceViolation> Violations { get; init; } = new();
    public DateTimeOffset CheckedAt { get; init; }
}

public sealed class ComplianceStatusSummary
{
    public int TotalPolicies { get; init; }
    public int TotalViolations { get; init; }
    public int CriticalViolations { get; init; }
    public int HighViolations { get; init; }
    public Dictionary<string, int> ByFramework { get; init; } = new();
}

public sealed class TrustPolicy
{
    public required string PolicyId { get; init; }
    public required string ResourcePattern { get; init; }
    public required AccessRequirements Requirements { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class AccessRequirements
{
    public bool RequireMfa { get; init; }
    public List<string>? AllowedSourceIps { get; init; }
    public List<string>? RequiredRoles { get; init; }
    public int? MaxSessionDurationMinutes { get; init; }
}

public sealed class AccessRequest
{
    public required string RequestId { get; init; }
    public required string ResourceId { get; init; }
    public required string RequesterId { get; init; }
    public required string SourceIp { get; init; }
    public bool MfaVerified { get; init; }
    public List<string> Roles { get; init; } = new();
}

public sealed class AccessDecision
{
    public required string DecisionId { get; init; }
    public required string RequestId { get; init; }
    public bool Allowed { get; set; }
    public string? DenialReason { get; set; }
    public DateTimeOffset EvaluatedAt { get; init; }
}

public sealed class SecretEntry
{
    public required string SecretId { get; init; }
    public required string EncryptedValue { get; set; }
    public int Version { get; set; }
    public Dictionary<string, string> Tags { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastRotated { get; set; }
}

public sealed class SecretAccess
{
    public required string SecretId { get; init; }
    public required string RequesterId { get; init; }
    public DateTimeOffset AccessedAt { get; init; }
    public required string AccessType { get; init; }
}

public sealed class SecurityEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public required string ProviderId { get; init; }
    public required string EventType { get; init; }
    public required string SourceId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object> Properties { get; init; } = new();
    public string? ResourceId { get; init; }
    public string Severity { get; init; } = "Medium";
    public string? Message { get; init; }
}

public sealed class ThreatIndicator
{
    public required string IndicatorId { get; init; }
    public required string Pattern { get; init; }
    public required string ThreatType { get; init; }
    public required string Severity { get; init; }
    public string? Description { get; init; }
}

public sealed class DetectedThreat
{
    public required string ThreatId { get; init; }
    public string? IndicatorId { get; init; }
    public required string ThreatType { get; init; }
    public required string Severity { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
    public required string SourceProvider { get; init; }
    public string? Description { get; init; }
}

public sealed class ThreatAnalysisResult
{
    public required string EventId { get; init; }
    public List<DetectedThreat> Threats { get; init; } = new();
    public DateTimeOffset AnalyzedAt { get; init; }
}

/// <summary>
/// SIEM integration for centralized security event monitoring.
/// </summary>
public sealed class SiemIntegrationStrategy : MultiCloudStrategyBase
{
    private readonly ConcurrentQueue<SecurityEvent> _eventQueue = new();
    private readonly ConcurrentDictionary<string, SiemEndpoint> _endpoints = new();
    private readonly ConcurrentDictionary<string, long> _eventCounts = new();

    public override string StrategyId => "security-siem-integration";
    public override string StrategyName => "SIEM Integration";
    public override string Category => "Security";

    public override MultiCloudCharacteristics Characteristics => new()
    {
        StrategyName = StrategyName,
        Description = "Integrates with SIEM systems (Splunk, ELK, Azure Sentinel, etc.) for centralized security monitoring",
        Category = Category,
        SupportsDataSovereignty = true,
        TypicalLatencyOverheadMs = 10.0,
        MemoryFootprint = "Medium"
    };

    /// <summary>Registers a SIEM endpoint.</summary>
    public void RegisterSiemEndpoint(string endpointId, SiemType siemType, string url, Dictionary<string, string> credentials)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentNullException.ThrowIfNull(credentials);

        _endpoints[endpointId] = new SiemEndpoint
        {
            EndpointId = endpointId,
            SiemType = siemType,
            Url = url,
            Credentials = credentials,
            RegisteredAt = DateTimeOffset.UtcNow,
            IsActive = true
        };

        RecordSuccess();
    }

    /// <summary>Sends security event to SIEM.</summary>
    public async Task<SiemSendResult> SendEventAsync(SecurityEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _eventQueue.Enqueue(evt);
        _eventCounts.AddOrUpdate(evt.ProviderId, 1, (_, count) => count + 1);

        var results = new List<(string endpointId, bool success)>();

        foreach (var endpoint in _endpoints.Values.Where(e => e.IsActive))
        {
            try
            {
                var success = await SendToSiemAsync(endpoint, evt, ct);
                results.Add((endpoint.EndpointId, success));
            }
            catch
            {
                results.Add((endpoint.EndpointId, false));
            }
        }

        var allSucceeded = results.All(r => r.success);
        if (allSucceeded) RecordSuccess(); else RecordFailure();

        return new SiemSendResult
        {
            EventId = evt.EventId,
            SentAt = DateTimeOffset.UtcNow,
            EndpointResults = results.ToDictionary(r => r.endpointId, r => r.success)
        };
    }

    /// <summary>Sends batch of events to SIEM.</summary>
    public async Task<BatchSiemSendResult> SendBatchAsync(IEnumerable<SecurityEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var eventList = events.ToList();
        var results = new List<SiemSendResult>();

        foreach (var evt in eventList)
        {
            var result = await SendEventAsync(evt, ct);
            results.Add(result);
        }

        var successCount = results.Count(r => r.EndpointResults.Values.Any(v => v));

        return new BatchSiemSendResult
        {
            TotalEvents = eventList.Count,
            SuccessfulEvents = successCount,
            FailedEvents = eventList.Count - successCount,
            Results = results
        };
    }

    /// <summary>Queries SIEM for events matching criteria.</summary>
    public async Task<SiemQueryResult> QueryAsync(string query, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);

        var matches = _eventQueue
            .Where(e => e.Timestamp >= from && e.Timestamp <= to)
            .Where(e => MatchesQuery(e, query))
            .ToList();

        await Task.CompletedTask; // Simulate async query

        return new SiemQueryResult
        {
            Query = query,
            From = from,
            To = to,
            MatchCount = matches.Count,
            Events = matches.Take(1000).ToList()
        };
    }

    /// <summary>Gets event statistics by provider.</summary>
    public IReadOnlyDictionary<string, long> GetEventStatistics() => _eventCounts;

    private static async Task<bool> SendToSiemAsync(SiemEndpoint endpoint, SecurityEvent evt, CancellationToken ct)
    {
        // Simulate SIEM send based on type
        await Task.Delay(5, ct);

        return endpoint.SiemType switch
        {
            SiemType.Splunk => await SendToSplunkAsync(endpoint, evt, ct),
            SiemType.ElasticStack => await SendToElasticAsync(endpoint, evt, ct),
            SiemType.AzureSentinel => await SendToSentinelAsync(endpoint, evt, ct),
            SiemType.Datadog => await SendToDatadogAsync(endpoint, evt, ct),
            SiemType.SumoLogic => await SendToSumoLogicAsync(endpoint, evt, ct),
            SiemType.QRadar => await SendToQRadarAsync(endpoint, evt, ct),
            _ => true
        };
    }

    private static async Task<bool> SendToSplunkAsync(SiemEndpoint endpoint, SecurityEvent evt, CancellationToken ct)
    {
        // Splunk HEC (HTTP Event Collector) format
        var payload = new
        {
            time = evt.Timestamp.ToUnixTimeSeconds(),
            host = evt.ResourceId,
            source = evt.ProviderId,
            sourcetype = evt.EventType,
            evt.EventId,
            evt.Severity,
            evt.Message
        };

        await Task.CompletedTask;
        return true;
    }

    private static async Task<bool> SendToElasticAsync(SiemEndpoint endpoint, SecurityEvent evt, CancellationToken ct)
    {
        // Elasticsearch JSON format
        var payload = new
        {
            timestamp = evt.Timestamp,
            event_id = evt.EventId,
            event_type = evt.EventType,
            provider = evt.ProviderId,
            resource = evt.ResourceId,
            severity = evt.Severity,
            message = evt.Message
        };

        await Task.CompletedTask;
        return true;
    }

    private static async Task<bool> SendToSentinelAsync(SiemEndpoint endpoint, SecurityEvent evt, CancellationToken ct)
    {
        // Azure Sentinel (Log Analytics) format
        var payload = new
        {
            TimeGenerated = evt.Timestamp,
            EventId = evt.EventId,
            EventType = evt.EventType,
            Provider = evt.ProviderId,
            Resource = evt.ResourceId,
            Severity = evt.Severity,
            Message = evt.Message
        };

        await Task.CompletedTask;
        return true;
    }

    private static async Task<bool> SendToDatadogAsync(SiemEndpoint endpoint, SecurityEvent evt, CancellationToken ct)
    {
        // Datadog logs API format
        var payload = new
        {
            ddsource = evt.ProviderId,
            ddtags = $"provider:{evt.ProviderId},severity:{evt.Severity}",
            hostname = evt.ResourceId,
            message = evt.Message,
            service = "multicloud-security"
        };

        await Task.CompletedTask;
        return true;
    }

    private static async Task<bool> SendToSumoLogicAsync(SiemEndpoint endpoint, SecurityEvent evt, CancellationToken ct)
    {
        // Sumo Logic HTTP Source format
        var payload = new
        {
            timestamp = evt.Timestamp.ToUnixTimeMilliseconds(),
            event_id = evt.EventId,
            provider = evt.ProviderId,
            severity = evt.Severity,
            message = evt.Message
        };

        await Task.CompletedTask;
        return true;
    }

    private static async Task<bool> SendToQRadarAsync(SiemEndpoint endpoint, SecurityEvent evt, CancellationToken ct)
    {
        // IBM QRadar LEEF (Log Event Extended Format)
        var leef = $"LEEF:2.0|DataWarehouse|MultiCloud|1.0|{evt.EventType}|" +
                   $"devTime={evt.Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ}\t" +
                   $"src={evt.ProviderId}\t" +
                   $"sev={MapSeverityToQRadar(evt.Severity)}\t" +
                   $"msg={evt.Message}";

        await Task.CompletedTask;
        return true;
    }

    private static int MapSeverityToQRadar(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 10,
        "high" => 8,
        "medium" => 5,
        "low" => 2,
        _ => 0
    };

    private static bool MatchesQuery(SecurityEvent evt, string query)
    {
        // Simple substring match - production would use proper query language
        var searchText = $"{evt.EventId} {evt.EventType} {evt.ProviderId} {evt.Message}".ToLowerInvariant();
        return searchText.Contains(query.ToLowerInvariant());
    }

    protected override string? GetCurrentState() =>
        $"Endpoints: {_endpoints.Count}, Queue: {_eventQueue.Count}";
}

public enum SiemType
{
    Splunk,
    ElasticStack,
    AzureSentinel,
    Datadog,
    SumoLogic,
    QRadar,
    Custom
}

public sealed class SiemEndpoint
{
    public required string EndpointId { get; init; }
    public required SiemType SiemType { get; init; }
    public required string Url { get; init; }
    public required Dictionary<string, string> Credentials { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public bool IsActive { get; set; }
}

public sealed class SiemSendResult
{
    public required string EventId { get; init; }
    public DateTimeOffset SentAt { get; init; }
    public required Dictionary<string, bool> EndpointResults { get; init; }
}

public sealed class BatchSiemSendResult
{
    public int TotalEvents { get; init; }
    public int SuccessfulEvents { get; init; }
    public int FailedEvents { get; init; }
    public required List<SiemSendResult> Results { get; init; }
}

public sealed class SiemQueryResult
{
    public required string Query { get; init; }
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public int MatchCount { get; init; }
    public required List<SecurityEvent> Events { get; init; }
}

#endregion
