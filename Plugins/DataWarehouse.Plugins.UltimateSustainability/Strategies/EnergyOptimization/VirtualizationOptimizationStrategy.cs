namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyOptimization;

/// <summary>
/// Optimizes VM and container density for energy efficiency.
/// Monitors hypervisor and container runtime for consolidation opportunities.
/// </summary>
public sealed class VirtualizationOptimizationStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, HostInfo> _hosts = new();
    private readonly Dictionary<string, VmInfo> _vms = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "virtualization-optimization";
    /// <inheritdoc/>
    public override string DisplayName => "Virtualization Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.PredictiveAnalytics;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Optimizes VM and container density for energy efficiency through workload consolidation.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "virtualization", "vm", "container", "docker", "kubernetes", "hypervisor" };

    /// <summary>Target CPU utilization for hosts.</summary>
    public int TargetHostCpuPercent { get; set; } = 70;
    /// <summary>Minimum VMs per host before consolidation.</summary>
    public int MinVmsPerHost { get; set; } = 3;

    /// <summary>Registers a host.</summary>
    public void RegisterHost(string hostId, int cpuCores, long memoryBytes, double idlePowerWatts, double maxPowerWatts)
    {
        lock (_lock)
        {
            _hosts[hostId] = new HostInfo
            {
                HostId = hostId,
                CpuCores = cpuCores,
                MemoryBytes = memoryBytes,
                IdlePowerWatts = idlePowerWatts,
                MaxPowerWatts = maxPowerWatts,
                CurrentCpuPercent = 0,
                VmCount = 0
            };
        }
    }

    /// <summary>Registers a VM on a host.</summary>
    public void RegisterVm(string vmId, string hostId, int vCpus, long memoryBytes, double cpuUtilization)
    {
        lock (_lock)
        {
            _vms[vmId] = new VmInfo
            {
                VmId = vmId,
                HostId = hostId,
                VCpus = vCpus,
                MemoryBytes = memoryBytes,
                CpuUtilization = cpuUtilization
            };

            if (_hosts.TryGetValue(hostId, out var host))
            {
                host.VmCount++;
                host.CurrentCpuPercent = _vms.Values
                    .Where(v => v.HostId == hostId)
                    .Sum(v => v.CpuUtilization * v.VCpus / host.CpuCores);
            }
        }
        RecordOptimizationAction();
        EvaluateConsolidation();
    }

    /// <summary>Updates VM utilization.</summary>
    public void UpdateVmUtilization(string vmId, double cpuUtilization)
    {
        lock (_lock)
        {
            if (_vms.TryGetValue(vmId, out var vm))
            {
                _vms[vmId] = vm with { CpuUtilization = cpuUtilization };
                UpdateHostUtilization(vm.HostId);
            }
        }
        RecordSample(cpuUtilization, 0);
    }

    private void UpdateHostUtilization(string hostId)
    {
        if (_hosts.TryGetValue(hostId, out var host))
        {
            var totalCpu = _vms.Values
                .Where(v => v.HostId == hostId)
                .Sum(v => v.CpuUtilization * v.VCpus);
            host.CurrentCpuPercent = totalCpu / host.CpuCores;
        }
    }

    /// <summary>Gets consolidation recommendations.</summary>
    public IReadOnlyList<ConsolidationRecommendation> GetConsolidationRecommendations()
    {
        var recommendations = new List<ConsolidationRecommendation>();

        lock (_lock)
        {
            var underutilizedHosts = _hosts.Values
                .Where(h => h.CurrentCpuPercent < 30 && h.VmCount > 0)
                .OrderBy(h => h.CurrentCpuPercent)
                .ToList();

            var targetHosts = _hosts.Values
                .Where(h => h.CurrentCpuPercent < TargetHostCpuPercent - 20)
                .OrderByDescending(h => h.CurrentCpuPercent)
                .ToList();

            foreach (var source in underutilizedHosts)
            {
                var vmsOnSource = _vms.Values.Where(v => v.HostId == source.HostId).ToList();
                foreach (var target in targetHosts.Where(t => t.HostId != source.HostId))
                {
                    var availableCpu = (TargetHostCpuPercent - target.CurrentCpuPercent) / 100.0 * target.CpuCores;
                    var movableVms = vmsOnSource.Where(v => v.VCpus * v.CpuUtilization / 100.0 <= availableCpu).ToList();

                    if (movableVms.Any())
                    {
                        recommendations.Add(new ConsolidationRecommendation
                        {
                            SourceHostId = source.HostId,
                            TargetHostId = target.HostId,
                            VmIds = movableVms.Select(v => v.VmId).ToList(),
                            EstimatedPowerSavingsWatts = source.IdlePowerWatts * 0.5,
                            SourceHostUtilization = source.CurrentCpuPercent,
                            TargetHostUtilization = target.CurrentCpuPercent
                        });
                    }
                }
            }
        }

        return recommendations;
    }

    /// <summary>Gets power efficiency for a host.</summary>
    public double GetHostPowerEfficiency(string hostId)
    {
        lock (_lock)
        {
            if (!_hosts.TryGetValue(hostId, out var host)) return 0;
            var utilization = host.CurrentCpuPercent / 100.0;
            var power = host.IdlePowerWatts + (host.MaxPowerWatts - host.IdlePowerWatts) * utilization;
            return host.CurrentCpuPercent / power; // Work per watt
        }
    }

    private void EvaluateConsolidation()
    {
        ClearRecommendations();
        var recs = GetConsolidationRecommendations();

        if (recs.Count > 0)
        {
            var totalSavings = recs.Sum(r => r.EstimatedPowerSavingsWatts);
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-consolidate",
                Type = "VmConsolidation",
                Priority = 7,
                Description = $"{recs.Sum(r => r.VmIds.Count)} VMs can be consolidated for {totalSavings:F0}W savings.",
                EstimatedEnergySavingsWh = totalSavings * 24,
                CanAutoApply = false,
                Action = "consolidate-vms"
            });
        }
    }
}

/// <summary>Host information.</summary>
public sealed class HostInfo
{
    public required string HostId { get; init; }
    public required int CpuCores { get; init; }
    public required long MemoryBytes { get; init; }
    public required double IdlePowerWatts { get; init; }
    public required double MaxPowerWatts { get; init; }
    public double CurrentCpuPercent { get; set; }
    public int VmCount { get; set; }
}

/// <summary>VM information.</summary>
public sealed record VmInfo
{
    public required string VmId { get; init; }
    public required string HostId { get; init; }
    public required int VCpus { get; init; }
    public required long MemoryBytes { get; init; }
    public required double CpuUtilization { get; init; }
}

/// <summary>Consolidation recommendation.</summary>
public sealed record ConsolidationRecommendation
{
    public required string SourceHostId { get; init; }
    public required string TargetHostId { get; init; }
    public required List<string> VmIds { get; init; }
    public required double EstimatedPowerSavingsWatts { get; init; }
    public required double SourceHostUtilization { get; init; }
    public required double TargetHostUtilization { get; init; }
}
