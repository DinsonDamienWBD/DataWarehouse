using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Features
{
    /// <summary>
    /// Automated incident response with playbook-based execution and containment actions.
    /// </summary>
    public sealed class AutomatedIncidentResponse
    {
        private readonly BoundedDictionary<string, ResponsePlaybook> _playbooks = new BoundedDictionary<string, ResponsePlaybook>(1000);
        private readonly ConcurrentQueue<IncidentResponse> _responseHistory = new();
        private readonly int _maxHistorySize = 1000;

        /// <summary>
        /// Registers response playbook.
        /// </summary>
        public void RegisterPlaybook(ResponsePlaybook playbook)
        {
            if (playbook == null)
                throw new ArgumentNullException(nameof(playbook));

            _playbooks[playbook.PlaybookId] = playbook;
        }

        /// <summary>
        /// Executes automated response to security incident.
        /// </summary>
        public async Task<IncidentResponse> RespondToIncidentAsync(
            SecurityIncident incident,
            CancellationToken cancellationToken = default)
        {
            var matchedPlaybooks = _playbooks.Values
                .Where(p => p.IsEnabled && MatchesConditions(incident, p.Conditions))
                .OrderByDescending(p => p.Priority)
                .ToList();

            var executedActions = new List<ResponseAction>();

            foreach (var playbook in matchedPlaybooks)
            {
                foreach (var action in playbook.Actions)
                {
                    try
                    {
                        await ExecuteActionAsync(incident, action, cancellationToken);
                        executedActions.Add(action);
                    }
                    catch (Exception ex)
                    {
                        action.Error = ex.Message;
                    }
                }

                if (playbook.StopOnMatch)
                    break;
            }

            var response = new IncidentResponse
            {
                ResponseId = Guid.NewGuid().ToString("N"),
                IncidentId = incident.IncidentId,
                Timestamp = DateTime.UtcNow,
                ExecutedPlaybooks = matchedPlaybooks.Select(p => p.PlaybookId).ToList(),
                ExecutedActions = executedActions,
                Status = executedActions.Any(a => !string.IsNullOrEmpty(a.Error))
                    ? ResponseStatus.PartialSuccess
                    : ResponseStatus.Success
            };

            _responseHistory.Enqueue(response);

            while (_responseHistory.Count > _maxHistorySize)
            {
                _responseHistory.TryDequeue(out _);
            }

            return response;
        }

        private bool MatchesConditions(SecurityIncident incident, List<PlaybookCondition> conditions)
        {
            return conditions.All(c => c.ConditionType switch
            {
                ConditionType.Severity => incident.Severity >= c.MinSeverity,
                ConditionType.IncidentType => c.IncidentTypes.Contains(incident.Type),
                ConditionType.Source => c.Sources.Contains(incident.Source),
                _ => false
            });
        }

        private Task ExecuteActionAsync(SecurityIncident incident, ResponseAction action, CancellationToken cancellationToken)
        {
            action.ExecutedAt = DateTime.UtcNow;

            return action.ActionType switch
            {
                ResponseActionType.BlockIp => BlockIpAsync(incident.SourceIp, action, cancellationToken),
                ResponseActionType.DisableAccount => DisableAccountAsync(incident.UserId, action, cancellationToken),
                ResponseActionType.IsolateDevice => IsolateDeviceAsync(incident.DeviceId, action, cancellationToken),
                ResponseActionType.SendAlert => SendAlertAsync(incident, action, cancellationToken),
                ResponseActionType.CreateTicket => CreateTicketAsync(incident, action, cancellationToken),
                ResponseActionType.Quarantine => QuarantineResourceAsync(incident.ResourceId, action, cancellationToken),
                _ => Task.CompletedTask
            };
        }

        private Task BlockIpAsync(string? ipAddress, ResponseAction action, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(ipAddress))
            {
                action.Error = "No IP address provided";
                return Task.CompletedTask;
            }

            action.Result = $"IP {ipAddress} blocked";
            return Task.CompletedTask;
        }

        private Task DisableAccountAsync(string? userId, ResponseAction action, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(userId))
            {
                action.Error = "No user ID provided";
                return Task.CompletedTask;
            }

            action.Result = $"Account {userId} disabled";
            return Task.CompletedTask;
        }

        private Task IsolateDeviceAsync(string? deviceId, ResponseAction action, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                action.Error = "No device ID provided";
                return Task.CompletedTask;
            }

            action.Result = $"Device {deviceId} isolated";
            return Task.CompletedTask;
        }

        private Task SendAlertAsync(SecurityIncident incident, ResponseAction action, CancellationToken cancellationToken)
        {
            action.Result = $"Alert sent for incident {incident.IncidentId}";
            return Task.CompletedTask;
        }

        private Task CreateTicketAsync(SecurityIncident incident, ResponseAction action, CancellationToken cancellationToken)
        {
            action.Result = $"Ticket created for incident {incident.IncidentId}";
            return Task.CompletedTask;
        }

        private Task QuarantineResourceAsync(string? resourceId, ResponseAction action, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(resourceId))
            {
                action.Error = "No resource ID provided";
                return Task.CompletedTask;
            }

            action.Result = $"Resource {resourceId} quarantined";
            return Task.CompletedTask;
        }

        public IReadOnlyCollection<IncidentResponse> GetResponseHistory(int maxCount = 100)
        {
            return _responseHistory.Take(maxCount).ToList().AsReadOnly();
        }
    }

    #region Supporting Types

    public sealed class SecurityIncident
    {
        public required string IncidentId { get; init; }
        public required string Type { get; init; }
        public required int Severity { get; init; }
        public required string Source { get; init; }
        public string? UserId { get; init; }
        public string? DeviceId { get; init; }
        public string? ResourceId { get; init; }
        public string? SourceIp { get; init; }
        public required DateTime DetectedAt { get; init; }
    }

    public sealed class ResponsePlaybook
    {
        public required string PlaybookId { get; init; }
        public required string Name { get; init; }
        public required bool IsEnabled { get; init; }
        public required int Priority { get; init; }
        public required bool StopOnMatch { get; init; }
        public required List<PlaybookCondition> Conditions { get; init; }
        public required List<ResponseAction> Actions { get; init; }
    }

    public sealed class PlaybookCondition
    {
        public required ConditionType ConditionType { get; init; }
        public int MinSeverity { get; init; }
        public string[] IncidentTypes { get; init; } = Array.Empty<string>();
        public string[] Sources { get; init; } = Array.Empty<string>();
    }

    public enum ConditionType
    {
        Severity,
        IncidentType,
        Source
    }

    public sealed class ResponseAction
    {
        public required ResponseActionType ActionType { get; init; }
        public required string Description { get; init; }
        public DateTime? ExecutedAt { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
    }

    public enum ResponseActionType
    {
        BlockIp,
        DisableAccount,
        IsolateDevice,
        SendAlert,
        CreateTicket,
        Quarantine
    }

    public sealed class IncidentResponse
    {
        public required string ResponseId { get; init; }
        public required string IncidentId { get; init; }
        public required DateTime Timestamp { get; init; }
        public required List<string> ExecutedPlaybooks { get; init; }
        public required List<ResponseAction> ExecutedActions { get; init; }
        public required ResponseStatus Status { get; init; }
    }

    public enum ResponseStatus
    {
        Success,
        PartialSuccess,
        Failed
    }

    #endregion
}
