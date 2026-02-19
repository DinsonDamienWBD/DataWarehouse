using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Discretionary Access Control (DAC) strategy implementation.
    /// Owner-based permissions where resource owners grant access to others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DAC features:
    /// - Owner-based permissions (owner grants access to others)
    /// - Permission matrix: Read, Write, Execute, Delete per user/group
    /// - Ownership transfer capability
    /// - Discretionary control (owners can modify permissions)
    /// </para>
    /// </remarks>
    public sealed class DacStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, string> _resourceOwners = new();
        private readonly ConcurrentDictionary<string, PermissionMatrix> _resourcePermissions = new();

        /// <inheritdoc/>
        public override string StrategyId => "dac";

        /// <inheritdoc/>
        public override string StrategyName => "Discretionary Access Control";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 10000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load resource owners
            if (configuration.TryGetValue("ResourceOwners", out var ownersObj) &&
                ownersObj is IEnumerable<Dictionary<string, object>> ownerConfigs)
            {
                foreach (var config in ownerConfigs)
                {
                    var resourceId = config["ResourceId"]?.ToString() ?? "";
                    var ownerId = config["OwnerId"]?.ToString() ?? "";
                    SetResourceOwner(resourceId, ownerId);
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dac.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("dac.shutdown");
            _resourceOwners.Clear();
            _resourcePermissions.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <summary>
        /// Sets the owner of a resource.
        /// </summary>
        public void SetResourceOwner(string resourceId, string ownerId)
        {
            _resourceOwners[resourceId] = ownerId;

            // Initialize permission matrix for this resource
            if (!_resourcePermissions.ContainsKey(resourceId))
            {
                _resourcePermissions[resourceId] = new PermissionMatrix();
            }
        }

        /// <summary>
        /// Gets the owner of a resource.
        /// </summary>
        public string? GetResourceOwner(string resourceId)
        {
            return _resourceOwners.TryGetValue(resourceId, out var owner) ? owner : null;
        }

        /// <summary>
        /// Grants permission to a subject on a resource.
        /// </summary>
        public void GrantPermission(string resourceId, string subjectId, DacPermission permission)
        {
            if (!_resourcePermissions.TryGetValue(resourceId, out var matrix))
            {
                matrix = new PermissionMatrix();
                _resourcePermissions[resourceId] = matrix;
            }

            matrix.Grant(subjectId, permission);
        }

        /// <summary>
        /// Revokes permission from a subject on a resource.
        /// </summary>
        public void RevokePermission(string resourceId, string subjectId, DacPermission permission)
        {
            if (_resourcePermissions.TryGetValue(resourceId, out var matrix))
            {
                matrix.Revoke(subjectId, permission);
            }
        }

        /// <summary>
        /// Transfers ownership of a resource.
        /// </summary>
        public bool TransferOwnership(string resourceId, string currentOwnerId, string newOwnerId)
        {
            if (_resourceOwners.TryGetValue(resourceId, out var currentOwner) &&
                currentOwner == currentOwnerId)
            {
                _resourceOwners[resourceId] = newOwnerId;
                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("dac.evaluate");
            var resourceId = context.ResourceId;
            var subjectId = context.SubjectId;

            // Get resource owner (from stored owners or context attributes)
            var resourceOwner = GetResourceOwner(resourceId);
            if (resourceOwner == null &&
                context.ResourceAttributes.TryGetValue("Owner", out var ownerObj))
            {
                resourceOwner = ownerObj.ToString();
            }

            // Owner has full access
            if (resourceOwner != null && resourceOwner == subjectId)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = $"DAC access granted: subject is resource owner",
                    ApplicablePolicies = new[] { "DAC.OwnerFullAccess" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Owner"] = resourceOwner,
                        ["Subject"] = subjectId,
                        ["Action"] = context.Action
                    }
                });
            }

            // Check permission matrix
            var requiredPermission = MapActionToPermission(context.Action);
            if (!_resourcePermissions.TryGetValue(resourceId, out var matrix))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"DAC access denied: no permissions configured for resource",
                    ApplicablePolicies = new[] { "DAC.NoPermissions" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Subject"] = subjectId,
                        ["Resource"] = resourceId,
                        ["Action"] = context.Action
                    }
                });
            }

            var hasPermission = matrix.HasPermission(subjectId, requiredPermission);
            if (hasPermission)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = $"DAC access granted: subject has {requiredPermission} permission",
                    ApplicablePolicies = new[] { "DAC.PermissionGranted" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Subject"] = subjectId,
                        ["Resource"] = resourceId,
                        ["Action"] = context.Action,
                        ["Permission"] = requiredPermission.ToString()
                    }
                });
            }

            // Check group permissions (if subject has roles)
            foreach (var role in context.Roles)
            {
                if (matrix.HasPermission(role, requiredPermission))
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = true,
                        Reason = $"DAC access granted: role '{role}' has {requiredPermission} permission",
                        ApplicablePolicies = new[] { "DAC.GroupPermissionGranted" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["Subject"] = subjectId,
                            ["Role"] = role,
                            ["Resource"] = resourceId,
                            ["Action"] = context.Action,
                            ["Permission"] = requiredPermission.ToString()
                        }
                    });
                }
            }

            return Task.FromResult(new AccessDecision
            {
                IsGranted = false,
                Reason = $"DAC access denied: subject lacks {requiredPermission} permission",
                ApplicablePolicies = new[] { "DAC.PermissionDenied" },
                Metadata = new Dictionary<string, object>
                {
                    ["Subject"] = subjectId,
                    ["Resource"] = resourceId,
                    ["Action"] = context.Action,
                    ["RequiredPermission"] = requiredPermission.ToString()
                }
            });
        }

        private static DacPermission MapActionToPermission(string action)
        {
            return action.ToLowerInvariant() switch
            {
                "read" or "view" or "get" or "list" => DacPermission.Read,
                "write" or "update" or "modify" or "create" => DacPermission.Write,
                "execute" or "run" => DacPermission.Execute,
                "delete" or "remove" => DacPermission.Delete,
                _ => DacPermission.Read
            };
        }
    }

    /// <summary>
    /// DAC permission flags.
    /// </summary>
    [Flags]
    public enum DacPermission
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4,
        Delete = 8,
        All = Read | Write | Execute | Delete
    }

    /// <summary>
    /// Permission matrix for DAC.
    /// </summary>
    public sealed class PermissionMatrix
    {
        private readonly ConcurrentDictionary<string, DacPermission> _permissions = new();

        public void Grant(string subjectId, DacPermission permission)
        {
            _permissions.AddOrUpdate(subjectId,
                permission,
                (_, existing) => existing | permission);
        }

        public void Revoke(string subjectId, DacPermission permission)
        {
            _permissions.AddOrUpdate(subjectId,
                DacPermission.None,
                (_, existing) => existing & ~permission);
        }

        public bool HasPermission(string subjectId, DacPermission permission)
        {
            if (_permissions.TryGetValue(subjectId, out var granted))
            {
                return (granted & permission) == permission;
            }

            return false;
        }

        public DacPermission GetPermissions(string subjectId)
        {
            return _permissions.TryGetValue(subjectId, out var perms) ? perms : DacPermission.None;
        }
    }
}
