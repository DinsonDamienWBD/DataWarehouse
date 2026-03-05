using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Carbon
{
    /// <summary>
    /// Service contract for measuring energy consumption of storage operations.
    /// Implementations obtain power draw data from hardware counters (RAPL, IPMI),
    /// smart PDUs, cloud provider APIs, or software estimation models.
    /// </summary>
    public interface IEnergyMeasurementService
    {
        /// <summary>
        /// Measures the energy consumed by a specific storage operation.
        /// </summary>
        /// <param name="operationId">Unique identifier for the operation being measured.</param>
        /// <param name="operationType">Type of operation: "read", "write", "delete", or "list".</param>
        /// <param name="dataSizeBytes">Size of the data involved in the operation, in bytes.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>An <see cref="EnergyMeasurement"/> capturing watts consumed, duration, and computed energy.</returns>
        Task<EnergyMeasurement> MeasureOperationAsync(string operationId, string operationType, long dataSizeBytes, CancellationToken ct = default);

        /// <summary>
        /// Returns the average power draw in watts for a given operation type,
        /// based on historical measurements.
        /// </summary>
        /// <param name="operationType">Type of operation: "read", "write", "delete", or "list".</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Average watts consumed for the specified operation type.</returns>
        Task<double> GetWattsPerOperationAsync(string operationType, CancellationToken ct = default);

        /// <summary>
        /// Retrieves historical energy measurements within a time range,
        /// optionally filtered by tenant.
        /// </summary>
        /// <param name="from">Start of the time range (inclusive, UTC).</param>
        /// <param name="to">End of the time range (inclusive, UTC).</param>
        /// <param name="tenantId">Optional tenant filter. Null returns all tenants.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Collection of energy measurements matching the criteria.</returns>
        Task<IReadOnlyList<EnergyMeasurement>> GetMeasurementsAsync(DateTimeOffset from, DateTimeOffset to, string? tenantId = null, CancellationToken ct = default);

        /// <summary>
        /// Returns the current instantaneous system power draw in watts.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Current power consumption in watts.</returns>
        Task<double> GetCurrentPowerDrawWattsAsync(CancellationToken ct = default);
    }
}
