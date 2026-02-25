using System.Diagnostics;
using System.Reflection;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateDataTransit.Audit;
using DataWarehouse.Plugins.UltimateDataTransit.Layers;
using DataWarehouse.Plugins.UltimateDataTransit.QoS;

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
public sealed class UltimateDataTransitPlugin : DataTransitPluginBase, ITransitOrchestrator, IDisposable
{
    private readonly BoundedDictionary<string, ActiveTransfer> _activeTransfers = new BoundedDictionary<string, ActiveTransfer>(1000);
    private TransitAuditService? _auditService;
    private QoSThrottlingManager? _qosManager;
    private CostAwareRouter? _costRouter;
    private IDisposable? _transferRequestSubscription;
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
    /// Discovers all transit strategies in the current assembly via the inherited
    /// <see cref="DataTransitPluginBase.TransitStrategyRegistry"/>.
    /// </summary>
    public UltimateDataTransitPlugin()
    {
        TransitStrategyRegistry.DiscoverFromAssembly(Assembly.GetExecutingAssembly());
    }

    /// <inheritdoc/>
    protected override async Task OnStartCoreAsync(CancellationToken ct)
    {
        // Initialize audit service
        if (MessageBus != null)
        {
            _auditService = new TransitAuditService(MessageBus);
        }

        // Initialize QoS throttling with default configuration
        // Total: 100 MB/s, Critical: 50%/10MB min, High: 30%/5MB min, Normal: 15%/2MB min, Low: 5%/1MB min
        _qosManager = new QoSThrottlingManager(new QoSConfiguration
        {
            TotalBandwidthBytesPerSecond = 100L * 1024 * 1024, // 100 MB/s
            PriorityConfigs = new Dictionary<TransitPriority, PriorityConfig>
            {
                [TransitPriority.Critical] = new PriorityConfig
                {
                    WeightPercent = 0.50,
                    MinBandwidthBytesPerSecond = 10L * 1024 * 1024, // 10 MB/s min
                    MaxBandwidthBytesPerSecond = long.MaxValue
                },
                [TransitPriority.High] = new PriorityConfig
                {
                    WeightPercent = 0.30,
                    MinBandwidthBytesPerSecond = 5L * 1024 * 1024, // 5 MB/s min
                    MaxBandwidthBytesPerSecond = long.MaxValue
                },
                [TransitPriority.Normal] = new PriorityConfig
                {
                    WeightPercent = 0.15,
                    MinBandwidthBytesPerSecond = 2L * 1024 * 1024, // 2 MB/s min
                    MaxBandwidthBytesPerSecond = long.MaxValue
                },
                [TransitPriority.Low] = new PriorityConfig
                {
                    WeightPercent = 0.05,
                    MinBandwidthBytesPerSecond = 1L * 1024 * 1024, // 1 MB/s min
                    MaxBandwidthBytesPerSecond = long.MaxValue
                }
            }
        });

        // Initialize cost-aware router with balanced default policy
        _costRouter = new CostAwareRouter(RoutingPolicy.Balanced);

        // Register default cost profiles for each strategy type
        RegisterDefaultCostProfiles();

        // Configure Intelligence integration on all discovered strategies
        foreach (var strategy in TransitStrategyRegistry.GetAll())
        {
            if (strategy is DataTransitStrategyBase baseStrategy)
            {
                baseStrategy.ConfigureIntelligence(MessageBus);
            }
        }

        // Subscribe to cross-plugin transfer requests for inter-plugin transport delegation
        if (MessageBus != null)
        {
            _transferRequestSubscription = MessageBus.Subscribe(
                "transit.transfer.request",
                HandleTransferRequestAsync);

            // Publish registration events for each strategy
            foreach (var strategy in TransitStrategyRegistry.GetAll())
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

    /// <summary>
    /// Registers default cost profiles for common strategy types.
    /// HTTP strategies are free tier, FTP/SFTP are metered, P2P strategies are free.
    /// </summary>
    private void RegisterDefaultCostProfiles()
    {
        if (_costRouter == null) return;

        foreach (var strategy in TransitStrategyRegistry.GetAll())
        {
            var profile = strategy.StrategyId switch
            {
                var id when id.Contains("http", StringComparison.OrdinalIgnoreCase) => new TransitCostProfile
                {
                    CostPerGB = 0.00m,
                    FixedCostPerTransfer = 0.00m,
                    Tier = TransitCostTier.Free,
                    IsMetered = false
                },
                var id when id.Contains("ftp", StringComparison.OrdinalIgnoreCase) ||
                            id.Contains("sftp", StringComparison.OrdinalIgnoreCase) => new TransitCostProfile
                {
                    CostPerGB = 0.01m,
                    FixedCostPerTransfer = 0.001m,
                    Tier = TransitCostTier.Metered,
                    IsMetered = true
                },
                var id when id.Contains("grpc", StringComparison.OrdinalIgnoreCase) => new TransitCostProfile
                {
                    CostPerGB = 0.00m,
                    FixedCostPerTransfer = 0.00m,
                    Tier = TransitCostTier.Free,
                    IsMetered = false
                },
                var id when id.Contains("p2p", StringComparison.OrdinalIgnoreCase) ||
                            id.Contains("swarm", StringComparison.OrdinalIgnoreCase) => new TransitCostProfile
                {
                    CostPerGB = 0.00m,
                    FixedCostPerTransfer = 0.00m,
                    Tier = TransitCostTier.Free,
                    IsMetered = false
                },
                var id when id.Contains("store-and-forward", StringComparison.OrdinalIgnoreCase) ||
                            id.Contains("offline", StringComparison.OrdinalIgnoreCase) => new TransitCostProfile
                {
                    CostPerGB = 0.00m,
                    FixedCostPerTransfer = 0.05m,
                    Tier = TransitCostTier.Free,
                    IsMetered = false
                },
                _ => new TransitCostProfile
                {
                    CostPerGB = 0.005m,
                    FixedCostPerTransfer = 0.001m,
                    Tier = TransitCostTier.Metered,
                    IsMetered = true
                }
            };

            _costRouter.RegisterCostProfile(strategy.StrategyId, profile);
        }
    }

    /// <summary>
    /// Handles cross-plugin transfer requests received via the message bus.
    /// Parses the request from the message payload, executes the transfer using the
    /// orchestrator pipeline, and returns the result via message response.
    /// </summary>
    /// <param name="message">The incoming plugin message containing transfer request details.</param>
    /// <returns>A message response containing the transfer result.</returns>
    private async Task<MessageResponse> HandleTransferRequestAsync(PluginMessage message)
    {
        try
        {
            // Parse transfer request from message payload
            var payload = message.Payload;
            var sourceUri = payload.TryGetValue("sourceUri", out var srcObj) && srcObj is string srcStr
                ? new Uri(srcStr) : null;
            var destUri = payload.TryGetValue("destinationUri", out var dstObj) && dstObj is string dstStr
                ? new Uri(dstStr) : null;

            if (sourceUri == null || destUri == null)
            {
                return MessageResponse.Error("Missing sourceUri or destinationUri in transfer request.", "INVALID_REQUEST");
            }

            var sizeBytes = payload.TryGetValue("sizeBytes", out var sizeObj)
                ? Convert.ToInt64(sizeObj) : 0L;
            var preferredStrategy = payload.TryGetValue("preferredStrategy", out var stratObj)
                ? stratObj?.ToString() : null;
            var protocol = payload.TryGetValue("protocol", out var protoObj)
                ? protoObj?.ToString() : null;

            var request = new TransitRequest
            {
                TransferId = $"cross-plugin-{Guid.NewGuid():N}",
                Source = new TransitEndpoint { Uri = sourceUri, Protocol = protocol },
                Destination = new TransitEndpoint { Uri = destUri, Protocol = protocol },
                SizeBytes = sizeBytes
            };

            // Execute transfer using the full orchestrator pipeline
            var result = await TransferAsync(request, null, CancellationToken.None);

            return new MessageResponse
            {
                Success = result.Success,
                Payload = result,
                Metadata = new Dictionary<string, object>
                {
                    ["transferId"] = result.TransferId,
                    ["bytesTransferred"] = result.BytesTransferred,
                    ["strategyUsed"] = result.StrategyUsed ?? string.Empty
                }
            };
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Transfer request failed: {ex.Message}", "TRANSFER_FAILED");
        }
    }

    /// <inheritdoc/>
    protected override Task OnStopCoreAsync()
    {
        // Dispose subscription
        _transferRequestSubscription?.Dispose();
        _transferRequestSubscription = null;

        // Cancel all active transfers
        foreach (var kvp in _activeTransfers)
        {
            kvp.Value.CancellationTokenSource.Cancel();
        }

        _activeTransfers.Clear();

        // Dispose QoS manager
        _qosManager?.Dispose();
        _qosManager = null;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<IDataTransitStrategy> SelectStrategyAsync(TransitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var destinationProtocol = request.Destination.Protocol
            ?? request.Destination.Uri.Scheme;

        var candidates = TransitStrategyRegistry.GetAll();

        // Build list of available, protocol-matching strategies
        var availableStrategies = new List<IDataTransitStrategy>();
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

            availableStrategies.Add(strategy);
        }

        if (availableStrategies.Count == 0)
        {
            throw new InvalidOperationException(
                $"No available transit strategy found for protocol '{destinationProtocol}' " +
                $"and destination '{request.Destination.Uri}'.");
        }

        // If cost limit is set and cost router is available, use cost-aware route selection
        if (_costRouter != null && request.QoSPolicy?.CostLimit > 0)
        {
            var routes = availableStrategies.Select(s =>
            {
                var costProfile = _costRouter.GetCostProfile(s.StrategyId) ?? new TransitCostProfile();
                return new TransitRoute
                {
                    StrategyId = s.StrategyId,
                    Endpoint = request.Destination,
                    CostProfile = costProfile,
                    EstimatedThroughputBytesPerSec = s.Capabilities.SupportsStreaming ? 100_000_000 : 50_000_000,
                    EstimatedLatencyMs = s.Capabilities.SupportsStreaming ? 10 : 50
                };
            }).ToList();

            try
            {
                var selectedRoute = _costRouter.SelectRoute(routes, request, RoutingPolicy.CostCapped);

                // Audit cost-aware route selection
                _auditService?.LogEvent(new TransitAuditEntry
                {
                    TransferId = request.TransferId,
                    EventType = TransitAuditEventType.CostRouteSelected,
                    StrategyId = selectedRoute.StrategyId,
                    SourceEndpoint = request.Source.Uri.ToString(),
                    DestinationEndpoint = request.Destination.Uri.ToString(),
                    Details = new Dictionary<string, object>
                    {
                        ["costLimit"] = request.QoSPolicy.CostLimit,
                        ["selectedRoute"] = selectedRoute.StrategyId,
                        ["candidateCount"] = routes.Count
                    }
                });

                var costSelectedStrategy = availableStrategies
                    .FirstOrDefault(s => s.StrategyId == selectedRoute.StrategyId);
                if (costSelectedStrategy != null)
                    return costSelectedStrategy;
            }
            catch (InvalidOperationException ex)
            {

                // No routes within cost limit; fall through to standard scoring
                System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Standard scoring-based selection
        IDataTransitStrategy? bestStrategy = null;
        var bestScore = int.MinValue;

        foreach (var strategy in availableStrategies)
        {
            var score = ScoreStrategy(strategy, request);
            if (score > bestScore)
            {
                bestScore = score;
                bestStrategy = strategy;
            }
        }

        // Audit strategy selection
        if (bestStrategy != null)
        {
            _auditService?.LogEvent(new TransitAuditEntry
            {
                TransferId = request.TransferId,
                EventType = TransitAuditEventType.StrategySelected,
                StrategyId = bestStrategy.StrategyId,
                SourceEndpoint = request.Source.Uri.ToString(),
                DestinationEndpoint = request.Destination.Uri.ToString(),
                Details = new Dictionary<string, object>
                {
                    ["score"] = bestScore,
                    ["candidateCount"] = availableStrategies.Count
                }
            });
        }

        return bestStrategy!;
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

        // Audit: transfer started
        _auditService?.LogEvent(new TransitAuditEntry
        {
            TransferId = request.TransferId,
            EventType = TransitAuditEventType.TransferStarted,
            StrategyId = strategy.StrategyId,
            SourceEndpoint = request.Source.Uri.ToString(),
            DestinationEndpoint = request.Destination.Uri.ToString(),
            BytesTransferred = 0,
            Details = new Dictionary<string, object>
            {
                ["sizeBytes"] = request.SizeBytes,
                ["hasCompression"] = request.Layers?.EnableCompression == true,
                ["hasEncryption"] = request.Layers?.EnableEncryption == true,
                ["haQoS"] = request.QoSPolicy != null
            }
        });

        // Publish transfer started event to message bus
        await PublishTransferEventAsync(TransitMessageTopics.TransferStarted, new Dictionary<string, object>
        {
            ["transferId"] = request.TransferId,
            ["strategyId"] = strategy.StrategyId,
            ["source"] = request.Source.Uri.ToString(),
            ["destination"] = request.Destination.Uri.ToString(),
            ["sizeBytes"] = request.SizeBytes
        }, ct);

        var stopwatch = Stopwatch.StartNew();
        var currentRequest = request;

        try
        {
            // Apply QoS throttling if QoS policy is set
            if (_qosManager != null && request.QoSPolicy != null && request.DataStream != null)
            {
                var throttledStream = await _qosManager.CreateThrottledStreamAsync(
                    request.DataStream,
                    request.QoSPolicy.Priority,
                    cts.Token);

                currentRequest = request with { DataStream = throttledStream };

                _auditService?.LogEvent(new TransitAuditEntry
                {
                    TransferId = request.TransferId,
                    EventType = TransitAuditEventType.QoSEnforced,
                    StrategyId = strategy.StrategyId,
                    SourceEndpoint = request.Source.Uri.ToString(),
                    DestinationEndpoint = request.Destination.Uri.ToString(),
                    Details = new Dictionary<string, object>
                    {
                        ["priority"] = request.QoSPolicy.Priority.ToString(),
                        ["maxBandwidth"] = request.QoSPolicy.MaxBandwidthBytesPerSecond
                    }
                });
            }

            // Apply decorator layers: compression first, then encryption (per research pitfall 4)
            IDataTransitStrategy wrappedStrategy = strategy;
            if (currentRequest.Layers != null && MessageBus != null)
            {
                if (currentRequest.Layers.EnableCompression)
                {
                    wrappedStrategy = new CompressionInTransitLayer(wrappedStrategy, MessageBus);

                    _auditService?.LogEvent(new TransitAuditEntry
                    {
                        TransferId = request.TransferId,
                        EventType = TransitAuditEventType.LayerApplied,
                        StrategyId = strategy.StrategyId,
                        Details = new Dictionary<string, object>
                        {
                            ["layer"] = "compression",
                            ["algorithm"] = currentRequest.Layers.CompressionAlgorithm ?? "gzip"
                        }
                    });
                }

                if (currentRequest.Layers.EnableEncryption)
                {
                    wrappedStrategy = new EncryptionInTransitLayer(wrappedStrategy, MessageBus);

                    _auditService?.LogEvent(new TransitAuditEntry
                    {
                        TransferId = request.TransferId,
                        EventType = TransitAuditEventType.LayerApplied,
                        StrategyId = strategy.StrategyId,
                        Details = new Dictionary<string, object>
                        {
                            ["layer"] = "encryption",
                            ["algorithm"] = currentRequest.Layers.EncryptionAlgorithm ?? "aes-256-gcm"
                        }
                    });
                }
            }

            var result = await wrappedStrategy.TransferAsync(currentRequest, progress, cts.Token);
            stopwatch.Stop();

            if (result.Success)
            {
                // Audit: transfer completed
                _auditService?.LogEvent(new TransitAuditEntry
                {
                    TransferId = request.TransferId,
                    EventType = TransitAuditEventType.TransferCompleted,
                    StrategyId = strategy.StrategyId,
                    SourceEndpoint = request.Source.Uri.ToString(),
                    DestinationEndpoint = request.Destination.Uri.ToString(),
                    BytesTransferred = result.BytesTransferred,
                    Duration = stopwatch.Elapsed,
                    Success = true
                });

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
                // Audit: transfer failed
                _auditService?.LogEvent(new TransitAuditEntry
                {
                    TransferId = request.TransferId,
                    EventType = TransitAuditEventType.TransferFailed,
                    StrategyId = strategy.StrategyId,
                    SourceEndpoint = request.Source.Uri.ToString(),
                    DestinationEndpoint = request.Destination.Uri.ToString(),
                    BytesTransferred = result.BytesTransferred,
                    Duration = stopwatch.Elapsed,
                    Success = false,
                    ErrorMessage = result.ErrorMessage
                });

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
            _auditService?.LogEvent(new TransitAuditEntry
            {
                TransferId = request.TransferId,
                EventType = TransitAuditEventType.TransferCancelled,
                StrategyId = strategy.StrategyId,
                SourceEndpoint = request.Source.Uri.ToString(),
                DestinationEndpoint = request.Destination.Uri.ToString(),
                Duration = stopwatch.Elapsed,
                Success = false,
                ErrorMessage = "Transfer was cancelled."
            });

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
            _auditService?.LogEvent(new TransitAuditEntry
            {
                TransferId = request.TransferId,
                EventType = TransitAuditEventType.TransferFailed,
                StrategyId = strategy.StrategyId,
                SourceEndpoint = request.Source.Uri.ToString(),
                DestinationEndpoint = request.Destination.Uri.ToString(),
                Duration = stopwatch.Elapsed,
                Success = false,
                ErrorMessage = ex.Message
            });

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
        return TransitStrategyRegistry.GetAll();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<TransitHealthStatus>> GetHealthAsync(CancellationToken ct = default)
    {
        var results = new List<TransitHealthStatus>();

        foreach (var strategy in TransitStrategyRegistry.GetAll())
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
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    /// <summary>
    /// Releases resources used by the plugin.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
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
        base.Dispose(disposing);
    }

    #region Hierarchy DataTransitPluginBase Abstract Methods
    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> TransferAsync(string key, Dictionary<string, object> target, CancellationToken ct = default)
    {
        var result = new Dictionary<string, object> { ["key"] = key, ["status"] = "delegated-to-strategy", ["target"] = target };
        return Task.FromResult(result);
    }
    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> GetTransferStatusAsync(string transferId, CancellationToken ct = default)
    {
        var result = new Dictionary<string, object> { ["transferId"] = transferId };
        if (_activeTransfers.TryGetValue(transferId, out var transfer))
        {
            result["status"] = "active";
        }
        else
        {
            result["status"] = "unknown";
        }
        return Task.FromResult(result);
    }
    #endregion
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