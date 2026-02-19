// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.TamperProof.TimeLock;

namespace DataWarehouse.Plugins.TamperProof.Registration;

/// <summary>
/// Registers all Phase 59 time-lock providers, ransomware vaccination service,
/// and policy engine into the TamperProof plugin lifecycle. Publishes provider
/// and vaccination availability to the message bus for cross-plugin discovery.
/// </summary>
/// <remarks>
/// Components registered:
/// <list type="bullet">
///   <item>SoftwareTimeLockProvider - UTC clock-enforced locks for single-node</item>
///   <item>HsmTimeLockProvider - HSM-backed key-release time enforcement</item>
///   <item>CloudTimeLockProvider - S3 Object Lock / Azure Immutable Blob / GCS Retention</item>
///   <item>RansomwareVaccinationService - Multi-layer anti-ransomware orchestrator</item>
///   <item>TimeLockPolicyEngine - Compliance-based auto-assignment of lock durations</item>
/// </list>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock integration")]
public static class TimeLockRegistration
{
    /// <summary>
    /// Instantiates and registers all three time-lock providers, publishing each to the
    /// message bus topic <c>timelock.provider.register</c>.
    /// </summary>
    /// <param name="bus">The message bus for provider registration events.</param>
    /// <param name="sourcePluginId">The ID of the publishing plugin.</param>
    /// <returns>List of instantiated time-lock providers.</returns>
    public static async Task<IReadOnlyList<TimeLockProviderPluginBase>> RegisterTimeLockProviders(
        IMessageBus bus,
        string sourcePluginId = "com.datawarehouse.tamperproof")
    {
        ArgumentNullException.ThrowIfNull(bus);

        var providers = new TimeLockProviderPluginBase[]
        {
            new SoftwareTimeLockProvider(),
            new HsmTimeLockProvider(),
            new CloudTimeLockProvider(),
        };

        foreach (var provider in providers)
        {
            await bus.PublishAsync("timelock.provider.register", new PluginMessage
            {
                Type = "timelock.provider.register",
                Source = sourcePluginId,
                Payload = new Dictionary<string, object>
                {
                    ["Id"] = provider.Id,
                    ["Name"] = provider.Name,
                    ["TimeLockMode"] = provider.DefaultMode.ToString(),
                    ["Capabilities"] = new Dictionary<string, object>
                    {
                        ["SupportsLock"] = true,
                        ["SupportsUnlock"] = true,
                        ["SupportsExtend"] = true,
                        ["SupportsEmergencyUnlock"] = true,
                        ["SupportsTamperDetection"] = true,
                    },
                },
            });
        }

        return providers;
    }

    /// <summary>
    /// Instantiates the ransomware vaccination service and publishes its availability
    /// to the message bus topic <c>timelock.vaccination.available</c>.
    /// </summary>
    /// <param name="bus">The message bus for vaccination registration and coordination.</param>
    /// <param name="sourcePluginId">The ID of the publishing plugin.</param>
    /// <returns>The instantiated vaccination service.</returns>
    public static async Task<RansomwareVaccinationService> RegisterVaccinationService(
        IMessageBus bus,
        string sourcePluginId = "com.datawarehouse.tamperproof")
    {
        ArgumentNullException.ThrowIfNull(bus);

        var vaccinationService = new RansomwareVaccinationService(bus);

        await bus.PublishAsync("timelock.vaccination.available", new PluginMessage
        {
            Type = "timelock.vaccination.available",
            Source = sourcePluginId,
            Payload = new Dictionary<string, object>
            {
                ["ServiceId"] = "tamperproof.vaccination.ransomware",
                ["ServiceName"] = "Ransomware Vaccination Service",
                ["Capabilities"] = new Dictionary<string, object>
                {
                    ["SupportsVaccination"] = true,
                    ["SupportsBatchScan"] = true,
                    ["SupportsThreatDashboard"] = true,
                    ["SupportsThreatScoring"] = true,
                    ["ProtectionLayers"] = new[] { "time-lock", "integrity-hash", "pqc-signature", "blockchain-anchor" },
                },
            },
        });

        return vaccinationService;
    }

    /// <summary>
    /// Instantiates the time-lock policy engine with built-in compliance rules
    /// (HIPAA, PCI-DSS, GDPR, SOX, Classified, Default).
    /// </summary>
    /// <returns>The instantiated policy engine with default rules.</returns>
    public static TimeLockPolicyEngine RegisterPolicyEngine()
    {
        return new TimeLockPolicyEngine();
    }

    /// <summary>
    /// Publishes an aggregate capability registration for the entire time-lock subsystem
    /// to the message bus topic <c>timelock.subsystem.capabilities</c>.
    /// </summary>
    /// <param name="bus">The message bus to publish on.</param>
    /// <param name="providerCount">Number of registered providers.</param>
    /// <param name="sourcePluginId">The ID of the publishing plugin.</param>
    /// <returns>A task that completes when the capability is published.</returns>
    public static async Task PublishTimeLockCapabilities(
        IMessageBus bus,
        int providerCount = 3,
        string sourcePluginId = "com.datawarehouse.tamperproof")
    {
        ArgumentNullException.ThrowIfNull(bus);

        await bus.PublishAsync("timelock.subsystem.capabilities", new PluginMessage
        {
            Type = "timelock.subsystem.capabilities",
            Source = sourcePluginId,
            Payload = new Dictionary<string, object>
            {
                ["ProviderCount"] = providerCount,
                ["Modes"] = new[] { "Software", "HardwareHsm", "CloudNative" },
                ["VaccinationAvailable"] = true,
                ["PolicyEngineAvailable"] = true,
                ["ComplianceRules"] = new[] { "HIPAA", "PCI-DSS", "GDPR", "SOX", "Classified", "Default" },
                ["BusTopics"] = new[]
                {
                    TimeLockMessageBusIntegration.TimeLockLocked,
                    TimeLockMessageBusIntegration.TimeLockUnlocked,
                    TimeLockMessageBusIntegration.TimeLockExtended,
                    TimeLockMessageBusIntegration.TimeLockTamperDetected,
                    TimeLockMessageBusIntegration.TimeLockVaccinationScan,
                },
            },
        });
    }
}
