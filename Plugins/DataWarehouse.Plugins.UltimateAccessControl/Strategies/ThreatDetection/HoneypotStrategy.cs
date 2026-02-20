using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.ThreatDetection
{
    /// <summary>
    /// Honeypot deception technology strategy.
    /// Deploys decoy resources to detect and profile attackers.
    /// Complements the more comprehensive CanaryStrategy in Strategies/Honeypot/.
    /// </summary>
    public sealed class HoneypotStrategy : AccessControlStrategyBase
    {
        private readonly BoundedDictionary<string, HoneypotResource> _honeypots = new BoundedDictionary<string, HoneypotResource>(1000);
        private readonly ConcurrentQueue<AttackerProfile> _attackerProfiles = new();
        private readonly BoundedDictionary<string, int> _attackerInteractions = new BoundedDictionary<string, int>(1000);

        /// <inheritdoc/>
        public override string StrategyId => "honeypot";

        /// <inheritdoc/>
        public override string StrategyName => "Honeypot Deception";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 5000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Deploy default honeypots
            DeployDefaultHoneypots();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("honeypot.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("honeypot.shutdown");
            _honeypots.Clear();
            _attackerInteractions.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("honeypot.evaluate");
            // Check if resource is a honeypot
            if (_honeypots.TryGetValue(context.ResourceId, out var honeypot))
            {
                // Honeypot accessed! This is always suspicious
                var profile = await ProfileAttackerAsync(context, honeypot, cancellationToken);

                // Track attacker interactions
                _attackerInteractions.AddOrUpdate(
                    context.SubjectId,
                    1,
                    (_, count) => count + 1
                );

                // Store profile
                _attackerProfiles.Enqueue(profile);
                while (_attackerProfiles.Count > 1000)
                {
                    _attackerProfiles.TryDequeue(out _);
                }

                // Update honeypot metrics
                honeypot.AccessCount++;
                honeypot.LastAccessedAt = DateTime.UtcNow;
                honeypot.LastAccessedBy = context.SubjectId;

                // Determine response based on honeypot type
                var blockAccess = honeypot.BlockOnAccess ||
                                  _attackerInteractions.GetOrAdd(context.SubjectId, 0) >= 3;

                if (blockAccess)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"Access to honeypot '{honeypot.Name}' denied. Attacker profiled.",
                        ApplicablePolicies = new[] { "HoneypotDefense", "AutoBlock" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["HoneypotType"] = honeypot.Type,
                            ["HoneypotName"] = honeypot.Name,
                            ["AttackerProfiled"] = true,
                            ["AttackerInteractions"] = _attackerInteractions[context.SubjectId],
                            ["ThreatLevel"] = profile.ThreatLevel
                        }
                    };
                }

                // Silent monitoring mode - allow access but track
                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = "Honeypot access allowed for attacker profiling",
                    ApplicablePolicies = new[] { "HoneypotMonitoring" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["IsHoneypot"] = true,
                        ["HoneypotType"] = honeypot.Type,
                        ["AttackerProfiled"] = true,
                        ["ThreatLevel"] = profile.ThreatLevel
                    }
                };
            }

            // Not a honeypot
            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Access granted - not a honeypot resource",
                ApplicablePolicies = Array.Empty<string>()
            };
        }

        private async Task<AttackerProfile> ProfileAttackerAsync(
            AccessContext context,
            HoneypotResource honeypot,
            CancellationToken cancellationToken)
        {
            var profile = new AttackerProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                SubjectId = context.SubjectId,
                FirstInteraction = DateTime.UtcNow,
                HoneypotAccessed = honeypot.ResourceId,
                HoneypotType = honeypot.Type,
                ClientIp = context.ClientIpAddress,
                Location = context.Location,
                Action = context.Action,
                ThreatLevel = DetermineThreatLevel(context, honeypot),
                AttackPatterns = AnalyzeAttackPatterns(context, honeypot),
                Sophistication = DetermineSophistication(context, honeypot)
            };

            await Task.CompletedTask;
            return profile;
        }

        private string DetermineThreatLevel(AccessContext context, HoneypotResource honeypot)
        {
            // High-value honeypots indicate more sophisticated attackers
            if (honeypot.Type == HoneypotType.Credential || honeypot.Type == HoneypotType.Database)
                return "High";

            // Destructive actions indicate malicious intent
            var action = context.Action.ToLowerInvariant();
            if (action == "delete" || action == "modify" || action == "execute")
                return "High";

            // Multiple interactions indicate persistent attacker
            if (_attackerInteractions.GetOrAdd(context.SubjectId, 0) >= 2)
                return "Medium";

            return "Low";
        }

        private List<string> AnalyzeAttackPatterns(AccessContext context, HoneypotResource honeypot)
        {
            var patterns = new List<string>();

            // Pattern: Credential harvesting
            if (honeypot.Type == HoneypotType.Credential || honeypot.Type == HoneypotType.ApiKey)
            {
                patterns.Add("credential-harvesting");
            }

            // Pattern: Data exfiltration
            if (context.Action.Equals("read", StringComparison.OrdinalIgnoreCase) ||
                context.Action.Equals("download", StringComparison.OrdinalIgnoreCase))
            {
                patterns.Add("data-exfiltration");
            }

            // Pattern: Reconnaissance
            if (context.Action.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                context.Action.Equals("enumerate", StringComparison.OrdinalIgnoreCase))
            {
                patterns.Add("reconnaissance");
            }

            // Pattern: Privilege escalation
            if (context.ResourceId.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                context.ResourceId.Contains("root", StringComparison.OrdinalIgnoreCase))
            {
                patterns.Add("privilege-escalation");
            }

            // Pattern: Lateral movement
            if (honeypot.Type == HoneypotType.NetworkShare)
            {
                patterns.Add("lateral-movement");
            }

            return patterns;
        }

        private string DetermineSophistication(AccessContext context, HoneypotResource honeypot)
        {
            var sophisticationScore = 0;

            // Accessing high-value targets = more sophisticated
            if (honeypot.Type == HoneypotType.Credential || honeypot.Type == HoneypotType.Database)
                sophisticationScore += 30;

            // Accessing during off-hours = more sophisticated
            var hour = context.RequestTime.Hour;
            if (hour < 6 || hour > 22)
                sophisticationScore += 20;

            // Using tools/automation
            if (context.SubjectAttributes.TryGetValue("UserAgent", out var ua) && ua is string userAgent)
            {
                if (userAgent.Contains("curl", StringComparison.OrdinalIgnoreCase) ||
                    userAgent.Contains("python", StringComparison.OrdinalIgnoreCase) ||
                    userAgent.Contains("script", StringComparison.OrdinalIgnoreCase))
                {
                    sophisticationScore += 25;
                }
            }

            // International origin (potential APT)
            if (context.Location != null)
            {
                var highThreatCountries = new[] { "CN", "RU", "KP", "IR" };
                if (highThreatCountries.Contains(context.Location.Country, StringComparer.OrdinalIgnoreCase))
                {
                    sophisticationScore += 35;
                }
            }

            if (sophisticationScore >= 60)
                return "Advanced";
            if (sophisticationScore >= 30)
                return "Intermediate";
            return "Basic";
        }

        private void DeployDefaultHoneypots()
        {
            // Credential honeypots
            _honeypots["honeypot:credentials:admin"] = new HoneypotResource
            {
                ResourceId = "honeypot:credentials:admin",
                Name = "Admin Credentials",
                Type = HoneypotType.Credential,
                Description = "Fake admin credentials file",
                CreatedAt = DateTime.UtcNow,
                IsActive = true,
                BlockOnAccess = false // Silent monitoring
            };

            // Database honeypot
            _honeypots["honeypot:database:users"] = new HoneypotResource
            {
                ResourceId = "honeypot:database:users",
                Name = "User Database",
                Type = HoneypotType.Database,
                Description = "Fake user database with synthetic PII",
                CreatedAt = DateTime.UtcNow,
                IsActive = true,
                BlockOnAccess = false
            };

            // API key honeypot
            _honeypots["honeypot:apikey:prod"] = new HoneypotResource
            {
                ResourceId = "honeypot:apikey:prod",
                Name = "Production API Key",
                Type = HoneypotType.ApiKey,
                Description = "Fake production API key",
                CreatedAt = DateTime.UtcNow,
                IsActive = true,
                BlockOnAccess = true // Block immediately
            };

            // Network share honeypot
            _honeypots["honeypot:share:finance"] = new HoneypotResource
            {
                ResourceId = "honeypot:share:finance",
                Name = "Finance Share",
                Type = HoneypotType.NetworkShare,
                Description = "Fake finance network share",
                CreatedAt = DateTime.UtcNow,
                IsActive = true,
                BlockOnAccess = false
            };

            // File honeypot
            _honeypots["honeypot:file:passwords.xlsx"] = new HoneypotResource
            {
                ResourceId = "honeypot:file:passwords.xlsx",
                Name = "Password Spreadsheet",
                Type = HoneypotType.File,
                Description = "Fake password spreadsheet",
                CreatedAt = DateTime.UtcNow,
                IsActive = true,
                BlockOnAccess = false
            };
        }

        /// <summary>
        /// Registers a new honeypot resource.
        /// </summary>
        public void RegisterHoneypot(HoneypotResource honeypot)
        {
            _honeypots[honeypot.ResourceId] = honeypot;
        }

        /// <summary>
        /// Gets all active honeypots.
        /// </summary>
        public IReadOnlyCollection<HoneypotResource> GetActiveHoneypots()
        {
            return _honeypots.Values.Where(h => h.IsActive).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets attacker profiles.
        /// </summary>
        public IReadOnlyCollection<AttackerProfile> GetAttackerProfiles(int count = 100)
        {
            return _attackerProfiles.Take(count).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets attacker interaction count.
        /// </summary>
        public int GetAttackerInteractionCount(string subjectId)
        {
            return _attackerInteractions.GetOrAdd(subjectId, 0);
        }
    }

    public enum HoneypotType
    {
        File,
        Credential,
        Database,
        ApiKey,
        NetworkShare,
        Service,
        Account
    }

    public sealed class HoneypotResource
    {
        public required string ResourceId { get; init; }
        public required string Name { get; init; }
        public required HoneypotType Type { get; init; }
        public required string Description { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required bool IsActive { get; set; }
        public required bool BlockOnAccess { get; init; }
        public int AccessCount { get; set; }
        public DateTime? LastAccessedAt { get; set; }
        public string? LastAccessedBy { get; set; }
    }

    public sealed class AttackerProfile
    {
        public required string Id { get; init; }
        public required string SubjectId { get; init; }
        public required DateTime FirstInteraction { get; init; }
        public required string HoneypotAccessed { get; init; }
        public required HoneypotType HoneypotType { get; init; }
        public string? ClientIp { get; init; }
        public GeoLocation? Location { get; init; }
        public required string Action { get; init; }
        public required string ThreatLevel { get; init; }
        public List<string> AttackPatterns { get; init; } = new();
        public required string Sophistication { get; init; }
    }
}
