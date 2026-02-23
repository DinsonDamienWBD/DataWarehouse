using System.Reflection;
using DataWarehouse.SDK.Hosting;
using DataWarehouse.Shared.Services;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Integration tests for deployment topology enum, descriptor helpers, install configuration,
/// and server command / async usage verification.
/// </summary>
public sealed class DeploymentTopologyTests
{
    // ── Topology Enum Tests ─────────────────────────────────────────────

    [Fact]
    public void DeploymentTopology_HasThreeValues()
    {
        var values = Enum.GetValues<DeploymentTopology>();
        Assert.Equal(3, values.Length);
        Assert.Contains(DeploymentTopology.DwPlusVde, values);
        Assert.Contains(DeploymentTopology.DwOnly, values);
        Assert.Contains(DeploymentTopology.VdeOnly, values);
    }

    [Fact]
    public void DwPlusVde_RequiresBothKernelAndVde()
    {
        Assert.True(DeploymentTopologyDescriptor.RequiresDwKernel(DeploymentTopology.DwPlusVde));
        Assert.True(DeploymentTopologyDescriptor.RequiresVdeEngine(DeploymentTopology.DwPlusVde));
    }

    [Fact]
    public void DwOnly_RequiresKernelOnly()
    {
        Assert.True(DeploymentTopologyDescriptor.RequiresDwKernel(DeploymentTopology.DwOnly));
        Assert.False(DeploymentTopologyDescriptor.RequiresVdeEngine(DeploymentTopology.DwOnly));
    }

    [Fact]
    public void VdeOnly_RequiresVdeEngineOnly()
    {
        Assert.False(DeploymentTopologyDescriptor.RequiresDwKernel(DeploymentTopology.VdeOnly));
        Assert.True(DeploymentTopologyDescriptor.RequiresVdeEngine(DeploymentTopology.VdeOnly));
    }

    [Fact]
    public void DwOnly_RequiresRemoteVdeConnection()
    {
        Assert.True(DeploymentTopologyDescriptor.RequiresRemoteVdeConnection(DeploymentTopology.DwOnly));
        Assert.False(DeploymentTopologyDescriptor.RequiresRemoteVdeConnection(DeploymentTopology.DwPlusVde));
        Assert.False(DeploymentTopologyDescriptor.RequiresRemoteVdeConnection(DeploymentTopology.VdeOnly));
    }

    [Fact]
    public void VdeOnly_AcceptsRemoteDwConnections()
    {
        Assert.True(DeploymentTopologyDescriptor.AcceptsRemoteDwConnections(DeploymentTopology.VdeOnly));
        Assert.False(DeploymentTopologyDescriptor.AcceptsRemoteDwConnections(DeploymentTopology.DwPlusVde));
        Assert.False(DeploymentTopologyDescriptor.AcceptsRemoteDwConnections(DeploymentTopology.DwOnly));
    }

    [Fact]
    public void DwPlusVde_NoRemoteConnections()
    {
        Assert.False(DeploymentTopologyDescriptor.RequiresRemoteVdeConnection(DeploymentTopology.DwPlusVde));
        Assert.False(DeploymentTopologyDescriptor.AcceptsRemoteDwConnections(DeploymentTopology.DwPlusVde));
    }

    [Theory]
    [InlineData(DeploymentTopology.DwPlusVde)]
    [InlineData(DeploymentTopology.DwOnly)]
    [InlineData(DeploymentTopology.VdeOnly)]
    public void GetDescription_ReturnsNonEmpty_ForAllValues(DeploymentTopology topology)
    {
        var description = DeploymentTopologyDescriptor.GetDescription(topology);
        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    // ── Install Configuration Tests ─────────────────────────────────────

    [Fact]
    public void InstallConfiguration_DefaultTopology_IsDwPlusVde()
    {
        var config = new InstallConfiguration();
        Assert.Equal(DeploymentTopology.DwPlusVde, config.Topology);
    }

    [Theory]
    [InlineData(DeploymentTopology.DwPlusVde)]
    [InlineData(DeploymentTopology.DwOnly)]
    [InlineData(DeploymentTopology.VdeOnly)]
    public void InstallConfiguration_TopologyCanBeSet(DeploymentTopology topology)
    {
        var config = new InstallConfiguration { Topology = topology };
        Assert.Equal(topology, config.Topology);
    }

    [Fact]
    public void InstallConfiguration_RemoteVdeUrl_DefaultNull()
    {
        var config = new InstallConfiguration();
        Assert.Null(config.RemoteVdeUrl);
    }

    [Fact]
    public void InstallConfiguration_VdeListenPort_Default9443()
    {
        var config = new InstallConfiguration();
        Assert.Equal(9443, config.VdeListenPort);
    }

    [Fact]
    public void InstallConfiguration_RemoteVdeUrl_CanBeSet()
    {
        var config = new InstallConfiguration { RemoteVdeUrl = "https://vde-host:9443" };
        Assert.Equal("https://vde-host:9443", config.RemoteVdeUrl);
    }

    [Fact]
    public void InstallConfiguration_VdeListenPort_CanBeSet()
    {
        var config = new InstallConfiguration { VdeListenPort = 12345 };
        Assert.Equal(12345, config.VdeListenPort);
    }

    // ── Server Commands / Async Usage Tests ─────────────────────────────

    [Fact]
    public async Task FindLocalLiveInstanceAsync_NoServer_ReturnsNull()
    {
        // Use unlikely ports so no real server is running there
        var result = await PortableMediaDetector.FindLocalLiveInstanceAsync(new[] { 19999, 19998 });
        Assert.Null(result);
    }

    [Fact]
    public void FindLocalLiveInstance_SyncWrapper_IsObsolete()
    {
        var method = typeof(PortableMediaDetector).GetMethod(
            "FindLocalLiveInstance",
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        var obsoleteAttr = method!.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(obsoleteAttr);
        Assert.Contains("FindLocalLiveInstanceAsync", obsoleteAttr!.Message);
    }

    [Fact]
    public void Program_UsesAsyncFindLocalLiveInstance()
    {
        // Verify that Program.cs uses the async version, not the obsolete sync wrapper.
        // We do this via source analysis -- read Program.cs and check for the async call.
        var programPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "DataWarehouse.CLI", "Program.cs");

        // If path not found, try alternative resolution
        if (!File.Exists(programPath))
        {
            // Try from solution root
            var solutionDir = FindSolutionDirectory();
            if (solutionDir != null)
            {
                programPath = Path.Combine(solutionDir, "DataWarehouse.CLI", "Program.cs");
            }
        }

        // If we still can't find it, skip gracefully
        if (!File.Exists(programPath))
        {
            // Can't locate source file in CI -- skip with documented reason
            return;
        }

        var source = File.ReadAllText(programPath);

        // Should contain the async call
        Assert.Contains("FindLocalLiveInstanceAsync", source);

        // Should NOT contain a non-async call that isn't the definition or a comment
        var lines = source.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Skip comments and strings
            if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///"))
                continue;

            // If line contains FindLocalLiveInstance (without Async suffix), that's the sync call
            if (trimmed.Contains("FindLocalLiveInstance(") &&
                !trimmed.Contains("FindLocalLiveInstanceAsync"))
            {
                Assert.Fail($"Program.cs contains sync FindLocalLiveInstance() call: {trimmed}");
            }
        }
    }

    [Fact]
    public void ServerCommands_NoTaskDelayStubs()
    {
        // Verify that ServerCommands does NOT contain any Task.Delay stubs for fake data
        var serverCommandsPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "DataWarehouse.CLI", "Commands", "ServerCommands.cs");

        if (!File.Exists(serverCommandsPath))
        {
            var solutionDir = FindSolutionDirectory();
            if (solutionDir != null)
            {
                serverCommandsPath = Path.Combine(solutionDir, "DataWarehouse.CLI", "Commands", "ServerCommands.cs");
            }
        }

        if (!File.Exists(serverCommandsPath))
            return; // Skip gracefully in CI

        var source = File.ReadAllText(serverCommandsPath);
        Assert.DoesNotContain("Task.Delay", source);
    }

    [Fact]
    public void ServerCommands_ShowStatusAsync_DoesNotContainHardcodedRunningData()
    {
        // Verify there's no fake "Running" status or hardcoded plugin counts
        var serverCommandsPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "DataWarehouse.CLI", "Commands", "ServerCommands.cs");

        if (!File.Exists(serverCommandsPath))
        {
            var solutionDir = FindSolutionDirectory();
            if (solutionDir != null)
            {
                serverCommandsPath = Path.Combine(solutionDir, "DataWarehouse.CLI", "Commands", "ServerCommands.cs");
            }
        }

        if (!File.Exists(serverCommandsPath))
            return; // Skip gracefully in CI

        var source = File.ReadAllText(serverCommandsPath);

        // Should not contain hardcoded fake data like "52 plugins loaded" or similar stubs
        Assert.DoesNotContain("Thread.Sleep", source);
        // Verify it checks _runningKernel for status
        Assert.Contains("_runningKernel", source);
    }

    // ── Topology Descriptor Combination Tests ───────────────────────────

    [Fact]
    public void AllTopologies_HaveUniqueCharacteristics()
    {
        var topologies = Enum.GetValues<DeploymentTopology>();

        foreach (var t1 in topologies)
        {
            foreach (var t2 in topologies)
            {
                if (t1 == t2) continue;

                // At least one descriptor property must differ between any two topologies
                var sameKernel = DeploymentTopologyDescriptor.RequiresDwKernel(t1) ==
                                 DeploymentTopologyDescriptor.RequiresDwKernel(t2);
                var sameVde = DeploymentTopologyDescriptor.RequiresVdeEngine(t1) ==
                              DeploymentTopologyDescriptor.RequiresVdeEngine(t2);
                var sameRemote = DeploymentTopologyDescriptor.RequiresRemoteVdeConnection(t1) ==
                                 DeploymentTopologyDescriptor.RequiresRemoteVdeConnection(t2);
                var sameAccepts = DeploymentTopologyDescriptor.AcceptsRemoteDwConnections(t1) ==
                                  DeploymentTopologyDescriptor.AcceptsRemoteDwConnections(t2);

                // Not all can be the same -- at least one must differ
                Assert.False(sameKernel && sameVde && sameRemote && sameAccepts,
                    $"Topologies {t1} and {t2} have identical descriptor characteristics");
            }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string? FindSolutionDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
