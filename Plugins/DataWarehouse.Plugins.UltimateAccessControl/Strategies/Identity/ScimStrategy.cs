using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Identity
{
    /// <summary>
    /// SCIM 2.0 (System for Cross-domain Identity Management) user provisioning and identity synchronization strategy (RFC 7643, RFC 7644).
    /// </summary>
    /// <remarks>
    /// Provides identity provisioning, de-provisioning, and synchronization with external identity providers.
    /// Supports SCIM 2.0 User and Group resources.
    /// </remarks>
    public sealed class ScimStrategy : AccessControlStrategyBase
    {
        private readonly HttpClient _httpClient;
        private string? _scimEndpoint;
        private string? _bearerToken;

        public ScimStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public override string StrategyId => "identity-scim";
        public override string StrategyName => "SCIM 2.0";

        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 1000
        };

        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("ScimEndpoint", out var endpoint) && endpoint is string endpointStr)
                _scimEndpoint = endpointStr?.TrimEnd('/');

            if (configuration.TryGetValue("BearerToken", out var token) && token is string tokenStr)
            {
                _bearerToken = tokenStr;
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenStr);
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_scimEndpoint))
                return false;

            try
            {
                var response = await _httpClient.GetAsync($"{_scimEndpoint}/ServiceProviderConfig", cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Retrieves a SCIM user by ID.
        /// </summary>
        public async Task<ScimUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_scimEndpoint))
                return null;

            try
            {
                var response = await _httpClient.GetAsync($"{_scimEndpoint}/Users/{userId}", cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<ScimUser>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Creates a new SCIM user.
        /// </summary>
        public async Task<ScimUser?> CreateUserAsync(ScimUser user, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_scimEndpoint))
                return null;

            try
            {
                var json = JsonSerializer.Serialize(user);
                var content = new StringContent(json, Encoding.UTF8, "application/scim+json");

                var response = await _httpClient.PostAsync($"{_scimEndpoint}/Users", content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<ScimUser>(responseJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Updates an existing SCIM user.
        /// </summary>
        public async Task<ScimUser?> UpdateUserAsync(string userId, ScimUser user, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_scimEndpoint))
                return null;

            try
            {
                var json = JsonSerializer.Serialize(user);
                var content = new StringContent(json, Encoding.UTF8, "application/scim+json");

                var response = await _httpClient.PutAsync($"{_scimEndpoint}/Users/{userId}", content, cancellationToken);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<ScimUser>(responseJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Deletes a SCIM user.
        /// </summary>
        public async Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_scimEndpoint))
                return false;

            try
            {
                var response = await _httpClient.DeleteAsync($"{_scimEndpoint}/Users/{userId}", cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // SCIM is primarily for provisioning, not authentication
            // However, we can validate that a user exists and is active
            var userId = context.SubjectId;

            var user = await GetUserAsync(userId, cancellationToken);
            if (user == null)
            {
                return new AccessDecision { IsGranted = false, Reason = "User not found in SCIM directory" };
            }

            if (!user.Active)
            {
                return new AccessDecision { IsGranted = false, Reason = "User is inactive" };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "User validated via SCIM",
                Metadata = new Dictionary<string, object>
                {
                    ["UserName"] = user.UserName ?? "",
                    ["Email"] = user.Emails?.Count > 0 ? user.Emails[0].Value : "",
                    ["Active"] = user.Active
                }
            };
        }
    }

    #region Supporting Types

    /// <summary>
    /// SCIM 2.0 User resource.
    /// </summary>
    public sealed class ScimUser
    {
        public string? Id { get; set; }
        public string? ExternalId { get; set; }
        public string? UserName { get; set; }
        public ScimName? Name { get; set; }
        public string? DisplayName { get; set; }
        public List<ScimEmail>? Emails { get; set; }
        public bool Active { get; set; } = true;
        public List<string>? Schemas { get; set; } = new() { "urn:ietf:params:scim:schemas:core:2.0:User" };
    }

    public sealed class ScimName
    {
        public string? Formatted { get; set; }
        public string? FamilyName { get; set; }
        public string? GivenName { get; set; }
    }

    public sealed class ScimEmail
    {
        public required string Value { get; set; }
        public string Type { get; set; } = "work";
        public bool Primary { get; set; } = true;
    }

    #endregion
}
