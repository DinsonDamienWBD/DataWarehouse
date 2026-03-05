using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.IO;

namespace DataWarehouse.Plugins.AedsCore.Extensions;

/// <summary>
/// Policy Engine Plugin: Client-side policy rule evaluation.
/// Implements IClientPolicyEngine with expression parser for dynamic rule evaluation.
/// </summary>
public sealed class PolicyEnginePlugin : SecurityPluginBase, IClientPolicyEngine
{
    private readonly ConcurrentBag<PolicyRule> _rules = new();

    /// <summary>
    /// Gets the plugin identifier.
    /// </summary>
    public override string Id => "aeds.policy-engine";

    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public override string Name => "PolicyEnginePlugin";

    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SecurityDomain => "PolicyEngine";

    /// <summary>
    /// Gets the plugin category.
    /// </summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Evaluates policy rules against manifest and context.
    /// </summary>
    /// <param name="manifest">Intent manifest to evaluate.</param>
    /// <param name="context">Policy evaluation context.</param>
    /// <returns>Policy decision result.</returns>
    public Task<PolicyDecision> EvaluateAsync(IntentManifest manifest, PolicyContext context)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));
        if (context == null)
            throw new ArgumentNullException(nameof(context));

        // Apply default policies first
        var defaultDecision = ApplyDefaultPolicies(manifest, context);
        if (!defaultDecision.Allowed)
            return Task.FromResult(defaultDecision);

        // Evaluate custom rules in order
        foreach (var rule in _rules)
        {
            if (EvaluateCondition(rule.Condition, manifest, context))
            {
                return Task.FromResult(new PolicyDecision(
                    rule.Action == PolicyAction.Allow,
                    rule.Reason ?? $"Rule '{rule.Name}' matched",
                    rule.Action
                ));
            }
        }

        // Default: allow
        return Task.FromResult(new PolicyDecision(true, "No matching rules, default allow", PolicyAction.Allow));
    }

    /// <summary>
    /// Loads policy rules from JSON file.
    /// </summary>
    /// <param name="policyPath">Path to policy file.</param>
    public async Task LoadPolicyAsync(string policyPath)
    {
        if (string.IsNullOrEmpty(policyPath))
            throw new ArgumentException("Policy path cannot be null or empty.", nameof(policyPath));

        if (!File.Exists(policyPath))
            throw new InvalidOperationException($"Policy file not found: {policyPath}");

        var json = await File.ReadAllTextAsync(policyPath).ConfigureAwait(false);
        var doc = System.Text.Json.JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("rules", out var rulesElement) &&
            rulesElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var ruleElement in rulesElement.EnumerateArray())
            {
                var name = ruleElement.GetProperty("name").GetString()
                    ?? throw new InvalidOperationException("Policy rule missing 'name' property.");
                var condition = ruleElement.GetProperty("condition").GetString()
                    ?? throw new InvalidOperationException($"Policy rule '{name}' missing 'condition' property.");
                var actionStr = ruleElement.GetProperty("action").GetString()
                    ?? throw new InvalidOperationException($"Policy rule '{name}' missing 'action' property.");

                if (!Enum.TryParse<PolicyAction>(actionStr, ignoreCase: true, out var action))
                    throw new InvalidOperationException($"Policy rule '{name}' has invalid action: '{actionStr}'.");

                var reason = ruleElement.TryGetProperty("reason", out var reasonProp)
                    ? reasonProp.GetString()
                    : null;

                AddRule(new PolicyRule(name, condition, action, reason));
            }
        }
        else
        {
            throw new InvalidOperationException("Policy file must contain a 'rules' array.");
        }
    }

    /// <summary>
    /// Adds a policy rule.
    /// </summary>
    /// <param name="rule">Rule to add.</param>
    public void AddRule(PolicyRule rule)
    {
        if (rule == null)
            throw new ArgumentNullException(nameof(rule));

        _rules.Add(rule);
    }

    private PolicyDecision ApplyDefaultPolicies(IntentManifest manifest, PolicyContext context)
    {
        // Policy 1: Execute requires Trusted or higher
        if (manifest.Action == ActionPrimitive.Execute && context.SourceTrustLevel < ClientTrustLevel.Trusted)
        {
            return new PolicyDecision(false, "Execute action requires Trusted trust level", PolicyAction.Deny);
        }

        // Policy 2: Block large downloads on metered networks
        if (context.NetworkType == NetworkType.Metered && context.FileSizeBytes > 100_000_000)
        {
            return new PolicyDecision(false, "Large download blocked on metered network", PolicyAction.Deny);
        }

        // Policy 3: Always allow critical priority
        if (context.Priority >= 90)
        {
            return new PolicyDecision(true, "Critical priority always allowed", PolicyAction.Allow);
        }

        return new PolicyDecision(true, "Default policies passed", PolicyAction.Allow);
    }

    /// <summary>
    /// Evaluates a simple boolean condition expression against manifest and policy context.
    /// Supports: comparisons (&gt;, &lt;, &gt;=, &lt;=, ==, !=), AND/OR, variable substitution.
    /// Variables: Priority, FileSizeBytes, NetworkType, SourceTrustLevel, IsPeer, Action.
    /// Falls back to false (deny) on parse errors (fail-closed, finding 976).
    /// </summary>
    private bool EvaluateCondition(string condition, IntentManifest manifest, PolicyContext context)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return false;

        try
        {
            // Substitute named variables with their values before parsing.
            condition = condition
                .Replace("Priority", context.Priority.ToString())
                .Replace("FileSizeBytes", context.FileSizeBytes.ToString())
                .Replace("NetworkType", $"\"{context.NetworkType}\"")
                .Replace("SourceTrustLevel", $"\"{context.SourceTrustLevel}\"")
                .Replace("IsPeer", context.IsPeer.ToString().ToLowerInvariant())
                .Replace("Action", $"\"{manifest.Action}\"");

            // Split on AND/OR and evaluate each clause.
            return EvaluateOr(condition.Trim());
        }
        catch
        {
            return false; // Fail-closed: invalid expression denies the rule.
        }
    }

    private static bool EvaluateOr(string expr)
    {
        // Split on OR first (lower precedence).
        var parts = expr.Split([" OR "], StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => EvaluateAnd(p.Trim()));
    }

    private static bool EvaluateAnd(string expr)
    {
        var parts = expr.Split([" AND "], StringSplitOptions.RemoveEmptyEntries);
        return parts.All(p => EvaluatePrimary(p.Trim()));
    }

    private static bool EvaluatePrimary(string expr)
    {
        // Handle explicit true/false literals.
        if (string.Equals(expr, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(expr, "false", StringComparison.OrdinalIgnoreCase)) return false;

        // Operators in descending length order to avoid ">=" being matched as ">".
        var operators = new[] { ">=", "<=", "!=", ">", "<", "==" };
        foreach (var op in operators)
        {
            var idx = expr.IndexOf(op, StringComparison.Ordinal);
            if (idx < 0) continue;

            var lhs = expr[..idx].Trim().Trim('"');
            var rhs = expr[(idx + op.Length)..].Trim().Trim('"');

            // Numeric comparison if both sides parse as numbers.
            if (double.TryParse(lhs, out var lNum) && double.TryParse(rhs, out var rNum))
            {
                return op switch
                {
                    ">=" => lNum >= rNum,
                    "<=" => lNum <= rNum,
                    "!=" => lNum != rNum,
                    ">" => lNum > rNum,
                    "<" => lNum < rNum,
                    "==" => lNum == rNum,
                    _ => false
                };
            }

            // String comparison.
            return op switch
            {
                "==" => string.Equals(lhs, rhs, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(lhs, rhs, StringComparison.OrdinalIgnoreCase),
                _ => false // Ordered comparisons not supported for non-numeric values.
            };
        }

        // No operator found — not a recognisable expression; fail-closed.
        return false;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        // Load default policies
        AddRule(new PolicyRule(
            "BlockExecuteUntrusted",
            "Action == Execute AND SourceTrustLevel < Trusted",
            PolicyAction.Deny,
            "Execute requires trusted source"
        ));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task StopAsync()
    {
        return Task.CompletedTask;
    }
}
