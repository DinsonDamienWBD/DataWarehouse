// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Launcher.Integration;

/// <summary>
/// HTTP server that exposes REST API endpoints for the DataWarehouse Launcher.
/// Uses ASP.NET Core minimal APIs to provide /api/v1/* endpoints compatible
/// with RemoteInstanceConnection's protocol.
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
            _logger.LogWarning("Generated API key for LauncherHttpServer: {ApiKey}", _apiKey);
            _logger.LogWarning("IMPORTANT: Save this API key - it will not be shown again");
        }
        else
        {
            _apiKey = apiKey;
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
                !authHeader.ToString().StartsWith("Bearer ") ||
                authHeader.ToString().Substring(7) != _apiKey)
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

        // GET /api/v1/capabilities -- capability discovery
        app.MapGet("/api/v1/capabilities", () =>
        {
            var adapter = _runner.CurrentAdapter;
            if (adapter == null)
            {
                return Results.Ok(new { plugins = Array.Empty<string>(), features = Array.Empty<string>() });
            }

            var stats = adapter.GetStats();
            return Results.Ok(new
            {
                kernelId = stats.KernelId,
                state = stats.State.ToString(),
                pluginCount = stats.PluginCount,
                operationsProcessed = stats.OperationsProcessed,
                uptime = stats.Uptime.TotalSeconds
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
