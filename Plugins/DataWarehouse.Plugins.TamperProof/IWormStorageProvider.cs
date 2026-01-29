// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;

namespace DataWarehouse.Plugins.TamperProof;

/// <summary>
/// Interface for WORM (Write-Once-Read-Many) storage providers.
/// Provides immutable storage for disaster recovery and compliance.
/// </summary>
public interface IWormStorageProvider
{
    /// <summary>
    /// Write immutable data to WORM storage.
    /// </summary>
    /// <param name="request">Write request with data and retention policy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Write result with record ID.</returns>
    Task<WormWriteResult> WriteAsync(WormWriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// Read data from WORM storage.
    /// </summary>
    /// <param name="recordId">Record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stored data or null if not found.</returns>
    Task<byte[]?> ReadAsync(string recordId, CancellationToken ct = default);

    /// <summary>
    /// Check if a WORM record exists and is accessible.
    /// </summary>
    /// <param name="recordId">Record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if record exists.</returns>
    Task<bool> ExistsAsync(string recordId, CancellationToken ct = default);

    /// <summary>
    /// Get retention information for a WORM record.
    /// </summary>
    /// <param name="recordId">Record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Retention policy or null if not found.</returns>
    Task<WormRetentionPolicy?> GetRetentionAsync(string recordId, CancellationToken ct = default);
}
