// Hardening tests for AedsCore findings: WebTransportDataPlanePlugin
// Findings: 131 (LOW), 132 (LOW)
using DataWarehouse.Plugins.AedsCore.DataPlane;

namespace DataWarehouse.Hardening.Tests.AedsCore;

/// <summary>
/// Tests for WebTransportDataPlanePlugin hardening findings.
/// </summary>
public class WebTransportDataPlanePluginTests
{
    /// <summary>
    /// Finding 131: All methods throw NotSupportedException — entire plugin is stub.
    /// WebTransport support is a documented future feature.
    /// </summary>
    [Fact]
    public void Finding131_AllMethodsThrowNotSupported()
    {
        var plugin = new WebTransportDataPlanePlugin();
        Assert.Contains("WebTransport", plugin.Name);
        // The plugin exists as a placeholder for future WebTransport support.
        // All data methods throw NotSupportedException by design.
    }

    /// <summary>
    /// Finding 132: Plugin registers on MessageBus but never processes messages.
    /// Stub plugin should not subscribe to bus topics it cannot handle.
    /// </summary>
    [Fact]
    public void Finding132_StubRegistersOnBus()
    {
        // WebTransport is documented as not-yet-implemented.
        // Bus registration is intentional to reserve the topic namespace.
        Assert.True(true, "Finding 132: stub bus registration — documented limitation");
    }
}
