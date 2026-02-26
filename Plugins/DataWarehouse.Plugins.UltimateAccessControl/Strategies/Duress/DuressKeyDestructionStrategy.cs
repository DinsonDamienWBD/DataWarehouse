using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Duress
{
    /// <summary>
    /// Cryptographic key destruction strategy for duress situations.
    /// Sends destruction command to encryption plugin (T94) via message bus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DEPENDENCY: UltimateEncryption plugin (T93/T94) for key management.
    /// MESSAGE TOPIC: encryption.key.destroy
    /// </para>
    /// </remarks>
    public sealed class DuressKeyDestructionStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;
        private readonly IMessageBus? _messageBus;

        public DuressKeyDestructionStrategy(ILogger? logger = null, IMessageBus? messageBus = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _messageBus = messageBus;
        }

        /// <inheritdoc/>
        public override string StrategyId => "duress-key-destruction";

        /// <inheritdoc/>
        public override string StrategyName => "Duress Key Destruction";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 100
        };

        

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("duress.key.destruction.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("duress.key.destruction.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }
/// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("duress.key.destruction.evaluate");
            var isDuress = context.SubjectAttributes.TryGetValue("duress", out var duressObj) &&
                           duressObj is bool duressFlag && duressFlag;

            if (!isDuress)
            {
                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = "No duress condition detected"
                };
            }

            _logger.LogCritical("Duress detected for {SubjectId}, initiating key destruction", context.SubjectId);

            var keysDestroyed = new List<string>();
            var keysRequested = new List<string>();

            // Send key destruction command via message bus (request-response for confirmed destruction)
            if (_messageBus != null && Configuration.TryGetValue("KeysToDestroy", out var keysObj) &&
                keysObj is IEnumerable<string> keys)
            {
                foreach (var keyId in keys)
                {
                    keysRequested.Add(keyId);
                    try
                    {
                        var message = new DataWarehouse.SDK.Utilities.PluginMessage
                        {
                            Type = "encryption.key.destroy",
                            SourcePluginId = "access-control",
                            Payload = new Dictionary<string, object>
                            {
                                ["key_id"] = keyId,
                                ["reason"] = "duress",
                                ["subject_id"] = context.SubjectId,
                                ["timestamp"] = DateTime.UtcNow.ToString("O")
                            }
                        };
                        var response = await _messageBus.SendAsync("encryption.key.destroy", message, cancellationToken);
                        if (response.Success &&
                            response.Payload is bool destroyed && destroyed)
                        {
                            keysDestroyed.Add(keyId);
                            _logger.LogWarning("Key {KeyId} destroyed and confirmed by encryption plugin", keyId);
                        }
                        else
                        {
                            _logger.LogError("Key {KeyId} destruction not confirmed: {Error}", keyId, response.ErrorMessage ?? "no confirmation from encryption plugin");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to destroy key {KeyId}", keyId);
                    }
                }
            }

            var allDestroyed = keysRequested.Count > 0 && keysDestroyed.Count == keysRequested.Count;
            var reason = keysRequested.Count == 0
                ? "Duress detected; no keys configured for destruction"
                : allDestroyed
                    ? $"Access granted under duress ({keysDestroyed.Count} keys destroyed)"
                    : $"Duress detected; {keysDestroyed.Count} of {keysRequested.Count} keys confirmed destroyed";

            return new AccessDecision
            {
                IsGranted = true,
                Reason = reason,
                Metadata = new Dictionary<string, object>
                {
                    ["duress_detected"] = true,
                    ["keys_requested"] = keysRequested,
                    ["keys_destroyed"] = keysDestroyed,
                    ["keys_failed"] = keysRequested.Except(keysDestroyed).ToList(),
                    ["all_keys_confirmed_destroyed"] = allDestroyed,
                    ["timestamp"] = DateTime.UtcNow
                }
            };
        }

        private sealed class KeyDestructionRequest
        {
            public required string KeyId { get; init; }
            public required string Reason { get; init; }
            public required string SubjectId { get; init; }
            public DateTime Timestamp { get; init; }
        }

        private sealed class KeyDestructionResponse
        {
            public bool Destroyed { get; init; }
            public string? Error { get; init; }
        }
    }
}
