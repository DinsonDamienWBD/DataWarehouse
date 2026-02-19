using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Carbon
{
    /// <summary>
    /// Service contract for renewable-aware storage placement.
    /// Selects storage backends based on grid carbon intensity, renewable energy percentage,
    /// and composite green scores to minimize the carbon footprint of data placement.
    /// </summary>
    public interface IGreenPlacementService
    {
        /// <summary>
        /// Selects the most sustainable storage backend from a list of candidates
        /// based on current grid carbon data and green scores.
        /// </summary>
        /// <param name="candidateBackendIds">List of candidate storage backend identifiers to evaluate.</param>
        /// <param name="dataSizeBytes">Size of the data to be placed, in bytes.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="CarbonPlacementDecision"/> identifying the greenest backend and alternatives.</returns>
        Task<CarbonPlacementDecision> SelectGreenestBackendAsync(IReadOnlyList<string> candidateBackendIds, long dataSizeBytes, CancellationToken ct = default);

        /// <summary>
        /// Returns the current green scores for all known storage backends.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Collection of <see cref="GreenScore"/> entries, one per backend.</returns>
        Task<IReadOnlyList<GreenScore>> GetGreenScoresAsync(CancellationToken ct = default);

        /// <summary>
        /// Retrieves the current or forecasted grid carbon data for a specific region.
        /// </summary>
        /// <param name="region">Grid region identifier (e.g., "US-CAL-CISO", "GB").</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Grid carbon intensity and renewable mix data for the region.</returns>
        Task<GridCarbonData> GetGridCarbonDataAsync(string region, CancellationToken ct = default);

        /// <summary>
        /// Forces a refresh of grid carbon data from external APIs (WattTime, Electricity Maps, etc.).
        /// Use sparingly to respect API rate limits.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        Task RefreshGridDataAsync(CancellationToken ct = default);
    }
}
