using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Advanced
{
    /// <summary>
    /// Implements continuous behavioral authentication using keystroke dynamics and mouse movement patterns.
    /// Non-intrusive re-authentication based on behavioral deviation scoring.
    /// </summary>
    public sealed class BehavioralBiometricStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, BehavioralProfile> _profiles = new();

        public BehavioralBiometricStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public override string StrategyId => "behavioral-biometric";
        public override string StrategyName => "Behavioral Biometric Strategy";

        public override AccessControlCapabilities Capabilities => new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 1000
        };

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();

            var profile = _profiles.GetOrAdd(context.SubjectId, _ => new BehavioralProfile { SubjectId = context.SubjectId });

            // Extract behavioral metrics
            var typingSpeed = context.SubjectAttributes.TryGetValue("typing_speed_wpm", out var speed) && speed is double s ? s : 60.0;
            var mouseDistance = context.SubjectAttributes.TryGetValue("mouse_distance_px", out var dist) && dist is double d ? d : 1000.0;

            // Calculate deviation
            var deviation = CalculateDeviation(profile, typingSpeed, mouseDistance);

            // Update profile
            UpdateProfile(profile, typingSpeed, mouseDistance);

            var riskThreshold = Configuration.TryGetValue("risk_threshold", out var threshold) && threshold is double t ? t : 0.7;
            var isGranted = deviation < riskThreshold;

            return new AccessDecision
            {
                IsGranted = isGranted,
                Reason = isGranted
                    ? $"Behavioral biometric authentication successful (deviation: {deviation:F2})"
                    : $"Behavioral biometric authentication failed (deviation: {deviation:F2} exceeds threshold)",
                ApplicablePolicies = new[] { "behavioral-biometric-policy" },
                Metadata = new Dictionary<string, object>
                {
                    ["deviation_score"] = deviation,
                    ["risk_threshold"] = riskThreshold,
                    ["sample_count"] = profile.SampleCount
                }
            };
        }

        private double CalculateDeviation(BehavioralProfile profile, double typingSpeed, double mouseDistance)
        {
            if (profile.SampleCount == 0) return 0.0;

            var typingDev = Math.Abs(typingSpeed - profile.AvgTypingSpeed) / (profile.StdDevTypingSpeed + 1.0);
            var mouseDev = Math.Abs(mouseDistance - profile.AvgMouseDistance) / (profile.StdDevMouseDistance + 1.0);

            return Math.Min((typingDev + mouseDev) / 2.0, 1.0);
        }

        private void UpdateProfile(BehavioralProfile profile, double typingSpeed, double mouseDistance)
        {
            const double alpha = 0.2;

            if (profile.SampleCount == 0)
            {
                profile.AvgTypingSpeed = typingSpeed;
                profile.AvgMouseDistance = mouseDistance;
                profile.StdDevTypingSpeed = 10.0;
                profile.StdDevMouseDistance = 200.0;
            }
            else
            {
                profile.AvgTypingSpeed = alpha * typingSpeed + (1 - alpha) * profile.AvgTypingSpeed;
                profile.AvgMouseDistance = alpha * mouseDistance + (1 - alpha) * profile.AvgMouseDistance;

                var typingDiff = Math.Abs(typingSpeed - profile.AvgTypingSpeed);
                profile.StdDevTypingSpeed = alpha * typingDiff + (1 - alpha) * profile.StdDevTypingSpeed;
            }

            profile.SampleCount++;
        }

        private sealed class BehavioralProfile
        {
            public required string SubjectId { get; init; }
            public int SampleCount { get; set; }
            public double AvgTypingSpeed { get; set; }
            public double StdDevTypingSpeed { get; set; }
            public double AvgMouseDistance { get; set; }
            public double StdDevMouseDistance { get; set; }
        }
    }
}
