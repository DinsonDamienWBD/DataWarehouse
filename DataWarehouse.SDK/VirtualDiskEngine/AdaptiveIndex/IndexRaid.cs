using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Index;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// RAID modes available for index composition.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-08 Index RAID")]
public enum IndexRaidMode
{
    /// <summary>N-way striping for parallel throughput (RAID-0).</summary>
    Stripe,

    /// <summary>M-copy mirroring for redundancy (RAID-1).</summary>
    Mirror,

    /// <summary>Stripe across N groups, each mirrored M times (RAID-10).</summary>
    StripeMirror,

    /// <summary>3-tier hot/warm/cold access with frequency-based promotion.</summary>
    Tiered,

    /// <summary>Stripe across N groups, each with tiered access.</summary>
    StripeTiered
}

/// <summary>
/// Serializable configuration specifying a RAID mode and its parameters.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-08 Index RAID")]
public sealed class IndexRaidConfig
{
    /// <summary>
    /// Gets or sets the RAID mode.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IndexRaidMode Mode { get; set; }

    /// <summary>
    /// Gets or sets the number of stripes (used by <see cref="IndexRaidMode.Stripe"/>,
    /// <see cref="IndexRaidMode.StripeMirror"/>, and <see cref="IndexRaidMode.StripeTiered"/>).
    /// </summary>
    public int StripeCount { get; set; } = 4;

    /// <summary>
    /// Gets or sets the number of mirror copies (used by <see cref="IndexRaidMode.Mirror"/>
    /// and <see cref="IndexRaidMode.StripeMirror"/>).
    /// </summary>
    public int MirrorCount { get; set; } = 2;

    /// <summary>
    /// Gets or sets the mirror write mode.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MirrorWriteMode WriteMode { get; set; } = MirrorWriteMode.Synchronous;

    /// <summary>
    /// Gets or sets the promotion threshold for tiered mode.
    /// </summary>
    public int PromotionThreshold { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum number of entries in the L1 hot tier.
    /// </summary>
    public long L1MaxEntries { get; set; } = 100_000;

    /// <summary>
    /// Gets or sets the maximum number of entries in the L2 warm tier.
    /// </summary>
    public long L2MaxEntries { get; set; } = 1_000_000;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Serializes this configuration to JSON.
    /// </summary>
    /// <returns>A JSON string representing this configuration.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, s_jsonOptions);

    /// <summary>
    /// Deserializes a configuration from JSON.
    /// </summary>
    /// <param name="json">The JSON string to parse.</param>
    /// <returns>A new <see cref="IndexRaidConfig"/> instance.</returns>
    /// <exception cref="JsonException">Thrown if the JSON is invalid.</exception>
    public static IndexRaidConfig FromJson(string json)
        => JsonSerializer.Deserialize<IndexRaidConfig>(json, s_jsonOptions)
           ?? throw new JsonException("Failed to deserialize IndexRaidConfig.");
}

/// <summary>
/// Factory that composes <see cref="IndexStriping"/>, <see cref="IndexMirroring"/>, and
/// <see cref="IndexTiering"/> into unified RAID-like configurations. All factory methods
/// return <see cref="IAdaptiveIndex"/>, hiding the internal composition from callers.
/// </summary>
/// <remarks>
/// <para>
/// Supported configurations:
/// <list type="bullet">
///   <item><description><b>Striped</b> (RAID-0): N parallel stripes for throughput.</description></item>
///   <item><description><b>Mirrored</b> (RAID-1): M redundant copies for fault tolerance.</description></item>
///   <item><description><b>StripeMirror</b> (RAID-10): N stripe groups, each M-mirrored.</description></item>
///   <item><description><b>Tiered</b>: 3-tier hot/warm/cold with count-min sketch promotion.</description></item>
///   <item><description><b>StripeTiered</b>: N stripe groups, each with tiered access.</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-08 Index RAID")]
public static class IndexRaid
{
    /// <summary>
    /// Creates an N-stripe index for parallel throughput.
    /// </summary>
    /// <param name="stripeCount">Number of stripes.</param>
    /// <param name="stripeFactory">Factory that creates an index per stripe ordinal.</param>
    /// <returns>A striped <see cref="IAdaptiveIndex"/>.</returns>
    public static IAdaptiveIndex CreateStriped(int stripeCount, Func<int, IAdaptiveIndex> stripeFactory)
        => new IndexStriping(stripeCount, stripeFactory);

    /// <summary>
    /// Creates an M-mirror index for redundancy.
    /// </summary>
    /// <param name="mirrorCount">Number of mirror copies.</param>
    /// <param name="mirrorFactory">Factory that creates an index per mirror ordinal.</param>
    /// <param name="writeMode">Synchronous or asynchronous write propagation.</param>
    /// <returns>A mirrored <see cref="IAdaptiveIndex"/>.</returns>
    public static IAdaptiveIndex CreateMirrored(
        int mirrorCount,
        Func<int, IAdaptiveIndex> mirrorFactory,
        MirrorWriteMode writeMode = MirrorWriteMode.Synchronous)
        => new IndexMirroring(mirrorCount, mirrorFactory, writeMode);

    /// <summary>
    /// Creates a RAID-10 configuration: N stripe groups, each mirrored M times.
    /// Provides both parallel throughput and redundancy.
    /// </summary>
    /// <param name="stripeCount">Number of stripe groups.</param>
    /// <param name="mirrorCount">Number of mirrors per stripe group.</param>
    /// <param name="indexFactory">Factory that creates an index for a given (stripe, mirror) pair.</param>
    /// <param name="writeMode">Mirror write mode.</param>
    /// <returns>A stripe-mirrored <see cref="IAdaptiveIndex"/>.</returns>
    public static IAdaptiveIndex CreateStripeMirror(
        int stripeCount,
        int mirrorCount,
        Func<int, int, IAdaptiveIndex> indexFactory,
        MirrorWriteMode writeMode = MirrorWriteMode.Synchronous)
    {
        ArgumentNullException.ThrowIfNull(indexFactory);

        // Outer layer: striping across N groups
        // Inner layer: each stripe group is M-mirrored
        return new IndexStriping(stripeCount, stripeOrdinal =>
            new IndexMirroring(mirrorCount,
                mirrorOrdinal => indexFactory(stripeOrdinal, mirrorOrdinal),
                writeMode));
    }

    /// <summary>
    /// Creates a 3-tier hot/warm/cold index with frequency-based promotion and demotion.
    /// </summary>
    /// <param name="l1Hot">The hot tier index.</param>
    /// <param name="l2Warm">The warm tier index.</param>
    /// <param name="l3Cold">The cold tier index.</param>
    /// <param name="promotionThreshold">Access count threshold for promotion (default 10).</param>
    /// <param name="l1MaxEntries">Maximum L1 entries before demotion (default 100K).</param>
    /// <param name="l2MaxEntries">Maximum L2 entries before demotion to L3 (default 1M).</param>
    /// <returns>A tiered <see cref="IAdaptiveIndex"/>.</returns>
    public static IAdaptiveIndex CreateTiered(
        IAdaptiveIndex l1Hot,
        IAdaptiveIndex l2Warm,
        IAdaptiveIndex l3Cold,
        int promotionThreshold = 10,
        long l1MaxEntries = 100_000,
        long l2MaxEntries = 1_000_000)
        => new IndexTiering(l1Hot, l2Warm, l3Cold, promotionThreshold, l1MaxEntries, l2MaxEntries);

    /// <summary>
    /// Creates a stripe-tiered configuration: N stripe groups, each with 3-tier hot/warm/cold access.
    /// </summary>
    /// <param name="stripeCount">Number of stripe groups.</param>
    /// <param name="tierFactory">Factory that creates a (l1, l2, l3) tier set for a given stripe ordinal.</param>
    /// <param name="promotionThreshold">Access count threshold for promotion (default 10).</param>
    /// <param name="l1MaxEntries">Maximum L1 entries per stripe before demotion (default 100K).</param>
    /// <param name="l2MaxEntries">Maximum L2 entries per stripe before demotion to L3 (default 1M).</param>
    /// <returns>A stripe-tiered <see cref="IAdaptiveIndex"/>.</returns>
    public static IAdaptiveIndex CreateStripeTiered(
        int stripeCount,
        Func<int, (IAdaptiveIndex L1, IAdaptiveIndex L2, IAdaptiveIndex L3)> tierFactory,
        int promotionThreshold = 10,
        long l1MaxEntries = 100_000,
        long l2MaxEntries = 1_000_000)
    {
        ArgumentNullException.ThrowIfNull(tierFactory);

        return new IndexStriping(stripeCount, stripeOrdinal =>
        {
            var (l1, l2, l3) = tierFactory(stripeOrdinal);
            return new IndexTiering(l1, l2, l3, promotionThreshold, l1MaxEntries, l2MaxEntries);
        });
    }

    /// <summary>
    /// Creates a RAID configuration from a serializable <see cref="IndexRaidConfig"/>.
    /// </summary>
    /// <param name="config">The RAID configuration.</param>
    /// <param name="indexFactory">Factory that creates a bare index for a given ordinal.</param>
    /// <returns>A composed <see cref="IAdaptiveIndex"/> matching the configuration.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the mode is not recognized.</exception>
    public static IAdaptiveIndex CreateFromConfig(
        IndexRaidConfig config,
        Func<int, IAdaptiveIndex> indexFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(indexFactory);

        return config.Mode switch
        {
            IndexRaidMode.Stripe => CreateStriped(config.StripeCount, indexFactory),
            IndexRaidMode.Mirror => CreateMirrored(config.MirrorCount, indexFactory, config.WriteMode),
            IndexRaidMode.StripeMirror => CreateStripeMirror(
                config.StripeCount, config.MirrorCount,
                (s, m) => indexFactory(s * config.MirrorCount + m),
                config.WriteMode),
            IndexRaidMode.Tiered => CreateTiered(
                indexFactory(0), indexFactory(1), indexFactory(2),
                config.PromotionThreshold, config.L1MaxEntries, config.L2MaxEntries),
            IndexRaidMode.StripeTiered => CreateStripeTiered(
                config.StripeCount,
                s => (indexFactory(s * 3), indexFactory(s * 3 + 1), indexFactory(s * 3 + 2)),
                config.PromotionThreshold, config.L1MaxEntries, config.L2MaxEntries),
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Mode, "Unrecognized RAID mode.")
        };
    }
}
