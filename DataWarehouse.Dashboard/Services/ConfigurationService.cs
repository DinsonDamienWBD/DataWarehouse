using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Dashboard.Services;

/// <summary>
/// Service for managing system and plugin configurations.
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// Gets the global system configuration.
    /// </summary>
    SystemConfiguration GetSystemConfiguration();

    /// <summary>
    /// Updates the global system configuration.
    /// </summary>
    Task UpdateSystemConfigurationAsync(SystemConfiguration config);

    /// <summary>
    /// Gets security policy settings.
    /// </summary>
    SecurityPolicySettings GetSecurityPolicies();

    /// <summary>
    /// Updates security policy settings.
    /// </summary>
    Task UpdateSecurityPoliciesAsync(SecurityPolicySettings policies);

    /// <summary>
    /// Gets tenant configurations.
    /// </summary>
    IEnumerable<TenantConfiguration> GetTenants();

    /// <summary>
    /// Gets a specific tenant configuration.
    /// </summary>
    TenantConfiguration? GetTenant(string tenantId);

    /// <summary>
    /// Creates or updates a tenant configuration.
    /// </summary>
    Task SaveTenantAsync(TenantConfiguration tenant);

    /// <summary>
    /// Deletes a tenant configuration.
    /// </summary>
    Task DeleteTenantAsync(string tenantId);

    /// <summary>
    /// Gets plugin-specific configuration.
    /// </summary>
    Dictionary<string, object>? GetPluginConfiguration(string pluginId);

    /// <summary>
    /// Updates plugin-specific configuration.
    /// </summary>
    Task UpdatePluginConfigurationAsync(string pluginId, Dictionary<string, object> config);

    /// <summary>
    /// Event raised when configuration changes.
    /// </summary>
    event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;
}

public class SystemConfiguration
{
    public string InstanceName { get; set; } = "DataWarehouse";
    public string Environment { get; set; } = "Development";
    public string DataDirectory { get; set; } = "./data";
    public string LogDirectory { get; set; } = "./logs";
    public int MaxConcurrentOperations { get; set; } = 100;
    public long MaxStorageSizeBytes { get; set; } = 1024L * 1024 * 1024 * 100; // 100 GB
    public bool EnableTelemetry { get; set; } = true;
    public bool EnableAutoBackup { get; set; } = true;
    public int BackupRetentionDays { get; set; } = 30;
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MetricsCollectionInterval { get; set; } = TimeSpan.FromSeconds(30);
    public Dictionary<string, string> CustomSettings { get; set; } = new();
}

public class SecurityPolicySettings
{
    public bool RequireAuthentication { get; set; } = true;
    public bool RequireHttps { get; set; } = true;
    public bool EnableAuditLogging { get; set; } = true;
    public bool EnableRateLimiting { get; set; } = true;
    public int MaxRequestsPerMinute { get; set; } = 1000;
    public int MaxFailedLoginAttempts { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(8);
    public TimeSpan TokenExpiration { get; set; } = TimeSpan.FromHours(1);
    public bool EnforcePasswordComplexity { get; set; } = true;
    public int MinPasswordLength { get; set; } = 12;
    public bool RequireMfa { get; set; } = false;
    public List<string> AllowedOrigins { get; set; } = new() { "*" };
    public List<string> AllowedIpRanges { get; set; } = new();
    public bool EnableEncryptionAtRest { get; set; } = true;
    public bool EnableEncryptionInTransit { get; set; } = true;
}

public class TenantConfiguration
{
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }
    public TenantQuotas Quotas { get; set; } = new();
    public List<string> AllowedPlugins { get; set; } = new();
    public List<string> AllowedStoragePools { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class TenantQuotas
{
    public long MaxStorageBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB
    public int MaxOperationsPerHour { get; set; } = 10000;
    public int MaxConcurrentConnections { get; set; } = 100;
    public int MaxDatabases { get; set; } = 10;
    public int MaxUsers { get; set; } = 50;
}

public class ConfigurationChangedEventArgs : EventArgs
{
    public string ConfigurationType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public object? OldValue { get; set; }
    public object? NewValue { get; set; }
    public string? ChangedBy { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Implementation of configuration service with in-memory storage.
/// </summary>
public class ConfigurationService : IConfigurationService
{
    private SystemConfiguration _systemConfig = new();
    private SecurityPolicySettings _securityPolicies = new();
    private readonly BoundedDictionary<string, TenantConfiguration> _tenants = new BoundedDictionary<string, TenantConfiguration>(1000);
    private readonly BoundedDictionary<string, Dictionary<string, object>> _pluginConfigs = new BoundedDictionary<string, Dictionary<string, object>>(1000);
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    public ConfigurationService()
    {
        // Initialize with sample tenants
        InitializeSampleData();
    }

    public SystemConfiguration GetSystemConfiguration() => _systemConfig;

    public async Task UpdateSystemConfigurationAsync(SystemConfiguration config)
    {
        await _saveLock.WaitAsync();
        try
        {
            var oldConfig = _systemConfig;
            _systemConfig = config;

            OnConfigurationChanged(new ConfigurationChangedEventArgs
            {
                ConfigurationType = "System",
                OldValue = oldConfig,
                NewValue = config
            });
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public SecurityPolicySettings GetSecurityPolicies() => _securityPolicies;

    public async Task UpdateSecurityPoliciesAsync(SecurityPolicySettings policies)
    {
        await _saveLock.WaitAsync();
        try
        {
            var oldPolicies = _securityPolicies;
            _securityPolicies = policies;

            OnConfigurationChanged(new ConfigurationChangedEventArgs
            {
                ConfigurationType = "SecurityPolicies",
                OldValue = oldPolicies,
                NewValue = policies
            });
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public IEnumerable<TenantConfiguration> GetTenants() => _tenants.Values.ToList();

    public TenantConfiguration? GetTenant(string tenantId)
    {
        return _tenants.TryGetValue(tenantId, out var tenant) ? tenant : null;
    }

    public async Task SaveTenantAsync(TenantConfiguration tenant)
    {
        await _saveLock.WaitAsync();
        try
        {
            var isNew = !_tenants.ContainsKey(tenant.TenantId);
            var oldTenant = isNew ? null : _tenants[tenant.TenantId];

            tenant.ModifiedAt = DateTime.UtcNow;
            _tenants[tenant.TenantId] = tenant;

            OnConfigurationChanged(new ConfigurationChangedEventArgs
            {
                ConfigurationType = "Tenant",
                EntityId = tenant.TenantId,
                OldValue = oldTenant,
                NewValue = tenant
            });
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task DeleteTenantAsync(string tenantId)
    {
        await _saveLock.WaitAsync();
        try
        {
            if (_tenants.TryRemove(tenantId, out var tenant))
            {
                OnConfigurationChanged(new ConfigurationChangedEventArgs
                {
                    ConfigurationType = "Tenant",
                    EntityId = tenantId,
                    OldValue = tenant,
                    NewValue = null
                });
            }
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public Dictionary<string, object>? GetPluginConfiguration(string pluginId)
    {
        return _pluginConfigs.TryGetValue(pluginId, out var config) ? config : null;
    }

    public async Task UpdatePluginConfigurationAsync(string pluginId, Dictionary<string, object> config)
    {
        await _saveLock.WaitAsync();
        try
        {
            var oldConfig = _pluginConfigs.TryGetValue(pluginId, out var existing) ? existing : null;
            _pluginConfigs[pluginId] = config;

            OnConfigurationChanged(new ConfigurationChangedEventArgs
            {
                ConfigurationType = "PluginConfiguration",
                EntityId = pluginId,
                OldValue = oldConfig,
                NewValue = config
            });
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void OnConfigurationChanged(ConfigurationChangedEventArgs args)
    {
        ConfigurationChanged?.Invoke(this, args);
    }

    private void InitializeSampleData()
    {
        // Add sample tenants
        var defaultTenant = new TenantConfiguration
        {
            TenantId = "default",
            Name = "Default Tenant",
            Description = "Default system tenant",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            Quotas = new TenantQuotas
            {
                MaxStorageBytes = 100L * 1024 * 1024 * 1024, // 100 GB
                MaxOperationsPerHour = 100000
            }
        };
        _tenants["default"] = defaultTenant;

        var devTenant = new TenantConfiguration
        {
            TenantId = "dev",
            Name = "Development",
            Description = "Development and testing tenant",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow.AddDays(-15),
            Quotas = new TenantQuotas
            {
                MaxStorageBytes = 10L * 1024 * 1024 * 1024, // 10 GB
                MaxOperationsPerHour = 10000
            }
        };
        _tenants["dev"] = devTenant;

        var prodTenant = new TenantConfiguration
        {
            TenantId = "prod",
            Name = "Production",
            Description = "Production tenant",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            Quotas = new TenantQuotas
            {
                MaxStorageBytes = 500L * 1024 * 1024 * 1024, // 500 GB
                MaxOperationsPerHour = 500000,
                MaxConcurrentConnections = 500,
                MaxDatabases = 50
            }
        };
        _tenants["prod"] = prodTenant;
    }
}
