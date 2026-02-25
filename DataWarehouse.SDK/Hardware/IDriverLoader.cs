using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Provides dynamic loading and unloading of storage drivers at runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drivers are loaded in isolated <see cref="System.Runtime.Loader.AssemblyLoadContext"/>s
    /// (via <see cref="DataWarehouse.SDK.Infrastructure.PluginAssemblyLoadContext"/>), allowing
    /// them to be unloaded and garbage collected without process restart.
    /// </para>
    /// <para>
    /// Hot-plug support: Combine with <see cref="IHardwareProbe.OnHardwareChanged"/> to
    /// automatically load drivers when new hardware is detected, or watch a directory to
    /// auto-load drivers when new DLLs are added.
    /// </para>
    /// <para>
    /// Thread-safe: All operations are serialized and safe for concurrent access.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Dynamic driver loading interface (HAL-04)")]
    public interface IDriverLoader : IDisposable
    {
        /// <summary>
        /// Scans a single assembly for storage drivers without loading them into the runtime.
        /// </summary>
        /// <param name="assemblyPath">The path to the assembly to scan.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A list of discovered driver metadata.</returns>
        /// <exception cref="System.IO.FileNotFoundException">Assembly file does not exist.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="assemblyPath"/> is null.</exception>
        Task<IReadOnlyList<DriverInfo>> ScanAsync(string assemblyPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Scans all assemblies in a directory for storage drivers without loading them.
        /// </summary>
        /// <param name="directoryPath">The directory to scan.</param>
        /// <param name="searchPattern">File search pattern (default: "*.dll").</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A list of discovered driver metadata from all scanned assemblies.</returns>
        /// <exception cref="System.IO.DirectoryNotFoundException">Directory does not exist.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="directoryPath"/> is null.</exception>
        /// <remarks>
        /// Assemblies that fail to load are logged and skipped. The operation continues
        /// even if individual assemblies cannot be scanned.
        /// </remarks>
        Task<IReadOnlyList<DriverInfo>> ScanDirectoryAsync(string directoryPath, string searchPattern = "*.dll", CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads a storage driver into the runtime in an isolated assembly context.
        /// </summary>
        /// <param name="assemblyPath">The path to the assembly containing the driver.</param>
        /// <param name="typeName">The fully qualified type name of the driver class.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>A handle to the loaded driver.</returns>
        /// <exception cref="InvalidOperationException">
        /// The type does not have a <see cref="StorageDriverAttribute"/>, or the maximum
        /// number of loaded drivers has been reached.
        /// </exception>
        /// <exception cref="System.IO.FileNotFoundException">Assembly file does not exist.</exception>
        /// <exception cref="TypeLoadException">The specified type cannot be found or loaded.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="assemblyPath"/> or <paramref name="typeName"/> is null.</exception>
        /// <remarks>
        /// The driver is loaded in a collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
        /// which enables it to be unloaded via <see cref="UnloadAsync"/>.
        /// </remarks>
        Task<DriverHandle> LoadAsync(string assemblyPath, string typeName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Unloads a previously loaded driver from the runtime.
        /// </summary>
        /// <param name="handle">The handle to the driver to unload.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The driver is not currently loaded.</exception>
        /// <remarks>
        /// Unloading releases the driver's <see cref="System.Runtime.Loader.AssemblyLoadContext"/>,
        /// allowing the garbage collector to eventually reclaim the driver assembly.
        /// </remarks>
        Task UnloadAsync(DriverHandle handle, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets all currently loaded drivers.
        /// </summary>
        /// <returns>A read-only list of loaded driver handles.</returns>
        IReadOnlyList<DriverHandle> GetLoadedDrivers();

        /// <summary>
        /// Finds a loaded driver by name.
        /// </summary>
        /// <param name="driverName">The driver name (from <see cref="StorageDriverAttribute.Name"/>).</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>The driver handle if found, otherwise null.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="driverName"/> is null.</exception>
        Task<DriverHandle?> GetDriverAsync(string driverName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Fired when a driver is successfully loaded.
        /// </summary>
        event EventHandler<DriverEventArgs>? OnDriverLoaded;

        /// <summary>
        /// Fired when a driver is successfully unloaded.
        /// </summary>
        event EventHandler<DriverEventArgs>? OnDriverUnloaded;
    }

    /// <summary>
    /// Metadata about a discovered storage driver.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Driver discovery metadata (HAL-04)")]
    public sealed record DriverInfo
    {
        /// <summary>
        /// The path to the assembly containing the driver.
        /// </summary>
        public required string AssemblyPath { get; init; }

        /// <summary>
        /// The fully qualified type name of the driver class.
        /// </summary>
        public required string TypeName { get; init; }

        /// <summary>
        /// The driver name from <see cref="StorageDriverAttribute.Name"/>.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// The driver description from <see cref="StorageDriverAttribute.Description"/>.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// The driver version from <see cref="StorageDriverAttribute.Version"/>.
        /// </summary>
        public string? Version { get; init; }

        /// <summary>
        /// The hardware device types this driver supports.
        /// </summary>
        public HardwareDeviceType SupportedDevices { get; init; }

        /// <summary>
        /// Whether the driver should be auto-loaded when supported hardware is detected.
        /// </summary>
        public bool AutoLoad { get; init; }
    }

    /// <summary>
    /// Handle to a loaded storage driver.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Driver lifecycle management (HAL-04)")]
    public sealed class DriverHandle
    {
        /// <summary>
        /// Unique identifier for this driver handle.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Metadata about the driver.
        /// </summary>
        public DriverInfo Info { get; }

        /// <summary>
        /// The loaded driver type.
        /// </summary>
        public Type DriverType { get; }

        /// <summary>
        /// Whether the driver is currently loaded.
        /// </summary>
        public bool IsLoaded { get; internal set; }

        /// <summary>
        /// Internal constructor. Only <see cref="IDriverLoader"/> implementations should create handles.
        /// </summary>
        internal DriverHandle(string id, DriverInfo info, Type driverType)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Info = info ?? throw new ArgumentNullException(nameof(info));
            DriverType = driverType ?? throw new ArgumentNullException(nameof(driverType));
            IsLoaded = true;
        }
    }

    /// <summary>
    /// Event arguments for driver lifecycle events.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Driver lifecycle events (HAL-04)")]
    public sealed record DriverEventArgs
    {
        /// <summary>
        /// The driver handle associated with the event.
        /// </summary>
        public required DriverHandle Handle { get; init; }

        /// <summary>
        /// The type of event that occurred.
        /// </summary>
        public required DriverEventType EventType { get; init; }
    }

    /// <summary>
    /// Types of driver lifecycle events.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Driver lifecycle event types (HAL-04)")]
    public enum DriverEventType
    {
        /// <summary>Driver was successfully loaded.</summary>
        Loaded,

        /// <summary>Driver was successfully unloaded.</summary>
        Unloaded,

        /// <summary>Driver load operation failed.</summary>
        LoadFailed,

        /// <summary>Driver unload operation failed.</summary>
        UnloadFailed
    }
}
