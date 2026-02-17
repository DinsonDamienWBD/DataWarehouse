// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Access control strategy that delegates to the multi-level AccessVerificationMatrix.
    /// This strategy evaluates the full System → Tenant → Instance → UserGroup → User hierarchy
    /// with deny-takes-absolute-precedence at every level.
    ///
    /// UNIVERSAL ENFORCEMENT: This strategy should be registered as the FIRST strategy
    /// in the evaluation chain so it runs before any other strategy.
    /// </summary>
    public sealed class HierarchyVerificationStrategy : AccessControlStrategyBase
    {
        private readonly AccessVerificationMatrix _matrix;

        /// <inheritdoc/>
        public override string StrategyId => "hierarchy-verification";

        /// <inheritdoc/>
        public override string StrategyName => "Hierarchy Verification";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 100000
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="HierarchyVerificationStrategy"/> class.
        /// </summary>
        /// <param name="matrix">The AccessVerificationMatrix instance for multi-level verification.</param>
        public HierarchyVerificationStrategy(AccessVerificationMatrix matrix)
        {
            _matrix = matrix ?? throw new ArgumentNullException(nameof(matrix));
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // If no CommandIdentity on the context, we cannot evaluate hierarchy — deny (fail-closed)
            // Other strategies like RBAC/ABAC will handle access based on SubjectId
            if (context.Identity is null)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "No CommandIdentity provided — cannot verify hierarchy. Access denied (fail-closed).",
                    ApplicablePolicies = new[] { "HierarchyVerification.NoIdentity" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["StrategyName"] = StrategyName,
                        ["RequiresIdentity"] = true
                    }
                });
            }

            // Evaluate through the AccessVerificationMatrix
            var verdict = _matrix.Evaluate(
                context.Identity,
                context.ResourceId,
                context.Action);

            // Map AccessVerdict to AccessDecision
            return Task.FromResult(new AccessDecision
            {
                IsGranted = verdict.Allowed,
                Reason = verdict.Reason,
                ApplicablePolicies = new[] { $"HierarchyVerification.{verdict.RuleId}" },
                Metadata = new Dictionary<string, object>
                {
                    ["StrategyName"] = StrategyName,
                    ["DecidedAtLevel"] = verdict.DecidedAtLevel.ToString(),
                    ["RuleId"] = verdict.RuleId,
                    ["EvaluatedAt"] = verdict.EvaluatedAt,
                    ["EffectivePrincipalId"] = verdict.Identity.EffectivePrincipalId,
                    ["TenantId"] = verdict.Identity.TenantId,
                    ["InstanceId"] = verdict.Identity.InstanceId,
                    ["LevelResults"] = verdict.LevelResults
                }
            });
        }
    }
}
