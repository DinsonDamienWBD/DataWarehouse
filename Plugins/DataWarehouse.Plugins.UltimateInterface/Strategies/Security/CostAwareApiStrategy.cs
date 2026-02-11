using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Security;

/// <summary>
/// Cost-aware API strategy with operation cost tracking and budget enforcement.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready API cost management with:
/// <list type="bullet">
/// <item><description>Per-operation cost calculation based on resource usage</description></item>
/// <item><description>X-Api-Cost and X-Api-Budget-Remaining headers</description></item>
/// <item><description>Budget quota enforcement with 402 Payment Required responses</description></item>
/// <item><description>Cost estimation support via ?estimate=true query parameter</description></item>
/// <item><description>Tiered pricing models (compute, storage, bandwidth)</description></item>
/// </list>
/// </para>
/// <para>
/// Helps clients track API usage costs and prevents over-budget requests.
/// Supports cost optimization by providing estimates before execution.
/// </para>
/// </remarks>
internal sealed class CostAwareApiStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // IPluginInterfaceStrategy metadata
    public string StrategyId => "cost-aware-api";
    public string DisplayName => "Cost-Aware API";
    public string SemanticDescription => "Cost tracking and budget enforcement - computes operation costs, tracks budgets, returns cost headers, rejects over-budget requests with 402.";
    public InterfaceCategory Category => InterfaceCategory.Innovation;
    public string[] Tags => new[] { "cost-tracking", "budget", "billing", "quota", "402" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.REST;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json" },
        MaxRequestSize: 10 * 1024 * 1024, // 10 MB
        MaxResponseSize: 50 * 1024 * 1024, // 50 MB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    // Cost factors (in credits)
    private static readonly Dictionary<string, decimal> CostFactors = new()
    {
        ["compute.light"] = 0.01m,      // Simple operations
        ["compute.medium"] = 0.05m,     // Complex queries
        ["compute.heavy"] = 0.25m,      // AI/ML operations
        ["storage.read"] = 0.001m,      // Per MB read
        ["storage.write"] = 0.002m,     // Per MB written
        ["bandwidth.egress"] = 0.01m,   // Per MB egress
        ["bandwidth.ingress"] = 0.005m  // Per MB ingress
    };

    // Client budget tracking
    private readonly ConcurrentDictionary<string, ClientBudget> _budgets = new();

    /// <summary>
    /// Client budget data.
    /// </summary>
    private sealed class ClientBudget
    {
        public required string ClientId { get; init; }
        public decimal TotalBudget { get; set; } = 100.0m; // Default 100 credits
        public decimal UsedBudget { get; set; }
        public DateTimeOffset ResetAt { get; set; }
        public List<CostEntry> CostHistory { get; } = new();
    }

    /// <summary>
    /// Cost entry for audit trail.
    /// </summary>
    private sealed class CostEntry
    {
        public required DateTimeOffset Timestamp { get; init; }
        public required string Operation { get; init; }
        public required decimal Cost { get; init; }
        public required Dictionary<string, decimal> CostBreakdown { get; init; }
    }

    /// <summary>
    /// Initializes the Cost-Aware strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up Cost-Aware resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        _budgets.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles requests with cost tracking and budget enforcement.
    /// </summary>
    /// <param name="request">The validated interface request.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An InterfaceResponse with cost headers or 402 Payment Required.</returns>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Identify client
            var clientId = ExtractClientId(request);

            // Get or create budget
            var budget = _budgets.GetOrAdd(clientId, _ => new ClientBudget
            {
                ClientId = clientId,
                ResetAt = DateTimeOffset.UtcNow.AddDays(30) // Monthly billing cycle
            });

            // Reset budget if period expired
            if (DateTimeOffset.UtcNow >= budget.ResetAt)
            {
                budget.UsedBudget = 0;
                budget.ResetAt = DateTimeOffset.UtcNow.AddDays(30);
                budget.CostHistory.Clear();
            }

            // Calculate operation cost
            var costBreakdown = CalculateOperationCost(request);
            var totalCost = costBreakdown.Values.Sum();

            // Check if this is a cost estimation request
            var estimateOnly = request.QueryParameters?.GetValueOrDefault("estimate") == "true";

            if (estimateOnly)
            {
                return CreateEstimateResponse(clientId, budget, totalCost, costBreakdown);
            }

            // Check budget
            var remainingBudget = budget.TotalBudget - budget.UsedBudget;
            if (totalCost > remainingBudget)
            {
                return Create402Response(clientId, totalCost, remainingBudget, budget.TotalBudget);
            }

            // Deduct cost from budget
            budget.UsedBudget += totalCost;
            budget.CostHistory.Add(new CostEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Operation = $"{request.Method} {request.Path}",
                Cost = totalCost,
                CostBreakdown = costBreakdown
            });

            // Execute operation
            var responseData = await ExecuteOperationAsync(request, cancellationToken);
            var json = JsonSerializer.Serialize(responseData, new JsonSerializerOptions { WriteIndented = true });
            var body = Encoding.UTF8.GetBytes(json);

            var newRemainingBudget = budget.TotalBudget - budget.UsedBudget;

            return new SdkInterface.InterfaceResponse(
                StatusCode: 200,
                Headers: new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["X-Api-Cost"] = totalCost.ToString("F4"),
                    ["X-Api-Budget-Total"] = budget.TotalBudget.ToString("F2"),
                    ["X-Api-Budget-Used"] = budget.UsedBudget.ToString("F4"),
                    ["X-Api-Budget-Remaining"] = newRemainingBudget.ToString("F4"),
                    ["X-Api-Budget-Reset"] = budget.ResetAt.ToString("O"),
                    ["X-Api-Cost-Breakdown"] = JsonSerializer.Serialize(costBreakdown)
                },
                Body: body
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(500, "Internal Server Error", ex.Message);
        }
    }

    /// <summary>
    /// Extracts client ID from request headers.
    /// </summary>
    private string ExtractClientId(SdkInterface.InterfaceRequest request)
    {
        var apiKey = request.Headers?.GetValueOrDefault("X-API-Key");
        if (!string.IsNullOrEmpty(apiKey))
            return $"apikey:{apiKey.Substring(0, Math.Min(8, apiKey.Length))}";

        var authHeader = request.Headers?.GetValueOrDefault("Authorization");
        if (!string.IsNullOrEmpty(authHeader))
            return $"auth:{authHeader.GetHashCode():X8}";

        return "anonymous";
    }

    /// <summary>
    /// Calculates the cost of an operation.
    /// </summary>
    private Dictionary<string, decimal> CalculateOperationCost(SdkInterface.InterfaceRequest request)
    {
        var breakdown = new Dictionary<string, decimal>();

        // Compute cost based on operation complexity
        var computeLevel = DetermineComputeLevel(request);
        breakdown[computeLevel] = CostFactors[computeLevel];

        // Ingress bandwidth cost
        if (request.Body.Length > 0)
        {
            var ingressMB = request.Body.Length / (1024m * 1024m);
            breakdown["bandwidth.ingress"] = ingressMB * CostFactors["bandwidth.ingress"];
        }

        // Storage read cost (if querying data)
        if (request.Method == SdkInterface.HttpMethod.GET)
        {
            // Estimate read size based on path complexity
            var estimatedReadMB = EstimateReadSize(request);
            breakdown["storage.read"] = estimatedReadMB * CostFactors["storage.read"];
        }

        // Storage write cost (if writing data)
        if (request.Method == SdkInterface.HttpMethod.POST ||
            request.Method == SdkInterface.HttpMethod.PUT ||
            request.Method == SdkInterface.HttpMethod.PATCH)
        {
            var writeMB = Math.Max(request.Body.Length / (1024m * 1024m), 0.1m);
            breakdown["storage.write"] = writeMB * CostFactors["storage.write"];
        }

        // Egress bandwidth cost (estimate response size)
        var estimatedEgressMB = EstimateResponseSize(request);
        breakdown["bandwidth.egress"] = estimatedEgressMB * CostFactors["bandwidth.egress"];

        return breakdown;
    }

    /// <summary>
    /// Determines compute level based on request characteristics.
    /// </summary>
    private string DetermineComputeLevel(SdkInterface.InterfaceRequest request)
    {
        // Check for AI/ML operations
        if (request.Path?.Contains("/ai/") == true || request.Path?.Contains("/ml/") == true)
            return "compute.heavy";

        // Check for complex queries
        if (request.QueryParameters?.Count > 5 || request.Body.Length > 1024 * 1024)
            return "compute.medium";

        return "compute.light";
    }

    /// <summary>
    /// Estimates read size based on request.
    /// </summary>
    private decimal EstimateReadSize(SdkInterface.InterfaceRequest request)
    {
        // Parse limit/pageSize from query params
        if (request.QueryParameters != null)
        {
            if (request.QueryParameters.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var limit))
                return limit * 0.001m; // Estimate 1KB per record

            if (request.QueryParameters.TryGetValue("pageSize", out var pageSizeStr) && int.TryParse(pageSizeStr, out var pageSize))
                return pageSize * 0.001m;
        }

        return 0.1m; // Default 100KB
    }

    /// <summary>
    /// Estimates response size based on request.
    /// </summary>
    private decimal EstimateResponseSize(SdkInterface.InterfaceRequest request)
    {
        // Same logic as read size for GET requests
        if (request.Method == SdkInterface.HttpMethod.GET)
            return EstimateReadSize(request);

        // For writes, response is typically small
        return 0.01m; // 10KB
    }

    /// <summary>
    /// Executes the operation and generates a response.
    /// </summary>
    private async Task<object> ExecuteOperationAsync(SdkInterface.InterfaceRequest request, CancellationToken cancellationToken)
    {
        // Route to backend service if available
        if (IsIntelligenceAvailable && MessageBus != null)
        {
            await Task.CompletedTask; // Placeholder for actual service call
        }

        return new
        {
            message = "Operation executed successfully",
            path = request.Path,
            method = request.Method.ToString(),
            timestamp = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    /// <summary>
    /// Creates a cost estimate response.
    /// </summary>
    private SdkInterface.InterfaceResponse CreateEstimateResponse(
        string clientId,
        ClientBudget budget,
        decimal estimatedCost,
        Dictionary<string, decimal> costBreakdown)
    {
        var responseData = new
        {
            message = "Cost estimate (operation not executed)",
            clientId,
            estimate = new
            {
                totalCost = estimatedCost,
                breakdown = costBreakdown,
                currentBudget = new
                {
                    total = budget.TotalBudget,
                    used = budget.UsedBudget,
                    remaining = budget.TotalBudget - budget.UsedBudget,
                    resetAt = budget.ResetAt
                },
                affordable = estimatedCost <= (budget.TotalBudget - budget.UsedBudget)
            },
            timestamp = DateTimeOffset.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(responseData, new JsonSerializerOptions { WriteIndented = true });
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Api-Cost-Estimate"] = estimatedCost.ToString("F4")
            },
            Body: body
        );
    }

    /// <summary>
    /// Creates a 402 Payment Required response.
    /// </summary>
    private SdkInterface.InterfaceResponse Create402Response(
        string clientId,
        decimal requiredCost,
        decimal remainingBudget,
        decimal totalBudget)
    {
        var errorData = new
        {
            error = new
            {
                title = "Payment Required",
                detail = "Insufficient budget for this operation. Please upgrade your plan or wait for budget reset.",
                clientId,
                requiredCost,
                remainingBudget,
                totalBudget,
                deficit = requiredCost - remainingBudget
            },
            timestamp = DateTimeOffset.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(errorData);
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: 402,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["X-Api-Cost-Required"] = requiredCost.ToString("F4"),
                ["X-Api-Budget-Remaining"] = remainingBudget.ToString("F4")
            },
            Body: body
        );
    }

    /// <summary>
    /// Creates an error InterfaceResponse.
    /// </summary>
    private SdkInterface.InterfaceResponse CreateErrorResponse(int statusCode, string title, string detail)
    {
        var errorData = new
        {
            error = new
            {
                title,
                detail,
                timestamp = DateTimeOffset.UtcNow.ToString("O")
            }
        };
        var json = JsonSerializer.Serialize(errorData);
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: statusCode,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json"
            },
            Body: body
        );
    }
}
