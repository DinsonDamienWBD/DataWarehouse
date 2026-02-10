using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.ThreatDetection
{
    /// <summary>
    /// SIEM integration strategy for forwarding security events to Splunk, Azure Sentinel, and other SIEM platforms.
    /// Supports Syslog (CEF, LEEF) and REST API integration.
    /// </summary>
    public sealed class SiemIntegrationStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private SiemPlatform _platform = SiemPlatform.Generic;
        private string? _siemEndpoint;
        private string? _apiKey;
        private SyslogFormat _syslogFormat = SyslogFormat.Cef;

        public SiemIntegrationStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        /// <inheritdoc/>
        public override string StrategyId => "siem-integration";

        /// <inheritdoc/>
        public override string StrategyName => "SIEM Integration";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 5000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("SiemEndpoint", out var endpoint) && endpoint is string endpointStr)
                _siemEndpoint = endpointStr;

            if (configuration.TryGetValue("ApiKey", out var key) && key is string keyStr)
                _apiKey = keyStr;

            if (configuration.TryGetValue("Platform", out var platform) && platform is string platformStr)
            {
                if (Enum.TryParse<SiemPlatform>(platformStr, true, out var parsedPlatform))
                    _platform = parsedPlatform;
            }

            if (configuration.TryGetValue("SyslogFormat", out var format) && format is string formatStr)
            {
                if (Enum.TryParse<SyslogFormat>(formatStr, true, out var parsedFormat))
                    _syslogFormat = parsedFormat;
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Forward event to SIEM asynchronously (don't block access decision)
            _ = Task.Run(async () =>
            {
                try
                {
                    await ForwardToSiemAsync(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to forward event to SIEM");
                }
            }, cancellationToken);

            // SIEM integration doesn't make access decisions, just logs
            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Event forwarded to SIEM for analysis",
                ApplicablePolicies = new[] { "SiemIntegration" },
                Metadata = new Dictionary<string, object>
                {
                    ["SiemPlatform"] = _platform.ToString(),
                    ["SiemEndpoint"] = _siemEndpoint ?? "not configured"
                }
            };
        }

        private async Task ForwardToSiemAsync(AccessContext context, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_siemEndpoint))
            {
                _logger.LogWarning("SIEM endpoint not configured");
                return;
            }

            var payload = _platform switch
            {
                SiemPlatform.Splunk => BuildSplunkPayload(context),
                SiemPlatform.AzureSentinel => BuildSentinelPayload(context),
                SiemPlatform.ElasticSiem => BuildElasticPayload(context),
                _ => BuildGenericPayload(context)
            };

            var request = new HttpRequestMessage(HttpMethod.Post, _siemEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrEmpty(_apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        private string BuildSplunkPayload(AccessContext context)
        {
            var splunkEvent = new
            {
                time = new DateTimeOffset(context.RequestTime).ToUnixTimeSeconds(),
                source = "DataWarehouse.AccessControl",
                sourcetype = "access:control",
                @event = new
                {
                    subject = context.SubjectId,
                    resource = context.ResourceId,
                    action = context.Action,
                    roles = context.Roles,
                    client_ip = context.ClientIpAddress,
                    location = context.Location != null ? new
                    {
                        country = context.Location.Country,
                        city = context.Location.City,
                        latitude = context.Location.Latitude,
                        longitude = context.Location.Longitude
                    } : null,
                    timestamp = context.RequestTime.ToString("O")
                }
            };

            return JsonSerializer.Serialize(splunkEvent);
        }

        private string BuildSentinelPayload(AccessContext context)
        {
            var sentinelEvent = new
            {
                TimeGenerated = context.RequestTime.ToString("O"),
                Category = "AccessControl",
                OperationName = context.Action,
                ResultType = "Success",
                Identity = new
                {
                    Claim = context.SubjectId,
                    Roles = context.Roles
                },
                Properties = new
                {
                    ResourceId = context.ResourceId,
                    Action = context.Action,
                    ClientIP = context.ClientIpAddress,
                    Location = context.Location,
                    SubjectAttributes = context.SubjectAttributes,
                    ResourceAttributes = context.ResourceAttributes
                }
            };

            return JsonSerializer.Serialize(sentinelEvent);
        }

        private string BuildElasticPayload(AccessContext context)
        {
            var elasticEvent = new
            {
                timestamp = context.RequestTime.ToString("O"),
                @event = new
                {
                    kind = "event",
                    category = new[] { "authentication", "authorization" },
                    type = new[] { "access" },
                    outcome = "success"
                },
                user = new
                {
                    id = context.SubjectId,
                    roles = context.Roles
                },
                resource = new
                {
                    id = context.ResourceId,
                    attributes = context.ResourceAttributes
                },
                action = context.Action,
                source = new
                {
                    ip = context.ClientIpAddress,
                    geo = context.Location != null ? new
                    {
                        country_name = context.Location.Country,
                        city_name = context.Location.City,
                        location = new
                        {
                            lat = context.Location.Latitude,
                            lon = context.Location.Longitude
                        }
                    } : null
                }
            };

            return JsonSerializer.Serialize(elasticEvent);
        }

        private string BuildGenericPayload(AccessContext context)
        {
            // Build CEF or LEEF format syslog message
            if (_syslogFormat == SyslogFormat.Cef)
            {
                return BuildCefMessage(context);
            }
            else
            {
                return BuildLeefMessage(context);
            }
        }

        private string BuildCefMessage(AccessContext context)
        {
            // CEF Format: CEF:Version|Device Vendor|Device Product|Device Version|Signature ID|Name|Severity|Extension
            var severity = DetermineEventSeverity(context);

            var extensions = new List<string>
            {
                $"src={context.ClientIpAddress ?? "unknown"}",
                $"suser={context.SubjectId}",
                $"act={context.Action}",
                $"rt={context.RequestTime:o}",
                $"cs1={context.ResourceId}",
                "cs1Label=ResourceId"
            };

            if (context.Location != null)
            {
                extensions.Add($"cs2={context.Location.Country}");
                extensions.Add("cs2Label=Country");
            }

            var cefMessage = $"CEF:0|DataWarehouse|AccessControl|1.0|ACCESS|Access Control Event|{severity}|{string.Join(" ", extensions)}";
            return cefMessage;
        }

        private string BuildLeefMessage(AccessContext context)
        {
            // LEEF Format: LEEF:Version|Vendor|Product|Version|EventID|delimiter|key=value pairs
            var leefMessage = $"LEEF:2.0|DataWarehouse|AccessControl|1.0|ACCESS|\t" +
                $"devTime={context.RequestTime:o}\t" +
                $"src={context.ClientIpAddress ?? "unknown"}\t" +
                $"usrName={context.SubjectId}\t" +
                $"action={context.Action}\t" +
                $"resource={context.ResourceId}\t" +
                $"cat=AccessControl";

            if (context.Location != null)
            {
                leefMessage += $"\tcountry={context.Location.Country}";
            }

            return leefMessage;
        }

        private int DetermineEventSeverity(AccessContext context)
        {
            // CEF severity: 0-10 scale
            var action = context.Action.ToLowerInvariant();

            if (action == "delete" || action == "destroy")
                return 7;

            if (action == "modify" || action == "update" || action == "write")
                return 5;

            if (action == "execute" || action == "admin")
                return 6;

            return 3; // Read/list operations
        }
    }

    /// <summary>
    /// Supported SIEM platforms.
    /// </summary>
    public enum SiemPlatform
    {
        Generic,
        Splunk,
        AzureSentinel,
        ElasticSiem,
        QRadar,
        ArcSight
    }

    /// <summary>
    /// Syslog message formats.
    /// </summary>
    public enum SyslogFormat
    {
        /// <summary>Common Event Format</summary>
        Cef,
        /// <summary>Log Event Extended Format</summary>
        Leef
    }
}
