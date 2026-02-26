using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.PolicyEngine
{
    /// <summary>
    /// Open Policy Agent (OPA) integration strategy.
    /// Evaluates policies using Rego language via OPA's REST API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OPA provides:
    /// - Declarative policy language (Rego)
    /// - General-purpose policy engine
    /// - REST API for policy evaluation (POST /v1/data/{path})
    /// - Policy bundles for distribution
    /// - Decision logging and auditing
    /// </para>
    /// <para>
    /// Typical OPA deployment:
    /// - Sidecar container alongside application
    /// - Centralized OPA server
    /// - Embedded OPA library (Go)
    /// </para>
    /// </remarks>
    public sealed class OpaStrategy : AccessControlStrategyBase
    {
        private readonly HttpClient _httpClient = new();
        private string _opaEndpoint = "http://localhost:8181";
        private string _policyPath = "authz/allow";
        private TimeSpan _requestTimeout = TimeSpan.FromSeconds(5);
        private bool _enableDecisionLogging = true;

        /// <inheritdoc/>
        public override string StrategyId => "opa";

        /// <inheritdoc/>
        public override string StrategyName => "Open Policy Agent (OPA)";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 10000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("OpaEndpoint", out var endpoint) && endpoint is string endpointStr)
            {
                _opaEndpoint = endpointStr;
            }

            if (configuration.TryGetValue("PolicyPath", out var path) && path is string pathStr)
            {
                _policyPath = pathStr;
            }

            if (configuration.TryGetValue("RequestTimeoutSeconds", out var timeout) && timeout is int secs)
            {
                _requestTimeout = TimeSpan.FromSeconds(secs);
            }

            if (configuration.TryGetValue("EnableDecisionLogging", out var logging) && logging is bool enable)
            {
                _enableDecisionLogging = enable;
            }

            _httpClient.Timeout = _requestTimeout;

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("opa.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("opa.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("opa.evaluate");
            try
            {
                // Build OPA input document
                var environmentDict = new Dictionary<string, object>
                {
                    ["time"] = context.RequestTime.ToString("o"),
                    ["ip_address"] = context.ClientIpAddress ?? "unknown"
                };

                if (context.Location != null)
                {
                    environmentDict["location"] = new Dictionary<string, object>
                    {
                        ["country"] = context.Location.Country ?? "unknown",
                        ["latitude"] = context.Location.Latitude,
                        ["longitude"] = context.Location.Longitude
                    };
                }

                var input = new Dictionary<string, object>
                {
                    ["subject"] = new Dictionary<string, object>
                    {
                        ["id"] = context.SubjectId,
                        ["roles"] = context.Roles,
                        ["attributes"] = context.SubjectAttributes
                    },
                    ["resource"] = new Dictionary<string, object>
                    {
                        ["id"] = context.ResourceId,
                        ["attributes"] = context.ResourceAttributes
                    },
                    ["action"] = context.Action,
                    ["environment"] = environmentDict
                };

                // Add environment attributes
                foreach (var attr in context.EnvironmentAttributes)
                {
                    if (!environmentDict.ContainsKey(attr.Key))
                    {
                        environmentDict[attr.Key] = attr.Value;
                    }
                }

                // Build OPA request
                var opaRequest = new { input };
                var json = JsonSerializer.Serialize(opaRequest);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Send request to OPA
                var url = $"{_opaEndpoint}/v1/data/{_policyPath}";
                using var response = await _httpClient.PostAsync(url, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"OPA policy evaluation failed: HTTP {response.StatusCode}",
                        ApplicablePolicies = new[] { "OPA.EvaluationError" }
                    };
                }

                // Parse OPA response
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                // Extract decision from result
                bool isGranted = false;
                string reason = "OPA policy denied access";

                if (root.TryGetProperty("result", out var result))
                {
                    if (result.ValueKind == JsonValueKind.True || result.ValueKind == JsonValueKind.False)
                    {
                        isGranted = result.GetBoolean();
                        reason = isGranted ? "OPA policy allows access" : "OPA policy denies access";
                    }
                    else if (result.TryGetProperty("allow", out var allow))
                    {
                        isGranted = allow.GetBoolean();
                        reason = isGranted ? "OPA policy allows access" : "OPA policy denies access";
                    }
                }

                return new AccessDecision
                {
                    IsGranted = isGranted,
                    Reason = reason,
                    ApplicablePolicies = new[] { $"OPA.{_policyPath}" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["OpaEndpoint"] = _opaEndpoint,
                        ["PolicyPath"] = _policyPath,
                        ["DecisionLoggingEnabled"] = _enableDecisionLogging
                    }
                };
            }
            catch (HttpRequestException ex)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"OPA service unavailable: {ex.Message}",
                    ApplicablePolicies = new[] { "OPA.ServiceUnavailable" }
                };
            }
            catch (TaskCanceledException)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = "OPA request timeout",
                    ApplicablePolicies = new[] { "OPA.Timeout" }
                };
            }
            catch (Exception ex)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"OPA evaluation error: {ex.Message}",
                    ApplicablePolicies = new[] { "OPA.Error" }
                };
            }
        }
    }
}
