using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateWorkflow;

/// <summary>
/// Registry for workflow strategies.
/// </summary>
public sealed class WorkflowStrategyRegistry
{
    private readonly ConcurrentDictionary<string, WorkflowStrategyBase> _strategies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of registered strategies.</summary>
    public int Count => _strategies.Count;

    /// <summary>Registered strategy names.</summary>
    public IReadOnlyCollection<string> RegisteredStrategies => _strategies.Keys.ToList().AsReadOnly();

    /// <summary>
    /// Registers a strategy.
    /// </summary>
    public void Register(WorkflowStrategyBase strategy)
    {
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>
    /// Gets a strategy by name.
    /// </summary>
    public WorkflowStrategyBase? Get(string name) =>
        _strategies.TryGetValue(name, out var strategy) ? strategy : null;

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    public IEnumerable<WorkflowStrategyBase> GetAll() => _strategies.Values;

    /// <summary>
    /// Gets strategies by category.
    /// </summary>
    public IEnumerable<WorkflowStrategyBase> GetByCategory(WorkflowCategory category) =>
        _strategies.Values.Where(s => s.Characteristics.Category == category);

    /// <summary>
    /// Selects the best strategy based on requirements.
    /// </summary>
    public WorkflowStrategyBase? SelectBest(
        bool requiresParallel = false,
        bool requiresDynamic = false,
        bool requiresDistributed = false,
        bool requiresCheckpointing = false)
    {
        return _strategies.Values
            .Where(s =>
                (!requiresParallel || s.Characteristics.Capabilities.SupportsParallelExecution) &&
                (!requiresDynamic || s.Characteristics.Capabilities.SupportsDynamicDag) &&
                (!requiresDistributed || s.Characteristics.Capabilities.SupportsDistributed) &&
                (!requiresCheckpointing || s.Characteristics.Capabilities.SupportsCheckpointing))
            .OrderByDescending(s => s.Characteristics.Capabilities.MaxParallelTasks)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets strategy summaries.
    /// </summary>
    public IEnumerable<(string Name, string Description, WorkflowCategory Category, bool SupportsParallel)> GetSummary() =>
        _strategies.Values.Select(s => (
            s.Characteristics.StrategyName,
            s.Characteristics.Description,
            s.Characteristics.Category,
            s.Characteristics.Capabilities.SupportsParallelExecution));
}
