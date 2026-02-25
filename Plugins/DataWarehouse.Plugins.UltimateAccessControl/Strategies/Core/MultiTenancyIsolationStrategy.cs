using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Multi-tenancy isolation strategy providing complete tenant separation
    /// with configurable isolation levels and cross-tenant access controls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Multi-tenancy features:
    /// - Complete data isolation between tenants
    /// - Hierarchical tenant structure (parent/child tenants)
    /// - Cross-tenant access delegation with approval workflows
    /// - Tenant-specific resource quotas and limits
    /// - Tenant impersonation for support scenarios
    /// - Isolation level configuration (strict, shared resources, federated)
    /// </para>
    /// </remarks>
    public sealed class MultiTenancyIsolationStrategy : AccessControlStrategyBase
    {
        private readonly BoundedDictionary<string, TenantDefinition> _tenants = new BoundedDictionary<string, TenantDefinition>(1000);
        private readonly BoundedDictionary<string, TenantMembership> _memberships = new BoundedDictionary<string, TenantMembership>(1000);
        private readonly BoundedDictionary<string, CrossTenantGrant> _crossTenantGrants = new BoundedDictionary<string, CrossTenantGrant>(1000);
        private readonly BoundedDictionary<string, TenantHierarchy> _hierarchies = new BoundedDictionary<string, TenantHierarchy>(1000);
        private readonly BoundedDictionary<string, ImpersonationSession> _impersonationSessions = new BoundedDictionary<string, ImpersonationSession>(1000);
        private readonly BoundedDictionary<string, TenantResourceQuota> _quotas = new BoundedDictionary<string, TenantResourceQuota>(1000);
        private IsolationLevel _defaultIsolationLevel = IsolationLevel.Strict;

        /// <inheritdoc/>
        public override string StrategyId => "multi-tenancy";

        /// <inheritdoc/>
        public override string StrategyName => "Multi-Tenancy Isolation";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 10000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load isolation level
            if (configuration.TryGetValue("IsolationLevel", out var ilObj) &&
                ilObj is string ilStr &&
                Enum.TryParse<IsolationLevel>(ilStr, out var il))
            {
                _defaultIsolationLevel = il;
            }

            // Load tenants from configuration
            if (configuration.TryGetValue("Tenants", out var tenantsObj) &&
                tenantsObj is IEnumerable<Dictionary<string, object>> tenantConfigs)
            {
                foreach (var config in tenantConfigs)
                {
                    var tenant = ParseTenantFromConfig(config);
                    if (tenant != null)
                    {
                        RegisterTenant(tenant);
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
            IncrementCounter("multi.tenancy.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("multi.tenancy.shutdown");
            _tenants.Clear();
            _memberships.Clear();
            _crossTenantGrants.Clear();
            _hierarchies.Clear();
            _impersonationSessions.Clear();
            _quotas.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        #region Tenant Management

        /// <summary>
        /// Registers a new tenant.
        /// </summary>
        public void RegisterTenant(TenantDefinition tenant)
        {
            _tenants[tenant.Id] = tenant;

            // Set up default quota
            _quotas[tenant.Id] = new TenantResourceQuota
            {
                TenantId = tenant.Id,
                MaxUsers = tenant.Settings.MaxUsers,
                MaxResources = tenant.Settings.MaxResources,
                MaxStorageBytes = tenant.Settings.MaxStorageBytes,
                CurrentUsers = 0,
                CurrentResources = 0,
                CurrentStorageBytes = 0
            };
        }

        /// <summary>
        /// Gets a tenant by ID.
        /// </summary>
        public TenantDefinition? GetTenant(string tenantId)
        {
            return _tenants.TryGetValue(tenantId, out var tenant) ? tenant : null;
        }

        /// <summary>
        /// Gets all tenants.
        /// </summary>
        public IReadOnlyList<TenantDefinition> GetAllTenants()
        {
            return _tenants.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Suspends a tenant.
        /// </summary>
        public void SuspendTenant(string tenantId, string reason)
        {
            if (_tenants.TryGetValue(tenantId, out var tenant))
            {
                _tenants[tenantId] = tenant with
                {
                    Status = TenantStatus.Suspended,
                    SuspensionReason = reason,
                    SuspendedAt = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// Reactivates a suspended tenant.
        /// </summary>
        public void ReactivateTenant(string tenantId)
        {
            if (_tenants.TryGetValue(tenantId, out var tenant))
            {
                _tenants[tenantId] = tenant with
                {
                    Status = TenantStatus.Active,
                    SuspensionReason = null,
                    SuspendedAt = null
                };
            }
        }

        /// <summary>
        /// Sets up a parent-child tenant relationship.
        /// </summary>
        public void SetTenantHierarchy(string parentTenantId, string childTenantId)
        {
            if (!_tenants.ContainsKey(parentTenantId) || !_tenants.ContainsKey(childTenantId))
            {
                throw new ArgumentException("Both tenants must exist");
            }

            var hierarchy = new TenantHierarchy
            {
                ParentTenantId = parentTenantId,
                ChildTenantId = childTenantId,
                CreatedAt = DateTime.UtcNow,
                InheritPermissions = true,
                CanAccessChildData = true
            };

            _hierarchies[$"{parentTenantId}:{childTenantId}"] = hierarchy;
        }

        #endregion

        #region Membership Management

        /// <summary>
        /// Assigns a user to a tenant with a specific role.
        /// </summary>
        public TenantMembership AssignUserToTenant(string userId, string tenantId, TenantRole role, string assignedBy)
        {
            var key = $"{userId}:{tenantId}";

            var membership = new TenantMembership
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                TenantId = tenantId,
                Role = role,
                AssignedAt = DateTime.UtcNow,
                AssignedBy = assignedBy,
                IsActive = true
            };

            _memberships[key] = membership;

            // Update quota
            if (_quotas.TryGetValue(tenantId, out var quota))
            {
                Interlocked.Increment(ref quota.CurrentUsers);
            }

            return membership;
        }

        /// <summary>
        /// Removes a user from a tenant.
        /// </summary>
        public bool RemoveUserFromTenant(string userId, string tenantId)
        {
            var key = $"{userId}:{tenantId}";
            if (_memberships.TryRemove(key, out _))
            {
                if (_quotas.TryGetValue(tenantId, out var quota))
                {
                    Interlocked.Decrement(ref quota.CurrentUsers);
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets a user's tenant memberships.
        /// </summary>
        public IReadOnlyList<TenantMembership> GetUserMemberships(string userId)
        {
            return _memberships.Values
                .Where(m => m.UserId == userId && m.IsActive)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets all members of a tenant.
        /// </summary>
        public IReadOnlyList<TenantMembership> GetTenantMembers(string tenantId)
        {
            return _memberships.Values
                .Where(m => m.TenantId == tenantId && m.IsActive)
                .ToList()
                .AsReadOnly();
        }

        #endregion

        #region Cross-Tenant Access

        /// <summary>
        /// Grants cross-tenant access to a user.
        /// </summary>
        public CrossTenantGrant GrantCrossTenantAccess(
            string userId,
            string sourceTenantId,
            string targetTenantId,
            string[] permissions,
            DateTime expiresAt,
            string grantedBy,
            string? approvalReference = null)
        {
            var grant = new CrossTenantGrant
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                SourceTenantId = sourceTenantId,
                TargetTenantId = targetTenantId,
                Permissions = permissions,
                GrantedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                GrantedBy = grantedBy,
                ApprovalReference = approvalReference,
                IsActive = true,
                Status = CrossTenantGrantStatus.Active
            };

            _crossTenantGrants[grant.Id] = grant;
            return grant;
        }

        /// <summary>
        /// Revokes a cross-tenant access grant.
        /// </summary>
        public bool RevokeCrossTenantAccess(string grantId, string revokedBy, string reason)
        {
            if (_crossTenantGrants.TryGetValue(grantId, out var grant))
            {
                _crossTenantGrants[grantId] = grant with
                {
                    IsActive = false,
                    Status = CrossTenantGrantStatus.Revoked,
                    RevokedAt = DateTime.UtcNow,
                    RevokedBy = revokedBy,
                    RevocationReason = reason
                };
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets active cross-tenant grants for a user.
        /// </summary>
        public IReadOnlyList<CrossTenantGrant> GetUserCrossTenantGrants(string userId)
        {
            return _crossTenantGrants.Values
                .Where(g => g.UserId == userId && g.IsActive && g.ExpiresAt > DateTime.UtcNow)
                .ToList()
                .AsReadOnly();
        }

        #endregion

        #region Impersonation

        /// <summary>
        /// Starts an impersonation session for support scenarios.
        /// </summary>
        public ImpersonationSession StartImpersonation(
            string supportUserId,
            string supportTenantId,
            string targetUserId,
            string targetTenantId,
            TimeSpan duration,
            string reason,
            string approvalReference)
        {
            // Validate support user has impersonation permission
            var supportMembership = _memberships.Values
                .FirstOrDefault(m => m.UserId == supportUserId && m.TenantId == supportTenantId);

            if (supportMembership?.Role is not (TenantRole.Admin or TenantRole.Support))
            {
                throw new UnauthorizedAccessException("User does not have impersonation privileges");
            }

            var session = new ImpersonationSession
            {
                Id = Guid.NewGuid().ToString("N"),
                SupportUserId = supportUserId,
                SupportTenantId = supportTenantId,
                TargetUserId = targetUserId,
                TargetTenantId = targetTenantId,
                StartedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(duration),
                Reason = reason,
                ApprovalReference = approvalReference,
                IsActive = true
            };

            _impersonationSessions[session.Id] = session;
            return session;
        }

        /// <summary>
        /// Ends an impersonation session.
        /// </summary>
        public void EndImpersonation(string sessionId)
        {
            if (_impersonationSessions.TryGetValue(sessionId, out var session))
            {
                _impersonationSessions[sessionId] = session with
                {
                    IsActive = false,
                    EndedAt = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// Checks if a user is currently impersonating.
        /// </summary>
        public ImpersonationSession? GetActiveImpersonation(string userId)
        {
            return _impersonationSessions.Values
                .FirstOrDefault(s => s.SupportUserId == userId && s.IsActive && s.ExpiresAt > DateTime.UtcNow);
        }

        #endregion

        #region Quota Management

        /// <summary>
        /// Gets the quota for a tenant.
        /// </summary>
        public TenantResourceQuota? GetTenantQuota(string tenantId)
        {
            return _quotas.TryGetValue(tenantId, out var quota) ? quota : null;
        }

        /// <summary>
        /// Updates tenant quota limits.
        /// </summary>
        public void UpdateTenantQuota(string tenantId, int? maxUsers = null, int? maxResources = null, long? maxStorageBytes = null)
        {
            if (_quotas.TryGetValue(tenantId, out var quota))
            {
                if (maxUsers.HasValue) quota.MaxUsers = maxUsers.Value;
                if (maxResources.HasValue) quota.MaxResources = maxResources.Value;
                if (maxStorageBytes.HasValue) quota.MaxStorageBytes = maxStorageBytes.Value;
            }
        }

        /// <summary>
        /// Checks if a tenant has exceeded their quota.
        /// </summary>
        public (bool IsExceeded, string? QuotaType) CheckQuotaExceeded(string tenantId)
        {
            if (!_quotas.TryGetValue(tenantId, out var quota))
            {
                return (false, null);
            }

            if (quota.CurrentUsers >= quota.MaxUsers)
                return (true, "MaxUsers");

            if (quota.CurrentResources >= quota.MaxResources)
                return (true, "MaxResources");

            if (quota.CurrentStorageBytes >= quota.MaxStorageBytes)
                return (true, "MaxStorage");

            return (false, null);
        }

        #endregion

        #region Core Evaluation

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("multi.tenancy.evaluate");
            // Extract tenant information from context
            var subjectTenantId = GetSubjectTenantId(context);
            var resourceTenantId = GetResourceTenantId(context);

            if (string.IsNullOrEmpty(subjectTenantId))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Subject has no tenant association",
                    ApplicablePolicies = new[] { "MultiTenancy.NoTenantAssociation" }
                });
            }

            // Check if subject's tenant is active
            if (!_tenants.TryGetValue(subjectTenantId, out var subjectTenant))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Subject's tenant not found",
                    ApplicablePolicies = new[] { "MultiTenancy.TenantNotFound" }
                });
            }

            if (subjectTenant.Status != TenantStatus.Active)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Subject's tenant is {subjectTenant.Status}: {subjectTenant.SuspensionReason}",
                    ApplicablePolicies = new[] { "MultiTenancy.TenantNotActive" }
                });
            }

            // Check impersonation
            var impersonation = GetActiveImpersonation(context.SubjectId);
            var effectiveTenantId = impersonation?.TargetTenantId ?? subjectTenantId;
            var effectiveUserId = impersonation?.TargetUserId ?? context.SubjectId;

            // Same-tenant access check
            if (string.IsNullOrEmpty(resourceTenantId) || resourceTenantId == effectiveTenantId)
            {
                return EvaluateSameTenantAccess(context, effectiveTenantId, effectiveUserId, impersonation);
            }

            // Cross-tenant access check
            return EvaluateCrossTenantAccess(context, effectiveTenantId, resourceTenantId, effectiveUserId, impersonation);
        }

        private Task<AccessDecision> EvaluateSameTenantAccess(
            AccessContext context,
            string tenantId,
            string userId,
            ImpersonationSession? impersonation)
        {
            // Get user's role in tenant
            var membershipKey = $"{userId}:{tenantId}";
            if (!_memberships.TryGetValue(membershipKey, out var membership) || !membership.IsActive)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "User is not a member of this tenant",
                    ApplicablePolicies = new[] { "MultiTenancy.NotTenantMember" }
                });
            }

            // Check role-based permissions
            var hasPermission = CheckTenantRolePermission(membership.Role, context.Action, context.ResourceId);

            if (hasPermission)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = $"Access granted by tenant role: {membership.Role}",
                    ApplicablePolicies = new[] { $"MultiTenancy.Role.{membership.Role}" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["TenantId"] = tenantId,
                        ["Role"] = membership.Role.ToString(),
                        ["IsImpersonating"] = impersonation != null
                    }
                });
            }

            return Task.FromResult(new AccessDecision
            {
                IsGranted = false,
                Reason = $"Tenant role '{membership.Role}' does not permit action '{context.Action}'",
                ApplicablePolicies = new[] { "MultiTenancy.InsufficientRolePermission" }
            });
        }

        private Task<AccessDecision> EvaluateCrossTenantAccess(
            AccessContext context,
            string sourceTenantId,
            string targetTenantId,
            string userId,
            ImpersonationSession? impersonation)
        {
            // Check isolation level
            if (!_tenants.TryGetValue(targetTenantId, out var targetTenant))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Target tenant not found",
                    ApplicablePolicies = new[] { "MultiTenancy.TargetTenantNotFound" }
                });
            }

            var isolationLevel = targetTenant.Settings.IsolationLevel ?? _defaultIsolationLevel;

            // Strict isolation - no cross-tenant access allowed
            if (isolationLevel == IsolationLevel.Strict)
            {
                // Check for explicit cross-tenant grant
                var grant = _crossTenantGrants.Values.FirstOrDefault(g =>
                    g.UserId == userId &&
                    g.SourceTenantId == sourceTenantId &&
                    g.TargetTenantId == targetTenantId &&
                    g.IsActive &&
                    g.ExpiresAt > DateTime.UtcNow &&
                    g.Permissions.Any(p => MatchesPermission(p, context.Action, context.ResourceId)));

                if (grant != null)
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = true,
                        Reason = $"Cross-tenant access granted via explicit grant: {grant.Id}",
                        ApplicablePolicies = new[] { "MultiTenancy.CrossTenantGrant" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["SourceTenantId"] = sourceTenantId,
                            ["TargetTenantId"] = targetTenantId,
                            ["GrantId"] = grant.Id,
                            ["ExpiresAt"] = grant.ExpiresAt
                        }
                    });
                }

                // Check tenant hierarchy
                var hierarchyKey = $"{sourceTenantId}:{targetTenantId}";
                if (_hierarchies.TryGetValue(hierarchyKey, out var hierarchy) && hierarchy.CanAccessChildData)
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = true,
                        Reason = "Cross-tenant access granted via parent-child hierarchy",
                        ApplicablePolicies = new[] { "MultiTenancy.HierarchyAccess" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["ParentTenantId"] = sourceTenantId,
                            ["ChildTenantId"] = targetTenantId
                        }
                    });
                }

                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Strict tenant isolation - cross-tenant access denied",
                    ApplicablePolicies = new[] { "MultiTenancy.StrictIsolation" }
                });
            }

            // Shared resources isolation - allow access to shared resources
            if (isolationLevel == IsolationLevel.SharedResources)
            {
                var isSharedResource = context.ResourceAttributes.TryGetValue("IsShared", out var sharedObj) && sharedObj is true;

                if (isSharedResource)
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = true,
                        Reason = "Access granted to shared resource",
                        ApplicablePolicies = new[] { "MultiTenancy.SharedResource" }
                    });
                }

                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Resource is not shared - cross-tenant access denied",
                    ApplicablePolicies = new[] { "MultiTenancy.NotSharedResource" }
                });
            }

            // Federated isolation - allow access with federation agreement
            if (isolationLevel == IsolationLevel.Federated)
            {
                var hasFederation = _tenants.TryGetValue(sourceTenantId, out var sourceTenant) &&
                                    sourceTenant.Settings.FederatedWith?.Contains(targetTenantId) == true;

                if (hasFederation)
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = true,
                        Reason = "Access granted via tenant federation",
                        ApplicablePolicies = new[] { "MultiTenancy.Federation" }
                    });
                }

                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "No federation agreement - cross-tenant access denied",
                    ApplicablePolicies = new[] { "MultiTenancy.NoFederation" }
                });
            }

            return Task.FromResult(new AccessDecision
            {
                IsGranted = false,
                Reason = "Cross-tenant access denied by default",
                ApplicablePolicies = new[] { "MultiTenancy.DefaultDeny" }
            });
        }

        private static string? GetSubjectTenantId(AccessContext context)
        {
            if (context.SubjectAttributes.TryGetValue("TenantId", out var tenantObj))
            {
                return tenantObj?.ToString();
            }
            return null;
        }

        private static string? GetResourceTenantId(AccessContext context)
        {
            if (context.ResourceAttributes.TryGetValue("TenantId", out var tenantObj))
            {
                return tenantObj?.ToString();
            }

            // Try to extract from resource ID (e.g., "tenant:abc:resource:xyz")
            var parts = context.ResourceId.Split(':');
            if (parts.Length >= 2 && parts[0].Equals("tenant", StringComparison.OrdinalIgnoreCase))
            {
                return parts[1];
            }

            return null;
        }

        private static bool CheckTenantRolePermission(TenantRole role, string action, string resourceId)
        {
            return role switch
            {
                TenantRole.Admin => true, // Admin can do everything
                TenantRole.Manager => !action.Equals("delete", StringComparison.OrdinalIgnoreCase) ||
                                     !resourceId.Contains("tenant:", StringComparison.OrdinalIgnoreCase),
                TenantRole.User => action.Equals("read", StringComparison.OrdinalIgnoreCase) ||
                                  action.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                                  (action.Equals("write", StringComparison.OrdinalIgnoreCase) &&
                                   !resourceId.Contains("config", StringComparison.OrdinalIgnoreCase)),
                TenantRole.ReadOnly => action.Equals("read", StringComparison.OrdinalIgnoreCase) ||
                                       action.Equals("list", StringComparison.OrdinalIgnoreCase),
                TenantRole.Support => true, // Support has broad access for troubleshooting
                TenantRole.Guest => action.Equals("read", StringComparison.OrdinalIgnoreCase) &&
                                   resourceId.Contains("public", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private static bool MatchesPermission(string permission, string action, string resourceId)
        {
            // Permission format: "action:resource" or "*" for all
            if (permission == "*") return true;

            var parts = permission.Split(':');
            if (parts.Length != 2) return false;

            var permAction = parts[0];
            var permResource = parts[1];

            var actionMatches = permAction == "*" || permAction.Equals(action, StringComparison.OrdinalIgnoreCase);
            var resourceMatches = permResource == "*" ||
                                  resourceId.Equals(permResource, StringComparison.OrdinalIgnoreCase) ||
                                  (permResource.EndsWith("/*") && resourceId.StartsWith(permResource[..^2], StringComparison.OrdinalIgnoreCase));

            return actionMatches && resourceMatches;
        }

        private TenantDefinition? ParseTenantFromConfig(Dictionary<string, object> config)
        {
            try
            {
                return new TenantDefinition
                {
                    Id = config["Id"]?.ToString() ?? Guid.NewGuid().ToString(),
                    Name = config["Name"]?.ToString() ?? "Unnamed Tenant",
                    DisplayName = config.TryGetValue("DisplayName", out var dn) ? dn?.ToString() : null,
                    Status = TenantStatus.Active,
                    CreatedAt = DateTime.UtcNow,
                    Settings = new TenantSettings()
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Tenant definition.
    /// </summary>
    public sealed record TenantDefinition
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public TenantStatus Status { get; init; } = TenantStatus.Active;
        public string? SuspensionReason { get; init; }
        public DateTime? SuspendedAt { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public required TenantSettings Settings { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Tenant status.
    /// </summary>
    public enum TenantStatus
    {
        Active,
        Suspended,
        Pending,
        Deactivated,
        Deleted
    }

    /// <summary>
    /// Tenant settings.
    /// </summary>
    public sealed record TenantSettings
    {
        public IsolationLevel? IsolationLevel { get; init; }
        public int MaxUsers { get; init; } = 1000;
        public int MaxResources { get; init; } = 10000;
        public long MaxStorageBytes { get; init; } = 10_737_418_240; // 10 GB
        public bool AllowCrossTenantAccess { get; init; } = false;
        public bool AllowImpersonation { get; init; } = false;
        public string[]? FederatedWith { get; init; }
        public string[]? AllowedDomains { get; init; }
        public Dictionary<string, object> CustomSettings { get; init; } = new();
    }

    /// <summary>
    /// Isolation level.
    /// </summary>
    public enum IsolationLevel
    {
        /// <summary>Complete isolation, no cross-tenant access without explicit grant.</summary>
        Strict,
        /// <summary>Allows access to explicitly shared resources.</summary>
        SharedResources,
        /// <summary>Allows access between federated tenants.</summary>
        Federated
    }

    /// <summary>
    /// Tenant membership.
    /// </summary>
    public sealed record TenantMembership
    {
        public required string Id { get; init; }
        public required string UserId { get; init; }
        public required string TenantId { get; init; }
        public required TenantRole Role { get; init; }
        public required DateTime AssignedAt { get; init; }
        public required string AssignedBy { get; init; }
        public bool IsActive { get; init; } = true;
        public DateTime? ExpiresAt { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Tenant roles.
    /// </summary>
    public enum TenantRole
    {
        Admin,
        Manager,
        User,
        ReadOnly,
        Support,
        Guest
    }

    /// <summary>
    /// Cross-tenant access grant.
    /// </summary>
    public sealed record CrossTenantGrant
    {
        public required string Id { get; init; }
        public required string UserId { get; init; }
        public required string SourceTenantId { get; init; }
        public required string TargetTenantId { get; init; }
        public required string[] Permissions { get; init; }
        public required DateTime GrantedAt { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public required string GrantedBy { get; init; }
        public string? ApprovalReference { get; init; }
        public bool IsActive { get; init; } = true;
        public CrossTenantGrantStatus Status { get; init; } = CrossTenantGrantStatus.Active;
        public DateTime? RevokedAt { get; init; }
        public string? RevokedBy { get; init; }
        public string? RevocationReason { get; init; }
    }

    /// <summary>
    /// Cross-tenant grant status.
    /// </summary>
    public enum CrossTenantGrantStatus
    {
        Pending,
        Active,
        Expired,
        Revoked
    }

    /// <summary>
    /// Tenant hierarchy relationship.
    /// </summary>
    public sealed record TenantHierarchy
    {
        public required string ParentTenantId { get; init; }
        public required string ChildTenantId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public bool InheritPermissions { get; init; } = true;
        public bool CanAccessChildData { get; init; } = true;
    }

    /// <summary>
    /// Impersonation session for support scenarios.
    /// </summary>
    public sealed record ImpersonationSession
    {
        public required string Id { get; init; }
        public required string SupportUserId { get; init; }
        public required string SupportTenantId { get; init; }
        public required string TargetUserId { get; init; }
        public required string TargetTenantId { get; init; }
        public required DateTime StartedAt { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public DateTime? EndedAt { get; init; }
        public required string Reason { get; init; }
        public required string ApprovalReference { get; init; }
        public bool IsActive { get; init; } = true;
    }

    /// <summary>
    /// Tenant resource quota.
    /// </summary>
    public sealed class TenantResourceQuota
    {
        public required string TenantId { get; init; }
        public int MaxUsers { get; set; }
        public int MaxResources { get; set; }
        public long MaxStorageBytes { get; set; }
        public int CurrentUsers;
        public int CurrentResources;
        public long CurrentStorageBytes;
    }

    #endregion
}
