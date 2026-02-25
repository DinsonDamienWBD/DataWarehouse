using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Configuration;

// ============================================================================
// CONFIGURATION HIERARCHY
// Implements Instance -> Tenant -> User configuration cascade.
// Resolution order: User > Tenant > Instance > Default
// Respects AllowUserToOverride/AllowTenantToOverride flags.
// ============================================================================

#region Configuration Level

/// <summary>
/// Hierarchy level for configuration values.
/// Higher levels override lower levels when allowed.
/// </summary>
public enum ConfigurationLevel
{
    /// <summary>
    /// Instance-wide defaults. Lowest priority.
    /// Scope: empty string (global).
    /// </summary>
    Instance = 0,

    /// <summary>
    /// Tenant-specific overrides. Middle priority.
    /// Scope: tenantId.
    /// </summary>
    Tenant = 1,

    /// <summary>
    /// User-specific overrides. Highest priority.
    /// Scope: userId.
    /// </summary>
    User = 2
}

#endregion

#region Configuration Store

/// <summary>
/// Persistence interface for configuration values.
/// </summary>
public interface IConfigurationStore
{
    /// <summary>
    /// Saves a configuration value to persistent storage.
    /// </summary>
    Task SaveAsync(ConfigurationLevel level, string scope, string key, object? value, CancellationToken ct = default);

    /// <summary>
    /// Removes a configuration value from persistent storage.
    /// </summary>
    Task RemoveAsync(ConfigurationLevel level, string scope, string key, CancellationToken ct = default);

    /// <summary>
    /// Loads all configuration entries from storage.
    /// </summary>
    Task<IReadOnlyList<ConfigurationEntry>> LoadAllAsync(CancellationToken ct = default);
}

/// <summary>
/// A single configuration entry as stored persistently.
/// </summary>
public sealed record ConfigurationEntry
{
    public ConfigurationLevel Level { get; init; }
    public string Scope { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public object? Value { get; init; }
    public DateTimeOffset LastModified { get; init; } = DateTimeOffset.UtcNow;
    public string? ModifiedBy { get; init; }
}

/// <summary>
/// File-based configuration store. Persists to {dataDir}/config/{level}/{scope}.json.
/// </summary>
public sealed class FileConfigurationStore : IConfigurationStore
{
    private readonly string _baseDir;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileConfigurationStore(string dataDirectory)
    {
        _baseDir = Path.Combine(dataDirectory, "config");
        Directory.CreateDirectory(_baseDir);
    }

    public async Task SaveAsync(ConfigurationLevel level, string scope, string key, object? value, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await LoadScopeAsync(level, scope, ct).ConfigureAwait(false);
            var mutable = new Dictionary<string, ConfigurationEntry>(entries);
            mutable[key] = new ConfigurationEntry
            {
                Level = level,
                Scope = scope,
                Key = key,
                Value = value,
                LastModified = DateTimeOffset.UtcNow
            };
            await SaveScopeAsync(level, scope, mutable, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RemoveAsync(ConfigurationLevel level, string scope, string key, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await LoadScopeAsync(level, scope, ct).ConfigureAwait(false);
            var mutable = new Dictionary<string, ConfigurationEntry>(entries);
            mutable.Remove(key);
            await SaveScopeAsync(level, scope, mutable, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ConfigurationEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        var result = new List<ConfigurationEntry>();

        foreach (ConfigurationLevel level in Enum.GetValues(typeof(ConfigurationLevel)))
        {
            var levelDir = GetLevelDirectory(level);
            if (!Directory.Exists(levelDir))
                continue;

            foreach (var file in Directory.GetFiles(levelDir, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    var entries = JsonSerializer.Deserialize<Dictionary<string, ConfigurationEntry>>(json, _jsonOptions);
                    if (entries is not null)
                        result.AddRange(entries.Values);
                }
                catch (JsonException)
                {
                    // Skip corrupt files
                }
            }
        }

        return result;
    }

    private async Task<Dictionary<string, ConfigurationEntry>> LoadScopeAsync(
        ConfigurationLevel level, string scope, CancellationToken ct)
    {
        var path = GetFilePath(level, scope);
        if (!File.Exists(path))
            return new Dictionary<string, ConfigurationEntry>();

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Dictionary<string, ConfigurationEntry>>(json, _jsonOptions)
                ?? new Dictionary<string, ConfigurationEntry>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, ConfigurationEntry>();
        }
    }

    private async Task SaveScopeAsync(
        ConfigurationLevel level, string scope, Dictionary<string, ConfigurationEntry> entries, CancellationToken ct)
    {
        var path = GetFilePath(level, scope);
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(entries, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    private string GetLevelDirectory(ConfigurationLevel level) =>
        Path.Combine(_baseDir, level.ToString().ToLowerInvariant());

    private string GetFilePath(ConfigurationLevel level, string scope)
    {
        var safeScope = string.IsNullOrEmpty(scope) ? "_global" : SanitizeFileName(scope);
        return Path.Combine(GetLevelDirectory(level), $"{safeScope}.json");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        return new string(sanitized);
    }
}

/// <summary>
/// In-memory configuration store for testing and ephemeral scenarios.
/// </summary>
public sealed class InMemoryConfigurationStore : IConfigurationStore
{
    private readonly BoundedDictionary<(ConfigurationLevel Level, string Scope, string Key), ConfigurationEntry> _store = new BoundedDictionary<(ConfigurationLevel Level, string Scope, string Key), ConfigurationEntry>(1000);

    public Task SaveAsync(ConfigurationLevel level, string scope, string key, object? value, CancellationToken ct = default)
    {
        var entry = new ConfigurationEntry
        {
            Level = level,
            Scope = scope,
            Key = key,
            Value = value,
            LastModified = DateTimeOffset.UtcNow
        };
        _store[(level, scope, key)] = entry;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(ConfigurationLevel level, string scope, string key, CancellationToken ct = default)
    {
        _store.TryRemove((level, scope, key), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConfigurationEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ConfigurationEntry> result = _store.Values.ToList();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Clears all stored configuration entries.
    /// </summary>
    public void Clear() => _store.Clear();

    /// <summary>
    /// Gets the count of stored entries.
    /// </summary>
    public int Count => _store.Count;
}

#endregion

#region Configuration Hierarchy

/// <summary>
/// Manages configuration hierarchy with Instance -> Tenant -> User cascade.
/// Resolution order: User > Tenant > Instance > Default.
/// Respects AllowUserToOverride/AllowTenantToOverride flags per parameter.
/// </summary>
public sealed class ConfigurationHierarchy
{
    private readonly BoundedDictionary<(ConfigurationLevel Level, string Scope, string Key), object?> _values = new BoundedDictionary<(ConfigurationLevel Level, string Scope, string Key), object?>(1000);
    private readonly BoundedDictionary<string, ConfigurableParameter> _parameterDefinitions = new BoundedDictionary<string, ConfigurableParameter>(1000);
    private readonly IConfigurationStore? _store;
    private readonly SemaphoreSlim _persistLock = new(1, 1);

    /// <summary>
    /// Creates a configuration hierarchy with optional persistence.
    /// </summary>
    /// <param name="store">Optional persistent store. Pass null for in-memory only.</param>
    public ConfigurationHierarchy(IConfigurationStore? store = null)
    {
        _store = store;
    }

    /// <summary>
    /// Loads all configuration from the persistent store.
    /// Call once at startup.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_store is null)
            return;

        var entries = await _store.LoadAllAsync(ct).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            _values[(entry.Level, entry.Scope, entry.Key)] = entry.Value;
        }
    }

    /// <summary>
    /// Registers a parameter definition for override control enforcement.
    /// </summary>
    public void RegisterParameter(ConfigurableParameter parameter)
    {
        _parameterDefinitions[parameter.Name] = parameter;
    }

    /// <summary>
    /// Registers multiple parameter definitions.
    /// </summary>
    public void RegisterParameters(IEnumerable<ConfigurableParameter> parameters)
    {
        foreach (var param in parameters)
            RegisterParameter(param);
    }

    /// <summary>
    /// Sets a configuration value at the specified level and scope.
    /// </summary>
    /// <param name="level">Configuration level (Instance, Tenant, User).</param>
    /// <param name="scope">Scope identifier: empty for Instance, tenantId for Tenant, userId for User.</param>
    /// <param name="key">Configuration key.</param>
    /// <param name="value">Configuration value.</param>
    /// <exception cref="InvalidOperationException">If override is not allowed at this level.</exception>
    public async Task SetConfigurationAsync(
        ConfigurationLevel level, string scope, string key, object? value, CancellationToken ct = default)
    {
        // Enforce override rules
        if (_parameterDefinitions.TryGetValue(key, out var paramDef))
        {
            if (level == ConfigurationLevel.Tenant && !paramDef.AllowTenantToOverride)
                throw new InvalidOperationException(
                    $"Parameter '{key}' does not allow tenant-level overrides.");

            if (level == ConfigurationLevel.User && !paramDef.AllowUserToOverride)
                throw new InvalidOperationException(
                    $"Parameter '{key}' does not allow user-level overrides.");

            // Validate value
            var errors = ParameterValidator.Validate(paramDef, value);
            if (errors.Count > 0)
                throw new ConfigurationValidationException(key, value, errors);
        }

        _values[(level, scope, key)] = value;

        if (_store is not null)
        {
            await _persistLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _store.SaveAsync(level, scope, key, value, ct).ConfigureAwait(false);
            }
            finally
            {
                _persistLock.Release();
            }
        }
    }

    /// <summary>
    /// Sets a configuration value synchronously (for backward compatibility).
    /// </summary>
    public void SetConfiguration(ConfigurationLevel level, string scope, string key, object? value)
    {
        // Enforce override rules
        if (_parameterDefinitions.TryGetValue(key, out var paramDef))
        {
            if (level == ConfigurationLevel.Tenant && !paramDef.AllowTenantToOverride)
                throw new InvalidOperationException(
                    $"Parameter '{key}' does not allow tenant-level overrides.");

            if (level == ConfigurationLevel.User && !paramDef.AllowUserToOverride)
                throw new InvalidOperationException(
                    $"Parameter '{key}' does not allow user-level overrides.");

            var errors = ParameterValidator.Validate(paramDef, value);
            if (errors.Count > 0)
                throw new ConfigurationValidationException(key, value, errors);
        }

        _values[(level, scope, key)] = value;
    }

    /// <summary>
    /// Removes a configuration value at the specified level and scope.
    /// </summary>
    public async Task RemoveConfigurationAsync(
        ConfigurationLevel level, string scope, string key, CancellationToken ct = default)
    {
        _values.TryRemove((level, scope, key), out _);

        if (_store is not null)
        {
            await _persistLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _store.RemoveAsync(level, scope, key, ct).ConfigureAwait(false);
            }
            finally
            {
                _persistLock.Release();
            }
        }
    }

    /// <summary>
    /// Gets the effective value for a key, resolving through the hierarchy.
    /// Resolution order: User > Tenant > Instance > Default.
    /// Respects AllowUserToOverride and AllowTenantToOverride flags.
    /// </summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">Configuration key.</param>
    /// <param name="tenantId">Optional tenant ID for tenant-level resolution.</param>
    /// <param name="userId">Optional user ID for user-level resolution.</param>
    /// <returns>Effective value or default(T) if not found.</returns>
    public T? GetEffectiveValue<T>(string key, string? tenantId = null, string? userId = null)
    {
        var (found, value) = ResolveValue(key, tenantId, userId);
        if (!found || value is null)
            return default;

        return ConvertValue<T>(value);
    }

    /// <summary>
    /// Gets the effective value for a key with a specified default.
    /// </summary>
    public T GetEffectiveValueOrDefault<T>(string key, T defaultValue, string? tenantId = null, string? userId = null)
    {
        var (found, value) = ResolveValue(key, tenantId, userId);
        if (!found || value is null)
            return defaultValue;

        try
        {
            return ConvertValue<T>(value) ?? defaultValue;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigurationHierarchy.GetEffectiveValueOrDefault] {ex.GetType().Name}: {ex.Message}");
            return defaultValue;
        }
    }

    /// <summary>
    /// Gets all effective configuration considering hierarchy, for the given context.
    /// </summary>
    public IReadOnlyDictionary<string, object> GetEffectiveConfiguration(
        string? tenantId = null, string? userId = null)
    {
        var result = new Dictionary<string, object>();

        // Collect all keys at Instance level
        var allKeys = new HashSet<string>();
        foreach (var key in _values.Keys)
            allKeys.Add(key.Key);

        // Resolve each key through hierarchy
        foreach (var key in allKeys)
        {
            var (found, value) = ResolveValue(key, tenantId, userId);
            if (found && value is not null)
                result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Gets the level at which a value is effectively set.
    /// </summary>
    public ConfigurationLevel? GetEffectiveLevel(string key, string? tenantId = null, string? userId = null)
    {
        var allowUserOverride = true;
        var allowTenantOverride = true;

        if (_parameterDefinitions.TryGetValue(key, out var paramDef))
        {
            allowUserOverride = paramDef.AllowUserToOverride;
            allowTenantOverride = paramDef.AllowTenantToOverride;
        }

        // Check User level
        if (allowUserOverride && !string.IsNullOrEmpty(userId) &&
            _values.ContainsKey((ConfigurationLevel.User, userId, key)))
            return ConfigurationLevel.User;

        // Check Tenant level
        if (allowTenantOverride && !string.IsNullOrEmpty(tenantId) &&
            _values.ContainsKey((ConfigurationLevel.Tenant, tenantId, key)))
            return ConfigurationLevel.Tenant;

        // Check Instance level
        if (_values.ContainsKey((ConfigurationLevel.Instance, string.Empty, key)))
            return ConfigurationLevel.Instance;

        return null;
    }

    /// <summary>
    /// Gets all entries at a specific level and scope.
    /// </summary>
    public IReadOnlyDictionary<string, object?> GetLevelConfiguration(ConfigurationLevel level, string scope = "")
    {
        var result = new Dictionary<string, object?>();
        foreach (var kvp in _values)
        {
            if (kvp.Key.Level == level && kvp.Key.Scope == scope)
                result[kvp.Key.Key] = kvp.Value;
        }
        return result;
    }

    /// <summary>
    /// Gets a snapshot of all configuration entries (for export/diagnostics).
    /// </summary>
    public IReadOnlyList<ConfigurationEntry> GetAllEntries()
    {
        return _values.Select(kvp => new ConfigurationEntry
        {
            Level = kvp.Key.Level,
            Scope = kvp.Key.Scope,
            Key = kvp.Key.Key,
            Value = kvp.Value
        }).ToList();
    }

    #region Private Resolution

    private (bool Found, object? Value) ResolveValue(string key, string? tenantId, string? userId)
    {
        var allowUserOverride = true;
        var allowTenantOverride = true;

        if (_parameterDefinitions.TryGetValue(key, out var paramDef))
        {
            allowUserOverride = paramDef.AllowUserToOverride;
            allowTenantOverride = paramDef.AllowTenantToOverride;
        }

        // User level (highest priority)
        if (allowUserOverride && !string.IsNullOrEmpty(userId) &&
            _values.TryGetValue((ConfigurationLevel.User, userId, key), out var userValue))
            return (true, userValue);

        // Tenant level
        if (allowTenantOverride && !string.IsNullOrEmpty(tenantId) &&
            _values.TryGetValue((ConfigurationLevel.Tenant, tenantId, key), out var tenantValue))
            return (true, tenantValue);

        // Instance level (lowest priority)
        if (_values.TryGetValue((ConfigurationLevel.Instance, string.Empty, key), out var instanceValue))
            return (true, instanceValue);

        // Default from parameter definition
        if (paramDef?.DefaultValue is not null)
            return (true, paramDef.DefaultValue);

        return (false, null);
    }

    private static T? ConvertValue<T>(object value)
    {
        if (value is T typed)
            return typed;

        if (value is JsonElement jsonElement)
            return ConvertJsonElement<T>(jsonElement);

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigurationHierarchy.ConvertValue] {ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    private static T? ConvertJsonElement<T>(JsonElement element)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConfigurationHierarchy.ConvertJsonElement] {ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    #endregion
}

#endregion
