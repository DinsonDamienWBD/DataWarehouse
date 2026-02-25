# Plugin: PluginMarketplace
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.PluginMarketplace

### File: Plugins/DataWarehouse.Plugins.PluginMarketplace/PluginMarketplacePlugin.cs
```csharp
public sealed class PluginMarketplacePlugin : PlatformPluginBase
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string PlatformDomain;;
    public override PluginCategory Category;;
    public PluginMarketplacePlugin(PluginMarketplaceConfig? config = null);
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    public override async Task OnMessageAsync(PluginMessage message);
    public override async Task StartAsync(CancellationToken ct);
    public override async Task StopAsync();
}
```
```csharp
public sealed record PluginCatalogEntry
{
}
    public string Id { get; init; };
    public string Name { get; init; };
    public string Author { get; init; };
    public string Description { get; init; };
    public string? FullDescription { get; init; }
    public string Category { get; init; };
    public string[]? Tags { get; init; }
    public string CurrentVersion { get; init; };
    public string LatestVersion { get; init; };
    public string[]? AvailableVersions { get; init; }
    public bool IsInstalled { get; init; }
    public bool HasUpdate { get; init; }
    public CertificationLevel CertificationLevel { get; init; };
    public double AverageRating { get; init; }
    public int RatingCount { get; init; }
    public long InstallCount { get; init; }
    public PluginDependencyInfo[]? Dependencies { get; init; }
    public DateTime CreatedAt { get; init; };
    public DateTime UpdatedAt { get; init; };
    public DateTime? InstalledAt { get; init; }
    public decimal Price { get; init; }
}
```
```csharp
public sealed record PluginMarketplaceConfig
{
}
    public string? StoragePath { get; init; }
    public TimeSpan CatalogRefreshInterval { get; init; };
    public int MaxInstallRetries { get; init; };
    public bool RequireCertification { get; init; }
    public bool AutoUpdateEnabled { get; init; }
    public CertificationPolicy? CertificationPolicyOverride { get; init; }
    public RevenueConfig? RevenueConfigOverride { get; init; }
}
```
```csharp
internal sealed class MarketplaceState
{
}
    public DateTime LastCatalogRefresh { get; set; };
    internal long _totalInstalls;
    internal long _totalUninstalls;
    public int ActivePluginCount { get; set; }
}
```
```csharp
internal sealed record MarketplaceStateDto
{
}
    public DateTime LastCatalogRefresh { get; init; }
    public long TotalInstalls { get; init; }
    public long TotalUninstalls { get; init; }
    public int ActivePluginCount { get; init; }
}
```
```csharp
public sealed record CertificationPolicy
{
}
    public bool RequireSignedAssembly { get; init; };
    public bool ValidateAssemblyHash { get; init; };
    public int MaxAssemblySizeMb { get; init; };
    public bool RequireSdkCompatibility { get; init; };
    public double MinimumScore { get; init; };
    public int CertificationValidityDays { get; init; };
}
```
```csharp
public sealed record PluginReviewEntry
{
}
    public string Id { get; init; };
    public string PluginId { get; init; };
    public string ReviewerId { get; init; };
    public string ReviewerName { get; init; };
    public int OverallRating { get; init; }
    public string? Title { get; init; }
    public string? Content { get; init; }
    public int? ReliabilityRating { get; init; }
    public int? PerformanceRating { get; init; }
    public int? DocumentationRating { get; init; }
    public bool IsVerifiedInstall { get; init; }
    public ReviewModerationStatus ReviewStatus { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? OwnerResponse { get; init; }
    public DateTime? OwnerRespondedAt { get; init; }
}
```
```csharp
public sealed record PluginReviewSubmission
{
}
    public string PluginId { get; init; };
    public string ReviewerId { get; init; };
    public string ReviewerName { get; init; };
    public int OverallRating { get; init; }
    public string? Title { get; init; }
    public string? Content { get; init; }
    public int? ReliabilityRating { get; init; }
    public int? PerformanceRating { get; init; }
    public int? DocumentationRating { get; init; }
}
```
```csharp
public sealed record PluginReviewsResponse
{
}
    public string PluginId { get; init; };
    public PluginReviewEntry[] Reviews { get; init; };
    public int TotalCount { get; init; }
    public double AverageRating { get; init; }
    public Dictionary<int, int>? RatingDistribution { get; init; }
}
```
```csharp
public sealed record DeveloperRevenueRecord
{
}
    public string DeveloperId { get; init; };
    public string PluginId { get; init; };
    public string Period { get; init; };
    public decimal TotalEarnings { get; init; }
    public decimal CommissionRate { get; init; };
    public decimal CommissionAmount { get; init; }
    public decimal NetEarnings { get; init; }
    public int InstallCount { get; init; }
    public PayoutStatus PayoutStatus { get; init; }
}
```
```csharp
public sealed record RevenueConfig
{
}
    public decimal DefaultCommissionRate { get; init; };
    public decimal MinimumPayoutThreshold { get; init; };
    public string PayoutCurrency { get; init; };
}
```
```csharp
public sealed record PluginUsageEvent
{
}
    public string EventId { get; init; };
    public string PluginId { get; init; };
    public UsageEventType EventType { get; init; }
    public DateTime Timestamp { get; init; }
    public string? UserId { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}
```
```csharp
public sealed record PluginUsageAnalytics
{
}
    public string PluginId { get; init; };
    public string Period { get; init; };
    public int InstallCount { get; init; }
    public int UninstallCount { get; init; }
    public int UpdateCount { get; init; }
    public int ActiveUserCount { get; init; }
    public int ErrorCount { get; init; }
    public long MessageCount { get; init; }
    public double PopularityScore { get; init; }
    public TrendDirection TrendDirection { get; init; }
}
```
```csharp
public sealed record PluginAnalyticsSummary
{
}
    public string PluginId { get; init; };
    public long TotalInstalls { get; init; }
    public long TotalUninstalls { get; init; }
    public int CurrentActiveInstalls { get; init; }
    public TimeSpan AverageSessionDuration { get; init; }
    public string MostActiveMonth { get; init; };
    public PluginUsageAnalytics[] MonthlyData { get; init; };
}
```
```csharp
public sealed record MarketplaceAnalyticsSummary
{
}
    public int TotalPlugins { get; init; }
    public long TotalInstalls { get; init; }
    public string[] MostPopularPlugins { get; init; };
    public string MostActiveCategory { get; init; };
    public double GrowthRate { get; init; }
    public DateTime GeneratedAt { get; init; }
}
```
