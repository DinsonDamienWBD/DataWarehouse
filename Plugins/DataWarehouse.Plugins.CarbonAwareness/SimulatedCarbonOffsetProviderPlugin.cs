using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Simulated carbon offset provider for development, testing, and offline scenarios.
///
/// IMPORTANT: This is NOT a production implementation. Use <see cref="PatchCarbonOffsetProviderPlugin"/>
/// for production deployments with real offset marketplace integration.
///
/// This simulated provider is useful for:
/// - Unit and integration testing without API dependencies
/// - Development and prototyping
/// - Offline demonstrations
/// - CI/CD pipelines
/// </summary>
/// <remarks>
/// For production use, configure the <see cref="PatchCarbonOffsetProviderPlugin"/> with your
/// Patch API key from https://dashboard.patch.io/
/// </remarks>
public class SimulatedCarbonOffsetProviderPlugin : CarbonOffsetProviderPluginBase
{
    private readonly ConcurrentBag<OffsetPurchase> _purchaseHistory = new();

    // Reference offset projects for testing
    // These mirror real project types available from offset marketplaces
    private static readonly List<OffsetProject> TestProjects = new()
    {
        new OffsetProject(
            "test-proj-001",
            "Test: Amazon Rainforest Conservation",
            "Reforestation",
            OffsetStandard.VCS,
            15.00,
            "Brazil"
        ),
        new OffsetProject(
            "test-proj-002",
            "Test: Nordic Wind Farm",
            "Renewable Energy",
            OffsetStandard.GoldStandard,
            12.50,
            "Sweden"
        ),
        new OffsetProject(
            "test-proj-003",
            "Test: California Forest Carbon",
            "Forest Management",
            OffsetStandard.ACR,
            18.00,
            "USA"
        ),
        new OffsetProject(
            "test-proj-004",
            "Test: Kenya Cookstoves",
            "Energy Efficiency",
            OffsetStandard.GoldStandard,
            8.00,
            "Kenya"
        ),
        new OffsetProject(
            "test-proj-005",
            "Test: Texas Methane Capture",
            "Methane Capture",
            OffsetStandard.CAR,
            10.00,
            "USA"
        ),
    };

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.offsetprovider.simulated";

    /// <inheritdoc />
    public override string Name => "Simulated Carbon Offset Provider (Test Only)";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <inheritdoc />
    protected override Task<OffsetPurchase> ExecutePurchaseAsync(double carbonGrams, OffsetOptions options)
    {
        // Select project based on options
        OffsetProject project;

        if (!string.IsNullOrEmpty(options.PreferredProjectId))
        {
            project = TestProjects.FirstOrDefault(p => p.ProjectId == options.PreferredProjectId)
                ?? throw new ArgumentException($"Test project {options.PreferredProjectId} not found");
        }
        else if (options.RequiredStandard.HasValue)
        {
            project = TestProjects
                .Where(p => p.Standard == options.RequiredStandard.Value)
                .OrderBy(p => p.PricePerTonCO2)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"No test projects with {options.RequiredStandard} certification");
        }
        else
        {
            // Default: cheapest project
            project = TestProjects.OrderBy(p => p.PricePerTonCO2).First();
        }

        // Calculate cost
        var tons = GramsToTons(carbonGrams);
        var cost = tons * project.PricePerTonCO2;

        // Generate test certificate
        var purchaseId = $"TEST-PUR-{Guid.NewGuid():N}";
        var certificateUrl = $"https://test.offsets.example.com/certificates/{purchaseId}";

        var purchase = new OffsetPurchase(
            purchaseId,
            project.ProjectId,
            carbonGrams,
            cost,
            DateTimeOffset.UtcNow,
            certificateUrl
        );

        _purchaseHistory.Add(purchase);

        return Task.FromResult(purchase);
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<OffsetProject>> FetchProjectsAsync()
    {
        return Task.FromResult<IReadOnlyList<OffsetProject>>(TestProjects);
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<OffsetPurchase>> FetchPurchaseHistoryAsync()
    {
        return Task.FromResult<IReadOnlyList<OffsetPurchase>>(
            _purchaseHistory.OrderByDescending(p => p.PurchasedAt).ToList());
    }

    /// <summary>
    /// Gets total carbon offset in grams.
    /// </summary>
    /// <returns>Total offset amount.</returns>
    public double GetTotalOffsetGrams()
    {
        return _purchaseHistory.Sum(p => p.CarbonGramsOffset);
    }

    /// <summary>
    /// Gets total cost of all offset purchases.
    /// </summary>
    /// <returns>Total cost in USD.</returns>
    public double GetTotalCost()
    {
        return _purchaseHistory.Sum(p => p.Cost);
    }

    /// <summary>
    /// Gets purchases by project.
    /// </summary>
    /// <returns>Dictionary of project ID to total offset grams.</returns>
    public Dictionary<string, double> GetOffsetsByProject()
    {
        return _purchaseHistory
            .GroupBy(p => p.ProjectId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.CarbonGramsOffset));
    }

    /// <summary>
    /// Estimates the cost to offset a given amount of carbon.
    /// </summary>
    /// <param name="carbonGrams">Amount of carbon to offset.</param>
    /// <param name="standard">Optional certification standard requirement.</param>
    /// <returns>Estimated cost in USD.</returns>
    public double EstimateCost(double carbonGrams, OffsetStandard? standard = null)
    {
        var eligibleProjects = standard.HasValue
            ? TestProjects.Where(p => p.Standard == standard.Value)
            : TestProjects;

        var cheapestPrice = eligibleProjects.Min(p => p.PricePerTonCO2);
        return GramsToTons(carbonGrams) * cheapestPrice;
    }

    /// <summary>
    /// Clears purchase history (for testing).
    /// </summary>
    public void ClearHistory()
    {
        _purchaseHistory.Clear();
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;
}
