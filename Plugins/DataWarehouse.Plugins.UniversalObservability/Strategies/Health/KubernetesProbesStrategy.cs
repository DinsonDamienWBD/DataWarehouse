using System.Net;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.Health;

/// <summary>
/// Observability strategy for Kubernetes health probes (liveness, readiness, startup).
/// Provides HTTP endpoints for k8s probe configuration and health status reporting.
/// </summary>
public sealed class KubernetesProbesStrategy : ObservabilityStrategyBase
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private string _prefix = "http://+:8080/";
    private volatile bool _isLive = true;
    private volatile bool _isReady = false;
    private volatile bool _isStarted = false;
    private readonly BoundedDictionary<string, HealthCheck> _healthChecks = new BoundedDictionary<string, HealthCheck>(1000);
    private readonly BoundedDictionary<string, string> _metadata = new BoundedDictionary<string, string>(1000);

    public override string StrategyId => "kubernetes-probes";
    public override string Name => "Kubernetes Probes";

    public KubernetesProbesStrategy() : base(new ObservabilityCapabilities(
        SupportsMetrics: false, SupportsTracing: false, SupportsLogging: false,
        SupportsDistributedTracing: false, SupportsAlerting: false,
        SupportedExporters: new[] { "Kubernetes", "K8sProbes", "HTTP" }))
    {
    }

    public void Configure(string prefix = "http://+:8080/")
    {
        _prefix = prefix;
    }

    /// <summary>
    /// Starts the HTTP listener for probes. Idempotent — calling when already started is a no-op.
    /// </summary>
    public void StartProbeServer()
    {
        if (_isStarted) return; // Idempotency guard — prevent listener and CTS leak

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();

        _serverTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    await HandleRequestAsync(context);
                }
                catch (Exception ex) when (_cts.Token.IsCancellationRequested)
                {
                    _ = ex; // Expected on shutdown
                    break;
                }
                catch (Exception ex)
                {
                    // Log unexpected errors from GetContextAsync but keep the probe server alive
                    System.Diagnostics.Trace.TraceWarning(
                        "[KubernetesProbes] GetContextAsync error: {0}", ex.Message);
                }
            }
        });

        _isStarted = true;
        _isReady = true;
    }

    /// <summary>
    /// Stops the HTTP listener.
    /// </summary>
    public void StopProbeServer()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _serverTask?.Wait(TimeSpan.FromSeconds(5));
        _listener?.Close();
        _isReady = false;
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var (statusCode, body) = path switch
        {
            "/livez" or "/healthz/live" => GetLivenessResponse(),
            "/readyz" or "/healthz/ready" => GetReadinessResponse(),
            "/startupz" or "/healthz/startup" => GetStartupResponse(),
            "/healthz" => GetFullHealthResponse(),
            _ => (404, "{\"error\":\"Not found\"}")
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var buffer = Encoding.UTF8.GetBytes(body);
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.Close();
    }

    private (int, string) GetLivenessResponse()
    {
        var status = _isLive ? 200 : 503;
        return (status, JsonSerializer.Serialize(new { status = _isLive ? "live" : "dead", timestamp = DateTime.UtcNow }));
    }

    private (int, string) GetReadinessResponse()
    {
        var allHealthy = _healthChecks.Values.All(h => h.IsHealthy);
        var isReady = _isReady && allHealthy;
        var status = isReady ? 200 : 503;

        return (status, JsonSerializer.Serialize(new
        {
            status = isReady ? "ready" : "not ready",
            checks = _healthChecks.ToDictionary(h => h.Key, h => new { healthy = h.Value.IsHealthy, message = h.Value.Message }),
            timestamp = DateTime.UtcNow
        }));
    }

    private (int, string) GetStartupResponse()
    {
        var status = _isStarted ? 200 : 503;
        return (status, JsonSerializer.Serialize(new { status = _isStarted ? "started" : "starting", timestamp = DateTime.UtcNow }));
    }

    private (int, string) GetFullHealthResponse()
    {
        var allHealthy = _isLive && _isReady && _healthChecks.Values.All(h => h.IsHealthy);
        var status = allHealthy ? 200 : 503;

        return (status, JsonSerializer.Serialize(new
        {
            status = allHealthy ? "healthy" : "unhealthy",
            liveness = _isLive,
            readiness = _isReady,
            startup = _isStarted,
            checks = _healthChecks.ToDictionary(h => h.Key, h => new { healthy = h.Value.IsHealthy, message = h.Value.Message }),
            metadata = _metadata,
            timestamp = DateTime.UtcNow
        }));
    }

    /// <summary>
    /// Sets the liveness state.
    /// </summary>
    public void SetLive(bool isLive) => _isLive = isLive;

    /// <summary>
    /// Sets the readiness state.
    /// </summary>
    public void SetReady(bool isReady) => _isReady = isReady;

    /// <summary>
    /// Sets the startup state.
    /// </summary>
    public void SetStarted(bool isStarted) => _isStarted = isStarted;

    /// <summary>
    /// Registers or updates a health check.
    /// </summary>
    public void RegisterHealthCheck(string name, bool isHealthy, string message = "")
    {
        _healthChecks[name] = new HealthCheck { IsHealthy = isHealthy, Message = message, LastCheck = DateTime.UtcNow };
    }

    /// <summary>
    /// Removes a health check.
    /// </summary>
    public void RemoveHealthCheck(string name) => _healthChecks.TryRemove(name, out _);

    /// <summary>
    /// Sets metadata visible in health responses.
    /// </summary>
    public void SetMetadata(string key, string value) => _metadata[key] = value;

    protected override Task MetricsAsyncCore(IEnumerable<MetricValue> metrics, CancellationToken ct)
    {
        IncrementCounter("kubernetes_probes.metrics_sent");
        // Update health checks based on metrics
        foreach (var metric in metrics.Where(m => m.Name.Contains("health", StringComparison.OrdinalIgnoreCase)))
        {
            RegisterHealthCheck(metric.Name, metric.Value > 0, $"Value: {metric.Value}");
        }
        return Task.CompletedTask;
    }

    protected override Task TracingAsyncCore(IEnumerable<SpanContext> spans, CancellationToken ct)
        => throw new NotSupportedException("Kubernetes probes do not support tracing");

    protected override Task LoggingAsyncCore(IEnumerable<LogEntry> logEntries, CancellationToken cancellationToken)
    {
        IncrementCounter("kubernetes_probes.logs_sent");
        // If critical errors, set liveness to false
        if (logEntries.Any(e => e.Level == LogLevel.Critical))
        {
            _isLive = false;
        }
        // If errors, set readiness to false
        if (logEntries.Any(e => e.Level == LogLevel.Error))
        {
            _isReady = false;
        }
        return Task.CompletedTask;
    }

    protected override Task<HealthCheckResult> HealthCheckAsyncCore(CancellationToken ct)
    {
        var allHealthy = _isLive && _isReady && _healthChecks.Values.All(h => h.IsHealthy);
        return Task.FromResult(new HealthCheckResult(allHealthy,
            allHealthy ? "Kubernetes probes healthy" : "Kubernetes probes indicate issues",
            new Dictionary<string, object>
            {
                ["live"] = _isLive,
                ["ready"] = _isReady,
                ["started"] = _isStarted,
                ["checksCount"] = _healthChecks.Count
            }));
    }


    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Configuration validated via Configure method
        IncrementCounter("kubernetes_probes.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }


    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("kubernetes_probes.shutdown");
        await base.ShutdownAsyncCore(cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopProbeServer();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }

    private class HealthCheck
    {
        public bool IsHealthy { get; set; }
        public string Message { get; set; } = "";
        public DateTime LastCheck { get; set; }
    }
}
