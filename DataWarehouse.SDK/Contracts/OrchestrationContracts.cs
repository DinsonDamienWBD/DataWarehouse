using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;

using DataWarehouse.SDK.Contracts.Hierarchy;

namespace DataWarehouse.SDK.Contracts
{
    #region Operation Context

    /// <summary>
    /// Context for an operation being intercepted.
    /// </summary>
    public class OperationContext
    {
        /// <summary>
        /// Unique operation ID for tracing.
        /// </summary>
        public string OperationId { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Type of operation being performed.
        /// </summary>
        public OperationType OperationType { get; init; }

        /// <summary>
        /// Resource being operated on (e.g., blob ID, path).
        /// </summary>
        public string? Resource { get; init; }

        /// <summary>
        /// Principal performing the operation.
        /// </summary>
        public System.Security.Claims.ClaimsPrincipal? Principal { get; init; }

        /// <summary>
        /// Tenant ID for multi-tenant operations.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Source IP address.
        /// </summary>
        public string? SourceIP { get; init; }

        /// <summary>
        /// When the operation started.
        /// </summary>
        public DateTime StartedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Size of data being operated on (if applicable).
        /// </summary>
        public long? DataSize { get; init; }

        /// <summary>
        /// Additional context data added by interceptors.
        /// </summary>
        public ConcurrentDictionary<string, object> Data { get; } = new();

        /// <summary>
        /// Access to the plugin registry for inter-plugin communication.
        /// </summary>
        public IPluginRegistry? Registry { get; init; }
    }

    /// <summary>
    /// Types of kernel operations.
    /// </summary>
    public enum OperationType
    {
        Read,
        Write,
        Delete,
        Search,
        List,
        Metadata,
        Admin
    }

    /// <summary>
    /// Interface for the plugin registry, used for inter-plugin discovery.
    /// </summary>
    public interface IPluginRegistry
    {
        /// <summary>
        /// Gets a plugin of the specified type.
        /// </summary>
        T? Get<T>() where T : class, IPlugin;

        /// <summary>
        /// Gets all plugins of the specified type.
        /// </summary>
        IEnumerable<T> GetAll<T>() where T : class, IPlugin;

        /// <summary>
        /// Gets a plugin by ID.
        /// </summary>
        IPlugin? GetById(string pluginId);

        /// <summary>
        /// Checks if a plugin type is available.
        /// </summary>
        bool Has<T>() where T : class, IPlugin;
    }

    #endregion

    #region Indexable Content

    /// <summary>
    /// Content that can be indexed for search.
    /// </summary>
    public class IndexableContent
    {
        /// <summary>
        /// Object ID.
        /// </summary>
        public string ObjectId { get; init; } = string.Empty;

        /// <summary>
        /// Filename or path.
        /// </summary>
        public string? Filename { get; init; }

        /// <summary>
        /// Extracted text content.
        /// </summary>
        public string? TextContent { get; init; }

        /// <summary>
        /// Embeddings for semantic search.
        /// </summary>
        public float[]? Embeddings { get; init; }

        /// <summary>
        /// AI-generated summary.
        /// </summary>
        public string? Summary { get; init; }

        /// <summary>
        /// Metadata attributes.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }

        /// <summary>
        /// Content type (MIME type).
        /// </summary>
        public string? ContentType { get; init; }

        /// <summary>
        /// File size.
        /// </summary>
        public long? Size { get; init; }
    }

    #endregion

    #region Content Processing Interfaces

    /// <summary>
    /// Interface for content processors that extract, transform, or enrich content.
    /// Used in the fan-out write pattern.
    /// </summary>
    public interface IContentProcessor : IPlugin
    {
        /// <summary>
        /// Type of processing this processor does.
        /// </summary>
        ContentProcessingType ProcessingType { get; }

        /// <summary>
        /// Content types this processor can handle (e.g., "text/*", "application/pdf").
        /// Empty = all content types.
        /// </summary>
        string[] SupportedContentTypes { get; }

        /// <summary>
        /// Processes content and returns the result.
        /// </summary>
        Task<ContentProcessingResult> ProcessAsync(
            Stream content,
            string contentType,
            Dictionary<string, object>? context = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Types of content processing.
    /// </summary>
    public enum ContentProcessingType
    {
        /// <summary>
        /// Extract text from documents (PDF, Office, etc.).
        /// </summary>
        TextExtraction,

        /// <summary>
        /// Generate embeddings for semantic search.
        /// </summary>
        EmbeddingGeneration,

        /// <summary>
        /// Generate summaries using LLM.
        /// </summary>
        Summarization,

        /// <summary>
        /// Extract metadata (EXIF, document properties, etc.).
        /// </summary>
        MetadataExtraction,

        /// <summary>
        /// Generate thumbnails or previews.
        /// </summary>
        ThumbnailGeneration,

        /// <summary>
        /// Classify content (category, sensitivity, etc.).
        /// </summary>
        Classification,

        /// <summary>
        /// Extract entities (people, places, organizations).
        /// </summary>
        EntityExtraction
    }

    /// <summary>
    /// Result of content processing.
    /// </summary>
    public class ContentProcessingResult
    {
        /// <summary>
        /// Whether processing succeeded.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Type of processing performed.
        /// </summary>
        public ContentProcessingType ProcessingType { get; init; }

        /// <summary>
        /// Extracted text (for TextExtraction).
        /// </summary>
        public string? ExtractedText { get; init; }

        /// <summary>
        /// Generated embeddings (for EmbeddingGeneration).
        /// </summary>
        public float[]? Embeddings { get; init; }

        /// <summary>
        /// Generated summary (for Summarization).
        /// </summary>
        public string? Summary { get; init; }

        /// <summary>
        /// Extracted metadata (for MetadataExtraction).
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }

        /// <summary>
        /// Thumbnail data (for ThumbnailGeneration).
        /// </summary>
        public byte[]? Thumbnail { get; init; }

        /// <summary>
        /// Classification result (for Classification).
        /// </summary>
        public ContentClassification? Classification { get; init; }

        /// <summary>
        /// Extracted entities (for EntityExtraction).
        /// </summary>
        public IReadOnlyList<ExtractedEntity>? Entities { get; init; }

        /// <summary>
        /// Error message if failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Processing duration.
        /// </summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Content classification result.
    /// </summary>
    public class ContentClassification
    {
        public string Category { get; init; } = string.Empty;
        public double Confidence { get; init; }
        public string? Sensitivity { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
    }

    /// <summary>
    /// An extracted entity.
    /// </summary>
    public class ExtractedEntity
    {
        public string Type { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public double Confidence { get; init; }
        public int? StartPosition { get; init; }
        public int? EndPosition { get; init; }
    }

    #endregion

    #region Write Fan-Out Orchestration

    /// <summary>
    /// Interface for write fan-out orchestration.
    /// Handles parallel writes to multiple destinations.
    /// </summary>
    public interface IWriteFanOutOrchestrator : IPlugin
    {
        /// <summary>
        /// Performs a fan-out write to all registered destinations.
        /// </summary>
        Task<FanOutWriteResult> WriteAsync(
            string objectId,
            Stream data,
            Manifest manifest,
            FanOutWriteOptions? options = null,
            CancellationToken ct = default);

        /// <summary>
        /// Gets registered write destinations.
        /// </summary>
        IReadOnlyList<IWriteDestination> GetDestinations();

        /// <summary>
        /// Registers a write destination.
        /// </summary>
        void RegisterDestination(IWriteDestination destination);
    }

    /// <summary>
    /// Interface for a write destination in the fan-out pattern.
    /// </summary>
    public interface IWriteDestination : IPlugin
    {
        /// <summary>
        /// Type of destination.
        /// </summary>
        WriteDestinationType DestinationType { get; }

        /// <summary>
        /// Whether this destination is required for a successful write.
        /// </summary>
        bool IsRequired { get; }

        /// <summary>
        /// Priority (higher = more important).
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Writes to this destination.
        /// </summary>
        Task<WriteDestinationResult> WriteAsync(
            string objectId,
            IndexableContent content,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Types of write destinations.
    /// </summary>
    public enum WriteDestinationType
    {
        /// <summary>
        /// Primary blob/object storage.
        /// </summary>
        PrimaryStorage,

        /// <summary>
        /// Metadata storage (SQL, document DB).
        /// </summary>
        MetadataStorage,

        /// <summary>
        /// Full-text search index.
        /// </summary>
        TextIndex,

        /// <summary>
        /// Vector/embedding storage.
        /// </summary>
        VectorStore,

        /// <summary>
        /// NoSQL/document storage (for summaries, etc.).
        /// </summary>
        DocumentStore,

        /// <summary>
        /// Cache layer.
        /// </summary>
        Cache
    }

    /// <summary>
    /// Options for fan-out write.
    /// </summary>
    public class FanOutWriteOptions
    {
        /// <summary>
        /// Content types to process.
        /// </summary>
        public ContentProcessingType[] ProcessingTypes { get; init; } = new[]
        {
            ContentProcessingType.TextExtraction,
            ContentProcessingType.EmbeddingGeneration,
            ContentProcessingType.Summarization
        };

        /// <summary>
        /// Whether to wait for all destinations or just required ones.
        /// </summary>
        public bool WaitForAll { get; init; } = false;

        /// <summary>
        /// Timeout for non-required destinations.
        /// </summary>
        public TimeSpan NonRequiredTimeout { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Result of a fan-out write.
    /// </summary>
    public class FanOutWriteResult
    {
        /// <summary>
        /// Overall success (all required destinations succeeded).
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Object ID.
        /// </summary>
        public string ObjectId { get; init; } = string.Empty;

        /// <summary>
        /// Results per destination.
        /// </summary>
        public IReadOnlyDictionary<WriteDestinationType, WriteDestinationResult> DestinationResults { get; init; }
            = new Dictionary<WriteDestinationType, WriteDestinationResult>();

        /// <summary>
        /// Content processing results.
        /// </summary>
        public IReadOnlyDictionary<ContentProcessingType, ContentProcessingResult> ProcessingResults { get; init; }
            = new Dictionary<ContentProcessingType, ContentProcessingResult>();

        /// <summary>
        /// Total duration.
        /// </summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Result of writing to a single destination.
    /// </summary>
    public class WriteDestinationResult
    {
        public bool Success { get; init; }
        public WriteDestinationType DestinationType { get; init; }
        public TimeSpan Duration { get; init; }
        public string? ErrorMessage { get; init; }
    }

    #endregion

    #region Write Destination Base

    /// <summary>
    /// Abstract base class for write destination plugins.
    /// Provides common infrastructure for implementing IWriteDestination.
    /// </summary>
    public abstract class WriteDestinationPluginBase : InterfacePluginBase, IWriteDestination
    {
        /// <inheritdoc/>
        public override string Protocol => "WriteDestination";

        public override PluginCategory Category => PluginCategory.StorageProvider;

        /// <summary>
        /// Type of destination. Must be overridden.
        /// </summary>
        public abstract WriteDestinationType DestinationType { get; }

        /// <summary>
        /// Whether this destination is required for a successful write.
        /// Override to customize. Default is false for non-primary storage.
        /// </summary>
        public virtual bool IsRequired => DestinationType == WriteDestinationType.PrimaryStorage;

        /// <summary>
        /// Priority for write ordering (higher = writes first). Override to customize.
        /// </summary>
        public virtual int Priority => DestinationType switch
        {
            WriteDestinationType.PrimaryStorage => 100,
            WriteDestinationType.MetadataStorage => 90,
            WriteDestinationType.TextIndex => 50,
            WriteDestinationType.VectorStore => 50,
            WriteDestinationType.DocumentStore => 40,
            WriteDestinationType.Cache => 30,
            _ => 50
        };

        /// <summary>
        /// Writes to this destination. Must be implemented by derived classes.
        /// </summary>
        public abstract Task<WriteDestinationResult> WriteAsync(
            string objectId,
            IndexableContent content,
            CancellationToken ct = default);

        /// <summary>
        /// Creates a successful write result.
        /// </summary>
        protected WriteDestinationResult SuccessResult(TimeSpan? duration = null)
        {
            return new WriteDestinationResult
            {
                Success = true,
                DestinationType = DestinationType,
                Duration = duration ?? TimeSpan.Zero
            };
        }

        /// <summary>
        /// Creates a failure write result.
        /// </summary>
        protected WriteDestinationResult FailureResult(string errorMessage, TimeSpan? duration = null)
        {
            return new WriteDestinationResult
            {
                Success = false,
                DestinationType = DestinationType,
                ErrorMessage = errorMessage,
                Duration = duration ?? TimeSpan.Zero
            };
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["DestinationType"] = DestinationType.ToString();
            metadata["IsRequired"] = IsRequired;
            metadata["Priority"] = Priority;
            return metadata;
        }
    }

    #endregion
}
