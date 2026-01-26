using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DataWarehouse.Dashboard.Security;
using System.ComponentModel.DataAnnotations;
using System.Collections.Concurrent;

namespace DataWarehouse.Dashboard.Controllers;

/// <summary>
/// API controller for authentication operations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly IJwtTokenService _tokenService;
    private readonly ILogger<AuthController> _logger;
    private readonly IConfiguration _configuration;

    // In-memory store for demo purposes. In production, use a database or distributed cache.
    private static readonly ConcurrentDictionary<string, UserCredential> _users = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, RefreshTokenInfo> _refreshTokens = new();

    static AuthController()
    {
        // Initialize default admin user (in production, use secure storage)
        _users["admin"] = new UserCredential
        {
            UserId = "admin-001",
            Username = "admin",
            // In production, store hashed passwords. This is for demo only.
            PasswordHash = HashPassword("admin"),
            Roles = new[] { UserRoles.Admin, UserRoles.Operator, UserRoles.User }
        };

        _users["operator"] = new UserCredential
        {
            UserId = "operator-001",
            Username = "operator",
            PasswordHash = HashPassword("operator"),
            Roles = new[] { UserRoles.Operator, UserRoles.User }
        };

        _users["user"] = new UserCredential
        {
            UserId = "user-001",
            Username = "user",
            PasswordHash = HashPassword("user"),
            Roles = new[] { UserRoles.User }
        };
    }

    public AuthController(
        IJwtTokenService tokenService,
        ILogger<AuthController> logger,
        IConfiguration configuration)
    {
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Authenticates a user and returns a JWT token.
    /// </summary>
    /// <param name="request">Login credentials.</param>
    /// <returns>JWT token if authentication succeeds.</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<LoginResponse> Login([FromBody] LoginRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        // Validate credentials
        if (!_users.TryGetValue(request.Username, out var user) ||
            !VerifyPassword(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("Failed login attempt for user '{Username}' from {IpAddress}",
                request.Username,
                HttpContext.Connection.RemoteIpAddress);

            return Unauthorized(new ErrorResponse
            {
                Error = "invalid_credentials",
                ErrorDescription = "Invalid username or password."
            });
        }

        // Generate token
        var tokenResult = _tokenService.GenerateToken(user.UserId, user.Username, user.Roles);

        // Store refresh token
        if (tokenResult.RefreshToken != null)
        {
            _refreshTokens[tokenResult.RefreshToken] = new RefreshTokenInfo
            {
                UserId = user.UserId,
                Username = user.Username,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                IsRevoked = false
            };
        }

        _logger.LogInformation("User '{Username}' logged in successfully from {IpAddress}",
            user.Username,
            HttpContext.Connection.RemoteIpAddress);

        return Ok(new LoginResponse
        {
            AccessToken = tokenResult.AccessToken,
            TokenType = tokenResult.TokenType,
            ExpiresIn = tokenResult.ExpiresIn,
            ExpiresAt = tokenResult.ExpiresAt,
            RefreshToken = tokenResult.RefreshToken,
            User = new UserInfo
            {
                UserId = user.UserId,
                Username = user.Username,
                Roles = user.Roles
            }
        });
    }

    /// <summary>
    /// Refreshes an access token using a refresh token.
    /// </summary>
    /// <param name="request">Refresh token request.</param>
    /// <returns>New JWT token if refresh succeeds.</returns>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public ActionResult<LoginResponse> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unauthorized(new ErrorResponse
            {
                Error = "invalid_token",
                ErrorDescription = "Refresh token is required."
            });
        }

        if (!_refreshTokens.TryGetValue(request.RefreshToken, out var tokenInfo) ||
            tokenInfo.IsRevoked ||
            tokenInfo.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogWarning("Invalid refresh token attempt from {IpAddress}",
                HttpContext.Connection.RemoteIpAddress);

            return Unauthorized(new ErrorResponse
            {
                Error = "invalid_token",
                ErrorDescription = "Invalid or expired refresh token."
            });
        }

        // Get user
        var user = _users.Values.FirstOrDefault(u => u.UserId == tokenInfo.UserId);
        if (user == null)
        {
            return Unauthorized(new ErrorResponse
            {
                Error = "invalid_token",
                ErrorDescription = "User no longer exists."
            });
        }

        // Revoke old refresh token
        tokenInfo.IsRevoked = true;

        // Generate new token
        var tokenResult = _tokenService.GenerateToken(user.UserId, user.Username, user.Roles);

        // Store new refresh token
        if (tokenResult.RefreshToken != null)
        {
            _refreshTokens[tokenResult.RefreshToken] = new RefreshTokenInfo
            {
                UserId = user.UserId,
                Username = user.Username,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                IsRevoked = false
            };
        }

        _logger.LogInformation("Token refreshed for user '{Username}'", user.Username);

        return Ok(new LoginResponse
        {
            AccessToken = tokenResult.AccessToken,
            TokenType = tokenResult.TokenType,
            ExpiresIn = tokenResult.ExpiresIn,
            ExpiresAt = tokenResult.ExpiresAt,
            RefreshToken = tokenResult.RefreshToken,
            User = new UserInfo
            {
                UserId = user.UserId,
                Username = user.Username,
                Roles = user.Roles
            }
        });
    }

    /// <summary>
    /// Logs out the current user by revoking the refresh token.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Logout([FromBody] LogoutRequest? request)
    {
        if (!string.IsNullOrWhiteSpace(request?.RefreshToken) &&
            _refreshTokens.TryGetValue(request.RefreshToken, out var tokenInfo))
        {
            tokenInfo.IsRevoked = true;
        }

        var username = User.Identity?.Name ?? "unknown";
        _logger.LogInformation("User '{Username}' logged out", username);

        return Ok(new { message = "Logged out successfully" });
    }

    /// <summary>
    /// Gets the current user's profile.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<UserInfo> GetCurrentUser()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var username = User.Identity?.Name;
        var roles = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToArray();

        if (userId == null || username == null)
        {
            return Unauthorized();
        }

        return Ok(new UserInfo
        {
            UserId = userId,
            Username = username,
            Roles = roles
        });
    }

    /// <summary>
    /// Changes the current user's password.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public ActionResult ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var username = User.Identity?.Name;
        if (username == null || !_users.TryGetValue(username, out var user))
        {
            return Unauthorized();
        }

        if (!VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            return BadRequest(new ErrorResponse
            {
                Error = "invalid_password",
                ErrorDescription = "Current password is incorrect."
            });
        }

        // Update password
        user.PasswordHash = HashPassword(request.NewPassword);

        _logger.LogInformation("Password changed for user '{Username}'", username);

        return Ok(new { message = "Password changed successfully" });
    }

    private static string HashPassword(string password)
    {
        // In production, use a proper password hashing algorithm like Argon2 or bcrypt
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(password + "DataWarehouse_Salt_2024");
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    private static bool VerifyPassword(string password, string hash)
    {
        return HashPassword(password) == hash;
    }

    private sealed class UserCredential
    {
        public required string UserId { get; init; }
        public required string Username { get; init; }
        public required string PasswordHash { get; set; }
        public required string[] Roles { get; init; }
    }

    private sealed class RefreshTokenInfo
    {
        public required string UserId { get; init; }
        public required string Username { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public bool IsRevoked { get; set; }
    }
}

#region Request/Response Models

/// <summary>
/// Login request model.
/// </summary>
public sealed class LoginRequest
{
    /// <summary>
    /// The username.
    /// </summary>
    [Required(ErrorMessage = "Username is required.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Username must be between 1 and 100 characters.")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The password.
    /// </summary>
    [Required(ErrorMessage = "Password is required.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Password must be between 1 and 100 characters.")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Login response model.
/// </summary>
public sealed class LoginResponse
{
    /// <summary>
    /// The JWT access token.
    /// </summary>
    public required string AccessToken { get; init; }

    /// <summary>
    /// The token type (always "Bearer").
    /// </summary>
    public string TokenType { get; init; } = "Bearer";

    /// <summary>
    /// Token expiration time in seconds.
    /// </summary>
    public long ExpiresIn { get; init; }

    /// <summary>
    /// Token expiration date/time.
    /// </summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>
    /// The refresh token for obtaining new access tokens.
    /// </summary>
    public string? RefreshToken { get; init; }

    /// <summary>
    /// The authenticated user's information.
    /// </summary>
    public UserInfo? User { get; init; }
}

/// <summary>
/// User information model.
/// </summary>
public sealed class UserInfo
{
    /// <summary>
    /// The user's unique identifier.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// The user's username.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// The user's roles.
    /// </summary>
    public required string[] Roles { get; init; }
}

/// <summary>
/// Refresh token request model.
/// </summary>
public sealed class RefreshTokenRequest
{
    /// <summary>
    /// The refresh token.
    /// </summary>
    [Required(ErrorMessage = "Refresh token is required.")]
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>
/// Logout request model.
/// </summary>
public sealed class LogoutRequest
{
    /// <summary>
    /// The refresh token to revoke.
    /// </summary>
    public string? RefreshToken { get; set; }
}

/// <summary>
/// Change password request model.
/// </summary>
public sealed class ChangePasswordRequest
{
    /// <summary>
    /// The current password.
    /// </summary>
    [Required(ErrorMessage = "Current password is required.")]
    public string CurrentPassword { get; set; } = string.Empty;

    /// <summary>
    /// The new password.
    /// </summary>
    [Required(ErrorMessage = "New password is required.")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// Error response model.
/// </summary>
public sealed class ErrorResponse
{
    /// <summary>
    /// The error code.
    /// </summary>
    public required string Error { get; init; }

    /// <summary>
    /// The error description.
    /// </summary>
    public required string ErrorDescription { get; init; }
}

#endregion
