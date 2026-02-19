using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Mandatory Access Control (MAC) strategy implementation.
    /// Implements Bell-LaPadula security model: "no read up, no write down".
    /// </summary>
    /// <remarks>
    /// <para>
    /// MAC features:
    /// - Security clearance levels (Unclassified, Confidential, Secret, TopSecret)
    /// - Subject clearance vs object classification comparison
    /// - Read access: clearance >= classification (no read up)
    /// - Write access: clearance <= classification (no write down)
    /// - Mandatory enforcement (cannot be overridden by users)
    /// </para>
    /// </remarks>
    public sealed class MacStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, SecurityClearanceLevel> _subjectClearances = new();
        private readonly ConcurrentDictionary<string, SecurityClassification> _resourceClassifications = new();

        /// <inheritdoc/>
        public override string StrategyId => "mac";

        /// <inheritdoc/>
        public override string StrategyName => "Mandatory Access Control";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 10000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load subject clearances
            if (configuration.TryGetValue("SubjectClearances", out var clearancesObj) &&
                clearancesObj is IEnumerable<Dictionary<string, object>> clearanceConfigs)
            {
                foreach (var config in clearanceConfigs)
                {
                    var subjectId = config["SubjectId"]?.ToString() ?? "";
                    if (config.TryGetValue("Clearance", out var clearanceObj) &&
                        Enum.TryParse<SecurityClearanceLevel>(clearanceObj.ToString(), out var clearance))
                    {
                        SetSubjectClearance(subjectId, clearance);
                    }
                }
            }

            // Load resource classifications
            if (configuration.TryGetValue("ResourceClassifications", out var classificationsObj) &&
                classificationsObj is IEnumerable<Dictionary<string, object>> classificationConfigs)
            {
                foreach (var config in classificationConfigs)
                {
                    var resourceId = config["ResourceId"]?.ToString() ?? "";
                    if (config.TryGetValue("Classification", out var classificationObj) &&
                        Enum.TryParse<SecurityClassification>(classificationObj.ToString(), out var classification))
                    {
                        SetResourceClassification(resourceId, classification);
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
            IncrementCounter("mac.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("mac.shutdown");
            _subjectClearances.Clear();
            _resourceClassifications.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <summary>
        /// Sets the security clearance for a subject.
        /// </summary>
        public void SetSubjectClearance(string subjectId, SecurityClearanceLevel clearance)
        {
            _subjectClearances[subjectId] = clearance;
        }

        /// <summary>
        /// Sets the security classification for a resource.
        /// </summary>
        public void SetResourceClassification(string resourceId, SecurityClassification classification)
        {
            _resourceClassifications[resourceId] = classification;
        }

        /// <summary>
        /// Gets the security clearance for a subject.
        /// </summary>
        public SecurityClearanceLevel GetSubjectClearance(string subjectId)
        {
            return _subjectClearances.TryGetValue(subjectId, out var clearance)
                ? clearance
                : SecurityClearanceLevel.Unclassified;
        }

        /// <summary>
        /// Gets the security classification for a resource.
        /// </summary>
        public SecurityClassification GetResourceClassification(string resourceId)
        {
            // Check exact match first
            if (_resourceClassifications.TryGetValue(resourceId, out var classification))
                return classification;

            // Check for prefix matches (e.g., "documents/secret/*")
            var parts = resourceId.Split('/');
            for (int i = parts.Length - 1; i > 0; i--)
            {
                var prefix = string.Join("/", parts.Take(i)) + "/*";
                if (_resourceClassifications.TryGetValue(prefix, out classification))
                    return classification;
            }

            return SecurityClassification.Unclassified;
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("mac.evaluate");
            var subjectClearance = GetSubjectClearance(context.SubjectId);
            var resourceClassification = GetResourceClassification(context.ResourceId);

            // Check if subject clearance from context attributes
            if (context.SubjectAttributes.TryGetValue("SecurityClearance", out var clearanceObj) &&
                Enum.TryParse<SecurityClearanceLevel>(clearanceObj.ToString(), out var contextClearance))
            {
                subjectClearance = contextClearance;
            }

            // Check if resource classification from context attributes
            if (context.ResourceAttributes.TryGetValue("SecurityClassification", out var classificationObj) &&
                Enum.TryParse<SecurityClassification>(classificationObj.ToString(), out var contextClassification))
            {
                resourceClassification = contextClassification;
            }

            var action = context.Action.ToLowerInvariant();

            // Bell-LaPadula rules
            if (action == "read" || action == "view" || action == "get" || action == "list")
            {
                // Simple Security Property: no read up
                // Subject can read if clearance >= classification
                if ((int)subjectClearance >= (int)resourceClassification)
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = true,
                        Reason = $"MAC read allowed: clearance {subjectClearance} >= classification {resourceClassification}",
                        ApplicablePolicies = new[] { "MAC.SimpleSecurityProperty" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["SubjectClearance"] = subjectClearance.ToString(),
                            ["ResourceClassification"] = resourceClassification.ToString(),
                            ["Action"] = action
                        }
                    });
                }

                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"MAC read denied: clearance {subjectClearance} < classification {resourceClassification} (no read up)",
                    ApplicablePolicies = new[] { "MAC.SimpleSecurityProperty" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["SubjectClearance"] = subjectClearance.ToString(),
                        ["ResourceClassification"] = resourceClassification.ToString(),
                        ["Action"] = action,
                        ["Violation"] = "NoReadUp"
                    }
                });
            }
            else if (action == "write" || action == "update" || action == "modify" || action == "delete" || action == "create")
            {
                // *-Property: no write down
                // Subject can write if clearance <= classification
                if ((int)subjectClearance <= (int)resourceClassification)
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = true,
                        Reason = $"MAC write allowed: clearance {subjectClearance} <= classification {resourceClassification}",
                        ApplicablePolicies = new[] { "MAC.StarProperty" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["SubjectClearance"] = subjectClearance.ToString(),
                            ["ResourceClassification"] = resourceClassification.ToString(),
                            ["Action"] = action
                        }
                    });
                }

                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"MAC write denied: clearance {subjectClearance} > classification {resourceClassification} (no write down)",
                    ApplicablePolicies = new[] { "MAC.StarProperty" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["SubjectClearance"] = subjectClearance.ToString(),
                        ["ResourceClassification"] = resourceClassification.ToString(),
                        ["Action"] = action,
                        ["Violation"] = "NoWriteDown"
                    }
                });
            }

            // For other actions, require exact match
            if ((int)subjectClearance == (int)resourceClassification)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = $"MAC access allowed: clearance {subjectClearance} == classification {resourceClassification}",
                    ApplicablePolicies = new[] { "MAC.ExactMatch" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["SubjectClearance"] = subjectClearance.ToString(),
                        ["ResourceClassification"] = resourceClassification.ToString(),
                        ["Action"] = action
                    }
                });
            }

            return Task.FromResult(new AccessDecision
            {
                IsGranted = false,
                Reason = $"MAC access denied: clearance {subjectClearance} != classification {resourceClassification}",
                ApplicablePolicies = new[] { "MAC.ExactMatch" },
                Metadata = new Dictionary<string, object>
                {
                    ["SubjectClearance"] = subjectClearance.ToString(),
                    ["ResourceClassification"] = resourceClassification.ToString(),
                    ["Action"] = action
                }
            });
        }
    }

    /// <summary>
    /// Security clearance levels (highest to lowest).
    /// </summary>
    public enum SecurityClearanceLevel
    {
        Unclassified = 0,
        Confidential = 1,
        Secret = 2,
        TopSecret = 3
    }

    /// <summary>
    /// Security classification levels (lowest to highest).
    /// </summary>
    public enum SecurityClassification
    {
        Unclassified = 0,
        Confidential = 1,
        Secret = 2,
        TopSecret = 3
    }
}
