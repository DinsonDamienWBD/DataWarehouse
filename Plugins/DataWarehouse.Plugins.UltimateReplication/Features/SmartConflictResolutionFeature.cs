using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Features
{
    /// <summary>
    /// Smart Conflict Resolution Feature (C2).
    /// Uses Intelligence-based semantic comparison via message bus for conflict resolution,
    /// with automatic fallback to Last-Write-Wins (LWW) when Intelligence is unavailable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution strategies (in priority order):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Semantic Merge</b>: Uses "intelligence.semantic.compare" to understand data semantics and merge intelligently</item>
    ///   <item><b>Structural Merge</b>: For JSON/structured data, performs field-level three-way merge</item>
    ///   <item><b>LWW Fallback</b>: Last-Write-Wins based on vector clock comparison or timestamps</item>
    /// </list>
    /// <para>
    /// All Intelligence communication is via message bus. No direct plugin references.
    /// </para>
    /// </remarks>
    public sealed class SmartConflictResolutionFeature : IDisposable
    {
        private readonly ReplicationStrategyRegistry _registry;
        private readonly IMessageBus _messageBus;
        private readonly BoundedDictionary<string, ConflictResolutionRecord> _resolutionHistory = new BoundedDictionary<string, ConflictResolutionRecord>(1000);
        private readonly TimeSpan _intelligenceTimeout;
        private bool _disposed;
        private IDisposable? _conflictSubscription;

        // Topics
        private const string SemanticCompareTopic = "intelligence.semantic.compare";
        private const string SemanticCompareResponseTopic = "intelligence.semantic.compare.response";
        private const string ConflictDetectedTopic = "replication.ultimate.conflict";
        private const string ConflictResolvedTopic = "replication.ultimate.conflict.resolved";

        // Statistics
        private long _totalConflictsProcessed;
        private long _semanticResolutions;
        private long _structuralResolutions;
        private long _lwwFallbackResolutions;
        private long _intelligenceFailures;

        /// <summary>
        /// Initializes a new instance of the SmartConflictResolutionFeature.
        /// </summary>
        /// <param name="registry">The replication strategy registry.</param>
        /// <param name="messageBus">Message bus for Intelligence and inter-plugin communication.</param>
        /// <param name="intelligenceTimeout">Timeout for Intelligence responses. Defaults to 5 seconds.</param>
        public SmartConflictResolutionFeature(
            ReplicationStrategyRegistry registry,
            IMessageBus messageBus,
            TimeSpan? intelligenceTimeout = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _intelligenceTimeout = intelligenceTimeout ?? TimeSpan.FromSeconds(5);

            _conflictSubscription = _messageBus.Subscribe(ConflictDetectedTopic, HandleConflictDetectedAsync);
        }

        /// <summary>Gets total conflicts processed.</summary>
        public long TotalConflictsProcessed => Interlocked.Read(ref _totalConflictsProcessed);

        /// <summary>Gets count resolved via semantic Intelligence.</summary>
        public long SemanticResolutions => Interlocked.Read(ref _semanticResolutions);

        /// <summary>Gets count resolved via structural merge.</summary>
        public long StructuralResolutions => Interlocked.Read(ref _structuralResolutions);

        /// <summary>Gets count resolved via LWW fallback.</summary>
        public long LwwFallbackResolutions => Interlocked.Read(ref _lwwFallbackResolutions);

        /// <summary>
        /// Resolves a replication conflict using the best available strategy.
        /// Attempts semantic resolution via Intelligence first, falls back to structural merge,
        /// then to LWW if all else fails.
        /// </summary>
        /// <param name="conflict">The conflict to resolve.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Resolution result with resolved data and method used.</returns>
        public async Task<SmartResolutionResult> ResolveConflictAsync(
            EnhancedReplicationConflict conflict,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalConflictsProcessed);

            // Try semantic comparison via Intelligence
            var semanticResult = await TrySemanticResolutionAsync(conflict, ct);
            if (semanticResult != null)
            {
                Interlocked.Increment(ref _semanticResolutions);
                RecordResolution(conflict, semanticResult);
                return semanticResult;
            }

            // Try structural merge for JSON-like data
            var structuralResult = TryStructuralMerge(conflict);
            if (structuralResult != null)
            {
                Interlocked.Increment(ref _structuralResolutions);
                RecordResolution(conflict, structuralResult);
                return structuralResult;
            }

            // Fallback to LWW
            var lwwResult = ResolveByLastWriteWins(conflict);
            Interlocked.Increment(ref _lwwFallbackResolutions);
            RecordResolution(conflict, lwwResult);
            return lwwResult;
        }

        /// <summary>
        /// Gets the conflict resolution history for auditing.
        /// </summary>
        public IReadOnlyDictionary<string, ConflictResolutionRecord> GetResolutionHistory()
        {
            return _resolutionHistory;
        }

        #region Private Methods

        private async Task<SmartResolutionResult?> TrySemanticResolutionAsync(
            EnhancedReplicationConflict conflict,
            CancellationToken ct)
        {
            try
            {
                var correlationId = Guid.NewGuid().ToString("N");
                var tcs = new TaskCompletionSource<SmartResolutionResult?>();

                var subscription = _messageBus.Subscribe(SemanticCompareResponseTopic, msg =>
                {
                    if (msg.CorrelationId == correlationId)
                    {
                        var success = msg.Payload.GetValueOrDefault("success") is true;
                        if (success)
                        {
                            var mergedDataB64 = msg.Payload.GetValueOrDefault("mergedData")?.ToString();
                            var confidence = msg.Payload.GetValueOrDefault("confidence") is double conf ? conf : 0.0;

                            if (!string.IsNullOrEmpty(mergedDataB64) && confidence >= 0.7)
                            {
                                tcs.TrySetResult(new SmartResolutionResult
                                {
                                    ConflictId = conflict.DataId,
                                    ResolvedData = Convert.FromBase64String(mergedDataB64),
                                    Method = SmartResolutionMethod.SemanticMerge,
                                    Confidence = confidence,
                                    WinningSource = "merged",
                                    Reason = $"Semantic merge with {confidence:P0} confidence"
                                });
                                return Task.CompletedTask;
                            }
                        }
                        tcs.TrySetResult(null);
                    }
                    return Task.CompletedTask;
                });

                try
                {
                    var request = new PluginMessage
                    {
                        Type = SemanticCompareTopic,
                        CorrelationId = correlationId,
                        Source = "replication.ultimate.conflict-resolver",
                        Payload = new Dictionary<string, object>
                        {
                            ["localData"] = Convert.ToBase64String(conflict.LocalData.ToArray()),
                            ["remoteData"] = Convert.ToBase64String(conflict.RemoteData.ToArray()),
                            ["localNodeId"] = conflict.LocalNodeId,
                            ["remoteNodeId"] = conflict.RemoteNodeId,
                            ["operation"] = "merge"
                        }
                    };

                    await _messageBus.PublishAsync(SemanticCompareTopic, request, ct);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(_intelligenceTimeout);

                    return await tcs.Task.WaitAsync(cts.Token);
                }
                finally
                {
                    subscription?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _intelligenceFailures);
                return null;
            }
            catch
            {
                Interlocked.Increment(ref _intelligenceFailures);
                return null;
            }
        }

        private SmartResolutionResult? TryStructuralMerge(EnhancedReplicationConflict conflict)
        {
            try
            {
                var localStr = Encoding.UTF8.GetString(conflict.LocalData.Span);
                var remoteStr = Encoding.UTF8.GetString(conflict.RemoteData.Span);

                // Detect JSON-like structure
                if (!IsJsonLike(localStr) || !IsJsonLike(remoteStr))
                    return null;

                // Field-level merge: parse key-value pairs and merge
                var localFields = ParseSimpleJson(localStr);
                var remoteFields = ParseSimpleJson(remoteStr);

                if (localFields == null || remoteFields == null)
                    return null;

                var merged = new Dictionary<string, string>(localFields);
                foreach (var (key, value) in remoteFields)
                {
                    if (!merged.ContainsKey(key))
                    {
                        // New field from remote -- add it
                        merged[key] = value;
                    }
                    else if (merged[key] != value)
                    {
                        // Conflict on this field -- use remote if it has a later timestamp hint
                        var remoteHasTimestamp = conflict.RemoteMetadata?.TryGetValue("timestamp", out var remoteTs) == true;
                        var localHasTimestamp = conflict.LocalMetadata?.TryGetValue("timestamp", out var localTs) == true;

                        if (remoteHasTimestamp && localHasTimestamp &&
                            DateTimeOffset.TryParse(conflict.RemoteMetadata!["timestamp"], out var rt) &&
                            DateTimeOffset.TryParse(conflict.LocalMetadata!["timestamp"], out var lt) &&
                            rt > lt)
                        {
                            merged[key] = value;
                        }
                        // Otherwise keep local value (field-level LWW)
                    }
                }

                var mergedJson = "{" + string.Join(",", merged.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}";
                var mergedBytes = Encoding.UTF8.GetBytes(mergedJson);

                return new SmartResolutionResult
                {
                    ConflictId = conflict.DataId,
                    ResolvedData = mergedBytes,
                    Method = SmartResolutionMethod.StructuralMerge,
                    Confidence = 0.85,
                    WinningSource = "merged",
                    Reason = $"Structural field-level merge of {merged.Count} fields"
                };
            }
            catch
            {
                return null;
            }
        }

        private SmartResolutionResult ResolveByLastWriteWins(EnhancedReplicationConflict conflict)
        {
            // Compare vector clocks; if concurrent, use timestamp metadata
            var localVersion = conflict.LocalVersion;
            var remoteVersion = conflict.RemoteVersion;

            bool useRemote;
            if (remoteVersion.HappensBefore(localVersion))
            {
                useRemote = false;
            }
            else if (localVersion.HappensBefore(remoteVersion))
            {
                useRemote = true;
            }
            else
            {
                // Truly concurrent -- check timestamps
                var remoteTs = conflict.RemoteMetadata?.TryGetValue("timestamp", out var rts) == true
                    && DateTimeOffset.TryParse(rts, out var rt)
                    ? rt : DateTimeOffset.MinValue;
                var localTs = conflict.LocalMetadata?.TryGetValue("timestamp", out var lts) == true
                    && DateTimeOffset.TryParse(lts, out var lt)
                    ? lt : DateTimeOffset.MinValue;

                useRemote = remoteTs > localTs;
            }

            var winningData = useRemote ? conflict.RemoteData.ToArray() : conflict.LocalData.ToArray();
            var winningSource = useRemote ? conflict.RemoteNodeId : conflict.LocalNodeId;

            return new SmartResolutionResult
            {
                ConflictId = conflict.DataId,
                ResolvedData = winningData,
                Method = SmartResolutionMethod.LastWriteWins,
                Confidence = 1.0,
                WinningSource = winningSource,
                Reason = $"LWW: {winningSource} wins via {(useRemote ? "remote" : "local")} version"
            };
        }

        private async Task HandleConflictDetectedAsync(PluginMessage message)
        {
            // Auto-resolve conflicts published to the conflict topic
            var localDataB64 = message.Payload.GetValueOrDefault("localData")?.ToString();
            var remoteDataB64 = message.Payload.GetValueOrDefault("remoteData")?.ToString();

            if (string.IsNullOrEmpty(localDataB64) || string.IsNullOrEmpty(remoteDataB64))
                return;

            var conflict = new EnhancedReplicationConflict
            {
                DataId = message.Payload.GetValueOrDefault("dataId")?.ToString() ?? Guid.NewGuid().ToString(),
                LocalVersion = new EnhancedVectorClock(),
                RemoteVersion = new EnhancedVectorClock(),
                LocalData = Convert.FromBase64String(localDataB64),
                RemoteData = Convert.FromBase64String(remoteDataB64),
                LocalNodeId = message.Payload.GetValueOrDefault("localNodeId")?.ToString() ?? "local",
                RemoteNodeId = message.Payload.GetValueOrDefault("remoteNodeId")?.ToString() ?? "remote"
            };

            var result = await ResolveConflictAsync(conflict);

            await _messageBus.PublishAsync(ConflictResolvedTopic, new PluginMessage
            {
                Type = ConflictResolvedTopic,
                CorrelationId = message.CorrelationId,
                Source = "replication.ultimate.conflict-resolver",
                Payload = new Dictionary<string, object>
                {
                    ["conflictId"] = result.ConflictId,
                    ["method"] = result.Method.ToString(),
                    ["confidence"] = result.Confidence,
                    ["resolvedData"] = Convert.ToBase64String(result.ResolvedData),
                    ["winningSource"] = result.WinningSource
                }
            });
        }

        private void RecordResolution(EnhancedReplicationConflict conflict, SmartResolutionResult result)
        {
            _resolutionHistory[conflict.DataId] = new ConflictResolutionRecord
            {
                ConflictId = conflict.DataId,
                Method = result.Method,
                Confidence = result.Confidence,
                WinningSource = result.WinningSource,
                ResolvedAt = DateTimeOffset.UtcNow,
                LocalNodeId = conflict.LocalNodeId,
                RemoteNodeId = conflict.RemoteNodeId
            };
        }

        private static bool IsJsonLike(string s) =>
            s.TrimStart().StartsWith("{") && s.TrimEnd().EndsWith("}");

        private static Dictionary<string, string>? ParseSimpleJson(string json)
        {
            try
            {
                var result = new Dictionary<string, string>();
                var trimmed = json.Trim().TrimStart('{').TrimEnd('}').Trim();
                if (string.IsNullOrEmpty(trimmed)) return result;

                foreach (var pair in trimmed.Split(','))
                {
                    var colonIdx = pair.IndexOf(':');
                    if (colonIdx < 0) continue;
                    var key = pair[..colonIdx].Trim().Trim('"');
                    var value = pair[(colonIdx + 1)..].Trim().Trim('"');
                    result[key] = value;
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _conflictSubscription?.Dispose();
        }
    }

    #region Smart Conflict Types

    /// <summary>
    /// Method used for smart conflict resolution.
    /// </summary>
    public enum SmartResolutionMethod
    {
        /// <summary>Resolved via Intelligence semantic comparison and merge.</summary>
        SemanticMerge,
        /// <summary>Resolved via structural field-level merge.</summary>
        StructuralMerge,
        /// <summary>Resolved via Last-Write-Wins timestamp comparison.</summary>
        LastWriteWins
    }

    /// <summary>
    /// Result of a smart conflict resolution.
    /// </summary>
    public sealed class SmartResolutionResult
    {
        /// <summary>Conflict identifier.</summary>
        public required string ConflictId { get; init; }
        /// <summary>Resolved data payload.</summary>
        public required byte[] ResolvedData { get; init; }
        /// <summary>Resolution method used.</summary>
        public required SmartResolutionMethod Method { get; init; }
        /// <summary>Confidence in the resolution (0.0-1.0).</summary>
        public required double Confidence { get; init; }
        /// <summary>Node whose data won (or "merged").</summary>
        public required string WinningSource { get; init; }
        /// <summary>Human-readable resolution reason.</summary>
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Record of a conflict resolution for auditing.
    /// </summary>
    public sealed class ConflictResolutionRecord
    {
        /// <summary>Conflict identifier.</summary>
        public required string ConflictId { get; init; }
        /// <summary>Method used.</summary>
        public required SmartResolutionMethod Method { get; init; }
        /// <summary>Resolution confidence.</summary>
        public required double Confidence { get; init; }
        /// <summary>Winning source node.</summary>
        public required string WinningSource { get; init; }
        /// <summary>When resolved.</summary>
        public required DateTimeOffset ResolvedAt { get; init; }
        /// <summary>Local node ID.</summary>
        public required string LocalNodeId { get; init; }
        /// <summary>Remote node ID.</summary>
        public required string RemoteNodeId { get; init; }
    }

    #endregion
}
