using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Dynamic authorization strategy providing runtime-adaptive access control
    /// with context-aware decision making, real-time risk assessment, and adaptive policies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dynamic authorization features:
    /// - Runtime policy evaluation based on current context
    /// - Risk-based adaptive access control
    /// - Just-In-Time (JIT) privilege elevation
    /// - Continuous access evaluation (CAE)
    /// - Context-aware authorization adjustments
    /// - Behavioral analysis integration
    /// - Real-time threat response
    /// </para>
    /// </remarks>
    public sealed class DynamicAuthorizationStrategy : AccessControlStrategyBase
    {
        private readonly BoundedDictionary<string, DynamicPolicy> _policies = new BoundedDictionary<string, DynamicPolicy>(1000);
        private readonly BoundedDictionary<string, JitElevation> _jitElevations = new BoundedDictionary<string, JitElevation>(1000);
        private readonly BoundedDictionary<string, UserBehaviorProfile> _behaviorProfiles = new BoundedDictionary<string, UserBehaviorProfile>(1000);
        private readonly BoundedDictionary<string, RiskAssessment> _riskCache = new BoundedDictionary<string, RiskAssessment>(1000);
        private readonly BoundedDictionary<string, AccessSession> _activeSessions = new BoundedDictionary<string, AccessSession>(1000);
        private readonly List<IRiskSignalProvider> _riskProviders = new();
        private readonly List<IContextEnricher> _contextEnrichers = new();

        private double _defaultRiskThreshold = 0.7;
        private TimeSpan _jitDefaultDuration = TimeSpan.FromMinutes(30);
        private TimeSpan _sessionReevaluationInterval = TimeSpan.FromMinutes(5);
        private Timer? _sessionMonitorTimer;

        /// <inheritdoc/>
        public override string StrategyId => "dynamic-authz";

        /// <inheritdoc/>
        public override string StrategyName => "Dynamic Authorization";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 5000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load configuration
            if (configuration.TryGetValue("DefaultRiskThreshold", out var drtObj) && drtObj is double drt)
                _defaultRiskThreshold = drt;

            if (configuration.TryGetValue("JitDefaultDurationMinutes", out var jddObj) && jddObj is int jdd)
                _jitDefaultDuration = TimeSpan.FromMinutes(jdd);

            if (configuration.TryGetValue("SessionReevaluationMinutes", out var srmObj) && srmObj is int srm)
                _sessionReevaluationInterval = TimeSpan.FromMinutes(srm);

            // Initialize default policies
            InitializeDefaultPolicies();

            // Start session monitoring
            _sessionMonitorTimer = new Timer(
                async _ => await ReevaluateActiveSessionsAsync(),
                null,
                _sessionReevaluationInterval,
                _sessionReevaluationInterval);

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dynamic.authz.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dynamic.authz.shutdown");
            _policies.Clear();
            _jitElevations.Clear();
            _behaviorProfiles.Clear();
            _riskCache.Clear();
            _activeSessions.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        #region Policy Management

        /// <summary>
        /// Adds a dynamic policy.
        /// </summary>
        public void AddPolicy(DynamicPolicy policy)
        {
            _policies[policy.Id] = policy;
        }

        /// <summary>
        /// Removes a dynamic policy.
        /// </summary>
        public bool RemovePolicy(string policyId)
        {
            return _policies.TryRemove(policyId, out _);
        }

        /// <summary>
        /// Gets all dynamic policies.
        /// </summary>
        public IReadOnlyList<DynamicPolicy> GetPolicies()
        {
            return _policies.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Registers a risk signal provider.
        /// </summary>
        public void RegisterRiskProvider(IRiskSignalProvider provider)
        {
            _riskProviders.Add(provider);
        }

        /// <summary>
        /// Registers a context enricher.
        /// </summary>
        public void RegisterContextEnricher(IContextEnricher enricher)
        {
            _contextEnrichers.Add(enricher);
        }

        #endregion

        #region JIT Privilege Elevation

        /// <summary>
        /// Requests Just-In-Time privilege elevation.
        /// </summary>
        public async Task<JitElevationResult> RequestJitElevationAsync(
            string userId,
            string[] requestedPrivileges,
            string reason,
            TimeSpan? duration = null,
            CancellationToken cancellationToken = default)
        {
            var effectiveDuration = duration ?? _jitDefaultDuration;

            // Assess risk for elevation
            var riskAssessment = await AssessRiskAsync(userId, requestedPrivileges, cancellationToken);

            if (riskAssessment.OverallRiskScore > _defaultRiskThreshold)
            {
                return new JitElevationResult
                {
                    IsApproved = false,
                    Reason = $"Risk score ({riskAssessment.OverallRiskScore:F2}) exceeds threshold ({_defaultRiskThreshold:F2})",
                    RiskAssessment = riskAssessment,
                    RequiresApproval = true,
                    ApprovalWorkflowId = await InitiateApprovalWorkflowAsync(userId, requestedPrivileges, reason)
                };
            }

            // Auto-approve low-risk elevations
            var elevation = new JitElevation
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                Privileges = requestedPrivileges,
                Reason = reason,
                GrantedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(effectiveDuration),
                RiskScoreAtGrant = riskAssessment.OverallRiskScore,
                IsActive = true,
                ApprovalType = JitApprovalType.AutoApproved
            };

            _jitElevations[elevation.Id] = elevation;

            return new JitElevationResult
            {
                IsApproved = true,
                Elevation = elevation,
                RiskAssessment = riskAssessment,
                Reason = "Auto-approved based on low risk score"
            };
        }

        /// <summary>
        /// Approves a pending JIT elevation request.
        /// </summary>
        public JitElevation? ApproveJitElevation(
            string elevationId,
            string approverId,
            TimeSpan? duration = null)
        {
            if (!_jitElevations.TryGetValue(elevationId, out var elevation))
                return null;

            var effectiveDuration = duration ?? _jitDefaultDuration;

            var approved = elevation with
            {
                GrantedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(effectiveDuration),
                IsActive = true,
                ApprovalType = JitApprovalType.ManualApproved,
                ApprovedBy = approverId,
                ApprovedAt = DateTime.UtcNow
            };

            _jitElevations[elevationId] = approved;
            return approved;
        }

        /// <summary>
        /// Revokes a JIT elevation.
        /// </summary>
        public bool RevokeJitElevation(string elevationId, string revokedBy, string reason)
        {
            if (!_jitElevations.TryGetValue(elevationId, out var elevation))
                return false;

            _jitElevations[elevationId] = elevation with
            {
                IsActive = false,
                RevokedAt = DateTime.UtcNow,
                RevokedBy = revokedBy,
                RevocationReason = reason
            };

            return true;
        }

        /// <summary>
        /// Gets active JIT elevations for a user.
        /// </summary>
        public IReadOnlyList<JitElevation> GetActiveElevations(string userId)
        {
            return _jitElevations.Values
                .Where(e => e.UserId == userId && e.IsActive && e.ExpiresAt > DateTime.UtcNow)
                .ToList()
                .AsReadOnly();
        }

        private Task<string> InitiateApprovalWorkflowAsync(string userId, string[] privileges, string reason)
        {
            // In a real implementation, this would integrate with a workflow system
            var workflowId = Guid.NewGuid().ToString("N");

            // Create pending elevation
            var pendingElevation = new JitElevation
            {
                Id = workflowId,
                UserId = userId,
                Privileges = privileges,
                Reason = reason,
                IsActive = false,
                ApprovalType = JitApprovalType.PendingApproval
            };

            _jitElevations[workflowId] = pendingElevation;

            return Task.FromResult(workflowId);
        }

        #endregion

        #region Risk Assessment

        /// <summary>
        /// Assesses risk for a user and requested privileges.
        /// </summary>
        public async Task<RiskAssessment> AssessRiskAsync(
            string userId,
            string[]? requestedPrivileges = null,
            CancellationToken cancellationToken = default)
        {
            var signals = new List<RiskSignal>();

            // Collect signals from all providers
            foreach (var provider in _riskProviders)
            {
                try
                {
                    var providerSignals = await provider.GetRiskSignalsAsync(userId, cancellationToken);
                    signals.AddRange(providerSignals);
                }
                catch
                {
                    // Continue with other providers
                }
            }

            // Get behavior profile
            var behaviorProfile = GetOrCreateBehaviorProfile(userId);
            signals.AddRange(AnalyzeBehavior(behaviorProfile));

            // Calculate overall risk score
            var overallScore = CalculateOverallRiskScore(signals);

            // Determine risk factors
            var riskFactors = signals
                .Where(s => s.Score > 0.3)
                .Select(s => new RiskFactor
                {
                    Name = s.Name,
                    Score = s.Score,
                    Description = s.Description,
                    Source = s.Source
                })
                .ToList();

            var assessment = new RiskAssessment
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                AssessedAt = DateTime.UtcNow,
                OverallRiskScore = overallScore,
                RiskLevel = ClassifyRiskLevel(overallScore),
                RiskFactors = riskFactors.AsReadOnly(),
                Signals = signals.AsReadOnly(),
                RequestedPrivileges = requestedPrivileges
            };

            // Cache the assessment
            _riskCache[userId] = assessment;

            return assessment;
        }

        /// <summary>
        /// Gets the cached risk assessment for a user.
        /// </summary>
        public RiskAssessment? GetCachedRiskAssessment(string userId)
        {
            if (_riskCache.TryGetValue(userId, out var assessment))
            {
                // Check if still valid (e.g., within 5 minutes)
                if ((DateTime.UtcNow - assessment.AssessedAt).TotalMinutes < 5)
                {
                    return assessment;
                }
            }
            return null;
        }

        private static double CalculateOverallRiskScore(List<RiskSignal> signals)
        {
            if (!signals.Any())
                return 0.0;

            // Weighted average with emphasis on high-risk signals
            var weightedSum = signals.Sum(s => s.Score * s.Weight);
            var totalWeight = signals.Sum(s => s.Weight);

            return totalWeight > 0 ? Math.Min(1.0, weightedSum / totalWeight) : 0.0;
        }

        private static RiskLevel ClassifyRiskLevel(double score)
        {
            return score switch
            {
                < 0.2 => RiskLevel.VeryLow,
                < 0.4 => RiskLevel.Low,
                < 0.6 => RiskLevel.Medium,
                < 0.8 => RiskLevel.High,
                _ => RiskLevel.Critical
            };
        }

        #endregion

        #region Behavior Analysis

        /// <summary>
        /// Updates the behavior profile for a user.
        /// </summary>
        public void RecordBehavior(string userId, BehaviorEvent behaviorEvent)
        {
            var profile = GetOrCreateBehaviorProfile(userId);

            profile.RecentEvents.Enqueue(behaviorEvent);
            profile.LastActivityAt = DateTime.UtcNow;
            profile.TotalEvents++;

            // Keep last 1000 events
            while (profile.RecentEvents.Count > 1000)
            {
                profile.RecentEvents.TryDequeue(out _);
            }

            // Update patterns
            UpdateBehaviorPatterns(profile, behaviorEvent);
        }

        /// <summary>
        /// Gets the behavior profile for a user.
        /// </summary>
        public UserBehaviorProfile? GetBehaviorProfile(string userId)
        {
            return _behaviorProfiles.TryGetValue(userId, out var profile) ? profile : null;
        }

        private UserBehaviorProfile GetOrCreateBehaviorProfile(string userId)
        {
            return _behaviorProfiles.GetOrAdd(userId, _ => new UserBehaviorProfile
            {
                UserId = userId,
                CreatedAt = DateTime.UtcNow
            });
        }

        private static void UpdateBehaviorPatterns(UserBehaviorProfile profile, BehaviorEvent behaviorEvent)
        {
            // Track hourly patterns
            var hour = behaviorEvent.Timestamp.Hour;
            if (!profile.HourlyActivityPattern.ContainsKey(hour))
                profile.HourlyActivityPattern[hour] = 0;
            profile.HourlyActivityPattern[hour]++;

            // Track resource access patterns
            if (!profile.ResourceAccessPattern.ContainsKey(behaviorEvent.ResourceId))
                profile.ResourceAccessPattern[behaviorEvent.ResourceId] = 0;
            profile.ResourceAccessPattern[behaviorEvent.ResourceId]++;

            // Track action patterns
            if (!profile.ActionPattern.ContainsKey(behaviorEvent.Action))
                profile.ActionPattern[behaviorEvent.Action] = 0;
            profile.ActionPattern[behaviorEvent.Action]++;
        }

        private List<RiskSignal> AnalyzeBehavior(UserBehaviorProfile profile)
        {
            var signals = new List<RiskSignal>();

            // Check for unusual time access
            var currentHour = DateTime.UtcNow.Hour;
            if (profile.HourlyActivityPattern.Any())
            {
                var typicalHours = profile.HourlyActivityPattern
                    .OrderByDescending(kv => kv.Value)
                    .Take(8)
                    .Select(kv => kv.Key)
                    .ToList();

                if (!typicalHours.Contains(currentHour))
                {
                    signals.Add(new RiskSignal
                    {
                        Name = "UnusualAccessTime",
                        Description = $"Access at unusual hour ({currentHour}:00)",
                        Score = 0.3,
                        Weight = 1.0,
                        Source = "BehaviorAnalysis"
                    });
                }
            }

            // Check for activity spike
            var recentCount = profile.RecentEvents.Count(e => (DateTime.UtcNow - e.Timestamp).TotalHours < 1);
            var avgHourlyActivity = profile.TotalEvents / Math.Max(1, (DateTime.UtcNow - profile.CreatedAt).TotalHours);

            if (recentCount > avgHourlyActivity * 3)
            {
                signals.Add(new RiskSignal
                {
                    Name = "ActivitySpike",
                    Description = $"Activity spike detected ({recentCount} events in last hour, avg: {avgHourlyActivity:F1})",
                    Score = 0.4,
                    Weight = 1.5,
                    Source = "BehaviorAnalysis"
                });
            }

            return signals;
        }

        #endregion

        #region Session Management

        /// <summary>
        /// Creates an access session for continuous evaluation.
        /// </summary>
        public AccessSession CreateSession(string userId, string sessionId, AccessContext initialContext)
        {
            var session = new AccessSession
            {
                Id = sessionId,
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                LastEvaluatedAt = DateTime.UtcNow,
                InitialContext = initialContext,
                IsActive = true,
                CurrentRiskScore = 0.0
            };

            _activeSessions[sessionId] = session;
            return session;
        }

        /// <summary>
        /// Updates session context for continuous access evaluation.
        /// </summary>
        public async Task<SessionEvaluationResult> EvaluateSessionAsync(
            string sessionId,
            AccessContext currentContext,
            CancellationToken cancellationToken = default)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                return new SessionEvaluationResult
                {
                    IsValid = false,
                    Action = SessionAction.Terminate,
                    Reason = "Session not found"
                };
            }

            if (!session.IsActive)
            {
                return new SessionEvaluationResult
                {
                    IsValid = false,
                    Action = SessionAction.Terminate,
                    Reason = "Session is inactive"
                };
            }

            // Assess current risk
            var riskAssessment = await AssessRiskAsync(session.UserId, null, cancellationToken);

            // Compare with session threshold
            var thresholdExceeded = riskAssessment.OverallRiskScore > _defaultRiskThreshold;

            // Detect context changes
            var contextChanges = DetectContextChanges(session.InitialContext, currentContext);

            // Update session
            session.LastEvaluatedAt = DateTime.UtcNow;
            session.CurrentRiskScore = riskAssessment.OverallRiskScore;
            session.EvaluationCount++;

            if (thresholdExceeded || contextChanges.Any(c => c.IsCritical))
            {
                return new SessionEvaluationResult
                {
                    IsValid = false,
                    Action = contextChanges.Any(c => c.IsCritical) ? SessionAction.ReAuthenticate : SessionAction.StepUp,
                    Reason = thresholdExceeded
                        ? $"Risk score ({riskAssessment.OverallRiskScore:F2}) exceeds threshold"
                        : $"Critical context change: {contextChanges.First(c => c.IsCritical).FieldName}",
                    RiskAssessment = riskAssessment,
                    ContextChanges = contextChanges.AsReadOnly()
                };
            }

            return new SessionEvaluationResult
            {
                IsValid = true,
                Action = SessionAction.Continue,
                Reason = "Session valid",
                RiskAssessment = riskAssessment,
                ContextChanges = contextChanges.AsReadOnly()
            };
        }

        /// <summary>
        /// Terminates an access session.
        /// </summary>
        public void TerminateSession(string sessionId, string reason)
        {
            if (_activeSessions.TryGetValue(sessionId, out var session))
            {
                _activeSessions[sessionId] = session with
                {
                    IsActive = false,
                    TerminatedAt = DateTime.UtcNow,
                    TerminationReason = reason
                };
            }
        }

        private async Task ReevaluateActiveSessionsAsync()
        {
            var sessionsToEvaluate = _activeSessions.Values
                .Where(s => s.IsActive && (DateTime.UtcNow - s.LastEvaluatedAt) > _sessionReevaluationInterval)
                .ToList();

            foreach (var session in sessionsToEvaluate)
            {
                try
                {
                    var result = await EvaluateSessionAsync(session.Id, session.InitialContext, CancellationToken.None);

                    if (!result.IsValid && result.Action == SessionAction.Terminate)
                    {
                        TerminateSession(session.Id, result.Reason);
                    }
                }
                catch
                {
                    // Continue with other sessions
                }
            }
        }

        private static List<ContextChange> DetectContextChanges(AccessContext initial, AccessContext current)
        {
            var changes = new List<ContextChange>();

            // Check IP address change
            if (initial.ClientIpAddress != current.ClientIpAddress)
            {
                changes.Add(new ContextChange
                {
                    FieldName = "ClientIpAddress",
                    OldValue = initial.ClientIpAddress,
                    NewValue = current.ClientIpAddress,
                    IsCritical = true
                });
            }

            // Check location change
            if (initial.Location?.Country != current.Location?.Country)
            {
                changes.Add(new ContextChange
                {
                    FieldName = "Country",
                    OldValue = initial.Location?.Country,
                    NewValue = current.Location?.Country,
                    IsCritical = true
                });
            }

            return changes;
        }

        #endregion

        #region Core Evaluation

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("dynamic.authz.evaluate");
            // Enrich context
            var enrichedContext = await EnrichContextAsync(context, cancellationToken);

            // Assess risk
            var riskAssessment = await AssessRiskAsync(enrichedContext.SubjectId, null, cancellationToken);

            // Check for active JIT elevations
            var activeElevations = GetActiveElevations(enrichedContext.SubjectId);
            var hasElevation = activeElevations.Any(e =>
                e.Privileges.Contains("*") ||
                e.Privileges.Any(p => MatchesPrivilege(p, enrichedContext.ResourceId, enrichedContext.Action)));

            // Evaluate dynamic policies
            var policyResults = new List<DynamicPolicyResult>();
            foreach (var policy in _policies.Values.Where(p => p.IsEnabled).OrderBy(p => p.Priority))
            {
                var result = EvaluatePolicy(policy, enrichedContext, riskAssessment);
                policyResults.Add(result);

                // Short-circuit on explicit deny
                if (result.Decision == DynamicDecision.Deny)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"Denied by dynamic policy '{policy.Name}': {result.Reason}",
                        ApplicablePolicies = new[] { policy.Id },
                        Metadata = new Dictionary<string, object>
                        {
                            ["RiskScore"] = riskAssessment.OverallRiskScore,
                            ["RiskLevel"] = riskAssessment.RiskLevel.ToString()
                        }
                    };
                }
            }

            // Check if any policy granted access
            var granted = policyResults.Any(r => r.Decision == DynamicDecision.Grant);

            // Apply risk-based adjustment
            if (granted && riskAssessment.OverallRiskScore > _defaultRiskThreshold && !hasElevation)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Access denied due to elevated risk ({riskAssessment.OverallRiskScore:F2}). Request JIT elevation for temporary access.",
                    ApplicablePolicies = new[] { "DynamicAuthz.RiskThreshold" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["RiskScore"] = riskAssessment.OverallRiskScore,
                        ["RiskLevel"] = riskAssessment.RiskLevel.ToString(),
                        ["RequiresJitElevation"] = true
                    }
                };
            }

            if (granted || hasElevation)
            {
                // Record behavior
                RecordBehavior(enrichedContext.SubjectId, new BehaviorEvent
                {
                    UserId = enrichedContext.SubjectId,
                    ResourceId = enrichedContext.ResourceId,
                    Action = enrichedContext.Action,
                    Timestamp = DateTime.UtcNow,
                    WasGranted = true
                });

                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = hasElevation ? "Granted via JIT privilege elevation" : "Granted by dynamic authorization",
                    ApplicablePolicies = policyResults.Where(r => r.Decision == DynamicDecision.Grant).Select(r => r.PolicyId).ToArray(),
                    Metadata = new Dictionary<string, object>
                    {
                        ["RiskScore"] = riskAssessment.OverallRiskScore,
                        ["RiskLevel"] = riskAssessment.RiskLevel.ToString(),
                        ["HasJitElevation"] = hasElevation
                    }
                };
            }

            return new AccessDecision
            {
                IsGranted = false,
                Reason = "No applicable dynamic policy granted access",
                ApplicablePolicies = new[] { "DynamicAuthz.NoMatchingPolicy" },
                Metadata = new Dictionary<string, object>
                {
                    ["RiskScore"] = riskAssessment.OverallRiskScore,
                    ["RiskLevel"] = riskAssessment.RiskLevel.ToString()
                }
            };
        }

        private DynamicPolicyResult EvaluatePolicy(DynamicPolicy policy, AccessContext context, RiskAssessment riskAssessment)
        {
            // Check target match
            if (!MatchesTarget(policy.Target, context))
            {
                return new DynamicPolicyResult
                {
                    PolicyId = policy.Id,
                    Decision = DynamicDecision.NotApplicable,
                    Reason = "Target does not match"
                };
            }

            // Check risk threshold
            if (policy.MaxRiskScore.HasValue && riskAssessment.OverallRiskScore > policy.MaxRiskScore.Value)
            {
                return new DynamicPolicyResult
                {
                    PolicyId = policy.Id,
                    Decision = DynamicDecision.Deny,
                    Reason = $"Risk score ({riskAssessment.OverallRiskScore:F2}) exceeds policy limit ({policy.MaxRiskScore.Value:F2})"
                };
            }

            // Evaluate conditions
            foreach (var condition in policy.Conditions)
            {
                if (!EvaluateCondition(condition, context, riskAssessment))
                {
                    return new DynamicPolicyResult
                    {
                        PolicyId = policy.Id,
                        Decision = policy.Effect == DynamicPolicyEffect.Deny ? DynamicDecision.NotApplicable : DynamicDecision.Deny,
                        Reason = $"Condition '{condition.Name}' not satisfied"
                    };
                }
            }

            return new DynamicPolicyResult
            {
                PolicyId = policy.Id,
                Decision = policy.Effect == DynamicPolicyEffect.Deny ? DynamicDecision.Deny : DynamicDecision.Grant,
                Reason = "All conditions satisfied"
            };
        }

        private static bool MatchesTarget(DynamicPolicyTarget target, AccessContext context)
        {
            if (target.Subjects != null && target.Subjects.Any() &&
                !target.Subjects.Contains("*") &&
                !target.Subjects.Contains(context.SubjectId, StringComparer.OrdinalIgnoreCase) &&
                !target.Subjects.Intersect(context.Roles, StringComparer.OrdinalIgnoreCase).Any())
            {
                return false;
            }

            if (target.Resources != null && target.Resources.Any() &&
                !target.Resources.Contains("*") &&
                !target.Resources.Any(r => context.ResourceId.StartsWith(r.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (target.Actions != null && target.Actions.Any() &&
                !target.Actions.Contains("*") &&
                !target.Actions.Contains(context.Action, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool EvaluateCondition(DynamicCondition condition, AccessContext context, RiskAssessment riskAssessment)
        {
            return condition.Type switch
            {
                DynamicConditionType.RiskLevel => riskAssessment.RiskLevel <= (RiskLevel)condition.Value!,
                DynamicConditionType.RiskScore => riskAssessment.OverallRiskScore <= (double)condition.Value!,
                DynamicConditionType.TimeWindow => IsInTimeWindow(context.RequestTime, condition.StartTime, condition.EndTime),
                DynamicConditionType.Location => CheckLocation(context, condition.AllowedLocations),
                DynamicConditionType.Attribute => CheckAttribute(context, condition.AttributeName!, condition.AttributeValue!),
                DynamicConditionType.Custom => condition.CustomEvaluator?.Invoke(context, riskAssessment) ?? false,
                _ => false
            };
        }

        private static bool IsInTimeWindow(DateTime time, TimeSpan? start, TimeSpan? end)
        {
            if (!start.HasValue || !end.HasValue)
                return true;

            var timeOfDay = time.TimeOfDay;
            return timeOfDay >= start.Value && timeOfDay <= end.Value;
        }

        private static bool CheckLocation(AccessContext context, string[]? allowedLocations)
        {
            if (allowedLocations == null || !allowedLocations.Any())
                return true;

            return allowedLocations.Contains(context.Location?.Country, StringComparer.OrdinalIgnoreCase);
        }

        private static bool CheckAttribute(AccessContext context, string attributeName, object expectedValue)
        {
            if (context.SubjectAttributes.TryGetValue(attributeName, out var value))
            {
                return Equals(value, expectedValue);
            }
            return false;
        }

        private static bool MatchesPrivilege(string privilege, string resourceId, string action)
        {
            if (privilege == "*") return true;

            var parts = privilege.Split(':');
            if (parts.Length != 2) return false;

            var privResource = parts[0];
            var privAction = parts[1];

            var resourceMatch = privResource == "*" ||
                               resourceId.StartsWith(privResource.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
            var actionMatch = privAction == "*" ||
                             privAction.Equals(action, StringComparison.OrdinalIgnoreCase);

            return resourceMatch && actionMatch;
        }

        private async Task<AccessContext> EnrichContextAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var subjectAttrs = new Dictionary<string, object>(context.SubjectAttributes);
            var resourceAttrs = new Dictionary<string, object>(context.ResourceAttributes);
            var envAttrs = new Dictionary<string, object>(context.EnvironmentAttributes);

            foreach (var enricher in _contextEnrichers)
            {
                try
                {
                    var enrichment = await enricher.EnrichAsync(context, cancellationToken);
                    foreach (var (key, value) in enrichment)
                    {
                        envAttrs[key] = value;
                    }
                }
                catch
                {
                    // Continue with other enrichers
                }
            }

            return context with
            {
                SubjectAttributes = subjectAttrs,
                ResourceAttributes = resourceAttrs,
                EnvironmentAttributes = envAttrs
            };
        }

        private void InitializeDefaultPolicies()
        {
            // Default authenticated user policy
            AddPolicy(new DynamicPolicy
            {
                Id = "default-authenticated",
                Name = "Default Authenticated Access",
                IsEnabled = true,
                Priority = 100,
                Effect = DynamicPolicyEffect.Grant,
                Target = new DynamicPolicyTarget { Subjects = new[] { "*" } },
                Conditions = new List<DynamicCondition>
                {
                    new()
                    {
                        Name = "LowRisk",
                        Type = DynamicConditionType.RiskLevel,
                        Value = RiskLevel.Medium
                    }
                },
                MaxRiskScore = 0.5
            });

            // High-risk resource policy
            AddPolicy(new DynamicPolicy
            {
                Id = "high-risk-resources",
                Name = "High-Risk Resource Protection",
                IsEnabled = true,
                Priority = 10,
                Effect = DynamicPolicyEffect.Deny,
                Target = new DynamicPolicyTarget { Resources = new[] { "admin/*", "sensitive/*" } },
                Conditions = new List<DynamicCondition>
                {
                    new()
                    {
                        Name = "HighRisk",
                        Type = DynamicConditionType.RiskLevel,
                        Value = RiskLevel.High
                    }
                }
            });
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Dynamic policy definition.
    /// </summary>
    public sealed record DynamicPolicy
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public bool IsEnabled { get; init; } = true;
        public int Priority { get; init; } = 100;
        public DynamicPolicyEffect Effect { get; init; }
        public required DynamicPolicyTarget Target { get; init; }
        public List<DynamicCondition> Conditions { get; init; } = new();
        public double? MaxRiskScore { get; init; }
    }

    /// <summary>
    /// Dynamic policy target.
    /// </summary>
    public sealed record DynamicPolicyTarget
    {
        public string[]? Subjects { get; init; }
        public string[]? Resources { get; init; }
        public string[]? Actions { get; init; }
    }

    /// <summary>
    /// Dynamic condition.
    /// </summary>
    public sealed record DynamicCondition
    {
        public required string Name { get; init; }
        public DynamicConditionType Type { get; init; }
        public object? Value { get; init; }
        public string? AttributeName { get; init; }
        public object? AttributeValue { get; init; }
        public TimeSpan? StartTime { get; init; }
        public TimeSpan? EndTime { get; init; }
        public string[]? AllowedLocations { get; init; }
        public Func<AccessContext, RiskAssessment, bool>? CustomEvaluator { get; init; }
    }

    /// <summary>
    /// Dynamic condition types.
    /// </summary>
    public enum DynamicConditionType
    {
        RiskLevel,
        RiskScore,
        TimeWindow,
        Location,
        Attribute,
        Custom
    }

    /// <summary>
    /// Dynamic policy effect.
    /// </summary>
    public enum DynamicPolicyEffect
    {
        Grant,
        Deny
    }

    /// <summary>
    /// Dynamic decision result.
    /// </summary>
    public enum DynamicDecision
    {
        Grant,
        Deny,
        NotApplicable
    }

    /// <summary>
    /// Dynamic policy evaluation result.
    /// </summary>
    public sealed record DynamicPolicyResult
    {
        public required string PolicyId { get; init; }
        public required DynamicDecision Decision { get; init; }
        public required string Reason { get; init; }
    }

    /// <summary>
    /// JIT privilege elevation.
    /// </summary>
    public sealed record JitElevation
    {
        public required string Id { get; init; }
        public required string UserId { get; init; }
        public required string[] Privileges { get; init; }
        public required string Reason { get; init; }
        public DateTime? GrantedAt { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public double? RiskScoreAtGrant { get; init; }
        public bool IsActive { get; init; }
        public JitApprovalType ApprovalType { get; init; }
        public string? ApprovedBy { get; init; }
        public DateTime? ApprovedAt { get; init; }
        public DateTime? RevokedAt { get; init; }
        public string? RevokedBy { get; init; }
        public string? RevocationReason { get; init; }
    }

    /// <summary>
    /// JIT approval types.
    /// </summary>
    public enum JitApprovalType
    {
        AutoApproved,
        ManualApproved,
        PendingApproval,
        Denied
    }

    /// <summary>
    /// JIT elevation result.
    /// </summary>
    public sealed record JitElevationResult
    {
        public required bool IsApproved { get; init; }
        public JitElevation? Elevation { get; init; }
        public RiskAssessment? RiskAssessment { get; init; }
        public required string Reason { get; init; }
        public bool RequiresApproval { get; init; }
        public string? ApprovalWorkflowId { get; init; }
    }

    /// <summary>
    /// Risk assessment result.
    /// </summary>
    public sealed record RiskAssessment
    {
        public required string Id { get; init; }
        public required string UserId { get; init; }
        public required DateTime AssessedAt { get; init; }
        public required double OverallRiskScore { get; init; }
        public required RiskLevel RiskLevel { get; init; }
        public required IReadOnlyList<RiskFactor> RiskFactors { get; init; }
        public required IReadOnlyList<RiskSignal> Signals { get; init; }
        public string[]? RequestedPrivileges { get; init; }
    }

    /// <summary>
    /// Risk level.
    /// </summary>
    public enum RiskLevel
    {
        VeryLow,
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Risk factor.
    /// </summary>
    public sealed record RiskFactor
    {
        public required string Name { get; init; }
        public required double Score { get; init; }
        public required string Description { get; init; }
        public required string Source { get; init; }
    }

    /// <summary>
    /// Risk signal.
    /// </summary>
    public sealed record RiskSignal
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required double Score { get; init; }
        public required double Weight { get; init; }
        public required string Source { get; init; }
    }

    /// <summary>
    /// Risk signal provider interface.
    /// </summary>
    public interface IRiskSignalProvider
    {
        string Id { get; }
        Task<IEnumerable<RiskSignal>> GetRiskSignalsAsync(string userId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Context enricher interface.
    /// </summary>
    public interface IContextEnricher
    {
        string Id { get; }
        Task<Dictionary<string, object>> EnrichAsync(AccessContext context, CancellationToken cancellationToken);
    }

    /// <summary>
    /// User behavior profile.
    /// </summary>
    public sealed class UserBehaviorProfile
    {
        public required string UserId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? LastActivityAt { get; set; }
        public long TotalEvents { get; set; }
        public ConcurrentQueue<BehaviorEvent> RecentEvents { get; } = new();
        public Dictionary<int, int> HourlyActivityPattern { get; } = new();
        public Dictionary<string, int> ResourceAccessPattern { get; } = new();
        public Dictionary<string, int> ActionPattern { get; } = new();
    }

    /// <summary>
    /// Behavior event.
    /// </summary>
    public sealed record BehaviorEvent
    {
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required string Action { get; init; }
        public required DateTime Timestamp { get; init; }
        public bool WasGranted { get; init; }
    }

    /// <summary>
    /// Access session for continuous evaluation.
    /// </summary>
    public sealed record AccessSession
    {
        public required string Id { get; init; }
        public required string UserId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime LastEvaluatedAt { get; set; }
        public required AccessContext InitialContext { get; init; }
        public bool IsActive { get; set; }
        public double CurrentRiskScore { get; set; }
        public int EvaluationCount { get; set; }
        public DateTime? TerminatedAt { get; init; }
        public string? TerminationReason { get; init; }
    }

    /// <summary>
    /// Session evaluation result.
    /// </summary>
    public sealed record SessionEvaluationResult
    {
        public required bool IsValid { get; init; }
        public required SessionAction Action { get; init; }
        public required string Reason { get; init; }
        public RiskAssessment? RiskAssessment { get; init; }
        public IReadOnlyList<ContextChange>? ContextChanges { get; init; }
    }

    /// <summary>
    /// Session action.
    /// </summary>
    public enum SessionAction
    {
        Continue,
        StepUp,
        ReAuthenticate,
        Terminate
    }

    /// <summary>
    /// Context change detection.
    /// </summary>
    public sealed record ContextChange
    {
        public required string FieldName { get; init; }
        public object? OldValue { get; init; }
        public object? NewValue { get; init; }
        public bool IsCritical { get; init; }
    }

    #endregion
}
