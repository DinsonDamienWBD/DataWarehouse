using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.SemanticSync;

/// <summary>
/// Interface for meaning-based conflict resolution. Implementations detect conflicts
/// between local and remote data versions using semantic analysis, classify the conflict type,
/// and resolve conflicts using AI-driven strategies.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public interface ISemanticConflictResolver
{
    /// <summary>
    /// Detects whether a semantic conflict exists between local and remote versions of a data item.
    /// Returns null if no conflict is detected (versions are compatible).
    /// </summary>
    /// <param name="dataId">Unique identifier of the data item.</param>
    /// <param name="localData">The local version of the data payload.</param>
    /// <param name="remoteData">The remote version of the data payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SemanticConflict"/> if a conflict exists, or null if versions are compatible.</returns>
    Task<SemanticConflict?> DetectConflictAsync(
        string dataId,
        ReadOnlyMemory<byte> localData,
        ReadOnlyMemory<byte> remoteData,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a semantic conflict using AI-driven meaning analysis and the appropriate
    /// resolution strategy.
    /// </summary>
    /// <param name="conflict">The semantic conflict to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ConflictResolutionResult"/> with the resolved data and explanation.</returns>
    Task<ConflictResolutionResult> ResolveAsync(
        SemanticConflict conflict,
        CancellationToken ct = default);

    /// <summary>
    /// Classifies the type of a semantic conflict without resolving it.
    /// Useful for reporting and policy decisions before committing to a resolution strategy.
    /// </summary>
    /// <param name="conflict">The semantic conflict to classify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="ConflictType"/> of the conflict.</returns>
    Task<ConflictType> ClassifyConflictAsync(
        SemanticConflict conflict,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the capabilities of this conflict resolver implementation.
    /// </summary>
    ConflictResolverCapabilities Capabilities { get; }
}

/// <summary>
/// Describes the capabilities of an <see cref="ISemanticConflictResolver"/> implementation.
/// </summary>
/// <param name="SupportsAutoMerge">Whether the resolver can automatically merge conflicting data.</param>
/// <param name="SupportedTypes">The conflict types this resolver can handle.</param>
/// <param name="RequiresAI">Whether the resolver requires an AI provider for resolution.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record ConflictResolverCapabilities(
    bool SupportsAutoMerge,
    ConflictType[] SupportedTypes,
    bool RequiresAI);
