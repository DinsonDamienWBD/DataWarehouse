using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Federation;

#region Enums and Core Types

/// <summary>
/// Status of a federated DataWarehouse instance.
/// </summary>
public enum InstanceStatus
{
    /// <summary>Instance is healthy and accepting requests.</summary>
    Healthy,

    /// <summary>Instance is experiencing degraded performance.</summary>
    Degraded,

    /// <summary>Instance is unreachable or not responding.</summary>
    Unhealthy,

    /// <summary>Instance status is unknown (not yet checked).</summary>
    Unknown,

    /// <summary>Instance has been explicitly disconnected.</summary>
    Disconnected
}

/// <summary>
/// Authentication method for instance communication.
/// </summary>
public enum InstanceAuthMethod
{
    /// <summary>No authentication (not recommended for production).</summary>
    None,

    /// <summary>Bearer token authentication.</summary>
    BearerToken,

    /// <summary>API key authentication.</summary>
    ApiKey,

    /// <summary>Mutual TLS (mTLS) authentication.</summary>
    MutualTls,

    /// <summary>OAuth 2.0 client credentials.</summary>
    OAuth2ClientCredentials,

    /// <summary>JSON Web Token (JWT) authentication.</summary>
    Jwt
}

/// <summary>
/// Conflict resolution strategy for knowledge from multiple sources.
/// </summary>
public enum ConflictResolutionStrategy
{
    /// <summary>Take the most recently updated knowledge.</summary>
    LastWriteWins,

    /// <summary>Take knowledge from the highest priority instance.</summary>
    HighestPriority,

    /// <summary>Merge knowledge from all sources.</summary>
    Merge,

    /// <summary>Require manual resolution.</summary>
    Manual,

    /// <summary>Take knowledge with highest trust score.</summary>
    HighestTrust,

    /// <summary>Use consensus from majority of instances.</summary>
    MajorityConsensus,

    /// <summary>Use vector clock for causal ordering.</summary>
    VectorClock
}

/// <summary>
/// Capabilities that a federated instance can expose.
/// </summary>
[Flags]
public enum InstanceCapabilities : long
{
    /// <summary>No capabilities.</summary>
    None = 0,

    /// <summary>Can accept knowledge queries.</summary>
    Query = 1 << 0,

    /// <summary>Can receive knowledge updates.</summary>
    Write = 1 << 1,

    /// <summary>Supports streaming responses.</summary>
    Streaming = 1 << 2,

    /// <summary>Supports vector similarity search.</summary>
    VectorSearch = 1 << 3,

    /// <summary>Supports knowledge graph traversal.</summary>
    GraphTraversal = 1 << 4,

    /// <summary>Supports semantic search.</summary>
    SemanticSearch = 1 << 5,

    /// <summary>Supports full-text search.</summary>
    FullTextSearch = 1 << 6,

    /// <summary>Supports temporal queries.</summary>
    TemporalQuery = 1 << 7,

    /// <summary>Supports provenance tracking.</summary>
    ProvenanceTracking = 1 << 8,

    /// <summary>Supports knowledge signing.</summary>
    KnowledgeSigning = 1 << 9,

    /// <summary>Supports encryption at rest.</summary>
    EncryptionAtRest = 1 << 10,

    /// <summary>Supports encryption in transit.</summary>
    EncryptionInTransit = 1 << 11,

    /// <summary>Supports batch operations.</summary>
    BatchOperations = 1 << 12,

    /// <summary>All basic capabilities.</summary>
    Basic = Query | Write,

    /// <summary>All search capabilities.</summary>
    AllSearch = VectorSearch | GraphTraversal | SemanticSearch | FullTextSearch,

    /// <summary>All security capabilities.</summary>
    AllSecurity = EncryptionAtRest | EncryptionInTransit | KnowledgeSigning | ProvenanceTracking
}

#endregion

#region Instance Configuration and Information

/// <summary>
/// Configuration for connecting to a federated DataWarehouse instance.
/// </summary>
public sealed record InstanceConfiguration
{
    /// <summary>Unique identifier for the instance.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Human-readable name for the instance.</summary>
    public required string Name { get; init; }

    /// <summary>Base endpoint URL for the instance.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Authentication method.</summary>
    public InstanceAuthMethod AuthMethod { get; init; } = InstanceAuthMethod.BearerToken;

    /// <summary>Authentication credentials (token, API key, etc.).</summary>
    public string? AuthCredential { get; init; }

    /// <summary>Client certificate for mTLS (base64 encoded PFX).</summary>
    public string? ClientCertificate { get; init; }

    /// <summary>Client certificate password.</summary>
    public string? ClientCertificatePassword { get; init; }

    /// <summary>Whether to validate the server certificate.</summary>
    public bool ValidateServerCertificate { get; init; } = true;

    /// <summary>
    /// Secondary dangerous-mode flag. Both ValidateServerCertificate=false AND AllowInsecureTls=true
    /// must be set to bypass TLS validation. This prevents accidental certificate bypass from a
    /// single misconfigured property. Only for non-production/test environments.
    /// </summary>
    public bool AllowInsecureTls { get; init; } = false;

    /// <summary>Priority of this instance (higher = preferred).</summary>
    public int Priority { get; init; } = 50;

    /// <summary>Maximum concurrent requests to this instance.</summary>
    public int MaxConcurrentRequests { get; init; } = 10;

    /// <summary>Request timeout in milliseconds.</summary>
    public int TimeoutMs { get; init; } = 30000;

    /// <summary>Connection timeout in milliseconds.</summary>
    public int ConnectionTimeoutMs { get; init; } = 5000;

    /// <summary>Retry count for failed requests.</summary>
    public int RetryCount { get; init; } = 3;

    /// <summary>Retry delay in milliseconds (exponential backoff base).</summary>
    public int RetryDelayMs { get; init; } = 1000;

    /// <summary>Health check interval in seconds.</summary>
    public int HealthCheckIntervalSeconds { get; init; } = 30;

    /// <summary>Tags for instance categorization.</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>Region or data center location.</summary>
    public string? Region { get; init; }

    /// <summary>Whether this instance is read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Knowledge domains this instance is authoritative for.</summary>
    public string[] AuthoritativeDomains { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Runtime information about a federated instance.
/// </summary>
public sealed record InstanceInfo
{
    /// <summary>Instance configuration.</summary>
    public required InstanceConfiguration Configuration { get; init; }

    /// <summary>Current status of the instance.</summary>
    public InstanceStatus Status { get; set; } = InstanceStatus.Unknown;

    /// <summary>Capabilities advertised by the instance.</summary>
    public InstanceCapabilities Capabilities { get; set; }

    /// <summary>Last time the instance was successfully contacted.</summary>
    public DateTimeOffset? LastContactTime { get; set; }

    /// <summary>Last health check result.</summary>
    public HealthCheckResult? LastHealthCheck { get; set; }

    /// <summary>Current average response latency in milliseconds.</summary>
    public double AverageLatencyMs { get; set; }

    /// <summary>Total number of requests sent to this instance.</summary>
    public long TotalRequests { get; set; }

    /// <summary>Number of failed requests.</summary>
    public long FailedRequests { get; set; }

    /// <summary>Number of active concurrent requests.</summary>
    public int ActiveRequests { get; set; }

    /// <summary>Trust score for this instance (0.0 - 1.0).</summary>
    public double TrustScore { get; set; } = 1.0;

    /// <summary>Instance version information.</summary>
    public string? Version { get; set; }

    /// <summary>Last known total knowledge count.</summary>
    public long KnowledgeCount { get; set; }
}

/// <summary>
/// Result of a health check on an instance.
/// </summary>
public sealed record HealthCheckResult
{
    /// <summary>Whether the instance is healthy.</summary>
    public bool IsHealthy { get; init; }

    /// <summary>Time the health check was performed.</summary>
    public DateTimeOffset CheckTime { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Response latency in milliseconds.</summary>
    public double LatencyMs { get; init; }

    /// <summary>Error message if unhealthy.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Additional health details.</summary>
    public Dictionary<string, object> Details { get; init; } = new();
}

#endregion

#region Instance Registry

/// <summary>
/// Registry for managing known DataWarehouse instances in the federation.
/// Provides discovery, registration, and health monitoring.
/// </summary>
public sealed class InstanceRegistry : IAsyncDisposable
{
    private readonly BoundedDictionary<string, InstanceInfo> _instances = new BoundedDictionary<string, InstanceInfo>(1000);
    private readonly BoundedDictionary<string, HttpClient> _httpClients = new BoundedDictionary<string, HttpClient>(1000);
    private readonly BoundedDictionary<string, SemaphoreSlim> _requestSemaphores = new BoundedDictionary<string, SemaphoreSlim>(1000);
    private readonly BoundedDictionary<string, Timer> _healthCheckTimers = new BoundedDictionary<string, Timer>(1000);
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Event raised when an instance status changes.
    /// </summary>
    public event EventHandler<InstanceStatusChangedEventArgs>? InstanceStatusChanged;

    /// <summary>
    /// Event raised when a new instance is registered.
    /// </summary>
    public event EventHandler<InstanceInfo>? InstanceRegistered;

    /// <summary>
    /// Event raised when an instance is unregistered.
    /// </summary>
    public event EventHandler<string>? InstanceUnregistered;

    /// <summary>
    /// Gets all registered instance IDs.
    /// </summary>
    public IEnumerable<string> InstanceIds => _instances.Keys;

    /// <summary>
    /// Gets all registered instances.
    /// </summary>
    public IEnumerable<InstanceInfo> Instances => _instances.Values;

    /// <summary>
    /// Gets all healthy instances.
    /// </summary>
    public IEnumerable<InstanceInfo> HealthyInstances =>
        _instances.Values.Where(i => i.Status == InstanceStatus.Healthy);

    /// <summary>
    /// Gets the count of registered instances.
    /// </summary>
    public int Count => _instances.Count;

    /// <summary>
    /// Registers a new instance in the registry.
    /// </summary>
    /// <param name="config">Instance configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The registered instance info.</returns>
    public async Task<InstanceInfo> RegisterAsync(InstanceConfiguration config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var info = new InstanceInfo
        {
            Configuration = config,
            Status = InstanceStatus.Unknown
        };

        // Create HTTP client for this instance
        var httpClient = CreateHttpClient(config);

        // Atomically register all associated state; if a concurrent call already won, discard ours.
        if (!_instances.TryAdd(config.InstanceId, info))
        {
            httpClient.Dispose();
            throw new InvalidOperationException($"Instance '{config.InstanceId}' is already registered");
        }

        _httpClients[config.InstanceId] = httpClient;

        // Create request semaphore for concurrency control
        _requestSemaphores[config.InstanceId] = new SemaphoreSlim(config.MaxConcurrentRequests);

        // Perform initial health check
        await PerformHealthCheckAsync(config.InstanceId, ct);

        // Start health check timer
        StartHealthCheckTimer(config.InstanceId, config.HealthCheckIntervalSeconds);

        InstanceRegistered?.Invoke(this, info);

        return info;
    }

    /// <summary>
    /// Unregisters an instance from the registry.
    /// </summary>
    /// <param name="instanceId">Instance ID to unregister.</param>
    public async Task UnregisterAsync(string instanceId)
    {
        if (_instances.TryRemove(instanceId, out _))
        {
            // Stop health check timer
            if (_healthCheckTimers.TryRemove(instanceId, out var timer))
            {
                await timer.DisposeAsync();
            }

            // Dispose HTTP client
            if (_httpClients.TryRemove(instanceId, out var httpClient))
            {
                httpClient.Dispose();
            }

            // Dispose semaphore
            if (_requestSemaphores.TryRemove(instanceId, out var semaphore))
            {
                semaphore.Dispose();
            }

            InstanceUnregistered?.Invoke(this, instanceId);
        }
    }

    /// <summary>
    /// Gets an instance by ID.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <returns>Instance info, or null if not found.</returns>
    public InstanceInfo? Get(string instanceId)
    {
        _instances.TryGetValue(instanceId, out var info);
        return info;
    }

    /// <summary>
    /// Gets instances by capability.
    /// </summary>
    /// <param name="requiredCapabilities">Required capabilities.</param>
    /// <returns>Matching instances.</returns>
    public IEnumerable<InstanceInfo> GetByCapabilities(InstanceCapabilities requiredCapabilities)
    {
        return _instances.Values.Where(i =>
            (i.Capabilities & requiredCapabilities) == requiredCapabilities &&
            i.Status == InstanceStatus.Healthy);
    }

    /// <summary>
    /// Gets instances by tag.
    /// </summary>
    /// <param name="tag">Tag to filter by.</param>
    /// <returns>Matching instances.</returns>
    public IEnumerable<InstanceInfo> GetByTag(string tag)
    {
        return _instances.Values.Where(i =>
            i.Configuration.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) &&
            i.Status == InstanceStatus.Healthy);
    }

    /// <summary>
    /// Gets instances by region.
    /// </summary>
    /// <param name="region">Region to filter by.</param>
    /// <returns>Matching instances.</returns>
    public IEnumerable<InstanceInfo> GetByRegion(string region)
    {
        return _instances.Values.Where(i =>
            string.Equals(i.Configuration.Region, region, StringComparison.OrdinalIgnoreCase) &&
            i.Status == InstanceStatus.Healthy);
    }

    /// <summary>
    /// Gets the authoritative instance for a knowledge domain.
    /// </summary>
    /// <param name="domain">Knowledge domain.</param>
    /// <returns>Authoritative instance, or null if none found.</returns>
    public InstanceInfo? GetAuthoritativeForDomain(string domain)
    {
        return _instances.Values
            .Where(i => i.Configuration.AuthoritativeDomains.Contains(domain, StringComparer.OrdinalIgnoreCase) &&
                        i.Status == InstanceStatus.Healthy)
            .OrderByDescending(i => i.Configuration.Priority)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets the HTTP client for an instance.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <returns>HTTP client.</returns>
    public HttpClient GetHttpClient(string instanceId)
    {
        if (!_httpClients.TryGetValue(instanceId, out var client))
        {
            throw new InvalidOperationException($"Instance '{instanceId}' is not registered");
        }
        return client;
    }

    /// <summary>
    /// Gets the request semaphore for an instance.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <returns>Semaphore for concurrency control.</returns>
    public SemaphoreSlim GetSemaphore(string instanceId)
    {
        if (!_requestSemaphores.TryGetValue(instanceId, out var semaphore))
        {
            throw new InvalidOperationException($"Instance '{instanceId}' is not registered");
        }
        return semaphore;
    }

    /// <summary>
    /// Forces a health check on an instance.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health check result.</returns>
    public async Task<HealthCheckResult> PerformHealthCheckAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(instanceId, out var info))
        {
            throw new InvalidOperationException($"Instance '{instanceId}' is not registered");
        }

        var httpClient = GetHttpClient(instanceId);
        var sw = Stopwatch.StartNew();
        HealthCheckResult result;

        try
        {
            using var response = await httpClient.GetAsync("/health", ct);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var details = new Dictionary<string, object>();

                try
                {
                    var healthResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(content);
                    if (healthResponse != null)
                    {
                        details = healthResponse;
                    }
                }
                catch
                {
                    Debug.WriteLine($"Caught exception in FederationSystem.cs");
                    // Ignore JSON parsing errors
                }

                result = new HealthCheckResult
                {
                    IsHealthy = true,
                    LatencyMs = sw.Elapsed.TotalMilliseconds,
                    Details = details
                };

                UpdateInstanceStatus(instanceId, InstanceStatus.Healthy);
            }
            else
            {
                result = new HealthCheckResult
                {
                    IsHealthy = false,
                    LatencyMs = sw.Elapsed.TotalMilliseconds,
                    ErrorMessage = $"Health check returned {(int)response.StatusCode} {response.ReasonPhrase}"
                };

                UpdateInstanceStatus(instanceId, response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                    ? InstanceStatus.Degraded
                    : InstanceStatus.Unhealthy);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Caught exception in FederationSystem.cs: {ex.Message}");
            sw.Stop();
            result = new HealthCheckResult
            {
                IsHealthy = false,
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                ErrorMessage = ex.Message
            };

            UpdateInstanceStatus(instanceId, InstanceStatus.Unhealthy);
        }

        info.LastHealthCheck = result;
        info.LastContactTime = result.IsHealthy ? DateTimeOffset.UtcNow : info.LastContactTime;
        info.AverageLatencyMs = (info.AverageLatencyMs * 0.9) + (result.LatencyMs * 0.1); // EMA

        return result;
    }

    /// <summary>
    /// Gets registry statistics.
    /// </summary>
    public RegistryStatistics GetStatistics()
    {
        var instances = _instances.Values.ToList();
        return new RegistryStatistics
        {
            TotalInstances = instances.Count,
            HealthyInstances = instances.Count(i => i.Status == InstanceStatus.Healthy),
            DegradedInstances = instances.Count(i => i.Status == InstanceStatus.Degraded),
            UnhealthyInstances = instances.Count(i => i.Status == InstanceStatus.Unhealthy),
            TotalRequests = instances.Sum(i => i.TotalRequests),
            TotalFailures = instances.Sum(i => i.FailedRequests),
            AverageLatencyMs = instances.Count > 0 ? instances.Average(i => i.AverageLatencyMs) : 0,
            InstancesByRegion = instances
                .Where(i => !string.IsNullOrEmpty(i.Configuration.Region))
                .GroupBy(i => i.Configuration.Region!)
                .ToDictionary(g => g.Key, g => g.Count()),
            InstancesByStatus = instances
                .GroupBy(i => i.Status)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private HttpClient CreateHttpClient(InstanceConfiguration config)
    {
        var handler = new HttpClientHandler();

        // SECURITY: TLS certificate validation must be explicitly disabled via both
        // ValidateServerCertificate=false AND AllowInsecureTls=true (defense-in-depth).
        // Bypassing TLS validation in production exposes connections to MITM attacks.
        if (!config.ValidateServerCertificate && config.AllowInsecureTls)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        if (config.AuthMethod == InstanceAuthMethod.MutualTls && !string.IsNullOrEmpty(config.ClientCertificate))
        {
            var certBytes = Convert.FromBase64String(config.ClientCertificate);
            var cert = X509CertificateLoader.LoadPkcs12(certBytes, config.ClientCertificatePassword);
            handler.ClientCertificates.Add(cert);
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(config.Endpoint),
            Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs)
        };

        // Set up authentication
        switch (config.AuthMethod)
        {
            case InstanceAuthMethod.BearerToken:
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);
                break;

            case InstanceAuthMethod.ApiKey:
                client.DefaultRequestHeaders.Remove("X-API-Key");
                client.DefaultRequestHeaders.Add("X-API-Key", config.AuthCredential);
                break;

            case InstanceAuthMethod.Jwt:
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);
                break;
        }

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Remove("X-Federation-Instance");
        client.DefaultRequestHeaders.Add("X-Federation-Instance", config.InstanceId);

        return client;
    }

    private void StartHealthCheckTimer(string instanceId, int intervalSeconds)
    {
        var timer = new Timer(
            async _ => await PerformHealthCheckAsync(instanceId),
            null,
            TimeSpan.FromSeconds(intervalSeconds),
            TimeSpan.FromSeconds(intervalSeconds));

        _healthCheckTimers[instanceId] = timer;
    }

    private void UpdateInstanceStatus(string instanceId, InstanceStatus newStatus)
    {
        if (_instances.TryGetValue(instanceId, out var info) && info.Status != newStatus)
        {
            var oldStatus = info.Status;
            info.Status = newStatus;
            InstanceStatusChanged?.Invoke(this, new InstanceStatusChangedEventArgs
            {
                InstanceId = instanceId,
                OldStatus = oldStatus,
                NewStatus = newStatus
            });
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var timer in _healthCheckTimers.Values)
        {
            await timer.DisposeAsync();
        }
        _healthCheckTimers.Clear();

        foreach (var client in _httpClients.Values)
        {
            client.Dispose();
        }
        _httpClients.Clear();

        foreach (var semaphore in _requestSemaphores.Values)
        {
            semaphore.Dispose();
        }
        _requestSemaphores.Clear();

        _instances.Clear();
    }
}

/// <summary>
/// Event arguments for instance status changes.
/// </summary>
public sealed class InstanceStatusChangedEventArgs : EventArgs
{
    /// <summary>Instance ID.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Previous status.</summary>
    public InstanceStatus OldStatus { get; init; }

    /// <summary>New status.</summary>
    public InstanceStatus NewStatus { get; init; }
}

/// <summary>
/// Statistics for the instance registry.
/// </summary>
public sealed record RegistryStatistics
{
    /// <summary>Total number of registered instances.</summary>
    public int TotalInstances { get; init; }

    /// <summary>Number of healthy instances.</summary>
    public int HealthyInstances { get; init; }

    /// <summary>Number of degraded instances.</summary>
    public int DegradedInstances { get; init; }

    /// <summary>Number of unhealthy instances.</summary>
    public int UnhealthyInstances { get; init; }

    /// <summary>Total requests across all instances.</summary>
    public long TotalRequests { get; init; }

    /// <summary>Total failures across all instances.</summary>
    public long TotalFailures { get; init; }

    /// <summary>Average latency across all instances.</summary>
    public double AverageLatencyMs { get; init; }

    /// <summary>Instance count by region.</summary>
    public Dictionary<string, int> InstancesByRegion { get; init; } = new();

    /// <summary>Instance count by status.</summary>
    public Dictionary<InstanceStatus, int> InstancesByStatus { get; init; } = new();
}

#endregion

#region Federation Protocol

/// <summary>
/// Federation protocol message type.
/// </summary>
public enum FederationMessageType
{
    /// <summary>Query request.</summary>
    Query,

    /// <summary>Query response.</summary>
    QueryResponse,

    /// <summary>Knowledge sync request.</summary>
    SyncRequest,

    /// <summary>Knowledge sync response.</summary>
    SyncResponse,

    /// <summary>Health check.</summary>
    HealthCheck,

    /// <summary>Capability discovery.</summary>
    CapabilityDiscovery,

    /// <summary>Instance registration.</summary>
    Registration,

    /// <summary>Knowledge update notification.</summary>
    UpdateNotification,

    /// <summary>Conflict resolution request.</summary>
    ConflictResolution,

    /// <summary>Error response.</summary>
    Error
}

/// <summary>
/// Base class for federation protocol messages.
/// </summary>
public abstract record FederationMessage
{
    /// <summary>Unique message ID.</summary>
    public string MessageId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Correlation ID for request-response matching.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Source instance ID.</summary>
    public required string SourceInstanceId { get; init; }

    /// <summary>Target instance ID (null for broadcast).</summary>
    public string? TargetInstanceId { get; init; }

    /// <summary>Message type.</summary>
    public abstract FederationMessageType MessageType { get; }

    /// <summary>Timestamp when message was created.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Time-to-live in seconds (0 = no expiry).</summary>
    public int TtlSeconds { get; init; } = 300;

    /// <summary>Digital signature of the message.</summary>
    public string? Signature { get; init; }
}

/// <summary>
/// Federated query message.
/// </summary>
public sealed record FederatedQueryMessage : FederationMessage
{
    /// <inheritdoc/>
    public override FederationMessageType MessageType => FederationMessageType.Query;

    /// <summary>Query content.</summary>
    public required FederatedQueryRequest Query { get; init; }
}

/// <summary>
/// Federated query response message.
/// </summary>
public sealed record FederatedQueryResponseMessage : FederationMessage
{
    /// <inheritdoc/>
    public override FederationMessageType MessageType => FederationMessageType.QueryResponse;

    /// <summary>Query results.</summary>
    public required FederatedQueryResponse Response { get; init; }
}

/// <summary>
/// Secure inter-instance communication protocol.
/// Provides message signing, encryption, and reliable delivery.
/// </summary>
public sealed class FederationProtocol : IAsyncDisposable
{
    private readonly InstanceRegistry _registry;
    private readonly ECDsa? _signingKey;
    private readonly BoundedDictionary<string, ECDsa> _instancePublicKeys = new BoundedDictionary<string, ECDsa>(1000);
    private readonly BoundedDictionary<string, TaskCompletionSource<FederationMessage>> _pendingResponses = new BoundedDictionary<string, TaskCompletionSource<FederationMessage>>(1000);
    private readonly SemaphoreSlim _sendLock = new(100);
    private bool _disposed;

    /// <summary>
    /// Gets the local instance ID.
    /// </summary>
    public string LocalInstanceId { get; }

    /// <summary>
    /// Event raised when a message is received.
    /// </summary>
    public event EventHandler<FederationMessage>? MessageReceived;

    /// <summary>
    /// Creates a new federation protocol handler.
    /// </summary>
    /// <param name="localInstanceId">Local instance ID.</param>
    /// <param name="registry">Instance registry.</param>
    /// <param name="privateKeyPem">Optional private key for message signing (PEM format).</param>
    public FederationProtocol(string localInstanceId, InstanceRegistry registry, string? privateKeyPem = null)
    {
        LocalInstanceId = localInstanceId ?? throw new ArgumentNullException(nameof(localInstanceId));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        if (!string.IsNullOrEmpty(privateKeyPem))
        {
            _signingKey = ECDsa.Create();
            _signingKey.ImportFromPem(privateKeyPem);
        }
    }

    /// <summary>
    /// Registers a public key for an instance.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <param name="publicKeyPem">Public key in PEM format.</param>
    public void RegisterPublicKey(string instanceId, string publicKeyPem)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        _instancePublicKeys[instanceId] = ecdsa;
    }

    /// <summary>
    /// Sends a message to a specific instance.
    /// </summary>
    /// <typeparam name="TMessage">Message type.</typeparam>
    /// <param name="message">Message to send.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendAsync<TMessage>(TMessage message, CancellationToken ct = default)
        where TMessage : FederationMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrEmpty(message.TargetInstanceId))
        {
            throw new ArgumentException("Target instance ID is required for direct messages");
        }

        var instance = _registry.Get(message.TargetInstanceId);
        if (instance == null)
        {
            throw new InvalidOperationException($"Target instance '{message.TargetInstanceId}' is not registered");
        }

        await _sendLock.WaitAsync(ct);
        try
        {
            var signedMessage = SignMessage(message);
            var json = JsonSerializer.Serialize(signedMessage, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            var httpClient = _registry.GetHttpClient(message.TargetInstanceId);
            var semaphore = _registry.GetSemaphore(message.TargetInstanceId);

            await semaphore.WaitAsync(ct);
            try
            {
                instance.ActiveRequests++;
                instance.TotalRequests++;

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync("/federation/message", content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    instance.FailedRequests++;
                    throw new FederationException(
                        $"Failed to send message to '{message.TargetInstanceId}': {response.StatusCode}");
                }
            }
            finally
            {
                instance.ActiveRequests--;
                semaphore.Release();
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends a request and waits for a response.
    /// </summary>
    /// <typeparam name="TRequest">Request message type.</typeparam>
    /// <typeparam name="TResponse">Response message type.</typeparam>
    /// <param name="request">Request message.</param>
    /// <param name="timeoutMs">Timeout in milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Response message.</returns>
    public async Task<TResponse> SendAndReceiveAsync<TRequest, TResponse>(
        TRequest request,
        int timeoutMs = 30000,
        CancellationToken ct = default)
        where TRequest : FederationMessage
        where TResponse : FederationMessage
    {
        var tcs = new TaskCompletionSource<FederationMessage>();
        _pendingResponses[request.MessageId] = tcs;

        try
        {
            await SendAsync(request, ct);

            var completedTask = await Task.WhenAny(
                tcs.Task,
                Task.Delay(timeoutMs, ct));

            if (completedTask != tcs.Task)
            {
                throw new TimeoutException($"Request to '{request.TargetInstanceId}' timed out after {timeoutMs}ms");
            }

            return (TResponse)await tcs.Task;
        }
        finally
        {
            _pendingResponses.TryRemove(request.MessageId, out _);
        }
    }

    /// <summary>
    /// Broadcasts a message to all healthy instances.
    /// </summary>
    /// <typeparam name="TMessage">Message type.</typeparam>
    /// <param name="message">Message to broadcast.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of instances the message was sent to.</returns>
    public async Task<int> BroadcastAsync<TMessage>(TMessage message, CancellationToken ct = default)
        where TMessage : FederationMessage
    {
        var healthyInstances = _registry.HealthyInstances.ToList();
        var sentCount = 0;

        var tasks = healthyInstances.Select(async instance =>
        {
            try
            {
                var instanceMessage = message with { TargetInstanceId = instance.Configuration.InstanceId };
                await SendAsync(instanceMessage, ct);
                Interlocked.Increment(ref sentCount);
            }
            catch
            {
                Debug.WriteLine($"Caught exception in FederationSystem.cs");
                // Log but continue broadcasting to other instances
            }
        });

        await Task.WhenAll(tasks);
        return sentCount;
    }

    /// <summary>
    /// Handles an incoming message.
    /// </summary>
    /// <param name="messageJson">Message JSON.</param>
    public void HandleIncomingMessage(string messageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            var messageType = doc.RootElement.GetProperty("MessageType").GetString();

            FederationMessage? message = messageType switch
            {
                "Query" => JsonSerializer.Deserialize<FederatedQueryMessage>(messageJson),
                "QueryResponse" => JsonSerializer.Deserialize<FederatedQueryResponseMessage>(messageJson),
                _ => null
            };

            if (message == null) return;

            // Verify signature if public key is registered
            if (!string.IsNullOrEmpty(message.Signature) &&
                _instancePublicKeys.TryGetValue(message.SourceInstanceId, out var publicKey))
            {
                if (!VerifyMessage(message, publicKey))
                {
                    throw new FederationException("Message signature verification failed");
                }
            }

            // Check if this is a response to a pending request
            if (message.CorrelationId != null &&
                _pendingResponses.TryRemove(message.CorrelationId, out var tcs))
            {
                tcs.SetResult(message);
                return;
            }

            // Raise event for other handlers
            MessageReceived?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Caught exception in FederationSystem.cs: {ex.Message}");
            // Log error
            System.Diagnostics.Debug.WriteLine($"Error handling incoming message: {ex.Message}");
        }
    }

    private TMessage SignMessage<TMessage>(TMessage message) where TMessage : FederationMessage
    {
        if (_signingKey == null) return message;

        var messageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message with { Signature = null }));
        var signature = _signingKey.SignData(messageBytes, HashAlgorithmName.SHA256);

        return message with { Signature = Convert.ToBase64String(signature) };
    }

    private bool VerifyMessage(FederationMessage message, ECDsa publicKey)
    {
        if (string.IsNullOrEmpty(message.Signature)) return false;

        var messageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message with { Signature = null }));
        var signature = Convert.FromBase64String(message.Signature);

        return publicKey.VerifyData(messageBytes, signature, HashAlgorithmName.SHA256);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _signingKey?.Dispose();
        _sendLock.Dispose();

        foreach (var key in _instancePublicKeys.Values)
        {
            key.Dispose();
        }
        _instancePublicKeys.Clear();

        foreach (var tcs in _pendingResponses.Values)
        {
            tcs.TrySetCanceled();
        }
        _pendingResponses.Clear();

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Exception thrown by federation operations.
/// </summary>
public sealed class FederationException : Exception
{
    /// <summary>
    /// Creates a new federation exception.
    /// </summary>
    /// <param name="message">Error message.</param>
    public FederationException(string message) : base(message) { }

    /// <summary>
    /// Creates a new federation exception with inner exception.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="innerException">Inner exception.</param>
    public FederationException(string message, Exception innerException)
        : base(message, innerException) { }
}

#endregion

#region Federated Query

/// <summary>
/// Request for a federated query across multiple instances.
/// </summary>
public sealed record FederatedQueryRequest
{
    /// <summary>Query ID for tracking.</summary>
    public string QueryId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Query text or structured query.</summary>
    public required string Query { get; init; }

    /// <summary>Query type.</summary>
    public FederatedQueryType QueryType { get; init; } = FederatedQueryType.Semantic;

    /// <summary>Target instances (null = all healthy instances).</summary>
    public string[]? TargetInstances { get; init; }

    /// <summary>Maximum results per instance.</summary>
    public int MaxResultsPerInstance { get; init; } = 10;

    /// <summary>Total maximum results.</summary>
    public int MaxTotalResults { get; init; } = 100;

    /// <summary>Minimum relevance score (0.0 - 1.0).</summary>
    public double MinRelevanceScore { get; init; } = 0.5;

    /// <summary>Query timeout in milliseconds.</summary>
    public int TimeoutMs { get; init; } = 30000;

    /// <summary>Whether to include provenance in results.</summary>
    public bool IncludeProvenance { get; init; } = true;

    /// <summary>Conflict resolution strategy.</summary>
    public ConflictResolutionStrategy ConflictResolution { get; init; } = ConflictResolutionStrategy.HighestTrust;

    /// <summary>Filter by knowledge domains.</summary>
    public string[]? Domains { get; init; }

    /// <summary>Filter by knowledge types.</summary>
    public string[]? Types { get; init; }

    /// <summary>Filter by tags.</summary>
    public string[]? Tags { get; init; }

    /// <summary>Custom metadata filters.</summary>
    public Dictionary<string, object>? MetadataFilters { get; init; }
}

/// <summary>
/// Type of federated query.
/// </summary>
public enum FederatedQueryType
{
    /// <summary>Semantic similarity search.</summary>
    Semantic,

    /// <summary>Full-text search.</summary>
    FullText,

    /// <summary>Structured query (SQL-like).</summary>
    Structured,

    /// <summary>Graph traversal query.</summary>
    Graph,

    /// <summary>Exact match lookup.</summary>
    ExactMatch
}

/// <summary>
/// Response from a federated query.
/// </summary>
public sealed record FederatedQueryResponse
{
    /// <summary>Query ID.</summary>
    public required string QueryId { get; init; }

    /// <summary>Query results.</summary>
    public required List<FederatedQueryResult> Results { get; init; }

    /// <summary>Total matching results across all instances.</summary>
    public int TotalMatches { get; init; }

    /// <summary>Number of instances queried.</summary>
    public int InstancesQueried { get; init; }

    /// <summary>Number of instances that responded successfully.</summary>
    public int InstancesResponded { get; init; }

    /// <summary>Total query execution time in milliseconds.</summary>
    public double ExecutionTimeMs { get; init; }

    /// <summary>Errors from instances that failed.</summary>
    public Dictionary<string, string> InstanceErrors { get; init; } = new();

    /// <summary>Whether results were truncated due to limits.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Single result from a federated query.
/// </summary>
public sealed record FederatedQueryResult
{
    /// <summary>Result ID.</summary>
    public required string Id { get; init; }

    /// <summary>Source instance ID.</summary>
    public required string SourceInstanceId { get; init; }

    /// <summary>Relevance score (0.0 - 1.0).</summary>
    public double RelevanceScore { get; init; }

    /// <summary>Trust score of the source (0.0 - 1.0).</summary>
    public double TrustScore { get; init; }

    /// <summary>Combined score (relevance * trust).</summary>
    public double CombinedScore => RelevanceScore * TrustScore;

    /// <summary>Knowledge content.</summary>
    public required object Content { get; init; }

    /// <summary>Content type.</summary>
    public string? ContentType { get; init; }

    /// <summary>Knowledge domain.</summary>
    public string? Domain { get; init; }

    /// <summary>Provenance information.</summary>
    public FederatedProvenance? Provenance { get; init; }

    /// <summary>Timestamp of the knowledge.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Provenance information for federated results.
/// </summary>
public sealed record FederatedProvenance
{
    /// <summary>Original source of the knowledge.</summary>
    public required string OriginalSource { get; init; }

    /// <summary>Chain of instances the knowledge passed through.</summary>
    public List<string> InstanceChain { get; init; } = new();

    /// <summary>When the knowledge was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the knowledge was last modified.</summary>
    public DateTimeOffset ModifiedAt { get; init; }

    /// <summary>Digital signature of the knowledge.</summary>
    public string? Signature { get; init; }

    /// <summary>Whether the signature has been verified.</summary>
    public bool SignatureVerified { get; init; }
}

#endregion

#region Query Fan Out

/// <summary>
/// Sends queries to multiple instances in parallel and collects responses.
/// Implements configurable fan-out patterns for distributed queries.
/// </summary>
public sealed class QueryFanOut
{
    private readonly InstanceRegistry _registry;
    private readonly FederationProtocol _protocol;
    private readonly ResponseMerger _merger;
    private readonly LatencyOptimizer _latencyOptimizer;

    /// <summary>
    /// Creates a new query fan-out handler.
    /// </summary>
    /// <param name="registry">Instance registry.</param>
    /// <param name="protocol">Federation protocol.</param>
    /// <param name="merger">Response merger.</param>
    /// <param name="latencyOptimizer">Latency optimizer.</param>
    public QueryFanOut(
        InstanceRegistry registry,
        FederationProtocol protocol,
        ResponseMerger merger,
        LatencyOptimizer latencyOptimizer)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _merger = merger ?? throw new ArgumentNullException(nameof(merger));
        _latencyOptimizer = latencyOptimizer ?? throw new ArgumentNullException(nameof(latencyOptimizer));
    }

    /// <summary>
    /// Executes a federated query across multiple instances.
    /// </summary>
    /// <param name="request">Query request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Merged query response.</returns>
    public async Task<FederatedQueryResponse> ExecuteAsync(
        FederatedQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();

        // Determine target instances
        var targetInstances = GetTargetInstances(request);
        if (!targetInstances.Any())
        {
            return new FederatedQueryResponse
            {
                QueryId = request.QueryId,
                Results = new List<FederatedQueryResult>(),
                InstancesQueried = 0,
                InstancesResponded = 0,
                ExecutionTimeMs = sw.Elapsed.TotalMilliseconds
            };
        }

        // Optimize query order based on latency
        var orderedInstances = _latencyOptimizer.OptimizeOrder(targetInstances);

        // Create timeout
        using var timeoutCts = new CancellationTokenSource(request.TimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // Execute queries in parallel
        var instanceResponses = new BoundedDictionary<string, FederatedQueryResponse>(1000);
        var instanceErrors = new BoundedDictionary<string, string>(1000);

        var queryTasks = orderedInstances.Select(async instance =>
        {
            try
            {
                var message = new FederatedQueryMessage
                {
                    SourceInstanceId = _protocol.LocalInstanceId,
                    TargetInstanceId = instance.Configuration.InstanceId,
                    Query = request
                };

                var response = await _protocol.SendAndReceiveAsync<FederatedQueryMessage, FederatedQueryResponseMessage>(
                    message,
                    request.TimeoutMs,
                    linkedCts.Token);

                instanceResponses[instance.Configuration.InstanceId] = response.Response;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Caught exception in FederationSystem.cs: {ex.Message}");
                instanceErrors[instance.Configuration.InstanceId] = ex.Message;
            }
        });

        await Task.WhenAll(queryTasks);

        sw.Stop();

        // Merge responses
        var mergedResults = _merger.Merge(
            instanceResponses.Values.ToList(),
            request.MaxTotalResults,
            request.ConflictResolution);

        return new FederatedQueryResponse
        {
            QueryId = request.QueryId,
            Results = mergedResults,
            TotalMatches = instanceResponses.Values.Sum(r => r.TotalMatches),
            InstancesQueried = targetInstances.Count(),
            InstancesResponded = instanceResponses.Count,
            ExecutionTimeMs = sw.Elapsed.TotalMilliseconds,
            InstanceErrors = new Dictionary<string, string>(instanceErrors),
            Truncated = mergedResults.Count >= request.MaxTotalResults
        };
    }

    /// <summary>
    /// Executes a query with streaming results.
    /// </summary>
    /// <param name="request">Query request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Streaming query results.</returns>
    public async IAsyncEnumerable<FederatedQueryResult> ExecuteStreamingAsync(
        FederatedQueryRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetInstances = GetTargetInstances(request);
        var orderedInstances = _latencyOptimizer.OptimizeOrder(targetInstances).ToList();

        using var timeoutCts = new CancellationTokenSource(request.TimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var resultChannel = System.Threading.Channels.Channel.CreateUnbounded<FederatedQueryResult>();

        // Start producer tasks
        var producerTasks = orderedInstances.Select(async instance =>
        {
            try
            {
                var message = new FederatedQueryMessage
                {
                    SourceInstanceId = _protocol.LocalInstanceId,
                    TargetInstanceId = instance.Configuration.InstanceId,
                    Query = request
                };

                var response = await _protocol.SendAndReceiveAsync<FederatedQueryMessage, FederatedQueryResponseMessage>(
                    message,
                    request.TimeoutMs,
                    linkedCts.Token);

                foreach (var result in response.Response.Results)
                {
                    await resultChannel.Writer.WriteAsync(result, linkedCts.Token);
                }
            }
            catch
            {
                Debug.WriteLine($"Caught exception in FederationSystem.cs");
                // Log but continue
            }
        }).ToList();

        // Complete channel when all producers are done
        _ = Task.WhenAll(producerTasks).ContinueWith(_ => resultChannel.Writer.Complete(), ct);

        // Consume results
        var seenIds = new HashSet<string>();
        var yieldedCount = 0;

        await foreach (var result in resultChannel.Reader.ReadAllAsync(ct))
        {
            if (yieldedCount >= request.MaxTotalResults) break;
            if (seenIds.Add(result.Id))
            {
                yield return result;
                yieldedCount++;
            }
        }
    }

    private IEnumerable<InstanceInfo> GetTargetInstances(FederatedQueryRequest request)
    {
        IEnumerable<InstanceInfo> instances;

        if (request.TargetInstances != null && request.TargetInstances.Length > 0)
        {
            instances = request.TargetInstances
                .Select(id => _registry.Get(id))
                .Where(i => i != null && i.Status == InstanceStatus.Healthy)!;
        }
        else
        {
            instances = _registry.HealthyInstances;
        }

        // Filter by capability based on query type
        var requiredCapability = request.QueryType switch
        {
            FederatedQueryType.Semantic => InstanceCapabilities.SemanticSearch,
            FederatedQueryType.FullText => InstanceCapabilities.FullTextSearch,
            FederatedQueryType.Graph => InstanceCapabilities.GraphTraversal,
            FederatedQueryType.Structured => InstanceCapabilities.Query,
            _ => InstanceCapabilities.Query
        };

        return instances.Where(i => (i.Capabilities & requiredCapability) == requiredCapability);
    }
}

#endregion

#region Response Merger

/// <summary>
/// Merges responses from multiple federated instances.
/// Handles deduplication, ranking, and conflict resolution.
/// </summary>
public sealed class ResponseMerger
{
    private readonly ConflictResolver _conflictResolver;

    /// <summary>
    /// Creates a new response merger.
    /// </summary>
    /// <param name="conflictResolver">Conflict resolver.</param>
    public ResponseMerger(ConflictResolver conflictResolver)
    {
        _conflictResolver = conflictResolver ?? throw new ArgumentNullException(nameof(conflictResolver));
    }

    /// <summary>
    /// Merges multiple query responses into a single result set.
    /// </summary>
    /// <param name="responses">Responses to merge.</param>
    /// <param name="maxResults">Maximum number of results.</param>
    /// <param name="strategy">Conflict resolution strategy.</param>
    /// <returns>Merged and ranked results.</returns>
    public List<FederatedQueryResult> Merge(
        IList<FederatedQueryResponse> responses,
        int maxResults,
        ConflictResolutionStrategy strategy)
    {
        if (responses.Count == 0) return new List<FederatedQueryResult>();

        // Flatten all results
        var allResults = responses.SelectMany(r => r.Results).ToList();

        // Group by content similarity (detect duplicates)
        var groups = GroupBySimilarity(allResults);

        // Resolve conflicts within groups
        var resolvedResults = groups.Select(group =>
            _conflictResolver.Resolve(group, strategy)).ToList();

        // Rank by combined score
        var ranked = resolvedResults
            .OrderByDescending(r => r.CombinedScore)
            .Take(maxResults)
            .ToList();

        return ranked;
    }

    private List<List<FederatedQueryResult>> GroupBySimilarity(List<FederatedQueryResult> results)
    {
        var groups = new List<List<FederatedQueryResult>>();
        var processed = new HashSet<string>();

        foreach (var result in results)
        {
            if (processed.Contains(result.Id)) continue;

            var group = new List<FederatedQueryResult> { result };
            processed.Add(result.Id);

            // Find similar results (same ID from different instances)
            foreach (var other in results)
            {
                if (processed.Contains(other.Id)) continue;

                if (AreResultsSimilar(result, other))
                {
                    group.Add(other);
                    processed.Add(other.Id);
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private bool AreResultsSimilar(FederatedQueryResult a, FederatedQueryResult b)
    {
        // Same ID = definitely similar
        if (a.Id == b.Id) return true;

        // Same domain and content type with similar content hash
        if (a.Domain == b.Domain &&
            a.ContentType == b.ContentType &&
            a.Metadata.TryGetValue("contentHash", out var hashA) &&
            b.Metadata.TryGetValue("contentHash", out var hashB) &&
            hashA?.Equals(hashB) == true)
        {
            return true;
        }

        return false;
    }
}

#endregion

#region Conflict Resolver

/// <summary>
/// Resolves conflicts between knowledge from different sources.
/// Implements various conflict resolution strategies.
/// </summary>
public sealed class ConflictResolver
{
    /// <summary>
    /// Resolves conflicts within a group of similar results.
    /// </summary>
    /// <param name="conflictingResults">Results that may conflict.</param>
    /// <param name="strategy">Resolution strategy.</param>
    /// <returns>Resolved result.</returns>
    public FederatedQueryResult Resolve(
        IList<FederatedQueryResult> conflictingResults,
        ConflictResolutionStrategy strategy)
    {
        if (conflictingResults.Count == 0)
        {
            throw new ArgumentException("At least one result is required", nameof(conflictingResults));
        }

        if (conflictingResults.Count == 1)
        {
            return conflictingResults[0];
        }

        return strategy switch
        {
            ConflictResolutionStrategy.LastWriteWins => ResolveByLastWrite(conflictingResults),
            ConflictResolutionStrategy.HighestPriority => ResolveByPriority(conflictingResults),
            ConflictResolutionStrategy.HighestTrust => ResolveByTrust(conflictingResults),
            ConflictResolutionStrategy.MajorityConsensus => ResolveByConsensus(conflictingResults),
            ConflictResolutionStrategy.Merge => MergeResults(conflictingResults),
            ConflictResolutionStrategy.VectorClock => ResolveByVectorClock(conflictingResults),
            ConflictResolutionStrategy.Manual => conflictingResults[0] with
            {
                Metadata = new Dictionary<string, object>(conflictingResults[0].Metadata)
                {
                    ["requiresManualResolution"] = true,
                    ["conflictingInstanceCount"] = conflictingResults.Count
                }
            },
            _ => ResolveByTrust(conflictingResults)
        };
    }

    private FederatedQueryResult ResolveByLastWrite(IList<FederatedQueryResult> results)
    {
        return results.OrderByDescending(r => r.Timestamp).First();
    }

    private FederatedQueryResult ResolveByPriority(IList<FederatedQueryResult> results)
    {
        return results.OrderByDescending(r =>
            r.Metadata.TryGetValue("instancePriority", out var priority)
                ? Convert.ToInt32(priority)
                : 0).First();
    }

    private FederatedQueryResult ResolveByTrust(IList<FederatedQueryResult> results)
    {
        return results.OrderByDescending(r => r.TrustScore).First();
    }

    private FederatedQueryResult ResolveByConsensus(IList<FederatedQueryResult> results)
    {
        // Group by content hash and take the most common
        var contentGroups = results
            .GroupBy(r => r.Metadata.TryGetValue("contentHash", out var hash) ? hash?.ToString() : r.Id)
            .OrderByDescending(g => g.Count())
            .First();

        return contentGroups.OrderByDescending(r => r.TrustScore).First();
    }

    private FederatedQueryResult MergeResults(IList<FederatedQueryResult> results)
    {
        var primary = results.OrderByDescending(r => r.TrustScore).First();

        // Merge metadata from all results
        var mergedMetadata = new Dictionary<string, object>(primary.Metadata);
        mergedMetadata["mergedFromInstances"] = results.Select(r => r.SourceInstanceId).Distinct().ToArray();

        // Average scores
        var avgRelevance = results.Average(r => r.RelevanceScore);
        var avgTrust = results.Average(r => r.TrustScore);

        return primary with
        {
            RelevanceScore = avgRelevance,
            TrustScore = avgTrust,
            Metadata = mergedMetadata
        };
    }

    private FederatedQueryResult ResolveByVectorClock(IList<FederatedQueryResult> results)
    {
        // Parse vector clocks from metadata and find the causally latest
        var resultsWithClocks = results
            .Where(r => r.Metadata.TryGetValue("vectorClock", out var vc) && vc != null)
            .Select(r => (
                Result: r,
                Clock: ParseVectorClock(r.Metadata["vectorClock"])))
            .ToList();

        if (resultsWithClocks.Count == 0)
        {
            return ResolveByLastWrite(results);
        }

        // Find the causally latest clock
        var latest = resultsWithClocks.First();
        foreach (var candidate in resultsWithClocks.Skip(1))
        {
            if (IsClockAfter(candidate.Clock, latest.Clock))
            {
                latest = candidate;
            }
        }

        return latest.Result;
    }

    private Dictionary<string, long> ParseVectorClock(object clockObj)
    {
        if (clockObj is Dictionary<string, long> clock) return clock;
        if (clockObj is JsonElement element)
        {
            return JsonSerializer.Deserialize<Dictionary<string, long>>(element.GetRawText())
                ?? new Dictionary<string, long>();
        }
        return new Dictionary<string, long>();
    }

    private bool IsClockAfter(Dictionary<string, long> a, Dictionary<string, long> b)
    {
        var aGtB = false;
        foreach (var (key, aValue) in a)
        {
            var bValue = b.TryGetValue(key, out var bv) ? bv : 0;
            if (aValue < bValue) return false;
            if (aValue > bValue) aGtB = true;
        }
        return aGtB;
    }
}

#endregion

#region Latency Optimizer

/// <summary>
/// Optimizes query routing to minimize cross-instance latency.
/// Uses historical latency data and adaptive routing.
/// </summary>
public sealed class LatencyOptimizer
{
    private readonly BoundedDictionary<string, LatencyProfile> _latencyProfiles = new BoundedDictionary<string, LatencyProfile>(1000);

    /// <summary>
    /// Configuration for the latency optimizer.
    /// </summary>
    public LatencyOptimizerConfig Config { get; init; } = new();

    /// <summary>
    /// Optimizes the order of instances for querying based on latency.
    /// </summary>
    /// <param name="instances">Instances to order.</param>
    /// <returns>Optimally ordered instances.</returns>
    public IEnumerable<InstanceInfo> OptimizeOrder(IEnumerable<InstanceInfo> instances)
    {
        var instanceList = instances.ToList();

        return instanceList
            .Select(i => (
                Instance: i,
                Score: CalculateRoutingScore(i)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Instance);
    }

    /// <summary>
    /// Records a latency measurement for an instance.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <param name="latencyMs">Latency in milliseconds.</param>
    /// <param name="success">Whether the request was successful.</param>
    public void RecordLatency(string instanceId, double latencyMs, bool success)
    {
        var profile = _latencyProfiles.GetOrAdd(instanceId, _ => new LatencyProfile());

        profile.RecordMeasurement(latencyMs, success);
    }

    /// <summary>
    /// Gets the latency profile for an instance.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <returns>Latency profile, or null if not available.</returns>
    public LatencyProfile? GetProfile(string instanceId)
    {
        _latencyProfiles.TryGetValue(instanceId, out var profile);
        return profile;
    }

    /// <summary>
    /// Gets all latency profiles.
    /// </summary>
    public IReadOnlyDictionary<string, LatencyProfile> GetAllProfiles()
    {
        return new Dictionary<string, LatencyProfile>(_latencyProfiles);
    }

    private double CalculateRoutingScore(InstanceInfo instance)
    {
        var baseScore = 100.0;

        // Apply priority bonus
        baseScore += instance.Configuration.Priority * Config.PriorityWeight;

        // Apply latency penalty
        if (_latencyProfiles.TryGetValue(instance.Configuration.InstanceId, out var profile))
        {
            var latencyPenalty = Math.Min(profile.AverageLatencyMs / 10.0, 50);
            baseScore -= latencyPenalty * Config.LatencyWeight;

            // Apply success rate bonus
            baseScore += profile.SuccessRate * 20 * Config.SuccessRateWeight;

            // Penalize high variance
            if (profile.LatencyVariance > 100)
            {
                baseScore -= 10 * Config.VarianceWeight;
            }
        }

        // Apply trust score bonus
        baseScore += instance.TrustScore * 10;

        return Math.Max(0, baseScore);
    }
}

/// <summary>
/// Configuration for the latency optimizer.
/// </summary>
public sealed record LatencyOptimizerConfig
{
    /// <summary>Weight for instance priority in scoring.</summary>
    public double PriorityWeight { get; init; } = 1.0;

    /// <summary>Weight for latency in scoring.</summary>
    public double LatencyWeight { get; init; } = 1.0;

    /// <summary>Weight for success rate in scoring.</summary>
    public double SuccessRateWeight { get; init; } = 1.0;

    /// <summary>Weight for variance in scoring.</summary>
    public double VarianceWeight { get; init; } = 0.5;

    /// <summary>Number of recent measurements to consider.</summary>
    public int MeasurementWindow { get; init; } = 100;
}

/// <summary>
/// Latency profile for an instance.
/// </summary>
public sealed class LatencyProfile
{
    private readonly object _lock = new();
    private readonly Queue<(double Latency, bool Success)> _measurements = new();
    private const int MaxMeasurements = 100;

    /// <summary>Average latency in milliseconds.</summary>
    public double AverageLatencyMs { get; private set; }

    /// <summary>Minimum latency observed.</summary>
    public double MinLatencyMs { get; private set; } = double.MaxValue;

    /// <summary>Maximum latency observed.</summary>
    public double MaxLatencyMs { get; private set; }

    /// <summary>Latency variance.</summary>
    public double LatencyVariance { get; private set; }

    /// <summary>P95 latency.</summary>
    public double P95LatencyMs { get; private set; }

    /// <summary>Success rate (0.0 - 1.0).</summary>
    public double SuccessRate { get; private set; } = 1.0;

    /// <summary>Total number of measurements.</summary>
    public long TotalMeasurements { get; private set; }

    /// <summary>Records a latency measurement.</summary>
    public void RecordMeasurement(double latencyMs, bool success)
    {
        lock (_lock)
        {
            _measurements.Enqueue((latencyMs, success));
            while (_measurements.Count > MaxMeasurements)
            {
                _measurements.Dequeue();
            }

            TotalMeasurements++;
            MinLatencyMs = Math.Min(MinLatencyMs, latencyMs);
            MaxLatencyMs = Math.Max(MaxLatencyMs, latencyMs);

            // Recalculate statistics
            var measurements = _measurements.ToList();
            var latencies = measurements.Select(m => m.Latency).ToList();

            AverageLatencyMs = latencies.Average();
            SuccessRate = measurements.Count(m => m.Success) / (double)measurements.Count;

            // Calculate variance
            var sumSquares = latencies.Sum(l => (l - AverageLatencyMs) * (l - AverageLatencyMs));
            LatencyVariance = sumSquares / latencies.Count;

            // Calculate P95
            var sorted = latencies.OrderBy(l => l).ToList();
            var p95Index = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
            P95LatencyMs = sorted[Math.Max(0, p95Index)];
        }
    }
}

#endregion

#region Federation Manager

/// <summary>
/// High-level manager for federated DataWarehouse operations.
/// Coordinates instance registry, protocol, queries, and health monitoring.
/// </summary>
public sealed class FederationManager : IAsyncDisposable
{
    private readonly InstanceRegistry _registry;
    private readonly FederationProtocol _protocol;
    private readonly QueryFanOut _queryFanOut;
    private readonly ResponseMerger _merger;
    private readonly ConflictResolver _conflictResolver;
    private readonly LatencyOptimizer _latencyOptimizer;
    private bool _disposed;

    /// <summary>
    /// Gets the instance registry.
    /// </summary>
    public InstanceRegistry Registry => _registry;

    /// <summary>
    /// Gets the federation protocol.
    /// </summary>
    public FederationProtocol Protocol => _protocol;

    /// <summary>
    /// Gets the latency optimizer.
    /// </summary>
    public LatencyOptimizer LatencyOptimizer => _latencyOptimizer;

    /// <summary>
    /// Gets the local instance ID.
    /// </summary>
    public string LocalInstanceId => _protocol.LocalInstanceId;

    /// <summary>
    /// Creates a new federation manager.
    /// </summary>
    /// <param name="localInstanceId">Local instance ID.</param>
    /// <param name="signingKeyPem">Optional private key for message signing.</param>
    public FederationManager(string localInstanceId, string? signingKeyPem = null)
    {
        _registry = new InstanceRegistry();
        _protocol = new FederationProtocol(localInstanceId, _registry, signingKeyPem);
        _conflictResolver = new ConflictResolver();
        _merger = new ResponseMerger(_conflictResolver);
        _latencyOptimizer = new LatencyOptimizer();
        _queryFanOut = new QueryFanOut(_registry, _protocol, _merger, _latencyOptimizer);

        // Wire up events
        _registry.InstanceStatusChanged += OnInstanceStatusChanged;
    }

    /// <summary>
    /// Registers a new federated instance.
    /// </summary>
    /// <param name="config">Instance configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Registered instance info.</returns>
    public Task<InstanceInfo> RegisterInstanceAsync(InstanceConfiguration config, CancellationToken ct = default)
    {
        return _registry.RegisterAsync(config, ct);
    }

    /// <summary>
    /// Unregisters a federated instance.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    public Task UnregisterInstanceAsync(string instanceId)
    {
        return _registry.UnregisterAsync(instanceId);
    }

    /// <summary>
    /// Executes a federated query across connected instances.
    /// </summary>
    /// <param name="request">Query request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query response.</returns>
    public Task<FederatedQueryResponse> QueryAsync(FederatedQueryRequest request, CancellationToken ct = default)
    {
        return _queryFanOut.ExecuteAsync(request, ct);
    }

    /// <summary>
    /// Executes a federated query with streaming results.
    /// </summary>
    /// <param name="request">Query request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Streaming results.</returns>
    public IAsyncEnumerable<FederatedQueryResult> QueryStreamingAsync(
        FederatedQueryRequest request,
        CancellationToken ct = default)
    {
        return _queryFanOut.ExecuteStreamingAsync(request, ct);
    }

    /// <summary>
    /// Gets federation statistics.
    /// </summary>
    public FederationStatistics GetStatistics()
    {
        var registryStats = _registry.GetStatistics();
        var latencyProfiles = _latencyOptimizer.GetAllProfiles();

        return new FederationStatistics
        {
            LocalInstanceId = LocalInstanceId,
            RegistryStats = registryStats,
            TotalFederatedQueries = 0, // Federated query counter: wire into query execution path to track.
            AverageFederatedQueryTimeMs = latencyProfiles.Count > 0
                ? latencyProfiles.Values.Average(p => p.AverageLatencyMs)
                : 0,
            LatencyProfiles = new Dictionary<string, LatencyProfile>(latencyProfiles)
        };
    }

    private void OnInstanceStatusChanged(object? sender, InstanceStatusChangedEventArgs e)
    {
        // Log or handle status changes
        System.Diagnostics.Debug.WriteLine($"Instance '{e.InstanceId}' status changed from {e.OldStatus} to {e.NewStatus}");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _registry.InstanceStatusChanged -= OnInstanceStatusChanged;

        await _protocol.DisposeAsync();
        await _registry.DisposeAsync();
    }
}

/// <summary>
/// Statistics for the federation manager.
/// </summary>
public sealed record FederationStatistics
{
    /// <summary>Local instance ID.</summary>
    public required string LocalInstanceId { get; init; }

    /// <summary>Registry statistics.</summary>
    public required RegistryStatistics RegistryStats { get; init; }

    /// <summary>Total federated queries executed.</summary>
    public long TotalFederatedQueries { get; init; }

    /// <summary>Average federated query time in milliseconds.</summary>
    public double AverageFederatedQueryTimeMs { get; init; }

    /// <summary>Latency profiles by instance ID.</summary>
    public Dictionary<string, LatencyProfile> LatencyProfiles { get; init; } = new();
}

#endregion

#region Instance Authentication

/// <summary>
/// Handles authentication of federated instances.
/// Supports multiple authentication methods including mTLS, tokens, and JWT.
/// </summary>
public sealed class InstanceAuthentication
{
    private readonly BoundedDictionary<string, AuthenticationEntry> _authenticatedInstances = new BoundedDictionary<string, AuthenticationEntry>(1000);
    private readonly BoundedDictionary<string, X509Certificate2> _instanceCertificates = new BoundedDictionary<string, X509Certificate2>(1000);
    private readonly RSA? _jwtSigningKey;
    private readonly string? _jwtIssuer;

    /// <summary>
    /// Creates a new instance authentication handler.
    /// </summary>
    /// <param name="jwtSigningKeyPem">Optional JWT signing key (PEM format).</param>
    /// <param name="jwtIssuer">JWT issuer identifier.</param>
    public InstanceAuthentication(string? jwtSigningKeyPem = null, string? jwtIssuer = null)
    {
        _jwtIssuer = jwtIssuer;

        if (!string.IsNullOrEmpty(jwtSigningKeyPem))
        {
            _jwtSigningKey = RSA.Create();
            _jwtSigningKey.ImportFromPem(jwtSigningKeyPem);
        }
    }

    /// <summary>
    /// Registers a certificate for an instance (for mTLS).
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <param name="certificate">X.509 certificate.</param>
    public void RegisterCertificate(string instanceId, X509Certificate2 certificate)
    {
        _instanceCertificates[instanceId] = certificate;
    }

    /// <summary>
    /// Authenticates an instance using the specified method.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <param name="method">Authentication method.</param>
    /// <param name="credential">Authentication credential.</param>
    /// <param name="certificate">Client certificate (for mTLS).</param>
    /// <returns>Authentication result.</returns>
    public AuthenticationResult Authenticate(
        string instanceId,
        InstanceAuthMethod method,
        string? credential,
        X509Certificate2? certificate = null)
    {
        try
        {
            var result = method switch
            {
                InstanceAuthMethod.None => new AuthenticationResult
                {
                    IsAuthenticated = true,
                    InstanceId = instanceId,
                    AuthMethod = method,
                    Message = "No authentication required"
                },
                InstanceAuthMethod.BearerToken => AuthenticateToken(instanceId, credential),
                InstanceAuthMethod.ApiKey => AuthenticateApiKey(instanceId, credential),
                InstanceAuthMethod.MutualTls => AuthenticateMtls(instanceId, certificate),
                InstanceAuthMethod.Jwt => AuthenticateJwt(instanceId, credential),
                _ => new AuthenticationResult
                {
                    IsAuthenticated = false,
                    InstanceId = instanceId,
                    AuthMethod = method,
                    Message = $"Unsupported authentication method: {method}"
                }
            };

            if (result.IsAuthenticated)
            {
                _authenticatedInstances[instanceId] = new AuthenticationEntry
                {
                    InstanceId = instanceId,
                    AuthMethod = method,
                    AuthenticatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = result.ExpiresAt
                };
            }

            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Caught exception in FederationSystem.cs: {ex.Message}");
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                InstanceId = instanceId,
                AuthMethod = method,
                Message = $"Authentication failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Checks if an instance is currently authenticated.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <returns>True if authenticated and not expired.</returns>
    public bool IsAuthenticated(string instanceId)
    {
        if (!_authenticatedInstances.TryGetValue(instanceId, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            _authenticatedInstances.TryRemove(instanceId, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Revokes authentication for an instance.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    public void Revoke(string instanceId)
    {
        _authenticatedInstances.TryRemove(instanceId, out _);
    }

    private AuthenticationResult AuthenticateToken(string instanceId, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                InstanceId = instanceId,
                AuthMethod = InstanceAuthMethod.BearerToken,
                Message = "Token is required"
            };
        }

        // In production, validate against token store
        return new AuthenticationResult
        {
            IsAuthenticated = true,
            InstanceId = instanceId,
            AuthMethod = InstanceAuthMethod.BearerToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
    }

    private AuthenticationResult AuthenticateApiKey(string instanceId, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                InstanceId = instanceId,
                AuthMethod = InstanceAuthMethod.ApiKey,
                Message = "API key is required"
            };
        }

        // In production, validate against API key store
        return new AuthenticationResult
        {
            IsAuthenticated = true,
            InstanceId = instanceId,
            AuthMethod = InstanceAuthMethod.ApiKey
        };
    }

    private AuthenticationResult AuthenticateMtls(string instanceId, X509Certificate2? certificate)
    {
        if (certificate == null)
        {
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                InstanceId = instanceId,
                AuthMethod = InstanceAuthMethod.MutualTls,
                Message = "Client certificate is required"
            };
        }

        // Verify certificate is registered
        if (!_instanceCertificates.TryGetValue(instanceId, out var registeredCert))
        {
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                InstanceId = instanceId,
                AuthMethod = InstanceAuthMethod.MutualTls,
                Message = "Instance certificate not registered"
            };
        }

        // Verify certificate matches
        if (certificate.Thumbprint != registeredCert.Thumbprint)
        {
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                InstanceId = instanceId,
                AuthMethod = InstanceAuthMethod.MutualTls,
                Message = "Certificate thumbprint mismatch"
            };
        }

        // Verify certificate is valid
        if (certificate.NotAfter < DateTime.UtcNow || certificate.NotBefore > DateTime.UtcNow)
        {
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                InstanceId = instanceId,
                AuthMethod = InstanceAuthMethod.MutualTls,
                Message = "Certificate is expired or not yet valid"
            };
        }

        return new AuthenticationResult
        {
            IsAuthenticated = true,
            InstanceId = instanceId,
            AuthMethod = InstanceAuthMethod.MutualTls,
            ExpiresAt = new DateTimeOffset(certificate.NotAfter)
        };
    }

    private AuthenticationResult AuthenticateJwt(string instanceId, string? jwt)
    {
        if (string.IsNullOrEmpty(jwt))
        {
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                InstanceId = instanceId,
                AuthMethod = InstanceAuthMethod.Jwt,
                Message = "JWT is required"
            };
        }

        // Parse and validate JWT (simplified - use proper JWT library in production)
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                InstanceId = instanceId,
                AuthMethod = InstanceAuthMethod.Jwt,
                Message = "Invalid JWT format"
            };
        }

        // In production, validate signature, claims, and expiration
        return new AuthenticationResult
        {
            IsAuthenticated = true,
            InstanceId = instanceId,
            AuthMethod = InstanceAuthMethod.Jwt,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
    }
}

/// <summary>
/// Result of an authentication attempt.
/// </summary>
public sealed record AuthenticationResult
{
    /// <summary>Whether authentication was successful.</summary>
    public bool IsAuthenticated { get; init; }

    /// <summary>Instance ID.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Authentication method used.</summary>
    public InstanceAuthMethod AuthMethod { get; init; }

    /// <summary>Message describing the result.</summary>
    public string? Message { get; init; }

    /// <summary>When the authentication expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Granted permissions.</summary>
    public string[] Permissions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Authentication entry for an authenticated instance.
/// </summary>
internal sealed record AuthenticationEntry
{
    public required string InstanceId { get; init; }
    public InstanceAuthMethod AuthMethod { get; init; }
    public DateTimeOffset AuthenticatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

#endregion

#region Knowledge ACL

/// <summary>
/// Access control for federated knowledge sharing.
/// Defines what knowledge can be shared with which instances.
/// </summary>
public sealed class KnowledgeACL
{
    private readonly BoundedDictionary<string, InstanceACL> _instanceAcls = new BoundedDictionary<string, InstanceACL>(1000);
    private readonly BoundedDictionary<string, DomainACL> _domainAcls = new BoundedDictionary<string, DomainACL>(1000);

    /// <summary>
    /// Default access policy for instances not explicitly configured.
    /// </summary>
    public AccessPolicy DefaultPolicy { get; set; } = AccessPolicy.Deny;

    /// <summary>
    /// Sets access rules for an instance.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <param name="acl">Access control rules.</param>
    public void SetInstanceACL(string instanceId, InstanceACL acl)
    {
        _instanceAcls[instanceId] = acl;
    }

    /// <summary>
    /// Sets access rules for a knowledge domain.
    /// </summary>
    /// <param name="domain">Knowledge domain.</param>
    /// <param name="acl">Access control rules.</param>
    public void SetDomainACL(string domain, DomainACL acl)
    {
        _domainAcls[domain] = acl;
    }

    /// <summary>
    /// Checks if an instance can access knowledge.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <param name="domain">Knowledge domain.</param>
    /// <param name="operation">Requested operation.</param>
    /// <returns>Access check result.</returns>
    public AccessCheckResult CheckAccess(string instanceId, string domain, KnowledgeOperation operation)
    {
        // Check instance-specific rules first
        if (_instanceAcls.TryGetValue(instanceId, out var instanceAcl))
        {
            if (instanceAcl.DeniedDomains.Contains(domain))
            {
                return new AccessCheckResult
                {
                    Allowed = false,
                    Reason = $"Domain '{domain}' is explicitly denied for instance '{instanceId}'"
                };
            }

            if (instanceAcl.AllowedDomains.Contains("*") || instanceAcl.AllowedDomains.Contains(domain))
            {
                if (instanceAcl.AllowedOperations.Contains(operation))
                {
                    return new AccessCheckResult { Allowed = true };
                }
            }
        }

        // Check domain-specific rules
        if (_domainAcls.TryGetValue(domain, out var domainAcl))
        {
            if (domainAcl.DeniedInstances.Contains(instanceId))
            {
                return new AccessCheckResult
                {
                    Allowed = false,
                    Reason = $"Instance '{instanceId}' is denied access to domain '{domain}'"
                };
            }

            if (domainAcl.AllowedInstances.Contains("*") || domainAcl.AllowedInstances.Contains(instanceId))
            {
                if (domainAcl.AllowedOperations.Contains(operation))
                {
                    return new AccessCheckResult { Allowed = true };
                }
            }
        }

        // Apply default policy
        return new AccessCheckResult
        {
            Allowed = DefaultPolicy == AccessPolicy.Allow,
            Reason = $"Default policy: {DefaultPolicy}"
        };
    }

    /// <summary>
    /// Gets the ACL for an instance.
    /// </summary>
    /// <param name="instanceId">Instance ID.</param>
    /// <returns>Instance ACL, or null if not set.</returns>
    public InstanceACL? GetInstanceACL(string instanceId)
    {
        _instanceAcls.TryGetValue(instanceId, out var acl);
        return acl;
    }

    /// <summary>
    /// Gets the ACL for a domain.
    /// </summary>
    /// <param name="domain">Domain name.</param>
    /// <returns>Domain ACL, or null if not set.</returns>
    public DomainACL? GetDomainACL(string domain)
    {
        _domainAcls.TryGetValue(domain, out var acl);
        return acl;
    }
}

/// <summary>
/// Access control rules for an instance.
/// </summary>
public sealed record InstanceACL
{
    /// <summary>Instance ID.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Allowed knowledge domains ("*" for all).</summary>
    public HashSet<string> AllowedDomains { get; init; } = new();

    /// <summary>Denied knowledge domains.</summary>
    public HashSet<string> DeniedDomains { get; init; } = new();

    /// <summary>Allowed operations.</summary>
    public HashSet<KnowledgeOperation> AllowedOperations { get; init; } = new();

    /// <summary>Maximum data size per request (bytes, 0 = unlimited).</summary>
    public long MaxDataSizeBytes { get; init; }

    /// <summary>Rate limit (requests per minute, 0 = unlimited).</summary>
    public int RateLimitPerMinute { get; init; }
}

/// <summary>
/// Access control rules for a knowledge domain.
/// </summary>
public sealed record DomainACL
{
    /// <summary>Domain name.</summary>
    public required string Domain { get; init; }

    /// <summary>Allowed instances ("*" for all).</summary>
    public HashSet<string> AllowedInstances { get; init; } = new();

    /// <summary>Denied instances.</summary>
    public HashSet<string> DeniedInstances { get; init; } = new();

    /// <summary>Allowed operations.</summary>
    public HashSet<KnowledgeOperation> AllowedOperations { get; init; } = new();

    /// <summary>Whether knowledge must be signed.</summary>
    public bool RequireSigning { get; init; }

    /// <summary>Whether knowledge must be encrypted.</summary>
    public bool RequireEncryption { get; init; }
}

/// <summary>
/// Knowledge operation types.
/// </summary>
public enum KnowledgeOperation
{
    /// <summary>Read/query knowledge.</summary>
    Read,

    /// <summary>Write/update knowledge.</summary>
    Write,

    /// <summary>Delete knowledge.</summary>
    Delete,

    /// <summary>Sync knowledge.</summary>
    Sync,

    /// <summary>Subscribe to knowledge updates.</summary>
    Subscribe
}

/// <summary>
/// Default access policy.
/// </summary>
public enum AccessPolicy
{
    /// <summary>Allow access by default.</summary>
    Allow,

    /// <summary>Deny access by default.</summary>
    Deny
}

/// <summary>
/// Result of an access check.
/// </summary>
public sealed record AccessCheckResult
{
    /// <summary>Whether access is allowed.</summary>
    public bool Allowed { get; init; }

    /// <summary>Reason for the decision.</summary>
    public string? Reason { get; init; }
}

#endregion

#region Encrypted Transport

/// <summary>
/// Provides TLS configuration and transport encryption for knowledge transfer.
/// </summary>
public sealed class EncryptedTransport
{
    private readonly X509Certificate2? _serverCertificate;
    private readonly X509Certificate2Collection _trustedCertificates = new();

    /// <summary>
    /// Gets the minimum TLS version allowed.
    /// </summary>
    public System.Security.Authentication.SslProtocols MinimumTlsVersion { get; init; }
        = System.Security.Authentication.SslProtocols.Tls12;

    /// <summary>
    /// Gets whether client certificate validation is required.
    /// </summary>
    public bool RequireClientCertificate { get; init; }

    /// <summary>
    /// Gets the allowed cipher suites.
    /// </summary>
    public string[]? AllowedCipherSuites { get; init; }

    /// <summary>
    /// Creates a new encrypted transport configuration.
    /// </summary>
    /// <param name="serverCertificatePfx">Server certificate (PFX format, base64 encoded).</param>
    /// <param name="password">Certificate password.</param>
    public EncryptedTransport(string? serverCertificatePfx = null, string? password = null)
    {
        if (!string.IsNullOrEmpty(serverCertificatePfx))
        {
            var certBytes = Convert.FromBase64String(serverCertificatePfx);
            _serverCertificate = X509CertificateLoader.LoadPkcs12(certBytes, password);
        }
    }

    /// <summary>
    /// Adds a trusted certificate.
    /// </summary>
    /// <param name="certificate">Certificate to trust.</param>
    public void AddTrustedCertificate(X509Certificate2 certificate)
    {
        _trustedCertificates.Add(certificate);
    }

    /// <summary>
    /// Validates a client certificate.
    /// </summary>
    /// <param name="certificate">Client certificate.</param>
    /// <returns>Validation result.</returns>
    public CertificateValidationResult ValidateCertificate(X509Certificate2 certificate)
    {
        // Check if certificate is in trusted list
        var isTrusted = _trustedCertificates.Any(c => c.Thumbprint == certificate.Thumbprint);

        if (!isTrusted)
        {
            // Build chain and validate
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            var isValid = chain.Build(certificate);

            return new CertificateValidationResult
            {
                IsValid = isValid,
                Errors = chain.ChainStatus.Select(s => s.StatusInformation).ToArray()
            };
        }

        // Check validity period
        if (certificate.NotAfter < DateTime.UtcNow)
        {
            return new CertificateValidationResult
            {
                IsValid = false,
                Errors = new[] { "Certificate has expired" }
            };
        }

        if (certificate.NotBefore > DateTime.UtcNow)
        {
            return new CertificateValidationResult
            {
                IsValid = false,
                Errors = new[] { "Certificate is not yet valid" }
            };
        }

        return new CertificateValidationResult { IsValid = true };
    }

    /// <summary>
    /// Gets the server certificate.
    /// </summary>
    public X509Certificate2? GetServerCertificate() => _serverCertificate;

    /// <summary>
    /// Gets all trusted certificates.
    /// </summary>
    public IReadOnlyCollection<X509Certificate2> GetTrustedCertificates() =>
        new List<X509Certificate2>(_trustedCertificates);
}

/// <summary>
/// Result of certificate validation.
/// </summary>
public sealed record CertificateValidationResult
{
    /// <summary>Whether the certificate is valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Validation errors.</summary>
    public string[] Errors { get; init; } = Array.Empty<string>();
}

#endregion
