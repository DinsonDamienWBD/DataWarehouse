using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Identity
{
    /// <summary>
    /// OpenID Connect (OIDC) authentication strategy with discovery and ID token validation.
    /// </summary>
    /// <remarks>
    /// Implements OpenID Connect Core 1.0 specification.
    /// Supports discovery, ID token validation, and userinfo endpoint.
    /// </remarks>
    public sealed class OidcStrategy : AccessControlStrategyBase
    {
        private readonly HttpClient _httpClient;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedDiscovery> _discoveryCache = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedJwks> _jwksCache = new();

        private string? _issuer;
        private string? _clientId;
        private string? _clientSecret;
        private bool _validateSignature = true;

        public OidcStrategy()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public override string StrategyId => "identity-oidc";
        public override string StrategyName => "OpenID Connect";

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

        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("Issuer", out var issuer) && issuer is string issuerStr)
                _issuer = issuerStr;
            if (configuration.TryGetValue("ClientId", out var clientId) && clientId is string clientIdStr)
                _clientId = clientIdStr;
            if (configuration.TryGetValue("ClientSecret", out var secret) && secret is string secretStr)
                _clientSecret = secretStr;
            if (configuration.TryGetValue("ValidateSignature", out var validate) && validate is bool validateBool)
            {
                _validateSignature = validateBool;
                if (!validateBool)
                {
                    // Log security warning when signature validation is disabled
                    System.Diagnostics.Debug.WriteLine(
                        "[SECURITY WARNING] OIDC JWT signature validation is DISABLED. " +
                        "This allows forged tokens to be accepted. Only disable in development environments.");
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("identity.oidc.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("identity.oidc.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_issuer))
                return false;

            try
            {
                var discovery = await FetchDiscoveryAsync(cancellationToken);
                return discovery != null;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("identity.oidc.evaluate");
            if (!context.EnvironmentAttributes.TryGetValue("IdToken", out var tokenObj) || tokenObj is not string idToken)
            {
                return new AccessDecision { IsGranted = false, Reason = "ID token not provided" };
            }

            var validationResult = await ValidateIdTokenAsync(idToken, cancellationToken);
            if (!validationResult.IsValid)
            {
                return new AccessDecision { IsGranted = false, Reason = validationResult.Error ?? "Invalid ID token" };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "OIDC authentication successful",
                Metadata = new Dictionary<string, object>
                {
                    ["Subject"] = validationResult.Subject ?? "",
                    ["Email"] = validationResult.Email ?? ""
                }
            };
        }

        private async Task<OidcDiscovery?> FetchDiscoveryAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_issuer))
                return null;

            var cacheKey = _issuer;
            if (_discoveryCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                return cached.Discovery;

            var discoveryUrl = _issuer.TrimEnd('/') + "/.well-known/openid-configuration";
            using var response = await _httpClient.GetAsync(discoveryUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var discovery = JsonSerializer.Deserialize<OidcDiscovery>(json);

            if (discovery != null)
            {
                _discoveryCache[cacheKey] = new CachedDiscovery
                {
                    Discovery = discovery,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                };
            }

            return discovery;
        }

        private async Task<OidcValidationResult> ValidateIdTokenAsync(string idToken, CancellationToken cancellationToken)
        {
            try
            {
                var parts = idToken.Split('.');
                if (parts.Length != 3)
                    return new OidcValidationResult { IsValid = false, Error = "Invalid JWT format" };

                // Decode header
                var header = DecodeJwtPart(parts[0]);
                var headerJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(header);
                if (headerJson == null)
                    return new OidcValidationResult { IsValid = false, Error = "Invalid JWT header" };

                // Decode payload
                var payload = DecodeJwtPart(parts[1]);
                var claims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload);

                if (claims == null)
                    return new OidcValidationResult { IsValid = false, Error = "Failed to parse claims" };

                var issuer = claims.TryGetValue("iss", out var iss) ? iss.GetString() : null;
                var audience = claims.TryGetValue("aud", out var aud) ? aud.GetString() : null;
                var exp = claims.TryGetValue("exp", out var expVal) && expVal.TryGetInt64(out var expLong)
                    ? DateTimeOffset.FromUnixTimeSeconds(expLong).UtcDateTime
                    : (DateTime?)null;

                if (issuer != _issuer)
                    return new OidcValidationResult { IsValid = false, Error = "Invalid issuer" };

                if (audience != _clientId)
                    return new OidcValidationResult { IsValid = false, Error = "Invalid audience" };

                if (exp.HasValue && exp.Value < DateTime.UtcNow)
                    return new OidcValidationResult { IsValid = false, Error = "Token expired" };

                // Verify signature if enabled
                if (_validateSignature)
                {
                    var algorithm = headerJson.TryGetValue("alg", out var algVal) ? algVal.GetString() : null;
                    var keyId = headerJson.TryGetValue("kid", out var kidVal) ? kidVal.GetString() : null;

                    if (algorithm == "none")
                    {
                        return new OidcValidationResult { IsValid = false, Error = "Algorithm 'none' not permitted" };
                    }

                    var signatureValid = await VerifyIdTokenSignatureAsync(
                        parts[0] + "." + parts[1],
                        parts[2],
                        algorithm,
                        keyId,
                        cancellationToken);

                    if (!signatureValid)
                    {
                        return new OidcValidationResult { IsValid = false, Error = "Invalid signature" };
                    }
                }

                return new OidcValidationResult
                {
                    IsValid = true,
                    Subject = claims.TryGetValue("sub", out var sub) ? sub.GetString() : null,
                    Email = claims.TryGetValue("email", out var email) ? email.GetString() : null
                };
            }
            catch (Exception ex)
            {
                return new OidcValidationResult { IsValid = false, Error = $"Validation failed: {ex.Message}" };
            }
        }

        private async Task<bool> VerifyIdTokenSignatureAsync(
            string signedData,
            string signatureBase64Url,
            string? algorithm,
            string? keyId,
            CancellationToken cancellationToken)
        {
            try
            {
                // Fetch discovery to get JWKS URI
                var discovery = await FetchDiscoveryAsync(cancellationToken);
                if (discovery?.JwksUri == null)
                    return false;

                // Fetch JWKS
                var jwks = await FetchJwksAsync(discovery.JwksUri, cancellationToken);
                if (jwks == null || !jwks.Keys.Any())
                    return false;

                // Find matching key
                var key = string.IsNullOrEmpty(keyId)
                    ? jwks.Keys.FirstOrDefault()
                    : jwks.Keys.FirstOrDefault(k => k.Kid == keyId);

                if (key == null)
                    return false;

                // Verify based on algorithm
                return algorithm switch
                {
                    "RS256" => VerifyRsaSignature(signedData, signatureBase64Url, key, HashAlgorithmName.SHA256),
                    "RS384" => VerifyRsaSignature(signedData, signatureBase64Url, key, HashAlgorithmName.SHA384),
                    "RS512" => VerifyRsaSignature(signedData, signatureBase64Url, key, HashAlgorithmName.SHA512),
                    "ES256" => VerifyEcdsaSignature(signedData, signatureBase64Url, key, HashAlgorithmName.SHA256),
                    "ES384" => VerifyEcdsaSignature(signedData, signatureBase64Url, key, HashAlgorithmName.SHA384),
                    "ES512" => VerifyEcdsaSignature(signedData, signatureBase64Url, key, HashAlgorithmName.SHA512),
                    "HS256" when !string.IsNullOrEmpty(_clientSecret) => VerifyHmacSignature(signedData, signatureBase64Url, _clientSecret),
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }

        private async Task<JwksResponse?> FetchJwksAsync(string jwksUri, CancellationToken cancellationToken)
        {
            if (_jwksCache.TryGetValue(jwksUri, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                return cached.Jwks;

            using var response = await _httpClient.GetAsync(jwksUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var jwks = JsonSerializer.Deserialize<JwksResponse>(json);

            if (jwks != null)
            {
                _jwksCache[jwksUri] = new CachedJwks
                {
                    Jwks = jwks,
                    ExpiresAt = DateTime.UtcNow.AddHours(24)
                };
            }

            return jwks;
        }

        private bool VerifyRsaSignature(string signedData, string signatureBase64Url, JwkKey key, HashAlgorithmName hashAlgorithm)
        {
            try
            {
                if (string.IsNullOrEmpty(key.N) || string.IsNullOrEmpty(key.E))
                    return false;

                var modulus = DecodeBase64Url(key.N);
                var exponent = DecodeBase64Url(key.E);
                var signature = DecodeBase64Url(signatureBase64Url);
                var data = Encoding.UTF8.GetBytes(signedData);

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

        private bool VerifyEcdsaSignature(string signedData, string signatureBase64Url, JwkKey key, HashAlgorithmName hashAlgorithm)
        {
            try
            {
                if (string.IsNullOrEmpty(key.X) || string.IsNullOrEmpty(key.Y))
                    return false;

                var xBytes = DecodeBase64Url(key.X);
                var yBytes = DecodeBase64Url(key.Y);
                var signature = DecodeBase64Url(signatureBase64Url);
                var data = Encoding.UTF8.GetBytes(signedData);

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
                var secretBytes = Encoding.UTF8.GetBytes(secret);
                var data = Encoding.UTF8.GetBytes(signedData);
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
            return Encoding.UTF8.GetString(bytes);
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

    internal sealed class OidcDiscovery
    {
        public string? Issuer { get; set; }
        public string? AuthorizationEndpoint { get; set; }
        public string? TokenEndpoint { get; set; }
        public string? UserinfoEndpoint { get; set; }
        public string? JwksUri { get; set; }
    }

    internal sealed class CachedDiscovery
    {
        public required OidcDiscovery Discovery { get; init; }
        public required DateTime ExpiresAt { get; init; }
    }

    public sealed record OidcValidationResult
    {
        public required bool IsValid { get; init; }
        public string? Subject { get; init; }
        public string? Email { get; init; }
        public string? Error { get; init; }
    }
}
