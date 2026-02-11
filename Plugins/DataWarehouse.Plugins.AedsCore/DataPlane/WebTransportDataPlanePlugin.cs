using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.AedsCore.DataPlane;

/// <summary>
/// WebTransport data plane plugin (FUTURE SUPPORT).
/// </summary>
/// <remarks>
/// <para>
/// WebTransport is not yet supported in .NET 9 System.Net.Quic API.
/// This plugin is a stub awaiting future .NET release with WebTransport support.
/// </para>
/// <para>
/// <strong>Status:</strong> Tracked in dotnet/runtime GitHub repository. WebTransport API is under
/// active development as part of the QUIC transport layer expansion.
/// </para>
/// <para>
/// <strong>Workaround:</strong> Use QUIC or HTTP/3 data plane transports for production deployments.
/// Both provide similar performance characteristics and are fully supported in .NET 9.
/// </para>
/// </remarks>
public class WebTransportDataPlanePlugin : DataPlaneTransportPluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.aeds.dataplane.webtransport";

    /// <inheritdoc />
    public override string Name => "WebTransport Data Plane (STUB)";

    /// <inheritdoc />
    public override string Version => "0.1.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <inheritdoc />
    public override string TransportId => "webtransport";

    /// <inheritdoc />
    protected override Task<Stream> FetchPayloadAsync(
        string payloadId,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress,
        CancellationToken ct)
        => throw new NotSupportedException(
            "WebTransport is not yet supported in .NET 9. Use QUIC (TransportId='quic') or HTTP/3 (TransportId='http3') data plane transports instead.");

    /// <inheritdoc />
    protected override Task<Stream> FetchDeltaAsync(
        string payloadId,
        string baseVersion,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress,
        CancellationToken ct)
        => throw new NotSupportedException(
            "WebTransport is not yet supported in .NET 9. Use QUIC (TransportId='quic') or HTTP/3 (TransportId='http3') data plane transports instead.");

    /// <inheritdoc />
    protected override Task<string> PushPayloadAsync(
        Stream data,
        PayloadMetadata metadata,
        DataPlaneConfig config,
        IProgress<TransferProgress>? progress,
        CancellationToken ct)
        => throw new NotSupportedException(
            "WebTransport is not yet supported in .NET 9. Use QUIC (TransportId='quic') or HTTP/3 (TransportId='http3') data plane transports instead.");

    /// <inheritdoc />
    protected override Task<bool> CheckExistsAsync(
        string payloadId,
        DataPlaneConfig config,
        CancellationToken ct)
        => throw new NotSupportedException(
            "WebTransport is not yet supported in .NET 9. Use QUIC (TransportId='quic') or HTTP/3 (TransportId='http3') data plane transports instead.");

    /// <inheritdoc />
    protected override Task<PayloadDescriptor?> FetchInfoAsync(
        string payloadId,
        DataPlaneConfig config,
        CancellationToken ct)
        => throw new NotSupportedException(
            "WebTransport is not yet supported in .NET 9. Use QUIC (TransportId='quic') or HTTP/3 (TransportId='http3') data plane transports instead.");

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;
}
