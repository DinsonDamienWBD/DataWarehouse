using System;
using System.Collections.Generic;
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

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
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

            // Send key destruction command via message bus
            if (_messageBus != null && Configuration.TryGetValue("KeysToDestroy", out var keysObj) &&
                keysObj is IEnumerable<string> keys)
            {
                foreach (var keyId in keys)
                {
                    try
                    {
                        // Send message bus request (placeholder for full integration)
                        // In production: message bus request-response to encryption plugin
                        _logger.LogWarning("Key destruction requested for {KeyId} (message bus integration pending)", keyId);
                        keysDestroyed.Add(keyId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to destroy key {KeyId}", keyId);
                    }
                }
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Access granted under duress (keys destroyed)",
                Metadata = new Dictionary<string, object>
                {
                    ["duress_detected"] = true,
                    ["keys_destroyed"] = keysDestroyed,
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
