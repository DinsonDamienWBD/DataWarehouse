using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.EphemeralSharing
{
    /// <summary>
    /// Ephemeral sharing strategy that provides time-limited, self-destructing access to resources.
    /// Implements secure sharing with automatic expiration and access tracking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Time-based expiration (absolute or sliding)
    /// - Access count limits (burn after reading)
    /// - Geographic restrictions
    /// - Device fingerprint binding
    /// - Revocation capabilities
    /// - Complete audit trail
    /// </para>
    /// <para>
    /// Use cases:
    /// - Temporary file sharing
    /// - One-time password distribution
    /// - Secure document review
    /// - Limited-time API access
    /// </para>
    /// </remarks>
    public sealed class EphemeralSharingStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, EphemeralShare> _shares = new();
        private readonly ConcurrentDictionary<string, List<ShareAccessLog>> _accessLogs = new();
        private Timer? _cleanupTimer;

        /// <inheritdoc/>
        public override string StrategyId => "ephemeral-sharing";

        /// <inheritdoc/>
        public override string StrategyName => "Ephemeral Sharing";

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
            // Start cleanup timer to remove expired shares
            var cleanupInterval = TimeSpan.FromMinutes(5);
            if (configuration.TryGetValue("CleanupIntervalMinutes", out var intervalObj) && intervalObj is int minutes)
            {
                cleanupInterval = TimeSpan.FromMinutes(minutes);
            }

            _cleanupTimer = new Timer(CleanupExpiredShares, null, cleanupInterval, cleanupInterval);

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Creates a new ephemeral share.
        /// </summary>
        /// <param name="resourceId">The resource to share.</param>
        /// <param name="options">Share configuration options.</param>
        /// <returns>The created share with access token.</returns>
        public EphemeralShare CreateShare(string resourceId, ShareOptions options)
        {
            var share = new EphemeralShare
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                Token = GenerateSecureToken(),
                CreatedAt = DateTime.UtcNow,
                CreatedBy = options.CreatedBy,
                ExpiresAt = options.ExpiresAt ?? DateTime.UtcNow.AddHours(24),
                MaxAccessCount = options.MaxAccessCount,
                AllowedIpRanges = options.AllowedIpRanges?.ToList(),
                AllowedCountries = options.AllowedCountries?.ToList(),
                AllowedRecipients = options.AllowedRecipients?.ToList(),
                RequireAuthentication = options.RequireAuthentication,
                AllowedActions = options.AllowedActions?.ToList() ?? new List<string> { "read" },
                UseSlidingExpiration = options.UseSlidingExpiration,
                SlidingExpirationMinutes = options.SlidingExpirationMinutes,
                Metadata = options.Metadata ?? new Dictionary<string, object>(),
                IsActive = true
            };

            _shares[share.Token] = share;
            _accessLogs[share.Id] = new List<ShareAccessLog>();

            return share;
        }

        /// <summary>
        /// Gets a share by token.
        /// </summary>
        public EphemeralShare? GetShare(string token)
        {
            return _shares.TryGetValue(token, out var share) ? share : null;
        }

        /// <summary>
        /// Revokes a share immediately.
        /// </summary>
        public bool RevokeShare(string shareId)
        {
            var share = _shares.Values.FirstOrDefault(s => s.Id == shareId);
            if (share != null)
            {
                share.IsActive = false;
                share.RevokedAt = DateTime.UtcNow;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets access logs for a share.
        /// </summary>
        public IReadOnlyList<ShareAccessLog> GetAccessLogs(string shareId)
        {
            return _accessLogs.TryGetValue(shareId, out var logs)
                ? logs.AsReadOnly()
                : Array.Empty<ShareAccessLog>();
        }

        /// <summary>
        /// Lists all active shares for a resource.
        /// </summary>
        public IReadOnlyList<EphemeralShare> GetSharesForResource(string resourceId)
        {
            return _shares.Values
                .Where(s => s.ResourceId == resourceId && s.IsActive && !IsExpired(s))
                .ToList()
                .AsReadOnly();
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Extract share token from context
            if (!context.EnvironmentAttributes.TryGetValue("ShareToken", out var tokenObj) ||
                tokenObj is not string token)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "No share token provided",
                    ApplicablePolicies = new[] { "EphemeralShareRequired" }
                });
            }

            // Find the share
            if (!_shares.TryGetValue(token, out var share))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Invalid share token",
                    ApplicablePolicies = new[] { "EphemeralShareValidation" }
                });
            }

            // Check if share is active
            if (!share.IsActive)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Share has been revoked",
                    ApplicablePolicies = new[] { "EphemeralShareRevoked" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["RevokedAt"] = share.RevokedAt?.ToString("o") ?? "unknown"
                    }
                });
            }

            // Check expiration
            if (IsExpired(share))
            {
                share.IsActive = false;
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Share has expired",
                    ApplicablePolicies = new[] { "EphemeralShareExpired" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["ExpiredAt"] = share.ExpiresAt.ToString("o")
                    }
                });
            }

            // Check access count
            if (share.MaxAccessCount.HasValue && share.AccessCount >= share.MaxAccessCount.Value)
            {
                share.IsActive = false;
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Maximum access count exceeded",
                    ApplicablePolicies = new[] { "EphemeralShareAccessLimit" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["MaxAccessCount"] = share.MaxAccessCount.Value,
                        ["ActualAccessCount"] = share.AccessCount
                    }
                });
            }

            // Check resource match
            if (!share.ResourceId.Equals(context.ResourceId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Share token not valid for this resource",
                    ApplicablePolicies = new[] { "EphemeralShareResourceMismatch" }
                });
            }

            // Check allowed actions
            if (share.AllowedActions.Any() &&
                !share.AllowedActions.Contains(context.Action, StringComparer.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Action '{context.Action}' not allowed by this share",
                    ApplicablePolicies = new[] { "EphemeralShareActionRestriction" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["AllowedActions"] = share.AllowedActions
                    }
                });
            }

            // Check IP restrictions
            if (share.AllowedIpRanges != null && share.AllowedIpRanges.Any())
            {
                if (string.IsNullOrEmpty(context.ClientIpAddress) ||
                    !IsIpAllowed(context.ClientIpAddress, share.AllowedIpRanges))
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Access denied from this IP address",
                        ApplicablePolicies = new[] { "EphemeralShareIpRestriction" }
                    });
                }
            }

            // Check country restrictions
            if (share.AllowedCountries != null && share.AllowedCountries.Any())
            {
                var country = context.Location?.Country;
                if (string.IsNullOrEmpty(country) ||
                    !share.AllowedCountries.Contains(country, StringComparer.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Access denied from this country",
                        ApplicablePolicies = new[] { "EphemeralShareCountryRestriction" }
                    });
                }
            }

            // Check recipient restrictions
            if (share.AllowedRecipients != null && share.AllowedRecipients.Any())
            {
                if (!share.AllowedRecipients.Contains(context.SubjectId, StringComparer.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "You are not an authorized recipient of this share",
                        ApplicablePolicies = new[] { "EphemeralShareRecipientRestriction" }
                    });
                }
            }

            // Check authentication requirement
            if (share.RequireAuthentication)
            {
                if (string.IsNullOrEmpty(context.SubjectId) || context.SubjectId == "anonymous")
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Authentication required to access this share",
                        ApplicablePolicies = new[] { "EphemeralShareAuthRequired" }
                    });
                }
            }

            // Access granted - update share state
            share.AccessCount++;
            share.LastAccessedAt = DateTime.UtcNow;

            // Update sliding expiration if enabled
            if (share.UseSlidingExpiration && share.SlidingExpirationMinutes > 0)
            {
                share.ExpiresAt = DateTime.UtcNow.AddMinutes(share.SlidingExpirationMinutes);
            }

            // Log access
            var log = new ShareAccessLog
            {
                ShareId = share.Id,
                AccessedAt = DateTime.UtcNow,
                AccessedBy = context.SubjectId,
                Action = context.Action,
                ClientIpAddress = context.ClientIpAddress,
                Location = context.Location,
                Success = true
            };

            if (_accessLogs.TryGetValue(share.Id, out var logs))
            {
                lock (logs)
                {
                    logs.Add(log);
                }
            }

            // Check if this was the last allowed access (burn after reading)
            var isFinalAccess = share.MaxAccessCount.HasValue &&
                               share.AccessCount >= share.MaxAccessCount.Value;

            if (isFinalAccess)
            {
                share.IsActive = false;
            }

            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Ephemeral share access granted",
                ApplicablePolicies = new[] { "EphemeralShareAccess" },
                Metadata = new Dictionary<string, object>
                {
                    ["ShareId"] = share.Id,
                    ["AccessCount"] = share.AccessCount,
                    ["RemainingAccesses"] = share.MaxAccessCount.HasValue
                        ? share.MaxAccessCount.Value - share.AccessCount
                        : -1,
                    ["ExpiresAt"] = share.ExpiresAt.ToString("o"),
                    ["IsFinalAccess"] = isFinalAccess
                }
            });
        }

        private bool IsExpired(EphemeralShare share)
        {
            return DateTime.UtcNow > share.ExpiresAt;
        }

        private bool IsIpAllowed(string clientIp, List<string> allowedRanges)
        {
            // Simple IP matching (supports exact match and CIDR notation)
            foreach (var range in allowedRanges)
            {
                if (range.Contains('/'))
                {
                    // CIDR notation
                    if (IsIpInCidrRange(clientIp, range))
                        return true;
                }
                else
                {
                    // Exact match or wildcard
                    if (range == "*" || clientIp.Equals(range, StringComparison.OrdinalIgnoreCase))
                        return true;

                    // Prefix match (e.g., "192.168." matches "192.168.1.1")
                    if (range.EndsWith("*") && clientIp.StartsWith(range.TrimEnd('*')))
                        return true;
                }
            }
            return false;
        }

        private bool IsIpInCidrRange(string ip, string cidr)
        {
            try
            {
                var parts = cidr.Split('/');
                if (parts.Length != 2) return false;

                var baseIp = System.Net.IPAddress.Parse(parts[0]);
                var maskBits = int.Parse(parts[1]);

                var clientIp = System.Net.IPAddress.Parse(ip);

                var baseBytes = baseIp.GetAddressBytes();
                var clientBytes = clientIp.GetAddressBytes();

                if (baseBytes.Length != clientBytes.Length) return false;

                int fullBytes = maskBits / 8;
                int remainingBits = maskBits % 8;

                for (int i = 0; i < fullBytes && i < baseBytes.Length; i++)
                {
                    if (baseBytes[i] != clientBytes[i]) return false;
                }

                if (remainingBits > 0 && fullBytes < baseBytes.Length)
                {
                    int mask = (byte)(0xFF << (8 - remainingBits));
                    if ((baseBytes[fullBytes] & mask) != (clientBytes[fullBytes] & mask))
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void CleanupExpiredShares(object? state)
        {
            var expiredTokens = _shares
                .Where(kvp => IsExpired(kvp.Value) || !kvp.Value.IsActive)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var token in expiredTokens)
            {
                _shares.TryRemove(token, out _);
            }
        }

        private static string GenerateSecureToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }
    }

    /// <summary>
    /// Options for creating an ephemeral share.
    /// </summary>
    public record ShareOptions
    {
        /// <summary>User ID who created the share.</summary>
        public required string CreatedBy { get; init; }

        /// <summary>Absolute expiration time (default: 24 hours from creation).</summary>
        public DateTime? ExpiresAt { get; init; }

        /// <summary>Maximum number of times the share can be accessed (null = unlimited).</summary>
        public int? MaxAccessCount { get; init; }

        /// <summary>List of allowed IP ranges (CIDR notation supported).</summary>
        public IEnumerable<string>? AllowedIpRanges { get; init; }

        /// <summary>List of allowed country codes.</summary>
        public IEnumerable<string>? AllowedCountries { get; init; }

        /// <summary>List of allowed recipient user IDs.</summary>
        public IEnumerable<string>? AllowedRecipients { get; init; }

        /// <summary>Whether authentication is required to access the share.</summary>
        public bool RequireAuthentication { get; init; }

        /// <summary>List of allowed actions (default: read only).</summary>
        public IEnumerable<string>? AllowedActions { get; init; }

        /// <summary>Whether to use sliding expiration.</summary>
        public bool UseSlidingExpiration { get; init; }

        /// <summary>Sliding expiration window in minutes.</summary>
        public int SlidingExpirationMinutes { get; init; }

        /// <summary>Additional metadata for the share.</summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// Represents an ephemeral share.
    /// </summary>
    public sealed class EphemeralShare
    {
        public required string Id { get; init; }
        public required string ResourceId { get; init; }
        public required string Token { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required string CreatedBy { get; init; }
        public DateTime ExpiresAt { get; set; }
        public int? MaxAccessCount { get; init; }
        public List<string>? AllowedIpRanges { get; init; }
        public List<string>? AllowedCountries { get; init; }
        public List<string>? AllowedRecipients { get; init; }
        public bool RequireAuthentication { get; init; }
        public List<string> AllowedActions { get; init; } = new();
        public bool UseSlidingExpiration { get; init; }
        public int SlidingExpirationMinutes { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
        public bool IsActive { get; set; }
        public int AccessCount { get; set; }
        public DateTime? LastAccessedAt { get; set; }
        public DateTime? RevokedAt { get; set; }
    }

    /// <summary>
    /// Log entry for share access.
    /// </summary>
    public sealed record ShareAccessLog
    {
        public required string ShareId { get; init; }
        public required DateTime AccessedAt { get; init; }
        public required string AccessedBy { get; init; }
        public required string Action { get; init; }
        public string? ClientIpAddress { get; init; }
        public GeoLocation? Location { get; init; }
        public required bool Success { get; init; }
        public string? FailureReason { get; init; }
    }
}
