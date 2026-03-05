// Hardening tests for Shared findings: CommandRecorder
// Finding: 12 (LOW) Collection never updated
using DataWarehouse.Shared.Services;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for CommandRecorder hardening findings.
/// </summary>
public class CommandRecorderTests
{
    /// <summary>
    /// Finding 12: ParameterOverrides collection was never updated.
    /// Changed to IReadOnlyDictionary to make intent explicit.
    /// </summary>
    [Fact]
    public void Finding012_ParameterOverridesIsReadOnly()
    {
        var options = new ReplayOptions();
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(options.ParameterOverrides);
        Assert.Empty(options.ParameterOverrides);
    }
}
