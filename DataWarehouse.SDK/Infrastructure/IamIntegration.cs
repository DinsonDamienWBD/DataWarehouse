namespace DataWarehouse.SDK.Infrastructure;

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Federation;
using DataWarehouse.SDK.Security;

#region Identity Provider Interfaces

/// <summary>
/// Standard claims used across identity providers.
/// </summary>
public static class StandardClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string GivenName = "given_name";
    public const string FamilyName = "family_name";
    public const string PreferredUsername = "preferred_username";
    public const string Groups = "groups";
    public const string Roles = "roles";
    public const string TenantId = "tenant_id";
    public const string Audience = "aud";
    public const string Issuer = "iss";
    public const string Expiration = "exp";
    public const string IssuedAt = "iat";
    public const string NotBefore = "nbf";
    public const string Scope = "scope";
}

/// <summary>
/// Identity from an external provider.
/// </summary>
public sealed class ExternalIdentity
{
    /// <summary>Unique identifier from the provider.</summary>
    public string SubjectId { get; init; } = string.Empty;

    /// <summary>Provider name (aws, azure, google, etc.).</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Email address if available.</summary>
    public string? Email { get; init; }

    /// <summary>Display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Tenant/organization ID.</summary>
    public string? TenantId { get; init; }

    /// <summary>Groups/roles from the provider.</summary>
    public HashSet<string> Groups { get; init; } = new();

    /// <summary>All claims from the token.</summary>
    public Dictionary<string, string> Claims { get; init; } = new();

    /// <summary>When the identity was authenticated.</summary>
    public DateTimeOffset AuthenticatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the identity token expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Raw access token for API calls.</summary>
    public string? AccessToken { get; init; }

    /// <summary>Refresh token for token renewal.</summary>
    public string? RefreshToken { get; init; }
}

/// <summary>
/// Result of authentication attempt.
/// </summary>
public sealed class AuthenticationResult
{
    public bool IsSuccess { get; init; }
    public ExternalIdentity? Identity { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static AuthenticationResult Success(ExternalIdentity identity) =>
        new() { IsSuccess = true, Identity = identity };

    public static AuthenticationResult Failed(string code, string message) =>
        new() { IsSuccess = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Configuration for identity providers.
/// </summary>
public class IdentityProviderConfig
{
    /// <summary>Provider identifier.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Provider type (aws, azure, google, oidc, saml).</summary>
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>Whether this provider is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Priority for provider selection (lower = higher priority).</summary>
    public int Priority { get; set; } = 100;

    /// <summary>Claim mappings (external claim -> internal claim).</summary>
    public Dictionary<string, string> ClaimMappings { get; set; } = new();

    /// <summary>Group mappings (external group -> internal group).</summary>
    public Dictionary<string, string> GroupMappings { get; set; } = new();

    /// <summary>Provider-specific settings.</summary>
    public Dictionary<string, string> Settings { get; set; } = new();
}

/// <summary>
/// Interface for pluggable identity providers.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>Provider identifier.</summary>
    string ProviderId { get; }

    /// <summary>Provider type (aws, azure, google, oidc, saml).</summary>
    string ProviderType { get; }

    /// <summary>Authenticates with the given credentials/token.</summary>
    Task<AuthenticationResult> AuthenticateAsync(
        string credential,
        Dictionary<string, string>? context = null,
        CancellationToken ct = default);

    /// <summary>Validates an existing token.</summary>
    Task<AuthenticationResult> ValidateTokenAsync(
        string token,
        CancellationToken ct = default);

    /// <summary>Refreshes an expired token.</summary>
    Task<AuthenticationResult> RefreshTokenAsync(
        string refreshToken,
        CancellationToken ct = default);

    /// <summary>Revokes a token.</summary>
    Task<bool> RevokeTokenAsync(string token, CancellationToken ct = default);

    /// <summary>Gets the authorization URL for OAuth flows.</summary>
    string? GetAuthorizationUrl(string redirectUri, string state, string? scope = null);

    /// <summary>Exchanges an authorization code for tokens.</summary>
    Task<AuthenticationResult> ExchangeCodeAsync(
        string code,
        string redirectUri,
        CancellationToken ct = default);
}

#endregion

#region AWS IAM Provider

/// <summary>
/// AWS IAM configuration.
/// </summary>
public sealed class AwsIamConfig : IdentityProviderConfig
{
    public string Region { get; set; } = "us-east-1";
    public string? RoleArn { get; set; }
    public string? ExternalId { get; set; }
    public int SessionDurationSeconds { get; set; } = 3600;
    public string? WebIdentityTokenFile { get; set; }
    public string? IdentityPoolId { get; set; }
    public string? UserPoolId { get; set; }
    public string? AppClientId { get; set; }
}

/// <summary>
/// AWS IAM identity provider with STS AssumeRole support.
/// </summary>
public sealed class AwsIamProvider : IIdentityProvider
{
    private readonly AwsIamConfig _config;
    private readonly HttpClient _httpClient;

    public string ProviderId => _config.ProviderId;
    public string ProviderType => "aws";

    public AwsIamProvider(AwsIamConfig config)
    {
        _config = config;
        _httpClient = new HttpClient();
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string credential,
        Dictionary<string, string>? context = null,
        CancellationToken ct = default)
    {
        try
        {
            // Determine auth type from context
            var authType = context?.GetValueOrDefault("auth_type", "access_key") ?? "access_key";

            return authType switch
            {
                "access_key" => await AuthenticateWithAccessKeyAsync(credential, context, ct),
                "assume_role" => await AssumeRoleAsync(credential, context, ct),
                "web_identity" => await AssumeRoleWithWebIdentityAsync(credential, context, ct),
                "cognito" => await AuthenticateWithCognitoAsync(credential, context, ct),
                _ => AuthenticationResult.Failed("invalid_auth_type", $"Unknown auth type: {authType}")
            };
        }
        catch (Exception ex)
        {
            return AuthenticationResult.Failed("auth_error", ex.Message);
        }
    }

    private async Task<AuthenticationResult> AuthenticateWithAccessKeyAsync(
        string credential,
        Dictionary<string, string>? context,
        CancellationToken ct)
    {
        // Parse access key ID and secret from credential
        var parts = credential.Split(':');
        if (parts.Length != 2)
            return AuthenticationResult.Failed("invalid_credential", "Expected format: accessKeyId:secretAccessKey");

        var accessKeyId = parts[0];
        var secretAccessKey = parts[1];

        // Call STS GetCallerIdentity to validate credentials
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var date = DateTime.UtcNow.ToString("yyyyMMdd");

        var endpoint = $"https://sts.{_config.Region}.amazonaws.com/";
        var action = "GetCallerIdentity";

        // Create AWS Signature V4 request
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("X-Amz-Date", timestamp);

        var body = $"Action={action}&Version=2011-06-15";
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        // Sign the request (simplified - production would use full SigV4)
        var signature = ComputeAwsSignature(secretAccessKey, date, _config.Region, "sts", request, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("AWS4-HMAC-SHA256",
            $"Credential={accessKeyId}/{date}/{_config.Region}/sts/aws4_request, " +
            $"SignedHeaders=host;x-amz-date, Signature={signature}");

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("sts_error", "Failed to validate AWS credentials");

        var responseContent = await response.Content.ReadAsStringAsync(ct);

        // Parse response to get ARN and account
        var arn = ExtractXmlValue(responseContent, "Arn");
        var accountId = ExtractXmlValue(responseContent, "Account");
        var userId = ExtractXmlValue(responseContent, "UserId");

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = userId ?? accessKeyId,
            Provider = ProviderId,
            TenantId = accountId,
            Claims = new Dictionary<string, string>
            {
                ["arn"] = arn ?? string.Empty,
                ["account_id"] = accountId ?? string.Empty,
                ["access_key_id"] = accessKeyId
            },
            AccessToken = credential // Store for API calls
        });
    }

    private async Task<AuthenticationResult> AssumeRoleAsync(
        string credential,
        Dictionary<string, string>? context,
        CancellationToken ct)
    {
        var roleArn = context?.GetValueOrDefault("role_arn") ?? _config.RoleArn;
        if (string.IsNullOrEmpty(roleArn))
            return AuthenticationResult.Failed("missing_role", "Role ARN not specified");

        var sessionName = context?.GetValueOrDefault("session_name") ?? $"dw-session-{Guid.NewGuid():N}";

        // Build AssumeRole request
        var endpoint = $"https://sts.{_config.Region}.amazonaws.com/";
        var queryParams = new Dictionary<string, string>
        {
            ["Action"] = "AssumeRole",
            ["Version"] = "2011-06-15",
            ["RoleArn"] = roleArn,
            ["RoleSessionName"] = sessionName,
            ["DurationSeconds"] = _config.SessionDurationSeconds.ToString()
        };

        if (!string.IsNullOrEmpty(_config.ExternalId))
            queryParams["ExternalId"] = _config.ExternalId;

        var body = string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        // Use provided credentials to sign (in practice, would use AWS SDK)
        var parts = credential.Split(':');
        if (parts.Length != 2)
            return AuthenticationResult.Failed("invalid_credential", "Expected format: accessKeyId:secretAccessKey");

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var date = DateTime.UtcNow.ToString("yyyyMMdd");
        request.Headers.Add("X-Amz-Date", timestamp);

        var signature = ComputeAwsSignature(parts[1], date, _config.Region, "sts", request, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("AWS4-HMAC-SHA256",
            $"Credential={parts[0]}/{date}/{_config.Region}/sts/aws4_request, " +
            $"SignedHeaders=host;x-amz-date, Signature={signature}");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("assume_role_failed", $"AssumeRole failed: {responseContent}");

        // Parse temporary credentials
        var accessKeyId = ExtractXmlValue(responseContent, "AccessKeyId");
        var secretAccessKey = ExtractXmlValue(responseContent, "SecretAccessKey");
        var sessionToken = ExtractXmlValue(responseContent, "SessionToken");
        var expiration = ExtractXmlValue(responseContent, "Expiration");
        var assumedRoleArn = ExtractXmlValue(responseContent, "Arn");

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = assumedRoleArn ?? roleArn,
            Provider = ProviderId,
            Claims = new Dictionary<string, string>
            {
                ["role_arn"] = roleArn,
                ["session_name"] = sessionName,
                ["assumed_role_arn"] = assumedRoleArn ?? string.Empty
            },
            AccessToken = $"{accessKeyId}:{secretAccessKey}:{sessionToken}",
            ExpiresAt = DateTimeOffset.TryParse(expiration, out var exp) ? exp : null
        });
    }

    private async Task<AuthenticationResult> AssumeRoleWithWebIdentityAsync(
        string webIdentityToken,
        Dictionary<string, string>? context,
        CancellationToken ct)
    {
        var roleArn = context?.GetValueOrDefault("role_arn") ?? _config.RoleArn;
        if (string.IsNullOrEmpty(roleArn))
            return AuthenticationResult.Failed("missing_role", "Role ARN not specified");

        var sessionName = context?.GetValueOrDefault("session_name") ?? $"dw-web-{Guid.NewGuid():N}";

        var endpoint = $"https://sts.{_config.Region}.amazonaws.com/";
        var queryParams = new Dictionary<string, string>
        {
            ["Action"] = "AssumeRoleWithWebIdentity",
            ["Version"] = "2011-06-15",
            ["RoleArn"] = roleArn,
            ["RoleSessionName"] = sessionName,
            ["WebIdentityToken"] = webIdentityToken,
            ["DurationSeconds"] = _config.SessionDurationSeconds.ToString()
        };

        var body = string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("web_identity_failed", responseContent);

        var accessKeyId = ExtractXmlValue(responseContent, "AccessKeyId");
        var secretAccessKey = ExtractXmlValue(responseContent, "SecretAccessKey");
        var sessionToken = ExtractXmlValue(responseContent, "SessionToken");
        var expiration = ExtractXmlValue(responseContent, "Expiration");
        var subjectFromProvider = ExtractXmlValue(responseContent, "SubjectFromWebIdentityToken");

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = subjectFromProvider ?? roleArn,
            Provider = ProviderId,
            Claims = new Dictionary<string, string>
            {
                ["role_arn"] = roleArn,
                ["web_identity_subject"] = subjectFromProvider ?? string.Empty
            },
            AccessToken = $"{accessKeyId}:{secretAccessKey}:{sessionToken}",
            ExpiresAt = DateTimeOffset.TryParse(expiration, out var exp) ? exp : null
        });
    }

    private async Task<AuthenticationResult> AuthenticateWithCognitoAsync(
        string credential,
        Dictionary<string, string>? context,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.UserPoolId) || string.IsNullOrEmpty(_config.AppClientId))
            return AuthenticationResult.Failed("cognito_not_configured", "Cognito User Pool not configured");

        // Parse username:password
        var parts = credential.Split(':');
        if (parts.Length != 2)
            return AuthenticationResult.Failed("invalid_credential", "Expected format: username:password");

        var username = parts[0];
        var password = parts[1];

        // Call Cognito InitiateAuth
        var endpoint = $"https://cognito-idp.{_config.Region}.amazonaws.com/";
        var payload = new
        {
            AuthFlow = "USER_PASSWORD_AUTH",
            ClientId = _config.AppClientId,
            AuthParameters = new Dictionary<string, string>
            {
                ["USERNAME"] = username,
                ["PASSWORD"] = password
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("X-Amz-Target", "AWSCognitoIdentityProviderService.InitiateAuth");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/x-amz-json-1.1");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("cognito_auth_failed", responseContent);

        using var doc = JsonDocument.Parse(responseContent);
        var authResult = doc.RootElement.GetProperty("AuthenticationResult");

        var accessToken = authResult.GetProperty("AccessToken").GetString();
        var idToken = authResult.GetProperty("IdToken").GetString();
        var refreshToken = authResult.TryGetProperty("RefreshToken", out var rt) ? rt.GetString() : null;
        var expiresIn = authResult.GetProperty("ExpiresIn").GetInt32();

        // Decode ID token to get claims
        var claims = DecodeJwtClaims(idToken!);

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = claims.GetValueOrDefault("sub", username),
            Provider = ProviderId,
            Email = claims.GetValueOrDefault("email"),
            DisplayName = claims.GetValueOrDefault("name"),
            Groups = claims.TryGetValue("cognito:groups", out var groups)
                ? groups.Split(',').ToHashSet()
                : new HashSet<string>(),
            Claims = claims,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    public Task<AuthenticationResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        // For AWS, validation would involve checking STS GetCallerIdentity
        return AuthenticateAsync(token, new Dictionary<string, string> { ["auth_type"] = "access_key" }, ct);
    }

    public async Task<AuthenticationResult> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.AppClientId))
            return AuthenticationResult.Failed("cognito_not_configured", "Cognito not configured for refresh");

        var endpoint = $"https://cognito-idp.{_config.Region}.amazonaws.com/";
        var payload = new
        {
            AuthFlow = "REFRESH_TOKEN_AUTH",
            ClientId = _config.AppClientId,
            AuthParameters = new Dictionary<string, string>
            {
                ["REFRESH_TOKEN"] = refreshToken
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("X-Amz-Target", "AWSCognitoIdentityProviderService.InitiateAuth");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/x-amz-json-1.1");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("refresh_failed", responseContent);

        using var doc = JsonDocument.Parse(responseContent);
        var authResult = doc.RootElement.GetProperty("AuthenticationResult");

        var accessToken = authResult.GetProperty("AccessToken").GetString();
        var idToken = authResult.GetProperty("IdToken").GetString();
        var expiresIn = authResult.GetProperty("ExpiresIn").GetInt32();

        var claims = DecodeJwtClaims(idToken!);

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = claims.GetValueOrDefault("sub", "unknown"),
            Provider = ProviderId,
            Email = claims.GetValueOrDefault("email"),
            Claims = claims,
            AccessToken = accessToken,
            RefreshToken = refreshToken, // Keep original refresh token
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    public async Task<bool> RevokeTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.AppClientId))
            return false;

        var endpoint = $"https://cognito-idp.{_config.Region}.amazonaws.com/";
        var payload = new { Token = token, ClientId = _config.AppClientId };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("X-Amz-Target", "AWSCognitoIdentityProviderService.RevokeToken");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/x-amz-json-1.1");

        var response = await _httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public string? GetAuthorizationUrl(string redirectUri, string state, string? scope = null)
    {
        if (string.IsNullOrEmpty(_config.UserPoolId) || string.IsNullOrEmpty(_config.AppClientId))
            return null;

        var domain = _config.Settings.GetValueOrDefault("cognito_domain");
        if (string.IsNullOrEmpty(domain))
            return null;

        var scopes = scope ?? "openid email profile";
        return $"https://{domain}.auth.{_config.Region}.amazoncognito.com/oauth2/authorize?" +
               $"client_id={_config.AppClientId}&response_type=code&scope={Uri.EscapeDataString(scopes)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={state}";
    }

    public async Task<AuthenticationResult> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct = default)
    {
        var domain = _config.Settings.GetValueOrDefault("cognito_domain");
        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(_config.AppClientId))
            return AuthenticationResult.Failed("not_configured", "OAuth not configured");

        var endpoint = $"https://{domain}.auth.{_config.Region}.amazoncognito.com/oauth2/token";
        var body = $"grant_type=authorization_code&client_id={_config.AppClientId}" +
                   $"&code={code}&redirect_uri={Uri.EscapeDataString(redirectUri)}";

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("code_exchange_failed", responseContent);

        using var doc = JsonDocument.Parse(responseContent);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var idToken = doc.RootElement.GetProperty("id_token").GetString();
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        var claims = DecodeJwtClaims(idToken!);

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = claims.GetValueOrDefault("sub", "unknown"),
            Provider = ProviderId,
            Email = claims.GetValueOrDefault("email"),
            Claims = claims,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    private static string ComputeAwsSignature(string secretKey, string date, string region, string service,
        HttpRequestMessage request, string body)
    {
        // Simplified AWS Signature V4 - production would use full implementation
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes($"AWS4{secretKey}"));
        var dateKey = hmac.ComputeHash(Encoding.UTF8.GetBytes(date));
        using var hmac2 = new HMACSHA256(dateKey);
        var regionKey = hmac2.ComputeHash(Encoding.UTF8.GetBytes(region));
        using var hmac3 = new HMACSHA256(regionKey);
        var serviceKey = hmac3.ComputeHash(Encoding.UTF8.GetBytes(service));
        using var hmac4 = new HMACSHA256(serviceKey);
        var signingKey = hmac4.ComputeHash(Encoding.UTF8.GetBytes("aws4_request"));

        var stringToSign = $"AWS4-HMAC-SHA256\n{date}T000000Z\n{date}/{region}/{service}/aws4_request\n" +
                           Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        using var finalHmac = new HMACSHA256(signingKey);
        return Convert.ToHexString(finalHmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
    }

    private static string? ExtractXmlValue(string xml, string element)
    {
        var start = xml.IndexOf($"<{element}>", StringComparison.Ordinal);
        if (start < 0) return null;
        start += element.Length + 2;
        var end = xml.IndexOf($"</{element}>", start, StringComparison.Ordinal);
        return end > start ? xml[start..end] : null;
    }

    private static Dictionary<string, string> DecodeJwtClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return new Dictionary<string, string>();

        var payload = parts[1];
        // Add padding if needed
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        payload = payload.Replace('-', '+').Replace('_', '/');

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = JsonDocument.Parse(json);

        var claims = new Dictionary<string, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            claims[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : prop.Value.GetRawText();
        }
        return claims;
    }
}

#endregion

#region Azure AD Provider

/// <summary>
/// Azure AD configuration.
/// </summary>
public sealed class AzureAdConfig : IdentityProviderConfig
{
    public string TenantId { get; set; } = "common";
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string Authority => $"https://login.microsoftonline.com/{TenantId}";
    public string GraphApiEndpoint { get; set; } = "https://graph.microsoft.com/v1.0";
    public List<string> DefaultScopes { get; set; } = new() { "openid", "profile", "email" };
}

/// <summary>
/// Azure AD identity provider with OAuth2/OIDC and Graph API integration.
/// </summary>
public sealed class AzureAdProvider : IIdentityProvider
{
    private readonly AzureAdConfig _config;
    private readonly HttpClient _httpClient;

    public string ProviderId => _config.ProviderId;
    public string ProviderType => "azure";

    public AzureAdProvider(AzureAdConfig config)
    {
        _config = config;
        _httpClient = new HttpClient();
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string credential,
        Dictionary<string, string>? context = null,
        CancellationToken ct = default)
    {
        var authType = context?.GetValueOrDefault("auth_type", "client_credentials") ?? "client_credentials";

        return authType switch
        {
            "client_credentials" => await AuthenticateClientCredentialsAsync(ct),
            "password" => await AuthenticateWithPasswordAsync(credential, ct),
            "device_code" => await AuthenticateWithDeviceCodeAsync(context, ct),
            "managed_identity" => await AuthenticateWithManagedIdentityAsync(ct),
            _ => AuthenticationResult.Failed("invalid_auth_type", $"Unknown auth type: {authType}")
        };
    }

    private async Task<AuthenticationResult> AuthenticateClientCredentialsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.ClientSecret))
            return AuthenticationResult.Failed("missing_secret", "Client secret not configured");

        var endpoint = $"{_config.Authority}/oauth2/v2.0/token";
        var body = $"client_id={_config.ClientId}&client_secret={Uri.EscapeDataString(_config.ClientSecret)}" +
                   $"&scope={Uri.EscapeDataString("https://graph.microsoft.com/.default")}&grant_type=client_credentials";

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("auth_failed", responseContent);

        using var doc = JsonDocument.Parse(responseContent);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = _config.ClientId,
            Provider = ProviderId,
            TenantId = _config.TenantId,
            Claims = new Dictionary<string, string>
            {
                ["client_id"] = _config.ClientId,
                ["tenant_id"] = _config.TenantId
            },
            AccessToken = accessToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    private async Task<AuthenticationResult> AuthenticateWithPasswordAsync(string credential, CancellationToken ct)
    {
        var parts = credential.Split(':');
        if (parts.Length != 2)
            return AuthenticationResult.Failed("invalid_credential", "Expected format: username:password");

        var endpoint = $"{_config.Authority}/oauth2/v2.0/token";
        var scopes = string.Join(" ", _config.DefaultScopes);
        var body = $"client_id={_config.ClientId}&scope={Uri.EscapeDataString(scopes)}" +
                   $"&username={Uri.EscapeDataString(parts[0])}&password={Uri.EscapeDataString(parts[1])}" +
                   $"&grant_type=password";

        if (!string.IsNullOrEmpty(_config.ClientSecret))
            body += $"&client_secret={Uri.EscapeDataString(_config.ClientSecret)}";

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("auth_failed", responseContent);

        return await ParseTokenResponseAsync(responseContent, ct);
    }

    private async Task<AuthenticationResult> AuthenticateWithDeviceCodeAsync(
        Dictionary<string, string>? context, CancellationToken ct)
    {
        // Step 1: Get device code
        var endpoint = $"{_config.Authority}/oauth2/v2.0/devicecode";
        var scopes = string.Join(" ", _config.DefaultScopes);
        var body = $"client_id={_config.ClientId}&scope={Uri.EscapeDataString(scopes)}";

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("device_code_failed", responseContent);

        using var doc = JsonDocument.Parse(responseContent);
        var deviceCode = doc.RootElement.GetProperty("device_code").GetString();
        var userCode = doc.RootElement.GetProperty("user_code").GetString();
        var verificationUri = doc.RootElement.GetProperty("verification_uri").GetString();
        var interval = doc.RootElement.GetProperty("interval").GetInt32();
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        // Return pending result with user code info
        return AuthenticationResult.Failed("pending_user_action",
            $"Please visit {verificationUri} and enter code: {userCode}. Device code: {deviceCode}");
    }

    private async Task<AuthenticationResult> AuthenticateWithManagedIdentityAsync(CancellationToken ct)
    {
        // Azure IMDS endpoint for managed identity
        var endpoint = "http://169.254.169.254/metadata/identity/oauth2/token" +
                       "?api-version=2019-08-01&resource=https://graph.microsoft.com";

        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("Metadata", "true");

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return AuthenticationResult.Failed("imds_failed", responseContent);

            using var doc = JsonDocument.Parse(responseContent);
            var accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = int.Parse(doc.RootElement.GetProperty("expires_in").GetString() ?? "3600");
            var clientId = doc.RootElement.TryGetProperty("client_id", out var cid) ? cid.GetString() : null;

            return AuthenticationResult.Success(new ExternalIdentity
            {
                SubjectId = clientId ?? "managed-identity",
                Provider = ProviderId,
                TenantId = _config.TenantId,
                Claims = new Dictionary<string, string> { ["auth_type"] = "managed_identity" },
                AccessToken = accessToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            });
        }
        catch
        {
            return AuthenticationResult.Failed("imds_unavailable", "Managed identity endpoint not available");
        }
    }

    public async Task<AuthenticationResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        // Call Graph API to validate token and get user info
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.GraphApiEndpoint}/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("invalid_token", responseContent);

        using var doc = JsonDocument.Parse(responseContent);

        var id = doc.RootElement.GetProperty("id").GetString();
        var displayName = doc.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
        var email = doc.RootElement.TryGetProperty("mail", out var m) ? m.GetString() : null;
        var upn = doc.RootElement.TryGetProperty("userPrincipalName", out var u) ? u.GetString() : null;

        // Get group memberships
        var groups = await GetUserGroupsAsync(token, ct);

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = id ?? "unknown",
            Provider = ProviderId,
            Email = email ?? upn,
            DisplayName = displayName,
            TenantId = _config.TenantId,
            Groups = groups,
            Claims = new Dictionary<string, string>
            {
                ["oid"] = id ?? string.Empty,
                ["upn"] = upn ?? string.Empty
            },
            AccessToken = token
        });
    }

    private async Task<HashSet<string>> GetUserGroupsAsync(string token, CancellationToken ct)
    {
        var groups = new HashSet<string>();

        var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.GraphApiEndpoint}/me/memberOf");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return groups;

        var content = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(content);

        if (doc.RootElement.TryGetProperty("value", out var value))
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.TryGetProperty("displayName", out var name))
                    groups.Add(name.GetString() ?? string.Empty);
            }
        }

        return groups;
    }

    public async Task<AuthenticationResult> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var endpoint = $"{_config.Authority}/oauth2/v2.0/token";
        var scopes = string.Join(" ", _config.DefaultScopes);
        var body = $"client_id={_config.ClientId}&scope={Uri.EscapeDataString(scopes)}" +
                   $"&refresh_token={refreshToken}&grant_type=refresh_token";

        if (!string.IsNullOrEmpty(_config.ClientSecret))
            body += $"&client_secret={Uri.EscapeDataString(_config.ClientSecret)}";

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("refresh_failed", responseContent);

        return await ParseTokenResponseAsync(responseContent, ct);
    }

    public Task<bool> RevokeTokenAsync(string token, CancellationToken ct = default)
    {
        // Azure AD doesn't have a token revocation endpoint for access tokens
        // Revocation is done by revoking refresh tokens or user sessions
        return Task.FromResult(false);
    }

    public string? GetAuthorizationUrl(string redirectUri, string state, string? scope = null)
    {
        var scopes = scope ?? string.Join(" ", _config.DefaultScopes);
        return $"{_config.Authority}/oauth2/v2.0/authorize?" +
               $"client_id={_config.ClientId}&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&response_mode=query&scope={Uri.EscapeDataString(scopes)}&state={state}";
    }

    public async Task<AuthenticationResult> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct = default)
    {
        var endpoint = $"{_config.Authority}/oauth2/v2.0/token";
        var scopes = string.Join(" ", _config.DefaultScopes);
        var body = $"client_id={_config.ClientId}&scope={Uri.EscapeDataString(scopes)}" +
                   $"&code={code}&redirect_uri={Uri.EscapeDataString(redirectUri)}&grant_type=authorization_code";

        if (!string.IsNullOrEmpty(_config.ClientSecret))
            body += $"&client_secret={Uri.EscapeDataString(_config.ClientSecret)}";

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("code_exchange_failed", responseContent);

        return await ParseTokenResponseAsync(responseContent, ct);
    }

    private async Task<AuthenticationResult> ParseTokenResponseAsync(string responseContent, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(responseContent);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var idToken = doc.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        // Parse claims from ID token if available
        var claims = !string.IsNullOrEmpty(idToken) ? DecodeJwtClaims(idToken) : new Dictionary<string, string>();

        // Validate and get full user info
        var userInfo = await ValidateTokenAsync(accessToken!, ct);
        if (userInfo.IsSuccess && userInfo.Identity != null)
        {
            return AuthenticationResult.Success(new ExternalIdentity
            {
                SubjectId = userInfo.Identity.SubjectId,
                Provider = ProviderId,
                Email = userInfo.Identity.Email,
                DisplayName = userInfo.Identity.DisplayName,
                TenantId = userInfo.Identity.TenantId,
                Groups = userInfo.Identity.Groups,
                Claims = claims,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            });
        }

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = claims.GetValueOrDefault("sub", claims.GetValueOrDefault("oid", "unknown")),
            Provider = ProviderId,
            Email = claims.GetValueOrDefault("email"),
            DisplayName = claims.GetValueOrDefault("name"),
            TenantId = _config.TenantId,
            Claims = claims,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    private static Dictionary<string, string> DecodeJwtClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return new Dictionary<string, string>();

        var payload = parts[1].PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '=');
        payload = payload.Replace('-', '+').Replace('_', '/');

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = JsonDocument.Parse(json);

        var claims = new Dictionary<string, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            claims[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : prop.Value.GetRawText();
        }
        return claims;
    }
}

#endregion

#region Google Cloud IAM Provider

/// <summary>
/// Google Cloud IAM configuration.
/// </summary>
public sealed class GoogleCloudIamConfig : IdentityProviderConfig
{
    public string ProjectId { get; set; } = string.Empty;
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? ServiceAccountKeyPath { get; set; }
    public string? WorkloadIdentityProvider { get; set; }
    public List<string> DefaultScopes { get; set; } = new() { "openid", "email", "profile" };
}

/// <summary>
/// Google Cloud IAM provider with service accounts and workload identity.
/// </summary>
public sealed class GoogleCloudIamProvider : IIdentityProvider
{
    private readonly GoogleCloudIamConfig _config;
    private readonly HttpClient _httpClient;

    public string ProviderId => _config.ProviderId;
    public string ProviderType => "google";

    public GoogleCloudIamProvider(GoogleCloudIamConfig config)
    {
        _config = config;
        _httpClient = new HttpClient();
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string credential,
        Dictionary<string, string>? context = null,
        CancellationToken ct = default)
    {
        var authType = context?.GetValueOrDefault("auth_type", "service_account") ?? "service_account";

        return authType switch
        {
            "service_account" => await AuthenticateServiceAccountAsync(credential, ct),
            "workload_identity" => await AuthenticateWorkloadIdentityAsync(credential, context, ct),
            "oauth" => await AuthenticateOAuthAsync(credential, ct),
            "metadata" => await AuthenticateFromMetadataAsync(ct),
            _ => AuthenticationResult.Failed("invalid_auth_type", $"Unknown auth type: {authType}")
        };
    }

    private async Task<AuthenticationResult> AuthenticateServiceAccountAsync(string keyJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(keyJson);
            var clientEmail = doc.RootElement.GetProperty("client_email").GetString();
            var privateKey = doc.RootElement.GetProperty("private_key").GetString();
            var projectId = doc.RootElement.TryGetProperty("project_id", out var pid) ? pid.GetString() : _config.ProjectId;

            // Create JWT assertion
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" })));

            var claims = new
            {
                iss = clientEmail,
                scope = "https://www.googleapis.com/auth/cloud-platform",
                aud = "https://oauth2.googleapis.com/token",
                iat = now,
                exp = now + 3600
            };
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));

            // Sign with private key (simplified - would use RSA in production)
            var signatureInput = $"{header}.{payload}";
            var signature = SignWithRsa(signatureInput, privateKey!);

            var jwt = $"{signatureInput}.{signature}";

            // Exchange JWT for access token
            var endpoint = "https://oauth2.googleapis.com/token";
            var body = $"grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer&assertion={jwt}";

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

            var response = await _httpClient.SendAsync(request, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return AuthenticationResult.Failed("token_exchange_failed", responseContent);

            using var tokenDoc = JsonDocument.Parse(responseContent);
            var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = tokenDoc.RootElement.GetProperty("expires_in").GetInt32();

            return AuthenticationResult.Success(new ExternalIdentity
            {
                SubjectId = clientEmail ?? "unknown",
                Provider = ProviderId,
                Email = clientEmail,
                TenantId = projectId,
                Claims = new Dictionary<string, string>
                {
                    ["project_id"] = projectId ?? string.Empty,
                    ["auth_type"] = "service_account"
                },
                AccessToken = accessToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            });
        }
        catch (Exception ex)
        {
            return AuthenticationResult.Failed("invalid_key", ex.Message);
        }
    }

    private async Task<AuthenticationResult> AuthenticateWorkloadIdentityAsync(
        string idToken, Dictionary<string, string>? context, CancellationToken ct)
    {
        var provider = context?.GetValueOrDefault("provider") ?? _config.WorkloadIdentityProvider;
        if (string.IsNullOrEmpty(provider))
            return AuthenticationResult.Failed("missing_provider", "Workload identity provider not specified");

        // Exchange external token for Google token
        var endpoint = "https://sts.googleapis.com/v1/token";
        var payload = new
        {
            grantType = "urn:ietf:params:oauth:grant-type:token-exchange",
            audience = provider,
            scope = "https://www.googleapis.com/auth/cloud-platform",
            requestedTokenType = "urn:ietf:params:oauth:token-type:access_token",
            subjectToken = idToken,
            subjectTokenType = "urn:ietf:params:oauth:token-type:jwt"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("sts_exchange_failed", responseContent);

        using var doc = JsonDocument.Parse(responseContent);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        // Decode original token for identity info
        var claims = DecodeJwtClaims(idToken);

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = claims.GetValueOrDefault("sub", "unknown"),
            Provider = ProviderId,
            Email = claims.GetValueOrDefault("email"),
            TenantId = _config.ProjectId,
            Claims = claims,
            AccessToken = accessToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    private async Task<AuthenticationResult> AuthenticateOAuthAsync(string credential, CancellationToken ct)
    {
        // Parse username:password for OAuth password grant
        var parts = credential.Split(':');
        if (parts.Length != 2)
            return AuthenticationResult.Failed("invalid_credential", "Expected format: username:password");

        // Google doesn't support password grant for regular accounts
        // This would be for Google Workspace with domain-wide delegation
        return AuthenticationResult.Failed("not_supported",
            "Password authentication not supported. Use OAuth authorization code flow.");
    }

    private async Task<AuthenticationResult> AuthenticateFromMetadataAsync(CancellationToken ct)
    {
        // GCE/GKE metadata server
        var endpoint = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token";

        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("Metadata-Flavor", "Google");

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return AuthenticationResult.Failed("metadata_failed", responseContent);

            using var doc = JsonDocument.Parse(responseContent);
            var accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

            // Get service account email
            var emailEndpoint = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/email";
            var emailRequest = new HttpRequestMessage(HttpMethod.Get, emailEndpoint);
            emailRequest.Headers.Add("Metadata-Flavor", "Google");

            var emailResponse = await _httpClient.SendAsync(emailRequest, ct);
            var email = emailResponse.IsSuccessStatusCode
                ? await emailResponse.Content.ReadAsStringAsync(ct)
                : null;

            return AuthenticationResult.Success(new ExternalIdentity
            {
                SubjectId = email ?? "default",
                Provider = ProviderId,
                Email = email,
                TenantId = _config.ProjectId,
                Claims = new Dictionary<string, string> { ["auth_type"] = "metadata" },
                AccessToken = accessToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            });
        }
        catch
        {
            return AuthenticationResult.Failed("metadata_unavailable", "GCE metadata server not available");
        }
    }

    public async Task<AuthenticationResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        var endpoint = $"https://oauth2.googleapis.com/tokeninfo?access_token={token}";

        var response = await _httpClient.GetAsync(endpoint, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("invalid_token", responseContent);

        using var doc = JsonDocument.Parse(responseContent);

        var email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? int.Parse(exp.GetString() ?? "0") : 0;

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = email ?? "unknown",
            Provider = ProviderId,
            Email = email,
            TenantId = _config.ProjectId,
            AccessToken = token,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    public async Task<AuthenticationResult> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ClientId) || string.IsNullOrEmpty(_config.ClientSecret))
            return AuthenticationResult.Failed("not_configured", "OAuth not configured");

        var endpoint = "https://oauth2.googleapis.com/token";
        var body = $"client_id={_config.ClientId}&client_secret={_config.ClientSecret}" +
                   $"&refresh_token={refreshToken}&grant_type=refresh_token";

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("refresh_failed", responseContent);

        using var doc = JsonDocument.Parse(responseContent);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
        var idToken = doc.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() : null;

        var claims = !string.IsNullOrEmpty(idToken) ? DecodeJwtClaims(idToken) : new Dictionary<string, string>();

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = claims.GetValueOrDefault("sub", "unknown"),
            Provider = ProviderId,
            Email = claims.GetValueOrDefault("email"),
            Claims = claims,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    public async Task<bool> RevokeTokenAsync(string token, CancellationToken ct = default)
    {
        var endpoint = $"https://oauth2.googleapis.com/revoke?token={token}";
        var response = await _httpClient.PostAsync(endpoint, null, ct);
        return response.IsSuccessStatusCode;
    }

    public string? GetAuthorizationUrl(string redirectUri, string state, string? scope = null)
    {
        if (string.IsNullOrEmpty(_config.ClientId)) return null;

        var scopes = scope ?? string.Join(" ", _config.DefaultScopes);
        return $"https://accounts.google.com/o/oauth2/v2/auth?" +
               $"client_id={_config.ClientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&response_type=code&scope={Uri.EscapeDataString(scopes)}&state={state}&access_type=offline";
    }

    public async Task<AuthenticationResult> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ClientId) || string.IsNullOrEmpty(_config.ClientSecret))
            return AuthenticationResult.Failed("not_configured", "OAuth not configured");

        var endpoint = "https://oauth2.googleapis.com/token";
        var body = $"client_id={_config.ClientId}&client_secret={_config.ClientSecret}" +
                   $"&code={code}&redirect_uri={Uri.EscapeDataString(redirectUri)}&grant_type=authorization_code";

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("code_exchange_failed", responseContent);

        using var doc = JsonDocument.Parse(responseContent);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var idToken = doc.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        var claims = !string.IsNullOrEmpty(idToken) ? DecodeJwtClaims(idToken) : new Dictionary<string, string>();

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = claims.GetValueOrDefault("sub", "unknown"),
            Provider = ProviderId,
            Email = claims.GetValueOrDefault("email"),
            DisplayName = claims.GetValueOrDefault("name"),
            TenantId = _config.ProjectId,
            Claims = claims,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    private static string SignWithRsa(string input, string privateKeyPem)
    {
        // Extract key from PEM format (simplified)
        var keyData = privateKeyPem
            .Replace("-----BEGIN PRIVATE KEY-----", "")
            .Replace("-----END PRIVATE KEY-----", "")
            .Replace("-----BEGIN RSA PRIVATE KEY-----", "")
            .Replace("-----END RSA PRIVATE KEY-----", "")
            .Replace("\n", "")
            .Replace("\r", "");

        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(keyData), out _);

        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(input),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return Convert.ToBase64String(signature)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static Dictionary<string, string> DecodeJwtClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return new Dictionary<string, string>();

        var payload = parts[1].PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '=');
        payload = payload.Replace('-', '+').Replace('_', '/');

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = JsonDocument.Parse(json);

        var claims = new Dictionary<string, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            claims[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : prop.Value.GetRawText();
        }
        return claims;
    }
}

#endregion

#region OIDC/SAML Bridge

/// <summary>
/// Generic OIDC configuration.
/// </summary>
public sealed class OidcConfig : IdentityProviderConfig
{
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }
    public string? MetadataUrl { get; set; }
    public List<string> DefaultScopes { get; set; } = new() { "openid", "profile", "email" };
    public bool ValidateIssuer { get; set; } = true;
    public bool ValidateAudience { get; set; } = true;
}

/// <summary>
/// SAML configuration for enterprise SSO.
/// </summary>
public sealed class SamlConfig : IdentityProviderConfig
{
    public string EntityId { get; set; } = string.Empty;
    public string AssertionConsumerServiceUrl { get; set; } = string.Empty;
    public string? SingleSignOnUrl { get; set; }
    public string? SingleLogoutUrl { get; set; }
    public string? IdpCertificate { get; set; }
    public string? SpCertificate { get; set; }
    public string? SpPrivateKey { get; set; }
    public bool SignAuthnRequest { get; set; } = true;
    public bool WantAssertionsSigned { get; set; } = true;
}

/// <summary>
/// OIDC/SAML bridge for enterprise SSO with claim mapping.
/// </summary>
public sealed class OidcSamlBridge : IIdentityProvider
{
    private readonly IdentityProviderConfig _config;
    private readonly HttpClient _httpClient;
    private readonly OidcMetadata? _oidcMetadata;

    public string ProviderId => _config.ProviderId;
    public string ProviderType => _config.ProviderType;

    public OidcSamlBridge(IdentityProviderConfig config)
    {
        _config = config;
        _httpClient = new HttpClient();
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string credential,
        Dictionary<string, string>? context = null,
        CancellationToken ct = default)
    {
        return _config switch
        {
            OidcConfig oidc => await AuthenticateOidcAsync(oidc, credential, context, ct),
            SamlConfig saml => await AuthenticateSamlAsync(saml, credential, context, ct),
            _ => AuthenticationResult.Failed("unsupported", "Unsupported configuration type")
        };
    }

    private async Task<AuthenticationResult> AuthenticateOidcAsync(
        OidcConfig config, string credential, Dictionary<string, string>? context, CancellationToken ct)
    {
        // Load OIDC metadata if needed
        var metadata = await GetOidcMetadataAsync(config, ct);
        if (metadata == null)
            return AuthenticationResult.Failed("metadata_error", "Failed to load OIDC metadata");

        // Determine if this is a token or an auth code
        if (credential.Contains('.') && credential.Split('.').Length == 3)
        {
            // Looks like a JWT, validate it
            return await ValidateOidcTokenAsync(config, metadata, credential, ct);
        }

        // Otherwise treat as authorization code
        var redirectUri = context?.GetValueOrDefault("redirect_uri") ?? string.Empty;
        return await ExchangeCodeAsync(credential, redirectUri, ct);
    }

    private async Task<AuthenticationResult> AuthenticateSamlAsync(
        SamlConfig config, string samlResponse, Dictionary<string, string>? context, CancellationToken ct)
    {
        try
        {
            // Decode SAML response
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(samlResponse));

            // Parse SAML assertion (simplified - production would use full XML parsing)
            var nameId = ExtractSamlAttribute(decoded, "NameID");
            var email = ExtractSamlAttribute(decoded, "email") ?? ExtractSamlAttribute(decoded, "mail");
            var name = ExtractSamlAttribute(decoded, "displayName") ?? ExtractSamlAttribute(decoded, "name");
            var groups = ExtractSamlAttributes(decoded, "groups") ?? ExtractSamlAttributes(decoded, "memberOf");

            // Verify signature if configured
            if (config.WantAssertionsSigned && !string.IsNullOrEmpty(config.IdpCertificate))
            {
                if (!VerifySamlSignature(decoded, config.IdpCertificate))
                    return AuthenticationResult.Failed("invalid_signature", "SAML signature verification failed");
            }

            // Extract session info
            var notOnOrAfter = ExtractSamlAttribute(decoded, "NotOnOrAfter");
            DateTimeOffset? expiresAt = null;
            if (!string.IsNullOrEmpty(notOnOrAfter) && DateTimeOffset.TryParse(notOnOrAfter, out var exp))
                expiresAt = exp;

            // Map claims
            var claims = new Dictionary<string, string>
            {
                [StandardClaims.Subject] = nameId ?? string.Empty,
                [StandardClaims.Email] = email ?? string.Empty,
                [StandardClaims.Name] = name ?? string.Empty
            };

            // Apply claim mappings
            foreach (var mapping in config.ClaimMappings)
            {
                if (claims.TryGetValue(mapping.Key, out var value))
                    claims[mapping.Value] = value;
            }

            // Map groups
            var mappedGroups = new HashSet<string>();
            foreach (var group in groups ?? Array.Empty<string>())
            {
                if (config.GroupMappings.TryGetValue(group, out var mappedGroup))
                    mappedGroups.Add(mappedGroup);
                else
                    mappedGroups.Add(group);
            }

            return AuthenticationResult.Success(new ExternalIdentity
            {
                SubjectId = nameId ?? Guid.NewGuid().ToString(),
                Provider = ProviderId,
                Email = email,
                DisplayName = name,
                Groups = mappedGroups,
                Claims = claims,
                ExpiresAt = expiresAt
            });
        }
        catch (Exception ex)
        {
            return AuthenticationResult.Failed("saml_error", ex.Message);
        }
    }

    private async Task<OidcMetadata?> GetOidcMetadataAsync(OidcConfig config, CancellationToken ct)
    {
        var metadataUrl = config.MetadataUrl ?? $"{config.Authority}/.well-known/openid-configuration";

        try
        {
            var response = await _httpClient.GetAsync(metadataUrl, ct);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);

            return new OidcMetadata
            {
                Issuer = doc.RootElement.GetProperty("issuer").GetString() ?? string.Empty,
                AuthorizationEndpoint = doc.RootElement.GetProperty("authorization_endpoint").GetString() ?? string.Empty,
                TokenEndpoint = doc.RootElement.GetProperty("token_endpoint").GetString() ?? string.Empty,
                UserInfoEndpoint = doc.RootElement.TryGetProperty("userinfo_endpoint", out var ui) ? ui.GetString() : null,
                JwksUri = doc.RootElement.GetProperty("jwks_uri").GetString() ?? string.Empty,
                EndSessionEndpoint = doc.RootElement.TryGetProperty("end_session_endpoint", out var es) ? es.GetString() : null
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<AuthenticationResult> ValidateOidcTokenAsync(
        OidcConfig config, OidcMetadata metadata, string token, CancellationToken ct)
    {
        var claims = DecodeJwtClaims(token);

        // Validate issuer
        if (config.ValidateIssuer && claims.GetValueOrDefault("iss") != metadata.Issuer)
            return AuthenticationResult.Failed("invalid_issuer", "Token issuer mismatch");

        // Validate audience
        if (config.ValidateAudience)
        {
            var aud = claims.GetValueOrDefault("aud");
            if (aud != config.ClientId && !aud?.Contains(config.ClientId) == true)
                return AuthenticationResult.Failed("invalid_audience", "Token audience mismatch");
        }

        // Check expiration
        if (claims.TryGetValue("exp", out var expStr) && long.TryParse(expStr, out var expUnix))
        {
            var expTime = DateTimeOffset.FromUnixTimeSeconds(expUnix);
            if (expTime < DateTimeOffset.UtcNow)
                return AuthenticationResult.Failed("token_expired", "Token has expired");
        }

        // Map claims
        var mappedClaims = new Dictionary<string, string>();
        foreach (var claim in claims)
        {
            var targetClaim = config.ClaimMappings.GetValueOrDefault(claim.Key, claim.Key);
            mappedClaims[targetClaim] = claim.Value;
        }

        // Extract groups
        var groups = new HashSet<string>();
        if (claims.TryGetValue("groups", out var groupsJson))
        {
            try
            {
                using var groupsDoc = JsonDocument.Parse(groupsJson);
                foreach (var g in groupsDoc.RootElement.EnumerateArray())
                {
                    var groupName = g.GetString() ?? string.Empty;
                    var mappedGroup = config.GroupMappings.GetValueOrDefault(groupName, groupName);
                    groups.Add(mappedGroup);
                }
            }
            catch
            {
                // Single group value
                var mappedGroup = config.GroupMappings.GetValueOrDefault(groupsJson, groupsJson);
                groups.Add(mappedGroup);
            }
        }

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = claims.GetValueOrDefault("sub", "unknown"),
            Provider = ProviderId,
            Email = claims.GetValueOrDefault("email"),
            DisplayName = claims.GetValueOrDefault("name"),
            Groups = groups,
            Claims = mappedClaims,
            AccessToken = token,
            ExpiresAt = claims.TryGetValue("exp", out var exp) && long.TryParse(exp, out var expVal)
                ? DateTimeOffset.FromUnixTimeSeconds(expVal)
                : null
        });
    }

    public async Task<AuthenticationResult> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        if (_config is OidcConfig oidc)
        {
            var metadata = await GetOidcMetadataAsync(oidc, ct);
            if (metadata == null)
                return AuthenticationResult.Failed("metadata_error", "Failed to load OIDC metadata");
            return await ValidateOidcTokenAsync(oidc, metadata, token, ct);
        }

        return AuthenticationResult.Failed("not_supported", "Token validation not supported for this provider type");
    }

    public async Task<AuthenticationResult> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        if (_config is not OidcConfig oidc)
            return AuthenticationResult.Failed("not_supported", "Refresh not supported");

        var metadata = await GetOidcMetadataAsync(oidc, ct);
        if (metadata == null)
            return AuthenticationResult.Failed("metadata_error", "Failed to load metadata");

        var body = $"client_id={oidc.ClientId}&refresh_token={refreshToken}&grant_type=refresh_token";
        if (!string.IsNullOrEmpty(oidc.ClientSecret))
            body += $"&client_secret={Uri.EscapeDataString(oidc.ClientSecret)}";

        var request = new HttpRequestMessage(HttpMethod.Post, metadata.TokenEndpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("refresh_failed", responseContent);

        using var doc = JsonDocument.Parse(responseContent);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var newRefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : refreshToken;
        var idToken = doc.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        var claims = !string.IsNullOrEmpty(idToken) ? DecodeJwtClaims(idToken) : new Dictionary<string, string>();

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = claims.GetValueOrDefault("sub", "unknown"),
            Provider = ProviderId,
            Email = claims.GetValueOrDefault("email"),
            Claims = claims,
            AccessToken = accessToken,
            RefreshToken = newRefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    public Task<bool> RevokeTokenAsync(string token, CancellationToken ct = default)
    {
        // Would call revocation endpoint if available
        return Task.FromResult(false);
    }

    public string? GetAuthorizationUrl(string redirectUri, string state, string? scope = null)
    {
        if (_config is OidcConfig oidc)
        {
            var scopes = scope ?? string.Join(" ", oidc.DefaultScopes);
            return $"{oidc.Authority}/authorize?" +
                   $"client_id={oidc.ClientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                   $"&response_type=code&scope={Uri.EscapeDataString(scopes)}&state={state}";
        }

        if (_config is SamlConfig saml && !string.IsNullOrEmpty(saml.SingleSignOnUrl))
        {
            // Generate SAML AuthnRequest
            var authnRequest = GenerateSamlAuthnRequest(saml, redirectUri);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(authnRequest));
            return $"{saml.SingleSignOnUrl}?SAMLRequest={Uri.EscapeDataString(encoded)}&RelayState={state}";
        }

        return null;
    }

    public async Task<AuthenticationResult> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct = default)
    {
        if (_config is not OidcConfig oidc)
            return AuthenticationResult.Failed("not_supported", "Code exchange not supported");

        var metadata = await GetOidcMetadataAsync(oidc, ct);
        if (metadata == null)
            return AuthenticationResult.Failed("metadata_error", "Failed to load metadata");

        var body = $"client_id={oidc.ClientId}&code={code}" +
                   $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&grant_type=authorization_code";
        if (!string.IsNullOrEmpty(oidc.ClientSecret))
            body += $"&client_secret={Uri.EscapeDataString(oidc.ClientSecret)}";

        var request = new HttpRequestMessage(HttpMethod.Post, metadata.TokenEndpoint);
        request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        var response = await _httpClient.SendAsync(request, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return AuthenticationResult.Failed("code_exchange_failed", responseContent);

        using var doc = JsonDocument.Parse(responseContent);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var idToken = doc.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        var claims = !string.IsNullOrEmpty(idToken) ? DecodeJwtClaims(idToken) : new Dictionary<string, string>();

        return AuthenticationResult.Success(new ExternalIdentity
        {
            SubjectId = claims.GetValueOrDefault("sub", "unknown"),
            Provider = ProviderId,
            Email = claims.GetValueOrDefault("email"),
            DisplayName = claims.GetValueOrDefault("name"),
            Claims = claims,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn)
        });
    }

    private static string GenerateSamlAuthnRequest(SamlConfig config, string acsUrl)
    {
        var id = $"_{Guid.NewGuid():N}";
        var issueInstant = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        return $@"<samlp:AuthnRequest xmlns:samlp=""urn:oasis:names:tc:SAML:2.0:protocol""
            ID=""{id}"" Version=""2.0"" IssueInstant=""{issueInstant}""
            Destination=""{config.SingleSignOnUrl}""
            AssertionConsumerServiceURL=""{acsUrl}"">
            <saml:Issuer xmlns:saml=""urn:oasis:names:tc:SAML:2.0:assertion"">{config.EntityId}</saml:Issuer>
        </samlp:AuthnRequest>";
    }

    private static string? ExtractSamlAttribute(string xml, string name)
    {
        // Simplified extraction - production would use proper XML parsing
        var searchPatterns = new[]
        {
            $"<saml:Attribute Name=\"{name}\">.*?<saml:AttributeValue.*?>(.*?)</saml:AttributeValue>",
            $"<saml:{name}.*?>(.*?)</saml:{name}>",
            $"<{name}.*?>(.*?)</{name}>"
        };

        foreach (var pattern in searchPatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(xml, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    private static string[]? ExtractSamlAttributes(string xml, string name)
    {
        var values = new List<string>();
        var pattern = $"<saml:Attribute Name=\"{name}\">.*?</saml:Attribute>";
        var match = System.Text.RegularExpressions.Regex.Match(xml, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);

        if (match.Success)
        {
            var valuePattern = "<saml:AttributeValue.*?>(.*?)</saml:AttributeValue>";
            var valueMatches = System.Text.RegularExpressions.Regex.Matches(match.Value, valuePattern);
            foreach (System.Text.RegularExpressions.Match vm in valueMatches)
            {
                values.Add(vm.Groups[1].Value);
            }
        }

        return values.Count > 0 ? values.ToArray() : null;
    }

    private static bool VerifySamlSignature(string xml, string certificatePem)
    {
        // Simplified - production would use full XML signature verification
        return xml.Contains("<ds:Signature") || xml.Contains("<Signature");
    }

    private static Dictionary<string, string> DecodeJwtClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return new Dictionary<string, string>();

        var payload = parts[1].PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '=');
        payload = payload.Replace('-', '+').Replace('_', '/');

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        using var doc = JsonDocument.Parse(json);

        var claims = new Dictionary<string, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            claims[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : prop.Value.GetRawText();
        }
        return claims;
    }

    private sealed class OidcMetadata
    {
        public string Issuer { get; init; } = string.Empty;
        public string AuthorizationEndpoint { get; init; } = string.Empty;
        public string TokenEndpoint { get; init; } = string.Empty;
        public string? UserInfoEndpoint { get; init; }
        public string JwksUri { get; init; } = string.Empty;
        public string? EndSessionEndpoint { get; init; }
    }
}

#endregion

#region IAM Session Manager

/// <summary>
/// Session state for an authenticated user.
/// </summary>
public sealed class IamSession
{
    /// <summary>Unique session identifier.</summary>
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>The authenticated identity.</summary>
    public ExternalIdentity Identity { get; init; } = null!;

    /// <summary>Capability tokens granted to this session.</summary>
    public List<CapabilityToken> Capabilities { get; set; } = new();

    /// <summary>Federation groups the user belongs to.</summary>
    public HashSet<GroupId> Groups { get; set; } = new();

    /// <summary>When the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the session was last active.</summary>
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the session expires.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Whether the session is active.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Additional session metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Checks if the session is expired.</summary>
    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;
}

/// <summary>
/// Configuration for IAM session management.
/// </summary>
public sealed class IamSessionConfig
{
    /// <summary>Default session duration.</summary>
    public TimeSpan DefaultSessionDuration { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Maximum session duration.</summary>
    public TimeSpan MaxSessionDuration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Session inactivity timeout.</summary>
    public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often to run cleanup.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Token refresh threshold (refresh when this close to expiry).</summary>
    public TimeSpan TokenRefreshThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Enable automatic token refresh.</summary>
    public bool AutoRefreshTokens { get; set; } = true;
}

/// <summary>
/// Manages IAM sessions, token lifecycle, and integration with capabilities.
/// </summary>
public sealed class IamSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, IamSession> _sessions = new();
    private readonly ConcurrentDictionary<string, IIdentityProvider> _providers = new();
    private readonly IamSessionConfig _config;
    private readonly CapabilityIssuer? _capabilityIssuer;
    private readonly GroupRegistry? _groupRegistry;
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    /// <summary>Event raised when a session is created.</summary>
    public event EventHandler<IamSession>? SessionCreated;

    /// <summary>Event raised when a session expires or is terminated.</summary>
    public event EventHandler<IamSession>? SessionEnded;

    /// <summary>Event raised when tokens are refreshed.</summary>
    public event EventHandler<IamSession>? TokensRefreshed;

    public IamSessionManager(
        IamSessionConfig config,
        CapabilityIssuer? capabilityIssuer = null,
        GroupRegistry? groupRegistry = null)
    {
        _config = config;
        _capabilityIssuer = capabilityIssuer;
        _groupRegistry = groupRegistry;
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, _config.CleanupInterval, _config.CleanupInterval);
    }

    /// <summary>
    /// Registers an identity provider.
    /// </summary>
    public void RegisterProvider(IIdentityProvider provider)
    {
        _providers[provider.ProviderId] = provider;
    }

    /// <summary>
    /// Gets a registered provider.
    /// </summary>
    public IIdentityProvider? GetProvider(string providerId)
    {
        _providers.TryGetValue(providerId, out var provider);
        return provider;
    }

    /// <summary>
    /// Gets all registered providers.
    /// </summary>
    public IEnumerable<IIdentityProvider> GetProviders() => _providers.Values;

    /// <summary>
    /// Creates a session from an authentication result.
    /// </summary>
    public async Task<IamSession?> CreateSessionAsync(
        AuthenticationResult authResult,
        TimeSpan? duration = null,
        CancellationToken ct = default)
    {
        if (!authResult.IsSuccess || authResult.Identity == null)
            return null;

        var identity = authResult.Identity;
        var sessionDuration = duration ?? _config.DefaultSessionDuration;
        if (sessionDuration > _config.MaxSessionDuration)
            sessionDuration = _config.MaxSessionDuration;

        var session = new IamSession
        {
            Identity = identity,
            ExpiresAt = DateTimeOffset.UtcNow.Add(sessionDuration)
        };

        // Map external groups to federation groups
        if (_groupRegistry != null)
        {
            foreach (var externalGroup in identity.Groups)
            {
                // Try to find matching federation group
                var group = FindGroupByExternalName(externalGroup);
                if (group != null)
                    session.Groups.Add(group.Id);
            }
        }

        // Issue capability tokens for the session
        if (_capabilityIssuer != null)
        {
            var capability = _capabilityIssuer.Issue(
                CapabilityType.Federation,
                "*", // Federation-wide access based on groups
                CapabilityPermissions.Reader, // Default permissions
                session.SessionId,
                new CapabilityConstraints
                {
                    ExpiresAt = session.ExpiresAt,
                    AllowedGroups = session.Groups.Select(g => g.Value).ToHashSet()
                });

            session.Capabilities.Add(capability);
        }

        _sessions[session.SessionId] = session;
        SessionCreated?.Invoke(this, session);

        return session;
    }

    /// <summary>
    /// Gets a session by ID.
    /// </summary>
    public IamSession? GetSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            if (session.IsActive && !session.IsExpired)
            {
                session.LastActivityAt = DateTimeOffset.UtcNow;
                return session;
            }
        }
        return null;
    }

    /// <summary>
    /// Refreshes tokens for a session if needed.
    /// </summary>
    public async Task<bool> RefreshSessionTokensAsync(string sessionId, CancellationToken ct = default)
    {
        var session = GetSession(sessionId);
        if (session == null) return false;

        var identity = session.Identity;
        if (string.IsNullOrEmpty(identity.RefreshToken)) return false;

        // Check if refresh is needed
        if (identity.ExpiresAt.HasValue &&
            identity.ExpiresAt.Value - DateTimeOffset.UtcNow > _config.TokenRefreshThreshold)
            return true; // No refresh needed yet

        // Get the provider
        if (!_providers.TryGetValue(identity.Provider, out var provider))
            return false;

        // Refresh tokens
        var result = await provider.RefreshTokenAsync(identity.RefreshToken, ct);
        if (!result.IsSuccess || result.Identity == null)
            return false;

        // Update session with new identity
        _sessions[sessionId] = new IamSession
        {
            SessionId = session.SessionId,
            Identity = result.Identity,
            Capabilities = session.Capabilities,
            Groups = session.Groups,
            CreatedAt = session.CreatedAt,
            LastActivityAt = DateTimeOffset.UtcNow,
            ExpiresAt = session.ExpiresAt,
            IsActive = true,
            Metadata = session.Metadata
        };

        TokensRefreshed?.Invoke(this, _sessions[sessionId]);
        return true;
    }

    /// <summary>
    /// Terminates a session.
    /// </summary>
    public async Task<bool> TerminateSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return false;

        session.IsActive = false;

        // Revoke access token if possible
        var identity = session.Identity;
        if (!string.IsNullOrEmpty(identity.AccessToken) &&
            _providers.TryGetValue(identity.Provider, out var provider))
        {
            await provider.RevokeTokenAsync(identity.AccessToken, ct);
        }

        SessionEnded?.Invoke(this, session);
        return true;
    }

    /// <summary>
    /// Gets all active sessions for a subject.
    /// </summary>
    public IEnumerable<IamSession> GetSessionsForSubject(string subjectId)
    {
        return _sessions.Values
            .Where(s => s.IsActive && !s.IsExpired && s.Identity.SubjectId == subjectId);
    }

    /// <summary>
    /// Validates a capability token against a session.
    /// </summary>
    public bool ValidateCapability(
        string sessionId,
        CapabilityToken token,
        CapabilityPermissions requiredPermissions)
    {
        var session = GetSession(sessionId);
        if (session == null) return false;

        // Check if session has this token
        var sessionToken = session.Capabilities.FirstOrDefault(t => t.TokenId == token.TokenId);
        if (sessionToken == null) return false;

        // Check permissions
        return sessionToken.HasPermission(requiredPermissions);
    }

    /// <summary>
    /// Checks if a session has access to a resource via groups.
    /// </summary>
    public bool HasGroupAccess(string sessionId, string requiredGroupId)
    {
        var session = GetSession(sessionId);
        if (session == null) return false;

        return session.Groups.Any(g => g.Value == requiredGroupId);
    }

    private FederationGroup? FindGroupByExternalName(string externalName)
    {
        // This would be configured with external -> internal group mappings
        // For now, try to find by name match
        // In production, this would use a mapping table
        return null;
    }

    private void CleanupExpiredSessions(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        var toRemove = new List<string>();

        foreach (var kvp in _sessions)
        {
            var session = kvp.Value;

            // Check expiration
            if (session.IsExpired)
            {
                toRemove.Add(kvp.Key);
                continue;
            }

            // Check inactivity
            if (now - session.LastActivityAt > _config.InactivityTimeout)
            {
                toRemove.Add(kvp.Key);
                continue;
            }

            // Auto-refresh tokens if enabled
            if (_config.AutoRefreshTokens && session.Identity.ExpiresAt.HasValue)
            {
                var timeUntilExpiry = session.Identity.ExpiresAt.Value - now;
                if (timeUntilExpiry < _config.TokenRefreshThreshold)
                {
                    _ = RefreshSessionTokensAsync(kvp.Key, CancellationToken.None);
                }
            }
        }

        foreach (var sessionId in toRemove)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.IsActive = false;
                SessionEnded?.Invoke(this, session);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();
    }
}

#endregion

#region IAM Integration Registry

/// <summary>
/// Central registry for all IAM providers and sessions.
/// </summary>
public sealed class IamIntegrationRegistry
{
    private readonly IamSessionManager _sessionManager;
    private readonly Dictionary<string, IdentityProviderConfig> _providerConfigs = new();

    public IamIntegrationRegistry(
        IamSessionConfig sessionConfig,
        CapabilityIssuer? capabilityIssuer = null,
        GroupRegistry? groupRegistry = null)
    {
        _sessionManager = new IamSessionManager(sessionConfig, capabilityIssuer, groupRegistry);
    }

    /// <summary>Gets the session manager.</summary>
    public IamSessionManager Sessions => _sessionManager;

    /// <summary>
    /// Configures and registers an AWS IAM provider.
    /// </summary>
    public AwsIamProvider ConfigureAws(AwsIamConfig config)
    {
        var provider = new AwsIamProvider(config);
        _providerConfigs[config.ProviderId] = config;
        _sessionManager.RegisterProvider(provider);
        return provider;
    }

    /// <summary>
    /// Configures and registers an Azure AD provider.
    /// </summary>
    public AzureAdProvider ConfigureAzureAd(AzureAdConfig config)
    {
        var provider = new AzureAdProvider(config);
        _providerConfigs[config.ProviderId] = config;
        _sessionManager.RegisterProvider(provider);
        return provider;
    }

    /// <summary>
    /// Configures and registers a Google Cloud IAM provider.
    /// </summary>
    public GoogleCloudIamProvider ConfigureGoogleCloud(GoogleCloudIamConfig config)
    {
        var provider = new GoogleCloudIamProvider(config);
        _providerConfigs[config.ProviderId] = config;
        _sessionManager.RegisterProvider(provider);
        return provider;
    }

    /// <summary>
    /// Configures and registers a generic OIDC provider.
    /// </summary>
    public OidcSamlBridge ConfigureOidc(OidcConfig config)
    {
        var provider = new OidcSamlBridge(config);
        _providerConfigs[config.ProviderId] = config;
        _sessionManager.RegisterProvider(provider);
        return provider;
    }

    /// <summary>
    /// Configures and registers a SAML provider.
    /// </summary>
    public OidcSamlBridge ConfigureSaml(SamlConfig config)
    {
        var provider = new OidcSamlBridge(config);
        _providerConfigs[config.ProviderId] = config;
        _sessionManager.RegisterProvider(provider);
        return provider;
    }

    /// <summary>
    /// Authenticates using the specified provider.
    /// </summary>
    public async Task<IamSession?> AuthenticateAsync(
        string providerId,
        string credential,
        Dictionary<string, string>? context = null,
        CancellationToken ct = default)
    {
        var provider = _sessionManager.GetProvider(providerId);
        if (provider == null) return null;

        var result = await provider.AuthenticateAsync(credential, context, ct);
        if (!result.IsSuccess) return null;

        return await _sessionManager.CreateSessionAsync(result, ct: ct);
    }

    /// <summary>
    /// Gets the authorization URL for OAuth/SAML flows.
    /// </summary>
    public string? GetAuthorizationUrl(string providerId, string redirectUri, string state, string? scope = null)
    {
        var provider = _sessionManager.GetProvider(providerId);
        return provider?.GetAuthorizationUrl(redirectUri, state, scope);
    }

    /// <summary>
    /// Handles OAuth callback and creates session.
    /// </summary>
    public async Task<IamSession?> HandleCallbackAsync(
        string providerId,
        string code,
        string redirectUri,
        CancellationToken ct = default)
    {
        var provider = _sessionManager.GetProvider(providerId);
        if (provider == null) return null;

        var result = await provider.ExchangeCodeAsync(code, redirectUri, ct);
        if (!result.IsSuccess) return null;

        return await _sessionManager.CreateSessionAsync(result, ct: ct);
    }

    /// <summary>
    /// Gets provider configuration.
    /// </summary>
    public IdentityProviderConfig? GetProviderConfig(string providerId)
    {
        _providerConfigs.TryGetValue(providerId, out var config);
        return config;
    }
}

#endregion
