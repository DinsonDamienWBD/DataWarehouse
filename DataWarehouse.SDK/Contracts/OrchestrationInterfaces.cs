using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Contracts
{
    #region Interceptor Interfaces

    /// <summary>
    /// Interface for pre-operation interceptors.
    /// Called before any kernel operation (read, write, delete, search).
    /// Examples: IAM, rate limiting, threat detection, audit logging.
    /// </summary>
    public interface IPreOperationInterceptor : IPlugin
    {
        /// <summary>
        /// Execution order. Lower values execute first.
        /// Recommended ranges:
        /// - 0-99: Authentication/Identity
        /// - 100-199: Authorization/Access Control
        /// - 200-299: Rate Limiting/Throttling
        /// - 300-399: Validation/Threat Detection
        /// - 400-499: Audit Logging
        /// - 500+: Custom
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Whether this interceptor is required. If true and it fails,
        /// the operation is aborted. If false, failure is logged but operation continues.
        /// </summary>
        bool IsRequired { get; }

        /// <summary>
        /// Operation types this interceptor applies to.
        /// Empty = all operations.
        /// </summary>
        OperationType[] ApplicableOperations { get; }

        /// <summary>
        /// Called before the operation executes.
        /// Throw an exception to abort the operation.
        /// </summary>
        Task<InterceptorResult> OnBeforeOperationAsync(
            OperationContext context,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for post-operation interceptors.
    /// Called after any kernel operation completes (success or failure).
    /// Examples: Audit logging, metrics, event publishing.
    /// </summary>
    public interface IPostOperationInterceptor : IPlugin
    {
        /// <summary>
        /// Execution order. Lower values execute first.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Operation types this interceptor applies to.
        /// Empty = all operations.
        /// </summary>
        OperationType[] ApplicableOperations { get; }

        /// <summary>
        /// Called after the operation completes.
        /// Exceptions are logged but don't affect the operation result.
        /// </summary>
        Task OnAfterOperationAsync(
            OperationContext context,
            OperationResult result,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Result of an interceptor execution.
    /// </summary>
    public class InterceptorResult
    {
        /// <summary>
        /// Whether the operation should proceed.
        /// </summary>
        public bool ShouldProceed { get; init; } = true;

        /// <summary>
        /// If ShouldProceed is false, the reason why.
        /// </summary>
        public string? DenialReason { get; init; }

        /// <summary>
        /// Optional data to add to the operation context.
        /// </summary>
        public Dictionary<string, object>? ContextData { get; init; }

        /// <summary>
        /// Allow the operation to proceed.
        /// </summary>
        public static InterceptorResult Allow() => new() { ShouldProceed = true };

        /// <summary>
        /// Deny the operation with a reason.
        /// </summary>
        public static InterceptorResult Deny(string reason) => new()
        {
            ShouldProceed = false,
            DenialReason = reason
        };

        /// <summary>
        /// Allow with additional context data.
        /// </summary>
        public static InterceptorResult AllowWithContext(Dictionary<string, object> data) => new()
        {
            ShouldProceed = true,
            ContextData = data
        };
    }

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
    /// Result of an operation.
    /// </summary>
    public class OperationResult
    {
        /// <summary>
        /// Whether the operation succeeded.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Operation duration.
        /// </summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Bytes processed (if applicable).
        /// </summary>
        public long? BytesProcessed { get; init; }

        /// <summary>
        /// Error message if failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Exception if failed.
        /// </summary>
        public Exception? Exception { get; init; }

        /// <summary>
        /// Result data (e.g., manifest ID for writes).
        /// </summary>
        public Dictionary<string, object>? ResultData { get; init; }
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

    #region Interceptor Base Classes

    /// <summary>
    /// Abstract base class for pre-operation interceptors.
    /// </summary>
    public abstract class PreOperationInterceptorBase : PluginBase, IPreOperationInterceptor
    {
        public override PluginCategory Category => PluginCategory.SecurityProvider;

        /// <summary>
        /// Execution order. Override to customize.
        /// </summary>
        public virtual int Order => 100;

        /// <summary>
        /// Whether this interceptor is required. Override to customize.
        /// </summary>
        public virtual bool IsRequired => false;

        /// <summary>
        /// Operations this interceptor applies to. Override to customize.
        /// Empty = all operations.
        /// </summary>
        public virtual OperationType[] ApplicableOperations => Array.Empty<OperationType>();

        public abstract Task<InterceptorResult> OnBeforeOperationAsync(
            OperationContext context,
            CancellationToken ct = default);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["InterceptorType"] = "Pre";
            metadata["Order"] = Order;
            metadata["IsRequired"] = IsRequired;
            metadata["ApplicableOperations"] = ApplicableOperations.Length > 0
                ? string.Join(",", ApplicableOperations)
                : "All";
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for post-operation interceptors.
    /// </summary>
    public abstract class PostOperationInterceptorBase : PluginBase, IPostOperationInterceptor
    {
        public override PluginCategory Category => PluginCategory.SecurityProvider;

        /// <summary>
        /// Execution order. Override to customize.
        /// </summary>
        public virtual int Order => 100;

        /// <summary>
        /// Operations this interceptor applies to. Override to customize.
        /// Empty = all operations.
        /// </summary>
        public virtual OperationType[] ApplicableOperations => Array.Empty<OperationType>();

        public abstract Task OnAfterOperationAsync(
            OperationContext context,
            OperationResult result,
            CancellationToken ct = default);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["InterceptorType"] = "Post";
            metadata["Order"] = Order;
            metadata["ApplicableOperations"] = ApplicableOperations.Length > 0
                ? string.Join(",", ApplicableOperations)
                : "All";
            return metadata;
        }
    }

    #endregion

    #region Search Orchestration Interfaces

    /// <summary>
    /// Interface for search orchestrators that fan out to multiple search providers.
    /// Enables unified search across filename, keyword, semantic, and agent search.
    /// </summary>
    public interface ISearchOrchestrator : IPlugin
    {
        /// <summary>
        /// Performs a unified search across all available search providers.
        /// </summary>
        Task<UnifiedSearchResult> SearchAsync(
            SearchRequest request,
            CancellationToken ct = default);

        /// <summary>
        /// Gets available search providers.
        /// </summary>
        IReadOnlyList<ISearchProvider> GetAvailableProviders();

        /// <summary>
        /// Registers a search provider.
        /// </summary>
        void RegisterProvider(ISearchProvider provider);
    }

    /// <summary>
    /// Interface for individual search providers.
    /// Each provider handles a specific type of search.
    /// </summary>
    public interface ISearchProvider : IPlugin
    {
        /// <summary>
        /// Type of search this provider handles.
        /// </summary>
        SearchType SearchType { get; }

        /// <summary>
        /// Priority for result ranking (higher = more important).
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Whether this provider is available (e.g., has index data).
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Performs the search.
        /// </summary>
        Task<SearchProviderResult> SearchAsync(
            SearchRequest request,
            CancellationToken ct = default);

        /// <summary>
        /// Indexes content for later search.
        /// </summary>
        Task IndexAsync(
            string objectId,
            IndexableContent content,
            CancellationToken ct = default);

        /// <summary>
        /// Removes content from the index.
        /// </summary>
        Task RemoveFromIndexAsync(string objectId, CancellationToken ct = default);
    }

    /// <summary>
    /// Types of search.
    /// </summary>
    public enum SearchType
    {
        /// <summary>
        /// Filename/path pattern matching.
        /// </summary>
        Filename,

        /// <summary>
        /// Full-text keyword search.
        /// </summary>
        Keyword,

        /// <summary>
        /// Semantic/vector similarity search.
        /// </summary>
        Semantic,

        /// <summary>
        /// AI agent-powered search with reasoning.
        /// </summary>
        Agent,

        /// <summary>
        /// Metadata attribute search.
        /// </summary>
        Metadata
    }

    /// <summary>
    /// Search request.
    /// </summary>
    public class SearchRequest
    {
        /// <summary>
        /// The search query.
        /// </summary>
        public string Query { get; init; } = string.Empty;

        /// <summary>
        /// Maximum results to return.
        /// </summary>
        public int Limit { get; init; } = 50;

        /// <summary>
        /// Search types to include. Empty = all available.
        /// </summary>
        public SearchType[] IncludeTypes { get; init; } = Array.Empty<SearchType>();

        /// <summary>
        /// Search types to exclude.
        /// </summary>
        public SearchType[] ExcludeTypes { get; init; } = Array.Empty<SearchType>();

        /// <summary>
        /// Filters to apply.
        /// </summary>
        public Dictionary<string, object>? Filters { get; init; }

        /// <summary>
        /// Tenant ID for multi-tenant search.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Maximum time to wait for slow providers.
        /// </summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Result from a single search provider.
    /// </summary>
    public class SearchProviderResult
    {
        /// <summary>
        /// Type of search performed.
        /// </summary>
        public SearchType SearchType { get; init; }

        /// <summary>
        /// Search results.
        /// </summary>
        public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();

        /// <summary>
        /// Time taken by this provider.
        /// </summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Whether the search completed successfully.
        /// </summary>
        public bool Success { get; init; } = true;

        /// <summary>
        /// Error message if failed.
        /// </summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Unified search result combining all providers.
    /// </summary>
    public class UnifiedSearchResult
    {
        /// <summary>
        /// Merged and ranked results.
        /// </summary>
        public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();

        /// <summary>
        /// Results grouped by search type.
        /// </summary>
        public IReadOnlyDictionary<SearchType, SearchProviderResult> ByType { get; init; }
            = new Dictionary<SearchType, SearchProviderResult>();

        /// <summary>
        /// Total time for the search.
        /// </summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Number of providers that responded.
        /// </summary>
        public int ProvidersQueried { get; init; }

        /// <summary>
        /// Number of providers that succeeded.
        /// </summary>
        public int ProvidersSucceeded { get; init; }
    }

    /// <summary>
    /// A single search hit.
    /// </summary>
    public class SearchHit
    {
        /// <summary>
        /// Object ID.
        /// </summary>
        public string ObjectId { get; init; } = string.Empty;

        /// <summary>
        /// Relevance score (0-1).
        /// </summary>
        public double Score { get; init; }

        /// <summary>
        /// Which search type found this result.
        /// </summary>
        public SearchType FoundBy { get; init; }

        /// <summary>
        /// Snippet or preview of the match.
        /// </summary>
        public string? Snippet { get; init; }

        /// <summary>
        /// Highlight positions in the content.
        /// </summary>
        public IReadOnlyList<HighlightRange>? Highlights { get; init; }

        /// <summary>
        /// Additional metadata.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }

    /// <summary>
    /// A highlight range in content.
    /// </summary>
    public class HighlightRange
    {
        public int Start { get; init; }
        public int End { get; init; }
    }

    /// <summary>
    /// Content to be indexed.
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

    #region Search Orchestrator Base Class

    /// <summary>
    /// Abstract base class for search orchestrators.
    /// Provides parallel fan-out search with result merging.
    /// </summary>
    public abstract class SearchOrchestratorPluginBase : FeaturePluginBase, ISearchOrchestrator
    {
        private readonly List<ISearchProvider> _providers = new();
        private readonly object _lock = new();

        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        public void RegisterProvider(ISearchProvider provider)
        {
            lock (_lock)
            {
                _providers.Add(provider);
            }
        }

        public IReadOnlyList<ISearchProvider> GetAvailableProviders()
        {
            lock (_lock)
            {
                return _providers.Where(p => p.IsAvailable).ToList();
            }
        }

        public virtual async Task<UnifiedSearchResult> SearchAsync(
            SearchRequest request,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var providers = GetProvidersForRequest(request);

            // Fan out to all providers in parallel
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(request.Timeout);

            var tasks = providers.Select(async p =>
            {
                try
                {
                    return await p.SearchAsync(request, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return new SearchProviderResult
                    {
                        SearchType = p.SearchType,
                        Success = false,
                        ErrorMessage = "Timeout"
                    };
                }
                catch (Exception ex)
                {
                    return new SearchProviderResult
                    {
                        SearchType = p.SearchType,
                        Success = false,
                        ErrorMessage = ex.Message
                    };
                }
            });

            var results = await Task.WhenAll(tasks);
            sw.Stop();

            // Merge and rank results
            var byType = results.ToDictionary(r => r.SearchType, r => r);
            var mergedHits = MergeAndRankResults(results, request.Limit);

            return new UnifiedSearchResult
            {
                Hits = mergedHits,
                ByType = byType,
                Duration = sw.Elapsed,
                ProvidersQueried = providers.Count,
                ProvidersSucceeded = results.Count(r => r.Success)
            };
        }

        /// <summary>
        /// Gets providers applicable to the request.
        /// </summary>
        protected virtual IReadOnlyList<ISearchProvider> GetProvidersForRequest(SearchRequest request)
        {
            var available = GetAvailableProviders();

            if (request.IncludeTypes.Length > 0)
            {
                available = available.Where(p => request.IncludeTypes.Contains(p.SearchType)).ToList();
            }

            if (request.ExcludeTypes.Length > 0)
            {
                available = available.Where(p => !request.ExcludeTypes.Contains(p.SearchType)).ToList();
            }

            return available;
        }

        /// <summary>
        /// Merges and ranks results from multiple providers.
        /// Override to customize ranking algorithm.
        /// </summary>
        protected virtual IReadOnlyList<SearchHit> MergeAndRankResults(
            IEnumerable<SearchProviderResult> results,
            int limit)
        {
            return results
                .Where(r => r.Success)
                .SelectMany(r => r.Hits.Select(h => new
                {
                    Hit = h,
                    ProviderPriority = GetProviderPriority(h.FoundBy)
                }))
                .GroupBy(x => x.Hit.ObjectId)
                .Select(g =>
                {
                    var best = g.OrderByDescending(x => x.Hit.Score * x.ProviderPriority).First();
                    return new SearchHit
                    {
                        ObjectId = best.Hit.ObjectId,
                        Score = g.Sum(x => x.Hit.Score * x.ProviderPriority) / g.Count(),
                        FoundBy = best.Hit.FoundBy,
                        Snippet = best.Hit.Snippet,
                        Highlights = best.Hit.Highlights,
                        Metadata = best.Hit.Metadata
                    };
                })
                .OrderByDescending(h => h.Score)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// Gets priority weight for a search type. Override to customize.
        /// </summary>
        protected virtual double GetProviderPriority(SearchType type) => type switch
        {
            SearchType.Agent => 1.5,      // AI results weighted higher
            SearchType.Semantic => 1.3,   // Semantic search weighted high
            SearchType.Keyword => 1.0,    // Standard weight
            SearchType.Filename => 0.8,   // Filename match weighted lower
            SearchType.Metadata => 0.7,   // Metadata match weighted lower
            _ => 1.0
        };

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "SearchOrchestrator";
            metadata["RegisteredProviders"] = _providers.Count;
            metadata["AvailableProviders"] = GetAvailableProviders().Count;
            return metadata;
        }
    }

    #endregion

    #region Search Provider Base Class

    /// <summary>
    /// Abstract base class for search providers.
    /// Provides common functionality for search indexing and querying.
    /// </summary>
    public abstract class SearchProviderPluginBase : FeaturePluginBase, ISearchProvider
    {
        public override PluginCategory Category => PluginCategory.MetadataIndexingProvider;

        /// <summary>
        /// Type of search this provider handles. Must be overridden.
        /// </summary>
        public abstract SearchType SearchType { get; }

        /// <summary>
        /// Priority for result ranking (higher = more important). Override to customize.
        /// </summary>
        public virtual int Priority => SearchType switch
        {
            SearchType.Agent => 100,
            SearchType.Semantic => 80,
            SearchType.Keyword => 60,
            SearchType.Filename => 40,
            SearchType.Metadata => 30,
            _ => 50
        };

        /// <summary>
        /// Whether this provider is available. Override to implement availability check.
        /// </summary>
        public virtual bool IsAvailable => true;

        /// <summary>
        /// Performs the search. Must be implemented by derived classes.
        /// </summary>
        public abstract Task<SearchProviderResult> SearchAsync(
            SearchRequest request,
            CancellationToken ct = default);

        /// <summary>
        /// Indexes content for later search. Must be implemented by derived classes.
        /// </summary>
        public abstract Task IndexAsync(
            string objectId,
            IndexableContent content,
            CancellationToken ct = default);

        /// <summary>
        /// Removes content from the index. Must be implemented by derived classes.
        /// </summary>
        public abstract Task RemoveFromIndexAsync(string objectId, CancellationToken ct = default);

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SearchType"] = SearchType.ToString();
            metadata["Priority"] = Priority;
            metadata["IsAvailable"] = IsAvailable;
            return metadata;
        }
    }

    #endregion

    #region Content Processor Base Class

    /// <summary>
    /// Abstract base class for content processors.
    /// Provides common functionality for text extraction, embedding generation, etc.
    /// </summary>
    public abstract class ContentProcessorPluginBase : FeaturePluginBase, IContentProcessor
    {
        public override PluginCategory Category => PluginCategory.DataTransformationProvider;

        /// <summary>
        /// Type of processing this processor does. Must be overridden.
        /// </summary>
        public abstract ContentProcessingType ProcessingType { get; }

        /// <summary>
        /// Content types this processor can handle.
        /// Override to restrict to specific types. Empty = all types.
        /// </summary>
        public virtual string[] SupportedContentTypes => Array.Empty<string>();

        /// <summary>
        /// Processes content. Must be implemented by derived classes.
        /// </summary>
        public abstract Task<ContentProcessingResult> ProcessAsync(
            Stream content,
            string contentType,
            Dictionary<string, object>? context = null,
            CancellationToken ct = default);

        /// <summary>
        /// Checks if this processor supports the given content type.
        /// </summary>
        protected virtual bool SupportsContentType(string contentType)
        {
            if (SupportedContentTypes.Length == 0)
                return true;

            foreach (var supported in SupportedContentTypes)
            {
                if (supported == "*/*")
                    return true;

                if (supported.EndsWith("/*"))
                {
                    var category = supported[..^2];
                    if (contentType.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (string.Equals(supported, contentType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Creates a successful result with the specified data.
        /// </summary>
        protected ContentProcessingResult SuccessResult(
            string? extractedText = null,
            float[]? embeddings = null,
            string? summary = null,
            Dictionary<string, object>? metadata = null,
            byte[]? thumbnail = null,
            ContentClassification? classification = null,
            IReadOnlyList<ExtractedEntity>? entities = null,
            TimeSpan? duration = null)
        {
            return new ContentProcessingResult
            {
                Success = true,
                ProcessingType = ProcessingType,
                ExtractedText = extractedText,
                Embeddings = embeddings,
                Summary = summary,
                Metadata = metadata,
                Thumbnail = thumbnail,
                Classification = classification,
                Entities = entities,
                Duration = duration ?? TimeSpan.Zero
            };
        }

        /// <summary>
        /// Creates a failure result with the specified error.
        /// </summary>
        protected ContentProcessingResult FailureResult(string errorMessage, TimeSpan? duration = null)
        {
            return new ContentProcessingResult
            {
                Success = false,
                ProcessingType = ProcessingType,
                ErrorMessage = errorMessage,
                Duration = duration ?? TimeSpan.Zero
            };
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ProcessingType"] = ProcessingType.ToString();
            metadata["SupportedContentTypes"] = SupportedContentTypes.Length > 0
                ? string.Join(", ", SupportedContentTypes)
                : "All";
            return metadata;
        }
    }

    #endregion

    #region Write Fan-Out Orchestrator Base Class

    /// <summary>
    /// Abstract base class for write fan-out orchestrators.
    /// Provides parallel write coordination to multiple destinations.
    /// </summary>
    public abstract class WriteFanOutOrchestratorPluginBase : FeaturePluginBase, IWriteFanOutOrchestrator
    {
        private readonly List<IWriteDestination> _destinations = new();
        private readonly object _lock = new();

        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        /// <summary>
        /// Registers a write destination.
        /// </summary>
        public void RegisterDestination(IWriteDestination destination)
        {
            lock (_lock)
            {
                _destinations.Add(destination);
            }
        }

        /// <summary>
        /// Gets all registered destinations.
        /// </summary>
        public IReadOnlyList<IWriteDestination> GetDestinations()
        {
            lock (_lock)
            {
                return _destinations.ToList();
            }
        }

        /// <summary>
        /// Performs a fan-out write to all registered destinations.
        /// </summary>
        public virtual async Task<FanOutWriteResult> WriteAsync(
            string objectId,
            Stream data,
            Manifest manifest,
            FanOutWriteOptions? options = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            options ??= new FanOutWriteOptions();

            var destinations = GetDestinations();
            var requiredDestinations = destinations.Where(d => d.IsRequired).ToList();
            var optionalDestinations = destinations.Where(d => !d.IsRequired).ToList();

            // Process content in parallel to get indexable content
            var processingResults = await ProcessContentAsync(data, manifest, options, ct);

            // Create indexable content from processing results
            var indexableContent = CreateIndexableContent(objectId, manifest, processingResults);

            // Reset stream position for primary storage
            if (data.CanSeek)
                data.Position = 0;

            // Write to all destinations in parallel
            var destinationResults = new ConcurrentDictionary<WriteDestinationType, WriteDestinationResult>();

            // Required destinations - must all succeed
            var requiredTasks = requiredDestinations.Select(async d =>
            {
                var result = await WriteToDestinationAsync(d, objectId, indexableContent, ct);
                destinationResults[d.DestinationType] = result;
                return result;
            });

            var requiredResults = await Task.WhenAll(requiredTasks);
            var requiredSuccess = requiredResults.All(r => r.Success);

            // Optional destinations - fire and forget with timeout
            if (optionalDestinations.Count > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(options.NonRequiredTimeout);

                var optionalTasks = optionalDestinations.Select(async d =>
                {
                    try
                    {
                        var result = await WriteToDestinationAsync(d, objectId, indexableContent, timeoutCts.Token);
                        destinationResults[d.DestinationType] = result;
                    }
                    catch (OperationCanceledException)
                    {
                        destinationResults[d.DestinationType] = new WriteDestinationResult
                        {
                            Success = false,
                            DestinationType = d.DestinationType,
                            ErrorMessage = "Timeout"
                        };
                    }
                });

                if (options.WaitForAll)
                {
                    await Task.WhenAll(optionalTasks);
                }
                else
                {
                    // Fire and forget
                    _ = Task.WhenAll(optionalTasks);
                }
            }

            sw.Stop();

            return new FanOutWriteResult
            {
                Success = requiredSuccess,
                ObjectId = objectId,
                DestinationResults = destinationResults.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                ProcessingResults = processingResults.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                Duration = sw.Elapsed
            };
        }

        /// <summary>
        /// Processes content through available content processors.
        /// Override to customize processing behavior.
        /// </summary>
        protected virtual async Task<Dictionary<ContentProcessingType, ContentProcessingResult>> ProcessContentAsync(
            Stream data,
            Manifest manifest,
            FanOutWriteOptions options,
            CancellationToken ct)
        {
            var results = new Dictionary<ContentProcessingType, ContentProcessingResult>();
            // Default: no processing. Override to add content processors.
            return results;
        }

        /// <summary>
        /// Creates indexable content from manifest and processing results.
        /// Override to customize content creation.
        /// </summary>
        protected virtual IndexableContent CreateIndexableContent(
            string objectId,
            Manifest manifest,
            Dictionary<ContentProcessingType, ContentProcessingResult> processingResults)
        {
            return new IndexableContent
            {
                ObjectId = objectId,
                Filename = manifest.Name,
                ContentType = manifest.ContentType,
                Size = manifest.OriginalSize,
                Metadata = manifest.Metadata?.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value),
                TextContent = processingResults.TryGetValue(ContentProcessingType.TextExtraction, out var textResult)
                    ? textResult.ExtractedText
                    : null,
                Embeddings = processingResults.TryGetValue(ContentProcessingType.EmbeddingGeneration, out var embResult)
                    ? embResult.Embeddings
                    : null,
                Summary = processingResults.TryGetValue(ContentProcessingType.Summarization, out var sumResult)
                    ? sumResult.Summary
                    : null
            };
        }

        /// <summary>
        /// Writes to a single destination with error handling.
        /// </summary>
        private async Task<WriteDestinationResult> WriteToDestinationAsync(
            IWriteDestination destination,
            string objectId,
            IndexableContent content,
            CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await destination.WriteAsync(objectId, content, ct);
                sw.Stop();
                return new WriteDestinationResult
                {
                    Success = result.Success,
                    DestinationType = destination.DestinationType,
                    Duration = sw.Elapsed,
                    ErrorMessage = result.ErrorMessage
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new WriteDestinationResult
                {
                    Success = false,
                    DestinationType = destination.DestinationType,
                    Duration = sw.Elapsed,
                    ErrorMessage = ex.Message
                };
            }
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "WriteFanOutOrchestrator";
            metadata["DestinationCount"] = _destinations.Count;
            return metadata;
        }
    }

    #endregion

    #region Write Destination Base Class

    /// <summary>
    /// Abstract base class for write destinations.
    /// Provides common functionality for destination-specific writes.
    /// </summary>
    public abstract class WriteDestinationPluginBase : FeaturePluginBase, IWriteDestination
    {
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
