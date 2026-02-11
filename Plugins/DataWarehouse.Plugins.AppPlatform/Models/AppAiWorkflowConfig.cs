namespace DataWarehouse.Plugins.AppPlatform.Models;

/// <summary>
/// Defines the AI workflow modes that control how UltimateIntelligence processes
/// requests from a registered application. Each mode represents a different
/// cost/control trade-off for AI operations.
/// </summary>
public enum AiWorkflowMode
{
    /// <summary>
    /// Intelligence chooses the optimal provider and model automatically based on
    /// request characteristics, availability, and cost efficiency.
    /// </summary>
    Auto,

    /// <summary>
    /// The application specifies the provider and model per request. Intelligence
    /// routes directly to the designated provider without selection logic.
    /// </summary>
    Manual,

    /// <summary>
    /// Intelligence optimizes for cost within the configured budget constraints,
    /// preferring cheaper providers and models that meet quality thresholds.
    /// </summary>
    Budget,

    /// <summary>
    /// Requests whose estimated cost exceeds the per-request budget limit require
    /// human approval before execution. Requests below the threshold proceed automatically.
    /// </summary>
    Approval
}

/// <summary>
/// Per-application AI workflow configuration that controls how UltimateIntelligence
/// handles requests from a registered application. Each app independently configures
/// its workflow mode, budget limits, model preferences, operation restrictions,
/// and concurrency caps.
/// </summary>
/// <remarks>
/// <para>
/// Configuration is sent to UltimateIntelligence via the <c>intelligence.workflow.configure</c>
/// message bus topic when bound, and removed via <c>intelligence.workflow.remove</c> when unbound.
/// </para>
/// <para>
/// Budget enforcement prevents requests from exceeding monthly or per-request cost limits.
/// Concurrency enforcement prevents request storms by capping active concurrent requests.
/// Operation restrictions filter to allowed AI operation types only.
/// </para>
/// </remarks>
public sealed record AppAiWorkflowConfig
{
    /// <summary>
    /// Identifier of the application this AI workflow configuration belongs to.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// The AI workflow mode controlling how Intelligence handles requests for this app.
    /// Defaults to <see cref="AiWorkflowMode.Auto"/>.
    /// </summary>
    public AiWorkflowMode Mode { get; init; } = AiWorkflowMode.Auto;

    /// <summary>
    /// Maximum total AI spend allowed per calendar month in USD. <c>null</c> means unlimited.
    /// When the monthly spend reaches this limit, further requests are rejected.
    /// </summary>
    public decimal? BudgetLimitPerMonth { get; init; }

    /// <summary>
    /// Maximum cost allowed per individual AI request in USD. <c>null</c> means unlimited.
    /// Requests with estimated costs exceeding this limit are rejected or flagged for approval.
    /// </summary>
    public decimal? BudgetLimitPerRequest { get; init; }

    /// <summary>
    /// Preferred AI provider identifier (e.g., "openai", "claude", "ollama").
    /// <c>null</c> allows Intelligence to auto-select the optimal provider.
    /// Only used in <see cref="AiWorkflowMode.Manual"/> mode.
    /// </summary>
    public string? PreferredProvider { get; init; }

    /// <summary>
    /// Preferred AI model identifier (e.g., "gpt-4", "claude-3").
    /// <c>null</c> allows Intelligence to auto-select the optimal model.
    /// Only used in <see cref="AiWorkflowMode.Manual"/> mode.
    /// </summary>
    public string? PreferredModel { get; init; }

    /// <summary>
    /// Whether requests with estimated costs above the per-request budget limit
    /// require human approval before execution. Defaults to <c>false</c>.
    /// </summary>
    public bool RequireApproval { get; init; }

    /// <summary>
    /// Maximum number of concurrent AI requests allowed for this application.
    /// Prevents request storms and ensures fair resource sharing. Defaults to 10.
    /// </summary>
    public int MaxConcurrentRequests { get; init; } = 10;

    /// <summary>
    /// AI operation types that this application is allowed to perform.
    /// Requests for operations not in this list are rejected.
    /// Defaults to chat, embeddings, and analysis.
    /// </summary>
    public string[] AllowedOperations { get; init; } = ["chat", "embeddings", "analysis"];

    /// <summary>
    /// UTC timestamp when this configuration was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp of the most recent update, or <c>null</c> if never updated.
    /// </summary>
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>
/// Tracks per-application AI usage metrics for budget enforcement and reporting.
/// Maintains monthly spend totals, request counts, and active concurrency tracking.
/// </summary>
/// <remarks>
/// Usage is tracked in-memory and reset at the start of each calendar month.
/// The <see cref="ActiveConcurrentRequests"/> field is managed via
/// <see cref="System.Threading.Interlocked"/> operations for thread safety.
/// </remarks>
public sealed record AiUsageTracking
{
    /// <summary>
    /// Identifier of the application whose AI usage is being tracked.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// Total amount spent on AI requests during the current billing period in USD.
    /// </summary>
    public decimal TotalSpentThisMonth { get; init; }

    /// <summary>
    /// Total number of AI requests processed during the current billing period.
    /// </summary>
    public int RequestCountThisMonth { get; init; }

    /// <summary>
    /// Number of AI requests currently being processed concurrently for this application.
    /// Managed via <see cref="System.Threading.Interlocked"/> for thread safety.
    /// </summary>
    public int ActiveConcurrentRequests { get; init; }

    /// <summary>
    /// UTC timestamp marking the start of the current billing period.
    /// </summary>
    public DateTime PeriodStart { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent AI request, or <c>null</c> if no requests have been made.
    /// </summary>
    public DateTime? LastRequestTime { get; init; }
}
