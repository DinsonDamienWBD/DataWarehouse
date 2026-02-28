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
    /// D8: Break-glass emergency access system with comprehensive audit logging.
    /// Provides emergency access to keys when normal access controls are unavailable,
    /// while maintaining strict audit trails and requiring multi-party authorization.
    /// Designed for disaster recovery, legal holds, and compliance emergencies.
    /// </summary>
    public sealed class BreakGlassAccess : IDisposable
    {
        private readonly BoundedDictionary<string, BreakGlassSession> _activeSessions = new BoundedDictionary<string, BreakGlassSession>(1000);
        // #3415: Compliance audit log must never silently evict entries.
        // Use ConcurrentDictionary (unbounded) so no audit entry is silently dropped.
        // Callers should periodically flush to durable storage to avoid unbounded growth.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BreakGlassAuditEntry> _auditLog = new();
        private readonly BoundedDictionary<string, EmergencyAccessPolicy> _policies = new BoundedDictionary<string, EmergencyAccessPolicy>(1000);
        private readonly BoundedDictionary<string, AuthorizedResponder> _responders = new BoundedDictionary<string, AuthorizedResponder>(1000);
        private readonly IKeyStore _keyStore;
        private readonly IMessageBus? _messageBus;
        private readonly SemaphoreSlim _accessLock = new(1, 1);
        private bool _disposed;

        /// <summary>
        /// Event raised when break-glass access is initiated.
        /// </summary>
        public event EventHandler<BreakGlassInitiatedEventArgs>? AccessInitiated;

        /// <summary>
        /// Event raised when break-glass access is authorized.
        /// </summary>
        public event EventHandler<BreakGlassAuthorizedEventArgs>? AccessAuthorized;

        /// <summary>
        /// Event raised when a key is accessed via break-glass.
        /// </summary>
        public event EventHandler<BreakGlassKeyAccessedEventArgs>? KeyAccessed;

        /// <summary>
        /// Event raised when break-glass session ends.
        /// </summary>
        public event EventHandler<BreakGlassSessionEndedEventArgs>? SessionEnded;

        /// <summary>
        /// Default session timeout duration.
        /// </summary>
        public TimeSpan DefaultSessionTimeout { get; set; } = TimeSpan.FromHours(4);

        /// <summary>
        /// Maximum keys accessible per session.
        /// </summary>
        public int MaxKeysPerSession { get; set; } = 100;

        /// <summary>
        /// Whether to require real-time notification to security team.
        /// </summary>
        public bool RequireSecurityNotification { get; set; } = true;

        /// <summary>
        /// Creates a new break-glass access service.
        /// </summary>
        public BreakGlassAccess(IKeyStore keyStore, IMessageBus? messageBus = null)
        {
            _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
            _messageBus = messageBus;
        }

        /// <summary>
        /// Registers an authorized emergency responder.
        /// </summary>
        public async Task<AuthorizedResponder> RegisterResponderAsync(
            string responderId,
            ResponderRegistration registration,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(responderId);
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentNullException.ThrowIfNull(context);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can register emergency responders.");
            }

            var responder = new AuthorizedResponder
            {
                ResponderId = responderId,
                Name = registration.Name,
                Email = registration.Email,
                Phone = registration.Phone,
                Role = registration.Role,
                AuthorizationLevel = registration.AuthorizationLevel,
                CanInitiateBreakGlass = registration.CanInitiateBreakGlass,
                CanAuthorizeBreakGlass = registration.CanAuthorizeBreakGlass,
                RegisteredAt = DateTime.UtcNow,
                RegisteredBy = context.UserId,
                IsActive = true,
                Metadata = registration.Metadata ?? new Dictionary<string, object>()
            };

            _responders[responderId] = responder;

            await AuditAsync(new BreakGlassAuditEntry
            {
                Action = BreakGlassAction.ResponderRegistered,
                ResponderId = responderId,
                PerformedBy = context.UserId,
                Details = $"Registered responder: {registration.Name} ({registration.Role})"
            });

            await PublishEventAsync("breakglass.responder.registered", new Dictionary<string, object>
            {
                ["responderId"] = responderId,
                ["name"] = responder.Name,
                ["role"] = responder.Role.ToString(),
                ["registeredBy"] = context.UserId
            });

            return responder;
        }

        /// <summary>
        /// Creates an emergency access policy.
        /// </summary>
        public async Task<EmergencyAccessPolicy> CreatePolicyAsync(
            string policyId,
            EmergencyAccessPolicyRequest request,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
            ArgumentNullException.ThrowIfNull(request);

            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can create emergency access policies.");
            }

            if (request.RequiredAuthorizationCount < 1)
            {
                throw new ArgumentException("Required authorization count must be at least 1.");
            }

            if (request.AuthorizedResponderIds.Count < request.RequiredAuthorizationCount)
            {
                throw new ArgumentException(
                    $"Number of authorized responders ({request.AuthorizedResponderIds.Count}) must be >= required authorizations ({request.RequiredAuthorizationCount}).");
            }

            // Validate all responders exist and can authorize
            foreach (var responderId in request.AuthorizedResponderIds)
            {
                if (!_responders.TryGetValue(responderId, out var responder) || !responder.IsActive)
                {
                    throw new KeyNotFoundException($"Responder '{responderId}' not found or inactive.");
                }
                if (!responder.CanAuthorizeBreakGlass)
                {
                    throw new UnauthorizedAccessException($"Responder '{responderId}' is not authorized to approve break-glass access.");
                }
            }

            var policy = new EmergencyAccessPolicy
            {
                PolicyId = policyId,
                Name = request.Name,
                Description = request.Description,
                RequiredAuthorizationCount = request.RequiredAuthorizationCount,
                AuthorizedResponderIds = request.AuthorizedResponderIds.ToList(),
                AllowedKeyPatterns = request.AllowedKeyPatterns?.ToList() ?? new List<string> { "*" },
                MaxSessionDurationHours = request.MaxSessionDurationHours,
                RequiresJustification = request.RequiresJustification,
                RequiresIncidentTicket = request.RequiresIncidentTicket,
                AllowedEmergencyTypes = request.AllowedEmergencyTypes?.ToList() ?? new List<EmergencyType>
                {
                    EmergencyType.DisasterRecovery,
                    EmergencyType.LegalHold,
                    EmergencyType.SecurityIncident,
                    EmergencyType.ComplianceAudit,
                    EmergencyType.BusinessContinuity
                },
                CreatedAt = DateTime.UtcNow,
                CreatedBy = context.UserId,
                IsActive = true
            };

            _policies[policyId] = policy;

            await AuditAsync(new BreakGlassAuditEntry
            {
                Action = BreakGlassAction.PolicyCreated,
                PolicyId = policyId,
                PerformedBy = context.UserId,
                Details = $"Created policy: {request.Name} (M={request.RequiredAuthorizationCount}/N={request.AuthorizedResponderIds.Count})"
            });

            await PublishEventAsync("breakglass.policy.created", new Dictionary<string, object>
            {
                ["policyId"] = policyId,
                ["name"] = policy.Name,
                ["requiredAuthorizations"] = policy.RequiredAuthorizationCount,
                ["createdBy"] = context.UserId
            });

            return policy;
        }

        /// <summary>
        /// Initiates a break-glass emergency access request.
        /// </summary>
        public async Task<BreakGlassSession> InitiateBreakGlassAsync(
            string policyId,
            BreakGlassRequest request,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(context);

            // Validate responder can initiate
            var initiatorId = request.InitiatorResponderId ?? context.UserId;
            if (_responders.TryGetValue(initiatorId, out var initiator))
            {
                if (!initiator.CanInitiateBreakGlass)
                {
                    throw new UnauthorizedAccessException($"Responder '{initiatorId}' is not authorized to initiate break-glass access.");
                }
            }

            if (!_policies.TryGetValue(policyId, out var policy) || !policy.IsActive)
            {
                throw new KeyNotFoundException($"Emergency access policy '{policyId}' not found or inactive.");
            }

            // Validate emergency type
            if (!policy.AllowedEmergencyTypes.Contains(request.EmergencyType))
            {
                throw new UnauthorizedAccessException(
                    $"Emergency type '{request.EmergencyType}' is not allowed by policy '{policyId}'.");
            }

            // Validate justification requirement
            if (policy.RequiresJustification && string.IsNullOrWhiteSpace(request.Justification))
            {
                throw new ArgumentException("Justification is required by emergency access policy.");
            }

            // Validate incident ticket requirement
            if (policy.RequiresIncidentTicket && string.IsNullOrWhiteSpace(request.IncidentTicketId))
            {
                throw new ArgumentException("Incident ticket ID is required by emergency access policy.");
            }

            await _accessLock.WaitAsync(ct);
            try
            {
                var sessionId = GenerateSessionId();
                var session = new BreakGlassSession
                {
                    SessionId = sessionId,
                    PolicyId = policyId,
                    InitiatedAt = DateTime.UtcNow,
                    InitiatedBy = initiatorId,
                    EmergencyType = request.EmergencyType,
                    Justification = request.Justification,
                    IncidentTicketId = request.IncidentTicketId,
                    RequestedKeyPatterns = request.RequestedKeyPatterns?.ToList() ?? new List<string>(),
                    Status = BreakGlassSessionStatus.PendingAuthorization,
                    RequiredAuthorizations = policy.RequiredAuthorizationCount,
                    Authorizations = new List<BreakGlassAuthorization>(),
                    ExpiresAt = DateTime.UtcNow.AddHours(policy.MaxSessionDurationHours ?? DefaultSessionTimeout.TotalHours),
                    KeyAccessLog = new List<KeyAccessLogEntry>()
                };

                _activeSessions[sessionId] = session;

                await AuditAsync(new BreakGlassAuditEntry
                {
                    SessionId = sessionId,
                    Action = BreakGlassAction.SessionInitiated,
                    ResponderId = initiatorId,
                    PerformedBy = context.UserId,
                    Details = $"Break-glass initiated. Type: {request.EmergencyType}, Justification: {request.Justification}",
                    EmergencyType = request.EmergencyType,
                    IncidentTicketId = request.IncidentTicketId
                });

                // Notify all authorized responders
                await NotifyRespondersAsync(policy.AuthorizedResponderIds, session);

                // Raise event
                AccessInitiated?.Invoke(this, new BreakGlassInitiatedEventArgs
                {
                    Session = session,
                    Policy = policy
                });

                await PublishEventAsync("breakglass.session.initiated", new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["policyId"] = policyId,
                    ["emergencyType"] = request.EmergencyType.ToString(),
                    ["initiatedBy"] = initiatorId,
                    ["justification"] = request.Justification ?? "",
                    ["incidentTicket"] = request.IncidentTicketId ?? ""
                });

                return session;
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Authorizes a break-glass session.
        /// </summary>
        public async Task<AuthorizationResult> AuthorizeSessionAsync(
            string sessionId,
            string responderId,
            AuthorizationDetails details,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(responderId);

            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                return new AuthorizationResult
                {
                    Success = false,
                    Message = $"Break-glass session '{sessionId}' not found."
                };
            }

            if (!_responders.TryGetValue(responderId, out var responder) || !responder.IsActive)
            {
                return new AuthorizationResult
                {
                    Success = false,
                    Message = $"Responder '{responderId}' not found or inactive."
                };
            }

            if (!responder.CanAuthorizeBreakGlass)
            {
                return new AuthorizationResult
                {
                    Success = false,
                    Message = $"Responder '{responderId}' is not authorized to approve break-glass access."
                };
            }

            if (!_policies.TryGetValue(session.PolicyId, out var policy))
            {
                return new AuthorizationResult
                {
                    Success = false,
                    Message = $"Policy '{session.PolicyId}' not found."
                };
            }

            if (!policy.AuthorizedResponderIds.Contains(responderId))
            {
                return new AuthorizationResult
                {
                    Success = false,
                    Message = $"Responder '{responderId}' is not in the authorized list for policy '{session.PolicyId}'."
                };
            }

            if (session.Status != BreakGlassSessionStatus.PendingAuthorization)
            {
                return new AuthorizationResult
                {
                    Success = false,
                    Message = $"Session is not pending authorization. Status: {session.Status}"
                };
            }

            // Check if already authorized
            if (session.Authorizations.Any(a => a.ResponderId == responderId))
            {
                return new AuthorizationResult
                {
                    Success = false,
                    Message = $"Responder '{responderId}' has already authorized this session."
                };
            }

            await _accessLock.WaitAsync(ct);
            try
            {
                var authorization = new BreakGlassAuthorization
                {
                    ResponderId = responderId,
                    AuthorizedAt = DateTime.UtcNow,
                    Comment = details.Comment,
                    VerificationMethod = details.VerificationMethod ?? VerificationMethod.SystemLogin
                };

                session.Authorizations.Add(authorization);

                await AuditAsync(new BreakGlassAuditEntry
                {
                    SessionId = sessionId,
                    Action = BreakGlassAction.SessionAuthorized,
                    ResponderId = responderId,
                    PerformedBy = context.UserId,
                    Details = $"Authorization provided. Total: {session.Authorizations.Count}/{session.RequiredAuthorizations}"
                });

                // Check if we have enough authorizations
                if (session.Authorizations.Count >= session.RequiredAuthorizations)
                {
                    session.Status = BreakGlassSessionStatus.Active;
                    session.ActivatedAt = DateTime.UtcNow;

                    AccessAuthorized?.Invoke(this, new BreakGlassAuthorizedEventArgs
                    {
                        Session = session,
                        FinalAuthorizer = responder
                    });

                    await PublishEventAsync("breakglass.session.authorized", new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId,
                        ["totalAuthorizations"] = session.Authorizations.Count,
                        ["activatedAt"] = DateTime.UtcNow
                    });
                }

                return new AuthorizationResult
                {
                    Success = true,
                    Message = session.Status == BreakGlassSessionStatus.Active
                        ? "Session is now active. Emergency access granted."
                        : $"Authorization recorded. {session.RequiredAuthorizations - session.Authorizations.Count} more authorizations needed.",
                    AuthorizationCount = session.Authorizations.Count,
                    RequiredAuthorizations = session.RequiredAuthorizations,
                    SessionActive = session.Status == BreakGlassSessionStatus.Active
                };
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Denies a break-glass session request.
        /// </summary>
        public async Task<bool> DenySessionAsync(
            string sessionId,
            string responderId,
            string reason,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                return false;
            }

            if (session.Status != BreakGlassSessionStatus.PendingAuthorization)
            {
                return false;
            }

            await _accessLock.WaitAsync(ct);
            try
            {
                session.Status = BreakGlassSessionStatus.Denied;
                session.DeniedAt = DateTime.UtcNow;
                session.DeniedBy = responderId;
                session.DenialReason = reason;

                await AuditAsync(new BreakGlassAuditEntry
                {
                    SessionId = sessionId,
                    Action = BreakGlassAction.SessionDenied,
                    ResponderId = responderId,
                    PerformedBy = context.UserId,
                    Details = $"Session denied. Reason: {reason}"
                });

                await PublishEventAsync("breakglass.session.denied", new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["deniedBy"] = responderId,
                    ["reason"] = reason
                });

                return true;
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Accesses a key using break-glass emergency access.
        /// </summary>
        public async Task<BreakGlassKeyAccessResult> AccessKeyAsync(
            string sessionId,
            string keyId,
            string accessReason,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
            ArgumentException.ThrowIfNullOrWhiteSpace(accessReason);

            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                return new BreakGlassKeyAccessResult
                {
                    Success = false,
                    Message = $"Break-glass session '{sessionId}' not found."
                };
            }

            if (session.Status != BreakGlassSessionStatus.Active)
            {
                return new BreakGlassKeyAccessResult
                {
                    Success = false,
                    Message = $"Session is not active. Status: {session.Status}"
                };
            }

            if (session.ExpiresAt < DateTime.UtcNow)
            {
                // Mutate status inside the lock to prevent race conditions (#3408)
                await _accessLock.WaitAsync();
                try { session.Status = BreakGlassSessionStatus.Expired; }
                finally { _accessLock.Release(); }
                return new BreakGlassKeyAccessResult
                {
                    Success = false,
                    Message = "Break-glass session has expired."
                };
            }

            if (session.KeyAccessLog.Count >= MaxKeysPerSession)
            {
                return new BreakGlassKeyAccessResult
                {
                    Success = false,
                    Message = $"Maximum keys per session ({MaxKeysPerSession}) exceeded."
                };
            }

            // Validate key pattern
            if (!_policies.TryGetValue(session.PolicyId, out var policy))
            {
                return new BreakGlassKeyAccessResult
                {
                    Success = false,
                    Message = "Session policy not found."
                };
            }

            if (!IsKeyAllowedByPolicy(keyId, policy.AllowedKeyPatterns))
            {
                return new BreakGlassKeyAccessResult
                {
                    Success = false,
                    Message = $"Key '{keyId}' is not allowed by emergency access policy."
                };
            }

            await _accessLock.WaitAsync(ct);
            try
            {
                // Create emergency security context
                var emergencyContext = new EmergencySecurityContext(context, session);

                // Access the key
                var keyData = await _keyStore.GetKeyAsync(keyId, emergencyContext);

                // Log the access
                var accessEntry = new KeyAccessLogEntry
                {
                    KeyId = keyId,
                    AccessedAt = DateTime.UtcNow,
                    AccessedBy = context.UserId,
                    Reason = accessReason,
                    KeySizeBytes = keyData.Length
                };

                session.KeyAccessLog.Add(accessEntry);
                session.TotalKeysAccessed++;

                await AuditAsync(new BreakGlassAuditEntry
                {
                    SessionId = sessionId,
                    Action = BreakGlassAction.KeyAccessed,
                    KeyId = keyId,
                    PerformedBy = context.UserId,
                    Details = $"Emergency key access. Reason: {accessReason}",
                    EmergencyType = session.EmergencyType,
                    IncidentTicketId = session.IncidentTicketId
                });

                KeyAccessed?.Invoke(this, new BreakGlassKeyAccessedEventArgs
                {
                    SessionId = sessionId,
                    KeyId = keyId,
                    AccessedBy = context.UserId,
                    Reason = accessReason
                });

                await PublishEventAsync("breakglass.key.accessed", new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["keyId"] = keyId,
                    ["accessedBy"] = context.UserId,
                    ["reason"] = accessReason,
                    ["emergencyType"] = session.EmergencyType.ToString(),
                    ["totalKeysAccessed"] = session.TotalKeysAccessed
                });

                return new BreakGlassKeyAccessResult
                {
                    Success = true,
                    Message = "Key accessed successfully via break-glass emergency access.",
                    KeyData = keyData,
                    KeyId = keyId,
                    SessionId = sessionId
                };
            }
            catch (Exception ex)
            {
                await AuditAsync(new BreakGlassAuditEntry
                {
                    SessionId = sessionId,
                    Action = BreakGlassAction.KeyAccessFailed,
                    KeyId = keyId,
                    PerformedBy = context.UserId,
                    Details = $"Key access failed: {ex.Message}",
                    EmergencyType = session.EmergencyType
                });

                return new BreakGlassKeyAccessResult
                {
                    Success = false,
                    Message = $"Failed to access key: {ex.Message}"
                };
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Ends a break-glass session.
        /// </summary>
        public async Task<bool> EndSessionAsync(
            string sessionId,
            string reason,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
            {
                return false;
            }

            await _accessLock.WaitAsync(ct);
            try
            {
                session.Status = BreakGlassSessionStatus.Ended;
                session.EndedAt = DateTime.UtcNow;
                session.EndedBy = context.UserId;
                session.EndReason = reason;

                await AuditAsync(new BreakGlassAuditEntry
                {
                    SessionId = sessionId,
                    Action = BreakGlassAction.SessionEnded,
                    PerformedBy = context.UserId,
                    Details = $"Session ended. Reason: {reason}. Keys accessed: {session.TotalKeysAccessed}"
                });

                SessionEnded?.Invoke(this, new BreakGlassSessionEndedEventArgs
                {
                    Session = session,
                    EndedBy = context.UserId,
                    Reason = reason
                });

                await PublishEventAsync("breakglass.session.ended", new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["endedBy"] = context.UserId,
                    ["reason"] = reason,
                    ["totalKeysAccessed"] = session.TotalKeysAccessed,
                    ["durationMinutes"] = (DateTime.UtcNow - session.InitiatedAt).TotalMinutes
                });

                return true;
            }
            finally
            {
                _accessLock.Release();
            }
        }

        /// <summary>
        /// Gets the status of a break-glass session.
        /// </summary>
        public BreakGlassSessionStatus? GetSessionStatus(string sessionId)
        {
            return _activeSessions.TryGetValue(sessionId, out var session)
                ? session.Status
                : null;
        }

        /// <summary>
        /// Gets details of a break-glass session.
        /// </summary>
        public BreakGlassSession? GetSession(string sessionId)
        {
            return _activeSessions.TryGetValue(sessionId, out var session) ? session : null;
        }

        /// <summary>
        /// Gets all active break-glass sessions.
        /// </summary>
        public IReadOnlyList<BreakGlassSession> GetActiveSessions()
        {
            return _activeSessions.Values
                .Where(s => s.Status == BreakGlassSessionStatus.Active ||
                           s.Status == BreakGlassSessionStatus.PendingAuthorization)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets the complete audit log for a session.
        /// </summary>
        public IReadOnlyList<BreakGlassAuditEntry> GetSessionAuditLog(string sessionId)
        {
            return _auditLog.Values
                .Where(e => e.SessionId == sessionId)
                .OrderBy(e => e.Timestamp)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets all audit entries for a time period.
        /// </summary>
        public IReadOnlyList<BreakGlassAuditEntry> GetAuditLog(DateTime? fromDate = null, DateTime? toDate = null)
        {
            var entries = _auditLog.Values.AsEnumerable();

            if (fromDate.HasValue)
            {
                entries = entries.Where(e => e.Timestamp >= fromDate.Value);
            }

            if (toDate.HasValue)
            {
                entries = entries.Where(e => e.Timestamp <= toDate.Value);
            }

            return entries
                .OrderByDescending(e => e.Timestamp)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets all registered responders.
        /// </summary>
        public IReadOnlyList<AuthorizedResponder> GetResponders(bool activeOnly = true)
        {
            var responders = _responders.Values.AsEnumerable();
            if (activeOnly)
            {
                responders = responders.Where(r => r.IsActive);
            }
            return responders.ToList().AsReadOnly();
        }

        /// <summary>
        /// Deactivates an emergency responder.
        /// </summary>
        public async Task<bool> DeactivateResponderAsync(
            string responderId,
            ISecurityContext context,
            CancellationToken ct = default)
        {
            if (!context.IsSystemAdmin)
            {
                throw new UnauthorizedAccessException("Only system administrators can deactivate responders.");
            }

            if (_responders.TryGetValue(responderId, out var responder))
            {
                responder.IsActive = false;
                responder.DeactivatedAt = DateTime.UtcNow;
                responder.DeactivatedBy = context.UserId;

                await AuditAsync(new BreakGlassAuditEntry
                {
                    Action = BreakGlassAction.ResponderDeactivated,
                    ResponderId = responderId,
                    PerformedBy = context.UserId,
                    Details = $"Responder deactivated: {responder.Name}"
                });

                return true;
            }
            return false;
        }

        /// <summary>
        /// Generates a compliance report for break-glass activities.
        /// </summary>
        public BreakGlassComplianceReport GenerateComplianceReport(DateTime fromDate, DateTime toDate)
        {
            var sessionsInPeriod = _activeSessions.Values
                .Where(s => s.InitiatedAt >= fromDate && s.InitiatedAt <= toDate)
                .ToList();

            var auditEntriesInPeriod = _auditLog.Values
                .Where(e => e.Timestamp >= fromDate && e.Timestamp <= toDate)
                .ToList();

            return new BreakGlassComplianceReport
            {
                ReportPeriodStart = fromDate,
                ReportPeriodEnd = toDate,
                GeneratedAt = DateTime.UtcNow,
                TotalSessionsInitiated = sessionsInPeriod.Count,
                TotalSessionsAuthorized = sessionsInPeriod.Count(s => s.Status == BreakGlassSessionStatus.Active ||
                                                                       s.Status == BreakGlassSessionStatus.Ended),
                TotalSessionsDenied = sessionsInPeriod.Count(s => s.Status == BreakGlassSessionStatus.Denied),
                TotalKeysAccessed = sessionsInPeriod.Sum(s => s.TotalKeysAccessed),
                SessionsByEmergencyType = sessionsInPeriod
                    .GroupBy(s => s.EmergencyType)
                    .ToDictionary(g => g.Key, g => g.Count()),
                AverageSessionDurationMinutes = sessionsInPeriod
                    .Where(s => s.EndedAt.HasValue)
                    .Select(s => (s.EndedAt!.Value - s.InitiatedAt).TotalMinutes)
                    .DefaultIfEmpty(0)
                    .Average(),
                AuditEntryCount = auditEntriesInPeriod.Count,
                Sessions = sessionsInPeriod.Select(s => new BreakGlassSessionSummary
                {
                    SessionId = s.SessionId,
                    InitiatedAt = s.InitiatedAt,
                    InitiatedBy = s.InitiatedBy,
                    EmergencyType = s.EmergencyType,
                    Status = s.Status,
                    KeysAccessed = s.TotalKeysAccessed,
                    IncidentTicketId = s.IncidentTicketId
                }).ToList()
            };
        }

        #region Private Methods

        private bool IsKeyAllowedByPolicy(string keyId, List<string> allowedPatterns)
        {
            if (allowedPatterns.Contains("*"))
                return true;

            foreach (var pattern in allowedPatterns)
            {
                if (pattern.EndsWith("*"))
                {
                    var prefix = pattern.TrimEnd('*');
                    if (keyId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (pattern.StartsWith("*"))
                {
                    var suffix = pattern.TrimStart('*');
                    if (keyId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (pattern.Equals(keyId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task NotifyRespondersAsync(List<string> responderIds, BreakGlassSession session)
        {
            var notifications = responderIds
                .Where(id => _responders.TryGetValue(id, out var r) && r.IsActive)
                .Select(id => NotifyResponderAsync(_responders[id], session));

            await Task.WhenAll(notifications);
        }

        private async Task NotifyResponderAsync(AuthorizedResponder responder, BreakGlassSession session)
        {
            if (_messageBus != null)
            {
                var message = new PluginMessage
                {
                    Type = "breakglass.notification.authorization_needed",
                    Payload = new Dictionary<string, object>
                    {
                        ["responderId"] = responder.ResponderId,
                        ["responderName"] = responder.Name,
                        ["responderEmail"] = responder.Email,
                        ["sessionId"] = session.SessionId,
                        ["emergencyType"] = session.EmergencyType.ToString(),
                        ["justification"] = session.Justification ?? "",
                        ["initiatedBy"] = session.InitiatedBy ?? "unknown",
                        ["expiresAt"] = session.ExpiresAt
                    }
                };

                await _messageBus.PublishAsync($"breakglass.notification.{responder.ResponderId}", message);
            }
        }

        private async Task AuditAsync(BreakGlassAuditEntry entry)
        {
            entry.EntryId = GenerateAuditId();
            entry.Timestamp = DateTime.UtcNow;

            _auditLog[entry.EntryId] = entry;

            if (_messageBus != null)
            {
                await PublishEventAsync("breakglass.audit", new Dictionary<string, object>
                {
                    ["entryId"] = entry.EntryId,
                    ["action"] = entry.Action.ToString(),
                    ["sessionId"] = entry.SessionId ?? "",
                    ["keyId"] = entry.KeyId ?? "",
                    ["performedBy"] = entry.PerformedBy ?? "",
                    ["details"] = entry.Details ?? ""
                });
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
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }

        private string GenerateSessionId()
        {
            return $"bg-{DateTime.UtcNow:yyyyMMddHHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        }

        private string GenerateAuditId()
        {
            return $"audit-{DateTime.UtcNow:yyyyMMddHHmmssffff}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        }

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _activeSessions.Clear();
            _auditLog.Clear();
            _policies.Clear();
            _responders.Clear();
            _accessLock.Dispose();

            GC.SuppressFinalize(this);
        }
    }

    #region Supporting Types

    /// <summary>
    /// Emergency access types.
    /// </summary>
    public enum EmergencyType
    {
        /// <summary>Disaster recovery scenario.</summary>
        DisasterRecovery,
        /// <summary>Legal hold / eDiscovery requirement.</summary>
        LegalHold,
        /// <summary>Active security incident response.</summary>
        SecurityIncident,
        /// <summary>Compliance audit requirement.</summary>
        ComplianceAudit,
        /// <summary>Business continuity emergency.</summary>
        BusinessContinuity,
        /// <summary>System failure recovery.</summary>
        SystemFailure,
        /// <summary>Other emergency (requires detailed justification).</summary>
        Other
    }

    /// <summary>
    /// Break-glass session status.
    /// </summary>
    public enum BreakGlassSessionStatus
    {
        /// <summary>Waiting for required authorizations.</summary>
        PendingAuthorization,
        /// <summary>Authorized and active for key access.</summary>
        Active,
        /// <summary>Session was denied.</summary>
        Denied,
        /// <summary>Session has expired.</summary>
        Expired,
        /// <summary>Session was manually ended.</summary>
        Ended,
        /// <summary>Session was revoked.</summary>
        Revoked
    }

    /// <summary>
    /// Break-glass audit actions.
    /// </summary>
    public enum BreakGlassAction
    {
        ResponderRegistered,
        ResponderDeactivated,
        PolicyCreated,
        PolicyUpdated,
        PolicyDeactivated,
        SessionInitiated,
        SessionAuthorized,
        SessionDenied,
        SessionEnded,
        SessionExpired,
        SessionRevoked,
        KeyAccessed,
        KeyAccessFailed
    }

    /// <summary>
    /// Verification method for authorization.
    /// </summary>
    public enum VerificationMethod
    {
        SystemLogin,
        PhoneCallback,
        HardwareToken,
        Biometric,
        ManagerApproval,
        OutOfBandConfirmation
    }

    /// <summary>
    /// Responder role in emergency access.
    /// </summary>
    public enum ResponderRole
    {
        SecurityOfficer,
        SystemAdministrator,
        IncidentResponder,
        ComplianceOfficer,
        ExecutiveManagement,
        LegalCounsel,
        AuditTeam
    }

    /// <summary>
    /// Authorization level for responders.
    /// </summary>
    public enum AuthorizationLevel
    {
        Standard,
        Elevated,
        Critical,
        Executive
    }

    /// <summary>
    /// Responder registration details.
    /// </summary>
    public class ResponderRegistration
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public ResponderRole Role { get; set; }
        public AuthorizationLevel AuthorizationLevel { get; set; } = AuthorizationLevel.Standard;
        public bool CanInitiateBreakGlass { get; set; }
        public bool CanAuthorizeBreakGlass { get; set; }
        public Dictionary<string, object>? Metadata { get; set; }
    }

    /// <summary>
    /// Authorized emergency responder.
    /// </summary>
    public class AuthorizedResponder
    {
        public string ResponderId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public ResponderRole Role { get; set; }
        public AuthorizationLevel AuthorizationLevel { get; set; }
        public bool CanInitiateBreakGlass { get; set; }
        public bool CanAuthorizeBreakGlass { get; set; }
        public DateTime RegisteredAt { get; set; }
        public string? RegisteredBy { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime? DeactivatedAt { get; set; }
        public string? DeactivatedBy { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Emergency access policy request.
    /// </summary>
    public class EmergencyAccessPolicyRequest
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int RequiredAuthorizationCount { get; set; }
        public List<string> AuthorizedResponderIds { get; set; } = new();
        public List<string>? AllowedKeyPatterns { get; set; }
        public double? MaxSessionDurationHours { get; set; }
        public bool RequiresJustification { get; set; } = true;
        public bool RequiresIncidentTicket { get; set; }
        public List<EmergencyType>? AllowedEmergencyTypes { get; set; }
    }

    /// <summary>
    /// Emergency access policy.
    /// </summary>
    public class EmergencyAccessPolicy
    {
        public string PolicyId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int RequiredAuthorizationCount { get; set; }
        public List<string> AuthorizedResponderIds { get; set; } = new();
        public List<string> AllowedKeyPatterns { get; set; } = new();
        public double? MaxSessionDurationHours { get; set; }
        public bool RequiresJustification { get; set; }
        public bool RequiresIncidentTicket { get; set; }
        public List<EmergencyType> AllowedEmergencyTypes { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Break-glass request.
    /// </summary>
    public class BreakGlassRequest
    {
        public string? InitiatorResponderId { get; set; }
        public EmergencyType EmergencyType { get; set; }
        public string? Justification { get; set; }
        public string? IncidentTicketId { get; set; }
        public List<string>? RequestedKeyPatterns { get; set; }
    }

    /// <summary>
    /// Break-glass session.
    /// </summary>
    public class BreakGlassSession
    {
        public string SessionId { get; set; } = "";
        public string PolicyId { get; set; } = "";
        public DateTime InitiatedAt { get; set; }
        public string? InitiatedBy { get; set; }
        public EmergencyType EmergencyType { get; set; }
        public string? Justification { get; set; }
        public string? IncidentTicketId { get; set; }
        public List<string> RequestedKeyPatterns { get; set; } = new();
        public BreakGlassSessionStatus Status { get; set; }
        public int RequiredAuthorizations { get; set; }
        public List<BreakGlassAuthorization> Authorizations { get; set; } = new();
        public DateTime? ActivatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public string? EndedBy { get; set; }
        public string? EndReason { get; set; }
        public DateTime? DeniedAt { get; set; }
        public string? DeniedBy { get; set; }
        public string? DenialReason { get; set; }
        public List<KeyAccessLogEntry> KeyAccessLog { get; set; } = new();
        public int TotalKeysAccessed { get; set; }
    }

    /// <summary>
    /// Break-glass authorization.
    /// </summary>
    public class BreakGlassAuthorization
    {
        public string ResponderId { get; set; } = "";
        public DateTime AuthorizedAt { get; set; }
        public string? Comment { get; set; }
        public VerificationMethod VerificationMethod { get; set; }
    }

    /// <summary>
    /// Authorization details.
    /// </summary>
    public class AuthorizationDetails
    {
        public string? Comment { get; set; }
        public VerificationMethod? VerificationMethod { get; set; }
    }

    /// <summary>
    /// Authorization result.
    /// </summary>
    public class AuthorizationResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public int AuthorizationCount { get; set; }
        public int RequiredAuthorizations { get; set; }
        public bool SessionActive { get; set; }
    }

    /// <summary>
    /// Key access log entry.
    /// </summary>
    public class KeyAccessLogEntry
    {
        public string KeyId { get; set; } = "";
        public DateTime AccessedAt { get; set; }
        public string? AccessedBy { get; set; }
        public string? Reason { get; set; }
        public int KeySizeBytes { get; set; }
    }

    /// <summary>
    /// Break-glass key access result.
    /// </summary>
    public class BreakGlassKeyAccessResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public byte[]? KeyData { get; set; }
        public string? KeyId { get; set; }
        public string? SessionId { get; set; }
    }

    /// <summary>
    /// Break-glass audit entry.
    /// </summary>
    public class BreakGlassAuditEntry
    {
        public string EntryId { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public BreakGlassAction Action { get; set; }
        public string? SessionId { get; set; }
        public string? PolicyId { get; set; }
        public string? ResponderId { get; set; }
        public string? KeyId { get; set; }
        public string? PerformedBy { get; set; }
        public string? Details { get; set; }
        public EmergencyType? EmergencyType { get; set; }
        public string? IncidentTicketId { get; set; }
    }

    /// <summary>
    /// Break-glass compliance report.
    /// </summary>
    public class BreakGlassComplianceReport
    {
        public DateTime ReportPeriodStart { get; set; }
        public DateTime ReportPeriodEnd { get; set; }
        public DateTime GeneratedAt { get; set; }
        public int TotalSessionsInitiated { get; set; }
        public int TotalSessionsAuthorized { get; set; }
        public int TotalSessionsDenied { get; set; }
        public int TotalKeysAccessed { get; set; }
        public Dictionary<EmergencyType, int> SessionsByEmergencyType { get; set; } = new();
        public double AverageSessionDurationMinutes { get; set; }
        public int AuditEntryCount { get; set; }
        public List<BreakGlassSessionSummary> Sessions { get; set; } = new();
    }

    /// <summary>
    /// Break-glass session summary for reports.
    /// </summary>
    public class BreakGlassSessionSummary
    {
        public string SessionId { get; set; } = "";
        public DateTime InitiatedAt { get; set; }
        public string? InitiatedBy { get; set; }
        public EmergencyType EmergencyType { get; set; }
        public BreakGlassSessionStatus Status { get; set; }
        public int KeysAccessed { get; set; }
        public string? IncidentTicketId { get; set; }
    }

    /// <summary>
    /// Event args for break-glass initiated.
    /// </summary>
    public class BreakGlassInitiatedEventArgs : EventArgs
    {
        public required BreakGlassSession Session { get; set; }
        public required EmergencyAccessPolicy Policy { get; set; }
    }

    /// <summary>
    /// Event args for break-glass authorized.
    /// </summary>
    public class BreakGlassAuthorizedEventArgs : EventArgs
    {
        public required BreakGlassSession Session { get; set; }
        public required AuthorizedResponder FinalAuthorizer { get; set; }
    }

    /// <summary>
    /// Event args for key accessed.
    /// </summary>
    public class BreakGlassKeyAccessedEventArgs : EventArgs
    {
        public string SessionId { get; set; } = "";
        public string KeyId { get; set; } = "";
        public string? AccessedBy { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Event args for session ended.
    /// </summary>
    public class BreakGlassSessionEndedEventArgs : EventArgs
    {
        public required BreakGlassSession Session { get; set; }
        public string? EndedBy { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Emergency security context for break-glass operations.
    /// </summary>
    internal sealed class EmergencySecurityContext : ISecurityContext
    {
        private readonly ISecurityContext _originalContext;
        private readonly BreakGlassSession _session;

        public EmergencySecurityContext(ISecurityContext originalContext, BreakGlassSession session)
        {
            _originalContext = originalContext;
            _session = session;
        }

        public string UserId => $"EMERGENCY:{_originalContext.UserId}";
        public string? TenantId => _originalContext.TenantId;
        public IEnumerable<string> Roles => _originalContext.Roles.Concat(new[] { "EMERGENCY_ACCESS" });
        public bool IsSystemAdmin => true; // Emergency access grants elevated privileges

        public string SessionId => _session.SessionId;
        public EmergencyType EmergencyType => _session.EmergencyType;
    }

    #endregion
}
