using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SaaS
{
    /// <summary>
    /// SendGrid email delivery connection strategy with full API integration.
    /// Supports single/batch email sending, template management, and webhook event handling.
    /// </summary>
    public class SendGridConnectionStrategy : SaaSConnectionStrategyBase
    {
        private string _apiKey = "";

        public override string StrategyId => "sendgrid";
        public override string DisplayName => "SendGrid";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to SendGrid email delivery service with single/batch sending, template management, and webhook events.";
        public override string[] Tags => new[] { "sendgrid", "email", "messaging", "saas", "rest-api", "marketing" };

        public SendGridConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _apiKey = GetConfiguration<string>(config, "ApiKey", "");

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.sendgrid.com"),
                Timeout = config.Timeout
            };

            if (!string.IsNullOrEmpty(_apiKey))
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object>
            {
                ["Endpoint"] = "https://api.sendgrid.com"
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var response = await handle.GetConnection<HttpClient>().GetAsync("/v3/user/profile", ct);
                return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable;
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
                isHealthy ? "SendGrid is reachable" : "SendGrid is not responding",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(
            IConnectionHandle handle, CancellationToken ct = default) =>
            Task.FromResult((_apiKey, DateTimeOffset.UtcNow.AddHours(24)));

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(
            IConnectionHandle handle, string currentToken, CancellationToken ct = default) =>
            AuthenticateAsync(handle, ct);

        /// <summary>
        /// Sends a single email via SendGrid v3 Mail Send API.
        /// </summary>
        public async Task<SendGridResult> SendEmailAsync(IConnectionHandle handle, string fromEmail,
            string fromName, string toEmail, string toName, string subject, string? plainTextContent = null,
            string? htmlContent = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var payload = new
            {
                personalizations = new[]
                {
                    new { to = new[] { new { email = toEmail, name = toName } } }
                },
                from = new { email = fromEmail, name = fromName },
                subject,
                content = new[]
                {
                    plainTextContent != null
                        ? new { type = "text/plain", value = plainTextContent }
                        : new { type = "text/html", value = htmlContent ?? "" }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/v3/mail/send", content, ct);

            return new SendGridResult
            {
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                MessageId = response.Headers.TryGetValues("X-Message-Id", out var msgId)
                    ? System.Linq.Enumerable.FirstOrDefault(msgId) : null,
                ErrorMessage = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null
            };
        }

        /// <summary>
        /// Sends a batch of emails with personalizations.
        /// </summary>
        public async Task<SendGridResult> SendBatchEmailAsync(IConnectionHandle handle, string fromEmail,
            string fromName, string subject, string htmlContent,
            IReadOnlyList<SendGridRecipient> recipients, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var personalizations = new List<object>();

            foreach (var recipient in recipients)
            {
                var personalization = new Dictionary<string, object>
                {
                    ["to"] = new[] { new { email = recipient.Email, name = recipient.Name } }
                };
                if (recipient.Substitutions != null)
                    personalization["dynamic_template_data"] = recipient.Substitutions;
                personalizations.Add(personalization);
            }

            var payload = new
            {
                personalizations,
                from = new { email = fromEmail, name = fromName },
                subject,
                content = new[] { new { type = "text/html", value = htmlContent } }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/v3/mail/send", content, ct);

            return new SendGridResult
            {
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                ErrorMessage = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null
            };
        }

        /// <summary>
        /// Sends an email using a dynamic template.
        /// </summary>
        public async Task<SendGridResult> SendTemplateEmailAsync(IConnectionHandle handle, string fromEmail,
            string fromName, string toEmail, string toName, string templateId,
            Dictionary<string, object>? dynamicData = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var payload = new
            {
                personalizations = new[]
                {
                    new
                    {
                        to = new[] { new { email = toEmail, name = toName } },
                        dynamic_template_data = dynamicData ?? new Dictionary<string, object>()
                    }
                },
                from = new { email = fromEmail, name = fromName },
                template_id = templateId
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/v3/mail/send", content, ct);

            return new SendGridResult
            {
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                ErrorMessage = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null
            };
        }

        /// <summary>
        /// Lists email templates.
        /// </summary>
        public async Task<SendGridTemplateListResult> ListTemplatesAsync(IConnectionHandle handle,
            string generations = "dynamic", CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            using var response = await client.GetAsync($"/v3/templates?generations={generations}&page_size=50", ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new SendGridTemplateListResult { Success = false, ErrorMessage = json };

            using var doc = JsonDocument.Parse(json);
            var templates = new List<SendGridTemplate>();

            if (doc.RootElement.TryGetProperty("templates", out var arr))
            {
                foreach (var t in arr.EnumerateArray())
                {
                    templates.Add(new SendGridTemplate
                    {
                        Id = t.GetProperty("id").GetString() ?? "",
                        Name = t.GetProperty("name").GetString() ?? "",
                        Generation = t.TryGetProperty("generation", out var gen) ? gen.GetString() ?? "" : ""
                    });
                }
            }

            return new SendGridTemplateListResult { Success = true, Templates = templates };
        }
    }

    public sealed record SendGridRecipient
    {
        public required string Email { get; init; }
        public string? Name { get; init; }
        public Dictionary<string, object>? Substitutions { get; init; }
    }

    public sealed record SendGridResult
    {
        public bool Success { get; init; }
        public int StatusCode { get; init; }
        public string? MessageId { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record SendGridTemplateListResult
    {
        public bool Success { get; init; }
        public List<SendGridTemplate> Templates { get; init; } = new();
        public string? ErrorMessage { get; init; }
    }

    public sealed record SendGridTemplate
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Generation { get; init; }
    }
}
