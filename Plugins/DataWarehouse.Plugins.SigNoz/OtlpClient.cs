using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.SigNoz
{
    /// <summary>
    /// HTTP client for sending OTLP data to SigNoz.
    /// Handles metrics, logs, and traces export via OTLP/HTTP protocol.
    /// </summary>
    public sealed class OtlpClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly SigNozConfiguration _config;
        private readonly string _metricsEndpoint;
        private readonly string _logsEndpoint;
        private readonly string _tracesEndpoint;
        private readonly JsonSerializerOptions _jsonOptions;
        private long _exportedMetrics;
        private long _exportedLogs;
        private long _exportedTraces;
        private long _exportErrors;

        /// <summary>
        /// Gets the total number of exported metrics.
        /// </summary>
        public long ExportedMetrics => Interlocked.Read(ref _exportedMetrics);

        /// <summary>
        /// Gets the total number of exported logs.
        /// </summary>
        public long ExportedLogs => Interlocked.Read(ref _exportedLogs);

        /// <summary>
        /// Gets the total number of exported traces.
        /// </summary>
        public long ExportedTraces => Interlocked.Read(ref _exportedTraces);

        /// <summary>
        /// Gets the total number of export errors.
        /// </summary>
        public long ExportErrors => Interlocked.Read(ref _exportErrors);

        /// <summary>
        /// Initializes a new instance of the <see cref="OtlpClient"/> class.
        /// </summary>
        /// <param name="config">The SigNoz configuration.</param>
        public OtlpClient(SigNozConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _httpClient = new HttpClient
            {
                Timeout = config.HttpTimeout
            };

            // Set up endpoints
            var baseUrl = _config.SigNozUrl.TrimEnd('/');
            _metricsEndpoint = $"{baseUrl}/v1/metrics";
            _logsEndpoint = $"{baseUrl}/v1/logs";
            _tracesEndpoint = $"{baseUrl}/v1/traces";

            // Configure headers
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            foreach (var header in _config.Headers)
            {
                _httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
            }

            // JSON serialization options
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
        }

        /// <summary>
        /// Exports metrics to SigNoz via OTLP.
        /// </summary>
        /// <param name="request">The OTLP metrics export request.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if successful; otherwise, false.</returns>
        public async Task<bool> ExportMetricsAsync(OtlpMetricsExportRequest request, CancellationToken ct = default)
        {
            if (!_config.EnableMetrics)
            {
                return false;
            }

            try
            {
                var json = JsonSerializer.Serialize(request, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_metricsEndpoint, content, ct);
                response.EnsureSuccessStatusCode();

                Interlocked.Increment(ref _exportedMetrics);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _exportErrors);
                Console.Error.WriteLine($"[SigNoz] Error exporting metrics: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Exports logs to SigNoz via OTLP.
        /// </summary>
        /// <param name="request">The OTLP logs export request.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if successful; otherwise, false.</returns>
        public async Task<bool> ExportLogsAsync(OtlpLogsExportRequest request, CancellationToken ct = default)
        {
            if (!_config.EnableLogs)
            {
                return false;
            }

            try
            {
                var json = JsonSerializer.Serialize(request, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_logsEndpoint, content, ct);
                response.EnsureSuccessStatusCode();

                Interlocked.Increment(ref _exportedLogs);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _exportErrors);
                Console.Error.WriteLine($"[SigNoz] Error exporting logs: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Exports traces to SigNoz via OTLP.
        /// </summary>
        /// <param name="request">The OTLP traces export request.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if successful; otherwise, false.</returns>
        public async Task<bool> ExportTracesAsync(OtlpTracesExportRequest request, CancellationToken ct = default)
        {
            if (!_config.EnableTraces)
            {
                return false;
            }

            try
            {
                var json = JsonSerializer.Serialize(request, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_tracesEndpoint, content, ct);
                response.EnsureSuccessStatusCode();

                Interlocked.Increment(ref _exportedTraces);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _exportErrors);
                Console.Error.WriteLine($"[SigNoz] Error exporting traces: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates a resource with common service attributes.
        /// </summary>
        /// <returns>An OTLP resource.</returns>
        public OtlpResource CreateResource()
        {
            var resource = new OtlpResource();

            // Service attributes
            resource.Attributes.Add(new OtlpKeyValue
            {
                Key = "service.name",
                Value = new OtlpAnyValue { StringValue = _config.ServiceName }
            });

            resource.Attributes.Add(new OtlpKeyValue
            {
                Key = "service.version",
                Value = new OtlpAnyValue { StringValue = _config.ServiceVersion }
            });

            // Optional attributes
            if (!string.IsNullOrEmpty(_config.Environment))
            {
                resource.Attributes.Add(new OtlpKeyValue
                {
                    Key = "deployment.environment",
                    Value = new OtlpAnyValue { StringValue = _config.Environment }
                });
            }

            if (!string.IsNullOrEmpty(_config.Deployment))
            {
                resource.Attributes.Add(new OtlpKeyValue
                {
                    Key = "deployment.name",
                    Value = new OtlpAnyValue { StringValue = _config.Deployment }
                });
            }

            // Process information
            var process = Process.GetCurrentProcess();
            resource.Attributes.Add(new OtlpKeyValue
            {
                Key = "process.pid",
                Value = new OtlpAnyValue { IntValue = process.Id }
            });

            resource.Attributes.Add(new OtlpKeyValue
            {
                Key = "process.executable.name",
                Value = new OtlpAnyValue { StringValue = process.ProcessName }
            });

            // Host information
            resource.Attributes.Add(new OtlpKeyValue
            {
                Key = "host.name",
                Value = new OtlpAnyValue { StringValue = Environment.MachineName }
            });

            resource.Attributes.Add(new OtlpKeyValue
            {
                Key = "os.type",
                Value = new OtlpAnyValue { StringValue = Environment.OSVersion.Platform.ToString() }
            });

            return resource;
        }

        /// <summary>
        /// Creates an instrumentation scope.
        /// </summary>
        /// <param name="name">The scope name.</param>
        /// <param name="version">The scope version.</param>
        /// <returns>An OTLP instrumentation scope.</returns>
        public OtlpInstrumentationScope CreateScope(string name = "datawarehouse", string? version = null)
        {
            return new OtlpInstrumentationScope
            {
                Name = name,
                Version = version ?? _config.ServiceVersion
            };
        }

        /// <summary>
        /// Converts a DateTime to OTLP Unix nanoseconds format.
        /// </summary>
        /// <param name="dateTime">The datetime to convert.</param>
        /// <returns>Unix nanoseconds as a string.</returns>
        public static string ToUnixNano(DateTime dateTime)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var nanos = (dateTime.ToUniversalTime() - epoch).Ticks * 100;
            return nanos.ToString();
        }

        /// <summary>
        /// Creates attributes from a dictionary.
        /// </summary>
        /// <param name="attributes">The attribute dictionary.</param>
        /// <returns>A list of OTLP key-value pairs.</returns>
        public static List<OtlpKeyValue> CreateAttributes(Dictionary<string, object>? attributes)
        {
            var result = new List<OtlpKeyValue>();

            if (attributes == null)
            {
                return result;
            }

            foreach (var kvp in attributes)
            {
                var anyValue = new OtlpAnyValue();

                switch (kvp.Value)
                {
                    case string s:
                        anyValue.StringValue = s;
                        break;
                    case int i:
                        anyValue.IntValue = i;
                        break;
                    case long l:
                        anyValue.IntValue = l;
                        break;
                    case double d:
                        anyValue.DoubleValue = d;
                        break;
                    case float f:
                        anyValue.DoubleValue = f;
                        break;
                    case bool b:
                        anyValue.BoolValue = b;
                        break;
                    default:
                        anyValue.StringValue = kvp.Value?.ToString() ?? string.Empty;
                        break;
                }

                result.Add(new OtlpKeyValue
                {
                    Key = kvp.Key,
                    Value = anyValue
                });
            }

            return result;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
