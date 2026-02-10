using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Features
{
    /// <summary>
    /// Privileged Access Management (PAM) with session recording, JIT access, and privilege escalation controls.
    /// </summary>
    public sealed class PrivilegedAccessManager
    {
        private readonly ConcurrentDictionary<string, PrivilegedSession> _activeSessions = new();
        private readonly ConcurrentQueue<SessionRecord> _sessionHistory = new();
        private readonly ConcurrentDictionary<string, JitAccessGrant> _jitGrants = new();
        private readonly int _maxSessionHistorySize = 1000;

        /// <summary>
        /// Requests Just-In-Time privileged access.
        /// </summary>
        public Task<JitAccessGrant> RequestJitAccessAsync(
            string userId,
            string resourceId,
            string privilege,
            TimeSpan duration,
            string justification,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (string.IsNullOrWhiteSpace(resourceId))
                throw new ArgumentException("Resource ID cannot be null or empty", nameof(resourceId));

            if (duration > TimeSpan.FromHours(8))
                throw new ArgumentException("JIT access duration cannot exceed 8 hours", nameof(duration));

            var grantId = Guid.NewGuid().ToString("N");
            var grant = new JitAccessGrant
            {
                GrantId = grantId,
                UserId = userId,
                ResourceId = resourceId,
                Privilege = privilege,
                GrantedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(duration),
                Justification = justification,
                Status = JitAccessStatus.Active
            };

            _jitGrants[grantId] = grant;

            // Auto-revoke after duration
            _ = Task.Run(async () =>
            {
                await Task.Delay(duration, cancellationToken);
                if (_jitGrants.TryGetValue(grantId, out var g) && g.Status == JitAccessStatus.Active)
                {
                    g.Status = JitAccessStatus.Expired;
                    g.RevokedAt = DateTime.UtcNow;
                }
            }, cancellationToken);

            return Task.FromResult(grant);
        }

        /// <summary>
        /// Validates JIT access grant.
        /// </summary>
        public Task<bool> ValidateJitAccessAsync(string grantId, CancellationToken cancellationToken = default)
        {
            if (!_jitGrants.TryGetValue(grantId, out var grant))
                return Task.FromResult(false);

            if (grant.Status != JitAccessStatus.Active)
                return Task.FromResult(false);

            if (DateTime.UtcNow > grant.ExpiresAt)
            {
                grant.Status = JitAccessStatus.Expired;
                grant.RevokedAt = DateTime.UtcNow;
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        /// <summary>
        /// Starts a privileged session.
        /// </summary>
        public Task<PrivilegedSession> StartSessionAsync(
            string userId,
            string resourceId,
            string privilege,
            string? jitGrantId = null,
            CancellationToken cancellationToken = default)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var session = new PrivilegedSession
            {
                SessionId = sessionId,
                UserId = userId,
                ResourceId = resourceId,
                Privilege = privilege,
                JitGrantId = jitGrantId,
                StartedAt = DateTime.UtcNow,
                Status = SessionStatus.Active,
                Actions = new List<SessionAction>()
            };

            _activeSessions[sessionId] = session;
            return Task.FromResult(session);
        }

        /// <summary>
        /// Records an action in a privileged session.
        /// </summary>
        public Task RecordSessionActionAsync(
            string sessionId,
            string action,
            string? details = null,
            CancellationToken cancellationToken = default)
        {
            if (!_activeSessions.TryGetValue(sessionId, out var session))
                throw new InvalidOperationException($"Session '{sessionId}' not found or expired");

            session.Actions.Add(new SessionAction
            {
                Action = action,
                Timestamp = DateTime.UtcNow,
                Details = details
            });

            return Task.CompletedTask;
        }

        /// <summary>
        /// Ends a privileged session.
        /// </summary>
        public Task<SessionRecord> EndSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (!_activeSessions.TryRemove(sessionId, out var session))
                throw new InvalidOperationException($"Session '{sessionId}' not found");

            session.EndedAt = DateTime.UtcNow;
            session.Status = SessionStatus.Ended;

            var record = new SessionRecord
            {
                SessionId = session.SessionId,
                UserId = session.UserId,
                ResourceId = session.ResourceId,
                Privilege = session.Privilege,
                StartedAt = session.StartedAt,
                EndedAt = session.EndedAt.Value,
                Duration = session.EndedAt.Value - session.StartedAt,
                ActionCount = session.Actions.Count,
                JitGrantId = session.JitGrantId
            };

            _sessionHistory.Enqueue(record);

            // Prune old records
            while (_sessionHistory.Count > _maxSessionHistorySize)
            {
                _sessionHistory.TryDequeue(out _);
            }

            return Task.FromResult(record);
        }

        /// <summary>
        /// Gets active sessions.
        /// </summary>
        public IReadOnlyCollection<PrivilegedSession> GetActiveSessions()
        {
            return _activeSessions.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets session history.
        /// </summary>
        public IReadOnlyCollection<SessionRecord> GetSessionHistory(int maxCount = 100)
        {
            return _sessionHistory.Take(maxCount).ToList().AsReadOnly();
        }

        /// <summary>
        /// Revokes JIT access grant.
        /// </summary>
        public Task<bool> RevokeJitAccessAsync(string grantId, string reason, CancellationToken cancellationToken = default)
        {
            if (!_jitGrants.TryGetValue(grantId, out var grant))
                return Task.FromResult(false);

            grant.Status = JitAccessStatus.Revoked;
            grant.RevokedAt = DateTime.UtcNow;
            grant.RevocationReason = reason;

            return Task.FromResult(true);
        }
    }

    #region Supporting Types

    public sealed class JitAccessGrant
    {
        public required string GrantId { get; init; }
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required string Privilege { get; init; }
        public required DateTime GrantedAt { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public required string Justification { get; init; }
        public required JitAccessStatus Status { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? RevocationReason { get; set; }
    }

    public enum JitAccessStatus
    {
        Active,
        Expired,
        Revoked
    }

    public sealed class PrivilegedSession
    {
        public required string SessionId { get; init; }
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required string Privilege { get; init; }
        public string? JitGrantId { get; init; }
        public required DateTime StartedAt { get; init; }
        public DateTime? EndedAt { get; set; }
        public required SessionStatus Status { get; set; }
        public required List<SessionAction> Actions { get; init; }
    }

    public enum SessionStatus
    {
        Active,
        Ended,
        Suspended
    }

    public sealed class SessionAction
    {
        public required string Action { get; init; }
        public required DateTime Timestamp { get; init; }
        public string? Details { get; init; }
    }

    public sealed class SessionRecord
    {
        public required string SessionId { get; init; }
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required string Privilege { get; init; }
        public required DateTime StartedAt { get; init; }
        public required DateTime EndedAt { get; init; }
        public required TimeSpan Duration { get; init; }
        public required int ActionCount { get; init; }
        public string? JitGrantId { get; init; }
    }

    #endregion
}
