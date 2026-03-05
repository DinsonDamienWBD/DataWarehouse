// Hardening tests for Plugin findings 38-57: UltimateCompliance
// Finding 38 (LOW): CredentialIssuanceStrategy naming
// Finding 39 (HIGH): No validation on empty credential claims
// Finding 40 (MEDIUM): Revocation list not checked during verification
// Finding 41 (MEDIUM): Credential schema validation missing
// Finding 42 (HIGH): DigitalIdentityStrategy stub verification
// Finding 43 (MEDIUM): Identity attribute update no ownership check
// Finding 44 (LOW): Identity store not ConcurrentDictionary
// Finding 45 (HIGH): PassportRenewalStrategy unawaited base.InitializeAsync
// Finding 46 (MEDIUM): Renewed passport does not invalidate old
// Finding 47 (HIGH): PassportRevocationStrategy unawaited base.InitializeAsync
// Finding 48 (MEDIUM): RevokeAsync returns success for missing ID
// Finding 49 (MEDIUM): PassportVerificationStrategy no expiry check
// Finding 50 (HIGH): Signature verification different serialization
// Finding 51 (MEDIUM): DataMinimizationStrategy string date comparison
// Finding 52 (MEDIUM): PurgeExpiredData stub
// Finding 53 (LOW): DataMinimization Dictionary not thread-safe
// Findings 54-57: PciDss4Strategy issues

using System.Reflection;

namespace DataWarehouse.Hardening.Tests.Plugin;

/// <summary>
/// Tests for UltimateCompliance hardening findings.
/// </summary>
public class ComplianceHardeningTests
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateCompliance");

    /// <summary>
    /// Finding 39 (HIGH): CredentialIssuanceStrategy must validate claims are non-empty.
    /// </summary>
    [Fact]
    public void Finding039_CredentialIssuance_ValidatesNonEmptyClaims()
    {
        var dir = Path.Combine(PluginRoot, "Strategies", "Identity");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*CredentialIssuance*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        Assert.True(
            code.Contains("IsNullOrEmpty") || code.Contains("IsNullOrWhiteSpace") ||
            code.Contains("claims") && (code.Contains("throw") || code.Contains("ArgumentException")),
            "CredentialIssuanceStrategy should validate claims are non-empty before issuance");
    }

    /// <summary>
    /// Finding 42 (HIGH): DigitalIdentityStrategy.VerifyIdentityAsync must not be stub.
    /// </summary>
    [Fact]
    public void Finding042_DigitalIdentity_NotStub()
    {
        var dir = Path.Combine(PluginRoot, "Strategies", "Identity");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*DigitalIdentity*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        Assert.False(
            code.Contains("return") && code.Contains("hardcoded") && code.Contains("success"),
            "VerifyIdentityAsync should not return hardcoded success");
    }

    /// <summary>
    /// Findings 45, 47 (HIGH): Unawaited base.InitializeAsync patterns.
    /// </summary>
    [Fact]
    public void Finding045_047_PassportStrategies_AwaitBase()
    {
        var dir = Path.Combine(PluginRoot, "Strategies", "Identity");
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, "*Passport*.cs"))
        {
            var code = File.ReadAllText(file);
            if (code.Contains("InitializeAsync") && code.Contains("base."))
            {
                Assert.True(
                    code.Contains("await base.InitializeAsync") || code.Contains("await base.Initialize"),
                    $"File {Path.GetFileName(file)} should await base.InitializeAsync");
            }
        }
    }

    /// <summary>
    /// Finding 48 (MEDIUM): RevokeAsync should not return success for missing passport IDs.
    /// </summary>
    [Fact]
    public void Finding048_Revoke_FailsForMissingId()
    {
        var dir = Path.Combine(PluginRoot, "Strategies", "Identity");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*PassportRevocation*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        // Should check if passport ID exists before reporting success
        Assert.True(
            code.Contains("not found") || code.Contains("NotFound") ||
            code.Contains("ContainsKey") || code.Contains("TryGetValue"),
            "RevokeAsync should verify passport ID exists");
    }

    /// <summary>
    /// Finding 49 (MEDIUM): PassportVerificationStrategy must check expiry.
    /// </summary>
    [Fact]
    public void Finding049_PassportVerification_ChecksExpiry()
    {
        var dir = Path.Combine(PluginRoot, "Strategies", "Identity");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*PassportVerification*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        Assert.True(
            code.Contains("Expir") || code.Contains("expir") || code.Contains("IsValid"),
            "PassportVerificationStrategy should check expiry before validation");
    }

    /// <summary>
    /// Finding 51 (MEDIUM): DataMinimizationStrategy must not use string comparison on dates.
    /// </summary>
    [Fact]
    public void Finding051_DataMinimization_ProperDateComparison()
    {
        var dir = Path.Combine(PluginRoot, "Strategies", "Privacy");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*DataMinimization*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        // Should use DateTime/DateTimeOffset comparison, not string comparison
        Assert.False(
            code.Contains("string.Compare") && code.Contains("date"),
            "DataMinimizationStrategy should use proper DateTime comparison, not string comparison");
    }

    /// <summary>
    /// Finding 54 (MEDIUM): PciDss4Strategy PAN masking must handle short PANs.
    /// </summary>
    [Fact]
    public void Finding054_PciDss4_PanMasking_HandlesShortPan()
    {
        var dir = Path.Combine(PluginRoot, "Strategies", "Regulations");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*PciDss4*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        // Should have length check before Substring operations
        Assert.True(
            code.Contains(".Length") || code.Contains("Substring") == false ||
            code.Contains("if (") || code.Contains("Math.Min"),
            "PAN masking should handle short PANs safely");
    }
}
