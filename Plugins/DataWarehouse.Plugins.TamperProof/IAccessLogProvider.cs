// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;

namespace DataWarehouse.Plugins.TamperProof;

/// <summary>
/// Interface for access logging providers.
/// Records all access operations for audit trails and tamper attribution.
/// </summary>
public interface IAccessLogProvider
{
    /// <summary>
    /// Log an access operation.
    /// </summary>
    /// <param name="log">Access log entry.</param>
    /// <param name="ct">Cancellation token.</param>
    Task LogAccessAsync(AccessLog log, CancellationToken ct = default);

    /// <summary>
    /// Query access logs for a specific object.
    /// </summary>
    /// <param name="objectId">Object ID to query.</param>
    /// <param name="startTime">Start of time range.</param>
    /// <param name="endTime">End of time range.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of access log entries.</returns>
    Task<IReadOnlyList<AccessLog>> QueryAccessLogsAsync(
        Guid objectId,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        CancellationToken ct = default);

    /// <summary>
    /// Query access logs by principal (user/service).
    /// </summary>
    /// <param name="principal">Principal identifier.</param>
    /// <param name="startTime">Start of time range.</param>
    /// <param name="endTime">End of time range.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of access log entries.</returns>
    Task<IReadOnlyList<AccessLog>> QueryAccessLogsByPrincipalAsync(
        string principal,
        DateTimeOffset? startTime = null,
        DateTimeOffset? endTime = null,
        CancellationToken ct = default);
}
