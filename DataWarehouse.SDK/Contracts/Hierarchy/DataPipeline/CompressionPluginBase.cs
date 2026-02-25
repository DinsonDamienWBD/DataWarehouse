using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compression;
using DataWarehouse.SDK.Security;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base for compression pipeline plugins. Provides algorithm metadata,
/// compression level configuration, AI-driven algorithm selection, and strategy-dispatched
/// domain operations (CompressAsync/DecompressAsync).
/// </summary>
public abstract class CompressionPluginBase : DataTransformationPluginBase
{
    /// <inheritdoc/>
    public override string SubCategory => "Compression";

    /// <summary>Compression algorithm identifier (e.g., "LZ4", "Zstd", "Brotli").</summary>
    public abstract string CompressionAlgorithm { get; }

    /// <summary>Compression level (1-22, higher = better compression, slower).</summary>
    public virtual int CompressionLevel => 6;

    /// <inheritdoc/>
    /// <remarks>Default returns <see cref="CompressionAlgorithm"/> for compression-specific selection.</remarks>
    protected override Task<string> SelectOptimalAlgorithmAsync(Dictionary<string, object> context, CancellationToken ct = default)
        => Task.FromResult(CompressionAlgorithm);

    /// <summary>AI hook: Predict compression ratio before compressing.</summary>
    protected virtual Task<double> PredictCompressionRatioAsync(Dictionary<string, object> dataProfile, CancellationToken ct = default)
        => Task.FromResult(0.5);

    #region Typed Compression Strategy Registry

    /// <summary>
    /// Lazy-initialized typed registry for <see cref="ICompressionStrategy"/> instances.
    /// ICompressionStrategy does not inherit IStrategy, so it uses its own dedicated registry
    /// rather than the PluginBase IStrategy registry.
    /// </summary>
    private StrategyRegistry<ICompressionStrategy>? _compressionStrategyRegistry;

    /// <summary>Lock protecting lazy initialization of <see cref="_compressionStrategyRegistry"/>.</summary>
    private readonly object _compressionRegistryLock = new();

    /// <summary>
    /// Gets the typed compression strategy registry. Lazily initialized on first access.
    /// </summary>
    protected StrategyRegistry<ICompressionStrategy> CompressionStrategyRegistry
    {
        get
        {
            if (_compressionStrategyRegistry is not null) return _compressionStrategyRegistry;
            lock (_compressionRegistryLock)
            {
                _compressionStrategyRegistry ??= new StrategyRegistry<ICompressionStrategy>(
                    s => s.Characteristics.AlgorithmName.ToLowerInvariant().Replace(" ", "-"));
            }
            return _compressionStrategyRegistry;
        }
    }

    /// <summary>
    /// Registers a compression strategy with the typed registry.
    /// </summary>
    /// <param name="strategy">The compression strategy to register.</param>
    protected void RegisterCompressionStrategy(ICompressionStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        CompressionStrategyRegistry.Register(strategy);
    }

    /// <summary>
    /// Dispatches an operation to the optimal compression strategy, using AI-driven algorithm
    /// selection when no explicit strategy is specified. Routes through the typed
    /// <see cref="CompressionStrategyRegistry"/> with CommandIdentity ACL enforcement.
    /// </summary>
    /// <typeparam name="TResult">The operation result type.</typeparam>
    /// <param name="explicitStrategyId">Explicit strategy ID (null = use AI selection via SelectOptimalAlgorithmAsync).</param>
    /// <param name="identity">Optional CommandIdentity for ACL checks. When null, ACL is skipped.</param>
    /// <param name="dataContext">Context about the data for AI algorithm selection.</param>
    /// <param name="operation">The operation to execute on the resolved compression strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no strategy is found for the given ID.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the identity is denied by the ACL provider.</exception>
    protected async Task<TResult> DispatchCompressionStrategyAsync<TResult>(
        string? explicitStrategyId,
        CommandIdentity? identity,
        Dictionary<string, object>? dataContext,
        Func<ICompressionStrategy, Task<TResult>> operation,
        CancellationToken ct = default)
    {
        string strategyId;
        if (!string.IsNullOrEmpty(explicitStrategyId))
        {
            strategyId = explicitStrategyId;
        }
        else
        {
            // Delegate to AI hook -- SelectOptimalAlgorithmAsync is active code here
            strategyId = await SelectOptimalAlgorithmAsync(
                dataContext ?? new Dictionary<string, object>(), ct).ConfigureAwait(false);
        }

        // ACL check if provider is configured
        if (identity != null && StrategyAclProvider != null)
        {
            if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                throw new UnauthorizedAccessException(
                    $"Principal '{identity.EffectivePrincipalId}' is not authorized to use compression strategy '{strategyId}'.");
        }

        var strategy = CompressionStrategyRegistry.Get(strategyId)
            ?? throw new InvalidOperationException(
                $"Compression strategy '{strategyId}' is not registered. " +
                $"Call RegisterCompressionStrategy before dispatching.");

        return await operation(strategy).ConfigureAwait(false);
    }

    #endregion

    #region Domain Operations (Strategy-Dispatched)

    /// <summary>
    /// Compresses data using the specified or default compression strategy.
    /// Resolves the strategy from the typed <see cref="CompressionStrategyRegistry"/> with
    /// optional CommandIdentity ACL, falls back to <see cref="SelectOptimalAlgorithmAsync"/>
    /// for AI-driven algorithm selection when no strategy is specified.
    /// </summary>
    /// <param name="data">The data to compress.</param>
    /// <param name="strategyId">Optional strategy ID. When null, AI selection applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compressed data.</returns>
    protected async Task<byte[]> CompressAsync(
        byte[] data,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        return await DispatchCompressionStrategyAsync<byte[]>(
            strategyId,
            identity,
            new Dictionary<string, object>
            {
                ["operation"] = "compress",
                ["dataSize"] = data.Length,
                ["algorithm"] = CompressionAlgorithm
            },
            strategy => strategy.CompressAsync(data, ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Decompresses data using the specified or default compression strategy.
    /// Resolves the strategy from the typed <see cref="CompressionStrategyRegistry"/> with
    /// optional CommandIdentity ACL.
    /// </summary>
    /// <param name="compressedData">The compressed data to decompress.</param>
    /// <param name="strategyId">Optional strategy ID. When null, AI selection applies.</param>
    /// <param name="identity">Optional identity for ACL enforcement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Decompressed data.</returns>
    protected async Task<byte[]> DecompressAsync(
        byte[] compressedData,
        string? strategyId = null,
        CommandIdentity? identity = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(compressedData);

        return await DispatchCompressionStrategyAsync<byte[]>(
            strategyId,
            identity,
            new Dictionary<string, object>
            {
                ["operation"] = "decompress",
                ["dataSize"] = compressedData.Length,
                ["algorithm"] = CompressionAlgorithm
            },
            strategy => strategy.DecompressAsync(compressedData, ct),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>Returns <see cref="CompressionAlgorithm"/> to route compression-specific dispatches to the correct algorithm.</remarks>
    protected override string? GetDefaultStrategyId() => CompressionAlgorithm;

    #endregion

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["CompressionAlgorithm"] = CompressionAlgorithm;
        metadata["CompressionLevel"] = CompressionLevel;
        return metadata;
    }
}
