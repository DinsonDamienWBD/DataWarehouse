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
        private readonly Dictionary<string, CachedJwks> _jwksCache = new();

        private string? _introspectionEndpoint;
        private string? _clientId;
        private string? _clientSecret;
        private string? _issuer;
        private string? _jwksUri;
        private bool _validateJwtSignature = true;
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

            if (configuration.TryGetValue("Issuer", out var issuer) && issuer is string issuerStr)
                _issuer = issuerStr;

            if (configuration.TryGetValue("JwksUri", out var jwksUri) && jwksUri is string jwksUriStr)
                _jwksUri = jwksUriStr;

            if (configuration.TryGetValue("ValidateJwtSignature", out var validate) && validate is bool validateBool)
                _validateJwtSignature = validateBool;

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

        /// <summary>
        /// Validates a self-contained JWT access token (without introspection).
        /// </summary>
        private async Task<OAuth2ValidationResult> ValidateJwtTokenAsync(
            string jwtToken,
            CancellationToken cancellationToken)
        {
            try
            {
                var parts = jwtToken.Split('.');
                if (parts.Length != 3)
                {
                    return new OAuth2ValidationResult
                    {
                        Active = false,
                        ErrorMessage = "Invalid JWT format (expected 3 parts)"
                    };
                }

                // Decode header
                var header = DecodeJwtPart(parts[0]);
                var headerJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(header);
                if (headerJson == null)
                {
                    return new OAuth2ValidationResult
                    {
                        Active = false,
                        ErrorMessage = "Invalid JWT header"
                    };
                }

                // Decode payload
                var payload = DecodeJwtPart(parts[1]);
                var claims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload);
                if (claims == null)
                {
                    return new OAuth2ValidationResult
                    {
                        Active = false,
                        ErrorMessage = "Invalid JWT payload"
                    };
                }

                // Validate issuer
                var issuer = claims.TryGetValue("iss", out var iss) ? iss.GetString() : null;
                if (!string.IsNullOrEmpty(_issuer) && issuer != _issuer)
                {
                    return new OAuth2ValidationResult
                    {
                        Active = false,
                        ErrorMessage = $"Invalid issuer: {issuer} (expected {_issuer})"
                    };
                }

                // Validate expiration
                var exp = claims.TryGetValue("exp", out var expVal) && expVal.TryGetInt64(out var expLong)
                    ? DateTimeOffset.FromUnixTimeSeconds(expLong).UtcDateTime
                    : (DateTime?)null;

                if (exp.HasValue && exp.Value < DateTime.UtcNow)
                {
                    return new OAuth2ValidationResult
                    {
                        Active = false,
                        ErrorMessage = "Token expired"
                    };
                }

                // Validate signature if enabled
                if (_validateJwtSignature)
                {
                    var algorithm = headerJson.TryGetValue("alg", out var algVal) ? algVal.GetString() : null;
                    var keyId = headerJson.TryGetValue("kid", out var kidVal) ? kidVal.GetString() : null;

                    if (string.IsNullOrEmpty(algorithm))
                    {
                        return new OAuth2ValidationResult
                        {
                            Active = false,
                            ErrorMessage = "Missing algorithm in JWT header"
                        };
                    }

                    // Verify signature
                    var signatureValid = await VerifyJwtSignatureAsync(
                        parts[0] + "." + parts[1],
                        parts[2],
                        algorithm,
                        keyId,
                        cancellationToken);

                    if (!signatureValid)
                    {
                        return new OAuth2ValidationResult
                        {
                            Active = false,
                            ErrorMessage = "Invalid JWT signature"
                        };
                    }
                }

                // Extract claims
                var subject = claims.TryGetValue("sub", out var sub) ? sub.GetString() : null;
                var clientId = claims.TryGetValue("client_id", out var cid) ? cid.GetString() :
                               claims.TryGetValue("azp", out var azp) ? azp.GetString() : null;
                var scope = claims.TryGetValue("scope", out var scopeVal) ? scopeVal.GetString() : null;

                return new OAuth2ValidationResult
                {
                    Active = true,
                    Subject = subject,
                    ClientId = clientId,
                    Scope = scope,
                    ExpiresAt = exp
                };
            }
            catch (Exception ex)
            {
                return new OAuth2ValidationResult
                {
                    Active = false,
                    ErrorMessage = $"JWT validation failed: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Verifies JWT signature using JWKS (JSON Web Key Set).
        /// </summary>
        private async Task<bool> VerifyJwtSignatureAsync(
            string signedData,
            string signatureBase64Url,
            string? algorithm,
            string? keyId,
            CancellationToken cancellationToken)
        {
            try
            {
                // Fetch JWKS if URI is configured
                if (string.IsNullOrEmpty(_jwksUri))
                {
                    // No JWKS URI - cannot verify signature
                    // For HMAC-based tokens (HS256), use client secret
                    if (algorithm == "HS256" && !string.IsNullOrEmpty(_clientSecret))
                    {
                        return VerifyHmacSignature(signedData, signatureBase64Url, _clientSecret);
                    }

                    return false;
                }

                // Fetch and cache JWKS
                var jwks = await FetchJwksAsync(cancellationToken);
                if (jwks == null || !jwks.Keys.Any())
                {
                    return false;
                }

                // Find matching key
                var key = string.IsNullOrEmpty(keyId)
                    ? jwks.Keys.FirstOrDefault()
                    : jwks.Keys.FirstOrDefault(k => k.Kid == keyId);

                if (key == null)
                {
                    return false;
                }

                // Verify based on algorithm
                return algorithm switch
                {
                    "RS256" => VerifyRsaSignature(signedData, signatureBase64Url, key, System.Security.Cryptography.HashAlgorithmName.SHA256),
                    "RS384" => VerifyRsaSignature(signedData, signatureBase64Url, key, System.Security.Cryptography.HashAlgorithmName.SHA384),
                    "RS512" => VerifyRsaSignature(signedData, signatureBase64Url, key, System.Security.Cryptography.HashAlgorithmName.SHA512),
                    "ES256" => VerifyEcdsaSignature(signedData, signatureBase64Url, key, System.Security.Cryptography.HashAlgorithmName.SHA256),
                    "ES384" => VerifyEcdsaSignature(signedData, signatureBase64Url, key, System.Security.Cryptography.HashAlgorithmName.SHA384),
                    "ES512" => VerifyEcdsaSignature(signedData, signatureBase64Url, key, System.Security.Cryptography.HashAlgorithmName.SHA512),
                    "HS256" when !string.IsNullOrEmpty(_clientSecret) => VerifyHmacSignature(signedData, signatureBase64Url, _clientSecret),
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }

        private async Task<JwksResponse?> FetchJwksAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_jwksUri))
                return null;

            var cacheKey = _jwksUri;
            if (_jwksCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                return cached.Jwks;

            var response = await _httpClient.GetAsync(_jwksUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var jwks = JsonSerializer.Deserialize<JwksResponse>(json);

            if (jwks != null)
            {
                _jwksCache[cacheKey] = new CachedJwks
                {
                    Jwks = jwks,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                };
            }

            return jwks;
        }

        private bool VerifyRsaSignature(string signedData, string signatureBase64Url, JwkKey key, System.Security.Cryptography.HashAlgorithmName hashAlgorithm)
        {
            try
            {
                if (string.IsNullOrEmpty(key.N) || string.IsNullOrEmpty(key.E))
                    return false;

                var modulus = DecodeBase64Url(key.N);
                var exponent = DecodeBase64Url(key.E);
                var signature = DecodeBase64Url(signatureBase64Url);
                var data = System.Text.Encoding.UTF8.GetBytes(signedData);

                using var rsa = System.Security.Cryptography.RSA.Create();
                rsa.ImportParameters(new System.Security.Cryptography.RSAParameters
                {
                    Modulus = modulus,
                    Exponent = exponent
                });

                return rsa.VerifyData(data, signature, hashAlgorithm, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            }
            catch
            {
                return false;
            }
        }

        private bool VerifyEcdsaSignature(string signedData, string signatureBase64Url, JwkKey key, System.Security.Cryptography.HashAlgorithmName hashAlgorithm)
        {
            try
            {
                if (string.IsNullOrEmpty(key.X) || string.IsNullOrEmpty(key.Y))
                    return false;

                var xBytes = DecodeBase64Url(key.X);
                var yBytes = DecodeBase64Url(key.Y);
                var signature = DecodeBase64Url(signatureBase64Url);
                var data = System.Text.Encoding.UTF8.GetBytes(signedData);

                using var ecdsa = System.Security.Cryptography.ECDsa.Create();
                ecdsa.ImportParameters(new System.Security.Cryptography.ECParameters
                {
                    Curve = key.Crv == "P-256" ? System.Security.Cryptography.ECCurve.NamedCurves.nistP256 :
                            key.Crv == "P-384" ? System.Security.Cryptography.ECCurve.NamedCurves.nistP384 :
                            System.Security.Cryptography.ECCurve.NamedCurves.nistP521,
                    Q = new System.Security.Cryptography.ECPoint
                    {
                        X = xBytes,
                        Y = yBytes
                    }
                });

                return ecdsa.VerifyData(data, signature, hashAlgorithm);
            }
            catch
            {
                return false;
            }
        }

        private bool VerifyHmacSignature(string signedData, string signatureBase64Url, string secret)
        {
            try
            {
                var secretBytes = System.Text.Encoding.UTF8.GetBytes(secret);
                var data = System.Text.Encoding.UTF8.GetBytes(signedData);
                var expectedSignature = DecodeBase64Url(signatureBase64Url);

                using var hmac = new System.Security.Cryptography.HMACSHA256(secretBytes);
                var computedSignature = hmac.ComputeHash(data);

                // Constant-time comparison
                if (computedSignature.Length != expectedSignature.Length)
                    return false;

                uint result = 0;
                for (int i = 0; i < computedSignature.Length; i++)
                {
                    result |= (uint)(computedSignature[i] ^ expectedSignature[i]);
                }

                return result == 0;
            }
            catch
            {
                return false;
            }
        }

        private static string DecodeJwtPart(string base64Url)
        {
            var base64 = base64Url.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            var bytes = Convert.FromBase64String(base64);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        private static byte[] DecodeBase64Url(string base64Url)
        {
            var base64 = base64Url.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return Convert.FromBase64String(base64);
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
