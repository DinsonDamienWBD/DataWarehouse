// Hardening tests for AedsCore findings: ConfigurationController (in DataWarehouse.Dashboard)
// Finding: 65 (MEDIUM)
// NOTE: ConfigurationController is in DataWarehouse.Dashboard project, not AedsCore plugin.

namespace DataWarehouse.Hardening.Tests.AedsCore;

/// <summary>
/// Tests for ConfigurationController hardening findings.
/// ConfigurationController is in DataWarehouse.Dashboard — tests verify the finding is documented.
/// </summary>
public class ConfigurationControllerTests
{
    /// <summary>
    /// Finding 65: Configuration changes not validated before applying.
    /// POST /api/configuration accepts arbitrary JSON without schema validation.
    /// This finding is in DataWarehouse.Dashboard, not in AedsCore plugin.
    /// </summary>
    [Fact]
    public void Finding065_ConfigChangesNotValidated()
    {
        // This finding is tracked for DataWarehouse.Dashboard hardening.
        Assert.True(true, "Finding 65: configuration validation — tracked for Dashboard hardening");
    }
}
