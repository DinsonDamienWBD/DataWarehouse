using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Composition;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateDataIntegration.Composition
{
    /// <summary>
    /// Self-Evolving Schema Engine that orchestrates UltimateIntelligence pattern detection,
    /// SchemaEvolution compatibility checking, and LivingCatalog tracking via message bus.
    /// Implements COMP-01: automatic schema adaptation when data patterns change.
    /// </summary>
    /// <remarks>
    /// This engine wires together three capabilities:
    /// 1. UltimateIntelligence: Pattern detection via anomaly detection
    /// 2. SchemaEvolution: Forward-compatibility checking
    /// 3. LivingCatalog: Schema change tracking
    ///
    /// When >10% of ingested records contain new fields, the engine proposes a schema evolution.
    /// If auto-approve is enabled and the change is forward-compatible, it applies automatically.
    /// Otherwise, manual approval is required.
    ///
    /// Gracefully degrades when UltimateIntelligence is unavailable (manual proposal path still works).
    /// </remarks>
    [SdkCompatibility("3.0.0")]
    public sealed class SchemaEvolutionEngine : IDisposable
    {
        private readonly IMessageBus _messageBus;
        private readonly SchemaEvolutionEngineConfig _config;
        private readonly ILogger? _logger;

        // Bounded collections per Phase 23 memory safety
        private readonly BoundedDictionary<string, SchemaEvolutionProposal> _pendingProposals = new BoundedDictionary<string, SchemaEvolutionProposal>(1000);
        private readonly BoundedDictionary<string, List<SchemaEvolutionProposal>> _proposalHistory = new BoundedDictionary<string, List<SchemaEvolutionProposal>>(1000);
        private const int MaxHistoryPerSchema = 1000;

        private readonly List<IDisposable> _subscriptions = new();
        private Timer? _detectionTimer;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the SchemaEvolutionEngine.
        /// </summary>
        /// <param name="messageBus">Message bus for cross-plugin communication.</param>
        /// <param name="config">Engine configuration. Uses defaults if null.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public SchemaEvolutionEngine(
            IMessageBus messageBus,
            SchemaEvolutionEngineConfig? config = null,
            ILogger? logger = null)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _config = config ?? new SchemaEvolutionEngineConfig();
            _logger = logger;
        }

        /// <summary>
        /// Starts the schema evolution engine.
        /// Subscribes to message bus topics and begins periodic pattern detection.
        /// </summary>
        public Task StartAsync(CancellationToken ct = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SchemaEvolutionEngine));

            _logger?.LogInformation("Starting SchemaEvolutionEngine with threshold {Threshold}%",
                _config.ChangeThresholdPercent);

            // Subscribe to data ingestion events
            var ingestSub = _messageBus.Subscribe("composition.schema.data-ingested", HandleDataIngestedAsync);
            _subscriptions.Add(ingestSub);

            // Subscribe to intelligence anomaly detection responses
            var anomalySub = _messageBus.Subscribe("intelligence.connector.detect-anomaly.response", HandleAnomalyResponseAsync);
            _subscriptions.Add(anomalySub);

            // Subscribe to approved evolution events for applying changes
            var approvedSub = _messageBus.Subscribe("composition.schema.evolution-approved", HandleApprovedEvolutionAsync);
            _subscriptions.Add(approvedSub);

            // Start periodic pattern detection timer
            _detectionTimer = new Timer(
                // P2-2317: observe the returned Task so exceptions are not silently swallowed.
                callback: _ => _ = RequestPatternDetection(),
                state: null,
                dueTime: _config.DetectionInterval,
                period: _config.DetectionInterval);

            _logger?.LogInformation("SchemaEvolutionEngine started successfully");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stops the schema evolution engine.
        /// </summary>
        public Task StopAsync()
        {
            _logger?.LogInformation("Stopping SchemaEvolutionEngine");

            _detectionTimer?.Dispose();
            _detectionTimer = null;

            foreach (var sub in _subscriptions)
            {
                sub.Dispose();
            }
            _subscriptions.Clear();

            _logger?.LogInformation("SchemaEvolutionEngine stopped");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Evaluates a pending proposal for forward compatibility.
        /// If compatible and auto-approve is enabled, marks as AutoApproved.
        /// </summary>
        public async Task EvaluateProposalAsync(string proposalId, CancellationToken ct = default)
        {
            if (!_pendingProposals.TryGetValue(proposalId, out var proposal))
            {
                _logger?.LogWarning("Proposal {ProposalId} not found", proposalId);
                return;
            }

            _logger?.LogInformation("Evaluating proposal {ProposalId} for schema {SchemaId}",
                proposalId, proposal.SchemaId);

            try
            {
                // Build compatibility check request
                var checkMessage = PluginMessage.Create("composition.schema.check-compatibility", new Dictionary<string, object>
                {
                    ["schemaId"] = proposal.SchemaId,
                    ["proposalId"] = proposalId,
                    ["changes"] = proposal.DetectedChanges
                });

                // Send compatibility check request with timeout
                var response = await _messageBus.SendAsync(
                    "composition.schema.check-compatibility",
                    checkMessage,
                    TimeSpan.FromSeconds(30),
                    ct);

                if (response.Success && response.Payload is JsonElement element)
                {
                    var isCompatible = element.GetProperty("isCompatible").GetBoolean();

                    if (isCompatible && _config.AutoApproveForwardCompatible)
                    {
                        // Auto-approve forward-compatible changes
                        var approvedProposal = proposal with
                        {
                            Decision = SchemaEvolutionDecision.AutoApproved,
                            DecidedAt = DateTimeOffset.UtcNow
                        };

                        _pendingProposals[proposalId] = approvedProposal;

                        // Publish approval event
                        await _messageBus.PublishAsync("composition.schema.evolution-approved",
                            PluginMessage.Create("composition.schema.evolution-approved", new Dictionary<string, object>
                            {
                                ["proposalId"] = proposalId,
                                ["schemaId"] = proposal.SchemaId,
                                ["decision"] = "AutoApproved"
                            }), ct);

                        _logger?.LogInformation("Proposal {ProposalId} auto-approved (forward compatible)", proposalId);
                    }
                    else
                    {
                        _logger?.LogInformation("Proposal {ProposalId} requires manual approval (not forward compatible)", proposalId);
                    }
                }
                else
                {
                    _logger?.LogWarning("Compatibility check failed for proposal {ProposalId}: {Error}",
                        proposalId, response.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error evaluating proposal {ProposalId}", proposalId);
            }
        }

        /// <summary>
        /// Manually approves a pending proposal.
        /// </summary>
        public async Task ApproveProposalAsync(string proposalId, string approvedBy, CancellationToken ct = default)
        {
            if (!_pendingProposals.TryGetValue(proposalId, out var proposal))
            {
                _logger?.LogWarning("Proposal {ProposalId} not found for approval", proposalId);
                return;
            }

            var approvedProposal = proposal with
            {
                Decision = SchemaEvolutionDecision.ManuallyApproved,
                ApprovedBy = approvedBy,
                DecidedAt = DateTimeOffset.UtcNow
            };

            _pendingProposals[proposalId] = approvedProposal;

            await _messageBus.PublishAsync("composition.schema.evolution-approved",
                PluginMessage.Create("composition.schema.evolution-approved", new Dictionary<string, object>
                {
                    ["proposalId"] = proposalId,
                    ["schemaId"] = proposal.SchemaId,
                    ["decision"] = "ManuallyApproved",
                    ["approvedBy"] = approvedBy
                }), ct);

            _logger?.LogInformation("Proposal {ProposalId} manually approved by {User}", proposalId, approvedBy);
        }

        /// <summary>
        /// Rejects a pending proposal.
        /// </summary>
        public async Task RejectProposalAsync(string proposalId, string rejectedBy, CancellationToken ct = default)
        {
            if (!_pendingProposals.TryGetValue(proposalId, out var proposal))
            {
                _logger?.LogWarning("Proposal {ProposalId} not found for rejection", proposalId);
                return;
            }

            var rejectedProposal = proposal with
            {
                Decision = SchemaEvolutionDecision.Rejected,
                // P2-2302: use RejectedBy — not ApprovedBy — for rejection records
                RejectedBy = rejectedBy,
                DecidedAt = DateTimeOffset.UtcNow
            };

            _pendingProposals[proposalId] = rejectedProposal;

            await _messageBus.PublishAsync("composition.schema.evolution-rejected",
                PluginMessage.Create("composition.schema.evolution-rejected", new Dictionary<string, object>
                {
                    ["proposalId"] = proposalId,
                    ["schemaId"] = proposal.SchemaId,
                    ["rejectedBy"] = rejectedBy
                }), ct);

            // Move to history
            MoveToHistory(proposal.SchemaId, rejectedProposal);
            _pendingProposals.TryRemove(proposalId, out _);

            _logger?.LogInformation("Proposal {ProposalId} rejected by {User}", proposalId, rejectedBy);
        }

        /// <summary>
        /// Gets all pending proposals.
        /// </summary>
        public Task<IReadOnlyList<SchemaEvolutionProposal>> GetPendingProposalsAsync()
        {
            var proposals = _pendingProposals.Values.ToList();
            return Task.FromResult<IReadOnlyList<SchemaEvolutionProposal>>(proposals.AsReadOnly());
        }

        /// <summary>
        /// Gets proposal history for a specific schema.
        /// </summary>
        public Task<IReadOnlyList<SchemaEvolutionProposal>> GetProposalHistoryAsync(string schemaId)
        {
            if (_proposalHistory.TryGetValue(schemaId, out var history))
            {
                lock (history)
                {
                    return Task.FromResult<IReadOnlyList<SchemaEvolutionProposal>>(history.AsReadOnly());
                }
            }

            return Task.FromResult<IReadOnlyList<SchemaEvolutionProposal>>(Array.Empty<SchemaEvolutionProposal>());
        }

        /// <summary>
        /// Handles data ingestion events (currently a placeholder for future batching logic).
        /// </summary>
        private Task HandleDataIngestedAsync(PluginMessage message)
        {
            // Future enhancement: batch ingestion events and trigger pattern detection
            _logger?.LogDebug("Data ingestion event received");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles anomaly detection responses from UltimateIntelligence.
        /// Creates schema evolution proposals when pattern changes exceed threshold.
        /// </summary>
        private async Task HandleAnomalyResponseAsync(PluginMessage message)
        {
            try
            {
                var payload = message.Payload;
                if (!payload.ContainsKey("schemaId") || !payload.ContainsKey("changePercentage"))
                {
                    _logger?.LogDebug("Anomaly response missing required fields");
                    return;
                }

                var schemaId = payload["schemaId"]?.ToString() ?? "";
                var changePercentage = Convert.ToDouble(payload["changePercentage"]);

                if (changePercentage < _config.ChangeThresholdPercent)
                {
                    _logger?.LogDebug("Change percentage {Percent}% below threshold {Threshold}%",
                        changePercentage, _config.ChangeThresholdPercent);
                    return;
                }

                // Extract detected field changes
                var detectedChanges = new List<FieldChange>();
                if (payload.ContainsKey("detectedFields") && payload["detectedFields"] is JsonElement fieldsElement)
                {
                    foreach (var fieldElement in fieldsElement.EnumerateArray())
                    {
                        detectedChanges.Add(new FieldChange
                        {
                            FieldName = fieldElement.GetProperty("fieldName").GetString() ?? "",
                            ChangeType = Enum.Parse<FieldChangeType>(
                                fieldElement.GetProperty("changeType").GetString() ?? "Added"),
                            OldType = fieldElement.TryGetProperty("oldType", out var oldType)
                                ? oldType.GetString()
                                : null,
                            NewType = fieldElement.TryGetProperty("newType", out var newType)
                                ? newType.GetString()
                                : null,
                            DefaultValue = fieldElement.TryGetProperty("defaultValue", out var defVal)
                                ? defVal.ToString()
                                : null
                        });
                    }
                }

                // Enforce bounded collection limits
                if (detectedChanges.Count > _config.MaxFieldChangesPerProposal)
                {
                    _logger?.LogWarning("Detected {Count} field changes, truncating to {Max}",
                        detectedChanges.Count, _config.MaxFieldChangesPerProposal);
                    detectedChanges = detectedChanges.Take(_config.MaxFieldChangesPerProposal).ToList();
                }

                // Create proposal
                var proposal = new SchemaEvolutionProposal
                {
                    ProposalId = Guid.NewGuid().ToString("N"),
                    SchemaId = schemaId,
                    DetectedChanges = detectedChanges.AsReadOnly(),
                    ChangePercentage = changePercentage,
                    DetectedAt = DateTimeOffset.UtcNow
                };

                // Enforce max pending proposals limit
                if (_pendingProposals.Count >= _config.MaxPendingProposals)
                {
                    _logger?.LogWarning("Max pending proposals reached ({Max}), dropping oldest",
                        _config.MaxPendingProposals);
                    var oldest = _pendingProposals.Values.OrderBy(p => p.DetectedAt).First();
                    _pendingProposals.TryRemove(oldest.ProposalId, out _);
                }

                _pendingProposals[proposal.ProposalId] = proposal;

                // Publish proposal event
                await _messageBus.PublishAsync("composition.schema.evolution-proposed",
                    PluginMessage.Create("composition.schema.evolution-proposed", new Dictionary<string, object>
                    {
                        ["proposalId"] = proposal.ProposalId,
                        ["schemaId"] = proposal.SchemaId,
                        ["changePercentage"] = proposal.ChangePercentage,
                        ["detectedAt"] = proposal.DetectedAt
                    }));

                _logger?.LogInformation("Created proposal {ProposalId} for schema {SchemaId} ({Percent}% change)",
                    proposal.ProposalId, schemaId, changePercentage);

                // Auto-evaluate if configured
                if (_config.AutoApproveForwardCompatible)
                {
                    await EvaluateProposalAsync(proposal.ProposalId);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling anomaly response");
            }
        }

        /// <summary>
        /// Handles approved schema evolution events.
        /// Triggers migration and catalog updates.
        /// </summary>
        private async Task HandleApprovedEvolutionAsync(PluginMessage message)
        {
            try
            {
                var proposalId = message.Payload["proposalId"]?.ToString();
                if (string.IsNullOrEmpty(proposalId) || !_pendingProposals.TryGetValue(proposalId, out var proposal))
                {
                    _logger?.LogWarning("Approved evolution for unknown proposal {ProposalId}", proposalId);
                    return;
                }

                // Trigger schema migration
                await _messageBus.PublishAsync("composition.schema.apply-migration",
                    PluginMessage.Create("composition.schema.apply-migration", new Dictionary<string, object>
                    {
                        ["proposalId"] = proposalId,
                        ["schemaId"] = proposal.SchemaId,
                        ["changes"] = proposal.DetectedChanges
                    }));

                // Update living catalog
                await _messageBus.PublishAsync("composition.schema.update-catalog",
                    PluginMessage.Create("composition.schema.update-catalog", new Dictionary<string, object>
                    {
                        ["proposalId"] = proposalId,
                        ["schemaId"] = proposal.SchemaId,
                        ["decision"] = proposal.Decision.ToString(),
                        ["appliedAt"] = DateTimeOffset.UtcNow
                    }));

                // Move to history
                MoveToHistory(proposal.SchemaId, proposal);
                _pendingProposals.TryRemove(proposalId, out _);

                _logger?.LogInformation("Applied schema evolution for proposal {ProposalId}", proposalId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling approved evolution");
            }
        }

        /// <summary>
        /// Requests pattern detection from UltimateIntelligence via message bus.
        /// Gracefully handles unavailable intelligence plugin.
        /// </summary>
        private Task RequestPatternDetection()
        {
            return Task.Run(async () =>
            {
                try
                {
                    var message = PluginMessage.Create("intelligence.connector.detect-anomaly", new Dictionary<string, object>
                    {
                        ["source"] = "SchemaEvolutionEngine",
                        ["timestamp"] = DateTimeOffset.UtcNow
                    });

                    // Fire-and-forget pattern detection request
                    // If intelligence plugin is unavailable, this will timeout/fail silently
                    await _messageBus.PublishAsync("intelligence.connector.detect-anomaly", message);

                    _logger?.LogDebug("Pattern detection request sent");
                }
                catch (Exception ex)
                {
                    // Graceful degradation: log warning and continue
                    _logger?.LogWarning(ex, "Pattern detection request failed (intelligence plugin may be unavailable)");
                }
            });
        }

        /// <summary>
        /// Moves a proposal to history with bounded retention.
        /// </summary>
        private void MoveToHistory(string schemaId, SchemaEvolutionProposal proposal)
        {
            var history = _proposalHistory.GetOrAdd(schemaId, _ => new List<SchemaEvolutionProposal>());

            lock (history)
            {
                history.Add(proposal);

                // Enforce bounded history (oldest-first eviction)
                if (history.Count > MaxHistoryPerSchema)
                {
                    history.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Disposes resources used by the engine.
        /// </summary>
        public void Dispose()
        {
            // P2-2318: set _disposed first so concurrent Start/Stop cannot use resources
            // that are mid-disposal.
            if (_disposed) return;
            _disposed = true;

            _detectionTimer?.Dispose();
            foreach (var sub in _subscriptions)
            {
                sub.Dispose();
            }
            _subscriptions.Clear();
        }
    }
}
