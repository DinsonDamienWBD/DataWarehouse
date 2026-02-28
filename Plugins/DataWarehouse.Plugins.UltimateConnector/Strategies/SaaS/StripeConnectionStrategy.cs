using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    /// <summary>
    /// Stripe payment platform connection strategy with full API integration.
    /// Supports customer/charge/subscription management and webhook signature verification.
    /// </summary>
    public class StripeConnectionStrategy : SaaSConnectionStrategyBase
    {
        private volatile string _secretKey = "";
        private volatile string _webhookSecret = "";

        public override string StrategyId => "stripe";
        public override string DisplayName => "Stripe";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Stripe payment platform with customer/charge/subscription management and webhook verification.";
        public override string[] Tags => new[] { "stripe", "payments", "fintech", "saas", "rest-api", "webhooks" };

        public StripeConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _secretKey = GetConfiguration<string>(config, "SecretKey", "");
            _webhookSecret = GetConfiguration<string>(config, "WebhookSecret", "");

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.stripe.com"),
                Timeout = config.Timeout
            };

            if (!string.IsNullOrEmpty(_secretKey))
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _secretKey);

            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object>
            {
                ["Endpoint"] = "https://api.stripe.com"
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var response = await handle.GetConnection<HttpClient>().GetAsync("/v1/balance", ct);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            handle.GetConnection<HttpClient>()?.Dispose();
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy,
                isHealthy ? "Stripe is reachable" : "Stripe is not responding",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(
            IConnectionHandle handle, CancellationToken ct = default) =>
            Task.FromResult((_secretKey, DateTimeOffset.UtcNow.AddHours(24)));

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(
            IConnectionHandle handle, string currentToken, CancellationToken ct = default) =>
            AuthenticateAsync(handle, ct);

        /// <summary>
        /// Creates a Stripe customer.
        /// </summary>
        public async Task<StripeResult> CreateCustomerAsync(IConnectionHandle handle, string email,
            string? name = null, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var formData = new Dictionary<string, string> { ["email"] = email };
            if (name != null) formData["name"] = name;
            if (metadata != null)
            {
                foreach (var (key, value) in metadata)
                    formData[$"metadata[{key}]"] = value;
            }

            using var response = await client.PostAsync("/v1/customers", new FormUrlEncodedContent(formData), ct);
            return await ParseStripeResponseAsync(response, ct);
        }

        /// <summary>
        /// Creates a payment intent (charge).
        /// </summary>
        public async Task<StripeResult> CreatePaymentIntentAsync(IConnectionHandle handle, long amount,
            string currency, string? customerId = null, string? paymentMethod = null,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var formData = new Dictionary<string, string>
            {
                ["amount"] = amount.ToString(),
                ["currency"] = currency
            };
            if (customerId != null) formData["customer"] = customerId;
            if (paymentMethod != null) formData["payment_method"] = paymentMethod;
            if (metadata != null)
            {
                foreach (var (key, value) in metadata)
                    formData[$"metadata[{key}]"] = value;
            }

            using var response = await client.PostAsync("/v1/payment_intents", new FormUrlEncodedContent(formData), ct);
            return await ParseStripeResponseAsync(response, ct);
        }

        /// <summary>
        /// Creates a subscription for a customer.
        /// </summary>
        public async Task<StripeResult> CreateSubscriptionAsync(IConnectionHandle handle, string customerId,
            string priceId, string? paymentBehavior = "default_incomplete", CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var formData = new Dictionary<string, string>
            {
                ["customer"] = customerId,
                ["items[0][price]"] = priceId,
                ["payment_behavior"] = paymentBehavior ?? "default_incomplete"
            };

            using var response = await client.PostAsync("/v1/subscriptions", new FormUrlEncodedContent(formData), ct);
            return await ParseStripeResponseAsync(response, ct);
        }

        /// <summary>
        /// Lists charges with optional filters.
        /// </summary>
        public async Task<StripeListResult> ListChargesAsync(IConnectionHandle handle, string? customerId = null,
            int limit = 10, string? startingAfter = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var queryParams = new List<string> { $"limit={limit}" };
            if (customerId != null) queryParams.Add($"customer={customerId}");
            if (startingAfter != null) queryParams.Add($"starting_after={startingAfter}");

            var url = $"/v1/charges?{string.Join("&", queryParams)}";
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new StripeListResult { Success = false, ErrorMessage = json };

            using var doc = JsonDocument.Parse(json);
            var items = new List<Dictionary<string, object?>>();
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var item in data.EnumerateArray())
                {
                    var dict = new Dictionary<string, object?>
                    {
                        ["id"] = item.GetProperty("id").GetString(),
                        ["amount"] = item.GetProperty("amount").GetInt64(),
                        ["currency"] = item.GetProperty("currency").GetString(),
                        ["status"] = item.GetProperty("status").GetString()
                    };
                    items.Add(dict);
                }
            }

            return new StripeListResult
            {
                Success = true,
                Items = items,
                HasMore = doc.RootElement.TryGetProperty("has_more", out var hm) && hm.GetBoolean()
            };
        }

        /// <summary>
        /// Verifies a Stripe webhook signature.
        /// </summary>
        public bool VerifyWebhookSignature(string payload, string signatureHeader, long? toleranceSeconds = null)
        {
            if (string.IsNullOrEmpty(_webhookSecret) || string.IsNullOrEmpty(signatureHeader))
                return false;

            var elements = signatureHeader.Split(',')
                .Select(e => e.Split('=', 2))
                .Where(e => e.Length == 2)
                .ToDictionary(e => e[0].Trim(), e => e[1].Trim());

            if (!elements.TryGetValue("t", out var timestamp) || !elements.TryGetValue("v1", out var signature))
                return false;

            // Verify timestamp tolerance (default 5 minutes)
            if (long.TryParse(timestamp, out var ts))
            {
                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
                if (age > (toleranceSeconds ?? 300)) return false;
            }

            // Compute expected signature
            var signedPayload = $"{timestamp}.{payload}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_webhookSecret));
            var expectedBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
            var expectedSignature = Convert.ToHexString(expectedBytes).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expectedSignature));
        }

        private static async Task<StripeResult> ParseStripeResponseAsync(HttpResponseMessage response, CancellationToken ct)
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new StripeResult { Success = false, ErrorMessage = json };

            using var doc = JsonDocument.Parse(json);
            return new StripeResult
            {
                Success = true,
                ObjectId = doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null,
                ObjectType = doc.RootElement.TryGetProperty("object", out var obj) ? obj.GetString() : null,
                RawJson = json
            };
        }
    }

    public sealed record StripeResult
    {
        public bool Success { get; init; }
        public string? ObjectId { get; init; }
        public string? ObjectType { get; init; }
        public string? RawJson { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record StripeListResult
    {
        public bool Success { get; init; }
        public List<Dictionary<string, object?>> Items { get; init; } = new();
        public bool HasMore { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
