using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace DataWarehouse.Dashboard.Security;

/// <summary>
/// Configuration options for JWT authentication.
/// </summary>
public sealed class JwtAuthenticationOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Authentication:Jwt";

    /// <summary>
    /// Gets or sets the secret key for signing tokens.
    /// In production, use a secure key store (Azure Key Vault, AWS Secrets Manager, etc.)
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the token issuer.
    /// </summary>
    public string Issuer { get; set; } = "DataWarehouse";

    /// <summary>
    /// Gets or sets the token audience.
    /// </summary>
    public string Audience { get; set; } = "DataWarehouse.Dashboard";

    /// <summary>
    /// Gets or sets the token expiration time.
    /// </summary>
    public TimeSpan TokenExpiration { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// Gets or sets the refresh token expiration time.
    /// </summary>
    public TimeSpan RefreshTokenExpiration { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets whether to validate the issuer.
    /// </summary>
    public bool ValidateIssuer { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to validate the audience.
    /// </summary>
    public bool ValidateAudience { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to validate the token lifetime.
    /// </summary>
    public bool ValidateLifetime { get; set; } = true;

    /// <summary>
    /// Gets or sets the clock skew tolerance for token validation.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the signing key from the secret key.
    /// </summary>
    public SymmetricSecurityKey GetSigningKey()
    {
        if (string.IsNullOrEmpty(SecretKey))
        {
            throw new InvalidOperationException(
                "JWT SecretKey is not configured. Set Authentication:Jwt:SecretKey in appsettings.json or environment variables.");
        }

        var keyBytes = Encoding.UTF8.GetBytes(SecretKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "JWT SecretKey must be at least 256 bits (32 characters) for HS256 algorithm.");
        }

        return new SymmetricSecurityKey(keyBytes);
    }

    /// <summary>
    /// Gets the token validation parameters.
    /// </summary>
    public TokenValidationParameters GetTokenValidationParameters()
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = ValidateIssuer,
            ValidIssuer = Issuer,
            ValidateAudience = ValidateAudience,
            ValidAudience = Audience,
            ValidateLifetime = ValidateLifetime,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = GetSigningKey(),
            ClockSkew = ClockSkew
        };
    }
}

/// <summary>
/// CORS configuration options.
/// </summary>
public sealed class CorsOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Cors";

    /// <summary>
    /// Gets or sets the allowed origins.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the allowed methods.
    /// </summary>
    public string[] AllowedMethods { get; set; } = new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS" };

    /// <summary>
    /// Gets or sets the allowed headers.
    /// </summary>
    public string[] AllowedHeaders { get; set; } = new[] { "Content-Type", "Authorization", "X-Requested-With", "X-Correlation-Id" };

    /// <summary>
    /// Gets or sets the exposed headers.
    /// </summary>
    public string[] ExposedHeaders { get; set; } = new[] { "X-Correlation-Id", "X-Request-Id" };

    /// <summary>
    /// Gets or sets whether to allow credentials.
    /// </summary>
    public bool AllowCredentials { get; set; } = true;

    /// <summary>
    /// Gets or sets the preflight max age in seconds.
    /// </summary>
    public int PreflightMaxAge { get; set; } = 86400; // 24 hours
}

/// <summary>
/// Service for generating and validating JWT tokens.
/// </summary>
public interface IJwtTokenService
{
    /// <summary>
    /// Generates a JWT token for the specified user.
    /// </summary>
    TokenResult GenerateToken(string userId, string username, IEnumerable<string> roles);

    /// <summary>
    /// Generates a refresh token.
    /// </summary>
    string GenerateRefreshToken();

    /// <summary>
    /// Validates a token and returns the claims principal.
    /// </summary>
    ClaimsPrincipal? ValidateToken(string token);
}

/// <summary>
/// Result of token generation.
/// </summary>
public sealed class TokenResult
{
    /// <summary>
    /// Gets or sets the access token.
    /// </summary>
    public required string AccessToken { get; init; }

    /// <summary>
    /// Gets or sets the token type (always "Bearer").
    /// </summary>
    public string TokenType { get; init; } = "Bearer";

    /// <summary>
    /// Gets or sets the token expiration time in seconds.
    /// </summary>
    public long ExpiresIn { get; init; }

    /// <summary>
    /// Gets or sets the token expiration date/time.
    /// </summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>
    /// Gets or sets the refresh token.
    /// </summary>
    public string? RefreshToken { get; init; }
}

/// <summary>
/// Default implementation of IJwtTokenService.
/// </summary>
public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtAuthenticationOptions _options;
    private readonly ILogger<JwtTokenService> _logger;

    public JwtTokenService(JwtAuthenticationOptions options, ILogger<JwtTokenService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public TokenResult GenerateToken(string userId, string username, IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(username);

        var now = DateTime.UtcNow;
        var expires = now.Add(_options.TokenExpiration);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.UniqueName, username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var key = _options.GetSigningKey();
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: credentials
        );

        var tokenHandler = new JwtSecurityTokenHandler();
        var accessToken = tokenHandler.WriteToken(token);

        _logger.LogInformation(
            "Generated JWT token for user {UserId} with roles [{Roles}], expires at {ExpiresAt}",
            userId,
            string.Join(", ", roles),
            expires);

        return new TokenResult
        {
            AccessToken = accessToken,
            ExpiresIn = (long)_options.TokenExpiration.TotalSeconds,
            ExpiresAt = expires,
            RefreshToken = GenerateRefreshToken()
        };
    }

    /// <inheritdoc />
    public string GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    /// <inheritdoc />
    public ClaimsPrincipal? ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var tokenHandler = new JwtSecurityTokenHandler();

        try
        {
            var validationParameters = _options.GetTokenValidationParameters();
            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);
            return principal;
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Token validation failed: {Message}", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during token validation");
            return null;
        }
    }
}

/// <summary>
/// Predefined authorization policies.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Policy requiring admin role.
    /// </summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>
    /// Policy requiring operator or admin role.
    /// </summary>
    public const string OperatorOrAdmin = "OperatorOrAdmin";

    /// <summary>
    /// Policy requiring any authenticated user.
    /// </summary>
    public const string Authenticated = "Authenticated";

    /// <summary>
    /// Policy for read-only access.
    /// </summary>
    public const string ReadOnly = "ReadOnly";
}

/// <summary>
/// Predefined user roles.
/// </summary>
public static class UserRoles
{
    /// <summary>
    /// Administrator role with full access.
    /// </summary>
    public const string Admin = "admin";

    /// <summary>
    /// Operator role with management access.
    /// </summary>
    public const string Operator = "operator";

    /// <summary>
    /// Standard user role.
    /// </summary>
    public const string User = "user";

    /// <summary>
    /// Read-only role.
    /// </summary>
    public const string ReadOnly = "readonly";
}
