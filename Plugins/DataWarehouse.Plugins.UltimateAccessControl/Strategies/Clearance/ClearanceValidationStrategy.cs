using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Clearance
{
    /// <summary>
    /// Clearance validation strategy that verifies clearances against authoritative sources.
    /// Performs real-time validation of security clearances via external APIs.
    /// </summary>
    public sealed class ClearanceValidationStrategy : AccessControlStrategyBase
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient();
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;

        public ClearanceValidationStrategy(ILogger? logger = null, HttpClient? httpClient = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _httpClient = httpClient ?? SharedHttpClient;
        }

        /// <inheritdoc/>
        public override string StrategyId => "clearance-validation";

        /// <inheritdoc/>
        public override string StrategyName => "Clearance Validation";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 500
        };

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var subjectId = context.SubjectId;

            // Try to validate clearance via external source
            var validationResult = await ValidateClearanceAsync(subjectId, cancellationToken);

            if (!validationResult.IsValid)
            {
                _logger.LogWarning("Clearance validation failed for {SubjectId}: {Reason}", subjectId, validationResult.Reason);
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Clearance validation failed: {validationResult.Reason}"
                };
            }

            // Check if clearance is sufficient for resource
            var requiredLevel = context.ResourceAttributes.TryGetValue("required_clearance", out var reqObj)
                ? reqObj?.ToString()
                : null;

            if (requiredLevel != null && !IsClearanceSufficient(validationResult.ClearanceLevel, requiredLevel))
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Validated clearance ({validationResult.ClearanceLevel}) insufficient for required level ({requiredLevel})"
                };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Clearance validated successfully",
                Metadata = new Dictionary<string, object>
                {
                    ["validated_clearance"] = validationResult.ClearanceLevel ?? "unknown",
                    ["validation_source"] = validationResult.Source,
                    ["validation_timestamp"] = DateTime.UtcNow
                }
            };
        }

        private async Task<ClearanceValidationResult> ValidateClearanceAsync(string subjectId, CancellationToken cancellationToken)
        {
            // Check if validation endpoint is configured
            if (!Configuration.TryGetValue("ValidationEndpoint", out var endpointObj) ||
                endpointObj is not string endpoint)
            {
                _logger.LogWarning("No validation endpoint configured, skipping external validation");
                return new ClearanceValidationResult
                {
                    IsValid = true,
                    ClearanceLevel = "UNVALIDATED",
                    Source = "local"
                };
            }

            try
            {
                var request = new { subject_id = subjectId };
                var content = new StringContent(
                    JsonSerializer.Serialize(request),
                    System.Text.Encoding.UTF8,
                    "application/json"
                );

                var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var responseData = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<ValidationApiResponse>(responseData);

                return new ClearanceValidationResult
                {
                    IsValid = result?.is_valid ?? false,
                    ClearanceLevel = result?.clearance_level,
                    Source = "external_api",
                    Reason = result?.reason
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Clearance validation API call failed");
                return new ClearanceValidationResult
                {
                    IsValid = false,
                    Reason = $"Validation service unavailable: {ex.Message}",
                    Source = "error"
                };
            }
        }

        private bool IsClearanceSufficient(string? userLevel, string requiredLevel)
        {
            // Simple hierarchical check (can be extended)
            var levels = new[] { "UNCLASSIFIED", "CONFIDENTIAL", "SECRET", "TOPSECRET", "TS-SCI" };
            var userIndex = Array.IndexOf(levels, userLevel?.ToUpperInvariant());
            var reqIndex = Array.IndexOf(levels, requiredLevel.ToUpperInvariant());

            return userIndex >= reqIndex;
        }

        private sealed class ClearanceValidationResult
        {
            public bool IsValid { get; init; }
            public string? ClearanceLevel { get; init; }
            public string Source { get; init; } = "unknown";
            public string? Reason { get; init; }
        }

        private sealed class ValidationApiResponse
        {
            public bool is_valid { get; set; }
            public string? clearance_level { get; set; }
            public string? reason { get; set; }
        }
    }
}
