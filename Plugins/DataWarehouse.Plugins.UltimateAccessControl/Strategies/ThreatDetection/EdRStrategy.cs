using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.ThreatDetection
{
    /// <summary>
    /// Endpoint Detection and Response (EDR) strategy.
    /// Monitors process execution, file integrity, and registry/configuration changes.
    /// </summary>
    public sealed class EdRStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, EndpointProfile> _endpointProfiles = new();
        private readonly ConcurrentQueue<EndpointThreat> _threats = new();
        private readonly HashSet<string> _suspiciousProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "mimikatz", "procdump", "psexec", "powersploit", "bloodhound",
            "sharphound", "rubeus", "certutil", "bitsadmin", "regsvr32"
        };

        /// <inheritdoc/>
        public override string StrategyId => "edr";

        /// <inheritdoc/>
        public override string StrategyName => "Endpoint Detection and Response";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 5000
        };

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Extract endpoint information
            var machineName = context.EnvironmentAttributes.TryGetValue("MachineName", out var machine)
                ? machine?.ToString() ?? "unknown"
                : "unknown";

            // Get or create endpoint profile
            var profile = _endpointProfiles.GetOrAdd(machineName, _ => new EndpointProfile
            {
                MachineName = machineName,
                FirstSeen = DateTime.UtcNow
            });

            // Analyze endpoint behavior
            var threatScore = await AnalyzeEndpointBehaviorAsync(context, profile, cancellationToken);

            // Update profile
            UpdateProfile(profile, context);

            // Record threat if detected
            if (threatScore >= 60.0)
            {
                var threat = new EndpointThreat
                {
                    Id = Guid.NewGuid().ToString("N"),
                    MachineName = machineName,
                    SubjectId = context.SubjectId,
                    DetectedAt = DateTime.UtcNow,
                    ThreatScore = threatScore,
                    ThreatType = DetermineThreatType(context, profile),
                    Details = BuildThreatDetails(context, profile)
                };

                _threats.Enqueue(threat);

                while (_threats.Count > 1000)
                {
                    _threats.TryDequeue(out _);
                }
            }

            // Block critical endpoint threats
            if (threatScore >= 80.0)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Critical endpoint threat detected (score: {threatScore:F2})",
                    ApplicablePolicies = new[] { "EndpointDefense", "AutoBlock" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["ThreatScore"] = threatScore,
                        ["ThreatType"] = DetermineThreatType(context, profile),
                        ["MachineName"] = machineName
                    }
                };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = threatScore >= 60.0
                    ? $"Access granted with endpoint threat warning (score: {threatScore:F2})"
                    : "Access granted - normal endpoint behavior",
                ApplicablePolicies = new[] { "EndpointMonitoring" },
                Metadata = new Dictionary<string, object>
                {
                    ["ThreatScore"] = threatScore
                }
            };
        }

        private async Task<double> AnalyzeEndpointBehaviorAsync(AccessContext context, EndpointProfile profile, CancellationToken cancellationToken)
        {
            var score = 0.0;

            // Check for suspicious process execution
            if (context.SubjectAttributes.TryGetValue("ProcessName", out var processObj) &&
                processObj is string processName)
            {
                if (_suspiciousProcessNames.Any(s => processName.Contains(s, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 70.0; // Known attack tool
                }

                // Check for living-off-the-land binaries (LOLBins)
                if (IsLolBin(processName))
                {
                    score += 40.0;
                }

                // Track process execution
                profile.ObservedProcesses.Add(processName);
            }

            // Check for file integrity violations
            var action = context.Action.ToLowerInvariant();
            if (action == "modify" || action == "write" || action == "delete")
            {
                if (IsCriticalSystemFile(context.ResourceId))
                {
                    score += 50.0; // Modification of critical system files
                }
            }

            // Check for registry modifications (Windows-specific)
            if (context.ResourceId.Contains("registry", StringComparison.OrdinalIgnoreCase) ||
                context.ResourceId.Contains("hkey", StringComparison.OrdinalIgnoreCase))
            {
                if (IsPersistenceRegistry(context.ResourceId))
                {
                    score += 55.0; // Registry persistence mechanism
                }
            }

            // Check for credential access patterns
            if (context.ResourceId.Contains("sam", StringComparison.OrdinalIgnoreCase) ||
                context.ResourceId.Contains("lsass", StringComparison.OrdinalIgnoreCase) ||
                context.ResourceId.Contains("credential", StringComparison.OrdinalIgnoreCase))
            {
                score += 60.0; // Credential dumping attempt
            }

            // Check for rapid file changes (ransomware indicator)
            var recentFileChanges = profile.FileModificationTimestamps
                .Where(t => (DateTime.UtcNow - t).TotalMinutes < 2)
                .Count();

            if (recentFileChanges > 100)
            {
                score += 80.0; // Potential ransomware
            }

            // Check for unusual execution paths
            if (context.SubjectAttributes.TryGetValue("ProcessPath", out var pathObj) &&
                pathObj is string processPath)
            {
                if (IsUnusualExecutionPath(processPath))
                {
                    score += 35.0;
                }
            }

            await Task.CompletedTask;
            return Math.Min(score, 100.0);
        }

        private void UpdateProfile(EndpointProfile profile, AccessContext context)
        {
            profile.TotalAccesses++;
            profile.LastSeen = DateTime.UtcNow;

            var action = context.Action.ToLowerInvariant();
            if (action == "modify" || action == "write" || action == "delete")
            {
                profile.FileModificationTimestamps.Add(DateTime.UtcNow);
                if (profile.FileModificationTimestamps.Count > 500)
                {
                    profile.FileModificationTimestamps.RemoveAt(0);
                }
            }

            profile.AccessedResources.Add(context.ResourceId);
            if (profile.AccessedResources.Count > 100)
            {
                profile.AccessedResources = profile.AccessedResources.Take(100).ToHashSet();
            }
        }

        private bool IsLolBin(string processName)
        {
            var lolBins = new[] { "powershell", "cmd", "wscript", "cscript", "mshta", "rundll32",
                                  "regsvr32", "certutil", "bitsadmin", "wmic", "sc", "net" };

            return lolBins.Any(lol => processName.Contains(lol, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsCriticalSystemFile(string resourceId)
        {
            var lowerResource = resourceId.ToLowerInvariant();

            return lowerResource.Contains("system32") ||
                   lowerResource.Contains("windows/system") ||
                   lowerResource.Contains("/etc/passwd") ||
                   lowerResource.Contains("/etc/shadow") ||
                   lowerResource.Contains("boot") ||
                   lowerResource.Contains("kernel");
        }

        private bool IsPersistenceRegistry(string resourceId)
        {
            var lowerResource = resourceId.ToLowerInvariant();

            return lowerResource.Contains("run") ||
                   lowerResource.Contains("runonce") ||
                   lowerResource.Contains("startup") ||
                   lowerResource.Contains("services") ||
                   lowerResource.Contains("currentversion\\windows\\run");
        }

        private bool IsUnusualExecutionPath(string processPath)
        {
            var lowerPath = processPath.ToLowerInvariant();

            // Check for execution from temp directories
            if (lowerPath.Contains("\\temp\\") ||
                lowerPath.Contains("\\tmp\\") ||
                lowerPath.Contains("appdata\\local\\temp") ||
                lowerPath.Contains("downloads"))
                return true;

            // Check for execution from user directories
            if (lowerPath.Contains("\\users\\") && !lowerPath.Contains("program files"))
                return true;

            return false;
        }

        private string DetermineThreatType(AccessContext context, EndpointProfile profile)
        {
            if (context.SubjectAttributes.TryGetValue("ProcessName", out var processObj) &&
                processObj is string processName)
            {
                if (_suspiciousProcessNames.Any(s => processName.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    return "attack-tool";
            }

            if (context.ResourceId.Contains("lsass", StringComparison.OrdinalIgnoreCase) ||
                context.ResourceId.Contains("sam", StringComparison.OrdinalIgnoreCase))
                return "credential-dumping";

            var recentFileChanges = profile.FileModificationTimestamps
                .Where(t => (DateTime.UtcNow - t).TotalMinutes < 2)
                .Count();

            if (recentFileChanges > 100)
                return "ransomware";

            if (IsPersistenceRegistry(context.ResourceId))
                return "persistence-attempt";

            return "endpoint-anomaly";
        }

        private string BuildThreatDetails(AccessContext context, EndpointProfile profile)
        {
            var details = new List<string>();

            if (context.SubjectAttributes.TryGetValue("ProcessName", out var processObj))
                details.Add($"Process: {processObj}");

            details.Add($"Action: {context.Action}");
            details.Add($"Resource: {context.ResourceId}");

            return string.Join(", ", details);
        }

        /// <summary>
        /// Gets endpoint profile.
        /// </summary>
        public EndpointProfile? GetProfile(string machineName)
        {
            return _endpointProfiles.TryGetValue(machineName, out var profile) ? profile : null;
        }

        /// <summary>
        /// Gets recent endpoint threats.
        /// </summary>
        public IReadOnlyCollection<EndpointThreat> GetRecentThreats(int count = 100)
        {
            return _threats.Take(count).ToList().AsReadOnly();
        }
    }

    public sealed class EndpointProfile
    {
        public required string MachineName { get; init; }
        public required DateTime FirstSeen { get; init; }
        public DateTime LastSeen { get; set; }
        public int TotalAccesses { get; set; }
        public HashSet<string> ObservedProcesses { get; init; } = new();
        public HashSet<string> AccessedResources { get; set; } = new();
        public List<DateTime> FileModificationTimestamps { get; init; } = new();
    }

    public sealed class EndpointThreat
    {
        public required string Id { get; init; }
        public required string MachineName { get; init; }
        public required string SubjectId { get; init; }
        public required DateTime DetectedAt { get; init; }
        public required double ThreatScore { get; init; }
        public required string ThreatType { get; init; }
        public required string Details { get; init; }
    }
}
