using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Zero Trust verification strategy implementing "never trust, always verify" principles.
    /// Requires continuous authentication and authorization for every access request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero Trust principles implemented:
    /// - Verify explicitly: Always authenticate and authorize based on all available data points
    /// - Use least privilege: Limit access with Just-In-Time and Just-Enough-Access
    /// - Assume breach: Minimize blast radius and segment access
    /// </para>
    /// <para>
    /// Verification factors:
    /// - Identity verification
    /// - Device trust assessment
    /// - Network location analysis
    /// - Application health
    /// - Data sensitivity classification
    /// - Risk-based adaptive authentication
    /// </para>
    /// </remarks>
    public sealed class ZeroTrustStrategy : AccessControlStrategyBase
    {
        private readonly BoundedDictionary<string, DeviceTrustRecord> _deviceTrust = new BoundedDictionary<string, DeviceTrustRecord>(1000);
        private readonly BoundedDictionary<string, SessionRecord> _sessions = new BoundedDictionary<string, SessionRecord>(1000);
        private readonly BoundedDictionary<string, int> _failedAttempts = new BoundedDictionary<string, int>(1000);
        private int _maxFailedAttempts = 5;
        private TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30);
        private TimeSpan _lockoutDuration = TimeSpan.FromMinutes(15);
        private double _riskThresholdMultiplier = 1.0;
        private string? _trustBrokerEndpoint;
        private string? _policyEngineEndpoint;
        private int _devicePostureCheckIntervalMs = 60000; // 1 minute default

        /// <inheritdoc/>
        public override string StrategyId => "zero-trust";

        /// <inheritdoc/>
        public override string StrategyName => "Zero Trust Verification";

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
            if (configuration.TryGetValue("MaxFailedAttempts", out var maxAttempts) && maxAttempts is int max)
            {
                _maxFailedAttempts = max;
            }

            if (configuration.TryGetValue("SessionTimeoutMinutes", out var timeout) && timeout is int mins)
            {
                _sessionTimeout = TimeSpan.FromMinutes(mins);
            }

            if (configuration.TryGetValue("LockoutDurationMinutes", out var lockout) && lockout is int lockMins)
            {
                _lockoutDuration = TimeSpan.FromMinutes(lockMins);
            }

            if (configuration.TryGetValue("TrustBrokerEndpoint", out var broker) && broker is string brokerStr)
            {
                _trustBrokerEndpoint = brokerStr;
            }

            if (configuration.TryGetValue("PolicyEngineEndpoint", out var policyEngine) && policyEngine is string policyStr)
            {
                _policyEngineEndpoint = policyStr;
            }

            if (configuration.TryGetValue("DevicePostureCheckIntervalMs", out var intervalObj) && intervalObj is int interval)
            {
                _devicePostureCheckIntervalMs = interval;
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            // Validate trust broker endpoint if provided
            if (!string.IsNullOrWhiteSpace(_trustBrokerEndpoint))
            {
                if (!Uri.TryCreate(_trustBrokerEndpoint, UriKind.Absolute, out var brokerUri))
                {
                    throw new InvalidOperationException($"Invalid trust broker endpoint: {_trustBrokerEndpoint}");
                }

                if (brokerUri.Scheme != "https" && !brokerUri.Host.Contains("localhost"))
                {
                    throw new InvalidOperationException(
                        $"Trust broker endpoint must use HTTPS except for localhost: {_trustBrokerEndpoint}");
                }
            }

            // Validate policy engine endpoint if provided
            if (!string.IsNullOrWhiteSpace(_policyEngineEndpoint))
            {
                if (!Uri.TryCreate(_policyEngineEndpoint, UriKind.Absolute, out var policyUri))
                {
                    throw new InvalidOperationException($"Invalid policy engine endpoint: {_policyEngineEndpoint}");
                }
            }

            // Validate device posture check interval (1s to 5min)
            if (_devicePostureCheckIntervalMs < 1000 || _devicePostureCheckIntervalMs > 300000)
            {
                throw new ArgumentException(
                    $"Device posture check interval must be between 1000 and 300000ms, got: {_devicePostureCheckIntervalMs}");
            }

            // Validate max failed attempts (1-100)
            if (_maxFailedAttempts < 1 || _maxFailedAttempts > 100)
            {
                throw new ArgumentException($"Max failed attempts must be between 1 and 100, got: {_maxFailedAttempts}");
            }

            // Validate session timeout (1min to 24h)
            if (_sessionTimeout.TotalMinutes < 1 || _sessionTimeout.TotalHours > 24)
            {
                throw new ArgumentException(
                    $"Session timeout must be between 1 minute and 24 hours, got: {_sessionTimeout}");
            }

            return base.InitializeAsyncCore(cancellationToken);
        }

        protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            // Terminate all active sessions
            foreach (var sessionId in _sessions.Keys.ToArray())
            {
                TerminateSession(sessionId);
            }

            // Do NOT clear device trust records (persistent state)
            // Clear failed attempt records (transient state)
            _failedAttempts.Clear();

            // Close any persistent connections to trust broker (if applicable)
            // In production, this would disconnect from external policy engines
            await Task.CompletedTask;

            await base.ShutdownAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Gets the health status of the Zero Trust strategy with caching.
        /// </summary>
        public async Task<StrategyHealthCheckResult> GetHealthAsync(CancellationToken ct = default)
        {
            return await GetCachedHealthAsync(async (cancellationToken) =>
            {
                var details = new Dictionary<string, object>
                {
                    ["activeSessions"] = _sessions.Count(s => s.Value.IsActive),
                    ["totalSessions"] = _sessions.Count,
                    ["trustedDevices"] = _deviceTrust.Count,
                    ["lockedAccounts"] = _failedAttempts.Count(f => f.Value >= _maxFailedAttempts)
                };

                // Check trust broker reachability if configured
                if (!string.IsNullOrWhiteSpace(_trustBrokerEndpoint))
                {
                    try
                    {
                        // Production: would ping trust broker endpoint
                        // For now, just validate configuration
                        details["trustBrokerConfigured"] = true;
                        details["trustBrokerEndpoint"] = _trustBrokerEndpoint;
                        await Task.CompletedTask;
                    }
                    catch
                    {
                        return new StrategyHealthCheckResult(
                            IsHealthy: false,
                            Message: $"Trust broker {_trustBrokerEndpoint} unreachable",
                            Details: details);
                    }
                }

                return new StrategyHealthCheckResult(
                    IsHealthy: true,
                    Message: "Zero Trust strategy operational",
                    Details: details);
            }, TimeSpan.FromSeconds(60), ct);
        }

        /// <summary>
        /// Registers a trusted device.
        /// </summary>
        public DeviceTrustRecord RegisterDevice(string deviceId, string userId, DeviceInfo deviceInfo)
        {
            var record = new DeviceTrustRecord
            {
                DeviceId = deviceId,
                UserId = userId,
                DeviceInfo = deviceInfo,
                RegisteredAt = DateTime.UtcNow,
                TrustScore = CalculateInitialTrustScore(deviceInfo),
                IsManaged = deviceInfo.IsManaged,
                LastVerifiedAt = DateTime.UtcNow
            };

            _deviceTrust[deviceId] = record;
            return record;
        }

        /// <summary>
        /// Creates or updates a session.
        /// </summary>
        public SessionRecord CreateSession(string sessionId, string userId, string deviceId)
        {
            var session = new SessionRecord
            {
                SessionId = sessionId,
                UserId = userId,
                DeviceId = deviceId,
                CreatedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow,
                IsActive = true
            };

            _sessions[sessionId] = session;
            return session;
        }

        /// <summary>
        /// Validates and refreshes a session.
        /// </summary>
        public bool ValidateSession(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return false;

            if (!session.IsActive)
                return false;

            if (DateTime.UtcNow - session.LastActivityAt > _sessionTimeout)
            {
                session.IsActive = false;
                return false;
            }

            session.LastActivityAt = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// Terminates a session.
        /// </summary>
        public void TerminateSession(string sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.IsActive = false;
                session.TerminatedAt = DateTime.UtcNow;
            }
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("ztna.access_request");

            var verificationResults = new List<VerificationResult>();
            var overallRiskScore = 0.0;

            // 1. Identity Verification
            var identityResult = VerifyIdentity(context);
            verificationResults.Add(identityResult);
            if (!identityResult.Passed)
            {
                return Task.FromResult(CreateDenyDecision("Identity verification failed", identityResult, verificationResults));
            }

            // Device posture check (incremented when device verification happens)
            IncrementCounter("ztna.posture_check");

            // 2. Account Lockout Check
            var lockoutResult = CheckAccountLockout(context.SubjectId);
            verificationResults.Add(lockoutResult);
            if (!lockoutResult.Passed)
            {
                return Task.FromResult(CreateDenyDecision("Account is locked", lockoutResult, verificationResults));
            }

            // 3. Session Verification
            var sessionResult = VerifySession(context);
            verificationResults.Add(sessionResult);
            overallRiskScore += sessionResult.RiskContribution;

            // 4. Device Trust Verification
            var deviceResult = VerifyDevice(context);
            verificationResults.Add(deviceResult);
            overallRiskScore += deviceResult.RiskContribution;
            if (!deviceResult.Passed && IsHighSensitivityResource(context.ResourceId))
            {
                return Task.FromResult(CreateDenyDecision("Untrusted device accessing sensitive resource", deviceResult, verificationResults));
            }

            // 5. Network Location Verification
            var networkResult = VerifyNetworkLocation(context);
            verificationResults.Add(networkResult);
            overallRiskScore += networkResult.RiskContribution;

            // 6. Behavioral Analysis
            var behaviorResult = AnalyzeBehavior(context);
            verificationResults.Add(behaviorResult);
            overallRiskScore += behaviorResult.RiskContribution;

            // 7. Time-based Risk Assessment
            var timeResult = AssessTimeBasedRisk(context);
            verificationResults.Add(timeResult);
            overallRiskScore += timeResult.RiskContribution;

            // 8. Resource Sensitivity Check
            var sensitivityResult = CheckResourceSensitivity(context);
            verificationResults.Add(sensitivityResult);

            // Calculate final risk threshold
            var riskThreshold = GetRiskThreshold(context);

            if (overallRiskScore > riskThreshold)
            {
                RecordFailedAttempt(context.SubjectId);
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Risk score ({overallRiskScore:F2}) exceeds threshold ({riskThreshold:F2})",
                    ApplicablePolicies = new[] { "ZeroTrust.RiskThreshold" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["RiskScore"] = overallRiskScore,
                        ["Threshold"] = riskThreshold,
                        ["VerificationResults"] = verificationResults.Select(r => new { r.Name, r.Passed, r.RiskContribution }).ToList()
                    }
                });
            }

            // Clear failed attempts on successful access
            _failedAttempts.TryRemove(context.SubjectId, out _);

            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Zero Trust verification passed",
                ApplicablePolicies = new[] { "ZeroTrust.Verified" },
                Metadata = new Dictionary<string, object>
                {
                    ["RiskScore"] = overallRiskScore,
                    ["Threshold"] = riskThreshold,
                    ["VerificationResults"] = verificationResults.Select(r => new { r.Name, r.Passed, r.RiskContribution }).ToList(),
                    ["RequiresStepUp"] = overallRiskScore > riskThreshold * 0.7
                }
            });
        }

        private VerificationResult VerifyIdentity(AccessContext context)
        {
            if (string.IsNullOrEmpty(context.SubjectId) || context.SubjectId == "anonymous")
            {
                return new VerificationResult
                {
                    Name = "Identity",
                    Passed = false,
                    Message = "Anonymous access not allowed",
                    RiskContribution = 1.0
                };
            }

            return new VerificationResult
            {
                Name = "Identity",
                Passed = true,
                Message = "Identity verified",
                RiskContribution = 0
            };
        }

        private VerificationResult CheckAccountLockout(string userId)
        {
            if (_failedAttempts.TryGetValue(userId, out var attempts) && attempts >= _maxFailedAttempts)
            {
                return new VerificationResult
                {
                    Name = "AccountLockout",
                    Passed = false,
                    Message = $"Account locked after {attempts} failed attempts",
                    RiskContribution = 1.0
                };
            }

            return new VerificationResult
            {
                Name = "AccountLockout",
                Passed = true,
                Message = "Account not locked",
                RiskContribution = 0
            };
        }

        private VerificationResult VerifySession(AccessContext context)
        {
            if (context.EnvironmentAttributes.TryGetValue("SessionId", out var sessionIdObj) &&
                sessionIdObj is string sessionId)
            {
                if (ValidateSession(sessionId))
                {
                    return new VerificationResult
                    {
                        Name = "Session",
                        Passed = true,
                        Message = "Valid session",
                        RiskContribution = 0
                    };
                }

                return new VerificationResult
                {
                    Name = "Session",
                    Passed = false,
                    Message = "Invalid or expired session",
                    RiskContribution = 0.3
                };
            }

            return new VerificationResult
            {
                Name = "Session",
                Passed = true,
                Message = "No session required",
                RiskContribution = 0.1
            };
        }

        private VerificationResult VerifyDevice(AccessContext context)
        {
            if (context.EnvironmentAttributes.TryGetValue("DeviceId", out var deviceIdObj) &&
                deviceIdObj is string deviceId)
            {
                if (_deviceTrust.TryGetValue(deviceId, out var device))
                {
                    if (device.UserId != context.SubjectId)
                    {
                        return new VerificationResult
                        {
                            Name = "Device",
                            Passed = false,
                            Message = "Device registered to different user",
                            RiskContribution = 0.5
                        };
                    }

                    var daysSinceVerification = (DateTime.UtcNow - device.LastVerifiedAt).TotalDays;
                    var riskContribution = Math.Min(0.3, daysSinceVerification * 0.01);

                    return new VerificationResult
                    {
                        Name = "Device",
                        Passed = true,
                        Message = $"Trusted device (score: {device.TrustScore:F2})",
                        RiskContribution = 1.0 - device.TrustScore + riskContribution
                    };
                }

                return new VerificationResult
                {
                    Name = "Device",
                    Passed = false,
                    Message = "Unregistered device",
                    RiskContribution = 0.4
                };
            }

            return new VerificationResult
            {
                Name = "Device",
                Passed = true,
                Message = "No device verification required",
                RiskContribution = 0.2
            };
        }

        private VerificationResult VerifyNetworkLocation(AccessContext context)
        {
            var ip = context.ClientIpAddress;

            if (string.IsNullOrEmpty(ip))
            {
                return new VerificationResult
                {
                    Name = "Network",
                    Passed = true,
                    Message = "No IP information",
                    RiskContribution = 0.2
                };
            }

            // Check for internal network
            if (IsInternalNetwork(ip))
            {
                return new VerificationResult
                {
                    Name = "Network",
                    Passed = true,
                    Message = "Internal network",
                    RiskContribution = 0
                };
            }

            // Check for known VPN/corporate gateway
            if (context.EnvironmentAttributes.TryGetValue("IsVpn", out var isVpn) && isVpn is true)
            {
                return new VerificationResult
                {
                    Name = "Network",
                    Passed = true,
                    Message = "VPN connection",
                    RiskContribution = 0.1
                };
            }

            // External network
            return new VerificationResult
            {
                Name = "Network",
                Passed = true,
                Message = "External network",
                RiskContribution = 0.3
            };
        }

        private VerificationResult AnalyzeBehavior(AccessContext context)
        {
            var riskScore = 0.0;
            var issues = new List<string>();

            // Check for unusual action patterns
            if (context.Action.Equals("delete", StringComparison.OrdinalIgnoreCase) ||
                context.Action.Equals("bulk-export", StringComparison.OrdinalIgnoreCase))
            {
                riskScore += 0.2;
                issues.Add("High-risk action");
            }

            // Check for geographic anomaly
            if (context.EnvironmentAttributes.TryGetValue("PreviousCountry", out var prevCountry) &&
                context.Location?.Country != null &&
                prevCountry?.ToString() != context.Location.Country)
            {
                riskScore += 0.3;
                issues.Add("Geographic location change");
            }

            return new VerificationResult
            {
                Name = "Behavior",
                Passed = riskScore < 0.5,
                Message = issues.Any() ? string.Join(", ", issues) : "Normal behavior",
                RiskContribution = riskScore
            };
        }

        private VerificationResult AssessTimeBasedRisk(AccessContext context)
        {
            var hour = context.RequestTime.Hour;
            var day = context.RequestTime.DayOfWeek;

            // Weekend access
            if (day == DayOfWeek.Saturday || day == DayOfWeek.Sunday)
            {
                return new VerificationResult
                {
                    Name = "TimeAccess",
                    Passed = true,
                    Message = "Weekend access",
                    RiskContribution = 0.15
                };
            }

            // After hours
            if (hour < 6 || hour > 22)
            {
                return new VerificationResult
                {
                    Name = "TimeAccess",
                    Passed = true,
                    Message = "After-hours access",
                    RiskContribution = 0.2
                };
            }

            return new VerificationResult
            {
                Name = "TimeAccess",
                Passed = true,
                Message = "Business hours",
                RiskContribution = 0
            };
        }

        private VerificationResult CheckResourceSensitivity(AccessContext context)
        {
            var sensitivity = "normal";
            if (context.ResourceAttributes.TryGetValue("Sensitivity", out var sensObj))
            {
                sensitivity = sensObj?.ToString()?.ToLowerInvariant() ?? "normal";
            }

            return new VerificationResult
            {
                Name = "ResourceSensitivity",
                Passed = true,
                Message = $"Sensitivity: {sensitivity}",
                RiskContribution = sensitivity switch
                {
                    "critical" => 0.3,
                    "high" => 0.2,
                    "medium" => 0.1,
                    _ => 0
                }
            };
        }

        private double GetRiskThreshold(AccessContext context)
        {
            // Base threshold
            var threshold = 1.0;

            // Adjust based on resource sensitivity
            if (context.ResourceAttributes.TryGetValue("Sensitivity", out var sensObj))
            {
                var sensitivity = sensObj?.ToString()?.ToLowerInvariant() ?? "normal";
                threshold = sensitivity switch
                {
                    "critical" => 0.5,
                    "high" => 0.7,
                    "medium" => 0.85,
                    _ => 1.0
                };
            }

            return threshold;
        }

        private bool IsHighSensitivityResource(string resourceId)
        {
            // Could be enhanced with actual resource metadata lookup
            return resourceId.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                   resourceId.Contains("confidential", StringComparison.OrdinalIgnoreCase) ||
                   resourceId.Contains("pii", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsInternalNetwork(string ip)
        {
            return ip.StartsWith("10.") || ip.StartsWith("192.168.") ||
                   ip.StartsWith("172.16.") || ip.StartsWith("172.17.") ||
                   ip.StartsWith("172.18.") || ip.StartsWith("172.19.") ||
                   ip.StartsWith("127.") || ip == "::1";
        }

        private double CalculateInitialTrustScore(DeviceInfo deviceInfo)
        {
            var score = 0.5; // Base score

            if (deviceInfo.IsManaged) score += 0.3;
            if (deviceInfo.HasEncryption) score += 0.1;
            if (deviceInfo.HasAntivirus) score += 0.1;

            return Math.Min(1.0, score);
        }

        private void RecordFailedAttempt(string userId)
        {
            _failedAttempts.AddOrUpdate(userId, 1, (_, count) => count + 1);
        }

        private AccessDecision CreateDenyDecision(string reason, VerificationResult failedResult, List<VerificationResult> allResults)
        {
            return new AccessDecision
            {
                IsGranted = false,
                Reason = reason,
                ApplicablePolicies = new[] { $"ZeroTrust.{failedResult.Name}Failed" },
                Metadata = new Dictionary<string, object>
                {
                    ["FailedVerification"] = failedResult.Name,
                    ["FailureMessage"] = failedResult.Message,
                    ["AllVerifications"] = allResults.Select(r => new { r.Name, r.Passed, r.Message }).ToList()
                }
            };
        }
    }

    /// <summary>
    /// Device trust record.
    /// </summary>
    public sealed class DeviceTrustRecord
    {
        public required string DeviceId { get; init; }
        public required string UserId { get; init; }
        public required DeviceInfo DeviceInfo { get; init; }
        public required DateTime RegisteredAt { get; init; }
        public double TrustScore { get; set; }
        public bool IsManaged { get; init; }
        public DateTime LastVerifiedAt { get; set; }
    }

    /// <summary>
    /// Device information.
    /// </summary>
    public record DeviceInfo
    {
        public string? Platform { get; init; }
        public string? OsVersion { get; init; }
        public bool IsManaged { get; init; }
        public bool HasEncryption { get; init; }
        public bool HasAntivirus { get; init; }
        public string? Fingerprint { get; init; }
    }

    /// <summary>
    /// Session record.
    /// </summary>
    public sealed class SessionRecord
    {
        public required string SessionId { get; init; }
        public required string UserId { get; init; }
        public required string DeviceId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime LastActivityAt { get; set; }
        public bool IsActive { get; set; }
        public DateTime? TerminatedAt { get; set; }
    }

    /// <summary>
    /// Result of a verification step.
    /// </summary>
    internal record VerificationResult
    {
        public required string Name { get; init; }
        public required bool Passed { get; init; }
        public required string Message { get; init; }
        public required double RiskContribution { get; init; }
    }
}
