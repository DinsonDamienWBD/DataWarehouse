using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.ZeroTrust
{
    /// <summary>
    /// Network micro-segmentation strategy for fine-grained access control.
    /// Implements zone-based network policies, application-layer segmentation, and dynamic policy updates based on threat level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Micro-segmentation principles:
    /// - Zero Trust network architecture
    /// - Workload-to-workload isolation
    /// - East-west traffic control (not just north-south)
    /// - Application-aware network policies
    /// - Dynamic security zones based on threat intelligence
    /// </para>
    /// <para>
    /// Segmentation strategies:
    /// - Zone-based: Group resources into security zones
    /// - Application-layer: Segment by application identity
    /// - Dynamic: Adjust policies based on threat level
    /// - Least privilege: Only allow necessary communications
    /// </para>
    /// </remarks>
    public sealed class MicroSegmentationStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, SecurityZone> _securityZones = new();
        private readonly ConcurrentDictionary<string, SegmentationPolicy> _policies = new();
        private readonly ConcurrentDictionary<string, WorkloadRecord> _workloads = new();
        private ThreatLevel _currentThreatLevel = ThreatLevel.Normal;
        private bool _enableDynamicSegmentation = true;

        /// <inheritdoc/>
        public override string StrategyId => "micro-segmentation";

        /// <inheritdoc/>
        public override string StrategyName => "Network Micro-Segmentation";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 15000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("EnableDynamicSegmentation", out var dynamic) && dynamic is bool enableDynamic)
            {
                _enableDynamicSegmentation = enableDynamic;
            }

            if (configuration.TryGetValue("InitialThreatLevel", out var threatLevel) && threatLevel is string levelStr)
            {
                if (Enum.TryParse<ThreatLevel>(levelStr, true, out var level))
                {
                    _currentThreatLevel = level;
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Defines a security zone.
        /// </summary>
        public SecurityZone DefineZone(string zoneName, SecurityLevel securityLevel, string[] allowedProtocols)
        {
            var zone = new SecurityZone
            {
                ZoneName = zoneName,
                SecurityLevel = securityLevel,
                AllowedProtocols = allowedProtocols,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            _securityZones[zoneName] = zone;
            return zone;
        }

        /// <summary>
        /// Registers a workload in a security zone.
        /// </summary>
        public WorkloadRecord RegisterWorkload(string workloadId, string zoneName, string ipAddress, string[] tags)
        {
            if (!_securityZones.ContainsKey(zoneName))
                throw new ArgumentException($"Security zone not found: {zoneName}", nameof(zoneName));

            var workload = new WorkloadRecord
            {
                WorkloadId = workloadId,
                ZoneName = zoneName,
                IpAddress = ipAddress,
                Tags = tags,
                RegisteredAt = DateTime.UtcNow
            };

            _workloads[workloadId] = workload;
            return workload;
        }

        /// <summary>
        /// Adds a segmentation policy between zones.
        /// </summary>
        public SegmentationPolicy AddPolicy(string policyName, string sourceZone, string targetZone,
            string[] allowedProtocols, int[] allowedPorts, string[]? requiredTags)
        {
            var policy = new SegmentationPolicy
            {
                PolicyName = policyName,
                SourceZone = sourceZone,
                TargetZone = targetZone,
                AllowedProtocols = allowedProtocols,
                AllowedPorts = allowedPorts,
                RequiredTags = requiredTags ?? Array.Empty<string>(),
                CreatedAt = DateTime.UtcNow,
                IsActive = true,
                MinThreatLevel = ThreatLevel.Normal
            };

            _policies[policyName] = policy;
            return policy;
        }

        /// <summary>
        /// Updates the current threat level and adjusts policies dynamically.
        /// </summary>
        public void UpdateThreatLevel(ThreatLevel newLevel)
        {
            var oldLevel = _currentThreatLevel;
            _currentThreatLevel = newLevel;

            if (_enableDynamicSegmentation && newLevel > oldLevel)
            {
                // Tighten policies when threat level increases
                TightenPolicies(newLevel);
            }
            else if (_enableDynamicSegmentation && newLevel < oldLevel)
            {
                // Relax policies when threat level decreases
                RelaxPolicies(newLevel);
            }
        }

        /// <summary>
        /// Tightens policies in response to increased threat level.
        /// </summary>
        private void TightenPolicies(ThreatLevel threatLevel)
        {
            foreach (var policy in _policies.Values)
            {
                // Disable policies that don't meet the minimum threat level requirement
                if (policy.MinThreatLevel < threatLevel)
                {
                    policy.IsActive = false;
                }
            }
        }

        /// <summary>
        /// Relaxes policies in response to decreased threat level.
        /// </summary>
        private void RelaxPolicies(ThreatLevel threatLevel)
        {
            foreach (var policy in _policies.Values)
            {
                // Re-enable policies that now meet the threat level requirement
                if (policy.MinThreatLevel <= threatLevel && !policy.IsActive)
                {
                    policy.IsActive = true;
                }
            }
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Extract source and target identifiers
            var sourceId = context.SubjectId;
            var targetId = context.ResourceId;

            // Find source workload
            if (!_workloads.TryGetValue(sourceId, out var sourceWorkload))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Source workload not registered: {sourceId}",
                    ApplicablePolicies = new[] { "MicroSegmentation.UnregisteredSource" }
                });
            }

            // Find target workload
            if (!_workloads.TryGetValue(targetId, out var targetWorkload))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Target workload not registered: {targetId}",
                    ApplicablePolicies = new[] { "MicroSegmentation.UnregisteredTarget" }
                });
            }

            // Check if zones exist
            if (!_securityZones.TryGetValue(sourceWorkload.ZoneName, out var sourceZone))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Source zone not found: {sourceWorkload.ZoneName}",
                    ApplicablePolicies = new[] { "MicroSegmentation.InvalidSourceZone" }
                });
            }

            if (!_securityZones.TryGetValue(targetWorkload.ZoneName, out var targetZone))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Target zone not found: {targetWorkload.ZoneName}",
                    ApplicablePolicies = new[] { "MicroSegmentation.InvalidTargetZone" }
                });
            }

            // Check if zones are active
            if (!sourceZone.IsActive || !targetZone.IsActive)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Source or target zone is inactive",
                    ApplicablePolicies = new[] { "MicroSegmentation.InactiveZone" }
                });
            }

            // Find applicable policies
            var applicablePolicies = _policies.Values
                .Where(p => p.IsActive &&
                           p.SourceZone == sourceWorkload.ZoneName &&
                           p.TargetZone == targetWorkload.ZoneName &&
                           p.MinThreatLevel <= _currentThreatLevel)
                .ToList();

            if (!applicablePolicies.Any())
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"No policy allows communication from {sourceWorkload.ZoneName} to {targetWorkload.ZoneName}",
                    ApplicablePolicies = new[] { "MicroSegmentation.NoMatchingPolicy" }
                });
            }

            // Extract protocol and port from context
            var protocol = context.EnvironmentAttributes.TryGetValue("Protocol", out var proto) && proto is string protocolStr
                ? protocolStr : "tcp";

            var port = context.EnvironmentAttributes.TryGetValue("Port", out var portObj) && portObj is int portInt
                ? portInt : 0;

            // Check if any policy allows the protocol and port
            var allowingPolicy = applicablePolicies.FirstOrDefault(p =>
                (p.AllowedProtocols.Contains("*") || p.AllowedProtocols.Contains(protocol, StringComparer.OrdinalIgnoreCase)) &&
                (p.AllowedPorts.Contains(0) || p.AllowedPorts.Contains(port)));

            if (allowingPolicy == null)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Protocol '{protocol}' or port '{port}' not allowed by any policy",
                    ApplicablePolicies = applicablePolicies.Select(p => p.PolicyName).ToArray()
                });
            }

            // Check required tags (if any)
            if (allowingPolicy.RequiredTags.Any())
            {
                var missingTags = allowingPolicy.RequiredTags.Except(sourceWorkload.Tags).ToArray();
                if (missingTags.Any())
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"Source workload missing required tags: {string.Join(", ", missingTags)}",
                        ApplicablePolicies = new[] { allowingPolicy.PolicyName }
                    });
                }
            }

            // Access granted
            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Micro-segmentation policy allows access",
                ApplicablePolicies = new[] { allowingPolicy.PolicyName },
                Metadata = new Dictionary<string, object>
                {
                    ["SourceZone"] = sourceWorkload.ZoneName,
                    ["TargetZone"] = targetWorkload.ZoneName,
                    ["Protocol"] = protocol,
                    ["Port"] = port,
                    ["ThreatLevel"] = _currentThreatLevel.ToString(),
                    ["SourceIp"] = sourceWorkload.IpAddress,
                    ["TargetIp"] = targetWorkload.IpAddress
                }
            });
        }
    }

    /// <summary>
    /// Security zone definition.
    /// </summary>
    public sealed class SecurityZone
    {
        public required string ZoneName { get; init; }
        public required SecurityLevel SecurityLevel { get; init; }
        public required string[] AllowedProtocols { get; init; }
        public required DateTime CreatedAt { get; init; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Workload registration in a security zone.
    /// </summary>
    public sealed class WorkloadRecord
    {
        public required string WorkloadId { get; init; }
        public required string ZoneName { get; init; }
        public required string IpAddress { get; init; }
        public required string[] Tags { get; init; }
        public required DateTime RegisteredAt { get; init; }
    }

    /// <summary>
    /// Segmentation policy between zones.
    /// </summary>
    public sealed class SegmentationPolicy
    {
        public required string PolicyName { get; init; }
        public required string SourceZone { get; init; }
        public required string TargetZone { get; init; }
        public required string[] AllowedProtocols { get; init; }
        public required int[] AllowedPorts { get; init; }
        public required string[] RequiredTags { get; init; }
        public required DateTime CreatedAt { get; init; }
        public bool IsActive { get; set; }
        public ThreatLevel MinThreatLevel { get; set; }
    }

    /// <summary>
    /// Security level for zones.
    /// </summary>
    public enum SecurityLevel
    {
        Public,
        Internal,
        Restricted,
        Confidential,
        HighlySensitive
    }

    /// <summary>
    /// Threat level for dynamic policy adjustment.
    /// </summary>
    public enum ThreatLevel
    {
        Low = 0,
        Normal = 1,
        Elevated = 2,
        High = 3,
        Critical = 4
    }
}
