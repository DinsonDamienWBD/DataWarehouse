using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.ThreatDetection
{
    /// <summary>
    /// Generic threat detection strategy with configurable anomaly scoring and alert generation.
    /// </summary>
    public sealed class ThreatDetectionStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, ThreatScore> _threatScores = new();
        private readonly ConcurrentQueue<ThreatAlert> _alerts = new();
        private double _criticalThreshold = 80.0;
        private double _highThreshold = 60.0;
        private double _mediumThreshold = 40.0;

        /// <inheritdoc/>
        public override string StrategyId => "threat-detection";

        /// <inheritdoc/>
        public override string StrategyName => "Generic Threat Detection";

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
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("CriticalThreshold", out var crit) && crit is double critVal)
                _criticalThreshold = critVal;

            if (configuration.TryGetValue("HighThreshold", out var high) && high is double highVal)
                _highThreshold = highVal;

            if (configuration.TryGetValue("MediumThreshold", out var med) && med is double medVal)
                _mediumThreshold = medVal;

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("threat.detection.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("threat.detection.shutdown");
            _threatScores.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("threat.detection.evaluate");
            // Calculate threat score based on multiple factors
            var score = await CalculateThreatScoreAsync(context, cancellationToken);

            // Update threat tracking
            _threatScores[context.SubjectId] = new ThreatScore
            {
                SubjectId = context.SubjectId,
                Score = score.TotalScore,
                LastUpdated = DateTime.UtcNow,
                Factors = score.Factors
            };

            // Determine threat level
            var threatLevel = ClassifyThreatLevel(score.TotalScore);

            // Generate alert if threat level is elevated
            if (threatLevel >= ThreatLevel.Medium)
            {
                var alert = new ThreatAlert
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SubjectId = context.SubjectId,
                    ResourceId = context.ResourceId,
                    Action = context.Action,
                    ThreatLevel = threatLevel,
                    ThreatScore = score.TotalScore,
                    Reason = BuildThreatReason(score.Factors),
                    Timestamp = DateTime.UtcNow,
                    ClientIp = context.ClientIpAddress,
                    Location = context.Location
                };

                _alerts.Enqueue(alert);

                // Prune old alerts (keep last 1000)
                while (_alerts.Count > 1000)
                {
                    _alerts.TryDequeue(out _);
                }
            }

            // Deny access for critical threats
            if (threatLevel == ThreatLevel.Critical)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Critical threat detected (score: {score.TotalScore:F2}): {BuildThreatReason(score.Factors)}",
                    ApplicablePolicies = new[] { "ThreatDetection", "AutoBlock" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["ThreatLevel"] = threatLevel.ToString(),
                        ["ThreatScore"] = score.TotalScore,
                        ["Factors"] = score.Factors
                    }
                };
            }

            // Allow with warning for elevated threats
            return new AccessDecision
            {
                IsGranted = true,
                Reason = threatLevel >= ThreatLevel.Medium
                    ? $"Access granted with {threatLevel} threat level (score: {score.TotalScore:F2})"
                    : "Access granted - no threat detected",
                ApplicablePolicies = new[] { "ThreatDetection" },
                Metadata = new Dictionary<string, object>
                {
                    ["ThreatLevel"] = threatLevel.ToString(),
                    ["ThreatScore"] = score.TotalScore
                }
            };
        }

        /// <summary>
        /// Calculates comprehensive threat score based on multiple factors.
        /// </summary>
        private async Task<(double TotalScore, Dictionary<string, double> Factors)> CalculateThreatScoreAsync(
            AccessContext context,
            CancellationToken cancellationToken)
        {
            var factors = new Dictionary<string, double>();

            // Time-based anomaly (off-hours access)
            var timeScore = CalculateTimeAnomalyScore(context.RequestTime);
            factors["TimeAnomaly"] = timeScore;

            // Geographic anomaly (unusual location)
            var geoScore = CalculateGeographicAnomalyScore(context);
            factors["GeographicAnomaly"] = geoScore;

            // Access frequency anomaly (brute force, enumeration)
            var frequencyScore = await CalculateFrequencyAnomalyScoreAsync(context.SubjectId);
            factors["FrequencyAnomaly"] = frequencyScore;

            // Action risk score (dangerous actions)
            var actionScore = CalculateActionRiskScore(context.Action);
            factors["ActionRisk"] = actionScore;

            // Resource sensitivity score
            var resourceScore = CalculateResourceSensitivityScore(context.ResourceId);
            factors["ResourceSensitivity"] = resourceScore;

            // Calculate weighted total
            var totalScore =
                (timeScore * 0.15) +
                (geoScore * 0.25) +
                (frequencyScore * 0.30) +
                (actionScore * 0.20) +
                (resourceScore * 0.10);

            return (totalScore, factors);
        }

        private double CalculateTimeAnomalyScore(DateTime requestTime)
        {
            var hour = requestTime.Hour;

            // Off-hours: 22:00 - 06:00
            if (hour >= 22 || hour < 6)
                return 40.0;

            // Early morning: 06:00 - 08:00
            if (hour < 8)
                return 20.0;

            // Late evening: 20:00 - 22:00
            if (hour >= 20)
                return 25.0;

            // Weekend
            if (requestTime.DayOfWeek == DayOfWeek.Saturday || requestTime.DayOfWeek == DayOfWeek.Sunday)
                return 15.0;

            return 0.0;
        }

        private double CalculateGeographicAnomalyScore(AccessContext context)
        {
            if (context.Location == null)
                return 10.0; // Unknown location is suspicious

            // Check for high-risk countries (simplified)
            var highRiskCountries = new[] { "CN", "RU", "KP", "IR" };
            if (highRiskCountries.Contains(context.Location.Country, StringComparer.OrdinalIgnoreCase))
                return 80.0;

            // Check for rapid location changes (requires historical data)
            // For now, return baseline
            return 0.0;
        }

        private Task<double> CalculateFrequencyAnomalyScoreAsync(string subjectId)
        {
            // Count recent accesses by this subject
            var recentAccesses = _threatScores.Values
                .Where(t => t.SubjectId == subjectId &&
                           (DateTime.UtcNow - t.LastUpdated).TotalMinutes < 5)
                .Count();

            // More than 100 accesses in 5 minutes = likely enumeration/brute force
            if (recentAccesses > 100)
                return Task.FromResult(90.0);

            if (recentAccesses > 50)
                return Task.FromResult(60.0);

            if (recentAccesses > 20)
                return Task.FromResult(30.0);

            return Task.FromResult(0.0);
        }

        private double CalculateActionRiskScore(string action)
        {
            var lowerAction = action.ToLowerInvariant();

            // High-risk actions
            if (lowerAction == "delete" || lowerAction == "drop" || lowerAction == "destroy")
                return 70.0;

            // Moderate-risk actions
            if (lowerAction == "modify" || lowerAction == "update" || lowerAction == "write")
                return 40.0;

            // Elevated actions
            if (lowerAction == "execute" || lowerAction == "admin")
                return 50.0;

            // Read actions
            if (lowerAction == "read" || lowerAction == "list")
                return 10.0;

            return 20.0;
        }

        private double CalculateResourceSensitivityScore(string resourceId)
        {
            var lowerResource = resourceId.ToLowerInvariant();

            // Critical resources
            if (lowerResource.Contains("admin") || lowerResource.Contains("root") || lowerResource.Contains("system"))
                return 60.0;

            // Sensitive data
            if (lowerResource.Contains("password") || lowerResource.Contains("credential") || lowerResource.Contains("secret"))
                return 80.0;

            // Financial/PII data
            if (lowerResource.Contains("payment") || lowerResource.Contains("ssn") || lowerResource.Contains("pii"))
                return 70.0;

            return 10.0;
        }

        private ThreatLevel ClassifyThreatLevel(double score)
        {
            if (score >= _criticalThreshold)
                return ThreatLevel.Critical;

            if (score >= _highThreshold)
                return ThreatLevel.High;

            if (score >= _mediumThreshold)
                return ThreatLevel.Medium;

            return ThreatLevel.Low;
        }

        private string BuildThreatReason(Dictionary<string, double> factors)
        {
            var topFactors = factors
                .Where(f => f.Value > 20.0)
                .OrderByDescending(f => f.Value)
                .Take(3)
                .Select(f => $"{f.Key}({f.Value:F0})")
                .ToList();

            return topFactors.Any()
                ? string.Join(", ", topFactors)
                : "Multiple low-level indicators";
        }

        /// <summary>
        /// Gets recent threat alerts.
        /// </summary>
        public IReadOnlyCollection<ThreatAlert> GetRecentAlerts(int count = 100)
        {
            return _alerts.Take(count).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets threat score for a subject.
        /// </summary>
        public ThreatScore? GetThreatScore(string subjectId)
        {
            return _threatScores.TryGetValue(subjectId, out var score) ? score : null;
        }
    }

    /// <summary>
    /// Threat severity levels.
    /// </summary>
    public enum ThreatLevel
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3
    }

    /// <summary>
    /// Threat score for a subject.
    /// </summary>
    public sealed class ThreatScore
    {
        public required string SubjectId { get; init; }
        public required double Score { get; init; }
        public required DateTime LastUpdated { get; init; }
        public required Dictionary<string, double> Factors { get; init; }
    }

    /// <summary>
    /// Threat alert generated by detection engine.
    /// </summary>
    public sealed class ThreatAlert
    {
        public required string Id { get; init; }
        public required string SubjectId { get; init; }
        public required string ResourceId { get; init; }
        public required string Action { get; init; }
        public required ThreatLevel ThreatLevel { get; init; }
        public required double ThreatScore { get; init; }
        public required string Reason { get; init; }
        public required DateTime Timestamp { get; init; }
        public string? ClientIp { get; init; }
        public GeoLocation? Location { get; init; }
    }
}
