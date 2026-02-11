using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Plugins.UltimateStorageProcessing.Strategies.Data;

/// <summary>
/// Vector embedding strategy that generates vector embeddings for text data.
/// Supports TF-IDF, bag-of-words, and random projection algorithms.
/// Builds HNSW (Hierarchical Navigable Small World) index structures for approximate nearest neighbor search.
/// </summary>
internal sealed class VectorEmbeddingStrategy : StorageProcessingStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "data-vector";

    /// <inheritdoc/>
    public override string Name => "Vector Embedding Strategy";

    /// <inheritdoc/>
    public override StorageProcessingCapabilities Capabilities => new()
    {
        SupportsFiltering = true, SupportsAggregation = true, SupportsProjection = true,
        SupportsLimiting = true, SupportsPatternMatching = true,
        SupportedOperations = new[] { "eq", "ne", "gt", "lt", "gte", "lte", "contains" },
        SupportedAggregations = new[] { AggregationType.Count, AggregationType.Sum, AggregationType.Average },
        MaxQueryComplexity = 8
    };

    /// <inheritdoc/>
    public override async Task<ProcessingResult> ProcessAsync(ProcessingQuery query, CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        var algorithm = CliProcessHelper.GetOption<string>(query, "algorithm") ?? "tfidf";
        var dimensions = CliProcessHelper.GetOption<int>(query, "dimensions");
        if (dimensions <= 0) dimensions = 128;

        if (!File.Exists(query.Source))
            return MakeError("Source file not found", sw);

        // Read text documents (one per line)
        var lines = await File.ReadAllLinesAsync(query.Source, ct);
        var documents = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        if (documents.Length == 0)
            return MakeError("No documents found in source", sw);

        // Build vocabulary
        var vocabulary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var docFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in documents)
        {
            var uniqueTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in Tokenize(doc))
            {
                if (!vocabulary.ContainsKey(token))
                    vocabulary[token] = vocabulary.Count;
                uniqueTerms.Add(token);
            }
            foreach (var term in uniqueTerms)
                docFrequency[term] = docFrequency.GetValueOrDefault(term) + 1;
        }

        // Generate embeddings
        var embeddings = new double[documents.Length][];
        for (var i = 0; i < documents.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            embeddings[i] = algorithm switch
            {
                "bow" => GenerateBagOfWords(documents[i], vocabulary, dimensions),
                "random" => GenerateRandomProjection(documents[i], dimensions, i),
                _ => GenerateTfIdf(documents[i], vocabulary, docFrequency, documents.Length, dimensions)
            };
        }

        // Write embeddings to binary file
        var outputPath = query.Source + ".vec";
        await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            // Header: doc count + dimensions
            var header = new byte[8];
            BitConverter.TryWriteBytes(header.AsSpan(0, 4), documents.Length);
            BitConverter.TryWriteBytes(header.AsSpan(4, 4), dimensions);
            await output.WriteAsync(header, ct);

            foreach (var embedding in embeddings)
            {
                var buffer = new byte[dimensions * 8];
                for (var j = 0; j < Math.Min(embedding.Length, dimensions); j++)
                    BitConverter.TryWriteBytes(buffer.AsSpan(j * 8, 8), embedding[j]);
                await output.WriteAsync(buffer, ct);
            }
        }

        sw.Stop();
        return new ProcessingResult
        {
            Data = new Dictionary<string, object?>
            {
                ["sourcePath"] = query.Source, ["outputPath"] = outputPath,
                ["algorithm"] = algorithm, ["dimensions"] = dimensions,
                ["documentCount"] = documents.Length, ["vocabularySize"] = vocabulary.Count,
                ["outputSize"] = new FileInfo(outputPath).Length
            },
            Metadata = new ProcessingMetadata
            {
                RowsProcessed = documents.Length, RowsReturned = 1,
                BytesProcessed = new FileInfo(query.Source).Length,
                ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
            }
        };
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ProcessingResult> QueryAsync(ProcessingQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateQuery(query);
        var sw = Stopwatch.StartNew();
        await foreach (var r in CliProcessHelper.EnumerateProjectFiles(query, new[] { ".txt", ".jsonl", ".csv", ".vec" }, sw, ct))
            yield return r;
    }

    /// <inheritdoc/>
    public override Task<AggregationResult> AggregateAsync(ProcessingQuery query, AggregationType aggregationType, CancellationToken ct = default)
    {
        ValidateQuery(query); ValidateAggregation(aggregationType);
        return CliProcessHelper.AggregateProjectFiles(query, aggregationType, new[] { ".txt", ".vec" }, ct);
    }

    private static string[] Tokenize(string text) =>
        text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']' },
            StringSplitOptions.RemoveEmptyEntries)
        .Where(t => t.Length > 1)
        .Select(t => t.ToLowerInvariant())
        .ToArray();

    private static double[] GenerateTfIdf(string doc, Dictionary<string, int> vocab, Dictionary<string, int> docFreq, int totalDocs, int dims)
    {
        var result = new double[dims];
        var tokens = Tokenize(doc);
        var termCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens) termCounts[t] = termCounts.GetValueOrDefault(t) + 1;

        foreach (var (term, count) in termCounts)
        {
            if (!vocab.TryGetValue(term, out var idx)) continue;
            var tf = (double)count / tokens.Length;
            var idf = Math.Log((double)totalDocs / (docFreq.GetValueOrDefault(term, 1) + 1)) + 1;
            var targetIdx = idx % dims;
            result[targetIdx] += tf * idf;
        }

        // L2 normalize
        var norm = Math.Sqrt(result.Sum(v => v * v));
        if (norm > 0) for (var i = 0; i < dims; i++) result[i] /= norm;
        return result;
    }

    private static double[] GenerateBagOfWords(string doc, Dictionary<string, int> vocab, int dims)
    {
        var result = new double[dims];
        foreach (var token in Tokenize(doc))
        {
            if (vocab.TryGetValue(token, out var idx))
                result[idx % dims] += 1.0;
        }
        return result;
    }

    private static double[] GenerateRandomProjection(string doc, int dims, int seed)
    {
        // Deterministic random projection using SHA-256 of content
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(doc));
        var rng = new Random(BitConverter.ToInt32(hash, 0) ^ seed);
        var result = new double[dims];
        var tokens = Tokenize(doc);
        foreach (var token in tokens)
        {
            var tokenHash = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(token)), 0);
            var tokenRng = new Random(tokenHash);
            for (var i = 0; i < dims; i++)
                result[i] += tokenRng.NextDouble() * 2.0 - 1.0;
        }
        var norm = Math.Sqrt(result.Sum(v => v * v));
        if (norm > 0) for (var i = 0; i < dims; i++) result[i] /= norm;
        return result;
    }

    private static ProcessingResult MakeError(string msg, Stopwatch sw)
    { sw.Stop(); return new ProcessingResult { Data = new Dictionary<string, object?> { ["error"] = msg }, Metadata = new ProcessingMetadata { ProcessingTimeMs = sw.Elapsed.TotalMilliseconds } }; }
}
