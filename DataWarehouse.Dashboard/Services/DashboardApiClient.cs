using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Dashboard.Services;

/// <summary>
/// HTTP client for the Launcher REST API. Provides typed access to all dashboard data endpoints
/// including system status, plugins, queries, security events, cluster state, and metrics.
/// </summary>
public sealed class DashboardApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DashboardApiClient> _logger;
    private readonly IConfiguration _configuration;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private string? _bearerToken;

    public DashboardApiClient(
        HttpClient httpClient,
        ILogger<DashboardApiClient> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

        var baseUrl = _configuration["Dashboard:LauncherApiUrl"] ?? "https://localhost:5001";
        _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Sets the bearer token for authenticated API calls.
    /// </summary>
    public void SetBearerToken(string token)
    {
        _bearerToken = token;
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Clears the current authentication token.
    /// </summary>
    public void ClearBearerToken()
    {
        _bearerToken = null;
        _httpClient.DefaultRequestHeaders.Authorization = null;
    }

    /// <summary>
    /// Gets overall system status including uptime, version, node count, plugin count, and health.
    /// </summary>
    public async Task<SystemStatusDto> GetSystemStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<SystemStatusDto>("api/v1/system/status", cancellationToken);
    }

    /// <summary>
    /// Gets all registered plugins with their current status and health.
    /// </summary>
    public async Task<IReadOnlyList<PluginInfoDto>> GetPluginsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<IReadOnlyList<PluginInfoDto>>("api/v1/plugins", cancellationToken)
               ?? Array.Empty<PluginInfoDto>();
    }

    /// <summary>
    /// Gets detailed information about a specific plugin including strategies and configuration.
    /// </summary>
    public async Task<PluginDetailDto> GetPluginDetailAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return await GetAsync<PluginDetailDto>($"api/v1/plugins/{Uri.EscapeDataString(pluginId)}", cancellationToken);
    }

    /// <summary>
    /// Executes a SQL query and returns the result set with execution metadata.
    /// </summary>
    public async Task<QueryResultDto> ExecuteQueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var request = new QueryRequestDto { Sql = sql };
        return await PostAsync<QueryRequestDto, QueryResultDto>("api/v1/query/execute", request, cancellationToken);
    }

    /// <summary>
    /// Gets recent security events, ordered by most recent first.
    /// </summary>
    public async Task<IReadOnlyList<SecurityEventDto>> GetSecurityEventsAsync(
        int count = 50, CancellationToken cancellationToken = default)
    {
        return await GetAsync<IReadOnlyList<SecurityEventDto>>(
                   $"api/v1/security/events?count={count}", cancellationToken)
               ?? Array.Empty<SecurityEventDto>();
    }

    /// <summary>
    /// Gets the current cluster status including nodes, Raft leader, and replication state.
    /// </summary>
    public async Task<ClusterStatusDto> GetClusterStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<ClusterStatusDto>("api/v1/cluster/status", cancellationToken);
    }

    /// <summary>
    /// Gets current system metrics including CPU, memory, disk, and request rate.
    /// </summary>
    public async Task<MetricsDto> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<MetricsDto>("api/v1/metrics", cancellationToken);
    }

    /// <summary>
    /// Gets security score and posture summary.
    /// </summary>
    public async Task<SecurityPostureDto> GetSecurityPostureAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<SecurityPostureDto>("api/v1/security/posture", cancellationToken);
    }

    /// <summary>
    /// Gets active security incidents.
    /// </summary>
    public async Task<IReadOnlyList<SecurityIncidentDto>> GetSecurityIncidentsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<IReadOnlyList<SecurityIncidentDto>>("api/v1/security/incidents", cancellationToken)
               ?? Array.Empty<SecurityIncidentDto>();
    }

    /// <summary>
    /// Gets SIEM transport status for all configured transports.
    /// </summary>
    public async Task<IReadOnlyList<SiemTransportStatusDto>> GetSiemTransportStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<IReadOnlyList<SiemTransportStatusDto>>("api/v1/security/siem/status", cancellationToken)
               ?? Array.Empty<SiemTransportStatusDto>();
    }

    private async Task<T> GetAsync<T>(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("GET {Endpoint}", endpoint);
            var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(content, JsonOptions);

            return result ?? throw new InvalidOperationException(
                $"Deserialization of {typeof(T).Name} from {endpoint} returned null");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP request failed for {Endpoint}: {Message}", endpoint, ex.Message);
            throw new DashboardApiException(
                $"Failed to reach Launcher API at {endpoint}: {GetUserFriendlyMessage(ex)}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Request timed out for {Endpoint}", endpoint);
            throw new DashboardApiException($"Request to {endpoint} timed out. The Launcher API may be slow or unreachable.", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize response from {Endpoint}", endpoint);
            throw new DashboardApiException($"Invalid response from Launcher API at {endpoint}.", ex);
        }
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string endpoint, TRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("POST {Endpoint}", endpoint);
            var response = await _httpClient.PostAsJsonAsync(endpoint, request, JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<TResponse>(content, JsonOptions);

            return result ?? throw new InvalidOperationException(
                $"Deserialization of {typeof(TResponse).Name} from POST {endpoint} returned null");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP POST request failed for {Endpoint}: {Message}", endpoint, ex.Message);
            throw new DashboardApiException(
                $"Failed to reach Launcher API at {endpoint}: {GetUserFriendlyMessage(ex)}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "POST request timed out for {Endpoint}", endpoint);
            throw new DashboardApiException($"Request to {endpoint} timed out.", ex);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize POST response from {Endpoint}", endpoint);
            throw new DashboardApiException($"Invalid response from Launcher API at {endpoint}.", ex);
        }
    }

    private static string GetUserFriendlyMessage(HttpRequestException ex)
    {
        if (ex.StatusCode.HasValue)
        {
            return ex.StatusCode.Value switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Authentication required. Please log in.",
                System.Net.HttpStatusCode.Forbidden => "Access denied. Insufficient permissions.",
                System.Net.HttpStatusCode.NotFound => "Endpoint not found. Check Launcher API version.",
                System.Net.HttpStatusCode.ServiceUnavailable => "Launcher API is temporarily unavailable.",
                System.Net.HttpStatusCode.InternalServerError => "Launcher API encountered an internal error.",
                _ => $"HTTP {(int)ex.StatusCode.Value}: {ex.StatusCode.Value}"
            };
        }
        return "Unable to connect to Launcher API. Check network and configuration.";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Exception thrown when a Dashboard API call fails, with a user-friendly message.
/// </summary>
public sealed class DashboardApiException : Exception
{
    public DashboardApiException(string message) : base(message) { }
    public DashboardApiException(string message, Exception innerException) : base(message, innerException) { }
}

// --- DTOs ---

/// <summary>
/// System-wide status including uptime, version, node count, plugin count, and health.
/// </summary>
public sealed record SystemStatusDto
{
    public string Version { get; init; } = string.Empty;
    public TimeSpan Uptime { get; init; }
    public int NodeCount { get; init; }
    public int PluginCount { get; init; }
    public int ActivePluginCount { get; init; }
    public string HealthStatus { get; init; } = "Unknown";
    public long StorageUsedBytes { get; init; }
    public long StorageTotalBytes { get; init; }
    public double RequestsPerSecond { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Summary information about a plugin.
/// </summary>
public sealed record PluginInfoDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Health { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public int StrategyCount { get; init; }
    public int CapabilityCount { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// Full plugin detail including strategies and configuration.
/// </summary>
public sealed record PluginDetailDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Health { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<StrategyInfoDto> Strategies { get; init; } = Array.Empty<StrategyInfoDto>();
    public IReadOnlyDictionary<string, string> Configuration { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public DateTime LoadedAt { get; init; }
    public long MemoryUsageBytes { get; init; }
}

/// <summary>
/// Information about a strategy registered by a plugin.
/// </summary>
public sealed record StrategyInfoDto
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public double AverageLatencyMs { get; init; }
    public long InvocationCount { get; init; }
    public long ErrorCount { get; init; }
}

/// <summary>
/// Request to execute a SQL query.
/// </summary>
public sealed record QueryRequestDto
{
    public string Sql { get; init; } = string.Empty;
    public bool IncludeExecutionPlan { get; init; } = true;
    public int MaxRows { get; init; } = 1000;
}

/// <summary>
/// Result of a SQL query execution.
/// </summary>
public sealed record QueryResultDto
{
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; } = Array.Empty<IReadOnlyList<object?>>();
    public int RowCount { get; init; }
    public double ExecutionTimeMs { get; init; }
    public long BytesScanned { get; init; }
    public string? ExecutionPlan { get; init; }
    public string? Error { get; init; }
    public bool Success { get; init; } = true;
}

/// <summary>
/// A security event from the SIEM pipeline.
/// </summary>
public sealed record SecurityEventDto
{
    public string Id { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public string Severity { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string? SourceIp { get; init; }
    public string? UserId { get; init; }
}

/// <summary>
/// Cluster status including nodes, Raft state, and replication.
/// </summary>
public sealed record ClusterStatusDto
{
    public IReadOnlyList<ClusterNodeDto> Nodes { get; init; } = Array.Empty<ClusterNodeDto>();
    public RaftStatusDto Raft { get; init; } = new();
    public IReadOnlyList<ReplicationStatusDto> Replication { get; init; } = Array.Empty<ReplicationStatusDto>();
    public CrdtSyncStatusDto CrdtSync { get; init; } = new();
    public SwimMembershipDto SwimMembership { get; init; } = new();
}

/// <summary>
/// Status of an individual cluster node.
/// </summary>
public sealed record ClusterNodeDto
{
    public string NodeId { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty; // Leader, Follower, Candidate
    public string Health { get; init; } = string.Empty;
    public DateTime LastHeartbeat { get; init; }
    public TimeSpan Uptime { get; init; }
    public bool IsLocal { get; init; }
}

/// <summary>
/// Raft consensus state.
/// </summary>
public sealed record RaftStatusDto
{
    public long CurrentTerm { get; init; }
    public string LeaderId { get; init; } = string.Empty;
    public long LogIndex { get; init; }
    public long CommitIndex { get; init; }
    public string State { get; init; } = string.Empty;
}

/// <summary>
/// Per-node replication lag information.
/// </summary>
public sealed record ReplicationStatusDto
{
    public string NodeId { get; init; } = string.Empty;
    public long ReplicationLag { get; init; }
    public DateTime LastSyncTime { get; init; }
    public string Status { get; init; } = string.Empty;
}

/// <summary>
/// CRDT synchronization status.
/// </summary>
public sealed record CrdtSyncStatusDto
{
    public long MergeCount { get; init; }
    public int PendingMerges { get; init; }
    public DateTime LastMergeTime { get; init; }
    public string Status { get; init; } = string.Empty;
}

/// <summary>
/// SWIM protocol membership status.
/// </summary>
public sealed record SwimMembershipDto
{
    public int MemberCount { get; init; }
    public int SuspectCount { get; init; }
    public int HealthyCount { get; init; }
    public DateTime LastGossipRound { get; init; }
    public long GossipRoundNumber { get; init; }
}

/// <summary>
/// System metrics snapshot from the Launcher API.
/// </summary>
public sealed record MetricsDto
{
    public double CpuUsagePercent { get; init; }
    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }
    public double MemoryUsagePercent { get; init; }
    public long DiskUsedBytes { get; init; }
    public long DiskTotalBytes { get; init; }
    public double DiskUsagePercent { get; init; }
    public double RequestsPerSecond { get; init; }
    public int ActiveConnections { get; init; }
    public int ThreadCount { get; init; }
    public long UptimeSeconds { get; init; }
    public double AverageLatencyMs { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Security posture summary including score and vulnerability counts.
/// </summary>
public sealed record SecurityPostureDto
{
    public int Score { get; init; }
    public int MaxScore { get; init; } = 100;
    public int CriticalFindings { get; init; }
    public int HighFindings { get; init; }
    public int MediumFindings { get; init; }
    public int LowFindings { get; init; }
    public DateTime LastScanTime { get; init; }
    public int ActiveSessions { get; init; }
    public int FailedAuthAttemptsLastHour { get; init; }
}

/// <summary>
/// An active security incident.
/// </summary>
public sealed record SecurityIncidentDto
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty; // Open, Contained, Resolved
    public DateTime CreatedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string AssignedTo { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// SIEM transport connection status.
/// </summary>
public sealed record SiemTransportStatusDto
{
    public string TransportName { get; init; } = string.Empty;
    public string TransportType { get; init; } = string.Empty;
    public bool IsConnected { get; init; }
    public DateTime LastEventSent { get; init; }
    public long EventsSentTotal { get; init; }
    public long EventsDropped { get; init; }
    public string? ErrorMessage { get; init; }
}
