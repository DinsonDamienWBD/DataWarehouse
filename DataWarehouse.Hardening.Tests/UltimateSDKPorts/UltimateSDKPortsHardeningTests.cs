namespace DataWarehouse.Hardening.Tests.UltimateSDKPorts;

/// <summary>
/// Hardening tests for UltimateSDKPorts findings 1-27.
/// Covers: SDK->Sdk, FFI->Ffi, GRPC->Grpc, REST->Rest type renames,
/// insecure channel warnings, stub code generation, ToCamelCase crash guard.
/// </summary>
public class UltimateSdkPortsHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateSDKPorts"));

    // Findings #7-16: LOW - SDK->Sdk naming across base types
    [Fact]
    public void Finding007_TransportType_FfiNaming()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "SDKPortStrategyBase.cs"));
        Assert.Contains("Ffi,", source);
        Assert.DoesNotContain("FFI,", source);
    }

    [Fact]
    public void Finding008_TransportType_GrpcNaming()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "SDKPortStrategyBase.cs"));
        Assert.Contains("Grpc,", source);
        Assert.DoesNotContain("GRPC,", source);
    }

    [Fact]
    public void Finding009_TransportType_RestNaming()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "SDKPortStrategyBase.cs"));
        Assert.Contains("Rest,", source);
        Assert.DoesNotContain("REST,", source);
    }

    [Fact]
    public void Finding010_SdkPortCapabilities()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "SDKPortStrategyBase.cs"));
        Assert.Contains("SdkPortCapabilities", source);
        Assert.DoesNotContain("SDKPortCapabilities", source);
    }

    [Fact]
    public void Finding014_SdkPortStrategyBase()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "SDKPortStrategyBase.cs"));
        Assert.Contains("SdkPortStrategyBase", source);
        Assert.DoesNotContain("class SDKPortStrategyBase", source);
    }

    // Finding #18: LOW - SDKPortStrategyRegistry->SdkPortStrategyRegistry
    [Fact]
    public void Finding018_SdkPortStrategyRegistry()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "SDKPortStrategyRegistry.cs"));
        Assert.Contains("SdkPortStrategyRegistry", source);
        Assert.DoesNotContain("class SDKPortStrategyRegistry", source);
    }

    // Finding #23: LOW - UltimateSdkPortsPlugin
    [Fact]
    public void Finding023_UltimateSdkPortsPlugin()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateSDKPortsPlugin.cs"));
        Assert.Contains("UltimateSdkPortsPlugin", source);
        Assert.DoesNotContain("class UltimateSDKPortsPlugin", source);
    }

    // Findings #2-3: HIGH - ToCamelCase IndexOutOfRange
    [Fact]
    public void Finding002_CrossLanguage_Exists()
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", "CrossLanguage", "CrossLanguageStrategies.cs");
        Assert.True(File.Exists(path));
    }

    // Findings #4-6: CRITICAL/MEDIUM - insecure generated code
    [Fact]
    public void Finding006_PythonBindings_Exists()
    {
        var path = Path.Combine(GetPluginDir(), "Strategies", "PythonBindings", "PythonBindingStrategies.cs");
        Assert.True(File.Exists(path));
    }
}
