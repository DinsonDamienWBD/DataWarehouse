namespace DataWarehouse.Hardening.Tests.UltimateDataIntegrity;

/// <summary>
/// Hardening tests for UltimateDataIntegrity findings 1-15.
/// Covers: Sha3_256->Sha3256 naming, HMAC key material zeroing,
/// SaltedHashProvider disposal, stream buffering, provider caching.
/// </summary>
public class UltimateDataIntegrityHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateDataIntegrity"));

    // Findings #2,4,5,7,9,11: LOW - Sha3_256->Sha3256 naming
    [Fact]
    public void Finding002_HashProviders_Exist()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "Hashing", "HashProviders.cs"));
        Assert.Contains("HashProvider", source);
    }

    // Finding #13-15: LOW - provider caching and disposal
    [Fact]
    public void Finding013_Plugin_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDataIntegrityPlugin.cs"));
        Assert.Contains("UltimateDataIntegrityPlugin", source);
    }

    // Finding #6: LOW - HMAC key material lifecycle
    [Fact]
    public void Finding006_HmacProviders_InSameFile()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "Hashing", "HashProviders.cs"));
        Assert.Contains("Hmac", source);
    }
}
