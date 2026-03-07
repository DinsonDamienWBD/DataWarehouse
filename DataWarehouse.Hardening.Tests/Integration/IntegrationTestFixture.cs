using DataWarehouse.Kernel;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DataWarehouse.Hardening.Tests.Integration;

/// <summary>
/// Shared integration test fixture that boots the DataWarehouse Kernel with all 52 plugins.
/// Uses reflection-based plugin discovery to find and instantiate every IPlugin implementation
/// across all referenced plugin assemblies, then registers them via the KernelBuilder.
///
/// The fixture manages the full Kernel lifecycle:
///   1. Discover all plugin types from loaded assemblies
///   2. Build the Kernel with KernelBuilder (in-memory storage, unsigned assembly mode)
///   3. Register all discovered plugin instances
///   4. Expose Kernel, MessageBus, PluginRegistry, and temp directory for tests
///   5. Gracefully dispose on teardown
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    /// <summary>Expected number of plugins across the entire DataWarehouse ecosystem.</summary>
    public const int ExpectedPluginCount = 52;

    /// <summary>Prefix used to identify DataWarehouse plugin assemblies.</summary>
    private const string PluginAssemblyPrefix = "DataWarehouse.Plugins.";

    private DataWarehouseKernel? _kernel;
    private string? _tempDirectory;
    private readonly List<string> _pluginLoadErrors = new();
    private readonly List<string> _discoveredPluginTypes = new();

    /// <summary>The booted DataWarehouse Kernel instance.</summary>
    public DataWarehouseKernel Kernel => _kernel ?? throw new InvalidOperationException("Fixture not initialized");

    /// <summary>The Kernel's message bus for inter-plugin communication.</summary>
    public IMessageBus MessageBus => Kernel.MessageBus;

    /// <summary>The plugin registry containing all loaded plugins.</summary>
    public PluginRegistry PluginRegistry => Kernel.Plugins;

    /// <summary>Temporary directory for VDE volumes and test artifacts.</summary>
    public string TempDirectory => _tempDirectory ?? throw new InvalidOperationException("Fixture not initialized");

    /// <summary>Any errors encountered during plugin loading.</summary>
    public IReadOnlyList<string> PluginLoadErrors => _pluginLoadErrors;

    /// <summary>All discovered plugin type names (for diagnostics).</summary>
    public IReadOnlyList<string> DiscoveredPluginTypes => _discoveredPluginTypes;

    /// <summary>Number of successfully registered plugins.</summary>
    public int RegisteredPluginCount => PluginRegistry.Count;

    /// <summary>
    /// Initializes the Kernel: creates temp directory, discovers plugins via reflection,
    /// builds the Kernel with KernelBuilder, and registers all plugin instances.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Step 1: Create temporary directory for VDE volumes and test data
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"dw-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        // Step 2: Discover all plugin types via reflection
        var pluginInstances = DiscoverAndInstantiatePlugins();

        // Step 3: Build the Kernel via KernelBuilder
        //   - In-memory storage (no filesystem dependency for base tests)
        //   - Plugin signature verification disabled (test assemblies are unsigned)
        //   - Temp directory as root path
        var builder = KernelBuilder.Create()
            .WithKernelId("integration-test")
            .WithOperatingMode(OperatingMode.Workstation)
            .WithRootPath(_tempDirectory)
            .UseInMemoryStorage();

        // Register discovered plugins
        foreach (var plugin in pluginInstances)
        {
            builder.WithPlugin(plugin);
        }

        // Step 4: Build and initialize
        _kernel = builder.Build();

        // Initialize with a timeout to detect deadlocks during boot
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await _kernel.InitializeAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _pluginLoadErrors.Add("DEADLOCK: Kernel initialization timed out after 5 minutes");
            throw new TimeoutException("Kernel initialization deadlocked — timed out after 5 minutes");
        }
    }

    /// <summary>
    /// Discovers all concrete IPlugin implementations across all loaded assemblies
    /// that match the DataWarehouse.Plugins.* naming convention.
    /// </summary>
    private List<IPlugin> DiscoverAndInstantiatePlugins()
    {
        var plugins = new List<IPlugin>();

        // Force-load all plugin assemblies that are referenced but not yet loaded
        var referencedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetReferencedAssemblies(); }
                catch { return Array.Empty<AssemblyName>(); }
            })
            .Where(name => name.FullName.StartsWith(PluginAssemblyPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(new AssemblyNameComparer())
            .ToList();

        foreach (var asmName in referencedAssemblies)
        {
            try
            {
                if (AppDomain.CurrentDomain.GetAssemblies().All(a => a.GetName().Name != asmName.Name))
                {
                    Assembly.Load(asmName);
                }
            }
            catch (Exception ex)
            {
                _pluginLoadErrors.Add($"Failed to load assembly {asmName.Name}: {ex.Message}");
            }
        }

        // Find all concrete IPlugin implementations
        var pluginTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith(PluginAssemblyPrefix, StringComparison.OrdinalIgnoreCase) == true
                     || a.GetName().Name == "DataWarehouse.Kernel")
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes().Where(t =>
                        !t.IsAbstract &&
                        !t.IsInterface &&
                        typeof(IPlugin).IsAssignableFrom(t));
                }
                catch (ReflectionTypeLoadException ex)
                {
                    _pluginLoadErrors.Add($"ReflectionTypeLoadException in {a.GetName().Name}: {string.Join("; ", ex.LoaderExceptions?.Select(e => e?.Message) ?? Array.Empty<string?>())}");
                    return ex.Types?.Where(t => t != null && !t.IsAbstract && typeof(IPlugin).IsAssignableFrom(t)).Cast<Type>() ?? Enumerable.Empty<Type>();
                }
            })
            .ToList();

        foreach (var type in pluginTypes)
        {
            _discoveredPluginTypes.Add(type.FullName ?? type.Name);

            try
            {
                // Try parameterless constructor first
                var ctor = type.GetConstructor(Type.EmptyTypes);
                if (ctor != null)
                {
                    var plugin = (IPlugin)ctor.Invoke(null);
                    plugins.Add(plugin);
                }
                else
                {
                    // Skip types without parameterless constructors — these require DI
                    _pluginLoadErrors.Add($"No parameterless constructor for {type.FullName} — skipped (requires DI)");
                }
            }
            catch (Exception ex)
            {
                _pluginLoadErrors.Add($"Failed to instantiate {type.FullName}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        return plugins;
    }

    /// <summary>
    /// Gracefully shuts down the Kernel and cleans up temporary directory.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_kernel != null)
        {
            try
            {
                await _kernel.DisposeAsync();
            }
            catch (Exception ex)
            {
                _pluginLoadErrors.Add($"Kernel disposal error: {ex.Message}");
            }
            _kernel = null;
        }

        if (_tempDirectory != null && Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — temp directory will be garbage collected by OS
            }
        }
    }

    /// <summary>Comparer for AssemblyName based on Name property.</summary>
    private sealed class AssemblyNameComparer : IEqualityComparer<AssemblyName>
    {
        public bool Equals(AssemblyName? x, AssemblyName? y) =>
            string.Equals(x?.Name, y?.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(AssemblyName obj) =>
            obj.Name?.ToUpperInvariant().GetHashCode() ?? 0;
    }
}
