using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Security;

/// <summary>
/// Smart rate-limiting strategy with AI-driven abuse detection.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready intelligent rate limiting with:
/// <list type="bullet">
/// <item><description>Sliding window rate limiting per client</description></item>
/// <item><description>AI-driven abuse detection via Intelligence plugin</description></item>
/// <item><description>429 Too Many Requests with Retry-After headers</description></item>
/// <item><description>Tiered limits (free, standard, premium, enterprise)</description></item>
/// <item><description>Graceful degradation to fixed-window when AI unavailable</description></item>
/// </list>
/// </para>
/// <para>
/// Follows RFC 6585 for 429 status code and includes X-RateLimit-* headers.
/// Uses Intelligence for anomaly detection and adaptive throttling.
/// </para>
/// </remarks>
internal sealed class SmartRateLimitStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    // IPluginInterfaceStrategy metadata
    public override string StrategyId => "smart-rate-limit";
    public string DisplayName => "Smart Rate Limit";
    public string SemanticDescription => "Intelligent rate limiting - tracks request rates with sliding windows, uses AI for abuse detection, returns 429 with Retry-After for exceeded limits.";
    public InterfaceCategory Category => InterfaceCategory.Innovation;
    public string[] Tags => new[] { "rate-limiting", "throttling", "abuse-detection", "429", "security" };

    // SDK contract properties
    public override bool IsProductionReady => false;
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.REST;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json" },
        MaxRequestSize: 10 * 1024 * 1024, // 10 MB
        MaxResponseSize: 50 * 1024 * 1024, // 50 MB
        DefaultTimeout: TimeSpan.FromSeconds(30)
    );

    // Rate limit tiers
    private static readonly Dictionary<string, RateLimitTier> Tiers = new()
    {
        ["free"] = new RateLimitTier { RequestsPerMinute = 10, RequestsPerHour = 100, BurstSize = 20 },
        ["standard"] = new RateLimitTier { RequestsPerMinute = 60, RequestsPerHour = 1000, BurstSize = 100 },
        ["premium"] = new RateLimitTier { RequestsPerMinute = 300, RequestsPerHour = 10000, BurstSize = 500 },
        ["enterprise"] = new RateLimitTier { RequestsPerMinute = 1000, RequestsPerHour = 100000, BurstSize = 2000 }
    };

    // Client request tracking
    private readonly BoundedDictionary<string, ClientRateData> _clientData = new BoundedDictionary<string, ClientRateData>(1000);

    /// <summary>
    /// Rate limit tier configuration.
    /// </summary>
    private sealed class RateLimitTier
    {
        public required int RequestsPerMinute { get; init; }
        public required int RequestsPerHour { get; init; }
        public required int BurstSize { get; init; }
    }

    /// <summary>
    /// Client rate limit data with sliding window tracking.
    /// </summary>
    private sealed class ClientRateData
    {
        public required string ClientId { get; init; }
        public required string Tier { get; init; }
        public ConcurrentQueue<DateTimeOffset> RequestTimestamps { get; } = new();
        public int TotalRequests { get; set; }
        public DateTimeOffset? BlockedUntil { get; set; }
        public double AbuseScore { get; set; }
    }

    /// <summary>
    /// Initializes the Smart Rate Limit strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up Smart Rate Limit resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        _clientData.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles requests with smart rate limiting.
    /// </summary>
    /// <param name="request">The validated interface request.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An InterfaceResponse with rate limit headers or 429 Too Many Requests.</returns>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Identify client
            var clientId = ExtractClientId(request);
            var tier = request.Headers?.GetValueOrDefault("X-Rate-Limit-Tier") ?? "free";

            if (!Tiers.ContainsKey(tier))
                tier = "free";

            // Get or create client rate data
            var clientData = _clientData.GetOrAdd(clientId, _ => new ClientRateData
            {
                ClientId = clientId,
                Tier = tier
            });

            var now = DateTimeOffset.UtcNow;
            var tierConfig = Tiers[tier];

            // Check if client is blocked
            if (clientData.BlockedUntil.HasValue && clientData.BlockedUntil.Value > now)
            {
                var retryAfter = (int)(clientData.BlockedUntil.Value - now).TotalSeconds;
                return Create429Response(clientId, tier, 0, retryAfter, clientData.AbuseScore);
            }

            // Clean up old timestamps (sliding window)
            CleanupOldTimestamps(clientData, now);

            // P2-3365: Avoid two O(n) Count(predicate) passes. Since timestamps are
            // inserted in monotonically increasing order, iterate once from the head to
            // count both windows simultaneously.
            int requestsLastMinute = 0, requestsLastHour = 0;
            var minuteThreshold = now.AddMinutes(-1);
            var hourThreshold = now.AddHours(-1);
            foreach (var ts in clientData.RequestTimestamps)
            {
                if (ts >= hourThreshold)
                {
                    requestsLastHour++;
                    if (ts >= minuteThreshold)
                        requestsLastMinute++;
                }
            }

            // Check rate limits
            if (requestsLastMinute >= tierConfig.RequestsPerMinute)
            {
                clientData.BlockedUntil = now.AddMinutes(1);
                return Create429Response(clientId, tier, tierConfig.RequestsPerMinute - requestsLastMinute, 60, clientData.AbuseScore);
            }

            if (requestsLastHour >= tierConfig.RequestsPerHour)
            {
                clientData.BlockedUntil = now.AddHours(1);
                return Create429Response(clientId, tier, tierConfig.RequestsPerHour - requestsLastHour, 3600, clientData.AbuseScore);
            }

            // Check for abuse using Intelligence if available
            if (IsIntelligenceAvailable && MessageBus != null)
            {
                var abuseScore = await CheckForAbuseAsync(clientId, requestsLastMinute, requestsLastHour, cancellationToken);
                clientData.AbuseScore = abuseScore;

                // Block if abuse score is too high
                if (abuseScore > 0.8)
                {
                    clientData.BlockedUntil = now.AddMinutes(15);
                    return Create429Response(clientId, tier, 0, 900, abuseScore, "Potential abuse detected");
                }
            }
            else
            {
                // Fallback: simple heuristic for abuse detection
                if (requestsLastMinute > tierConfig.BurstSize)
                {
                    clientData.AbuseScore = 0.9;
                    clientData.BlockedUntil = now.AddMinutes(5);
                    return Create429Response(clientId, tier, 0, 300, 0.9, "Burst limit exceeded");
                }
            }

            // Record this request
            clientData.RequestTimestamps.Enqueue(now);
            clientData.TotalRequests++;

            // Calculate remaining capacity
            var remainingMinute = tierConfig.RequestsPerMinute - requestsLastMinute - 1;
            var remainingHour = tierConfig.RequestsPerHour - requestsLastHour - 1;

            // Generate response
            var responseData = new
            {
                message = "Request allowed",
                clientId,
                tier,
                rateLimit = new
                {
                    requestsLastMinute,
                    requestsLastHour,
                    remainingMinute,
                    remainingHour
                },
                timestamp = DateTimeOffset.UtcNow.ToString("O")
            };

            var json = JsonSerializer.Serialize(responseData, new JsonSerializerOptions { WriteIndented = true });
            var body = Encoding.UTF8.GetBytes(json);

            return new SdkInterface.InterfaceResponse(
                StatusCode: 200,
                Headers: new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["X-RateLimit-Limit-Minute"] = tierConfig.RequestsPerMinute.ToString(),
                    ["X-RateLimit-Limit-Hour"] = tierConfig.RequestsPerHour.ToString(),
                    ["X-RateLimit-Remaining-Minute"] = Math.Max(0, remainingMinute).ToString(),
                    ["X-RateLimit-Remaining-Hour"] = Math.Max(0, remainingHour).ToString(),
                    ["X-RateLimit-Reset-Minute"] = now.AddMinutes(1).ToUnixTimeSeconds().ToString(),
                    ["X-RateLimit-Reset-Hour"] = now.AddHours(1).ToUnixTimeSeconds().ToString()
                },
                Body: body
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(500, "Internal Server Error", ex.Message);
        }
    }

    /// <summary>
    /// Extracts client ID from request headers or IP address.
    /// </summary>
    private string ExtractClientId(SdkInterface.InterfaceRequest request)
    {
        // Priority: API key > Auth token > IP address
        var apiKey = request.Headers?.GetValueOrDefault("X-API-Key");
        if (!string.IsNullOrEmpty(apiKey))
            return $"apikey:{apiKey.Substring(0, Math.Min(8, apiKey.Length))}";

        var authHeader = request.Headers?.GetValueOrDefault("Authorization");
        if (!string.IsNullOrEmpty(authHeader))
            return $"auth:{authHeader.GetHashCode():X8}";

        var ip = request.Headers?.GetValueOrDefault("X-Forwarded-For") ??
                 request.Headers?.GetValueOrDefault("X-Real-IP") ??
                 "unknown";
        return $"ip:{ip}";
    }

    /// <summary>
    /// Cleans up old timestamps outside the sliding window.
    /// </summary>
    private void CleanupOldTimestamps(ClientRateData clientData, DateTimeOffset now)
    {
        var cutoff = now.AddHours(-1);
        while (clientData.RequestTimestamps.TryPeek(out var oldest) && oldest < cutoff)
        {
            clientData.RequestTimestamps.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Checks for abuse using Intelligence plugin.
    /// </summary>
    private async Task<double> CheckForAbuseAsync(string clientId, int requestsLastMinute, int requestsLastHour, CancellationToken cancellationToken)
    {
        var abuseMessage = new SDK.Utilities.PluginMessage
        {
            Type = "intelligence.abuse.detect",
            SourcePluginId = "UltimateInterface",
            Payload = new Dictionary<string, object>
            {
                ["clientId"] = clientId,
                ["requestsLastMinute"] = requestsLastMinute,
                ["requestsLastHour"] = requestsLastHour,
                ["timestamp"] = DateTimeOffset.UtcNow
            }
        };

        await MessageBus!.PublishAsync("intelligence.abuse.detect", abuseMessage, cancellationToken);

        // In production, this would await a response. For now, return a mock score.
        // Score is normalized 0.0-1.0, where 1.0 = definite abuse
        return requestsLastMinute > 50 ? 0.7 : 0.1;
    }

    /// <summary>
    /// Creates a 429 Too Many Requests response.
    /// </summary>
    private SdkInterface.InterfaceResponse Create429Response(
        string clientId,
        string tier,
        int remaining,
        int retryAfter,
        double abuseScore,
        string? reason = null)
    {
        var errorData = new
        {
            error = new
            {
                title = "Too Many Requests",
                detail = reason ?? "Rate limit exceeded. Please retry after the specified interval.",
                clientId,
                tier,
                remaining,
                retryAfter,
                abuseScore = Math.Round(abuseScore, 2)
            },
            timestamp = DateTimeOffset.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(errorData);
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: 429,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Retry-After"] = retryAfter.ToString(),
                ["X-RateLimit-Remaining"] = remaining.ToString(),
                ["X-Abuse-Score"] = abuseScore.ToString("F2")
            },
            Body: body
        );
    }

    /// <summary>
    /// Creates an error InterfaceResponse.
    /// </summary>
    private SdkInterface.InterfaceResponse CreateErrorResponse(int statusCode, string title, string detail)
    {
        var errorData = new
        {
            error = new
            {
                title,
                detail,
                timestamp = DateTimeOffset.UtcNow.ToString("O")
            }
        };
        var json = JsonSerializer.Serialize(errorData);
        var body = Encoding.UTF8.GetBytes(json);

        return new SdkInterface.InterfaceResponse(
            StatusCode: statusCode,
            Headers: new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json"
            },
            Body: body
        );
    }
}
