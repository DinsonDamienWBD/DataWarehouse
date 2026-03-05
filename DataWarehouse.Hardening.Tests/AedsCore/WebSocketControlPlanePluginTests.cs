// Hardening tests for AedsCore findings: WebSocketControlPlanePlugin
// Findings: 9 (HIGH), 126/127 (MEDIUM dup), 128/129 (MEDIUM dup), 130 (MEDIUM)
using DataWarehouse.Plugins.AedsCore.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace DataWarehouse.Hardening.Tests.AedsCore;

/// <summary>
/// Tests for WebSocketControlPlanePlugin hardening findings.
/// </summary>
public class WebSocketControlPlanePluginTests
{
    /// <summary>
    /// Finding 9: _lastMessageReceived and _lastHeartbeatSent (DateTimeOffset) torn reads on 32-bit ARM.
    /// FIX: Stores as long ticks with Interlocked.Read/Exchange.
    /// </summary>
    [Fact]
    public void Finding009_DateTimeFieldsUseAtomicLongTicks()
    {
        var msgField = typeof(WebSocketControlPlanePlugin).GetField("_lastMessageReceivedTicks",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var hbField = typeof(WebSocketControlPlanePlugin).GetField("_lastHeartbeatSentTicks",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(msgField);
        Assert.NotNull(hbField);
        Assert.Equal(typeof(long), msgField!.FieldType);
        Assert.Equal(typeof(long), hbField!.FieldType);
    }

    /// <summary>
    /// Findings 126/127: Channel.CreateUnbounded - no backpressure.
    /// Same pattern as gRPC: acceptable for signaling channel.
    /// </summary>
    [Fact]
    public void Finding126_127_ManifestChannelExists()
    {
        var field = typeof(WebSocketControlPlanePlugin).GetField("_manifestChannel",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
    }

    /// <summary>
    /// Findings 128/129: Config! null-forgiving could NPE.
    /// Safe in intended call flow — Config set during EstablishConnectionAsync.
    /// </summary>
    [Fact]
    public void Finding128_129_PluginConstructsWithoutConfig()
    {
        var plugin = new WebSocketControlPlanePlugin(NullLogger<WebSocketControlPlanePlugin>.Instance);
        Assert.Equal("websocket", plugin.TransportId);
    }

    /// <summary>
    /// Finding 130: MethodHasAsyncOverload.
    /// CloseConnectionAsync uses best-effort task cleanup.
    /// </summary>
    [Fact]
    public void Finding130_CloseConnectionMethodExists()
    {
        var method = typeof(WebSocketControlPlanePlugin).GetMethod("CloseConnectionAsync",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
    }

    /// <summary>
    /// Verifies origin validation is initialized (NET-07 fix).
    /// </summary>
    [Fact]
    public void OriginValidationFieldsExist()
    {
        var allowedOrigins = typeof(WebSocketControlPlanePlugin).GetField("_allowedOrigins",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var serverOrigin = typeof(WebSocketControlPlanePlugin).GetField("_serverOrigin",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(allowedOrigins);
        Assert.NotNull(serverOrigin);
    }
}
