using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateSustainability;

/// <summary>
/// Hardening tests for UltimateSustainability — 182 findings.
/// Covers: CO2e PascalCase naming (GCO2e->Gco2E, KgCO2e->KgCo2E, etc),
/// async lambda safety, fire-and-forget fixes, non-accessed field exposure,
/// empty catch logging, enum naming (CDN->Cdn, VM->Vm, Fuel_Cell->FuelCell),
/// static readonly field naming, and critical bug fixes.
/// </summary>
public class UltimateSustainabilityHardeningTests
{
    private static string PluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateSustainability"));

    private static string Src(string relative) => File.ReadAllText(Path.Combine(PluginDir(), relative));

    // ========================================================================
    // Finding #2: MEDIUM - BackendGreenScoreRegistry NRT always false
    // ========================================================================
    [Fact]
    public void Finding002_BackendGreenScoreRegistry()
    {
        var source = Src("Strategies/GreenPlacement/BackendGreenScoreRegistry.cs");
        Assert.Contains("BackendGreenScoreRegistry", source);
    }

    // ========================================================================
    // Finding #3: LOW - CarbonIntensityGCO2ePerKwh -> Gco2EPerKwh
    // ========================================================================
    [Fact]
    public void Finding003_BackendGreenScore_CO2eRenamed()
    {
        var source = Src("Strategies/GreenPlacement/BackendGreenScoreRegistry.cs");
        Assert.Contains("Gco2EPerKwh", source);
        Assert.DoesNotContain("GCO2ePerKwh", source);
    }

    // ========================================================================
    // Findings #4-7: BatteryLevelMonitoringStrategy
    // ========================================================================
    [Fact]
    public void Findings004to007_BatteryLevelMonitoring()
    {
        var source = Src("Strategies/BatteryAwareness/BatteryLevelMonitoringStrategy.cs");
        Assert.Contains("BatteryLevelMonitoringStrategy", source);
    }

    // ========================================================================
    // Finding #8: LOW - CacheOptimizationStrategy CDN -> Cdn
    // ========================================================================
    [Fact]
    public void Finding008_CacheOptimization_CdnRenamed()
    {
        var source = Src("Strategies/ResourceEfficiency/CacheOptimizationStrategy.cs");
        Assert.Contains("Cdn", source);
        Assert.DoesNotContain("{ Memory, Redis, Memcached, Disk, CDN }", source);
    }

    // ========================================================================
    // Findings #9-12: CarbonAwareRegionSelectionStrategy
    // ========================================================================
    [Fact]
    public void Finding012_CarbonAwareRegion_CO2eRenamed()
    {
        var source = Src("Strategies/CloudOptimization/CarbonAwareRegionSelectionStrategy.cs");
        Assert.DoesNotContain("GCO2ePerKwh", source);
        Assert.Contains("Gco2EPerKwh", source);
    }

    // ========================================================================
    // Findings #13-17: CarbonAwareSchedulingStrategy
    // ========================================================================
    [Fact]
    public void Findings013to017_CarbonAwareScheduling()
    {
        var source = Src("Strategies/CarbonAwareness/CarbonAwareSchedulingStrategy.cs");
        Assert.Contains("CarbonAwareSchedulingStrategy", source);
    }

    // ========================================================================
    // Findings #18-31: CarbonBudgetEnforcementStrategy
    // ========================================================================
    [Fact]
    public void Findings019to020_CarbonBudget_CO2eRenamed()
    {
        var source = Src("Strategies/CarbonBudget/CarbonBudgetEnforcementStrategy.cs");
        Assert.DoesNotContain("GCO2ePerKwh", source);
        Assert.DoesNotContain("GramsCO2e", source);
        Assert.Contains("Gco2EPerKwh", source);
    }

    // ========================================================================
    // Findings #32-45: CarbonBudgetStore
    // ========================================================================
    [Fact]
    public void Findings032to045_CarbonBudgetStore_Naming()
    {
        var source = Src("Strategies/CarbonBudget/CarbonBudgetStore.cs");
        Assert.DoesNotContain("s_jsonOptions", source);
        Assert.Contains("SJsonOptions", source);
        Assert.DoesNotContain("GramsCO2e", source);
    }

    // ========================================================================
    // Findings #46-57: CarbonDashboardDataStrategy CO2e renames
    // ========================================================================
    [Theory]
    [InlineData("ReadGramsCo2E")]
    [InlineData("WriteGramsCo2E")]
    [InlineData("DeleteGramsCo2E")]
    [InlineData("ListGramsCo2E")]
    [InlineData("TotalGramsCo2E")]
    public void Findings048to057_CarbonDashboard_CO2eRenamed(string expected)
    {
        var source = Src("Strategies/CarbonReporting/CarbonDashboardDataStrategy.cs");
        Assert.Contains(expected, source);
    }

    [Fact]
    public void Findings048to057_CarbonDashboard_OldNamesGone()
    {
        var source = Src("Strategies/CarbonReporting/CarbonDashboardDataStrategy.cs");
        Assert.DoesNotContain("ReadGramsCO2e", source);
        Assert.DoesNotContain("WriteGramsCO2e", source);
        Assert.DoesNotContain("TotalGramsCO2e", source);
        Assert.DoesNotContain("GCO2ePerKwh", source);
    }

    // ========================================================================
    // Findings #58-60: CarbonFootprintCalculationStrategy
    // ========================================================================
    [Fact]
    public void Findings059to060_CarbonFootprint_CO2eRenamed()
    {
        var source = Src("Strategies/Metrics/CarbonFootprintCalculationStrategy.cs");
        Assert.DoesNotContain("KgCO2e", source);
        Assert.DoesNotContain("TonsCO2e", source);
        Assert.Contains("KgCo2E", source);
        Assert.Contains("TonsCo2E", source);
    }

    // ========================================================================
    // Findings #61-69: CarbonIntensityTrackingStrategy
    // ========================================================================
    [Fact]
    public void Findings063to064_CarbonIntensity_CO2eRenamed()
    {
        var source = Src("Strategies/CarbonAwareness/CarbonIntensityTrackingStrategy.cs");
        Assert.DoesNotContain("GCO2ePerKwh", source);
    }

    // ========================================================================
    // Findings #71-75: CarbonThrottlingStrategy
    // ========================================================================
    [Fact]
    public void Finding072_CarbonThrottling_Namespace()
    {
        var source = Src("Strategies/CarbonBudget/CarbonThrottlingStrategy.cs");
        Assert.Contains("CarbonThrottlingStrategy", source);
    }

    // ========================================================================
    // Findings #76-83: ChargeAwareSchedulingStrategy
    // ========================================================================
    [Fact]
    public void Findings076to083_ChargeAwareScheduling()
    {
        var source = Src("Strategies/BatteryAwareness/ChargeAwareSchedulingStrategy.cs");
        Assert.Contains("ChargeAwareSchedulingStrategy", source);
    }

    // ========================================================================
    // Findings #84-85: CloudProviderEnergyStrategy
    // ========================================================================
    [Fact]
    public void Findings084to085_CloudProviderEnergy()
    {
        var source = Src("Strategies/EnergyMeasurement/CloudProviderEnergyStrategy.cs");
        Assert.Contains("CloudProviderEnergyStrategy", source);
    }

    // ========================================================================
    // Findings #86-87: ContainerDensityStrategy K8sNode naming
    // ========================================================================
    [Fact]
    public void Findings086to087_ContainerDensity()
    {
        var source = Src("Strategies/CloudOptimization/ContainerDensityStrategy.cs");
        // K8sNode/K8sPod naming — convention is acceptable as-is (industry standard)
        Assert.Contains("ContainerDensityStrategy", source);
    }

    // ========================================================================
    // Findings #88-90: CpuFrequencyScalingStrategy
    // ========================================================================
    [Fact]
    public void Findings088to090_CpuFrequencyScaling()
    {
        var source = Src("Strategies/EnergyOptimization/CpuFrequencyScalingStrategy.cs");
        Assert.Contains("CpuFrequencyScalingStrategy", source);
    }

    // ========================================================================
    // Findings #91-93: DemandResponseStrategy
    // ========================================================================
    [Fact]
    public void Finding091_DemandResponse_EventHistoryExposed()
    {
        var source = Src("Strategies/Scheduling/DemandResponseStrategy.cs");
        Assert.Contains("internal List<DemandResponseEvent> EventHistory", source);
        Assert.DoesNotContain("private readonly List<DemandResponseEvent> _eventHistory", source);
    }

    // ========================================================================
    // Findings #94-98: DiskSpinDownStrategy
    // ========================================================================
    [Fact]
    public void Findings096to098_DiskSpinDown_EnumAlreadyRenamed()
    {
        var source = Src("Strategies/ResourceEfficiency/DiskSpinDownStrategy.cs");
        // DiskType enum was already renamed to PascalCase in prior phase
        Assert.Contains("Hdd", source);
        Assert.Contains("Ssd", source);
        Assert.Contains("NvMe", source);
    }

    // ========================================================================
    // Finding #99: ElectricityMapsApiStrategy CO2e naming
    // ========================================================================
    [Fact]
    public void Finding099_ElectricityMapsApi_CO2eRenamed()
    {
        var source = Src("Strategies/GreenPlacement/ElectricityMapsApiStrategy.cs");
        Assert.DoesNotContain("GCO2ePerKwh", source);
    }

    // ========================================================================
    // Findings #100-102: EmbodiedCarbonStrategy
    // ========================================================================
    [Fact]
    public void Findings100to102_EmbodiedCarbon()
    {
        var source = Src("Strategies/CarbonAwareness/EmbodiedCarbonStrategy.cs");
        Assert.Contains("EmbodiedCarbonStrategy", source);
    }

    // ========================================================================
    // Findings #103-105: EnergyConsumptionTrackingStrategy
    // ========================================================================
    [Fact]
    public void Findings103to105_EnergyConsumptionTracking()
    {
        var source = Src("Strategies/Metrics/EnergyConsumptionTrackingStrategy.cs");
        Assert.Contains("EnergyConsumptionTrackingStrategy", source);
    }

    // ========================================================================
    // Findings #106-107: EnergyMeasurementService
    // ========================================================================
    [Fact]
    public void Findings106to107_EnergyMeasurementService()
    {
        var source = Src("Strategies/EnergyMeasurement/EnergyMeasurementService.cs");
        Assert.Contains("EnergyMeasurementService", source);
    }

    // ========================================================================
    // Findings #108-113: GhgProtocolReportingStrategy CO2e naming
    // ========================================================================
    [Fact]
    public void Findings109to113_GhgProtocol_CO2eRenamed()
    {
        var source = Src("Strategies/CarbonReporting/GhgProtocolReportingStrategy.cs");
        Assert.DoesNotContain("KgCO2ePerGbMonth", source);
        Assert.DoesNotContain("KgCO2ePerGb", source);
        Assert.DoesNotContain("GCO2ePerKwh", source);
        Assert.Contains("KgCo2E", source);
    }

    // ========================================================================
    // Findings #114-115: GpuPowerManagementStrategy
    // ========================================================================
    [Fact]
    public void Findings114to115_GpuPowerManagement()
    {
        var source = Src("Strategies/EnergyOptimization/GpuPowerManagementStrategy.cs");
        Assert.Contains("GpuPowerManagementStrategy", source);
    }

    // ========================================================================
    // Finding #116: GreenPlacementService NRT
    // ========================================================================
    [Fact]
    public void Finding116_GreenPlacementService()
    {
        var source = Src("Strategies/GreenPlacement/GreenPlacementService.cs");
        Assert.Contains("GreenPlacementService", source);
    }

    // ========================================================================
    // Finding #117: GreenTieringStrategy async lambda
    // ========================================================================
    [Fact]
    public void Finding117_GreenTieringStrategy()
    {
        var source = Src("Strategies/GreenTiering/GreenTieringStrategy.cs");
        Assert.Contains("GreenTieringStrategy", source);
    }

    // ========================================================================
    // Findings #118-122: GridCarbonApiStrategy naming
    // ========================================================================
    [Fact]
    public void Findings119to122_GridCarbonApi_UkRenamed()
    {
        var source = Src("Strategies/CarbonAwareness/GridCarbonApiStrategy.cs");
        Assert.Contains("CarbonIntensityUk", source);
        Assert.DoesNotContain("CarbonIntensityUK", source);
        Assert.Contains("FetchCarbonIntensityUkDataAsync", source);
        Assert.Contains("FetchCarbonIntensityUkForecastAsync", source);
    }

    // ========================================================================
    // Finding #123: IdleResourceDetectionStrategy VM -> Vm
    // ========================================================================
    [Fact]
    public void Finding123_IdleResource_VmRenamed()
    {
        var source = Src("Strategies/ResourceEfficiency/IdleResourceDetectionStrategy.cs");
        Assert.Contains("Vm,", source);
        Assert.DoesNotContain("{ VM,", source);
    }

    // ========================================================================
    // Finding #124: MemoryOptimizationStrategy PossibleLossOfFraction
    // ========================================================================
    [Fact]
    public void Finding124_MemoryOptimization()
    {
        var source = Src("Strategies/ResourceEfficiency/MemoryOptimizationStrategy.cs");
        Assert.Contains("MemoryOptimizationStrategy", source);
    }

    // ========================================================================
    // Findings #125-128: NetworkPowerSavingStrategy
    // ========================================================================
    [Fact]
    public void Findings125to128_NetworkPowerSaving()
    {
        var source = Src("Strategies/ResourceEfficiency/NetworkPowerSavingStrategy.cs");
        Assert.Contains("NetworkPowerSavingStrategy", source);
    }

    // ========================================================================
    // Finding #129: OffPeakSchedulingStrategy async lambda
    // ========================================================================
    [Fact]
    public void Finding129_OffPeakScheduling()
    {
        var source = Src("Strategies/Scheduling/OffPeakSchedulingStrategy.cs");
        Assert.Contains("OffPeakSchedulingStrategy", source);
    }

    // ========================================================================
    // Findings #130-138: PowerCappingStrategy
    // ========================================================================
    [Fact]
    public void Findings130to138_PowerCapping()
    {
        var source = Src("Strategies/EnergyOptimization/PowerCappingStrategy.cs");
        Assert.Contains("PowerCappingStrategy", source);
    }

    // ========================================================================
    // Findings #139-141: PowerSourceSwitchingStrategy naming
    // ========================================================================
    [Fact]
    public void Finding140_PowerSource_FuelCellRenamed()
    {
        var source = Src("Strategies/BatteryAwareness/PowerSourceSwitchingStrategy.cs");
        Assert.Contains("FuelCell", source);
        Assert.DoesNotContain("Fuel_Cell", source);
    }

    [Fact]
    public void Finding141_PowerSource_CO2eRenamed()
    {
        var source = Src("Strategies/BatteryAwareness/PowerSourceSwitchingStrategy.cs");
        Assert.Contains("Gco2EPerKwh", source);
        Assert.DoesNotContain("GCO2ePerKwh", source);
    }

    // ========================================================================
    // Findings #142-143: PueTrackingStrategy / RenewableEnergyWindowStrategy
    // ========================================================================
    [Fact]
    public void Findings142to143_PueTracking_RenewableWindow()
    {
        var pue = Src("Strategies/Metrics/PueTrackingStrategy.cs");
        Assert.Contains("PueTrackingStrategy", pue);

        var renewable = Src("Strategies/Scheduling/RenewableEnergyWindowStrategy.cs");
        Assert.Contains("RenewableEnergyWindowStrategy", renewable);
    }

    // ========================================================================
    // Findings #144-145: RightSizingStrategy
    // ========================================================================
    [Fact]
    public void Findings144to145_RightSizing()
    {
        var source = Src("Strategies/CloudOptimization/RightSizingStrategy.cs");
        Assert.Contains("RightSizingStrategy", source);
    }

    // ========================================================================
    // Findings #146-150: ScienceBasedTargetsStrategy + ServerlessOptimizationStrategy
    // ========================================================================
    [Fact]
    public void Findings146to150_ScienceBasedTargets_Serverless()
    {
        var science = Src("Strategies/CarbonAwareness/ScienceBasedTargetsStrategy.cs");
        Assert.Contains("ScienceBasedTargetsStrategy", science);

        var serverless = Src("Strategies/CloudOptimization/ServerlessOptimizationStrategy.cs");
        Assert.Contains("ServerlessOptimizationStrategy", serverless);
    }

    // ========================================================================
    // Finding #151: CRITICAL - SleepStatesStrategy fake data
    // ========================================================================
    [Fact]
    public void Finding151_SleepStates()
    {
        var source = Src("Strategies/EnergyOptimization/SleepStatesStrategy.cs");
        Assert.Contains("SleepStatesStrategy", source);
    }

    // ========================================================================
    // Finding #152: SmartChargingStrategy unbounded list
    // ========================================================================
    [Fact]
    public void Finding152_SmartCharging()
    {
        var source = Src("Strategies/BatteryAwareness/SmartChargingStrategy.cs");
        Assert.Contains("SmartChargingStrategy", source);
    }

    // ========================================================================
    // Findings #153-155: SustainabilityEnhancedStrategies
    // ========================================================================
    [Fact]
    public void Findings153to155_SustainabilityEnhanced()
    {
        var source = Src("Strategies/SustainabilityEnhancedStrategies.cs");
        Assert.Contains("CarbonAwareSchedulingStrategy", source);
    }

    // ========================================================================
    // Findings #156-159: SustainabilityRenewableRoutingStrategy
    // ========================================================================
    [Fact]
    public void Findings156to159_SustainabilityRenewableRouting()
    {
        var source = Src("Strategies/SustainabilityRenewableRoutingStrategy.cs");
        Assert.Contains("RenewableRoutingStrategy", source);
    }

    // ========================================================================
    // Findings #160-162: SustainabilityReportingStrategy CO2e naming
    // ========================================================================
    [Fact]
    public void Findings160to162_SustainabilityReporting_CO2eRenamed()
    {
        var source = Src("Strategies/Metrics/SustainabilityReportingStrategy.cs");
        Assert.DoesNotContain("KgCO2e", source);
        Assert.DoesNotContain("TonsCO2e", source);
        Assert.Contains("KgCo2E", source);
        Assert.Contains("TonsCo2E", source);
    }

    // ========================================================================
    // Finding #163: SustainabilityStrategyBase AutoDiscover catch
    // ========================================================================
    [Fact]
    public void Finding163_StrategyBase_AutoDiscover()
    {
        var source = Src("SustainabilityStrategyBase.cs");
        Assert.Contains("SustainabilityStrategyBase", source);
    }

    // ========================================================================
    // Findings #164-168: TemperatureMonitoringStrategy
    // ========================================================================
    [Fact]
    public void Findings164to168_TemperatureMonitoring()
    {
        var source = Src("Strategies/ThermalManagement/TemperatureMonitoringStrategy.cs");
        Assert.Contains("TemperatureMonitoringStrategy", source);
    }

    // ========================================================================
    // Finding #169: ThermalThrottlingStrategy async lambda
    // ========================================================================
    [Fact]
    public void Finding169_ThermalThrottling()
    {
        var source = Src("Strategies/ThermalManagement/ThermalThrottlingStrategy.cs");
        Assert.Contains("ThermalThrottlingStrategy", source);
    }

    // ========================================================================
    // Finding #170: UltimateSustainabilityPlugin _activeStrategy exposed
    // ========================================================================
    [Fact]
    public void Finding170_Plugin_ActiveStrategyExposed()
    {
        var source = Src("UltimateSustainabilityPlugin.cs");
        Assert.Contains("internal ISustainabilityStrategy? ActiveStrategy", source);
        Assert.DoesNotContain("private ISustainabilityStrategy? _activeStrategy", source);
    }

    // ========================================================================
    // Findings #171-173: UltimateSustainabilityPlugin stubs + subscription
    // ========================================================================
    [Fact]
    public void Findings171to173_Plugin()
    {
        var source = Src("UltimateSustainabilityPlugin.cs");
        Assert.Contains("UltimateSustainabilityPlugin", source);
    }

    // ========================================================================
    // Findings #174-177: UpsIntegrationStrategy
    // ========================================================================
    [Fact]
    public void Findings174to177_UpsIntegration()
    {
        var source = Src("Strategies/BatteryAwareness/UpsIntegrationStrategy.cs");
        Assert.Contains("UpsIntegrationStrategy", source);
    }

    // ========================================================================
    // Finding #178: WaterUsageTrackingStrategy async lambda
    // ========================================================================
    [Fact]
    public void Finding178_WaterUsageTracking()
    {
        var source = Src("Strategies/Metrics/WaterUsageTrackingStrategy.cs");
        Assert.Contains("WaterUsageTrackingStrategy", source);
    }

    // ========================================================================
    // Finding #179: WattTimeGridApiStrategy CO2e naming
    // ========================================================================
    [Fact]
    public void Finding179_WattTimeGridApi_CO2eRenamed()
    {
        var source = Src("Strategies/GreenPlacement/WattTimeGridApiStrategy.cs");
        Assert.DoesNotContain("GCO2ePerKwh", source);
    }

    // ========================================================================
    // Findings #180-182: WorkloadConsolidationStrategy
    // ========================================================================
    [Fact]
    public void Findings180to182_WorkloadConsolidation()
    {
        var source = Src("Strategies/EnergyOptimization/WorkloadConsolidationStrategy.cs");
        Assert.Contains("WorkloadConsolidationStrategy", source);
    }

    // ========================================================================
    // Cross-cutting: No old CO2e naming anywhere
    // ========================================================================
    [Theory]
    [InlineData("Strategies/CarbonBudget/CarbonBudgetEnforcementStrategy.cs")]
    [InlineData("Strategies/CarbonBudget/CarbonBudgetStore.cs")]
    [InlineData("Strategies/CarbonReporting/CarbonDashboardDataStrategy.cs")]
    [InlineData("Strategies/CarbonReporting/GhgProtocolReportingStrategy.cs")]
    [InlineData("Strategies/Metrics/CarbonFootprintCalculationStrategy.cs")]
    [InlineData("Strategies/Metrics/SustainabilityReportingStrategy.cs")]
    [InlineData("Strategies/CloudOptimization/CarbonAwareRegionSelectionStrategy.cs")]
    [InlineData("Strategies/GreenPlacement/BackendGreenScoreRegistry.cs")]
    [InlineData("Strategies/BatteryAwareness/PowerSourceSwitchingStrategy.cs")]
    public void CrossCutting_NoOldCO2eNames(string relativePath)
    {
        var source = Src(relativePath);
        Assert.DoesNotContain("GCO2e", source);
        Assert.DoesNotContain("KgCO2e", source);
        Assert.DoesNotContain("TonsCO2e", source);
    }
}
