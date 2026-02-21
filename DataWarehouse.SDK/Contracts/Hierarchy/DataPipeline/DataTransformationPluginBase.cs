using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for plugins that mutate data in the pipeline (encryption, compression).
/// Provides OnWrite/OnRead stream transformation with async support.
/// </summary>
public abstract class DataTransformationPluginBase : DataPipelinePluginBase
{
    /// <inheritdoc/>
    public override bool MutatesData => true;

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.DataTransformationProvider;

    /// <summary>Sub-category (e.g., "Encryption", "Compression").</summary>
    public abstract string SubCategory { get; }

    /// <summary>Quality level (1-100) for sorting and selection.</summary>
    public virtual int QualityLevel => 50;

    /// <summary>
    /// AI hook: Select optimal algorithm based on data characteristics and context.
    /// Override in derived classes to provide algorithm-specific selection logic.
    /// Default returns SubCategory as a generic fallback.
    /// </summary>
    /// <param name="context">Context information for algorithm selection (data type, size, performance hints).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The selected algorithm identifier.</returns>
    protected virtual Task<string> SelectOptimalAlgorithmAsync(Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(SubCategory);

    /// <summary>
    /// Dispatches an operation to the optimal IStrategy-based strategy, using AI-driven algorithm
    /// selection when no explicit strategy is specified. This is the primary dispatch entry point
    /// for data transformation operations backed by the PluginBase IStrategy registry.
    /// </summary>
    /// <typeparam name="TStrategy">The strategy interface type (must implement <see cref="IStrategy"/>).</typeparam>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use AI selection).</param>
    /// <param name="identity">CommandIdentity for ACL checks.</param>
    /// <param name="dataContext">Context about the data for AI algorithm selection.</param>
    /// <param name="operation">The operation to execute on the resolved strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    protected async Task<TResult> DispatchWithOptimalStrategyAsync<TStrategy, TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<TStrategy, Task<TResult>> operation,
        CancellationToken ct = default) where TStrategy : class, IStrategy
    {
        string strategyId;
        if (!string.IsNullOrEmpty(explicitStrategyId))
        {
            strategyId = explicitStrategyId;
        }
        else
        {
            // Use AI-driven selection (SelectOptimalAlgorithmAsync is no longer dead code)
            strategyId = await SelectOptimalAlgorithmAsync(
                dataContext ?? new Dictionary<string, object>(), ct).ConfigureAwait(false);
        }

        return await ExecuteWithStrategyAsync<TStrategy, TResult>(
            strategyId, identity, operation, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="SubCategory"/> ("Encryption", "Compression", etc.) as the default strategy ID.</remarks>
    protected override string? GetDefaultStrategyId() => SubCategory;

    /// <summary>Transform data during write operations.</summary>
    public virtual Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args, CancellationToken ct = default)
        => Task.FromResult(input);

    /// <summary>Transform data during read operations.</summary>
    public virtual Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args, CancellationToken ct = default)
        => Task.FromResult(stored);

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["SubCategory"] = SubCategory;
        metadata["QualityLevel"] = QualityLevel;
        return metadata;
    }
}
