using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace DataWarehouse.SDK.Infrastructure;

// ============================================================================
// PHASE 5 - E1: Single File Deploy
// Uses: ZeroConfigurationStartup
// ============================================================================

#region Embedded Resource Manager

/// <summary>
/// Manages embedded resources for bundled plugins.
/// </summary>
public sealed class EmbeddedResourceManager
{
    private readonly ConcurrentDictionary<string, byte[]> _cachedResources = new();
    private readonly Assembly _assembly;

    public EmbeddedResourceManager(Assembly? assembly = null)
    {
        _assembly = assembly ?? Assembly.GetExecutingAssembly();
    }

    /// <summary>
    /// Gets an embedded resource by name.
    /// </summary>
    public byte[]? GetResource(string resourceName)
    {
        if (_cachedResources.TryGetValue(resourceName, out var cached))
            return cached;

        var fullName = _assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

        if (fullName == null) return null;

        using var stream = _assembly.GetManifestResourceStream(fullName);
        if (stream == null) return null;

        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer, 0, buffer.Length);
        _cachedResources[resourceName] = buffer;
        return buffer;
    }

    /// <summary>
    /// Lists all embedded resources.
    /// </summary>
    public IReadOnlyList<string> ListResources() =>
        _assembly.GetManifestResourceNames().ToList();

    /// <summary>
    /// Extracts a resource to a file.
    /// </summary>
    public async Task<bool> ExtractResourceAsync(string resourceName, string targetPath, CancellationToken ct = default)
    {
        var data = GetResource(resourceName);
        if (data == null) return false;

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(targetPath, data, ct);
        return true;
    }
}

#endregion

#region Configuration Embedder

/// <summary>
/// Embeds and extracts default configuration.
/// </summary>
public sealed class ConfigurationEmbedder
{
    private readonly EmbeddedResourceManager _resourceManager;
    private readonly string _configDirectory;

    public ConfigurationEmbedder(EmbeddedResourceManager resourceManager, string? configDirectory = null)
    {
        _resourceManager = resourceManager;
        _configDirectory = configDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataWarehouse", "config");
    }

    /// <summary>
    /// Extracts embedded default configuration if not present.
    /// </summary>
    public async Task<ConfigExtractionResult> EnsureConfigurationAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_configDirectory))
            Directory.CreateDirectory(_configDirectory);

        var extracted = new List<string>();
        var skipped = new List<string>();

        foreach (var resourceName in _resourceManager.ListResources().Where(r => r.EndsWith(".json") || r.EndsWith(".yaml")))
        {
            var fileName = Path.GetFileName(resourceName);
            var targetPath = Path.Combine(_configDirectory, fileName);

            if (File.Exists(targetPath))
            {
                skipped.Add(fileName);
                continue;
            }

            if (await _resourceManager.ExtractResourceAsync(resourceName, targetPath, ct))
                extracted.Add(fileName);
        }

        return new ConfigExtractionResult
        {
            ConfigDirectory = _configDirectory,
            ExtractedFiles = extracted,
            SkippedFiles = skipped
        };
    }

    /// <summary>
    /// Resets configuration to embedded defaults.
    /// </summary>
    public async Task<int> ResetToDefaultsAsync(CancellationToken ct = default)
    {
        int count = 0;
        foreach (var resourceName in _resourceManager.ListResources().Where(r => r.EndsWith(".json") || r.EndsWith(".yaml")))
        {
            var fileName = Path.GetFileName(resourceName);
            var targetPath = Path.Combine(_configDirectory, fileName);

            if (await _resourceManager.ExtractResourceAsync(resourceName, targetPath, ct))
                count++;
        }
        return count;
    }
}

#endregion

#region Plugin Extractor

/// <summary>
/// Extracts bundled plugins at runtime.
/// </summary>
public sealed class PluginExtractor
{
    private readonly EmbeddedResourceManager _resourceManager;
    private readonly string _pluginDirectory;
    private readonly ConcurrentDictionary<string, PluginInfo> _extractedPlugins = new();

    public PluginExtractor(EmbeddedResourceManager resourceManager, string? pluginDirectory = null)
    {
        _resourceManager = resourceManager;
        _pluginDirectory = pluginDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataWarehouse", "plugins");
    }

    /// <summary>
    /// Extracts all bundled plugins.
    /// </summary>
    public async Task<PluginExtractionResult> ExtractPluginsAsync(bool forceOverwrite = false, CancellationToken ct = default)
    {
        if (!Directory.Exists(_pluginDirectory))
            Directory.CreateDirectory(_pluginDirectory);

        var extracted = new List<PluginInfo>();
        var errors = new List<PluginExtractionError>();

        foreach (var resourceName in _resourceManager.ListResources().Where(r => r.EndsWith(".dll") || r.EndsWith(".zip")))
        {
            try
            {
                var fileName = Path.GetFileName(resourceName);
                var targetPath = resourceName.EndsWith(".zip")
                    ? Path.Combine(_pluginDirectory, Path.GetFileNameWithoutExtension(fileName))
                    : Path.Combine(_pluginDirectory, fileName);

                if (!forceOverwrite && (File.Exists(targetPath) || Directory.Exists(targetPath)))
                {
                    var existing = _extractedPlugins.GetValueOrDefault(fileName);
                    if (existing != null)
                    {
                        extracted.Add(existing);
                        continue;
                    }
                }

                var data = _resourceManager.GetResource(resourceName);
                if (data == null) continue;

                if (resourceName.EndsWith(".zip"))
                {
                    await ExtractZipAsync(data, targetPath, ct);
                }
                else
                {
                    await File.WriteAllBytesAsync(targetPath, data, ct);
                }

                var info = new PluginInfo
                {
                    Name = Path.GetFileNameWithoutExtension(fileName),
                    Path = targetPath,
                    ExtractedAt = DateTime.UtcNow,
                    Size = data.Length,
                    Hash = ComputeHash(data)
                };

                _extractedPlugins[fileName] = info;
                extracted.Add(info);
            }
            catch (Exception ex)
            {
                errors.Add(new PluginExtractionError { ResourceName = resourceName, Error = ex.Message });
            }
        }

        return new PluginExtractionResult
        {
            PluginDirectory = _pluginDirectory,
            ExtractedPlugins = extracted,
            Errors = errors
        };
    }

    /// <summary>
    /// Gets list of extracted plugins.
    /// </summary>
    public IReadOnlyList<PluginInfo> GetExtractedPlugins() => _extractedPlugins.Values.ToList();

    private async Task ExtractZipAsync(byte[] zipData, string targetDirectory, CancellationToken ct)
    {
        if (Directory.Exists(targetDirectory))
            Directory.Delete(targetDirectory, true);

        Directory.CreateDirectory(targetDirectory);

        using var stream = new MemoryStream(zipData);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var targetPath = Path.Combine(targetDirectory, entry.FullName);
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!string.IsNullOrEmpty(entry.Name))
            {
                entry.ExtractToFile(targetPath, true);
            }
        }
    }

    private string ComputeHash(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash);
    }
}

#endregion

#region Auto Update Manager

/// <summary>
/// Manages automatic updates with version checking.
/// </summary>
public sealed class AutoUpdateManager : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Timer _checkTimer;
    private readonly AutoUpdateConfig _config;
    private readonly string _currentVersion;
    private volatile bool _disposed;

    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;
    public event EventHandler<UpdateProgressEventArgs>? UpdateProgress;
    public event EventHandler<UpdateCompletedEventArgs>? UpdateCompleted;

    public AutoUpdateManager(AutoUpdateConfig? config = null, string? currentVersion = null)
    {
        _config = config ?? new AutoUpdateConfig();
        _currentVersion = currentVersion ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _checkTimer = new Timer(CheckForUpdates, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(_config.CheckIntervalHours));
    }

    /// <summary>
    /// Checks for available updates.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetStringAsync($"{_config.UpdateServerUrl}/version?current={_currentVersion}", ct);
            var versionInfo = System.Text.Json.JsonSerializer.Deserialize<VersionInfo>(response);

            if (versionInfo == null)
                return new UpdateCheckResult { Success = false, Error = "Invalid response" };

            var updateAvailable = CompareVersions(versionInfo.LatestVersion, _currentVersion) > 0;

            if (updateAvailable)
            {
                UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs
                {
                    CurrentVersion = _currentVersion,
                    NewVersion = versionInfo.LatestVersion,
                    ReleaseNotes = versionInfo.ReleaseNotes,
                    IsCritical = versionInfo.IsCritical
                });
            }

            return new UpdateCheckResult
            {
                Success = true,
                CurrentVersion = _currentVersion,
                LatestVersion = versionInfo.LatestVersion,
                UpdateAvailable = updateAvailable,
                DownloadUrl = versionInfo.DownloadUrl,
                ReleaseNotes = versionInfo.ReleaseNotes
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Downloads and applies an update.
    /// </summary>
    public async Task<UpdateResult> DownloadAndApplyAsync(string downloadUrl, CancellationToken ct = default)
    {
        try
        {
            UpdateProgress?.Invoke(this, new UpdateProgressEventArgs { Stage = "Downloading", Progress = 0 });

            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            var updatePath = Path.Combine(Path.GetTempPath(), $"datawarehouse_update_{Guid.NewGuid():N}.zip");

            await using var fileStream = new FileStream(updatePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var downloadStream = await response.Content.ReadAsStreamAsync(ct);

            var buffer = new byte[81920];
            var totalRead = 0L;
            int bytesRead;

            while ((bytesRead = await downloadStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    UpdateProgress?.Invoke(this, new UpdateProgressEventArgs
                    {
                        Stage = "Downloading",
                        Progress = (int)(totalRead * 100 / totalBytes)
                    });
                }
            }

            UpdateProgress?.Invoke(this, new UpdateProgressEventArgs { Stage = "Verifying", Progress = 100 });

            UpdateCompleted?.Invoke(this, new UpdateCompletedEventArgs
            {
                Success = true,
                UpdatePath = updatePath,
                RequiresRestart = true
            });

            return new UpdateResult { Success = true, UpdatePath = updatePath, RequiresRestart = true };
        }
        catch (Exception ex)
        {
            return new UpdateResult { Success = false, Error = ex.Message };
        }
    }

    private void CheckForUpdates(object? state)
    {
        if (_disposed || !_config.AutoCheck) return;
        _ = CheckForUpdatesAsync();
    }

    private int CompareVersions(string v1, string v2)
    {
        var parts1 = v1.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var parts2 = v2.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();

        for (int i = 0; i < Math.Max(parts1.Length, parts2.Length); i++)
        {
            var p1 = i < parts1.Length ? parts1[i] : 0;
            var p2 = i < parts2.Length ? parts2[i] : 0;
            if (p1 != p2) return p1.CompareTo(p2);
        }
        return 0;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _checkTimer.Dispose();
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}

#endregion

#region Types

public sealed class ConfigExtractionResult
{
    public string ConfigDirectory { get; init; } = string.Empty;
    public List<string> ExtractedFiles { get; init; } = new();
    public List<string> SkippedFiles { get; init; } = new();
}

public sealed class PluginInfo
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public DateTime ExtractedAt { get; init; }
    public long Size { get; init; }
    public string Hash { get; init; } = string.Empty;
}

public sealed class PluginExtractionResult
{
    public string PluginDirectory { get; init; } = string.Empty;
    public List<PluginInfo> ExtractedPlugins { get; init; } = new();
    public List<PluginExtractionError> Errors { get; init; } = new();
}

public sealed class PluginExtractionError
{
    public string ResourceName { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}

public sealed class AutoUpdateConfig
{
    public string UpdateServerUrl { get; set; } = "https://updates.datawarehouse.local";
    public int CheckIntervalHours { get; set; } = 24;
    public bool AutoCheck { get; set; } = true;
    public bool AutoDownload { get; set; } = false;
    public bool AutoInstall { get; set; } = false;
}

public sealed class VersionInfo
{
    public string LatestVersion { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string? ReleaseNotes { get; set; }
    public bool IsCritical { get; set; }
}

public sealed class UpdateCheckResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string LatestVersion { get; init; } = string.Empty;
    public bool UpdateAvailable { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ReleaseNotes { get; init; }
}

public sealed class UpdateResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? UpdatePath { get; init; }
    public bool RequiresRestart { get; init; }
}

public sealed class UpdateAvailableEventArgs : EventArgs
{
    public string CurrentVersion { get; init; } = string.Empty;
    public string NewVersion { get; init; } = string.Empty;
    public string? ReleaseNotes { get; init; }
    public bool IsCritical { get; init; }
}

public sealed class UpdateProgressEventArgs : EventArgs
{
    public string Stage { get; init; } = string.Empty;
    public int Progress { get; init; }
}

public sealed class UpdateCompletedEventArgs : EventArgs
{
    public bool Success { get; init; }
    public string? UpdatePath { get; init; }
    public bool RequiresRestart { get; init; }
}

#endregion
