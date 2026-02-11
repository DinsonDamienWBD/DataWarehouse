using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Infrastructure;

/// <summary>
/// Data-locality-aware compute placement engine that selects optimal runtime strategies
/// based on proximity between data and compute resources.
/// </summary>
/// <remarks>
/// <para>
/// Scoring model for data locality:
/// </para>
/// <list type="bullet">
/// <item><description>Same node: 1.0 (zero network transfer)</description></item>
/// <item><description>Same rack: 0.7 (low-latency switch-level transfer)</description></item>
/// <item><description>Same datacenter: 0.4 (moderate latency, high bandwidth)</description></item>
/// <item><description>Remote: 0.1 (high latency, WAN transfer)</description></item>
/// </list>
/// <para>
/// The engine considers both data locality and runtime suitability to produce a composite
/// score for strategy selection.
/// </para>
/// </remarks>
internal sealed class DataLocalityPlacement
{
    /// <summary>
    /// Locality score for compute on the same node as data.
    /// </summary>
    public const double SameNodeScore = 1.0;

    /// <summary>
    /// Locality score for compute on the same rack as data.
    /// </summary>
    public const double SameRackScore = 0.7;

    /// <summary>
    /// Locality score for compute in the same datacenter as data.
    /// </summary>
    public const double SameDatacenterScore = 0.4;

    /// <summary>
    /// Locality score for compute at a remote location.
    /// </summary>
    public const double RemoteScore = 0.1;

    /// <summary>
    /// Weight applied to locality score in composite scoring (0.0 - 1.0).
    /// </summary>
    public double LocalityWeight { get; set; } = 0.6;

    /// <summary>
    /// Weight applied to capability fitness score in composite scoring (0.0 - 1.0).
    /// </summary>
    public double CapabilityWeight { get; set; } = 0.4;

    /// <summary>
    /// Finds the optimal compute runtime strategy for a given task considering data locality.
    /// </summary>
    /// <param name="task">The compute task to place.</param>
    /// <param name="strategies">Available runtime strategies.</param>
    /// <param name="dataLocation">The location of the data to process.</param>
    /// <param name="runtimeLocations">Map of strategy ID to its physical location.</param>
    /// <returns>
    /// A ranked list of <see cref="PlacementScore"/> objects, ordered by composite score descending.
    /// Returns an empty list if no strategies are available.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if task or strategies is null.</exception>
    public IReadOnlyList<PlacementScore> FindOptimalRuntime(
        ComputeTask task,
        IReadOnlyList<IComputeRuntimeStrategy> strategies,
        DataLocation? dataLocation = null,
        IReadOnlyDictionary<string, DataLocation>? runtimeLocations = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(strategies);

        if (strategies.Count == 0)
            return Array.Empty<PlacementScore>();

        var scores = new List<PlacementScore>(strategies.Count);

        foreach (var strategy in strategies)
        {
            var strategyId = strategy is ComputeRuntimeStrategyBase b ? b.StrategyId : strategy.Runtime.ToString();

            // Compute locality score
            var localityScore = SameNodeScore; // Default: assume same node if no location info
            if (dataLocation != null && runtimeLocations != null && runtimeLocations.TryGetValue(strategyId, out var runtimeLocation))
            {
                localityScore = ScoreLocality(dataLocation, runtimeLocation);
            }

            // Compute capability fitness score
            var capabilityScore = ScoreCapabilityFitness(task, strategy);

            // Composite score
            var compositeScore = (LocalityWeight * localityScore) + (CapabilityWeight * capabilityScore);

            scores.Add(new PlacementScore(
                StrategyId: strategyId,
                Strategy: strategy,
                LocalityScore: localityScore,
                CapabilityScore: capabilityScore,
                CompositeScore: compositeScore
            ));
        }

        scores.Sort((a, b) => b.CompositeScore.CompareTo(a.CompositeScore));
        return scores.AsReadOnly();
    }

    /// <summary>
    /// Computes the locality score between a data location and a runtime location.
    /// </summary>
    /// <param name="dataLocation">The physical location of the data.</param>
    /// <param name="runtimeLocation">The physical location of the compute runtime.</param>
    /// <returns>A score between 0.0 and 1.0 indicating locality proximity.</returns>
    public static double ScoreLocality(DataLocation dataLocation, DataLocation runtimeLocation)
    {
        ArgumentNullException.ThrowIfNull(dataLocation);
        ArgumentNullException.ThrowIfNull(runtimeLocation);

        // Same node
        if (!string.IsNullOrEmpty(dataLocation.NodeId) &&
            string.Equals(dataLocation.NodeId, runtimeLocation.NodeId, StringComparison.OrdinalIgnoreCase))
        {
            return SameNodeScore;
        }

        // Same rack
        if (!string.IsNullOrEmpty(dataLocation.RackId) &&
            string.Equals(dataLocation.RackId, runtimeLocation.RackId, StringComparison.OrdinalIgnoreCase))
        {
            return SameRackScore;
        }

        // Same datacenter
        if (!string.IsNullOrEmpty(dataLocation.DatacenterId) &&
            string.Equals(dataLocation.DatacenterId, runtimeLocation.DatacenterId, StringComparison.OrdinalIgnoreCase))
        {
            return SameDatacenterScore;
        }

        // Remote
        return RemoteScore;
    }

    /// <summary>
    /// Scores how well a strategy's capabilities match a compute task's requirements.
    /// </summary>
    /// <param name="task">The compute task.</param>
    /// <param name="strategy">The strategy to evaluate.</param>
    /// <returns>A fitness score between 0.0 and 1.0.</returns>
    private static double ScoreCapabilityFitness(ComputeTask task, IComputeRuntimeStrategy strategy)
    {
        var score = 0.5; // Base score

        // Check language support
        var languages = strategy.Capabilities.SupportedLanguages;
        if (languages != null && languages.Any(l => task.Language.Contains(l, StringComparison.OrdinalIgnoreCase)))
        {
            score += 0.2;
        }

        // Check memory suitability
        if (task.ResourceLimits?.MaxMemoryBytes != null && strategy.Capabilities.MaxMemoryBytes != null)
        {
            if (strategy.Capabilities.MaxMemoryBytes >= task.ResourceLimits.MaxMemoryBytes)
                score += 0.1;
        }
        else
        {
            score += 0.05; // No constraint = partial credit
        }

        // Check sandboxing
        if (strategy.Capabilities.SupportsSandboxing)
            score += 0.1;

        // Check network access match
        if (task.ResourceLimits?.AllowNetworkAccess == true && strategy.Capabilities.SupportsNetworkAccess)
            score += 0.05;
        else if (task.ResourceLimits?.AllowNetworkAccess != true)
            score += 0.05; // Not needed = fine

        return Math.Min(1.0, score);
    }
}

/// <summary>
/// Represents the physical location of data or compute resources for locality scoring.
/// </summary>
/// <param name="NodeId">The node/server identifier within a rack.</param>
/// <param name="RackId">The rack identifier within a datacenter.</param>
/// <param name="DatacenterId">The datacenter/region identifier.</param>
/// <param name="Zone">Optional availability zone.</param>
internal record DataLocation(
    string? NodeId = null,
    string? RackId = null,
    string? DatacenterId = null,
    string? Zone = null
);

/// <summary>
/// Placement score for a compute strategy, combining locality and capability fitness.
/// </summary>
/// <param name="StrategyId">The strategy identifier.</param>
/// <param name="Strategy">The strategy instance.</param>
/// <param name="LocalityScore">Score for data-compute proximity (0.0 - 1.0).</param>
/// <param name="CapabilityScore">Score for capability fitness (0.0 - 1.0).</param>
/// <param name="CompositeScore">Weighted composite of locality and capability scores.</param>
internal record PlacementScore(
    string StrategyId,
    IComputeRuntimeStrategy Strategy,
    double LocalityScore,
    double CapabilityScore,
    double CompositeScore
);
