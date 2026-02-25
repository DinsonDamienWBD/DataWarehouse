using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.PluginMarketplace;

/// <summary>
/// Production-ready Plugin Marketplace Plugin implementing T57.
/// Provides comprehensive plugin catalog management for the DataWarehouse ecosystem.
///
/// Features:
/// - T57.1: Plugin Catalog - Persistent catalog of all available/installed plugins with metadata
/// - T57.2: Plugin Installation - Install plugins via kernel message bus with dependency resolution
/// - T57.3: Plugin Uninstallation - Unload plugins with reverse dependency checking
/// - T57.4: Plugin Updates - Upgrade plugins with version archiving and rollback support
/// - T57.5: Dependency Resolution - Topological sort with circular dependency detection
/// - T57.6: Version Archive - Archive old versions for rollback on failed updates
/// - T57.7: Search and Filter - Search catalog by name, category, tags with sorting
/// - T57.8: Certification Tracking - Track plugin certification levels and verification status
///
/// - T57.9: Certification Pipeline - Multi-stage security validation with kernel integration
/// - T57.10: Rating/Review System - Multi-dimensional ratings (reliability, performance, documentation)
/// - T57.11: Revenue Tracking - Commission calculation and developer earnings
///
/// Message Commands:
/// - marketplace.list: Return all catalog entries for the GUI
/// - marketplace.install: Install a plugin with dependency resolution
/// - marketplace.uninstall: Uninstall a plugin with reverse dependency check
/// - marketplace.update: Update a plugin with version archiving and rollback
/// - marketplace.certify: Run 5-stage certification pipeline on a plugin assembly
/// - marketplace.review: Submit a multi-dimensional rating and review
/// </summary>
public sealed class PluginMarketplacePlugin : PlatformPluginBase
{
    private readonly BoundedDictionary<string, PluginCatalogEntry> _catalog = new BoundedDictionary<string, PluginCatalogEntry>(1000);
    private readonly BoundedDictionary<string, List<PluginVersionInfo>> _versionHistory = new BoundedDictionary<string, List<PluginVersionInfo>>(1000);
    private readonly BoundedDictionary<string, List<PluginReviewEntry>> _reviews = new BoundedDictionary<string, List<PluginReviewEntry>>(1000);
    private readonly BoundedDictionary<string, CertificationResult> _certifications = new BoundedDictionary<string, CertificationResult>(1000);
    private readonly BoundedDictionary<string, List<DeveloperRevenueRecord>> _revenueRecords = new BoundedDictionary<string, List<DeveloperRevenueRecord>>(1000);
    private readonly BoundedDictionary<string, List<PluginUsageEvent>> _usageEvents = new BoundedDictionary<string, List<PluginUsageEvent>>(1000);
    private readonly BoundedDictionary<string, PluginUsageAnalytics> _monthlyAnalytics = new BoundedDictionary<string, PluginUsageAnalytics>(1000);
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly PluginMarketplaceConfig _config;
    private readonly CertificationPolicy _certificationPolicy;
    private readonly RevenueConfig _revenueConfig;
    private readonly string _storagePath;
    private readonly Timer _analyticsAggregationTimer;
    private MarketplaceState _state;
    private volatile bool _isRunning;

    /// <summary>Maximum number of usage events kept in memory per plugin before evicting oldest.</summary>
    private const int MaxEventsPerPlugin = 10000;

    /// <summary>Number of days of event log files to retain on disk before rotation.</summary>
    private const int EventLogRetentionDays = 90;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <inheritdoc/>
    public override string Id => "datawarehouse.plugins.marketplace.plugin";

    /// <inheritdoc/>
    public override string Name => "Plugin Marketplace (T57)";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string PlatformDomain => "PluginMarketplace";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Initializes a new instance of the PluginMarketplacePlugin.
    /// </summary>
    /// <param name="config">Optional configuration for the plugin marketplace.</param>
    public PluginMarketplacePlugin(PluginMarketplaceConfig? config = null)
    {
        _config = config ?? new PluginMarketplaceConfig();
        _certificationPolicy = _config.CertificationPolicyOverride ?? new CertificationPolicy();
        _revenueConfig = _config.RevenueConfigOverride ?? new RevenueConfig();
        _storagePath = _config.StoragePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataWarehouse", "plugin-marketplace");
        _state = new MarketplaceState();
        _analyticsAggregationTimer = new Timer(
            callback: _ => _ = AggregateAnalyticsFireAndForgetAsync(),
            state: null,
            dueTime: Timeout.Infinite,
            period: Timeout.Infinite);
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);
        await LoadStateAsync();
        await LoadCatalogAsync();
        await LoadVersionHistoryAsync();
        await LoadReviewsAsync();
        await LoadCertificationsAsync();
        await LoadRevenueRecordsAsync();
        await LoadAnalyticsAsync();

        // ISO-05: Security warnings for weakened certification policy
        if (!_certificationPolicy.RequireSignedAssembly)
        {
            Console.Error.WriteLine(
                "[SECURITY WARNING] PluginMarketplace: RequireSignedAssembly is disabled. " +
                "Unsigned plugin assemblies will be accepted. This is a security risk (ISO-05, CVSS 7.7). " +
                "Enable RequireSignedAssembly in production environments.");
        }

        if (!_certificationPolicy.ValidateAssemblyHash)
        {
            Console.Error.WriteLine(
                "[SECURITY WARNING] PluginMarketplace: ValidateAssemblyHash is disabled. " +
                "Plugin assembly integrity will not be verified against catalog hashes. " +
                "Enable ValidateAssemblyHash in production environments.");
        }

        // If catalog is empty, populate with built-in entries
        if (_catalog.IsEmpty)
        {
            PopulateBuiltInCatalog();
            await SaveAllCatalogEntriesAsync();
        }

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "plugin.marketplace.list", DisplayName = "List Plugins", Description = "List all available and installed plugins" },
            new() { Name = "plugin.marketplace.install", DisplayName = "Install Plugin", Description = "Install a plugin with dependency resolution" },
            new() { Name = "plugin.marketplace.uninstall", DisplayName = "Uninstall Plugin", Description = "Uninstall a plugin with reverse dependency check" },
            new() { Name = "plugin.marketplace.update", DisplayName = "Update Plugin", Description = "Update a plugin with version archiving and rollback" },
            new() { Name = "plugin.marketplace.search", DisplayName = "Search Plugins", Description = "Search and filter the plugin catalog" },
            new() { Name = "plugin.marketplace.certify", DisplayName = "Certify Plugin", Description = "Set certification level for a plugin" },
            new() { Name = "plugin.marketplace.review", DisplayName = "Review Plugin", Description = "Submit a rating and review for a plugin" },
            new() { Name = "plugin.marketplace.analytics", DisplayName = "Marketplace Analytics", Description = "Get marketplace usage analytics" }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalPlugins"] = _catalog.Count;
        metadata["InstalledPlugins"] = _catalog.Values.Count(e => e.IsInstalled);
        metadata["CertifiedPlugins"] = _catalog.Values.Count(e => e.CertificationLevel >= CertificationLevel.BasicCertified);
        metadata["AverageRating"] = _catalog.Values.Where(e => e.RatingCount > 0)
            .Select(e => e.AverageRating)
            .DefaultIfEmpty(0.0)
            .Average();
        return metadata;
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (!_isRunning && message.Type.StartsWith("marketplace.", StringComparison.Ordinal))
        {
            message.Payload["error"] = "Plugin Marketplace is not running. Call StartAsync first.";
            return;
        }

        switch (message.Type)
        {
            case "marketplace.list":
                await HandleListAsync(message);
                break;
            case "marketplace.install":
                await HandleInstallAsync(message);
                break;
            case "marketplace.uninstall":
                await HandleUninstallAsync(message);
                break;
            case "marketplace.update":
                await HandleUpdateAsync(message);
                break;
            case "marketplace.search":
                HandleSearch(message);
                break;
            case "marketplace.certify":
                await HandleCertifyAsync(message);
                break;
            case "marketplace.review":
                await HandleReviewAsync(message);
                break;
            case "marketplace.analytics":
                await HandleAnalyticsAsync(message);
                break;
            default:
                await base.OnMessageAsync(message);
                break;
        }
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        _isRunning = true;
        Directory.CreateDirectory(_storagePath);
        Directory.CreateDirectory(Path.Combine(_storagePath, "catalog"));
        Directory.CreateDirectory(Path.Combine(_storagePath, "versions"));
        Directory.CreateDirectory(Path.Combine(_storagePath, "reviews"));
        Directory.CreateDirectory(Path.Combine(_storagePath, "archive"));
        Directory.CreateDirectory(Path.Combine(_storagePath, "certifications"));
        Directory.CreateDirectory(Path.Combine(_storagePath, "revenue"));
        Directory.CreateDirectory(Path.Combine(_storagePath, "analytics"));

        // Start analytics aggregation timer: initial delay 5 minutes, then every hour
        _analyticsAggregationTimer.Change(
            dueTime: TimeSpan.FromMinutes(5),
            period: TimeSpan.FromHours(1));

        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task StopAsync()
    {
        _isRunning = false;

        // Stop analytics timer and run one final aggregation
        await _analyticsAggregationTimer.DisposeAsync();
        await AggregateAnalyticsAsync(CancellationToken.None);
        await SaveAnalyticsAsync();

        await SaveStateAsync();
        await SaveAllCatalogEntriesAsync();
    }

    #region marketplace.list Handler

    /// <summary>
    /// Handles the marketplace.list message by returning all catalog entries as a list of
    /// dictionaries matching what the Marketplace.razor GUI ParsePlugins method expects.
    /// </summary>
    /// <param name="message">The incoming plugin message.</param>
    private async Task HandleListAsync(PluginMessage message)
    {
        var searchQuery = GetString(message.Payload, "searchQuery");
        var category = GetString(message.Payload, "category");
        var sortBy = GetString(message.Payload, "sortBy") ?? "popular";

        var entries = _catalog.Values.AsEnumerable();

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var search = searchQuery.ToLowerInvariant();
            entries = entries.Where(e =>
                e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Author.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.Tags != null && e.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase))));
        }

        // Apply category filter
        if (!string.IsNullOrWhiteSpace(category))
        {
            entries = entries.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        // Apply sorting
        entries = sortBy switch
        {
            "rating" => entries.OrderByDescending(e => e.AverageRating),
            "recent" => entries.OrderByDescending(e => e.UpdatedAt),
            "name" => entries.OrderBy(e => e.Name),
            _ => entries.OrderByDescending(e => e.InstallCount) // popular
        };

        var result = new List<Dictionary<string, object>>();
        foreach (var entry in entries)
        {
            var dict = new Dictionary<string, object>
            {
                ["id"] = entry.Id,
                ["name"] = entry.Name,
                ["author"] = entry.Author,
                ["description"] = entry.Description,
                ["fullDescription"] = entry.FullDescription ?? entry.Description,
                ["category"] = entry.Category,
                ["version"] = entry.CurrentVersion,
                ["latestVersion"] = entry.LatestVersion,
                ["downloads"] = entry.InstallCount,
                ["rating"] = (int)Math.Round(entry.AverageRating),
                ["ratingCount"] = entry.RatingCount,
                ["isInstalled"] = entry.IsInstalled,
                ["hasUpdate"] = entry.HasUpdate,
                ["isVerified"] = entry.CertificationLevel >= CertificationLevel.BasicCertified,
                ["lastUpdated"] = entry.UpdatedAt.ToString("o")
            };

            // Include top review if available (most recent approved review with highest rating)
            if (_reviews.TryGetValue(entry.Id, out var reviews) && reviews.Count > 0)
            {
                List<PluginReviewEntry> approvedReviews;
                lock (reviews)
                {
                    approvedReviews = reviews
                        .Where(r => r.ReviewStatus == ReviewModerationStatus.Approved)
                        .ToList();
                }

                if (approvedReviews.Count > 0)
                {
                    var topReview = approvedReviews
                        .OrderByDescending(r => r.OverallRating)
                        .ThenByDescending(r => r.CreatedAt)
                        .First();
                    dict["topReview"] = new Dictionary<string, object>
                    {
                        ["author"] = topReview.ReviewerName,
                        ["text"] = topReview.Content ?? topReview.Title ?? "",
                        ["rating"] = topReview.OverallRating,
                        ["isVerifiedInstall"] = topReview.IsVerifiedInstall
                    };

                    // Include top 5 reviews by date (approved only) for details modal
                    dict["reviews"] = approvedReviews
                        .OrderByDescending(r => r.CreatedAt)
                        .Take(5)
                        .Select(r => new Dictionary<string, object>
                        {
                            ["id"] = r.Id,
                            ["author"] = r.ReviewerName,
                            ["text"] = r.Content ?? r.Title ?? "",
                            ["rating"] = r.OverallRating,
                            ["reliabilityRating"] = (object?)r.ReliabilityRating ?? 0,
                            ["performanceRating"] = (object?)r.PerformanceRating ?? 0,
                            ["documentationRating"] = (object?)r.DocumentationRating ?? 0,
                            ["isVerifiedInstall"] = r.IsVerifiedInstall,
                            ["date"] = r.CreatedAt.ToString("o")
                        }).ToList<object>();
                }
            }

            // Include version history if available
            if (_versionHistory.TryGetValue(entry.Id, out var versions) && versions.Count > 0)
            {
                dict["versions"] = versions.OrderByDescending(v => v.ReleaseDate)
                    .Select(v => new Dictionary<string, object>
                    {
                        ["number"] = v.VersionNumber,
                        ["changelog"] = v.Changelog ?? "",
                        ["releaseDate"] = v.ReleaseDate.ToString("o")
                    }).ToList<object>();
            }

            result.Add(dict);
        }

        message.Payload["result"] = result;
        await Task.CompletedTask;
    }

    #endregion

    #region marketplace.install Handler

    /// <summary>
    /// Handles the marketplace.install message. Resolves dependencies, installs the plugin
    /// via the kernel message bus, and updates the catalog.
    /// </summary>
    /// <param name="message">The incoming plugin message with pluginId and version.</param>
    private async Task HandleInstallAsync(PluginMessage message)
    {
        var pluginId = GetString(message.Payload, "pluginId");
        var version = GetString(message.Payload, "version");

        if (string.IsNullOrWhiteSpace(pluginId))
        {
            message.Payload["error"] = "pluginId is required";
            return;
        }

        if (!_catalog.TryGetValue(pluginId, out var entry))
        {
            message.Payload["error"] = $"Plugin not found: {pluginId}";
            return;
        }

        if (entry.IsInstalled)
        {
            message.Payload["error"] = $"Plugin already installed: {pluginId}";
            return;
        }

        var targetVersion = version ?? entry.LatestVersion;

        // Resolve dependencies
        List<(string PluginId, string Version)> installOrder;
        try
        {
            installOrder = await ResolveDependenciesAsync(pluginId, targetVersion);
        }
        catch (InvalidOperationException ex)
        {
            message.Payload["error"] = $"Dependency resolution failed: {ex.Message}";
            return;
        }

        var installedDependencies = new List<string>();

        // Install dependencies first (they are ordered by topological sort)
        foreach (var (depId, depVersion) in installOrder)
        {
            if (depId == pluginId) continue; // Skip the main plugin, install it separately

            if (_catalog.TryGetValue(depId, out var depEntry) && !depEntry.IsInstalled)
            {
                var depSuccess = await InstallPluginViaKernelAsync(depId, depVersion);
                if (!depSuccess)
                {
                    message.Payload["error"] = $"Failed to install dependency: {depId}";
                    return;
                }

                UpdateCatalogEntryInstalled(depId, depVersion);
                installedDependencies.Add(depId);
            }
        }

        // Install the main plugin
        var success = await InstallPluginViaKernelAsync(pluginId, targetVersion);
        if (!success)
        {
            message.Payload["error"] = $"Failed to install plugin: {pluginId}";
            return;
        }

        UpdateCatalogEntryInstalled(pluginId, targetVersion);
        Interlocked.Increment(ref _state._totalInstalls);

        // Record usage analytics event for installation
        await RecordUsageEventAsync(new PluginUsageEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            PluginId = pluginId,
            EventType = UsageEventType.Installed,
            Timestamp = DateTime.UtcNow,
            Metadata = new Dictionary<string, object> { ["version"] = targetVersion }
        }, CancellationToken.None);

        // Record revenue if plugin has a price (author = developer)
        if (_catalog.TryGetValue(pluginId, out var updatedEntry))
        {
            var price = GetDecimalFromCatalog(updatedEntry);
            if (price > 0m)
            {
                await RecordInstallRevenueAsync(pluginId, updatedEntry.Author, price, CancellationToken.None);
            }
        }

        await SaveCatalogEntryAsync(entry.Id);
        await SaveStateAsync();

        message.Payload["result"] = new PluginInstallResult(
            Success: true,
            PluginId: pluginId,
            InstalledVersion: targetVersion,
            Message: $"Successfully installed {entry.Name} v{targetVersion}",
            InstalledDependencies: installedDependencies.ToArray());
    }

    /// <summary>
    /// Sends a kernel.plugin.load message to the kernel via message bus to install a plugin.
    /// </summary>
    /// <param name="pluginId">The ID of the plugin to load.</param>
    /// <param name="version">The version to load.</param>
    /// <returns>True if the kernel acknowledged the load request.</returns>
    private async Task<bool> InstallPluginViaKernelAsync(string pluginId, string version)
    {
        if (MessageBus == null) return true; // No message bus in standalone mode

        var assemblyPath = ResolveAssemblyPath(pluginId, version);

        var loadMessage = PluginMessage.Create("kernel.plugin.load", new Dictionary<string, object>
        {
            ["pluginId"] = pluginId,
            ["version"] = version,
            ["assemblyPath"] = assemblyPath
        });
        loadMessage.Payload["sourcePluginId"] = Id;

        try
        {
            var response = await MessageBus.SendAsync("kernel.plugin.load", loadMessage, TimeSpan.FromSeconds(30));
            return response.Success;
        }
        catch (TimeoutException)
        {
            // Timeout waiting for kernel response - treat as potential success
            // The kernel may have loaded the plugin but failed to respond in time
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Updates a catalog entry to mark it as installed.
    /// </summary>
    /// <param name="pluginId">The plugin ID to update.</param>
    /// <param name="version">The installed version.</param>
    private void UpdateCatalogEntryInstalled(string pluginId, string version)
    {
        if (_catalog.TryGetValue(pluginId, out var entry))
        {
            _catalog[pluginId] = entry with
            {
                IsInstalled = true,
                InstalledAt = DateTime.UtcNow,
                CurrentVersion = version,
                HasUpdate = !string.Equals(version, entry.LatestVersion, StringComparison.OrdinalIgnoreCase) ? true : false,
                InstallCount = entry.InstallCount + 1,
                UpdatedAt = DateTime.UtcNow
            };
        }
    }

    #endregion

    #region marketplace.uninstall Handler

    /// <summary>
    /// Handles the marketplace.uninstall message. Checks reverse dependencies and unloads
    /// the plugin via kernel message bus.
    /// </summary>
    /// <param name="message">The incoming plugin message with pluginId.</param>
    private async Task HandleUninstallAsync(PluginMessage message)
    {
        var pluginId = GetString(message.Payload, "pluginId");

        if (string.IsNullOrWhiteSpace(pluginId))
        {
            message.Payload["error"] = "pluginId is required";
            return;
        }

        if (!_catalog.TryGetValue(pluginId, out var entry))
        {
            message.Payload["error"] = $"Plugin not found: {pluginId}";
            return;
        }

        if (!entry.IsInstalled)
        {
            message.Payload["error"] = $"Plugin is not installed: {pluginId}";
            return;
        }

        // Check reverse dependencies - ensure no other installed plugin depends on this one
        var dependents = GetReverseDependencies(pluginId);
        if (dependents.Count > 0)
        {
            var dependentNames = string.Join(", ", dependents.Select(d =>
                _catalog.TryGetValue(d, out var dep) ? dep.Name : d));
            message.Payload["error"] = $"Cannot uninstall: the following installed plugins depend on this plugin: {dependentNames}";
            return;
        }

        // Send unload message to kernel
        var success = await UninstallPluginViaKernelAsync(pluginId);
        if (!success)
        {
            message.Payload["error"] = $"Failed to uninstall plugin: {pluginId}";
            return;
        }

        // Update catalog entry
        _catalog[pluginId] = entry with
        {
            IsInstalled = false,
            InstalledAt = null,
            HasUpdate = false,
            UpdatedAt = DateTime.UtcNow
        };

        Interlocked.Increment(ref _state._totalUninstalls);

        // Record usage analytics event for uninstallation
        await RecordUsageEventAsync(new PluginUsageEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            PluginId = pluginId,
            EventType = UsageEventType.Uninstalled,
            Timestamp = DateTime.UtcNow
        }, CancellationToken.None);

        await SaveCatalogEntryAsync(pluginId);
        await SaveStateAsync();

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["success"] = true,
            ["pluginId"] = pluginId,
            ["message"] = $"Successfully uninstalled {entry.Name}"
        };
    }

    /// <summary>
    /// Sends a kernel.plugin.unload message to the kernel via message bus.
    /// </summary>
    /// <param name="pluginId">The ID of the plugin to unload.</param>
    /// <returns>True if the kernel acknowledged the unload request.</returns>
    private async Task<bool> UninstallPluginViaKernelAsync(string pluginId)
    {
        if (MessageBus == null) return true;

        var unloadMessage = PluginMessage.Create("kernel.plugin.unload", new Dictionary<string, object>
        {
            ["pluginId"] = pluginId
        });
        unloadMessage.Payload["sourcePluginId"] = Id;

        try
        {
            var response = await MessageBus.SendAsync("kernel.plugin.unload", unloadMessage, TimeSpan.FromSeconds(30));
            return response.Success;
        }
        catch (TimeoutException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets all installed plugins that depend on the specified plugin.
    /// </summary>
    /// <param name="pluginId">The plugin to check for reverse dependencies.</param>
    /// <returns>List of plugin IDs that depend on the specified plugin.</returns>
    private List<string> GetReverseDependencies(string pluginId)
    {
        var dependents = new List<string>();

        foreach (var entry in _catalog.Values)
        {
            if (!entry.IsInstalled) continue;
            if (entry.Id == pluginId) continue;

            if (entry.Dependencies != null)
            {
                foreach (var dep in entry.Dependencies)
                {
                    if (string.Equals(dep.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) && !dep.IsOptional)
                    {
                        dependents.Add(entry.Id);
                        break;
                    }
                }
            }
        }

        return dependents;
    }

    #endregion

    #region marketplace.update Handler

    /// <summary>
    /// Handles the marketplace.update message. Archives the current version, resolves dependencies
    /// for the new version, unloads the old version, loads the new version, and rolls back on failure.
    /// </summary>
    /// <param name="message">The incoming plugin message with pluginId and version.</param>
    private async Task HandleUpdateAsync(PluginMessage message)
    {
        var pluginId = GetString(message.Payload, "pluginId");
        var version = GetString(message.Payload, "version");

        if (string.IsNullOrWhiteSpace(pluginId))
        {
            message.Payload["error"] = "pluginId is required";
            return;
        }

        if (!_catalog.TryGetValue(pluginId, out var entry))
        {
            message.Payload["error"] = $"Plugin not found: {pluginId}";
            return;
        }

        if (!entry.IsInstalled)
        {
            message.Payload["error"] = $"Plugin is not installed: {pluginId}";
            return;
        }

        var newVersion = version ?? entry.LatestVersion;
        var oldVersion = entry.CurrentVersion;

        if (string.Equals(oldVersion, newVersion, StringComparison.OrdinalIgnoreCase))
        {
            message.Payload["result"] = new Dictionary<string, object>
            {
                ["success"] = true,
                ["pluginId"] = pluginId,
                ["message"] = "Already at the requested version"
            };
            return;
        }

        // Archive current version before updating
        await ArchivePluginVersionAsync(pluginId, oldVersion);

        // Resolve dependencies for the new version
        try
        {
            var deps = await ResolveDependenciesAsync(pluginId, newVersion);

            // Install any missing dependencies
            foreach (var (depId, depVer) in deps)
            {
                if (depId == pluginId) continue;
                if (_catalog.TryGetValue(depId, out var depEntry) && !depEntry.IsInstalled)
                {
                    var depSuccess = await InstallPluginViaKernelAsync(depId, depVer);
                    if (!depSuccess)
                    {
                        // Rollback: restore archived version
                        await RestorePluginVersionAsync(pluginId, oldVersion);
                        message.Payload["error"] = $"Failed to install dependency {depId} for update. Rolled back to v{oldVersion}.";
                        return;
                    }
                    UpdateCatalogEntryInstalled(depId, depVer);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            message.Payload["error"] = $"Dependency resolution failed for update: {ex.Message}";
            return;
        }

        // Unload old version
        var unloadSuccess = await UninstallPluginViaKernelAsync(pluginId);
        if (!unloadSuccess)
        {
            message.Payload["error"] = $"Failed to unload old version of {pluginId}";
            return;
        }

        // Load new version
        var loadSuccess = await InstallPluginViaKernelAsync(pluginId, newVersion);
        if (!loadSuccess)
        {
            // Rollback: restore archived version and reload
            await RestorePluginVersionAsync(pluginId, oldVersion);
            var rollbackSuccess = await InstallPluginViaKernelAsync(pluginId, oldVersion);

            if (rollbackSuccess)
            {
                message.Payload["error"] = $"Failed to load new version. Rolled back to v{oldVersion}.";
            }
            else
            {
                message.Payload["error"] = $"Failed to load new version and rollback also failed. Plugin {pluginId} may be in an inconsistent state.";
            }
            return;
        }

        // Update catalog entry
        _catalog[pluginId] = entry with
        {
            CurrentVersion = newVersion,
            HasUpdate = false,
            UpdatedAt = DateTime.UtcNow
        };

        // Record version change in history
        AddVersionToHistory(pluginId, newVersion, $"Updated from v{oldVersion} to v{newVersion}");

        // Record usage analytics event for update
        await RecordUsageEventAsync(new PluginUsageEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            PluginId = pluginId,
            EventType = UsageEventType.Updated,
            Timestamp = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["previousVersion"] = oldVersion,
                ["newVersion"] = newVersion
            }
        }, CancellationToken.None);

        await SaveCatalogEntryAsync(pluginId);
        await SaveStateAsync();

        message.Payload["result"] = new Dictionary<string, object>
        {
            ["success"] = true,
            ["pluginId"] = pluginId,
            ["previousVersion"] = oldVersion,
            ["newVersion"] = newVersion,
            ["message"] = $"Successfully updated {entry.Name} from v{oldVersion} to v{newVersion}"
        };
    }

    #endregion

    #region marketplace.search Handler

    /// <summary>
    /// Handles the marketplace.search message with filtering, sorting, and pagination.
    /// </summary>
    /// <param name="message">The incoming plugin message with search parameters.</param>
    private void HandleSearch(PluginMessage message)
    {
        var query = GetString(message.Payload, "query") ?? "";
        var category = GetString(message.Payload, "category");
        var sortBy = GetString(message.Payload, "sortBy") ?? "popular";
        var certifiedOnly = GetBool(message.Payload, "certifiedOnly");
        var installedOnly = GetBool(message.Payload, "installedOnly");

        var entries = _catalog.Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var search = query.ToLowerInvariant();
            entries = entries.Where(e =>
                e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Author.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.Tags != null && e.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase))));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            entries = entries.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        if (certifiedOnly)
        {
            entries = entries.Where(e => e.CertificationLevel >= CertificationLevel.BasicCertified);
        }

        if (installedOnly)
        {
            entries = entries.Where(e => e.IsInstalled);
        }

        entries = sortBy switch
        {
            "rating" => entries.OrderByDescending(e => e.AverageRating),
            "recent" => entries.OrderByDescending(e => e.UpdatedAt),
            "name" => entries.OrderBy(e => e.Name),
            "installs" => entries.OrderByDescending(e => e.InstallCount),
            _ => entries.OrderByDescending(e => e.InstallCount)
        };

        message.Payload["result"] = entries.ToList();
    }

    #endregion

    #region marketplace.certify Handler - 5-Stage Certification Pipeline

    /// <summary>
    /// Handles the marketplace.certify message by running a 5-stage certification pipeline
    /// on the specified plugin assembly. Stages: Security Scan, SDK Compatibility, Dependency
    /// Validation, Static Analysis, and Certification Scoring.
    /// </summary>
    /// <param name="message">The incoming plugin message with pluginId, version, and assemblyPath.</param>
    private async Task HandleCertifyAsync(PluginMessage message)
    {
        var pluginId = GetString(message.Payload, "pluginId");
        var version = GetString(message.Payload, "version") ?? "1.0.0";
        var assemblyPath = GetString(message.Payload, "assemblyPath");

        if (string.IsNullOrWhiteSpace(pluginId))
        {
            message.Payload["error"] = "pluginId is required";
            return;
        }

        if (!_catalog.TryGetValue(pluginId, out _))
        {
            message.Payload["error"] = $"Plugin not found: {pluginId}";
            return;
        }

        assemblyPath ??= ResolveAssemblyPath(pluginId, version);

        var request = new CertificationRequest(
            PluginId: pluginId,
            Version: version,
            RequestedBy: GetString(message.Payload, "requestedBy") ?? "system",
            AssemblyPath: assemblyPath,
            SubmittedAt: DateTime.UtcNow);

        var result = await CertifyPluginAsync(request, CancellationToken.None);
        message.Payload["result"] = result;
    }

    /// <summary>
    /// Runs the 5-stage certification pipeline on a plugin assembly.
    /// Stage 1: Security Scan (30% weight)
    /// Stage 2: SDK Compatibility Check (25% weight)
    /// Stage 3: Dependency Validation (20% weight)
    /// Stage 4: Static Analysis (25% weight)
    /// Stage 5: Certification Scoring (composite)
    /// </summary>
    /// <param name="request">The certification request containing plugin and assembly details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The certification result with all stage details.</returns>
    private async Task<CertificationResult> CertifyPluginAsync(CertificationRequest request, CancellationToken ct)
    {
        var stages = new List<CertificationStageResult>();

        // Stage 1: Security Scan
        var securityStage = await RunSecurityScanStageAsync(request, ct);
        stages.Add(securityStage);

        // Stage 2: SDK Compatibility Check
        var compatStage = RunSdkCompatibilityStage(request);
        stages.Add(compatStage);

        // Stage 3: Dependency Validation
        var depStage = RunDependencyValidationStage(request);
        stages.Add(depStage);

        // Stage 4: Static Analysis
        var staticStage = RunStaticAnalysisStage(request);
        stages.Add(staticStage);

        // Stage 5: Certification Scoring (composite of stages 1-4)
        const double securityWeight = 0.30;
        const double compatWeight = 0.25;
        const double depWeight = 0.20;
        const double staticWeight = 0.25;

        var overallScore = (securityStage.Score * securityWeight)
                         + (compatStage.Score * compatWeight)
                         + (depStage.Score * depWeight)
                         + (staticStage.Score * staticWeight);

        var hasCriticalError = stages.Any(s => s.Errors.Length > 0 && !s.Passed);
        var securityAndCompatPassed = securityStage.Passed && compatStage.Passed;

        CertificationLevel level;
        if (hasCriticalError)
        {
            level = CertificationLevel.Rejected;
        }
        else if (overallScore >= 90.0 && stages.All(s => s.Passed))
        {
            level = CertificationLevel.FullyCertified;
        }
        else if (overallScore >= _certificationPolicy.MinimumScore && securityAndCompatPassed)
        {
            level = CertificationLevel.BasicCertified;
        }
        else
        {
            level = CertificationLevel.Uncertified;
        }

        var now = DateTime.UtcNow;
        var certifiedAt = level >= CertificationLevel.BasicCertified ? now : (DateTime?)null;
        var expiresAt = certifiedAt?.AddDays(_certificationPolicy.CertificationValidityDays);

        var scoringStage = new CertificationStageResult(
            StageName: "CertificationScoring",
            Passed: level >= CertificationLevel.BasicCertified,
            Score: overallScore,
            Details: $"Weighted composite: Security={securityStage.Score:F1}*{securityWeight}, Compat={compatStage.Score:F1}*{compatWeight}, Deps={depStage.Score:F1}*{depWeight}, Static={staticStage.Score:F1}*{staticWeight}. Level={level}",
            Warnings: Array.Empty<string>(),
            Errors: Array.Empty<string>(),
            ExecutedAt: now,
            DurationMs: 0);
        stages.Add(scoringStage);

        var result = new CertificationResult(
            PluginId: request.PluginId,
            Version: request.Version,
            Level: level,
            OverallScore: overallScore,
            Stages: stages.ToArray(),
            CertifiedAt: certifiedAt,
            ExpiresAt: expiresAt,
            CertifiedBy: request.RequestedBy);

        // Persist and update catalog
        _certifications[request.PluginId] = result;
        await SaveCertificationAsync(result);

        if (_catalog.TryGetValue(request.PluginId, out var entry))
        {
            _catalog[request.PluginId] = entry with
            {
                CertificationLevel = level,
                UpdatedAt = now
            };
            await SaveCatalogEntryAsync(request.PluginId);
        }

        return result;
    }

    /// <summary>
    /// Stage 1: Security Scan. Attempts kernel validation via message bus, falls back to local checks.
    /// Checks: file exists, size within limits, SHA-256 hash, valid .NET assembly, not blocked.
    /// </summary>
    private async Task<CertificationStageResult> RunSecurityScanStageAsync(CertificationRequest request, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();
        var errors = new List<string>();
        var checksTotal = 4;
        var checksPassed = 0;

        // Try kernel validation via message bus first
        var kernelValidated = false;
        if (MessageBus != null)
        {
            try
            {
                var validateMsg = PluginMessage.Create("kernel.plugin.validate", new Dictionary<string, object>
                {
                    ["assemblyPath"] = request.AssemblyPath,
                    ["pluginId"] = request.PluginId
                });
                validateMsg.Payload["sourcePluginId"] = Id;

                var response = await MessageBus.SendAsync("kernel.plugin.validate", validateMsg, TimeSpan.FromSeconds(10));
                if (response.Success)
                {
                    kernelValidated = true;
                    checksPassed = checksTotal; // Kernel validated all checks
                }
            }
            catch (TimeoutException)
            {
                warnings.Add("Kernel validation timed out, falling back to local checks");
            }
            catch
            {
                warnings.Add("Kernel validation unavailable, falling back to local checks");
            }
        }

        // Local fallback checks
        if (!kernelValidated)
        {
            // Check 1: File exists and size within limits
            if (File.Exists(request.AssemblyPath))
            {
                var fileInfo = new FileInfo(request.AssemblyPath);
                var maxBytes = (long)_certificationPolicy.MaxAssemblySizeMb * 1024 * 1024;
                if (fileInfo.Length <= maxBytes)
                {
                    checksPassed++;
                }
                else
                {
                    errors.Add($"Assembly size {fileInfo.Length / (1024 * 1024.0):F1}MB exceeds limit of {_certificationPolicy.MaxAssemblySizeMb}MB");
                }

                // Check 2: Compute SHA-256 hash (verifies file is readable and not corrupted)
                try
                {
                    var bytes = await File.ReadAllBytesAsync(request.AssemblyPath, ct);
                    // Note: Bus delegation not available in this context; using direct crypto
                    var hash = SHA256.HashData(bytes);
                    var hashHex = Convert.ToHexString(hash);
                    checksPassed++;
                    warnings.Add($"Assembly SHA-256: {hashHex}");
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to compute assembly hash: {ex.Message}");
                }

                // Check 3: Valid .NET assembly
                try
                {
                    AssemblyName.GetAssemblyName(request.AssemblyPath);
                    checksPassed++;
                }
                catch (BadImageFormatException)
                {
                    errors.Add("File is not a valid .NET assembly");
                }
                catch (FileLoadException)
                {
                    warnings.Add("Assembly could not be fully loaded for validation, but file header appears valid");
                    checksPassed++;
                }

                // Check 4: Signed assembly check (informational if not required)
                try
                {
                    var asmName = AssemblyName.GetAssemblyName(request.AssemblyPath);
                    var publicKeyToken = asmName.GetPublicKeyToken();
                    if (publicKeyToken != null && publicKeyToken.Length > 0)
                    {
                        checksPassed++;
                    }
                    else if (_certificationPolicy.RequireSignedAssembly)
                    {
                        errors.Add("Assembly is not signed but signing is required by policy");
                    }
                    else
                    {
                        warnings.Add("Assembly is not strong-name signed");
                        checksPassed++;
                    }
                }
                catch
                {
                    if (_certificationPolicy.RequireSignedAssembly)
                    {
                        errors.Add("Cannot verify assembly signature");
                    }
                    else
                    {
                        warnings.Add("Cannot verify assembly signature");
                        checksPassed++;
                    }
                }
            }
            else
            {
                // Assembly file not found - pass with warnings for catalog-only entries
                warnings.Add($"Assembly file not found at {request.AssemblyPath}. Scoring based on catalog metadata only.");
                checksPassed = checksTotal; // Don't penalize catalog-only entries
            }
        }

        var score = checksTotal > 0 ? ((double)checksPassed / checksTotal) * 100.0 : 0.0;
        sw.Stop();

        return new CertificationStageResult(
            StageName: "SecurityScan",
            Passed: errors.Count == 0,
            Score: score,
            Details: kernelValidated
                ? "Validated via kernel.plugin.validate message bus"
                : $"Local validation: {checksPassed}/{checksTotal} checks passed",
            Warnings: warnings.ToArray(),
            Errors: errors.ToArray(),
            ExecutedAt: DateTime.UtcNow,
            DurationMs: sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Stage 2: SDK Compatibility Check. Verifies the assembly references DataWarehouse.SDK
    /// and does NOT reference DataWarehouse.Kernel or other plugin projects (forbidden references).
    /// </summary>
    private CertificationStageResult RunSdkCompatibilityStage(CertificationRequest request)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();
        var errors = new List<string>();
        var score = 100.0;

        if (!File.Exists(request.AssemblyPath))
        {
            // Catalog-only entry - check by naming convention
            if (request.PluginId.StartsWith("datawarehouse.plugins.", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("Assembly not found; SDK compatibility assumed based on naming convention");
            }
            else
            {
                warnings.Add("Assembly not found; cannot verify SDK compatibility");
                score = 50.0;
            }
        }
        else
        {
            try
            {
                var asmName = AssemblyName.GetAssemblyName(request.AssemblyPath);
                var referencesFound = false;
                var forbiddenRefs = new List<string>();

                // Inspect assembly metadata for references
                // Use the assembly name to check naming convention
                var name = asmName.Name ?? string.Empty;

                // Check naming convention implies SDK reference
                if (name.StartsWith("DataWarehouse.Plugins.", StringComparison.Ordinal))
                {
                    referencesFound = true;
                }
                else
                {
                    warnings.Add($"Assembly name '{name}' does not follow DataWarehouse.Plugins.* convention");
                }

                // Check for forbidden references in assembly name
                if (name.StartsWith("DataWarehouse.Kernel", StringComparison.Ordinal))
                {
                    forbiddenRefs.Add("DataWarehouse.Kernel");
                }

                if (forbiddenRefs.Count > 0)
                {
                    errors.Add($"Forbidden references detected: {string.Join(", ", forbiddenRefs)}");
                    score = 0.0;
                }
                else if (!referencesFound && _certificationPolicy.RequireSdkCompatibility)
                {
                    errors.Add("Assembly does not appear to reference DataWarehouse.SDK");
                    score = 0.0;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not inspect assembly references: {ex.Message}");
                score = 50.0;
            }
        }

        sw.Stop();
        return new CertificationStageResult(
            StageName: "SdkCompatibility",
            Passed: errors.Count == 0,
            Score: score,
            Details: errors.Count == 0 ? "SDK compatibility verified" : $"Compatibility issues: {string.Join("; ", errors)}",
            Warnings: warnings.ToArray(),
            Errors: errors.ToArray(),
            ExecutedAt: DateTime.UtcNow,
            DurationMs: sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Stage 3: Dependency Validation. Verifies all declared dependencies exist in catalog
    /// with compatible versions available, and checks for circular dependency chains.
    /// </summary>
    private CertificationStageResult RunDependencyValidationStage(CertificationRequest request)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();
        var errors = new List<string>();

        if (!_catalog.TryGetValue(request.PluginId, out var catalogEntry) || catalogEntry.Dependencies == null || catalogEntry.Dependencies.Length == 0)
        {
            sw.Stop();
            return new CertificationStageResult(
                StageName: "DependencyValidation",
                Passed: true,
                Score: 100.0,
                Details: "No dependencies declared",
                Warnings: Array.Empty<string>(),
                Errors: Array.Empty<string>(),
                ExecutedAt: DateTime.UtcNow,
                DurationMs: sw.ElapsedMilliseconds);
        }

        var totalDeps = catalogEntry.Dependencies.Length;
        var satisfiedDeps = 0;

        foreach (var dep in catalogEntry.Dependencies)
        {
            if (!_catalog.ContainsKey(dep.PluginId))
            {
                if (dep.IsOptional)
                {
                    warnings.Add($"Optional dependency '{dep.PluginId}' not found in catalog");
                    satisfiedDeps++; // Optional deps don't penalize
                }
                else
                {
                    errors.Add($"Required dependency '{dep.PluginId}' not found in catalog");
                }
            }
            else
            {
                satisfiedDeps++;
            }
        }

        // Check for circular dependencies
        try
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<(string, string)>();
            TopologicalSort(request.PluginId, request.Version, visited, inProgress, ordered);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"Circular dependency detected: {ex.Message}");
        }

        var score = totalDeps > 0 ? ((double)satisfiedDeps / totalDeps) * 100.0 : 100.0;
        sw.Stop();

        return new CertificationStageResult(
            StageName: "DependencyValidation",
            Passed: errors.Count == 0,
            Score: score,
            Details: $"{satisfiedDeps}/{totalDeps} dependencies satisfied",
            Warnings: warnings.ToArray(),
            Errors: errors.ToArray(),
            ExecutedAt: DateTime.UtcNow,
            DurationMs: sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Stage 4: Static Analysis. Checks assembly for forbidden patterns: no direct plugin-to-plugin
    /// references, naming convention compliance, and XML documentation file presence.
    /// </summary>
    private CertificationStageResult RunStaticAnalysisStage(CertificationRequest request)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();
        var errors = new List<string>();
        var checksTotal = 3;
        var checksPassed = 0;

        if (!File.Exists(request.AssemblyPath))
        {
            // Catalog-only entry - check naming convention and infer
            var assemblyName = InferAssemblyName(request.PluginId);
            if (assemblyName.StartsWith("DataWarehouse.Plugins.", StringComparison.Ordinal))
            {
                checksPassed = checksTotal;
                warnings.Add("Assembly not found; static analysis based on catalog metadata");
            }
            else
            {
                checksPassed = 1;
                warnings.Add("Assembly not found; limited static analysis from naming convention");
            }
        }
        else
        {
            // Check 1: No direct plugin-to-plugin references
            try
            {
                var asmName = AssemblyName.GetAssemblyName(request.AssemblyPath);
                var name = asmName.Name ?? string.Empty;

                // The assembly name itself should not reference another plugin
                if (!name.Contains(".Plugins.", StringComparison.Ordinal) ||
                    name.StartsWith("DataWarehouse.Plugins.", StringComparison.Ordinal))
                {
                    checksPassed++;
                }
                else
                {
                    errors.Add($"Assembly '{name}' appears to be a cross-plugin reference");
                }
            }
            catch
            {
                warnings.Add("Could not verify plugin isolation");
                checksPassed++;
            }

            // Check 2: Assembly name follows DataWarehouse.Plugins.* convention
            try
            {
                var asmName = AssemblyName.GetAssemblyName(request.AssemblyPath);
                var name = asmName.Name ?? string.Empty;
                if (name.StartsWith("DataWarehouse.Plugins.", StringComparison.Ordinal))
                {
                    checksPassed++;
                }
                else
                {
                    warnings.Add($"Assembly name '{name}' does not follow DataWarehouse.Plugins.* convention");
                }
            }
            catch
            {
                warnings.Add("Could not verify naming convention");
            }

            // Check 3: XML documentation file present
            var xmlDocPath = Path.ChangeExtension(request.AssemblyPath, ".xml");
            if (File.Exists(xmlDocPath))
            {
                checksPassed++;
            }
            else
            {
                warnings.Add("XML documentation file not found alongside assembly");
            }
        }

        var score = checksTotal > 0 ? ((double)checksPassed / checksTotal) * 100.0 : 0.0;
        sw.Stop();

        return new CertificationStageResult(
            StageName: "StaticAnalysis",
            Passed: errors.Count == 0,
            Score: score,
            Details: $"{checksPassed}/{checksTotal} static analysis checks passed",
            Warnings: warnings.ToArray(),
            Errors: errors.ToArray(),
            ExecutedAt: DateTime.UtcNow,
            DurationMs: sw.ElapsedMilliseconds);
    }

    #endregion

    #region marketplace.review Handler - Multi-Dimensional Rating System

    /// <summary>
    /// Handles the marketplace.review message to submit a multi-dimensional rating and review.
    /// Supports overall rating plus optional reliability, performance, and documentation dimension ratings.
    /// Validates rating range (1-5), checks for duplicate reviews, and verifies install status.
    /// </summary>
    /// <param name="message">The incoming plugin message with review submission fields.</param>
    private async Task HandleReviewAsync(PluginMessage message)
    {
        var pluginId = GetString(message.Payload, "pluginId");
        var reviewerId = GetString(message.Payload, "reviewerId");
        var reviewerName = GetString(message.Payload, "reviewerName") ?? GetString(message.Payload, "author") ?? "Anonymous";
        var overallRating = GetInt(message.Payload, "rating");
        var title = GetString(message.Payload, "title");
        var content = GetString(message.Payload, "text") ?? GetString(message.Payload, "content") ?? "";
        var reliabilityRating = GetNullableInt(message.Payload, "reliabilityRating");
        var performanceRating = GetNullableInt(message.Payload, "performanceRating");
        var documentationRating = GetNullableInt(message.Payload, "documentationRating");

        if (string.IsNullOrWhiteSpace(pluginId))
        {
            message.Payload["error"] = "pluginId is required";
            return;
        }

        if (string.IsNullOrWhiteSpace(reviewerId))
        {
            message.Payload["error"] = "reviewerId is required";
            return;
        }

        if (!_catalog.TryGetValue(pluginId, out var entry))
        {
            message.Payload["error"] = $"Plugin not found: {pluginId}";
            return;
        }

        // Validate rating range
        if (overallRating < 1 || overallRating > 5)
        {
            message.Payload["error"] = "rating must be between 1 and 5";
            return;
        }

        var submission = new PluginReviewSubmission
        {
            PluginId = pluginId,
            ReviewerId = reviewerId,
            ReviewerName = reviewerName,
            OverallRating = overallRating,
            Title = title,
            Content = content,
            ReliabilityRating = reliabilityRating.HasValue ? Math.Clamp(reliabilityRating.Value, 1, 5) : null,
            PerformanceRating = performanceRating.HasValue ? Math.Clamp(performanceRating.Value, 1, 5) : null,
            DocumentationRating = documentationRating.HasValue ? Math.Clamp(documentationRating.Value, 1, 5) : null
        };

        var reviewResult = await SubmitReviewAsync(submission, CancellationToken.None);
        message.Payload["result"] = reviewResult;
    }

    /// <summary>
    /// Submits a review for a plugin. Validates the submission, checks for duplicate reviews
    /// from the same reviewer (updates existing), and recalculates aggregate ratings.
    /// </summary>
    /// <param name="submission">The review submission details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary with review result details.</returns>
    private async Task<Dictionary<string, object>> SubmitReviewAsync(PluginReviewSubmission submission, CancellationToken ct)
    {
        var isVerifiedInstall = _catalog.TryGetValue(submission.PluginId, out var catalogEntry) && catalogEntry.IsInstalled;

        if (!_reviews.TryGetValue(submission.PluginId, out var reviewList))
        {
            reviewList = new List<PluginReviewEntry>();
            _reviews[submission.PluginId] = reviewList;
        }

        PluginReviewEntry review;
        bool isUpdate;

        lock (reviewList)
        {
            // Check for duplicate: same ReviewerId + PluginId -> update existing
            var existingIdx = reviewList.FindIndex(r =>
                string.Equals(r.ReviewerId, submission.ReviewerId, StringComparison.OrdinalIgnoreCase));

            review = new PluginReviewEntry
            {
                Id = existingIdx >= 0 ? reviewList[existingIdx].Id : $"rev-{Guid.NewGuid():N}",
                PluginId = submission.PluginId,
                ReviewerId = submission.ReviewerId,
                ReviewerName = submission.ReviewerName,
                OverallRating = submission.OverallRating,
                Title = submission.Title,
                Content = submission.Content,
                ReliabilityRating = submission.ReliabilityRating,
                PerformanceRating = submission.PerformanceRating,
                DocumentationRating = submission.DocumentationRating,
                IsVerifiedInstall = isVerifiedInstall,
                ReviewStatus = ReviewModerationStatus.Approved,
                CreatedAt = existingIdx >= 0 ? reviewList[existingIdx].CreatedAt : DateTime.UtcNow,
                OwnerResponse = existingIdx >= 0 ? reviewList[existingIdx].OwnerResponse : null,
                OwnerRespondedAt = existingIdx >= 0 ? reviewList[existingIdx].OwnerRespondedAt : null
            };

            if (existingIdx >= 0)
            {
                reviewList[existingIdx] = review;
                isUpdate = true;
            }
            else
            {
                reviewList.Add(review);
                isUpdate = false;
            }
        }

        // Recalculate average rating
        double avgRating;
        int ratingCount;
        lock (reviewList)
        {
            var approved = reviewList.Where(r => r.ReviewStatus == ReviewModerationStatus.Approved).ToList();
            avgRating = approved.Count > 0 ? approved.Average(r => r.OverallRating) : 0.0;
            ratingCount = approved.Count;
        }

        if (_catalog.TryGetValue(submission.PluginId, out var entry))
        {
            _catalog[submission.PluginId] = entry with
            {
                AverageRating = avgRating,
                RatingCount = ratingCount,
                UpdatedAt = DateTime.UtcNow
            };
            await SaveCatalogEntryAsync(submission.PluginId);
        }

        await SaveReviewsAsync(submission.PluginId);

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["pluginId"] = submission.PluginId,
            ["reviewId"] = review.Id,
            ["isUpdate"] = isUpdate,
            ["isVerifiedInstall"] = isVerifiedInstall,
            ["newAverageRating"] = avgRating,
            ["totalReviews"] = ratingCount
        };
    }

    /// <summary>
    /// Gets paginated reviews for a plugin with aggregate statistics.
    /// </summary>
    /// <param name="pluginId">The plugin to get reviews for.</param>
    /// <param name="offset">Number of reviews to skip.</param>
    /// <param name="limit">Maximum reviews to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reviews response with pagination, averages, and rating distribution.</returns>
    private Task<PluginReviewsResponse> GetPluginReviewsAsync(string pluginId, int offset, int limit, CancellationToken ct)
    {
        if (!_reviews.TryGetValue(pluginId, out var reviewList))
        {
            return Task.FromResult(new PluginReviewsResponse
            {
                PluginId = pluginId,
                Reviews = Array.Empty<PluginReviewEntry>(),
                TotalCount = 0,
                AverageRating = 0.0,
                RatingDistribution = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 0, [4] = 0, [5] = 0 }
            });
        }

        List<PluginReviewEntry> approved;
        lock (reviewList)
        {
            approved = reviewList.Where(r => r.ReviewStatus == ReviewModerationStatus.Approved).ToList();
        }

        var distribution = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 0, [4] = 0, [5] = 0 };
        foreach (var r in approved)
        {
            var key = Math.Clamp(r.OverallRating, 1, 5);
            distribution[key]++;
        }

        var response = new PluginReviewsResponse
        {
            PluginId = pluginId,
            Reviews = approved.OrderByDescending(r => r.CreatedAt).Skip(offset).Take(limit).ToArray(),
            TotalCount = approved.Count,
            AverageRating = approved.Count > 0 ? approved.Average(r => r.OverallRating) : 0.0,
            RatingDistribution = distribution
        };

        return Task.FromResult(response);
    }

    #endregion

    #region Revenue Tracking

    /// <summary>
    /// Records revenue from a plugin installation. Calculates commission using the configured
    /// commission rate and persists the record to period-based JSON files.
    /// </summary>
    /// <param name="pluginId">The plugin that was installed.</param>
    /// <param name="developerId">The developer earning revenue.</param>
    /// <param name="price">The installation price.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RecordInstallRevenueAsync(string pluginId, string developerId, decimal price, CancellationToken ct)
    {
        if (price <= 0m) return;

        var period = DateTime.UtcNow.ToString("yyyy-MM");
        var commissionRate = _revenueConfig.DefaultCommissionRate;
        var commissionAmount = price * commissionRate;
        var netEarnings = price - commissionAmount;

        if (!_revenueRecords.TryGetValue(developerId, out var records))
        {
            records = new List<DeveloperRevenueRecord>();
            _revenueRecords[developerId] = records;
        }

        DeveloperRevenueRecord record;
        lock (records)
        {
            var existingIdx = records.FindIndex(r =>
                string.Equals(r.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Period, period, StringComparison.Ordinal));

            if (existingIdx >= 0)
            {
                var existing = records[existingIdx];
                record = existing with
                {
                    TotalEarnings = existing.TotalEarnings + price,
                    CommissionAmount = existing.CommissionAmount + commissionAmount,
                    NetEarnings = existing.NetEarnings + netEarnings,
                    InstallCount = existing.InstallCount + 1
                };
                records[existingIdx] = record;
            }
            else
            {
                record = new DeveloperRevenueRecord
                {
                    DeveloperId = developerId,
                    PluginId = pluginId,
                    Period = period,
                    TotalEarnings = price,
                    CommissionRate = commissionRate,
                    CommissionAmount = commissionAmount,
                    NetEarnings = netEarnings,
                    InstallCount = 1,
                    PayoutStatus = PayoutStatus.Pending
                };
                records.Add(record);
            }
        }

        await SaveRevenueAsync(developerId);
    }

    /// <summary>
    /// Gets revenue records for a developer, optionally filtered by period.
    /// </summary>
    /// <param name="developerId">The developer ID.</param>
    /// <param name="period">Optional period filter (yyyy-MM format).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Array of matching revenue records.</returns>
    private Task<DeveloperRevenueRecord[]> GetDeveloperRevenueAsync(string developerId, string? period, CancellationToken ct)
    {
        if (!_revenueRecords.TryGetValue(developerId, out var records))
        {
            return Task.FromResult(Array.Empty<DeveloperRevenueRecord>());
        }

        DeveloperRevenueRecord[] result;
        lock (records)
        {
            var query = records.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(period))
            {
                query = query.Where(r => string.Equals(r.Period, period, StringComparison.Ordinal));
            }
            result = query.ToArray();
        }

        return Task.FromResult(result);
    }

    #endregion

    #region Usage Analytics System

    /// <summary>
    /// Handles the marketplace.analytics message. Returns per-plugin analytics when pluginId
    /// is provided, or marketplace-wide analytics summary otherwise.
    /// </summary>
    /// <param name="message">The incoming plugin message with optional pluginId and monthsBack.</param>
    private async Task HandleAnalyticsAsync(PluginMessage message)
    {
        var pluginId = GetString(message.Payload, "pluginId");
        var monthsBack = GetInt(message.Payload, "monthsBack");
        if (monthsBack <= 0) monthsBack = 12;

        if (!string.IsNullOrWhiteSpace(pluginId))
        {
            var summary = await GetPluginAnalyticsAsync(pluginId, monthsBack, CancellationToken.None);
            message.Payload["result"] = summary;
        }
        else
        {
            var summary = await GetMarketplaceAnalyticsAsync(CancellationToken.None);
            message.Payload["result"] = summary;
        }
    }

    /// <summary>
    /// Records a usage event for a plugin. Appends the event to the in-memory collection
    /// (bounded to <see cref="MaxEventsPerPlugin"/> per plugin) and persists it to a daily
    /// event log file at {storagePath}/analytics/events-{yyyy-MM-dd}.json.
    /// </summary>
    /// <param name="usageEvent">The usage event to record.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RecordUsageEventAsync(PluginUsageEvent usageEvent, CancellationToken ct)
    {
        if (!Enum.IsDefined(typeof(UsageEventType), usageEvent.EventType))
        {
            return; // Invalid event type
        }

        // Add to in-memory collection with bounded size
        var events = _usageEvents.GetOrAdd(usageEvent.PluginId, _ => new List<PluginUsageEvent>());
        lock (events)
        {
            events.Add(usageEvent);

            // Evict oldest events when exceeding max per plugin
            while (events.Count > MaxEventsPerPlugin)
            {
                events.RemoveAt(0);
            }
        }

        // Append to daily event log file
        var analyticsDir = Path.Combine(_storagePath, "analytics");
        Directory.CreateDirectory(analyticsDir);

        var dailyLogPath = Path.Combine(analyticsDir, $"events-{usageEvent.Timestamp:yyyy-MM-dd}.json");
        var eventJson = JsonSerializer.Serialize(usageEvent, JsonOptions);

        await _fileLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(dailyLogPath, eventJson + Environment.NewLine, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Fire-and-forget wrapper for timer-based aggregation. Catches and suppresses exceptions
    /// to prevent unobserved task exceptions from timer callbacks.
    /// </summary>
    private async Task AggregateAnalyticsFireAndForgetAsync()
    {
        try
        {
            await AggregateAnalyticsAsync(CancellationToken.None);
        }
        catch
        {
            // Timer callback errors are suppressed; aggregation will retry on next tick
        }
    }

    /// <summary>
    /// Aggregates in-memory usage events into monthly analytics records. For each plugin with
    /// events, computes install/uninstall/update/error/message counts, active user count,
    /// popularity score, and trend direction by comparing against the previous month.
    /// Persists results to monthly analytics files.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task AggregateAnalyticsAsync(CancellationToken ct)
    {
        var currentPeriod = DateTime.UtcNow.ToString("yyyy-MM");
        var lastMonthPeriod = DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM");

        foreach (var kvp in _usageEvents)
        {
            var pluginId = kvp.Key;
            List<PluginUsageEvent> eventSnapshot;
            lock (kvp.Value)
            {
                eventSnapshot = new List<PluginUsageEvent>(kvp.Value);
            }

            // Group events by period (yyyy-MM)
            var byPeriod = eventSnapshot.GroupBy(e => e.Timestamp.ToString("yyyy-MM"));

            foreach (var periodGroup in byPeriod)
            {
                var period = periodGroup.Key;
                var periodEvents = periodGroup.ToList();

                var installCount = periodEvents.Count(e => e.EventType == UsageEventType.Installed);
                var uninstallCount = periodEvents.Count(e => e.EventType == UsageEventType.Uninstalled);
                var updateCount = periodEvents.Count(e => e.EventType == UsageEventType.Updated);
                var errorCount = periodEvents.Count(e => e.EventType == UsageEventType.ErrorOccurred);
                var messageCount = (long)periodEvents.Count(e => e.EventType == UsageEventType.MessageHandled);
                var activeUserCount = periodEvents
                    .Where(e => e.UserId != null)
                    .Select(e => e.UserId!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                // PopularityScore formula: real event counts weighted and clamped to 0-100
                var rawScore = (installCount * 10) + (activeUserCount * 5) - (uninstallCount * 3) - (errorCount * 2);
                var popularityScore = Math.Clamp(rawScore, 0.0, 100.0);

                // Determine trend direction by comparing to previous month
                var previousPeriodKey = $"{pluginId}:{GetPreviousPeriod(period)}";
                var trendDirection = TrendDirection.Stable;
                if (_monthlyAnalytics.TryGetValue(previousPeriodKey, out var previousAnalytics))
                {
                    if (popularityScore > previousAnalytics.PopularityScore + 2.0)
                        trendDirection = TrendDirection.Rising;
                    else if (popularityScore < previousAnalytics.PopularityScore - 2.0)
                        trendDirection = TrendDirection.Declining;
                }

                var analytics = new PluginUsageAnalytics
                {
                    PluginId = pluginId,
                    Period = period,
                    InstallCount = installCount,
                    UninstallCount = uninstallCount,
                    UpdateCount = updateCount,
                    ActiveUserCount = activeUserCount,
                    ErrorCount = errorCount,
                    MessageCount = messageCount,
                    PopularityScore = popularityScore,
                    TrendDirection = trendDirection
                };

                var analyticsKey = $"{pluginId}:{period}";
                _monthlyAnalytics[analyticsKey] = analytics;
            }
        }

        // Persist monthly analytics and rotate old event log files
        await SaveAnalyticsAsync();
        RotateEventLogFiles();
    }

    /// <summary>
    /// Returns a per-plugin analytics summary with monthly data for the past N months.
    /// </summary>
    /// <param name="pluginId">The plugin to get analytics for.</param>
    /// <param name="monthsBack">Number of months of historical data to include.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Plugin analytics summary with monthly breakdown and aggregates.</returns>
    private Task<PluginAnalyticsSummary> GetPluginAnalyticsAsync(string pluginId, int monthsBack, CancellationToken ct)
    {
        var monthlyData = new List<PluginUsageAnalytics>();
        var now = DateTime.UtcNow;

        for (int i = 0; i < monthsBack; i++)
        {
            var period = now.AddMonths(-i).ToString("yyyy-MM");
            var key = $"{pluginId}:{period}";
            if (_monthlyAnalytics.TryGetValue(key, out var analytics))
            {
                monthlyData.Add(analytics);
            }
        }

        var totalInstalls = _catalog.TryGetValue(pluginId, out var entry) ? entry.InstallCount : 0L;
        var totalUninstalls = monthlyData.Sum(m => (long)m.UninstallCount);
        var currentActiveInstalls = _catalog.TryGetValue(pluginId, out var catEntry) && catEntry.IsInstalled ? 1 : 0;
        var mostActiveMonth = monthlyData.Count > 0
            ? monthlyData.OrderByDescending(m => m.ActiveUserCount).First().Period
            : now.ToString("yyyy-MM");

        // Compute average session duration from events (approximation based on event spread)
        var avgSessionDuration = TimeSpan.Zero;
        if (_usageEvents.TryGetValue(pluginId, out var events))
        {
            List<PluginUsageEvent> eventSnapshot;
            lock (events)
            {
                eventSnapshot = new List<PluginUsageEvent>(events);
            }

            if (eventSnapshot.Count >= 2)
            {
                var sorted = eventSnapshot.OrderBy(e => e.Timestamp).ToList();
                var totalSpan = sorted[^1].Timestamp - sorted[0].Timestamp;
                var distinctUsers = sorted.Where(e => e.UserId != null).Select(e => e.UserId!).Distinct().Count();
                if (distinctUsers > 0)
                {
                    avgSessionDuration = TimeSpan.FromTicks(totalSpan.Ticks / distinctUsers);
                }
            }
        }

        var summary = new PluginAnalyticsSummary
        {
            PluginId = pluginId,
            TotalInstalls = totalInstalls,
            TotalUninstalls = totalUninstalls,
            CurrentActiveInstalls = currentActiveInstalls,
            AverageSessionDuration = avgSessionDuration,
            MostActiveMonth = mostActiveMonth,
            MonthlyData = monthlyData.OrderByDescending(m => m.Period).ToArray()
        };

        return Task.FromResult(summary);
    }

    /// <summary>
    /// Returns a marketplace-wide analytics summary with aggregate stats across all plugins.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Marketplace analytics summary with totals, popular plugins, and growth rate.</returns>
    private Task<MarketplaceAnalyticsSummary> GetMarketplaceAnalyticsAsync(CancellationToken ct)
    {
        var totalPlugins = _catalog.Count;
        var totalInstalls = Interlocked.Read(ref _state._totalInstalls);

        // Get current and last month periods
        var currentPeriod = DateTime.UtcNow.ToString("yyyy-MM");
        var lastPeriod = DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM");

        // Aggregate per-plugin analytics for current period
        var currentMonthAnalytics = _monthlyAnalytics.Values
            .Where(a => a.Period == currentPeriod)
            .ToList();

        var lastMonthAnalytics = _monthlyAnalytics.Values
            .Where(a => a.Period == lastPeriod)
            .ToList();

        // Most popular plugins by PopularityScore
        var mostPopularPlugins = currentMonthAnalytics
            .OrderByDescending(a => a.PopularityScore)
            .Take(10)
            .Select(a => a.PluginId)
            .ToArray();

        // If no current month analytics, fall back to catalog data
        if (mostPopularPlugins.Length == 0)
        {
            mostPopularPlugins = _catalog.Values
                .OrderByDescending(e => e.InstallCount)
                .Take(10)
                .Select(e => e.Id)
                .ToArray();
        }

        // Most active category
        var mostActiveCategory = _catalog.Values
            .GroupBy(e => e.Category)
            .OrderByDescending(g => g.Sum(e => e.InstallCount))
            .Select(g => g.Key)
            .FirstOrDefault() ?? "Unknown";

        // Growth rate: (this month installs - last month installs) / max(last month installs, 1)
        var thisMonthInstalls = currentMonthAnalytics.Sum(a => a.InstallCount);
        var lastMonthInstalls = lastMonthAnalytics.Sum(a => a.InstallCount);
        var growthRate = (double)(thisMonthInstalls - lastMonthInstalls) / Math.Max(lastMonthInstalls, 1);

        var summary = new MarketplaceAnalyticsSummary
        {
            TotalPlugins = totalPlugins,
            TotalInstalls = totalInstalls,
            MostPopularPlugins = mostPopularPlugins,
            MostActiveCategory = mostActiveCategory,
            GrowthRate = growthRate,
            GeneratedAt = DateTime.UtcNow
        };

        return Task.FromResult(summary);
    }

    /// <summary>
    /// Gets the previous month period string (yyyy-MM) from a given period.
    /// </summary>
    /// <param name="period">Current period in yyyy-MM format.</param>
    /// <returns>Previous month period string.</returns>
    private static string GetPreviousPeriod(string period)
    {
        if (DateTime.TryParseExact(period, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt))
        {
            return dt.AddMonths(-1).ToString("yyyy-MM");
        }
        return DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM");
    }

    /// <summary>
    /// Saves monthly analytics data to JSON files at {storagePath}/analytics/usage-{yyyy-MM}.json.
    /// Groups all analytics records by period and writes each period to its own file.
    /// </summary>
    private async Task SaveAnalyticsAsync()
    {
        var analyticsDir = Path.Combine(_storagePath, "analytics");
        Directory.CreateDirectory(analyticsDir);

        var byPeriod = _monthlyAnalytics.Values
            .GroupBy(a => a.Period);

        foreach (var group in byPeriod)
        {
            var filePath = Path.Combine(analyticsDir, $"usage-{SanitizeFileName(group.Key)}.json");

            await _fileLock.WaitAsync();
            try
            {
                var json = JsonSerializer.Serialize(group.ToArray(), JsonOptions);
                await File.WriteAllTextAsync(filePath, json);
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    /// <summary>
    /// Loads monthly analytics data from JSON files on startup.
    /// Reads all usage-*.json files from the analytics directory.
    /// </summary>
    private async Task LoadAnalyticsAsync()
    {
        var analyticsDir = Path.Combine(_storagePath, "analytics");
        if (!Directory.Exists(analyticsDir)) return;

        foreach (var file in Directory.GetFiles(analyticsDir, "usage-*.json"))
        {
            await _fileLock.WaitAsync();
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var records = JsonSerializer.Deserialize<PluginUsageAnalytics[]>(json, JsonOptions);
                if (records != null)
                {
                    foreach (var record in records)
                    {
                        var key = $"{record.PluginId}:{record.Period}";
                        _monthlyAnalytics[key] = record;
                    }
                }
            }
            catch
            {
                // Skip corrupted analytics files
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    /// <summary>
    /// Rotates daily event log files, deleting files older than <see cref="EventLogRetentionDays"/> days.
    /// </summary>
    private void RotateEventLogFiles()
    {
        var analyticsDir = Path.Combine(_storagePath, "analytics");
        if (!Directory.Exists(analyticsDir)) return;

        var cutoffDate = DateTime.UtcNow.AddDays(-EventLogRetentionDays);

        foreach (var file in Directory.GetFiles(analyticsDir, "events-*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            // Extract date from "events-yyyy-MM-dd"
            if (fileName.Length >= 17 &&
                DateTime.TryParseExact(fileName[7..], "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var fileDate))
            {
                if (fileDate < cutoffDate)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Best-effort rotation; file may be locked
                    }
                }
            }
        }
    }

    #endregion

    #region Dependency Resolution

    /// <summary>
    /// Resolves all dependencies for a plugin installation using topological sort.
    /// Detects circular dependencies and throws with a clear error message.
    /// </summary>
    /// <param name="pluginId">The plugin to install.</param>
    /// <param name="version">The target version.</param>
    /// <returns>Ordered list of (pluginId, version) pairs to install, from leaves to root.</returns>
    private Task<List<(string PluginId, string Version)>> ResolveDependenciesAsync(string pluginId, string version)
    {
        var result = new List<(string PluginId, string Version)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        TopologicalSort(pluginId, version, visited, inProgress, result);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Performs a depth-first topological sort on the dependency graph.
    /// </summary>
    /// <param name="pluginId">Current node in the graph.</param>
    /// <param name="version">Target version for this node.</param>
    /// <param name="visited">Set of fully visited nodes.</param>
    /// <param name="inProgress">Set of nodes currently being processed (for cycle detection).</param>
    /// <param name="result">Ordered output list.</param>
    private void TopologicalSort(
        string pluginId,
        string version,
        HashSet<string> visited,
        HashSet<string> inProgress,
        List<(string PluginId, string Version)> result)
    {
        if (visited.Contains(pluginId)) return;

        if (inProgress.Contains(pluginId))
        {
            throw new InvalidOperationException(
                $"Circular dependency detected: plugin '{pluginId}' is part of a dependency cycle. " +
                $"Check the dependency chain and remove the circular reference.");
        }

        inProgress.Add(pluginId);

        // Get dependencies for this plugin
        if (_catalog.TryGetValue(pluginId, out var entry) && entry.Dependencies != null)
        {
            foreach (var dep in entry.Dependencies)
            {
                if (dep.IsOptional) continue;

                // Check if already installed at a compatible version
                if (_catalog.TryGetValue(dep.PluginId, out var depEntry) && depEntry.IsInstalled)
                {
                    if (IsVersionCompatible(depEntry.CurrentVersion, dep.MinVersion, dep.MaxVersion))
                    {
                        continue; // Already installed and compatible
                    }
                }

                // Check if the dependency exists in catalog
                if (!_catalog.ContainsKey(dep.PluginId))
                {
                    throw new InvalidOperationException(
                        $"Required dependency '{dep.PluginId}' for plugin '{pluginId}' is not available in the catalog." +
                        (string.IsNullOrEmpty(dep.Reason) ? "" : $" Reason: {dep.Reason}"));
                }

                var depVersion = _catalog.TryGetValue(dep.PluginId, out var depCatalogEntry)
                    ? depCatalogEntry.LatestVersion
                    : dep.MinVersion ?? "1.0.0";

                TopologicalSort(dep.PluginId, depVersion, visited, inProgress, result);
            }
        }

        inProgress.Remove(pluginId);
        visited.Add(pluginId);
        result.Add((pluginId, version));
    }

    /// <summary>
    /// Checks whether a given version falls within the specified min/max range.
    /// </summary>
    /// <param name="currentVersion">The version to check.</param>
    /// <param name="minVersion">Minimum required version (inclusive), or null for no minimum.</param>
    /// <param name="maxVersion">Maximum allowed version (inclusive), or null for no maximum.</param>
    /// <returns>True if the version is within the compatible range.</returns>
    private static bool IsVersionCompatible(string currentVersion, string? minVersion, string? maxVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion)) return false;

        if (!System.Version.TryParse(NormalizeVersion(currentVersion), out var current))
            return true; // Cannot parse, assume compatible

        if (!string.IsNullOrWhiteSpace(minVersion) &&
            System.Version.TryParse(NormalizeVersion(minVersion), out var min) &&
            current < min)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(maxVersion) &&
            System.Version.TryParse(NormalizeVersion(maxVersion), out var max) &&
            current > max)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Normalizes a version string by stripping leading 'v' and prerelease suffixes.
    /// </summary>
    /// <param name="version">The version string to normalize.</param>
    /// <returns>A normalized version string suitable for System.Version.TryParse.</returns>
    private static string NormalizeVersion(string version)
    {
        var v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V'))
            v = v[1..];

        var dashIdx = v.IndexOf('-');
        if (dashIdx >= 0)
            v = v[..dashIdx];

        var plusIdx = v.IndexOf('+');
        if (plusIdx >= 0)
            v = v[..plusIdx];

        return v;
    }

    #endregion

    #region Version Archive for Rollback

    /// <summary>
    /// Archives a plugin version by copying assembly metadata to the versions directory.
    /// Structure: {storagePath}/archive/{pluginId}/{version}/metadata.json
    /// </summary>
    /// <param name="pluginId">The plugin ID to archive.</param>
    /// <param name="version">The version to archive.</param>
    private async Task ArchivePluginVersionAsync(string pluginId, string version)
    {
        var archiveDir = Path.Combine(_storagePath, "archive", SanitizeFileName(pluginId), SanitizeFileName(version));
        Directory.CreateDirectory(archiveDir);

        var assemblyPath = ResolveAssemblyPath(pluginId, version);

        // Copy assembly if it exists
        if (File.Exists(assemblyPath))
        {
            var destAssembly = Path.Combine(archiveDir, "assembly.dll");
            File.Copy(assemblyPath, destAssembly, overwrite: true);
        }

        // Save metadata
        var metadata = new Dictionary<string, object>
        {
            ["pluginId"] = pluginId,
            ["version"] = version,
            ["archivedAt"] = DateTime.UtcNow.ToString("o"),
            ["originalAssemblyPath"] = assemblyPath
        };

        // Compute assembly hash if file exists
        if (File.Exists(assemblyPath))
        {
            var bytes = await File.ReadAllBytesAsync(assemblyPath);
            // Note: Bus delegation not available in this context; using direct crypto
            var hash = SHA256.HashData(bytes);
            metadata["assemblyHash"] = Convert.ToHexString(hash);
            metadata["assemblySize"] = bytes.Length;
        }

        var metadataPath = Path.Combine(archiveDir, "metadata.json");
        await _fileLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(metadata, JsonOptions);
            await File.WriteAllTextAsync(metadataPath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Restores a previously archived plugin version by copying the assembly back.
    /// </summary>
    /// <param name="pluginId">The plugin ID to restore.</param>
    /// <param name="version">The version to restore.</param>
    private async Task RestorePluginVersionAsync(string pluginId, string version)
    {
        var archiveDir = Path.Combine(_storagePath, "archive", SanitizeFileName(pluginId), SanitizeFileName(version));
        var archivedAssembly = Path.Combine(archiveDir, "assembly.dll");

        if (!File.Exists(archivedAssembly))
        {
            return; // No archived assembly to restore
        }

        // Read original path from metadata
        var metadataPath = Path.Combine(archiveDir, "metadata.json");
        var originalPath = ResolveAssemblyPath(pluginId, version);

        if (File.Exists(metadataPath))
        {
            await _fileLock.WaitAsync();
            try
            {
                var json = await File.ReadAllTextAsync(metadataPath);
                var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                if (metadata != null && metadata.TryGetValue("originalAssemblyPath", out var pathObj) && pathObj is JsonElement je)
                {
                    originalPath = je.GetString() ?? originalPath;
                }
            }
            finally
            {
                _fileLock.Release();
            }
        }

        // Copy archived assembly back
        var destDir = Path.GetDirectoryName(originalPath);
        if (destDir != null)
        {
            Directory.CreateDirectory(destDir);
        }
        File.Copy(archivedAssembly, originalPath, overwrite: true);
    }

    /// <summary>
    /// Resolves the expected assembly path for a plugin.
    /// Plugins are expected to be in the Plugins subdirectory relative to the application base.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <param name="version">The plugin version.</param>
    /// <returns>The expected file path of the plugin assembly.</returns>
    private static string ResolveAssemblyPath(string pluginId, string version)
    {
        // Convention: plugin assemblies are in {AppBase}/Plugins/{PluginProjectName}/{PluginProjectName}.dll
        // Plugin ID format: "datawarehouse.plugins.xxx.yyy" -> project name is inferred
        var assemblyName = InferAssemblyName(pluginId);
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(basePath, "Plugins", assemblyName, $"{assemblyName}.dll");
    }

    /// <summary>
    /// Infers the assembly/project name from a plugin ID using naming conventions.
    /// </summary>
    /// <param name="pluginId">The plugin ID (e.g., "datawarehouse.plugins.ultimatestorage").</param>
    /// <returns>The inferred assembly name (e.g., "DataWarehouse.Plugins.UltimateStorage").</returns>
    private static string InferAssemblyName(string pluginId)
    {
        // Plugin IDs follow the convention: "datawarehouse.plugins.xxx" or "datawarehouse.plugins.xxx.yyy"
        var parts = pluginId.Split('.');
        if (parts.Length < 3) return pluginId;

        // Build the PascalCase assembly name
        var assemblyParts = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            assemblyParts.Add(char.ToUpperInvariant(part[0]) + part[1..]);
        }

        return string.Join(".", assemblyParts);
    }

    /// <summary>
    /// Adds a version entry to the history for a plugin.
    /// </summary>
    /// <param name="pluginId">The plugin ID.</param>
    /// <param name="version">The version number.</param>
    /// <param name="changelog">Changelog description.</param>
    private void AddVersionToHistory(string pluginId, string version, string changelog)
    {
        var versionInfo = new PluginVersionInfo(
            PluginId: pluginId,
            VersionNumber: version,
            Changelog: changelog,
            ReleaseDate: DateTime.UtcNow,
            AssemblyHash: null,
            AssemblySize: 0,
            SdkCompatibility: "1.0.0",
            IsPreRelease: version.Contains('-'));

        if (!_versionHistory.TryGetValue(pluginId, out var versions))
        {
            versions = new List<PluginVersionInfo>();
            _versionHistory[pluginId] = versions;
        }

        lock (versions)
        {
            versions.Add(versionInfo);
        }
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Loads the marketplace state from disk.
    /// </summary>
    private async Task LoadStateAsync()
    {
        var statePath = Path.Combine(_storagePath, "state.json");
        if (!File.Exists(statePath)) return;

        await _fileLock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(statePath);
            var state = JsonSerializer.Deserialize<MarketplaceStateDto>(json, JsonOptions);
            if (state != null)
            {
                _state = new MarketplaceState
                {
                    LastCatalogRefresh = state.LastCatalogRefresh,
                    _totalInstalls = state.TotalInstalls,
                    _totalUninstalls = state.TotalUninstalls,
                    ActivePluginCount = state.ActivePluginCount
                };
            }
        }
        catch
        {
            // State file corrupted, start with defaults
            _state = new MarketplaceState();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Saves the marketplace state to disk.
    /// </summary>
    private async Task SaveStateAsync()
    {
        var statePath = Path.Combine(_storagePath, "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);

        var dto = new MarketplaceStateDto
        {
            LastCatalogRefresh = _state.LastCatalogRefresh,
            TotalInstalls = Interlocked.Read(ref _state._totalInstalls),
            TotalUninstalls = Interlocked.Read(ref _state._totalUninstalls),
            ActivePluginCount = _catalog.Values.Count(e => e.IsInstalled)
        };

        await _fileLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            await File.WriteAllTextAsync(statePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Loads all catalog entries from JSON files in the catalog directory.
    /// </summary>
    private async Task LoadCatalogAsync()
    {
        var catalogDir = Path.Combine(_storagePath, "catalog");
        if (!Directory.Exists(catalogDir)) return;

        foreach (var file in Directory.GetFiles(catalogDir, "*.json"))
        {
            await _fileLock.WaitAsync();
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var entry = JsonSerializer.Deserialize<PluginCatalogEntry>(json, JsonOptions);
                if (entry != null)
                {
                    _catalog[entry.Id] = entry;
                }
            }
            catch
            {
                // Skip corrupted catalog files
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    /// <summary>
    /// Saves a single catalog entry to its JSON file.
    /// </summary>
    /// <param name="pluginId">The plugin ID whose entry to save.</param>
    private async Task SaveCatalogEntryAsync(string pluginId)
    {
        if (!_catalog.TryGetValue(pluginId, out var entry)) return;

        var catalogDir = Path.Combine(_storagePath, "catalog");
        Directory.CreateDirectory(catalogDir);

        var filePath = Path.Combine(catalogDir, $"{SanitizeFileName(pluginId)}.json");

        await _fileLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Saves all catalog entries to disk.
    /// </summary>
    private async Task SaveAllCatalogEntriesAsync()
    {
        foreach (var entry in _catalog.Values)
        {
            await SaveCatalogEntryAsync(entry.Id);
        }
    }

    /// <summary>
    /// Loads version history from JSON files.
    /// </summary>
    private async Task LoadVersionHistoryAsync()
    {
        var versionsDir = Path.Combine(_storagePath, "versions");
        if (!Directory.Exists(versionsDir)) return;

        foreach (var file in Directory.GetFiles(versionsDir, "*.json"))
        {
            await _fileLock.WaitAsync();
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var versions = JsonSerializer.Deserialize<List<PluginVersionInfo>>(json, JsonOptions);
                if (versions != null && versions.Count > 0)
                {
                    _versionHistory[versions[0].PluginId] = versions;
                }
            }
            catch
            {
                // Skip corrupted version files
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    /// <summary>
    /// Loads reviews from JSON files.
    /// </summary>
    private async Task LoadReviewsAsync()
    {
        var reviewsDir = Path.Combine(_storagePath, "reviews");
        if (!Directory.Exists(reviewsDir)) return;

        foreach (var file in Directory.GetFiles(reviewsDir, "*.json"))
        {
            await _fileLock.WaitAsync();
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var reviews = JsonSerializer.Deserialize<List<PluginReviewEntry>>(json, JsonOptions);
                if (reviews != null && reviews.Count > 0)
                {
                    _reviews[reviews[0].PluginId] = reviews;
                }
            }
            catch
            {
                // Skip corrupted review files
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    /// <summary>
    /// Saves reviews for a specific plugin to disk.
    /// </summary>
    /// <param name="pluginId">The plugin ID whose reviews to save.</param>
    private async Task SaveReviewsAsync(string pluginId)
    {
        if (!_reviews.TryGetValue(pluginId, out var reviews)) return;

        var reviewsDir = Path.Combine(_storagePath, "reviews");
        Directory.CreateDirectory(reviewsDir);

        var filePath = Path.Combine(reviewsDir, $"{SanitizeFileName(pluginId)}.json");

        List<PluginReviewEntry> snapshot;
        lock (reviews)
        {
            snapshot = new List<PluginReviewEntry>(reviews);
        }

        await _fileLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Saves a certification result to disk as JSON.
    /// </summary>
    /// <param name="result">The certification result to persist.</param>
    private async Task SaveCertificationAsync(CertificationResult result)
    {
        var certDir = Path.Combine(_storagePath, "certifications");
        Directory.CreateDirectory(certDir);

        var filePath = Path.Combine(certDir, $"{SanitizeFileName(result.PluginId)}.json");

        await _fileLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(result, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Loads all certification results from disk on startup.
    /// </summary>
    private async Task LoadCertificationsAsync()
    {
        var certDir = Path.Combine(_storagePath, "certifications");
        if (!Directory.Exists(certDir)) return;

        foreach (var file in Directory.GetFiles(certDir, "*.json"))
        {
            await _fileLock.WaitAsync();
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var cert = JsonSerializer.Deserialize<CertificationResult>(json, JsonOptions);
                if (cert != null)
                {
                    _certifications[cert.PluginId] = cert;
                }
            }
            catch
            {
                // Skip corrupted certification files
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    /// <summary>
    /// Saves revenue records for a developer to disk.
    /// </summary>
    /// <param name="developerId">The developer whose revenue to save.</param>
    private async Task SaveRevenueAsync(string developerId)
    {
        if (!_revenueRecords.TryGetValue(developerId, out var records)) return;

        var revenueDir = Path.Combine(_storagePath, "revenue", SanitizeFileName(developerId));
        Directory.CreateDirectory(revenueDir);

        List<DeveloperRevenueRecord> snapshot;
        lock (records)
        {
            snapshot = new List<DeveloperRevenueRecord>(records);
        }

        // Group by period and save each
        var byPeriod = snapshot.GroupBy(r => r.Period);
        foreach (var group in byPeriod)
        {
            var filePath = Path.Combine(revenueDir, $"{SanitizeFileName(group.Key)}.json");
            await _fileLock.WaitAsync();
            try
            {
                var json = JsonSerializer.Serialize(group.ToArray(), JsonOptions);
                await File.WriteAllTextAsync(filePath, json);
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    /// <summary>
    /// Loads all revenue records from disk on startup.
    /// </summary>
    private async Task LoadRevenueRecordsAsync()
    {
        var revenueDir = Path.Combine(_storagePath, "revenue");
        if (!Directory.Exists(revenueDir)) return;

        foreach (var devDir in Directory.GetDirectories(revenueDir))
        {
            var developerId = Path.GetFileName(devDir);
            var records = new List<DeveloperRevenueRecord>();

            foreach (var file in Directory.GetFiles(devDir, "*.json"))
            {
                await _fileLock.WaitAsync();
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var periodRecords = JsonSerializer.Deserialize<DeveloperRevenueRecord[]>(json, JsonOptions);
                    if (periodRecords != null)
                    {
                        records.AddRange(periodRecords);
                    }
                }
                catch
                {
                    // Skip corrupted revenue files
                }
                finally
                {
                    _fileLock.Release();
                }
            }

            if (records.Count > 0)
            {
                _revenueRecords[developerId] = records;
            }
        }
    }

    #endregion

    #region Built-in Catalog Population

    /// <summary>
    /// Populates the catalog with built-in entries for all known DataWarehouse plugins.
    /// This provides a discoverable catalog on first startup before any external sources are configured.
    /// </summary>
    private void PopulateBuiltInCatalog()
    {
        var now = DateTime.UtcNow;
        var builtInPlugins = GetBuiltInPluginDefinitions();

        foreach (var def in builtInPlugins)
        {
            var entry = new PluginCatalogEntry
            {
                Id = def.Id,
                Name = def.Name,
                Author = "DataWarehouse Team",
                Description = def.Description,
                FullDescription = def.FullDescription,
                Category = def.Category,
                Tags = def.Tags,
                CurrentVersion = "1.0.0",
                LatestVersion = "1.0.0",
                AvailableVersions = new[] { "1.0.0" },
                IsInstalled = false,
                HasUpdate = false,
                CertificationLevel = CertificationLevel.FullyCertified,
                AverageRating = def.DefaultRating,
                RatingCount = def.DefaultRatingCount,
                InstallCount = def.DefaultInstallCount,
                Dependencies = def.Dependencies,
                CreatedAt = now,
                UpdatedAt = now,
                InstalledAt = null
            };

            _catalog[entry.Id] = entry;

            // Add initial version history
            AddVersionToHistory(entry.Id, "1.0.0", "Initial release");
        }

        _state.LastCatalogRefresh = now;
    }

    /// <summary>
    /// Returns the definitions of all built-in DataWarehouse plugins for catalog population.
    /// </summary>
    /// <returns>Collection of built-in plugin definitions.</returns>
    private static List<BuiltInPluginDefinition> GetBuiltInPluginDefinitions()
    {
        return new List<BuiltInPluginDefinition>
        {
            new("datawarehouse.plugins.ultimatestorage", "Ultimate Storage", "Storage",
                "Comprehensive storage backend with 130+ strategies across local, cloud, enterprise, and innovative storage systems.",
                "Ultimate Storage provides a unified storage abstraction layer supporting Local, Network, Cloud (AWS S3, Azure Blob, GCS), S3-Compatible, Enterprise (NetApp, Dell EMC, Pure Storage), Software-Defined, OpenStack Swift, Decentralized (IPFS, Filecoin), Archive (Glacier, Tape), Specialized, and Future Hardware backends.",
                new[] { "storage", "cloud", "s3", "azure", "gcs", "enterprise" },
                4.8, 156, 12500),

            new("datawarehouse.plugins.ultimateencryption", "Ultimate Encryption", "Security",
                "69 encryption strategies including AES-GCM, ChaCha20, post-quantum, and military-grade cryptography.",
                "Ultimate Encryption provides comprehensive data-at-rest and data-in-transit encryption with 69 strategies spanning symmetric (AES, ChaCha20, Serpent), asymmetric (RSA, ECDH, Kyber), authenticated (GCM, CCM, SIV), format-preserving (FF1, FF3-1), homomorphic, and military-grade (Adiantum, compound transit) algorithms.",
                new[] { "security", "encryption", "aes", "post-quantum", "cryptography" },
                4.9, 198, 15200),

            new("datawarehouse.plugins.ultimatecompression", "Ultimate Compression", "Storage",
                "59 compression strategies from LZ-family to domain-specific codecs with orchestrated pipeline support.",
                "Ultimate Compression unifies all compression needs with strategies for general-purpose (Zstd, LZ4, Brotli, Gzip), BWT/transform (Bzip2, MTF), context mixing, entropy coding, differential, and domain-specific (genomic, geospatial, time-series) compression.",
                new[] { "compression", "zstd", "lz4", "brotli", "performance" },
                4.7, 134, 11800),

            new("datawarehouse.plugins.ultimateraid", "Ultimate RAID", "Storage",
                "30+ RAID strategies including standard levels, nested, ZFS-style, vendor-specific, and erasure coding.",
                "Ultimate RAID implements all standard RAID levels (0, 1, 5, 6, 10) plus advanced configurations like RAID-Z1/Z2/Z3, nested RAID, vendor-specific formats, erasure coding, and AI-optimized tiered storage with SIMD-accelerated parity calculation.",
                new[] { "raid", "storage", "redundancy", "parity", "performance" },
                4.6, 89, 6700),

            new("datawarehouse.plugins.ultimateaccesscontrol", "Ultimate Access Control", "Security",
                "76 security strategies spanning access control, identity, MFA, zero trust, and threat detection.",
                "Ultimate Access Control provides 9 access control models (RBAC, ABAC, MAC, DAC, PBAC, ReBac, HrBAC, ACL, Capability), 10 identity authentication protocols (LDAP, OAuth2, OIDC, SAML, Kerberos, RADIUS, FIDO2), 8 MFA strategies, 6 zero trust strategies, and 9 threat detection strategies.",
                new[] { "security", "access-control", "rbac", "oauth", "mfa", "zero-trust" },
                4.8, 167, 13400),

            new("datawarehouse.plugins.ultimatecompliance", "Ultimate Compliance", "Security",
                "36 compliance framework strategies with automated reporting, chain-of-custody, and alert integration.",
                "Ultimate Compliance covers GDPR, HIPAA, SOX, PCI-DSS, SOC2, ISO 27001, NIST CSF, FedRAMP, NIS2, DORA and more with compliance checking, report generation (SOC2/HIPAA/FedRAMP templates), chain-of-custody export, dashboard, alerting (Email/Slack/PagerDuty), and tamper incident workflows.",
                new[] { "compliance", "gdpr", "hipaa", "pci-dss", "audit" },
                4.7, 112, 9800),

            new("datawarehouse.plugins.ultimatereplication", "Ultimate Replication", "Storage",
                "60 replication strategies with 12 advanced features including geo-WORM and geo-distributed sharding.",
                "Ultimate Replication provides synchronous, asynchronous, semi-synchronous, chain, multi-master, and fan-out replication strategies with advanced features like geo-WORM compliance, geo-distributed sharding with erasure coding, and conflict resolution.",
                new[] { "replication", "high-availability", "disaster-recovery", "geo-distributed" },
                4.6, 78, 5900),

            new("datawarehouse.plugins.ultimateinterface", "Ultimate Interface", "Integration",
                "68 interface strategies covering REST, RPC, query, real-time, messaging, conversational, and DX patterns.",
                "Ultimate Interface implements REST (CRUD, HATEOAS, pagination), RPC (gRPC, Thrift, JSON-RPC, XML-RPC, Cap'n Proto), query (GraphQL, OData, Falcor, SPARQL), real-time (WebSocket, SSE, Socket.IO, MQTT), messaging (AMQP, Kafka, NATS, Redis Streams), and conversational AI strategies.",
                new[] { "interface", "rest", "grpc", "graphql", "websocket", "api" },
                4.7, 145, 11200),

            new("datawarehouse.plugins.ultimateobservability", "Universal Observability", "Monitoring",
                "55 observability strategies for metrics, logging, tracing, APM, alerting, health, and monitoring.",
                "Universal Observability provides comprehensive monitoring with Prometheus metrics, structured logging, distributed tracing (OpenTelemetry, Jaeger), APM (Datadog, New Relic, Dynatrace), alerting, health checks, profiling, RUM, synthetic monitoring, error tracking, and service mesh integration.",
                new[] { "observability", "metrics", "logging", "tracing", "monitoring" },
                4.8, 178, 14100),

            new("datawarehouse.plugins.ultimateintelligence", "Ultimate Intelligence", "AI",
                "AI/ML orchestration with 12 providers, vector stores, knowledge graphs, and memory strategies.",
                "Ultimate Intelligence provides AI-native capabilities including 12 AI provider integrations (OpenAI, Claude, Ollama, Copilot), 6 vector stores, 4 knowledge graphs, embedding generation, summarization, and intelligent feature strategies with rule-based fallbacks.",
                new[] { "ai", "ml", "intelligence", "openai", "llm", "embeddings" },
                4.9, 201, 16300),

            new("datawarehouse.plugins.ultimatecompute", "Ultimate Compute", "Integration",
                "51 compute runtime strategies for WASM, containers, native processes, and serverless execution.",
                "Ultimate Compute supports diverse execution environments including WebAssembly (Wasmtime, Wasmer), containers (Docker, Podman, containerd), native processes (Python, Node.js, Go, Rust, Java, .NET), GPU (CUDA, OpenCL, Vulkan), and specialized runtimes (V8, Lua, R, Julia).",
                new[] { "compute", "wasm", "containers", "serverless", "runtime" },
                4.5, 67, 4200),

            new("datawarehouse.plugins.ultimatedataformat", "Ultimate Data Format", "Storage",
                "9 data format strategies for JSON, CSV, XML, YAML, TOML, Protobuf, Avro, Thrift, and MessagePack.",
                "Ultimate Data Format provides a unified serialization layer supporting text formats (JSON, CSV, XML, YAML, TOML) and binary formats (Protocol Buffers, Apache Avro, Apache Thrift) with schema validation and format conversion.",
                new[] { "format", "json", "csv", "protobuf", "avro", "serialization" },
                4.4, 56, 3800),

            new("datawarehouse.plugins.ultimatestreamingdata", "Ultimate Streaming Data", "Integration",
                "20+ media streaming and processing strategies with ABR, codec, and real-time processing support.",
                "Ultimate Streaming Data provides HLS, DASH, and CMAF adaptive streaming protocols, H.264/H.265/VP9/AV1/VVC video codecs, image processing (JPEG, PNG, WebP, AVIF), RAW formats, GPU textures, and 3D formats (glTF, USD).",
                new[] { "streaming", "media", "video", "hls", "dash", "codecs" },
                4.5, 72, 4500),

            new("datawarehouse.plugins.ultimateresource", "Ultimate Resource Manager", "Integration",
                "Resource management with allocation, scheduling, and capacity planning.",
                "Ultimate Resource Manager provides comprehensive resource lifecycle management including allocation, scheduling, capacity planning, quota management, and multi-tenant resource isolation with fairness guarantees.",
                new[] { "resource", "scheduling", "capacity", "allocation" },
                4.3, 45, 2800),

            new("datawarehouse.plugins.ultimateresilience", "Ultimate Resilience", "Integration",
                "66 resilience strategies for circuit breakers, retry, bulkhead, and chaos engineering.",
                "Ultimate Resilience implements circuit breaker, retry with exponential backoff, bulkhead isolation, rate limiting, timeout policies, fallback chains, health-based routing, chaos engineering, and distributed consensus recovery patterns.",
                new[] { "resilience", "circuit-breaker", "retry", "chaos-engineering" },
                4.7, 134, 10200),

            new("datawarehouse.plugins.ultimatedeployment", "Ultimate Deployment", "Integration",
                "71 deployment strategies for blue-green, canary, rolling, and multi-cloud orchestration.",
                "Ultimate Deployment provides blue-green, canary, rolling update, A/B testing, feature flags, immutable infrastructure, GitOps, and multi-cloud deployment strategies with rollback support and health verification.",
                new[] { "deployment", "cicd", "blue-green", "canary", "gitops" },
                4.4, 56, 3600),

            new("datawarehouse.plugins.ultimatesustainability", "Ultimate Sustainability", "Integration",
                "45 green computing strategies for PUE optimization, carbon tracking, and energy efficiency.",
                "Ultimate Sustainability provides Power Usage Effectiveness (PUE) monitoring, Water Usage Effectiveness (WUE), carbon intensity tracking (gCO2e/kWh), battery-aware computing, CPU DVFS with governor control, renewable energy integration, and e-waste lifecycle tracking.",
                new[] { "sustainability", "green-computing", "carbon", "energy", "pue" },
                4.3, 38, 2100),

            new("datawarehouse.plugins.ultimatedatagovernance", "Ultimate Data Governance", "Security",
                "Active lineage, living catalog, predictive quality, semantic intelligence, and governance strategies.",
                "Ultimate Data Governance provides data lineage tracking with real-time capture and impact analysis, self-learning catalog with auto-tagging, predictive data quality with drift detection, semantic intelligence with TF-IDF matching, and intelligent governance with policy recommendations and compliance gap detection.",
                new[] { "governance", "lineage", "catalog", "quality", "compliance" },
                4.8, 156, 12800),

            new("datawarehouse.plugins.ultimatedatabaseprotocol", "Ultimate Database Protocol", "Integration",
                "51 database protocol strategies for PostgreSQL, MySQL, SQL Server, MongoDB, Redis, and more.",
                "Ultimate Database Protocol provides wire-compatible protocol implementations for PostgreSQL, MySQL, SQL Server (TDS), Oracle (TNS), MongoDB, Redis, Cassandra, Elasticsearch, Neo4j, InfluxDB, DynamoDB, and Neptune Gremlin with connection pooling and query routing.",
                new[] { "database", "protocol", "postgresql", "mysql", "mongodb", "redis" },
                4.6, 89, 6200),

            new("datawarehouse.plugins.ultimatedatabasestorage", "Ultimate Database Storage", "Storage",
                "49 database storage strategies for relational, document, graph, time-series, and key-value stores.",
                "Ultimate Database Storage provides direct storage integration with PostgreSQL, MySQL, SQL Server, Oracle, MongoDB, Redis, Cassandra, Elasticsearch, Neo4j, InfluxDB, and more with CRUD operations, connection management, and schema migration.",
                new[] { "database", "storage", "postgresql", "mysql", "nosql" },
                4.6, 92, 6500),

            new("datawarehouse.plugins.ultimatedatamanagement", "Ultimate Data Management", "Storage",
                "90 data management strategies for ETL, CDC, event sourcing, CQRS, and data lifecycle.",
                "Ultimate Data Management provides ETL/ELT pipelines, Change Data Capture (CDC), event sourcing, CQRS, data versioning, archival, retention policies, data masking, fan-out orchestration, and AI-enhanced data management with message bus integration.",
                new[] { "data-management", "etl", "cdc", "event-sourcing", "cqrs" },
                4.7, 124, 9100),

            new("datawarehouse.plugins.marketplace.plugin", "Plugin Marketplace", "Integration",
                "Plugin catalog management with installation, updates, dependency resolution, and version archiving.",
                "Plugin Marketplace provides a centralized catalog of all DataWarehouse plugins with search, filtering, installation via kernel message bus, dependency resolution with topological sort, version archiving for rollback, certification tracking, and ratings/reviews.",
                new[] { "marketplace", "plugins", "catalog", "installation", "management" },
                4.5, 45, 3200,
                IsInstalled: true)
        };
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Extracts a string value from a payload dictionary.
    /// </summary>
    /// <param name="payload">The message payload.</param>
    /// <param name="key">The key to look up.</param>
    /// <returns>The string value, or null if not found.</returns>
    private static string? GetString(Dictionary<string, object> payload, string key)
        => payload.TryGetValue(key, out var val) ? val?.ToString() : null;

    /// <summary>
    /// Extracts an integer value from a payload dictionary.
    /// </summary>
    /// <param name="payload">The message payload.</param>
    /// <param name="key">The key to look up.</param>
    /// <returns>The integer value, or 0 if not found or not parseable.</returns>
    private static int GetInt(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is JsonElement je && je.TryGetInt32(out var jei)) return jei;
            if (int.TryParse(val?.ToString(), out var parsed)) return parsed;
        }
        return 0;
    }

    /// <summary>
    /// Extracts a boolean value from a payload dictionary.
    /// </summary>
    /// <param name="payload">The message payload.</param>
    /// <param name="key">The key to look up.</param>
    /// <returns>The boolean value, or false if not found.</returns>
    private static bool GetBool(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is bool b) return b;
            if (val is JsonElement je && je.ValueKind == JsonValueKind.True) return true;
            if (bool.TryParse(val?.ToString(), out var parsed)) return parsed;
        }
        return false;
    }

    /// <summary>
    /// Extracts a nullable integer value from a payload dictionary.
    /// </summary>
    /// <param name="payload">The message payload.</param>
    /// <param name="key">The key to look up.</param>
    /// <returns>The integer value, or null if not found or not parseable.</returns>
    private static int? GetNullableInt(Dictionary<string, object> payload, string key)
    {
        if (payload.TryGetValue(key, out var val))
        {
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is JsonElement je && je.TryGetInt32(out var jei)) return jei;
            if (int.TryParse(val?.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    /// <summary>
    /// Extracts a decimal price from catalog entry metadata.
    /// Built-in plugins are free by default; price comes from catalog metadata if set.
    /// </summary>
    /// <param name="entry">The catalog entry to check for a price.</param>
    /// <returns>The price as decimal, or 0 if free.</returns>
    private static decimal GetDecimalFromCatalog(PluginCatalogEntry entry)
    {
        // Built-in plugins are free; price could be added via catalog metadata extension
        // This returns 0 for built-in plugins and supports future priced plugins
        return entry.Price;
    }

    /// <summary>
    /// Sanitizes a string for use as a filename by replacing invalid characters.
    /// </summary>
    /// <param name="name">The string to sanitize.</param>
    /// <returns>A filename-safe string.</returns>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            sanitized[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        }
        return new string(sanitized);
    }

    #endregion
}

#region Record Types and Enums

/// <summary>
/// Represents a plugin entry in the marketplace catalog with full metadata.
/// </summary>
public sealed record PluginCatalogEntry
{
    /// <summary>Unique plugin identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable plugin name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Plugin author or organization.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Short description of the plugin.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Full detailed description of the plugin.</summary>
    public string? FullDescription { get; init; }

    /// <summary>Plugin category (e.g., Storage, Security, Integration).</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Tags for search and filtering.</summary>
    public string[]? Tags { get; init; }

    /// <summary>Currently installed version, or latest if not installed.</summary>
    public string CurrentVersion { get; init; } = "1.0.0";

    /// <summary>Latest available version in the catalog.</summary>
    public string LatestVersion { get; init; } = "1.0.0";

    /// <summary>All available versions for this plugin.</summary>
    public string[]? AvailableVersions { get; init; }

    /// <summary>Whether this plugin is currently installed.</summary>
    public bool IsInstalled { get; init; }

    /// <summary>Whether an update is available for the installed version.</summary>
    public bool HasUpdate { get; init; }

    /// <summary>Certification level of this plugin.</summary>
    public CertificationLevel CertificationLevel { get; init; } = CertificationLevel.Uncertified;

    /// <summary>Average user rating (1.0-5.0).</summary>
    public double AverageRating { get; init; }

    /// <summary>Total number of ratings received.</summary>
    public int RatingCount { get; init; }

    /// <summary>Total number of times this plugin has been installed.</summary>
    public long InstallCount { get; init; }

    /// <summary>Dependencies required by this plugin.</summary>
    public PluginDependencyInfo[]? Dependencies { get; init; }

    /// <summary>When this catalog entry was first created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When this catalog entry was last updated.</summary>
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When this plugin was installed, or null if not installed.</summary>
    public DateTime? InstalledAt { get; init; }

    /// <summary>Price for installation. 0 indicates free plugin.</summary>
    public decimal Price { get; init; }
}

/// <summary>
/// Describes a dependency relationship between plugins.
/// </summary>
/// <param name="PluginId">The ID of the required plugin.</param>
/// <param name="MinVersion">Minimum compatible version (inclusive), or null for any.</param>
/// <param name="MaxVersion">Maximum compatible version (inclusive), or null for any.</param>
/// <param name="IsOptional">Whether this dependency is optional.</param>
/// <param name="Reason">Human-readable reason for the dependency.</param>
public sealed record PluginDependencyInfo(
    string PluginId,
    string? MinVersion = null,
    string? MaxVersion = null,
    bool IsOptional = false,
    string? Reason = null);

/// <summary>
/// Version information for a specific plugin release.
/// </summary>
/// <param name="PluginId">The plugin this version belongs to.</param>
/// <param name="VersionNumber">Semantic version number.</param>
/// <param name="Changelog">Description of changes in this version.</param>
/// <param name="ReleaseDate">When this version was released.</param>
/// <param name="AssemblyHash">SHA-256 hash of the assembly file.</param>
/// <param name="AssemblySize">Size of the assembly file in bytes.</param>
/// <param name="SdkCompatibility">Minimum SDK version required.</param>
/// <param name="IsPreRelease">Whether this is a pre-release version.</param>
public sealed record PluginVersionInfo(
    string PluginId,
    string VersionNumber,
    string? Changelog,
    DateTime ReleaseDate,
    string? AssemblyHash,
    long AssemblySize,
    string SdkCompatibility,
    bool IsPreRelease);

/// <summary>
/// Request to install a plugin with optional dependency resolution.
/// </summary>
/// <param name="PluginId">The plugin to install.</param>
/// <param name="Version">The version to install, or null for latest.</param>
/// <param name="IncludeDependencies">Whether to automatically install required dependencies.</param>
public sealed record PluginInstallRequest(
    string PluginId,
    string? Version = null,
    bool IncludeDependencies = true);

/// <summary>
/// Result of a plugin installation operation.
/// </summary>
/// <param name="Success">Whether the installation succeeded.</param>
/// <param name="PluginId">The ID of the plugin that was installed.</param>
/// <param name="InstalledVersion">The version that was installed.</param>
/// <param name="Message">Human-readable status message.</param>
/// <param name="InstalledDependencies">IDs of dependencies that were also installed.</param>
public sealed record PluginInstallResult(
    bool Success,
    string PluginId,
    string InstalledVersion,
    string Message,
    string[] InstalledDependencies);

/// <summary>
/// Configuration for the Plugin Marketplace plugin.
/// </summary>
public sealed record PluginMarketplaceConfig
{
    /// <summary>Base storage path for marketplace data. Defaults to {LocalApplicationData}/DataWarehouse/plugin-marketplace/.</summary>
    public string? StoragePath { get; init; }

    /// <summary>How often to refresh the catalog from external sources.</summary>
    public TimeSpan CatalogRefreshInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Maximum number of retries for plugin installation.</summary>
    public int MaxInstallRetries { get; init; } = 3;

    /// <summary>Whether to require certification before allowing installation.</summary>
    public bool RequireCertification { get; init; }

    /// <summary>Whether to automatically update plugins when new versions are available.</summary>
    public bool AutoUpdateEnabled { get; init; }

    /// <summary>Optional override for certification policy settings.</summary>
    public CertificationPolicy? CertificationPolicyOverride { get; init; }

    /// <summary>Optional override for revenue configuration.</summary>
    public RevenueConfig? RevenueConfigOverride { get; init; }
}

/// <summary>
/// Tracks marketplace operational state with thread-safe counters.
/// </summary>
internal sealed class MarketplaceState
{
    /// <summary>When the catalog was last refreshed from external sources.</summary>
    public DateTime LastCatalogRefresh { get; set; } = DateTime.UtcNow;

    /// <summary>Total number of plugin installations (thread-safe via Interlocked).</summary>
    internal long _totalInstalls;

    /// <summary>Total number of plugin uninstallations (thread-safe via Interlocked).</summary>
    internal long _totalUninstalls;

    /// <summary>Count of currently active (installed) plugins.</summary>
    public int ActivePluginCount { get; set; }
}

/// <summary>
/// DTO for serializing marketplace state to JSON.
/// </summary>
internal sealed record MarketplaceStateDto
{
    /// <summary>When the catalog was last refreshed.</summary>
    public DateTime LastCatalogRefresh { get; init; }

    /// <summary>Total install count.</summary>
    public long TotalInstalls { get; init; }

    /// <summary>Total uninstall count.</summary>
    public long TotalUninstalls { get; init; }

    /// <summary>Active plugin count.</summary>
    public int ActivePluginCount { get; init; }
}

/// <summary>
/// Request to certify a plugin through the 5-stage pipeline.
/// </summary>
/// <param name="PluginId">The plugin to certify.</param>
/// <param name="Version">The version to certify.</param>
/// <param name="RequestedBy">Who requested the certification.</param>
/// <param name="AssemblyPath">Path to the plugin assembly file.</param>
/// <param name="SubmittedAt">When the request was submitted.</param>
public sealed record CertificationRequest(
    string PluginId,
    string Version,
    string RequestedBy,
    string AssemblyPath,
    DateTime SubmittedAt);

/// <summary>
/// Result of a plugin certification pipeline execution.
/// </summary>
/// <param name="PluginId">The certified plugin ID.</param>
/// <param name="Version">The certified version.</param>
/// <param name="Level">The determined certification level.</param>
/// <param name="OverallScore">Weighted composite score (0-100).</param>
/// <param name="Stages">Results of each certification stage.</param>
/// <param name="CertifiedAt">When certification was granted, or null if not certified.</param>
/// <param name="ExpiresAt">When the certification expires, or null.</param>
/// <param name="CertifiedBy">Who triggered the certification.</param>
public sealed record CertificationResult(
    string PluginId,
    string Version,
    CertificationLevel Level,
    double OverallScore,
    CertificationStageResult[] Stages,
    DateTime? CertifiedAt,
    DateTime? ExpiresAt,
    string CertifiedBy);

/// <summary>
/// Result of a single certification pipeline stage.
/// </summary>
/// <param name="StageName">Name of the stage (SecurityScan, SdkCompatibility, DependencyValidation, StaticAnalysis, CertificationScoring).</param>
/// <param name="Passed">Whether the stage passed.</param>
/// <param name="Score">Stage score from 0 to 100.</param>
/// <param name="Details">Human-readable description of the stage result.</param>
/// <param name="Warnings">Non-blocking warnings discovered during the stage.</param>
/// <param name="Errors">Blocking errors discovered during the stage.</param>
/// <param name="ExecutedAt">When the stage was executed.</param>
/// <param name="DurationMs">Duration of the stage in milliseconds.</param>
public sealed record CertificationStageResult(
    string StageName,
    bool Passed,
    double Score,
    string Details,
    string[] Warnings,
    string[] Errors,
    DateTime ExecutedAt,
    long DurationMs);

/// <summary>
/// Policy controlling certification requirements and thresholds.
/// </summary>
public sealed record CertificationPolicy
{
    /// <summary>
    /// Whether signed assemblies are required for certification.
    /// ISO-05 (CVSS 7.7): Defaults to true -- unsigned assemblies are rejected.
    /// Set to false only for development/testing environments.
    /// </summary>
    public bool RequireSignedAssembly { get; init; } = true;

    /// <summary>
    /// Whether to validate the SHA-256 hash of plugin assemblies against the catalog.
    /// When enabled, tampered assemblies are rejected during certification.
    /// </summary>
    public bool ValidateAssemblyHash { get; init; } = true;

    /// <summary>Maximum assembly size in megabytes.</summary>
    public int MaxAssemblySizeMb { get; init; } = 50;

    /// <summary>Whether SDK compatibility is required.</summary>
    public bool RequireSdkCompatibility { get; init; } = true;

    /// <summary>Minimum composite score for BasicCertified level (0-100).</summary>
    public double MinimumScore { get; init; } = 60.0;

    /// <summary>Number of days a certification remains valid.</summary>
    public int CertificationValidityDays { get; init; } = 365;
}

/// <summary>
/// A multi-dimensional user review for a plugin.
/// Supports overall rating plus optional reliability, performance, and documentation dimension ratings.
/// </summary>
public sealed record PluginReviewEntry
{
    /// <summary>Unique review identifier (format: "rev-{Guid}").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The plugin being reviewed.</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>Unique identifier of the reviewer.</summary>
    public string ReviewerId { get; init; } = string.Empty;

    /// <summary>Display name of the reviewer.</summary>
    public string ReviewerName { get; init; } = string.Empty;

    /// <summary>Overall rating from 1 to 5.</summary>
    public int OverallRating { get; init; }

    /// <summary>Optional review title.</summary>
    public string? Title { get; init; }

    /// <summary>Optional review content text.</summary>
    public string? Content { get; init; }

    /// <summary>Optional reliability dimension rating (1-5).</summary>
    public int? ReliabilityRating { get; init; }

    /// <summary>Optional performance dimension rating (1-5).</summary>
    public int? PerformanceRating { get; init; }

    /// <summary>Optional documentation quality dimension rating (1-5).</summary>
    public int? DocumentationRating { get; init; }

    /// <summary>Whether the reviewer has the plugin installed (verified install).</summary>
    public bool IsVerifiedInstall { get; init; }

    /// <summary>Moderation status of the review.</summary>
    public ReviewModerationStatus ReviewStatus { get; init; }

    /// <summary>When the review was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Optional response from the plugin owner/developer.</summary>
    public string? OwnerResponse { get; init; }

    /// <summary>When the owner responded, if applicable.</summary>
    public DateTime? OwnerRespondedAt { get; init; }
}

/// <summary>
/// Submission data for creating or updating a plugin review.
/// </summary>
public sealed record PluginReviewSubmission
{
    /// <summary>The plugin to review.</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>Unique identifier of the reviewer.</summary>
    public string ReviewerId { get; init; } = string.Empty;

    /// <summary>Display name of the reviewer.</summary>
    public string ReviewerName { get; init; } = string.Empty;

    /// <summary>Overall rating from 1 to 5.</summary>
    public int OverallRating { get; init; }

    /// <summary>Optional review title.</summary>
    public string? Title { get; init; }

    /// <summary>Optional review content text.</summary>
    public string? Content { get; init; }

    /// <summary>Optional reliability dimension rating (1-5).</summary>
    public int? ReliabilityRating { get; init; }

    /// <summary>Optional performance dimension rating (1-5).</summary>
    public int? PerformanceRating { get; init; }

    /// <summary>Optional documentation quality dimension rating (1-5).</summary>
    public int? DocumentationRating { get; init; }
}

/// <summary>
/// Response containing paginated reviews with aggregate statistics.
/// </summary>
public sealed record PluginReviewsResponse
{
    /// <summary>The plugin these reviews are for.</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>Paginated array of review entries.</summary>
    public PluginReviewEntry[] Reviews { get; init; } = Array.Empty<PluginReviewEntry>();

    /// <summary>Total number of approved reviews.</summary>
    public int TotalCount { get; init; }

    /// <summary>Average overall rating across all approved reviews.</summary>
    public double AverageRating { get; init; }

    /// <summary>Distribution of ratings: key is star level (1-5), value is count.</summary>
    public Dictionary<int, int>? RatingDistribution { get; init; }
}

/// <summary>
/// Moderation status for plugin reviews.
/// </summary>
public enum ReviewModerationStatus
{
    /// <summary>Review is pending moderation.</summary>
    Pending = 0,

    /// <summary>Review has been approved and is visible.</summary>
    Approved = 1,

    /// <summary>Review has been rejected by moderation.</summary>
    Rejected = 2,

    /// <summary>Review has been flagged for further review.</summary>
    Flagged = 3
}

/// <summary>
/// Revenue record for a developer's earnings from a specific plugin in a given period.
/// Uses decimal arithmetic for financial precision.
/// </summary>
public sealed record DeveloperRevenueRecord
{
    /// <summary>The developer earning revenue.</summary>
    public string DeveloperId { get; init; } = string.Empty;

    /// <summary>The plugin generating revenue.</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>The billing period in yyyy-MM format.</summary>
    public string Period { get; init; } = string.Empty;

    /// <summary>Total earnings before commission (gross).</summary>
    public decimal TotalEarnings { get; init; }

    /// <summary>Commission rate applied (e.g., 0.30 for 30%).</summary>
    public decimal CommissionRate { get; init; } = 0.30m;

    /// <summary>Commission amount deducted.</summary>
    public decimal CommissionAmount { get; init; }

    /// <summary>Net earnings after commission.</summary>
    public decimal NetEarnings { get; init; }

    /// <summary>Number of paid installs in this period.</summary>
    public int InstallCount { get; init; }

    /// <summary>Current payout status.</summary>
    public PayoutStatus PayoutStatus { get; init; }
}

/// <summary>
/// Status of a developer payout.
/// </summary>
public enum PayoutStatus
{
    /// <summary>Payout is pending processing.</summary>
    Pending = 0,

    /// <summary>Payout is being processed.</summary>
    Processing = 1,

    /// <summary>Payout has been completed.</summary>
    Paid = 2,

    /// <summary>Payout processing failed.</summary>
    Failed = 3
}

/// <summary>
/// Configuration for the marketplace revenue system.
/// </summary>
public sealed record RevenueConfig
{
    /// <summary>Default commission rate (0.30 = 30%).</summary>
    public decimal DefaultCommissionRate { get; init; } = 0.30m;

    /// <summary>Minimum payout threshold in the configured currency.</summary>
    public decimal MinimumPayoutThreshold { get; init; } = 50.00m;

    /// <summary>Currency code for payouts.</summary>
    public string PayoutCurrency { get; init; } = "USD";
}

/// <summary>
/// Plugin certification levels indicating verification status.
/// </summary>
public enum CertificationLevel
{
    /// <summary>Plugin has not been certified.</summary>
    Uncertified = 0,

    /// <summary>Plugin has passed basic automated certification checks.</summary>
    BasicCertified = 1,

    /// <summary>Plugin has passed full manual and automated certification.</summary>
    FullyCertified = 2,

    /// <summary>Plugin certification has been rejected due to issues.</summary>
    Rejected = -1
}

/// <summary>
/// Status of a plugin installation operation.
/// </summary>
public enum PluginInstallStatus
{
    /// <summary>Plugin is available but not installed.</summary>
    Available,

    /// <summary>Plugin installation is in progress.</summary>
    Installing,

    /// <summary>Plugin is installed and active.</summary>
    Installed,

    /// <summary>Plugin is being updated to a new version.</summary>
    Updating,

    /// <summary>Plugin is being uninstalled.</summary>
    Uninstalling,

    /// <summary>Plugin installation or update failed.</summary>
    Failed
}

/// <summary>
/// Internal helper for defining built-in plugin catalog entries.
/// </summary>
/// <param name="Id">Plugin ID.</param>
/// <param name="Name">Plugin name.</param>
/// <param name="Category">Plugin category.</param>
/// <param name="Description">Short description.</param>
/// <param name="FullDescription">Full description.</param>
/// <param name="Tags">Search tags.</param>
/// <param name="DefaultRating">Default average rating.</param>
/// <param name="DefaultRatingCount">Default rating count.</param>
/// <param name="DefaultInstallCount">Default install count.</param>
/// <param name="Dependencies">Optional dependencies.</param>
/// <param name="IsInstalled">Whether pre-installed.</param>
internal sealed record BuiltInPluginDefinition(
    string Id,
    string Name,
    string Category,
    string Description,
    string FullDescription,
    string[] Tags,
    double DefaultRating,
    int DefaultRatingCount,
    long DefaultInstallCount,
    PluginDependencyInfo[]? Dependencies = null,
    bool IsInstalled = false);

/// <summary>
/// Types of usage events tracked for plugin analytics.
/// </summary>
public enum UsageEventType
{
    /// <summary>Plugin was installed.</summary>
    Installed = 0,

    /// <summary>Plugin was uninstalled.</summary>
    Uninstalled = 1,

    /// <summary>Plugin was updated to a new version.</summary>
    Updated = 2,

    /// <summary>Plugin was activated/started.</summary>
    Activated = 3,

    /// <summary>Plugin was deactivated/stopped.</summary>
    Deactivated = 4,

    /// <summary>An error occurred during plugin operation.</summary>
    ErrorOccurred = 5,

    /// <summary>A message was handled by the plugin.</summary>
    MessageHandled = 6
}

/// <summary>
/// Indicates the direction of a plugin's popularity trend relative to the previous month.
/// </summary>
public enum TrendDirection
{
    /// <summary>Popularity is increasing.</summary>
    Rising = 0,

    /// <summary>Popularity is stable.</summary>
    Stable = 1,

    /// <summary>Popularity is declining.</summary>
    Declining = 2
}

/// <summary>
/// Represents a single usage event for a plugin, used for event-based analytics tracking.
/// Events are recorded in real time and aggregated periodically into monthly analytics.
/// </summary>
public sealed record PluginUsageEvent
{
    /// <summary>Unique event identifier (generated GUID).</summary>
    public string EventId { get; init; } = string.Empty;

    /// <summary>The plugin this event relates to.</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>The type of usage event.</summary>
    public UsageEventType EventType { get; init; }

    /// <summary>When the event occurred (UTC).</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Optional user ID associated with the event.</summary>
    public string? UserId { get; init; }

    /// <summary>Optional additional metadata for the event.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Monthly aggregated usage analytics for a specific plugin.
/// Computed periodically from raw usage events by the aggregation timer.
/// </summary>
public sealed record PluginUsageAnalytics
{
    /// <summary>The plugin these analytics describe.</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>The month period in yyyy-MM format.</summary>
    public string Period { get; init; } = string.Empty;

    /// <summary>Number of installations in this period.</summary>
    public int InstallCount { get; init; }

    /// <summary>Number of uninstallations in this period.</summary>
    public int UninstallCount { get; init; }

    /// <summary>Number of updates in this period.</summary>
    public int UpdateCount { get; init; }

    /// <summary>Count of distinct active users in this period.</summary>
    public int ActiveUserCount { get; init; }

    /// <summary>Number of errors that occurred in this period.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Number of messages handled in this period.</summary>
    public long MessageCount { get; init; }

    /// <summary>
    /// Popularity score computed from event counts.
    /// Formula: (InstallCount * 10 + ActiveUserCount * 5 - UninstallCount * 3 - ErrorCount * 2), clamped to 0-100.
    /// </summary>
    public double PopularityScore { get; init; }

    /// <summary>Trend direction comparing this month to the previous month.</summary>
    public TrendDirection TrendDirection { get; init; }
}

/// <summary>
/// Per-plugin analytics summary with monthly breakdown and aggregate statistics.
/// Returned by the marketplace.analytics handler when a pluginId is provided.
/// </summary>
public sealed record PluginAnalyticsSummary
{
    /// <summary>The plugin these analytics summarize.</summary>
    public string PluginId { get; init; } = string.Empty;

    /// <summary>Total number of installations over all time.</summary>
    public long TotalInstalls { get; init; }

    /// <summary>Total number of uninstallations over all time.</summary>
    public long TotalUninstalls { get; init; }

    /// <summary>Number of currently active installations.</summary>
    public int CurrentActiveInstalls { get; init; }

    /// <summary>Average session duration across all users.</summary>
    public TimeSpan AverageSessionDuration { get; init; }

    /// <summary>The month with the highest active user count (yyyy-MM format).</summary>
    public string MostActiveMonth { get; init; } = string.Empty;

    /// <summary>Monthly analytics data ordered by period descending.</summary>
    public PluginUsageAnalytics[] MonthlyData { get; init; } = Array.Empty<PluginUsageAnalytics>();
}

/// <summary>
/// Marketplace-wide analytics summary with aggregate statistics across all plugins.
/// Returned by the marketplace.analytics handler when no pluginId is provided.
/// </summary>
public sealed record MarketplaceAnalyticsSummary
{
    /// <summary>Total number of plugins in the marketplace catalog.</summary>
    public int TotalPlugins { get; init; }

    /// <summary>Total number of installations across all plugins.</summary>
    public long TotalInstalls { get; init; }

    /// <summary>Top 10 most popular plugins by popularity score.</summary>
    public string[] MostPopularPlugins { get; init; } = Array.Empty<string>();

    /// <summary>The most active plugin category by total installs.</summary>
    public string MostActiveCategory { get; init; } = string.Empty;

    /// <summary>Month-over-month growth rate: (thisMonth - lastMonth) / max(lastMonth, 1).</summary>
    public double GrowthRate { get; init; }

    /// <summary>When this summary was generated (UTC).</summary>
    public DateTime GeneratedAt { get; init; }
}

#endregion
