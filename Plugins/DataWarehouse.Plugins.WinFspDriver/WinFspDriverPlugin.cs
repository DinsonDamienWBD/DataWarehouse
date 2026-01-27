// <copyright file="WinFspDriverPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Windows native filesystem driver plugin using WinFSP (Windows File System Proxy).
/// Mounts DataWarehouse storage as a local drive letter (e.g., D:\DataWarehouse\).
/// </summary>
/// <remarks>
/// <para>
/// This plugin provides a user-mode filesystem implementation that bridges Windows filesystem
/// operations to DataWarehouse storage backends (S3, Local, Azure Blob, etc.). It uses the
/// WinFSP library for efficient, NTFS-compatible filesystem emulation.
/// </para>
/// <para>
/// Features include:
/// <list type="bullet">
/// <item>Full Windows filesystem semantics (ACLs, security descriptors, alternate data streams)</item>
/// <item>Read/write caching with adaptive prefetch</item>
/// <item>Volume Shadow Copy (VSS) integration for backups</item>
/// <item>Windows Shell integration (overlay icons, context menus, property sheets)</item>
/// <item>Thread-safe concurrent file operations</item>
/// <item>Atomic rename with proper Windows semantics</item>
/// </list>
/// </para>
/// <para>
/// Message Commands:
/// <list type="bullet">
/// <item>winfsp.mount - Mount the filesystem at specified drive letter or path</item>
/// <item>winfsp.unmount - Unmount the filesystem</item>
/// <item>winfsp.status - Get current mount status and statistics</item>
/// <item>winfsp.cache.stats - Get cache statistics</item>
/// <item>winfsp.cache.clear - Clear all caches</item>
/// <item>winfsp.vss.snapshot - Create a VSS snapshot</item>
/// <item>winfsp.vss.list - List available snapshots</item>
/// <item>winfsp.shell.register - Register shell extensions</item>
/// </list>
/// </para>
/// </remarks>
public sealed class WinFspDriverPlugin : FeaturePluginBase, IDisposable
{
    private readonly WinFspConfig _config;
    private readonly ConcurrentDictionary<string, WinFspMountedInstance> _mountedInstances;
    private readonly SemaphoreSlim _mountLock;
    private IStorageProvider? _storageProvider;
    private IKernelContext? _kernelContext;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Gets the unique identifier for this plugin.
    /// </summary>
    public override string Id => "com.datawarehouse.filesystem.winfsp";

    /// <summary>
    /// Gets the display name of this plugin.
    /// </summary>
    public override string Name => "WinFSP Filesystem Driver";

    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <summary>
    /// Gets the plugin category.
    /// </summary>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>
    /// Gets whether the plugin is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the list of currently mounted instances.
    /// </summary>
    public IReadOnlyCollection<string> MountedPaths => _mountedInstances.Keys.ToList().AsReadOnly();

    /// <summary>
    /// Initializes a new instance of the WinFSP driver plugin with default configuration.
    /// </summary>
    public WinFspDriverPlugin() : this(WinFspConfig.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the WinFSP driver plugin with specified configuration.
    /// </summary>
    /// <param name="config">Configuration options for the driver.</param>
    public WinFspDriverPlugin(WinFspConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _mountedInstances = new ConcurrentDictionary<string, WinFspMountedInstance>(StringComparer.OrdinalIgnoreCase);
        _mountLock = new SemaphoreSlim(1, 1);
    }

    #region Plugin Lifecycle

    /// <summary>
    /// Handles the plugin handshake during initialization.
    /// </summary>
    /// <param name="request">The handshake request containing configuration.</param>
    /// <returns>The handshake response indicating success or failure.</returns>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        // Verify we're running on Windows
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new HandshakeResponse
            {
                PluginId = Id,
                Name = Name,
                Version = ParseSemanticVersion(Version),
                Category = Category,
                Success = false,
                ReadyState = PluginReadyState.Failed,
                Capabilities = GetCapabilities(),
                Metadata = new Dictionary<string, object>
                {
                    ["Error"] = "WinFSP driver is only supported on Windows"
                }
            };
        }

        // Check if WinFSP is installed
        var winfspInstalled = await CheckWinFspInstalledAsync();

        return new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = winfspInstalled,
            ReadyState = winfspInstalled ? PluginReadyState.Ready : PluginReadyState.Failed,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        };
    }

    /// <summary>
    /// Starts the plugin and optionally auto-mounts the filesystem.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning)
            return;

        _isRunning = true;

        // Auto-start if configured
        if (_config.AutoStart)
        {
            try
            {
                await MountAsync(_config.DriveLetter ?? _config.MountPoint, ct);
            }
            catch (Exception)
            {
                // Log but don't fail startup
            }
        }
    }

    /// <summary>
    /// Stops the plugin and unmounts all filesystems.
    /// </summary>
    public override async Task StopAsync()
    {
        if (!_isRunning)
            return;

        // Unmount all instances
        foreach (var mountPoint in _mountedInstances.Keys.ToList())
        {
            try
            {
                await UnmountAsync(mountPoint);
            }
            catch
            {
                // Ignore unmount errors during shutdown
            }
        }

        _isRunning = false;
    }

    #endregion

    #region Message Handling

    /// <summary>
    /// Handles incoming messages for this plugin.
    /// </summary>
    /// <param name="message">The incoming message.</param>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        var response = message.Type switch
        {
            "winfsp.mount" => await HandleMountAsync(message),
            "winfsp.unmount" => await HandleUnmountAsync(message),
            "winfsp.status" => HandleStatus(message),
            "winfsp.cache.stats" => HandleCacheStats(message),
            "winfsp.cache.clear" => HandleCacheClear(message),
            "winfsp.vss.snapshot" => await HandleVssSnapshotAsync(message),
            "winfsp.vss.list" => HandleVssList(message),
            "winfsp.shell.register" => await HandleShellRegisterAsync(message),
            _ => null
        };

        if (response != null && message.Payload is Dictionary<string, object> payload)
        {
            payload["_response"] = response;
        }
    }

    private async Task<MessageResponse> HandleMountAsync(PluginMessage message)
    {
        if (message.Payload is not Dictionary<string, object> payload)
            return MessageResponse.Error("Invalid payload");

        var mountPoint = payload.TryGetValue("mountPoint", out var mp) ? mp?.ToString() : null;
        mountPoint ??= payload.TryGetValue("driveLetter", out var dl) ? dl?.ToString() : null;

        if (string.IsNullOrEmpty(mountPoint))
            return MessageResponse.Error("Mount point or drive letter required");

        try
        {
            var result = await MountAsync(mountPoint);
            return MessageResponse.Ok(new
            {
                MountPoint = result,
                Success = true
            });
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Mount failed: {ex.Message}");
        }
    }

    private async Task<MessageResponse> HandleUnmountAsync(PluginMessage message)
    {
        if (message.Payload is not Dictionary<string, object> payload)
            return MessageResponse.Error("Invalid payload");

        var mountPoint = payload.TryGetValue("mountPoint", out var mp) ? mp?.ToString() : null;

        if (string.IsNullOrEmpty(mountPoint))
        {
            // Unmount all
            foreach (var point in _mountedInstances.Keys.ToList())
            {
                await UnmountAsync(point);
            }
            return MessageResponse.Ok(new { Message = "All filesystems unmounted" });
        }

        try
        {
            await UnmountAsync(mountPoint);
            return MessageResponse.Ok(new { MountPoint = mountPoint, Success = true });
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Unmount failed: {ex.Message}");
        }
    }

    private MessageResponse HandleStatus(PluginMessage message)
    {
        var instances = _mountedInstances.Select(kv => new
        {
            MountPoint = kv.Key,
            IsMounted = kv.Value.FileSystem?.IsMounted ?? false,
            CacheStats = kv.Value.CacheManager?.GetStatistics()
        }).ToList();

        return MessageResponse.Ok(new
        {
            IsRunning = _isRunning,
            MountedCount = instances.Count,
            Instances = instances
        });
    }

    private MessageResponse HandleCacheStats(PluginMessage message)
    {
        var allStats = _mountedInstances.Select(kv => new
        {
            MountPoint = kv.Key,
            Stats = kv.Value.CacheManager?.GetStatistics()
        }).ToList();

        return MessageResponse.Ok(new { CacheStatistics = allStats });
    }

    private MessageResponse HandleCacheClear(PluginMessage message)
    {
        foreach (var instance in _mountedInstances.Values)
        {
            instance.CacheManager?.ClearReadCache();
        }

        return MessageResponse.Ok(new { Message = "All caches cleared" });
    }

    private async Task<MessageResponse> HandleVssSnapshotAsync(PluginMessage message)
    {
        if (message.Payload is not Dictionary<string, object> payload)
            return MessageResponse.Error("Invalid payload");

        var mountPoint = payload.TryGetValue("mountPoint", out var mp) ? mp?.ToString() : null;
        var description = payload.TryGetValue("description", out var desc) ? desc?.ToString() : null;

        if (string.IsNullOrEmpty(mountPoint) || !_mountedInstances.TryGetValue(mountPoint, out var instance))
        {
            return MessageResponse.Error("Mount point not found");
        }

        if (instance.VssProvider == null)
        {
            return MessageResponse.Error("VSS not enabled for this mount");
        }

        try
        {
            var snapshot = await instance.VssProvider.CreateShadowCopyAsync(description);
            return MessageResponse.Ok(new
            {
                SnapshotId = snapshot.Id,
                Created = snapshot.CreatedAt,
                Description = snapshot.Description
            });
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Snapshot failed: {ex.Message}");
        }
    }

    private MessageResponse HandleVssList(PluginMessage message)
    {
        if (message.Payload is not Dictionary<string, object> payload)
            return MessageResponse.Error("Invalid payload");

        var mountPoint = payload.TryGetValue("mountPoint", out var mp) ? mp?.ToString() : null;

        if (string.IsNullOrEmpty(mountPoint) || !_mountedInstances.TryGetValue(mountPoint, out var instance))
        {
            return MessageResponse.Error("Mount point not found");
        }

        var shadowCopies = instance.VssProvider?.ShadowCopies;
        var snapshotList = new List<object>();
        if (shadowCopies != null)
        {
            foreach (var s in shadowCopies)
            {
                snapshotList.Add(new
                {
                    s.Id,
                    s.CreatedAt,
                    s.Description,
                    s.FileCount,
                    s.TotalBytes
                });
            }
        }

        return MessageResponse.Ok(new { Snapshots = snapshotList });
    }

    private async Task<MessageResponse> HandleShellRegisterAsync(PluginMessage message)
    {
        if (message.Payload is not Dictionary<string, object> payload)
            return MessageResponse.Error("Invalid payload");

        var mountPoint = payload.TryGetValue("mountPoint", out var mp) ? mp?.ToString() : null;

        if (string.IsNullOrEmpty(mountPoint) || !_mountedInstances.TryGetValue(mountPoint, out var instance))
        {
            return MessageResponse.Error("Mount point not found");
        }

        try
        {
            var success = instance.ShellExtension?.RegisterAll() ?? false;
            return MessageResponse.Ok(new { Success = success });
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Shell registration failed: {ex.Message}");
        }
    }

    #endregion

    #region Mount/Unmount Operations

    /// <summary>
    /// Mounts the filesystem at the specified path.
    /// </summary>
    /// <param name="mountPoint">Drive letter (e.g., "D") or mount point path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The actual mount point used.</returns>
    public async Task<string> MountAsync(string? mountPoint = null, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("WinFSP is only supported on Windows.");

        await _mountLock.WaitAsync(ct);
        try
        {
            // Determine mount configuration - clone the config to avoid modifying original
            var config = CloneConfig(_config);
            if (!string.IsNullOrEmpty(mountPoint))
            {
                if (mountPoint.Length == 1 && char.IsLetter(mountPoint[0]))
                {
                    config.DriveLetter = mountPoint;
                    config.MountPoint = null;
                }
                else if (mountPoint.Length == 2 && mountPoint[1] == ':')
                {
                    config.DriveLetter = mountPoint[0].ToString();
                    config.MountPoint = null;
                }
                else
                {
                    config.DriveLetter = null;
                    config.MountPoint = mountPoint;
                }
            }

            // Check if already mounted
            var effectiveMountPoint = GetEffectiveMountPoint(config);
            if (_mountedInstances.ContainsKey(effectiveMountPoint))
            {
                return effectiveMountPoint;
            }

            // Create components
            var fileSystem = new WinFspFileSystem(config, _storageProvider);
            var cacheManager = new WinFspCacheManager(config.ReadCache, config.WriteCache);
            var securityHandler = new WinFspSecurityHandler(config.Security);
            var operations = new WinFspOperations(fileSystem, cacheManager, securityHandler, config);
            var vssProvider = config.Vss.Enabled ? new VssProvider(config.Vss, fileSystem) : null;
            var shellExtension = new ShellExtension(config.Shell, fileSystem);

            // Wire up cache flush events
            cacheManager.OnWriteBufferFlushed += (sender, args) =>
            {
                foreach (var chunk in args.Chunks)
                {
                    fileSystem.WriteData(args.Path, chunk.Offset, chunk.Data);
                }
            };

            // Mount the filesystem
            if (!fileSystem.Mount())
            {
                throw new InvalidOperationException("Failed to mount filesystem.");
            }

            var instance = new WinFspMountedInstance
            {
                Config = config,
                FileSystem = fileSystem,
                CacheManager = cacheManager,
                SecurityHandler = securityHandler,
                Operations = operations,
                VssProvider = vssProvider,
                ShellExtension = shellExtension,
                MountedAt = DateTime.UtcNow
            };

            var actualMountPoint = fileSystem.MountPoint ?? effectiveMountPoint;
            _mountedInstances[actualMountPoint] = instance;

            // Register VSS provider if enabled
            vssProvider?.Register();

            // Register shell extensions if configured
            if (config.Shell.OverlayIcons || config.Shell.ContextMenus || config.Shell.PropertySheets)
            {
                try
                {
                    shellExtension.RegisterAll();
                }
                catch
                {
                    // Shell registration may fail without admin rights - not critical
                }
            }

            return actualMountPoint;
        }
        finally
        {
            _mountLock.Release();
        }
    }

    /// <summary>
    /// Unmounts the filesystem at the specified path.
    /// </summary>
    /// <param name="mountPoint">The mount point to unmount.</param>
    public async Task UnmountAsync(string mountPoint)
    {
        if (!_mountedInstances.TryRemove(mountPoint, out var instance))
        {
            // Try with/without colon
            var altKey = mountPoint.EndsWith(":") ? mountPoint.TrimEnd(':') : mountPoint + ":";
            if (!_mountedInstances.TryRemove(altKey, out instance))
            {
                throw new InvalidOperationException($"Mount point {mountPoint} not found.");
            }
        }

        try
        {
            // Flush all caches
            if (instance.CacheManager != null)
            {
                await instance.CacheManager.FlushAllWriteBuffersAsync();
            }

            // Unregister shell extensions
            instance.ShellExtension?.UnregisterAll();

            // Unregister VSS provider
            instance.VssProvider?.Unregister();

            // Unmount filesystem
            instance.FileSystem?.Unmount();
        }
        finally
        {
            // Dispose all components
            instance.Operations?.Dispose();
            instance.CacheManager?.Dispose();
            instance.SecurityHandler?.Dispose();
            instance.VssProvider?.Dispose();
            instance.ShellExtension?.Dispose();
            instance.FileSystem?.Dispose();
        }
    }

    /// <summary>
    /// Gets information about a mounted instance.
    /// </summary>
    /// <param name="mountPoint">The mount point to query.</param>
    /// <returns>Instance information, or null if not mounted.</returns>
    public WinFspMountedInstanceInfo? GetMountedInstance(string mountPoint)
    {
        if (!_mountedInstances.TryGetValue(mountPoint, out var instance))
            return null;

        return new WinFspMountedInstanceInfo
        {
            MountPoint = mountPoint,
            IsMounted = instance.FileSystem?.IsMounted ?? false,
            MountedAt = instance.MountedAt,
            VssEnabled = instance.VssProvider?.IsEnabled ?? false,
            CacheStatistics = instance.CacheManager?.GetStatistics()
        };
    }

    #endregion

    #region Storage Provider Integration

    /// <summary>
    /// Sets the storage provider to use for backend storage.
    /// </summary>
    /// <param name="provider">The storage provider.</param>
    public void SetStorageProvider(IStorageProvider provider)
    {
        _storageProvider = provider;
    }

    /// <summary>
    /// Sets the kernel context for accessing services.
    /// </summary>
    /// <param name="context">The kernel context.</param>
    public void SetKernelContext(IKernelContext context)
    {
        _kernelContext = context;
    }

    #endregion

    #region Private Methods

    private static string GetEffectiveMountPoint(WinFspConfig config)
    {
        if (!string.IsNullOrEmpty(config.MountPoint))
            return config.MountPoint;

        if (!string.IsNullOrEmpty(config.DriveLetter))
            return $"{config.DriveLetter}:";

        return "Z:"; // Default
    }

    private static WinFspConfig CloneConfig(WinFspConfig source)
    {
        return new WinFspConfig
        {
            DriveLetter = source.DriveLetter,
            MountPoint = source.MountPoint,
            VolumeLabel = source.VolumeLabel,
            FileSystemName = source.FileSystemName,
            MaxComponentLength = source.MaxComponentLength,
            SectorSize = source.SectorSize,
            SectorsPerAllocationUnit = source.SectorsPerAllocationUnit,
            TotalSize = source.TotalSize,
            FreeSize = source.FreeSize,
            CaseSensitive = source.CaseSensitive,
            CasePreserved = source.CasePreserved,
            UnicodeOnDisk = source.UnicodeOnDisk,
            PersistentAcls = source.PersistentAcls,
            SupportExtendedAttributes = source.SupportExtendedAttributes,
            SupportReparsePoints = source.SupportReparsePoints,
            SupportHardLinks = source.SupportHardLinks,
            SupportNamedStreams = source.SupportNamedStreams,
            SupportSparseFiles = source.SupportSparseFiles,
            ReadCache = source.ReadCache,
            WriteCache = source.WriteCache,
            Security = source.Security,
            Shell = source.Shell,
            Vss = source.Vss,
            Debug = source.Debug,
            StorageProviderId = source.StorageProviderId,
            StorageConnectionString = source.StorageConnectionString,
            MaxConcurrentOperations = source.MaxConcurrentOperations,
            OperationTimeoutMs = source.OperationTimeoutMs,
            AutoStart = source.AutoStart,
            CreateMountPoint = source.CreateMountPoint
        };
    }

    private static async Task<bool> CheckWinFspInstalledAsync()
    {
        // Check if WinFSP DLL is available
        var winfspPath = Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\WinFsp\bin\winfsp-x64.dll");
        if (File.Exists(winfspPath))
            return true;

        winfspPath = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\WinFsp\bin\winfsp-x64.dll");
        if (File.Exists(winfspPath))
            return true;

        // Check system PATH
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';'))
        {
            var dllPath = Path.Combine(dir, "winfsp-x64.dll");
            if (File.Exists(dllPath))
                return true;
        }

        await Task.CompletedTask;
        return false;
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
                Name = "winfsp.mount",
                DisplayName = "Mount Filesystem",
                Description = "Mount DataWarehouse storage as a Windows drive"
            },
            new()
            {
                Name = "winfsp.unmount",
                DisplayName = "Unmount Filesystem",
                Description = "Unmount a previously mounted drive"
            },
            new()
            {
                Name = "winfsp.status",
                DisplayName = "Get Status",
                Description = "Get current mount status and statistics"
            },
            new()
            {
                Name = "winfsp.vss.snapshot",
                DisplayName = "Create Snapshot",
                Description = "Create a VSS snapshot of the filesystem"
            },
            new()
            {
                Name = "winfsp.shell.register",
                DisplayName = "Register Shell Extensions",
                Description = "Register Windows Explorer integrations"
            }
        };
    }

    /// <summary>
    /// Gets the plugin metadata.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Description"] = "Windows native filesystem driver using WinFSP for mounting DataWarehouse storage as drive letters.";
        metadata["Platform"] = "Windows";
        metadata["RequiresWinFsp"] = true;
        metadata["SupportsVSS"] = true;
        metadata["SupportsShellIntegration"] = true;
        metadata["SupportsCaching"] = true;
        metadata["SupportsACLs"] = true;
        metadata["MountedInstances"] = _mountedInstances.Count;
        metadata["IsRunning"] = _isRunning;
        return metadata;
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the plugin and all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        // Stop the plugin
        StopAsync().Wait(TimeSpan.FromSeconds(30));

        _mountLock.Dispose();
        _disposed = true;
    }

    #endregion
}

/// <summary>
/// Represents a mounted WinFSP filesystem instance.
/// </summary>
internal sealed class WinFspMountedInstance
{
    /// <summary>
    /// Configuration used for this mount.
    /// </summary>
    public required WinFspConfig Config { get; init; }

    /// <summary>
    /// The WinFSP filesystem instance.
    /// </summary>
    public WinFspFileSystem? FileSystem { get; init; }

    /// <summary>
    /// The cache manager.
    /// </summary>
    public WinFspCacheManager? CacheManager { get; init; }

    /// <summary>
    /// The security handler.
    /// </summary>
    public WinFspSecurityHandler? SecurityHandler { get; init; }

    /// <summary>
    /// The operations handler.
    /// </summary>
    public WinFspOperations? Operations { get; init; }

    /// <summary>
    /// The VSS provider.
    /// </summary>
    public VssProvider? VssProvider { get; init; }

    /// <summary>
    /// The shell extension handler.
    /// </summary>
    public ShellExtension? ShellExtension { get; init; }

    /// <summary>
    /// When the filesystem was mounted.
    /// </summary>
    public DateTime MountedAt { get; init; }
}

/// <summary>
/// Information about a mounted WinFSP instance.
/// </summary>
public sealed class WinFspMountedInstanceInfo
{
    /// <summary>
    /// The mount point (drive letter or path).
    /// </summary>
    public required string MountPoint { get; init; }

    /// <summary>
    /// Whether the filesystem is currently mounted.
    /// </summary>
    public bool IsMounted { get; init; }

    /// <summary>
    /// When the filesystem was mounted.
    /// </summary>
    public DateTime MountedAt { get; init; }

    /// <summary>
    /// Whether VSS is enabled for this mount.
    /// </summary>
    public bool VssEnabled { get; init; }

    /// <summary>
    /// Cache statistics for this mount.
    /// </summary>
    public CacheStatistics? CacheStatistics { get; init; }
}
