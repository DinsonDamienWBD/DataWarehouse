using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Features
{
    /// <summary>
    /// Automated workflows to fix compliance violations with remediation actions
    /// and rollback support.
    /// </summary>
    public sealed class AutomatedRemediationEngine
    {
        private readonly ConcurrentDictionary<string, RemediationAction> _remediationActions = new();
        private readonly ConcurrentDictionary<string, RemediationHistory> _history = new();

        /// <summary>
        /// Registers a remediation action for a specific violation code.
        /// </summary>
        public void RegisterRemediationAction(string violationCode, Func<ComplianceViolation, Task<RemediationResult>> action, Func<string, Task<bool>>? rollbackAction = null)
        {
            _remediationActions[violationCode] = new RemediationAction
            {
                ViolationCode = violationCode,
                Action = action,
                RollbackAction = rollbackAction
            };
        }

        /// <summary>
        /// Attempts to remediate a compliance violation automatically.
        /// </summary>
        public async Task<RemediationResult> RemediateAsync(ComplianceViolation violation, CancellationToken cancellationToken = default)
        {
            if (!_remediationActions.TryGetValue(violation.Code, out var remediationAction))
            {
                return new RemediationResult
                {
                    Success = false,
                    Message = $"No remediation action registered for violation code: {violation.Code}",
                    RemediationId = Guid.NewGuid().ToString()
                };
            }

            var remediationId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                var result = await remediationAction.Action(violation);
                result.RemediationId = remediationId;

                var historyEntry = new RemediationHistory
                {
                    RemediationId = remediationId,
                    ViolationCode = violation.Code,
                    ViolationDescription = violation.Description,
                    Success = result.Success,
                    Message = result.Message,
                    StartTime = startTime,
                    EndTime = DateTime.UtcNow,
                    AffectedResource = violation.AffectedResource
                };

                _history[remediationId] = historyEntry;
                return result;
            }
            catch (Exception ex)
            {
                var failureResult = new RemediationResult
                {
                    Success = false,
                    Message = $"Remediation failed with exception: {ex.Message}",
                    RemediationId = remediationId
                };

                _history[remediationId] = new RemediationHistory
                {
                    RemediationId = remediationId,
                    ViolationCode = violation.Code,
                    ViolationDescription = violation.Description,
                    Success = false,
                    Message = failureResult.Message,
                    StartTime = startTime,
                    EndTime = DateTime.UtcNow,
                    AffectedResource = violation.AffectedResource
                };

                return failureResult;
            }
        }

        /// <summary>
        /// Rolls back a remediation action.
        /// </summary>
        public async Task<bool> RollbackAsync(string remediationId, CancellationToken cancellationToken = default)
        {
            if (!_history.TryGetValue(remediationId, out var historyEntry))
            {
                return false;
            }

            if (!_remediationActions.TryGetValue(historyEntry.ViolationCode, out var remediationAction))
            {
                return false;
            }

            if (remediationAction.RollbackAction == null)
            {
                return false;
            }

            try
            {
                var rollbackSuccess = await remediationAction.RollbackAction(remediationId);

                if (rollbackSuccess)
                {
                    historyEntry.RolledBack = true;
                    historyEntry.RollbackTime = DateTime.UtcNow;
                }

                return rollbackSuccess;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets remediation history for a specific remediation ID.
        /// </summary>
        public RemediationHistory? GetHistory(string remediationId)
        {
            _history.TryGetValue(remediationId, out var history);
            return history;
        }

        /// <summary>
        /// Gets all remediation history entries.
        /// </summary>
        public IReadOnlyList<RemediationHistory> GetAllHistory()
        {
            return _history.Values.OrderByDescending(h => h.StartTime).ToList();
        }

        /// <summary>
        /// Gets success rate for a specific violation code.
        /// </summary>
        public double GetSuccessRate(string violationCode)
        {
            var entries = _history.Values.Where(h => h.ViolationCode == violationCode).ToList();
            if (entries.Count == 0)
                return 0.0;

            var successCount = entries.Count(h => h.Success);
            return (double)successCount / entries.Count * 100.0;
        }
    }

    /// <summary>
    /// Represents a remediation action for a violation code.
    /// </summary>
    public sealed class RemediationAction
    {
        public required string ViolationCode { get; init; }
        public required Func<ComplianceViolation, Task<RemediationResult>> Action { get; init; }
        public Func<string, Task<bool>>? RollbackAction { get; init; }
    }

    /// <summary>
    /// Result of a remediation attempt.
    /// </summary>
    public sealed class RemediationResult
    {
        public required bool Success { get; init; }
        public required string Message { get; init; }
        public string? RemediationId { get; set; }
        public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Historical record of a remediation attempt.
    /// </summary>
    public sealed class RemediationHistory
    {
        public required string RemediationId { get; init; }
        public required string ViolationCode { get; init; }
        public required string ViolationDescription { get; init; }
        public required bool Success { get; init; }
        public required string Message { get; init; }
        public required DateTime StartTime { get; init; }
        public required DateTime EndTime { get; init; }
        public string? AffectedResource { get; init; }
        public bool RolledBack { get; set; }
        public DateTime? RollbackTime { get; set; }
    }
}
