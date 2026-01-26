using System.Net;

namespace DataWarehouse.Plugins.ZeroTrust
{
    /// <summary>
    /// Anomaly detection engine for zero-trust verification.
    /// Analyzes request patterns and behavior to detect potential threats.
    /// </summary>
    internal sealed class AnomalyDetector
    {
        private readonly ZeroTrustConfig _config;

        /// <summary>
        /// Initializes a new instance of the <see cref="AnomalyDetector"/> class.
        /// </summary>
        /// <param name="config">The zero-trust configuration.</param>
        public AnomalyDetector(ZeroTrustConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Analyzes a request context against a behavior profile for anomalies.
        /// </summary>
        /// <param name="context">The request context to analyze.</param>
        /// <param name="profile">The behavior profile to compare against.</param>
        /// <returns>The anomaly analysis result.</returns>
        public AnomalyAnalysisResult Analyze(RequestContext context, BehaviorProfile? profile)
        {
            var result = new AnomalyAnalysisResult();

            if (profile == null || profile.AccessPatterns == null || profile.AccessPatterns.Count < 5)
            {
                // Not enough history to detect anomalies
                return result;
            }

            var anomalies = new List<string>();
            var totalPenalty = 0.0;

            // Check for unusual time of access
            var timeAnomaly = CheckTimeAnomaly(context, profile);
            if (timeAnomaly.isAnomaly)
            {
                anomalies.Add("unusual_access_time");
                totalPenalty += timeAnomaly.penalty;
            }

            // Check for unusual location (IP range)
            var locationAnomaly = CheckLocationAnomaly(context, profile);
            if (locationAnomaly.isAnomaly)
            {
                anomalies.Add("unusual_location");
                totalPenalty += locationAnomaly.penalty;
            }

            // Check for unusual access frequency
            var frequencyAnomaly = CheckFrequencyAnomaly(context, profile);
            if (frequencyAnomaly.isAnomaly)
            {
                anomalies.Add("unusual_frequency");
                totalPenalty += frequencyAnomaly.penalty;
            }

            // Check for unusual resource access pattern
            var resourceAnomaly = CheckResourceAnomaly(context, profile);
            if (resourceAnomaly.isAnomaly)
            {
                anomalies.Add("unusual_resource_access");
                totalPenalty += resourceAnomaly.penalty;
            }

            // Check for impossible travel
            var travelAnomaly = CheckImpossibleTravel(context, profile);
            if (travelAnomaly.isAnomaly)
            {
                anomalies.Add("impossible_travel");
                totalPenalty += travelAnomaly.penalty;
            }

            // Check for device anomaly
            var deviceAnomaly = CheckDeviceAnomaly(context, profile);
            if (deviceAnomaly.isAnomaly)
            {
                anomalies.Add("unusual_device");
                totalPenalty += deviceAnomaly.penalty;
            }

            if (anomalies.Count > 0)
            {
                result.IsAnomalous = true;
                result.AnomalyTypes = anomalies;
                result.SeverityPenalty = Math.Min(totalPenalty, 50); // Cap at 50 points
                result.Details = $"Detected anomalies: {string.Join(", ", anomalies)}";
            }

            return result;
        }

        private (bool isAnomaly, double penalty) CheckTimeAnomaly(RequestContext context, BehaviorProfile profile)
        {
            if (profile.AccessPatterns == null || profile.AccessPatterns.Count == 0)
                return (false, 0);

            var currentHour = DateTime.UtcNow.Hour;
            var recentPatterns = profile.AccessPatterns
                .Where(p => p.Timestamp > DateTime.UtcNow.Subtract(_config.AnomalyDetectionWindow))
                .ToList();

            if (recentPatterns.Count < 5)
                return (false, 0);

            // Calculate typical access hours
            var hourDistribution = recentPatterns
                .GroupBy(p => p.Timestamp.Hour)
                .ToDictionary(g => g.Key, g => g.Count());

            var totalAccesses = recentPatterns.Count;
            var currentHourCount = hourDistribution.GetValueOrDefault(currentHour, 0);

            // If this hour has less than 5% of typical accesses, it's unusual
            var hourProbability = (double)currentHourCount / totalAccesses;
            if (hourProbability < 0.05 && _config.AnomalyDetectionSensitivity > 0.5)
            {
                return (true, 10 * _config.AnomalyDetectionSensitivity);
            }

            return (false, 0);
        }

        private (bool isAnomaly, double penalty) CheckLocationAnomaly(RequestContext context, BehaviorProfile profile)
        {
            if (string.IsNullOrEmpty(context.SourceIp) || profile.AccessPatterns == null)
                return (false, 0);

            var recentPatterns = profile.AccessPatterns
                .Where(p => p.Timestamp > DateTime.UtcNow.Subtract(_config.AnomalyDetectionWindow))
                .Where(p => !string.IsNullOrEmpty(p.SourceIp))
                .ToList();

            if (recentPatterns.Count < 3)
                return (false, 0);

            var knownIpPrefixes = recentPatterns
                .Select(p => GetIpPrefix(p.SourceIp!))
                .Distinct()
                .ToHashSet();

            var currentPrefix = GetIpPrefix(context.SourceIp);

            if (!knownIpPrefixes.Contains(currentPrefix))
            {
                // New IP range - could be anomalous
                return (true, 15 * _config.AnomalyDetectionSensitivity);
            }

            return (false, 0);
        }

        private (bool isAnomaly, double penalty) CheckFrequencyAnomaly(RequestContext context, BehaviorProfile profile)
        {
            if (profile.AccessPatterns == null)
                return (false, 0);

            var lastHourPatterns = profile.AccessPatterns
                .Where(p => p.Timestamp > DateTime.UtcNow.AddHours(-1))
                .Count();

            // If current rate is more than 3x the average, flag as anomaly
            if (profile.AverageAccessesPerHour > 0 && lastHourPatterns > profile.AverageAccessesPerHour * 3)
            {
                return (true, 20 * _config.AnomalyDetectionSensitivity);
            }

            return (false, 0);
        }

        private (bool isAnomaly, double penalty) CheckResourceAnomaly(RequestContext context, BehaviorProfile profile)
        {
            if (string.IsNullOrEmpty(context.ResourcePath) || profile.AccessPatterns == null)
                return (false, 0);

            var recentPatterns = profile.AccessPatterns
                .Where(p => p.Timestamp > DateTime.UtcNow.Subtract(_config.AnomalyDetectionWindow))
                .Where(p => !string.IsNullOrEmpty(p.ResourcePath))
                .ToList();

            if (recentPatterns.Count < 10)
                return (false, 0);

            var knownResourcePaths = recentPatterns
                .Select(p => GetResourceRoot(p.ResourcePath!))
                .Distinct()
                .ToHashSet();

            var currentRoot = GetResourceRoot(context.ResourcePath);

            if (!knownResourcePaths.Contains(currentRoot))
            {
                // Accessing a new resource area
                return (true, 10 * _config.AnomalyDetectionSensitivity);
            }

            return (false, 0);
        }

        private (bool isAnomaly, double penalty) CheckImpossibleTravel(RequestContext context, BehaviorProfile profile)
        {
            if (string.IsNullOrEmpty(context.SourceIp) || profile.AccessPatterns == null)
                return (false, 0);

            var lastAccess = profile.AccessPatterns
                .Where(p => !string.IsNullOrEmpty(p.SourceIp))
                .OrderByDescending(p => p.Timestamp)
                .FirstOrDefault();

            if (lastAccess == null)
                return (false, 0);

            var timeSinceLastAccess = DateTime.UtcNow - lastAccess.Timestamp;

            // If different country-level IP in less than 1 hour, flag as impossible travel
            if (timeSinceLastAccess.TotalHours < 1)
            {
                var lastCountry = GetCountryFromIp(lastAccess.SourceIp!);
                var currentCountry = GetCountryFromIp(context.SourceIp);

                if (lastCountry != currentCountry && lastCountry != "unknown" && currentCountry != "unknown")
                {
                    return (true, 30 * _config.AnomalyDetectionSensitivity);
                }
            }

            return (false, 0);
        }

        private (bool isAnomaly, double penalty) CheckDeviceAnomaly(RequestContext context, BehaviorProfile profile)
        {
            if (string.IsNullOrEmpty(context.DeviceId) || profile.AccessPatterns == null)
                return (false, 0);

            var recentDevices = profile.AccessPatterns
                .Where(p => p.Timestamp > DateTime.UtcNow.Subtract(_config.AnomalyDetectionWindow))
                .Where(p => !string.IsNullOrEmpty(p.DeviceId))
                .Select(p => p.DeviceId)
                .Distinct()
                .ToHashSet();

            if (recentDevices.Count > 0 && !recentDevices.Contains(context.DeviceId))
            {
                // New device
                return (true, 15 * _config.AnomalyDetectionSensitivity);
            }

            return (false, 0);
        }

        private static string GetIpPrefix(string ip)
        {
            // Get the first two octets for IPv4 or first 4 groups for IPv6
            if (IPAddress.TryParse(ip, out var address))
            {
                var bytes = address.GetAddressBytes();
                if (bytes.Length == 4)
                {
                    return $"{bytes[0]}.{bytes[1]}";
                }
                else if (bytes.Length == 16)
                {
                    return $"{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}";
                }
            }
            return ip;
        }

        private static string GetResourceRoot(string resourcePath)
        {
            var parts = resourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : resourcePath;
        }

        private static string GetCountryFromIp(string ip)
        {
            // In a real implementation, this would use a GeoIP database
            // For now, we use IP ranges as a simple approximation

            if (!IPAddress.TryParse(ip, out var address))
                return "unknown";

            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
                return "unknown";

            // Very simplified country detection based on first octet
            // In production, use MaxMind or similar
            return bytes[0] switch
            {
                >= 1 and <= 126 => "region_a",
                >= 128 and <= 191 => "region_b",
                >= 192 and <= 223 => "region_c",
                _ => "unknown"
            };
        }
    }

    /// <summary>
    /// Trust score calculator for zero-trust verification.
    /// Computes a composite trust score based on multiple factors.
    /// </summary>
    internal sealed class TrustScoreCalculator
    {
        private readonly ZeroTrustConfig _config;

        /// <summary>
        /// Initializes a new instance of the <see cref="TrustScoreCalculator"/> class.
        /// </summary>
        /// <param name="config">The zero-trust configuration.</param>
        public TrustScoreCalculator(ZeroTrustConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Calculates the trust score based on multiple factors.
        /// </summary>
        /// <param name="context">The request context.</param>
        /// <param name="identityResult">The identity verification result.</param>
        /// <param name="deviceResult">The device verification result.</param>
        /// <param name="anomalyResult">The anomaly analysis result.</param>
        /// <returns>The calculated trust score (0-100).</returns>
        public double Calculate(
            RequestContext context,
            IdentityVerificationResult identityResult,
            DeviceVerificationResult? deviceResult,
            AnomalyAnalysisResult anomalyResult)
        {
            var score = 0.0;

            // Identity factor
            if (identityResult.IsValid)
            {
                var identityScore = identityResult.TrustScore * _config.IdentityVerificationWeight;
                score += identityScore;

                // Bonus for strong authentication
                if (!string.IsNullOrEmpty(context.AuthenticationMethod))
                {
                    var authBonus = context.AuthenticationMethod switch
                    {
                        "mfa" or "multi_factor" => 10,
                        "certificate" or "mtls" => 8,
                        "oauth2" or "oidc" => 5,
                        _ => 0
                    };
                    score += authBonus;
                }
            }

            // Device factor
            if (deviceResult?.IsCompliant == true)
            {
                var deviceScore = deviceResult.TrustScore * _config.DeviceComplianceWeight;
                score += deviceScore;
            }
            else if (deviceResult == null)
            {
                // No device verification - use partial weight
                score += _config.DefaultTrustScore * _config.DeviceComplianceWeight * 0.5;
            }

            // Behavior factor (absence of anomalies)
            if (!anomalyResult.IsAnomalous)
            {
                score += _config.DefaultTrustScore * _config.BehaviorWeight;
            }
            else
            {
                // Reduce score based on anomaly severity
                score -= anomalyResult.SeverityPenalty;
            }

            // Session factor
            if (!string.IsNullOrEmpty(context.SessionId))
            {
                score += _config.DefaultTrustScore * _config.SessionWeight;
            }

            // Context adjustments
            score = ApplyContextAdjustments(context, score);

            // Normalize to 0-100 range
            return Math.Clamp(score, 0, 100);
        }

        private double ApplyContextAdjustments(RequestContext context, double baseScore)
        {
            var score = baseScore;

            // Time-based adjustments
            var hour = DateTime.UtcNow.Hour;
            if (hour >= 22 || hour < 6)
            {
                // After-hours access - slight reduction
                score *= 0.95;
            }

            // Weekend adjustment
            var dayOfWeek = DateTime.UtcNow.DayOfWeek;
            if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
            {
                score *= 0.97;
            }

            // Resource sensitivity adjustment
            if (context.ResourceSensitivity.HasValue)
            {
                var sensitivityMultiplier = context.ResourceSensitivity.Value switch
                {
                    SensitivityLevel.Restricted => 0.9,
                    SensitivityLevel.TopSecret => 0.85,
                    _ => 1.0
                };
                score *= sensitivityMultiplier;
            }

            return score;
        }
    }
}
