// NOTE: Live types (OperationContext, OperationType, IPluginRegistry, IndexableContent,
// IContentProcessor, ContentProcessingType, ContentProcessingResult, ContentClassification,
// ExtractedEntity, IWriteFanOutOrchestrator, IWriteDestination, WriteDestinationType,
// FanOutWriteOptions, FanOutWriteResult, WriteDestinationResult, WriteDestinationPluginBase)
// have been moved to OrchestrationContracts.cs.
// This file contains ONLY dead types -- to be deleted in Plan 02.

using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;

using DataWarehouse.SDK.Contracts.Hierarchy;

namespace DataWarehouse.SDK.Contracts
{
    #region Dead Interceptor Interfaces

    /// <summary>
    /// Interface for pre-operation interceptors. (Dead -- 0 external refs)
    /// </summary>
    public interface IPreOperationInterceptor : IPlugin
    {
        int Order { get; }
        bool IsRequired { get; }
        OperationType[] ApplicableOperations { get; }
        Task<InterceptorResult> OnBeforeOperationAsync(OperationContext context, CancellationToken ct = default);
    }

    /// <summary>
    /// Interface for post-operation interceptors. (Dead -- 0 external refs)
    /// </summary>
    public interface IPostOperationInterceptor : IPlugin
    {
        int Order { get; }
        OperationType[] ApplicableOperations { get; }
        Task OnAfterOperationAsync(OperationContext context, OperationResult result, CancellationToken ct = default);
    }

    /// <summary>
    /// Result of an interceptor execution. (Dead -- 0 external refs)
    /// </summary>
    public class InterceptorResult
    {
        public bool ShouldProceed { get; init; } = true;
        public string? DenialReason { get; init; }
        public Dictionary<string, object>? ContextData { get; init; }
        public static InterceptorResult Allow() => new() { ShouldProceed = true };
        public static InterceptorResult Deny(string reason) => new() { ShouldProceed = false, DenialReason = reason };
        public static InterceptorResult AllowWithContext(Dictionary<string, object> data) => new() { ShouldProceed = true, ContextData = data };
    }

    /// <summary>
    /// Result of an operation. (Dead -- 0 external refs)
    /// </summary>
    public class OperationResult
    {
        public bool Success { get; init; }
        public TimeSpan Duration { get; init; }
        public long? BytesProcessed { get; init; }
        public string? ErrorMessage { get; init; }
        public Exception? Exception { get; init; }
        public Dictionary<string, object>? ResultData { get; init; }
    }

    #endregion

    #region Dead Interceptor Base Classes

    /// <summary>
    /// Abstract base class for pre-operation interceptors. (Dead -- 0 external refs)
    /// </summary>
    public abstract class PreOperationInterceptorBase : PluginBase, IPreOperationInterceptor
    {
        public override PluginCategory Category => PluginCategory.SecurityProvider;
        public virtual int Order => 100;
        public virtual bool IsRequired => false;
        public virtual OperationType[] ApplicableOperations => Array.Empty<OperationType>();
        public abstract Task<InterceptorResult> OnBeforeOperationAsync(OperationContext context, CancellationToken ct = default);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["InterceptorType"] = "Pre";
            metadata["Order"] = Order;
            metadata["IsRequired"] = IsRequired;
            metadata["ApplicableOperations"] = ApplicableOperations.Length > 0 ? string.Join(",", ApplicableOperations) : "All";
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for post-operation interceptors. (Dead -- 0 external refs)
    /// </summary>
    public abstract class PostOperationInterceptorBase : PluginBase, IPostOperationInterceptor
    {
        public override PluginCategory Category => PluginCategory.SecurityProvider;
        public virtual int Order => 100;
        public virtual OperationType[] ApplicableOperations => Array.Empty<OperationType>();
        public abstract Task OnAfterOperationAsync(OperationContext context, OperationResult result, CancellationToken ct = default);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["InterceptorType"] = "Post";
            metadata["Order"] = Order;
            metadata["ApplicableOperations"] = ApplicableOperations.Length > 0 ? string.Join(",", ApplicableOperations) : "All";
            return metadata;
        }
    }

    #endregion

    #region Dead Search Interfaces

    public interface ISearchOrchestrator : IPlugin
    {
        Task<UnifiedSearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default);
        IReadOnlyList<ISearchProvider> GetAvailableProviders();
        void RegisterProvider(ISearchProvider provider);
    }

    public interface ISearchProvider : IPlugin
    {
        SearchType SearchType { get; }
        int Priority { get; }
        bool IsAvailable { get; }
        Task<SearchProviderResult> SearchAsync(SearchRequest request, CancellationToken ct = default);
        Task IndexAsync(string objectId, IndexableContent content, CancellationToken ct = default);
        Task RemoveFromIndexAsync(string objectId, CancellationToken ct = default);
    }

    public enum SearchType { Filename, Keyword, Semantic, Agent, Metadata }

    public class SearchRequest
    {
        public string Query { get; init; } = string.Empty;
        public int Limit { get; init; } = 50;
        public SearchType[] IncludeTypes { get; init; } = Array.Empty<SearchType>();
        public SearchType[] ExcludeTypes { get; init; } = Array.Empty<SearchType>();
        public Dictionary<string, object>? Filters { get; init; }
        public string? TenantId { get; init; }
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    }

    public class SearchProviderResult
    {
        public SearchType SearchType { get; init; }
        public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();
        public TimeSpan Duration { get; init; }
        public bool Success { get; init; } = true;
        public string? ErrorMessage { get; init; }
    }

    public class UnifiedSearchResult
    {
        public IReadOnlyList<SearchHit> Hits { get; init; } = Array.Empty<SearchHit>();
        public IReadOnlyDictionary<SearchType, SearchProviderResult> ByType { get; init; } = new Dictionary<SearchType, SearchProviderResult>();
        public TimeSpan Duration { get; init; }
        public int ProvidersQueried { get; init; }
        public int ProvidersSucceeded { get; init; }
    }

    public class SearchHit
    {
        public string ObjectId { get; init; } = string.Empty;
        public double Score { get; init; }
        public SearchType FoundBy { get; init; }
        public string? Snippet { get; init; }
        public IReadOnlyList<HighlightRange>? Highlights { get; init; }
        public Dictionary<string, object>? Metadata { get; init; }
    }

    public class HighlightRange
    {
        public int Start { get; init; }
        public int End { get; init; }
    }

    #endregion

    #region Dead Search Orchestrator Base

    public abstract class SearchOrchestratorPluginBase : OrchestrationPluginBase, ISearchOrchestrator
    {
        public override string OrchestrationMode => "Search";
        private readonly List<ISearchProvider> _providers = new();
        private readonly object _lock = new();
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        public void RegisterProvider(ISearchProvider provider) { lock (_lock) { _providers.Add(provider); } }
        public IReadOnlyList<ISearchProvider> GetAvailableProviders() { lock (_lock) { return _providers.Where(p => p.IsAvailable).ToList(); } }

        public virtual async Task<UnifiedSearchResult> SearchAsync(SearchRequest request, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var providers = GetProvidersForRequest(request);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(request.Timeout);

            var tasks = providers.Select(async p =>
            {
                try { return await p.SearchAsync(request, cts.Token); }
                catch (OperationCanceledException) { return new SearchProviderResult { SearchType = p.SearchType, Success = false, ErrorMessage = "Timeout" }; }
                catch (Exception ex) { return new SearchProviderResult { SearchType = p.SearchType, Success = false, ErrorMessage = ex.Message }; }
            });
            var results = await Task.WhenAll(tasks);
            sw.Stop();
            var byType = results.ToDictionary(r => r.SearchType, r => r);
            var mergedHits = MergeAndRankResults(results, request.Limit);
            return new UnifiedSearchResult { Hits = mergedHits, ByType = byType, Duration = sw.Elapsed, ProvidersQueried = providers.Count, ProvidersSucceeded = results.Count(r => r.Success) };
        }

        protected virtual IReadOnlyList<ISearchProvider> GetProvidersForRequest(SearchRequest request)
        {
            var available = GetAvailableProviders();
            if (request.IncludeTypes.Length > 0) available = available.Where(p => request.IncludeTypes.Contains(p.SearchType)).ToList();
            if (request.ExcludeTypes.Length > 0) available = available.Where(p => !request.ExcludeTypes.Contains(p.SearchType)).ToList();
            return available;
        }

        protected virtual IReadOnlyList<SearchHit> MergeAndRankResults(IEnumerable<SearchProviderResult> results, int limit)
        {
            return results.Where(r => r.Success).SelectMany(r => r.Hits.Select(h => new { Hit = h, ProviderPriority = GetProviderPriority(h.FoundBy) }))
                .GroupBy(x => x.Hit.ObjectId)
                .Select(g => { var best = g.OrderByDescending(x => x.Hit.Score * x.ProviderPriority).First(); return new SearchHit { ObjectId = best.Hit.ObjectId, Score = g.Sum(x => x.Hit.Score * x.ProviderPriority) / g.Count(), FoundBy = best.Hit.FoundBy, Snippet = best.Hit.Snippet, Highlights = best.Hit.Highlights, Metadata = best.Hit.Metadata }; })
                .OrderByDescending(h => h.Score).Take(limit).ToList();
        }

        protected virtual double GetProviderPriority(SearchType type) => type switch { SearchType.Agent => 1.5, SearchType.Semantic => 1.3, SearchType.Keyword => 1.0, SearchType.Filename => 0.8, SearchType.Metadata => 0.7, _ => 1.0 };

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

    #region Dead Search Provider Base

    public abstract class SearchProviderPluginBase : InterfacePluginBase, ISearchProvider
    {
        public override string Protocol => "Search";
        public override PluginCategory Category => PluginCategory.MetadataIndexingProvider;
        public abstract SearchType SearchType { get; }
        public virtual int Priority => SearchType switch { SearchType.Agent => 100, SearchType.Semantic => 80, SearchType.Keyword => 60, SearchType.Filename => 40, SearchType.Metadata => 30, _ => 50 };
        public virtual bool IsAvailable => true;
        public abstract Task<SearchProviderResult> SearchAsync(SearchRequest request, CancellationToken ct = default);
        public abstract Task IndexAsync(string objectId, IndexableContent content, CancellationToken ct = default);
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

    #region Dead Content Processor Base

    public abstract class ContentProcessorPluginBase : ComputePluginBase, IContentProcessor
    {
        public override string RuntimeType => "ContentProcessor";
        public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default) => Task.FromResult(new Dictionary<string, object> { ["status"] = "delegated-to-content-processor" });
        public override PluginCategory Category => PluginCategory.DataTransformationProvider;
        public abstract ContentProcessingType ProcessingType { get; }
        public virtual string[] SupportedContentTypes => Array.Empty<string>();
        public abstract Task<ContentProcessingResult> ProcessAsync(Stream content, string contentType, Dictionary<string, object>? context = null, CancellationToken ct = default);

        protected virtual bool SupportsContentType(string contentType)
        {
            if (SupportedContentTypes.Length == 0) return true;
            foreach (var supported in SupportedContentTypes)
            {
                if (supported == "*/*") return true;
                if (supported.EndsWith("/*")) { var category = supported[..^2]; if (contentType.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase)) return true; }
                else if (string.Equals(supported, contentType, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        protected ContentProcessingResult SuccessResult(string? extractedText = null, float[]? embeddings = null, string? summary = null, Dictionary<string, object>? metadata = null, byte[]? thumbnail = null, ContentClassification? classification = null, IReadOnlyList<ExtractedEntity>? entities = null, TimeSpan? duration = null)
        {
            return new ContentProcessingResult { Success = true, ProcessingType = ProcessingType, ExtractedText = extractedText, Embeddings = embeddings, Summary = summary, Metadata = metadata, Thumbnail = thumbnail, Classification = classification, Entities = entities, Duration = duration ?? TimeSpan.Zero };
        }

        protected ContentProcessingResult FailureResult(string errorMessage, TimeSpan? duration = null)
        {
            return new ContentProcessingResult { Success = false, ProcessingType = ProcessingType, ErrorMessage = errorMessage, Duration = duration ?? TimeSpan.Zero };
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["ProcessingType"] = ProcessingType.ToString();
            metadata["SupportedContentTypes"] = SupportedContentTypes.Length > 0 ? string.Join(", ", SupportedContentTypes) : "All";
            return metadata;
        }
    }

    #endregion

    #region Dead Write Fan-Out Orchestrator Base

    public abstract class WriteFanOutOrchestratorPluginBase : OrchestrationPluginBase, IWriteFanOutOrchestrator
    {
        public override string OrchestrationMode => "WriteFanOut";
        private readonly List<IWriteDestination> _destinations = new();
        private readonly object _lock = new();
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        public void RegisterDestination(IWriteDestination destination) { lock (_lock) { _destinations.Add(destination); } }
        public IReadOnlyList<IWriteDestination> GetDestinations() { lock (_lock) { return _destinations.ToList(); } }

        public virtual async Task<FanOutWriteResult> WriteAsync(string objectId, Stream data, Manifest manifest, FanOutWriteOptions? options = null, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            options ??= new FanOutWriteOptions();
            var destinations = GetDestinations();
            var requiredDestinations = destinations.Where(d => d.IsRequired).ToList();
            var optionalDestinations = destinations.Where(d => !d.IsRequired).ToList();
            var processingResults = await ProcessContentAsync(data, manifest, options, ct);
            var indexableContent = CreateIndexableContent(objectId, manifest, processingResults);
            if (data.CanSeek) data.Position = 0;
            var destinationResults = new ConcurrentDictionary<WriteDestinationType, WriteDestinationResult>();

            var requiredTasks = requiredDestinations.Select(async d => { var result = await WriteToDestinationAsync(d, objectId, indexableContent, ct); destinationResults[d.DestinationType] = result; return result; });
            var requiredResults = await Task.WhenAll(requiredTasks);
            var requiredSuccess = requiredResults.All(r => r.Success);

            if (optionalDestinations.Count > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(options.NonRequiredTimeout);
                var optionalTasks = optionalDestinations.Select(async d =>
                {
                    try { var result = await WriteToDestinationAsync(d, objectId, indexableContent, timeoutCts.Token); destinationResults[d.DestinationType] = result; }
                    catch (OperationCanceledException) { destinationResults[d.DestinationType] = new WriteDestinationResult { Success = false, DestinationType = d.DestinationType, ErrorMessage = "Timeout" }; }
                });
                if (options.WaitForAll) await Task.WhenAll(optionalTasks);
                else _ = Task.WhenAll(optionalTasks);
            }

            sw.Stop();
            return new FanOutWriteResult { Success = requiredSuccess, ObjectId = objectId, DestinationResults = destinationResults.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), ProcessingResults = processingResults.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), Duration = sw.Elapsed };
        }

        protected virtual async Task<Dictionary<ContentProcessingType, ContentProcessingResult>> ProcessContentAsync(Stream data, Manifest manifest, FanOutWriteOptions options, CancellationToken ct)
        {
            return new Dictionary<ContentProcessingType, ContentProcessingResult>();
        }

        protected virtual IndexableContent CreateIndexableContent(string objectId, Manifest manifest, Dictionary<ContentProcessingType, ContentProcessingResult> processingResults)
        {
            return new IndexableContent
            {
                ObjectId = objectId, Filename = manifest.Name, ContentType = manifest.ContentType, Size = manifest.OriginalSize,
                Metadata = manifest.Metadata?.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value),
                TextContent = processingResults.TryGetValue(ContentProcessingType.TextExtraction, out var textResult) ? textResult.ExtractedText : null,
                Embeddings = processingResults.TryGetValue(ContentProcessingType.EmbeddingGeneration, out var embResult) ? embResult.Embeddings : null,
                Summary = processingResults.TryGetValue(ContentProcessingType.Summarization, out var sumResult) ? sumResult.Summary : null
            };
        }

        private async Task<WriteDestinationResult> WriteToDestinationAsync(IWriteDestination destination, string objectId, IndexableContent content, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { var result = await destination.WriteAsync(objectId, content, ct); sw.Stop(); return new WriteDestinationResult { Success = result.Success, DestinationType = destination.DestinationType, Duration = sw.Elapsed, ErrorMessage = result.ErrorMessage }; }
            catch (Exception ex) { sw.Stop(); return new WriteDestinationResult { Success = false, DestinationType = destination.DestinationType, Duration = sw.Elapsed, ErrorMessage = ex.Message }; }
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
}
