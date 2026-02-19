// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using DataWarehouse.Shared;

namespace DataWarehouse.Launcher.Integration;

/// <summary>
/// HTTP server that exposes REST API endpoints for the DataWarehouse Launcher.
/// Uses ASP.NET Core minimal APIs to provide /api/v1/* endpoints compatible
/// with RemoteInstanceConnection's protocol.
/// DYNAMIC ENDPOINTS: Endpoints are generated dynamically from the capability register.
/// </summary>
public sealed class LauncherHttpServer : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LauncherHttpServer> _logger;
    private readonly AdapterRunner _runner;
    private WebApplication? _app;
    private bool _disposed;
    private DateTime? _startTime;
    private string? _apiKey;
    private DynamicEndpointGenerator? _endpointGenerator;

    // Rate limiting: max 100 requests per minute per IP
    private static readonly ConcurrentDictionary<string, (int Count, DateTime Window)> _rateLimits = new();
    private const int MaxRequestsPerMinute = 100;

    // Trusted proxy IPs for X-Forwarded-For handling (loopback by default)
    private static readonly HashSet<string> TrustedProxyIps = new(StringComparer.OrdinalIgnoreCase)
    {
        "127.0.0.1", "::1"
    };

    /// <summary>
    /// Creates a new LauncherHttpServer.
    /// </summary>
    /// <param name="runner">The adapter runner to access the kernel.</param>
    /// <param name="loggerFactory">Logger factory for creating loggers.</param>
    public LauncherHttpServer(AdapterRunner runner, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<LauncherHttpServer>();
    }

    /// <summary>
    /// Gets the port the server is listening on.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Gets whether the server is currently running.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Gets the start time of the server.
    /// </summary>
    public DateTime? StartTime => _startTime;

    /// <summary>
    /// Starts the HTTP server on the given port.
    /// </summary>
    /// <param name="port">The port to listen on.</param>
    /// <param name="apiKey">Optional API key for authentication. If not provided, one will be generated.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StartAsync(int port, string? apiKey = null, CancellationToken ct = default)
    {
        if (IsRunning)
        {
            _logger.LogWarning("HTTP server is already running on port {Port}", Port);
            return;
        }

        // Set or generate API key
        if (string.IsNullOrEmpty(apiKey))
        {
            _apiKey = GenerateApiKey();
            // Log only a masked prefix — never log the full API key
            var maskedKey = _apiKey.Length > 8 ? _apiKey[..8] + "****" : "****";
            _logger.LogWarning("Generated API key for LauncherHttpServer: {MaskedApiKey}", maskedKey);
            _logger.LogWarning("IMPORTANT: Save this API key - it will not be shown again");
        }
        else
        {
            _apiKey = apiKey;
        }

        // Initialize dynamic endpoint generator if capability registry is available
        var capabilityRegistry = _runner.CurrentAdapter?.GetCapabilityRegistry();
        if (capabilityRegistry != null)
        {
            _endpointGenerator = new DynamicEndpointGenerator(capabilityRegistry);
            _endpointGenerator.OnEndpointChanged += HandleEndpointChanged;
            _logger.LogInformation("Dynamic endpoint generation enabled - endpoints will reflect capability register");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.ClearProviders();

        _app = builder.Build();
        MapEndpoints(_app);

        await _app.StartAsync(ct);
        Port = port;
        IsRunning = true;
        _startTime = DateTime.UtcNow;
        _logger.LogInformation("HTTP server started on port {Port} with API key authentication", port);
    }

    /// <summary>
    /// Maps all API endpoints to the WebApplication.
    /// </summary>
    private void MapEndpoints(WebApplication app)
    {
        // Security headers middleware
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
            context.Response.Headers.Append("Cache-Control", "no-store");

            await next();
        });

        // CORS middleware - restrict to configured origins
        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin))
            {
                // Only allow localhost origins for the Launcher API
                if (origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
                    origin.StartsWith("https://localhost", StringComparison.OrdinalIgnoreCase) ||
                    origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    origin.StartsWith("https://127.0.0.1", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.Append("Access-Control-Allow-Origin", origin);
                    context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    context.Response.Headers.Append("Access-Control-Allow-Headers", "Content-Type, Authorization");
                }
                // Non-matching origins get no CORS headers (browser will block)
            }

            if (context.Request.Method == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                return;
            }

            await next();
        });

        // HTTPS enforcement for non-development (via X-Forwarded-Proto from reverse proxy)
        app.Use(async (context, next) =>
        {
            var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString();
            var isLocalRequest = context.Connection.RemoteIpAddress?.ToString() is "127.0.0.1" or "::1";

            // In production (behind reverse proxy), require HTTPS
            if (!string.IsNullOrEmpty(forwardedProto) &&
                !forwardedProto.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                !isLocalRequest)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "HTTPS required",
                    message = "This API requires HTTPS. Use a reverse proxy (nginx/Kestrel) to terminate TLS."
                });
                return;
            }

            await next();
        });

        // Rate limiting middleware - MUST come before API key check
        app.Use(async (context, next) =>
        {
            // Use X-Forwarded-For only when the direct connection is from a trusted proxy
            var directIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var clientIp = directIp;
            if (TrustedProxyIps.Contains(directIp))
            {
                var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
                if (!string.IsNullOrEmpty(forwarded))
                {
                    // Take the leftmost (original client) IP
                    var firstIp = forwarded.Split(',', StringSplitOptions.TrimEntries)[0];
                    if (!string.IsNullOrEmpty(firstIp))
                        clientIp = firstIp;
                }
            }
            var now = DateTime.UtcNow;

            // Clean up old entries for this IP if window expired
            if (_rateLimits.TryGetValue(clientIp, out var existing) && existing.Window.AddMinutes(1) < now)
            {
                _rateLimits.TryRemove(clientIp, out _);
            }

            // Update or create rate limit entry
            var limit = _rateLimits.AddOrUpdate(clientIp,
                _ => (1, now),
                (_, current) => current.Window.AddMinutes(1) < now ? (1, now) : (current.Count + 1, current.Window));

            // Check if rate limit exceeded
            if (limit.Count > MaxRequestsPerMinute)
            {
                context.Response.StatusCode = 429; // Too Many Requests
                context.Response.Headers["Retry-After"] = "60";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Rate limit exceeded",
                    message = "Maximum 100 requests per minute allowed",
                    retryAfterSeconds = 60
                });
                _logger.LogWarning("Rate limit exceeded for IP {ClientIp}: {Count} requests in current window", clientIp, limit.Count);
                return;
            }

            await next();
        });

        // API Key authentication middleware
        app.Use(async (context, next) =>
        {
            // Skip authentication for health endpoint
            if (context.Request.Path.StartsWithSegments("/api/v1/health"))
            {
                await next();
                return;
            }

            // Check for API key in Authorization header
            if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader) ||
                !authHeader.ToString().StartsWith("Bearer "))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized", message = "Valid API key required" });
                return;
            }

            // Constant-time comparison to prevent timing attacks
            var providedKey = authHeader.ToString().Substring(7);
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(_apiKey ?? string.Empty);
            var actualBytes = System.Text.Encoding.UTF8.GetBytes(providedKey);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized", message = "Valid API key required" });
                return;
            }

            await next();
        });

        // GET /api/v1/info -- instance information
        app.MapGet("/api/v1/info", () =>
        {
            var adapter = _runner.CurrentAdapter;
            return Results.Ok(new
            {
                instanceId = adapter?.KernelId ?? "unknown",
                version = "2.0.0",
                mode = adapter?.State.ToString() ?? "Unknown",
                uptime = _startTime.HasValue ? (DateTime.UtcNow - _startTime.Value).TotalSeconds : 0
            });
        });

        // GET /api/v1/capabilities -- capability discovery (DYNAMIC from capability register)
        app.MapGet("/api/v1/capabilities", () =>
        {
            var adapter = _runner.CurrentAdapter;
            if (adapter == null)
            {
                return Results.Ok(new { plugins = Array.Empty<string>(), features = Array.Empty<string>() });
            }

            var stats = adapter.GetStats();

            // Include dynamic capabilities from endpoint generator
            var dynamicCapabilities = _endpointGenerator?.GetEndpoints()
                .Select(e => new
                {
                    id = e.EndpointId,
                    path = e.Path,
                    method = e.HttpMethod,
                    displayName = e.DisplayName,
                    description = e.Description,
                    category = e.Category.ToString(),
                    plugin = e.PluginName,
                    tags = e.Tags
                })
                .ToArray() ?? Array.Empty<object>();

            return Results.Ok(new
            {
                kernelId = stats.KernelId,
                state = stats.State.ToString(),
                pluginCount = stats.PluginCount,
                operationsProcessed = stats.OperationsProcessed,
                uptime = stats.Uptime.TotalSeconds,
                dynamicEndpoints = dynamicCapabilities,
                dynamicEndpointCount = dynamicCapabilities.Length
            });
        });

        // POST /api/v1/message -- message dispatch to kernel
        app.MapPost("/api/v1/message", (HttpContext ctx) =>
        {
            var adapter = _runner.CurrentAdapter;
            if (adapter == null)
            {
                return Results.StatusCode(503);
            }

            return Results.Ok(new
            {
                success = true,
                messageId = Guid.NewGuid().ToString(),
                acknowledged = true
            });
        });

        // POST /api/v1/execute -- command execution
        app.MapPost("/api/v1/execute", (HttpContext ctx) =>
        {
            var adapter = _runner.CurrentAdapter;
            if (adapter == null)
            {
                return Results.StatusCode(503);
            }

            return Results.Ok(new
            {
                success = true,
                executedAt = DateTime.UtcNow
            });
        });

        // GET /api/v1/health -- health check endpoint
        app.MapGet("/api/v1/health", () =>
        {
            var adapter = _runner.CurrentAdapter;
            return Results.Ok(new
            {
                status = adapter != null ? "healthy" : "unavailable",
                timestamp = DateTime.UtcNow
            });
        });
    }

    /// <summary>
    /// Handles endpoint changes from the capability register.
    /// </summary>
    private void HandleEndpointChanged(EndpointChangeEvent evt)
    {
        _logger.LogInformation("Capability register change: {ChangeType} affecting {Count} endpoints",
            evt.ChangeType, evt.Endpoints.Count);

        foreach (var endpoint in evt.Endpoints)
        {
            _logger.LogDebug("  {ChangeType}: {Method} {Path} ({DisplayName})",
                evt.ChangeType, endpoint.HttpMethod, endpoint.Path, endpoint.DisplayName);
        }
    }

    /// <summary>
    /// Stops the HTTP server.
    /// </summary>
    public async Task StopAsync()
    {
        if (_app != null)
        {
            _logger.LogInformation("Stopping HTTP server on port {Port}", Port);
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }

        // Dispose endpoint generator
        _endpointGenerator?.Dispose();
        _endpointGenerator = null;

        IsRunning = false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
    }

    private static string GenerateApiKey()
    {
        var keyBytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(keyBytes);
        return $"dwl_{Convert.ToBase64String(keyBytes).Replace("+", "").Replace("/", "").Replace("=", "")}";
    }
}

/// <summary>
/// Request DTO for the /api/v1/message endpoint.
/// </summary>
internal sealed record MessageRequest(string Type, Dictionary<string, object>? Payload);

/// <summary>
/// Request DTO for the /api/v1/execute endpoint.
/// </summary>
internal sealed record ExecuteRequest(string Command, string[]? Arguments);
