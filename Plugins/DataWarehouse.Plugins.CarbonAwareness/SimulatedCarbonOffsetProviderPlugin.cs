using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Simulated carbon offset provider for development and testing.
/// In production, replace with integration to actual offset marketplaces
/// like Patch, Cloverly, or direct registry APIs.
/// </summary>
public class SimulatedCarbonOffsetProviderPlugin : CarbonOffsetProviderPluginBase
{
    private readonly ConcurrentBag<OffsetPurchase> _purchaseHistory = new();

    // Simulated offset projects
    private static readonly List<OffsetProject> _projects = new()
    {
        new OffsetProject(
            "PROJ-001",
            "Amazon Rainforest Conservation",
            "Reforestation",
            OffsetStandard.VCS,
            15.00,
            "Brazil"
        ),
        new OffsetProject(
            "PROJ-002",
            "Nordic Wind Farm",
            "Renewable Energy",
            OffsetStandard.GoldStandard,
            12.50,
            "Sweden"
        ),
        new OffsetProject(
            "PROJ-003",
            "California Forest Carbon",
            "Forest Management",
            OffsetStandard.ACR,
            18.00,
            "USA"
        ),
        new OffsetProject(
            "PROJ-004",
            "Kenya Cookstoves",
            "Energy Efficiency",
            OffsetStandard.GoldStandard,
            8.00,
            "Kenya"
        ),
        new OffsetProject(
            "PROJ-005",
            "Texas Methane Capture",
            "Methane Capture",
            OffsetStandard.CAR,
            10.00,
            "USA"
        ),
        new OffsetProject(
            "PROJ-006",
            "Indonesian Mangrove Restoration",
            "Blue Carbon",
            OffsetStandard.VCS,
            20.00,
            "Indonesia"
        ),
        new OffsetProject(
            "PROJ-007",
            "Indian Solar Initiative",
            "Renewable Energy",
            OffsetStandard.GoldStandard,
            9.50,
            "India"
        ),
    };

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.offsetprovider.simulated";

    /// <inheritdoc />
    public override string Name => "Simulated Carbon Offset Provider";

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
            project = _projects.FirstOrDefault(p => p.ProjectId == options.PreferredProjectId)
                ?? throw new ArgumentException($"Project {options.PreferredProjectId} not found");
        }
        else if (options.RequiredStandard.HasValue)
        {
            project = _projects
                .Where(p => p.Standard == options.RequiredStandard.Value)
                .OrderBy(p => p.PricePerTonCO2)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"No projects with {options.RequiredStandard} certification");
        }
        else
        {
            // Default: cheapest project
            project = _projects.OrderBy(p => p.PricePerTonCO2).First();
        }

        // Calculate cost
        var tons = GramsToTons(carbonGrams);
        var cost = tons * project.PricePerTonCO2;

        // Generate certificate
        var purchaseId = $"PUR-{Guid.NewGuid():N}";
        var certificateUrl = $"https://offsets.example.com/certificates/{purchaseId}";

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
        return Task.FromResult<IReadOnlyList<OffsetProject>>(_projects);
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
            ? _projects.Where(p => p.Standard == standard.Value)
            : _projects;

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
