using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.UltimateDataManagement.FanOut;

/// <summary>
/// Defines success criteria for fan out write operations.
/// </summary>
public enum FanOutSuccessCriteria
{
    /// <summary>All enabled destinations must succeed (TamperProof default).</summary>
    AllRequired,

    /// <summary>Majority of enabled destinations must succeed.</summary>
    Majority,

    /// <summary>Primary storage + any one other must succeed.</summary>
    PrimaryPlusOne,

    /// <summary>Only primary storage must succeed (fire-and-forget for others).</summary>
    PrimaryOnly,

    /// <summary>Any single destination success is sufficient.</summary>
    Any
}

/// <summary>
/// Strategy result containing outcomes for all destinations.
/// </summary>
public sealed class FanOutStrategyResult
{
    /// <summary>Whether the strategy execution succeeded based on success criteria.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Results by destination type.</summary>
    public Dictionary<WriteDestinationType, WriteDestinationResult> DestinationResults { get; init; } = new();

    /// <summary>Total execution duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Number of successful destination writes.</summary>
    public int SuccessCount => DestinationResults.Count(r => r.Value.Success);

    /// <summary>Number of failed destination writes.</summary>
    public int FailureCount => DestinationResults.Count(r => !r.Value.Success);
}

/// <summary>
/// Base interface for fan out write strategies.
/// Defines how writes are coordinated across multiple destinations.
/// </summary>
/// <remarks>
/// Implementations include:
/// - TamperProofFanOutStrategy: Locked preset for tamper-proof instances
/// - StandardFanOutStrategy: Configurable for standard instances
/// - CustomFanOutStrategy: Fully user-defined destination combinations
/// </remarks>
public interface IFanOutStrategy
{
    /// <summary>Unique identifier for this strategy.</summary>
    string StrategyId { get; }

    /// <summary>Display name for the strategy.</summary>
    string DisplayName { get; }

    /// <summary>Semantic description for AI discovery.</summary>
    string SemanticDescription { get; }

    /// <summary>Whether this strategy configuration is locked (cannot be modified after deployment).</summary>
    bool IsLocked { get; }

    /// <summary>Success criteria for this strategy.</summary>
    FanOutSuccessCriteria SuccessCriteria { get; }

    /// <summary>Timeout for non-required destinations.</summary>
    TimeSpan NonRequiredTimeout { get; }

    /// <summary>Whether child levels can override this strategy configuration.</summary>
    bool AllowChildOverride { get; }

    /// <summary>Gets the enabled destination types for this strategy.</summary>
    IReadOnlySet<WriteDestinationType> EnabledDestinations { get; }

    /// <summary>Gets destination types that are required (must succeed for overall success).</summary>
    IReadOnlySet<WriteDestinationType> RequiredDestinations { get; }

    /// <summary>
    /// Executes the fan out write operation according to this strategy.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="content">Content to write.</param>
    /// <param name="destinations">Available write destinations.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Strategy execution result.</returns>
    Task<FanOutStrategyResult> ExecuteAsync(
        string objectId,
        IndexableContent content,
        IReadOnlyDictionary<WriteDestinationType, IWriteDestination> destinations,
        CancellationToken ct = default);

    /// <summary>
    /// Validates that required destinations are available.
    /// </summary>
    /// <param name="availableDestinations">Available destination types.</param>
    /// <returns>Validation result with any missing destinations.</returns>
    StrategyValidationResult ValidateDestinations(IEnumerable<WriteDestinationType> availableDestinations);
}

/// <summary>
/// Result of strategy validation.
/// </summary>
public sealed class StrategyValidationResult
{
    /// <summary>Whether validation passed.</summary>
    public bool IsValid { get; init; }

    /// <summary>Missing required destinations.</summary>
    public IReadOnlyList<WriteDestinationType> MissingDestinations { get; init; } = Array.Empty<WriteDestinationType>();

    /// <summary>Warning messages.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Error messages.</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Base class for fan out strategies providing common functionality.
/// </summary>
public abstract class FanOutStrategyBase : IFanOutStrategy
{
    /// <inheritdoc/>
    public abstract string StrategyId { get; }

    /// <inheritdoc/>
    public abstract string DisplayName { get; }

    /// <inheritdoc/>
    public abstract string SemanticDescription { get; }

    /// <inheritdoc/>
    public abstract bool IsLocked { get; }

    /// <inheritdoc/>
    public virtual FanOutSuccessCriteria SuccessCriteria { get; protected set; } = FanOutSuccessCriteria.AllRequired;

    /// <inheritdoc/>
    public virtual TimeSpan NonRequiredTimeout { get; protected set; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public virtual bool AllowChildOverride { get; protected set; } = true;

    /// <inheritdoc/>
    public abstract IReadOnlySet<WriteDestinationType> EnabledDestinations { get; }

    /// <inheritdoc/>
    public abstract IReadOnlySet<WriteDestinationType> RequiredDestinations { get; }

    /// <inheritdoc/>
    public virtual async Task<FanOutStrategyResult> ExecuteAsync(
        string objectId,
        IndexableContent content,
        IReadOnlyDictionary<WriteDestinationType, IWriteDestination> destinations,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(destinations);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new Dictionary<WriteDestinationType, WriteDestinationResult>();

        // Filter to enabled destinations
        var activeDestinations = destinations
            .Where(d => EnabledDestinations.Contains(d.Key))
            .ToDictionary(d => d.Key, d => d.Value);

        // Execute writes
        var tasks = activeDestinations.Select(async kvp =>
        {
            var isRequired = RequiredDestinations.Contains(kvp.Key);
            using var cts = isRequired ? CancellationTokenSource.CreateLinkedTokenSource(ct) : new CancellationTokenSource(NonRequiredTimeout);

            try
            {
                var result = await kvp.Value.WriteAsync(objectId, content, cts.Token);
                return (kvp.Key, result);
            }
            catch (OperationCanceledException) when (!isRequired)
            {
                return (kvp.Key, new WriteDestinationResult
                {
                    Success = false,
                    ErrorMessage = "Timeout exceeded",
                    Duration = NonRequiredTimeout
                });
            }
            catch (Exception ex)
            {
                return (kvp.Key, new WriteDestinationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Duration = TimeSpan.Zero
                });
            }
        }).ToList();

        var writeResults = await Task.WhenAll(tasks);
        foreach (var (type, result) in writeResults)
        {
            results[type] = result;
        }

        sw.Stop();

        // Evaluate success based on criteria
        var success = EvaluateSuccess(results);

        return new FanOutStrategyResult
        {
            Success = success,
            ErrorMessage = success ? null : GetFailureMessage(results),
            DestinationResults = results,
            Duration = sw.Elapsed
        };
    }

    /// <inheritdoc/>
    public virtual StrategyValidationResult ValidateDestinations(IEnumerable<WriteDestinationType> availableDestinations)
    {
        var available = new HashSet<WriteDestinationType>(availableDestinations);
        var missing = RequiredDestinations.Where(r => !available.Contains(r)).ToList();
        var warnings = new List<string>();

        foreach (var enabled in EnabledDestinations)
        {
            if (!available.Contains(enabled) && !RequiredDestinations.Contains(enabled))
            {
                warnings.Add($"Optional destination {enabled} is not available");
            }
        }

        return new StrategyValidationResult
        {
            IsValid = missing.Count == 0,
            MissingDestinations = missing,
            Warnings = warnings,
            Errors = missing.Select(m => $"Required destination {m} is not available").ToList()
        };
    }

    /// <summary>
    /// Evaluates whether the overall operation succeeded based on success criteria.
    /// </summary>
    protected virtual bool EvaluateSuccess(Dictionary<WriteDestinationType, WriteDestinationResult> results)
    {
        var requiredResults = results.Where(r => RequiredDestinations.Contains(r.Key)).ToList();
        var allResults = results.Values.ToList();

        return SuccessCriteria switch
        {
            FanOutSuccessCriteria.AllRequired => requiredResults.All(r => r.Value.Success),
            FanOutSuccessCriteria.Majority => allResults.Count(r => r.Success) > allResults.Count / 2,
            FanOutSuccessCriteria.PrimaryPlusOne => results.TryGetValue(WriteDestinationType.PrimaryStorage, out var primary)
                && primary.Success && allResults.Count(r => r.Success) >= 2,
            FanOutSuccessCriteria.PrimaryOnly => results.TryGetValue(WriteDestinationType.PrimaryStorage, out var p)
                && p.Success,
            FanOutSuccessCriteria.Any => allResults.Any(r => r.Success),
            _ => requiredResults.All(r => r.Value.Success)
        };
    }

    /// <summary>
    /// Gets a failure message summarizing what went wrong.
    /// </summary>
    protected virtual string GetFailureMessage(Dictionary<WriteDestinationType, WriteDestinationResult> results)
    {
        var failures = results.Where(r => !r.Value.Success && RequiredDestinations.Contains(r.Key))
            .Select(r => $"{r.Key}: {r.Value.ErrorMessage}")
            .ToList();

        return failures.Count > 0
            ? $"Required destinations failed: {string.Join("; ", failures)}"
            : "Operation failed to meet success criteria";
    }
}
