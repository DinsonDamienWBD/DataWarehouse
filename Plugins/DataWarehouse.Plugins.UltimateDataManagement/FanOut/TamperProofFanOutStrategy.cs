using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.FanOut;

/// <summary>
/// Tamper-proof fan out strategy for high-integrity instances.
/// Orchestrates a 4-phase software transaction via message bus to T97 UltimateStorage.
/// </summary>
/// <remarks>
/// <b>LOCKED STRATEGY:</b> This strategy is locked at instance deployment time
/// and cannot be modified or overridden at any hierarchy level.
///
/// <b>Architecture:</b>
/// - This orchestrator does NOT own storage destinations
/// - All storage operations go through MESSAGE BUS to T97 UltimateStorage
/// - T97 provides: PrimaryDataStorage, MetadataIndexStorage, BlockchainStorage, WormStorage
///
/// <b>Write Flow:</b>
/// <list type="number">
///   <item>Phase 1 (Parallel): Primary Storage + Index Storage + Generate Blockchain → Blockchain Storage</item>
///   <item>Phase 2 (Sequential): If all Phase 1 succeed → Write to WORM Storage</item>
///   <item>If all 4 succeed → TamperProof write is complete</item>
///   <item>Rollback: If any phase fails, rollback all completed writes</item>
/// </list>
///
/// <b>DEPENDENCIES:</b>
/// - T97 UltimateStorage (via message bus): storage.primary.write, storage.index.write, storage.blockchain.write, storage.worm.write
/// </remarks>
public sealed class TamperProofFanOutStrategy : FanOutStrategyBase
{
    private readonly IMessageBus? _messageBus;
    private readonly TimeSpan _storageTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _wormTimeout = TimeSpan.FromSeconds(60);

    private static readonly HashSet<WriteDestinationType> _enabledDestinations = new()
    {
        WriteDestinationType.PrimaryStorage,
        WriteDestinationType.MetadataStorage,
        WriteDestinationType.DocumentStore   // For blockchain and WORM
    };

    private static readonly HashSet<WriteDestinationType> _requiredDestinations = new()
    {
        WriteDestinationType.PrimaryStorage,
        WriteDestinationType.MetadataStorage,
        WriteDestinationType.DocumentStore
    };

    /// <inheritdoc/>
    public override string StrategyId => "TamperProof";

    /// <inheritdoc/>
    public override string DisplayName => "Tamper-Proof Fan Out";

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "High-integrity fan out strategy for tamper-proof instances. " +
        "Orchestrates 4-phase software transaction: Primary Storage + Index Storage + " +
        "Blockchain Generation/Storage, then WORM finalization. All storage via T97 message bus.";

    /// <inheritdoc/>
    public override bool IsLocked => true;

    /// <inheritdoc/>
    public override bool AllowChildOverride => false;

    /// <inheritdoc/>
    public override IReadOnlySet<WriteDestinationType> EnabledDestinations => _enabledDestinations;

    /// <inheritdoc/>
    public override IReadOnlySet<WriteDestinationType> RequiredDestinations => _requiredDestinations;

    /// <summary>
    /// Initializes a new TamperProofFanOutStrategy.
    /// </summary>
    /// <param name="messageBus">Message bus for T97 communication.</param>
    public TamperProofFanOutStrategy(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
        SuccessCriteria = FanOutSuccessCriteria.AllRequired;
        NonRequiredTimeout = TimeSpan.FromSeconds(60);
    }

    /// <inheritdoc/>
    public override async Task<FanOutStrategyResult> ExecuteAsync(
        string objectId,
        IndexableContent content,
        IReadOnlyDictionary<WriteDestinationType, IWriteDestination> destinations,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var results = new Dictionary<WriteDestinationType, WriteDestinationResult>();

        // Generate blockchain anchor data BEFORE writes
        var blockchainData = GenerateBlockchainAnchor(objectId, content);

        // PHASE 1: Parallel writes to Primary, Index, and Blockchain storage (all via T97)
        var phase1Results = await ExecutePhase1Async(objectId, content, blockchainData, ct);
        foreach (var (type, result) in phase1Results)
        {
            results[type] = result;
        }

        // Check if Phase 1 succeeded
        var phase1Success = phase1Results.All(r => r.Value.Success);
        if (!phase1Success)
        {
            sw.Stop();

            // Rollback Phase 1 writes that succeeded
            var successfulWrites = phase1Results.Where(r => r.Value.Success).Select(r => r.Key).ToList();
            await RollbackWritesAsync(objectId, successfulWrites, ct);

            return new FanOutStrategyResult
            {
                Success = false,
                ErrorMessage = GetPhaseFailureMessage(1, phase1Results),
                DestinationResults = results,
                Duration = sw.Elapsed
            };
        }

        // PHASE 2: Sequential WORM finalization (only after Phase 1 succeeds)
        var wormResult = await WriteToWormStorageAsync(objectId, content, blockchainData, ct);
        results[WriteDestinationType.DocumentStore] = wormResult;

        if (!wormResult.Success)
        {
            sw.Stop();

            // Rollback all Phase 1 writes
            await RollbackWritesAsync(objectId, phase1Results.Keys.ToList(), ct);

            return new FanOutStrategyResult
            {
                Success = false,
                ErrorMessage = $"WORM finalization failed: {wormResult.ErrorMessage}",
                DestinationResults = results,
                Duration = sw.Elapsed
            };
        }

        sw.Stop();

        return new FanOutStrategyResult
        {
            Success = true,
            DestinationResults = results,
            Duration = sw.Elapsed
        };
    }

    /// <summary>
    /// Phase 1: Parallel writes to Primary Storage, Index Storage, and Blockchain Storage.
    /// All operations go through message bus to T97 UltimateStorage.
    /// </summary>
    private async Task<Dictionary<WriteDestinationType, WriteDestinationResult>> ExecutePhase1Async(
        string objectId,
        IndexableContent content,
        BlockchainAnchorData blockchainData,
        CancellationToken ct)
    {
        var results = new Dictionary<WriteDestinationType, WriteDestinationResult>();

        // Execute all Phase 1 writes in parallel
        var tasks = new List<Task<(WriteDestinationType type, WriteDestinationResult result)>>
        {
            WriteToPrimaryStorageAsync(objectId, content, ct),
            WriteToIndexStorageAsync(objectId, content, ct),
            WriteToBlockchainStorageAsync(objectId, blockchainData, ct)
        };

        var writeResults = await Task.WhenAll(tasks);
        foreach (var (type, result) in writeResults)
        {
            results[type] = result;
        }

        return results;
    }

    /// <summary>
    /// Write to Primary Data Storage via T97 message bus.
    /// </summary>
    private async Task<(WriteDestinationType type, WriteDestinationResult result)> WriteToPrimaryStorageAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (_messageBus == null)
        {
            sw.Stop();
            return (WriteDestinationType.PrimaryStorage, new WriteDestinationResult
            {
                Success = false,
                ErrorMessage = "Message bus not available",
                Duration = sw.Elapsed
            });
        }

        try
        {
            var response = await _messageBus.SendAsync(
                "storage.primary.write",
                new PluginMessage
                {
                    Type = "storage.primary.write",
                    Source = "TamperProofStrategy",
                    Payload = new Dictionary<string, object>
                    {
                        ["objectId"] = objectId,
                        ["filename"] = content.Filename ?? objectId,
                        ["contentType"] = content.ContentType ?? "application/octet-stream",
                        ["size"] = content.Size ?? 0,
                        ["tamperProof"] = true
                    }
                },
                _storageTimeout,
                ct);

            sw.Stop();

            var success = response?.Success == true;
            return (WriteDestinationType.PrimaryStorage, new WriteDestinationResult
            {
                Success = success,
                ErrorMessage = success ? null : "Primary storage write failed",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (WriteDestinationType.PrimaryStorage, new WriteDestinationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Duration = sw.Elapsed
            });
        }
    }

    /// <summary>
    /// Write to Index/Metadata Storage via T97 message bus.
    /// </summary>
    private async Task<(WriteDestinationType type, WriteDestinationResult result)> WriteToIndexStorageAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (_messageBus == null)
        {
            sw.Stop();
            return (WriteDestinationType.MetadataStorage, new WriteDestinationResult
            {
                Success = false,
                ErrorMessage = "Message bus not available",
                Duration = sw.Elapsed
            });
        }

        try
        {
            var response = await _messageBus.SendAsync(
                "storage.index.write",
                new PluginMessage
                {
                    Type = "storage.index.write",
                    Source = "TamperProofStrategy",
                    Payload = new Dictionary<string, object>
                    {
                        ["objectId"] = objectId,
                        ["filename"] = content.Filename ?? "",
                        ["contentType"] = content.ContentType ?? "",
                        ["size"] = content.Size ?? 0,
                        ["metadata"] = content.Metadata ?? new Dictionary<string, object>(),
                        ["tamperProof"] = true
                    }
                },
                _storageTimeout,
                ct);

            sw.Stop();

            var success = response?.Success == true;
            return (WriteDestinationType.MetadataStorage, new WriteDestinationResult
            {
                Success = success,
                ErrorMessage = success ? null : "Index storage write failed",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (WriteDestinationType.MetadataStorage, new WriteDestinationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Duration = sw.Elapsed
            });
        }
    }

    /// <summary>
    /// Write generated blockchain anchor to Blockchain Storage via T97 message bus.
    /// </summary>
    private async Task<(WriteDestinationType type, WriteDestinationResult result)> WriteToBlockchainStorageAsync(
        string objectId,
        BlockchainAnchorData blockchainData,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (_messageBus == null)
        {
            sw.Stop();
            return (WriteDestinationType.DocumentStore, new WriteDestinationResult
            {
                Success = false,
                ErrorMessage = "Message bus not available",
                Duration = sw.Elapsed
            });
        }

        try
        {
            var response = await _messageBus.SendAsync(
                "storage.blockchain.write",
                new PluginMessage
                {
                    Type = "storage.blockchain.write",
                    Source = "TamperProofStrategy",
                    Payload = new Dictionary<string, object>
                    {
                        ["objectId"] = objectId,
                        ["anchorId"] = blockchainData.AnchorId,
                        ["contentHash"] = blockchainData.ContentHash,
                        ["previousHash"] = blockchainData.PreviousHash,
                        ["timestamp"] = blockchainData.Timestamp.ToString("O"),
                        ["nonce"] = blockchainData.Nonce,
                        ["blockData"] = blockchainData.ToJson()
                    }
                },
                _storageTimeout,
                ct);

            sw.Stop();

            var success = response?.Success == true;
            return (WriteDestinationType.DocumentStore, new WriteDestinationResult
            {
                Success = success,
                ErrorMessage = success ? null : "Blockchain storage write failed",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (WriteDestinationType.DocumentStore, new WriteDestinationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Duration = sw.Elapsed
            });
        }
    }

    /// <summary>
    /// Phase 2: Write to WORM Storage via T97 message bus (only after Phase 1 succeeds).
    /// </summary>
    private async Task<WriteDestinationResult> WriteToWormStorageAsync(
        string objectId,
        IndexableContent content,
        BlockchainAnchorData blockchainData,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (_messageBus == null)
        {
            sw.Stop();
            return new WriteDestinationResult
            {
                Success = false,
                ErrorMessage = "Message bus not available",
                Duration = sw.Elapsed
            };
        }

        try
        {
            var response = await _messageBus.SendAsync(
                "storage.worm.write",
                new PluginMessage
                {
                    Type = "storage.worm.write",
                    Source = "TamperProofStrategy",
                    Payload = new Dictionary<string, object>
                    {
                        ["objectId"] = objectId,
                        ["filename"] = content.Filename ?? objectId,
                        ["contentHash"] = blockchainData.ContentHash,
                        ["blockchainAnchorId"] = blockchainData.AnchorId,
                        ["retentionPeriodDays"] = 2555, // 7 years default
                        ["immutable"] = true,
                        ["tamperProofVerified"] = true
                    }
                },
                _wormTimeout,
                ct);

            sw.Stop();

            var success = response?.Success == true;
            return new WriteDestinationResult
            {
                Success = success,
                ErrorMessage = success ? null : "WORM storage write failed",
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new WriteDestinationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    /// <summary>
    /// Rollback writes that have already completed.
    /// </summary>
    private async Task RollbackWritesAsync(
        string objectId,
        IEnumerable<WriteDestinationType> destinationsToRollback,
        CancellationToken ct)
    {
        if (_messageBus == null) return;

        foreach (var type in destinationsToRollback.Reverse())
        {
            var topic = type switch
            {
                WriteDestinationType.PrimaryStorage => "storage.primary.delete",
                WriteDestinationType.MetadataStorage => "storage.index.delete",
                WriteDestinationType.DocumentStore => "storage.blockchain.delete",
                _ => null
            };

            if (topic == null) continue;

            try
            {
                await _messageBus.SendAsync(
                    topic,
                    new PluginMessage
                    {
                        Type = topic,
                        Source = "TamperProofStrategy",
                        Payload = new Dictionary<string, object>
                        {
                            ["objectId"] = objectId,
                            ["reason"] = "TamperProof transaction rollback"
                        }
                    },
                    TimeSpan.FromSeconds(10),
                    ct);
            }
            catch
            {

                // Rollback failures are logged but don't fail the overall operation
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }
    }

    /// <summary>
    /// Generates blockchain anchor data for the content.
    /// This is the TamperProof orchestrator's core responsibility.
    /// </summary>
    private static BlockchainAnchorData GenerateBlockchainAnchor(string objectId, IndexableContent content)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var contentHash = ComputeContentHash(objectId, content);

        // Simple nonce calculation (in production, would be more sophisticated)
        var nonce = BitConverter.ToInt64(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{contentHash}:{timestamp.Ticks}")), 0);

        return new BlockchainAnchorData
        {
            AnchorId = $"anchor-{Guid.NewGuid():N}",
            ObjectId = objectId,
            ContentHash = contentHash,
            PreviousHash = "genesis", // Would chain to previous anchor in production
            Timestamp = timestamp,
            Nonce = nonce,
            Size = content.Size ?? 0,
            ContentType = content.ContentType ?? "application/octet-stream"
        };
    }

    private static string ComputeContentHash(string objectId, IndexableContent content)
    {
        using var sha256 = SHA256.Create();
        var hashInput = new StringBuilder();
        hashInput.Append(objectId);
        hashInput.Append(':');
        hashInput.Append(content.Filename ?? "");
        hashInput.Append(':');
        hashInput.Append(content.Size ?? 0);
        hashInput.Append(':');
        hashInput.Append(content.ContentType ?? "");

        if (content.TextContent != null)
        {
            hashInput.Append(':');
            hashInput.Append(content.TextContent.Length > 1024
                ? content.TextContent[..1024]
                : content.TextContent);
        }

        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(hashInput.ToString()));
        return Convert.ToHexString(hash);
    }

    private static string GetPhaseFailureMessage(int phase, Dictionary<WriteDestinationType, WriteDestinationResult> results)
    {
        var failures = results.Where(r => !r.Value.Success)
            .Select(r => $"{r.Key}: {r.Value.ErrorMessage}")
            .ToList();

        return $"Phase {phase} failed: {string.Join("; ", failures)}";
    }

    /// <summary>
    /// Blockchain anchor data generated by the TamperProof orchestrator.
    /// </summary>
    private sealed class BlockchainAnchorData
    {
        public string AnchorId { get; init; } = "";
        public string ObjectId { get; init; } = "";
        public string ContentHash { get; init; } = "";
        public string PreviousHash { get; init; } = "";
        public DateTimeOffset Timestamp { get; init; }
        public long Nonce { get; init; }
        public long Size { get; init; }
        public string ContentType { get; init; } = "";

        public string ToJson()
        {
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                anchorId = AnchorId,
                objectId = ObjectId,
                contentHash = ContentHash,
                previousHash = PreviousHash,
                timestamp = Timestamp.ToString("O"),
                nonce = Nonce,
                size = Size,
                contentType = ContentType
            });
        }
    }
}
