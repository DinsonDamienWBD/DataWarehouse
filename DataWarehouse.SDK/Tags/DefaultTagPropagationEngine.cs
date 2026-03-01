using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Default implementation of <see cref="ITagPropagationEngine"/> that evaluates
/// propagation rules in priority order and carries tags through pipeline stages.
/// </summary>
/// <remarks>
/// <para>
/// Key behaviors:
/// <list type="bullet">
/// <item><description>Rules are evaluated in priority order (lower number = higher priority).</description></item>
/// <item><description>When no rule matches a tag, the default action is <see cref="PropagationAction.Copy"/> (safe default -- no silent tag loss).</description></item>
/// <item><description>When <see cref="TagPropagationRule.StopOnMatch"/> is true, no further rules evaluate for that tag.</description></item>
/// <item><description>Transform failures are captured in <see cref="TagPropagationResult.FailedTags"/>, not thrown.</description></item>
/// <item><description>If an <see cref="ITagAttachmentService"/> is provided, propagated tags are bulk-attached to the target object.</description></item>
/// <item><description>If an <see cref="ITagSchemaRegistry"/> is provided, transformed tag values are validated against their schema.</description></item>
/// </list>
/// </para>
/// <para>
/// Built-in rules are registered on construction for common patterns:
/// <list type="bullet">
/// <item><description>System tags (namespace "system") always copy.</description></item>
/// <item><description>Compliance tags (namespace "compliance") always copy (with immutability respected).</description></item>
/// <item><description>Temporary tags (namespace "temp") are dropped at the Store stage.</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Default tag propagation engine")]
public sealed class DefaultTagPropagationEngine : ITagPropagationEngine
{
    // Cat 15 (finding 661): use BoundedDictionary consistent with SDK pattern — caps unbounded growth at 10K rules
    private const int MaxRules = 10_000;
    private readonly ITagAttachmentService? _attachmentService;
    private readonly ITagSchemaRegistry? _schemaRegistry;
    private readonly BoundedDictionary<string, TagPropagationRule> _rulesById = new(MaxRules, StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new <see cref="DefaultTagPropagationEngine"/> with optional services
    /// for attaching propagated tags and validating against schemas.
    /// </summary>
    /// <param name="attachmentService">
    /// When provided, propagated tags are bulk-attached to the target object after evaluation.
    /// </param>
    /// <param name="schemaRegistry">
    /// When provided, transformed tag values are validated against their schema before propagation.
    /// </param>
    public DefaultTagPropagationEngine(
        ITagAttachmentService? attachmentService = null,
        ITagSchemaRegistry? schemaRegistry = null)
    {
        _attachmentService = attachmentService;
        _schemaRegistry = schemaRegistry;
        RegisterBuiltInRules();
    }

    /// <inheritdoc />
    public void AddRule(TagPropagationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        // BoundedDictionary is thread-safe — no external lock needed
        _rulesById[rule.RuleId] = rule;
    }

    /// <inheritdoc />
    public void RemoveRule(string ruleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        _rulesById.TryRemove(ruleId, out _);
    }

    /// <inheritdoc />
    public IReadOnlyList<TagPropagationRule> GetRules(PipelineStage? stage = null)
    {
        // BoundedDictionary enumerates under its own internal read lock; snapshot values first
        var values = _rulesById.Values.ToList();
        var rules = stage.HasValue
            ? values.Where(r => r.FromStage == stage.Value)
            : (IEnumerable<TagPropagationRule>)values;

        return rules
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<TagPropagationResult> PropagateAsync(
        TagPropagationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var propagated = new List<Tag>();
        var dropped = new List<(TagKey Key, string Reason)>();
        var failed = new List<(TagKey Key, string Error)>();

        // Snapshot rules — BoundedDictionary is thread-safe; snapshot before async work
        var rules = _rulesById.Values
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .ToList();

        foreach (var tag in context.SourceTags)
        {
            ct.ThrowIfCancellationRequested();

            var matchingRules = FindMatchingRules(rules, tag, context.CurrentStage);
            var handled = false;

            foreach (var rule in matchingRules)
            {
                var result = await ApplyRuleAsync(rule, tag, context, ct).ConfigureAwait(false);

                switch (result.Outcome)
                {
                    case RuleOutcome.Propagated:
                        propagated.Add(result.Tag!);
                        handled = true;
                        break;

                    case RuleOutcome.Dropped:
                        dropped.Add((tag.Key, result.Reason!));
                        handled = true;
                        break;

                    case RuleOutcome.Failed:
                        failed.Add((tag.Key, result.Error!));
                        handled = true;
                        break;
                }

                if (handled && rule.StopOnMatch)
                    break;

                // If the rule produced a result, stop processing this tag
                if (handled)
                    break;
            }

            // Default: Copy if no rules matched
            if (!handled)
            {
                propagated.Add(tag);
            }
        }

        // Bulk-attach propagated tags to target if service is available
        if (_attachmentService is not null && propagated.Count > 0)
        {
            try
            {
                var tagTuples = propagated.Select(t => (
                    t.Key,
                    t.Value,
                    new TagSourceInfo(TagSource.System, "propagation-engine", "Tag Propagation Engine")
                ));

                await _attachmentService.BulkAttachAsync(
                    context.TargetObjectKey,
                    tagTuples,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Attachment failure does not void the propagation result;
                // tags were evaluated successfully, attachment is a side effect
                foreach (var tag in propagated.ToList())
                {
                    failed.Add((tag.Key, $"Bulk attach failed: {ex.Message}"));
                }
                propagated.Clear();
            }
        }

        return new TagPropagationResult
        {
            PropagatedTags = propagated.AsReadOnly(),
            DroppedTags = dropped.AsReadOnly(),
            FailedTags = failed.AsReadOnly()
        };
    }

    private static List<TagPropagationRule> FindMatchingRules(
        List<TagPropagationRule> allRules,
        Tag tag,
        PipelineStage currentStage)
    {
        var matched = new List<TagPropagationRule>();

        foreach (var rule in allRules)
        {
            if (rule.FromStage != currentStage)
                continue;

            // Check tag key filter
            if (rule.TagKeyFilter is not null && !rule.TagKeyFilter.Equals(tag.Key))
                continue;

            // Check namespace filter (case-insensitive)
            if (rule.NamespaceFilter is not null &&
                !string.Equals(rule.NamespaceFilter, tag.Key.Namespace, StringComparison.OrdinalIgnoreCase))
                continue;

            matched.Add(rule);
        }

        return matched;
    }

    private async Task<RuleApplication> ApplyRuleAsync(
        TagPropagationRule rule,
        Tag tag,
        TagPropagationContext context,
        CancellationToken ct)
    {
        switch (rule.Action)
        {
            case PropagationAction.Copy:
                return RuleApplication.Propagate(tag);

            case PropagationAction.Drop:
                return RuleApplication.Drop(tag.Key, $"Dropped by rule '{rule.RuleId}'");

            case PropagationAction.Transform:
                return await ApplyTransformAsync(rule, tag, ct).ConfigureAwait(false);

            case PropagationAction.Merge:
                return await ApplyMergeAsync(rule, tag, context, ct).ConfigureAwait(false);

            case PropagationAction.InheritFromParent:
                return await ApplyInheritAsync(tag, context, ct).ConfigureAwait(false);

            default:
                return RuleApplication.Fail(tag.Key, $"Unknown action '{rule.Action}' in rule '{rule.RuleId}'");
        }
    }

    private async Task<RuleApplication> ApplyTransformAsync(
        TagPropagationRule rule,
        Tag tag,
        CancellationToken ct)
    {
        if (rule.TransformFunc is null)
            return RuleApplication.Fail(tag.Key, $"Rule '{rule.RuleId}' has Transform action but no TransformFunc");

        try
        {
            var transformed = rule.TransformFunc(tag);
            if (transformed is null)
                return RuleApplication.Fail(tag.Key, $"TransformFunc in rule '{rule.RuleId}' returned null");

            // Validate against schema if registry is available and tag has a schema
            if (_schemaRegistry is not null && transformed.SchemaId is not null)
            {
                var schema = await _schemaRegistry.GetAsync(transformed.SchemaId, ct).ConfigureAwait(false);
                if (schema is not null)
                {
                    var validationResult = TagSchemaValidator.Validate(transformed, schema);
                    if (!validationResult.IsValid)
                    {
                        return RuleApplication.Fail(tag.Key,
                            $"Transformed value fails schema validation: {string.Join("; ", validationResult.Errors)}");
                    }
                }
            }

            return RuleApplication.Propagate(transformed);
        }
        catch (Exception ex)
        {
            return RuleApplication.Fail(tag.Key, $"Transform failed in rule '{rule.RuleId}': {ex.Message}");
        }
    }

    private async Task<RuleApplication> ApplyMergeAsync(
        TagPropagationRule rule,
        Tag tag,
        TagPropagationContext context,
        CancellationToken ct)
    {
        // If attachment service is available, check if target already has this tag
        if (_attachmentService is not null)
        {
            try
            {
                var existingTag = await _attachmentService.GetAsync(
                    context.TargetObjectKey, tag.Key, ct).ConfigureAwait(false);

                if (existingTag is not null)
                {
                    // LWW: keep whichever tag was modified more recently
                    var winner = tag.ModifiedUtc >= existingTag.ModifiedUtc ? tag : existingTag;
                    return RuleApplication.Propagate(winner);
                }
            }
            catch (Exception ex)
            {
                return RuleApplication.Fail(tag.Key, $"Merge lookup failed: {ex.Message}");
            }
        }

        // No existing tag or no service -- just copy
        return RuleApplication.Propagate(tag);
    }

    private async Task<RuleApplication> ApplyInheritAsync(
        Tag tag,
        TagPropagationContext context,
        CancellationToken ct)
    {
        // Look up the same tag on the source (parent) object via attachment service
        if (_attachmentService is not null)
        {
            try
            {
                var parentTag = await _attachmentService.GetAsync(
                    context.SourceObjectKey, tag.Key, ct).ConfigureAwait(false);

                if (parentTag is not null)
                    return RuleApplication.Propagate(parentTag);

                return RuleApplication.Drop(tag.Key, "Parent tag not found for InheritFromParent");
            }
            catch (Exception ex)
            {
                return RuleApplication.Fail(tag.Key, $"InheritFromParent lookup failed: {ex.Message}");
            }
        }

        // No attachment service -- fall back to copy
        return RuleApplication.Propagate(tag);
    }

    private void RegisterBuiltInRules()
    {
        // System tags always copy across all stage transitions
        foreach (PipelineStage stage in Enum.GetValues<PipelineStage>())
        {
            if (stage == PipelineStage.Delete) continue;

            var nextStage = stage + 1;
            if (!Enum.IsDefined(nextStage)) continue;

            AddRule(new TagPropagationRule
            {
                RuleId = $"builtin:system-copy:{stage}->{nextStage}",
                NamespaceFilter = "system",
                FromStage = stage,
                ToStage = nextStage,
                Action = PropagationAction.Copy,
                Priority = 10,
                StopOnMatch = true
            });

            AddRule(new TagPropagationRule
            {
                RuleId = $"builtin:compliance-copy:{stage}->{nextStage}",
                NamespaceFilter = "compliance",
                FromStage = stage,
                ToStage = nextStage,
                Action = PropagationAction.Copy,
                Priority = 10,
                StopOnMatch = true
            });
        }

        // Temporary tags are dropped at the Store stage
        foreach (PipelineStage stage in Enum.GetValues<PipelineStage>())
        {
            if (stage != PipelineStage.Store) continue;

            var nextStage = stage + 1;
            if (!Enum.IsDefined(nextStage)) continue;

            AddRule(new TagPropagationRule
            {
                RuleId = $"builtin:temp-drop:{stage}->{nextStage}",
                NamespaceFilter = "temp",
                FromStage = stage,
                ToStage = nextStage,
                Action = PropagationAction.Drop,
                Priority = 5,
                StopOnMatch = true
            });
        }
    }

    /// <summary>
    /// Internal result of applying a single rule to a single tag.
    /// </summary>
    private readonly struct RuleApplication
    {
        public RuleOutcome Outcome { get; }
        public Tag? Tag { get; }
        public string? Reason { get; }
        public string? Error { get; }

        private RuleApplication(RuleOutcome outcome, Tag? tag, string? reason, string? error)
        {
            Outcome = outcome;
            Tag = tag;
            Reason = reason;
            Error = error;
        }

        public static RuleApplication Propagate(Tag tag) =>
            new(RuleOutcome.Propagated, tag, null, null);

        public static RuleApplication Drop(TagKey key, string reason) =>
            new(RuleOutcome.Dropped, null, reason, null);

        public static RuleApplication Fail(TagKey key, string error) =>
            new(RuleOutcome.Failed, null, null, error);
    }

    private enum RuleOutcome
    {
        Propagated,
        Dropped,
        Failed
    }
}
