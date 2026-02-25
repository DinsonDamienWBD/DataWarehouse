using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Security.Siem
{
    /// <summary>
    /// Transport interface for delivering security events to external SIEM systems
    /// (Splunk, Azure Sentinel, QRadar, ArcSight, etc.).
    /// Implementations handle protocol-specific formatting and delivery.
    /// </summary>
    public interface ISiemTransport
    {
        /// <summary>
        /// Unique identifier for this transport instance.
        /// </summary>
        string TransportId { get; }

        /// <summary>
        /// Human-readable name (e.g., "Splunk HEC", "Syslog UDP").
        /// </summary>
        string TransportName { get; }

        /// <summary>
        /// Send a single security event to the SIEM.
        /// </summary>
        Task SendEventAsync(SiemEvent evt, CancellationToken ct = default);

        /// <summary>
        /// Send a batch of security events to the SIEM.
        /// Implementations should optimize for bulk delivery where the protocol supports it.
        /// </summary>
        Task SendBatchAsync(IReadOnlyList<SiemEvent> events, CancellationToken ct = default);

        /// <summary>
        /// Test connectivity to the SIEM endpoint.
        /// Returns true if the transport can successfully reach and authenticate with the SIEM.
        /// </summary>
        Task<bool> TestConnectionAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Severity levels for SIEM events, aligned with syslog severity (RFC 5424).
    /// </summary>
    public enum SiemSeverity
    {
        /// <summary>Informational: routine operational events (syslog 6).</summary>
        Info = 6,

        /// <summary>Low severity: minor issues that don't affect operations (syslog 5).</summary>
        Low = 5,

        /// <summary>Medium severity: conditions that may require attention (syslog 4).</summary>
        Medium = 4,

        /// <summary>High severity: significant security events requiring investigation (syslog 3).</summary>
        High = 3,

        /// <summary>Critical severity: immediate action required, active threat or breach (syslog 2).</summary>
        Critical = 2
    }

    /// <summary>
    /// A security event formatted for SIEM consumption.
    /// Contains all fields needed for CEF (Common Event Format) and syslog output.
    /// </summary>
    public sealed record SiemEvent
    {
        /// <summary>Unique event identifier.</summary>
        public string EventId { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>When the event occurred.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Severity of the event.</summary>
        public SiemSeverity Severity { get; init; } = SiemSeverity.Info;

        /// <summary>Source system or component that generated the event.</summary>
        public required string Source { get; init; }

        /// <summary>Type/category of event (e.g., "AuthFailure", "AccessDenied", "DataExfiltration").</summary>
        public required string EventType { get; init; }

        /// <summary>Human-readable description of the event.</summary>
        public required string Description { get; init; }

        /// <summary>Additional structured metadata (IP addresses, user IDs, resource paths, etc.).</summary>
        public Dictionary<string, string> Metadata { get; init; } = new();

        /// <summary>Raw event data for forensic analysis (JSON, XML, or protocol-specific format).</summary>
        public string? RawData { get; init; }

        /// <summary>
        /// Formats this event as CEF (Common Event Format) string.
        /// CEF: Version|Device Vendor|Device Product|Device Version|Event ID|Name|Severity|Extension
        /// </summary>
        public string ToCef()
        {
            var cefSeverity = Severity switch
            {
                SiemSeverity.Critical => 10,
                SiemSeverity.High => 7,
                SiemSeverity.Medium => 5,
                SiemSeverity.Low => 3,
                SiemSeverity.Info => 1,
                _ => 0
            };

            var extensions = $"rt={Timestamp:o} src={Source} msg={EscapeCef(Description)}";
            foreach (var kv in Metadata)
            {
                extensions += $" {EscapeCefKey(kv.Key)}={EscapeCef(kv.Value)}";
            }

            return $"CEF:0|DataWarehouse|SecurityMonitor|1.0|{EscapeCef(EventType)}|{EscapeCef(Description)}|{cefSeverity}|{extensions}";
        }

        /// <summary>
        /// Formats this event as RFC 5424 syslog message.
        /// </summary>
        public string ToSyslog(string hostname = "datawarehouse")
        {
            var facility = 4; // security/authorization (auth)
            var priority = facility * 8 + (int)Severity;
            var structuredData = $"[dw@0 eventId=\"{EventId}\" eventType=\"{EventType}\" source=\"{Source}\"]";
            return $"<{priority}>1 {Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} {hostname} DataWarehouse - {EventId} {structuredData} {Description}";
        }

        private static string EscapeCef(string value)
        {
            return value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("=", "\\=").Replace("\n", "\\n");
        }

        private static string EscapeCefKey(string key)
        {
            // CEF keys: alphanumeric only
            return System.Text.RegularExpressions.Regex.Replace(key, @"[^a-zA-Z0-9]", "");
        }
    }

    /// <summary>
    /// Configuration options for SIEM transport connections.
    /// </summary>
    public sealed class SiemTransportOptions
    {
        /// <summary>SIEM endpoint URI (e.g., https://splunk:8088, syslog://logserver:514).</summary>
        public Uri? Endpoint { get; set; }

        /// <summary>Authentication token for the SIEM endpoint (HEC token, API key, etc.).</summary>
        public string? AuthToken { get; set; }

        /// <summary>Number of events to batch before sending. Default: 100.</summary>
        public int BatchSize { get; set; } = 100;

        /// <summary>Maximum time (ms) to hold events before flushing. Default: 5000.</summary>
        public int FlushIntervalMs { get; set; } = 5000;

        /// <summary>Maximum retry attempts on transport failure. Default: 3.</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>Maximum events to buffer before dropping oldest. Default: 10000.</summary>
        public int MaxBufferSize { get; set; } = 10_000;

        /// <summary>Consecutive failures before opening circuit breaker. Default: 5.</summary>
        public int CircuitBreakerThreshold { get; set; } = 5;

        /// <summary>Duration (ms) to keep circuit breaker open. Default: 60000.</summary>
        public int CircuitBreakerDurationMs { get; set; } = 60_000;

        /// <summary>Path for file-based transport (air-gapped environments).</summary>
        public string? FilePath { get; set; }

        /// <summary>Custom HTTP headers for webhook/HEC transports.</summary>
        public Dictionary<string, string> CustomHeaders { get; set; } = new();
    }
}
