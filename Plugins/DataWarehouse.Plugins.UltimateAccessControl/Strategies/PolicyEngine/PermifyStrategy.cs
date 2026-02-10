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
    /// Permify authorization service integration strategy.
    /// Implements schema-based authorization, permission checking, and data filtering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Permify features:
    /// - Schema-based authorization (define permissions in schema)
    /// - Permission checking via Permify API
    /// - Data filtering (returns allowed resources)
    /// - Relationship-based access control
    /// - Multi-tenancy support
    /// - Real-time permission evaluation
    /// </para>
    /// <para>
    /// Permify schema example:
    /// entity user {}
    /// entity document {
    ///   relation owner @user
    ///   relation viewer @user
    ///   action view = viewer or owner
    ///   action edit = owner
    /// }
    /// </para>
    /// </remarks>
    public sealed class PermifyStrategy : AccessControlStrategyBase
    {
        private readonly HttpClient _httpClient = new();
        private string _permifyEndpoint = "http://localhost:3476";
        private string _tenantId = "default";
        private TimeSpan _requestTimeout = TimeSpan.FromSeconds(5);

        /// <inheritdoc/>
        public override string StrategyId => "permify";

        /// <inheritdoc/>
        public override string StrategyName => "Permify Authorization";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 15000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("PermifyEndpoint", out var endpoint) && endpoint is string endpointStr)
            {
                _permifyEndpoint = endpointStr;
            }

            if (configuration.TryGetValue("TenantId", out var tenantId) && tenantId is string tenantIdStr)
            {
                _tenantId = tenantIdStr;
            }

            if (configuration.TryGetValue("RequestTimeoutSeconds", out var timeout) && timeout is int secs)
            {
                _requestTimeout = TimeSpan.FromSeconds(secs);
            }

            _httpClient.Timeout = _requestTimeout;

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            try
            {
                // Extract entity type and ID from resource
                // Format: "type:id" (e.g., "document:readme")
                var resourceParts = context.ResourceId.Split(':', 2);
                var entityType = resourceParts.Length > 1 ? resourceParts[0] : "resource";
                var entityId = resourceParts.Length > 1 ? resourceParts[1] : context.ResourceId;

                // Build Permify check request
                var request = new
                {
                    tenant_id = _tenantId,
                    entity = new
                    {
                        type = entityType,
                        id = entityId
                    },
                    permission = context.Action,
                    subject = new
                    {
                        type = "user",
                        id = context.SubjectId
                    },
                    metadata = new Dictionary<string, object>
                    {
                        ["attributes"] = context.SubjectAttributes,
                        ["environment"] = context.EnvironmentAttributes
                    }
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Send check request to Permify
                var response = await _httpClient.PostAsync($"{_permifyEndpoint}/v1/tenants/{_tenantId}/permissions/check", content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"Permify service error: HTTP {response.StatusCode}",
                        ApplicablePolicies = new[] { "Permify.ServiceError" }
                    };
                }

                // Parse Permify response
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                var can = root.GetProperty("can").GetBoolean();

                return new AccessDecision
                {
                    IsGranted = can,
                    Reason = can ? "Permify grants permission" : "Permify denies permission",
                    ApplicablePolicies = new[] { can ? "Permify.Allowed" : "Permify.Denied" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["TenantId"] = _tenantId,
                        ["EntityType"] = entityType,
                        ["EntityId"] = entityId,
                        ["Permission"] = context.Action
                    }
                };
            }
            catch (HttpRequestException ex)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Permify service unavailable: {ex.Message}",
                    ApplicablePolicies = new[] { "Permify.ServiceUnavailable" }
                };
            }
            catch (TaskCanceledException)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Permify request timeout",
                    ApplicablePolicies = new[] { "Permify.Timeout" }
                };
            }
            catch (Exception ex)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Permify evaluation error: {ex.Message}",
                    ApplicablePolicies = new[] { "Permify.Error" }
                };
            }
        }

        /// <summary>
        /// Filters a list of resources to only those the subject can access.
        /// </summary>
        public async Task<List<string>> FilterResourcesAsync(
            string subjectId,
            string permission,
            List<string> resourceIds,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new
                {
                    tenant_id = _tenantId,
                    subject = new
                    {
                        type = "user",
                        id = subjectId
                    },
                    permission,
                    entities = resourceIds.Select(rid =>
                    {
                        var parts = rid.Split(':', 2);
                        return new
                        {
                            type = parts.Length > 1 ? parts[0] : "resource",
                            id = parts.Length > 1 ? parts[1] : rid
                        };
                    }).ToList()
                };

                var json = JsonSerializer.Serialize(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_permifyEndpoint}/v1/tenants/{_tenantId}/permissions/lookup-entity", content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return new List<string>(); // Return empty list on error
                }

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                var allowedIds = new List<string>();
                if (root.TryGetProperty("entity_ids", out var entityIds))
                {
                    foreach (var entityId in entityIds.EnumerateArray())
                    {
                        allowedIds.Add(entityId.GetString()!);
                    }
                }

                return allowedIds;
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}
