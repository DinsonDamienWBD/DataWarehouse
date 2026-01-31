using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Production-ready carbon offset provider using Patch API.
/// Integrates with Patch (https://www.patch.io/) for purchasing verified carbon offsets.
/// API Documentation: https://docs.patch.io/
/// </summary>
public class PatchCarbonOffsetProviderPlugin : CarbonOffsetProviderPluginBase
{
    private const string BaseUrl = "https://api.patch.io/v1";
    private const string SandboxUrl = "https://api.sandbox.patch.io/v1";

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, OffsetPurchase> _purchaseCache = new();
    private string? _apiKey;
    private bool _useSandbox;
    private bool _disposed;

    // Cache for projects
    private IReadOnlyList<OffsetProject>? _cachedProjects;
    private DateTimeOffset _projectsCacheExpiry = DateTimeOffset.MinValue;
    private readonly TimeSpan _projectsCacheDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// Initializes a new instance with a default HttpClient.
    /// </summary>
    public PatchCarbonOffsetProviderPlugin() : this(new HttpClient())
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom HttpClient for testing.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for API calls.</param>
    public PatchCarbonOffsetProviderPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.offsetprovider.patch";

    /// <inheritdoc />
    public override string Name => "Patch Carbon Offset Provider";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <summary>
    /// Configures the Patch API key and environment.
    /// Get your API key at: https://dashboard.patch.io/
    /// </summary>
    /// <param name="apiKey">Patch API key (starts with 'key_live_' or 'key_test_').</param>
    /// <param name="useSandbox">Use sandbox environment for testing (default: false).</param>
    public void Configure(string apiKey, bool useSandbox = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));

        _apiKey = apiKey;
        _useSandbox = useSandbox;

        var baseUrl = useSandbox ? SandboxUrl : BaseUrl;
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc />
    protected override async Task<OffsetPurchase> ExecutePurchaseAsync(double carbonGrams, OffsetOptions options)
    {
        EnsureConfigured();

        try
        {
            // Convert grams to metric tons (Patch uses metric tons)
            var massG = (long)carbonGrams;

            // Build order request
            var orderRequest = new PatchOrderRequest
            {
                MassG = massG,
                ProjectId = options.PreferredProjectId,
                State = "placed" // Create and place the order immediately
            };

            // If a specific standard is required, we need to filter projects first
            if (options.RequiredStandard.HasValue && string.IsNullOrEmpty(options.PreferredProjectId))
            {
                var projects = await FetchProjectsAsync();
                var eligibleProject = projects
                    .Where(p => p.Standard == options.RequiredStandard.Value)
                    .OrderBy(p => p.PricePerTonCO2)
                    .FirstOrDefault();

                if (eligibleProject != null)
                {
                    orderRequest.ProjectId = eligibleProject.ProjectId;
                }
            }

            // POST /v1/orders
            var response = await _httpClient.PostAsJsonAsync("/v1/orders", orderRequest, JsonOptions);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PatchOrderResponse>(JsonOptions);

            if (result?.Data == null)
            {
                throw new InvalidOperationException("Empty response from Patch API");
            }

            var order = result.Data;

            // Map to our OffsetPurchase model
            var purchase = new OffsetPurchase(
                order.Id,
                order.Allocations?.FirstOrDefault()?.Project?.Id ?? "unknown",
                carbonGrams,
                (double)(order.PriceCentsUsd ?? 0) / 100.0, // Convert cents to dollars
                order.CreatedAt ?? DateTimeOffset.UtcNow,
                order.RegistryUrl ?? $"https://dashboard.patch.io/orders/{order.Id}"
            );

            // Cache the purchase
            _purchaseCache[purchase.PurchaseId] = purchase;

            return purchase;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to purchase carbon offsets: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<OffsetProject>> FetchProjectsAsync()
    {
        EnsureConfigured();

        // Return cached projects if still valid
        if (_cachedProjects != null && DateTimeOffset.UtcNow < _projectsCacheExpiry)
        {
            return _cachedProjects;
        }

        try
        {
            // GET /v1/projects
            var response = await _httpClient.GetAsync("/v1/projects?page=1&per_page=100");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PatchProjectsResponse>(JsonOptions);

            if (result?.Data == null)
            {
                return Array.Empty<OffsetProject>();
            }

            var projects = result.Data.Select(MapToOffsetProject).ToList();

            _cachedProjects = projects;
            _projectsCacheExpiry = DateTimeOffset.UtcNow.Add(_projectsCacheDuration);

            return _cachedProjects;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch offset projects: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<OffsetPurchase>> FetchPurchaseHistoryAsync()
    {
        EnsureConfigured();

        try
        {
            // GET /v1/orders
            var response = await _httpClient.GetAsync("/v1/orders?page=1&per_page=100");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PatchOrdersResponse>(JsonOptions);

            if (result?.Data == null)
            {
                return Array.Empty<OffsetPurchase>();
            }

            var purchases = result.Data
                .Where(o => o.State == "placed" || o.State == "complete")
                .Select(order => new OffsetPurchase(
                    order.Id,
                    order.Allocations?.FirstOrDefault()?.Project?.Id ?? "unknown",
                    order.MassG ?? 0,
                    (double)(order.PriceCentsUsd ?? 0) / 100.0,
                    order.CreatedAt ?? DateTimeOffset.UtcNow,
                    order.RegistryUrl ?? $"https://dashboard.patch.io/orders/{order.Id}"
                ))
                .OrderByDescending(p => p.PurchasedAt)
                .ToList();

            // Update cache
            foreach (var purchase in purchases)
            {
                _purchaseCache[purchase.PurchaseId] = purchase;
            }

            return purchases;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch purchase history: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets a specific order by ID.
    /// </summary>
    /// <param name="orderId">Order ID.</param>
    /// <returns>Order details.</returns>
    public async Task<OffsetPurchase?> GetOrderAsync(string orderId)
    {
        EnsureConfigured();

        // Check cache first
        if (_purchaseCache.TryGetValue(orderId, out var cached))
        {
            return cached;
        }

        try
        {
            // GET /v1/orders/{orderId}
            var response = await _httpClient.GetAsync($"/v1/orders/{Uri.EscapeDataString(orderId)}");

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<PatchOrderResponse>(JsonOptions);

            if (result?.Data == null)
            {
                return null;
            }

            var order = result.Data;
            var purchase = new OffsetPurchase(
                order.Id,
                order.Allocations?.FirstOrDefault()?.Project?.Id ?? "unknown",
                order.MassG ?? 0,
                (double)(order.PriceCentsUsd ?? 0) / 100.0,
                order.CreatedAt ?? DateTimeOffset.UtcNow,
                order.RegistryUrl ?? $"https://dashboard.patch.io/orders/{order.Id}"
            );

            _purchaseCache[orderId] = purchase;
            return purchase;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Cancels an order that hasn't been processed yet.
    /// </summary>
    /// <param name="orderId">Order ID to cancel.</param>
    /// <returns>True if cancelled successfully.</returns>
    public async Task<bool> CancelOrderAsync(string orderId)
    {
        EnsureConfigured();

        try
        {
            // PATCH /v1/orders/{orderId} with state: cancelled
            var request = new HttpRequestMessage(HttpMethod.Patch, $"/v1/orders/{Uri.EscapeDataString(orderId)}")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { state = "cancelled" }, JsonOptions),
                    Encoding.UTF8,
                    "application/json"
                )
            };

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _purchaseCache.TryRemove(orderId, out _);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets an estimate for offsetting a specific amount of carbon.
    /// </summary>
    /// <param name="carbonGrams">Amount of carbon in grams.</param>
    /// <param name="projectId">Optional specific project ID.</param>
    /// <returns>Estimated cost and details.</returns>
    public async Task<OffsetEstimate?> GetEstimateAsync(double carbonGrams, string? projectId = null)
    {
        EnsureConfigured();

        try
        {
            // POST /v1/estimates
            var request = new PatchEstimateRequest
            {
                MassG = (long)carbonGrams,
                ProjectId = projectId
            };

            var response = await _httpClient.PostAsJsonAsync("/v1/estimates", request, JsonOptions);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PatchEstimateResponse>(JsonOptions);

            if (result?.Data == null)
            {
                return null;
            }

            var estimate = result.Data;
            return new OffsetEstimate(
                carbonGrams,
                (double)(estimate.PriceCentsUsd ?? 0) / 100.0,
                estimate.Allocations?.FirstOrDefault()?.Project?.Id,
                estimate.Allocations?.FirstOrDefault()?.Project?.Name
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets total carbon offset in grams from all purchases.
    /// </summary>
    /// <returns>Total offset amount.</returns>
    public async Task<double> GetTotalOffsetGramsAsync()
    {
        var history = await FetchPurchaseHistoryAsync();
        return history.Sum(p => p.CarbonGramsOffset);
    }

    /// <summary>
    /// Gets total cost of all offset purchases.
    /// </summary>
    /// <returns>Total cost in USD.</returns>
    public async Task<double> GetTotalCostAsync()
    {
        var history = await FetchPurchaseHistoryAsync();
        return history.Sum(p => p.Cost);
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new InvalidOperationException(
                "Patch API key not configured. Call Configure(apiKey) before using this provider.");
        }
    }

    private static OffsetProject MapToOffsetProject(PatchProject p)
    {
        // Map Patch verification standard to our OffsetStandard enum
        var standard = MapVerificationStandard(p.VerificationStandard);

        return new OffsetProject(
            p.Id,
            p.Name ?? "Unknown Project",
            p.Type ?? "Unknown",
            standard,
            p.AveragePricePerTonneCentsUsd.HasValue
                ? p.AveragePricePerTonneCentsUsd.Value / 100.0
                : 0.0,
            p.Country ?? "Unknown"
        );
    }

    private static OffsetStandard MapVerificationStandard(string? standard)
    {
        if (string.IsNullOrEmpty(standard))
            return OffsetStandard.VCS;

        return standard.ToUpperInvariant() switch
        {
            "VCS" or "VERRA" or "VERIFIED CARBON STANDARD" => OffsetStandard.VCS,
            "GOLD_STANDARD" or "GOLD STANDARD" or "GS" => OffsetStandard.GoldStandard,
            "ACR" or "AMERICAN CARBON REGISTRY" => OffsetStandard.ACR,
            "CAR" or "CLIMATE ACTION RESERVE" => OffsetStandard.CAR,
            _ => OffsetStandard.VCS // Default to VCS for unknown standards
        };
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync()
    {
        _cachedProjects = null;
        _purchaseCache.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // API Request/Response Models

    private sealed class PatchOrderRequest
    {
        [JsonPropertyName("mass_g")]
        public long MassG { get; set; }

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; } = "placed";
    }

    private sealed class PatchEstimateRequest
    {
        [JsonPropertyName("mass_g")]
        public long MassG { get; set; }

        [JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }
    }

    private sealed class PatchOrderResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public PatchOrder? Data { get; set; }
    }

    private sealed class PatchOrdersResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public List<PatchOrder>? Data { get; set; }
    }

    private sealed class PatchEstimateResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public PatchEstimate? Data { get; set; }
    }

    private sealed class PatchProjectsResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public List<PatchProject>? Data { get; set; }
    }

    private sealed class PatchOrder
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("mass_g")]
        public long? MassG { get; set; }

        [JsonPropertyName("price_cents_usd")]
        public int? PriceCentsUsd { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("registry_url")]
        public string? RegistryUrl { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("allocations")]
        public List<PatchAllocation>? Allocations { get; set; }
    }

    private sealed class PatchEstimate
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("mass_g")]
        public long? MassG { get; set; }

        [JsonPropertyName("price_cents_usd")]
        public int? PriceCentsUsd { get; set; }

        [JsonPropertyName("allocations")]
        public List<PatchAllocation>? Allocations { get; set; }
    }

    private sealed class PatchAllocation
    {
        [JsonPropertyName("mass_g")]
        public long? MassG { get; set; }

        [JsonPropertyName("price_cents_usd")]
        public int? PriceCentsUsd { get; set; }

        [JsonPropertyName("project")]
        public PatchProjectRef? Project { get; set; }
    }

    private sealed class PatchProjectRef
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class PatchProject
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("verification_standard")]
        public string? VerificationStandard { get; set; }

        [JsonPropertyName("average_price_per_tonne_cents_usd")]
        public int? AveragePricePerTonneCentsUsd { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("technology_type")]
        public string? TechnologyType { get; set; }

        [JsonPropertyName("latitude")]
        public double? Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double? Longitude { get; set; }
    }
}

/// <summary>
/// Offset cost estimate.
/// </summary>
public record OffsetEstimate(
    /// <summary>Amount of carbon in grams.</summary>
    double CarbonGrams,

    /// <summary>Estimated cost in USD.</summary>
    double EstimatedCostUsd,

    /// <summary>Suggested project ID.</summary>
    string? SuggestedProjectId,

    /// <summary>Suggested project name.</summary>
    string? SuggestedProjectName
);
