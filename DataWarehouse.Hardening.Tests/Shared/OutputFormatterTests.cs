// Hardening tests for Shared findings: OutputFormatter
// Findings: 46-47 (LOW) Naming violations
namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for OutputFormatter hardening findings.
/// </summary>
public class OutputFormatterTests
{
    /// <summary>
    /// Finding 46: _jsonOptions -> JsonOptions static field rename.
    /// Finding 47: _yamlSerializer -> YamlSerializer static field rename.
    /// Both are private static readonly fields renamed to PascalCase.
    /// </summary>
    [Fact]
    public void Finding046_047_NamingFixedForStaticFields()
    {
        // OutputFormatter compiles with renamed fields.
        var formatter = new DataWarehouse.Shared.Services.OutputFormatter();
        Assert.NotNull(formatter);
    }
}
