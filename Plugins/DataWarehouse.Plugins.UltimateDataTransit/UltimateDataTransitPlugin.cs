using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataTransit;

/// <summary>
/// Ultimate Data Transit Plugin -- comprehensive data transport orchestrator providing
/// strategy-based data transfer across multiple protocols.
/// </summary>
/// <remarks>
/// <para>
/// This plugin implements the orchestrator pattern for data transit, automatically discovering
/// and registering transit strategies via reflection. It provides intelligent strategy selection
/// based on endpoint protocol, data size, required capabilities, and strategy availability.
/// </para>
/// <para>
/// Supported direct transfer strategies:
/// - HTTP/2 via System.Net.Http with streaming
/// - HTTP/3 via System.Net.Http with QUIC
/// - gRPC via Grpc.Net.Client with bidirectional streaming
/// - FTP/FTPS via FluentFTP with async API
/// - SFTP via SSH.NET with SFTP client
/// - SCP/rsync via SSH.NET with rolling-hash incremental sync
/// </para>
/// <para>
/// Features:
/// - Auto-discovery of IDataTransitStrategy implementations
/// - Thread-safe strategy registry with protocol/capability filtering
/// - Intelligent strategy selection with scoring (protocol match, resumable, streaming, encryption)
/// - Active transfer tracking with cancellation support
/// - Message bus event publishing for transfer lifecycle
/// - Intelligence integration for AI-enhanced routing
/// </para>
/// </remarks>
internal sealed class UltimateDataTransitPlugin : FeaturePluginBase, ITransitOrchestrator, IDisposable
{
    private readonly TransitStrategyRegistry _registry;
    private readonly ConcurrentDictionary<string, ActiveTransfer> _activeTransfers = new();
    private bool _disposed;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.transit.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Data Transit";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="UltimateDataTransitPlugin"/> class.
    /// Creates the strategy registry and discovers all strategies in the current assembly.
    /// </summary>
    public UltimateDataTransitPlugin()
    {
        _registry = new TransitStrategyRegistry();
        _registry.DiscoverStrategies(Assembly.GetExecutingAssembly());
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        // Configure Intelligence integration on all discovered strategies
        foreach (var strategy in _registry.GetAll())
        {
            if (strategy is DataTransitStrategyBase baseStrategy)
            {
                baseStrategy.ConfigureIntelligence(MessageBus);
            }
        }

        // Publish registration events for each strategy
        if (MessageBus != null)
        {
            foreach (var strategy in _registry.GetAll())
            {
                var message = new PluginMessage
                {
                    Type = TransitMessageTopics.StrategyRegistered,
                    SourcePluginId = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["strategyId"] = strategy.StrategyId,
                        ["name"] = strategy.Name,
                        ["protocols"] = strategy.Capabilities.SupportedProtocols
                    }
                };
                await MessageBus.PublishAsync(TransitMessageTopics.StrategyRegistered, message, ct);
            }
        }
    }

    /// <inheritdoc/>
    public override Task StopAsync()
    {
        // Cancel all active transfers
        foreach (var kvp in _activeTransfers)
        {
            kvp.Value.CancellationTokenSource.Cancel();
        }

        _activeTransfers.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<IDataTransitStrategy> SelectStrategyAsync(TransitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var destinationProtocol = request.Destination.Protocol
            ?? request.Destination.Uri.Scheme;

        var candidates = _registry.GetAll();
        IDataTransitStrategy? bestStrategy = null;
        var bestScore = int.MinValue;

        foreach (var strategy in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Check protocol match first
            var protocolMatch = strategy.Capabilities.SupportedProtocols
                .Any(p => p.Equals(destinationProtocol, StringComparison.OrdinalIgnoreCase));

            if (!protocolMatch)
                continue;

            // Check availability
            var available = await strategy.IsAvailableAsync(request.Destination, ct);
            if (!available)
                continue;

            // Score the strategy
            var score = ScoreStrategy(strategy, request);
            if (score > bestScore)
            {
                bestScore = score;
                bestStrategy = strategy;
            }
        }

        if (bestStrategy == null)
        {
            throw new InvalidOperationException(
                $"No available transit strategy found for protocol '{destinationProtocol}' " +
                $"and destination '{request.Destination.Uri}'.");
        }

        return bestStrategy;
    }

    /// <inheritdoc/>
    public async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var strategy = await SelectStrategyAsync(request, ct);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activeTransfer = new ActiveTransfer(
            request.TransferId,
            strategy.StrategyId,
            DateTime.UtcNow,
            cts);

        _activeTransfers[request.TransferId] = activeTransfer;

        // Publish transfer started event
        await PublishTransferEventAsync(TransitMessageTopics.TransferStarted, new Dictionary<string, object>
        {
            ["transferId"] = request.TransferId,
            ["strategyId"] = strategy.StrategyId,
            ["source"] = request.Source.Uri.ToString(),
            ["destination"] = request.Destination.Uri.ToString(),
            ["sizeBytes"] = request.SizeBytes
        }, ct);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await strategy.TransferAsync(request, progress, cts.Token);
            stopwatch.Stop();

            if (result.Success)
            {
                await PublishTransferEventAsync(TransitMessageTopics.TransferCompleted, new Dictionary<string, object>
                {
                    ["transferId"] = request.TransferId,
                    ["bytesTransferred"] = result.BytesTransferred,
                    ["duration"] = result.Duration.TotalSeconds,
                    ["contentHash"] = result.ContentHash ?? string.Empty,
                    ["strategyUsed"] = result.StrategyUsed ?? strategy.StrategyId
                }, ct);
            }
            else
            {
                await PublishTransferEventAsync(TransitMessageTopics.TransferFailed, new Dictionary<string, object>
                {
                    ["transferId"] = request.TransferId,
                    ["errorMessage"] = result.ErrorMessage ?? "Unknown error",
                    ["strategyId"] = strategy.StrategyId
                }, ct);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            await PublishTransferEventAsync(TransitMessageTopics.TransferCancelled, new Dictionary<string, object>
            {
                ["transferId"] = request.TransferId,
                ["cancelledBy"] = "user"
            }, ct);

            return new TransitResult
            {
                TransferId = request.TransferId,
                Success = false,
                ErrorMessage = "Transfer was cancelled.",
                Duration = stopwatch.Elapsed,
                StrategyUsed = strategy.StrategyId
            };
        }
        catch (Exception ex)
        {
            await PublishTransferEventAsync(TransitMessageTopics.TransferFailed, new Dictionary<string, object>
            {
                ["transferId"] = request.TransferId,
                ["errorMessage"] = ex.Message,
                ["strategyId"] = strategy.StrategyId
            }, ct);

            return new TransitResult
            {
                TransferId = request.TransferId,
                Success = false,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed,
                StrategyUsed = strategy.StrategyId
            };
        }
        finally
        {
            _activeTransfers.TryRemove(request.TransferId, out _);
            cts.Dispose();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<IDataTransitStrategy> GetRegisteredStrategies()
    {
        return _registry.GetAll();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<TransitHealthStatus>> GetHealthAsync(CancellationToken ct = default)
    {
        var results = new List<TransitHealthStatus>();

        foreach (var strategy in _registry.GetAll())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var health = await strategy.GetHealthAsync(ct);
                results.Add(health);
            }
            catch (Exception ex)
            {
                results.Add(new TransitHealthStatus
                {
                    StrategyId = strategy.StrategyId,
                    IsHealthy = false,
                    LastCheckTime = DateTime.UtcNow,
                    ErrorMessage = ex.Message
                });
            }
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Scores a strategy against a transfer request for optimal selection.
    /// Higher scores indicate better fitness for the request.
    /// </summary>
    /// <param name="strategy">The strategy to score.</param>
    /// <param name="request">The transfer request to score against.</param>
    /// <returns>An integer score. Higher is better.</returns>
    private static int ScoreStrategy(IDataTransitStrategy strategy, TransitRequest request)
    {
        var score = 0;
        var caps = strategy.Capabilities;
        var destinationProtocol = request.Destination.Protocol
            ?? request.Destination.Uri.Scheme;

        // Protocol match gives base score
        if (caps.SupportedProtocols.Any(p => p.Equals(destinationProtocol, StringComparison.OrdinalIgnoreCase)))
        {
            score += 100;
        }

        // Resumable capability bonus for large files (>100MB)
        if (caps.SupportsResumable && request.SizeBytes > 100L * 1024 * 1024)
        {
            score += 50;
        }

        // Streaming support bonus
        if (caps.SupportsStreaming)
        {
            score += 30;
        }

        // Encryption support bonus if layers request it
        if (caps.SupportsEncryption && request.Layers?.EnableEncryption == true)
        {
            score += 20;
        }

        // Compression support bonus if layers request it
        if (caps.SupportsCompression && request.Layers?.EnableCompression == true)
        {
            score += 15;
        }

        // Delta support bonus for incremental transfers
        if (caps.SupportsDelta)
        {
            score += 10;
        }

        return score;
    }

    /// <summary>
    /// Publishes a transfer event to the message bus.
    /// </summary>
    /// <param name="topic">The message topic.</param>
    /// <param name="payload">The event payload.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task PublishTransferEventAsync(string topic, Dictionary<string, object> payload, CancellationToken ct)
    {
        if (MessageBus == null) return;

        try
        {
            var message = new PluginMessage
            {
                Type = topic,
                SourcePluginId = Id,
                Payload = payload
            };
            await MessageBus.PublishAsync(topic, message, ct);
        }
        catch
        {
            // Message bus publish failures should not fail the transfer
        }
    }

    /// <summary>
    /// Releases resources used by the plugin.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _activeTransfers)
        {
            kvp.Value.CancellationTokenSource.Cancel();
            kvp.Value.CancellationTokenSource.Dispose();
        }

        _activeTransfers.Clear();
    }
}

/// <summary>
/// Tracks an active transfer in progress.
/// </summary>
/// <param name="TransferId">Unique identifier for the transfer.</param>
/// <param name="StrategyId">The strategy executing the transfer.</param>
/// <param name="StartTime">When the transfer started.</param>
/// <param name="CancellationTokenSource">Token source for cancellation.</param>
internal sealed record ActiveTransfer(
    string TransferId,
    string StrategyId,
    DateTime StartTime,
    CancellationTokenSource CancellationTokenSource);
