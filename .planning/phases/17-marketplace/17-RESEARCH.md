# Phase 17: Plugin Marketplace - Research

**Researched:** 2026-02-11
**Domain:** Plugin marketplace ecosystem -- discovery, installation, versioning, certification, ratings, analytics
**Confidence:** HIGH

## Summary

Phase 17 implements T57 (Plugin Marketplace & Certification), which is a **Plugin Marketplace** -- an ecosystem for discovering, installing, versioning, and certifying DataWarehouse plugins. This is distinct from T83 (Data Marketplace), which is a data commerce plugin for monetizing datasets and already exists as `DataWarehouse.Plugins.DataMarketplace`.

The codebase already has significant infrastructure supporting T57's goals:

1. **GUI Marketplace Page** (`DataWarehouse.GUI/Components/Pages/Marketplace.razor`) -- A fully built Blazor page with search, filtering, category selection, sorting, plugin cards with ratings/reviews, install/uninstall/update buttons, version history display, and a details modal. It communicates via `InstanceManager.ExecuteAsync("marketplace.install/uninstall/update/list")`.

2. **Kernel PluginLoader** (`DataWarehouse.Kernel/Plugins/PluginLoader.cs`) -- A production-ready plugin loader with `AssemblyLoadContext` isolation, security validation (signed assemblies, trusted publishers, hash verification, size limits, blocked list), hot reload via `IPluginReloader`, and load/unload lifecycle management.

3. **PluginRegistry** (`DataWarehouse.SDK/Services/PluginRegistry.cs`) -- Central plugin registry with type indexing, suitability scoring by `OperatingMode`, and intelligent plugin resolution.

4. **SDK Contracts** -- `PluginDescriptor`, `PluginCapabilityDescriptor`, `PluginDependency`, `PluginParameterDescriptor` in `SDK/Utilities/PluginDetails.cs`. Pipeline policy support for marketplace via `MarketplaceStagePolicy` in `SDK/Contracts/Pipeline/PipelinePolicyContracts.cs`.

The primary gaps are: (a) a backend marketplace service/plugin that bridges the GUI to the PluginLoader with catalog management, dependency resolution, versioning, certification workflow, and analytics; (b) message bus handlers for `marketplace.install`, `marketplace.uninstall`, `marketplace.update`, and `marketplace.list` commands that the GUI already calls; (c) a plugin certification and quality validation pipeline; (d) a rating/review system for plugins (not data listings).

**Primary recommendation:** Create a `PluginMarketplaceService` (or strategy within an appropriate plugin) that implements the backend for T57's seven features, wiring the existing GUI to the existing Kernel PluginLoader via the message bus, and adding catalog management, versioning with rollback, certification workflow, rating/reviews for plugins, and usage analytics.

## Standard Stack

### Core (Already in Codebase)

| Component | Location | Purpose | Status |
|-----------|----------|---------|--------|
| Marketplace.razor | `DataWarehouse.GUI/Components/Pages/Marketplace.razor` | Full GUI for plugin discovery, install, update, uninstall, ratings, reviews | EXISTS - fully built |
| PluginLoader | `DataWarehouse.Kernel/Plugins/PluginLoader.cs` | Assembly loading, security validation, hot reload | EXISTS - production-ready |
| PluginRegistry | `DataWarehouse.SDK/Services/PluginRegistry.cs` | Plugin registration, type indexing, suitability scoring | EXISTS - production-ready |
| PluginDescriptor | `DataWarehouse.SDK/Utilities/PluginDetails.cs` | Plugin identity and metadata | EXISTS |
| FeaturePluginBase | `DataWarehouse.SDK/Contracts/PluginBase.cs` | Base class for marketplace plugin | EXISTS |
| IMessageBus | `DataWarehouse.SDK/Contracts/IMessageBus.cs` | Inter-plugin communication | EXISTS |
| MarketplaceStagePolicy | `DataWarehouse.SDK/Contracts/Pipeline/PipelinePolicyContracts.cs` | Pipeline policy for marketplace | EXISTS |

### Must Be Built

| Component | Purpose | Approach |
|-----------|---------|----------|
| PluginMarketplacePlugin (T57) | Backend service for marketplace operations | New plugin extending `FeaturePluginBase` |
| Plugin Catalog | Persistent catalog of available/installed plugins with versions | JSON file-based storage (consistent with DataMarketplace pattern) |
| Dependency Resolver | Resolve plugin dependencies before install | Graph-based resolution using `PluginDependency` from SDK |
| Certification Pipeline | Validate plugin quality and security | Multi-stage validation using existing `PluginSecurityConfig` |
| Plugin Rating/Review System | Community feedback on plugins | Similar record pattern to DataMarketplace's review system |
| Usage Analytics Tracker | Track plugin usage, install counts, popularity | Event-based analytics with periodic aggregation |
| Version Manager | Track versions, support upgrade/rollback | Maintain version history, archive old assemblies |

### Supporting (Already Available)

| Library/Framework | Version | Purpose |
|-------------------|---------|---------|
| System.Text.Json | .NET 10 | Serialization for catalog/config persistence |
| System.Security.Cryptography | .NET 10 | Assembly hash verification, signature validation |
| System.Runtime.Loader | .NET 10 | AssemblyLoadContext for plugin isolation |
| System.Collections.Concurrent | .NET 10 | Thread-safe collections for concurrent access |

## Architecture Patterns

### Recommended Structure

The T57 Plugin Marketplace should be implemented as a standalone plugin (like T83 DataMarketplace), since it is a distinct feature domain with its own lifecycle, persistence, and message handling.

```
Plugins/DataWarehouse.Plugins.PluginMarketplace/
    DataWarehouse.Plugins.PluginMarketplace.csproj
    PluginMarketplacePlugin.cs          # Main plugin (FeaturePluginBase)
                                         # Contains: catalog, install, version,
                                         # certification, ratings, analytics
```

### Pattern 1: Message Bus Integration with GUI

**What:** The Marketplace.razor GUI already calls `InstanceManager.ExecuteAsync()` with specific message types. The backend plugin must handle these.

**Message commands the GUI expects:**
- `marketplace.list` -- Return all available plugins with metadata (install status, versions, ratings)
- `marketplace.install` -- Install plugin by ID and version (with dependency resolution)
- `marketplace.uninstall` -- Uninstall plugin by ID
- `marketplace.update` -- Update plugin to specified version

**How to wire:**
The PluginMarketplacePlugin registers these message handlers in `OnMessageAsync()`, exactly like DataMarketplacePlugin handles `marketplace.list`, `marketplace.search`, etc. The plugin receives messages via the message bus and coordinates with the Kernel's `IPluginReloader`/`PluginLoader` via kernel-level message bus topics.

### Pattern 2: Catalog Management

**What:** A persistent catalog of all known plugins (both installed and available), their versions, metadata, ratings, and certification status.

**Structure (following DataMarketplace persistence pattern):**
```
{LocalAppData}/DataWarehouse/plugin-marketplace/
    catalog/
        {pluginId}.json           # Plugin catalog entry
    versions/
        {pluginId}/
            {version}.json        # Version-specific metadata
            {version}.dll         # Archived assembly (for rollback)
    certifications/
        {pluginId}.json           # Certification status and history
    reviews/
        {pluginId}.json           # Reviews for this plugin
    analytics/
        usage-{yyyy-MM}.json      # Monthly usage aggregates
    state.json                    # Overall marketplace state
```

### Pattern 3: Certification Pipeline

**What:** Multi-stage validation pipeline for plugin quality and security, leveraging existing PluginLoader security features.

**Stages:**
1. **Security Scan** -- Use existing `PluginSecurityConfig` (signed assemblies, hash verification, size limits, blocked list)
2. **Compatibility Check** -- Verify SDK version compatibility, check required interfaces
3. **Dependency Validation** -- Ensure all declared dependencies are satisfiable
4. **Static Analysis** -- Check for forbidden patterns (direct plugin references, etc.)
5. **Certification Scoring** -- Compute composite score from all checks

**Certification Levels:**
- `Uncertified` -- Not yet reviewed
- `BasicCertified` -- Passes security scan and compatibility
- `FullyCertified` -- Passes all stages including manual review flag
- `Rejected` -- Failed critical checks

### Pattern 4: Version Management with Rollback

**What:** Track all versions of each plugin, support upgrade and rollback.

**Key operations:**
- Install: Download/copy assembly, validate, load via PluginLoader
- Upgrade: Archive current version, install new version, verify, update catalog
- Rollback: Unload current, restore archived version, reload
- Version History: Maintain ordered list of all versions with changelogs

### Pattern 5: Plugin Rating and Review System

**What:** Community feedback system for plugins (reusing patterns from DataMarketplace T83 review system).

**Reuse from DataMarketplace:**
- `Review` record structure (rating 1-5, title, content, verified, moderation status)
- `ReviewsResponse` with pagination and rating distribution
- Review submission validation (rating range, moderation support)
- Owner response capability

**Differences from DataMarketplace reviews:**
- Reviews are for plugins, not data listings
- Reviewer verification is by plugin installation (not subscription)
- Additional rating dimensions: reliability, performance, documentation quality

### Anti-Patterns to Avoid

- **Direct PluginLoader coupling:** The marketplace plugin is in the Plugins layer, not the Kernel layer. It MUST NOT directly reference PluginLoader. Instead, communicate via message bus topics like `kernel.plugin.load`, `kernel.plugin.unload`, etc.
- **Duplicating DataMarketplace:** T57 is about PLUGIN marketplace. T83 is about DATA marketplace. Do not conflate them. They are separate plugins.
- **Simulated/Mock implementations:** Per Rule 13, no stubs or placeholders. If a certification stage requires external tooling, implement it as a configurable check with real validation logic.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Assembly loading/isolation | Custom DLL loading | `PluginLoader` via Kernel (already exists) | Handles AssemblyLoadContext, security validation, hot reload |
| Plugin registration | Custom registry | `PluginRegistry` (already exists) | Handles type indexing, suitability scoring |
| Plugin descriptor model | Custom metadata class | `PluginDescriptor` from SDK | Already has Id, Name, Version, Category, Tags, Metadata |
| Security validation | Custom hash/signature checks | `PluginSecurityConfig` + `ValidateAssemblySecurity()` | Already handles signed assemblies, hash verification, size limits |
| GUI marketplace page | Custom UI | `Marketplace.razor` (already exists) | Full UI with search, filter, install, update, ratings, reviews |
| Review system pattern | Custom review records | Copy pattern from DataMarketplace T83 | Production-tested record types, validation, pagination |
| JSON persistence | Custom file I/O | Follow DataMarketplace file persistence pattern | Proven pattern with load/save, directory structure, error handling |

**Key insight:** Over 60% of T57's infrastructure already exists. The main work is creating the backend plugin that connects the existing GUI to the existing Kernel plugin management, adding catalog management, and implementing the certification/review/analytics features.

## Common Pitfalls

### Pitfall 1: Confusing T57 (Plugin Marketplace) with T83 (Data Marketplace)
**What goes wrong:** Developer assumes the existing DataMarketplacePlugin already implements T57.
**Why it happens:** Both use the word "marketplace" and have similar-sounding features (search, ratings, etc.).
**How to avoid:** T57 is about plugins (software components). T83 is about data (datasets for commerce). They are separate plugins with separate purposes.
**Warning signs:** Trying to add plugin install/uninstall features to DataMarketplacePlugin, or treating data listings as plugin listings.

### Pitfall 2: Direct Reference to PluginLoader from Plugin Layer
**What goes wrong:** The marketplace plugin tries to directly call `PluginLoader.LoadPluginAsync()`.
**Why it happens:** PluginLoader is in the Kernel, and plugins only reference the SDK.
**How to avoid:** Use message bus communication. The marketplace plugin sends messages to kernel topics; the kernel (which has the PluginLoader) handles them. The SDK already defines `IPluginReloader` in `IKernelInfrastructure.cs`.
**Warning signs:** Adding a ProjectReference to DataWarehouse.Kernel from the marketplace plugin.

### Pitfall 3: Missing Message Handlers for GUI Commands
**What goes wrong:** GUI sends `marketplace.install` but nothing handles it.
**Why it happens:** The Marketplace.razor calls `InstanceManager.ExecuteAsync("marketplace.install")`, `"marketplace.uninstall"`, `"marketplace.update"`, and `"marketplace.list"`. These need handlers.
**How to avoid:** Implement `OnMessageAsync` with cases for all four message types. Also handle the search/filter parameters that the GUI sends.
**Warning signs:** GUI shows "Loading marketplace..." forever, or install buttons throw errors about unhandled messages.

### Pitfall 4: No Rollback After Failed Upgrade
**What goes wrong:** Plugin upgrade fails mid-way, leaving the system without the old or new version.
**Why it happens:** Upgrade process unloads old version before new version is validated.
**How to avoid:** Archive the old assembly before unloading. Validate the new assembly before swapping. If validation fails, restore the archive. Use a transactional pattern.
**Warning signs:** Plugin disappears from registry after failed upgrade attempt.

### Pitfall 5: Certification Without Real Validation
**What goes wrong:** Certification becomes a rubber stamp with no actual checks.
**Why it happens:** Developer creates placeholder certification logic that always passes.
**How to avoid:** Rule 13 applies. Use real security validation from PluginLoader's existing `ValidateAssemblySecurity()`, check SDK compatibility, validate dependencies, check for forbidden patterns.
**Warning signs:** Certification method that just returns `true` or has `// TODO: implement` comments.

## Code Examples

### Example 1: Plugin Catalog Entry Record
```csharp
// Following the DataMarketplace record pattern
public sealed record PluginCatalogEntry
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string[] Tags { get; init; } = Array.Empty<string>();
    public string CurrentVersion { get; init; } = string.Empty;
    public string LatestVersion { get; init; } = string.Empty;
    public string[] AvailableVersions { get; init; } = Array.Empty<string>();
    public bool IsInstalled { get; init; }
    public bool HasUpdate { get; init; }
    public CertificationLevel Certification { get; init; }
    public double AverageRating { get; init; }
    public int RatingCount { get; init; }
    public long InstallCount { get; init; }
    public PluginDependency[] Dependencies { get; init; } = Array.Empty<PluginDependency>();
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? InstalledAt { get; init; }
}
```

### Example 2: Message Handler Pattern (from DataMarketplacePlugin)
```csharp
// Source: Plugins/DataWarehouse.Plugins.DataMarketplace/DataMarketplacePlugin.cs
public override async Task OnMessageAsync(PluginMessage message)
{
    switch (message.Type)
    {
        case "marketplace.list":
            await HandleListPluginsAsync(message);
            break;
        case "marketplace.install":
            await HandleInstallPluginAsync(message);
            break;
        case "marketplace.uninstall":
            await HandleUninstallPluginAsync(message);
            break;
        case "marketplace.update":
            await HandleUpdatePluginAsync(message);
            break;
        default:
            await base.OnMessageAsync(message);
            break;
    }
}
```

### Example 3: Security Validation Integration
```csharp
// Source: DataWarehouse.Kernel/Plugins/PluginLoader.cs
// The PluginLoader already validates assemblies before loading.
// The marketplace certification pipeline should compose on top of this.
// Communication via message bus:
var response = await MessageBus.RequestAsync<PluginLoadResult>(
    topic: "kernel.plugin.load",
    request: new { AssemblyPath = assemblyPath },
    timeout: TimeSpan.FromSeconds(30),
    ct: ct
);
```

### Example 4: Persistence Pattern (from DataMarketplacePlugin)
```csharp
// Source: DataMarketplace uses LocalApplicationData + JSON files
// PluginMarketplace should follow the same pattern
private readonly string _storagePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DataWarehouse", "plugin-marketplace");

private async Task SaveCatalogEntryAsync(PluginCatalogEntry entry, CancellationToken ct = default)
{
    var path = Path.Combine(_storagePath, "catalog");
    Directory.CreateDirectory(path);
    var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(Path.Combine(path, $"{entry.Id}.json"), json, ct);
}
```

## State of the Art

| Component | Current State | What Needs to Happen |
|-----------|--------------|---------------------|
| GUI (Marketplace.razor) | Fully built -- search, filter, install, update, uninstall, ratings, reviews, details modal | Wire to backend via message bus |
| Kernel PluginLoader | Production-ready -- security, isolation, hot reload | Expose via message bus topics for marketplace plugin |
| PluginRegistry | Production-ready -- register, resolve, score | Already used by PluginLoader |
| Backend Plugin (T57) | Does NOT exist | Must be created as new plugin |
| Plugin Catalog | Does NOT exist | Must be created with persistence |
| Dependency Resolution | `PluginDependency` type exists in SDK | Must implement resolution algorithm |
| Certification Pipeline | Security validation exists in PluginLoader | Must create multi-stage pipeline |
| Rating/Review for Plugins | Pattern exists in DataMarketplace (T83) | Must implement for plugin domain |
| Usage Analytics | Does NOT exist | Must be created |

## Scope Analysis: T57 Feature Mapping

| T57 Feature | Existing Infrastructure | Gap |
|-------------|------------------------|-----|
| Plugin Discovery (search, filter, recommend) | Marketplace.razor has full UI with search, category filter, sort | Backend catalog + search API needed |
| One-Click Install (automatic deployment) | Marketplace.razor has install buttons; PluginLoader handles loading | Backend install handler, dependency resolution needed |
| Version Management (upgrade, rollback) | PluginLoader supports reload; GUI has update buttons | Version catalog, archive, rollback logic needed |
| Certification Program (security review, testing) | PluginLoader has `ValidateAssemblySecurity()` | Multi-stage certification pipeline needed |
| Revenue Sharing (monetization for developers) | DataMarketplace has billing/payment models | Plugin-specific revenue/payment tracking needed |
| Rating & Reviews (community feedback) | Marketplace.razor shows ratings/reviews; DataMarketplace has review records | Plugin-specific review backend needed |
| Usage Analytics (telemetry for developers) | None | Full analytics system needed |

## Open Questions

1. **Kernel-to-Plugin Communication for Plugin Loading**
   - What we know: The marketplace plugin (Plugins layer) cannot directly reference PluginLoader (Kernel layer). Communication must go through the message bus.
   - What's unclear: Are there existing kernel-level message bus handlers for `kernel.plugin.load/unload/reload` topics, or do these need to be created?
   - Recommendation: Check during planning/implementation. If they don't exist, create them in the Kernel layer as part of this phase. The SDK already defines `IPluginReloader` with `ReloadPluginAsync` and `ReloadAllAsync`.

2. **Plugin Package Format**
   - What we know: PluginLoader works with DLL assemblies on disk. The marketplace needs a way to distribute plugin packages.
   - What's unclear: Should plugins be distributed as NuGet packages, zip archives, or raw DLLs?
   - Recommendation: Use a simple zip archive format containing the DLL and a `plugin-manifest.json` with metadata. This keeps things self-contained without requiring NuGet infrastructure. The manifest provides the metadata needed for the catalog.

3. **Revenue Sharing Implementation Depth**
   - What we know: T57 lists "Revenue Sharing" as a feature. DataMarketplace has full billing/invoicing.
   - What's unclear: How deep should the revenue sharing implementation go? Full payment processing or just tracking/reporting?
   - Recommendation: Implement revenue tracking and reporting (developer earnings, commission calculation, payout records). Actual payment processing should integrate with the same pattern as DataMarketplace (payment gateway integration points) but does not need to duplicate the full billing engine.

4. **Marketplace Content Source**
   - What we know: This is a self-contained DataWarehouse instance. There is no external marketplace server mentioned.
   - What's unclear: Is the "marketplace" a local catalog of plugins that come bundled, or does it connect to an external registry?
   - Recommendation: Implement as a local catalog with the ability to register plugins from local paths. Add an extensibility point (interface) for future remote registry integration, but the primary implementation should work entirely locally with the plugins available on disk.

## Sources

### Primary (HIGH confidence)
- `Plugins/DataWarehouse.Plugins.DataMarketplace/DataMarketplacePlugin.cs` -- Complete DataMarketplace implementation (T83), verified record types, persistence patterns, message handling
- `DataWarehouse.GUI/Components/Pages/Marketplace.razor` -- Complete marketplace GUI page with all UI features
- `DataWarehouse.Kernel/Plugins/PluginLoader.cs` -- Complete plugin loading with security validation and hot reload
- `DataWarehouse.SDK/Services/PluginRegistry.cs` -- Plugin registry with type indexing and scoring
- `DataWarehouse.SDK/Utilities/PluginDetails.cs` -- Plugin descriptor, capability, dependency types
- `DataWarehouse.SDK/Contracts/IKernelInfrastructure.cs` -- `IPluginReloader` interface
- `Metadata/TODO.md` (lines 579-597) -- T57 feature list (7 features, all not started)
- `.planning/ROADMAP.md` -- Phase 17 success criteria (6 criteria)
- `.planning/REQUIREMENTS.md` -- MKTPL-01 requirement definition
- `Metadata/CLAUDE.md` -- Architecture rules, plugin isolation, base class hierarchy

### Secondary (MEDIUM confidence)
- `DataWarehouse.SDK/Contracts/Pipeline/PipelinePolicyContracts.cs` -- MarketplaceStagePolicy exists (line 2510-2521)
- DataMarketplace review system pattern -- Applicable to plugin reviews with domain adaptation

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH -- All infrastructure exists in codebase and was directly verified
- Architecture: HIGH -- Patterns derived from existing production code (DataMarketplace, PluginLoader)
- Pitfalls: HIGH -- Identified from actual code relationships (layer isolation, message bus wiring)
- Scope: HIGH -- T57 features directly read from TODO.md, success criteria from ROADMAP.md
- Open questions: MEDIUM -- Some kernel integration details need verification during implementation

**Research date:** 2026-02-11
**Valid until:** 2026-03-11 (stable domain, internal codebase)
