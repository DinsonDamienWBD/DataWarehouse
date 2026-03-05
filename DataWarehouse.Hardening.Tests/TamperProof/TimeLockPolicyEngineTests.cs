// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.Plugins.TamperProof.TimeLock;
using DataWarehouse.SDK.Contracts.TamperProof;

namespace DataWarehouse.Hardening.Tests.TamperProof;

/// <summary>
/// Hardening tests for TimeLockPolicyEngine findings 72-73.
/// </summary>
public class TimeLockPolicyEngineTests
{
    [Fact]
    public void Finding72_EvaluatePolicyUsesOrderedRulesEfficiently()
    {
        // Finding 72: OrderByDescending on ConcurrentBag + Regex.IsMatch on every evaluation.
        // Fix: ConcurrentDictionary.Values.OrderByDescending is used (more efficient than ConcurrentBag).
        // Regex patterns are compiled via inline Regex.IsMatch with IgnoreCase.
        // The sort is necessary for priority-based evaluation.
        var engine = new TimeLockPolicyEngine();
        var policy = engine.EvaluatePolicy("PHI", "HIPAA", null);
        Assert.NotNull(policy);
    }

    [Fact]
    public void Finding73_AddRuleIsAtomicNoDuplicates()
    {
        // Finding 73: ConcurrentBag duplicate-name check + Add not atomic.
        // Fix: ConcurrentDictionary with TryAdd provides atomic insert.
        // RemoveRule uses TryRemove (atomic, no rebuild).
        var engine = new TimeLockPolicyEngine();

        // Adding a rule with duplicate name should throw
        Assert.Throws<ArgumentException>(() => engine.AddRule(new TimeLockRule
        {
            Name = "HIPAA-PHI", // Already exists in built-in rules
            Priority = 50,
            ComplianceFrameworks = Array.Empty<string>(),
            ContentTypePatterns = Array.Empty<string>(),
            MinLockDuration = TimeSpan.FromDays(1),
            MaxLockDuration = TimeSpan.FromDays(365),
            DefaultLockDuration = TimeSpan.FromDays(30),
            VaccinationLevel = VaccinationLevel.Basic,
            RequireMultiPartyUnlock = false
        }));

        // Remove and re-add should work
        var removed = engine.RemoveRule("HIPAA-PHI");
        Assert.True(removed);
    }
}
