using System;using System.Collections.Generic;using System.Net.Http;using System.Text;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using DataWarehouse.SDK.Connectors;using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Dashboard;

/// <summary>AWS QuickSight. HTTPS to quicksight.*.amazonaws.com. Cloud-native BI for AWS.</summary>
public sealed class AwsQuickSightConnectionStrategy : DashboardConnectionStrategyBase
{
    public override string StrategyId => "aws-quicksight";public override string DisplayName => "AWS QuickSight";public override ConnectionStrategyCapabilities Capabilities => new();public override string SemanticDescription => "AWS QuickSight cloud BI service. Serverless BI with ML-powered insights.";public override string[] Tags => ["aws", "quicksight", "bi", "cloud", "serverless"];
    public AwsQuickSightConnectionStrategy(ILogger? logger = null) : base(logger) { }
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct){var baseUrl = config.ConnectionString?.TrimEnd('/') ?? "https://quicksight.us-east-1.amazonaws.com";var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = config.Timeout };return new DefaultConnectionHandle(httpClient, new Dictionary<string, object> { ["Provider"] = "QuickSight", ["BaseUrl"] = baseUrl });}
    protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        => throw new NotSupportedException("AWS QuickSight requires an AWS account ID and IAM credentials. Configure AccountId via connection properties and use the AWS SDK for .NET (AWSSDK.QuickSight) for production connectivity.");
    protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct){handle.GetConnection<HttpClient>().Dispose();if (handle is DefaultConnectionHandle defaultHandle) defaultHandle.MarkDisconnected();return Task.CompletedTask;}
    protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        => throw new NotSupportedException("AWS QuickSight health checks require an AWS account ID and IAM credentials. Use the AWSSDK.QuickSight NuGet package.");
    public override Task<string> ProvisionDashboardAsync(IConnectionHandle handle, string dashboardDefinition, CancellationToken ct = default)
        => throw new NotSupportedException("AWS QuickSight dashboard provisioning requires a configured AWS account ID and IAM credentials. Use the AWSSDK.QuickSight NuGet package.");
    public override Task PushDataAsync(IConnectionHandle handle, string datasetId, IReadOnlyList<Dictionary<string, object?>> data, CancellationToken ct = default)
        => throw new NotSupportedException("AWS QuickSight data push requires a configured AWS account ID and IAM credentials. Use the AWSSDK.QuickSight NuGet package.");
}
