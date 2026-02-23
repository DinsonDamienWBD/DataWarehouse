using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Index;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Adaptive index contract that extends <see cref="IBTreeIndex"/> with morph-level awareness.
/// Implementations automatically transition between data structures as the dataset grows or shrinks.
/// </summary>
/// <remarks>
/// <para>
/// The adaptive index provides the same API surface as <see cref="IBTreeIndex"/> while adding
/// level-awareness capabilities. The engine selects the optimal data structure based on the
/// current object count and workload characteristics.
/// </para>
/// <para>
/// Morph transitions are atomic: all entries are migrated from the old structure to the new one
/// before any queries are served from the new level. Transitions are WAL-journaled for crash safety.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-01 Adaptive Index Engine")]
public interface IAdaptiveIndex : IBTreeIndex
{
    /// <summary>
    /// Gets the current morph level of this index.
    /// </summary>
    MorphLevel CurrentLevel { get; }

    /// <summary>
    /// Gets the current number of objects stored in this index.
    /// </summary>
    long ObjectCount { get; }

    /// <summary>
    /// Explicitly morphs the index to the specified target level, migrating all entries.
    /// </summary>
    /// <param name="targetLevel">The level to morph to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotSupportedException">Thrown if the target level is not yet implemented.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the morph cannot be completed.</exception>
    Task MorphToAsync(MorphLevel targetLevel, CancellationToken ct = default);

    /// <summary>
    /// Returns the recommended morph level based on the current object count and workload.
    /// This is advisory only; no transition occurs.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recommended morph level.</returns>
    Task<MorphLevel> RecommendLevelAsync(CancellationToken ct = default);

    /// <summary>
    /// Raised when the index transitions between morph levels.
    /// The first parameter is the old level, the second is the new level.
    /// </summary>
    event Action<MorphLevel, MorphLevel>? LevelChanged;
}
