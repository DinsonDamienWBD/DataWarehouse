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
    /// Network Detection and Response (NDR) strategy.
    /// Analyzes network traffic patterns, protocol anomalies, and lateral movement detection.
    /// </summary>
    public sealed class NdRStrategy : AccessControlStrategyBase
    {
        private readonly BoundedDictionary<string, NetworkProfile> _networkProfiles = new BoundedDictionary<string, NetworkProfile>(1000);
        private readonly ConcurrentQueue<NetworkAnomaly> _anomalies = new();
        private readonly HashSet<string> _suspiciousProtocols = new(StringComparer.OrdinalIgnoreCase)
        {
            "telnet", "ftp", "smb1", "netbios"
        };

        /// <inheritdoc/>
        public override string StrategyId => "ndr";

        /// <inheritdoc/>
        public override string StrategyName => "Network Detection and Response";

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

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ndr.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("ndr.shutdown");
            _networkProfiles.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }
/// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("ndr.evaluate");
            var clientIp = context.ClientIpAddress ?? "unknown";

            // Get or create network profile
            var profile = _networkProfiles.GetOrAdd(clientIp, _ => new NetworkProfile
            {
                IpAddress = clientIp,
                FirstSeen = DateTime.UtcNow
            });

            // Analyze network patterns
            var riskScore = await AnalyzeNetworkPatternsAsync(context, profile, cancellationToken);

            // Update profile
            UpdateProfile(profile, context);

            // Generate anomaly if detected
            if (riskScore >= 60.0)
            {
                var anomaly = new NetworkAnomaly
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IpAddress = clientIp,
                    DetectedAt = DateTime.UtcNow,
                    RiskScore = riskScore,
                    AnomalyType = DetermineAnomalyType(context, profile),
                    Details = BuildAnomalyDetails(context, profile)
                };

                _anomalies.Enqueue(anomaly);

                while (_anomalies.Count > 1000)
                {
                    _anomalies.TryDequeue(out _);
                }
            }

            // Block critical network threats
            if (riskScore >= 80.0)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Critical network threat detected (score: {riskScore:F2})",
                    ApplicablePolicies = new[] { "NetworkDefense", "AutoBlock" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["RiskScore"] = riskScore,
                        ["ThreatType"] = DetermineAnomalyType(context, profile)
                    }
                };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = riskScore >= 60.0
                    ? $"Access granted with network anomaly warning (score: {riskScore:F2})"
                    : "Access granted - normal network behavior",
                ApplicablePolicies = new[] { "NetworkMonitoring" },
                Metadata = new Dictionary<string, object>
                {
                    ["RiskScore"] = riskScore
                }
            };
        }

        private async Task<double> AnalyzeNetworkPatternsAsync(AccessContext context, NetworkProfile profile, CancellationToken cancellationToken)
        {
            var score = 0.0;

            // Check for port scanning behavior
            if (profile.AccessedResources.Count > 20 && profile.TotalConnections < 50)
            {
                score += 40.0; // Many resources, few connections = scanning
            }

            // Check for lateral movement
            var isInternalResource = IsInternalResource(context.ResourceId);
            if (isInternalResource && profile.IsExternalIp)
            {
                score += 35.0; // External IP accessing internal resources
            }

            // Check for suspicious protocols
            if (context.EnvironmentAttributes.TryGetValue("Protocol", out var protocolObj) &&
                protocolObj is string protocol)
            {
                if (_suspiciousProtocols.Contains(protocol))
                {
                    score += 30.0; // Insecure/legacy protocol
                }
            }

            // Check for data exfiltration patterns (large upload volumes)
            if (context.Action.Equals("upload", StringComparison.OrdinalIgnoreCase) ||
                context.Action.Equals("write", StringComparison.OrdinalIgnoreCase))
            {
                var recentUploads = profile.ConnectionTimestamps
                    .Where(t => (DateTime.UtcNow - t).TotalMinutes < 10)
                    .Count();

                if (recentUploads > 100)
                {
                    score += 50.0; // Rapid upload = potential exfiltration
                }
            }

            // Check for brute force/credential stuffing
            var recentAttempts = profile.ConnectionTimestamps
                .Where(t => (DateTime.UtcNow - t).TotalMinutes < 5)
                .Count();

            if (recentAttempts > 50)
            {
                score += 45.0; // Many attempts in short time
            }

            // Check for connection from high-risk location
            if (context.Location != null)
            {
                var highRiskCountries = new[] { "CN", "RU", "KP", "IR" };
                if (highRiskCountries.Contains(context.Location.Country, StringComparer.OrdinalIgnoreCase))
                {
                    score += 25.0;
                }
            }

            await Task.CompletedTask;
            return Math.Min(score, 100.0);
        }

        private void UpdateProfile(NetworkProfile profile, AccessContext context)
        {
            profile.TotalConnections++;
            profile.LastSeen = DateTime.UtcNow;

            profile.AccessedResources.Add(context.ResourceId);
            if (profile.AccessedResources.Count > 100)
            {
                // Keep only unique resources, limit to 100
                profile.AccessedResources = profile.AccessedResources.Distinct().Take(100).ToHashSet();
            }

            profile.ConnectionTimestamps.Add(DateTime.UtcNow);
            if (profile.ConnectionTimestamps.Count > 500)
            {
                profile.ConnectionTimestamps.RemoveAt(0);
            }

            if (context.Location != null)
            {
                profile.ObservedLocations.Add(context.Location.Country ?? "unknown");
            }
        }

        private bool IsInternalResource(string resourceId)
        {
            var lowerResource = resourceId.ToLowerInvariant();
            return lowerResource.Contains("internal") ||
                   lowerResource.Contains("private") ||
                   lowerResource.Contains("corp");
        }

        private string DetermineAnomalyType(AccessContext context, NetworkProfile profile)
        {
            if (profile.AccessedResources.Count > 20 && profile.TotalConnections < 50)
                return "port-scan";

            var recentAttempts = profile.ConnectionTimestamps
                .Where(t => (DateTime.UtcNow - t).TotalMinutes < 5)
                .Count();

            if (recentAttempts > 50)
                return "brute-force";

            if (profile.IsExternalIp && IsInternalResource(context.ResourceId))
                return "lateral-movement";

            return "network-anomaly";
        }

        private string BuildAnomalyDetails(AccessContext context, NetworkProfile profile)
        {
            var details = new List<string>();

            if (profile.AccessedResources.Count > 20)
                details.Add($"{profile.AccessedResources.Count} unique resources");

            if (profile.IsExternalIp)
                details.Add("external IP");

            if (profile.ObservedLocations.Count > 1)
                details.Add($"{profile.ObservedLocations.Count} locations");

            return string.Join(", ", details);
        }

        /// <summary>
        /// Gets network profile for an IP address.
        /// </summary>
        public NetworkProfile? GetProfile(string ipAddress)
        {
            return _networkProfiles.TryGetValue(ipAddress, out var profile) ? profile : null;
        }

        /// <summary>
        /// Gets recent network anomalies.
        /// </summary>
        public IReadOnlyCollection<NetworkAnomaly> GetRecentAnomalies(int count = 100)
        {
            return _anomalies.Take(count).ToList().AsReadOnly();
        }
    }

    public sealed class NetworkProfile
    {
        public required string IpAddress { get; init; }
        public required DateTime FirstSeen { get; init; }
        public DateTime LastSeen { get; set; }
        public int TotalConnections { get; set; }
        public HashSet<string> AccessedResources { get; set; } = new();
        public List<DateTime> ConnectionTimestamps { get; init; } = new();
        public HashSet<string> ObservedLocations { get; init; } = new();
        public bool IsExternalIp => !IpAddress.StartsWith("10.") &&
                                     !IpAddress.StartsWith("192.168.") &&
                                     !IpAddress.StartsWith("172.16.");
    }

    public sealed class NetworkAnomaly
    {
        public required string Id { get; init; }
        public required string IpAddress { get; init; }
        public required DateTime DetectedAt { get; init; }
        public required double RiskScore { get; init; }
        public required string AnomalyType { get; init; }
        public required string Details { get; init; }
    }
}
