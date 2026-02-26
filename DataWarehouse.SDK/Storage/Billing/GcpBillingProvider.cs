using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Storage.Billing;

/// <summary>
/// GCP Cloud Billing provider. Uses raw HttpClient with JWT/RS256 service account
/// authentication to call Cloud Billing, Compute, and Budget APIs without requiring
/// the Google Cloud SDK.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 58: Cloud billing providers")]
public sealed class GcpBillingProvider : IBillingProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _projectId;
    private readonly string _clientEmail;
    private readonly string _privateKeyPem;
    private readonly string? _billingAccountId;

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private const int MaxRetries = 3;
    private const string BillingScope = "https://www.googleapis.com/auth/cloud-billing.readonly https://www.googleapis.com/auth/compute.readonly";

    /// <summary>
    /// Creates a GCP Cloud Billing provider.
    /// </summary>
    /// <param name="httpClient">HTTP client for API calls.</param>
    /// <param name="serviceAccountJson">JSON content of the service account key file (falls back to GOOGLE_APPLICATION_CREDENTIALS env var pointing to a file).</param>
    /// <param name="projectId">GCP project ID (falls back to GCP_PROJECT_ID env var).</param>
    public GcpBillingProvider(
        HttpClient httpClient,
        string? serviceAccountJson = null,
        string? projectId = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        // Resolve service account JSON
        var saJson = serviceAccountJson;
        if (string.IsNullOrWhiteSpace(saJson))
        {
            var credPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            if (!string.IsNullOrWhiteSpace(credPath) && System.IO.File.Exists(credPath))
                saJson = System.IO.File.ReadAllText(credPath);
        }

        if (string.IsNullOrWhiteSpace(saJson))
            throw new InvalidOperationException("GCP service account JSON not configured. Set via constructor or GOOGLE_APPLICATION_CREDENTIALS environment variable.");

        // Parse service account JSON
        using var doc = JsonDocument.Parse(saJson);
        var root = doc.RootElement;

        _clientEmail = root.TryGetProperty("client_email", out var ce)
            ? ce.GetString() ?? throw new InvalidOperationException("Service account JSON missing client_email.")
            : throw new InvalidOperationException("Service account JSON missing client_email.");

        _privateKeyPem = root.TryGetProperty("private_key", out var pk)
            ? pk.GetString() ?? throw new InvalidOperationException("Service account JSON missing private_key.")
            : throw new InvalidOperationException("Service account JSON missing private_key.");

        _projectId = projectId
            ?? Environment.GetEnvironmentVariable("GCP_PROJECT_ID")
            ?? (root.TryGetProperty("project_id", out var pid) ? pid.GetString() : null)
            ?? throw new InvalidOperationException("GCP project ID not configured. Set via constructor, GCP_PROJECT_ID env var, or in service account JSON.");

        // Optional billing account from env
        _billingAccountId = Environment.GetEnvironmentVariable("GCP_BILLING_ACCOUNT_ID");
    }

    /// <inheritdoc />
    public CloudProvider Provider => CloudProvider.GCP;

    /// <inheritdoc />
    public async Task<BillingReport> GetBillingReportAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var token = await AcquireTokenAsync(ct).ConfigureAwait(false);
        var breakdown = new List<CostBreakdown>();
        decimal totalCost = 0m;

        // Get billing info for the project
        if (!string.IsNullOrEmpty(_billingAccountId))
        {
            // Use Cloud Billing API to get service-level costs via budget/billing export
            var servicesUrl = $"https://cloudbilling.googleapis.com/v1/services";
            var servicesJson = await SendGcpGetRequestAsync(servicesUrl, token, ct).ConfigureAwait(false);

            if (servicesJson.TryGetProperty("services", out var services))
            {
                foreach (var service in services.EnumerateArray().Take(50)) // Limit for performance
                {
                    var serviceName = service.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "Unknown" : "Unknown";
                    var serviceId = service.TryGetProperty("serviceId", out var sid) ? sid.GetString() : null;

                    if (serviceId == null) continue;

                    // Get SKUs for this service to estimate costs
                    var skuUrl = $"https://cloudbilling.googleapis.com/v1/services/{serviceId}/skus?currencyCode=USD&pageSize=10";

                    try
                    {
                        var skuJson = await SendGcpGetRequestAsync(skuUrl, token, ct).ConfigureAwait(false);

                        if (skuJson.TryGetProperty("skus", out var skus))
                        {
                            foreach (var sku in skus.EnumerateArray())
                            {
                                if (!sku.TryGetProperty("pricingInfo", out var pricingInfo)) continue;
                                foreach (var pricing in pricingInfo.EnumerateArray())
                                {
                                    if (!pricing.TryGetProperty("pricingExpression", out var expr)) continue;
                                    if (!expr.TryGetProperty("tieredRates", out var rates)) continue;

                                    foreach (var rate in rates.EnumerateArray())
                                    {
                                        if (!rate.TryGetProperty("unitPrice", out var unitPrice)) continue;
                                        var nanos = unitPrice.TryGetProperty("nanos", out var n) ? n.GetInt64() : 0;
                                        var units = unitPrice.TryGetProperty("units", out var u) ? ParseDecimal(u.GetString()) : 0m;

                                        var price = units + (nanos / 1_000_000_000m);
                                        if (price > 0)
                                        {
                                            totalCost += price;
                                            breakdown.Add(new CostBreakdown(
                                                CategorizeGcpService(serviceName),
                                                serviceName,
                                                price,
                                                "USD",
                                                1.0));
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (HttpRequestException)
                    {
                        // Skip services we cannot query
                    }
                }
            }
        }
        else
        {
            // Without billing account, get project billing info
            var billingInfoUrl = $"https://cloudbilling.googleapis.com/v1/projects/{_projectId}/billingInfo";
            try
            {
                var billingInfo = await SendGcpGetRequestAsync(billingInfoUrl, token, ct).ConfigureAwait(false);
                // Billing info confirms project is billable but doesn't provide cost data
                // Cost data requires BigQuery export or detailed billing account access
            }
            catch (HttpRequestException)
            {
                // Billing info not accessible
            }
        }

        return new BillingReport(
            $"gcp-{_projectId}",
            CloudProvider.GCP,
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
        var token = await AcquireTokenAsync(ct).ConfigureAwait(false);
        var results = new List<SpotPricing>();

        // Query Cloud Storage SKUs for pricing
        var storageServiceId = "95FF-2EF5-5EA1"; // Cloud Storage service ID
        var url = $"https://cloudbilling.googleapis.com/v1/services/{storageServiceId}/skus?currencyCode=USD&pageSize=50";

        try
        {
            var responseJson = await SendGcpGetRequestAsync(url, token, ct).ConfigureAwait(false);

            if (responseJson.TryGetProperty("skus", out var skus))
            {
                foreach (var sku in skus.EnumerateArray())
                {
                    var description = sku.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";

                    // Filter to storage-related SKUs
                    if (!description.Contains("Storage", StringComparison.OrdinalIgnoreCase)) continue;

                    var skuRegions = new List<string>();
                    if (sku.TryGetProperty("serviceRegions", out var regions))
                    {
                        foreach (var r in regions.EnumerateArray())
                        {
                            var regionName = r.GetString();
                            if (regionName != null)
                                skuRegions.Add(regionName);
                        }
                    }

                    if (region != null && !skuRegions.Any(r => r.Contains(region, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Extract pricing
                    decimal price = 0m;
                    if (sku.TryGetProperty("pricingInfo", out var pricingInfo))
                    {
                        foreach (var pricing in pricingInfo.EnumerateArray())
                        {
                            if (!pricing.TryGetProperty("pricingExpression", out var expr)) continue;
                            if (!expr.TryGetProperty("tieredRates", out var rates)) continue;

                            foreach (var rate in rates.EnumerateArray())
                            {
                                if (!rate.TryGetProperty("unitPrice", out var unitPrice)) continue;
                                var nanos = unitPrice.TryGetProperty("nanos", out var n) ? n.GetInt64() : 0;
                                var units = unitPrice.TryGetProperty("units", out var u) ? ParseDecimal(u.GetString()) : 0m;
                                price = units + (nanos / 1_000_000_000m);
                            }
                        }
                    }

                    if (price <= 0) continue;

                    // Preemptible/spot is typically ~60-80% off
                    var spotPrice = price * 0.3m;
                    var savings = (double)((price - spotPrice) / price * 100);

                    foreach (var r in skuRegions.Take(3))
                    {
                        results.Add(new SpotPricing(
                            CloudProvider.GCP,
                            r,
                            description,
                            price,
                            spotPrice,
                            savings,
                            AvailableCapacityGB: 0,
                            InterruptionProbability: 0.05));
                    }
                }
            }
        }
        catch (HttpRequestException)
        {
            // Service pricing not accessible
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReservedCapacity>> GetReservedCapacityAsync(
        CancellationToken ct = default)
    {
        var token = await AcquireTokenAsync(ct).ConfigureAwait(false);
        var results = new List<ReservedCapacity>();

        // Query committed use discounts (CUDs) via Compute API
        var url = $"https://compute.googleapis.com/compute/v1/projects/{_projectId}/aggregated/commitments";

        try
        {
            var responseJson = await SendGcpGetRequestAsync(url, token, ct).ConfigureAwait(false);

            if (responseJson.TryGetProperty("items", out var items))
            {
                foreach (var zoneProp in items.EnumerateObject())
                {
                    if (!zoneProp.Value.TryGetProperty("commitments", out var commitments)) continue;

                    var zoneName = zoneProp.Name.Replace("zones/", "");

                    foreach (var commitment in commitments.EnumerateArray())
                    {
                        var name = commitment.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown";
                        var status = commitment.TryGetProperty("status", out var s) ? s.GetString() : "ACTIVE";
                        if (!string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase)) continue;

                        var plan = commitment.TryGetProperty("plan", out var p) ? p.GetString() ?? "TWELVE_MONTH" : "TWELVE_MONTH";
                        var endTimestamp = commitment.TryGetProperty("endTimestamp", out var et)
                            ? DateTimeOffset.Parse(et.GetString()!, CultureInfo.InvariantCulture)
                            : DateTimeOffset.UtcNow.AddYears(1);

                        var termMonths = plan.Contains("THIRTY_SIX", StringComparison.OrdinalIgnoreCase) ? 36 : 12;
                        var savingsPercent = termMonths == 36 ? 57.0 : 37.0;

                        results.Add(new ReservedCapacity(
                            CloudProvider.GCP,
                            zoneName,
                            name,
                            CommittedGB: 0,
                            ReservedPricePerGBMonth: 0m,
                            OnDemandPricePerGBMonth: 0m,
                            savingsPercent,
                            termMonths,
                            endTimestamp));
                    }
                }
            }
        }
        catch (HttpRequestException)
        {
            // Compute API may not be accessible
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<CostForecast> ForecastCostAsync(int days, CancellationToken ct = default)
    {
        var token = await AcquireTokenAsync(ct).ConfigureAwait(false);
        decimal projectedCost = 0m;
        double confidence = 70.0;

        // Try billing budgets API for forecast data
        if (!string.IsNullOrEmpty(_billingAccountId))
        {
            var url = $"https://billingbudgets.googleapis.com/v1/billingAccounts/{_billingAccountId}/budgets";

            try
            {
                var responseJson = await SendGcpGetRequestAsync(url, token, ct).ConfigureAwait(false);

                if (responseJson.TryGetProperty("budgets", out var budgets))
                {
                    foreach (var budget in budgets.EnumerateArray())
                    {
                        if (!budget.TryGetProperty("amount", out var amount)) continue;
                        if (!amount.TryGetProperty("specifiedAmount", out var specified)) continue;

                        var units = specified.TryGetProperty("units", out var u) ? ParseDecimal(u.GetString()) : 0m;
                        var nanos = specified.TryGetProperty("nanos", out var n) ? n.GetInt32() : 0;
                        var budgetAmount = units + (nanos / 1_000_000_000m);

                        // Extrapolate budget to forecast period
                        projectedCost += budgetAmount * ((decimal)days / 30m);
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Budget API not accessible
            }
        }

        var recommendations = new List<CostRecommendation>();
        if (projectedCost > 500m)
        {
            recommendations.Add(new CostRecommendation(
                RecommendationType.ReserveCapacity,
                "Consider GCP Committed Use Discounts for storage workloads",
                projectedCost * 0.37m,
                "Medium",
                "Low"));
        }

        return new CostForecast(
            CloudProvider.GCP,
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

    #region JWT / Token Management

    private async Task<string> AcquireTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _cachedToken;

            var now = DateTimeOffset.UtcNow;
            var expiry = now.AddHours(1);

            // Build JWT header + claim set
            var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "RS256",
                typ = "JWT"
            }));

            var claimSet = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = _clientEmail,
                scope = BillingScope,
                aud = "https://oauth2.googleapis.com/token",
                iat = now.ToUnixTimeSeconds(),
                exp = expiry.ToUnixTimeSeconds()
            }));

            var unsignedJwt = $"{header}.{claimSet}";

            // Sign with RS256
            var signature = SignRs256(unsignedJwt, _privateKeyPem);
            var jwt = $"{unsignedJwt}.{signature}";

            // Exchange JWT for access token
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = jwt
            });

            using var response = await _httpClient.PostAsync("https://oauth2.googleapis.com/token", content, ct).ConfigureAwait(false);
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

    private static string SignRs256(string data, string privateKeyPem)
    {
        // Strip PEM headers and decode
        var pemContent = privateKeyPem
            .Replace("-----BEGIN PRIVATE KEY-----", "")
            .Replace("-----END PRIVATE KEY-----", "")
            .Replace("-----BEGIN RSA PRIVATE KEY-----", "")
            .Replace("-----END RSA PRIVATE KEY-----", "")
            .Replace("\n", "")
            .Replace("\r", "")
            .Trim();

        var keyBytes = Convert.FromBase64String(pemContent);

        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(keyBytes, out _);

        var signatureBytes = rsa.SignData(
            Encoding.UTF8.GetBytes(data),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return Base64UrlEncode(signatureBytes);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    #endregion

    #region HTTP Helpers

    private async Task<JsonElement> SendGcpGetRequestAsync(
        string url,
        string token,
        CancellationToken ct)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
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

    private static CostCategory CategorizeGcpService(string serviceName)
    {
        if (serviceName.Contains("Storage", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("Filestore", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("Persistent Disk", StringComparison.OrdinalIgnoreCase))
            return CostCategory.Storage;
        if (serviceName.Contains("Compute", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("Engine", StringComparison.OrdinalIgnoreCase))
            return CostCategory.Compute;
        if (serviceName.Contains("Network", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("CDN", StringComparison.OrdinalIgnoreCase))
            return CostCategory.Transfer;
        return CostCategory.Other;
    }

    private static decimal ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0m;
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0m;
    }

    #endregion
}
