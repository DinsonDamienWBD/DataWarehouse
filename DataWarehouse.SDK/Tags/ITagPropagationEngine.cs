using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Engine that evaluates <see cref="TagPropagationRule"/>s to carry tags
/// through pipeline stages. Manages a rule set and executes propagation
/// for a given <see cref="TagPropagationContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// The engine is the core of the tag lifecycle: when data moves from ingest
/// through processing, storage, and replication, this engine ensures that
/// tags follow. Without propagation, tags are dead metadata that exist only
/// at the point of attachment.
/// </para>
/// <para>
/// Rules are evaluated in priority order (lower = higher priority). If no rule
/// matches a tag, the default behavior is to copy (tags propagate by default).
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag propagation engine contract")]
public interface ITagPropagationEngine
{
    /// <summary>
    /// Propagates tags from a source object to a target object according to
    /// the registered rules and the current pipeline stage.
    /// </summary>
    /// <param name="context">The propagation context with source tags, target, and stage info.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result detailing propagated, dropped, and failed tags.</returns>
    Task<TagPropagationResult> PropagateAsync(TagPropagationContext context, CancellationToken ct = default);

    /// <summary>
    /// Adds a propagation rule to the engine. If a rule with the same <see cref="TagPropagationRule.RuleId"/>
    /// already exists, it is replaced.
    /// </summary>
    /// <param name="rule">The rule to add.</param>
    void AddRule(TagPropagationRule rule);

    /// <summary>
    /// Removes a propagation rule by its identifier.
    /// No-op if the rule does not exist.
    /// </summary>
    /// <param name="ruleId">The identifier of the rule to remove.</param>
    void RemoveRule(string ruleId);

    /// <summary>
    /// Gets all registered rules, optionally filtered to rules that apply
    /// at a specific pipeline stage (as <see cref="TagPropagationRule.FromStage"/>).
    /// </summary>
    /// <param name="stage">When specified, only returns rules with matching <see cref="TagPropagationRule.FromStage"/>.</param>
    /// <returns>The matching rules in priority order.</returns>
    IReadOnlyList<TagPropagationRule> GetRules(PipelineStage? stage = null);
}
