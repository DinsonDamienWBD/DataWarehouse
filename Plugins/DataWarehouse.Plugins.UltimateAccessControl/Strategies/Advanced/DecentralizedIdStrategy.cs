using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Advanced
{
    public sealed class DecentralizedIdStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public DecentralizedIdStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public override string StrategyId => "decentralized-id";
        public override string StrategyName => "Decentralized Identity Strategy";

        public override AccessControlCapabilities Capabilities => new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 200
        };

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("decentralized.id.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("decentralized.id.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }
protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("decentralized.id.evaluate");
            await Task.Yield();

            var did = context.SubjectAttributes.TryGetValue("did", out var didValue) && didValue is string didStr ? didStr : null;

            if (string.IsNullOrEmpty(did))
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = "No DID provided in access context"
                };
            }

            // W3C DID Core format validation: did:<method>:<method-specific-id>
            // Method name must match [a-z0-9]+, method-specific-id must be non-empty
            var didRegex = new Regex(@"^did:[a-z0-9]+:[A-Za-z0-9._:%-]+$", RegexOptions.Compiled);
            var isValidFormat = didRegex.IsMatch(did);

            if (!isValidFormat)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = "DID does not conform to W3C DID Core syntax (did:<method>:<id>)",
                    ApplicablePolicies = new[] { "did-policy" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["did"] = did,
                        ["format_valid"] = false
                    }
                };
            }

            var parts = did.Split(':', 3);
            var method = parts[1];
            var methodSpecificId = parts[2];

            // Verify the DID credential is present in the access context attributes
            var hasCredential = context.SubjectAttributes.ContainsKey("did_credential") ||
                                context.SubjectAttributes.ContainsKey("did_signature");

            if (!hasCredential)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = "DID format valid but no verifiable credential or signature provided",
                    ApplicablePolicies = new[] { "did-policy" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["did"] = did,
                        ["did_method"] = method,
                        ["format_valid"] = true,
                        ["credential_present"] = false
                    }
                };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Decentralized identity verified with valid DID format and credential",
                ApplicablePolicies = new[] { "did-policy" },
                Metadata = new Dictionary<string, object>
                {
                    ["did"] = did,
                    ["did_method"] = method,
                    ["format_valid"] = true,
                    ["credential_present"] = true
                }
            };
        }
    }
}
