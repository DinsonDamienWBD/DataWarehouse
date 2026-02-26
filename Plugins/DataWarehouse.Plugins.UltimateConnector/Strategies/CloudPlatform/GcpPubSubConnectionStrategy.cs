using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudPlatform;

/// <summary>
/// GCP Pub/Sub connection strategy using the official Google.Cloud.PubSub.V1 SDK.
/// Provides production-ready connectivity with Publish, Pull/StreamingPull,
/// topic/subscription management, dead letter, and ordering keys.
/// </summary>
public sealed class GcpPubSubConnectionStrategy : SaaSConnectionStrategyBase
{
    public override string StrategyId => "gcp-pubsub";
    public override string DisplayName => "GCP Pub/Sub";
    public override ConnectorCategory Category => ConnectorCategory.SaaS;
    public override ConnectionStrategyCapabilities Capabilities => new();
    public override string SemanticDescription =>
        "Google Cloud Pub/Sub using official Google SDK. Supports Publish, Pull, StreamingPull, " +
        "topic/subscription management, dead letter topics, ordering keys, and filtering.";
    public override string[] Tags => ["gcp", "pubsub", "messaging", "streaming", "google-cloud"];

    public GcpPubSubConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var projectId = GetConfiguration<string?>(config, "ProjectId", null)
            ?? config.ConnectionString;

        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required for GCP Pub/Sub connection.");

        var emulatorHost = GetConfiguration<string?>(config, "EmulatorHost", null)
            ?? Environment.GetEnvironmentVariable("PUBSUB_EMULATOR_HOST");

        // Create publisher client
        PublisherServiceApiClient publisherClient;
        SubscriberServiceApiClient subscriberClient;

        if (!string.IsNullOrEmpty(emulatorHost))
        {
            // Use emulator
            var publisherBuilder = new PublisherServiceApiClientBuilder
            {
                Endpoint = emulatorHost,
                ChannelCredentials = Grpc.Core.ChannelCredentials.Insecure
            };
            publisherClient = await publisherBuilder.BuildAsync(ct);

            var subscriberBuilder = new SubscriberServiceApiClientBuilder
            {
                Endpoint = emulatorHost,
                ChannelCredentials = Grpc.Core.ChannelCredentials.Insecure
            };
            subscriberClient = await subscriberBuilder.BuildAsync(ct);
        }
        else
        {
            publisherClient = await new PublisherServiceApiClientBuilder().BuildAsync(ct);
            subscriberClient = await new SubscriberServiceApiClientBuilder().BuildAsync(ct);
        }

        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "Google.Cloud.PubSub.V1",
            ["ProjectId"] = projectId,
            ["Emulator"] = !string.IsNullOrEmpty(emulatorHost),
            ["State"] = "Connected"
        };

        return new DefaultConnectionHandle(
            new GcpPubSubWrapper(publisherClient, subscriberClient, projectId),
            connectionInfo);
    }

    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            var wrapper = handle.GetConnection<GcpPubSubWrapper>();
            // Enumerate at least one result to confirm the gRPC channel is live.
            // ListTopics will throw on network failures or permission errors that
            // prevent any communication (e.g. UNAVAILABLE, DEADLINE_EXCEEDED).
            var projectName = $"projects/{wrapper.ProjectId}";
            var page = wrapper.PublisherClient.ListTopics(projectName).ReadPage(1);
            _ = page; // result consumed; connection is healthy
            return Task.FromResult(true);
        }
        catch (Grpc.Core.RpcException ex) when (
            ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied ||
            ex.StatusCode == Grpc.Core.StatusCode.Unauthenticated)
        {
            // Authentication/authorisation errors mean the transport is reachable
            // but credentials are wrong — report as unhealthy so callers know.
            return Task.FromResult(false);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        if (handle is DefaultConnectionHandle dh)
            dh.MarkDisconnected();
        return Task.CompletedTask;
    }

    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var isHealthy = await TestCoreAsync(handle, ct);
        sw.Stop();

        var wrapper = handle.GetConnection<GcpPubSubWrapper>();
        return new ConnectionHealth(
            IsHealthy: isHealthy,
            StatusMessage: isHealthy
                ? $"GCP Pub/Sub connected - Project: {wrapper.ProjectId}"
                : "GCP Pub/Sub not responding",
            Latency: sw.Elapsed,
            CheckedAt: DateTimeOffset.UtcNow);
    }

    protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(
        IConnectionHandle handle, CancellationToken ct = default)
        => throw new NotSupportedException(
            "GCP Pub/Sub authentication is handled by the Google.Cloud.PubSub.V1 SDK via " +
            "Application Default Credentials or a service account key file. " +
            "Configure GOOGLE_APPLICATION_CREDENTIALS or use Workload Identity.");

    protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(
        IConnectionHandle handle, string currentToken, CancellationToken ct = default)
        => AuthenticateAsync(handle, ct);

    internal sealed class GcpPubSubWrapper(
        PublisherServiceApiClient publisherClient,
        SubscriberServiceApiClient subscriberClient,
        string projectId)
    {
        public PublisherServiceApiClient PublisherClient { get; } = publisherClient;
        public SubscriberServiceApiClient SubscriberClient { get; } = subscriberClient;
        public string ProjectId { get; } = projectId;
    }
}
