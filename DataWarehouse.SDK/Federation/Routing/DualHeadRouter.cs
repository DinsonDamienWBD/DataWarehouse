using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Routing;

/// <summary>
/// Dual-head router that classifies storage requests and dispatches them to Object or FilePath pipelines.
/// </summary>
/// <remarks>
/// <para>
/// DualHeadRouter is the foundational entry point for federated object storage. It performs
/// three core operations:
/// <list type="number">
/// <item><description>Classify incoming requests using <see cref="IRequestClassifier"/></description></item>
/// <item><description>Select the appropriate pipeline (Object or FilePath) based on classification</description></item>
/// <item><description>Execute the pipeline and return the response with latency tracking</description></item>
/// </list>
/// </para>
/// <para>
/// The router maintains observability counters for routing decisions, enabling runtime monitoring
/// of Object vs FilePath classification distribution.
/// </para>
/// <para>
/// Future routing layers (permission-aware, location-aware, replication-aware) will build on top
/// of this classification, extending the pipelines with additional routing intelligence.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Dual-head router with language-based dispatch")]
public sealed class DualHeadRouter : IStorageRouter
{
    private readonly IRequestClassifier _classifier;
    private readonly RoutingPipeline _objectPipeline;
    private readonly RoutingPipeline _filePathPipeline;
    private readonly ConcurrentDictionary<RequestLanguage, long> _routingCounters;

    /// <summary>
    /// Initializes a new instance of the <see cref="DualHeadRouter"/> class.
    /// </summary>
    /// <param name="classifier">The request classifier for language detection.</param>
    /// <param name="objectPipeline">The pipeline for Object language requests.</param>
    /// <param name="filePathPipeline">The pipeline for FilePath language requests.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when pipelines do not match their expected languages.
    /// </exception>
    public DualHeadRouter(
        IRequestClassifier classifier,
        RoutingPipeline objectPipeline,
        RoutingPipeline filePathPipeline)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(objectPipeline);
        ArgumentNullException.ThrowIfNull(filePathPipeline);

        if (objectPipeline.Language != RequestLanguage.Object)
        {
            throw new ArgumentException(
                $"Object pipeline must handle Object language, but got {objectPipeline.Language}",
                nameof(objectPipeline));
        }

        if (filePathPipeline.Language != RequestLanguage.FilePath)
        {
            throw new ArgumentException(
                $"FilePath pipeline must handle FilePath language, but got {filePathPipeline.Language}",
                nameof(filePathPipeline));
        }

        _classifier = classifier;
        _objectPipeline = objectPipeline;
        _filePathPipeline = filePathPipeline;
        _routingCounters = new ConcurrentDictionary<RequestLanguage, long>();
    }

    /// <inheritdoc />
    public async Task<StorageResponse> RouteRequestAsync(StorageRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();

        try
        {
            // Classify request to determine language
            var language = _classifier.Classify(request);

            // Update routing metrics
            _routingCounters.AddOrUpdate(language, 1, (_, count) => count + 1);

            // Select the appropriate pipeline based on classification
            var pipeline = language switch
            {
                RequestLanguage.Object => _objectPipeline,
                RequestLanguage.FilePath => _filePathPipeline,
                _ => throw new InvalidOperationException($"Unknown request language: {language}")
            };

            // Execute the pipeline
            var response = await pipeline.ExecuteAsync(request, ct).ConfigureAwait(false);

            // Update response with accurate latency
            sw.Stop();
            return response with { Latency = sw.Elapsed };
        }
        catch (OperationCanceledException)
        {
            // Preserve cancellation exceptions without wrapping
            throw;
        }
        catch (Exception ex)
        {
            // Wrap all other exceptions in a failed StorageResponse
            sw.Stop();
            return new StorageResponse
            {
                Success = false,
                NodeId = "router",
                ErrorMessage = $"Routing failed: {ex.Message}",
                Latency = sw.Elapsed
            };
        }
    }

    /// <summary>
    /// Gets the routing counters for observability.
    /// </summary>
    /// <returns>
    /// A read-only dictionary mapping <see cref="RequestLanguage"/> to the count of requests
    /// routed through that language's pipeline.
    /// </returns>
    /// <remarks>
    /// Routing counters enable runtime monitoring of classification distribution. Use this
    /// for observability dashboards, alerting on skewed routing patterns, and capacity planning.
    /// </remarks>
    public IReadOnlyDictionary<RequestLanguage, long> GetRoutingCounters() => _routingCounters;
}
