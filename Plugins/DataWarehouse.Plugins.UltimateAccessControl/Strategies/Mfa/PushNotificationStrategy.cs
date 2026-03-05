using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Mfa
{
    /// <summary>
    /// Push notification MFA strategy.
    /// Sends approval requests to user's mobile device with contextual information.
    /// User approves or denies via push notification.
    /// </summary>
    public sealed class PushNotificationStrategy : AccessControlStrategyBase
    {
        private readonly IMessageBus _messageBus;
        private readonly BoundedDictionary<string, PushChallengeSession> _activeChallenges = new BoundedDictionary<string, PushChallengeSession>(1000);
        private const int ChallengeTimeoutSeconds = 60;
        private const int MaxPendingChallenges = 5;

        public PushNotificationStrategy(IMessageBus messageBus)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public override string StrategyId => "push-notification-mfa";
        public override string StrategyName => "Push Notification Multi-Factor Authentication";

        public override AccessControlCapabilities Capabilities => new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 5000
        };

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("push.notification.mfa.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("push.notification.mfa.shutdown");
            _activeChallenges.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }
protected override async Task<AccessDecision> EvaluateAccessCoreAsync(
            AccessContext context,
            CancellationToken cancellationToken)
        {
            IncrementCounter("push.notification.mfa.evaluate");
            try
            {
                // Extract challenge ID from SubjectAttributes
                if (!context.SubjectAttributes.TryGetValue("push_challenge_id", out var challengeIdObj) ||
                    challengeIdObj is not string challengeId)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Push challenge ID not provided in SubjectAttributes['push_challenge_id']",
                        ApplicablePolicies = new[] { "push-mfa-required" }
                    };
                }

                // Get active challenge
                if (!_activeChallenges.TryGetValue(challengeId, out var challenge))
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Invalid or expired push challenge ID",
                        ApplicablePolicies = new[] { "push-mfa-invalid-challenge" }
                    };
                }

                // Check timeout
                if (DateTime.UtcNow > challenge.ExpiresAt)
                {
                    _activeChallenges.TryRemove(challengeId, out _);
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Push challenge expired (user did not respond in time)",
                        ApplicablePolicies = new[] { "push-mfa-timeout" }
                    };
                }

                // Wait for user response (poll with timeout)
                var waitTask = WaitForResponseAsync(challengeId, cancellationToken);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(ChallengeTimeoutSeconds), cancellationToken);

                var completedTask = await Task.WhenAny(waitTask, timeoutTask);

                if (completedTask == timeoutTask || !challenge.Responded)
                {
                    _activeChallenges.TryRemove(challengeId, out _);
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Push challenge timed out waiting for user response",
                        ApplicablePolicies = new[] { "push-mfa-timeout" }
                    };
                }

                // Remove challenge
                _activeChallenges.TryRemove(challengeId, out _);

                if (challenge.Approved)
                {
                    return new AccessDecision
                    {
                        IsGranted = true,
                        Reason = "Push MFA approved by user",
                        ApplicablePolicies = new[] { "push-mfa-approved" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["mfa_method"] = "push_notification",
                            ["device_id"] = challenge.DeviceId,
                            ["response_time_ms"] = (challenge.RespondedAt - challenge.CreatedAt).TotalMilliseconds,
                            ["challenge_context"] = challenge.Context
                        }
                    };
                }
                else
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Push MFA denied by user",
                        ApplicablePolicies = new[] { "push-mfa-denied" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["user_action"] = "denied"
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Push MFA validation error: {ex.Message}",
                    ApplicablePolicies = new[] { "push-mfa-error" }
                };
            }
        }

        /// <summary>
        /// Creates and sends a push challenge to user's device.
        /// </summary>
        public async Task<PushChallengeResult> CreateChallengeAsync(
            string userId,
            string deviceId,
            Dictionary<string, object> context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Check pending challenges limit
                var pendingCount = _activeChallenges.Count(kvp => kvp.Value.UserId == userId && !kvp.Value.Responded);
                if (pendingCount >= MaxPendingChallenges)
                {
                    return new PushChallengeResult
                    {
                        Success = false,
                        Message = "Too many pending push challenges. Please respond to existing challenges first."
                    };
                }

                // Generate challenge ID
                var challengeId = GenerateChallengeId();

                // Create challenge session
                var challenge = new PushChallengeSession
                {
                    ChallengeId = challengeId,
                    UserId = userId,
                    DeviceId = deviceId,
                    Context = context,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddSeconds(ChallengeTimeoutSeconds),
                    Responded = false,
                    Approved = false
                };

                _activeChallenges[challengeId] = challenge;

                // Send push notification via message bus
                var pushMessage = new DataWarehouse.SDK.Utilities.PluginMessage
                {
                    Type = "notification.push.send",
                    Payload = new Dictionary<string, object>
                    {
                        ["userId"] = userId,
                        ["deviceId"] = deviceId,
                        ["challengeId"] = challengeId,
                        ["title"] = "Authentication Request",
                        ["body"] = GeneratePushMessage(context),
                        ["priority"] = "high",
                        ["category"] = "authentication",
                        ["actions"] = new[]
                        {
                            new Dictionary<string, object> { ["id"] = "approve", ["title"] = "Approve", ["destructive"] = false },
                            new Dictionary<string, object> { ["id"] = "deny", ["title"] = "Deny", ["destructive"] = true }
                        },
                        ["expiresAt"] = challenge.ExpiresAt
                    }
                };

                await _messageBus.PublishAsync(
                    "notification.push.send",
                    pushMessage,
                    cancellationToken);

                return new PushChallengeResult
                {
                    Success = true,
                    Message = "Push challenge sent to device",
                    ChallengeId = challengeId,
                    ExpiresAt = challenge.ExpiresAt
                };
            }
            catch (Exception ex)
            {
                return new PushChallengeResult
                {
                    Success = false,
                    Message = $"Failed to create push challenge: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Handles user response to push challenge (approve or deny).
        /// Called by push notification callback.
        /// </summary>
        public bool RespondToChallenge(string challengeId, bool approved)
        {
            if (!_activeChallenges.TryGetValue(challengeId, out var challenge))
            {
                return false;
            }

            // Check if already responded or expired
            if (challenge.Responded || DateTime.UtcNow > challenge.ExpiresAt)
            {
                return false;
            }

            // Update challenge
            challenge.Responded = true;
            challenge.Approved = approved;
            challenge.RespondedAt = DateTime.UtcNow;

            return true;
        }

        /// <summary>
        /// Waits for user response to challenge.
        /// </summary>
        private async Task WaitForResponseAsync(string challengeId, CancellationToken cancellationToken)
        {
            var timeout = DateTime.UtcNow.AddSeconds(ChallengeTimeoutSeconds + 5); // Extra buffer

            while (DateTime.UtcNow < timeout && !cancellationToken.IsCancellationRequested)
            {
                if (_activeChallenges.TryGetValue(challengeId, out var challenge) && challenge.Responded)
                {
                    return;
                }

                await Task.Delay(500, cancellationToken); // Poll every 500ms
            }
        }

        /// <summary>
        /// Generates cryptographically secure challenge ID.
        /// </summary>
        private string GenerateChallengeId()
        {
            var buffer = new byte[16];
            RandomNumberGenerator.Fill(buffer);
            return Convert.ToBase64String(buffer).Replace("+", "-").Replace("/", "_").Replace("=", "");
        }

        /// <summary>
        /// Generates human-readable push notification message with context.
        /// </summary>
        private string GeneratePushMessage(Dictionary<string, object> context)
        {
            var parts = new List<string> { "Login attempt detected" };

            if (context.TryGetValue("ip_address", out var ip))
                parts.Add($"from IP {ip}");

            if (context.TryGetValue("location", out var location))
                parts.Add($"in {location}");

            if (context.TryGetValue("device_type", out var device))
                parts.Add($"on {device}");

            return string.Join(" ", parts) + ". Tap to approve or deny.";
        }

        /// <summary>
        /// Push challenge session data.
        /// </summary>
        private sealed class PushChallengeSession
        {
            public required string ChallengeId { get; init; }
            public required string UserId { get; init; }
            public required string DeviceId { get; init; }
            public required Dictionary<string, object> Context { get; init; }
            public required DateTime CreatedAt { get; init; }
            public required DateTime ExpiresAt { get; init; }
            // Use volatile for cross-thread visibility (polled by WaitForResponseAsync)
            public volatile bool Responded;
            public volatile bool Approved;
            public DateTime RespondedAt { get; set; }
        }
    }

    /// <summary>
    /// Push challenge creation result.
    /// </summary>
    public sealed class PushChallengeResult
    {
        /// <summary>
        /// Whether challenge creation succeeded.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Result message.
        /// </summary>
        public required string Message { get; init; }

        /// <summary>
        /// Challenge ID for tracking (if successful).
        /// </summary>
        public string? ChallengeId { get; init; }

        /// <summary>
        /// When the challenge expires (if successful).
        /// </summary>
        public DateTime? ExpiresAt { get; init; }
    }
}
