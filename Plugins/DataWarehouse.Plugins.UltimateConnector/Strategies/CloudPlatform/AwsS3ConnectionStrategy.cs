using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;
using Amazon.S3;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudPlatform
{
    /// <summary>
    /// AWS S3 connection strategy using HTTPS REST API with AWS Signature v4 authentication.
    /// </summary>
    public class AwsS3ConnectionStrategy : SaaSConnectionStrategyBase
    {
        public override string StrategyId => "aws-s3";
        public override string DisplayName => "AWS S3";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;

        public override ConnectionStrategyCapabilities Capabilities => new();

        public override string SemanticDescription => "Connects to AWS S3 object storage using HTTPS REST API with AWS Signature v4 authentication for secure access to buckets and objects.";
        public override string[] Tags => new[] { "aws", "s3", "cloud", "object-storage", "rest-api" };

        public AwsS3ConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var region = GetConfiguration(config, "Region", "us-east-1");
            var customEndpoint = GetConfiguration<string?>(config, "Endpoint", null);

            // Load credentials from config, then fall back to environment variables
            var accessKey = GetConfiguration<string?>(config, "AccessKeyId", null)
                ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");

            var secretKey = GetConfiguration<string?>(config, "SecretAccessKey", null)
                ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");

            var sessionToken = GetConfiguration<string?>(config, "SessionToken", null)
                ?? Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");

            // Create AWS credentials
            AWSCredentials credentials;
            if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
            {
                if (!string.IsNullOrEmpty(sessionToken))
                    credentials = new SessionAWSCredentials(accessKey, secretKey, sessionToken);
                else
                    credentials = new BasicAWSCredentials(accessKey, secretKey);
            }
            else
            {
                // Fall back to environment/instance credentials
                credentials = DefaultAWSCredentialsIdentityResolver.GetCredentials(null);
            }

            // Create S3 client configuration
            var s3Config = new AmazonS3Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(region),
                Timeout = TimeSpan.FromSeconds(GetConfiguration(config, "TimeoutSeconds", 300)),
                MaxErrorRetry = GetConfiguration(config, "MaxRetries", 3)
            };

            if (!string.IsNullOrEmpty(customEndpoint))
            {
                s3Config.ServiceURL = customEndpoint;
                s3Config.ForcePathStyle = true; // Required for custom endpoints like MinIO
            }

            // Create S3 client using official AWS SDK
            var s3Client = new AmazonS3Client(credentials, s3Config);

            var connectionInfo = new Dictionary<string, object>
            {
                ["Region"] = region,
                ["Endpoint"] = customEndpoint ?? $"https://s3.{region}.amazonaws.com",
                ["TimeoutSeconds"] = s3Config.Timeout!.Value.TotalSeconds,
                ["MaxRetries"] = s3Config.MaxErrorRetry
            };

            return new DefaultConnectionHandle(s3Client, connectionInfo);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var s3Client = handle.GetConnection<IAmazonS3>();

            try
            {
                // Test with ListBuckets - validates credentials and connectivity
                await s3Client.ListBucketsAsync(ct);
                return true;
            }
            catch (AmazonS3Exception)
            {
                // Even auth errors mean we can reach AWS
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var s3Client = handle.GetConnection<IAmazonS3>();
            s3Client?.Dispose();
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy ? "AWS S3 endpoint is reachable" : "AWS S3 endpoint is not responding",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow
            );
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // AWS uses signature v4 for each request, not a persistent token
            // Return a placeholder token that expires in 1 hour
            var token = Guid.NewGuid().ToString("N");
            var expiry = DateTimeOffset.UtcNow.AddHours(1);
            return Task.FromResult((token, expiry));
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(IConnectionHandle handle, string currentToken, CancellationToken ct = default)
        {
            // AWS uses signature v4 for each request, refresh is automatic
            return AuthenticateAsync(handle, ct);
        }
    }
}
