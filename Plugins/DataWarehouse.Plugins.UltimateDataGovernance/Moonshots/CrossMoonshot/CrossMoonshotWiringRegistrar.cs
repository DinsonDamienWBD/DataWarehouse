using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Moonshots;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Moonshots.CrossMoonshot;

/// <summary>
/// Central registrar for all cross-moonshot event wiring.
/// Creates, registers, and manages the lifecycle of all wiring instances.
/// Each wiring bridges two moonshots via message bus events; the registrar
/// only activates wirings where BOTH involved moonshots are enabled.
///
/// Called during UltimateDataGovernance plugin initialization, after all
/// moonshot plugins have registered their bus handlers.
/// </summary>
public sealed class CrossMoonshotWiringRegistrar
{
    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    private readonly List<WiringEntry> _registeredWirings = new();

    /// <summary>
    /// Tracks a wiring instance with its metadata for lifecycle management.
    /// </summary>
    private sealed record WiringEntry(
        string Name,
        MoonshotId FirstMoonshot,
        MoonshotId SecondMoonshot,
        Func<Task> UnregisterAsync);

    public CrossMoonshotWiringRegistrar(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILoggerFactory loggerFactory)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<CrossMoonshotWiringRegistrar>();
    }

    /// <summary>
    /// Creates and registers all cross-moonshot wirings.
    /// Only wirings where both involved moonshots are enabled will be activated.
    /// </summary>
    public async Task RegisterAllAsync(CancellationToken ct)
    {
        _logger.LogInformation("CrossMoonshotWiringRegistrar: beginning registration of cross-moonshot wirings");

        // 1. TagConsciousnessWiring: DataConsciousness -> UniversalTags
        await TryRegisterAsync(
            "TagConsciousnessWiring",
            MoonshotId.DataConsciousness,
            MoonshotId.UniversalTags,
            () =>
            {
                var wiring = new TagConsciousnessWiring(
                    _messageBus, _config, _loggerFactory.CreateLogger<TagConsciousnessWiring>());
                return (wiring.RegisterAsync(ct), wiring.UnregisterAsync);
            }, ct);

        // 2. ComplianceSovereigntyWiring: SovereigntyMesh -> CompliancePassports
        await TryRegisterAsync(
            "ComplianceSovereigntyWiring",
            MoonshotId.SovereigntyMesh,
            MoonshotId.CompliancePassports,
            () =>
            {
                var wiring = new ComplianceSovereigntyWiring(
                    _messageBus, _config, _loggerFactory.CreateLogger<ComplianceSovereigntyWiring>());
                return (wiring.RegisterAsync(ct), wiring.UnregisterAsync);
            }, ct);

        // 3. PlacementCarbonWiring: CarbonAwareLifecycle -> ZeroGravityStorage
        await TryRegisterAsync(
            "PlacementCarbonWiring",
            MoonshotId.CarbonAwareLifecycle,
            MoonshotId.ZeroGravityStorage,
            () =>
            {
                var wiring = new PlacementCarbonWiring(
                    _messageBus, _config, _loggerFactory.CreateLogger<PlacementCarbonWiring>());
                return (wiring.RegisterAsync(ct), wiring.UnregisterAsync);
            }, ct);

        // 4. SyncConsciousnessWiring: DataConsciousness -> SemanticSync
        await TryRegisterAsync(
            "SyncConsciousnessWiring",
            MoonshotId.DataConsciousness,
            MoonshotId.SemanticSync,
            () =>
            {
                var wiring = new SyncConsciousnessWiring(
                    _messageBus, _config, _loggerFactory.CreateLogger<SyncConsciousnessWiring>());
                return (wiring.RegisterAsync(ct), wiring.UnregisterAsync);
            }, ct);

        // 5. TimeLockComplianceWiring: CompliancePassports -> CryptoTimeLocks
        await TryRegisterAsync(
            "TimeLockComplianceWiring",
            MoonshotId.CompliancePassports,
            MoonshotId.CryptoTimeLocks,
            () =>
            {
                var wiring = new TimeLockComplianceWiring(
                    _messageBus, _config, _loggerFactory.CreateLogger<TimeLockComplianceWiring>());
                return (wiring.RegisterAsync(ct), wiring.UnregisterAsync);
            }, ct);

        // 6. ChaosImmunityWiring: ChaosVaccination -> SovereigntyMesh
        await TryRegisterAsync(
            "ChaosImmunityWiring",
            MoonshotId.ChaosVaccination,
            MoonshotId.SovereigntyMesh,
            () =>
            {
                var wiring = new ChaosImmunityWiring(
                    _messageBus, _config, _loggerFactory.CreateLogger<ChaosImmunityWiring>());
                return (wiring.RegisterAsync(ct), wiring.UnregisterAsync);
            }, ct);

        // 7. FabricPlacementWiring: ZeroGravityStorage -> UniversalFabric
        await TryRegisterAsync(
            "FabricPlacementWiring",
            MoonshotId.ZeroGravityStorage,
            MoonshotId.UniversalFabric,
            () =>
            {
                var wiring = new FabricPlacementWiring(
                    _messageBus, _config, _loggerFactory.CreateLogger<FabricPlacementWiring>());
                return (wiring.RegisterAsync(ct), wiring.UnregisterAsync);
            }, ct);

        _logger.LogInformation(
            "CrossMoonshotWiringRegistrar: registration complete. {ActiveCount} of 7 wirings activated",
            _registeredWirings.Count);
    }

    /// <summary>
    /// Unregisters all active cross-moonshot wirings and releases resources.
    /// </summary>
    public async Task UnregisterAllAsync()
    {
        var count = _registeredWirings.Count;

        foreach (var entry in _registeredWirings)
        {
            try
            {
                await entry.UnregisterAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CrossMoonshotWiringRegistrar: error unregistering {WiringName}", entry.Name);
            }
        }

        _registeredWirings.Clear();
        _logger.LogInformation("CrossMoonshotWiringRegistrar: unregistered {Count} cross-moonshot wirings", count);
    }

    /// <summary>
    /// Returns the names of all currently active cross-moonshot wirings.
    /// Useful for dashboard and diagnostic displays.
    /// </summary>
    public IReadOnlyList<string> GetActiveWirings()
    {
        return _registeredWirings.Select(e => e.Name).ToList().AsReadOnly();
    }

    /// <summary>
    /// Attempts to register a single wiring. Checks if both moonshots are enabled
    /// before creating and registering the wiring instance.
    /// </summary>
    private async Task TryRegisterAsync(
        string name,
        MoonshotId first,
        MoonshotId second,
        Func<(Task RegisterTask, Func<Task> UnregisterFunc)> factory,
        CancellationToken ct)
    {
        if (!_config.IsEnabled(first) || !_config.IsEnabled(second))
        {
            var disabledMoonshot = !_config.IsEnabled(first) ? first : second;
            _logger.LogInformation(
                "CrossMoonshotWiringRegistrar: skipping {WiringName} -- {MoonshotId} is disabled",
                name, disabledMoonshot);
            return;
        }

        try
        {
            var (registerTask, unregisterFunc) = factory();
            await registerTask;

            _registeredWirings.Add(new WiringEntry(name, first, second, unregisterFunc));
            _logger.LogInformation("CrossMoonshotWiringRegistrar: registered {WiringName}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CrossMoonshotWiringRegistrar: failed to register {WiringName}, skipping",
                name);
        }
    }
}
