using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.Zabbix
{
    #region Configuration

    /// <summary>
    /// Configuration options for the Zabbix plugin.
    /// </summary>
    public sealed class ZabbixConfiguration
    {
        /// <summary>
        /// Gets or sets the Zabbix server URL for API calls.
        /// </summary>
        public string ZabbixServerUrl { get; set; } = "http://localhost/zabbix/api_jsonrpc.php";

        /// <summary>
        /// Gets or sets the Zabbix sender server address.
        /// </summary>
        public string ZabbixSenderHost { get; set; } = "localhost";

        /// <summary>
        /// Gets or sets the Zabbix sender port.
        /// </summary>
        public int ZabbixSenderPort { get; set; } = 10051;

        /// <summary>
        /// Gets or sets the hostname reported to Zabbix.
        /// </summary>
        public string HostName { get; set; } = Environment.MachineName;

        /// <summary>
        /// Gets or sets the API authentication token.
        /// </summary>
        public string? ApiToken { get; set; }

        /// <summary>
        /// Gets or sets the API username (alternative to token).
        /// </summary>
        public string? ApiUsername { get; set; }

        /// <summary>
        /// Gets or sets the API password (alternative to token).
        /// </summary>
        public string? ApiPassword { get; set; }

        /// <summary>
        /// Gets or sets whether to use Zabbix Sender protocol for metrics (faster).
        /// </summary>
        public bool UseSenderProtocol { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to use JSON-RPC API for metrics (slower but more flexible).
        /// </summary>
        public bool UseApiForMetrics { get; set; } = false;

        /// <summary>
        /// Gets or sets the batch size for sending metrics.
        /// </summary>
        public int BatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the flush interval for buffered metrics.
        /// </summary>
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the connection timeout.
        /// </summary>
        public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets whether to include process metrics.
        /// </summary>
        public bool IncludeProcessMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to include runtime metrics.
        /// </summary>
        public bool IncludeRuntimeMetrics { get; set; } = true;

        /// <summary>
        /// Gets or sets the metric key prefix.
        /// </summary>
        public string? MetricPrefix { get; set; }
    }

    #endregion

    #region JSON-RPC Types

    /// <summary>
    /// Represents a JSON-RPC 2.0 request.
    /// </summary>
    internal sealed class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public object? Params { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("auth")]
        public string? Auth { get; set; }
    }

    /// <summary>
    /// Represents a JSON-RPC 2.0 response.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    internal sealed class JsonRpcResponse<T>
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = string.Empty;

        [JsonPropertyName("result")]
        public T? Result { get; set; }

        [JsonPropertyName("error")]
        public JsonRpcError? Error { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    /// <summary>
    /// Represents a JSON-RPC 2.0 error.
    /// </summary>
    internal sealed class JsonRpcError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }

    #endregion

    #region Zabbix Sender Protocol Types

    /// <summary>
    /// Represents a Zabbix sender data packet.
    /// </summary>
    internal sealed class ZabbixSenderData
    {
        [JsonPropertyName("request")]
        public string Request { get; set; } = "sender data";

        [JsonPropertyName("data")]
        public List<ZabbixMetricData> Data { get; set; } = new();

        [JsonPropertyName("clock")]
        public long? Clock { get; set; }
    }

    /// <summary>
    /// Represents a single metric in Zabbix sender protocol.
    /// </summary>
    internal sealed class ZabbixMetricData
    {
        [JsonPropertyName("host")]
        public string Host { get; set; } = string.Empty;

        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("clock")]
        public long? Clock { get; set; }

        [JsonPropertyName("ns")]
        public long? Ns { get; set; }
    }

    /// <summary>
    /// Represents a Zabbix sender response.
    /// </summary>
    internal sealed class ZabbixSenderResponse
    {
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;

        [JsonPropertyName("info")]
        public string Info { get; set; } = string.Empty;
    }

    #endregion

    #region Metric Types

    /// <summary>
    /// Represents a metric value with metadata.
    /// </summary>
    public sealed class ZabbixMetric
    {
        /// <summary>
        /// Gets or sets the metric key.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the metric value.
        /// </summary>
        public object Value { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timestamp.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the host name (optional, uses config default if not set).
        /// </summary>
        public string? HostName { get; set; }

        /// <summary>
        /// Converts the value to a string suitable for Zabbix.
        /// </summary>
        /// <returns>The string representation of the value.</returns>
        public string GetValueString()
        {
            return Value switch
            {
                string s => s,
                bool b => b ? "1" : "0",
                double d => d.ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
                float f => f.ToString("G9", System.Globalization.CultureInfo.InvariantCulture),
                int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => Value?.ToString() ?? string.Empty
            };
        }
    }

    #endregion

    #region API Parameter Types

    /// <summary>
    /// Parameters for user.login method.
    /// </summary>
    internal sealed class LoginParams
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }
    }

    /// <summary>
    /// Parameters for item.get method.
    /// </summary>
    internal sealed class ItemGetParams
    {
        [JsonPropertyName("host")]
        public string? Host { get; set; }

        [JsonPropertyName("output")]
        public string Output { get; set; } = "extend";

        [JsonPropertyName("filter")]
        public Dictionary<string, object>? Filter { get; set; }
    }

    /// <summary>
    /// Parameters for history.push method (custom extension).
    /// </summary>
    internal sealed class HistoryPushParams
    {
        [JsonPropertyName("items")]
        public List<HistoryItem> Items { get; set; } = new();
    }

    /// <summary>
    /// Represents a history item for pushing via API.
    /// </summary>
    internal sealed class HistoryItem
    {
        [JsonPropertyName("itemid")]
        public string ItemId { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("clock")]
        public long Clock { get; set; }

        [JsonPropertyName("ns")]
        public long Ns { get; set; }
    }

    #endregion
}
