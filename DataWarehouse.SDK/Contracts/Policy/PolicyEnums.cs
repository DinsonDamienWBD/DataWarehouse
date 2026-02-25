using System.ComponentModel;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Defines the granularity level at which a policy applies within the VDE hierarchy.
    /// Policies can target individual blocks up to entire VDE instances.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-03)")]
    public enum PolicyLevel
    {
        /// <summary>Block-level policy: applies to individual storage blocks (finest granularity).</summary>
        [Description("Block-level policy: applies to individual storage blocks (finest granularity)")]
        Block = 0,

        /// <summary>Chunk-level policy: applies to logical chunks composed of multiple blocks.</summary>
        [Description("Chunk-level policy: applies to logical chunks composed of multiple blocks")]
        Chunk = 1,

        /// <summary>Object-level policy: applies to complete stored objects (files, blobs, records).</summary>
        [Description("Object-level policy: applies to complete stored objects (files, blobs, records)")]
        Object = 2,

        /// <summary>Container-level policy: applies to logical containers grouping multiple objects.</summary>
        [Description("Container-level policy: applies to logical containers grouping multiple objects")]
        Container = 3,

        /// <summary>VDE-level policy: applies to the entire Virtual Data Environment instance (broadest scope).</summary>
        [Description("VDE-level policy: applies to the entire Virtual Data Environment instance (broadest scope)")]
        VDE = 4
    }

    /// <summary>
    /// Determines how policies cascade through the VDE hierarchy from parent to child levels.
    /// Controls conflict resolution when multiple policy levels define overlapping rules.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-04)")]
    public enum CascadeStrategy
    {
        /// <summary>
        /// Picks the tightest (most restrictive) policy in the chain.
        /// When policies conflict, the one with the lowest permission or highest security wins.
        /// </summary>
        [Description("Picks the tightest policy in the chain; lowest permission or highest security wins")]
        MostRestrictive = 0,

        /// <summary>
        /// Higher-level policy always wins over lower-level Override policies.
        /// Enforced policies cannot be overridden by any descendant in the hierarchy.
        /// </summary>
        [Description("Higher-level policy always wins; cannot be overridden by any descendant")]
        Enforce = 1,

        /// <summary>
        /// Copies the parent policy unchanged to the child level.
        /// The child inherits all settings without modification.
        /// </summary>
        [Description("Copies the parent policy unchanged to the child level")]
        Inherit = 2,

        /// <summary>
        /// Replaces the parent policy entirely at this level.
        /// The child's policy takes full precedence, discarding inherited values.
        /// </summary>
        [Description("Replaces the parent policy entirely at this level")]
        Override = 3,

        /// <summary>
        /// Combines parent and child policies on a per-key basis.
        /// Non-conflicting keys are merged; conflicting keys use the child's value.
        /// </summary>
        [Description("Combines parent and child policies per-key; conflicting keys use child value")]
        Merge = 4
    }

    /// <summary>
    /// Defines the level of autonomy granted to AI subsystems for a given feature.
    /// Controls whether AI can act independently, must explain, or requires human approval.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-05)")]
    public enum AiAutonomyLevel
    {
        /// <summary>AI is fully disabled for this feature; all actions require manual human intervention.</summary>
        [Description("AI disabled; all actions require manual human intervention")]
        ManualOnly = 0,

        /// <summary>AI may suggest actions but cannot execute them; human must approve and trigger.</summary>
        [Description("AI suggests actions but cannot execute; human must approve and trigger")]
        Suggest = 1,

        /// <summary>AI suggests actions with detailed explanations of reasoning; human must approve.</summary>
        [Description("AI suggests with detailed explanations; human must approve")]
        SuggestExplain = 2,

        /// <summary>AI executes actions automatically but notifies human operators of each action taken.</summary>
        [Description("AI executes automatically but notifies operators of each action")]
        AutoNotify = 3,

        /// <summary>AI executes actions silently without notification; only audit logs record activity.</summary>
        [Description("AI executes silently; only audit logs record activity")]
        AutoSilent = 4
    }

    /// <summary>
    /// Enumerates high-impact actions that may require quorum approval before execution.
    /// These are security-critical operations that benefit from multi-party authorization.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-08)")]
    public enum QuorumAction
    {
        /// <summary>Override an AI decision or recommendation that was previously applied.</summary>
        [Description("Override an AI decision or recommendation")]
        OverrideAi = 0,

        /// <summary>Change security policy settings that affect data protection or access control.</summary>
        [Description("Change security policy affecting data protection or access control")]
        ChangeSecurityPolicy = 1,

        /// <summary>Completely disable AI subsystems for one or more features.</summary>
        [Description("Completely disable AI subsystems")]
        DisableAi = 2,

        /// <summary>Modify quorum membership, thresholds, or approval requirements.</summary>
        [Description("Modify quorum membership, thresholds, or approval requirements")]
        ModifyQuorum = 3,

        /// <summary>Permanently delete an entire Virtual Data Environment and all its contents.</summary>
        [Description("Permanently delete an entire VDE and all contents")]
        DeleteVde = 4,

        /// <summary>Export encryption keys or security credentials outside the system boundary.</summary>
        [Description("Export encryption keys or credentials outside the system boundary")]
        ExportKeys = 5,

        /// <summary>Disable the audit logging subsystem, removing accountability trails.</summary>
        [Description("Disable audit logging, removing accountability trails")]
        DisableAudit = 6
    }

    /// <summary>
    /// Named presets for operational profiles that define pre-configured policy bundles.
    /// Each preset balances performance, security, and AI autonomy differently.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 68: Policy Engine foundation (SDKF-06)")]
    public enum OperationalProfilePreset
    {
        /// <summary>Optimized for maximum throughput and minimum latency; relaxed security and high AI autonomy.</summary>
        [Description("Maximum throughput, minimum latency; relaxed security, high AI autonomy")]
        Speed = 0,

        /// <summary>Balanced trade-off between performance and security; moderate AI autonomy.</summary>
        [Description("Balanced performance and security; moderate AI autonomy")]
        Balanced = 1,

        /// <summary>Standard operational profile suitable for most production workloads.</summary>
        [Description("Standard profile suitable for most production workloads")]
        Standard = 2,

        /// <summary>Elevated security with reduced AI autonomy; suitable for regulated environments.</summary>
        [Description("Elevated security, reduced AI autonomy; suitable for regulated environments")]
        Strict = 3,

        /// <summary>Maximum security, AI fully manual, all critical actions require quorum; for highest-security environments.</summary>
        [Description("Maximum security, AI manual, quorum required; highest-security environments")]
        Paranoid = 4,

        /// <summary>User-defined custom profile with manually specified feature policies.</summary>
        [Description("User-defined custom profile with manually specified feature policies")]
        Custom = 5
    }
}
