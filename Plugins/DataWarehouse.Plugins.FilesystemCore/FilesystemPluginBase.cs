// <copyright file="FilesystemPluginBase.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.FilesystemCore;

/// <summary>
/// Abstract base class for filesystem plugins providing common functionality
/// for mount/unmount lifecycle, capability detection, and file/directory operations.
/// </summary>
/// <remarks>
/// Derived classes should implement filesystem-specific detection and operations.
/// This base class provides:
/// <list type="bullet">
/// <item>Common plugin infrastructure (handshake, messaging)</item>
/// <item>Mount/unmount lifecycle management</item>
/// <item>Thread-safe handle tracking</item>
/// <item>OS detection utilities</item>
/// <item>Default capability detection based on OS</item>
/// </list>
/// </remarks>
public abstract class FilesystemPluginBase : FeaturePluginBase, IFilesystemPlugin
{
    private readonly ConcurrentDictionary<string, IFilesystemHandle> _mountedHandles = new();
    private readonly SemaphoreSlim _mountLock = new(1, 1);
    private volatile bool _isRunning;

    /// <summary>
    /// Gets the unique identifier for this plugin.
    /// </summary>
    public abstract override string Id { get; }

    /// <summary>
    /// Gets the display name of this plugin.
    /// </summary>
    public abstract override string Name { get; }

    /// <summary>
    /// Gets the plugin category.
    /// </summary>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>
    /// Gets the capabilities this plugin can support.
    /// </summary>
    public abstract FilesystemCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the filesystem type name.
    /// </summary>
    public abstract string FilesystemType { get; }

    /// <summary>
    /// Gets the detection priority. Higher values are checked first.
    /// Default is 100.
    /// </summary>
    public virtual int DetectionPriority => 100;

    /// <summary>
    /// Gets whether the plugin is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the current operating system platform.
    /// </summary>
    protected static OSPlatform CurrentPlatform
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return OSPlatform.Windows;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return OSPlatform.Linux;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return OSPlatform.OSX;
            return OSPlatform.FreeBSD; // Fallback
        }
    }

    /// <summary>
    /// Gets whether we're running on Windows.
    /// </summary>
    protected static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Gets whether we're running on Linux.
    /// </summary>
    protected static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Gets whether we're running on macOS.
    /// </summary>
    protected static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    #region IPlugin Implementation

    /// <summary>
    /// Handles the plugin handshake during initialization.
    /// </summary>
    /// <param name="request">The handshake request containing configuration.</param>
    /// <returns>The handshake response indicating success or failure.</returns>
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        return Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    /// <summary>
    /// Handles messages sent to this plugin.
    /// </summary>
    /// <param name="message">The incoming message.</param>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
            return;

        var response = message.Type switch
        {
            "filesystem.mount" => await HandleMountMessageAsync(message.Payload),
            "filesystem.unmount" => await HandleUnmountMessageAsync(message.Payload),
            "filesystem.stats" => await HandleStatsMessageAsync(message.Payload),
            "filesystem.capabilities" => await HandleCapabilitiesMessageAsync(message.Payload),
            "filesystem.health" => await HandleHealthMessageAsync(message.Payload),
            "filesystem.info" => await HandleInfoMessageAsync(message.Payload),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }
    }

    /// <summary>
    /// Starts the filesystem plugin.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public override Task StartAsync(CancellationToken ct)
    {
        _isRunning = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the filesystem plugin and unmounts all handles.
    /// </summary>
    public override async Task StopAsync()
    {
        _isRunning = false;

        // Unmount all handles
        foreach (var kvp in _mountedHandles)
        {
            try
            {
                await kvp.Value.DisposeAsync();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        _mountedHandles.Clear();
    }

    #endregion

    #region IFilesystemPlugin Implementation

    /// <summary>
    /// Checks if this plugin can handle the specified path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if this plugin can handle the path.</returns>
    public abstract Task<bool> CanHandleAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Mounts a filesystem at the specified path.
    /// </summary>
    /// <param name="path">The path to mount.</param>
    /// <param name="options">Mount options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A filesystem handle for operations.</returns>
    public async Task<IFilesystemHandle> MountAsync(string path, MountOptions? options = null, CancellationToken ct = default)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Plugin is not running.");

        var normalizedPath = NormalizePath(path);
        options ??= new MountOptions();

        await _mountLock.WaitAsync(ct);
        try
        {
            // Check if already mounted
            if (_mountedHandles.TryGetValue(normalizedPath, out var existingHandle))
            {
                if (existingHandle.IsValid)
                    return existingHandle;

                // Handle is invalid, remove it
                _mountedHandles.TryRemove(normalizedPath, out _);
            }

            // Create new handle
            var handle = await CreateHandleAsync(normalizedPath, options, ct);
            _mountedHandles[normalizedPath] = handle;

            return handle;
        }
        finally
        {
            _mountLock.Release();
        }
    }

    /// <summary>
    /// Gets filesystem statistics.
    /// </summary>
    /// <param name="path">The path to get statistics for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Filesystem statistics.</returns>
    public abstract Task<FilesystemStats> GetStatsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Checks if a feature is supported.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="feature">The feature to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the feature is supported.</returns>
    public virtual async Task<bool> SupportsFeatureAsync(string path, FilesystemFeature feature, CancellationToken ct = default)
    {
        var capabilities = await DetectCapabilitiesAsync(path, ct);
        var flag = FeatureToCapability(feature);
        return (capabilities & flag) != 0;
    }

    /// <summary>
    /// Detects capabilities on the filesystem.
    /// </summary>
    /// <param name="path">The path to detect capabilities for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detected capabilities.</returns>
    public abstract Task<FilesystemCapabilities> DetectCapabilitiesAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Checks filesystem health.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health status.</returns>
    public virtual Task<FilesystemHealth> CheckHealthAsync(string path, CancellationToken ct = default)
    {
        // Default implementation - derived classes should override for better health checks
        return Task.FromResult(new FilesystemHealth
        {
            Status = FilesystemHealthStatus.Healthy,
            CheckedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Gets detailed filesystem information.
    /// </summary>
    /// <param name="path">The path to get information for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Filesystem information.</returns>
    public virtual async Task<FilesystemInfo> GetFilesystemInfoAsync(string path, CancellationToken ct = default)
    {
        var stats = await GetStatsAsync(path, ct);
        var capabilities = await DetectCapabilitiesAsync(path, ct);

        return new FilesystemInfo
        {
            Type = FilesystemType,
            MountPoint = path,
            Capabilities = capabilities,
            Stats = stats,
            IsReadOnly = stats.IsReadOnly,
            IsNetwork = stats.IsNetwork,
            IsRemovable = stats.IsRemovable,
            VolumeLabel = stats.VolumeLabel,
            VolumeId = stats.VolumeSerialNumber,
            OperatingSystem = RuntimeInformation.OSDescription
        };
    }

    #endregion

    #region Protected Methods for Derived Classes

    /// <summary>
    /// Creates a filesystem handle. Must be implemented by derived classes.
    /// </summary>
    /// <param name="path">The path to mount.</param>
    /// <param name="options">Mount options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new filesystem handle.</returns>
    protected abstract Task<IFilesystemHandle> CreateHandleAsync(string path, MountOptions options, CancellationToken ct);

    /// <summary>
    /// Normalizes a filesystem path for the current platform.
    /// </summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>The normalized path.</returns>
    protected virtual string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty.", nameof(path));

        // Get full path
        var fullPath = Path.GetFullPath(path);

        // Normalize separators for current platform
        if (IsWindows)
        {
            fullPath = fullPath.Replace('/', '\\');
        }
        else
        {
            fullPath = fullPath.Replace('\\', '/');
        }

        // Remove trailing separator unless it's root
        if (fullPath.Length > 1 && fullPath.EndsWith(Path.DirectorySeparatorChar))
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar);
        }

        return fullPath;
    }

    /// <summary>
    /// Converts a FilesystemFeature to the corresponding capability flag.
    /// </summary>
    /// <param name="feature">The feature to convert.</param>
    /// <returns>The corresponding capability flag.</returns>
    protected static FilesystemCapabilities FeatureToCapability(FilesystemFeature feature)
    {
        return feature switch
        {
            FilesystemFeature.HardLinks => FilesystemCapabilities.HardLinks,
            FilesystemFeature.SymbolicLinks => FilesystemCapabilities.SymbolicLinks,
            FilesystemFeature.ExtendedAttributes => FilesystemCapabilities.ExtendedAttributes,
            FilesystemFeature.ACLs => FilesystemCapabilities.ACLs,
            FilesystemFeature.Encryption => FilesystemCapabilities.Encryption,
            FilesystemFeature.Compression => FilesystemCapabilities.Compression,
            FilesystemFeature.Deduplication => FilesystemCapabilities.Deduplication,
            FilesystemFeature.Snapshots => FilesystemCapabilities.Snapshots,
            FilesystemFeature.Quotas => FilesystemCapabilities.Quotas,
            FilesystemFeature.Journaling => FilesystemCapabilities.Journaling,
            FilesystemFeature.CopyOnWrite => FilesystemCapabilities.CopyOnWrite,
            FilesystemFeature.Checksums => FilesystemCapabilities.Checksums,
            FilesystemFeature.SparseFiles => FilesystemCapabilities.SparseFiles,
            FilesystemFeature.AlternateDataStreams => FilesystemCapabilities.AlternateDataStreams,
            FilesystemFeature.CaseSensitive => FilesystemCapabilities.CaseSensitive,
            FilesystemFeature.Unicode => FilesystemCapabilities.Unicode,
            FilesystemFeature.LargeFiles => FilesystemCapabilities.LargeFiles,
            FilesystemFeature.Reflinks => FilesystemCapabilities.Reflinks,
            FilesystemFeature.ChangeNotifications => FilesystemCapabilities.ChangeNotifications,
            FilesystemFeature.MandatoryLocking => FilesystemCapabilities.MandatoryLocking,
            FilesystemFeature.AdvisoryLocking => FilesystemCapabilities.AdvisoryLocking,
            FilesystemFeature.AtomicRename => FilesystemCapabilities.AtomicRename,
            FilesystemFeature.MemoryMappedFiles => FilesystemCapabilities.MemoryMappedFiles,
            FilesystemFeature.AsyncIO => FilesystemCapabilities.AsyncIO,
            FilesystemFeature.DirectIO => FilesystemCapabilities.DirectIO,
            _ => FilesystemCapabilities.None
        };
    }

    /// <summary>
    /// Gets the plugin capabilities.
    /// </summary>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "filesystem.mount",
                DisplayName = "Mount Filesystem",
                Description = $"Mount a {FilesystemType} filesystem for access"
            },
            new()
            {
                Name = "filesystem.stats",
                DisplayName = "Get Statistics",
                Description = "Get filesystem space and inode statistics"
            },
            new()
            {
                Name = "filesystem.capabilities",
                DisplayName = "Detect Capabilities",
                Description = "Detect supported filesystem features"
            },
            new()
            {
                Name = "filesystem.health",
                DisplayName = "Health Check",
                Description = "Check filesystem health status"
            }
        };
    }

    /// <summary>
    /// Gets the plugin metadata.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FilesystemType"] = FilesystemType;
        metadata["DetectionPriority"] = DetectionPriority;
        metadata["Capabilities"] = Capabilities.ToString();
        metadata["Platform"] = RuntimeInformation.OSDescription;
        metadata["ActiveMounts"] = _mountedHandles.Count;
        return metadata;
    }

    #endregion

    #region Message Handlers

    private async Task<Dictionary<string, object>> HandleMountMessageAsync(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Path not specified"
            };
        }

        try
        {
            var options = new MountOptions();
            if (payload.TryGetValue("readOnly", out var ro) && ro is bool readOnly)
                options.ReadOnly = readOnly;

            var handle = await MountAsync(path, options);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["mountPath"] = handle.MountPath,
                ["capabilities"] = handle.Capabilities.ToString()
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private async Task<Dictionary<string, object>> HandleUnmountMessageAsync(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Path not specified"
            };
        }

        var normalizedPath = NormalizePath(path);

        if (_mountedHandles.TryRemove(normalizedPath, out var handle))
        {
            await handle.DisposeAsync();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["message"] = $"Unmounted {normalizedPath}"
            };
        }

        return new Dictionary<string, object>
        {
            ["success"] = false,
            ["error"] = $"Path {normalizedPath} is not mounted"
        };
    }

    private async Task<Dictionary<string, object>> HandleStatsMessageAsync(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Path not specified"
            };
        }

        try
        {
            var stats = await GetStatsAsync(path);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["totalBytes"] = stats.TotalSizeBytes,
                ["usedBytes"] = stats.UsedBytes,
                ["availableBytes"] = stats.AvailableBytes,
                ["usagePercent"] = stats.UsagePercent,
                ["filesystemType"] = stats.FilesystemType
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private async Task<Dictionary<string, object>> HandleCapabilitiesMessageAsync(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Path not specified"
            };
        }

        try
        {
            var capabilities = await DetectCapabilitiesAsync(path);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["capabilities"] = capabilities.ToString(),
                ["flags"] = (int)capabilities
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private async Task<Dictionary<string, object>> HandleHealthMessageAsync(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Path not specified"
            };
        }

        try
        {
            var health = await CheckHealthAsync(path);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["status"] = health.Status.ToString(),
                ["isHealthy"] = health.IsHealthy,
                ["issueCount"] = health.Issues.Count
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private async Task<Dictionary<string, object>> HandleInfoMessageAsync(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Path not specified"
            };
        }

        try
        {
            var info = await GetFilesystemInfoAsync(path);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["type"] = info.Type,
                ["volumeLabel"] = info.VolumeLabel ?? "",
                ["volumeId"] = info.VolumeId ?? "",
                ["capabilities"] = info.Capabilities.ToString(),
                ["isReadOnly"] = info.IsReadOnly,
                ["isNetwork"] = info.IsNetwork,
                ["isRemovable"] = info.IsRemovable
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    #endregion
}
