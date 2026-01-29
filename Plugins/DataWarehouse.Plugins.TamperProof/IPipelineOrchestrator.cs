// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

namespace DataWarehouse.Plugins.TamperProof;

/// <summary>
/// Interface for pipeline orchestration.
/// Manages user-defined transformation pipelines (compression, encryption, etc.).
/// </summary>
public interface IPipelineOrchestrator
{
    /// <summary>
    /// Apply configured pipeline transformations to data.
    /// </summary>
    /// <param name="data">Input data stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transformed data stream.</returns>
    Task<Stream> ApplyPipelineAsync(Stream data, CancellationToken ct = default);

    /// <summary>
    /// Reverse pipeline transformations to recover original data.
    /// </summary>
    /// <param name="transformedData">Transformed data stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Original data stream.</returns>
    Task<Stream> ReversePipelineAsync(Stream transformedData, CancellationToken ct = default);

    /// <summary>
    /// Get the list of configured pipeline stages.
    /// </summary>
    /// <returns>List of stage identifiers in execution order.</returns>
    IReadOnlyList<string> GetConfiguredStages();
}
