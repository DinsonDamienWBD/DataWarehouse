using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataTransit.QoS;

/// <summary>
/// Represents a candidate transfer route with strategy, endpoint, cost profile, and performance estimates.
/// Used by <see cref="CostAwareRouter"/> for route scoring and selection.
/// </summary>
internal sealed record TransitRoute
{
    /// <summary>
    /// Unique identifier for this route.
    /// </summary>
    public string RouteId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The strategy ID to use for this route (e.g., "transit-http2", "transit-sftp").
    /// </summary>
    public string StrategyId { get; init; } = string.Empty;

    /// <summary>
    /// The target endpoint for this route.
    /// </summary>
    public TransitEndpoint? Endpoint { get; init; }

    /// <summary>
    /// Cost profile for this route, defining per-GB and fixed costs.
    /// </summary>
    public TransitCostProfile CostProfile { get; init; } = new();

    /// <summary>
    /// Estimated round-trip latency in milliseconds for this route.
    /// </summary>
    public double EstimatedLatencyMs { get; init; }

    /// <summary>
    /// Estimated throughput in bytes per second for this route.
    /// </summary>
    public double EstimatedThroughputBytesPerSec { get; init; }
}

/// <summary>
/// Routing policies for cost-aware route selection.
/// Determines how routes are scored and ranked.
/// </summary>
internal enum RoutingPolicy
{
    /// <summary>
    /// Selects the route with the lowest total cost.
    /// Score = negative total cost (lower cost = higher score).
    /// </summary>
    Cheapest,

    /// <summary>
    /// Selects the route with the highest throughput-to-latency ratio.
    /// Score = throughput / max(1, latency).
    /// </summary>
    Fastest,

    /// <summary>
    /// Balances cost, throughput, and latency using weighted normalization.
    /// Score = 0.4 * normalizedThroughput - 0.3 * normalizedCost - 0.3 * normalizedLatency.
    /// </summary>
    Balanced,

    /// <summary>
    /// Selects the fastest route that fits within the cost budget.
    /// Routes exceeding the cost limit are filtered out before scoring by throughput.
    /// </summary>
    CostCapped
}

/// <summary>
/// Cost-aware router that selects the optimal transfer route from a set of candidates
/// based on configurable cost and performance policies.
/// </summary>
/// <remarks>
/// <para>
/// The router supports four routing policies:
/// <list type="bullet">
/// <item><description><b>Cheapest:</b> Minimizes total transfer cost (fixed + per-GB).</description></item>
/// <item><description><b>Fastest:</b> Maximizes throughput-to-latency ratio.</description></item>
/// <item><description><b>Balanced:</b> Weighted combination of throughput (40%), cost (30%), and latency (30%)
/// using min-max normalization across candidates.</description></item>
/// <item><description><b>CostCapped:</b> Selects fastest route within a cost budget from the request's QoS policy.</description></item>
/// </list>
/// </para>
/// <para>
/// Cost profiles can be registered per strategy ID and looked up for estimation.
/// Route selection filters out candidates with null endpoints before scoring.
/// </para>
/// </remarks>
internal sealed class CostAwareRouter
{
    private readonly RoutingPolicy _defaultPolicy;
    private readonly BoundedDictionary<string, TransitCostProfile> _costProfiles = new BoundedDictionary<string, TransitCostProfile>(1000);

    /// <summary>
    /// Number of bytes in one gigabyte, used for cost calculations.
    /// </summary>
    private const decimal BytesPerGB = 1_073_741_824m;

    /// <summary>
    /// Initializes a new instance of the <see cref="CostAwareRouter"/> class.
    /// </summary>
    /// <param name="defaultPolicy">The default routing policy for route selection.</param>
    public CostAwareRouter(RoutingPolicy defaultPolicy = RoutingPolicy.Balanced)
    {
        _defaultPolicy = defaultPolicy;
    }

    /// <summary>
    /// Registers a cost profile for a strategy, used for cost estimation and route scoring.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <param name="profile">The cost profile to register.</param>
    public void RegisterCostProfile(string strategyId, TransitCostProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentNullException.ThrowIfNull(profile);
        _costProfiles[strategyId] = profile;
    }

    /// <summary>
    /// Gets the registered cost profile for a strategy.
    /// </summary>
    /// <param name="strategyId">The strategy identifier.</param>
    /// <returns>The cost profile if registered; null otherwise.</returns>
    public TransitCostProfile? GetCostProfile(string strategyId)
    {
        return _costProfiles.TryGetValue(strategyId, out var profile) ? profile : null;
    }

    /// <summary>
    /// Selects the optimal route from candidates based on the routing policy.
    /// Filters candidates by endpoint reachability, computes costs, scores each route,
    /// and returns the highest-scoring route.
    /// </summary>
    /// <param name="candidates">The list of candidate routes to evaluate.</param>
    /// <param name="request">The transfer request providing size and QoS context.</param>
    /// <param name="overridePolicy">Optional policy override; uses default if null.</param>
    /// <returns>The selected route with the highest score.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no viable route is available.</exception>
    public TransitRoute SelectRoute(
        IReadOnlyList<TransitRoute> candidates,
        TransitRequest request,
        RoutingPolicy? overridePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(request);

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No candidate routes provided for selection.");
        }

        var policy = overridePolicy ?? _defaultPolicy;

        // Phase 1: Filter candidates by basic viability (non-null endpoint, strategy available)
        var viableRoutes = candidates
            .Where(r => r.Endpoint != null && !string.IsNullOrEmpty(r.StrategyId))
            .ToList();

        if (viableRoutes.Count == 0)
        {
            throw new InvalidOperationException("No viable routes available - all candidates have null endpoints or missing strategy IDs.");
        }

        // Phase 2: Compute total cost for each route
        var routeCosts = viableRoutes.Select(route =>
        {
            var totalCost = ComputeRouteCost(route.CostProfile, request.SizeBytes);
            return (Route: route, TotalCost: totalCost);
        }).ToList();

        // Phase 3: If CostCapped, filter by cost limit from QoS policy
        if (policy == RoutingPolicy.CostCapped && request.QoSPolicy?.CostLimit > 0)
        {
            var costLimit = request.QoSPolicy.CostLimit;
            routeCosts = routeCosts.Where(rc => rc.TotalCost <= costLimit).ToList();

            if (routeCosts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No routes available within cost limit of {costLimit:C}. " +
                    "Consider increasing the cost limit or using a different routing policy.");
            }
        }

        // Phase 4: Score each route based on policy
        var scored = routeCosts.Select(rc =>
        {
            var score = policy switch
            {
                RoutingPolicy.Cheapest => ScoreCheapest(rc.TotalCost),
                RoutingPolicy.Fastest => ScoreFastest(rc.Route),
                RoutingPolicy.Balanced => ScoreBalanced(rc.Route, rc.TotalCost, routeCosts),
                RoutingPolicy.CostCapped => ScoreCostCapped(rc.Route),
                _ => 0.0
            };
            return (rc.Route, Score: score);
        }).ToList();

        // Phase 5: Select highest-scoring route
        var bestRoute = scored.OrderByDescending(s => s.Score).First();
        return bestRoute.Route;
    }

    /// <summary>
    /// Estimates the total transfer cost for a specific strategy and data size.
    /// </summary>
    /// <param name="strategyId">The strategy identifier to look up.</param>
    /// <param name="sizeBytes">The data size in bytes.</param>
    /// <returns>The estimated total cost (fixed + per-GB).</returns>
    /// <exception cref="InvalidOperationException">Thrown when no cost profile is registered for the strategy.</exception>
    public decimal EstimateTransferCost(string strategyId, long sizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);

        if (!_costProfiles.TryGetValue(strategyId, out var profile))
        {
            throw new InvalidOperationException($"No cost profile registered for strategy '{strategyId}'.");
        }

        return ComputeRouteCost(profile, sizeBytes);
    }

    /// <summary>
    /// Ranks all provided routes by total transfer cost in ascending order.
    /// </summary>
    /// <param name="routes">The routes to rank.</param>
    /// <param name="sizeBytes">The data size in bytes for cost computation.</param>
    /// <returns>A list of (route, cost) tuples sorted by cost ascending.</returns>
    public IReadOnlyList<(TransitRoute Route, decimal Cost)> RankRoutesByCost(
        IReadOnlyList<TransitRoute> routes,
        long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        return routes
            .Select(route =>
            {
                var cost = ComputeRouteCost(route.CostProfile, sizeBytes);
                return (Route: route, Cost: cost);
            })
            .OrderBy(rc => rc.Cost)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Computes the total cost of a route: fixed cost per transfer + cost per GB * data size.
    /// </summary>
    /// <param name="profile">The cost profile.</param>
    /// <param name="sizeBytes">The data size in bytes.</param>
    /// <returns>The total cost in currency units.</returns>
    private static decimal ComputeRouteCost(TransitCostProfile profile, long sizeBytes)
    {
        var dataSizeGB = sizeBytes / BytesPerGB;
        return profile.FixedCostPerTransfer + (profile.CostPerGB * dataSizeGB);
    }

    /// <summary>
    /// Scores a route for the Cheapest policy: lower cost = higher score.
    /// </summary>
    /// <param name="totalCost">The total route cost.</param>
    /// <returns>The negative total cost as the score.</returns>
    private static double ScoreCheapest(decimal totalCost)
    {
        return -(double)totalCost;
    }

    /// <summary>
    /// Scores a route for the Fastest policy: throughput / max(1, latency).
    /// </summary>
    /// <param name="route">The route to score.</param>
    /// <returns>The throughput-to-latency ratio.</returns>
    private static double ScoreFastest(TransitRoute route)
    {
        return route.EstimatedThroughputBytesPerSec / Math.Max(1.0, route.EstimatedLatencyMs);
    }

    /// <summary>
    /// Scores a route for the Balanced policy using min-max normalization.
    /// Score = 0.4 * normalizedThroughput - 0.3 * normalizedCost - 0.3 * normalizedLatency.
    /// </summary>
    /// <param name="route">The route to score.</param>
    /// <param name="totalCost">The total cost of this route.</param>
    /// <param name="allRoutes">All routes for normalization context.</param>
    /// <returns>The balanced score.</returns>
    private static double ScoreBalanced(
        TransitRoute route,
        decimal totalCost,
        List<(TransitRoute Route, decimal TotalCost)> allRoutes)
    {
        // Compute min-max ranges for normalization
        var minCost = (double)allRoutes.Min(r => r.TotalCost);
        var maxCost = (double)allRoutes.Max(r => r.TotalCost);
        var minThroughput = allRoutes.Min(r => r.Route.EstimatedThroughputBytesPerSec);
        var maxThroughput = allRoutes.Max(r => r.Route.EstimatedThroughputBytesPerSec);
        var minLatency = allRoutes.Min(r => r.Route.EstimatedLatencyMs);
        var maxLatency = allRoutes.Max(r => r.Route.EstimatedLatencyMs);

        // Normalize to [0, 1] range (handle zero ranges)
        var normalizedThroughput = NormalizeValue(route.EstimatedThroughputBytesPerSec, minThroughput, maxThroughput);
        var normalizedCost = NormalizeValue((double)totalCost, minCost, maxCost);
        var normalizedLatency = NormalizeValue(route.EstimatedLatencyMs, minLatency, maxLatency);

        // Weighted combination: higher throughput is better, lower cost and latency are better
        return 0.4 * normalizedThroughput - 0.3 * normalizedCost - 0.3 * normalizedLatency;
    }

    /// <summary>
    /// Scores a route for the CostCapped policy: highest throughput among cost-filtered routes.
    /// </summary>
    /// <param name="route">The route to score.</param>
    /// <returns>The estimated throughput as the score.</returns>
    private static double ScoreCostCapped(TransitRoute route)
    {
        return route.EstimatedThroughputBytesPerSec;
    }

    /// <summary>
    /// Min-max normalizes a value to the [0, 1] range.
    /// Returns 0.5 if min equals max (all values are identical).
    /// </summary>
    /// <param name="value">The value to normalize.</param>
    /// <param name="min">The minimum value in the range.</param>
    /// <param name="max">The maximum value in the range.</param>
    /// <returns>The normalized value between 0.0 and 1.0.</returns>
    private static double NormalizeValue(double value, double min, double max)
    {
        var range = max - min;
        if (range <= 0) return 0.5;
        return (value - min) / range;
    }
}
