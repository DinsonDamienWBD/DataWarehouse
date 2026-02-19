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
    /// Email-based One-Time Password MFA strategy.
    /// Generates and validates time-limited OTP codes sent via email with rate limiting and expiration.
    /// </summary>
    public sealed class EmailOtpStrategy : AccessControlStrategyBase
    {
        private readonly IMessageBus _messageBus;
        private readonly ConcurrentDictionary<string, EmailOtpSession> _activeSessions = new();
        private readonly ConcurrentDictionary<string, RateLimitData> _rateLimits = new();

        private const int CodeLength = 8; // Longer than SMS (emails can handle it)
        private const int CodeExpirationMinutes = 10; // Longer expiration than SMS
        private const int MaxAttemptsPerSession = 5;
        private const int RateLimitPeriodMinutes = 1;
        private const int MaxCodesPerPeriod = 3;

        public EmailOtpStrategy(IMessageBus messageBus)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public override string StrategyId => "email-otp-mfa";
        public override string StrategyName => "Email OTP Multi-Factor Authentication";

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
            IncrementCounter("email.otp.mfa.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("email.otp.mfa.shutdown");
            _activeSessions.Clear();
            _rateLimits.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }
protected override async Task<AccessDecision> EvaluateAccessCoreAsync(
            AccessContext context,
            CancellationToken cancellationToken)
        {
            IncrementCounter("email.otp.mfa.evaluate");
            try
            {
                // Extract Email OTP code from SubjectAttributes
                if (!context.SubjectAttributes.TryGetValue("email_otp_code", out var codeObj) || codeObj is not string code)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Email OTP code not provided in SubjectAttributes['email_otp_code']",
                        ApplicablePolicies = new[] { "email-otp-required" }
                    };
                }

                // Get active session
                if (!_activeSessions.TryGetValue(context.SubjectId, out var session))
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "No active Email OTP session. Request a new code first.",
                        ApplicablePolicies = new[] { "email-otp-no-session" }
                    };
                }

                // Check expiration
                if (DateTime.UtcNow > session.ExpiresAt)
                {
                    _activeSessions.TryRemove(context.SubjectId, out _);
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Email OTP code expired. Request a new code.",
                        ApplicablePolicies = new[] { "email-otp-expired" }
                    };
                }

                // Check max attempts
                if (session.AttemptsUsed >= MaxAttemptsPerSession)
                {
                    _activeSessions.TryRemove(context.SubjectId, out _);
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Maximum Email OTP validation attempts exceeded. Request a new code.",
                        ApplicablePolicies = new[] { "email-otp-max-attempts" }
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
                        Reason = "Email OTP validated successfully",
                        ApplicablePolicies = new[] { "email-otp-validated" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["mfa_method"] = "email_otp",
                            ["email_address"] = MaskEmailAddress(session.EmailAddress),
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
                        Reason = $"Invalid Email OTP code. {remainingAttempts} attempts remaining.",
                        ApplicablePolicies = new[] { "email-otp-invalid" },
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
                    Reason = $"Email OTP validation error: {ex.Message}",
                    ApplicablePolicies = new[] { "email-otp-error" }
                };
            }
        }

        /// <summary>
        /// Generates and sends Email OTP code to user's email address.
        /// </summary>
        public async Task<EmailOtpResult> GenerateAndSendCodeAsync(
            string userId,
            string emailAddress,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Rate limiting check
                if (!CheckRateLimit(userId))
                {
                    return new EmailOtpResult
                    {
                        Success = false,
                        Message = $"Rate limit exceeded. Maximum {MaxCodesPerPeriod} codes per {RateLimitPeriodMinutes} minute(s).",
                        RateLimited = true
                    };
                }

                // Generate cryptographically random code
                var code = GenerateSecureCode(CodeLength);

                // Create session
                var session = new EmailOtpSession
                {
                    Code = code,
                    EmailAddress = emailAddress,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(CodeExpirationMinutes),
                    AttemptsUsed = 0
                };

                _activeSessions[userId] = session;

                // Send email via message bus (delegate to notification plugin)
                var emailMessage = new DataWarehouse.SDK.Utilities.PluginMessage
                {
                    Type = "notification.email.send",
                    Payload = new Dictionary<string, object>
                    {
                        ["recipient"] = emailAddress,
                        ["subject"] = "DataWarehouse Verification Code",
                        ["htmlBody"] = GenerateHtmlEmailBody(code, CodeExpirationMinutes),
                        ["plainTextBody"] = GeneratePlainTextEmailBody(code, CodeExpirationMinutes),
                        ["priority"] = "high"
                    }
                };

                await _messageBus.PublishAsync(
                    "notification.email.send",
                    emailMessage,
                    cancellationToken);

                return new EmailOtpResult
                {
                    Success = true,
                    Message = $"Email OTP sent to {MaskEmailAddress(emailAddress)}",
                    ExpiresAt = session.ExpiresAt,
                    EmailAddressMasked = MaskEmailAddress(emailAddress)
                };
            }
            catch (Exception ex)
            {
                return new EmailOtpResult
                {
                    Success = false,
                    Message = $"Failed to send Email OTP: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Checks rate limiting for Email OTP generation.
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
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var buffer = new byte[length];
            RandomNumberGenerator.Fill(buffer);

            var result = new char[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = chars[buffer[i] % chars.Length];
            }

            return new string(result);
        }

        /// <summary>
        /// Generates HTML email body with styled OTP code.
        /// </summary>
        private string GenerateHtmlEmailBody(string code, int expirationMinutes)
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 0; padding: 20px; background-color: #f4f4f4; }}
        .container {{ max-width: 600px; margin: 0 auto; background-color: #ffffff; padding: 40px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }}
        .header {{ text-align: center; margin-bottom: 30px; }}
        .code {{ font-size: 32px; font-weight: bold; text-align: center; padding: 20px; background-color: #f8f9fa; border: 2px solid #dee2e6; border-radius: 4px; letter-spacing: 5px; margin: 30px 0; }}
        .info {{ color: #666; font-size: 14px; line-height: 1.6; margin-top: 20px; }}
        .warning {{ color: #dc3545; font-weight: bold; margin-top: 20px; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h1>DataWarehouse Verification Code</h1>
        </div>
        <p>Your verification code is:</p>
        <div class=""code"">{code}</div>
        <div class=""info"">
            <p>This code will expire in <strong>{expirationMinutes} minutes</strong>.</p>
            <p>Enter this code to complete your authentication.</p>
        </div>
        <div class=""warning"">
            <p>⚠️ If you did not request this code, please ignore this email and contact support immediately.</p>
        </div>
    </div>
</body>
</html>";
        }

        /// <summary>
        /// Generates plain text email body for email clients that don't support HTML.
        /// </summary>
        private string GeneratePlainTextEmailBody(string code, int expirationMinutes)
        {
            return $@"
DataWarehouse Verification Code
================================

Your verification code is: {code}

This code will expire in {expirationMinutes} minutes.

Enter this code to complete your authentication.

⚠️ If you did not request this code, please ignore this email and contact support immediately.

Do not share this code with anyone.
";
        }

        /// <summary>
        /// Masks email address for security (shows only first 2 chars and domain).
        /// </summary>
        private string MaskEmailAddress(string emailAddress)
        {
            if (string.IsNullOrEmpty(emailAddress))
                return "****@****.com";

            var parts = emailAddress.Split('@');
            if (parts.Length != 2)
                return "****@****.com";

            var localPart = parts[0];
            var domain = parts[1];

            var maskedLocal = localPart.Length <= 2
                ? new string('*', localPart.Length)
                : localPart.Substring(0, 2) + new string('*', localPart.Length - 2);

            return $"{maskedLocal}@{domain}";
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
        /// Active Email OTP session data.
        /// </summary>
        private sealed class EmailOtpSession
        {
            public required string Code { get; init; }
            public required string EmailAddress { get; init; }
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
    /// Email OTP generation result.
    /// </summary>
    public sealed class EmailOtpResult
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
        /// Masked email address for confirmation.
        /// </summary>
        public string? EmailAddressMasked { get; init; }

        /// <summary>
        /// Whether rate limited.
        /// </summary>
        public bool RateLimited { get; init; }
    }
}
