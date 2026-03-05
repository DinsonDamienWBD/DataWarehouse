using System.Text.Json;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.GreenPlacement;

/// <summary>
/// Thread-safe registry storing composite green sustainability scores for storage backends.
/// Computes a weighted score from renewable energy percentage, grid carbon intensity,
/// power usage effectiveness (PUE), and water usage effectiveness (WUE).
/// Pre-populated with known cloud provider sustainability data and supports
/// persistence to JSON for reload across restarts.
/// </summary>
public sealed class BackendGreenScoreRegistry
{
    private readonly BoundedDictionary<string, GreenScore> _scores = new BoundedDictionary<string, GreenScore>(1000);
    private readonly BoundedDictionary<string, string> _backendRegions = new BoundedDictionary<string, string>(1000);
    private readonly string? _persistencePath;

    // Scoring weights
    private const double RenewableWeight = 0.4;
    private const double CarbonIntensityWeight = 0.3;
    private const double PueWeight = 0.2;
    private const double WueWeight = 0.1;

    /// <summary>
    /// Known cloud provider sustainability data, based on publicly reported figures.
    /// </summary>
    private static readonly (string Id, string Region, double RenewablePct, double Pue, double? Wue)[] KnownCloudBackends =
    {
        // AWS regions - based on Amazon Sustainability reports
        ("aws-us-west-2", "us-west-2", 96.0, 1.135, null),            // Oregon - hydroelectric heavy
        ("aws-eu-north-1", "eu-north-1", 100.0, 1.09, 0.18),         // Stockholm - 100% renewable
        ("aws-us-east-1", "us-east-1", 52.0, 1.20, null),            // Virginia - mixed grid
        ("aws-eu-west-1", "eu-west-1", 85.0, 1.12, null),            // Ireland - high wind
        ("aws-ap-southeast-1", "ap-southeast-1", 15.0, 1.30, null),  // Singapore - fossil heavy

        // Azure regions - based on Microsoft Sustainability reports
        ("azure-westeurope", "westeurope", 100.0, 1.12, 0.39),        // Netherlands - 100% renewable matched
        ("azure-swedencentral", "swedencentral", 100.0, 1.08, 0.15),  // Sweden - carbon-free
        ("azure-eastus", "eastus", 50.0, 1.18, null),                 // Virginia
        ("azure-uksouth", "uksouth", 80.0, 1.12, null),               // UK - improving grid

        // GCP regions - based on Google Carbon Free Energy reports
        ("gcp-us-central1", "us-central1", 97.0, 1.10, 0.20),       // Iowa - 97% CFE
        ("gcp-europe-north1", "europe-north1", 98.0, 1.08, 0.15),   // Finland - near 100% CFE
        ("gcp-us-east4", "us-east4", 36.0, 1.18, null),              // Virginia - lower CFE

        // On-premises defaults (configurable)
        ("onprem-default", "onprem", 40.0, 1.50, 1.80),
    };

    /// <summary>
    /// Creates a new BackendGreenScoreRegistry.
    /// </summary>
    /// <param name="persistencePath">Optional file path for persisting scores to JSON. Null disables persistence.</param>
    public BackendGreenScoreRegistry(string? persistencePath = null)
    {
        _persistencePath = persistencePath;
    }

    /// <summary>
    /// Initializes the registry by loading persisted data and pre-registering known cloud backends.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Load persisted scores if available
        await LoadFromDiskAsync(ct);

        // Pre-register known cloud provider data (only if not already loaded)
        foreach (var backend in KnownCloudBackends)
        {
            if (!_scores.ContainsKey(backend.Id))
            {
                RegisterBackend(backend.Id, backend.Region, backend.RenewablePct, backend.Pue, backend.Wue);
            }
        }
    }

    /// <summary>
    /// Registers or updates a storage backend with its sustainability parameters.
    /// Computes a composite green score from the provided metrics.
    /// </summary>
    /// <param name="backendId">Unique identifier of the storage backend.</param>
    /// <param name="region">Geographic region where the backend operates.</param>
    /// <param name="renewablePct">Percentage of energy from renewable sources (0-100).</param>
    /// <param name="pue">Power Usage Effectiveness ratio (1.0 = ideal).</param>
    /// <param name="wue">Optional Water Usage Effectiveness in liters/kWh.</param>
    /// <param name="carbonIntensity">Optional current carbon intensity (gCO2e/kWh). Defaults to 475 (global average).</param>
    public void RegisterBackend(string backendId, string region, double renewablePct, double pue, double? wue, double carbonIntensity = 475.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        var score = ComputeScore(renewablePct, carbonIntensity, pue, wue);

        var greenScore = new GreenScore
        {
            BackendId = backendId,
            Region = region,
            RenewablePercentage = Math.Clamp(renewablePct, 0, 100),
            CarbonIntensityGCO2ePerKwh = carbonIntensity,
            PowerUsageEffectiveness = Math.Max(1.0, pue),
            WaterUsageEffectiveness = wue.HasValue ? Math.Max(0, wue.Value) : null,
            Score = score,
            LastUpdated = DateTimeOffset.UtcNow
        };

        _scores[backendId] = greenScore;
        _backendRegions[backendId] = region;
    }

    /// <summary>
    /// Updates carbon intensity for all backends in the specified region.
    /// Re-computes green scores with the new intensity value.
    /// </summary>
    /// <param name="region">Region identifier to update.</param>
    /// <param name="intensity">New carbon intensity in gCO2e/kWh.</param>
    public void UpdateCarbonIntensity(string region, double intensity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        foreach (var kvp in _backendRegions)
        {
            if (string.Equals(kvp.Value, region, StringComparison.OrdinalIgnoreCase) &&
                _scores.TryGetValue(kvp.Key, out var existing))
            {
                var newScore = ComputeScore(
                    existing.RenewablePercentage,
                    intensity,
                    existing.PowerUsageEffectiveness,
                    existing.WaterUsageEffectiveness);

                _scores[kvp.Key] = existing with
                {
                    CarbonIntensityGCO2ePerKwh = intensity,
                    Score = newScore,
                    LastUpdated = DateTimeOffset.UtcNow
                };
            }
        }
    }

    /// <summary>
    /// Gets the green score for a specific backend.
    /// </summary>
    /// <param name="backendId">Backend identifier.</param>
    /// <returns>The GreenScore if found, null otherwise.</returns>
    public GreenScore? GetScore(string backendId)
    {
        return _scores.TryGetValue(backendId, out var score) ? score : null;
    }

    /// <summary>
    /// Gets all green scores, ordered by composite score descending (greenest first).
    /// </summary>
    /// <returns>Read-only list of all green scores.</returns>
    public IReadOnlyList<GreenScore> GetAllScores()
    {
        return _scores.Values
            .OrderByDescending(s => s.Score)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Selects the best (highest-scoring) backend from a list of candidates.
    /// </summary>
    /// <param name="candidates">List of candidate backend identifiers.</param>
    /// <returns>The backend ID with the highest green score, or null if none found.</returns>
    public string? GetBestBackend(IReadOnlyList<string> candidates)
    {
        if (candidates == null || candidates.Count == 0)
            return null;

        GreenScore? best = null;

        foreach (var candidateId in candidates)
        {
            if (_scores.TryGetValue(candidateId, out var score))
            {
                if (best == null || score.Score > best.Score)
                {
                    best = score;
                }
            }
        }

        return best?.BackendId;
    }

    /// <summary>
    /// Gets the region associated with a backend.
    /// </summary>
    /// <param name="backendId">Backend identifier.</param>
    /// <returns>Region string if found, null otherwise.</returns>
    public string? GetBackendRegion(string backendId)
    {
        return _backendRegions.TryGetValue(backendId, out var region) ? region : null;
    }

    /// <summary>
    /// Gets the total number of registered backends.
    /// </summary>
    public int Count => _scores.Count;

    /// <summary>
    /// Persists the current registry state to disk as JSON.
    /// </summary>
    public async Task PersistAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_persistencePath))
            return;

        try
        {
            var entries = _scores.Values.ToList();
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var directory = Path.GetDirectoryName(_persistencePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(_persistencePath, json, ct);
        }
        catch
        {

            // Persistence failure is non-fatal; data remains in memory
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    /// <summary>
    /// Computes the composite green score using the weighted formula.
    /// </summary>
    /// <remarks>
    /// Score = (renewablePct * 0.4) + ((1 - carbonIntensity/1000) * 100 * 0.3) +
    ///         ((2 - pue) * 100 * 0.2) + (wue component * 0.1)
    /// All components normalized to 0-100 range before weighting.
    /// </remarks>
    private static double ComputeScore(double renewablePct, double carbonIntensity, double pue, double? wue)
    {
        // Component 1: Renewable percentage (0-100) -- higher is better
        var renewableComponent = Math.Clamp(renewablePct, 0, 100) * RenewableWeight;

        // Component 2: Carbon intensity -- lower is better (0 = perfect, 1000+ = very dirty)
        var carbonComponent = Math.Clamp((1 - carbonIntensity / 1000.0) * 100, 0, 100) * CarbonIntensityWeight;

        // Component 3: PUE -- lower is better (1.0 = perfect, 2.0 = poor)
        var pueComponent = Math.Clamp((2 - Math.Max(1.0, pue)) * 100, 0, 100) * PueWeight;

        // Component 4: WUE -- lower is better (0 = no water use, 2.0 = high water use)
        double wueComponent;
        if (wue.HasValue)
        {
            wueComponent = Math.Clamp((2 - wue.Value) * 100, 0, 100) * WueWeight;
        }
        else
        {
            // Default to a moderate score (5 points of 10 max) when WUE is unknown
            wueComponent = 5.0;
        }

        return Math.Round(renewableComponent + carbonComponent + pueComponent + wueComponent, 2);
    }

    /// <summary>
    /// Loads previously persisted scores from disk.
    /// </summary>
    private async Task LoadFromDiskAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_persistencePath) || !File.Exists(_persistencePath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(_persistencePath, ct);
            var entries = JsonSerializer.Deserialize<List<GreenScorePersisted>>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (entries == null)
                return;

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.BackendId) || string.IsNullOrWhiteSpace(entry.Region))
                    continue;

                var score = new GreenScore
                {
                    BackendId = entry.BackendId,
                    Region = entry.Region,
                    RenewablePercentage = entry.RenewablePercentage,
                    CarbonIntensityGCO2ePerKwh = entry.CarbonIntensityGCO2ePerKwh,
                    PowerUsageEffectiveness = entry.PowerUsageEffectiveness,
                    WaterUsageEffectiveness = entry.WaterUsageEffectiveness,
                    Score = entry.Score,
                    LastUpdated = entry.LastUpdated
                };

                _scores[entry.BackendId] = score;
                _backendRegions[entry.BackendId] = entry.Region;
            }
        }
        catch
        {

            // Load failure is non-fatal; will re-populate from known data
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    /// <summary>
    /// DTO for JSON deserialization of persisted green scores.
    /// </summary>
    private sealed class GreenScorePersisted
    {
        public string BackendId { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public double RenewablePercentage { get; set; }
        public double CarbonIntensityGCO2ePerKwh { get; set; }
        public double PowerUsageEffectiveness { get; set; }
        public double? WaterUsageEffectiveness { get; set; }
        public double Score { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
    }
}
