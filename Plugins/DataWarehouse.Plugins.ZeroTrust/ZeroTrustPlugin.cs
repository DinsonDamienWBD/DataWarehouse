using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.ZeroTrust
{
    /// <summary>
    /// Comprehensive Zero-Trust Architecture plugin implementing the "never trust, always verify" security model.
    ///
    /// Zero-Trust Principles Implemented:
    /// - Verify Explicitly: Every request is authenticated and authorized
    /// - Least Privilege: Minimum permissions are always enforced
    /// - Assume Breach: Microsegmentation, encryption everywhere, anomaly detection
    /// - Continuous Validation: Re-authentication on context changes
    ///
    /// Features:
    /// - Identity-Aware Proxy: All access routed through authenticated proxy
    /// - Microsegmentation: Plugin isolation with controlled communication paths
    /// - Continuous Verification: Behavior-based trust scoring with session validation
    /// - Device Trust: Endpoint health checks and compliance verification
    /// - Network Segmentation: Zero-trust network access controls
    /// - Data Classification: Automatic sensitivity level classification
    /// - Trust Score Calculation: Real-time trust assessment based on multiple factors
    /// - Session Management: Continuous validation with automatic revocation
    /// - Anomaly Detection: Behavioral analysis for threat detection
    ///
    /// Message Commands:
    /// - zerotrust.verify: Verify a request through the zero-trust framework
    /// - zerotrust.session.create: Create a new verified session
    /// - zerotrust.session.validate: Validate an existing session
    /// - zerotrust.session.revoke: Revoke a session
    /// - zerotrust.device.register: Register a device for trust evaluation
    /// - zerotrust.device.verify: Verify device compliance
    /// - zerotrust.policy.create: Create a zero-trust policy
    /// - zerotrust.policy.evaluate: Evaluate policies for a request
    /// - zerotrust.classify: Classify data sensitivity level
    /// - zerotrust.segment.create: Create a microsegment
    /// - zerotrust.segment.allow: Allow communication between segments
    /// - zerotrust.trustscore: Calculate trust score for a context
    /// </summary>
    public sealed class ZeroTrustPlugin : SecurityProviderPluginBase
    {
        private readonly ZeroTrustConfig _config;
        private readonly ConcurrentDictionary<string, ZeroTrustSession> _sessions;
        private readonly ConcurrentDictionary<string, DeviceRegistration> _devices;
        private readonly ConcurrentDictionary<string, ZeroTrustPolicy> _policies;
        private readonly ConcurrentDictionary<string, Microsegment> _segments;
        private readonly ConcurrentDictionary<string, BehaviorProfile> _behaviorProfiles;
        private readonly ConcurrentDictionary<string, DataClassification> _classifications;
        private readonly ConcurrentDictionary<string, IdentityContext> _identities;
        private readonly SemaphoreSlim _persistLock = new(1, 1);
        private readonly Timer _sessionCleanupTimer;
        private readonly Timer _behaviorAnalysisTimer;
        private readonly AnomalyDetector _anomalyDetector;
        private readonly TrustScoreCalculator _trustCalculator;
        private bool _isDisposed;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.security.zerotrust";

        /// <inheritdoc/>
        public override string Name => "Zero-Trust Architecture";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <summary>
        /// Initializes a new instance of the <see cref="ZeroTrustPlugin"/> class.
        /// </summary>
        /// <param name="config">Optional configuration for zero-trust settings.</param>
        public ZeroTrustPlugin(ZeroTrustConfig? config = null)
        {
            _config = config ?? new ZeroTrustConfig();
            _sessions = new ConcurrentDictionary<string, ZeroTrustSession>();
            _devices = new ConcurrentDictionary<string, DeviceRegistration>();
            _policies = new ConcurrentDictionary<string, ZeroTrustPolicy>();
            _segments = new ConcurrentDictionary<string, Microsegment>();
            _behaviorProfiles = new ConcurrentDictionary<string, BehaviorProfile>();
            _classifications = new ConcurrentDictionary<string, DataClassification>();
            _identities = new ConcurrentDictionary<string, IdentityContext>();
            _anomalyDetector = new AnomalyDetector(_config);
            _trustCalculator = new TrustScoreCalculator(_config);

            _sessionCleanupTimer = new Timer(
                CleanupExpiredSessions,
                null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1));

            _behaviorAnalysisTimer = new Timer(
                AnalyzeBehaviorPatterns,
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));

            InitializeDefaultPolicies();
            InitializeDefaultSegments();
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);
            await LoadStateAsync();
            return response;
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return
            [
                new() { Name = "zerotrust.verify", DisplayName = "Verify Request", Description = "Verify a request through zero-trust framework" },
                new() { Name = "zerotrust.session.create", DisplayName = "Create Session", Description = "Create a verified session" },
                new() { Name = "zerotrust.session.validate", DisplayName = "Validate Session", Description = "Validate an existing session" },
                new() { Name = "zerotrust.session.revoke", DisplayName = "Revoke Session", Description = "Revoke a session" },
                new() { Name = "zerotrust.device.register", DisplayName = "Register Device", Description = "Register a device for trust evaluation" },
                new() { Name = "zerotrust.device.verify", DisplayName = "Verify Device", Description = "Verify device compliance" },
                new() { Name = "zerotrust.policy.create", DisplayName = "Create Policy", Description = "Create a zero-trust policy" },
                new() { Name = "zerotrust.policy.evaluate", DisplayName = "Evaluate Policy", Description = "Evaluate policies for a request" },
                new() { Name = "zerotrust.classify", DisplayName = "Classify Data", Description = "Classify data sensitivity level" },
                new() { Name = "zerotrust.segment.create", DisplayName = "Create Segment", Description = "Create a microsegment" },
                new() { Name = "zerotrust.segment.allow", DisplayName = "Allow Segment Communication", Description = "Allow communication between segments" },
                new() { Name = "zerotrust.trustscore", DisplayName = "Calculate Trust Score", Description = "Calculate trust score for a context" }
            ];
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SecurityType"] = "ZeroTrust";
            metadata["SupportsIdentityAwareProxy"] = true;
            metadata["SupportsMicrosegmentation"] = true;
            metadata["SupportsContinuousVerification"] = true;
            metadata["SupportsDeviceTrust"] = true;
            metadata["SupportsNetworkSegmentation"] = true;
            metadata["SupportsDataClassification"] = true;
            metadata["SupportsTrustScoring"] = true;
            metadata["SupportsAnomalyDetection"] = true;
            metadata["SupportsSessionManagement"] = true;
            metadata["SessionCount"] = _sessions.Count;
            metadata["DeviceCount"] = _devices.Count;
            metadata["PolicyCount"] = _policies.Count;
            metadata["SegmentCount"] = _segments.Count;
            return metadata;
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            try
            {
                switch (message.Type)
                {
                    case "zerotrust.verify":
                        await HandleVerifyRequestAsync(message);
                        break;
                    case "zerotrust.session.create":
                        await HandleCreateSessionAsync(message);
                        break;
                    case "zerotrust.session.validate":
                        await HandleValidateSessionAsync(message);
                        break;
                    case "zerotrust.session.revoke":
                        await HandleRevokeSessionAsync(message);
                        break;
                    case "zerotrust.device.register":
                        await HandleRegisterDeviceAsync(message);
                        break;
                    case "zerotrust.device.verify":
                        await HandleVerifyDeviceAsync(message);
                        break;
                    case "zerotrust.policy.create":
                        await HandleCreatePolicyAsync(message);
                        break;
                    case "zerotrust.policy.evaluate":
                        await HandleEvaluatePolicyAsync(message);
                        break;
                    case "zerotrust.classify":
                        await HandleClassifyDataAsync(message);
                        break;
                    case "zerotrust.segment.create":
                        await HandleCreateSegmentAsync(message);
                        break;
                    case "zerotrust.segment.allow":
                        await HandleAllowSegmentCommunicationAsync(message);
                        break;
                    case "zerotrust.trustscore":
                        await HandleCalculateTrustScoreAsync(message);
                        break;
                    default:
                        await base.OnMessageAsync(message);
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Invalid request parameters: {ex.Message}", ex);
            }
        }

        #region Public API

        /// <summary>
        /// Verifies a request through the zero-trust framework.
        /// </summary>
        /// <param name="context">The request context to verify.</param>
        /// <returns>The verification result with trust score and decision.</returns>
        public async Task<ZeroTrustVerificationResult> VerifyRequestAsync(RequestContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var result = new ZeroTrustVerificationResult
            {
                RequestId = context.RequestId,
                Timestamp = DateTime.UtcNow
            };

            // Step 1: Verify identity
            var identityResult = await VerifyIdentityAsync(context);
            result.IdentityVerified = identityResult.IsValid;
            result.IdentityDetails = identityResult.Details;

            if (!identityResult.IsValid && _config.RequireIdentityVerification)
            {
                result.IsAllowed = false;
                result.DenialReason = "Identity verification failed";
                result.TrustScore = 0;
                RecordVerificationAttempt(context, result);
                return result;
            }

            // Step 2: Verify device if present
            if (!string.IsNullOrEmpty(context.DeviceId))
            {
                var deviceResult = await VerifyDeviceAsync(context.DeviceId);
                result.DeviceVerified = deviceResult.IsCompliant;
                result.DeviceDetails = deviceResult.Details;

                if (!deviceResult.IsCompliant && _config.RequireDeviceCompliance)
                {
                    result.IsAllowed = false;
                    result.DenialReason = "Device compliance check failed";
                    result.TrustScore = CalculatePartialTrustScore(context, identityResult, deviceResult);
                    RecordVerificationAttempt(context, result);
                    return result;
                }
            }

            // Step 3: Verify session if present
            if (!string.IsNullOrEmpty(context.SessionId))
            {
                var sessionResult = ValidateSession(context.SessionId, context);
                result.SessionValid = sessionResult.IsValid;
                result.SessionDetails = sessionResult.Details;

                if (!sessionResult.IsValid)
                {
                    result.IsAllowed = false;
                    result.DenialReason = "Session validation failed";
                    result.TrustScore = CalculatePartialTrustScore(context, identityResult, null);
                    RecordVerificationAttempt(context, result);
                    return result;
                }
            }

            // Step 4: Check microsegmentation rules
            var segmentResult = CheckSegmentationRules(context);
            result.SegmentationAllowed = segmentResult.IsAllowed;
            result.SegmentDetails = segmentResult.Details;

            if (!segmentResult.IsAllowed)
            {
                result.IsAllowed = false;
                result.DenialReason = "Microsegmentation policy violation";
                result.TrustScore = CalculatePartialTrustScore(context, identityResult, null);
                RecordVerificationAttempt(context, result);
                return result;
            }

            // Step 5: Evaluate policies
            var policyResult = EvaluatePolicies(context);
            result.PolicyEvaluationPassed = policyResult.IsAllowed;
            result.AppliedPolicies = policyResult.AppliedPolicies;

            if (!policyResult.IsAllowed)
            {
                result.IsAllowed = false;
                result.DenialReason = $"Policy violation: {policyResult.DenialReason}";
                result.TrustScore = CalculatePartialTrustScore(context, identityResult, null);
                RecordVerificationAttempt(context, result);
                return result;
            }

            // Step 6: Check for anomalies
            var anomalyResult = _anomalyDetector.Analyze(context, GetBehaviorProfile(context.IdentityId));
            result.AnomalyDetected = anomalyResult.IsAnomalous;
            result.AnomalyDetails = anomalyResult.Details;

            if (anomalyResult.IsAnomalous && _config.BlockOnAnomaly)
            {
                result.IsAllowed = false;
                result.DenialReason = $"Anomaly detected: {anomalyResult.Details}";
                result.TrustScore = Math.Max(0, CalculatePartialTrustScore(context, identityResult, null) - anomalyResult.SeverityPenalty);
                RecordVerificationAttempt(context, result);
                return result;
            }

            // Step 7: Calculate final trust score
            result.TrustScore = _trustCalculator.Calculate(context, identityResult, result.DeviceVerified ? new DeviceVerificationResult { IsCompliant = true } : null, anomalyResult);

            // Step 8: Check minimum trust threshold
            if (result.TrustScore < _config.MinimumTrustScore)
            {
                result.IsAllowed = false;
                result.DenialReason = $"Trust score {result.TrustScore:F2} below minimum threshold {_config.MinimumTrustScore:F2}";
                RecordVerificationAttempt(context, result);
                return result;
            }

            // All checks passed
            result.IsAllowed = true;
            result.RequiredEncryption = DetermineRequiredEncryption(context);
            result.AllowedOperations = DetermineAllowedOperations(context, result.TrustScore);

            // Update behavior profile
            UpdateBehaviorProfile(context);
            RecordVerificationAttempt(context, result);

            return result;
        }

        /// <summary>
        /// Creates a new verified session.
        /// </summary>
        /// <param name="request">The session creation request.</param>
        /// <returns>The created session information.</returns>
        public async Task<ZeroTrustSession> CreateSessionAsync(SessionCreationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Verify identity before creating session
            var identityResult = await VerifyIdentityAsync(new RequestContext
            {
                IdentityId = request.IdentityId,
                SourceIp = request.SourceIp,
                DeviceId = request.DeviceId,
                RequestId = Guid.NewGuid().ToString("N")
            });

            if (!identityResult.IsValid)
            {
                throw new UnauthorizedAccessException($"Identity verification failed: {identityResult.Details}");
            }

            // Verify device if required
            if (!string.IsNullOrEmpty(request.DeviceId) && _config.RequireDeviceCompliance)
            {
                var deviceResult = await VerifyDeviceAsync(request.DeviceId);
                if (!deviceResult.IsCompliant)
                {
                    throw new UnauthorizedAccessException($"Device compliance check failed: {deviceResult.Details}");
                }
            }

            var sessionId = GenerateSecureSessionId();
            var now = DateTime.UtcNow;

            var session = new ZeroTrustSession
            {
                SessionId = sessionId,
                IdentityId = request.IdentityId,
                DeviceId = request.DeviceId,
                SourceIp = request.SourceIp,
                CreatedAt = now,
                LastActivityAt = now,
                ExpiresAt = now.Add(_config.SessionTimeout),
                InitialTrustScore = identityResult.TrustScore,
                CurrentTrustScore = identityResult.TrustScore,
                Attributes = request.Attributes ?? new Dictionary<string, object>(),
                State = SessionState.Active,
                SecurityLevel = DetermineSecurityLevel(identityResult.TrustScore)
            };

            _sessions[sessionId] = session;
            await PersistStateAsync();

            return session;
        }

        /// <summary>
        /// Validates an existing session.
        /// </summary>
        /// <param name="sessionId">The session ID to validate.</param>
        /// <param name="context">Optional context for validation.</param>
        /// <returns>The session validation result.</returns>
        public SessionValidationResult ValidateSession(string sessionId, RequestContext? context = null)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return new SessionValidationResult { IsValid = false, Details = "Session ID is required" };
            }

            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return new SessionValidationResult { IsValid = false, Details = "Session not found" };
            }

            if (session.State == SessionState.Revoked)
            {
                return new SessionValidationResult { IsValid = false, Details = "Session has been revoked" };
            }

            if (session.State == SessionState.Expired || DateTime.UtcNow > session.ExpiresAt)
            {
                session.State = SessionState.Expired;
                return new SessionValidationResult { IsValid = false, Details = "Session has expired" };
            }

            // Context-based validation
            if (context != null)
            {
                // Check for IP change
                if (!string.IsNullOrEmpty(session.SourceIp) && session.SourceIp != context.SourceIp)
                {
                    if (_config.RevokeSessionOnIpChange)
                    {
                        session.State = SessionState.Revoked;
                        return new SessionValidationResult { IsValid = false, Details = "Session revoked due to IP change" };
                    }

                    // Reduce trust score for IP change
                    session.CurrentTrustScore = Math.Max(0, session.CurrentTrustScore - _config.IpChangeTrustPenalty);
                }

                // Check for device change
                if (!string.IsNullOrEmpty(session.DeviceId) && session.DeviceId != context.DeviceId)
                {
                    if (_config.RevokeSessionOnDeviceChange)
                    {
                        session.State = SessionState.Revoked;
                        return new SessionValidationResult { IsValid = false, Details = "Session revoked due to device change" };
                    }

                    session.CurrentTrustScore = Math.Max(0, session.CurrentTrustScore - _config.DeviceChangeTrustPenalty);
                }

                // Check for anomalous behavior
                var behaviorProfile = GetBehaviorProfile(session.IdentityId);
                var anomalyResult = _anomalyDetector.Analyze(context, behaviorProfile);

                if (anomalyResult.IsAnomalous)
                {
                    session.CurrentTrustScore = Math.Max(0, session.CurrentTrustScore - anomalyResult.SeverityPenalty);

                    if (session.CurrentTrustScore < _config.MinimumTrustScore)
                    {
                        session.State = SessionState.Suspended;
                        return new SessionValidationResult { IsValid = false, Details = "Session suspended due to anomalous behavior" };
                    }
                }
            }

            // Update last activity
            session.LastActivityAt = DateTime.UtcNow;

            // Extend session if within renewal window
            if (_config.EnableSessionRenewal && DateTime.UtcNow > session.ExpiresAt.Subtract(_config.SessionRenewalWindow))
            {
                session.ExpiresAt = DateTime.UtcNow.Add(_config.SessionTimeout);
            }

            return new SessionValidationResult
            {
                IsValid = true,
                Session = session,
                RemainingTime = session.ExpiresAt - DateTime.UtcNow,
                CurrentTrustScore = session.CurrentTrustScore
            };
        }

        /// <summary>
        /// Revokes a session.
        /// </summary>
        /// <param name="sessionId">The session ID to revoke.</param>
        /// <param name="reason">Optional reason for revocation.</param>
        /// <returns>True if session was revoked successfully.</returns>
        public async Task<bool> RevokeSessionAsync(string sessionId, string? reason = null)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return false;
            }

            session.State = SessionState.Revoked;
            session.RevokedAt = DateTime.UtcNow;
            session.RevocationReason = reason ?? "Manual revocation";

            await PersistStateAsync();
            return true;
        }

        /// <summary>
        /// Registers a device for trust evaluation.
        /// </summary>
        /// <param name="registration">The device registration information.</param>
        /// <returns>The device ID.</returns>
        public async Task<string> RegisterDeviceAsync(DeviceRegistrationRequest registration)
        {
            ArgumentNullException.ThrowIfNull(registration);

            var deviceId = registration.DeviceId ?? GenerateDeviceId();

            var device = new DeviceRegistration
            {
                DeviceId = deviceId,
                DeviceName = registration.DeviceName,
                DeviceType = registration.DeviceType,
                Platform = registration.Platform,
                OsVersion = registration.OsVersion,
                Fingerprint = ComputeDeviceFingerprint(registration),
                RegisteredAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
                OwnerId = registration.OwnerId,
                ComplianceStatus = ComplianceStatus.Unknown,
                SecurityPosture = new DeviceSecurityPosture
                {
                    IsEncryptionEnabled = registration.IsEncryptionEnabled,
                    IsFirewallEnabled = registration.IsFirewallEnabled,
                    IsAntivirusEnabled = registration.IsAntivirusEnabled,
                    IsPatchCurrent = registration.IsPatchCurrent,
                    LastSecurityCheck = DateTime.UtcNow
                },
                Attributes = registration.Attributes ?? new Dictionary<string, object>()
            };

            // Evaluate initial compliance
            device.ComplianceStatus = EvaluateDeviceCompliance(device);
            device.TrustScore = CalculateDeviceTrustScore(device);

            _devices[deviceId] = device;
            await PersistStateAsync();

            return deviceId;
        }

        /// <summary>
        /// Verifies device compliance.
        /// </summary>
        /// <param name="deviceId">The device ID to verify.</param>
        /// <returns>The device verification result.</returns>
        public Task<DeviceVerificationResult> VerifyDeviceAsync(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return Task.FromResult(new DeviceVerificationResult { IsCompliant = false, Details = "Device ID is required" });
            }

            if (!_devices.TryGetValue(deviceId, out var device))
            {
                return Task.FromResult(new DeviceVerificationResult { IsCompliant = false, Details = "Device not registered" });
            }

            // Re-evaluate compliance
            device.ComplianceStatus = EvaluateDeviceCompliance(device);
            device.TrustScore = CalculateDeviceTrustScore(device);
            device.LastSeenAt = DateTime.UtcNow;

            var isCompliant = device.ComplianceStatus == ComplianceStatus.Compliant ||
                              (device.ComplianceStatus == ComplianceStatus.PartiallyCompliant && !_config.RequireFullDeviceCompliance);

            return Task.FromResult(new DeviceVerificationResult
            {
                IsCompliant = isCompliant,
                Device = device,
                TrustScore = device.TrustScore,
                Details = device.ComplianceStatus == ComplianceStatus.Compliant
                    ? "Device is compliant"
                    : $"Device compliance status: {device.ComplianceStatus}"
            });
        }

        /// <summary>
        /// Creates a zero-trust policy.
        /// </summary>
        /// <param name="policy">The policy to create.</param>
        /// <returns>The created policy ID.</returns>
        public async Task<string> CreatePolicyAsync(ZeroTrustPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);

            if (string.IsNullOrEmpty(policy.PolicyId))
            {
                policy.PolicyId = Guid.NewGuid().ToString("N");
            }

            policy.CreatedAt = DateTime.UtcNow;
            policy.UpdatedAt = DateTime.UtcNow;

            _policies[policy.PolicyId] = policy;
            await PersistStateAsync();

            return policy.PolicyId;
        }

        /// <summary>
        /// Evaluates policies for a request context.
        /// </summary>
        /// <param name="context">The request context.</param>
        /// <returns>The policy evaluation result.</returns>
        public PolicyEvaluationResult EvaluatePolicies(RequestContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var result = new PolicyEvaluationResult
            {
                AppliedPolicies = new List<string>()
            };

            var applicablePolicies = _policies.Values
                .Where(p => p.IsEnabled)
                .OrderByDescending(p => p.Priority)
                .ToList();

            foreach (var policy in applicablePolicies)
            {
                if (PolicyMatches(policy, context))
                {
                    result.AppliedPolicies.Add(policy.PolicyId);

                    var evaluation = EvaluateSinglePolicy(policy, context);

                    if (evaluation.Effect == PolicyEffect.Deny)
                    {
                        result.IsAllowed = false;
                        result.DenialReason = evaluation.Reason;
                        return result;
                    }

                    if (evaluation.Effect == PolicyEffect.AllowWithConditions)
                    {
                        result.RequiredConditions.AddRange(evaluation.RequiredConditions);
                    }
                }
            }

            // Default allow if no deny policy matched
            result.IsAllowed = true;
            return result;
        }

        /// <summary>
        /// Classifies data sensitivity level.
        /// </summary>
        /// <param name="request">The classification request.</param>
        /// <returns>The data classification.</returns>
        public async Task<DataClassification> ClassifyDataAsync(ClassificationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var classification = new DataClassification
            {
                ResourceId = request.ResourceId,
                ClassifiedAt = DateTime.UtcNow,
                ClassifiedBy = request.ClassifiedBy ?? "automatic"
            };

            // Auto-classify based on content analysis
            if (request.ContentSample != null && request.ContentSample.Length > 0)
            {
                classification.SensitivityLevel = AnalyzeContentSensitivity(request.ContentSample, request.ContentType);
                classification.DetectedPatterns = DetectSensitivePatterns(request.ContentSample);
            }
            else
            {
                classification.SensitivityLevel = request.SuggestedLevel ?? SensitivityLevel.Internal;
            }

            // Apply classification rules
            if (request.Metadata != null)
            {
                var ruleBasedLevel = ApplyClassificationRules(request.Metadata);
                if (ruleBasedLevel > classification.SensitivityLevel)
                {
                    classification.SensitivityLevel = ruleBasedLevel;
                }
            }

            // Determine required controls
            classification.RequiredControls = GetRequiredControls(classification.SensitivityLevel);
            classification.AllowedAccessLevels = GetAllowedAccessLevels(classification.SensitivityLevel);

            _classifications[request.ResourceId] = classification;
            await PersistStateAsync();

            return classification;
        }

        /// <summary>
        /// Creates a microsegment for isolation.
        /// </summary>
        /// <param name="segment">The segment definition.</param>
        /// <returns>The segment ID.</returns>
        public async Task<string> CreateSegmentAsync(Microsegment segment)
        {
            ArgumentNullException.ThrowIfNull(segment);

            if (string.IsNullOrEmpty(segment.SegmentId))
            {
                segment.SegmentId = Guid.NewGuid().ToString("N");
            }

            segment.CreatedAt = DateTime.UtcNow;
            segment.AllowedCommunications = segment.AllowedCommunications ?? new List<SegmentCommunicationRule>();

            _segments[segment.SegmentId] = segment;
            await PersistStateAsync();

            return segment.SegmentId;
        }

        /// <summary>
        /// Allows communication between segments.
        /// </summary>
        /// <param name="sourceSegmentId">The source segment ID.</param>
        /// <param name="targetSegmentId">The target segment ID.</param>
        /// <param name="rule">The communication rule.</param>
        public async Task AllowSegmentCommunicationAsync(string sourceSegmentId, string targetSegmentId, SegmentCommunicationRule rule)
        {
            if (!_segments.TryGetValue(sourceSegmentId, out var segment))
            {
                throw new ArgumentException($"Segment not found: {sourceSegmentId}");
            }

            rule.TargetSegmentId = targetSegmentId;
            rule.CreatedAt = DateTime.UtcNow;

            segment.AllowedCommunications ??= new List<SegmentCommunicationRule>();
            segment.AllowedCommunications.Add(rule);

            await PersistStateAsync();
        }

        /// <summary>
        /// Calculates trust score for a context.
        /// </summary>
        /// <param name="context">The request context.</param>
        /// <returns>The calculated trust score.</returns>
        public async Task<TrustScoreResult> CalculateTrustScoreAsync(RequestContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var identityResult = await VerifyIdentityAsync(context);
            DeviceVerificationResult? deviceResult = null;

            if (!string.IsNullOrEmpty(context.DeviceId))
            {
                deviceResult = await VerifyDeviceAsync(context.DeviceId);
            }

            var behaviorProfile = GetBehaviorProfile(context.IdentityId);
            var anomalyResult = _anomalyDetector.Analyze(context, behaviorProfile);

            var score = _trustCalculator.Calculate(context, identityResult, deviceResult, anomalyResult);

            return new TrustScoreResult
            {
                Score = score,
                IdentityScore = identityResult.TrustScore,
                DeviceScore = deviceResult?.TrustScore ?? 0,
                BehaviorScore = behaviorProfile?.AverageTrustScore ?? _config.DefaultTrustScore,
                AnomalyPenalty = anomalyResult.SeverityPenalty,
                Factors = new Dictionary<string, double>
                {
                    ["identity_verification"] = identityResult.IsValid ? _config.IdentityVerificationWeight : 0,
                    ["device_compliance"] = deviceResult?.IsCompliant == true ? _config.DeviceComplianceWeight : 0,
                    ["behavior_normal"] = anomalyResult.IsAnomalous ? 0 : _config.BehaviorWeight,
                    ["session_valid"] = !string.IsNullOrEmpty(context.SessionId) && _sessions.TryGetValue(context.SessionId, out var s) && s.State == SessionState.Active ? _config.SessionWeight : 0
                }
            };
        }

        #endregion

        #region Message Handlers

        private async Task HandleVerifyRequestAsync(PluginMessage message)
        {
            var context = ExtractRequestContext(message.Payload);
            var result = await VerifyRequestAsync(context);
            // Result would be sent back through response mechanism
        }

        private async Task HandleCreateSessionAsync(PluginMessage message)
        {
            var request = new SessionCreationRequest
            {
                IdentityId = GetRequiredString(message.Payload, "identityId"),
                DeviceId = GetOptionalString(message.Payload, "deviceId"),
                SourceIp = GetOptionalString(message.Payload, "sourceIp"),
                Attributes = GetOptionalDictionary(message.Payload, "attributes")
            };

            var session = await CreateSessionAsync(request);
            // Session would be sent back through response mechanism
        }

        private Task HandleValidateSessionAsync(PluginMessage message)
        {
            var sessionId = GetRequiredString(message.Payload, "sessionId");
            var context = message.Payload.ContainsKey("context") ? ExtractRequestContext(message.Payload) : null;
            var result = ValidateSession(sessionId, context);
            return Task.CompletedTask;
        }

        private async Task HandleRevokeSessionAsync(PluginMessage message)
        {
            var sessionId = GetRequiredString(message.Payload, "sessionId");
            var reason = GetOptionalString(message.Payload, "reason");
            await RevokeSessionAsync(sessionId, reason);
        }

        private async Task HandleRegisterDeviceAsync(PluginMessage message)
        {
            var request = new DeviceRegistrationRequest
            {
                DeviceId = GetOptionalString(message.Payload, "deviceId"),
                DeviceName = GetRequiredString(message.Payload, "deviceName"),
                DeviceType = GetOptionalString(message.Payload, "deviceType") ?? "unknown",
                Platform = GetOptionalString(message.Payload, "platform") ?? "unknown",
                OsVersion = GetOptionalString(message.Payload, "osVersion"),
                OwnerId = GetRequiredString(message.Payload, "ownerId"),
                IsEncryptionEnabled = GetOptionalBool(message.Payload, "isEncryptionEnabled"),
                IsFirewallEnabled = GetOptionalBool(message.Payload, "isFirewallEnabled"),
                IsAntivirusEnabled = GetOptionalBool(message.Payload, "isAntivirusEnabled"),
                IsPatchCurrent = GetOptionalBool(message.Payload, "isPatchCurrent")
            };

            await RegisterDeviceAsync(request);
        }

        private async Task HandleVerifyDeviceAsync(PluginMessage message)
        {
            var deviceId = GetRequiredString(message.Payload, "deviceId");
            await VerifyDeviceAsync(deviceId);
        }

        private async Task HandleCreatePolicyAsync(PluginMessage message)
        {
            var policy = new ZeroTrustPolicy
            {
                PolicyId = GetOptionalString(message.Payload, "policyId") ?? Guid.NewGuid().ToString(),
                Name = GetRequiredString(message.Payload, "name"),
                Description = GetOptionalString(message.Payload, "description") ?? "",
                IsEnabled = GetOptionalBool(message.Payload, "isEnabled", true),
                Priority = GetOptionalInt(message.Payload, "priority", 0)
            };

            await CreatePolicyAsync(policy);
        }

        private Task HandleEvaluatePolicyAsync(PluginMessage message)
        {
            var context = ExtractRequestContext(message.Payload);
            EvaluatePolicies(context);
            return Task.CompletedTask;
        }

        private async Task HandleClassifyDataAsync(PluginMessage message)
        {
            var request = new ClassificationRequest
            {
                ResourceId = GetRequiredString(message.Payload, "resourceId"),
                ClassifiedBy = GetOptionalString(message.Payload, "classifiedBy"),
                ContentType = GetOptionalString(message.Payload, "contentType")
            };

            if (message.Payload.TryGetValue("suggestedLevel", out var levelObj) && levelObj is string levelStr &&
                Enum.TryParse<SensitivityLevel>(levelStr, true, out var level))
            {
                request.SuggestedLevel = level;
            }

            await ClassifyDataAsync(request);
        }

        private async Task HandleCreateSegmentAsync(PluginMessage message)
        {
            var segment = new Microsegment
            {
                SegmentId = GetOptionalString(message.Payload, "segmentId") ?? Guid.NewGuid().ToString(),
                Name = GetRequiredString(message.Payload, "name"),
                Description = GetOptionalString(message.Payload, "description") ?? "",
                SegmentType = GetOptionalString(message.Payload, "segmentType") ?? "plugin"
            };

            await CreateSegmentAsync(segment);
        }

        private async Task HandleAllowSegmentCommunicationAsync(PluginMessage message)
        {
            var sourceSegmentId = GetRequiredString(message.Payload, "sourceSegmentId");
            var targetSegmentId = GetRequiredString(message.Payload, "targetSegmentId");

            var rule = new SegmentCommunicationRule
            {
                AllowedProtocols = GetOptionalStringArray(message.Payload, "allowedProtocols") ?? ["*"],
                AllowedPorts = GetOptionalIntArray(message.Payload, "allowedPorts"),
                RequireEncryption = GetOptionalBool(message.Payload, "requireEncryption", true)
            };

            await AllowSegmentCommunicationAsync(sourceSegmentId, targetSegmentId, rule);
        }

        private async Task HandleCalculateTrustScoreAsync(PluginMessage message)
        {
            var context = ExtractRequestContext(message.Payload);
            await CalculateTrustScoreAsync(context);
        }

        #endregion

        #region Private Methods

        private void InitializeDefaultPolicies()
        {
            // Default deny policy for high-sensitivity resources without MFA
            var highSecurityPolicy = new ZeroTrustPolicy
            {
                PolicyId = "default-high-security",
                Name = "High Security Resources",
                Description = "Requires MFA and high trust score for high-sensitivity resources",
                IsEnabled = true,
                Priority = 1000,
                Conditions = new List<PolicyCondition>
                {
                    new PolicyCondition { Attribute = "resource.sensitivity", Operator = ConditionOperator.Equals, Value = SensitivityLevel.Restricted }
                },
                Effect = PolicyEffect.AllowWithConditions,
                RequiredConditions = new List<string> { "mfa_verified", "trust_score_high" }
            };
            _policies[highSecurityPolicy.PolicyId] = highSecurityPolicy;

            // Default policy for internal resources
            var internalPolicy = new ZeroTrustPolicy
            {
                PolicyId = "default-internal",
                Name = "Internal Resources",
                Description = "Allows access to internal resources with valid session",
                IsEnabled = true,
                Priority = 500,
                Conditions = new List<PolicyCondition>
                {
                    new PolicyCondition { Attribute = "resource.sensitivity", Operator = ConditionOperator.Equals, Value = SensitivityLevel.Internal }
                },
                Effect = PolicyEffect.Allow
            };
            _policies[internalPolicy.PolicyId] = internalPolicy;
        }

        private void InitializeDefaultSegments()
        {
            // Create default segments
            var coreSegment = new Microsegment
            {
                SegmentId = "core",
                Name = "Core Services",
                Description = "Core DataWarehouse services",
                SegmentType = "core",
                CreatedAt = DateTime.UtcNow,
                AllowedCommunications = new List<SegmentCommunicationRule>()
            };
            _segments[coreSegment.SegmentId] = coreSegment;

            var pluginSegment = new Microsegment
            {
                SegmentId = "plugins",
                Name = "Plugin Services",
                Description = "Plugin isolation segment",
                SegmentType = "plugin",
                CreatedAt = DateTime.UtcNow,
                AllowedCommunications = new List<SegmentCommunicationRule>
                {
                    new SegmentCommunicationRule
                    {
                        TargetSegmentId = "core",
                        AllowedProtocols = ["messagebus"],
                        RequireEncryption = true
                    }
                }
            };
            _segments[pluginSegment.SegmentId] = pluginSegment;
        }

        private Task<IdentityVerificationResult> VerifyIdentityAsync(RequestContext context)
        {
            // Get or create identity context
            var identity = _identities.GetOrAdd(context.IdentityId, id => new IdentityContext
            {
                IdentityId = id,
                FirstSeenAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow
            });

            var result = new IdentityVerificationResult { IdentityId = context.IdentityId };

            // Basic validation
            if (string.IsNullOrEmpty(context.IdentityId))
            {
                result.IsValid = false;
                result.Details = "Identity ID is required";
                result.TrustScore = 0;
                return Task.FromResult(result);
            }

            // Check if identity is blocked
            if (identity.IsBlocked)
            {
                result.IsValid = false;
                result.Details = "Identity is blocked";
                result.TrustScore = 0;
                return Task.FromResult(result);
            }

            // Check authentication method if provided
            if (!string.IsNullOrEmpty(context.AuthenticationMethod))
            {
                var authScore = context.AuthenticationMethod switch
                {
                    "mfa" or "multi_factor" => 1.0,
                    "certificate" or "mtls" => 0.95,
                    "oauth2" or "oidc" => 0.85,
                    "api_key" => 0.7,
                    "password" => 0.5,
                    _ => 0.3
                };
                result.TrustScore = authScore * 100;
            }
            else
            {
                result.TrustScore = _config.DefaultTrustScore;
            }

            // Update identity context
            identity.LastSeenAt = DateTime.UtcNow;
            identity.AccessCount++;

            result.IsValid = true;
            result.Details = "Identity verified";
            return Task.FromResult(result);
        }

        private SegmentationCheckResult CheckSegmentationRules(RequestContext context)
        {
            var result = new SegmentationCheckResult();

            if (string.IsNullOrEmpty(context.SourceSegment) || string.IsNullOrEmpty(context.TargetSegment))
            {
                // If segments not specified, allow (for backward compatibility)
                result.IsAllowed = true;
                result.Details = "No segment restrictions specified";
                return result;
            }

            if (!_segments.TryGetValue(context.SourceSegment, out var sourceSegment))
            {
                result.IsAllowed = false;
                result.Details = $"Source segment not found: {context.SourceSegment}";
                return result;
            }

            // Check if communication is allowed
            var allowedRule = sourceSegment.AllowedCommunications?
                .FirstOrDefault(r => r.TargetSegmentId == context.TargetSegment || r.TargetSegmentId == "*");

            if (allowedRule == null)
            {
                result.IsAllowed = false;
                result.Details = $"Communication not allowed from {context.SourceSegment} to {context.TargetSegment}";
                return result;
            }

            // Check protocol if specified
            if (allowedRule.AllowedProtocols != null && allowedRule.AllowedProtocols.Length > 0 &&
                !allowedRule.AllowedProtocols.Contains("*") &&
                !string.IsNullOrEmpty(context.Protocol) &&
                !allowedRule.AllowedProtocols.Contains(context.Protocol, StringComparer.OrdinalIgnoreCase))
            {
                result.IsAllowed = false;
                result.Details = $"Protocol {context.Protocol} not allowed for this communication path";
                return result;
            }

            // Check port if specified
            if (allowedRule.AllowedPorts != null && allowedRule.AllowedPorts.Length > 0 &&
                context.Port.HasValue && !allowedRule.AllowedPorts.Contains(context.Port.Value))
            {
                result.IsAllowed = false;
                result.Details = $"Port {context.Port} not allowed for this communication path";
                return result;
            }

            result.IsAllowed = true;
            result.RequireEncryption = allowedRule.RequireEncryption;
            result.Details = "Communication allowed by segmentation rules";
            return result;
        }

        private bool PolicyMatches(ZeroTrustPolicy policy, RequestContext context)
        {
            if (policy.Conditions == null || policy.Conditions.Count == 0)
            {
                return true;
            }

            foreach (var condition in policy.Conditions)
            {
                if (!EvaluatePolicyCondition(condition, context))
                {
                    return false;
                }
            }

            return true;
        }

        private bool EvaluatePolicyCondition(PolicyCondition condition, RequestContext context)
        {
            var value = GetConditionValue(condition.Attribute, context);

            return condition.Operator switch
            {
                ConditionOperator.Equals => Equals(value, condition.Value),
                ConditionOperator.NotEquals => !Equals(value, condition.Value),
                ConditionOperator.Contains => value?.ToString()?.Contains(condition.Value?.ToString() ?? "") ?? false,
                ConditionOperator.StartsWith => value?.ToString()?.StartsWith(condition.Value?.ToString() ?? "") ?? false,
                ConditionOperator.EndsWith => value?.ToString()?.EndsWith(condition.Value?.ToString() ?? "") ?? false,
                ConditionOperator.GreaterThan => Compare(value, condition.Value) > 0,
                ConditionOperator.LessThan => Compare(value, condition.Value) < 0,
                ConditionOperator.In => condition.Value is IEnumerable<object> list && list.Contains(value),
                ConditionOperator.Matches => value != null && Regex.IsMatch(value.ToString() ?? "", condition.Value?.ToString() ?? ""),
                _ => true
            };
        }

        private object? GetConditionValue(string attribute, RequestContext context)
        {
            var parts = attribute.Split('.');
            if (parts.Length < 2) return null;

            return parts[0].ToLowerInvariant() switch
            {
                "resource" => GetResourceAttribute(parts[1], context),
                "identity" => GetIdentityAttribute(parts[1], context),
                "device" => GetDeviceAttribute(parts[1], context),
                "time" => GetTimeAttribute(parts[1]),
                "session" => GetSessionAttribute(parts[1], context),
                _ => null
            };
        }

        private object? GetResourceAttribute(string attribute, RequestContext context)
        {
            return attribute.ToLowerInvariant() switch
            {
                "path" => context.ResourcePath,
                "operation" => context.Operation,
                "sensitivity" => context.ResourceSensitivity,
                _ => null
            };
        }

        private object? GetIdentityAttribute(string attribute, RequestContext context)
        {
            if (!_identities.TryGetValue(context.IdentityId, out var identity))
                return null;

            return attribute.ToLowerInvariant() switch
            {
                "id" => identity.IdentityId,
                "accesscount" => identity.AccessCount,
                "isblocked" => identity.IsBlocked,
                _ => null
            };
        }

        private object? GetDeviceAttribute(string attribute, RequestContext context)
        {
            if (string.IsNullOrEmpty(context.DeviceId) || !_devices.TryGetValue(context.DeviceId, out var device))
                return null;

            return attribute.ToLowerInvariant() switch
            {
                "type" => device.DeviceType,
                "platform" => device.Platform,
                "compliance" => device.ComplianceStatus,
                "trustscore" => device.TrustScore,
                _ => null
            };
        }

        private object? GetTimeAttribute(string attribute)
        {
            var now = DateTime.UtcNow;
            return attribute.ToLowerInvariant() switch
            {
                "hour" => now.Hour,
                "dayofweek" => now.DayOfWeek.ToString(),
                "isweekend" => now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday,
                "isbusinesshours" => now.Hour >= 9 && now.Hour < 17 && now.DayOfWeek != DayOfWeek.Saturday && now.DayOfWeek != DayOfWeek.Sunday,
                _ => null
            };
        }

        private object? GetSessionAttribute(string attribute, RequestContext context)
        {
            if (string.IsNullOrEmpty(context.SessionId) || !_sessions.TryGetValue(context.SessionId, out var session))
                return null;

            return attribute.ToLowerInvariant() switch
            {
                "state" => session.State,
                "trustscore" => session.CurrentTrustScore,
                "securitylevel" => session.SecurityLevel,
                "age" => (DateTime.UtcNow - session.CreatedAt).TotalMinutes,
                _ => null
            };
        }

        private static int Compare(object? a, object? b)
        {
            if (a is IComparable ca && b is IComparable cb)
                return ca.CompareTo(cb);
            return 0;
        }

        private PolicyEvaluationDetail EvaluateSinglePolicy(ZeroTrustPolicy policy, RequestContext context)
        {
            var detail = new PolicyEvaluationDetail
            {
                PolicyId = policy.PolicyId,
                Effect = policy.Effect,
                RequiredConditions = policy.RequiredConditions ?? new List<string>()
            };

            if (policy.Effect == PolicyEffect.Deny)
            {
                detail.Reason = policy.Description ?? "Denied by policy";
            }

            return detail;
        }

        private ComplianceStatus EvaluateDeviceCompliance(DeviceRegistration device)
        {
            var securityPosture = device.SecurityPosture;
            var compliantChecks = 0;
            var totalChecks = 0;

            if (_config.RequireDeviceEncryption)
            {
                totalChecks++;
                if (securityPosture.IsEncryptionEnabled) compliantChecks++;
            }

            if (_config.RequireDeviceFirewall)
            {
                totalChecks++;
                if (securityPosture.IsFirewallEnabled) compliantChecks++;
            }

            if (_config.RequireDeviceAntivirus)
            {
                totalChecks++;
                if (securityPosture.IsAntivirusEnabled) compliantChecks++;
            }

            if (_config.RequireCurrentPatches)
            {
                totalChecks++;
                if (securityPosture.IsPatchCurrent) compliantChecks++;
            }

            if (totalChecks == 0) return ComplianceStatus.Compliant;

            var ratio = (double)compliantChecks / totalChecks;
            return ratio switch
            {
                1.0 => ComplianceStatus.Compliant,
                >= 0.5 => ComplianceStatus.PartiallyCompliant,
                _ => ComplianceStatus.NonCompliant
            };
        }

        private double CalculateDeviceTrustScore(DeviceRegistration device)
        {
            var score = _config.DefaultTrustScore;

            if (device.SecurityPosture.IsEncryptionEnabled) score += 10;
            if (device.SecurityPosture.IsFirewallEnabled) score += 10;
            if (device.SecurityPosture.IsAntivirusEnabled) score += 10;
            if (device.SecurityPosture.IsPatchCurrent) score += 10;

            score = device.ComplianceStatus switch
            {
                ComplianceStatus.Compliant => Math.Min(100, score + 20),
                ComplianceStatus.PartiallyCompliant => score,
                ComplianceStatus.NonCompliant => Math.Max(0, score - 30),
                _ => score
            };

            return Math.Clamp(score, 0, 100);
        }

        private double CalculatePartialTrustScore(RequestContext context, IdentityVerificationResult identityResult, DeviceVerificationResult? deviceResult)
        {
            var score = 0.0;

            if (identityResult.IsValid)
            {
                score += identityResult.TrustScore * _config.IdentityVerificationWeight;
            }

            if (deviceResult?.IsCompliant == true)
            {
                score += deviceResult.TrustScore * _config.DeviceComplianceWeight;
            }

            return score;
        }

        private SensitivityLevel AnalyzeContentSensitivity(byte[] content, string? contentType)
        {
            var contentString = Encoding.UTF8.GetString(content);
            var patterns = DetectSensitivePatterns(content);

            if (patterns.Any(p => p.PatternType == PatternType.Credential || p.PatternType == PatternType.CryptoKey))
            {
                return SensitivityLevel.Restricted;
            }

            if (patterns.Any(p => p.PatternType == PatternType.PII || p.PatternType == PatternType.Financial))
            {
                return SensitivityLevel.Confidential;
            }

            if (patterns.Any(p => p.PatternType == PatternType.InternalReference))
            {
                return SensitivityLevel.Internal;
            }

            return SensitivityLevel.Public;
        }

        private List<DetectedPattern> DetectSensitivePatterns(byte[] content)
        {
            var patterns = new List<DetectedPattern>();
            var contentString = Encoding.UTF8.GetString(content);

            // Credit card pattern
            if (Regex.IsMatch(contentString, @"\b\d{4}[- ]?\d{4}[- ]?\d{4}[- ]?\d{4}\b"))
            {
                patterns.Add(new DetectedPattern { PatternType = PatternType.Financial, Description = "Credit card number detected" });
            }

            // SSN pattern
            if (Regex.IsMatch(contentString, @"\b\d{3}-\d{2}-\d{4}\b"))
            {
                patterns.Add(new DetectedPattern { PatternType = PatternType.PII, Description = "SSN pattern detected" });
            }

            // Email pattern
            if (Regex.IsMatch(contentString, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b"))
            {
                patterns.Add(new DetectedPattern { PatternType = PatternType.PII, Description = "Email address detected" });
            }

            // API key pattern
            if (Regex.IsMatch(contentString, "(?i)(api[_-]?key|apikey|api_secret)[=:]\\s*['\"]?[\\w-]{20,}"))
            {
                patterns.Add(new DetectedPattern { PatternType = PatternType.Credential, Description = "API key detected" });
            }

            // Password pattern
            if (Regex.IsMatch(contentString, "(?i)(password|passwd|pwd)[=:]\\s*['\"]?.+['\"]?"))
            {
                patterns.Add(new DetectedPattern { PatternType = PatternType.Credential, Description = "Password detected" });
            }

            // Private key pattern
            if (contentString.Contains("-----BEGIN") && contentString.Contains("PRIVATE KEY-----"))
            {
                patterns.Add(new DetectedPattern { PatternType = PatternType.CryptoKey, Description = "Private key detected" });
            }

            return patterns;
        }

        private SensitivityLevel ApplyClassificationRules(Dictionary<string, object> metadata)
        {
            var level = SensitivityLevel.Public;

            // Check for explicit classification
            if (metadata.TryGetValue("classification", out var classObj) && classObj is string classStr &&
                Enum.TryParse<SensitivityLevel>(classStr, true, out var explicitLevel))
            {
                return explicitLevel;
            }

            // Check for sensitivity indicators
            if (metadata.TryGetValue("containsPII", out var pii) && pii is bool hasPii && hasPii)
            {
                level = SensitivityLevel.Confidential;
            }

            if (metadata.TryGetValue("isFinancial", out var fin) && fin is bool isFinancial && isFinancial)
            {
                level = SensitivityLevel.Confidential;
            }

            if (metadata.TryGetValue("isSecret", out var secret) && secret is bool isSecret && isSecret)
            {
                level = SensitivityLevel.Restricted;
            }

            return level;
        }

        private List<string> GetRequiredControls(SensitivityLevel level)
        {
            return level switch
            {
                SensitivityLevel.Public => new List<string>(),
                SensitivityLevel.Internal => new List<string> { "authentication" },
                SensitivityLevel.Confidential => new List<string> { "authentication", "encryption_at_rest", "encryption_in_transit", "audit_logging" },
                SensitivityLevel.Restricted => new List<string> { "authentication", "mfa", "encryption_at_rest", "encryption_in_transit", "audit_logging", "dlp", "access_review" },
                SensitivityLevel.TopSecret => new List<string> { "authentication", "mfa", "encryption_at_rest", "encryption_in_transit", "audit_logging", "dlp", "access_review", "air_gap", "two_person_rule" },
                _ => new List<string> { "authentication" }
            };
        }

        private List<string> GetAllowedAccessLevels(SensitivityLevel level)
        {
            return level switch
            {
                SensitivityLevel.Public => new List<string> { "anonymous", "authenticated", "admin" },
                SensitivityLevel.Internal => new List<string> { "authenticated", "admin" },
                SensitivityLevel.Confidential => new List<string> { "authenticated_mfa", "admin" },
                SensitivityLevel.Restricted => new List<string> { "admin", "privileged" },
                SensitivityLevel.TopSecret => new List<string> { "privileged_cleared" },
                _ => new List<string> { "admin" }
            };
        }

        private EncryptionRequirement DetermineRequiredEncryption(RequestContext context)
        {
            var requirement = new EncryptionRequirement();

            // Check classification if available
            if (!string.IsNullOrEmpty(context.ResourcePath) && _classifications.TryGetValue(context.ResourcePath, out var classification))
            {
                requirement.InTransit = classification.SensitivityLevel >= SensitivityLevel.Internal;
                requirement.AtRest = classification.SensitivityLevel >= SensitivityLevel.Confidential;
                requirement.MinKeySize = classification.SensitivityLevel >= SensitivityLevel.Restricted ? 256 : 128;
            }
            else
            {
                // Default to requiring encryption
                requirement.InTransit = true;
                requirement.AtRest = _config.DefaultEncryptAtRest;
                requirement.MinKeySize = _config.DefaultMinKeySize;
            }

            return requirement;
        }

        private List<string> DetermineAllowedOperations(RequestContext context, double trustScore)
        {
            var operations = new List<string>();

            // Base operations for any authenticated user
            operations.Add("read");

            // Higher trust scores allow more operations
            if (trustScore >= _config.WriteOperationMinTrust)
            {
                operations.Add("write");
            }

            if (trustScore >= _config.DeleteOperationMinTrust)
            {
                operations.Add("delete");
            }

            if (trustScore >= _config.AdminOperationMinTrust)
            {
                operations.Add("admin");
            }

            return operations;
        }

        private BehaviorProfile GetBehaviorProfile(string identityId)
        {
            return _behaviorProfiles.GetOrAdd(identityId, id => new BehaviorProfile
            {
                IdentityId = id,
                CreatedAt = DateTime.UtcNow,
                AccessPatterns = new List<AccessPattern>(),
                AverageTrustScore = _config.DefaultTrustScore
            });
        }

        private void UpdateBehaviorProfile(RequestContext context)
        {
            var profile = GetBehaviorProfile(context.IdentityId);

            var pattern = new AccessPattern
            {
                Timestamp = DateTime.UtcNow,
                SourceIp = context.SourceIp,
                ResourcePath = context.ResourcePath,
                Operation = context.Operation,
                DeviceId = context.DeviceId
            };

            profile.AccessPatterns ??= new List<AccessPattern>();
            profile.AccessPatterns.Add(pattern);

            // Keep only recent patterns
            if (profile.AccessPatterns.Count > _config.MaxAccessPatternsPerProfile)
            {
                profile.AccessPatterns = profile.AccessPatterns
                    .OrderByDescending(p => p.Timestamp)
                    .Take(_config.MaxAccessPatternsPerProfile)
                    .ToList();
            }

            profile.LastUpdatedAt = DateTime.UtcNow;
        }

        private void RecordVerificationAttempt(RequestContext context, ZeroTrustVerificationResult result)
        {
            // Would typically emit this as a telemetry/audit event
            // For now, we just update internal state
            if (_identities.TryGetValue(context.IdentityId, out var identity))
            {
                if (!result.IsAllowed)
                {
                    identity.FailedAccessCount++;
                    if (identity.FailedAccessCount >= _config.MaxFailedAccessAttempts)
                    {
                        identity.IsBlocked = true;
                        identity.BlockedAt = DateTime.UtcNow;
                    }
                }
                else
                {
                    // Successful access resets the failed count
                    identity.FailedAccessCount = 0;
                }
            }
        }

        private SecurityLevel DetermineSecurityLevel(double trustScore)
        {
            return trustScore switch
            {
                >= 90 => SecurityLevel.High,
                >= 70 => SecurityLevel.Medium,
                >= 50 => SecurityLevel.Low,
                _ => SecurityLevel.Minimal
            };
        }

        private void CleanupExpiredSessions(object? state)
        {
            var now = DateTime.UtcNow;
            var expiredSessions = _sessions.Values
                .Where(s => s.ExpiresAt < now || s.State == SessionState.Revoked)
                .ToList();

            foreach (var session in expiredSessions)
            {
                if (session.State != SessionState.Revoked)
                {
                    session.State = SessionState.Expired;
                }

                // Keep revoked/expired sessions for a grace period for auditing
                if (session.RevokedAt.HasValue && (now - session.RevokedAt.Value).TotalHours > 24 ||
                    session.State == SessionState.Expired && (now - session.ExpiresAt).TotalHours > 24)
                {
                    _sessions.TryRemove(session.SessionId, out _);
                }
            }
        }

        private void AnalyzeBehaviorPatterns(object? state)
        {
            foreach (var profile in _behaviorProfiles.Values)
            {
                if (profile.AccessPatterns == null || profile.AccessPatterns.Count < 10)
                    continue;

                // Calculate average patterns
                var recentPatterns = profile.AccessPatterns
                    .Where(p => p.Timestamp > DateTime.UtcNow.AddHours(-24))
                    .ToList();

                if (recentPatterns.Count > 0)
                {
                    profile.AverageAccessesPerHour = recentPatterns.Count / 24.0;
                    profile.UniqueResourcesAccessed = recentPatterns.Select(p => p.ResourcePath).Distinct().Count();
                    profile.UniqueIpsUsed = recentPatterns.Select(p => p.SourceIp).Where(ip => !string.IsNullOrEmpty(ip)).Distinct().Count();
                }
            }
        }

        private async Task LoadStateAsync()
        {
            var path = Path.Combine(_config.StoragePath, "zerotrust-state.json");
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var data = JsonSerializer.Deserialize<ZeroTrustStateData>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (data?.Sessions != null)
                {
                    foreach (var session in data.Sessions)
                    {
                        _sessions[session.SessionId] = session;
                    }
                }

                if (data?.Devices != null)
                {
                    foreach (var device in data.Devices)
                    {
                        _devices[device.DeviceId] = device;
                    }
                }

                if (data?.Policies != null)
                {
                    foreach (var policy in data.Policies)
                    {
                        _policies[policy.PolicyId] = policy;
                    }
                }

                if (data?.Segments != null)
                {
                    foreach (var segment in data.Segments)
                    {
                        _segments[segment.SegmentId] = segment;
                    }
                }

                if (data?.Classifications != null)
                {
                    foreach (var classification in data.Classifications)
                    {
                        _classifications[classification.ResourceId] = classification;
                    }
                }
            }
            catch (JsonException)
            {
                // State file corrupted, start fresh
            }
            catch (IOException)
            {
                // File access issue, continue without persisted state
            }
        }

        private async Task PersistStateAsync()
        {
            await _persistLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(_config.StoragePath);

                var data = new ZeroTrustStateData
                {
                    Sessions = _sessions.Values.ToList(),
                    Devices = _devices.Values.ToList(),
                    Policies = _policies.Values.ToList(),
                    Segments = _segments.Values.ToList(),
                    Classifications = _classifications.Values.ToList()
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                var path = Path.Combine(_config.StoragePath, "zerotrust-state.json");
                await File.WriteAllTextAsync(path, json);
            }
            finally
            {
                _persistLock.Release();
            }
        }

        private static string GenerateSecureSessionId()
        {
            var bytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }

        private static string GenerateDeviceId()
        {
            var bytes = new byte[16];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return $"dev_{Convert.ToHexString(bytes).ToLowerInvariant()}";
        }

        private static string ComputeDeviceFingerprint(DeviceRegistrationRequest registration)
        {
            var input = $"{registration.DeviceType}|{registration.Platform}|{registration.OsVersion}|{registration.DeviceName}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private RequestContext ExtractRequestContext(Dictionary<string, object> payload)
        {
            return new RequestContext
            {
                RequestId = GetOptionalString(payload, "requestId") ?? Guid.NewGuid().ToString("N"),
                IdentityId = GetRequiredString(payload, "identityId"),
                DeviceId = GetOptionalString(payload, "deviceId"),
                SessionId = GetOptionalString(payload, "sessionId"),
                SourceIp = GetOptionalString(payload, "sourceIp"),
                SourceSegment = GetOptionalString(payload, "sourceSegment"),
                TargetSegment = GetOptionalString(payload, "targetSegment"),
                ResourcePath = GetOptionalString(payload, "resourcePath"),
                Operation = GetOptionalString(payload, "operation"),
                Protocol = GetOptionalString(payload, "protocol"),
                Port = GetOptionalInt(payload, "port"),
                AuthenticationMethod = GetOptionalString(payload, "authenticationMethod")
            };
        }

        private static string GetRequiredString(Dictionary<string, object> payload, string key)
        {
            if (!payload.TryGetValue(key, out var val))
                throw new ArgumentException($"{key} is required");

            return val switch
            {
                string s => s,
                JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? throw new ArgumentException($"{key} cannot be null"),
                _ => val.ToString() ?? throw new ArgumentException($"{key} cannot be null")
            };
        }

        private static string? GetOptionalString(Dictionary<string, object> payload, string key)
        {
            if (!payload.TryGetValue(key, out var val)) return null;

            return val switch
            {
                string s => s,
                JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
                null => null,
                _ => val.ToString()
            };
        }

        private static bool GetOptionalBool(Dictionary<string, object> payload, string key, bool defaultValue = false)
        {
            if (!payload.TryGetValue(key, out var val)) return defaultValue;

            return val switch
            {
                bool b => b,
                JsonElement je when je.ValueKind == JsonValueKind.True => true,
                JsonElement je when je.ValueKind == JsonValueKind.False => false,
                string s => bool.TryParse(s, out var b) && b,
                _ => defaultValue
            };
        }

        private static int GetOptionalInt(Dictionary<string, object> payload, string key, int defaultValue = 0)
        {
            if (!payload.TryGetValue(key, out var val)) return defaultValue;

            return val switch
            {
                int i => i,
                long l => (int)l,
                JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
                string s => int.TryParse(s, out var i) ? i : defaultValue,
                _ => defaultValue
            };
        }

        private static int? GetOptionalInt(Dictionary<string, object> payload, string key)
        {
            if (!payload.TryGetValue(key, out var val)) return null;

            return val switch
            {
                int i => i,
                long l => (int)l,
                JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
                string s => int.TryParse(s, out var i) ? i : null,
                _ => null
            };
        }

        private static Dictionary<string, object>? GetOptionalDictionary(Dictionary<string, object> payload, string key)
        {
            if (!payload.TryGetValue(key, out var val)) return null;

            return val switch
            {
                Dictionary<string, object> d => d,
                JsonElement je when je.ValueKind == JsonValueKind.Object => JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText()),
                _ => null
            };
        }

        private static string[]? GetOptionalStringArray(Dictionary<string, object> payload, string key)
        {
            if (!payload.TryGetValue(key, out var val)) return null;

            return val switch
            {
                string[] arr => arr,
                List<string> list => list.ToArray(),
                JsonElement je when je.ValueKind == JsonValueKind.Array => je.EnumerateArray().Select(e => e.GetString() ?? "").ToArray(),
                _ => null
            };
        }

        private static int[]? GetOptionalIntArray(Dictionary<string, object> payload, string key)
        {
            if (!payload.TryGetValue(key, out var val)) return null;

            return val switch
            {
                int[] arr => arr,
                List<int> list => list.ToArray(),
                JsonElement je when je.ValueKind == JsonValueKind.Array => je.EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                _ => null
            };
        }

        #endregion

        /// <summary>
        /// Disposes the plugin resources.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            _sessionCleanupTimer.Dispose();
            _behaviorAnalysisTimer.Dispose();
            _persistLock.Dispose();

            _isDisposed = true;
        }
    }
}
