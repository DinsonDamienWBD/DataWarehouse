using DataWarehouse.Kernel.Configuration;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Kernel
{
    /// <summary>
    /// Fluent builder for creating and configuring DataWarehouse Kernel instances.
    /// </summary>
    public class KernelBuilder
    {
        private readonly KernelConfiguration _config = new();
        private ILogger<DataWarehouseKernel>? _logger;
        private readonly List<IPlugin> _plugins = new();
        private readonly List<Action<DataWarehouseKernel>> _postBuildActions = new();

        /// <summary>
        /// Create a new KernelBuilder.
        /// </summary>
        public static KernelBuilder Create() => new();

        /// <summary>
        /// Set the kernel ID.
        /// </summary>
        public KernelBuilder WithKernelId(string kernelId)
        {
            _config.KernelId = kernelId;
            return this;
        }

        /// <summary>
        /// Set the operating mode.
        /// </summary>
        public KernelBuilder WithOperatingMode(OperatingMode mode)
        {
            _config.OperatingMode = mode;
            return this;
        }

        /// <summary>
        /// Set the root path for data storage.
        /// </summary>
        public KernelBuilder WithRootPath(string path)
        {
            _config.RootPath = path;
            return this;
        }

        /// <summary>
        /// Add a path to load plugins from.
        /// </summary>
        public KernelBuilder WithPluginPath(string path)
        {
            _config.PluginPaths.Add(path);
            return this;
        }

        /// <summary>
        /// Add multiple plugin paths.
        /// </summary>
        public KernelBuilder WithPluginPaths(params string[] paths)
        {
            _config.PluginPaths.AddRange(paths);
            return this;
        }

        /// <summary>
        /// Configure the default pipeline.
        /// </summary>
        public KernelBuilder WithPipelineConfiguration(PipelineConfiguration config)
        {
            _config.PipelineConfiguration = config;
            return this;
        }

        /// <summary>
        /// Configure the default pipeline: Compress then Encrypt.
        /// </summary>
        public KernelBuilder WithDefaultPipeline()
        {
            _config.PipelineConfiguration = PipelineConfiguration.CreateDefault();
            return this;
        }

        /// <summary>
        /// Configure a custom pipeline order.
        /// </summary>
        public KernelBuilder WithPipelineOrder(params string[] stageTypes)
        {
            var stages = new List<PipelineStageConfig>();
            int order = 100;
            foreach (var stageType in stageTypes)
            {
                stages.Add(new PipelineStageConfig
                {
                    StageType = stageType,
                    Order = order,
                    Enabled = true
                });
                order += 100;
            }

            _config.PipelineConfiguration = new PipelineConfiguration
            {
                Name = "Custom",
                WriteStages = stages
            };
            return this;
        }

        /// <summary>
        /// Use the built-in in-memory storage by default.
        /// </summary>
        public KernelBuilder UseInMemoryStorage()
        {
            _config.UseInMemoryStorageByDefault = true;
            return this;
        }

        /// <summary>
        /// Auto-start feature plugins on load.
        /// </summary>
        public KernelBuilder AutoStartFeatures(bool autoStart = true)
        {
            _config.AutoStartFeatures = autoStart;
            return this;
        }

        /// <summary>
        /// Add a logger.
        /// </summary>
        public KernelBuilder WithLogger(ILogger<DataWarehouseKernel> logger)
        {
            _logger = logger;
            return this;
        }

        /// <summary>
        /// Add a logger factory.
        /// </summary>
        public KernelBuilder WithLoggerFactory(ILoggerFactory factory)
        {
            _logger = factory.CreateLogger<DataWarehouseKernel>();
            return this;
        }

        /// <summary>
        /// Register a plugin to be loaded on build.
        /// </summary>
        public KernelBuilder WithPlugin(IPlugin plugin)
        {
            _plugins.Add(plugin);
            return this;
        }

        /// <summary>
        /// Register multiple plugins.
        /// </summary>
        public KernelBuilder WithPlugins(params IPlugin[] plugins)
        {
            _plugins.AddRange(plugins);
            return this;
        }

        /// <summary>
        /// Set the primary storage provider.
        /// </summary>
        public KernelBuilder WithPrimaryStorage(IStorageProvider storage)
        {
            _postBuildActions.Add(kernel => kernel.SetPrimaryStorage(storage));
            return this;
        }

        /// <summary>
        /// Set the cache storage provider.
        /// </summary>
        public KernelBuilder WithCacheStorage(IStorageProvider storage)
        {
            _postBuildActions.Add(kernel => kernel.SetCacheStorage(storage));
            return this;
        }

        /// <summary>
        /// Add a post-build configuration action.
        /// </summary>
        public KernelBuilder Configure(Action<DataWarehouseKernel> configAction)
        {
            _postBuildActions.Add(configAction);
            return this;
        }

        /// <summary>
        /// Build the kernel (does not initialize).
        /// Call InitializeAsync() after building.
        /// </summary>
        public DataWarehouseKernel Build()
        {
            var kernel = new DataWarehouseKernel(_config, _logger);

            // Apply post-build actions
            foreach (var action in _postBuildActions)
            {
                action(kernel);
            }

            return kernel;
        }

        /// <summary>
        /// Build and initialize the kernel.
        /// </summary>
        public async Task<DataWarehouseKernel> BuildAndInitializeAsync(CancellationToken ct = default)
        {
            var kernel = Build();
            await kernel.InitializeAsync(ct);

            // Register additional plugins
            foreach (var plugin in _plugins)
            {
                await kernel.RegisterPluginAsync(plugin, ct);
            }

            return kernel;
        }
    }
}
