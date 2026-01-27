using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.K8sOperator.Managers;

/// <summary>
/// Manages Prometheus monitoring integration for DataWarehouse Kubernetes resources.
/// Provides ServiceMonitor CRD creation, PodMonitor CRD creation, custom metrics endpoint,
/// Alertmanager rules, and Grafana dashboard ConfigMap creation.
/// </summary>
public sealed class PrometheusIntegration
{
    private readonly IKubernetesClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the PrometheusIntegration.
    /// </summary>
    /// <param name="client">Kubernetes client for API interactions.</param>
    public PrometheusIntegration(IKubernetesClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Creates a ServiceMonitor for scraping metrics from a Kubernetes Service.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="name">ServiceMonitor name.</param>
    /// <param name="config">ServiceMonitor configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the ServiceMonitor creation.</returns>
    public async Task<PrometheusResult> CreateServiceMonitorAsync(
        string namespaceName,
        string name,
        ServiceMonitorConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        var serviceMonitor = new ServiceMonitor
        {
            ApiVersion = "monitoring.coreos.com/v1",
            Kind = "ServiceMonitor",
            Metadata = new ObjectMeta
            {
                Name = name,
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/name"] = name
                }
            },
            Spec = new ServiceMonitorSpec
            {
                Selector = new LabelSelector
                {
                    MatchLabels = config.ServiceSelector
                },
                NamespaceSelector = config.NamespaceSelector != null
                    ? new NamespaceSelector
                    {
                        MatchNames = config.NamespaceSelector
                    }
                    : null,
                Endpoints = config.Endpoints.Select(e => new Endpoint
                {
                    Port = e.Port,
                    Path = e.Path ?? "/metrics",
                    Interval = e.Interval ?? "30s",
                    ScrapeTimeout = e.ScrapeTimeout ?? "10s",
                    Scheme = e.Scheme ?? "http",
                    TlsConfig = e.TlsConfig != null ? new TlsConfig
                    {
                        InsecureSkipVerify = e.TlsConfig.InsecureSkipVerify,
                        CaFile = e.TlsConfig.CaFile,
                        CertFile = e.TlsConfig.CertFile,
                        KeyFile = e.TlsConfig.KeyFile
                    } : null,
                    BearerTokenSecret = e.BearerTokenSecretName != null ? new SecretKeySelector
                    {
                        Name = e.BearerTokenSecretName,
                        Key = e.BearerTokenSecretKey ?? "token"
                    } : null,
                    BasicAuth = e.BasicAuthSecret != null ? new BasicAuth
                    {
                        Username = new SecretKeySelector
                        {
                            Name = e.BasicAuthSecret,
                            Key = "username"
                        },
                        Password = new SecretKeySelector
                        {
                            Name = e.BasicAuthSecret,
                            Key = "password"
                        }
                    } : null,
                    RelabelConfigs = e.RelabelConfigs?.Select(r => new RelabelConfig
                    {
                        SourceLabels = r.SourceLabels,
                        Separator = r.Separator,
                        TargetLabel = r.TargetLabel,
                        Regex = r.Regex,
                        Replacement = r.Replacement,
                        Action = r.Action
                    }).ToList(),
                    MetricRelabelConfigs = e.MetricRelabelConfigs?.Select(r => new RelabelConfig
                    {
                        SourceLabels = r.SourceLabels,
                        Separator = r.Separator,
                        TargetLabel = r.TargetLabel,
                        Regex = r.Regex,
                        Replacement = r.Replacement,
                        Action = r.Action
                    }).ToList()
                }).ToList(),
                JobLabel = config.JobLabel,
                TargetLabels = config.TargetLabels,
                PodTargetLabels = config.PodTargetLabels
            }
        };

        var json = JsonSerializer.Serialize(serviceMonitor, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "monitoring.coreos.com/v1",
            "servicemonitors",
            namespaceName,
            name,
            json,
            ct);

        return new PrometheusResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "ServiceMonitor",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a PodMonitor for scraping metrics directly from pods.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="name">PodMonitor name.</param>
    /// <param name="config">PodMonitor configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the PodMonitor creation.</returns>
    public async Task<PrometheusResult> CreatePodMonitorAsync(
        string namespaceName,
        string name,
        PodMonitorConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        var podMonitor = new PodMonitor
        {
            ApiVersion = "monitoring.coreos.com/v1",
            Kind = "PodMonitor",
            Metadata = new ObjectMeta
            {
                Name = name,
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["app.kubernetes.io/name"] = name
                }
            },
            Spec = new PodMonitorSpec
            {
                Selector = new LabelSelector
                {
                    MatchLabels = config.PodSelector
                },
                NamespaceSelector = config.NamespaceSelector != null
                    ? new NamespaceSelector
                    {
                        MatchNames = config.NamespaceSelector
                    }
                    : null,
                PodMetricsEndpoints = config.Endpoints.Select(e => new PodMetricsEndpoint
                {
                    Port = e.Port,
                    Path = e.Path ?? "/metrics",
                    Interval = e.Interval ?? "30s",
                    ScrapeTimeout = e.ScrapeTimeout ?? "10s",
                    Scheme = e.Scheme ?? "http"
                }).ToList(),
                JobLabel = config.JobLabel,
                PodTargetLabels = config.PodTargetLabels
            }
        };

        var json = JsonSerializer.Serialize(podMonitor, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "monitoring.coreos.com/v1",
            "podmonitors",
            namespaceName,
            name,
            json,
            ct);

        return new PrometheusResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "PodMonitor",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates PrometheusRule for alerting.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="name">PrometheusRule name.</param>
    /// <param name="config">Alert rule configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the PrometheusRule creation.</returns>
    public async Task<PrometheusResult> CreatePrometheusRuleAsync(
        string namespaceName,
        string name,
        PrometheusRuleConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        var prometheusRule = new PrometheusRule
        {
            ApiVersion = "monitoring.coreos.com/v1",
            Kind = "PrometheusRule",
            Metadata = new ObjectMeta
            {
                Name = name,
                Namespace = namespaceName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["prometheus"] = config.PrometheusSelector ?? "kube-prometheus"
                }
            },
            Spec = new PrometheusRuleSpec
            {
                Groups = config.Groups.Select(g => new RuleGroup
                {
                    Name = g.Name,
                    Interval = g.Interval,
                    Rules = g.Rules.Select(r => new Rule
                    {
                        Alert = r.Alert,
                        Expr = r.Expression,
                        For = r.For,
                        Labels = r.Labels,
                        Annotations = r.Annotations
                    }).ToList()
                }).ToList()
            }
        };

        var json = JsonSerializer.Serialize(prometheusRule, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "monitoring.coreos.com/v1",
            "prometheusrules",
            namespaceName,
            name,
            json,
            ct);

        return new PrometheusResult
        {
            Success = result.Success,
            ResourceName = name,
            ResourceKind = "PrometheusRule",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates a ConfigMap containing a Grafana dashboard.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="name">Dashboard name.</param>
    /// <param name="config">Dashboard configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the ConfigMap creation.</returns>
    public async Task<PrometheusResult> CreateGrafanaDashboardAsync(
        string namespaceName,
        string name,
        GrafanaDashboardConfig config,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        var dashboardJson = config.DashboardJson ?? GenerateDefaultDashboard(name, config);

        var configMap = new
        {
            apiVersion = "v1",
            kind = "ConfigMap",
            metadata = new
            {
                name = $"{name}-dashboard",
                @namespace = namespaceName,
                labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "datawarehouse-operator",
                    ["grafana_dashboard"] = "1" // Label for Grafana sidecar discovery
                }
            },
            data = new Dictionary<string, string>
            {
                [$"{name}.json"] = dashboardJson
            }
        };

        var json = JsonSerializer.Serialize(configMap, _jsonOptions);
        var result = await _client.ApplyResourceAsync(
            "v1",
            "configmaps",
            namespaceName,
            $"{name}-dashboard",
            json,
            ct);

        return new PrometheusResult
        {
            Success = result.Success,
            ResourceName = $"{name}-dashboard",
            ResourceKind = "ConfigMap",
            Message = result.Message
        };
    }

    /// <summary>
    /// Creates default DataWarehouse monitoring resources.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="componentName">Component name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the monitoring setup.</returns>
    public async Task<MonitoringSetupResult> SetupDefaultMonitoringAsync(
        string namespaceName,
        string componentName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);

        var results = new List<PrometheusResult>();

        // 1. Create ServiceMonitor
        var smResult = await CreateServiceMonitorAsync(
            namespaceName,
            $"{componentName}-metrics",
            new ServiceMonitorConfig
            {
                ServiceSelector = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/name"] = componentName
                },
                Endpoints = new List<EndpointConfig>
                {
                    new EndpointConfig
                    {
                        Port = "metrics",
                        Path = "/metrics",
                        Interval = "30s"
                    }
                },
                JobLabel = "app.kubernetes.io/name"
            },
            ct);
        results.Add(smResult);

        // 2. Create PrometheusRules
        var rulesResult = await CreatePrometheusRuleAsync(
            namespaceName,
            $"{componentName}-alerts",
            new PrometheusRuleConfig
            {
                Groups = new List<RuleGroupConfig>
                {
                    new RuleGroupConfig
                    {
                        Name = $"{componentName}.rules",
                        Rules = GetDefaultAlertRules(componentName)
                    }
                }
            },
            ct);
        results.Add(rulesResult);

        // 3. Create Grafana Dashboard
        var dashResult = await CreateGrafanaDashboardAsync(
            namespaceName,
            componentName,
            new GrafanaDashboardConfig
            {
                Title = $"DataWarehouse - {componentName}",
                GenerateDefault = true
            },
            ct);
        results.Add(dashResult);

        return new MonitoringSetupResult
        {
            Success = results.All(r => r.Success),
            Results = results,
            Message = results.All(r => r.Success)
                ? "Monitoring setup completed successfully"
                : "Some monitoring resources failed to create"
        };
    }

    /// <summary>
    /// Deletes monitoring resources for a component.
    /// </summary>
    /// <param name="namespaceName">Target namespace.</param>
    /// <param name="componentName">Component name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the deletion.</returns>
    public async Task<PrometheusResult> DeleteMonitoringResourcesAsync(
        string namespaceName,
        string componentName,
        CancellationToken ct = default)
    {
        var errors = new List<string>();

        // Delete ServiceMonitor
        var smResult = await _client.DeleteResourceAsync(
            "monitoring.coreos.com/v1",
            "servicemonitors",
            namespaceName,
            $"{componentName}-metrics",
            ct);
        if (!smResult.Success && !smResult.NotFound)
            errors.Add($"ServiceMonitor: {smResult.Message}");

        // Delete PrometheusRule
        var ruleResult = await _client.DeleteResourceAsync(
            "monitoring.coreos.com/v1",
            "prometheusrules",
            namespaceName,
            $"{componentName}-alerts",
            ct);
        if (!ruleResult.Success && !ruleResult.NotFound)
            errors.Add($"PrometheusRule: {ruleResult.Message}");

        // Delete Dashboard ConfigMap
        var dashResult = await _client.DeleteResourceAsync(
            "v1",
            "configmaps",
            namespaceName,
            $"{componentName}-dashboard",
            ct);
        if (!dashResult.Success && !dashResult.NotFound)
            errors.Add($"Dashboard: {dashResult.Message}");

        return new PrometheusResult
        {
            Success = errors.Count == 0,
            ResourceName = componentName,
            ResourceKind = "MonitoringResources",
            Message = errors.Count > 0 ? string.Join("; ", errors) : "All monitoring resources deleted"
        };
    }

    private List<AlertRuleConfig> GetDefaultAlertRules(string componentName)
    {
        return new List<AlertRuleConfig>
        {
            new AlertRuleConfig
            {
                Alert = $"{componentName}Down",
                Expression = $"up{{job=\"{componentName}\"}} == 0",
                For = "5m",
                Labels = new Dictionary<string, string>
                {
                    ["severity"] = "critical",
                    ["component"] = componentName
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = $"{componentName} instance is down",
                    ["description"] = $"{{{{ $labels.instance }}}} of job {{{{ $labels.job }}}} has been down for more than 5 minutes."
                }
            },
            new AlertRuleConfig
            {
                Alert = $"{componentName}HighMemory",
                Expression = $"process_resident_memory_bytes{{job=\"{componentName}\"}} > 2e9",
                For = "10m",
                Labels = new Dictionary<string, string>
                {
                    ["severity"] = "warning",
                    ["component"] = componentName
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = $"{componentName} is using high memory",
                    ["description"] = $"{{{{ $labels.instance }}}} is using more than 2GB of memory."
                }
            },
            new AlertRuleConfig
            {
                Alert = $"{componentName}HighCPU",
                Expression = $"rate(process_cpu_seconds_total{{job=\"{componentName}\"}}[5m]) > 0.8",
                For = "10m",
                Labels = new Dictionary<string, string>
                {
                    ["severity"] = "warning",
                    ["component"] = componentName
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = $"{componentName} is using high CPU",
                    ["description"] = $"{{{{ $labels.instance }}}} CPU usage is above 80% for more than 10 minutes."
                }
            },
            new AlertRuleConfig
            {
                Alert = $"{componentName}HighLatency",
                Expression = $"histogram_quantile(0.99, sum(rate(http_request_duration_seconds_bucket{{job=\"{componentName}\"}}[5m])) by (le)) > 1",
                For = "5m",
                Labels = new Dictionary<string, string>
                {
                    ["severity"] = "warning",
                    ["component"] = componentName
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = $"{componentName} has high latency",
                    ["description"] = $"99th percentile latency is above 1 second."
                }
            },
            new AlertRuleConfig
            {
                Alert = $"{componentName}HighErrorRate",
                Expression = $"sum(rate(http_requests_total{{job=\"{componentName}\",status=~\"5..\"}}[5m])) / sum(rate(http_requests_total{{job=\"{componentName}\"}}[5m])) > 0.05",
                For = "5m",
                Labels = new Dictionary<string, string>
                {
                    ["severity"] = "critical",
                    ["component"] = componentName
                },
                Annotations = new Dictionary<string, string>
                {
                    ["summary"] = $"{componentName} has high error rate",
                    ["description"] = $"Error rate is above 5% for more than 5 minutes."
                }
            }
        };
    }

    private string GenerateDefaultDashboard(string name, GrafanaDashboardConfig config)
    {
        var dashboard = new
        {
            annotations = new { list = Array.Empty<object>() },
            editable = true,
            fiscalYearStartMonth = 0,
            graphTooltip = 0,
            id = (int?)null,
            links = Array.Empty<object>(),
            liveNow = false,
            panels = new object[]
            {
                new
                {
                    datasource = new { type = "prometheus", uid = "${datasource}" },
                    fieldConfig = new
                    {
                        defaults = new { color = new { mode = "palette-classic" }, mappings = Array.Empty<object>(), thresholds = new { mode = "absolute", steps = new object[] { new { color = "green", value = (int?)null }, new { color = "red", value = 80 } } } },
                        overrides = Array.Empty<object>()
                    },
                    gridPos = new { h = 8, w = 12, x = 0, y = 0 },
                    id = 1,
                    options = new { legend = new { calcs = Array.Empty<string>(), displayMode = "list", placement = "bottom", showLegend = true }, tooltip = new { mode = "single", sort = "none" } },
                    targets = new[] { new { datasource = new { type = "prometheus", uid = "${datasource}" }, expr = $"up{{job=\"{name}\"}}", legendFormat = "{{{{instance}}}}", refId = "A" } },
                    title = "Up Status",
                    type = "timeseries"
                },
                new
                {
                    datasource = new { type = "prometheus", uid = "${datasource}" },
                    fieldConfig = new
                    {
                        defaults = new { color = new { mode = "palette-classic" }, mappings = Array.Empty<object>(), thresholds = new { mode = "absolute", steps = new object[] { new { color = "green", value = (int?)null }, new { color = "red", value = 80 } } }, unit = "bytes" },
                        overrides = Array.Empty<object>()
                    },
                    gridPos = new { h = 8, w = 12, x = 12, y = 0 },
                    id = 2,
                    options = new { legend = new { calcs = Array.Empty<string>(), displayMode = "list", placement = "bottom", showLegend = true }, tooltip = new { mode = "single", sort = "none" } },
                    targets = new[] { new { datasource = new { type = "prometheus", uid = "${datasource}" }, expr = $"process_resident_memory_bytes{{job=\"{name}\"}}", legendFormat = "{{{{instance}}}}", refId = "A" } },
                    title = "Memory Usage",
                    type = "timeseries"
                },
                new
                {
                    datasource = new { type = "prometheus", uid = "${datasource}" },
                    fieldConfig = new
                    {
                        defaults = new { color = new { mode = "palette-classic" }, mappings = Array.Empty<object>(), thresholds = new { mode = "absolute", steps = new object[] { new { color = "green", value = (int?)null }, new { color = "red", value = 80 } } }, unit = "percentunit" },
                        overrides = Array.Empty<object>()
                    },
                    gridPos = new { h = 8, w = 12, x = 0, y = 8 },
                    id = 3,
                    options = new { legend = new { calcs = Array.Empty<string>(), displayMode = "list", placement = "bottom", showLegend = true }, tooltip = new { mode = "single", sort = "none" } },
                    targets = new[] { new { datasource = new { type = "prometheus", uid = "${datasource}" }, expr = $"rate(process_cpu_seconds_total{{job=\"{name}\"}}[5m])", legendFormat = "{{{{instance}}}}", refId = "A" } },
                    title = "CPU Usage",
                    type = "timeseries"
                },
                new
                {
                    datasource = new { type = "prometheus", uid = "${datasource}" },
                    fieldConfig = new
                    {
                        defaults = new { color = new { mode = "palette-classic" }, mappings = Array.Empty<object>(), thresholds = new { mode = "absolute", steps = new object[] { new { color = "green", value = (int?)null }, new { color = "red", value = 80 } } }, unit = "reqps" },
                        overrides = Array.Empty<object>()
                    },
                    gridPos = new { h = 8, w = 12, x = 12, y = 8 },
                    id = 4,
                    options = new { legend = new { calcs = Array.Empty<string>(), displayMode = "list", placement = "bottom", showLegend = true }, tooltip = new { mode = "single", sort = "none" } },
                    targets = new[] { new { datasource = new { type = "prometheus", uid = "${datasource}" }, expr = $"sum(rate(http_requests_total{{job=\"{name}\"}}[5m])) by (instance)", legendFormat = "{{{{instance}}}}", refId = "A" } },
                    title = "Request Rate",
                    type = "timeseries"
                }
            },
            schemaVersion = 38,
            style = "dark",
            tags = new[] { "datawarehouse", name },
            templating = new
            {
                list = new[]
                {
                    new
                    {
                        current = new { selected = false, text = "Prometheus", value = "Prometheus" },
                        hide = 0,
                        includeAll = false,
                        multi = false,
                        name = "datasource",
                        options = Array.Empty<object>(),
                        query = "prometheus",
                        queryValue = "",
                        refresh = 1,
                        regex = "",
                        skipUrlSync = false,
                        type = "datasource"
                    }
                }
            },
            time = new { from = "now-6h", to = "now" },
            timepicker = new { },
            timezone = "",
            title = config.Title ?? $"DataWarehouse - {name}",
            uid = $"dw-{name}",
            version = 1,
            weekStart = ""
        };

        return JsonSerializer.Serialize(dashboard, new JsonSerializerOptions { WriteIndented = true });
    }
}

#region Configuration Classes

/// <summary>Configuration for ServiceMonitor creation.</summary>
public sealed class ServiceMonitorConfig
{
    /// <summary>Label selector for matching services.</summary>
    public Dictionary<string, string> ServiceSelector { get; set; } = new();

    /// <summary>Namespace selector (list of namespaces to match).</summary>
    public List<string>? NamespaceSelector { get; set; }

    /// <summary>Endpoint configurations.</summary>
    public List<EndpointConfig> Endpoints { get; set; } = new();

    /// <summary>Label to use as the job name.</summary>
    public string? JobLabel { get; set; }

    /// <summary>Labels to transfer from the Kubernetes Service to the target.</summary>
    public List<string>? TargetLabels { get; set; }

    /// <summary>Labels to transfer from the Kubernetes Pod to the target.</summary>
    public List<string>? PodTargetLabels { get; set; }
}

/// <summary>Configuration for a scrape endpoint.</summary>
public sealed class EndpointConfig
{
    /// <summary>Port name or number.</summary>
    public string Port { get; set; } = string.Empty;

    /// <summary>HTTP path to scrape metrics from.</summary>
    public string? Path { get; set; }

    /// <summary>Scrape interval.</summary>
    public string? Interval { get; set; }

    /// <summary>Scrape timeout.</summary>
    public string? ScrapeTimeout { get; set; }

    /// <summary>Scheme (http or https).</summary>
    public string? Scheme { get; set; }

    /// <summary>TLS configuration.</summary>
    public TlsConfigSettings? TlsConfig { get; set; }

    /// <summary>Secret name containing bearer token.</summary>
    public string? BearerTokenSecretName { get; set; }

    /// <summary>Key in the secret containing the bearer token.</summary>
    public string? BearerTokenSecretKey { get; set; }

    /// <summary>Secret name for basic auth credentials.</summary>
    public string? BasicAuthSecret { get; set; }

    /// <summary>Relabel configurations.</summary>
    public List<RelabelConfigSettings>? RelabelConfigs { get; set; }

    /// <summary>Metric relabel configurations.</summary>
    public List<RelabelConfigSettings>? MetricRelabelConfigs { get; set; }
}

/// <summary>TLS configuration settings.</summary>
public sealed class TlsConfigSettings
{
    /// <summary>Skip TLS verification.</summary>
    public bool InsecureSkipVerify { get; set; }

    /// <summary>CA file path.</summary>
    public string? CaFile { get; set; }

    /// <summary>Certificate file path.</summary>
    public string? CertFile { get; set; }

    /// <summary>Key file path.</summary>
    public string? KeyFile { get; set; }
}

/// <summary>Relabel configuration settings.</summary>
public sealed class RelabelConfigSettings
{
    /// <summary>Source labels.</summary>
    public List<string>? SourceLabels { get; set; }

    /// <summary>Separator.</summary>
    public string? Separator { get; set; }

    /// <summary>Target label.</summary>
    public string? TargetLabel { get; set; }

    /// <summary>Regular expression.</summary>
    public string? Regex { get; set; }

    /// <summary>Replacement value.</summary>
    public string? Replacement { get; set; }

    /// <summary>Relabel action.</summary>
    public string? Action { get; set; }
}

/// <summary>Configuration for PodMonitor creation.</summary>
public sealed class PodMonitorConfig
{
    /// <summary>Label selector for matching pods.</summary>
    public Dictionary<string, string> PodSelector { get; set; } = new();

    /// <summary>Namespace selector.</summary>
    public List<string>? NamespaceSelector { get; set; }

    /// <summary>Endpoint configurations.</summary>
    public List<EndpointConfig> Endpoints { get; set; } = new();

    /// <summary>Label to use as the job name.</summary>
    public string? JobLabel { get; set; }

    /// <summary>Labels to transfer from the Kubernetes Pod to the target.</summary>
    public List<string>? PodTargetLabels { get; set; }
}

/// <summary>Configuration for PrometheusRule creation.</summary>
public sealed class PrometheusRuleConfig
{
    /// <summary>Rule groups.</summary>
    public List<RuleGroupConfig> Groups { get; set; } = new();

    /// <summary>Prometheus selector label value.</summary>
    public string? PrometheusSelector { get; set; }
}

/// <summary>Configuration for a rule group.</summary>
public sealed class RuleGroupConfig
{
    /// <summary>Group name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Evaluation interval.</summary>
    public string? Interval { get; set; }

    /// <summary>Alert rules in this group.</summary>
    public List<AlertRuleConfig> Rules { get; set; } = new();
}

/// <summary>Configuration for an alert rule.</summary>
public sealed class AlertRuleConfig
{
    /// <summary>Alert name.</summary>
    public string Alert { get; set; } = string.Empty;

    /// <summary>PromQL expression.</summary>
    public string Expression { get; set; } = string.Empty;

    /// <summary>For duration before firing.</summary>
    public string? For { get; set; }

    /// <summary>Labels to attach to the alert.</summary>
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>Annotations for the alert.</summary>
    public Dictionary<string, string>? Annotations { get; set; }
}

/// <summary>Configuration for Grafana dashboard creation.</summary>
public sealed class GrafanaDashboardConfig
{
    /// <summary>Dashboard title.</summary>
    public string? Title { get; set; }

    /// <summary>Custom dashboard JSON.</summary>
    public string? DashboardJson { get; set; }

    /// <summary>Whether to generate default dashboard.</summary>
    public bool GenerateDefault { get; set; } = true;
}

/// <summary>Result of a Prometheus operation.</summary>
public sealed class PrometheusResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Name of the resource.</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Kind of the resource.</summary>
    public string ResourceKind { get; set; } = string.Empty;

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Result of a complete monitoring setup.</summary>
public sealed class MonitoringSetupResult
{
    /// <summary>Whether the setup succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Individual operation results.</summary>
    public List<PrometheusResult> Results { get; set; } = new();

    /// <summary>Result message.</summary>
    public string Message { get; set; } = string.Empty;
}

#endregion

#region Kubernetes Resource Types

internal sealed class ServiceMonitor
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public ServiceMonitorSpec Spec { get; set; } = new();
}

internal sealed class ServiceMonitorSpec
{
    public LabelSelector? Selector { get; set; }
    public NamespaceSelector? NamespaceSelector { get; set; }
    public List<Endpoint>? Endpoints { get; set; }
    public string? JobLabel { get; set; }
    public List<string>? TargetLabels { get; set; }
    public List<string>? PodTargetLabels { get; set; }
}

internal sealed class NamespaceSelector
{
    public bool? Any { get; set; }
    public List<string>? MatchNames { get; set; }
}

internal sealed class Endpoint
{
    public string? Port { get; set; }
    public string? Path { get; set; }
    public string? Interval { get; set; }
    public string? ScrapeTimeout { get; set; }
    public string? Scheme { get; set; }
    public TlsConfig? TlsConfig { get; set; }
    public SecretKeySelector? BearerTokenSecret { get; set; }
    public BasicAuth? BasicAuth { get; set; }
    public List<RelabelConfig>? RelabelConfigs { get; set; }
    public List<RelabelConfig>? MetricRelabelConfigs { get; set; }
}

internal sealed class TlsConfig
{
    public bool? InsecureSkipVerify { get; set; }
    public string? CaFile { get; set; }
    public string? CertFile { get; set; }
    public string? KeyFile { get; set; }
}

internal sealed class BasicAuth
{
    public SecretKeySelector? Username { get; set; }
    public SecretKeySelector? Password { get; set; }
}

internal sealed class RelabelConfig
{
    public List<string>? SourceLabels { get; set; }
    public string? Separator { get; set; }
    public string? TargetLabel { get; set; }
    public string? Regex { get; set; }
    public string? Replacement { get; set; }
    public string? Action { get; set; }
}

internal sealed class PodMonitor
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public PodMonitorSpec Spec { get; set; } = new();
}

internal sealed class PodMonitorSpec
{
    public LabelSelector? Selector { get; set; }
    public NamespaceSelector? NamespaceSelector { get; set; }
    public List<PodMetricsEndpoint>? PodMetricsEndpoints { get; set; }
    public string? JobLabel { get; set; }
    public List<string>? PodTargetLabels { get; set; }
}

internal sealed class PodMetricsEndpoint
{
    public string? Port { get; set; }
    public string? Path { get; set; }
    public string? Interval { get; set; }
    public string? ScrapeTimeout { get; set; }
    public string? Scheme { get; set; }
}

internal sealed class PrometheusRule
{
    public string ApiVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public ObjectMeta Metadata { get; set; } = new();
    public PrometheusRuleSpec Spec { get; set; } = new();
}

internal sealed class PrometheusRuleSpec
{
    public List<RuleGroup>? Groups { get; set; }
}

internal sealed class RuleGroup
{
    public string? Name { get; set; }
    public string? Interval { get; set; }
    public List<Rule>? Rules { get; set; }
}

internal sealed class Rule
{
    public string? Alert { get; set; }
    public string? Expr { get; set; }
    public string? For { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public Dictionary<string, string>? Annotations { get; set; }
}

#endregion
