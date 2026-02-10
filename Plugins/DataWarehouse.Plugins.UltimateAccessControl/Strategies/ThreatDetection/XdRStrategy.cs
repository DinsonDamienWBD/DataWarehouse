using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.ThreatDetection
{
    /// <summary>
    /// Extended Detection and Response (XDR) strategy.
    /// Provides cross-domain correlation (network + endpoint + identity), unified threat timeline, and automated investigation workflows.
    /// </summary>
    public sealed class XdRStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, ThreatInvestigation> _activeInvestigations = new();
        private readonly ConcurrentQueue<CorrelatedThreatEvent> _eventTimeline = new();
        private readonly ConcurrentDictionary<string, CrossDomainThreatProfile> _profiles = new();

        /// <inheritdoc/>
        public override string StrategyId => "xdr";

        /// <inheritdoc/>
        public override string StrategyName => "Extended Detection and Response";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 10000
        };

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Get or create cross-domain profile
            var profile = _profiles.GetOrAdd(context.SubjectId, _ => new CrossDomainThreatProfile
            {
                SubjectId = context.SubjectId,
                FirstSeen = DateTime.UtcNow
            });

            // Correlate across domains (network, endpoint, identity)
            var correlatedEvent = await CorrelateAcrossDomainsAsync(context, profile, cancellationToken);

            // Add to unified timeline
            _eventTimeline.Enqueue(correlatedEvent);
            while (_eventTimeline.Count > 10000)
            {
                _eventTimeline.TryDequeue(out _);
            }

            // Update profile
            UpdateProfile(profile, correlatedEvent);

            // Calculate cross-domain risk score
            var riskScore = CalculateCrossDomainRiskScore(correlatedEvent, profile);

            // Trigger automated investigation for high-risk events
            if (riskScore >= 70.0 && !_activeInvestigations.ContainsKey(correlatedEvent.Id))
            {
                var investigation = await InitiateAutomatedInvestigationAsync(correlatedEvent, profile, cancellationToken);
                _activeInvestigations[investigation.Id] = investigation;
            }

            // Block critical cross-domain threats
            if (riskScore >= 85.0)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Critical cross-domain threat detected (score: {riskScore:F2}): {correlatedEvent.ThreatSummary}",
                    ApplicablePolicies = new[] { "XdrAutoBlock" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["RiskScore"] = riskScore,
                        ["CorrelatedDomains"] = correlatedEvent.CorrelatedDomains.Count,
                        ["ThreatSummary"] = correlatedEvent.ThreatSummary,
                        ["InvestigationId"] = _activeInvestigations.Values.FirstOrDefault(i => i.SubjectId == context.SubjectId)?.Id ?? "none"
                    }
                };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = riskScore >= 70.0
                    ? $"Access granted with cross-domain threat warning (score: {riskScore:F2})"
                    : "Access granted - normal cross-domain behavior",
                ApplicablePolicies = new[] { "XdrMonitoring" },
                Metadata = new Dictionary<string, object>
                {
                    ["RiskScore"] = riskScore,
                    ["CorrelatedDomains"] = correlatedEvent.CorrelatedDomains.Count
                }
            };
        }

        private async Task<CorrelatedThreatEvent> CorrelateAcrossDomainsAsync(
            AccessContext context,
            CrossDomainThreatProfile profile,
            CancellationToken cancellationToken)
        {
            var correlatedEvent = new CorrelatedThreatEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                SubjectId = context.SubjectId,
                ResourceId = context.ResourceId,
                Action = context.Action,
                ThreatSummary = "",
                AttackChainStage = ""
            };

            var signals = new List<ThreatSignal>();

            // Domain 1: Identity signals
            var identitySignals = AnalyzeIdentityDomain(context, profile);
            signals.AddRange(identitySignals);

            // Domain 2: Network signals
            var networkSignals = AnalyzeNetworkDomain(context, profile);
            signals.AddRange(networkSignals);

            // Domain 3: Endpoint signals
            var endpointSignals = AnalyzeEndpointDomain(context, profile);
            signals.AddRange(endpointSignals);

            // Correlate signals to identify multi-stage attacks
            correlatedEvent.Signals = signals;
            correlatedEvent.CorrelatedDomains = signals.Select(s => s.Domain).Distinct().ToList();
            correlatedEvent.ThreatSummary = BuildThreatSummary(signals);
            correlatedEvent.AttackChainStage = DetermineAttackChainStage(signals);

            await Task.CompletedTask;
            return correlatedEvent;
        }

        private List<ThreatSignal> AnalyzeIdentityDomain(AccessContext context, CrossDomainThreatProfile profile)
        {
            var signals = new List<ThreatSignal>();

            // Unusual time access
            var hour = context.RequestTime.Hour;
            if (hour < 6 || hour > 22)
            {
                signals.Add(new ThreatSignal
                {
                    Domain = "Identity",
                    SignalType = "temporal-anomaly",
                    Severity = 30,
                    Description = "Off-hours access"
                });
            }

            // Privilege escalation attempt
            if (context.Roles.Any(r => r.Contains("admin", StringComparison.OrdinalIgnoreCase)) &&
                !profile.HistoricalRoles.Any(r => r.Contains("admin", StringComparison.OrdinalIgnoreCase)))
            {
                signals.Add(new ThreatSignal
                {
                    Domain = "Identity",
                    SignalType = "privilege-escalation",
                    Severity = 70,
                    Description = "New elevated privileges detected"
                });
            }

            // Geographic anomaly
            if (context.Location != null &&
                !profile.ObservedCountries.Contains(context.Location.Country ?? "unknown"))
            {
                signals.Add(new ThreatSignal
                {
                    Domain = "Identity",
                    SignalType = "geographic-anomaly",
                    Severity = 40,
                    Description = $"Access from new country: {context.Location.Country}"
                });
            }

            return signals;
        }

        private List<ThreatSignal> AnalyzeNetworkDomain(AccessContext context, CrossDomainThreatProfile profile)
        {
            var signals = new List<ThreatSignal>();

            // External IP accessing internal resources
            var isExternalIp = context.ClientIpAddress != null &&
                              !context.ClientIpAddress.StartsWith("10.") &&
                              !context.ClientIpAddress.StartsWith("192.168.");

            var isInternalResource = context.ResourceId.Contains("internal", StringComparison.OrdinalIgnoreCase);

            if (isExternalIp && isInternalResource)
            {
                signals.Add(new ThreatSignal
                {
                    Domain = "Network",
                    SignalType = "lateral-movement",
                    Severity = 60,
                    Description = "External IP accessing internal resource"
                });
            }

            // High frequency access
            var recentAccesses = profile.AccessTimestamps
                .Where(t => (DateTime.UtcNow - t).TotalMinutes < 5)
                .Count();

            if (recentAccesses > 50)
            {
                signals.Add(new ThreatSignal
                {
                    Domain = "Network",
                    SignalType = "brute-force",
                    Severity = 50,
                    Description = $"{recentAccesses} accesses in 5 minutes"
                });
            }

            return signals;
        }

        private List<ThreatSignal> AnalyzeEndpointDomain(AccessContext context, CrossDomainThreatProfile profile)
        {
            var signals = new List<ThreatSignal>();

            // Suspicious process execution
            if (context.SubjectAttributes.TryGetValue("ProcessName", out var processObj) &&
                processObj is string processName)
            {
                var suspiciousProcesses = new[] { "mimikatz", "procdump", "psexec", "powersploit" };
                if (suspiciousProcesses.Any(s => processName.Contains(s, StringComparison.OrdinalIgnoreCase)))
                {
                    signals.Add(new ThreatSignal
                    {
                        Domain = "Endpoint",
                        SignalType = "malicious-process",
                        Severity = 80,
                        Description = $"Suspicious process: {processName}"
                    });
                }
            }

            // Credential access
            if (context.ResourceId.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                context.ResourceId.Contains("lsass", StringComparison.OrdinalIgnoreCase))
            {
                signals.Add(new ThreatSignal
                {
                    Domain = "Endpoint",
                    SignalType = "credential-access",
                    Severity = 70,
                    Description = "Credential store access detected"
                });
            }

            return signals;
        }

        private string BuildThreatSummary(List<ThreatSignal> signals)
        {
            if (signals.Count == 0)
                return "No threats detected";

            var topSignals = signals
                .OrderByDescending(s => s.Severity)
                .Take(3)
                .Select(s => $"{s.SignalType}({s.Severity})")
                .ToList();

            return string.Join(", ", topSignals);
        }

        private string DetermineAttackChainStage(List<ThreatSignal> signals)
        {
            // Map signals to MITRE ATT&CK stages
            if (signals.Any(s => s.SignalType == "initial-access"))
                return "Initial Access";

            if (signals.Any(s => s.SignalType == "privilege-escalation"))
                return "Privilege Escalation";

            if (signals.Any(s => s.SignalType == "credential-access"))
                return "Credential Access";

            if (signals.Any(s => s.SignalType == "lateral-movement"))
                return "Lateral Movement";

            if (signals.Any(s => s.SignalType == "exfiltration"))
                return "Exfiltration";

            return "Discovery";
        }

        private double CalculateCrossDomainRiskScore(CorrelatedThreatEvent event_obj, CrossDomainThreatProfile profile)
        {
            if (event_obj.Signals.Count == 0)
                return 0.0;

            // Base score from signals
            var baseScore = event_obj.Signals.Average(s => s.Severity);

            // Correlation multiplier (multiple domains = higher risk)
            var correlationMultiplier = 1.0 + (event_obj.CorrelatedDomains.Count - 1) * 0.3;

            // Attack chain multiplier
            var attackChainMultiplier = event_obj.AttackChainStage switch
            {
                "Initial Access" => 1.1,
                "Privilege Escalation" => 1.3,
                "Credential Access" => 1.4,
                "Lateral Movement" => 1.5,
                "Exfiltration" => 1.6,
                _ => 1.0
            };

            var finalScore = baseScore * correlationMultiplier * attackChainMultiplier;
            return Math.Min(finalScore, 100.0);
        }

        private async Task<ThreatInvestigation> InitiateAutomatedInvestigationAsync(
            CorrelatedThreatEvent event_obj,
            CrossDomainThreatProfile profile,
            CancellationToken cancellationToken)
        {
            var investigation = new ThreatInvestigation
            {
                Id = Guid.NewGuid().ToString("N"),
                SubjectId = event_obj.SubjectId,
                InitiatedAt = DateTime.UtcNow,
                TriggeringEventId = event_obj.Id,
                Status = InvestigationStatus.InProgress,
                Priority = event_obj.Signals.Max(s => s.Severity) >= 70 ? "High" : "Medium"
            };

            // Automated investigation steps
            investigation.Steps.Add("Collect related events from timeline");
            investigation.Steps.Add("Analyze attack chain progression");
            investigation.Steps.Add("Identify affected assets");
            investigation.Steps.Add("Assess lateral movement risk");
            investigation.Steps.Add("Generate containment recommendations");

            await Task.CompletedTask;
            return investigation;
        }

        private void UpdateProfile(CrossDomainThreatProfile profile, CorrelatedThreatEvent event_obj)
        {
            profile.TotalEvents++;
            profile.LastSeen = DateTime.UtcNow;

            profile.AccessTimestamps.Add(DateTime.UtcNow);
            if (profile.AccessTimestamps.Count > 500)
            {
                profile.AccessTimestamps.RemoveAt(0);
            }

            foreach (var signal in event_obj.Signals)
            {
                profile.ObservedSignalTypes.Add(signal.SignalType);
            }
        }

        /// <summary>
        /// Gets unified threat timeline.
        /// </summary>
        public IReadOnlyCollection<CorrelatedThreatEvent> GetThreatTimeline(int count = 100)
        {
            return _eventTimeline.Take(count).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets active investigations.
        /// </summary>
        public IReadOnlyCollection<ThreatInvestigation> GetActiveInvestigations()
        {
            return _activeInvestigations.Values.Where(i => i.Status == InvestigationStatus.InProgress).ToList().AsReadOnly();
        }
    }

    public sealed class CrossDomainThreatProfile
    {
        public required string SubjectId { get; init; }
        public required DateTime FirstSeen { get; init; }
        public DateTime LastSeen { get; set; }
        public int TotalEvents { get; set; }
        public List<DateTime> AccessTimestamps { get; init; } = new();
        public HashSet<string> ObservedCountries { get; init; } = new();
        public HashSet<string> HistoricalRoles { get; init; } = new();
        public HashSet<string> ObservedSignalTypes { get; init; } = new();
    }

    public sealed class CorrelatedThreatEvent
    {
        public required string Id { get; init; }
        public required DateTime Timestamp { get; init; }
        public required string SubjectId { get; init; }
        public required string ResourceId { get; init; }
        public required string Action { get; init; }
        public List<ThreatSignal> Signals { get; set; } = new();
        public List<string> CorrelatedDomains { get; set; } = new();
        public required string ThreatSummary { get; set; }
        public required string AttackChainStage { get; set; }
    }

    public sealed class ThreatSignal
    {
        public required string Domain { get; init; } // "Identity", "Network", "Endpoint"
        public required string SignalType { get; init; }
        public required int Severity { get; init; } // 0-100
        public required string Description { get; init; }
    }

    public sealed class ThreatInvestigation
    {
        public required string Id { get; init; }
        public required string SubjectId { get; init; }
        public required DateTime InitiatedAt { get; init; }
        public DateTime? CompletedAt { get; set; }
        public required string TriggeringEventId { get; init; }
        public required InvestigationStatus Status { get; set; }
        public required string Priority { get; init; }
        public List<string> Steps { get; init; } = new();
        public List<string> Findings { get; init; } = new();
    }

    public enum InvestigationStatus
    {
        InProgress,
        Completed,
        Escalated
    }
}
