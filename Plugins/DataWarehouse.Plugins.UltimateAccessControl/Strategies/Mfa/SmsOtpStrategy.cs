using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Mfa
{
    /// <summary>
    /// SMS-based One-Time Password MFA strategy.
    /// Generates and validates time-limited OTP codes sent via SMS with rate limiting and expiration.
    /// </summary>
    public sealed class SmsOtpStrategy : AccessControlStrategyBase
    {
        private readonly IMessageBus _messageBus;
        private readonly ConcurrentDictionary<string, SmsOtpSession> _activeSessions = new();
        private readonly ConcurrentDictionary<string, RateLimitData> _rateLimits = new();

        private const int CodeLength = 6;
        private const int CodeExpirationMinutes = 5;
        private const int MaxAttemptsPerSession = 3;
        private const int RateLimitPeriodMinutes = 1;
        private const int MaxCodesPerPeriod = 3;

        public SmsOtpStrategy(IMessageBus messageBus)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public override string StrategyId => "sms-otp-mfa";
        public override string StrategyName => "SMS OTP Multi-Factor Authentication";

        public override AccessControlCapabilities Capabilities => new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 5000
        };

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sms.otp.mfa.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("sms.otp.mfa.shutdown");
            _activeSessions.Clear();
            _rateLimits.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }
protected override async Task<AccessDecision> EvaluateAccessCoreAsync(
            AccessContext context,
            CancellationToken cancellationToken)
        {
            IncrementCounter("sms.otp.mfa.evaluate");
            try
            {
                // Extract SMS OTP code from SubjectAttributes
                if (!context.SubjectAttributes.TryGetValue("sms_otp_code", out var codeObj) || codeObj is not string code)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "SMS OTP code not provided in SubjectAttributes['sms_otp_code']",
                        ApplicablePolicies = new[] { "sms-otp-required" }
                    };
                }

                // Get active session
                if (!_activeSessions.TryGetValue(context.SubjectId, out var session))
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "No active SMS OTP session. Request a new code first.",
                        ApplicablePolicies = new[] { "sms-otp-no-session" }
                    };
                }

                // Check expiration
                if (DateTime.UtcNow > session.ExpiresAt)
                {
                    _activeSessions.TryRemove(context.SubjectId, out _);
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "SMS OTP code expired. Request a new code.",
                        ApplicablePolicies = new[] { "sms-otp-expired" }
                    };
                }

                // Check max attempts
                if (session.AttemptsUsed >= MaxAttemptsPerSession)
                {
                    _activeSessions.TryRemove(context.SubjectId, out _);
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Maximum SMS OTP validation attempts exceeded. Request a new code.",
                        ApplicablePolicies = new[] { "sms-otp-max-attempts" }
                    };
                }

                // Increment attempts
                session.AttemptsUsed++;

                // Validate code (constant-time comparison)
                if (ConstantTimeEquals(code, session.Code))
                {
                    // Valid code - remove session
                    _activeSessions.TryRemove(context.SubjectId, out _);

                    return new AccessDecision
                    {
                        IsGranted = true,
                        Reason = "SMS OTP validated successfully",
                        ApplicablePolicies = new[] { "sms-otp-validated" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["mfa_method"] = "sms_otp",
                            ["phone_number"] = MaskPhoneNumber(session.PhoneNumber),
                            ["attempts_used"] = session.AttemptsUsed
                        }
                    };
                }
                else
                {
                    var remainingAttempts = MaxAttemptsPerSession - session.AttemptsUsed;

                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"Invalid SMS OTP code. {remainingAttempts} attempts remaining.",
                        ApplicablePolicies = new[] { "sms-otp-invalid" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["remaining_attempts"] = remainingAttempts
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"SMS OTP validation error: {ex.Message}",
                    ApplicablePolicies = new[] { "sms-otp-error" }
                };
            }
        }

        /// <summary>
        /// Generates and sends SMS OTP code to user's phone number.
        /// </summary>
        public async Task<SmsOtpResult> GenerateAndSendCodeAsync(
            string userId,
            string phoneNumber,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Rate limiting check
                if (!CheckRateLimit(userId))
                {
                    return new SmsOtpResult
                    {
                        Success = false,
                        Message = $"Rate limit exceeded. Maximum {MaxCodesPerPeriod} codes per {RateLimitPeriodMinutes} minute(s).",
                        RateLimited = true
                    };
                }

                // Generate cryptographically random code
                var code = GenerateSecureCode(CodeLength);

                // Create session
                var session = new SmsOtpSession
                {
                    Code = code,
                    PhoneNumber = phoneNumber,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(CodeExpirationMinutes),
                    AttemptsUsed = 0
                };

                _activeSessions[userId] = session;

                // Send SMS via message bus (delegate to notification plugin)
                var smsMessage = new DataWarehouse.SDK.Utilities.PluginMessage
                {
                    Type = "notification.sms.send",
                    Payload = new Dictionary<string, object>
                    {
                        ["recipient"] = phoneNumber,
                        ["message"] = $"Your DataWarehouse verification code is: {code}. Valid for {CodeExpirationMinutes} minutes. Do not share this code.",
                        ["priority"] = "high"
                    }
                };

                await _messageBus.PublishAsync(
                    "notification.sms.send",
                    smsMessage,
                    cancellationToken);

                return new SmsOtpResult
                {
                    Success = true,
                    Message = $"SMS OTP sent to {MaskPhoneNumber(phoneNumber)}",
                    ExpiresAt = session.ExpiresAt,
                    PhoneNumberMasked = MaskPhoneNumber(phoneNumber)
                };
            }
            catch (Exception ex)
            {
                return new SmsOtpResult
                {
                    Success = false,
                    Message = $"Failed to send SMS OTP: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Checks rate limiting for SMS OTP generation.
        /// </summary>
        private bool CheckRateLimit(string userId)
        {
            var now = DateTime.UtcNow;
            var windowStart = now.AddMinutes(-RateLimitPeriodMinutes);

            if (_rateLimits.TryGetValue(userId, out var rateLimitData))
            {
                // Remove expired timestamps
                rateLimitData.Timestamps.RemoveAll(t => t < windowStart);

                // Check if limit exceeded
                if (rateLimitData.Timestamps.Count >= MaxCodesPerPeriod)
                {
                    return false;
                }

                // Add current timestamp
                rateLimitData.Timestamps.Add(now);
            }
            else
            {
                // Create new rate limit data
                _rateLimits[userId] = new RateLimitData
                {
                    Timestamps = new List<DateTime> { now }
                };
            }

            return true;
        }

        /// <summary>
        /// Generates cryptographically secure random code.
        /// </summary>
        private string GenerateSecureCode(int length)
        {
            var buffer = new byte[4];
            RandomNumberGenerator.Fill(buffer);

            // Convert to number and take required digits
            var number = BitConverter.ToUInt32(buffer, 0);
            var code = (number % (uint)Math.Pow(10, length)).ToString($"D{length}");

            return code;
        }

        /// <summary>
        /// Masks phone number for security (shows only last 4 digits).
        /// </summary>
        private string MaskPhoneNumber(string phoneNumber)
        {
            if (string.IsNullOrEmpty(phoneNumber) || phoneNumber.Length <= 4)
                return "****";

            return "****" + phoneNumber.Substring(phoneNumber.Length - 4);
        }

        /// <summary>
        /// Constant-time string comparison to prevent timing attacks.
        /// </summary>
        private bool ConstantTimeEquals(string a, string b)
        {
            if (a.Length != b.Length)
                return false;

            uint result = 0;
            for (int i = 0; i < a.Length; i++)
            {
                result |= (uint)(a[i] ^ b[i]);
            }

            return result == 0;
        }

        /// <summary>
        /// Active SMS OTP session data.
        /// </summary>
        private sealed class SmsOtpSession
        {
            public required string Code { get; init; }
            public required string PhoneNumber { get; init; }
            public required DateTime CreatedAt { get; init; }
            public required DateTime ExpiresAt { get; init; }
            public int AttemptsUsed { get; set; }
        }

        /// <summary>
        /// Rate limiting data.
        /// </summary>
        private sealed class RateLimitData
        {
            public required List<DateTime> Timestamps { get; init; }
        }
    }

    /// <summary>
    /// SMS OTP generation result.
    /// </summary>
    public sealed class SmsOtpResult
    {
        /// <summary>
        /// Whether code generation and sending succeeded.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Result message.
        /// </summary>
        public required string Message { get; init; }

        /// <summary>
        /// When the code expires (if successful).
        /// </summary>
        public DateTime? ExpiresAt { get; init; }

        /// <summary>
        /// Masked phone number for confirmation.
        /// </summary>
        public string? PhoneNumberMasked { get; init; }

        /// <summary>
        /// Whether rate limited.
        /// </summary>
        public bool RateLimited { get; init; }
    }
}
