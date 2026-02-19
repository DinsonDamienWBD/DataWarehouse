using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Billing;

/// <summary>
/// AWS Cost Explorer billing provider. Uses raw HttpClient with AWS Signature V4
/// signing to call Cost Explorer, EC2, and STS APIs without requiring the AWS SDK.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Cloud billing providers")]
public sealed class AwsCostExplorerProvider : IBillingProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _accessKeyId;
    private readonly string _secretAccessKey;
    private readonly string _region;

    private const int MaxRetries = 3;
    private const string ServiceCe = "ce";
    private const string ServiceEc2 = "ec2";
    private const string ServiceSts = "sts";

    /// <summary>
    /// Creates an AWS Cost Explorer billing provider.
    /// </summary>
    /// <param name="httpClient">HTTP client for API calls.</param>
    /// <param name="accessKeyId">AWS access key ID (falls back to AWS_ACCESS_KEY_ID env var).</param>
    /// <param name="secretAccessKey">AWS secret access key (falls back to AWS_SECRET_ACCESS_KEY env var).</param>
    /// <param name="region">AWS region (falls back to AWS_REGION env var, defaults to us-east-1).</param>
    public AwsCostExplorerProvider(
        HttpClient httpClient,
        string? accessKeyId = null,
        string? secretAccessKey = null,
        string region = "us-east-1")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _accessKeyId = accessKeyId
            ?? Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")
            ?? throw new InvalidOperationException("AWS access key ID not configured. Set via constructor or AWS_ACCESS_KEY_ID environment variable.");
        _secretAccessKey = secretAccessKey
            ?? Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")
            ?? throw new InvalidOperationException("AWS secret access key not configured. Set via constructor or AWS_SECRET_ACCESS_KEY environment variable.");
        _region = region
            ?? Environment.GetEnvironmentVariable("AWS_REGION")
            ?? "us-east-1";
    }

    /// <inheritdoc />
    public CloudProvider Provider => CloudProvider.AWS;

    /// <inheritdoc />
    public async Task<BillingReport> GetBillingReportAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            TimePeriod = new
            {
                Start = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                End = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            },
            Granularity = "MONTHLY",
            Metrics = new[] { "UnblendedCost", "UsageQuantity" },
            GroupBy = new[]
            {
                new { Type = "DIMENSION", Key = "SERVICE" }
            }
        });

        var responseJson = await SendAwsRequestAsync(
            $"https://ce.{_region}.amazonaws.com/",
            ServiceCe,
            "AWSInsightsIndexService.GetCostAndUsage",
            requestBody,
            ct).ConfigureAwait(false);

        var breakdown = new List<CostBreakdown>();
        decimal totalCost = 0m;

        if (responseJson.TryGetProperty("ResultsByTime", out var resultsByTime))
        {
            foreach (var period in resultsByTime.EnumerateArray())
            {
                if (!period.TryGetProperty("Groups", out var groups)) continue;
                foreach (var group in groups.EnumerateArray())
                {
                    var serviceName = group.GetProperty("Keys")[0].GetString() ?? "Unknown";
                    var amount = ParseDecimal(group.GetProperty("Metrics").GetProperty("UnblendedCost").GetProperty("Amount").GetString());
                    var quantity = ParseDouble(group.GetProperty("Metrics").GetProperty("UsageQuantity").GetProperty("Amount").GetString());
                    var unit = group.GetProperty("Metrics").GetProperty("UsageQuantity").GetProperty("Unit").GetString() ?? "N/A";

                    totalCost += amount;
                    breakdown.Add(new CostBreakdown(
                        CategorizeAwsService(serviceName),
                        serviceName,
                        amount,
                        unit,
                        quantity));
                }
            }
        }

        return new BillingReport(
            $"aws-{_region}",
            CloudProvider.AWS,
            from,
            to,
            totalCost,
            "USD",
            breakdown);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpotPricing>> GetSpotPricingAsync(
        string? region = null,
        CancellationToken ct = default)
    {
        var targetRegion = region ?? _region;
        var requestBody = JsonSerializer.Serialize(new
        {
            InstanceTypes = new[] { "i3.large", "i3.xlarge", "d2.xlarge", "d3.xlarge" },
            ProductDescriptions = new[] { "Linux/UNIX" },
            MaxResults = 100
        });

        var responseJson = await SendAwsRequestAsync(
            $"https://ec2.{targetRegion}.amazonaws.com/?Action=DescribeSpotPriceHistory&Version=2016-11-15",
            ServiceEc2,
            null,
            requestBody,
            ct,
            httpMethod: "POST",
            contentType: "application/x-www-form-urlencoded").ConfigureAwait(false);

        var results = new List<SpotPricing>();

        if (responseJson.TryGetProperty("spotPriceHistorySet", out var historySet))
        {
            foreach (var item in historySet.EnumerateArray())
            {
                var spotPrice = ParseDecimal(item.GetProperty("spotPrice").GetString());
                var instanceType = item.GetProperty("instanceType").GetString() ?? "unknown";
                var az = item.GetProperty("availabilityZone").GetString() ?? targetRegion;

                // Estimate on-demand price as ~3x spot for storage instances
                var onDemandEstimate = spotPrice * 3.0m;
                var savings = onDemandEstimate > 0 ? (double)((onDemandEstimate - spotPrice) / onDemandEstimate * 100) : 0;

                results.Add(new SpotPricing(
                    CloudProvider.AWS,
                    az,
                    instanceType,
                    onDemandEstimate,
                    spotPrice,
                    savings,
                    AvailableCapacityGB: 0,
                    InterruptionProbability: 0.05));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReservedCapacity>> GetReservedCapacityAsync(
        CancellationToken ct = default)
    {
        var requestBody = "Action=DescribeReservedInstances&Version=2016-11-15&Filter.1.Name=state&Filter.1.Value.1=active";

        var responseJson = await SendAwsRequestAsync(
            $"https://ec2.{_region}.amazonaws.com/",
            ServiceEc2,
            null,
            requestBody,
            ct,
            httpMethod: "POST",
            contentType: "application/x-www-form-urlencoded").ConfigureAwait(false);

        var results = new List<ReservedCapacity>();

        if (responseJson.TryGetProperty("reservedInstancesSet", out var riSet))
        {
            foreach (var ri in riSet.EnumerateArray())
            {
                var instanceType = ri.GetProperty("instanceType").GetString() ?? "unknown";
                var fixedPrice = ParseDecimal(ri.TryGetProperty("fixedPrice", out var fp) ? fp.GetString() : "0");
                var usagePrice = ParseDecimal(ri.TryGetProperty("usagePrice", out var up) ? up.GetString() : "0");
                var duration = ri.TryGetProperty("duration", out var dur) ? dur.GetInt64() : 31536000L; // default 1 year
                var end = ri.TryGetProperty("end", out var endProp)
                    ? DateTimeOffset.Parse(endProp.GetString()!, CultureInfo.InvariantCulture)
                    : DateTimeOffset.UtcNow.AddSeconds(duration);

                var termMonths = (int)(duration / 2592000); // seconds to months
                var onDemandEstimate = usagePrice * 1.4m;
                var savings = onDemandEstimate > 0 ? (double)((onDemandEstimate - usagePrice) / onDemandEstimate * 100) : 30.0;

                results.Add(new ReservedCapacity(
                    CloudProvider.AWS,
                    _region,
                    instanceType,
                    CommittedGB: 0,
                    usagePrice,
                    onDemandEstimate,
                    savings,
                    termMonths,
                    end));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<CostForecast> ForecastCostAsync(int days, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var requestBody = JsonSerializer.Serialize(new
        {
            TimePeriod = new
            {
                Start = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                End = now.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            },
            Granularity = "MONTHLY",
            Metric = "UNBLENDED_COST"
        });

        var responseJson = await SendAwsRequestAsync(
            $"https://ce.{_region}.amazonaws.com/",
            ServiceCe,
            "AWSInsightsIndexService.GetCostForecast",
            requestBody,
            ct).ConfigureAwait(false);

        decimal projectedCost = 0m;
        double confidence = 80.0;

        if (responseJson.TryGetProperty("Total", out var total))
        {
            projectedCost = ParseDecimal(total.GetProperty("Amount").GetString());
        }
        else if (responseJson.TryGetProperty("ForecastResultsByTime", out var forecastResults))
        {
            foreach (var period in forecastResults.EnumerateArray())
            {
                if (period.TryGetProperty("MeanValue", out var meanVal))
                    projectedCost += ParseDecimal(meanVal.GetString());
            }
        }

        var recommendations = new List<CostRecommendation>();
        if (projectedCost > 1000m)
        {
            recommendations.Add(new CostRecommendation(
                RecommendationType.ReserveCapacity,
                "Consider reserved instances for predictable storage workloads",
                projectedCost * 0.30m,
                "Medium",
                "Low"));
        }

        return new CostForecast(
            CloudProvider.AWS,
            days,
            projectedCost,
            confidence,
            recommendations);
    }

    /// <inheritdoc />
    public async Task<bool> ValidateCredentialsAsync(CancellationToken ct = default)
    {
        try
        {
            var requestBody = "Action=GetCallerIdentity&Version=2011-06-15";
            await SendAwsRequestAsync(
                $"https://sts.{_region}.amazonaws.com/",
                ServiceSts,
                null,
                requestBody,
                ct,
                httpMethod: "POST",
                contentType: "application/x-www-form-urlencoded").ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #region AWS Signature V4

    private async Task<JsonElement> SendAwsRequestAsync(
        string url,
        string service,
        string? amzTarget,
        string body,
        CancellationToken ct,
        string httpMethod = "POST",
        string contentType = "application/x-amz-json-1.1")
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var uri = new Uri(url);
            var now = DateTimeOffset.UtcNow;
            var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

            var request = new HttpRequestMessage(new HttpMethod(httpMethod), uri)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            };

            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
            request.Headers.TryAddWithoutValidation("Host", uri.Host);

            if (!string.IsNullOrEmpty(amzTarget))
                request.Headers.TryAddWithoutValidation("x-amz-target", amzTarget);

            // Build canonical request
            var payloadHash = HashSha256(body);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

            var signedHeaders = BuildSignedHeaders(request, amzTarget);
            var canonicalHeaders = BuildCanonicalHeaders(request, uri, amzDate, amzTarget);
            var canonicalRequest = BuildCanonicalRequest(httpMethod, uri, canonicalHeaders, signedHeaders, payloadHash);

            // String to sign
            var credentialScope = $"{dateStamp}/{_region}/{service}/aws4_request";
            var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{HashSha256(canonicalRequest)}";

            // Signing key chain
            var signingKey = DeriveSigningKey(dateStamp, _region, service);
            var signature = HmacSha256Hex(signingKey, stringToSign);

            // Authorization header
            var authorization = $"AWS4-HMAC-SHA256 Credential={_accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseText);
            return doc.RootElement.Clone();
        }, ct).ConfigureAwait(false);
    }

    private byte[] DeriveSigningKey(string dateStamp, string region, string service)
    {
        var kDate = HmacSha256Bytes(Encoding.UTF8.GetBytes($"AWS4{_secretAccessKey}"), dateStamp);
        var kRegion = HmacSha256Bytes(kDate, region);
        var kService = HmacSha256Bytes(kRegion, service);
        return HmacSha256Bytes(kService, "aws4_request");
    }

    private static string BuildSignedHeaders(HttpRequestMessage request, string? amzTarget)
    {
        var headers = new List<string> { "host", "x-amz-content-sha256", "x-amz-date" };
        if (!string.IsNullOrEmpty(amzTarget))
            headers.Add("x-amz-target");
        headers.Sort(StringComparer.Ordinal);
        return string.Join(";", headers);
    }

    private static string BuildCanonicalHeaders(HttpRequestMessage request, Uri uri, string amzDate, string? amzTarget)
    {
        var sb = new StringBuilder();
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.Host,
            ["x-amz-content-sha256"] = request.Headers.GetValues("x-amz-content-sha256").First(),
            ["x-amz-date"] = amzDate
        };
        if (!string.IsNullOrEmpty(amzTarget))
            headers["x-amz-target"] = amzTarget;

        foreach (var kvp in headers)
            sb.Append(kvp.Key).Append(':').Append(kvp.Value).Append('\n');
        return sb.ToString();
    }

    private static string BuildCanonicalRequest(string method, Uri uri, string canonicalHeaders, string signedHeaders, string payloadHash)
    {
        var canonicalUri = uri.AbsolutePath;
        if (string.IsNullOrEmpty(canonicalUri)) canonicalUri = "/";
        var canonicalQueryString = uri.Query.TrimStart('?');

        return $"{method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
    }

    private static string HashSha256(string data)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexStringLower(hash);
    }

    private static byte[] HmacSha256Bytes(byte[] key, string data)
    {
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    }

    private static string HmacSha256Hex(byte[] key, string data)
    {
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
        return Convert.ToHexStringLower(hash);
    }

    #endregion

    #region Helpers

    private static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        int delay = 200;
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries - 1 && IsRetryable(ex))
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2;
            }
        }

        return await action().ConfigureAwait(false); // final attempt, let exception propagate
    }

    private static bool IsRetryable(HttpRequestException ex)
    {
        if (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return true;
        if ((int?)ex.StatusCode >= 500) return true;
        return false;
    }

    private static CostCategory CategorizeAwsService(string serviceName)
    {
        if (serviceName.Contains("S3", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("EBS", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("Storage", StringComparison.OrdinalIgnoreCase))
            return CostCategory.Storage;
        if (serviceName.Contains("EC2", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("Lambda", StringComparison.OrdinalIgnoreCase))
            return CostCategory.Compute;
        if (serviceName.Contains("Transfer", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("CloudFront", StringComparison.OrdinalIgnoreCase))
            return CostCategory.Transfer;
        return CostCategory.Other;
    }

    private static decimal ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0m;
    }

    private static double ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0.0;
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0.0;
    }

    #endregion
}
