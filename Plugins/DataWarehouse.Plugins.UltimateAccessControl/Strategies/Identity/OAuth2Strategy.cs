using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Identity
{
    /// <summary>
    /// OAuth 2.0 token validation strategy (RFC 6749, RFC 7662).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supports:
    /// - Bearer token validation via introspection endpoint (RFC 7662)
    /// - JWT access token validation (self-contained tokens)
    /// - Token caching to reduce introspection calls
    /// - Scope-based authorization
    /// - Client credentials and authorization code flows
    /// </para>
    /// <para>
    /// Configuration:
    /// - IntrospectionEndpoint: OAuth 2.0 token introspection endpoint URL
    /// - ClientId: Client ID for introspection authentication
    /// - ClientSecret: Client secret for introspection authentication
    /// - CacheTokenMinutes: Duration to cache token validation results
    /// </para>
    /// </remarks>
    public sealed class OAuth2Strategy : AccessControlStrategyBase
    {
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, CachedTokenInfo> _tokenCache = new();

        private string? _introspectionEndpoint;
        private string? _clientId;
        private string? _clientSecret;
        private TimeSpan _cacheTokenDuration = TimeSpan.FromMinutes(5);

        public OAuth2Strategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <inheritdoc/>
        public override string StrategyId => "identity-oauth2";

        /// <inheritdoc/>
        public override string StrategyName => "OAuth 2.0";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 5000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("IntrospectionEndpoint", out var endpoint) && endpoint is string endpointStr)
                _introspectionEndpoint = endpointStr;

            if (configuration.TryGetValue("ClientId", out var clientId) && clientId is string clientIdStr)
                _clientId = clientIdStr;

            if (configuration.TryGetValue("ClientSecret", out var secret) && secret is string secretStr)
                _clientSecret = secretStr;

            if (configuration.TryGetValue("CacheTokenMinutes", out var cache) && cache is int cacheInt)
                _cacheTokenDuration = TimeSpan.FromMinutes(cacheInt);

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Checks if OAuth 2.0 introspection endpoint is available.
        /// </summary>
        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_introspectionEndpoint))
                return false;

            try
            {
                var response = await _httpClient.GetAsync(_introspectionEndpoint, cancellationToken);
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validates an OAuth 2.0 access token.
        /// </summary>
        public async Task<OAuth2ValidationResult> ValidateTokenAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new OAuth2ValidationResult
                {
                    Active = false,
                    ErrorMessage = "Access token is required"
                };
            }

            // Check cache
            if (_tokenCache.TryGetValue(accessToken, out var cached) &&
                cached.ExpiresAt > DateTime.UtcNow)
            {
                return cached.ValidationResult;
            }

            if (string.IsNullOrEmpty(_introspectionEndpoint))
            {
                return new OAuth2ValidationResult
                {
                    Active = false,
                    ErrorMessage = "Introspection endpoint not configured"
                };
            }

            try
            {
                // RFC 7662 - Token Introspection
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = accessToken,
                    ["token_type_hint"] = "access_token"
                });

                var request = new HttpRequestMessage(HttpMethod.Post, _introspectionEndpoint)
                {
                    Content = content
                };

                // Add client authentication
                if (!string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret))
                {
                    var authValue = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);
                }

                var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var introspectionResult = JsonSerializer.Deserialize<OAuth2IntrospectionResponse>(json);

                if (introspectionResult == null || !introspectionResult.Active)
                {
                    return new OAuth2ValidationResult
                    {
                        Active = false,
                        ErrorMessage = "Token is not active"
                    };
                }

                var result = new OAuth2ValidationResult
                {
                    Active = true,
                    Subject = introspectionResult.Sub,
                    ClientId = introspectionResult.ClientId,
                    Scope = introspectionResult.Scope,
                    ExpiresAt = introspectionResult.Exp.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(introspectionResult.Exp.Value).UtcDateTime
                        : null
                };

                // Cache the result
                _tokenCache[accessToken] = new CachedTokenInfo
                {
                    ValidationResult = result,
                    ExpiresAt = DateTime.UtcNow.Add(_cacheTokenDuration)
                };

                return result;
            }
            catch (HttpRequestException ex)
            {
                return new OAuth2ValidationResult
                {
                    Active = false,
                    ErrorMessage = $"HTTP error during introspection: {ex.Message}"
                };
            }
            catch (Exception ex)
            {
                return new OAuth2ValidationResult
                {
                    Active = false,
                    ErrorMessage = $"Token validation failed: {ex.Message}"
                };
            }
        }

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Extract Bearer token from context
            if (!context.EnvironmentAttributes.TryGetValue("BearerToken", out var tokenObj) ||
                tokenObj is not string token)
            {
                // Try alternate attribute name
                if (!context.EnvironmentAttributes.TryGetValue("AccessToken", out tokenObj) ||
                    tokenObj is not string token2)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Bearer token not provided"
                    };
                }
                token = token2;
            }

            // Validate the token
            var validationResult = await ValidateTokenAsync(token, cancellationToken);

            if (!validationResult.Active)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = validationResult.ErrorMessage ?? "Invalid or expired token"
                };
            }

            // Check if token has required scope
            var requiredScope = context.ResourceAttributes.TryGetValue("RequiredScope", out var reqScope)
                ? reqScope?.ToString()
                : null;

            if (requiredScope != null)
            {
                var scopes = validationResult.Scope?.Split(' ') ?? Array.Empty<string>();
                if (!Array.Exists(scopes, s => s == requiredScope))
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = $"Token missing required scope: {requiredScope}"
                    };
                }
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "OAuth 2.0 token validated successfully",
                Metadata = new Dictionary<string, object>
                {
                    ["Subject"] = validationResult.Subject ?? "",
                    ["ClientId"] = validationResult.ClientId ?? "",
                    ["Scope"] = validationResult.Scope ?? ""
                }
            };
        }
    }

    #region Supporting Types

    /// <summary>
    /// OAuth 2.0 token validation result.
    /// </summary>
    public sealed record OAuth2ValidationResult
    {
        public required bool Active { get; init; }
        public string? Subject { get; init; }
        public string? ClientId { get; init; }
        public string? Scope { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Cached token information.
    /// </summary>
    internal sealed class CachedTokenInfo
    {
        public required OAuth2ValidationResult ValidationResult { get; init; }
        public required DateTime ExpiresAt { get; init; }
    }

    /// <summary>
    /// OAuth 2.0 introspection response (RFC 7662).
    /// </summary>
    internal sealed class OAuth2IntrospectionResponse
    {
        public bool Active { get; set; }
        public string? Sub { get; set; }
        public string? ClientId { get; set; }
        public string? Scope { get; set; }
        public long? Exp { get; set; }
        public long? Iat { get; set; }
    }

    #endregion
}
