using Xunit;
using DataWarehouse.Plugins.UltimateDeployment;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDeploymentTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateDeploymentPlugin();

        Assert.Equal("com.datawarehouse.deployment.ultimate", plugin.Id);
        Assert.Equal("Ultimate Deployment", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void Plugin_AutoDiscoversStrategies()
    {
        using var plugin = new UltimateDeploymentPlugin();

        var strategies = plugin.GetRegisteredStrategies();
        Assert.NotNull(strategies);
        Assert.True(strategies.Count > 0, "Should auto-discover at least one deployment strategy");
    }

    [Fact]
    public void GetStrategy_ReturnsNullForUnknown()
    {
        using var plugin = new UltimateDeploymentPlugin();

        var strategy = plugin.GetStrategy("non-existent-strategy");
        Assert.Null(strategy);
    }

    [Fact]
    public void DeploymentConfig_DefaultValues()
    {
        var config = new DeploymentConfig
        {
            Environment = "staging",
            Version = "2.0.0",
            ArtifactUri = "docker://app:2.0.0"
        };

        Assert.Equal("staging", config.Environment);
        Assert.Equal("2.0.0", config.Version);
        Assert.Equal(1, config.TargetInstances);
        Assert.Equal("/health", config.HealthCheckPath);
        Assert.True(config.AutoRollbackOnFailure);
        Assert.Equal(10, config.CanaryPercent);
        Assert.Equal(30, config.DeploymentTimeoutMinutes);
    }

    [Fact]
    public void DeploymentState_RecordProperties()
    {
        var state = new DeploymentState
        {
            DeploymentId = "deploy-001",
            Version = "1.0.0",
            Health = DeploymentHealth.Healthy,
            ProgressPercent = 100,
            DeployedInstances = 3,
            TargetInstances = 3,
            HealthyInstances = 3
        };

        Assert.Equal("deploy-001", state.DeploymentId);
        Assert.Equal(DeploymentHealth.Healthy, state.Health);
        Assert.Equal(100, state.ProgressPercent);
        Assert.Equal(3, state.HealthyInstances);
    }

    [Fact]
    public void DeploymentCharacteristics_CorrectTypes()
    {
        var chars = new DeploymentCharacteristics
        {
            StrategyName = "Blue-Green",
            DeploymentType = DeploymentType.BlueGreen,
            SupportsZeroDowntime = true,
            SupportsInstantRollback = true,
            SupportsTrafficShifting = false,
            ComplexityLevel = 3,
            TypicalDeploymentTimeMinutes = 5,
            ResourceOverheadPercent = 100,
            RequiredInfrastructure = new[] { "LoadBalancer" }
        };

        Assert.Equal("Blue-Green", chars.StrategyName);
        Assert.Equal(DeploymentType.BlueGreen, chars.DeploymentType);
        Assert.True(chars.SupportsZeroDowntime);
        Assert.True(chars.SupportsInstantRollback);
        Assert.Single(chars.RequiredInfrastructure);
    }

    [Fact]
    public void HealthCheckResult_RecordProperties()
    {
        var result = new HealthCheckResult
        {
            InstanceId = "inst-001",
            IsHealthy = true,
            StatusCode = 200,
            ResponseTimeMs = 15.5
        };

        Assert.Equal("inst-001", result.InstanceId);
        Assert.True(result.IsHealthy);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(15.5, result.ResponseTimeMs);
    }
}
