using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Configurable admin policy that constrains morph behavior.
/// Provides guardrails including min/max level, forced level, backward morph prevention,
/// cooldown between morphs, and per-level disabling.
/// </summary>
/// <remarks>
/// <para>
/// The policy is checked by the index morph advisor before any morph recommendation
/// is executed. Admin operators can use policies to prevent oscillation, restrict morph ranges,
/// or force a specific level for testing/debugging.
/// </para>
/// <para>
/// Serializable via System.Text.Json for persistence in VDE metadata.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-06 IndexMorphPolicy")]
public sealed class IndexMorphPolicy
{
    /// <summary>
    /// Minimum allowed morph level. The advisor will never recommend a level below this.
    /// Null means no minimum constraint.
    /// </summary>
    [JsonPropertyName("minLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MorphLevel? MinLevel { get; set; }

    /// <summary>
    /// Maximum allowed morph level. The advisor will never recommend a level above this.
    /// Null means no maximum constraint.
    /// </summary>
    [JsonPropertyName("maxLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MorphLevel? MaxLevel { get; set; }

    /// <summary>
    /// Forces the index to always use this specific level, overriding all advisor logic.
    /// Null means no forced level (advisor decides).
    /// </summary>
    [JsonPropertyName("forcedLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MorphLevel? ForcedLevel { get; set; }

    /// <summary>
    /// Whether backward (demotion) morphs are allowed. Default: true.
    /// Set to false to prevent the index from ever downgrading to a simpler structure.
    /// </summary>
    [JsonPropertyName("allowBackwardMorph")]
    public bool AllowBackwardMorph { get; set; } = true;

    /// <summary>
    /// Minimum time between consecutive morph operations. Prevents oscillation.
    /// Default: 5 minutes.
    /// </summary>
    [JsonPropertyName("morphCooldownSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double MorphCooldownSeconds
    {
        get => MorphCooldown.TotalSeconds;
        set => MorphCooldown = TimeSpan.FromSeconds(value);
    }

    /// <summary>
    /// Minimum time between consecutive morph operations. Prevents oscillation.
    /// Default: 5 minutes.
    /// </summary>
    [JsonIgnore]
    public TimeSpan MorphCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Levels that are explicitly disabled. The advisor will skip these levels.
    /// </summary>
    [JsonPropertyName("disabledLevels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<MorphLevel, bool>? DisabledLevels { get; set; }

    /// <summary>
    /// Checks whether a morph transition from <paramref name="from"/> to <paramref name="to"/> is allowed.
    /// </summary>
    /// <param name="from">The current morph level.</param>
    /// <param name="to">The proposed target morph level.</param>
    /// <returns>True if the transition is allowed; false otherwise.</returns>
    public bool IsAllowed(MorphLevel from, MorphLevel to)
    {
        // Forced level: only the forced level is allowed as target
        if (ForcedLevel.HasValue)
        {
            return to == ForcedLevel.Value;
        }

        // Backward morph check
        if (!AllowBackwardMorph && to < from)
        {
            return false;
        }

        // Min level constraint
        if (MinLevel.HasValue && to < MinLevel.Value)
        {
            return false;
        }

        // Max level constraint
        if (MaxLevel.HasValue && to > MaxLevel.Value)
        {
            return false;
        }

        // Disabled level check
        if (DisabledLevels is not null && DisabledLevels.TryGetValue(to, out bool disabled) && disabled)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the forced level override if set, otherwise null.
    /// </summary>
    /// <returns>The forced morph level, or null if no override is active.</returns>
    public MorphLevel? GetOverride() => ForcedLevel;

    /// <summary>
    /// Default policy: no constraints, 5-minute cooldown.
    /// </summary>
    public static IndexMorphPolicy Default => new();

    /// <summary>
    /// Conservative policy: max level BeTree, no backward morph, 15-minute cooldown.
    /// </summary>
    public static IndexMorphPolicy Conservative => new()
    {
        MaxLevel = MorphLevel.BeTree,
        AllowBackwardMorph = false,
        MorphCooldown = TimeSpan.FromMinutes(15)
    };

    /// <summary>
    /// Performance policy: no constraints, 1-minute cooldown, backward morph allowed.
    /// </summary>
    public static IndexMorphPolicy Performance => new()
    {
        AllowBackwardMorph = true,
        MorphCooldown = TimeSpan.FromMinutes(1)
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Serializes this policy to a JSON string.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, _jsonOptions);

    /// <summary>
    /// Deserializes a policy from a JSON string.
    /// </summary>
    /// <param name="json">The JSON string.</param>
    /// <returns>The deserialized <see cref="IndexMorphPolicy"/>.</returns>
    public static IndexMorphPolicy FromJson(string json) =>
        JsonSerializer.Deserialize<IndexMorphPolicy>(json, _jsonOptions) ?? new IndexMorphPolicy();
}
