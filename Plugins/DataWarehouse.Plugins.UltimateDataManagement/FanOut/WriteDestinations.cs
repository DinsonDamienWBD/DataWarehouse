using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateDataManagement.Strategies.Caching;
using DataWarehouse.Plugins.UltimateDataManagement.Strategies.Indexing;

namespace DataWarehouse.Plugins.UltimateDataManagement.FanOut;

/// <summary>
/// Primary storage write destination.
/// Writes to main blob storage via T97 message bus integration.
/// </summary>
public sealed class PrimaryStorageDestination : WriteDestinationPluginBase
{
    private readonly IMessageBus? _messageBus;

    /// <summary>
    /// Initializes a new PrimaryStorageDestination.
    /// </summary>
    public PrimaryStorageDestination(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.destination.primary";

    /// <inheritdoc/>
    public override string Name => "Primary Storage Destination";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override WriteDestinationType DestinationType => WriteDestinationType.PrimaryStorage;

    /// <inheritdoc/>
    public override bool IsRequired => true;

    /// <inheritdoc/>
    public override int Priority => 100;

    /// <inheritdoc/>
    public override async Task<WriteDestinationResult> WriteAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (_messageBus == null)
        {
            sw.Stop();
            return FailureResult("Message bus unavailable - primary storage write requires T97 connection", sw.Elapsed);
        }

        try
        {
            var msgResponse = await _messageBus.SendAsync(
                "storage.write",
                new PluginMessage
                {
                    Type = "storage.write",
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["objectId"] = objectId,
                        ["filename"] = content.Filename ?? objectId,
                        ["contentType"] = content.ContentType ?? "application/octet-stream",
                        ["size"] = content.Size ?? 0
                    }
                },
                TimeSpan.FromSeconds(30),
                ct);

            sw.Stop();

            if (msgResponse?.Success == true)
            {
                return SuccessResult(sw.Elapsed);
            }

            return FailureResult(msgResponse?.ErrorMessage ?? "Storage write failed", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailureResult(ex.Message, sw.Elapsed);
        }
    }

}

/// <summary>
/// Metadata storage write destination.
/// Writes structured metadata to database via message bus.
/// </summary>
public sealed class MetadataStorageDestination : WriteDestinationPluginBase
{
    private readonly IMessageBus? _messageBus;

    /// <summary>
    /// Initializes a new MetadataStorageDestination.
    /// </summary>
    public MetadataStorageDestination(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.destination.metadata";

    /// <inheritdoc/>
    public override string Name => "Metadata Storage Destination";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override WriteDestinationType DestinationType => WriteDestinationType.MetadataStorage;

    /// <inheritdoc/>
    public override bool IsRequired => true;

    /// <inheritdoc/>
    public override int Priority => 90;

    /// <inheritdoc/>
    public override async Task<WriteDestinationResult> WriteAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (_messageBus == null)
        {
            sw.Stop();
            return FailureResult("Message bus unavailable - metadata storage write requires T97 connection", sw.Elapsed);
        }

        try
        {
            var metadata = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["filename"] = content.Filename ?? "",
                ["contentType"] = content.ContentType ?? "",
                ["size"] = content.Size ?? 0
            };

            if (content.Metadata != null)
            {
                foreach (var (key, value) in content.Metadata)
                {
                    metadata[key] = value;
                }
            }

            var msgResponse = await _messageBus.SendAsync(
                "metadata.write",
                new PluginMessage
                {
                    Type = "metadata.write",
                    Source = Id,
                    Payload = metadata
                },
                TimeSpan.FromSeconds(10),
                ct);

            sw.Stop();

            if (msgResponse?.Success == true)
            {
                return SuccessResult(sw.Elapsed);
            }

            return FailureResult(msgResponse?.ErrorMessage ?? "Metadata write failed", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailureResult(ex.Message, sw.Elapsed);
        }
    }
}

/// <summary>
/// Full-text index write destination.
/// Writes to full-text search index for keyword search.
/// </summary>
public sealed class TextIndexDestination : WriteDestinationPluginBase
{
    private readonly IIndexingStrategy _indexStrategy;

    /// <summary>
    /// Initializes a new TextIndexDestination.
    /// </summary>
    public TextIndexDestination(IIndexingStrategy indexStrategy)
    {
        _indexStrategy = indexStrategy ?? throw new ArgumentNullException(nameof(indexStrategy));
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.destination.textindex";

    /// <inheritdoc/>
    public override string Name => "Text Index Destination";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override WriteDestinationType DestinationType => WriteDestinationType.TextIndex;

    /// <inheritdoc/>
    public override bool IsRequired => false;

    /// <inheritdoc/>
    public override int Priority => 50;

    /// <inheritdoc/>
    public override async Task<WriteDestinationResult> WriteAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrEmpty(content.TextContent))
        {
            sw.Stop();
            return SuccessResult(sw.Elapsed); // Nothing to index
        }

        try
        {
            var result = await _indexStrategy.IndexAsync(objectId, content, ct);
            sw.Stop();

            if (result.Success)
            {
                return SuccessResult(sw.Elapsed);
            }

            return FailureResult(result.ErrorMessage ?? "Indexing failed", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailureResult(ex.Message, sw.Elapsed);
        }
    }
}

/// <summary>
/// Vector store write destination.
/// Writes embeddings for semantic search via T90 message bus.
/// </summary>
public sealed class VectorStoreDestination : WriteDestinationPluginBase
{
    private readonly IIndexingStrategy _indexStrategy;
    private readonly IMessageBus? _messageBus;

    /// <summary>
    /// Initializes a new VectorStoreDestination.
    /// </summary>
    public VectorStoreDestination(IIndexingStrategy indexStrategy, IMessageBus? messageBus = null)
    {
        _indexStrategy = indexStrategy ?? throw new ArgumentNullException(nameof(indexStrategy));
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.destination.vectorstore";

    /// <inheritdoc/>
    public override string Name => "Vector Store Destination";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override WriteDestinationType DestinationType => WriteDestinationType.VectorStore;

    /// <inheritdoc/>
    public override bool IsRequired => false;

    /// <inheritdoc/>
    public override int Priority => 50;

    /// <inheritdoc/>
    public override async Task<WriteDestinationResult> WriteAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Need either embeddings or text content
        if (content.Embeddings == null && string.IsNullOrEmpty(content.TextContent))
        {
            sw.Stop();
            return SuccessResult(sw.Elapsed); // Nothing to index
        }

        try
        {
            // If no embeddings, generate them via message bus
            var indexContent = content;
            if (content.Embeddings == null && !string.IsNullOrEmpty(content.TextContent) && _messageBus != null)
            {
                var embeddings = await GenerateEmbeddingsAsync(content.TextContent, ct);
                if (embeddings != null)
                {
                    indexContent = new IndexableContent
                    {
                        ObjectId = content.ObjectId,
                        Filename = content.Filename,
                        TextContent = content.TextContent,
                        Embeddings = embeddings,
                        Summary = content.Summary,
                        Metadata = content.Metadata,
                        ContentType = content.ContentType,
                        Size = content.Size
                    };
                }
            }

            var result = await _indexStrategy.IndexAsync(objectId, indexContent, ct);
            sw.Stop();

            if (result.Success)
            {
                return SuccessResult(sw.Elapsed);
            }

            return FailureResult(result.ErrorMessage ?? "Vector indexing failed", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailureResult(ex.Message, sw.Elapsed);
        }
    }

    private async Task<float[]?> GenerateEmbeddingsAsync(string text, CancellationToken ct)
    {
        if (_messageBus == null)
            return null;

        try
        {
            var msgResponse = await _messageBus.SendAsync(
                "ai.embedding.generate",
                new PluginMessage
                {
                    Type = "ai.embedding.generate",
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["text"] = text,
                        ["model"] = "sentence-transformers/all-MiniLM-L6-v2"
                    }
                },
                TimeSpan.FromSeconds(10),
                ct);

            if (msgResponse?.Success == true && msgResponse.Payload is Dictionary<string, object> payload)
            {
                if (payload.TryGetValue("Embedding", out var embeddingObj) && embeddingObj is float[] embedding)
                {
                    return embedding;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Cache layer write destination.
/// Writes to cache for fast subsequent reads.
/// </summary>
public sealed class CacheDestination : WriteDestinationPluginBase
{
    private readonly ICachingStrategy _cacheStrategy;
    private readonly TimeSpan _defaultTTL;

    /// <summary>
    /// Initializes a new CacheDestination.
    /// </summary>
    public CacheDestination(ICachingStrategy cacheStrategy, TimeSpan? defaultTTL = null)
    {
        _cacheStrategy = cacheStrategy ?? throw new ArgumentNullException(nameof(cacheStrategy));
        _defaultTTL = defaultTTL ?? TimeSpan.FromMinutes(15);
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.destination.cache";

    /// <inheritdoc/>
    public override string Name => "Cache Destination";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override WriteDestinationType DestinationType => WriteDestinationType.Cache;

    /// <inheritdoc/>
    public override bool IsRequired => false;

    /// <inheritdoc/>
    public override int Priority => 30;

    /// <inheritdoc/>
    public override async Task<WriteDestinationResult> WriteAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Cache the content metadata as JSON
            var cacheValue = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                objectId,
                filename = content.Filename,
                contentType = content.ContentType,
                size = content.Size,
                summary = content.Summary,
                metadata = content.Metadata
            });

            var options = new Strategies.Caching.CacheOptions
            {
                TTL = _defaultTTL,
                Tags = new[] { "content", content.ContentType ?? "unknown" }
            };

            await _cacheStrategy.SetAsync($"content:{objectId}", cacheValue, options, ct);
            sw.Stop();

            return SuccessResult(sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailureResult(ex.Message, sw.Elapsed);
        }
    }
}
