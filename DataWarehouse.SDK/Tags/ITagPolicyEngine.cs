using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Engine that evaluates and manages tag policies.
/// Policies enforce mandatory tag rules (e.g., "all PII objects MUST have data-classification tag").
/// Used by compliance passports and sovereignty mesh for enforceable governance.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag policy engine interface")]
public interface ITagPolicyEngine
{
    /// <summary>
    /// Evaluates all applicable policies against the given object's tags.
    /// Returns a full result with all violations (including Info/Warning).
    /// </summary>
    /// <param name="objectKey">The storage key of the object being evaluated.</param>
    /// <param name="tags">The object's current tag collection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The evaluation result containing compliance status and any violations.</returns>
    Task<PolicyEvaluationResult> EvaluateAsync(string objectKey, TagCollection tags, CancellationToken ct = default);

    /// <summary>
    /// Registers a new tag policy. Replaces any existing policy with the same ID.
    /// </summary>
    /// <param name="policy">The policy to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddPolicyAsync(TagPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Removes a policy by its ID. No-op if the policy does not exist.
    /// </summary>
    /// <param name="policyId">The ID of the policy to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemovePolicyAsync(string policyId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a policy by its ID, or null if not found.
    /// </summary>
    /// <param name="policyId">The ID of the policy to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The policy if found; otherwise null.</returns>
    Task<TagPolicy?> GetPolicyAsync(string policyId, CancellationToken ct = default);

    /// <summary>
    /// Lists all registered policies, optionally filtered by scope.
    /// </summary>
    /// <param name="scope">If specified, only returns policies with this scope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of matching policies.</returns>
    IAsyncEnumerable<TagPolicy> ListPoliciesAsync(PolicyScope? scope = null, CancellationToken ct = default);

    /// <summary>
    /// Fast-path compliance check. Returns true only if no Error or Critical violations exist.
    /// Use this for pre-write validation where full violation details are not needed.
    /// </summary>
    /// <param name="objectKey">The storage key of the object being checked.</param>
    /// <param name="tags">The object's current tag collection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the object is compliant (no Error/Critical violations).</returns>
    Task<bool> IsCompliantAsync(string objectKey, TagCollection tags, CancellationToken ct = default);
}
