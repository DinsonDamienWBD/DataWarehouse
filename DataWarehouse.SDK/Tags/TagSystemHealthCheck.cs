using System.Diagnostics;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Validates that all tag subsystems are operational by performing lightweight
/// read/write probes against each component. Returns a <see cref="TagHealthReport"/>
/// summarizing the health of every subsystem.
/// </summary>
/// <remarks>
/// <para>
/// The health check exercises six subsystems:
/// <list type="bullet">
/// <item><description><b>Schema Registry</b> - registers and retrieves a test schema.</description></item>
/// <item><description><b>Tag Store</b> - writes and reads a test tag.</description></item>
/// <item><description><b>Tag Index</b> - indexes and queries a test tag.</description></item>
/// <item><description><b>Policy Engine</b> - evaluates a test object against policies.</description></item>
/// <item><description><b>Propagation Engine</b> - verifies built-in rules are loaded.</description></item>
/// <item><description><b>Query API</b> - executes a simple count query.</description></item>
/// </list>
/// </para>
/// <para>
/// Each check catches its own exceptions and reports failure in the result rather
/// than propagating. This ensures one unhealthy subsystem does not prevent the
/// others from being checked.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag service registration")]
public sealed class TagSystemHealthCheck
{
    private const string HealthCheckNamespace = "__healthcheck__";
    private const string HealthCheckObjectKey = "__healthcheck__/probe";

    private readonly TagServiceCollection _services;

    /// <summary>
    /// Initializes a new <see cref="TagSystemHealthCheck"/> that validates
    /// all subsystems in the given service collection.
    /// </summary>
    /// <param name="services">The tag service collection to validate.</param>
    public TagSystemHealthCheck(TagServiceCollection services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Runs all subsystem health checks and returns a consolidated report.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="TagHealthReport"/> summarizing the health of every subsystem.</returns>
    public async Task<TagHealthReport> CheckAsync(CancellationToken ct = default)
    {
        var checks = new List<TagHealthCheckItem>
        {
            await CheckSchemaRegistryAsync(ct).ConfigureAwait(false),
            await CheckTagStoreAsync(ct).ConfigureAwait(false),
            await CheckTagIndexAsync(ct).ConfigureAwait(false),
            await CheckPolicyEngineAsync(ct).ConfigureAwait(false),
            CheckPropagationEngine(),
            await CheckQueryApiAsync(ct).ConfigureAwait(false)
        };

        return new TagHealthReport(
            IsHealthy: checks.All(c => c.IsHealthy),
            Checks: checks,
            CheckedAt: DateTimeOffset.UtcNow
        );
    }

    /// <summary>
    /// Checks the schema registry by registering and retrieving a test schema.
    /// </summary>
    private async Task<TagHealthCheckItem> CheckSchemaRegistryAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var testSchemaId = $"__healthcheck__{Guid.NewGuid():N}";
            var testTagKey = new TagKey(HealthCheckNamespace, "schema-probe");
            var testSchema = new TagSchema
            {
                SchemaId = testSchemaId,
                TagKey = testTagKey,
                DisplayName = "Health Check Schema",
                Description = "Temporary schema for health check probe",
                Versions = new[]
                {
                    new TagSchemaVersion
                    {
                        Version = "1.0.0",
                        RequiredKind = TagValueKind.String,
                        ChangeDescription = "Health check probe schema"
                    }
                }
            };

            await _services.SchemaRegistry.RegisterAsync(testSchema, ct).ConfigureAwait(false);
            var retrieved = await _services.SchemaRegistry.GetAsync(testSchemaId, ct).ConfigureAwait(false);

            sw.Stop();

            if (retrieved is null)
            {
                return new TagHealthCheckItem("SchemaRegistry", false,
                    "Schema registered but retrieval returned null", sw.Elapsed);
            }

            return new TagHealthCheckItem("SchemaRegistry", true,
                "Register and retrieve OK", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TagHealthCheckItem("SchemaRegistry", false,
                $"Error: {ex.Message}", sw.Elapsed);
        }
    }

    /// <summary>
    /// Checks the tag store by writing and reading a test tag.
    /// </summary>
    private async Task<TagHealthCheckItem> CheckTagStoreAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var testKey = new TagKey(HealthCheckNamespace, $"probe-{Guid.NewGuid():N}");
            var testTag = new Tag
            {
                Key = testKey,
                Value = TagValue.String("healthcheck"),
                Source = new TagSourceInfo(TagSource.System, "healthcheck", "TagSystemHealthCheck"),
                Version = 1,
                CreatedUtc = DateTimeOffset.UtcNow,
                ModifiedUtc = DateTimeOffset.UtcNow
            };

            await _services.TagStore.SetTagAsync(HealthCheckObjectKey, testTag, ct).ConfigureAwait(false);
            var retrieved = await _services.TagStore.GetTagAsync(HealthCheckObjectKey, testKey, ct).ConfigureAwait(false);

            // Clean up
            await _services.TagStore.RemoveTagAsync(HealthCheckObjectKey, testKey, ct).ConfigureAwait(false);

            sw.Stop();

            if (retrieved is null)
            {
                return new TagHealthCheckItem("TagStore", false,
                    "Tag stored but retrieval returned null", sw.Elapsed);
            }

            return new TagHealthCheckItem("TagStore", true,
                "Write and read OK", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TagHealthCheckItem("TagStore", false,
                $"Error: {ex.Message}", sw.Elapsed);
        }
    }

    /// <summary>
    /// Checks the tag index by indexing a test tag and querying for it.
    /// </summary>
    private async Task<TagHealthCheckItem> CheckTagIndexAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var testKey = new TagKey(HealthCheckNamespace, $"index-probe-{Guid.NewGuid():N}");
            var testTag = new Tag
            {
                Key = testKey,
                Value = TagValue.String("index-healthcheck"),
                Source = new TagSourceInfo(TagSource.System, "healthcheck", "TagSystemHealthCheck"),
                Version = 1,
                CreatedUtc = DateTimeOffset.UtcNow,
                ModifiedUtc = DateTimeOffset.UtcNow
            };

            await _services.TagIndex.IndexAsync(HealthCheckObjectKey, testTag, ct).ConfigureAwait(false);
            var count = await _services.TagIndex.CountByTagAsync(testKey, ct).ConfigureAwait(false);

            // Clean up
            await _services.TagIndex.RemoveAsync(HealthCheckObjectKey, testKey, ct).ConfigureAwait(false);

            sw.Stop();

            if (count < 1)
            {
                return new TagHealthCheckItem("TagIndex", false,
                    $"Tag indexed but count returned {count}", sw.Elapsed);
            }

            return new TagHealthCheckItem("TagIndex", true,
                "Index and count OK", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TagHealthCheckItem("TagIndex", false,
                $"Error: {ex.Message}", sw.Elapsed);
        }
    }

    /// <summary>
    /// Checks the policy engine by evaluating an empty tag collection against policies.
    /// </summary>
    private async Task<TagHealthCheckItem> CheckPolicyEngineAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Evaluate with empty tags -- the built-in tag-count-limit policy should pass
            var result = await _services.PolicyEngine.EvaluateAsync(
                HealthCheckObjectKey, TagCollection.Empty, ct).ConfigureAwait(false);

            sw.Stop();

            return new TagHealthCheckItem("PolicyEngine", true,
                $"Evaluation OK ({result.PoliciesEvaluated} policies evaluated)", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TagHealthCheckItem("PolicyEngine", false,
                $"Error: {ex.Message}", sw.Elapsed);
        }
    }

    /// <summary>
    /// Checks the propagation engine by verifying built-in rules are loaded.
    /// This is a synchronous check since rule inspection does not require I/O.
    /// </summary>
    private TagHealthCheckItem CheckPropagationEngine()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var rules = _services.PropagationEngine.GetRules();
            sw.Stop();

            if (rules.Count == 0)
            {
                return new TagHealthCheckItem("PropagationEngine", false,
                    "No propagation rules loaded (expected built-in rules)", sw.Elapsed);
            }

            return new TagHealthCheckItem("PropagationEngine", true,
                $"{rules.Count} rules loaded (including built-ins)", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TagHealthCheckItem("PropagationEngine", false,
                $"Error: {ex.Message}", sw.Elapsed);
        }
    }

    /// <summary>
    /// Checks the query API by executing a simple count query.
    /// </summary>
    private async Task<TagHealthCheckItem> CheckQueryApiAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Count all objects with a tag in a non-existent namespace -- should return 0 quickly
            var testKey = new TagKey(HealthCheckNamespace, "nonexistent");
            var expression = new TagFilterExpression(new TagFilter
            {
                TagKey = testKey,
                Operator = TagFilterOperator.Exists
            });

            var count = await _services.QueryApi.CountAsync(expression, ct).ConfigureAwait(false);
            sw.Stop();

            return new TagHealthCheckItem("QueryApi", true,
                $"Count query OK (returned {count})", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TagHealthCheckItem("QueryApi", false,
                $"Error: {ex.Message}", sw.Elapsed);
        }
    }
}

/// <summary>
/// Consolidated health report for the entire tag subsystem.
/// </summary>
/// <param name="IsHealthy">True if all subsystem checks passed.</param>
/// <param name="Checks">Individual check results for each subsystem.</param>
/// <param name="CheckedAt">UTC timestamp of when the health check was performed.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag health report")]
public record TagHealthReport(
    bool IsHealthy,
    IReadOnlyList<TagHealthCheckItem> Checks,
    DateTimeOffset CheckedAt);

/// <summary>
/// Result of a single subsystem health check.
/// </summary>
/// <param name="ComponentName">The name of the subsystem that was checked.</param>
/// <param name="IsHealthy">True if the subsystem passed its health check.</param>
/// <param name="Message">Optional descriptive message (success detail or error info).</param>
/// <param name="Latency">Time taken to complete the check.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag health check item")]
public record TagHealthCheckItem(
    string ComponentName,
    bool IsHealthy,
    string? Message = null,
    TimeSpan? Latency = null);
