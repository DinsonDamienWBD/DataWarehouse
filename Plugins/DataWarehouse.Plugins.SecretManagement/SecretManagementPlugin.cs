using System.Collections.Concurrent;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Plugins.SecretManagement;

/// <summary>
/// Plugin providing production-ready secret management with multi-provider support.
/// </summary>
public class SecretManagementPlugin : SecurityProviderPluginBase, ISecretManager, IAsyncDisposable
{
    public override string Id => "com.datawarehouse.security.secretmanagement";
    public override string Name => "Secret Management";

    private readonly ConcurrentDictionary<string, CachedSecret> _cache = new();
    private readonly ConcurrentDictionary<SecretProvider, ISecretProvider> _providers = new();
    private readonly SemaphoreSlim _rotationLock = new(1, 1);
    private readonly SecretManagerConfig _config;
    private readonly Timer? _cacheCleanupTimer;
    private readonly Timer? _rotationCheckTimer;
    private bool _disposed;

    // Patterns that indicate plain-text secrets
    private static readonly Regex[] PlainTextSecretPatterns = new[]
    {
        new Regex(@"password\s*[:=]\s*[""']?[^""'\s]{8,}[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"secret\s*[:=]\s*[""']?[^""'\s]{8,}[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"api[_-]?key\s*[:=]\s*[""']?[^""'\s]{16,}[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"access[_-]?key\s*[:=]\s*[""']?[A-Z0-9]{16,}[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"private[_-]?key\s*[:=]\s*[""']?-----BEGIN", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"connection[_-]?string\s*[:=]\s*[""']?[^""'\s]*password=[^""'\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"bearer\s+[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+", RegexOptions.Compiled),
    };

    public SecretManagementPlugin(SecretManagerConfig? config = null)
    {
        _config = config ?? new SecretManagerConfig();

        // Register default environment provider
        _providers[SecretProvider.Environment] = new EnvironmentSecretProvider();

        if (_config.CacheCleanupInterval > TimeSpan.Zero)
        {
            _cacheCleanupTimer = new Timer(
                _ => CleanupExpiredCache(),
                null,
                _config.CacheCleanupInterval,
                _config.CacheCleanupInterval);
        }

        if (_config.RotationCheckInterval > TimeSpan.Zero)
        {
            _rotationCheckTimer = new Timer(
                async _ => await CheckRotationsAsync(),
                null,
                _config.RotationCheckInterval,
                _config.RotationCheckInterval);
        }
    }

    public void RegisterProvider(SecretProvider type, ISecretProvider provider)
    {
        _providers[type] = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<string> GetSecretAsync(SecretReference reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var cacheKey = GetCacheKey(reference);
        var ttl = reference.CacheTtl ?? _config.DefaultCacheTtl;

        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.Value;
        }

        var provider = GetProvider(reference.Provider);
        var value = await provider.GetSecretAsync(reference.Path, reference.Version, ct);

        if (!string.IsNullOrEmpty(reference.Field) && value.StartsWith('{'))
        {
            value = ExtractField(value, reference.Field);
        }

        _cache[cacheKey] = new CachedSecret
        {
            Value = value,
            ExpiresAt = DateTime.UtcNow.Add(ttl),
            Reference = reference
        };

        return value;
    }

    public async Task<byte[]> GetSecretBytesAsync(SecretReference reference, CancellationToken ct = default)
    {
        var value = await GetSecretAsync(reference, ct);
        return Convert.FromBase64String(value);
    }

    public async Task SetSecretAsync(SecretReference reference, string value, SecretMetadata? metadata = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(value);

        var provider = GetProvider(reference.Provider);
        await provider.SetSecretAsync(reference.Path, value, metadata, ct);

        var cacheKey = GetCacheKey(reference);
        _cache.TryRemove(cacheKey, out _);
    }

    public async Task<SecretRotationResult> RotateSecretAsync(SecretReference reference, string newValue, TimeSpan gracePeriod, CancellationToken ct = default)
    {
        await _rotationLock.WaitAsync(ct);
        try
        {
            var provider = GetProvider(reference.Provider);

            string? oldVersion = null;
            try
            {
                oldVersion = await provider.GetCurrentVersionAsync(reference.Path, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SecretManagementPlugin] Operation failed: {ex.Message}");
            }

            var newVersion = $"v{DateTime.UtcNow:yyyyMMddHHmmss}";
            var metadata = new SecretMetadata
            {
                CreatedBy = "SecretManager.Rotate",
                Tags = new Dictionary<string, string>
                {
                    ["rotation"] = "true",
                    ["previousVersion"] = oldVersion ?? "none",
                    ["gracePeriodEnds"] = DateTime.UtcNow.Add(gracePeriod).ToString("O")
                }
            };

            await provider.SetSecretVersionAsync(reference.Path, newVersion, newValue, metadata, ct);

            var cacheKey = GetCacheKey(reference);
            _cache.TryRemove(cacheKey, out _);

            return new SecretRotationResult
            {
                Success = true,
                NewVersion = newVersion,
                OldVersion = oldVersion,
                GracePeriodEndsAt = DateTime.UtcNow.Add(gracePeriod)
            };
        }
        catch (Exception ex)
        {
            return new SecretRotationResult
            {
                Success = false,
                Error = ex.Message
            };
        }
        finally
        {
            _rotationLock.Release();
        }
    }

    public async Task DeleteSecretAsync(SecretReference reference, CancellationToken ct = default)
    {
        var provider = GetProvider(reference.Provider);
        await provider.DeleteSecretAsync(reference.Path, ct);

        var cacheKey = GetCacheKey(reference);
        _cache.TryRemove(cacheKey, out _);
    }

    public async Task<bool> ExistsAsync(SecretReference reference, CancellationToken ct = default)
    {
        var provider = GetProvider(reference.Provider);
        return await provider.ExistsAsync(reference.Path, ct);
    }

    public async Task<IEnumerable<SecretReference>> ListSecretsAsync(string? prefix = null, CancellationToken ct = default)
    {
        var results = new List<SecretReference>();
        foreach (var (providerType, provider) in _providers)
        {
            var paths = await provider.ListSecretsAsync(prefix, ct);
            results.AddRange(paths.Select(p => new SecretReference { Provider = providerType, Path = p }));
        }
        return results;
    }

    public void ValidateNoPlainTextSecrets(object config)
    {
        if (!_config.EnforceNoPlainTextSecrets) return;

        var json = JsonSerializer.Serialize(config);
        foreach (var pattern in PlainTextSecretPatterns)
        {
            if (pattern.IsMatch(json))
            {
                throw new SecurityException($"Plain-text secret detected in configuration. Pattern: {pattern}");
            }
        }
    }

    public async Task<T> ResolveSecretsAsync<T>(T config, CancellationToken ct = default) where T : class
    {
        var json = JsonSerializer.Serialize(config);
        var pattern = new Regex(@"\$\{secret:([^}]+)\}");

        var matches = pattern.Matches(json);
        foreach (Match match in matches)
        {
            var reference = SecretReference.Parse(match.Groups[1].Value);
            var value = await GetSecretAsync(reference, ct);
            json = json.Replace(match.Value, value);
        }

        return JsonSerializer.Deserialize<T>(json)!;
    }

    private ISecretProvider GetProvider(SecretProvider type)
    {
        if (_providers.TryGetValue(type, out var provider))
            return provider;
        throw new InvalidOperationException($"No provider registered for type: {type}");
    }

    private static string GetCacheKey(SecretReference reference)
        => $"{reference.Provider}:{reference.Path}:{reference.Version ?? "latest"}:{reference.Field ?? ""}";

    private static string ExtractField(string json, string field)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty(field, out var value))
            return value.ToString();
        throw new KeyNotFoundException($"Field '{field}' not found in secret");
    }

    private void CleanupExpiredCache()
    {
        var now = DateTime.UtcNow;
        var expiredKeys = _cache.Where(kvp => kvp.Value.ExpiresAt <= now).Select(kvp => kvp.Key).ToList();
        foreach (var key in expiredKeys)
            _cache.TryRemove(key, out _);
    }

    private async Task CheckRotationsAsync()
    {
        foreach (var cached in _cache.Values.Where(c => c.Reference.CacheTtl.HasValue))
        {
            try
            {
                var provider = GetProvider(cached.Reference.Provider);
                var currentVersion = await provider.GetCurrentVersionAsync(cached.Reference.Path);
                if (currentVersion != cached.Reference.Version)
                {
                    var cacheKey = GetCacheKey(cached.Reference);
                    _cache.TryRemove(cacheKey, out _);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SecretManagementPlugin] Operation failed: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cacheCleanupTimer?.Dispose();
        _rotationCheckTimer?.Dispose();
        _rotationLock.Dispose();
        _cache.Clear();

        await ValueTask.CompletedTask;
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["SecurityType"] = "SecretManagement";
        metadata["SupportsEncryption"] = false;
        metadata["SupportsACL"] = false;
        metadata["SupportsAuthentication"] = false;
        metadata["SupportsSecretRotation"] = true;
        metadata["SupportsMultiProvider"] = true;
        metadata["SupportsCaching"] = true;
        return metadata;
    }

    private sealed class CachedSecret
    {
        public string Value { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public SecretReference Reference { get; set; } = null!;
    }
}

/// <summary>
/// Environment variable secret provider.
/// </summary>
public class EnvironmentSecretProvider : ISecretProvider
{
    public Task<string> GetSecretAsync(string path, string? version = null, CancellationToken ct = default)
    {
        var value = Environment.GetEnvironmentVariable(path);
        if (value == null)
            throw new KeyNotFoundException($"Environment variable '{path}' not found");
        return Task.FromResult(value);
    }

    public Task SetSecretAsync(string path, string value, SecretMetadata? metadata = null, CancellationToken ct = default)
    {
        Environment.SetEnvironmentVariable(path, value);
        return Task.CompletedTask;
    }

    public Task SetSecretVersionAsync(string path, string version, string value, SecretMetadata? metadata = null, CancellationToken ct = default)
        => SetSecretAsync(path, value, metadata, ct);

    public Task<string?> GetCurrentVersionAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task DeleteSecretAsync(string path, CancellationToken ct = default)
    {
        Environment.SetEnvironmentVariable(path, null);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Environment.GetEnvironmentVariable(path) != null);

    public Task<IEnumerable<string>> ListSecretsAsync(string? prefix = null, CancellationToken ct = default)
    {
        var vars = Environment.GetEnvironmentVariables();
        var keys = vars.Keys.Cast<string>();
        if (!string.IsNullOrEmpty(prefix))
            keys = keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(keys);
    }
}

/// <summary>
/// Configuration for the secret management plugin.
/// </summary>
public class SecretManagerConfig
{
    public TimeSpan DefaultCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan CacheCleanupInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan RotationCheckInterval { get; set; } = TimeSpan.FromHours(1);
    public bool EnforceNoPlainTextSecrets { get; set; } = true;
}
