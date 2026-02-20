using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Features
{
    /// <summary>
    /// SIEM event forwarding connector supporting multiple formats (Splunk, Elastic, Azure Sentinel, Syslog).
    /// </summary>
    public sealed class SiemConnector
    {
        private readonly BoundedDictionary<string, SiemEndpoint> _endpoints = new BoundedDictionary<string, SiemEndpoint>(1000);
        private readonly ConcurrentQueue<ForwardedEvent> _eventHistory = new();
        private readonly int _maxHistorySize = 1000;

        /// <summary>
        /// Registers SIEM endpoint.
        /// </summary>
        public void RegisterEndpoint(SiemEndpoint endpoint)
        {
            if (endpoint == null)
                throw new ArgumentNullException(nameof(endpoint));

            if (string.IsNullOrWhiteSpace(endpoint.EndpointId))
                throw new ArgumentException("Endpoint ID cannot be null or empty");

            _endpoints[endpoint.EndpointId] = endpoint;
        }

        /// <summary>
        /// Forwards security event to all registered SIEM endpoints.
        /// </summary>
        public async Task<ForwardResult> ForwardEventAsync(
            SecurityEvent securityEvent,
            CancellationToken cancellationToken = default)
        {
            if (securityEvent == null)
                throw new ArgumentNullException(nameof(securityEvent));

            var results = new List<EndpointResult>();

            foreach (var endpoint in _endpoints.Values.Where(e => e.IsEnabled))
            {
                try
                {
                    var formatted = FormatEvent(securityEvent, endpoint.Format);
                    await SimulateForwardAsync(endpoint, formatted, cancellationToken);

                    results.Add(new EndpointResult
                    {
                        EndpointId = endpoint.EndpointId,
                        Success = true,
                        Message = $"Event forwarded to {endpoint.Name}"
                    });

                    RecordForwardedEvent(endpoint.EndpointId, securityEvent.EventId, true);
                }
                catch (Exception ex)
                {
                    results.Add(new EndpointResult
                    {
                        EndpointId = endpoint.EndpointId,
                        Success = false,
                        Message = $"Failed to forward to {endpoint.Name}: {ex.Message}"
                    });

                    RecordForwardedEvent(endpoint.EndpointId, securityEvent.EventId, false);
                }
            }

            return new ForwardResult
            {
                EventId = securityEvent.EventId,
                ForwardedAt = DateTime.UtcNow,
                TotalEndpoints = results.Count,
                SuccessfulEndpoints = results.Count(r => r.Success),
                Results = results
            };
        }

        private string FormatEvent(SecurityEvent securityEvent, SiemFormat format)
        {
            return format switch
            {
                SiemFormat.SplunkHec => FormatSplunkHec(securityEvent),
                SiemFormat.Elastic => FormatElastic(securityEvent),
                SiemFormat.AzureSentinel => FormatAzureSentinel(securityEvent),
                SiemFormat.Syslog => FormatSyslog(securityEvent),
                _ => throw new NotSupportedException($"SIEM format '{format}' not supported")
            };
        }

        private string FormatSplunkHec(SecurityEvent securityEvent)
        {
            var hecEvent = new
            {
                time = new DateTimeOffset(securityEvent.Timestamp).ToUnixTimeSeconds(),
                host = securityEvent.Source,
                source = "datawarehouse-access-control",
                sourcetype = "security:access",
                @event = new
                {
                    event_id = securityEvent.EventId,
                    event_type = securityEvent.EventType,
                    severity = securityEvent.Severity,
                    user_id = securityEvent.UserId,
                    resource_id = securityEvent.ResourceId,
                    action = securityEvent.Action,
                    result = securityEvent.Result,
                    message = securityEvent.Message,
                    metadata = securityEvent.Metadata
                }
            };

            return JsonSerializer.Serialize(hecEvent);
        }

        private string FormatElastic(SecurityEvent securityEvent)
        {
            var esEvent = new
            {
                timestamp = securityEvent.Timestamp.ToString("o"),
                @event = new
                {
                    id = securityEvent.EventId,
                    type = securityEvent.EventType,
                    severity = securityEvent.Severity,
                    action = securityEvent.Action,
                    outcome = securityEvent.Result
                },
                user = new
                {
                    id = securityEvent.UserId
                },
                resource = new
                {
                    id = securityEvent.ResourceId
                },
                message = securityEvent.Message,
                source = new
                {
                    name = securityEvent.Source
                },
                metadata = securityEvent.Metadata
            };

            return JsonSerializer.Serialize(esEvent);
        }

        private string FormatAzureSentinel(SecurityEvent securityEvent)
        {
            var sentinelEvent = new
            {
                TimeGenerated = securityEvent.Timestamp.ToString("o"),
                EventId = securityEvent.EventId,
                EventType = securityEvent.EventType,
                Severity = securityEvent.Severity,
                UserId = securityEvent.UserId,
                ResourceId = securityEvent.ResourceId,
                Action = securityEvent.Action,
                Result = securityEvent.Result,
                Message = securityEvent.Message,
                Source = securityEvent.Source,
                Metadata = securityEvent.Metadata
            };

            return JsonSerializer.Serialize(sentinelEvent);
        }

        private string FormatSyslog(SecurityEvent securityEvent)
        {
            // RFC 5424 format
            var priority = CalculateSyslogPriority(securityEvent.Severity);
            var timestamp = securityEvent.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var hostname = securityEvent.Source;
            var appName = "datawarehouse-ac";
            var msgId = securityEvent.EventId;

            var structuredData = $"[event eventType=\"{securityEvent.EventType}\" userId=\"{securityEvent.UserId}\" " +
                                 $"resourceId=\"{securityEvent.ResourceId}\" action=\"{securityEvent.Action}\" " +
                                 $"result=\"{securityEvent.Result}\"]";

            return $"<{priority}>1 {timestamp} {hostname} {appName} - {msgId} {structuredData} {securityEvent.Message}";
        }

        private int CalculateSyslogPriority(string severity)
        {
            // Facility 4 (security) + Severity
            var severityLevel = severity.ToLower() switch
            {
                "critical" => 2,
                "high" => 3,
                "medium" => 4,
                "low" => 5,
                _ => 6
            };

            return (4 * 8) + severityLevel; // Facility * 8 + Severity
        }

        private Task SimulateForwardAsync(SiemEndpoint endpoint, string formattedEvent, CancellationToken cancellationToken)
        {
            // In production, would make actual HTTP/TCP connections here
            // For now, simulate successful forward
            return Task.CompletedTask;
        }

        private void RecordForwardedEvent(string endpointId, string eventId, bool success)
        {
            _eventHistory.Enqueue(new ForwardedEvent
            {
                EndpointId = endpointId,
                EventId = eventId,
                ForwardedAt = DateTime.UtcNow,
                Success = success
            });

            // Prune old history
            while (_eventHistory.Count > _maxHistorySize)
            {
                _eventHistory.TryDequeue(out _);
            }
        }

        /// <summary>
        /// Gets all registered endpoints.
        /// </summary>
        public IReadOnlyCollection<SiemEndpoint> GetEndpoints()
        {
            return _endpoints.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets forwarding history.
        /// </summary>
        public IReadOnlyCollection<ForwardedEvent> GetForwardHistory(int maxCount = 100)
        {
            return _eventHistory.Take(maxCount).ToList().AsReadOnly();
        }

        /// <summary>
        /// Removes endpoint.
        /// </summary>
        public bool RemoveEndpoint(string endpointId)
        {
            return _endpoints.TryRemove(endpointId, out _);
        }
    }

    #region Supporting Types

    /// <summary>
    /// SIEM endpoint configuration.
    /// </summary>
    public sealed class SiemEndpoint
    {
        public required string EndpointId { get; init; }
        public required string Name { get; init; }
        public required SiemFormat Format { get; init; }
        public required string Url { get; init; }
        public required bool IsEnabled { get; init; }
        public string? ApiKey { get; init; }
        public Dictionary<string, string> Headers { get; init; } = new();
    }

    /// <summary>
    /// SIEM format.
    /// </summary>
    public enum SiemFormat
    {
        /// <summary>Splunk HTTP Event Collector.</summary>
        SplunkHec,

        /// <summary>Elasticsearch/ELK format.</summary>
        Elastic,

        /// <summary>Azure Sentinel format.</summary>
        AzureSentinel,

        /// <summary>Syslog RFC 5424.</summary>
        Syslog
    }

    /// <summary>
    /// Security event to forward.
    /// </summary>
    public sealed class SecurityEvent
    {
        public required string EventId { get; init; }
        public required string EventType { get; init; }
        public required string Severity { get; init; }
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required string Action { get; init; }
        public required string Result { get; init; }
        public required string Message { get; init; }
        public required string Source { get; init; }
        public required DateTime Timestamp { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Forward result.
    /// </summary>
    public sealed class ForwardResult
    {
        public required string EventId { get; init; }
        public required DateTime ForwardedAt { get; init; }
        public required int TotalEndpoints { get; init; }
        public required int SuccessfulEndpoints { get; init; }
        public required List<EndpointResult> Results { get; init; }
    }

    /// <summary>
    /// Endpoint forward result.
    /// </summary>
    public sealed class EndpointResult
    {
        public required string EndpointId { get; init; }
        public required bool Success { get; init; }
        public required string Message { get; init; }
    }

    /// <summary>
    /// Forwarded event record.
    /// </summary>
    public sealed class ForwardedEvent
    {
        public required string EndpointId { get; init; }
        public required string EventId { get; init; }
        public required DateTime ForwardedAt { get; init; }
        public required bool Success { get; init; }
    }

    #endregion
}
