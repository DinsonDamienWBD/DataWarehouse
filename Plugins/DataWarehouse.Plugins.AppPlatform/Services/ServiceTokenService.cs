using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.Plugins.AppPlatform.Models;

namespace DataWarehouse.Plugins.AppPlatform.Services;

/// <summary>
/// Manages service token lifecycle for registered applications.
/// Tokens are generated with cryptographically secure random keys, and only
/// the SHA-256 hash is stored. The raw key is returned exactly once at creation.
/// Supports creation, validation with caching, rotation, and revocation.
/// </summary>
internal sealed class ServiceTokenService
{
    /// <summary>
    /// Thread-safe dictionary storing all service tokens keyed by TokenId.
    /// </summary>
    private readonly ConcurrentDictionary<string, ServiceToken> _tokens = new();

    /// <summary>
    /// Cache for token validation results keyed by token hash, with 30-second TTL.
    /// Improves performance for repeated validation of the same token.
    /// </summary>
    private readonly ConcurrentDictionary<string, (TokenValidationResult Result, DateTime ExpiresAt)> _validationCache = new();

    /// <summary>
    /// TTL for cached validation results.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates a new service token for the specified application.
    /// Generates a 32-byte random key, computes its SHA-256 hash, and stores
    /// only the hash. The raw key is returned exactly once to the caller.
    /// </summary>
    /// <param name="appId">The application identifier to issue the token for.</param>
    /// <param name="scopes">Service scopes the token is authorized to access.</param>
    /// <param name="validity">Duration the token remains valid from creation.</param>
    /// <returns>A tuple of the raw key (returned once) and the stored <see cref="ServiceToken"/>.</returns>
    public Task<(string RawKey, ServiceToken Token)> CreateTokenAsync(
        string appId,
        string[] scopes,
        TimeSpan validity)
    {
        // Generate 32 cryptographically secure random bytes
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var rawKey = Convert.ToBase64String(randomBytes);

        // Compute SHA-256 hash of the raw key
        var tokenHash = ComputeHash(rawKey);

        var token = new ServiceToken
        {
            TokenId = Guid.NewGuid().ToString("N"),
            AppId = appId,
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(validity),
            AllowedScopes = scopes
        };

        _tokens[token.TokenId] = token;

        return Task.FromResult((rawKey, token));
    }

    /// <summary>
    /// Validates a raw token key by computing its hash and matching against stored tokens.
    /// Uses a validation cache with 30-second TTL for performance.
    /// </summary>
    /// <param name="rawKey">The raw key to validate.</param>
    /// <returns>A <see cref="TokenValidationResult"/> indicating validity and associated metadata.</returns>
    public Task<TokenValidationResult> ValidateTokenAsync(string rawKey)
    {
        var hash = ComputeHash(rawKey);

        // Check validation cache first
        if (_validationCache.TryGetValue(hash, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return Task.FromResult(cached.Result);
        }

        // Find token by hash match
        var matchingToken = _tokens.Values.FirstOrDefault(t =>
            t.TokenHash == hash &&
            !t.IsRevoked &&
            t.ExpiresAt > DateTime.UtcNow);

        TokenValidationResult result;

        if (matchingToken is not null)
        {
            result = new TokenValidationResult
            {
                IsValid = true,
                AppId = matchingToken.AppId,
                TokenId = matchingToken.TokenId,
                AllowedScopes = matchingToken.AllowedScopes
            };
        }
        else
        {
            // Determine specific failure reason
            var anyMatch = _tokens.Values.FirstOrDefault(t => t.TokenHash == hash);
            string failureReason;

            if (anyMatch is null)
                failureReason = "Token not found";
            else if (anyMatch.IsRevoked)
                failureReason = "Token has been revoked";
            else if (anyMatch.ExpiresAt <= DateTime.UtcNow)
                failureReason = "Token has expired";
            else
                failureReason = "Token validation failed";

            result = new TokenValidationResult
            {
                IsValid = false,
                FailureReason = failureReason
            };
        }

        // Cache the result
        _validationCache[hash] = (result, DateTime.UtcNow.Add(CacheTtl));

        return Task.FromResult(result);
    }

    /// <summary>
    /// Rotates a service token by revoking the old token and creating a new one.
    /// The old token must be valid for rotation to succeed.
    /// </summary>
    /// <param name="appId">The application identifier.</param>
    /// <param name="oldRawKey">The raw key of the token to rotate.</param>
    /// <param name="scopes">Service scopes for the new token.</param>
    /// <param name="validity">Duration the new token remains valid.</param>
    /// <returns>A tuple of the new raw key and token, or <c>null</c> if the old token is invalid.</returns>
    public async Task<(string RawKey, ServiceToken Token)?> RotateTokenAsync(
        string appId,
        string oldRawKey,
        string[] scopes,
        TimeSpan validity)
    {
        var oldHash = ComputeHash(oldRawKey);

        // Validate the old token
        var validation = await ValidateTokenAsync(oldRawKey);
        if (!validation.IsValid)
            return null;

        // Revoke the old token
        var oldToken = _tokens.Values.FirstOrDefault(t => t.TokenHash == oldHash && !t.IsRevoked);
        if (oldToken is not null)
        {
            var revoked = oldToken with
            {
                IsRevoked = true,
                RevokedAt = DateTime.UtcNow,
                RevocationReason = "Rotated"
            };
            _tokens[oldToken.TokenId] = revoked;
        }

        // Invalidate cache for old hash
        _validationCache.TryRemove(oldHash, out _);

        // Create a new token
        return await CreateTokenAsync(appId, scopes, validity);
    }

    /// <summary>
    /// Revokes a specific service token by its identifier.
    /// </summary>
    /// <param name="tokenId">The identifier of the token to revoke.</param>
    /// <param name="reason">The reason for revocation.</param>
    /// <returns><c>true</c> if the token was found and revoked; <c>false</c> otherwise.</returns>
    public Task<bool> RevokeTokenAsync(string tokenId, string reason)
    {
        if (!_tokens.TryGetValue(tokenId, out var existing))
            return Task.FromResult(false);

        var revoked = existing with
        {
            IsRevoked = true,
            RevokedAt = DateTime.UtcNow,
            RevocationReason = reason
        };

        _tokens[tokenId] = revoked;

        // Invalidate cache entry for this token's hash
        _validationCache.TryRemove(existing.TokenHash, out _);

        return Task.FromResult(true);
    }

    /// <summary>
    /// Revokes all tokens issued to the specified application.
    /// </summary>
    /// <param name="appId">The application identifier whose tokens should be revoked.</param>
    /// <returns>The number of tokens that were revoked.</returns>
    public Task<int> RevokeAllTokensForAppAsync(string appId)
    {
        var count = 0;
        var appTokens = _tokens.Values.Where(t => t.AppId == appId && !t.IsRevoked).ToArray();

        foreach (var token in appTokens)
        {
            var revoked = token with
            {
                IsRevoked = true,
                RevokedAt = DateTime.UtcNow,
                RevocationReason = "All tokens revoked for application"
            };

            _tokens[token.TokenId] = revoked;

            // Invalidate cache entry
            _validationCache.TryRemove(token.TokenHash, out _);

            count++;
        }

        return Task.FromResult(count);
    }

    /// <summary>
    /// Computes a SHA-256 hash of the input string and returns it as a Base64-encoded string.
    /// </summary>
    /// <param name="input">The string to hash.</param>
    /// <returns>Base64-encoded SHA-256 hash.</returns>
    private static string ComputeHash(string input)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(hashBytes);
    }
}
