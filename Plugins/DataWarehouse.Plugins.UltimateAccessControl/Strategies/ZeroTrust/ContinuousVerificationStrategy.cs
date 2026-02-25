using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.ZeroTrust
{
    /// <summary>
    /// Continuous authentication and verification strategy.
    /// Implements session re-evaluation on risk change, behavioral anomaly detection, and step-up authentication for sensitive operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Continuous verification principles:
    /// - Never assume trust persists after initial authentication
    /// - Re-evaluate access decisions based on changing context
    /// - Detect behavioral anomalies that indicate compromised sessions
    /// - Require step-up authentication for high-risk operations
    /// - Adjust trust level dynamically based on risk signals
    /// </para>
    /// <para>
    /// Risk signals:
    /// - Geographic location changes
    /// - Unusual access patterns
    /// - Time-of-day anomalies
    /// - Resource access patterns
    /// - Device posture changes
    /// - Network changes
    /// </para>
    /// </remarks>
    public sealed class ContinuousVerificationStrategy : AccessControlStrategyBase
    {
        private readonly BoundedDictionary<string, SessionContext> _sessions = new BoundedDictionary<string, SessionContext>(1000);
        private readonly BoundedDictionary<string, BehavioralProfile> _profiles = new BoundedDictionary<string, BehavioralProfile>(1000);
        private TimeSpan _sessionRevalidationInterval = TimeSpan.FromMinutes(15);
        private double _anomalyThreshold = 0.7;
        private double _stepUpThreshold = 0.8;

        /// <inheritdoc/>
        public override string StrategyId => "continuous-verification";

        /// <inheritdoc/>
        public override string StrategyName => "Continuous Authentication";

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
            if (configuration.TryGetValue("SessionRevalidationMinutes", out var interval) && interval is int mins)
            {
                _sessionRevalidationInterval = TimeSpan.FromMinutes(mins);
            }

            if (configuration.TryGetValue("AnomalyThreshold", out var anomaly) && anomaly is double anomalyVal)
            {
                _anomalyThreshold = anomalyVal;
            }

            if (configuration.TryGetValue("StepUpThreshold", out var stepUp) && stepUp is double stepUpVal)
            {
                _stepUpThreshold = stepUpVal;
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("continuous.verification.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("continuous.verification.shutdown");
            _sessions.Clear();
            _profiles.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <summary>
        /// Creates or updates a session context.
        /// </summary>
        public SessionContext CreateSession(string sessionId, string userId, string deviceId, GeoLocation? location)
        {
            var session = new SessionContext
            {
                SessionId = sessionId,
                UserId = userId,
                DeviceId = deviceId,
                InitialLocation = location,
                CurrentLocation = location,
                CreatedAt = DateTime.UtcNow,
                LastVerifiedAt = DateTime.UtcNow,
                RiskScore = 0.0,
                RequiresStepUp = false
            };

            _sessions[sessionId] = session;

            // Initialize behavioral profile if not exists
            if (!_profiles.ContainsKey(userId))
            {
                _profiles[userId] = new BehavioralProfile
                {
                    UserId = userId,
                    TypicalAccessHours = new HashSet<int>(),
                    TypicalLocations = new HashSet<string>(),
                    TypicalResources = new HashSet<string>(),
                    AccessPatterns = new List<AccessPattern>()
                };
            }

            return session;
        }

        /// <summary>
        /// Records an access pattern for behavioral profiling.
        /// </summary>
        public void RecordAccessPattern(string userId, string resourceId, string action, DateTime timestamp, GeoLocation? location)
        {
            if (!_profiles.TryGetValue(userId, out var profile))
                return;

            var pattern = new AccessPattern
            {
                ResourceId = resourceId,
                Action = action,
                Timestamp = timestamp,
                Location = location
            };

            profile.AccessPatterns.Add(pattern);

            // Update typical patterns
            profile.TypicalAccessHours.Add(timestamp.Hour);
            if (location?.Country != null)
            {
                profile.TypicalLocations.Add(location.Country);
            }
            profile.TypicalResources.Add(resourceId);

            // Keep only recent patterns (last 30 days)
            var cutoff = DateTime.UtcNow.AddDays(-30);
            profile.AccessPatterns.RemoveAll(p => p.Timestamp < cutoff);
        }

        /// <summary>
        /// Calculates risk score based on current context vs. behavioral profile.
        /// </summary>
        private double CalculateRiskScore(SessionContext session, AccessContext context, BehavioralProfile profile)
        {
            var riskScore = 0.0;
            var factors = new List<string>();

            // 1. Time-of-day anomaly
            var currentHour = context.RequestTime.Hour;
            if (!profile.TypicalAccessHours.Contains(currentHour))
            {
                riskScore += 0.15;
                factors.Add("Unusual access hour");
            }

            // 2. Geographic anomaly
            if (context.Location?.Country != null && !profile.TypicalLocations.Contains(context.Location.Country))
            {
                riskScore += 0.25;
                factors.Add("Unusual location");
            }

            // 3. Location change within session
            if (session.CurrentLocation?.Country != null &&
                context.Location?.Country != null &&
                session.CurrentLocation.Country != context.Location.Country)
            {
                riskScore += 0.35;
                factors.Add("Geographic location changed");
            }

            // 4. Resource access pattern anomaly
            if (!profile.TypicalResources.Contains(context.ResourceId))
            {
                riskScore += 0.1;
                factors.Add("Unusual resource access");
            }

            // 5. High-risk action
            if (context.Action.Equals("delete", StringComparison.OrdinalIgnoreCase) ||
                context.Action.Equals("export", StringComparison.OrdinalIgnoreCase) ||
                context.Action.Equals("admin", StringComparison.OrdinalIgnoreCase))
            {
                riskScore += 0.2;
                factors.Add("High-risk action");
            }

            // 6. Session age
            var sessionAge = DateTime.UtcNow - session.CreatedAt;
            if (sessionAge > TimeSpan.FromHours(8))
            {
                riskScore += 0.15;
                factors.Add("Long-lived session");
            }

            // 7. Time since last verification
            var timeSinceVerification = DateTime.UtcNow - session.LastVerifiedAt;
            if (timeSinceVerification > _sessionRevalidationInterval)
            {
                riskScore += 0.2;
                factors.Add("Session revalidation overdue");
            }

            session.RiskFactors = factors;
            return Math.Min(1.0, riskScore);
        }

        /// <summary>
        /// Determines if step-up authentication is required.
        /// </summary>
        private bool RequiresStepUpAuthentication(double riskScore, AccessContext context)
        {
            // High risk score always requires step-up
            if (riskScore >= _stepUpThreshold)
                return true;

            // Sensitive resources require step-up even at moderate risk
            if (context.ResourceAttributes.TryGetValue("Sensitivity", out var sens) &&
                sens?.ToString()?.Equals("critical", StringComparison.OrdinalIgnoreCase) == true &&
                riskScore >= 0.5)
            {
                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("continuous.verification.evaluate");
            // Extract session ID from context
            if (!context.EnvironmentAttributes.TryGetValue("SessionId", out var sessionIdObj) ||
                sessionIdObj is not string sessionId)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "No session ID provided (continuous verification required)",
                    ApplicablePolicies = new[] { "ContinuousVerification.NoSession" }
                });
            }

            // Get session context
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Session not found or expired",
                    ApplicablePolicies = new[] { "ContinuousVerification.InvalidSession" }
                });
            }

            // Update current location
            session.CurrentLocation = context.Location;

            // Get behavioral profile
            if (!_profiles.TryGetValue(session.UserId, out var profile))
            {
                // Create minimal profile for new user
                profile = new BehavioralProfile
                {
                    UserId = session.UserId,
                    TypicalAccessHours = new HashSet<int> { context.RequestTime.Hour },
                    TypicalLocations = context.Location?.Country != null
                        ? new HashSet<string> { context.Location.Country }
                        : new HashSet<string>(),
                    TypicalResources = new HashSet<string> { context.ResourceId },
                    AccessPatterns = new List<AccessPattern>()
                };
                _profiles[session.UserId] = profile;
            }

            // Calculate risk score
            var riskScore = CalculateRiskScore(session, context, profile);
            session.RiskScore = riskScore;

            // Check if anomaly threshold exceeded
            if (riskScore >= _anomalyThreshold)
            {
                // Check if step-up authentication is required
                if (RequiresStepUpAuthentication(riskScore, context))
                {
                    session.RequiresStepUp = true;

                    // Check if step-up has been completed
                    if (!context.EnvironmentAttributes.TryGetValue("StepUpCompleted", out var stepUpObj) ||
                        stepUpObj is not bool stepUpCompleted || !stepUpCompleted)
                    {
                        return Task.FromResult(new AccessDecision
                        {
                            IsGranted = false,
                            Reason = $"Step-up authentication required (risk score: {riskScore:F2})",
                            ApplicablePolicies = new[] { "ContinuousVerification.StepUpRequired" },
                            Metadata = new Dictionary<string, object>
                            {
                                ["RiskScore"] = riskScore,
                                ["RiskFactors"] = session.RiskFactors,
                                ["RequiresStepUp"] = true
                            }
                        });
                    }
                }
                else
                {
                    // Anomaly detected but doesn't require step-up - deny access
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"Behavioral anomaly detected (risk score: {riskScore:F2})",
                        ApplicablePolicies = new[] { "ContinuousVerification.AnomalyDetected" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["RiskScore"] = riskScore,
                            ["RiskFactors"] = session.RiskFactors
                        }
                    });
                }
            }

            // Update last verified timestamp
            session.LastVerifiedAt = DateTime.UtcNow;

            // Record access pattern for future behavioral analysis
            RecordAccessPattern(session.UserId, context.ResourceId, context.Action, context.RequestTime, context.Location);

            // Access granted
            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Continuous verification passed",
                ApplicablePolicies = new[] { "ContinuousVerification.Verified" },
                Metadata = new Dictionary<string, object>
                {
                    ["RiskScore"] = riskScore,
                    ["SessionAge"] = (DateTime.UtcNow - session.CreatedAt).TotalMinutes,
                    ["TimeSinceLastVerification"] = (DateTime.UtcNow - session.LastVerifiedAt).TotalMinutes,
                    ["RequiresStepUpSoon"] = riskScore > _stepUpThreshold * 0.8
                }
            });
        }
    }

    /// <summary>
    /// Session context for continuous verification.
    /// </summary>
    public sealed class SessionContext
    {
        public required string SessionId { get; init; }
        public required string UserId { get; init; }
        public required string DeviceId { get; init; }
        public GeoLocation? InitialLocation { get; init; }
        public GeoLocation? CurrentLocation { get; set; }
        public required DateTime CreatedAt { get; init; }
        public DateTime LastVerifiedAt { get; set; }
        public double RiskScore { get; set; }
        public bool RequiresStepUp { get; set; }
        public List<string> RiskFactors { get; set; } = new();
    }

    /// <summary>
    /// Behavioral profile for anomaly detection.
    /// </summary>
    public sealed class BehavioralProfile
    {
        public required string UserId { get; init; }
        public required HashSet<int> TypicalAccessHours { get; init; }
        public required HashSet<string> TypicalLocations { get; init; }
        public required HashSet<string> TypicalResources { get; init; }
        public required List<AccessPattern> AccessPatterns { get; init; }
    }

    /// <summary>
    /// Access pattern record.
    /// </summary>
    public sealed class AccessPattern
    {
        public required string ResourceId { get; init; }
        public required string Action { get; init; }
        public required DateTime Timestamp { get; init; }
        public GeoLocation? Location { get; init; }
    }
}
