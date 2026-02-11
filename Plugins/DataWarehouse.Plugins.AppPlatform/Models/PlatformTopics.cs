namespace DataWarehouse.Plugins.AppPlatform.Models;

/// <summary>
/// Message bus topic constants for all Application Platform operations.
/// All topics use the "platform" prefix to namespace platform messages
/// and avoid collisions with other plugin topics.
/// </summary>
internal static class PlatformTopics
{
    /// <summary>
    /// Common prefix for all platform message bus topics.
    /// </summary>
    public const string Prefix = "platform";

    // ========================================
    // App Lifecycle Topics
    // ========================================

    /// <summary>
    /// Topic for registering a new application on the platform.
    /// </summary>
    public const string AppRegister = $"{Prefix}.register";

    /// <summary>
    /// Topic for deregistering (deleting) an application from the platform.
    /// </summary>
    public const string AppDeregister = $"{Prefix}.deregister";

    /// <summary>
    /// Topic for updating an existing application registration.
    /// </summary>
    public const string AppUpdate = $"{Prefix}.update";

    /// <summary>
    /// Topic for retrieving a single application registration by ID.
    /// </summary>
    public const string AppGet = $"{Prefix}.get";

    /// <summary>
    /// Topic for listing all active application registrations.
    /// </summary>
    public const string AppList = $"{Prefix}.list";

    // ========================================
    // Token Management Topics
    // ========================================

    /// <summary>
    /// Topic for creating a new service token for an application.
    /// </summary>
    public const string TokenCreate = $"{Prefix}.token.create";

    /// <summary>
    /// Topic for rotating an existing service token (revoke old, create new).
    /// </summary>
    public const string TokenRotate = $"{Prefix}.token.rotate";

    /// <summary>
    /// Topic for revoking a service token.
    /// </summary>
    public const string TokenRevoke = $"{Prefix}.token.revoke";

    /// <summary>
    /// Topic for validating a raw service token key.
    /// </summary>
    public const string TokenValidate = $"{Prefix}.token.validate";

    // ========================================
    // Service Routing Topics
    // ========================================

    /// <summary>
    /// Topic for routing requests to the storage service.
    /// </summary>
    public const string ServiceStorage = $"{Prefix}.service.storage";

    /// <summary>
    /// Topic for routing requests to the access control service.
    /// </summary>
    public const string ServiceAccessControl = $"{Prefix}.service.accesscontrol";

    /// <summary>
    /// Topic for routing requests to the intelligence (AI) service.
    /// </summary>
    public const string ServiceIntelligence = $"{Prefix}.service.intelligence";

    /// <summary>
    /// Topic for routing requests to the observability service.
    /// </summary>
    public const string ServiceObservability = $"{Prefix}.service.observability";

    /// <summary>
    /// Topic for routing requests to the replication service.
    /// </summary>
    public const string ServiceReplication = $"{Prefix}.service.replication";

    /// <summary>
    /// Topic for routing requests to the compliance service.
    /// </summary>
    public const string ServiceCompliance = $"{Prefix}.service.compliance";

    // ========================================
    // Policy Management Topics
    // ========================================

    /// <summary>
    /// Topic for binding a per-app access control policy.
    /// </summary>
    public const string PolicyBind = $"{Prefix}.policy.bind";

    /// <summary>
    /// Topic for unbinding a per-app access control policy.
    /// </summary>
    public const string PolicyUnbind = $"{Prefix}.policy.unbind";

    /// <summary>
    /// Topic for retrieving a per-app access control policy.
    /// </summary>
    public const string PolicyGet = $"{Prefix}.policy.get";

    /// <summary>
    /// Topic for evaluating an access control decision against a per-app policy.
    /// </summary>
    public const string PolicyEvaluate = $"{Prefix}.policy.evaluate";

    // ========================================
    // AI Workflow Topics
    // ========================================

    /// <summary>
    /// Topic for configuring an application's AI workflow mode, budget limits,
    /// model preferences, and operation restrictions.
    /// </summary>
    public const string AiWorkflowConfigure = $"{Prefix}.ai.configure";

    /// <summary>
    /// Topic for removing an application's AI workflow configuration.
    /// </summary>
    public const string AiWorkflowRemove = $"{Prefix}.ai.remove";

    /// <summary>
    /// Topic for retrieving an application's current AI workflow configuration.
    /// </summary>
    public const string AiWorkflowGet = $"{Prefix}.ai.get";

    /// <summary>
    /// Topic for updating an application's AI workflow configuration.
    /// </summary>
    public const string AiWorkflowUpdate = $"{Prefix}.ai.update";

    /// <summary>
    /// Topic for submitting an app-scoped AI request that is routed through
    /// budget, concurrency, and operation enforcement before forwarding to Intelligence.
    /// </summary>
    public const string AiRequest = $"{Prefix}.ai.request";

    /// <summary>
    /// Topic for retrieving an application's current AI usage tracking data
    /// including monthly spend, request count, and active concurrent requests.
    /// </summary>
    public const string AiUsageGet = $"{Prefix}.ai.usage";

    /// <summary>
    /// Topic for resetting an application's monthly AI usage counters
    /// (total spend and request count).
    /// </summary>
    public const string AiUsageReset = $"{Prefix}.ai.usage.reset";

    // ========================================
    // Observability Topics
    // ========================================

    /// <summary>
    /// Topic for configuring per-app observability settings (metrics, traces, logs,
    /// retention, log level, and alerting thresholds).
    /// </summary>
    public const string ObservabilityConfigure = $"{Prefix}.observability.configure";

    /// <summary>
    /// Topic for removing per-app observability configuration.
    /// </summary>
    public const string ObservabilityRemove = $"{Prefix}.observability.remove";

    /// <summary>
    /// Topic for retrieving per-app observability configuration.
    /// </summary>
    public const string ObservabilityGet = $"{Prefix}.observability.get";

    /// <summary>
    /// Topic for updating per-app observability configuration.
    /// </summary>
    public const string ObservabilityUpdate = $"{Prefix}.observability.update";

    /// <summary>
    /// Topic for emitting a metric with per-app isolation via app_id tag injection.
    /// </summary>
    public const string ObservabilityEmitMetric = $"{Prefix}.observability.emit.metric";

    /// <summary>
    /// Topic for emitting a trace span with per-app isolation via app_id attribute injection.
    /// </summary>
    public const string ObservabilityEmitTrace = $"{Prefix}.observability.emit.trace";

    /// <summary>
    /// Topic for emitting a log entry with per-app isolation via app_id property injection.
    /// </summary>
    public const string ObservabilityEmitLog = $"{Prefix}.observability.emit.log";

    /// <summary>
    /// Topic for querying metrics with mandatory app_id filter for per-app isolation.
    /// </summary>
    public const string ObservabilityQueryMetrics = $"{Prefix}.observability.query.metrics";

    /// <summary>
    /// Topic for querying traces with mandatory app_id filter for per-app isolation.
    /// </summary>
    public const string ObservabilityQueryTraces = $"{Prefix}.observability.query.traces";

    /// <summary>
    /// Topic for querying logs with mandatory app_id filter for per-app isolation.
    /// </summary>
    public const string ObservabilityQueryLogs = $"{Prefix}.observability.query.logs";

    // ========================================
    // Service Discovery Topics
    // ========================================

    /// <summary>
    /// Topic for listing all available platform services and their access requirements.
    /// Returns a <c>PlatformServiceCatalog</c> with all service endpoints.
    /// </summary>
    public const string ServicesList = $"{Prefix}.services.list";

    /// <summary>
    /// Topic for retrieving a specific service endpoint by name.
    /// </summary>
    public const string ServicesGet = $"{Prefix}.services.get";

    /// <summary>
    /// Topic for retrieving the subset of services available to a specific app
    /// based on its token scopes.
    /// </summary>
    public const string ServicesForApp = $"{Prefix}.services.forapp";

    /// <summary>
    /// Topic for checking the health status of a specific platform service.
    /// </summary>
    public const string ServicesHealth = $"{Prefix}.services.health";
}
