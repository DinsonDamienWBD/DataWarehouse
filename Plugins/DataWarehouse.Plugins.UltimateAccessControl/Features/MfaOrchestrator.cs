using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Features
{
    /// <summary>
    /// Multi-Factor Authentication orchestrator with adaptive MFA and trusted device management.
    /// </summary>
    public sealed class MfaOrchestrator
    {
        private readonly ConcurrentDictionary<string, List<MfaMethod>> _userMethods = new();
        private readonly ConcurrentDictionary<string, TrustedDevice> _trustedDevices = new();
        private readonly ConcurrentDictionary<string, MfaChallenge> _activeChallenges = new();

        /// <summary>
        /// Determines required MFA factors based on risk score.
        /// </summary>
        public Task<MfaRequirement> DetermineRequiredMfaAsync(
            string userId,
            double riskScore,
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            var isTrusted = _trustedDevices.TryGetValue($"{userId}:{deviceId}", out var device) &&
                            device.TrustedUntil > DateTime.UtcNow;

            var requiredFactors = riskScore switch
            {
                >= 80 => 3, // Critical risk - require 3 factors
                >= 60 => 2, // High risk - require 2 factors
                >= 30 => isTrusted ? 0 : 1, // Medium risk - skip if trusted device
                _ => 0 // Low risk - no MFA required
            };

            var availableMethods = _userMethods.TryGetValue(userId, out var methods)
                ? methods.Where(m => m.IsEnabled).ToList()
                : new List<MfaMethod>();

            return Task.FromResult(new MfaRequirement
            {
                RequiredFactors = requiredFactors,
                AvailableMethods = availableMethods,
                IsTrustedDevice = isTrusted,
                RiskScore = riskScore,
                Reason = GetMfaReason(riskScore, isTrusted)
            });
        }

        /// <summary>
        /// Initiates MFA challenge.
        /// </summary>
        public Task<MfaChallenge> InitiateChallengeAsync(
            string userId,
            MfaMethodType methodType,
            CancellationToken cancellationToken = default)
        {
            var challengeId = Guid.NewGuid().ToString("N");
            var challenge = new MfaChallenge
            {
                ChallengeId = challengeId,
                UserId = userId,
                MethodType = methodType,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                Status = ChallengeStatus.Pending,
                Code = GenerateCode(methodType)
            };

            _activeChallenges[challengeId] = challenge;

            return Task.FromResult(challenge);
        }

        /// <summary>
        /// Verifies MFA challenge response.
        /// </summary>
        public Task<bool> VerifyChallengeAsync(
            string challengeId,
            string response,
            CancellationToken cancellationToken = default)
        {
            if (!_activeChallenges.TryGetValue(challengeId, out var challenge))
                return Task.FromResult(false);

            if (DateTime.UtcNow > challenge.ExpiresAt)
            {
                challenge.Status = ChallengeStatus.Expired;
                return Task.FromResult(false);
            }

            var isValid = challenge.Code.Equals(response, StringComparison.Ordinal);
            challenge.Status = isValid ? ChallengeStatus.Verified : ChallengeStatus.Failed;
            challenge.VerifiedAt = DateTime.UtcNow;

            return Task.FromResult(isValid);
        }

        /// <summary>
        /// Registers device as trusted.
        /// </summary>
        public Task RegisterTrustedDeviceAsync(
            string userId,
            string deviceId,
            TimeSpan trustDuration,
            CancellationToken cancellationToken = default)
        {
            var device = new TrustedDevice
            {
                UserId = userId,
                DeviceId = deviceId,
                TrustedAt = DateTime.UtcNow,
                TrustedUntil = DateTime.UtcNow.Add(trustDuration)
            };

            _trustedDevices[$"{userId}:{deviceId}"] = device;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Registers MFA method for user.
        /// </summary>
        public Task RegisterMethodAsync(string userId, MfaMethod method, CancellationToken cancellationToken = default)
        {
            var methods = _userMethods.GetOrAdd(userId, _ => new List<MfaMethod>());
            methods.Add(method);
            return Task.CompletedTask;
        }

        private static string GenerateCode(MfaMethodType methodType)
        {
            if (methodType == MfaMethodType.Totp)
            {
                // Use cryptographically secure RNG for MFA codes
                var code = RandomNumberGenerator.GetInt32(100000, 1000000);
                return code.ToString("D6");
            }
            else
            {
                // Use cryptographically secure random bytes for non-TOTP codes
                var bytes = RandomNumberGenerator.GetBytes(4);
                return Convert.ToHexString(bytes);
            }
        }

        private string GetMfaReason(double riskScore, bool isTrusted)
        {
            return riskScore switch
            {
                >= 80 => "Critical risk - multi-factor authentication required",
                >= 60 => "High risk - two-factor authentication required",
                >= 30 when !isTrusted => "New or untrusted device - authentication required",
                _ => "Authentication not required"
            };
        }
    }

    #region Supporting Types

    public sealed class MfaRequirement
    {
        public required int RequiredFactors { get; init; }
        public required List<MfaMethod> AvailableMethods { get; init; }
        public required bool IsTrustedDevice { get; init; }
        public required double RiskScore { get; init; }
        public required string Reason { get; init; }
    }

    public sealed class MfaMethod
    {
        public required string MethodId { get; init; }
        public required MfaMethodType Type { get; init; }
        public required string DisplayName { get; init; }
        public required bool IsEnabled { get; init; }
        public string? Identifier { get; init; } // Phone, email, etc.
    }

    public enum MfaMethodType
    {
        Totp,
        Sms,
        Email,
        Push,
        Biometric
    }

    public sealed class MfaChallenge
    {
        public required string ChallengeId { get; init; }
        public required string UserId { get; init; }
        public required MfaMethodType MethodType { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public required ChallengeStatus Status { get; set; }
        public required string Code { get; init; }
        public DateTime? VerifiedAt { get; set; }
    }

    public enum ChallengeStatus
    {
        Pending,
        Verified,
        Failed,
        Expired
    }

    public sealed class TrustedDevice
    {
        public required string UserId { get; init; }
        public required string DeviceId { get; init; }
        public required DateTime TrustedAt { get; init; }
        public required DateTime TrustedUntil { get; init; }
    }

    #endregion
}
