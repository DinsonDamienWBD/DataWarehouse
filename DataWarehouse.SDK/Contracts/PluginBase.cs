using DataWarehouse.SDK.Governance;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System;

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
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args);

        /// <summary>
        /// Transform data during read operations (e.g., decrypt, decompress).
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SubCategory"] = SubCategory;
            metadata["QualityLevel"] = QualityLevel;
            metadata["SupportsStreaming"] = true;
            metadata["TransformationType"] = "Bidirectional";
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
    public abstract class AccessControlPluginBase : SecurityProviderPluginBase, Security.IAccessControl
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
            Security.ISecurityContext context,
            string containerId,
            ContainerOptions? options = null,
            CancellationToken ct = default);

        public abstract Task<ContainerInfo?> GetContainerAsync(
            Security.ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public abstract IAsyncEnumerable<ContainerInfo> ListContainersAsync(
            Security.ISecurityContext context,
            CancellationToken ct = default);

        public abstract Task DeleteContainerAsync(
            Security.ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public abstract Task GrantAccessAsync(
            Security.ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            ContainerAccessLevel level,
            CancellationToken ct = default);

        public abstract Task RevokeAccessAsync(
            Security.ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            CancellationToken ct = default);

        public abstract Task<ContainerAccessLevel> GetAccessLevelAsync(
            Security.ISecurityContext context,
            string containerId,
            string? userId = null,
            CancellationToken ct = default);

        public abstract IAsyncEnumerable<ContainerAccessEntry> ListAccessAsync(
            Security.ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public virtual Task<ContainerQuota> GetQuotaAsync(
            Security.ISecurityContext context,
            string containerId,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ContainerQuota());
        }

        public virtual Task SetQuotaAsync(
            Security.ISecurityContext adminContext,
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
