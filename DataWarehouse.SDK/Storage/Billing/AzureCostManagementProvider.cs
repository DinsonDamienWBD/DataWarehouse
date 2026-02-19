using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Billing;

/// <summary>
/// Azure Cost Management billing provider. Uses raw HttpClient with OAuth2 client credentials
/// to call Azure Cost Management, Retail Pricing, and Reservation APIs without requiring the Azure SDK.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Cloud billing providers")]
public sealed class AzureCostManagementProvider : IBillingProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _subscriptionId;

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private const int MaxRetries = 3;
    private const string CostManagementApiVersion = "2023-11-01";
    private const string ReservationApiVersion = "2022-11-01";

    /// <summary>
    /// Creates an Azure Cost Management billing provider.
    /// </summary>
    /// <param name="httpClient">HTTP client for API calls.</param>
    /// <param name="tenantId">Azure AD tenant ID (falls back to AZURE_TENANT_ID env var).</param>
    /// <param name="clientId">Azure AD client/application ID (falls back to AZURE_CLIENT_ID env var).</param>
    /// <param name="clientSecret">Azure AD client secret (falls back to AZURE_CLIENT_SECRET env var).</param>
    /// <param name="subscriptionId">Azure subscription ID (falls back to AZURE_SUBSCRIPTION_ID env var).</param>
    public AzureCostManagementProvider(
        HttpClient httpClient,
        string? tenantId = null,
        string? clientId = null,
        string? clientSecret = null,
        string? subscriptionId = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tenantId = tenantId
            ?? Environment.GetEnvironmentVariable("AZURE_TENANT_ID")
            ?? throw new InvalidOperationException("Azure tenant ID not configured. Set via constructor or AZURE_TENANT_ID environment variable.");
        _clientId = clientId
            ?? Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")
            ?? throw new InvalidOperationException("Azure client ID not configured. Set via constructor or AZURE_CLIENT_ID environment variable.");
        _clientSecret = clientSecret
            ?? Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET")
            ?? throw new InvalidOperationException("Azure client secret not configured. Set via constructor or AZURE_CLIENT_SECRET environment variable.");
        _subscriptionId = subscriptionId
            ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID")
            ?? throw new InvalidOperationException("Azure subscription ID not configured. Set via constructor or AZURE_SUBSCRIPTION_ID environment variable.");
    }

    /// <inheritdoc />
    public CloudProvider Provider => CloudProvider.Azure;

    /// <inheritdoc />
    public async Task<BillingReport> GetBillingReportAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var token = await AcquireTokenAsync(ct).ConfigureAwait(false);

        var requestBody = JsonSerializer.Serialize(new
        {
            type = "ActualCost",
            dataSet = new
            {
                granularity = "Monthly",
                aggregation = new
                {
                    totalCost = new { name = "Cost", function = "Sum" },
                    totalCostUSD = new { name = "CostUSD", function = "Sum" }
                },
                grouping = new[]
                {
                    new { type = "Dimension", name = "ServiceName" }
                }
            },
            timeframe = "Custom",
            timePeriod = new
            {
                from = from.ToString("yyyy-MM-dd'T'HH:mm:ssZ", CultureInfo.InvariantCulture),
                to = to.ToString("yyyy-MM-dd'T'HH:mm:ssZ", CultureInfo.InvariantCulture)
            }
        });

        var url = $"https://management.azure.com/subscriptions/{_subscriptionId}/providers/Microsoft.CostManagement/query?api-version={CostManagementApiVersion}";
        var responseJson = await SendAzureRequestAsync(url, token, requestBody, ct).ConfigureAwait(false);

        var breakdown = new List<CostBreakdown>();
        decimal totalCost = 0m;

        if (responseJson.TryGetProperty("properties", out var properties) &&
            properties.TryGetProperty("rows", out var rows))
        {
            // Azure returns column-based data: columns define the schema, rows contain values
            var columns = properties.GetProperty("columns");
            int costIndex = FindColumnIndex(columns, "Cost");
            int serviceIndex = FindColumnIndex(columns, "ServiceName");

            foreach (var row in rows.EnumerateArray())
            {
                var cost = row[costIndex].GetDecimal();
                var service = row[serviceIndex].GetString() ?? "Unknown";

                totalCost += cost;
                breakdown.Add(new CostBreakdown(
                    CategorizeAzureService(service),
                    service,
                    cost,
                    "USD",
                    (double)cost));
            }
        }

        return new BillingReport(
            $"azure-{_subscriptionId}",
            CloudProvider.Azure,
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
        var filter = "serviceFamily eq 'Storage'";
        if (!string.IsNullOrEmpty(region))
            filter += $" and armRegionName eq '{region}'";

        var url = $"https://prices.azure.com/api/retail/prices?$filter={Uri.EscapeDataString(filter)}";

        // Retail Pricing API is public, no auth needed
        var responseJson = await ExecuteWithRetryAsync(async () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }, ct).ConfigureAwait(false);

        var results = new List<SpotPricing>();

        if (responseJson.TryGetProperty("Items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var retailPrice = item.TryGetProperty("retailPrice", out var rp) ? rp.GetDecimal() : 0m;
                var unitPrice = item.TryGetProperty("unitPrice", out var up) ? up.GetDecimal() : 0m;
                var skuName = item.TryGetProperty("skuName", out var sn) ? sn.GetString() ?? "Unknown" : "Unknown";
                var armRegion = item.TryGetProperty("armRegionName", out var arn) ? arn.GetString() ?? "global" : "global";
                var isPrimaryMeter = item.TryGetProperty("isPrimaryMeterRegion", out var ipm) && ipm.GetBoolean();

                if (!isPrimaryMeter) continue;

                // Estimate spot as percentage discount off retail
                var spotEstimate = retailPrice * 0.6m;
                var savings = retailPrice > 0 ? (double)((retailPrice - spotEstimate) / retailPrice * 100) : 0;

                results.Add(new SpotPricing(
                    CloudProvider.Azure,
                    armRegion,
                    skuName,
                    retailPrice,
                    spotEstimate,
                    savings,
                    AvailableCapacityGB: 0,
                    InterruptionProbability: 0.03));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReservedCapacity>> GetReservedCapacityAsync(
        CancellationToken ct = default)
    {
        var token = await AcquireTokenAsync(ct).ConfigureAwait(false);
        var url = $"https://management.azure.com/subscriptions/{_subscriptionId}/providers/Microsoft.Capacity/reservationOrders?api-version={ReservationApiVersion}";

        var responseJson = await SendAzureGetRequestAsync(url, token, ct).ConfigureAwait(false);
        var results = new List<ReservedCapacity>();

        if (responseJson.TryGetProperty("value", out var reservations))
        {
            foreach (var reservation in reservations.EnumerateArray())
            {
                if (!reservation.TryGetProperty("properties", out var props)) continue;

                var displayName = props.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "Unknown" : "Unknown";
                var term = props.TryGetProperty("term", out var t) ? t.GetString() ?? "P1Y" : "P1Y";
                var expiryDate = props.TryGetProperty("expiryDateTime", out var ed)
                    ? DateTimeOffset.Parse(ed.GetString()!, CultureInfo.InvariantCulture)
                    : DateTimeOffset.UtcNow.AddYears(1);

                var termMonths = term.Contains("3Y", StringComparison.OrdinalIgnoreCase) ? 36 : 12;

                results.Add(new ReservedCapacity(
                    CloudProvider.Azure,
                    "global",
                    displayName,
                    CommittedGB: 0,
                    ReservedPricePerGBMonth: 0m,
                    OnDemandPricePerGBMonth: 0m,
                    SavingsPercent: term.Contains("3Y", StringComparison.OrdinalIgnoreCase) ? 50.0 : 30.0,
                    termMonths,
                    expiryDate));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<CostForecast> ForecastCostAsync(int days, CancellationToken ct = default)
    {
        var token = await AcquireTokenAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var requestBody = JsonSerializer.Serialize(new
        {
            type = "ActualCost",
            dataSet = new
            {
                granularity = "Monthly",
                aggregation = new
                {
                    totalCost = new { name = "Cost", function = "Sum" }
                }
            },
            includeActualCost = true,
            includeFreshPartialCost = true,
            timeframe = "Custom",
            timePeriod = new
            {
                from = now.ToString("yyyy-MM-dd'T'HH:mm:ssZ", CultureInfo.InvariantCulture),
                to = now.AddDays(days).ToString("yyyy-MM-dd'T'HH:mm:ssZ", CultureInfo.InvariantCulture)
            }
        });

        var url = $"https://management.azure.com/subscriptions/{_subscriptionId}/providers/Microsoft.CostManagement/forecast?api-version={CostManagementApiVersion}";

        decimal projectedCost = 0m;
        double confidence = 75.0;

        try
        {
            var responseJson = await SendAzureRequestAsync(url, token, requestBody, ct).ConfigureAwait(false);

            if (responseJson.TryGetProperty("properties", out var properties) &&
                properties.TryGetProperty("rows", out var rows))
            {
                foreach (var row in rows.EnumerateArray())
                {
                    projectedCost += row[0].GetDecimal();
                }
            }
        }
        catch (HttpRequestException)
        {
            // Forecast API may not be available for all subscriptions
            projectedCost = 0m;
            confidence = 0.0;
        }

        var recommendations = new List<CostRecommendation>();
        if (projectedCost > 500m)
        {
            recommendations.Add(new CostRecommendation(
                RecommendationType.ReserveCapacity,
                "Consider Azure Reserved Instances for storage workloads",
                projectedCost * 0.25m,
                "Medium",
                "Low"));
        }

        return new CostForecast(
            CloudProvider.Azure,
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
            await AcquireTokenAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #region Token Management

    private async Task<string> AcquireTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _cachedToken;

            var tokenUrl = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token";
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["scope"] = "https://management.azure.com/.default"
            });

            var response = await _httpClient.PostAsync(tokenUrl, content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            _cachedToken = root.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Token response missing access_token.");
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    #endregion

    #region HTTP Helpers

    private async Task<JsonElement> SendAzureRequestAsync(
        string url,
        string token,
        string body,
        CancellationToken ct)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseText);
            return doc.RootElement.Clone();
        }, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendAzureGetRequestAsync(
        string url,
        string token,
        CancellationToken ct)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseText);
            return doc.RootElement.Clone();
        }, ct).ConfigureAwait(false);
    }

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

        return await action().ConfigureAwait(false);
    }

    private static bool IsRetryable(HttpRequestException ex)
    {
        if (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return true;
        if ((int?)ex.StatusCode >= 500) return true;
        return false;
    }

    #endregion

    #region Helpers

    private static int FindColumnIndex(JsonElement columns, string name)
    {
        int index = 0;
        foreach (var col in columns.EnumerateArray())
        {
            if (col.TryGetProperty("name", out var n) &&
                string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                return index;
            index++;
        }
        return 0; // default to first column
    }

    private static CostCategory CategorizeAzureService(string serviceName)
    {
        if (serviceName.Contains("Storage", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("Blob", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("Disk", StringComparison.OrdinalIgnoreCase))
            return CostCategory.Storage;
        if (serviceName.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            return CostCategory.Compute;
        if (serviceName.Contains("Bandwidth", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("CDN", StringComparison.OrdinalIgnoreCase))
            return CostCategory.Transfer;
        return CostCategory.Other;
    }

    #endregion
}
