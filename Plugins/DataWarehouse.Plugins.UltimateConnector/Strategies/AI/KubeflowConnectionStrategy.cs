using System;using System.Collections.Generic;using System.Net.Http;using System.Net.Http.Headers;using System.Runtime.CompilerServices;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.AI;

/// <summary>Kubeflow Pipelines. REST API for pipeline/run management, experiment tracking on Kubernetes.</summary>
public sealed class KubeflowConnectionStrategy : AiConnectionStrategyBase
{
    public override string StrategyId => "ai-kubeflow";public override string DisplayName => "Kubeflow Pipelines";public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: false, SupportsStreaming: false, SupportsAuthentication: true);public override string SemanticDescription => "Kubeflow Pipelines on Kubernetes. ML workflow orchestration, experiment management, and pipeline versioning.";public override string[] Tags => ["kubeflow", "kubernetes", "ml-pipelines", "orchestration", "ml-ops"];
    public KubeflowConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var baseUrl = config.ConnectionString ?? "http://localhost:8080";var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };if (config.Properties.TryGetValue("ApiKey", out var apiKey)){httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.ToString()!);}if (config.Properties.TryGetValue("Namespace", out var ns)){httpClient.DefaultRequestHeaders.Add("X-Kubeflow-Namespace", ns.ToString()!);}return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "Kubeflow", ["BaseUrl"] = baseUrl });}
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct){var httpClient = handle.GetConnection<HttpClient>();using var response = await httpClient.GetAsync("/apis/v2beta1/healthz", ct);return response.IsSuccessStatusCode;}
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct){var httpClient = handle.GetConnection<HttpClient>();try{using var response = await httpClient.GetAsync("/apis/v2beta1/healthz", ct);return new ConnectionHealth(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "Kubeflow healthy" : "Kubeflow unreachable", TimeSpan.Zero, DateTimeOffset.UtcNow);}catch (Exception ex){return new ConnectionHealth(false, $"Kubeflow error: {ex.Message}", TimeSpan.Zero, DateTimeOffset.UtcNow);}}
    public override async Task<string> SendRequestAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, CancellationToken ct = default)
    {
        var httpClient = handle.GetConnection<HttpClient>();
        var action = options?.GetValueOrDefault("action", "list_pipelines")?.ToString() ?? "list_pipelines";
        if (action == "list_pipelines")
        {
            using var response = await httpClient.GetAsync("/apis/v2beta1/pipelines?page_size=100", ct);
            // Finding 1776: Read error response body before throwing so callers get actionable detail.
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Kubeflow list_pipelines failed ({(int)response.StatusCode}): {errBody}");
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        else if (action == "create_run")
        {
            var pipelineId = options?.GetValueOrDefault("pipeline_id", "")?.ToString();
            var experimentId = options?.GetValueOrDefault("experiment_id", "")?.ToString();
            var json = JsonSerializer.Serialize(new { display_name = prompt, pipeline_version_reference = new { pipeline_id = pipelineId }, experiment_id = experimentId });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync("/apis/v2beta1/runs", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Kubeflow create_run failed ({(int)response.StatusCode}): {errBody}");
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        else if (action == "get_run")
        {
            var runId = options?.GetValueOrDefault("run_id", "")?.ToString();
            using var response = await httpClient.GetAsync($"/apis/v2beta1/runs/{Uri.EscapeDataString(runId ?? "")}", ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Kubeflow get_run failed ({(int)response.StatusCode}): {errBody}");
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        else if (action == "list_experiments")
        {
            using var response = await httpClient.GetAsync("/apis/v2beta1/experiments?page_size=100", ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Kubeflow list_experiments failed ({(int)response.StatusCode}): {errBody}");
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        else
        {
            using var response = await httpClient.GetAsync("/apis/v2beta1/pipelines?page_size=100", ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Kubeflow request failed ({(int)response.StatusCode}): {errBody}");
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
    }
    public override async IAsyncEnumerable<string> StreamResponseAsync(IConnectionHandle handle, string prompt, Dictionary<string, object>? options = null, [EnumeratorCancellation] CancellationToken ct = default){ThrowStreamingNotSupported("Kubeflow Pipelines does not support streaming responses. Use SendRequestAsync for pipeline management.");yield break;}
}
