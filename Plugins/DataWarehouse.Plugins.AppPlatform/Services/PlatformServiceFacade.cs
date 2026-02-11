using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.AppPlatform.Models;

namespace DataWarehouse.Plugins.AppPlatform.Services;

/// <summary>
/// Unified service discovery and consumption facade for the DataWarehouse platform.
/// Provides a catalog of all consumable services, their message bus topics, required
/// scopes, and supported operations so registered applications can discover and
/// access platform capabilities.
/// </summary>
/// <remarks>
/// <para>
/// The facade exposes four core operations:
/// <list type="bullet">
///   <item><see cref="GetServiceCatalogAsync"/>: Returns the full service catalog with all 6 services</item>
///   <item><see cref="GetServiceEndpointAsync"/>: Looks up a specific service by name</item>
///   <item><see cref="GetServicesForAppAsync"/>: Filters services by token scopes for a specific app</item>
///   <item><see cref="CheckServiceHealthAsync"/>: Pings a service to check its health status</item>
/// </list>
/// </para>
/// <para>
/// The service catalog is built statically from the known platform services (Storage,
/// AccessControl, Intelligence, Observability, Replication, Compliance) and includes
/// the platform version for compatibility checking.
/// </para>
/// </remarks>
internal sealed class PlatformServiceFacade
{
    /// <summary>
    /// Lazily-initialized static service catalog containing all platform service endpoints.
    /// </summary>
    private static readonly Lazy<ServiceEndpoint[]> _serviceCatalog = new(BuildServiceCatalog);

    /// <summary>
    /// Message bus for health-check pings to downstream services.
    /// </summary>
    private readonly IMessageBus _messageBus;

    /// <summary>
    /// Plugin identifier used as the source in outgoing health-check messages.
    /// </summary>
    private readonly string _pluginId;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlatformServiceFacade"/> class.
    /// </summary>
    /// <param name="messageBus">The message bus for inter-plugin communication.</param>
    /// <param name="pluginId">The plugin identifier used as message source.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="messageBus"/> or <paramref name="pluginId"/> is <c>null</c>.
    /// </exception>
    public PlatformServiceFacade(IMessageBus messageBus, string pluginId)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
    }

    /// <summary>
    /// Returns the complete platform service catalog containing all available services,
    /// the platform version, and the generation timestamp.
    /// </summary>
    /// <returns>A <see cref="PlatformServiceCatalog"/> listing all 6 platform services.</returns>
    public Task<PlatformServiceCatalog> GetServiceCatalogAsync()
    {
        var catalog = new PlatformServiceCatalog
        {
            Services = _serviceCatalog.Value,
            PlatformVersion = "1.0.0",
            GeneratedAt = DateTime.UtcNow
        };

        return Task.FromResult(catalog);
    }

    /// <summary>
    /// Looks up a specific service endpoint by its machine-friendly name.
    /// </summary>
    /// <param name="serviceName">The service name to look up (e.g., "storage", "intelligence").</param>
    /// <returns>The <see cref="ServiceEndpoint"/> if found; <c>null</c> otherwise.</returns>
    public Task<ServiceEndpoint?> GetServiceEndpointAsync(string serviceName)
    {
        var endpoint = _serviceCatalog.Value
            .FirstOrDefault(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(endpoint);
    }

    /// <summary>
    /// Returns the subset of platform services that the specified application's token scopes
    /// grant access to. A service is included if at least one of its required scopes is
    /// present in the provided token scopes.
    /// </summary>
    /// <param name="appId">The application identifier (for logging/auditing context).</param>
    /// <param name="tokenScopes">The scopes granted by the application's service token.</param>
    /// <returns>An array of <see cref="ServiceEndpoint"/> objects the app can access.</returns>
    public Task<ServiceEndpoint[]> GetServicesForAppAsync(string appId, string[] tokenScopes)
    {
        var accessible = _serviceCatalog.Value
            .Where(s => s.RequiredScopes.Any(required =>
                tokenScopes.Contains(required, StringComparer.OrdinalIgnoreCase)))
            .ToArray();

        return Task.FromResult(accessible);
    }

    /// <summary>
    /// Checks the health of a specific service by sending a health-check ping via the
    /// message bus. Returns <see cref="ServiceStatus.Available"/> if the service responds
    /// successfully, or <see cref="ServiceStatus.Unavailable"/> if it times out or returns an error.
    /// </summary>
    /// <param name="serviceName">The service name to check (e.g., "storage", "intelligence").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="ServiceStatus"/> of the specified service.</returns>
    public async Task<ServiceStatus> CheckServiceHealthAsync(string serviceName, CancellationToken ct = default)
    {
        var endpoint = _serviceCatalog.Value
            .FirstOrDefault(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));

        if (endpoint is null)
            return ServiceStatus.Unavailable;

        try
        {
            var response = await _messageBus.SendAsync(endpoint.RequestTopic, new PluginMessage
            {
                Type = $"{endpoint.RequestTopic}.health",
                SourcePluginId = _pluginId,
                Payload = new Dictionary<string, object>
                {
                    ["Operation"] = "health_check",
                    ["ServiceName"] = serviceName
                }
            }, TimeSpan.FromSeconds(5), ct);

            return response.Success ? ServiceStatus.Available : ServiceStatus.Degraded;
        }
        catch (OperationCanceledException)
        {
            return ServiceStatus.Unavailable;
        }
        catch
        {
            return ServiceStatus.Unavailable;
        }
    }

    /// <summary>
    /// Builds the canonical list of platform service endpoints. This static method defines
    /// all 6 services (Storage, AccessControl, Intelligence, Observability, Replication,
    /// Compliance) with their topics, scopes, and supported operations.
    /// </summary>
    /// <returns>An array of all platform <see cref="ServiceEndpoint"/> descriptors.</returns>
    private static ServiceEndpoint[] BuildServiceCatalog()
    {
        return
        [
            new ServiceEndpoint
            {
                ServiceName = "storage",
                DisplayName = "Storage Service",
                Description = "Persistent storage with provider selection, tiering, and quotas",
                RequestTopic = PlatformTopics.ServiceStorage,
                RequiredScopes = ["storage"],
                SupportedOperations = ["read", "write", "delete", "list", "metadata"]
            },
            new ServiceEndpoint
            {
                ServiceName = "accesscontrol",
                DisplayName = "Access Control Service",
                Description = "RBAC/ABAC/MAC/DAC policy evaluation and management",
                RequestTopic = PlatformTopics.ServiceAccessControl,
                RequiredScopes = ["accesscontrol"],
                SupportedOperations = ["evaluate", "grant", "revoke", "audit"]
            },
            new ServiceEndpoint
            {
                ServiceName = "intelligence",
                DisplayName = "AI Intelligence Service",
                Description = "AI-powered analysis, embeddings, chat, and knowledge operations",
                RequestTopic = PlatformTopics.ServiceIntelligence,
                RequiredScopes = ["intelligence"],
                SupportedOperations = ["chat", "embeddings", "analysis", "knowledge"],
                RequiresAiWorkflow = true
            },
            new ServiceEndpoint
            {
                ServiceName = "observability",
                DisplayName = "Observability Service",
                Description = "Metrics, tracing, logging, and alerting with per-app isolation",
                RequestTopic = PlatformTopics.ServiceObservability,
                RequiredScopes = ["observability"],
                SupportedOperations = ["emit_metric", "emit_trace", "emit_log", "query_metrics", "query_traces", "query_logs"]
            },
            new ServiceEndpoint
            {
                ServiceName = "replication",
                DisplayName = "Replication Service",
                Description = "Geo-dispersed data replication with WORM and sharding",
                RequestTopic = PlatformTopics.ServiceReplication,
                RequiredScopes = ["replication"],
                SupportedOperations = ["replicate", "status", "configure", "failover"]
            },
            new ServiceEndpoint
            {
                ServiceName = "compliance",
                DisplayName = "Compliance Service",
                Description = "Regulatory compliance evaluation (GDPR, HIPAA, SOC2, FedRAMP) and reporting",
                RequestTopic = PlatformTopics.ServiceCompliance,
                RequiredScopes = ["compliance"],
                SupportedOperations = ["evaluate", "report", "audit", "certify"]
            }
        ];
    }
}
