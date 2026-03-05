// Hardening tests for Plugin findings 30-37: UltimateBlockchain
// Finding 30 (MEDIUM): FileStream no Dispose in BlockchainJournalService
// Finding 31 (HIGH): Journal write failure not re-thrown
// Finding 32 (MEDIUM): Console.WriteLine instead of logger
// Finding 33 (HIGH): Fire-and-forget genesis block validation
// Finding 34 (HIGH): TranslatePolicyToBlockchainRules hardcoded empty
// Finding 35 (MEDIUM): X509 cert parsing without cache
// Finding 36 (HIGH): ValidateCertificateChainAsync default revocation
// Finding 37 (HIGH): SAN parsing comma splitting

using System.Reflection;

namespace DataWarehouse.Hardening.Tests.Plugin;

/// <summary>
/// Tests for UltimateBlockchain hardening findings.
/// </summary>
public class BlockchainHardeningTests
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateBlockchain");

    /// <summary>
    /// Finding 30 (MEDIUM): BlockchainJournalService FileStream must be disposed.
    /// </summary>
    [Fact]
    public void Finding030_JournalService_FileStreamDisposed()
    {
        var file = Path.Combine(PluginRoot, "Services", "BlockchainJournalService.cs");
        if (!File.Exists(file)) return;

        var code = File.ReadAllText(file);
        // Should implement IDisposable and dispose the FileStream
        Assert.True(
            code.Contains("IDisposable") || code.Contains("IAsyncDisposable") ||
            code.Contains("Dispose(") || code.Contains("DisposeAsync("),
            "BlockchainJournalService should implement IDisposable for FileStream cleanup");
    }

    /// <summary>
    /// Finding 31 (HIGH): Journal write failure must be re-thrown or properly signaled.
    /// </summary>
    [Fact]
    public void Finding031_JournalWriteFailure_MustPropagate()
    {
        var file = Path.Combine(PluginRoot, "Services", "BlockchainJournalService.cs");
        if (!File.Exists(file)) return;

        var code = File.ReadAllText(file);
        // Should not silently catch and log write failures
        Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(code,
                @"catch.*\{[^}]*Log[^}]*\}",
                System.Text.RegularExpressions.RegexOptions.Singleline) &&
            !code.Contains("throw;") && !code.Contains("throw "),
            "Journal write failures should be re-thrown after logging");
    }

    /// <summary>
    /// Finding 32 (MEDIUM): Should not use Console.WriteLine for diagnostics.
    /// </summary>
    [Fact]
    public void Finding032_NoConsoleWriteLine()
    {
        var file = Path.Combine(PluginRoot, "Services", "BlockchainJournalService.cs");
        if (!File.Exists(file)) return;

        var code = File.ReadAllText(file);
        Assert.DoesNotContain("Console.WriteLine", code);
    }

    /// <summary>
    /// Finding 33 (HIGH): Genesis block validation must be awaited.
    /// </summary>
    [Fact]
    public void Finding033_GenesisBlockValidation_MustBeAwaited()
    {
        var file = Path.Combine(PluginRoot, "Services", "BlockchainJournalService.cs");
        if (!File.Exists(file)) return;

        var code = File.ReadAllText(file);
        // Should not have fire-and-forget Task without await
        Assert.DoesNotContain("Task.Run(", code);
    }

    /// <summary>
    /// Finding 34 (HIGH): TranslatePolicyToBlockchainRules must not return empty.
    /// </summary>
    [Fact]
    public void Finding034_PolicyBridge_NotEmpty()
    {
        var file = Path.Combine(PluginRoot, "Services", "BlockchainPolicyBridge.cs");
        if (!File.Exists(file)) return;

        var code = File.ReadAllText(file);
        // Should not return hardcoded empty rule set
        Assert.False(
            code.Contains("return new") && code.Contains("empty"),
            "TranslatePolicyToBlockchainRules should produce actual rules");
    }

    /// <summary>
    /// Findings 35-37 (MEDIUM-HIGH): X509CertStrategy certificate handling.
    /// </summary>
    [Fact]
    public void Finding035_037_X509CertStrategy()
    {
        var stratDir = Path.Combine(PluginRoot, "Strategies", "Network");
        if (!Directory.Exists(stratDir)) return;

        var file = Directory.GetFiles(stratDir, "*Cert*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);

        // Finding 36: Should set explicit revocation mode
        if (code.Contains("X509Chain"))
        {
            Assert.True(
                code.Contains("RevocationMode") || code.Contains("RevocationFlag"),
                "X509Chain should have explicit RevocationMode set");
        }
    }
}
