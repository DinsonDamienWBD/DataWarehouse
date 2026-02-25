using DataWarehouse.Plugins.UltimateDeployment;
using DataWarehouse.Plugins.UltimateDeployment.Strategies.AppPlatform;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for AppPlatform strategies (merged from AppPlatform plugin into UltimateDeployment).
/// Validates AppHostingStrategy and AppRuntimeStrategy identity, characteristics, and operations.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "UltimateDeployment")]
[Trait("Strategy", "AppPlatform")]
public class AppPlatformTests
{
    [Fact]
    public void AppHostingStrategy_HasCorrectCharacteristics()
    {
        // Arrange
        var strategy = new AppHostingStrategy();

        // Assert
        Assert.Equal("App Hosting Platform", strategy.Characteristics.StrategyName);
        Assert.Equal(DeploymentType.ContainerOrchestration, strategy.Characteristics.DeploymentType);
        Assert.True(strategy.Characteristics.SupportsZeroDowntime);
        Assert.True(strategy.Characteristics.SupportsHealthChecks);
    }

    [Fact]
    public void AppHostingStrategy_RegisterApp_CreatesRegistration()
    {
        var strategy = new AppHostingStrategy();

        var reg = strategy.RegisterApp("TestApp", "test@example.com", new() { "storage.read" });

        Assert.NotNull(reg);
        Assert.Equal("TestApp", reg.AppName);
        Assert.Equal("test@example.com", reg.OwnerEmail);
        Assert.True(reg.IsActive);
    }

    [Fact]
    public void AppHostingStrategy_DeregisterApp_DisablesApp()
    {
        var strategy = new AppHostingStrategy();
        var reg = strategy.RegisterApp("TestApp", "test@example.com", new() { "storage.read" });

        var result = strategy.DeregisterApp(reg.AppId);

        Assert.True(result);
        var app = strategy.GetApp(reg.AppId);
        Assert.NotNull(app);
        Assert.False(app!.IsActive);
    }

    [Fact]
    public void AppHostingStrategy_CreateAndValidateToken()
    {
        var strategy = new AppHostingStrategy();
        var reg = strategy.RegisterApp("TestApp", "test@example.com", new() { "storage.read" });

        var token = strategy.CreateToken(reg.AppId, new() { "storage.read" });

        Assert.NotNull(token);
        Assert.True(strategy.ValidateToken(token.TokenId));
    }

    [Fact]
    public void AppHostingStrategy_RevokeToken_InvalidatesIt()
    {
        var strategy = new AppHostingStrategy();
        var reg = strategy.RegisterApp("TestApp", "test@example.com", new() { "storage.read" });
        var token = strategy.CreateToken(reg.AppId, new() { "storage.read" });

        strategy.RevokeToken(token.TokenId);

        Assert.False(strategy.ValidateToken(token.TokenId));
    }

    [Fact]
    public void AppRuntimeStrategy_HasCorrectCharacteristics()
    {
        var strategy = new AppRuntimeStrategy();

        Assert.Equal("App Runtime Services", strategy.Characteristics.StrategyName);
        Assert.Equal(DeploymentType.HotReload, strategy.Characteristics.DeploymentType);
        Assert.True(strategy.Characteristics.SupportsZeroDowntime);
        Assert.True(strategy.Characteristics.SupportsInstantRollback);
    }

    [Fact]
    public void AppRuntimeStrategy_ConfigureAiWorkflow()
    {
        var strategy = new AppRuntimeStrategy();
        var config = new AppRuntimeStrategy.AiWorkflowConfig
        {
            Mode = AppRuntimeStrategy.AiWorkflowMode.Budget,
            MonthlyBudgetUsd = 500m,
            MaxConcurrentRequests = 10
        };

        strategy.ConfigureAiWorkflow("app-1", config);
        var result = strategy.GetAiWorkflow("app-1");

        Assert.NotNull(result);
        Assert.Equal(AppRuntimeStrategy.AiWorkflowMode.Budget, result!.Mode);
        Assert.Equal(500m, result.MonthlyBudgetUsd);
    }

    [Fact]
    public void AppRuntimeStrategy_ConfigureObservability()
    {
        var strategy = new AppRuntimeStrategy();
        var config = new AppRuntimeStrategy.ObservabilityConfig
        {
            MetricsEnabled = true,
            TracesEnabled = true,
            LogsEnabled = false,
            RetentionDays = 14
        };

        strategy.ConfigureObservability("app-1", config);
        var result = strategy.GetObservability("app-1");

        Assert.NotNull(result);
        Assert.True(result!.MetricsEnabled);
        Assert.True(result.TracesEnabled);
        Assert.False(result.LogsEnabled);
    }
}
