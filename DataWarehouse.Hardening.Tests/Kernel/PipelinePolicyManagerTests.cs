using DataWarehouse.Kernel.Pipeline;
using DataWarehouse.SDK.Contracts.Pipeline;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for PipelinePolicyManager — findings 130-132.
/// </summary>
public class PipelinePolicyManagerTests
{
    // Finding 130: [CRITICAL] ValidateGroupAdminPrivileges allows access when securityContext==null
    // Finding 131: CRITICAL security inconsistency — ValidateAdminPrivileges denies on null
    // FIX APPLIED: ValidateGroupAdminPrivileges now denies access when securityContext is null
    // (fail-closed, consistent with ValidateAdminPrivileges)
    [Fact]
    public async Task Finding130_131_GroupAdmin_NullContext_Denied()
    {
        var configProvider = new InMemoryPipelineConfigProvider();
        var manager = new PipelinePolicyManager(configProvider);

        var policy = new PipelinePolicy
        {
            PolicyId = "test-group-policy",
            Name = "Test Group Policy",
            Level = PolicyLevel.UserGroup,
            ScopeId = "group-1",
            Stages = new List<PipelineStagePolicy>(),
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test-user"
        };

        // With null securityContext, should throw (fail-closed)
        await Assert.ThrowsAsync<SecurityOperationException>(
            () => manager.SetGroupPolicyAsync("group-1", policy, "test-user", securityContext: null));
    }

    // Finding 132: Warnings collection in PolicyValidationResult never queried
    [Fact]
    public void Finding132_Warnings_Collection_Exists()
    {
        var result = new PolicyValidationResult();
        Assert.NotNull(result.Warnings);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// Simple in-memory config provider for testing.
    /// </summary>
    private sealed class InMemoryPipelineConfigProvider : IPipelineConfigProvider
    {
        private readonly Dictionary<string, PipelinePolicy> _policies = new();

        public Task<PipelinePolicy?> GetPolicyAsync(PolicyLevel level, string scopeId, CancellationToken ct = default)
        {
            var key = $"{level}:{scopeId}";
            _policies.TryGetValue(key, out var policy);
            return Task.FromResult(policy);
        }

        public Task SetPolicyAsync(PipelinePolicy policy, CancellationToken ct = default)
        {
            var key = $"{policy.Level}:{policy.ScopeId}";
            _policies[key] = policy;
            return Task.CompletedTask;
        }

        public Task<bool> DeletePolicyAsync(PolicyLevel level, string scopeId, CancellationToken ct = default)
        {
            var key = $"{level}:{scopeId}";
            return Task.FromResult(_policies.Remove(key));
        }

        public Task<EffectivePolicyVisualization> VisualizeEffectivePolicyAsync(
            string? userId = null, string? groupId = null, string? operationId = null,
            CancellationToken ct = default)
        {
            return Task.FromResult(new EffectivePolicyVisualization());
        }

        public Task<PipelinePolicy> ResolveEffectivePolicyAsync(
            string? userId = null, string? groupId = null, string? operationId = null,
            CancellationToken ct = default)
        {
            return Task.FromResult(new PipelinePolicy
            {
                PolicyId = "default",
                Name = "Default",
                Level = PolicyLevel.Instance,
                ScopeId = "default",
                Version = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "system"
            });
        }

        public Task<IReadOnlyList<PipelinePolicy>> ListPoliciesAsync(PolicyLevel level, CancellationToken ct = default)
        {
            var result = _policies
                .Where(kvp => kvp.Key.StartsWith($"{level}:"))
                .Select(kvp => kvp.Value)
                .ToList();
            return Task.FromResult<IReadOnlyList<PipelinePolicy>>(result);
        }
    }
}
