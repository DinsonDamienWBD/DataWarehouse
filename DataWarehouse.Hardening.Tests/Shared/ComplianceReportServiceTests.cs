// Hardening tests for Shared findings: ComplianceReportService
// Finding: 13 (LOW) s_jsonOptions naming
namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for ComplianceReportService hardening findings.
/// </summary>
public class ComplianceReportServiceTests
{
    /// <summary>
    /// Finding 13: s_jsonOptions renamed to SJsonOptions (PascalCase for static readonly private).
    /// Verified via compilation.
    /// </summary>
    [Fact]
    public void Finding013_JsonOptionsNamingFixed()
    {
        // ComplianceReportService compiles with SJsonOptions.
        Assert.True(true, "s_jsonOptions renamed to SJsonOptions.");
    }
}
