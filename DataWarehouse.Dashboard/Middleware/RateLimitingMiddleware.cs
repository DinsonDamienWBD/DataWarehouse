using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DataWarehouse.Dashboard.Middleware;

/// <summary>
/// Rate limiting middleware configuration options.
/// </summary>
public sealed class RateLimitingOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Gets or sets whether rate limiting is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the default permits per window.
    /// </summary>
    public int DefaultPermitsPerWindow { get; set; } = 100;

    /// <summary>
    /// Gets or sets the default window duration in seconds.
    /// </summary>
    public int DefaultWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the burst limit (additional permits for bursting).
    /// </summary>
    public int BurstLimit { get; set; } = 20;

    /// <summary>
    /// Gets or sets the rate limit for authenticated users.
    /// </summary>
    public int AuthenticatedPermitsPerWindow { get; set; } = 300;

    /// <summary>
    /// Gets or sets the rate limit for anonymous users.
    /// </summary>
    public int AnonymousPermitsPerWindow { get; set; } = 50;

    /// <summary>
    /// Gets or sets endpoint-specific rate limits.
    /// </summary>
    public Dictionary<string, EndpointRateLimit> EndpointLimits { get; set; } = new()
    {
        ["/api/auth/login"] = new EndpointRateLimit { PermitsPerWindow = 10, WindowSeconds = 60 },
        ["/api/auth/refresh"] = new EndpointRateLimit { PermitsPerWindow = 20, WindowSeconds = 60 },
        ["/api/health/ping"] = new EndpointRateLimit { PermitsPerWindow = 1000, WindowSeconds = 60 },
    };

    /// <summary>
    /// Gets or sets IP addresses to whitelist (exempt from rate limiting).
    /// </summary>
    public string[] WhitelistedIPs { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets whether to include rate limit headers in responses.
    /// </summary>
    public bool IncludeHeaders { get; set; } = true;
}

/// <summary>
/// Endpoint-specific rate limit configuration.
/// </summary>
public sealed class EndpointRateLimit
{
    public int PermitsPerWindow { get; set; }
    public int WindowSeconds { get; set; }
}

/// <summary>
/// Rate limiting middleware for ASP.NET Core.
/// Uses token bucket algorithm with per-client tracking.
/// </summary>
public sealed class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RateLimitingMiddleware> _logger;
    private readonly RateLimitingOptions _options;
    private readonly ConcurrentDictionary<string, ClientRateLimiter> _limiters = new();
    private readonly Timer _cleanupTimer;
    private readonly HashSet<string> _whitelistedIPs;

    public RateLimitingMiddleware(
        RequestDelegate next,
        ILogger<RateLimitingMiddleware> logger,
        IOptions<RateLimitingOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? new RateLimitingOptions();
        _whitelistedIPs = new HashSet<string>(_options.WhitelistedIPs, StringComparer.OrdinalIgnoreCase);

        // Cleanup expired limiters every 5 minutes
        _cleanupTimer = new Timer(CleanupExpiredLimiters, null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var clientKey = GetClientKey(context);
        var endpoint = context.Request.Path.Value ?? "/";

        // Check whitelist
        var clientIp = GetClientIp(context);
        if (_whitelistedIPs.Contains(clientIp))
        {
            await _next(context);
            return;
        }

        // Get or create limiter for this client
        var limiter = _limiters.GetOrAdd(clientKey, _ => CreateLimiter(context));

        // Determine permits needed (1 per request)
        var result = limiter.TryAcquire(endpoint);

        // Add rate limit headers
        if (_options.IncludeHeaders)
        {
            AddRateLimitHeaders(context.Response, result, limiter);
        }

        if (!result.IsAllowed)
        {
            _logger.LogWarning(
                "Rate limit exceeded for client {ClientKey} on endpoint {Endpoint}. Retry after {RetryAfter}s",
                clientKey, endpoint, result.RetryAfter.TotalSeconds);

            context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
            context.Response.ContentType = "application/json";

            if (result.RetryAfter > TimeSpan.Zero)
            {
                context.Response.Headers.RetryAfter = ((int)result.RetryAfter.TotalSeconds).ToString();
            }

            var response = new
            {
                error = "rate_limit_exceeded",
                message = result.Message,
                retryAfterSeconds = (int)result.RetryAfter.TotalSeconds
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
            return;
        }

        await _next(context);
    }

    private string GetClientKey(HttpContext context)
    {
        // Use authenticated user ID if available, otherwise use IP address
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(userId))
            {
                return $"user:{userId}";
            }
        }

        return $"ip:{GetClientIp(context)}";
    }

    private static string GetClientIp(HttpContext context)
    {
        // Check for forwarded IP (behind proxy/load balancer)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',')[0].Trim();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private ClientRateLimiter CreateLimiter(HttpContext context)
    {
        var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
        var basePermits = isAuthenticated
            ? _options.AuthenticatedPermitsPerWindow
            : _options.AnonymousPermitsPerWindow;

        return new ClientRateLimiter(
            basePermits,
            TimeSpan.FromSeconds(_options.DefaultWindowSeconds),
            _options.BurstLimit,
            _options.EndpointLimits);
    }

    private static void AddRateLimitHeaders(HttpResponse response, RateLimitAttemptResult result, ClientRateLimiter limiter)
    {
        var status = limiter.GetStatus();
        response.Headers["X-RateLimit-Limit"] = status.MaxPermits.ToString();
        response.Headers["X-RateLimit-Remaining"] = Math.Max(0, status.RemainingPermits).ToString();
        response.Headers["X-RateLimit-Reset"] = ((DateTimeOffset)status.WindowReset).ToUnixTimeSeconds().ToString();
    }

    private void CleanupExpiredLimiters(object? state)
    {
        var now = DateTime.UtcNow;
        var keysToRemove = _limiters
            .Where(kvp => kvp.Value.IsExpired(now))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _limiters.TryRemove(key, out _);
        }

        if (keysToRemove.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} expired rate limiters", keysToRemove.Count);
        }
    }
}

/// <summary>
/// Per-client rate limiter using token bucket algorithm.
/// </summary>
internal sealed class ClientRateLimiter
{
    private readonly int _basePermits;
    private readonly TimeSpan _windowDuration;
    private readonly int _burstLimit;
    private readonly Dictionary<string, EndpointRateLimit> _endpointLimits;
    private readonly ConcurrentDictionary<string, EndpointTokenBucket> _endpointBuckets = new();
    private readonly object _lock = new();

    private double _tokens;
    private DateTime _lastRefill;
    private DateTime _windowStart;

    public ClientRateLimiter(
        int basePermits,
        TimeSpan windowDuration,
        int burstLimit,
        Dictionary<string, EndpointRateLimit> endpointLimits)
    {
        _basePermits = basePermits;
        _windowDuration = windowDuration;
        _burstLimit = burstLimit;
        _endpointLimits = endpointLimits;
        _tokens = basePermits;
        _lastRefill = DateTime.UtcNow;
        _windowStart = DateTime.UtcNow;
    }

    public RateLimitAttemptResult TryAcquire(string endpoint)
    {
        // Check endpoint-specific limit first
        if (TryGetEndpointLimit(endpoint, out var endpointLimit))
        {
            var bucket = _endpointBuckets.GetOrAdd(endpoint,
                _ => new EndpointTokenBucket(endpointLimit.PermitsPerWindow, TimeSpan.FromSeconds(endpointLimit.WindowSeconds)));

            var endpointResult = bucket.TryAcquire();
            if (!endpointResult.IsAllowed)
            {
                return endpointResult;
            }
        }

        // Check global client limit
        lock (_lock)
        {
            RefillTokens();

            if (_tokens >= 1)
            {
                _tokens -= 1;
                return RateLimitAttemptResult.Allowed((int)_tokens);
            }

            var tokensNeeded = 1 - _tokens;
            var refillRate = (double)_basePermits / _windowDuration.TotalSeconds;
            var secondsToWait = tokensNeeded / refillRate;
            var retryAfter = TimeSpan.FromSeconds(Math.Ceiling(secondsToWait));

            return RateLimitAttemptResult.Denied(retryAfter, "Rate limit exceeded");
        }
    }

    private bool TryGetEndpointLimit(string endpoint, out EndpointRateLimit limit)
    {
        // Direct match
        if (_endpointLimits.TryGetValue(endpoint, out limit!))
        {
            return true;
        }

        // Prefix match
        foreach (var (pattern, endpointLimit) in _endpointLimits)
        {
            if (endpoint.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            {
                limit = endpointLimit;
                return true;
            }
        }

        limit = default!;
        return false;
    }

    private void RefillTokens()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastRefill;

        var refillRate = (double)_basePermits / _windowDuration.TotalSeconds;
        var tokensToAdd = elapsed.TotalSeconds * refillRate;

        _tokens = Math.Min(_tokens + tokensToAdd, _basePermits + _burstLimit);
        _lastRefill = now;

        if (now - _windowStart > _windowDuration)
        {
            _windowStart = now;
        }
    }

    public RateLimitStatus GetStatus()
    {
        lock (_lock)
        {
            RefillTokens();

            return new RateLimitStatus
            {
                MaxPermits = _basePermits,
                RemainingPermits = (int)_tokens,
                WindowReset = _windowStart + _windowDuration
            };
        }
    }

    public bool IsExpired(DateTime now)
    {
        lock (_lock)
        {
            return now - _lastRefill > TimeSpan.FromMinutes(10);
        }
    }
}

/// <summary>
/// Token bucket for endpoint-specific rate limiting.
/// </summary>
internal sealed class EndpointTokenBucket
{
    private readonly int _maxTokens;
    private readonly TimeSpan _windowDuration;
    private readonly object _lock = new();

    private double _tokens;
    private DateTime _lastRefill;

    public EndpointTokenBucket(int maxTokens, TimeSpan windowDuration)
    {
        _maxTokens = maxTokens;
        _windowDuration = windowDuration;
        _tokens = maxTokens;
        _lastRefill = DateTime.UtcNow;
    }

    public RateLimitAttemptResult TryAcquire()
    {
        lock (_lock)
        {
            RefillTokens();

            if (_tokens >= 1)
            {
                _tokens -= 1;
                return RateLimitAttemptResult.Allowed((int)_tokens);
            }

            var tokensNeeded = 1 - _tokens;
            var refillRate = (double)_maxTokens / _windowDuration.TotalSeconds;
            var secondsToWait = tokensNeeded / refillRate;

            return RateLimitAttemptResult.Denied(
                TimeSpan.FromSeconds(Math.Ceiling(secondsToWait)),
                "Endpoint rate limit exceeded");
        }
    }

    private void RefillTokens()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastRefill;

        var refillRate = (double)_maxTokens / _windowDuration.TotalSeconds;
        _tokens = Math.Min(_tokens + elapsed.TotalSeconds * refillRate, _maxTokens);
        _lastRefill = now;
    }
}

/// <summary>
/// Result of a rate limit attempt.
/// </summary>
public readonly struct RateLimitAttemptResult
{
    public bool IsAllowed { get; }
    public int RemainingPermits { get; }
    public TimeSpan RetryAfter { get; }
    public string Message { get; }

    private RateLimitAttemptResult(bool isAllowed, int remaining, TimeSpan retryAfter, string message)
    {
        IsAllowed = isAllowed;
        RemainingPermits = remaining;
        RetryAfter = retryAfter;
        Message = message;
    }

    public static RateLimitAttemptResult Allowed(int remaining) =>
        new(true, remaining, TimeSpan.Zero, string.Empty);

    public static RateLimitAttemptResult Denied(TimeSpan retryAfter, string message) =>
        new(false, 0, retryAfter, message);
}

/// <summary>
/// Current rate limit status.
/// </summary>
public sealed class RateLimitStatus
{
    public int MaxPermits { get; init; }
    public int RemainingPermits { get; init; }
    public DateTime WindowReset { get; init; }
}

/// <summary>
/// Extension methods for rate limiting middleware.
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Adds rate limiting services to the service collection.
    /// </summary>
    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));
        return services;
    }

    /// <summary>
    /// Uses rate limiting middleware in the pipeline.
    /// </summary>
    public static IApplicationBuilder UseRateLimiting(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RateLimitingMiddleware>();
    }
}
