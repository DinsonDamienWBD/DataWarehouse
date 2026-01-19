using Microsoft.AspNetCore.SignalR;
using DataWarehouse.Dashboard.Services;

namespace DataWarehouse.Dashboard.Hubs;

/// <summary>
/// SignalR hub for real-time dashboard updates.
/// </summary>
public class DashboardHub : Hub
{
    private readonly ISystemHealthService _healthService;
    private readonly IPluginDiscoveryService _pluginService;
    private readonly IStorageManagementService _storageService;
    private readonly IAuditLogService _auditService;
    private readonly ILogger<DashboardHub> _logger;

    public DashboardHub(
        ISystemHealthService healthService,
        IPluginDiscoveryService pluginService,
        IStorageManagementService storageService,
        IAuditLogService auditService,
        ILogger<DashboardHub> logger)
    {
        _healthService = healthService;
        _pluginService = pluginService;
        _storageService = storageService;
        _auditService = auditService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);

        // Send initial state to the connected client
        await SendInitialState();

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(
            "Client disconnected: {ConnectionId}, Reason: {Reason}",
            Context.ConnectionId,
            exception?.Message ?? "Normal disconnect");

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to specific update channels.
    /// </summary>
    public async Task Subscribe(string channel)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, channel);
        _logger.LogDebug("Client {ConnectionId} subscribed to {Channel}", Context.ConnectionId, channel);
    }

    /// <summary>
    /// Unsubscribe from a channel.
    /// </summary>
    public async Task Unsubscribe(string channel)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, channel);
        _logger.LogDebug("Client {ConnectionId} unsubscribed from {Channel}", Context.ConnectionId, channel);
    }

    /// <summary>
    /// Request current system health status.
    /// </summary>
    public async Task GetHealthStatus()
    {
        var health = await _healthService.GetSystemHealthAsync();
        await Clients.Caller.SendAsync("HealthStatusUpdate", health);
    }

    /// <summary>
    /// Request current metrics.
    /// </summary>
    public async Task GetMetrics()
    {
        var metrics = _healthService.GetCurrentMetrics();
        await Clients.Caller.SendAsync("MetricsUpdate", metrics);
    }

    /// <summary>
    /// Request plugin list.
    /// </summary>
    public async Task GetPlugins()
    {
        var plugins = _pluginService.GetDiscoveredPlugins();
        await Clients.Caller.SendAsync("PluginsUpdate", plugins);
    }

    /// <summary>
    /// Request storage pools.
    /// </summary>
    public async Task GetStoragePools()
    {
        var pools = _storageService.GetStoragePools();
        await Clients.Caller.SendAsync("StoragePoolsUpdate", pools);
    }

    /// <summary>
    /// Request recent audit logs.
    /// </summary>
    public async Task GetRecentLogs(int count = 50)
    {
        var logs = _auditService.GetRecentLogs(count);
        await Clients.Caller.SendAsync("AuditLogsUpdate", logs);
    }

    /// <summary>
    /// Enable/disable a plugin.
    /// </summary>
    public async Task TogglePlugin(string pluginId, bool enable)
    {
        bool success;
        if (enable)
        {
            success = await _pluginService.EnablePluginAsync(pluginId);
        }
        else
        {
            success = await _pluginService.DisablePluginAsync(pluginId);
        }

        // Notify all clients about plugin state change
        var plugins = _pluginService.GetDiscoveredPlugins();
        await Clients.All.SendAsync("PluginsUpdate", plugins);

        await Clients.Caller.SendAsync("PluginToggleResult", new { PluginId = pluginId, Success = success, Enabled = enable });
    }

    /// <summary>
    /// Create a new storage pool.
    /// </summary>
    public async Task CreateStoragePool(string name, string poolType, long capacityBytes)
    {
        var pool = await _storageService.CreatePoolAsync(name, poolType, capacityBytes);

        // Notify all clients about new pool
        var pools = _storageService.GetStoragePools();
        await Clients.All.SendAsync("StoragePoolsUpdate", pools);

        await Clients.Caller.SendAsync("StoragePoolCreated", pool);
    }

    private async Task SendInitialState()
    {
        // Send initial state in parallel
        var healthTask = _healthService.GetSystemHealthAsync();

        var plugins = _pluginService.GetDiscoveredPlugins();
        var pools = _storageService.GetStoragePools();
        var metrics = _healthService.GetCurrentMetrics();
        var logs = _auditService.GetRecentLogs(20);

        var health = await healthTask;

        await Clients.Caller.SendAsync("InitialState", new
        {
            Health = health,
            Plugins = plugins,
            StoragePools = pools,
            Metrics = metrics,
            RecentLogs = logs
        });
    }
}

/// <summary>
/// Background service that broadcasts real-time updates via SignalR.
/// </summary>
public class DashboardBroadcastService : BackgroundService
{
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly ISystemHealthService _healthService;
    private readonly IStorageManagementService _storageService;
    private readonly IAuditLogService _auditService;
    private readonly ILogger<DashboardBroadcastService> _logger;

    public DashboardBroadcastService(
        IHubContext<DashboardHub> hubContext,
        ISystemHealthService healthService,
        IStorageManagementService storageService,
        IAuditLogService auditService,
        ILogger<DashboardBroadcastService> logger)
    {
        _hubContext = hubContext;
        _healthService = healthService;
        _storageService = storageService;
        _auditService = auditService;
        _logger = logger;

        // Subscribe to audit log events
        _auditService.EntryLogged += OnAuditEntryLogged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Dashboard broadcast service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Broadcast health status every 10 seconds
                var health = await _healthService.GetSystemHealthAsync();
                await _hubContext.Clients.Group("health").SendAsync("HealthStatusUpdate", health, stoppingToken);

                // Broadcast metrics every 5 seconds
                var metrics = _healthService.GetCurrentMetrics();
                await _hubContext.Clients.Group("metrics").SendAsync("MetricsUpdate", metrics, stoppingToken);

                // Broadcast storage stats every 30 seconds
                var pools = _storageService.GetStoragePools();
                await _hubContext.Clients.Group("storage").SendAsync("StoragePoolsUpdate", pools, stoppingToken);

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting dashboard updates");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        _logger.LogInformation("Dashboard broadcast service stopped");
    }

    private async void OnAuditEntryLogged(object? sender, AuditLogEntry entry)
    {
        try
        {
            await _hubContext.Clients.Group("audit").SendAsync("NewAuditEntry", entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting audit entry");
        }
    }
}
