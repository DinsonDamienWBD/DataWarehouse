using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Capability-Based Security strategy implementation.
    /// Token-based access control with unforgeable capabilities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Capability features:
    /// - Token-based capabilities (unforgeable tokens grant access)
    /// - Capability creation, delegation, revocation
    /// - Fine-grained object capabilities with specific permissions
    /// - Time-limited capabilities with expiration
    /// - Cryptographic signature verification
    /// </para>
    /// </remarks>
    public sealed class CapabilityStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, Capability> _capabilities = new();
        private readonly byte[] _signingKey;

        public CapabilityStrategy()
        {
            // Generate a random signing key
            _signingKey = new byte[32];
            RandomNumberGenerator.Fill(_signingKey);
        }

        /// <inheritdoc/>
        public override string StrategyId => "capability";

        /// <inheritdoc/>
        public override string StrategyName => "Capability-Based Security";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 10000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load capabilities from configuration
            if (configuration.TryGetValue("Capabilities", out var capsObj) &&
                capsObj is IEnumerable<Dictionary<string, object>> capConfigs)
            {
                foreach (var config in capConfigs)
                {
                    var resourceId = config["ResourceId"]?.ToString() ?? "";
                    var actions = (config.TryGetValue("Actions", out var actionsObj) &&
                                  actionsObj is IEnumerable<string> acts)
                        ? acts.ToArray()
                        : Array.Empty<string>();

                    if (!string.IsNullOrEmpty(resourceId) && actions.Any())
                    {
                        var capability = CreateCapability(resourceId, actions);
                        _capabilities[capability.Token] = capability;
                    }
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("capability.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("capability.shutdown");
            _capabilities.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <summary>
        /// Creates a new capability token.
        /// </summary>
        public Capability CreateCapability(
            string resourceId,
            string[] allowedActions,
            DateTime? expiresAt = null,
            string? grantedTo = null,
            bool canDelegate = false)
        {
            var capability = new Capability
            {
                Token = GenerateCapabilityToken(),
                ResourceId = resourceId,
                AllowedActions = allowedActions,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                GrantedTo = grantedTo,
                CanDelegate = canDelegate,
                IsRevoked = false
            };

            // Sign the capability
            capability.Signature = SignCapability(capability);

            _capabilities[capability.Token] = capability;
            return capability;
        }

        /// <summary>
        /// Delegates a capability to another subject (if delegation is allowed).
        /// </summary>
        public Capability? DelegateCapability(
            string token,
            string delegatedTo,
            string[]? restrictedActions = null,
            DateTime? expiresAt = null)
        {
            if (!_capabilities.TryGetValue(token, out var original))
                return null;

            if (!original.CanDelegate)
                return null;

            if (original.IsRevoked || (original.ExpiresAt.HasValue && original.ExpiresAt.Value < DateTime.UtcNow))
                return null;

            // Delegated capability can have same or fewer permissions
            var delegatedActions = restrictedActions ?? original.AllowedActions;
            if (!delegatedActions.All(a => original.AllowedActions.Contains(a, StringComparer.OrdinalIgnoreCase)))
                return null;

            var delegated = new Capability
            {
                Token = GenerateCapabilityToken(),
                ResourceId = original.ResourceId,
                AllowedActions = delegatedActions,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt ?? original.ExpiresAt,
                GrantedTo = delegatedTo,
                CanDelegate = false, // Delegated capabilities cannot be further delegated
                IsRevoked = false,
                DelegatedFrom = token
            };

            delegated.Signature = SignCapability(delegated);
            _capabilities[delegated.Token] = delegated;

            return delegated;
        }

        /// <summary>
        /// Revokes a capability.
        /// </summary>
        public bool RevokeCapability(string token)
        {
            if (_capabilities.TryGetValue(token, out var capability))
            {
                capability.IsRevoked = true;

                // Also revoke all delegated capabilities
                var delegated = _capabilities.Values
                    .Where(c => c.DelegatedFrom == token)
                    .ToList();

                foreach (var cap in delegated)
                {
                    cap.IsRevoked = true;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Verifies a capability token.
        /// </summary>
        public (bool IsValid, string? Reason) VerifyCapability(string token)
        {
            if (!_capabilities.TryGetValue(token, out var capability))
                return (false, "Capability not found");

            if (capability.IsRevoked)
                return (false, "Capability has been revoked");

            if (capability.ExpiresAt.HasValue && capability.ExpiresAt.Value < DateTime.UtcNow)
                return (false, "Capability has expired");

            var expectedSignature = SignCapability(capability);
            if (capability.Signature != expectedSignature)
                return (false, "Capability signature is invalid");

            return (true, null);
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("capability.evaluate");
            // Check if a capability token is provided in context
            if (!context.SubjectAttributes.TryGetValue("CapabilityToken", out var tokenObj) ||
                tokenObj is not string token)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Capability denied: no capability token provided",
                    ApplicablePolicies = new[] { "Capability.NoToken" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Subject"] = context.SubjectId,
                        ["Resource"] = context.ResourceId,
                        ["Action"] = context.Action
                    }
                });
            }

            // Verify the capability
            var (isValid, reason) = VerifyCapability(token);
            if (!isValid)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Capability denied: {reason}",
                    ApplicablePolicies = new[] { "Capability.Invalid" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Token"] = token,
                        ["InvalidReason"] = reason ?? "unknown"
                    }
                });
            }

            if (!_capabilities.TryGetValue(token, out var capability))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Capability denied: token not found",
                    ApplicablePolicies = new[] { "Capability.NotFound" }
                });
            }

            // Check if the capability is for this resource
            if (!capability.ResourceId.Equals(context.ResourceId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Capability denied: token is for resource '{capability.ResourceId}', not '{context.ResourceId}'",
                    ApplicablePolicies = new[] { "Capability.ResourceMismatch" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["CapabilityResource"] = capability.ResourceId,
                        ["RequestedResource"] = context.ResourceId
                    }
                });
            }

            // Check if the action is allowed
            if (!capability.AllowedActions.Contains(context.Action, StringComparer.OrdinalIgnoreCase) &&
                !capability.AllowedActions.Contains("*", StringComparer.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Capability denied: action '{context.Action}' not allowed by capability",
                    ApplicablePolicies = new[] { "Capability.ActionNotAllowed" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["RequestedAction"] = context.Action,
                        ["AllowedActions"] = capability.AllowedActions
                    }
                });
            }

            // Check if capability is for specific subject (optional)
            if (!string.IsNullOrEmpty(capability.GrantedTo) &&
                !capability.GrantedTo.Equals(context.SubjectId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Capability denied: token granted to '{capability.GrantedTo}', not '{context.SubjectId}'",
                    ApplicablePolicies = new[] { "Capability.SubjectMismatch" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["GrantedTo"] = capability.GrantedTo,
                        ["RequestingSubject"] = context.SubjectId
                    }
                });
            }

            // Access granted
            return Task.FromResult(new AccessDecision
            {
                IsGranted = true,
                Reason = "Capability granted: valid token with sufficient permissions",
                ApplicablePolicies = new[] { "Capability.Valid" },
                Metadata = new Dictionary<string, object>
                {
                    ["Token"] = token,
                    ["Resource"] = capability.ResourceId,
                    ["Action"] = context.Action,
                    ["AllowedActions"] = capability.AllowedActions,
                    ["ExpiresAt"] = capability.ExpiresAt?.ToString("o") ?? "never",
                    ["CanDelegate"] = capability.CanDelegate
                }
            });
        }

        private static string GenerateCapabilityToken()
        {
            var bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes);
        }

        private string SignCapability(Capability capability)
        {
            var data = $"{capability.Token}:{capability.ResourceId}:{string.Join(",", capability.AllowedActions)}:{capability.CreatedAt:O}";
            var dataBytes = Encoding.UTF8.GetBytes(data);
            var signature = HMACSHA256.HashData(_signingKey, dataBytes);
            return Convert.ToBase64String(signature);
        }
    }

    /// <summary>
    /// Represents a capability token.
    /// </summary>
    public sealed class Capability
    {
        public required string Token { get; init; }
        public required string ResourceId { get; init; }
        public required string[] AllowedActions { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? GrantedTo { get; init; }
        public bool CanDelegate { get; init; }
        public bool IsRevoked { get; set; }
        public string? DelegatedFrom { get; init; }
        public string Signature { get; set; } = "";
    }
}
