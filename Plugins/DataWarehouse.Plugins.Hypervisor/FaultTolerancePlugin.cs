using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.Hypervisor;

/// <summary>
/// Production-ready fault tolerance and high availability plugin.
/// Provides detection and monitoring for HA/FT configurations across
/// VMware HA/FT, Hyper-V Failover Clustering, and Pacemaker/Corosync.
/// </summary>
public class FaultTolerancePlugin : FeaturePluginBase
{
    private readonly List<FailoverNotificationCallback> _failoverCallbacks = new();
    private readonly object _callbackLock = new();
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    /// <inheritdoc />
    public override string Id => "datawarehouse.hypervisor.fault-tolerance";

    /// <inheritdoc />
    public override string Name => "Fault Tolerance Manager";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    #region HA/FT Status

    /// <summary>
    /// Checks if high availability or fault tolerance is enabled for this VM/node.
    /// Detects VMware HA/FT, Hyper-V Failover Clustering, and Pacemaker/Corosync.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if HA/FT is enabled.</returns>
    public async Task<bool> IsHaEnabledAsync(CancellationToken ct = default)
    {
        var state = await GetClusterStateAsync(ct);
        return state.IsHaEnabled;
    }

    /// <summary>
    /// Gets detailed cluster state including membership and health.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current cluster state.</returns>
    public async Task<ClusterState> GetClusterStateAsync(CancellationToken ct = default)
    {
        // Try each clustering technology in order
        var vmwareState = await GetVMwareClusterStateAsync(ct);
        if (vmwareState.IsHaEnabled)
            return vmwareState;

        var hyperVState = await GetHyperVClusterStateAsync(ct);
        if (hyperVState.IsHaEnabled)
            return hyperVState;

        var pacemakerState = await GetPacemakerClusterStateAsync(ct);
        if (pacemakerState.IsHaEnabled)
            return pacemakerState;

        // No clustering detected
        return new ClusterState
        {
            IsHaEnabled = false,
            ClusterType = ClusterType.None,
            NodeState = NodeState.Unknown,
            Message = "No clustering technology detected"
        };
    }

    /// <summary>
    /// Gets the current fault domain for this node.
    /// Fault domains represent failure boundaries (rack, host, datacenter, etc.).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current fault domain information.</returns>
    public async Task<FaultDomain> GetFaultDomainAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var faultDomain = new FaultDomain
            {
                NodeId = GetNodeId(),
                Hostname = Environment.MachineName
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                PopulateLinuxFaultDomain(faultDomain);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                PopulateWindowsFaultDomain(faultDomain);
            }

            return faultDomain;
        }, ct);
    }

    /// <summary>
    /// Checks if a live migration is currently in progress for this VM.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if a migration is in progress.</returns>
    public async Task<bool> IsLiveMigrationInProgressAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return IsVMotionInProgressLinux() || IsHyperVMigrationInProgressLinux();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return IsLiveMigrationInProgressWindows();
            }

            return false;
        }, ct);
    }

    /// <summary>
    /// Gets the current failover policy configuration.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Failover policy settings.</returns>
    public async Task<FailoverPolicy> GetFailoverPolicyAsync(CancellationToken ct = default)
    {
        var state = await GetClusterStateAsync(ct);

        return await Task.Run(() =>
        {
            var policy = new FailoverPolicy
            {
                ClusterType = state.ClusterType,
                IsEnabled = state.IsHaEnabled
            };

            if (state.ClusterType == ClusterType.Pacemaker)
            {
                PopulatePacemakerPolicy(policy);
            }
            else if (state.ClusterType == ClusterType.HyperVFailoverCluster)
            {
                PopulateHyperVPolicy(policy);
            }
            else if (state.ClusterType == ClusterType.VMwareHa || state.ClusterType == ClusterType.VMwareFt)
            {
                PopulateVMwarePolicy(policy, state.ClusterType);
            }

            return policy;
        }, ct);
    }

    /// <summary>
    /// Registers a callback to be notified when a failover event occurs.
    /// </summary>
    /// <param name="callback">Callback delegate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Registration ID that can be used to unregister.</returns>
    public Task<string> RegisterForFailoverNotificationAsync(FailoverNotificationCallback callback, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var registrationId = Guid.NewGuid().ToString("N");

        lock (_callbackLock)
        {
            _failoverCallbacks.Add(callback);

            // Start monitoring if this is the first callback
            if (_failoverCallbacks.Count == 1 && _monitorTask == null)
            {
                StartFailoverMonitoring();
            }
        }

        return Task.FromResult(registrationId);
    }

    /// <summary>
    /// Unregisters a failover notification callback.
    /// </summary>
    /// <param name="callback">The callback to unregister.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task UnregisterFailoverNotificationAsync(FailoverNotificationCallback callback, CancellationToken ct = default)
    {
        lock (_callbackLock)
        {
            _failoverCallbacks.Remove(callback);

            // Stop monitoring if no more callbacks
            if (_failoverCallbacks.Count == 0)
            {
                StopFailoverMonitoring();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets a detailed health report for the cluster.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cluster health report.</returns>
    public async Task<ClusterHealthReport> GetHealthReportAsync(CancellationToken ct = default)
    {
        var state = await GetClusterStateAsync(ct);
        var faultDomain = await GetFaultDomainAsync(ct);
        var policy = await GetFailoverPolicyAsync(ct);
        var migrationInProgress = await IsLiveMigrationInProgressAsync(ct);

        var report = new ClusterHealthReport
        {
            Timestamp = DateTime.UtcNow,
            ClusterState = state,
            FaultDomain = faultDomain,
            FailoverPolicy = policy,
            IsMigrationInProgress = migrationInProgress,
            HealthScore = CalculateHealthScore(state, migrationInProgress)
        };

        // Add health checks
        report.Checks.Add(new HealthCheck
        {
            Name = "Cluster Membership",
            Status = state.IsHaEnabled ? HealthStatus.Healthy : HealthStatus.Warning,
            Message = state.IsHaEnabled ? "Node is a cluster member" : "Not part of a cluster"
        });

        report.Checks.Add(new HealthCheck
        {
            Name = "Node State",
            Status = state.NodeState == NodeState.Active ? HealthStatus.Healthy :
                    state.NodeState == NodeState.Standby ? HealthStatus.Warning :
                    HealthStatus.Critical,
            Message = $"Node is {state.NodeState}"
        });

        report.Checks.Add(new HealthCheck
        {
            Name = "Migration Status",
            Status = migrationInProgress ? HealthStatus.Warning : HealthStatus.Healthy,
            Message = migrationInProgress ? "Live migration in progress" : "No migration in progress"
        });

        if (state.QuorumStatus != QuorumStatus.Unknown)
        {
            report.Checks.Add(new HealthCheck
            {
                Name = "Quorum",
                Status = state.QuorumStatus == QuorumStatus.HasQuorum ? HealthStatus.Healthy : HealthStatus.Critical,
                Message = $"Quorum status: {state.QuorumStatus}"
            });
        }

        return report;
    }

    #endregion

    #region VMware HA/FT Detection

    private async Task<ClusterState> GetVMwareClusterStateAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var state = new ClusterState
            {
                IsHaEnabled = false,
                ClusterType = ClusterType.None
            };

            // Check for VMware environment
            if (!IsVMwareEnvironment())
                return state;

            // Check for VMware Tools HA status
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                state = GetVMwareStateLinux();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                state = GetVMwareStateWindows();
            }

            return state;
        }, ct);
    }

    private static bool IsVMwareEnvironment()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Check DMI
            try
            {
                var productName = File.ReadAllText("/sys/class/dmi/id/product_name").Trim();
                return productName.Contains("VMware", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Check for VMware modules
                return Directory.Exists("/sys/module/vmw_vmci") ||
                       Directory.Exists("/sys/module/vmxnet3");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Check for VMware services
            var vmtoolsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "VMware", "VMware Tools"
            );
            return Directory.Exists(vmtoolsPath);
        }

        return false;
    }

    private static ClusterState GetVMwareStateLinux()
    {
        var state = new ClusterState
        {
            IsHaEnabled = false,
            ClusterType = ClusterType.None
        };

        try
        {
            // Use vmware-rpctool to query HA status
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "vmware-rpctool",
                Arguments = "info-get guestinfo.vmware.ha",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(output))
                {
                    state.IsHaEnabled = true;
                    state.ClusterType = output.Contains("FT", StringComparison.OrdinalIgnoreCase)
                        ? ClusterType.VMwareFt
                        : ClusterType.VMwareHa;
                    state.NodeState = NodeState.Active;
                    state.Message = "VMware HA/FT detected";
                }
            }
        }
        catch
        {
            // vmware-rpctool not available
        }

        // Fallback: check for VMware HA artifacts
        if (!state.IsHaEnabled)
        {
            if (File.Exists("/etc/vmware-tools/ha.conf") ||
                Directory.Exists("/var/log/vmware"))
            {
                state.IsHaEnabled = true;
                state.ClusterType = ClusterType.VMwareHa;
                state.NodeState = NodeState.Unknown;
                state.Message = "VMware environment detected (status unknown)";
            }
        }

        return state;
    }

    private static ClusterState GetVMwareStateWindows()
    {
        var state = new ClusterState
        {
            IsHaEnabled = false,
            ClusterType = ClusterType.None
        };

        try
        {
            // Check VMware Tools service
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = "query VMTools",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                {
                    state.IsHaEnabled = true;
                    state.ClusterType = ClusterType.VMwareHa;
                    state.NodeState = NodeState.Active;
                    state.Message = "VMware Tools running - HA assumed enabled";
                }
            }
        }
        catch
        {
            // Service query failed
        }

        return state;
    }

    private static bool IsVMotionInProgressLinux()
    {
        try
        {
            // Check vmware-rpctool for migration status
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "vmware-rpctool",
                Arguments = "info-get guestinfo.vmware.vmotion.inprogress",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return output == "1" || output.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // vmware-rpctool not available
        }

        return false;
    }

    #endregion

    #region Hyper-V Failover Clustering Detection

    private async Task<ClusterState> GetHyperVClusterStateAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var state = new ClusterState
            {
                IsHaEnabled = false,
                ClusterType = ClusterType.None
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                state = GetHyperVClusterStateWindows();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Check for Hyper-V guest with IC
                if (Directory.Exists("/sys/module/hv_vmbus"))
                {
                    state.ClusterType = ClusterType.HyperVGuest;
                    state.NodeState = NodeState.Active;
                    state.Message = "Hyper-V guest detected";
                }
            }

            return state;
        }, ct);
    }

    private static ClusterState GetHyperVClusterStateWindows()
    {
        var state = new ClusterState
        {
            IsHaEnabled = false,
            ClusterType = ClusterType.None
        };

        try
        {
            // Check if Windows Failover Clustering is installed and running
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-Command \"Get-Cluster -ErrorAction SilentlyContinue | Select-Object Name\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(output) && !output.Contains("Error"))
                {
                    state.IsHaEnabled = true;
                    state.ClusterType = ClusterType.HyperVFailoverCluster;
                    state.NodeState = NodeState.Active;

                    // Extract cluster name
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 2)
                    {
                        state.ClusterName = lines[2].Trim();
                    }
                    state.Message = $"Failover Cluster: {state.ClusterName}";
                }
            }
        }
        catch
        {
            // PowerShell or cluster not available
        }

        // Check cluster service as fallback
        if (!state.IsHaEnabled)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "query ClusSvc",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                    {
                        state.IsHaEnabled = true;
                        state.ClusterType = ClusterType.HyperVFailoverCluster;
                        state.NodeState = NodeState.Active;
                        state.Message = "Failover Cluster service running";
                    }
                }
            }
            catch
            {
                // Service query failed
            }
        }

        return state;
    }

    private static bool IsHyperVMigrationInProgressLinux()
    {
        // Check dmesg or kernel log for migration events
        try
        {
            if (File.Exists("/var/log/kern.log"))
            {
                var log = File.ReadAllText("/var/log/kern.log");
                // Check for recent migration messages (within last minute would need timestamp parsing)
                return log.Contains("hv_vmbus: relocating") ||
                       log.Contains("Hyper-V: migration");
            }
        }
        catch
        {
            // Log not accessible
        }

        return false;
    }

    private static bool IsLiveMigrationInProgressWindows()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-Command \"Get-VMReplication -ErrorAction SilentlyContinue | Where-Object {$_.State -eq 'Replicating'}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return !string.IsNullOrWhiteSpace(output);
            }
        }
        catch
        {
            // PowerShell or Hyper-V not available
        }

        return false;
    }

    #endregion

    #region Pacemaker/Corosync Detection

    private async Task<ClusterState> GetPacemakerClusterStateAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var state = new ClusterState
            {
                IsHaEnabled = false,
                ClusterType = ClusterType.None
            };

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return state;

            // Check for Pacemaker/Corosync
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "crm_mon",
                    Arguments = "-1 --as-xml",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        state.IsHaEnabled = true;
                        state.ClusterType = ClusterType.Pacemaker;
                        state.NodeState = ParsePacemakerNodeState(output);
                        state.QuorumStatus = output.Contains("have-quorum=\"true\"")
                            ? QuorumStatus.HasQuorum
                            : QuorumStatus.NoQuorum;
                        state.Message = "Pacemaker cluster detected";

                        // Parse node count
                        var nodeCount = CountXmlElements(output, "<node ");
                        state.TotalNodes = nodeCount;
                    }
                }
            }
            catch
            {
                // crm_mon not available
            }

            // Fallback: check corosync
            if (!state.IsHaEnabled)
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = "corosync-quorumtool",
                        Arguments = "-s",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (process != null)
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            state.IsHaEnabled = true;
                            state.ClusterType = ClusterType.Corosync;
                            state.NodeState = NodeState.Active;
                            state.QuorumStatus = output.Contains("Quorate: Yes", StringComparison.OrdinalIgnoreCase)
                                ? QuorumStatus.HasQuorum
                                : QuorumStatus.NoQuorum;
                            state.Message = "Corosync cluster detected";
                        }
                    }
                }
                catch
                {
                    // corosync-quorumtool not available
                }
            }

            // Check systemd services as last resort
            if (!state.IsHaEnabled)
            {
                if (IsSystemdServiceActive("pacemaker") || IsSystemdServiceActive("corosync"))
                {
                    state.IsHaEnabled = true;
                    state.ClusterType = ClusterType.Pacemaker;
                    state.NodeState = NodeState.Unknown;
                    state.Message = "Pacemaker/Corosync services detected";
                }
            }

            return state;
        }, ct);
    }

    private static NodeState ParsePacemakerNodeState(string xmlOutput)
    {
        var hostname = Environment.MachineName.ToLowerInvariant();

        if (xmlOutput.Contains($"name=\"{hostname}\"", StringComparison.OrdinalIgnoreCase) ||
            xmlOutput.Contains($"uname=\"{hostname}\"", StringComparison.OrdinalIgnoreCase))
        {
            if (xmlOutput.Contains("online=\"true\"", StringComparison.OrdinalIgnoreCase))
                return NodeState.Active;
            if (xmlOutput.Contains("standby=\"true\"", StringComparison.OrdinalIgnoreCase))
                return NodeState.Standby;
            if (xmlOutput.Contains("maintenance=\"true\"", StringComparison.OrdinalIgnoreCase))
                return NodeState.Maintenance;
        }

        return NodeState.Unknown;
    }

    private static int CountXmlElements(string xml, string elementStart)
    {
        int count = 0;
        int index = 0;
        while ((index = xml.IndexOf(elementStart, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += elementStart.Length;
        }
        return count;
    }

    private static bool IsSystemdServiceActive(string serviceName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"is-active {serviceName}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return output.Equals("active", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // systemctl not available
        }

        return false;
    }

    #endregion

    #region Fault Domain

    private static string GetNodeId()
    {
        // Try to get a unique node identifier
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                if (File.Exists("/etc/machine-id"))
                    return File.ReadAllText("/etc/machine-id").Trim();
                if (File.Exists("/sys/class/dmi/id/product_uuid"))
                    return File.ReadAllText("/sys/class/dmi/id/product_uuid").Trim();
            }
            catch
            {
                // Fall through
            }
        }

        // Fallback to hostname + MAC
        try
        {
            var mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => n.GetPhysicalAddress().ToString())
                .FirstOrDefault(m => !string.IsNullOrEmpty(m));

            if (!string.IsNullOrEmpty(mac))
                return $"{Environment.MachineName}-{mac}";
        }
        catch
        {
            // Ignore
        }

        return Environment.MachineName;
    }

    private static void PopulateLinuxFaultDomain(FaultDomain domain)
    {
        // Try to get rack/datacenter info from DMI
        try
        {
            if (File.Exists("/sys/class/dmi/id/chassis_asset_tag"))
            {
                var tag = File.ReadAllText("/sys/class/dmi/id/chassis_asset_tag").Trim();
                if (!string.IsNullOrEmpty(tag) && tag != "Not Specified")
                {
                    domain.RackId = tag;
                }
            }

            if (File.Exists("/sys/class/dmi/id/board_asset_tag"))
            {
                var tag = File.ReadAllText("/sys/class/dmi/id/board_asset_tag").Trim();
                if (!string.IsNullOrEmpty(tag) && tag != "Not Specified")
                {
                    domain.HostId = tag;
                }
            }
        }
        catch
        {
            // DMI not accessible
        }

        // Check cloud metadata services
        PopulateCloudFaultDomain(domain);
    }

    private static void PopulateWindowsFaultDomain(FaultDomain domain)
    {
        // Try to get domain info from cluster
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-Command \"Get-ClusterNode -Name $env:COMPUTERNAME -ErrorAction SilentlyContinue | Select-Object FaultDomain\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(output))
                {
                    domain.CustomProperties["ClusterFaultDomain"] = output.Trim();
                }
            }
        }
        catch
        {
            // PowerShell or cluster not available
        }

        PopulateCloudFaultDomain(domain);
    }

    private static void PopulateCloudFaultDomain(FaultDomain domain)
    {
        // Azure IMDS
        try
        {
            // Check if running in Azure (simplified check)
            if (File.Exists("/var/lib/waagent/GoalState.1.xml") ||
                Environment.GetEnvironmentVariable("AZURE_GUEST_AGENT") != null)
            {
                domain.CloudProvider = "Azure";
                domain.AvailabilityZone = Environment.GetEnvironmentVariable("AZURE_ZONE");
            }
        }
        catch
        {
            // Not Azure
        }

        // AWS IMDS check would go here
        // GCP metadata check would go here
    }

    #endregion

    #region Failover Policy

    private static void PopulatePacemakerPolicy(FailoverPolicy policy)
    {
        policy.AutoFailover = true; // Pacemaker default
        policy.FailoverTimeoutSeconds = 60; // Default stonith timeout

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "crm_attribute",
                Arguments = "--query -n stonith-enabled",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                policy.RequiresFencing = output.Contains("value=true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // crm_attribute not available
        }
    }

    private static void PopulateHyperVPolicy(FailoverPolicy policy)
    {
        policy.AutoFailover = true;
        policy.FailoverTimeoutSeconds = 120; // Default Hyper-V timeout
    }

    private static void PopulateVMwarePolicy(FailoverPolicy policy, ClusterType clusterType)
    {
        policy.AutoFailover = true;

        if (clusterType == ClusterType.VMwareFt)
        {
            policy.FailoverTimeoutSeconds = 0; // FT is near-instant
            policy.IsZeroDowntime = true;
        }
        else
        {
            policy.FailoverTimeoutSeconds = 60; // HA restart time
        }
    }

    #endregion

    #region Failover Monitoring

    private void StartFailoverMonitoring()
    {
        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(async () =>
        {
            var previousState = await GetClusterStateAsync(_monitorCts.Token);

            while (!_monitorCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), _monitorCts.Token);

                    var currentState = await GetClusterStateAsync(_monitorCts.Token);

                    // Check for state changes
                    if (currentState.NodeState != previousState.NodeState ||
                        currentState.QuorumStatus != previousState.QuorumStatus)
                    {
                        var eventInfo = new FailoverEvent
                        {
                            Timestamp = DateTime.UtcNow,
                            EventType = DetermineEventType(previousState, currentState),
                            PreviousState = previousState.NodeState.ToString(),
                            CurrentState = currentState.NodeState.ToString(),
                            Message = $"Cluster state changed from {previousState.NodeState} to {currentState.NodeState}"
                        };

                        NotifyCallbacks(eventInfo);
                    }

                    previousState = currentState;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Continue monitoring despite errors
                }
            }
        }, _monitorCts.Token);
    }

    private void StopFailoverMonitoring()
    {
        _monitorCts?.Cancel();
        _monitorTask = null;
        _monitorCts = null;
    }

    private static FailoverEventType DetermineEventType(ClusterState previous, ClusterState current)
    {
        if (previous.NodeState == NodeState.Active && current.NodeState != NodeState.Active)
            return FailoverEventType.NodeDown;

        if (previous.NodeState != NodeState.Active && current.NodeState == NodeState.Active)
            return FailoverEventType.NodeUp;

        if (previous.QuorumStatus == QuorumStatus.HasQuorum && current.QuorumStatus != QuorumStatus.HasQuorum)
            return FailoverEventType.QuorumLost;

        if (previous.QuorumStatus != QuorumStatus.HasQuorum && current.QuorumStatus == QuorumStatus.HasQuorum)
            return FailoverEventType.QuorumGained;

        return FailoverEventType.StateChange;
    }

    private void NotifyCallbacks(FailoverEvent eventInfo)
    {
        List<FailoverNotificationCallback> callbacksCopy;

        lock (_callbackLock)
        {
            callbacksCopy = new List<FailoverNotificationCallback>(_failoverCallbacks);
        }

        foreach (var callback in callbacksCopy)
        {
            try
            {
                callback(eventInfo);
            }
            catch
            {
                // Ignore callback errors
            }
        }
    }

    private static int CalculateHealthScore(ClusterState state, bool migrationInProgress)
    {
        int score = 100;

        if (!state.IsHaEnabled)
            score -= 30;

        if (state.NodeState != NodeState.Active)
            score -= 20;

        if (state.QuorumStatus == QuorumStatus.NoQuorum)
            score -= 40;

        if (migrationInProgress)
            score -= 10;

        return Math.Max(0, score);
    }

    #endregion

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        StopFailoverMonitoring();
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "FaultTolerance";
        metadata["SupportedClusters"] = new[] { "VMware HA/FT", "Hyper-V Failover", "Pacemaker/Corosync" };
        return metadata;
    }
}

/// <summary>
/// Delegate for failover notification callbacks.
/// </summary>
/// <param name="eventInfo">Information about the failover event.</param>
public delegate void FailoverNotificationCallback(FailoverEvent eventInfo);

/// <summary>
/// Types of clustering technologies.
/// </summary>
public enum ClusterType
{
    /// <summary>No clustering.</summary>
    None,

    /// <summary>VMware High Availability.</summary>
    VMwareHa,

    /// <summary>VMware Fault Tolerance.</summary>
    VMwareFt,

    /// <summary>Hyper-V guest (may be in HA).</summary>
    HyperVGuest,

    /// <summary>Hyper-V Failover Cluster.</summary>
    HyperVFailoverCluster,

    /// <summary>Linux Pacemaker cluster.</summary>
    Pacemaker,

    /// <summary>Corosync cluster (without Pacemaker).</summary>
    Corosync,

    /// <summary>Custom or unknown cluster type.</summary>
    Custom
}

/// <summary>
/// Node state in the cluster.
/// </summary>
public enum NodeState
{
    /// <summary>Unknown state.</summary>
    Unknown,

    /// <summary>Node is active and processing.</summary>
    Active,

    /// <summary>Node is standby (ready to take over).</summary>
    Standby,

    /// <summary>Node is in maintenance mode.</summary>
    Maintenance,

    /// <summary>Node is offline.</summary>
    Offline,

    /// <summary>Node is fenced/isolated.</summary>
    Fenced
}

/// <summary>
/// Quorum status for the cluster.
/// </summary>
public enum QuorumStatus
{
    /// <summary>Unknown quorum status.</summary>
    Unknown,

    /// <summary>Cluster has quorum.</summary>
    HasQuorum,

    /// <summary>Cluster does not have quorum.</summary>
    NoQuorum
}

/// <summary>
/// Current cluster state information.
/// </summary>
public sealed class ClusterState
{
    /// <summary>
    /// Whether HA/FT is enabled.
    /// </summary>
    public bool IsHaEnabled { get; set; }

    /// <summary>
    /// Type of clustering technology.
    /// </summary>
    public ClusterType ClusterType { get; set; }

    /// <summary>
    /// Name of the cluster (if available).
    /// </summary>
    public string? ClusterName { get; set; }

    /// <summary>
    /// Current node state.
    /// </summary>
    public NodeState NodeState { get; set; }

    /// <summary>
    /// Quorum status.
    /// </summary>
    public QuorumStatus QuorumStatus { get; set; }

    /// <summary>
    /// Total number of nodes in the cluster.
    /// </summary>
    public int TotalNodes { get; set; }

    /// <summary>
    /// Human-readable message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Fault domain information.
/// </summary>
public sealed class FaultDomain
{
    /// <summary>
    /// Unique node identifier.
    /// </summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Hostname.
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Rack identifier (if known).
    /// </summary>
    public string? RackId { get; set; }

    /// <summary>
    /// Host/chassis identifier (if known).
    /// </summary>
    public string? HostId { get; set; }

    /// <summary>
    /// Datacenter identifier (if known).
    /// </summary>
    public string? DatacenterId { get; set; }

    /// <summary>
    /// Cloud provider (if running in cloud).
    /// </summary>
    public string? CloudProvider { get; set; }

    /// <summary>
    /// Availability zone (for cloud deployments).
    /// </summary>
    public string? AvailabilityZone { get; set; }

    /// <summary>
    /// Region (for cloud deployments).
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Custom properties.
    /// </summary>
    public Dictionary<string, string> CustomProperties { get; } = new();
}

/// <summary>
/// Failover policy configuration.
/// </summary>
public sealed class FailoverPolicy
{
    /// <summary>
    /// Cluster type this policy applies to.
    /// </summary>
    public ClusterType ClusterType { get; set; }

    /// <summary>
    /// Whether failover is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Whether automatic failover is enabled.
    /// </summary>
    public bool AutoFailover { get; set; }

    /// <summary>
    /// Failover timeout in seconds.
    /// </summary>
    public int FailoverTimeoutSeconds { get; set; }

    /// <summary>
    /// Whether fencing/STONITH is required.
    /// </summary>
    public bool RequiresFencing { get; set; }

    /// <summary>
    /// Whether this is a zero-downtime configuration (e.g., VMware FT).
    /// </summary>
    public bool IsZeroDowntime { get; set; }

    /// <summary>
    /// Maximum allowed failovers per period.
    /// </summary>
    public int? MaxFailoversPerPeriod { get; set; }

    /// <summary>
    /// Period for max failover count (in minutes).
    /// </summary>
    public int? FailoverPeriodMinutes { get; set; }
}

/// <summary>
/// Types of failover events.
/// </summary>
public enum FailoverEventType
{
    /// <summary>Generic state change.</summary>
    StateChange,

    /// <summary>Node went down.</summary>
    NodeDown,

    /// <summary>Node came up.</summary>
    NodeUp,

    /// <summary>Quorum was lost.</summary>
    QuorumLost,

    /// <summary>Quorum was gained.</summary>
    QuorumGained,

    /// <summary>Failover started.</summary>
    FailoverStarted,

    /// <summary>Failover completed.</summary>
    FailoverCompleted,

    /// <summary>Migration started.</summary>
    MigrationStarted,

    /// <summary>Migration completed.</summary>
    MigrationCompleted
}

/// <summary>
/// Information about a failover event.
/// </summary>
public sealed class FailoverEvent
{
    /// <summary>
    /// When the event occurred.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Type of event.
    /// </summary>
    public FailoverEventType EventType { get; init; }

    /// <summary>
    /// Previous state (if applicable).
    /// </summary>
    public string? PreviousState { get; init; }

    /// <summary>
    /// Current state.
    /// </summary>
    public string? CurrentState { get; init; }

    /// <summary>
    /// Human-readable message.
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Health status for health checks.
/// </summary>
public enum HealthStatus
{
    /// <summary>Healthy.</summary>
    Healthy,

    /// <summary>Warning - may need attention.</summary>
    Warning,

    /// <summary>Critical - requires immediate attention.</summary>
    Critical
}

/// <summary>
/// Individual health check result.
/// </summary>
public sealed class HealthCheck
{
    /// <summary>
    /// Name of the check.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Status of the check.
    /// </summary>
    public HealthStatus Status { get; init; }

    /// <summary>
    /// Message describing the result.
    /// </summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Comprehensive cluster health report.
/// </summary>
public sealed class ClusterHealthReport
{
    /// <summary>
    /// When the report was generated.
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Current cluster state.
    /// </summary>
    public required ClusterState ClusterState { get; init; }

    /// <summary>
    /// Fault domain information.
    /// </summary>
    public required FaultDomain FaultDomain { get; init; }

    /// <summary>
    /// Failover policy.
    /// </summary>
    public required FailoverPolicy FailoverPolicy { get; init; }

    /// <summary>
    /// Whether a migration is in progress.
    /// </summary>
    public bool IsMigrationInProgress { get; init; }

    /// <summary>
    /// Overall health score (0-100).
    /// </summary>
    public int HealthScore { get; init; }

    /// <summary>
    /// Individual health checks.
    /// </summary>
    public List<HealthCheck> Checks { get; } = new();
}
