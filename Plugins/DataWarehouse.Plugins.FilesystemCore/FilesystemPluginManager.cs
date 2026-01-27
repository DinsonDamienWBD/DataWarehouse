// <copyright file="FilesystemPluginManager.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using DataWarehouse.Plugins.FilesystemCore.Plugins;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.FilesystemCore;

/// <summary>
/// Manages filesystem plugins and provides auto-detection of filesystem types.
/// </summary>
/// <remarks>
/// This manager automatically selects the appropriate filesystem plugin based on
/// the underlying filesystem type. Plugins are registered with priorities, and
/// the highest priority plugin that can handle a path is selected.
/// </remarks>
public sealed class FilesystemPluginManager : IAsyncDisposable
{
    private readonly List<IFilesystemPlugin> _plugins = new();
    private readonly ConcurrentDictionary<string, IFilesystemPlugin> _pathPluginCache = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _isInitialized;

    /// <summary>
    /// Gets the singleton instance of the filesystem plugin manager.
    /// </summary>
    public static FilesystemPluginManager Instance { get; } = new();

    /// <summary>
    /// Gets all registered filesystem plugins.
    /// </summary>
    public IReadOnlyList<IFilesystemPlugin> Plugins => _plugins.AsReadOnly();

    /// <summary>
    /// Initializes the manager with default plugins for the current platform.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            if (_isInitialized)
                return;

            // Register platform-appropriate plugins
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                RegisterPlugin(new NtfsFilesystemPlugin());
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                RegisterPlugin(new Ext4FilesystemPlugin());
                RegisterPlugin(new BtrfsFilesystemPlugin());
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                RegisterPlugin(new ApfsFilesystemPlugin());
            }

            // Start all plugins
            foreach (var plugin in _plugins)
            {
                if (plugin is FilesystemPluginBase featurePlugin)
                {
                    await featurePlugin.StartAsync(ct);
                }
            }

            _isInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Registers a filesystem plugin.
    /// </summary>
    /// <param name="plugin">The plugin to register.</param>
    public void RegisterPlugin(IFilesystemPlugin plugin)
    {
        if (plugin == null)
            throw new ArgumentNullException(nameof(plugin));

        // Insert sorted by priority (highest first)
        var index = _plugins.FindIndex(p => p.DetectionPriority < plugin.DetectionPriority);
        if (index >= 0)
            _plugins.Insert(index, plugin);
        else
            _plugins.Add(plugin);
    }

    /// <summary>
    /// Unregisters a filesystem plugin.
    /// </summary>
    /// <param name="pluginId">The ID of the plugin to unregister.</param>
    /// <returns>True if the plugin was removed, false if not found.</returns>
    public bool UnregisterPlugin(string pluginId)
    {
        var plugin = _plugins.FirstOrDefault(p => p.Id == pluginId);
        if (plugin != null)
        {
            _plugins.Remove(plugin);

            // Clear cache entries using this plugin
            var keysToRemove = _pathPluginCache
                .Where(kvp => kvp.Value.Id == pluginId)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _pathPluginCache.TryRemove(key, out _);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the appropriate filesystem plugin for the given path.
    /// </summary>
    /// <param name="path">The path to find a plugin for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The filesystem plugin that can handle the path, or null if none found.</returns>
    public async Task<IFilesystemPlugin?> GetPluginForPathAsync(string path, CancellationToken ct = default)
    {
        if (!_isInitialized)
            await InitializeAsync(ct);

        var normalizedPath = Path.GetFullPath(path);
        var rootPath = GetRootPath(normalizedPath);

        // Check cache first
        if (_pathPluginCache.TryGetValue(rootPath, out var cachedPlugin))
            return cachedPlugin;

        // Find the best plugin
        foreach (var plugin in _plugins)
        {
            if (await plugin.CanHandleAsync(normalizedPath, ct))
            {
                _pathPluginCache[rootPath] = plugin;
                return plugin;
            }
        }

        return null;
    }

    /// <summary>
    /// Mounts a filesystem at the specified path using auto-detected plugin.
    /// </summary>
    /// <param name="path">The path to mount.</param>
    /// <param name="options">Mount options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A filesystem handle for the mounted path.</returns>
    /// <exception cref="NotSupportedException">Thrown if no plugin can handle the path.</exception>
    public async Task<IFilesystemHandle> MountAsync(string path, MountOptions? options = null, CancellationToken ct = default)
    {
        var plugin = await GetPluginForPathAsync(path, ct)
            ?? throw new NotSupportedException($"No filesystem plugin can handle path: {path}");

        return await plugin.MountAsync(path, options, ct);
    }

    /// <summary>
    /// Gets filesystem information for the specified path.
    /// </summary>
    /// <param name="path">The path to get information for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Filesystem information.</returns>
    public async Task<FilesystemInfo> GetFilesystemInfoAsync(string path, CancellationToken ct = default)
    {
        var plugin = await GetPluginForPathAsync(path, ct)
            ?? throw new NotSupportedException($"No filesystem plugin can handle path: {path}");

        return await plugin.GetFilesystemInfoAsync(path, ct);
    }

    /// <summary>
    /// Gets filesystem statistics for the specified path.
    /// </summary>
    /// <param name="path">The path to get statistics for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Filesystem statistics.</returns>
    public async Task<FilesystemStats> GetStatsAsync(string path, CancellationToken ct = default)
    {
        var plugin = await GetPluginForPathAsync(path, ct)
            ?? throw new NotSupportedException($"No filesystem plugin can handle path: {path}");

        return await plugin.GetStatsAsync(path, ct);
    }

    /// <summary>
    /// Detects capabilities for the filesystem at the specified path.
    /// </summary>
    /// <param name="path">The path to detect capabilities for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detected filesystem capabilities.</returns>
    public async Task<FilesystemCapabilities> DetectCapabilitiesAsync(string path, CancellationToken ct = default)
    {
        var plugin = await GetPluginForPathAsync(path, ct)
            ?? throw new NotSupportedException($"No filesystem plugin can handle path: {path}");

        return await plugin.DetectCapabilitiesAsync(path, ct);
    }

    /// <summary>
    /// Checks if a specific feature is supported on the filesystem.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="feature">The feature to check for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the feature is supported.</returns>
    public async Task<bool> SupportsFeatureAsync(string path, FilesystemFeature feature, CancellationToken ct = default)
    {
        var plugin = await GetPluginForPathAsync(path, ct);
        if (plugin == null)
            return false;

        return await plugin.SupportsFeatureAsync(path, feature, ct);
    }

    /// <summary>
    /// Checks health of the filesystem at the specified path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Filesystem health status.</returns>
    public async Task<FilesystemHealth> CheckHealthAsync(string path, CancellationToken ct = default)
    {
        var plugin = await GetPluginForPathAsync(path, ct)
            ?? throw new NotSupportedException($"No filesystem plugin can handle path: {path}");

        return await plugin.CheckHealthAsync(path, ct);
    }

    /// <summary>
    /// Gets the filesystem type name for the specified path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The filesystem type name, or "unknown" if not detected.</returns>
    public async Task<string> GetFilesystemTypeAsync(string path, CancellationToken ct = default)
    {
        var plugin = await GetPluginForPathAsync(path, ct);
        return plugin?.FilesystemType ?? "unknown";
    }

    /// <summary>
    /// Clears the plugin cache.
    /// </summary>
    public void ClearCache()
    {
        _pathPluginCache.Clear();
    }

    /// <summary>
    /// Gets the root path for caching purposes.
    /// </summary>
    private static string GetRootPath(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, cache by drive letter
            var root = Path.GetPathRoot(path);
            return root ?? path;
        }
        else
        {
            // On Unix, cache by mount point - simplified version
            // A full implementation would parse /proc/mounts or use statfs
            var root = Path.GetPathRoot(path);
            return root ?? "/";
        }
    }

    /// <summary>
    /// Disposes the manager and all plugins.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var plugin in _plugins)
        {
            if (plugin is FilesystemPluginBase featurePlugin)
            {
                await featurePlugin.StopAsync();
            }
        }

        _plugins.Clear();
        _pathPluginCache.Clear();
        _isInitialized = false;
    }
}

/// <summary>
/// Extension methods for filesystem operations.
/// </summary>
public static class FilesystemExtensions
{
    /// <summary>
    /// Gets a quick summary of filesystem capabilities.
    /// </summary>
    /// <param name="capabilities">The capabilities to summarize.</param>
    /// <returns>A list of capability names.</returns>
    public static IEnumerable<string> ToSummary(this FilesystemCapabilities capabilities)
    {
        if ((capabilities & FilesystemCapabilities.HardLinks) != 0)
            yield return "Hard Links";
        if ((capabilities & FilesystemCapabilities.SymbolicLinks) != 0)
            yield return "Symbolic Links";
        if ((capabilities & FilesystemCapabilities.ExtendedAttributes) != 0)
            yield return "Extended Attributes";
        if ((capabilities & FilesystemCapabilities.ACLs) != 0)
            yield return "ACLs";
        if ((capabilities & FilesystemCapabilities.Encryption) != 0)
            yield return "Encryption";
        if ((capabilities & FilesystemCapabilities.Compression) != 0)
            yield return "Compression";
        if ((capabilities & FilesystemCapabilities.Deduplication) != 0)
            yield return "Deduplication";
        if ((capabilities & FilesystemCapabilities.Snapshots) != 0)
            yield return "Snapshots";
        if ((capabilities & FilesystemCapabilities.Quotas) != 0)
            yield return "Quotas";
        if ((capabilities & FilesystemCapabilities.Journaling) != 0)
            yield return "Journaling";
        if ((capabilities & FilesystemCapabilities.CopyOnWrite) != 0)
            yield return "Copy-on-Write";
        if ((capabilities & FilesystemCapabilities.Checksums) != 0)
            yield return "Checksums";
        if ((capabilities & FilesystemCapabilities.Reflinks) != 0)
            yield return "Reflinks";
    }

    /// <summary>
    /// Checks if the capabilities include all specified flags.
    /// </summary>
    /// <param name="capabilities">The capabilities to check.</param>
    /// <param name="required">The required flags.</param>
    /// <returns>True if all required flags are present.</returns>
    public static bool HasAll(this FilesystemCapabilities capabilities, FilesystemCapabilities required)
    {
        return (capabilities & required) == required;
    }

    /// <summary>
    /// Checks if the capabilities include any of the specified flags.
    /// </summary>
    /// <param name="capabilities">The capabilities to check.</param>
    /// <param name="any">The flags to check for.</param>
    /// <returns>True if any of the flags are present.</returns>
    public static bool HasAny(this FilesystemCapabilities capabilities, FilesystemCapabilities any)
    {
        return (capabilities & any) != 0;
    }
}
