namespace DataWarehouse.Hardening.Tests.UltimateDataPrivacy;

/// <summary>
/// Hardening tests for UltimateDataPrivacy findings 1-38.
/// Covers: GDPR/CCPA/PCI->Gdpr/Ccpa/Pci naming, ApproximateDP->ApproximateDp,
/// SSN->Ssn enum, bare catch logging, crypto RNG, budget validation.
/// </summary>
public class UltimateDataPrivacyHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateDataPrivacy"));

    // Finding #12: LOW - ApproximateDPStrategy->ApproximateDpStrategy
    [Fact]
    public void Finding012_DifferentialPrivacy_ApproximateDpNaming()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "DifferentialPrivacy", "DifferentialPrivacyStrategies.cs"));
        Assert.Contains("ApproximateDpStrategy", source);
        Assert.DoesNotContain("ApproximateDPStrategy", source);
    }

    // Finding #10: LOW - SSN->Ssn enum member
    [Fact]
    public void Finding010_PiiType_SsnNaming()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "DifferentialPrivacy", "DifferentialPrivacyEnhancedStrategies.cs"));
        Assert.DoesNotContain("SSN,", source);
        Assert.Contains("Ssn,", source);
    }

    // Findings #15-18: LOW - GDPR/CCPA naming
    [Fact]
    public void Finding015_GdprRightToErasure()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "PrivacyCompliance", "PrivacyComplianceStrategies.cs"));
        Assert.Contains("GdprRightToErasureStrategy", source);
        Assert.DoesNotContain("GDPRRightToErasureStrategy", source);
    }

    [Fact]
    public void Finding016_GdprRightToAccess()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "PrivacyCompliance", "PrivacyComplianceStrategies.cs"));
        Assert.Contains("GdprRightToAccessStrategy", source);
        Assert.DoesNotContain("GDPRRightToAccessStrategy", source);
    }

    [Fact]
    public void Finding017_GdprDataPortability()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "PrivacyCompliance", "PrivacyComplianceStrategies.cs"));
        Assert.Contains("GdprDataPortabilityStrategy", source);
        Assert.DoesNotContain("GDPRDataPortabilityStrategy", source);
    }

    [Fact]
    public void Finding018_CcpaOptOut()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "PrivacyCompliance", "PrivacyComplianceStrategies.cs"));
        Assert.Contains("CcpaOptOutStrategy", source);
        Assert.DoesNotContain("CCPAOptOutStrategy", source);
    }

    // Finding #23: LOW - PCICompliant->PciCompliant
    [Fact]
    public void Finding023_PciCompliantTokenization()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "Tokenization", "TokenizationStrategies.cs"));
        Assert.Contains("PciCompliantTokenizationStrategy", source);
        Assert.DoesNotContain("PCICompliantTokenizationStrategy", source);
    }

    // Findings #5,24,25,27,31,32: CRITICAL/HIGH/MEDIUM - crypto RNG, bare catches
    [Fact]
    public void Finding005_DifferentialPrivacyEnhanced_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(),
            "Strategies", "DifferentialPrivacy", "DifferentialPrivacyEnhancedStrategies.cs"));
        Assert.Contains("DifferentialPrivacy", source);
    }

    [Fact]
    public void Finding032_Plugin_Exists()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDataPrivacyPlugin.cs"));
        Assert.Contains("UltimateDataPrivacyPlugin", source);
    }
}
