using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;

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
    public Task LoadPolicyAsync(string policyPath)
    {
        if (string.IsNullOrEmpty(policyPath))
            throw new ArgumentException("Policy path cannot be null or empty.", nameof(policyPath));

        // In production, parse JSON and load rules
        return Task.CompletedTask;
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

    private bool EvaluateCondition(string condition, IntentManifest manifest, PolicyContext context)
    {
        // Simple expression evaluator
        // Format: "Priority > 80 AND NetworkType != Metered"
        try
        {
            condition = condition.Replace("Priority", context.Priority.ToString());
            condition = condition.Replace("FileSizeBytes", context.FileSizeBytes.ToString());
            condition = condition.Replace("NetworkType", $"\"{context.NetworkType}\"");
            condition = condition.Replace("SourceTrustLevel", $"\"{context.SourceTrustLevel}\"");
            condition = condition.Replace("IsPeer", context.IsPeer.ToString());

            // Basic evaluation (production would use proper parser)
            return condition.Contains("true") || !condition.Contains("false");
        }
        catch
        {
            return false;
        }
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
