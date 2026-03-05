using System;using System.Collections.Generic;using System.Net.Http;using System.Net.Http.Headers;using System.Runtime.CompilerServices;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.AI;

/// <summary>Pinecone vector database. HTTPS to *.pinecone.io REST API. Index management, upsert/query vectors.</summary>
public sealed class PineconeConnectionStrategy : AiConnectionStrategyBase
{
    public override string StrategyId => "ai-pinecone";public override string DisplayName => "Pinecone Vector DB";public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: false, SupportsStreaming: false, SupportsAuthentication: true);public override string SemanticDescription => "Pinecone managed vector database. High-performance similarity search with serverless and pod-based indexes.";public override string[] Tags => ["pinecone", "vector-db", "embeddings", "similarity-search", "serverless"];
    public PineconeConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var host = config.ConnectionString ?? throw new ArgumentException("Pinecone index host URL required");var httpClient = new HttpClient { BaseAddress = new Uri(host), Timeout = config.Timeout };if (config.Properties.TryGetValue("ApiKey", out var apiKey)){httpClient.DefaultRequestHeaders.Add("Api-Key", apiKey.ToString()!);}return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "Pinecone", ["Host"] = host });}
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct){var httpClient = handle.GetConnection<HttpClient>();using var response = await httpClient.GetAsync("/describe_index_stats", ct);return response.IsSuccessStatusCode;}
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct){var httpClient = handle.GetConnection<HttpClient>();try{using var response = await httpClient.GetAsync("/describe_index_stats", ct);return new ConnectionHealth(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "Pinecone index available" : "Pinecone unreachable", TimeSpan.Zero, DateTimeOffset.UtcNow);}catch (Exception ex){return new ConnectionHealth(false, $"Pinecone error: {ex.Message}", TimeSpan.Zero, DateTimeOffset.UtcNow);}}
    // Finding 1777: Validate expected vector dimension when provided in options.
    private static void ValidateVectorDimension(object? vector, Dictionary<string, object>? options)
    {
        if (vector == null) return;
        if (options?.TryGetValue("expected_dim", out var dimVal) == true && dimVal != null
            && int.TryParse(dimVal.ToString(), out var expectedDim) && expectedDim > 0)
        {
            if (vector is System.Collections.IList list && list.Count != expectedDim)
                throw new ArgumentException($"Vector dimension mismatch: expected {expectedDim}, got {list.Count}.");
        }
    }
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default){var httpClient = handle.GetConnection<HttpClient>();var action = options?.GetValueOrDefault("action", "query")?.ToString() ?? "query";if (action == "upsert"){var vectors = (options?.TryGetValue("vectors", out var vectorsVal) == true ? vectorsVal : null);var json = JsonSerializer.Serialize(new { vectors });using var content = new StringContent(json, Encoding.UTF8, "application/json");using var response = await httpClient.PostAsync("/vectors/upsert", content, ct);response.EnsureSuccessStatusCode();return await response.Content.ReadAsStringAsync(ct);}else{var vector = (options?.TryGetValue("vector", out var vectorVal) == true ? vectorVal : null);ValidateVectorDimension(vector, options);var topK = options?.GetValueOrDefault("topK", 10) ?? 10;var payload = new { vector, topK, includeMetadata = true };var json = JsonSerializer.Serialize(payload);using var content = new StringContent(json, Encoding.UTF8, "application/json");using var response = await httpClient.PostAsync("/query", content, ct);response.EnsureSuccessStatusCode();return await response.Content.ReadAsStringAsync(ct);}}
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default){ThrowStreamingNotSupported("Pinecone does not support streaming responses. Use SendRequestAsync for vector operations.");yield break;}
}
