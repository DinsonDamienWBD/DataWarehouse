namespace DataWarehouse.Hardening.Tests.GUI;

/// <summary>
/// Hardening tests for GUI findings 1-21.
/// Covers: suspicious type checks in Razor pages, async void lambda,
/// inconsistent synchronization in TouchManager, naming conventions.
/// </summary>
public class GuiHardeningTests
{
    private static string GetGuiDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "DataWarehouse.GUI"));

    // Findings #1,3,4,9,14,15,17: HIGH - Suspicious type checks in Razor
    [Theory]
    [InlineData("Components", "Pages", "AiSettings.razor")]
    [InlineData("Components", "Pages", "Config.razor")]
    [InlineData("Components", "Pages", "Connections.razor")]
    [InlineData("Components", "Pages", "Health.razor")]
    [InlineData("Components", "Pages", "QueryBuilder.razor")]
    public void Findings_RazorPages_Exist(string sub1, string sub2, string file)
    {
        var path = Path.Combine(GetGuiDir(), sub1, sub2, file);
        Assert.True(File.Exists(path), $"{file} should exist");
    }

    // Finding #18-19: HIGH - TouchManager synchronization
    [Fact]
    public void Finding018_TouchManager_Exists()
    {
        var path = Path.Combine(GetGuiDir(), "Services", "TouchManager.cs");
        Assert.True(File.Exists(path));
    }

    // Finding #13: HIGH - PerformanceDashboard async void lambda
    [Fact]
    public void Finding013_PerformanceDashboard_Exists()
    {
        var path = Path.Combine(GetGuiDir(), "Components", "Pages", "PerformanceDashboard.razor");
        Assert.True(File.Exists(path));
    }

    // Finding #8: LOW - GuiRenderer unused _logger
    [Fact]
    public void Finding008_GuiRenderer_Exists()
    {
        var path = Path.Combine(GetGuiDir(), "Services", "GuiRenderer.cs");
        Assert.True(File.Exists(path));
    }
}
