using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateKeyManagement.Features
{
    /// <summary>
    /// D7: Key escrow and split-key recovery using M-of-N threshold scheme.
    /// Supports configurable escrow agents, recovery request workflows with approval,
    /// and agent notification system for recovery events.
    /// </summary>
    public sealed class KeyEscrowRecovery : IDisposable
    {
        private readonly BoundedDictionary<string, EscrowConfiguration> _escrowConfigs = new BoundedDictionary<string, EscrowConfiguration>(1000);
        private readonly BoundedDictionary<string, RecoveryRequest> _recoveryRequests = new BoundedDictionary<string, RecoveryRequest>(1000);
        private readonly BoundedDictionary<string, EscrowAgent> _agents = new BoundedDictionary<string, EscrowAgent>(1000);
        private readonly BoundedDictionary<string, EscrowedKeyInfo> _escrowedKeys = new BoundedDictionary<string, EscrowedKeyInfo>(1000);
        private readonly IKeyStore _keyStore;
        private readonly IMessageBus? _messageBus;
        private readonly SemaphoreSlim _escrowLock = new(1, 1);
        private bool _disposed;

        /// <summary>
        /// Event raised when a recovery request is created.
        /// </summary>
        public event EventHandler<RecoveryRequestEventArgs>? RecoveryRequested;

        /// <summary>
        /// Event raised when an agent provides approval.
        /// </summary>
        public event EventHandler<AgentApprovalEventArgs>? AgentApproved;

        /// <summary>
        /// Event raised when recovery is completed.
        /// </summary>
        public event EventHandler<RecoveryCompletedEventArgs>? RecoveryCompleted;

        /// <summary>
        /// Creates a new key escrow recovery service.
        /// </summary>
        public KeyEscrowRecovery(IKeyStore keyStore, IMessageBus? messageBus = null)
        {
            _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
            _messageBus = messageBus;
        }

        /// <summary>
        /// Registers an escrow agent.
        /// </summary>
        public async Task<EscrowAgent> RegisterAgentAsync(
            string agentId,
            EscrowAgentRegistration registration,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentNullException.ThrowIfNull(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can register escrow agents.");
            }

            var agent = new EscrowAgent
            {
                AgentId = agentId,
                Name = registration.Name,
                Email = registration.Email,
                NotificationMethod = registration.NotificationMethod,
                PublicKey = registration.PublicKey,
                RegisteredAt = DateTime.UtcNow,
                RegisteredBy = context.UserId,
                IsActive = true,
                Metadata = registration.Metadata ?? new Dictionary<string, object>()
            };

            _agents[agentId] = agent;

            await PublishEventAsync("escrow.agent.registered", new Dictionary<string, object>
            {
                ["agentId"] = agentId,
                ["name"] = agent.Name,
                ["registeredBy"] = context.UserId
            });

            return agent;
        }

        /// <summary>
        /// Creates an escrow configuration for M-of-N recovery.
        /// </summary>
        public async Task<EscrowConfiguration> CreateEscrowConfigurationAsync(
            string configId,
            EscrowConfigurationRequest request,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configId);
            ArgumentNullException.ThrowIfNull(request);

            if (request.RequiredApprovals < 1)
            {
                throw new ArgumentException("Required approvals must be at least 1.");
            }

            if (request.AgentIds.Count < request.RequiredApprovals)
            {
                throw new ArgumentException(
                    $"Number of agents ({request.AgentIds.Count}) must be >= required approvals ({request.RequiredApprovals}).");
            }

            // Validate all agents exist
            foreach (var agentId in request.AgentIds)
            {
                if (!_agents.ContainsKey(agentId))
                {
                    throw new KeyNotFoundException($"Escrow agent '{agentId}' not found.");
                }
            }

            var config = new EscrowConfiguration
            {
                ConfigId = configId,
                RequiredApprovals = request.RequiredApprovals,
                TotalAgents = request.AgentIds.Count,
                AgentIds = request.AgentIds.ToList(),
                CreatedAt = DateTime.UtcNow,
                CreatedBy = context.UserId,
                ApprovalTimeoutHours = request.ApprovalTimeoutHours,
                RequiresReason = request.RequiresReason,
                RequiresManagerApproval = request.RequiresManagerApproval,
                AllowedRecoveryContexts = request.AllowedRecoveryContexts ?? new List<string>()
            };

            _escrowConfigs[configId] = config;

            await PublishEventAsync("escrow.configuration.created", new Dictionary<string, object>
            {
                ["configId"] = configId,
                ["requiredApprovals"] = config.RequiredApprovals,
                ["totalAgents"] = config.TotalAgents,
                ["createdBy"] = context.UserId
            });

            return config;
        }

        /// <summary>
        /// Places a key in escrow using the specified configuration.
        /// </summary>
        public async Task<EscrowedKeyInfo> EscrowKeyAsync(
            string keyId,
            string configId,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
            ArgumentException.ThrowIfNullOrWhiteSpace(configId);

            if (!_escrowConfigs.TryGetValue(configId, out var config))
            {
                throw new KeyNotFoundException($"Escrow configuration '{configId}' not found.");
            }

            await _escrowLock.WaitAsync(ct);
            try
            {
                // Get the key to escrow
                var keyData = await _keyStore.GetKeyAsync(keyId, context);

                // Generate escrow ID
                var escrowId = GenerateEscrowId();

                // Split key into shares using Shamir's Secret Sharing
                var shares = SplitKeyIntoShares(keyData, config.RequiredApprovals, config.TotalAgents);

                // Encrypt each share for its respective agent
                var encryptedShares = new Dictionary<string, byte[]>();
                for (int i = 0; i < config.AgentIds.Count; i++)
                {
                    var agentId = config.AgentIds[i];
                    var agent = _agents[agentId];

                    // In production, encrypt with agent's public key
                    // Here we store the share directly (simplified)
                    encryptedShares[agentId] = shares[i];
                }

                var escrowInfo = new EscrowedKeyInfo
                {
                    EscrowId = escrowId,
                    KeyId = keyId,
                    ConfigId = configId,
                    EscrowedAt = DateTime.UtcNow,
                    EscrowedBy = context.UserId,
                    KeySizeBytes = keyData.Length,
                    ShareCount = config.TotalAgents,
                    ThresholdRequired = config.RequiredApprovals,
                    EncryptedShares = encryptedShares,
                    Status = EscrowStatus.Active
                };

                _escrowedKeys[escrowId] = escrowInfo;

                // Notify agents of new escrowed key
                await NotifyAgentsAsync(config.AgentIds, "escrow.key.added", new Dictionary<string, object>
                {
                    ["escrowId"] = escrowId,
                    ["keyId"] = keyId,
                    ["escrowedBy"] = context.UserId
                });

                await PublishEventAsync("escrow.key.escrowed", new Dictionary<string, object>
                {
                    ["escrowId"] = escrowId,
                    ["keyId"] = keyId,
                    ["configId"] = configId,
                    ["escrowedBy"] = context.UserId
                });

                return escrowInfo;
            }
            finally
            {
                _escrowLock.Release();
            }
        }

        /// <summary>
        /// Initiates a recovery request for an escrowed key.
        /// </summary>
        public async Task<RecoveryRequest> RequestRecoveryAsync(
            string escrowId,
            RecoveryRequestDetails details,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(escrowId);
            ArgumentNullException.ThrowIfNull(details);

            if (!_escrowedKeys.TryGetValue(escrowId, out var escrowInfo))
            {
                throw new KeyNotFoundException($"Escrowed key '{escrowId}' not found.");
            }

            if (!_escrowConfigs.TryGetValue(escrowInfo.ConfigId, out var config))
            {
                throw new KeyNotFoundException($"Escrow configuration '{escrowInfo.ConfigId}' not found.");
            }

            if (config.RequiresReason && string.IsNullOrWhiteSpace(details.Reason))
            {
                throw new ArgumentException("Recovery reason is required by escrow policy.");
            }

            // Check if recovery context is allowed
            if (config.AllowedRecoveryContexts.Any() &&
                !config.AllowedRecoveryContexts.Contains(details.RecoveryContext ?? "default"))
            {
                throw new UnauthorizedAccessException(
                    $"Recovery context '{details.RecoveryContext}' is not allowed by escrow policy.");
            }

            await _escrowLock.WaitAsync(ct);
            try
            {
                var requestId = GenerateRequestId();
                var request = new RecoveryRequest
                {
                    RequestId = requestId,
                    EscrowId = escrowId,
                    KeyId = escrowInfo.KeyId,
                    ConfigId = escrowInfo.ConfigId,
                    RequestedAt = DateTime.UtcNow,
                    RequestedBy = context.UserId,
                    Reason = details.Reason,
                    RecoveryContext = details.RecoveryContext,
                    RequiredApprovals = config.RequiredApprovals,
                    ExpiresAt = DateTime.UtcNow.AddHours(config.ApprovalTimeoutHours),
                    Status = RecoveryRequestStatus.Pending,
                    Approvals = new List<AgentApproval>()
                };

                _recoveryRequests[requestId] = request;

                // Notify all agents of recovery request
                await NotifyAgentsOfRecoveryRequestAsync(config.AgentIds, request);

                RecoveryRequested?.Invoke(this, new RecoveryRequestEventArgs { Request = request });

                await PublishEventAsync("escrow.recovery.requested", new Dictionary<string, object>
                {
                    ["requestId"] = requestId,
                    ["escrowId"] = escrowId,
                    ["keyId"] = escrowInfo.KeyId,
                    ["requestedBy"] = context.UserId,
                    ["reason"] = details.Reason ?? ""
                });

                return request;
            }
            finally
            {
                _escrowLock.Release();
            }
        }

        /// <summary>
        /// Approves a recovery request as an escrow agent.
        /// </summary>
        public async Task<ApprovalResult> ApproveRecoveryAsync(
            string requestId,
            string agentId,
            ApprovalDetails details,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

            if (!_recoveryRequests.TryGetValue(requestId, out var request))
            {
                throw new KeyNotFoundException($"Recovery request '{requestId}' not found.");
            }

            if (!_agents.TryGetValue(agentId, out var agent))
            {
                throw new KeyNotFoundException($"Escrow agent '{agentId}' not found.");
            }

            if (request.Status != RecoveryRequestStatus.Pending)
            {
                return new ApprovalResult
                {
                    Success = false,
                    Message = $"Request is not pending. Current status: {request.Status}"
                };
            }

            if (request.ExpiresAt < DateTime.UtcNow)
            {
                request.Status = RecoveryRequestStatus.Expired;
                return new ApprovalResult
                {
                    Success = false,
                    Message = "Recovery request has expired."
                };
            }

            // Check if agent already approved
            if (request.Approvals.Any(a => a.AgentId == agentId))
            {
                return new ApprovalResult
                {
                    Success = false,
                    Message = "Agent has already provided approval for this request."
                };
            }

            await _escrowLock.WaitAsync(ct);
            try
            {
                // Get the agent's share
                var escrowInfo = _escrowedKeys[request.EscrowId];
                if (!escrowInfo.EncryptedShares.TryGetValue(agentId, out var share))
                {
                    return new ApprovalResult
                    {
                        Success = false,
                        Message = "Agent does not have a share for this escrowed key."
                    };
                }

                var approval = new AgentApproval
                {
                    AgentId = agentId,
                    ApprovedAt = DateTime.UtcNow,
                    Comment = details.Comment,
                    Share = share // In production, this would be decrypted by the agent
                };

                request.Approvals.Add(approval);

                AgentApproved?.Invoke(this, new AgentApprovalEventArgs
                {
                    RequestId = requestId,
                    AgentId = agentId,
                    TotalApprovals = request.Approvals.Count,
                    RequiredApprovals = request.RequiredApprovals
                });

                await PublishEventAsync("escrow.recovery.approved", new Dictionary<string, object>
                {
                    ["requestId"] = requestId,
                    ["agentId"] = agentId,
                    ["approvalCount"] = request.Approvals.Count,
                    ["requiredApprovals"] = request.RequiredApprovals
                });

                // Check if we have enough approvals
                if (request.Approvals.Count >= request.RequiredApprovals)
                {
                    request.Status = RecoveryRequestStatus.Approved;
                }

                return new ApprovalResult
                {
                    Success = true,
                    Message = request.Status == RecoveryRequestStatus.Approved
                        ? "Approval recorded. Recovery is now authorized."
                        : $"Approval recorded. {request.RequiredApprovals - request.Approvals.Count} more approvals needed.",
                    ApprovalCount = request.Approvals.Count,
                    RequiredApprovals = request.RequiredApprovals,
                    RecoveryAuthorized = request.Status == RecoveryRequestStatus.Approved
                };
            }
            finally
            {
                _escrowLock.Release();
            }
        }

        /// <summary>
        /// Denies a recovery request as an escrow agent.
        /// </summary>
        public async Task<bool> DenyRecoveryAsync(
            string requestId,
            string agentId,
            string reason,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            if (!_recoveryRequests.TryGetValue(requestId, out var request))
            {
                return false;
            }

            if (request.Status != RecoveryRequestStatus.Pending)
            {
                return false;
            }

            await _escrowLock.WaitAsync(ct);
            try
            {
                request.Status = RecoveryRequestStatus.Denied;
                request.DeniedBy = agentId;
                request.DenialReason = reason;
                request.DeniedAt = DateTime.UtcNow;

                await PublishEventAsync("escrow.recovery.denied", new Dictionary<string, object>
                {
                    ["requestId"] = requestId,
                    ["agentId"] = agentId,
                    ["reason"] = reason
                });

                return true;
            }
            finally
            {
                _escrowLock.Release();
            }
        }

        /// <summary>
        /// Executes recovery once sufficient approvals are obtained.
        /// </summary>
        public async Task<RecoveryResult> ExecuteRecoveryAsync(
            string requestId,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

            if (!_recoveryRequests.TryGetValue(requestId, out var request))
            {
                throw new KeyNotFoundException($"Recovery request '{requestId}' not found.");
            }

            if (request.Status != RecoveryRequestStatus.Approved)
            {
                return new RecoveryResult
                {
                    Success = false,
                    Message = $"Recovery not authorized. Status: {request.Status}"
                };
            }

            await _escrowLock.WaitAsync(ct);
            try
            {
                var escrowInfo = _escrowedKeys[request.EscrowId];

                // Collect shares from approvals
                var shares = request.Approvals
                    .Where(a => a.Share != null)
                    .Select((a, i) => (Index: i + 1, Share: a.Share!))
                    .ToList();

                if (shares.Count < escrowInfo.ThresholdRequired)
                {
                    return new RecoveryResult
                    {
                        Success = false,
                        Message = $"Insufficient shares. Have {shares.Count}, need {escrowInfo.ThresholdRequired}."
                    };
                }

                // Reconstruct the key
                var recoveredKey = ReconstructKeyFromShares(
                    shares.Take(escrowInfo.ThresholdRequired).ToList(),
                    escrowInfo.KeySizeBytes);

                request.Status = RecoveryRequestStatus.Completed;
                request.CompletedAt = DateTime.UtcNow;
                request.CompletedBy = context.UserId;

                RecoveryCompleted?.Invoke(this, new RecoveryCompletedEventArgs
                {
                    RequestId = requestId,
                    KeyId = request.KeyId,
                    RecoveredBy = context.UserId
                });

                await PublishEventAsync("escrow.recovery.completed", new Dictionary<string, object>
                {
                    ["requestId"] = requestId,
                    ["escrowId"] = request.EscrowId,
                    ["keyId"] = request.KeyId,
                    ["recoveredBy"] = context.UserId
                });

                // Notify all agents of completed recovery
                var config = _escrowConfigs[request.ConfigId];
                await NotifyAgentsAsync(config.AgentIds, "escrow.recovery.executed", new Dictionary<string, object>
                {
                    ["requestId"] = requestId,
                    ["keyId"] = request.KeyId,
                    ["recoveredBy"] = context.UserId,
                    ["completedAt"] = DateTime.UtcNow
                });

                return new RecoveryResult
                {
                    Success = true,
                    Message = "Key successfully recovered.",
                    RecoveredKey = recoveredKey,
                    RequestId = requestId,
                    KeyId = request.KeyId
                };
            }
            finally
            {
                _escrowLock.Release();
            }
        }

        /// <summary>
        /// Gets the status of a recovery request.
        /// </summary>
        public RecoveryRequestStatus? GetRecoveryStatus(string requestId)
        {
            return _recoveryRequests.TryGetValue(requestId, out var request)
                ? request.Status
                : null;
        }

        /// <summary>
        /// Gets all recovery requests for review.
        /// </summary>
        public IReadOnlyList<RecoveryRequest> GetPendingRecoveryRequests(string? agentId = null)
        {
            var requests = _recoveryRequests.Values
                .Where(r => r.Status == RecoveryRequestStatus.Pending);

            if (!string.IsNullOrEmpty(agentId))
            {
                requests = requests.Where(r =>
                {
                    var config = _escrowConfigs[r.ConfigId];
                    return config.AgentIds.Contains(agentId) &&
                           !r.Approvals.Any(a => a.AgentId == agentId);
                });
            }

            return requests.OrderBy(r => r.RequestedAt).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets all registered escrow agents.
        /// </summary>
        public IReadOnlyList<EscrowAgent> GetAgents(bool activeOnly = true)
        {
            var agents = _agents.Values.AsEnumerable();
            if (activeOnly)
            {
                agents = agents.Where(a => a.IsActive);
            }
            return agents.ToList().AsReadOnly();
        }

        /// <summary>
        /// Deactivates an escrow agent.
        /// </summary>
        public async Task<bool> DeactivateAgentAsync(
            string agentId,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can deactivate agents.");
            }

            if (_agents.TryGetValue(agentId, out var agent))
            {
                agent.IsActive = false;
                agent.DeactivatedAt = DateTime.UtcNow;
                agent.DeactivatedBy = context.UserId;

                await PublishEventAsync("escrow.agent.deactivated", new Dictionary<string, object>
                {
                    ["agentId"] = agentId,
                    ["deactivatedBy"] = context.UserId
                });

                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets escrow information for a key.
        /// </summary>
        public EscrowedKeyInfo? GetEscrowInfo(string escrowId)
        {
            return _escrowedKeys.TryGetValue(escrowId, out var info) ? info : null;
        }

        /// <summary>
        /// Lists all escrowed keys.
        /// </summary>
        public IReadOnlyList<EscrowedKeyInfo> GetEscrowedKeys()
        {
            return _escrowedKeys.Values
                .OrderByDescending(e => e.EscrowedAt)
                .ToList()
                .AsReadOnly();
        }

        #region Shamir Secret Sharing (Simplified)

        /// <summary>
        /// Splits a key into shares using Shamir's Secret Sharing.
        /// This is a simplified implementation - production should use BouncyCastle's full implementation.
        /// </summary>
        private List<byte[]> SplitKeyIntoShares(byte[] key, int threshold, int totalShares)
        {
            var shares = new List<byte[]>();

            // For each byte position in the key
            var shareArrays = new List<byte[]>();
            for (int s = 0; s < totalShares; s++)
            {
                shareArrays.Add(new byte[key.Length + 1]); // +1 for share index
                shareArrays[s][0] = (byte)(s + 1); // Share index (1-based)
            }

            // Generate random coefficients and compute shares
            using var rng = RandomNumberGenerator.Create();
            var coefficients = new byte[threshold];

            for (int byteIndex = 0; byteIndex < key.Length; byteIndex++)
            {
                coefficients[0] = key[byteIndex]; // Secret is coefficient[0]

                // Generate random coefficients
                for (int c = 1; c < threshold; c++)
                {
                    var randomByte = new byte[1];
                    rng.GetBytes(randomByte);
                    coefficients[c] = randomByte[0];
                }

                // Evaluate polynomial at each share point
                for (int s = 0; s < totalShares; s++)
                {
                    int x = s + 1;
                    int y = 0;
                    int xPower = 1;

                    for (int c = 0; c < threshold; c++)
                    {
                        y = (y + coefficients[c] * xPower) % 256;
                        xPower = (xPower * x) % 256;
                    }

                    shareArrays[s][byteIndex + 1] = (byte)y;
                }
            }

            return shareArrays;
        }

        /// <summary>
        /// Reconstructs the key from shares using Lagrange interpolation.
        /// </summary>
        private byte[] ReconstructKeyFromShares(List<(int Index, byte[] Share)> shares, int keySize)
        {
            var result = new byte[keySize];

            // For each byte position in the key
            for (int byteIndex = 0; byteIndex < keySize; byteIndex++)
            {
                // Lagrange interpolation at x=0
                int secret = 0;

                for (int i = 0; i < shares.Count; i++)
                {
                    int xi = shares[i].Index;
                    int yi = shares[i].Share[byteIndex + 1]; // +1 to skip index byte

                    // Calculate Lagrange basis polynomial at x=0
                    int numerator = 1;
                    int denominator = 1;

                    for (int j = 0; j < shares.Count; j++)
                    {
                        if (i == j) continue;
                        int xj = shares[j].Index;

                        numerator = (numerator * (0 - xj)) % 256;
                        denominator = (denominator * (xi - xj)) % 256;
                    }

                    // Handle negative modulo
                    numerator = ((numerator % 256) + 256) % 256;
                    denominator = ((denominator % 256) + 256) % 256;

                    // Modular inverse of denominator
                    int denominatorInverse = ModInverse(denominator, 256);

                    int lagrangeCoeff = (numerator * denominatorInverse) % 256;
                    secret = (secret + yi * lagrangeCoeff) % 256;
                }

                result[byteIndex] = (byte)(((secret % 256) + 256) % 256);
            }

            return result;
        }

        private int ModInverse(int a, int m)
        {
            a = ((a % m) + m) % m;
            for (int x = 1; x < m; x++)
            {
                if ((a * x) % m == 1)
                    return x;
            }
            return 1;
        }

        #endregion

        #region Helper Methods

        private string GenerateEscrowId()
        {
            return $"escrow-{DateTime.UtcNow:yyyyMMddHHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        }

        private string GenerateRequestId()
        {
            return $"recovery-{DateTime.UtcNow:yyyyMMddHHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        }

        private async Task NotifyAgentsOfRecoveryRequestAsync(List<string> agentIds, RecoveryRequest request)
        {
            var notifications = new List<Task>();

            foreach (var agentId in agentIds)
            {
                if (_agents.TryGetValue(agentId, out var agent) && agent.IsActive)
                {
                    notifications.Add(NotifyAgentAsync(agent, "escrow.recovery.requested", new Dictionary<string, object>
                    {
                        ["requestId"] = request.RequestId,
                        ["keyId"] = request.KeyId,
                        ["requestedBy"] = request.RequestedBy ?? "unknown",
                        ["reason"] = request.Reason ?? "",
                        ["expiresAt"] = request.ExpiresAt
                    }));
                }
            }

            await Task.WhenAll(notifications);
        }

        private async Task NotifyAgentsAsync(List<string> agentIds, string eventType, Dictionary<string, object> data)
        {
            var notifications = agentIds
                .Where(id => _agents.TryGetValue(id, out var a) && a.IsActive)
                .Select(id => NotifyAgentAsync(_agents[id], eventType, data));

            await Task.WhenAll(notifications);
        }

        private async Task NotifyAgentAsync(EscrowAgent agent, string eventType, Dictionary<string, object> data)
        {
            // In production, this would send actual notifications (email, webhook, etc.)
            // based on agent.NotificationMethod

            if (_messageBus != null)
            {
                var message = new PluginMessage
                {
                    Type = $"agent.notification.{eventType}",
                    Payload = new Dictionary<string, object>(data)
                    {
                        ["agentId"] = agent.AgentId,
                        ["agentName"] = agent.Name,
                        ["notificationMethod"] = agent.NotificationMethod.ToString()
                    }
                };

                await _messageBus.PublishAsync($"agent.notification.{agent.AgentId}", message);
            }
        }

        private async Task PublishEventAsync(string eventType, Dictionary<string, object> data)
        {
            if (_messageBus == null)
                return;

            try
            {
                var message = new PluginMessage
                {
                    Type = eventType,
                    Payload = new Dictionary<string, object>(data)
                    {
                        ["timestamp"] = DateTime.UtcNow
                    }
                };

                await _messageBus.PublishAsync(eventType, message);
            }
            catch
            {
                // Best-effort event publishing
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _escrowConfigs.Clear();
            _recoveryRequests.Clear();
            _agents.Clear();
            _escrowedKeys.Clear();
            _escrowLock.Dispose();

            GC.SuppressFinalize(this);
        }
    }

    #region Supporting Types

    /// <summary>
    /// Escrow agent notification method.
    /// </summary>
    public enum NotificationMethod
    {
        Email,
        Webhook,
        SMS,
        PushNotification,
        MessageBus
    }

    /// <summary>
    /// Escrow key status.
    /// </summary>
    public enum EscrowStatus
    {
        Active,
        Recovered,
        Expired,
        Revoked
    }

    /// <summary>
    /// Recovery request status.
    /// </summary>
    public enum RecoveryRequestStatus
    {
        Pending,
        Approved,
        Denied,
        Expired,
        Completed,
        Cancelled
    }

    /// <summary>
    /// Escrow agent registration details.
    /// </summary>
    public class EscrowAgentRegistration
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public NotificationMethod NotificationMethod { get; set; } = NotificationMethod.Email;
        public byte[]? PublicKey { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    /// <summary>
    /// Registered escrow agent.
    /// </summary>
    public class EscrowAgent
    {
        public string AgentId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public NotificationMethod NotificationMethod { get; set; }
        public byte[]? PublicKey { get; set; }
        public DateTime RegisteredAt { get; set; }
        public string? RegisteredBy { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime? DeactivatedAt { get; set; }
        public string? DeactivatedBy { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Escrow configuration request.
    /// </summary>
    public class EscrowConfigurationRequest
    {
        public int RequiredApprovals { get; set; }
        public List<string> AgentIds { get; set; } = new();
        public int ApprovalTimeoutHours { get; set; } = 72;
        public bool RequiresReason { get; set; } = true;
        public bool RequiresManagerApproval { get; set; }
        public List<string>? AllowedRecoveryContexts { get; set; }
    }

    /// <summary>
    /// Escrow configuration.
    /// </summary>
    public class EscrowConfiguration
    {
        public string ConfigId { get; set; } = "";
        public int RequiredApprovals { get; set; }
        public int TotalAgents { get; set; }
        public List<string> AgentIds { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public int ApprovalTimeoutHours { get; set; }
        public bool RequiresReason { get; set; }
        public bool RequiresManagerApproval { get; set; }
        public List<string> AllowedRecoveryContexts { get; set; } = new();
    }

    /// <summary>
    /// Escrowed key information.
    /// </summary>
    public class EscrowedKeyInfo
    {
        public string EscrowId { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string ConfigId { get; set; } = "";
        public DateTime EscrowedAt { get; set; }
        public string? EscrowedBy { get; set; }
        public int KeySizeBytes { get; set; }
        public int ShareCount { get; set; }
        public int ThresholdRequired { get; set; }
        public Dictionary<string, byte[]> EncryptedShares { get; set; } = new();
        public EscrowStatus Status { get; set; }
    }

    /// <summary>
    /// Recovery request details.
    /// </summary>
    public class RecoveryRequestDetails
    {
        public string? Reason { get; set; }
        public string? RecoveryContext { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    /// <summary>
    /// Recovery request.
    /// </summary>
    public class RecoveryRequest
    {
        public string RequestId { get; set; } = "";
        public string EscrowId { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string ConfigId { get; set; } = "";
        public DateTime RequestedAt { get; set; }
        public string? RequestedBy { get; set; }
        public string? Reason { get; set; }
        public string? RecoveryContext { get; set; }
        public int RequiredApprovals { get; set; }
        public DateTime ExpiresAt { get; set; }
        public RecoveryRequestStatus Status { get; set; }
        public List<AgentApproval> Approvals { get; set; } = new();
        public string? DeniedBy { get; set; }
        public string? DenialReason { get; set; }
        public DateTime? DeniedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? CompletedBy { get; set; }
    }

    /// <summary>
    /// Agent approval for recovery.
    /// </summary>
    public class AgentApproval
    {
        public string AgentId { get; set; } = "";
        public DateTime ApprovedAt { get; set; }
        public string? Comment { get; set; }
        public byte[]? Share { get; set; }
    }

    /// <summary>
    /// Approval details provided by agent.
    /// </summary>
    public class ApprovalDetails
    {
        public string? Comment { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    /// <summary>
    /// Approval result.
    /// </summary>
    public class ApprovalResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public int ApprovalCount { get; set; }
        public int RequiredApprovals { get; set; }
        public bool RecoveryAuthorized { get; set; }
    }

    /// <summary>
    /// Recovery result.
    /// </summary>
    public class RecoveryResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public byte[]? RecoveredKey { get; set; }
        public string? RequestId { get; set; }
        public string? KeyId { get; set; }
    }

    /// <summary>
    /// Event args for recovery request.
    /// </summary>
    public class RecoveryRequestEventArgs : EventArgs
    {
        public required RecoveryRequest Request { get; set; }
    }

    /// <summary>
    /// Event args for agent approval.
    /// </summary>
    public class AgentApprovalEventArgs : EventArgs
    {
        public string RequestId { get; set; } = "";
        public string AgentId { get; set; } = "";
        public int TotalApprovals { get; set; }
        public int RequiredApprovals { get; set; }
    }

    /// <summary>
    /// Event args for recovery completion.
    /// </summary>
    public class RecoveryCompletedEventArgs : EventArgs
    {
        public string RequestId { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string? RecoveredBy { get; set; }
    }

    #endregion
}
