using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Governance;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract base class for all plugins. Provides default implementations
    /// of common plugin functionality. Plugins should inherit from this instead
    /// of implementing IPlugin directly.
    /// AI-native: Includes built-in support for AI-driven operations.
    /// </summary>
    public abstract class PluginBase : IPlugin
    {
        /// <summary>
        /// Knowledge cache for performance optimization.
        /// Maps knowledge topic to cached KnowledgeObject.
        /// </summary>
        private readonly ConcurrentDictionary<string, KnowledgeObject> _knowledgeCache = new();

        /// <summary>
        /// Message bus reference for knowledge communication.
        /// Set during initialization via InitializeAsync.
        /// </summary>
        protected IMessageBus? MessageBus { get; private set; }

        /// <summary>
        /// Unique Plugin ID - must be set by derived classes.
        /// Use a stable identifier like "com.company.plugin.name" for consistency.
        /// </summary>
        public abstract string Id { get; }

        /// <summary>
        /// Gets the category of the plugin.
        /// </summary>
        public abstract PluginCategory Category { get; }

        /// <summary>
        /// Human-readable Name - must be set by derived classes.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Semantic Version - default implementation returns "1.0.0".
        /// Supports full semantic versioning (1.0.0-beta, 2.0.0-rc.1+build.123).
        /// </summary>
        public virtual string Version => "1.0.0";

        /// <summary>
        /// Parses a semantic version string into a Version object.
        /// Handles formats like: 1.0.0, v1.0.0, 1.0.0-beta, 1.0.0-rc.1+build.123
        /// </summary>
        protected static Version ParseSemanticVersion(string versionString)
        {
            if (string.IsNullOrWhiteSpace(versionString))
            {
                return new Version(1, 0, 0);
            }

            var version = versionString.Trim();

            // Strip leading 'v' or 'V'
            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                version = version.Substring(1);
            }

            // Strip prerelease and build metadata (everything after - or +)
            var dashIndex = version.IndexOf('-');
            var plusIndex = version.IndexOf('+');
            var stripIndex = -1;

            if (dashIndex >= 0 && plusIndex >= 0)
            {
                stripIndex = Math.Min(dashIndex, plusIndex);
            }
            else if (dashIndex >= 0)
            {
                stripIndex = dashIndex;
            }
            else if (plusIndex >= 0)
            {
                stripIndex = plusIndex;
            }

            if (stripIndex >= 0)
            {
                version = version.Substring(0, stripIndex);
            }

            // Try to parse as a valid version
            if (System.Version.TryParse(version, out var parsed))
            {
                return parsed;
            }

            // If parsing failed, try to extract numeric parts
            var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var numericParts = new List<int>();

            foreach (var part in parts)
            {
                // Extract leading digits from each part
                var numericChars = new string(part.TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(numericChars, out var num))
                {
                    numericParts.Add(num);
                }
                else
                {
                    numericParts.Add(0);
                }
            }

            // Ensure we have at least major.minor
            while (numericParts.Count < 2)
            {
                numericParts.Add(0);
            }

            return numericParts.Count switch
            {
                2 => new Version(numericParts[0], numericParts[1]),
                3 => new Version(numericParts[0], numericParts[1], numericParts[2]),
                _ => new Version(numericParts[0], numericParts[1], numericParts[2], numericParts[3])
            };
        }

        /// <summary>
        /// Default handshake implementation. Override to provide custom initialization.
        /// </summary>
        public virtual Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
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
                Dependencies = GetDependencies(),
                Metadata = GetMetadata()
            });
        }

        /// <summary>
        /// Default message handler. Override to handle custom messages.
        /// </summary>
        public virtual Task OnMessageAsync(PluginMessage message)
        {
            // Default: log and ignore
            return Task.CompletedTask;
        }

        /// <summary>
        /// Override to provide plugin capabilities for AI agents.
        /// </summary>
        protected virtual List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>();
        }

        /// <summary>
        /// Override to declare plugin dependencies.
        /// </summary>
        protected virtual List<PluginDependency> GetDependencies()
        {
            return new List<PluginDependency>();
        }

        /// <summary>
        /// Override to provide additional metadata for AI agents.
        /// </summary>
        protected virtual Dictionary<string, object> GetMetadata()
        {
            return new Dictionary<string, object>
            {
                ["Description"] = $"{Name} plugin for DataWarehouse",
                ["AIFriendly"] = true,
                ["SupportsStreaming"] = false
            };
        }

        #region Knowledge Integration (Task 99.D)

        /// <summary>
        /// Gets the registration knowledge object for this plugin.
        /// Override to provide plugin-specific knowledge for registration with Universal Intelligence.
        /// </summary>
        /// <returns>Knowledge object describing plugin capabilities, or null if not applicable.</returns>
        /// <remarks>
        /// This method is called during plugin initialization to register the plugin's knowledge
        /// with the Universal Intelligence system (T90). The default implementation returns null.
        /// Plugins should override this to expose their capabilities for AI-driven operations.
        /// </remarks>
        public virtual KnowledgeObject? GetRegistrationKnowledge()
        {
            // Default: No knowledge to register
            // Plugins override this to provide their specific knowledge
            return null;
        }

        /// <summary>
        /// Handles incoming knowledge requests from the Universal Intelligence system.
        /// Override to respond to knowledge queries about plugin capabilities.
        /// </summary>
        /// <param name="knowledge">Knowledge request object.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        /// <remarks>
        /// This method is called when the Universal Intelligence system queries plugin knowledge.
        /// The default implementation logs the request and returns immediately.
        /// Plugins should override this to provide dynamic knowledge responses.
        /// </remarks>
        public virtual Task HandleKnowledgeAsync(KnowledgeObject knowledge, CancellationToken ct = default)
        {
            // Default: No-op
            // Plugins override this to handle specific knowledge requests
            return Task.CompletedTask;
        }

        /// <summary>
        /// Auto-registers plugin knowledge with Universal Intelligence during initialization.
        /// Called automatically by InitializeAsync if MessageBus is available.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        /// <remarks>
        /// This method checks if the plugin has knowledge to register via GetRegistrationKnowledge(),
        /// and if so, publishes it to the "intelligence.knowledge.register" topic.
        /// Gracefully handles the case where Universal Intelligence is not available.
        /// </remarks>
        protected virtual async Task RegisterKnowledgeAsync(CancellationToken ct = default)
        {
            if (MessageBus == null)
            {
                // MessageBus not available - skip registration
                return;
            }

            var knowledge = GetRegistrationKnowledge();
            if (knowledge == null)
            {
                // No knowledge to register
                return;
            }

            try
            {
                // Publish knowledge registration to Universal Intelligence
                var message = new PluginMessage
                {
                    Type = "intelligence.knowledge.register",
                    Payload = new Dictionary<string, object>
                    {
                        ["Knowledge"] = knowledge,
                        ["PluginId"] = Id,
                        ["PluginName"] = Name,
                        ["Timestamp"] = DateTimeOffset.UtcNow
                    }
                };

                await MessageBus.PublishAsync("intelligence.knowledge.register", message, ct);

                // Cache the registered knowledge
                _knowledgeCache[knowledge.Topic] = knowledge;
            }
            catch (Exception)
            {
                // Graceful degradation: Universal Intelligence not available
                // This is not a fatal error - plugin can still function without knowledge registration
            }
        }

        /// <summary>
        /// Subscribes to knowledge request messages from Universal Intelligence.
        /// Called automatically by InitializeAsync if MessageBus is available.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Subscription token for cleanup, or null if subscription failed.</returns>
        /// <remarks>
        /// This method subscribes to the "intelligence.knowledge.request" topic to handle
        /// incoming knowledge queries. The subscription is stored for cleanup during shutdown.
        /// </remarks>
        protected virtual IDisposable? SubscribeToKnowledgeRequests(CancellationToken ct = default)
        {
            if (MessageBus == null)
            {
                return null;
            }

            try
            {
                // Subscribe to knowledge requests
                return MessageBus.Subscribe($"intelligence.knowledge.request.{Id}", async (message) =>
                {
                    if (message.Payload.TryGetValue("Knowledge", out var knowledgeObj) && knowledgeObj is KnowledgeObject knowledge)
                    {
                        await HandleKnowledgeAsync(knowledge, ct);
                    }
                });
            }
            catch (Exception)
            {
                // Graceful degradation: Unable to subscribe
                return null;
            }
        }

        /// <summary>
        /// Sets the message bus reference for knowledge communication.
        /// Should be called during plugin initialization.
        /// </summary>
        /// <param name="messageBus">Message bus instance.</param>
        /// <remarks>
        /// This method is typically called by the plugin host/kernel during initialization.
        /// Once set, the message bus is used for knowledge registration and communication.
        /// </remarks>
        protected virtual void SetMessageBus(IMessageBus? messageBus)
        {
            MessageBus = messageBus;
        }

        /// <summary>
        /// Gets cached knowledge object by topic.
        /// </summary>
        /// <param name="topic">Knowledge topic.</param>
        /// <returns>Cached knowledge object, or null if not found.</returns>
        /// <remarks>
        /// This method provides fast access to previously registered or received knowledge objects.
        /// The cache is maintained in-memory for performance.
        /// </remarks>
        protected KnowledgeObject? GetCachedKnowledge(string topic)
        {
            _knowledgeCache.TryGetValue(topic, out var knowledge);
            return knowledge;
        }

        /// <summary>
        /// Caches a knowledge object for later retrieval.
        /// </summary>
        /// <param name="topic">Knowledge topic.</param>
        /// <param name="knowledge">Knowledge object to cache.</param>
        /// <remarks>
        /// This method stores knowledge objects in the in-memory cache for fast access.
        /// Useful for caching frequently accessed knowledge or query results.
        /// </remarks>
        protected void CacheKnowledge(string topic, KnowledgeObject knowledge)
        {
            _knowledgeCache[topic] = knowledge;
        }

        /// <summary>
        /// Clears all cached knowledge objects.
        /// </summary>
        /// <remarks>
        /// This method clears the knowledge cache. Typically called during shutdown or reset operations.
        /// </remarks>
        protected void ClearKnowledgeCache()
        {
            _knowledgeCache.Clear();
        }

        #endregion
    }

    /// <summary>
    /// Abstract base class for data transformation plugins (Crypto, Compression, etc.).
    /// Provides default implementations for common transformation operations.
    /// AI-native: Supports intelligent transformation based on context.
    /// </summary>
    public abstract class DataTransformationPluginBase : PluginBase, IDataTransformation
    {
        /// <summary>
        /// Category is always DataTransformationProvider for transformation plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.DataTransformationProvider;

        /// <summary>
        /// Sub-category for more specific classification (e.g., "Encryption", "Compression").
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract string SubCategory { get; }

        /// <summary>
        /// Quality level (1-100) for sorting and selection.
        /// Default is 50 (balanced). Override for specific quality levels.
        /// </summary>
        public virtual int QualityLevel => 50;

        /// <summary>
        /// Transform data during write operations (e.g., encrypt, compress).
        /// LEGACY: Prefer OnWriteAsync for new implementations. This method calls OnWriteAsync synchronously.
        /// Must be implemented by derived classes OR override OnWriteAsync instead.
        /// </summary>
        public virtual Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            // Default implementation calls async version synchronously
            // Derived classes should override OnWriteAsync for proper async support
            return OnWriteAsync(input, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Transform data during read operations (e.g., decrypt, decompress).
        /// LEGACY: Prefer OnReadAsync for new implementations. This method calls OnReadAsync synchronously.
        /// Must be implemented by derived classes OR override OnReadAsync instead.
        /// </summary>
        public virtual Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            // Default implementation calls async version synchronously
            // Derived classes should override OnReadAsync for proper async support
            return OnReadAsync(stored, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async version of OnWrite. Transform data during write operations (e.g., encrypt, compress).
        /// RECOMMENDED: Override this method instead of OnWrite for proper async support.
        /// This is the preferred approach for plugins that need to call async APIs (e.g., IKeyStore).
        /// </summary>
        /// <param name="input">The input stream to transform.</param>
        /// <param name="context">The kernel context for logging and plugin access.</param>
        /// <param name="args">Operation-specific arguments.</param>
        /// <returns>A task that completes with the transformed stream.</returns>
        protected virtual Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            // Default implementation calls sync version
            // Derived classes override this for true async support
            return Task.FromResult(OnWrite(input, context, args));
        }

        /// <summary>
        /// Async version of OnRead. Transform data during read operations (e.g., decrypt, decompress).
        /// RECOMMENDED: Override this method instead of OnRead for proper async support.
        /// This is the preferred approach for plugins that need to call async APIs (e.g., IKeyStore).
        /// </summary>
        /// <param name="stored">The stored stream to transform.</param>
        /// <param name="context">The kernel context for logging and plugin access.</param>
        /// <param name="args">Operation-specific arguments.</param>
        /// <returns>A task that completes with the transformed stream.</returns>
        protected virtual Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            // Default implementation calls sync version
            // Derived classes override this for true async support
            return Task.FromResult(OnRead(stored, context, args));
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SubCategory"] = SubCategory;
            metadata["QualityLevel"] = QualityLevel;
            metadata["SupportsStreaming"] = true;
            metadata["TransformationType"] = "Bidirectional";
            metadata["SupportsAsyncTransform"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for storage provider plugins (Local, S3, IPFS, etc.).
    /// Provides default implementations for common storage operations.
    /// AI-native: Supports intelligent storage decisions based on content analysis.
    /// </summary>
    public abstract class StorageProviderPluginBase : PluginBase, IStorageProvider
    {
        /// <summary>
        /// Category is always StorageProvider for storage plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.StorageProvider;

        /// <summary>
        /// URI scheme for this storage provider (e.g., "file", "s3", "ipfs").
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract string Scheme { get; }

        /// <summary>
        /// Save data to storage. Must be implemented by derived classes.
        /// </summary>
        public abstract Task SaveAsync(Uri uri, Stream data);

        /// <summary>
        /// Retrieve data from storage. Must be implemented by derived classes.
        /// </summary>
        public abstract Task<Stream> LoadAsync(Uri uri);

        /// <summary>
        /// Delete data from storage. Must be implemented by derived classes.
        /// </summary>
        public abstract Task DeleteAsync(Uri uri);

        /// <summary>
        /// Check if data exists. Default implementation is optimized for efficiency.
        /// Override for provider-specific optimizations (e.g., HEAD request for HTTP).
        /// </summary>
        public virtual Task<bool> ExistsAsync(Uri uri)
        {
            // Default implementation - derived classes should override with
            // provider-specific efficient checks (e.g., S3 HeadObject, file system File.Exists)
            // This default uses try/load but is marked as inefficient for documentation
            return ExistsAsyncWithLoad(uri);
        }

        /// <summary>
        /// Fallback existence check using LoadAsync. Inefficient but works for any provider.
        /// Prefer overriding ExistsAsync with provider-specific implementation.
        /// </summary>
        protected async Task<bool> ExistsAsyncWithLoad(Uri uri)
        {
            try
            {
                // Use a limited read to avoid loading the entire file
                using var stream = await LoadAsync(uri);
                if (stream == null) return false;

                // Just check if we can read the first byte
                if (stream.CanRead)
                {
                    // Try to read 1 byte - if the file exists, this should succeed
                    var buffer = new byte[1];
                    var bytesRead = await stream.ReadAsync(buffer, 0, 1);
                    return true; // File exists (even if empty)
                }
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // File exists but we can't access it
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["StorageType"] = Scheme;
            metadata["SupportsStreaming"] = true;
            metadata["SupportsConcurrency"] = false;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for metadata indexing plugins (SQLite, Postgres, etc.).
    /// Provides default implementations for common indexing operations.
    /// AI-native: Supports semantic search and vector embeddings.
    /// </summary>
    public abstract class MetadataIndexPluginBase : PluginBase, IMetadataIndex
    {
        /// <summary>
        /// Category is always MetadataIndexingProvider for indexing plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.MetadataIndexingProvider;

        /// <summary>
        /// Index a manifest. Must be implemented by derived classes.
        /// </summary>
        public abstract Task IndexManifestAsync(Manifest manifest);

        /// <summary>
        /// Search for manifests with optional vector search.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<string[]> SearchAsync(string query, float[]? vector, int limit);

        /// <summary>
        /// Enumerate all manifests. Must be implemented by derived classes.
        /// </summary>
        public abstract IAsyncEnumerable<Manifest> EnumerateAllAsync(CancellationToken ct = default);

        /// <summary>
        /// Update last access time. Must be implemented by derived classes.
        /// </summary>
        public abstract Task UpdateLastAccessAsync(string id, long timestamp);

        /// <summary>
        /// Get manifest by ID. Must be implemented by derived classes.
        /// </summary>
        public abstract Task<Manifest?> GetManifestAsync(string id);

        /// <summary>
        /// Execute text query. Must be implemented by derived classes.
        /// </summary>
        public abstract Task<string[]> ExecuteQueryAsync(string query, int limit);

        /// <summary>
        /// Execute composite query. Default implementation delegates to text query.
        /// Override for advanced query support.
        /// </summary>
        public virtual Task<string[]> ExecuteQueryAsync(CompositeQuery query, int limit = 50)
        {
            // Default: convert composite to text
            return ExecuteQueryAsync(query.ToString() ?? string.Empty, limit);
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["IndexType"] = "Metadata";
            metadata["SupportsFullTextSearch"] = true;
            metadata["SupportsSemanticSearch"] = false;
            metadata["SupportsVectorSearch"] = false;
            metadata["SupportsCompositeQueries"] = false;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for feature plugins (SQL Listener, Consensus, Governance).
    /// Provides default implementations for lifecycle management.
    /// AI-native: Supports intelligent feature activation and monitoring.
    /// </summary>
    public abstract class FeaturePluginBase : PluginBase, IFeaturePlugin
    {
        /// <summary>
        /// Start the feature. Must be implemented by derived classes.
        /// </summary>
        public abstract Task StartAsync(CancellationToken ct);

        /// <summary>
        /// Stop the feature. Must be implemented by derived classes.
        /// </summary>
        public abstract Task StopAsync();

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Generic";
            metadata["RequiresLifecycleManagement"] = true;
            metadata["SupportsHotReload"] = false;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for security provider plugins (ACL, Encryption, etc.).
    /// Provides default implementations for common security operations.
    /// AI-native: Supports intelligent access control based on context.
    /// </summary>
    public abstract class SecurityProviderPluginBase : PluginBase
    {
        /// <summary>
        /// Category is always SecurityProvider for security plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.SecurityProvider;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SecurityType"] = "Generic";
            metadata["SupportsEncryption"] = false;
            metadata["SupportsACL"] = false;
            metadata["SupportsAuthentication"] = false;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for orchestration provider plugins (Workflow, Pipeline, etc.).
    /// Provides default implementations for common orchestration operations.
    /// AI-native: Supports autonomous workflow creation and execution.
    /// </summary>
    public abstract class OrchestrationProviderPluginBase : PluginBase
    {
        /// <summary>
        /// Category is always OrchestrationProvider for orchestration plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["OrchestrationType"] = "Generic";
            metadata["SupportsWorkflows"] = false;
            metadata["SupportsPipelines"] = false;
            metadata["SupportsAIOrchestration"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for intelligence/AI plugins (Embedding, Summarization, etc.).
    /// Provides default implementations for AI-powered operations.
    /// AI-native: Core class for integrating any AI/LLM provider.
    /// </summary>
    public abstract class IntelligencePluginBase : PluginBase
    {
        /// <summary>
        /// Category is always OrchestrationProvider for intelligence plugins (AI orchestration).
        /// </summary>
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        /// <summary>
        /// AI provider identifier (e.g., "openai", "claude", "ollama", "copilot").
        /// </summary>
        public abstract string ProviderType { get; }

        /// <summary>
        /// Model identifier (e.g., "gpt-4", "claude-3-opus", "llama3").
        /// </summary>
        public virtual string? ModelId => null;

        /// <summary>
        /// Whether this provider supports streaming responses.
        /// </summary>
        public virtual bool SupportsStreaming => false;

        /// <summary>
        /// Whether this provider supports embeddings.
        /// </summary>
        public virtual bool SupportsEmbeddings => false;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["IntelligenceType"] = ProviderType;
            metadata["ModelId"] = ModelId ?? "default";
            metadata["SupportsStreaming"] = SupportsStreaming;
            metadata["SupportsEmbeddings"] = SupportsEmbeddings;
            metadata["AIAgnostic"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for interface plugins (REST, SQL, gRPC, WebSocket).
    /// Provides default implementations for external interface operations.
    /// AI-native: Supports AI-driven interface discovery and usage.
    /// </summary>
    public abstract class InterfacePluginBase : FeaturePluginBase
    {
        /// <summary>
        /// Interface protocol type (e.g., "rest", "grpc", "sql", "websocket").
        /// </summary>
        public abstract string Protocol { get; }

        /// <summary>
        /// Port the interface listens on (if applicable).
        /// </summary>
        public virtual int? Port => null;

        /// <summary>
        /// Base path or endpoint for the interface.
        /// </summary>
        public virtual string? BasePath => null;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Interface";
            metadata["Protocol"] = Protocol;
            if (Port.HasValue) metadata["Port"] = Port.Value;
            if (BasePath != null) metadata["BasePath"] = BasePath;
            metadata["SupportsDiscovery"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for pipeline stage plugins (compression, encryption stages).
    /// Designed for runtime-configurable transformation pipelines.
    /// AI-native: Supports AI-driven pipeline optimization and ordering.
    /// </summary>
    public abstract class PipelinePluginBase : DataTransformationPluginBase
    {
        /// <summary>
        /// Default execution order (lower = earlier in pipeline).
        /// Can be overridden at runtime by the kernel.
        /// </summary>
        public virtual int DefaultOrder => 100;

        /// <summary>
        /// Whether this stage can be bypassed based on content analysis.
        /// </summary>
        public virtual bool AllowBypass => false;

        /// <summary>
        /// Stage dependencies - other stages that must run before this one.
        /// Empty means no dependencies.
        /// </summary>
        public virtual string[] RequiredPrecedingStages => Array.Empty<string>();

        /// <summary>
        /// Stage conflicts - stages that cannot run in the same pipeline.
        /// </summary>
        public virtual string[] IncompatibleStages => Array.Empty<string>();

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["PipelineStage"] = true;
            metadata["DefaultOrder"] = DefaultOrder;
            metadata["AllowBypass"] = AllowBypass;
            metadata["RequiredPrecedingStages"] = RequiredPrecedingStages;
            metadata["IncompatibleStages"] = IncompatibleStages;
            metadata["RuntimeOrderingSupported"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for listable storage providers that support file enumeration.
    /// Extends StorageProviderPluginBase with listing capabilities.
    /// </summary>
    public abstract class ListableStoragePluginBase : StorageProviderPluginBase, IListableStorage
    {
        /// <summary>
        /// List all files in storage matching a prefix.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract IAsyncEnumerable<StorageListItem> ListFilesAsync(string prefix = "", CancellationToken ct = default);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsListing"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for tiered storage providers that support data tiering.
    /// Extends ListableStoragePluginBase with tier management capabilities.
    /// </summary>
    public abstract class TieredStoragePluginBase : ListableStoragePluginBase, ITieredStorage
    {
        /// <summary>
        /// Move a blob to the specified storage tier.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<string> MoveToTierAsync(Manifest manifest, StorageTier targetTier);

        /// <summary>
        /// Get the current tier of a blob. Override for custom tier detection.
        /// </summary>
        public virtual Task<StorageTier> GetCurrentTierAsync(Uri uri)
        {
            return Task.FromResult(StorageTier.Hot);
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsTiering"] = true;
            metadata["SupportedTiers"] = new[] { "Hot", "Cool", "Cold", "Archive" };
            return metadata;
        }
    }

    #region Hybrid Storage Plugin Base Classes

    /// <summary>
    /// Abstract base class for cacheable storage providers.
    /// Adds TTL-based caching, expiration, and cache statistics to storage plugins.
    /// Plugins extending this class automatically gain caching capabilities.
    /// </summary>
    public abstract class CacheableStoragePluginBase : ListableStoragePluginBase, ICacheableStorage
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntryMetadata> _cacheMetadata = new();
        private readonly Timer? _cleanupTimer;
        private readonly CacheOptions _cacheOptions;

        /// <summary>
        /// Default cache options. Override to customize.
        /// </summary>
        protected virtual CacheOptions DefaultCacheOptions => new()
        {
            DefaultTtl = TimeSpan.FromHours(1),
            MaxEntries = 10000,
            EvictionPolicy = CacheEvictionPolicy.LRU,
            EnableStatistics = true
        };

        protected CacheableStoragePluginBase()
        {
            _cacheOptions = DefaultCacheOptions;
            if (_cacheOptions.CleanupInterval > TimeSpan.Zero)
            {
                _cleanupTimer = new Timer(
                    async _ => await CleanupExpiredAsync(),
                    null,
                    _cacheOptions.CleanupInterval,
                    _cacheOptions.CleanupInterval);
            }
        }

        /// <summary>
        /// Save data with a specific TTL.
        /// </summary>
        public virtual async Task SaveWithTtlAsync(Uri uri, Stream data, TimeSpan ttl, CancellationToken ct = default)
        {
            await SaveAsync(uri, data);
            var key = uri.ToString();
            _cacheMetadata[key] = new CacheEntryMetadata
            {
                Key = key,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(ttl),
                LastAccessedAt = DateTime.UtcNow,
                Size = data.Length
            };
        }

        /// <summary>
        /// Get the remaining TTL for an entry.
        /// </summary>
        public virtual Task<TimeSpan?> GetTtlAsync(Uri uri, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata) && metadata.ExpiresAt.HasValue)
            {
                var remaining = metadata.ExpiresAt.Value - DateTime.UtcNow;
                return Task.FromResult<TimeSpan?>(remaining > TimeSpan.Zero ? remaining : null);
            }
            return Task.FromResult<TimeSpan?>(null);
        }

        /// <summary>
        /// Set or update TTL for an existing entry.
        /// </summary>
        public virtual Task<bool> SetTtlAsync(Uri uri, TimeSpan ttl, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata))
            {
                metadata.ExpiresAt = DateTime.UtcNow.Add(ttl);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Invalidate all entries matching a pattern.
        /// </summary>
        public virtual async Task<int> InvalidatePatternAsync(string pattern, CancellationToken ct = default)
        {
            var regex = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$");

            var keysToRemove = _cacheMetadata.Keys.Where(k => regex.IsMatch(k)).ToList();
            var count = 0;

            foreach (var key in keysToRemove)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await DeleteAsync(new Uri(key));
                    _cacheMetadata.TryRemove(key, out _);
                    count++;
                }
                catch { /* Ignore errors during invalidation */ }
            }

            return count;
        }

        /// <summary>
        /// Get cache statistics.
        /// </summary>
        public virtual Task<CacheStatistics> GetCacheStatisticsAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var entries = _cacheMetadata.Values.ToList();

            return Task.FromResult(new CacheStatistics
            {
                TotalEntries = entries.Count,
                TotalSizeBytes = entries.Sum(e => e.Size),
                ExpiredEntries = entries.Count(e => e.ExpiresAt.HasValue && e.ExpiresAt < now),
                HitCount = entries.Sum(e => e.HitCount),
                MissCount = 0, // Would need separate tracking
                OldestEntry = entries.MinBy(e => e.CreatedAt)?.CreatedAt,
                NewestEntry = entries.MaxBy(e => e.CreatedAt)?.CreatedAt
            });
        }

        /// <summary>
        /// Remove all expired entries.
        /// </summary>
        public virtual Task<int> CleanupExpiredAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _cacheMetadata
                .Where(kv => kv.Value.ExpiresAt.HasValue && kv.Value.ExpiresAt < now)
                .Select(kv => kv.Key)
                .ToList();

            var count = 0;
            foreach (var key in expiredKeys)
            {
                if (ct.IsCancellationRequested) break;
                if (_cacheMetadata.TryRemove(key, out _))
                {
                    try { DeleteAsync(new Uri(key)).Wait(); } catch { }
                    count++;
                }
            }

            return Task.FromResult(count);
        }

        /// <summary>
        /// Touch an entry to update its last access time and reset sliding expiration.
        /// </summary>
        public virtual Task<bool> TouchAsync(Uri uri, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata))
            {
                metadata.LastAccessedAt = DateTime.UtcNow;
                metadata.HitCount++;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Remove TTL from an item, making it permanent.
        /// </summary>
        public virtual Task<bool> RemoveTtlAsync(Uri uri, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata))
            {
                metadata.ExpiresAt = null;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Invalidate all cached items with a specific tag.
        /// </summary>
        public virtual async Task<int> InvalidateByTagAsync(string tag, CancellationToken ct = default)
        {
            var keysWithTag = _cacheMetadata
                .Where(kv => kv.Value.Tags?.Contains(tag) == true)
                .Select(kv => kv.Key)
                .ToList();

            var count = 0;
            foreach (var key in keysWithTag)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await DeleteAsync(new Uri(key));
                    _cacheMetadata.TryRemove(key, out _);
                    count++;
                }
                catch { /* Ignore errors during invalidation */ }
            }

            return count;
        }

        /// <summary>
        /// Load with cache hit tracking. Derived classes must implement actual loading.
        /// Implementations should call TouchAsync(uri) to track cache access before loading.
        /// </summary>
        public abstract override Task<Stream> LoadAsync(Uri uri);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsCaching"] = true;
            metadata["SupportsTTL"] = true;
            metadata["SupportsExpiration"] = true;
            metadata["CacheEvictionPolicy"] = _cacheOptions.EvictionPolicy.ToString();
            return metadata;
        }

        /// <summary>
        /// Dispose cleanup timer.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cleanupTimer?.Dispose();
            }
        }
    }

    /// <summary>
    /// Abstract base class for indexable storage providers.
    /// Combines storage with full-text and metadata indexing capabilities.
    /// Plugins extending this class automatically gain indexing support.
    /// </summary>
    public abstract class IndexableStoragePluginBase : CacheableStoragePluginBase, IIndexableStorage
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, object>> _indexStore = new();
        private long _indexedCount;
        private DateTime? _lastIndexRebuild;

        /// <summary>
        /// Index a document with its metadata.
        /// </summary>
        public virtual Task IndexDocumentAsync(string id, Dictionary<string, object> metadata, CancellationToken ct = default)
        {
            metadata["_indexed_at"] = DateTime.UtcNow;
            _indexStore[id] = metadata;
            Interlocked.Increment(ref _indexedCount);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Remove a document from the index.
        /// </summary>
        public virtual Task<bool> RemoveFromIndexAsync(string id, CancellationToken ct = default)
        {
            var removed = _indexStore.TryRemove(id, out _);
            if (removed) Interlocked.Decrement(ref _indexedCount);
            return Task.FromResult(removed);
        }

        /// <summary>
        /// Search the index with a text query.
        /// </summary>
        public virtual Task<string[]> SearchIndexAsync(string query, int limit = 100, CancellationToken ct = default)
        {
            var queryLower = query.ToLowerInvariant();
            var results = _indexStore
                .Where(kv => kv.Value.Values.Any(v =>
                    v?.ToString()?.Contains(queryLower, StringComparison.OrdinalIgnoreCase) == true))
                .Take(limit)
                .Select(kv => kv.Key)
                .ToArray();

            return Task.FromResult(results);
        }

        /// <summary>
        /// Query by specific metadata criteria.
        /// </summary>
        public virtual Task<string[]> QueryByMetadataAsync(Dictionary<string, object> criteria, CancellationToken ct = default)
        {
            var results = _indexStore
                .Where(kv => criteria.All(c =>
                    kv.Value.TryGetValue(c.Key, out var v) &&
                    Equals(v?.ToString(), c.Value?.ToString())))
                .Select(kv => kv.Key)
                .ToArray();

            return Task.FromResult(results);
        }

        /// <summary>
        /// Get index statistics.
        /// </summary>
        public virtual Task<IndexStatistics> GetIndexStatisticsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new IndexStatistics
            {
                DocumentCount = _indexStore.Count,
                TermCount = _indexStore.Values
                    .SelectMany(v => v.Keys)
                    .Distinct()
                    .Count(),
                LastUpdated = _lastIndexRebuild ?? DateTime.UtcNow,
                IndexSizeBytes = _indexStore.Sum(kv =>
                    kv.Key.Length + kv.Value.Sum(v => (v.Key?.Length ?? 0) + (v.Value?.ToString()?.Length ?? 0))),
                IndexType = "InMemory"
            });
        }

        /// <summary>
        /// Optimize the index for better query performance.
        /// </summary>
        public virtual Task OptimizeIndexAsync(CancellationToken ct = default)
        {
            // Default implementation: no-op for in-memory index
            // Derived classes should override for actual optimization (compact, defrag, merge segments)
            return Task.CompletedTask;
        }

        /// <summary>
        /// Rebuild the entire index.
        /// </summary>
        public virtual async Task<int> RebuildIndexAsync(CancellationToken ct = default)
        {
            _indexStore.Clear();
            _indexedCount = 0;
            var count = 0;

            await foreach (var item in ListFilesAsync("", ct))
            {
                if (ct.IsCancellationRequested) break;

                // Index with basic metadata
                var id = item.Uri.ToString();
                await IndexDocumentAsync(id, new Dictionary<string, object>
                {
                    ["uri"] = id,
                    ["size"] = item.Size,
                    ["path"] = item.Uri.AbsolutePath
                }, ct);
                count++;
            }

            _lastIndexRebuild = DateTime.UtcNow;
            return count;
        }

        #region IMetadataIndex Implementation

        /// <summary>
        /// Index a manifest.
        /// </summary>
        public virtual async Task IndexManifestAsync(Manifest manifest)
        {
            var metadata = new Dictionary<string, object>
            {
                ["id"] = manifest.Id,
                ["hash"] = manifest.ContentHash,
                ["size"] = manifest.OriginalSize,
                ["created"] = manifest.CreatedAt,
                ["contentType"] = manifest.ContentType ?? ""
            };

            if (manifest.Metadata != null)
            {
                foreach (var kv in manifest.Metadata)
                {
                    metadata[kv.Key] = kv.Value;
                }
            }

            await IndexDocumentAsync(manifest.Id, metadata);
        }

        /// <summary>
        /// Search manifests with optional vector.
        /// </summary>
        public virtual Task<string[]> SearchAsync(string query, float[]? vector, int limit)
        {
            // Vector search would require specialized implementation
            return SearchIndexAsync(query, limit);
        }

        /// <summary>
        /// Enumerate all manifests in the index.
        /// </summary>
        public virtual async IAsyncEnumerable<Manifest> EnumerateAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var kv in _indexStore)
            {
                if (ct.IsCancellationRequested) yield break;

                yield return new Manifest
                {
                    Id = kv.Key,
                    ContentHash = kv.Value.TryGetValue("hash", out var h) ? h?.ToString() ?? "" : "",
                    OriginalSize = kv.Value.TryGetValue("size", out var s) && s is long size ? size : 0,
                    CreatedAt = kv.Value.TryGetValue("created", out var c) && c is DateTime dt
                        ? new DateTimeOffset(dt).ToUnixTimeSeconds()
                        : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Metadata = kv.Value.Where(x => !x.Key.StartsWith("_"))
                        .ToDictionary(x => x.Key, x => x.Value?.ToString() ?? "")
                };

                await Task.Yield();
            }
        }

        /// <summary>
        /// Update last access time for a manifest.
        /// </summary>
        public virtual Task UpdateLastAccessAsync(string id, long timestamp)
        {
            if (_indexStore.TryGetValue(id, out var metadata))
            {
                metadata["_last_access"] = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Get a manifest by ID.
        /// </summary>
        public virtual Task<Manifest?> GetManifestAsync(string id)
        {
            if (_indexStore.TryGetValue(id, out var metadata))
            {
                return Task.FromResult<Manifest?>(new Manifest
                {
                    Id = id,
                    ContentHash = metadata.TryGetValue("hash", out var h) ? h?.ToString() ?? "" : "",
                    OriginalSize = metadata.TryGetValue("size", out var s) && s is long size ? size : 0,
                    CreatedAt = metadata.TryGetValue("created", out var c) && c is DateTime dt
                        ? new DateTimeOffset(dt).ToUnixTimeSeconds()
                        : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Metadata = metadata.Where(x => !x.Key.StartsWith("_"))
                        .ToDictionary(x => x.Key, x => x.Value?.ToString() ?? "")
                });
            }
            return Task.FromResult<Manifest?>(null);
        }

        /// <summary>
        /// Execute a text query.
        /// </summary>
        public virtual Task<string[]> ExecuteQueryAsync(string query, int limit)
        {
            return SearchIndexAsync(query, limit);
        }

        /// <summary>
        /// Execute a composite query.
        /// </summary>
        public virtual Task<string[]> ExecuteQueryAsync(CompositeQuery query, int limit = 50)
        {
            return ExecuteQueryAsync(query.ToString() ?? "", limit);
        }

        #endregion

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsIndexing"] = true;
            metadata["SupportsFullTextSearch"] = true;
            metadata["SupportsMetadataQuery"] = true;
            metadata["IndexedDocuments"] = _indexedCount;
            return metadata;
        }
    }

    #endregion

    /// <summary>
    /// Abstract base class for consensus engine plugins (Raft, Paxos, etc.).
    /// Provides default implementations for distributed consensus operations.
    /// AI-native: Supports intelligent leader election and quorum decisions.
    /// </summary>
    public abstract class ConsensusPluginBase : FeaturePluginBase, IConsensusEngine
    {
        /// <summary>
        /// Category is always OrchestrationProvider for consensus plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        /// <summary>
        /// Whether this node is currently the leader.
        /// </summary>
        public abstract bool IsLeader { get; }

        /// <summary>
        /// Propose a state change to the cluster. Returns when quorum is reached.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<bool> ProposeAsync(Proposal proposal);

        /// <summary>
        /// Subscribe to committed entries from other nodes.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract void OnCommit(Action<Proposal> handler);

        /// <summary>
        /// Get current cluster state. Override for custom state reporting.
        /// </summary>
        public virtual Task<ClusterState> GetClusterStateAsync()
        {
            return Task.FromResult(new ClusterState { IsHealthy = true });
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Consensus";
            metadata["ConsensusAlgorithm"] = "Generic";
            metadata["SupportsLeaderElection"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Cluster state information.
    /// </summary>
    public class ClusterState
    {
        /// <summary>Whether the cluster is healthy.</summary>
        public bool IsHealthy { get; init; }
        /// <summary>Current leader node ID.</summary>
        public string? LeaderId { get; init; }
        /// <summary>Number of nodes in the cluster.</summary>
        public int NodeCount { get; init; }
        /// <summary>Current term/epoch.</summary>
        public long Term { get; init; }
    }

    /// <summary>
    /// Abstract base class for real-time event providers (pub/sub, change feeds).
    /// Provides default implementations for event publishing and subscription.
    /// AI-native: Supports intelligent event routing and filtering.
    /// </summary>
    public abstract class RealTimePluginBase : FeaturePluginBase, IRealTimeProvider
    {
        /// <summary>
        /// Category is always FeatureProvider for real-time plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <summary>
        /// Publish a storage event to the global fabric.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task PublishAsync(StorageEvent evt);

        /// <summary>
        /// Subscribe to changes matching a URI pattern.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<IAsyncDisposable> SubscribeAsync(string uriPattern, Action<StorageEvent> handler);

        /// <summary>
        /// Publish multiple events in batch. Default implementation uses parallel execution.
        /// Override for provider-specific batch optimizations (e.g., Kafka batch send).
        /// </summary>
        /// <param name="maxConcurrency">Maximum concurrent publish operations. Default is 10.</param>
        public virtual async Task PublishBatchAsync(IEnumerable<StorageEvent> events, int maxConcurrency = 10)
        {
            var eventList = events.ToList();
            if (eventList.Count == 0) return;

            // Use SemaphoreSlim for controlled concurrency
            using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var tasks = new List<Task>();

            foreach (var evt in eventList)
            {
                await semaphore.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await PublishAsync(evt);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Interface method for backward compatibility.
        /// </summary>
        public virtual Task PublishBatchAsync(IEnumerable<StorageEvent> events)
        {
            return PublishBatchAsync(events, maxConcurrency: 10);
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "RealTime";
            metadata["SupportsPubSub"] = true;
            metadata["SupportsPatternMatching"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for cloud environment plugins (AWS, Azure, GCP, Local).
    /// Provides default implementations for environment detection and provider creation.
    /// AI-native: Supports intelligent cloud resource optimization.
    /// </summary>
    public abstract class CloudEnvironmentPluginBase : PluginBase, ICloudEnvironment
    {
        /// <summary>
        /// Category is always FeatureProvider for environment plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <summary>
        /// Environment name (e.g., "AWS", "Azure", "Google", "Local").
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract string EnvironmentName { get; }

        /// <summary>
        /// Detect if running in this cloud environment.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract bool IsCurrentEnvironment();

        /// <summary>
        /// Create the storage provider optimized for this cloud.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract IStorageProvider CreateStorageProvider();

        /// <summary>
        /// Get environment-specific configuration. Override for custom config.
        /// </summary>
        public virtual Dictionary<string, string> GetEnvironmentConfig()
        {
            return new Dictionary<string, string>();
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "CloudEnvironment";
            metadata["EnvironmentName"] = EnvironmentName;
            metadata["SupportsAutoDetection"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for replication service plugins.
    /// Manages data redundancy, backups, and restoration.
    /// AI-native: Supports intelligent replica selection and auto-healing.
    /// </summary>
    public abstract class ReplicationPluginBase : FeaturePluginBase, IReplicationService
    {
        /// <summary>
        /// Category is always FederationProvider for replication plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.FederationProvider;

        /// <summary>
        /// Restores a corrupted blob using a healthy replica.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<bool> RestoreAsync(string blobId, string? replicaId);

        /// <summary>
        /// Get all available replicas for a blob. Override for custom logic.
        /// </summary>
        public virtual Task<string[]> GetAvailableReplicasAsync(string blobId)
        {
            return Task.FromResult(Array.Empty<string>());
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Replication";
            metadata["SupportsAutoHeal"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for serialization plugins (JSON, MessagePack, Protobuf).
    /// AI-native: Supports automatic format detection and optimization.
    /// </summary>
    public abstract class SerializerPluginBase : PluginBase, ISerializer
    {
        /// <summary>
        /// Category is always SerializationProvider.
        /// </summary>
        public override PluginCategory Category => PluginCategory.SerializationProvider;

        /// <summary>
        /// Format name (e.g., "json", "msgpack", "protobuf").
        /// </summary>
        public abstract string FormatName { get; }

        /// <summary>
        /// MIME type for this format.
        /// </summary>
        public abstract string MimeType { get; }

        public abstract string Serialize<T>(T value);
        public abstract T? Deserialize<T>(string value);
        public abstract object? Deserialize(string value, Type type);
        public abstract Task SerializeAsync<T>(Stream stream, T value);
        public abstract Task<T?> DeserializeAsync<T>(Stream stream);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FormatName"] = FormatName;
            metadata["MimeType"] = MimeType;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for semantic memory plugins.
    /// Provides AI-native memory storage and retrieval with vector search.
    /// </summary>
    public abstract class SemanticMemoryPluginBase : PluginBase, ISemanticMemory
    {
        /// <summary>
        /// Category is always AIProvider for semantic memory.
        /// </summary>
        public override PluginCategory Category => PluginCategory.AIProvider;

        public abstract Task<string> MemorizeAsync(string content, string[] tags, string? summary = null);
        public abstract Task<string> RecallAsync(string memoryId);
        public abstract Task<string[]> SearchMemoriesAsync(string query, float[]? vector, int limit = 5);

        /// <summary>
        /// Embedding dimension used by this memory provider.
        /// </summary>
        public virtual int EmbeddingDimension => 1536;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "SemanticMemory";
            metadata["EmbeddingDimension"] = EmbeddingDimension;
            metadata["SupportsVectorSearch"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for metrics and observability plugins.
    /// AI-native: Supports intelligent alerting and anomaly detection.
    /// </summary>
    public abstract class MetricsPluginBase : PluginBase, IMetricsProvider
    {
        /// <summary>
        /// Category is always MetricsProvider.
        /// </summary>
        public override PluginCategory Category => PluginCategory.MetricsProvider;

        public abstract void IncrementCounter(string metric);
        public abstract void RecordMetric(string metric, double value);
        public abstract IDisposable TrackDuration(string metric);

        /// <summary>
        /// Flush metrics to backend. Override for batched sending.
        /// </summary>
        public virtual Task FlushAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Metrics";
            metadata["SupportsHistograms"] = false;
            metadata["SupportsTags"] = false;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for access control plugins.
    /// Extends SecurityProviderPluginBase with ACL management.
    /// AI-native: Supports intelligent permission suggestions.
    /// </summary>
    public abstract class AccessControlPluginBase : SecurityProviderPluginBase, IAccessControl
    {
        public abstract void SetPermissions(string resource, string subject, Permission allow, Permission deny);
        public abstract bool HasAccess(string resource, string subject, Permission requested);
        public abstract void CreateScope(string resource, string owner);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SecurityType"] = "AccessControl";
            metadata["SupportsGranularACL"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for governance/sentinel plugins (Neural Sentinel).
    /// AI-native: Core class for AI-driven governance and compliance.
    /// </summary>
    public abstract class GovernancePluginBase : PluginBase, Governance.INeuralSentinel
    {
        /// <summary>
        /// Category is always GovernanceProvider.
        /// </summary>
        public override PluginCategory Category => PluginCategory.GovernanceProvider;

        /// <summary>
        /// Evaluates a data operation context and determines if intervention is required.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<Governance.GovernanceJudgment> EvaluateAsync(Governance.SentinelContext context);

        /// <summary>
        /// Registered sentinel modules for this governance plugin.
        /// </summary>
        protected List<Governance.ISentinelModule> Modules { get; } = new();

        /// <summary>
        /// Register a sentinel module.
        /// </summary>
        public void RegisterModule(Governance.ISentinelModule module)
        {
            Modules.Add(module);
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["GovernanceType"] = "NeuralSentinel";
            metadata["ModuleCount"] = Modules.Count;
            metadata["SupportsRealTimeScanning"] = true;
            return metadata;
        }
    }

    #region Key Store Plugin Base

    /// <summary>
    /// Abstract base class for all key management plugins.
    /// Provides common caching, initialization, and validation logic.
    /// All key management plugins MUST extend this class.
    /// </summary>
    public abstract class KeyStorePluginBase : SecurityProviderPluginBase, IKeyStore
    {
        #region Cache Infrastructure

        /// <summary>
        /// Cache entry for keys with expiration tracking.
        /// </summary>
        protected class CachedKey
        {
            public byte[] Key { get; init; } = Array.Empty<byte>();
            public DateTime CachedAt { get; init; }
            public DateTime? ExpiresAt { get; init; }
            public int AccessCount { get; set; }
            public DateTime LastAccessedAt { get; set; }
        }

        /// <summary>
        /// Thread-safe key cache.
        /// </summary>
        protected readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedKey> KeyCache = new();

        /// <summary>
        /// Lock for thread-safe initialization.
        /// </summary>
        protected readonly SemaphoreSlim InitLock = new(1, 1);

        /// <summary>
        /// The current active key ID.
        /// </summary>
        protected string? CurrentKeyId;

        /// <summary>
        /// Whether the key store has been initialized.
        /// </summary>
        protected bool Initialized;

        #endregion

        #region Configuration (Override in Derived Classes)

        /// <summary>
        /// Cache expiration time. Override to customize.
        /// Default: 1 hour.
        /// </summary>
        protected virtual TimeSpan CacheExpiration => TimeSpan.FromHours(1);

        /// <summary>
        /// Key size in bytes for newly created keys.
        /// Override to customize (e.g., 32 for AES-256).
        /// </summary>
        protected virtual int KeySizeBytes => 32;

        /// <summary>
        /// Maximum number of keys to cache.
        /// </summary>
        protected virtual int MaxCachedKeys => 100;

        /// <summary>
        /// Whether authentication is required for key operations.
        /// </summary>
        protected virtual bool RequireAuthentication => true;

        /// <summary>
        /// Whether admin access is required for key creation.
        /// </summary>
        protected virtual bool RequireAdminForCreate => true;

        /// <summary>
        /// Key store type for metadata (e.g., "file", "vault", "hsm").
        /// </summary>
        protected abstract string KeyStoreType { get; }

        #endregion

        #region IKeyStore Implementation

        /// <inheritdoc/>
        public virtual async Task<string> GetCurrentKeyIdAsync()
        {
            await EnsureInitializedAsync();
            return CurrentKeyId ?? throw new InvalidOperationException("No current key ID set. Call CreateKeyAsync first.");
        }

        /// <inheritdoc/>
        public virtual byte[] GetKey(string keyId)
        {
            // Sync wrapper - prefer async version
            return GetKeyInternalAsync(keyId, null).GetAwaiter().GetResult();
        }

        /// <inheritdoc/>
        public virtual async Task<byte[]> GetKeyAsync(string keyId, ISecurityContext context)
        {
            ValidateAccess(context);
            return await GetKeyInternalAsync(keyId, context);
        }

        /// <inheritdoc/>
        public virtual async Task<byte[]> CreateKeyAsync(string keyId, ISecurityContext context)
        {
            ValidateAdminAccess(context);
            await EnsureInitializedAsync();

            // Generate new key
            var key = GenerateKey();

            // Save to storage
            await SaveKeyToStorageAsync(keyId, key);

            // Update current key ID
            CurrentKeyId = keyId;

            // Cache the key
            CacheKey(keyId, key);

            // Log key creation for audit
            OnKeyCreated(keyId, context);

            return key;
        }

        #endregion

        #region Internal Key Operations

        private async Task<byte[]> GetKeyInternalAsync(string keyId, ISecurityContext? context)
        {
            await EnsureInitializedAsync();

            // Check cache first
            if (TryGetFromCache(keyId, out var cachedKey))
            {
                OnKeyAccessed(keyId, context, fromCache: true);
                return cachedKey;
            }

            // Load from storage
            var key = await LoadKeyFromStorageAsync(keyId);
            if (key == null)
            {
                throw new KeyNotFoundException($"Key '{keyId}' not found in storage.");
            }

            // Cache and return
            CacheKey(keyId, key);
            OnKeyAccessed(keyId, context, fromCache: false);

            return key;
        }

        /// <summary>
        /// Attempts to get a key from cache.
        /// </summary>
        protected bool TryGetFromCache(string keyId, out byte[] key)
        {
            key = Array.Empty<byte>();

            if (!KeyCache.TryGetValue(keyId, out var cached))
                return false;

            // Check expiration
            if (cached.ExpiresAt.HasValue && cached.ExpiresAt.Value < DateTime.UtcNow)
            {
                KeyCache.TryRemove(keyId, out _);
                return false;
            }

            // Update access stats
            cached.AccessCount++;
            cached.LastAccessedAt = DateTime.UtcNow;
            key = cached.Key;
            return true;
        }

        /// <summary>
        /// Caches a key with expiration.
        /// </summary>
        protected void CacheKey(string keyId, byte[] key)
        {
            // Evict oldest if at capacity
            if (KeyCache.Count >= MaxCachedKeys)
            {
                var oldest = KeyCache
                    .OrderBy(kv => kv.Value.LastAccessedAt)
                    .FirstOrDefault();
                if (oldest.Key != null)
                {
                    KeyCache.TryRemove(oldest.Key, out _);
                }
            }

            KeyCache[keyId] = new CachedKey
            {
                Key = key,
                CachedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(CacheExpiration),
                AccessCount = 1,
                LastAccessedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Generates a cryptographically secure random key.
        /// </summary>
        protected virtual byte[] GenerateKey()
        {
            var key = new byte[KeySizeBytes];
            System.Security.Cryptography.RandomNumberGenerator.Fill(key);
            return key;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Ensures the key store is initialized (thread-safe).
        /// </summary>
        protected async Task EnsureInitializedAsync()
        {
            if (Initialized) return;

            await InitLock.WaitAsync();
            try
            {
                if (Initialized) return;
                await InitializeStorageAsync();
                Initialized = true;
            }
            finally
            {
                InitLock.Release();
            }
        }

        #endregion

        #region Validation

        /// <summary>
        /// Validates that the caller has access to key operations.
        /// </summary>
        protected virtual void ValidateAccess(ISecurityContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (RequireAuthentication && string.IsNullOrEmpty(context.UserId))
                throw new UnauthorizedAccessException("Authentication required for key access.");
        }

        /// <summary>
        /// Validates that the caller has admin access for key creation.
        /// </summary>
        protected virtual void ValidateAdminAccess(ISecurityContext context)
        {
            ValidateAccess(context);

            if (RequireAdminForCreate && !context.IsSystemAdmin)
                throw new UnauthorizedAccessException("Admin access required for key creation.");
        }

        #endregion

        #region Abstract Methods (Implement in Derived Classes)

        /// <summary>
        /// Loads a key from the underlying storage.
        /// Returns null if key not found.
        /// </summary>
        protected abstract Task<byte[]?> LoadKeyFromStorageAsync(string keyId);

        /// <summary>
        /// Saves a key to the underlying storage.
        /// </summary>
        protected abstract Task SaveKeyToStorageAsync(string keyId, byte[] key);

        /// <summary>
        /// Initializes the key storage (create directories, connect to services, etc.).
        /// Called once during first operation.
        /// </summary>
        protected abstract Task InitializeStorageAsync();

        #endregion

        #region Event Hooks (Override for Custom Behavior)

        /// <summary>
        /// Called when a key is accessed. Override for custom logging/auditing.
        /// </summary>
        protected virtual void OnKeyAccessed(string keyId, ISecurityContext? context, bool fromCache)
        {
            // Default: no-op. Override for audit logging.
        }

        /// <summary>
        /// Called when a key is created. Override for custom logging/auditing.
        /// </summary>
        protected virtual void OnKeyCreated(string keyId, ISecurityContext context)
        {
            // Default: no-op. Override for audit logging.
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// Clears all cached keys.
        /// </summary>
        public virtual void ClearCache()
        {
            KeyCache.Clear();
        }

        /// <summary>
        /// Removes a specific key from cache.
        /// </summary>
        public virtual bool InvalidateCachedKey(string keyId)
        {
            return KeyCache.TryRemove(keyId, out _);
        }

        /// <summary>
        /// Gets cache statistics.
        /// </summary>
        public virtual (int Count, int TotalAccesses) GetCacheStats()
        {
            var entries = KeyCache.Values.ToList();
            return (entries.Count, entries.Sum(e => e.AccessCount));
        }

        #endregion

        #region Metadata

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SecurityType"] = "KeyStore";
            metadata["KeyStoreType"] = KeyStoreType;
            metadata["KeySizeBytes"] = KeySizeBytes;
            metadata["CacheEnabled"] = true;
            metadata["CacheExpirationMinutes"] = CacheExpiration.TotalMinutes;
            metadata["SupportsEncryption"] = true;
            return metadata;
        }

        #endregion

        #region IDisposable

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                // Clear sensitive data from cache
                foreach (var entry in KeyCache.Values)
                {
                    Array.Clear(entry.Key, 0, entry.Key.Length);
                }
                KeyCache.Clear();
                InitLock.Dispose();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    #endregion

    #region Encryption Plugin Base

    /// <summary>
    /// Abstract base class for all encryption plugins with composable key management.
    /// Supports per-user, per-operation configuration for maximum flexibility.
    /// All encryption plugins MUST extend this class.
    /// </summary>
    public abstract class EncryptionPluginBase : PipelinePluginBase, IDisposable
    {
        #region Configuration

        /// <summary>
        /// Default key store (fallback when no user preference or explicit override).
        /// </summary>
        protected IKeyStore? DefaultKeyStore;

        /// <summary>
        /// Default key management mode.
        /// </summary>
        protected KeyManagementMode DefaultKeyManagementMode = KeyManagementMode.Direct;

        /// <summary>
        /// Default envelope key store for Envelope mode.
        /// </summary>
        protected IEnvelopeKeyStore? DefaultEnvelopeKeyStore;

        /// <summary>
        /// Default KEK key ID for Envelope mode.
        /// </summary>
        protected string? DefaultKekKeyId;

        /// <summary>
        /// Per-user configuration provider (optional, for multi-tenant).
        /// </summary>
        protected IKeyManagementConfigProvider? ConfigProvider;

        /// <summary>
        /// Key store registry for resolving plugin IDs.
        /// </summary>
        protected IKeyStoreRegistry? KeyStoreRegistry;

        #endregion

        #region Statistics

        /// <summary>
        /// Lock for thread-safe statistics updates.
        /// </summary>
        protected readonly object StatsLock = new();

        /// <summary>
        /// Total encryption operations performed.
        /// </summary>
        protected long EncryptionCount;

        /// <summary>
        /// Total decryption operations performed.
        /// </summary>
        protected long DecryptionCount;

        /// <summary>
        /// Total bytes encrypted.
        /// </summary>
        protected long TotalBytesEncrypted;

        /// <summary>
        /// Total bytes decrypted.
        /// </summary>
        protected long TotalBytesDecrypted;

        /// <summary>
        /// Key access audit log (tracks when each key was last used).
        /// </summary>
        protected readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> KeyAccessLog = new();

        #endregion

        #region Abstract Configuration Properties

        /// <summary>
        /// Key size in bytes for this encryption algorithm.
        /// E.g., 32 for AES-256.
        /// </summary>
        protected abstract int KeySizeBytes { get; }

        /// <summary>
        /// IV/nonce size in bytes for this encryption algorithm.
        /// E.g., 12 for AES-GCM.
        /// </summary>
        protected abstract int IvSizeBytes { get; }

        /// <summary>
        /// Authentication tag size in bytes (for AEAD algorithms).
        /// E.g., 16 for AES-GCM.
        /// </summary>
        protected abstract int TagSizeBytes { get; }

        /// <summary>
        /// The encryption algorithm identifier.
        /// E.g., "AES-256-GCM", "ChaCha20-Poly1305".
        /// </summary>
        protected abstract string AlgorithmId { get; }

        #endregion

        #region Configuration Resolution

        /// <summary>
        /// Resolves key management configuration for this operation.
        /// Priority: 1. Explicit args, 2. User preferences, 3. Plugin defaults
        /// </summary>
        protected virtual async Task<ResolvedKeyManagementConfig> ResolveConfigAsync(
            Dictionary<string, object> args,
            ISecurityContext context)
        {
            // 1. Check for explicit overrides in args
            if (TryGetConfigFromArgs(args, out var argsConfig))
                return argsConfig;

            // 2. Check for user preferences via ConfigProvider
            if (ConfigProvider != null)
            {
                var userConfig = await ConfigProvider.GetConfigAsync(context);
                if (userConfig != null)
                    return ResolveFromUserConfig(userConfig);
            }

            // 3. Fall back to plugin defaults
            return new ResolvedKeyManagementConfig
            {
                Mode = DefaultKeyManagementMode,
                KeyStore = DefaultKeyStore,
                EnvelopeKeyStore = DefaultEnvelopeKeyStore,
                KekKeyId = DefaultKekKeyId
            };
        }

        /// <summary>
        /// Attempts to extract configuration from operation arguments.
        /// </summary>
        protected virtual bool TryGetConfigFromArgs(Dictionary<string, object> args, out ResolvedKeyManagementConfig config)
        {
            config = default!;

            // Check if explicit mode is specified
            if (!args.TryGetValue("keyManagementMode", out var modeObj))
                return false;

            var mode = modeObj switch
            {
                KeyManagementMode m => m,
                string s when Enum.TryParse<KeyManagementMode>(s, true, out var parsed) => parsed,
                _ => KeyManagementMode.Direct
            };

            // Resolve key store from args
            IKeyStore? keyStore = null;
            IEnvelopeKeyStore? envelopeKeyStore = null;
            string? keyStorePluginId = null;
            string? envelopeKeyStorePluginId = null;
            string? kekKeyId = null;
            string? keyId = null;

            if (args.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
                keyStore = ks;
            else if (args.TryGetValue("keyStorePluginId", out var kspObj) && kspObj is string ksp)
            {
                keyStorePluginId = ksp;
                keyStore = KeyStoreRegistry?.GetKeyStore(ksp);
            }

            if (args.TryGetValue("envelopeKeyStore", out var eksObj) && eksObj is IEnvelopeKeyStore eks)
                envelopeKeyStore = eks;
            else if (args.TryGetValue("envelopeKeyStorePluginId", out var ekspObj) && ekspObj is string eksp)
            {
                envelopeKeyStorePluginId = eksp;
                envelopeKeyStore = KeyStoreRegistry?.GetEnvelopeKeyStore(eksp);
            }

            if (args.TryGetValue("kekKeyId", out var kekObj) && kekObj is string kek)
                kekKeyId = kek;

            if (args.TryGetValue("keyId", out var kidObj) && kidObj is string kid)
                keyId = kid;

            config = new ResolvedKeyManagementConfig
            {
                Mode = mode,
                KeyStore = keyStore ?? DefaultKeyStore,
                KeyId = keyId,
                EnvelopeKeyStore = envelopeKeyStore ?? DefaultEnvelopeKeyStore,
                KekKeyId = kekKeyId ?? DefaultKekKeyId,
                KeyStorePluginId = keyStorePluginId,
                EnvelopeKeyStorePluginId = envelopeKeyStorePluginId
            };

            return true;
        }

        /// <summary>
        /// Resolves configuration from user preferences.
        /// </summary>
        protected virtual ResolvedKeyManagementConfig ResolveFromUserConfig(KeyManagementConfig userConfig)
        {
            return new ResolvedKeyManagementConfig
            {
                Mode = userConfig.Mode,
                KeyStore = userConfig.KeyStore ?? KeyStoreRegistry?.GetKeyStore(userConfig.KeyStorePluginId),
                KeyId = userConfig.KeyId,
                EnvelopeKeyStore = userConfig.EnvelopeKeyStore ?? KeyStoreRegistry?.GetEnvelopeKeyStore(userConfig.EnvelopeKeyStorePluginId),
                KekKeyId = userConfig.KekKeyId,
                KeyStorePluginId = userConfig.KeyStorePluginId,
                EnvelopeKeyStorePluginId = userConfig.EnvelopeKeyStorePluginId
            };
        }

        #endregion

        #region Key Management

        /// <summary>
        /// Gets a key for encryption based on resolved configuration.
        /// For Direct mode: retrieves key from key store.
        /// For Envelope mode: generates random DEK, wraps with KEK.
        /// </summary>
        protected virtual async Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetKeyForEncryptionAsync(
            ResolvedKeyManagementConfig config,
            ISecurityContext context)
        {
            if (config.Mode == KeyManagementMode.Envelope)
            {
                return await GetEnvelopeKeyForEncryptionAsync(config, context);
            }
            else
            {
                return await GetDirectKeyForEncryptionAsync(config, context);
            }
        }

        /// <summary>
        /// Gets a key for Direct mode encryption.
        /// </summary>
        protected virtual async Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetDirectKeyForEncryptionAsync(
            ResolvedKeyManagementConfig config,
            ISecurityContext context)
        {
            if (config.KeyStore == null)
                throw new InvalidOperationException("No key store configured for Direct mode encryption.");

            var keyId = config.KeyId ?? await config.KeyStore.GetCurrentKeyIdAsync();
            var key = await config.KeyStore.GetKeyAsync(keyId, context);

            // Log key access
            KeyAccessLog[keyId] = DateTime.UtcNow;

            return (key, keyId, null);
        }

        /// <summary>
        /// Gets a key for Envelope mode encryption.
        /// Generates a random DEK and wraps it with the KEK.
        /// </summary>
        protected virtual async Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetEnvelopeKeyForEncryptionAsync(
            ResolvedKeyManagementConfig config,
            ISecurityContext context)
        {
            if (config.EnvelopeKeyStore == null)
                throw new InvalidOperationException("No envelope key store configured for Envelope mode encryption.");
            if (string.IsNullOrEmpty(config.KekKeyId))
                throw new InvalidOperationException("No KEK key ID configured for Envelope mode encryption.");

            // Generate random DEK
            var dek = new byte[KeySizeBytes];
            System.Security.Cryptography.RandomNumberGenerator.Fill(dek);

            // Wrap DEK with KEK
            var wrappedDek = await config.EnvelopeKeyStore.WrapKeyAsync(config.KekKeyId, dek, context);

            // Generate IV
            var iv = GenerateIv();

            // Create envelope header
            var envelope = new EnvelopeHeader
            {
                KekId = config.KekKeyId,
                KeyStorePluginId = config.EnvelopeKeyStorePluginId ?? "",
                WrappedDek = wrappedDek,
                Iv = iv,
                EncryptionAlgorithm = AlgorithmId,
                EncryptionPluginId = Id,
                EncryptedAtTicks = DateTime.UtcNow.Ticks,
                EncryptedBy = context.UserId
            };

            // Log key access
            KeyAccessLog[$"envelope:{config.KekKeyId}"] = DateTime.UtcNow;

            return (dek, $"envelope:{Guid.NewGuid():N}", envelope);
        }

        /// <summary>
        /// Gets a key for decryption based on stored metadata.
        /// </summary>
        protected virtual async Task<byte[]> GetKeyForDecryptionAsync(
            EnvelopeHeader? envelope,
            string? keyId,
            ResolvedKeyManagementConfig config,
            ISecurityContext context)
        {
            if (envelope != null)
            {
                // Envelope mode: unwrap DEK
                var envelopeKeyStore = config.EnvelopeKeyStore
                    ?? KeyStoreRegistry?.GetEnvelopeKeyStore(envelope.KeyStorePluginId)
                    ?? throw new InvalidOperationException($"Cannot find envelope key store '{envelope.KeyStorePluginId}' to unwrap DEK.");

                var dek = await envelopeKeyStore.UnwrapKeyAsync(envelope.KekId, envelope.WrappedDek, context);

                KeyAccessLog[$"envelope:{envelope.KekId}"] = DateTime.UtcNow;

                return dek;
            }
            else
            {
                // Direct mode: get key from store
                if (string.IsNullOrEmpty(keyId))
                    throw new InvalidOperationException("Key ID required for Direct mode decryption.");

                var keyStore = config.KeyStore
                    ?? DefaultKeyStore
                    ?? throw new InvalidOperationException("No key store configured for Direct mode decryption.");

                var key = await keyStore.GetKeyAsync(keyId, context);

                KeyAccessLog[keyId] = DateTime.UtcNow;

                return key;
            }
        }

        #endregion

        #region Security Context Resolution

        /// <summary>
        /// Gets the security context from operation arguments.
        /// </summary>
        protected virtual ISecurityContext GetSecurityContext(Dictionary<string, object> args, IKernelContext context)
        {
            // Check for explicit security context in args
            if (args.TryGetValue("securityContext", out var ctxObj) && ctxObj is ISecurityContext secCtx)
                return secCtx;

            // Try to get from kernel context
            // TODO: Define how kernel context provides security context

            // Fallback to system context
            return new DefaultSecurityContext();
        }

        /// <summary>
        /// Default security context for when none is provided.
        /// </summary>
        protected class DefaultSecurityContext : ISecurityContext
        {
            public string UserId => "system";
            public string? TenantId => null;
            public IEnumerable<string> Roles => new[] { "system" };
            public bool IsSystemAdmin => true;
        }

        #endregion

        #region OnWrite/OnRead Implementation

        /// <summary>
        /// Encrypts data during write operations.
        /// Resolves config per-operation, supports both Direct and Envelope modes.
        /// </summary>
        protected override async Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            var securityContext = GetSecurityContext(args, context);
            var config = await ResolveConfigAsync(args, securityContext);

            var (key, keyId, envelope) = await GetKeyForEncryptionAsync(config, securityContext);

            try
            {
                // Get IV (from envelope or generate new)
                var iv = envelope?.Iv ?? GenerateIv();

                // Create encryption metadata for storage
                var metadata = new EncryptionMetadata
                {
                    EncryptionPluginId = Id,
                    KeyMode = config.Mode,
                    KeyId = config.Mode == KeyManagementMode.Direct ? keyId : null,
                    WrappedDek = envelope?.WrappedDek,
                    KekId = envelope?.KekId,
                    KeyStorePluginId = config.Mode == KeyManagementMode.Direct
                        ? config.KeyStorePluginId
                        : config.EnvelopeKeyStorePluginId,
                    AlgorithmParams = new Dictionary<string, object>
                    {
                        ["iv"] = Convert.ToBase64String(iv),
                        ["algorithm"] = AlgorithmId
                    },
                    EncryptedAt = DateTime.UtcNow,
                    EncryptedBy = securityContext.UserId
                };

                // Store metadata in args for downstream consumers (tamper-proof manifest, etc.)
                args["encryptionMetadata"] = metadata;

                // Perform encryption
                var result = await EncryptCoreAsync(input, key, iv, context);

                // Update statistics
                UpdateEncryptionStats(input.Length);

                return result;
            }
            finally
            {
                // Clear key from memory
                Array.Clear(key, 0, key.Length);
            }
        }

        /// <summary>
        /// Decrypts data during read operations.
        /// Uses stored metadata to determine correct decryption approach.
        /// </summary>
        protected override async Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            var securityContext = GetSecurityContext(args, context);

            // Try to get encryption metadata from args (from manifest or header)
            EnvelopeHeader? envelope = null;
            string? keyId = null;
            byte[]? iv = null;

            if (args.TryGetValue("encryptionMetadata", out var metaObj) && metaObj is EncryptionMetadata metadata)
            {
                // Use metadata from manifest
                envelope = metadata.KeyMode == KeyManagementMode.Envelope
                    ? new EnvelopeHeader
                    {
                        KekId = metadata.KekId ?? "",
                        KeyStorePluginId = metadata.KeyStorePluginId ?? "",
                        WrappedDek = metadata.WrappedDek ?? Array.Empty<byte>()
                    }
                    : null;
                keyId = metadata.KeyId;

                if (metadata.AlgorithmParams.TryGetValue("iv", out var ivObj) && ivObj is string ivStr)
                {
                    iv = Convert.FromBase64String(ivStr);
                }
            }
            else
            {
                // Check for envelope header in stream
                if (await EnvelopeHeader.IsEnvelopeEncryptedAsync(stored))
                {
                    // Read and parse envelope header
                    var headerBuffer = new byte[4096]; // Reasonable max header size
                    var bytesRead = await stored.ReadAsync(headerBuffer, 0, headerBuffer.Length);
                    stored.Position = 0;

                    if (EnvelopeHeader.TryDeserialize(headerBuffer, out envelope, out var headerLength))
                    {
                        // Skip header for decryption
                        stored.Position = headerLength;
                        iv = envelope!.Iv;
                    }
                }
                else if (args.TryGetValue("keyId", out var kidObj) && kidObj is string kid)
                {
                    keyId = kid;
                }
            }

            // Resolve config (may be overridden by args for testing/migration)
            var config = await ResolveConfigAsync(args, securityContext);

            // Get decryption key
            var key = await GetKeyForDecryptionAsync(envelope, keyId, config, securityContext);

            try
            {
                // Perform decryption
                var (result, _) = await DecryptCoreAsync(stored, key, iv, context);

                // Update statistics
                UpdateDecryptionStats(stored.Length);

                return result;
            }
            finally
            {
                // Clear key from memory
                Array.Clear(key, 0, key.Length);
            }
        }

        #endregion

        #region Abstract Methods (Algorithm-Specific)

        /// <summary>
        /// Performs the core encryption operation.
        /// Must be implemented by derived classes with specific algorithms.
        /// </summary>
        /// <param name="input">The plaintext input stream.</param>
        /// <param name="key">The encryption key.</param>
        /// <param name="iv">The initialization vector.</param>
        /// <param name="context">The kernel context.</param>
        /// <returns>The encrypted stream.</returns>
        protected abstract Task<Stream> EncryptCoreAsync(Stream input, byte[] key, byte[] iv, IKernelContext context);

        /// <summary>
        /// Performs the core decryption operation.
        /// Must be implemented by derived classes with specific algorithms.
        /// </summary>
        /// <param name="input">The ciphertext input stream.</param>
        /// <param name="key">The decryption key.</param>
        /// <param name="iv">The initialization vector (null if embedded in ciphertext).</param>
        /// <param name="context">The kernel context.</param>
        /// <returns>The decrypted stream and authentication tag (if applicable).</returns>
        protected abstract Task<(Stream data, byte[]? tag)> DecryptCoreAsync(Stream input, byte[] key, byte[]? iv, IKernelContext context);

        /// <summary>
        /// Generates a random IV/nonce.
        /// Override if algorithm requires specific IV generation.
        /// </summary>
        protected virtual byte[] GenerateIv()
        {
            var iv = new byte[IvSizeBytes];
            System.Security.Cryptography.RandomNumberGenerator.Fill(iv);
            return iv;
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Updates encryption statistics.
        /// </summary>
        protected virtual void UpdateEncryptionStats(long bytesProcessed)
        {
            lock (StatsLock)
            {
                EncryptionCount++;
                TotalBytesEncrypted += bytesProcessed;
            }
        }

        /// <summary>
        /// Updates decryption statistics.
        /// </summary>
        protected virtual void UpdateDecryptionStats(long bytesProcessed)
        {
            lock (StatsLock)
            {
                DecryptionCount++;
                TotalBytesDecrypted += bytesProcessed;
            }
        }

        /// <summary>
        /// Gets encryption statistics.
        /// </summary>
        public virtual EncryptionStatistics GetStatistics()
        {
            lock (StatsLock)
            {
                return new EncryptionStatistics
                {
                    EncryptionCount = EncryptionCount,
                    DecryptionCount = DecryptionCount,
                    TotalBytesEncrypted = TotalBytesEncrypted,
                    TotalBytesDecrypted = TotalBytesDecrypted,
                    UniqueKeysUsed = KeyAccessLog.Count,
                    LastKeyAccess = KeyAccessLog.Values.DefaultIfEmpty().Max()
                };
            }
        }

        #endregion

        #region Configuration Methods

        /// <summary>
        /// Configures the default key store for Direct mode.
        /// </summary>
        public virtual void SetDefaultKeyStore(IKeyStore keyStore)
        {
            DefaultKeyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        }

        /// <summary>
        /// Configures the default envelope key store for Envelope mode.
        /// </summary>
        public virtual void SetDefaultEnvelopeKeyStore(IEnvelopeKeyStore envelopeKeyStore, string kekKeyId)
        {
            DefaultEnvelopeKeyStore = envelopeKeyStore ?? throw new ArgumentNullException(nameof(envelopeKeyStore));
            DefaultKekKeyId = kekKeyId ?? throw new ArgumentNullException(nameof(kekKeyId));
        }

        /// <summary>
        /// Sets the default key management mode.
        /// </summary>
        public virtual void SetDefaultMode(KeyManagementMode mode)
        {
            DefaultKeyManagementMode = mode;
        }

        /// <summary>
        /// Sets the per-user configuration provider.
        /// </summary>
        public virtual void SetConfigProvider(IKeyManagementConfigProvider provider)
        {
            ConfigProvider = provider;
        }

        /// <summary>
        /// Sets the key store registry.
        /// </summary>
        public virtual void SetKeyStoreRegistry(IKeyStoreRegistry registry)
        {
            KeyStoreRegistry = registry;
        }

        #endregion

        #region Metadata

        public override string SubCategory => "Encryption";

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["EncryptionAlgorithm"] = AlgorithmId;
            metadata["KeySizeBytes"] = KeySizeBytes;
            metadata["IvSizeBytes"] = IvSizeBytes;
            metadata["TagSizeBytes"] = TagSizeBytes;
            metadata["SupportsEnvelopeMode"] = true;
            metadata["SupportsDirectMode"] = true;
            metadata["SupportsPerUserConfig"] = true;
            return metadata;
        }

        #endregion

        #region IDisposable

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                KeyAccessLog.Clear();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    /// <summary>
    /// Statistics for encryption plugin operations.
    /// </summary>
    public record EncryptionStatistics
    {
        public long EncryptionCount { get; init; }
        public long DecryptionCount { get; init; }
        public long TotalBytesEncrypted { get; init; }
        public long TotalBytesDecrypted { get; init; }
        public int UniqueKeysUsed { get; init; }
        public DateTime LastKeyAccess { get; init; }
    }

    #endregion

    /// <summary>
    /// Abstract base class for container/partition manager plugins.
    /// Provides storage-agnostic partition management.
    /// AI-native: Supports intelligent quota management and access suggestions.
    /// </summary>
    public abstract class ContainerManagerPluginBase : FeaturePluginBase, IContainerManager
    {
        /// <summary>
        /// Category is always OrchestrationProvider for container managers.
        /// </summary>
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        public abstract Task<ContainerInfo> CreateContainerAsync(
            ISecurityContext context,
            string containerId,
            ContainerOptions? options = null,
            CancellationToken ct = default);

        public abstract Task<ContainerInfo?> GetContainerAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public abstract IAsyncEnumerable<ContainerInfo> ListContainersAsync(
            ISecurityContext context,
            CancellationToken ct = default);

        public abstract Task DeleteContainerAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public abstract Task GrantAccessAsync(
            ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            ContainerAccessLevel level,
            CancellationToken ct = default);

        public abstract Task RevokeAccessAsync(
            ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            CancellationToken ct = default);

        public abstract Task<ContainerAccessLevel> GetAccessLevelAsync(
            ISecurityContext context,
            string containerId,
            string? userId = null,
            CancellationToken ct = default);

        public abstract IAsyncEnumerable<ContainerAccessEntry> ListAccessAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public virtual Task<ContainerQuota> GetQuotaAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ContainerQuota());
        }

        public virtual Task SetQuotaAsync(
            ISecurityContext adminContext,
            string containerId,
            ContainerQuota quota,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "ContainerManager";
            metadata["SupportsQuotas"] = true;
            metadata["SupportsAccessControl"] = true;
            metadata["StorageAgnostic"] = true;
            return metadata;
        }
    }
}
