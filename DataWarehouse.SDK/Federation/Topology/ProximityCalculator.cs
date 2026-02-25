using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Federation.Topology;

/// <summary>
/// Provides proximity scoring and distance calculation for location-aware routing.
/// </summary>
/// <remarks>
/// <para>
/// ProximityCalculator implements the scoring logic for node selection in location-aware routing.
/// It combines topology hierarchy (same-rack, same-datacenter, etc.) with node health scores and
/// routing policy preferences to produce a normalized proximity score (0.0 to 1.0).
/// </para>
/// <para>
/// <strong>Scoring Algorithm:</strong>
/// </para>
/// <list type="number">
///   <item><description>Compute base score from topology level (SameNode=1.0, SameRack=0.9, ..., CrossRegion=0.2)</description></item>
///   <item><description>Apply health penalty: base_score *= health_score</description></item>
///   <item><description>Apply policy-specific adjustments (load factor for ThroughputOptimized, etc.)</description></item>
/// </list>
/// <para>
/// Higher scores indicate better candidates for routing. Routers select the node with the highest score.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Proximity calculator for location-aware routing (FOS-04)")]
internal static class ProximityCalculator
{
    /// <summary>
    /// Calculates a proximity score between two nodes based on topology and routing policy.
    /// </summary>
    /// <param name="source">The source node (typically the local/requesting node).</param>
    /// <param name="target">The target node (candidate for routing).</param>
    /// <param name="policy">The routing policy to apply.</param>
    /// <returns>
    /// A normalized proximity score from 0.0 (poor candidate) to 1.0 (best candidate).
    /// Higher scores indicate better routing targets.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Base Scores by Topology Level:</strong>
    /// </para>
    /// <list type="bullet">
    ///   <item><description>SameNode: 1.0 (routing to self, uncommon but valid)</description></item>
    ///   <item><description>SameRack: 0.9 (minimal network hops, lowest latency)</description></item>
    ///   <item><description>SameDatacenter: 0.7 (intra-DC latency, still fast)</description></item>
    ///   <item><description>SameRegion: 0.5 (inter-DC latency within region)</description></item>
    ///   <item><description>CrossRegion: 0.2 (cross-region latency, highest cost)</description></item>
    /// </list>
    /// <para>
    /// <strong>Policy Adjustments:</strong>
    /// </para>
    /// <list type="bullet">
    ///   <item><description>LatencyOptimized: no adjustment (base topology score only)</description></item>
    ///   <item><description>ThroughputOptimized: multiply by load factor (FreeBytes / TotalBytes)</description></item>
    ///   <item><description>CostOptimized: not yet implemented (future: prefer on-prem or cheap regions)</description></item>
    ///   <item><description>BalancedAuto: combine topology and load factor</description></item>
    /// </list>
    /// </remarks>
    public static double CalculateProximityScore(NodeTopology source, NodeTopology target, RoutingPolicy policy)
    {
        var level = target.GetLevelRelativeTo(source);
        var baseScore = level switch
        {
            TopologyLevel.SameNode => 1.0,
            TopologyLevel.SameRack => 0.9,
            TopologyLevel.SameDatacenter => 0.7,
            TopologyLevel.SameRegion => 0.5,
            TopologyLevel.CrossRegion => 0.2,
            _ => 0.1
        };

        // Apply health penalty (unhealthy nodes get lower scores)
        baseScore *= target.HealthScore;

        // Policy-specific adjustments
        switch (policy)
        {
            case RoutingPolicy.ThroughputOptimized:
                // Prefer least-loaded nodes (higher free space = higher score)
                var loadFactor = target.TotalBytes > 0
                    ? target.FreeBytes / (double)target.TotalBytes
                    : 0.5; // Default to 0.5 if total bytes is zero
                baseScore *= loadFactor;
                break;

            case RoutingPolicy.BalancedAuto:
                // Balance topology and load: average of base score and load factor
                var balancedLoadFactor = target.TotalBytes > 0
                    ? target.FreeBytes / (double)target.TotalBytes
                    : 0.5;
                baseScore = (baseScore + balancedLoadFactor) / 2.0;
                break;

            case RoutingPolicy.CostOptimized:
                // Future: apply cost-based scoring
                // For now, use base topology score
                break;

            case RoutingPolicy.LatencyOptimized:
            default:
                // No adjustment -- pure topology-based scoring
                break;
        }

        return baseScore;
    }

    /// <summary>
    /// Calculates the great-circle distance between two geographic coordinates using the Haversine formula.
    /// </summary>
    /// <param name="lat1">Latitude of the first point (degrees).</param>
    /// <param name="lon1">Longitude of the first point (degrees).</param>
    /// <param name="lat2">Latitude of the second point (degrees).</param>
    /// <param name="lon2">Longitude of the second point (degrees).</param>
    /// <returns>
    /// The distance between the two points in kilometers.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The Haversine formula calculates the shortest distance over the earth's surface (great-circle distance),
    /// accounting for the spherical shape of the Earth. This is used when topology metadata (rack/datacenter/region)
    /// is unavailable or insufficient, and only lat/long coordinates are known.
    /// </para>
    /// <para>
    /// <strong>Earth Radius:</strong> 6371 km (mean radius).
    /// </para>
    /// <para>
    /// <strong>Limitations:</strong> Assumes a perfect sphere. For precision-critical applications,
    /// consider using geodesic calculations that account for Earth's ellipsoid shape.
    /// </para>
    /// </remarks>
    public static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusKm = 6371;

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusKm * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
