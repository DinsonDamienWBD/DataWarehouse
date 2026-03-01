using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Legacy
{
    /// <summary>
    /// SMTP connection strategy for email sending.
    /// Supports sending emails with attachments, HTML body, and configurable SMTP settings
    /// via System.Net.Mail.SmtpClient.
    /// </summary>
    public class SmtpConnectionStrategy : LegacyConnectionStrategyBase
    {
        private volatile string _host = "";
        private volatile int _port = 587;
        private volatile string _username = "";
        private volatile string _password = "";
        private volatile bool _useSsl = true;

        public override string StrategyId => "smtp";
        public override string DisplayName => "SMTP";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to SMTP servers for email sending with attachments, HTML body, TLS encryption, and configurable settings.";
        public override string[] Tags => new[] { "smtp", "email", "legacy", "tls", "messaging" };

        public SmtpConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            // P2-2132: Use ParseHostPortSafe to correctly handle IPv6 addresses like [::1]:587
            var (smtpDefaultHost, _) = ParseHostPortSafe(config.ConnectionString ?? throw new ArgumentException("Connection string required"), 587);
            _host = GetConfiguration<string>(config, "Host", smtpDefaultHost);
            if (string.IsNullOrWhiteSpace(_host)) throw new ArgumentException("SMTP host is required.");
            _port = GetConfiguration(config, "Port", 587);
            // Finding 1992: Validate port is in valid TCP range.
            if (_port < 1 || _port > 65535) throw new ArgumentOutOfRangeException(nameof(config), $"SMTP port {_port} is out of valid range (1-65535).");
            _username = GetConfiguration<string>(config, "Username", "");
            _password = GetConfiguration<string>(config, "Password", "");
            _useSsl = GetConfiguration(config, "UseSsl", true);

            // Test connectivity
            using var testClient = new TcpClient();
            await testClient.ConnectAsync(_host, _port, ct);

            var smtpConfig = new SmtpConnectionInfo
            {
                Host = _host,
                Port = _port,
                Username = _username,
                Password = _password,
                UseSsl = _useSsl
            };

            return new DefaultConnectionHandle(smtpConfig, new Dictionary<string, object>
            {
                ["protocol"] = "SMTP",
                ["host"] = _host,
                ["port"] = _port,
                ["ssl"] = _useSsl
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                using var testClient = new TcpClient();
                await testClient.ConnectAsync(_host, _port, ct);
                return true;
            }
            catch { return false; }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.CompletedTask;

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy,
                isHealthy ? $"SMTP server reachable at {_host}:{_port}" : "SMTP server unreachable",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            return Task.FromResult($"{{\"command\":\"{protocolCommand}\",\"protocol\":\"SMTP\",\"status\":\"queued\"}}");
        }

        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default)
        {
            var parts = modernCommand.Split(' ', 2);
            var translated = parts[0].ToUpperInvariant() switch
            {
                "SEND" => $"MAIL FROM: / RCPT TO: / DATA",
                "STATUS" => "NOOP",
                "QUIT" => "QUIT",
                _ => modernCommand
            };
            return Task.FromResult($"{{\"original\":\"{modernCommand}\",\"translated\":\"{translated}\",\"protocol\":\"SMTP\"}}");
        }

        /// <summary>
        /// Sends an email via SMTP.
        /// </summary>
        public async Task<SmtpSendResult> SendEmailAsync(IConnectionHandle handle, string from, string to,
            string subject, string body, bool isHtml = false, SmtpAttachment[]? attachments = null,
            string[]? cc = null, string[]? bcc = null, CancellationToken ct = default)
        {
            // Finding 1993: Validate sender and recipient email addresses before attempting send.
            if (string.IsNullOrWhiteSpace(from)) throw new ArgumentException("Sender address 'from' is required.");
            if (string.IsNullOrWhiteSpace(to)) throw new ArgumentException("Recipient address 'to' is required.");
            if (!from.Contains('@')) throw new ArgumentException($"Invalid sender address: '{from}'. Must contain '@'.");
            if (!to.Contains('@')) throw new ArgumentException($"Invalid recipient address: '{to}'. Must contain '@'.");
            if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("Email subject is required.");
            try
            {
#pragma warning disable SYSLIB0014 // SmtpClient is obsolete but functional for SMTP
                using var client = new SmtpClient(_host, _port)
                {
                    EnableSsl = _useSsl,
                    Timeout = 30000,
                    DeliveryMethod = SmtpDeliveryMethod.Network
                };

                if (!string.IsNullOrEmpty(_username))
                    client.Credentials = new NetworkCredential(_username, _password);

                using var message = new MailMessage
                {
                    From = new MailAddress(from),
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = isHtml
                };
                message.To.Add(to);

                if (cc != null)
                {
                    foreach (var addr in cc) message.CC.Add(addr);
                }
                if (bcc != null)
                {
                    foreach (var addr in bcc) message.Bcc.Add(addr);
                }

                if (attachments != null)
                {
                    foreach (var att in attachments)
                    {
                        var stream = new System.IO.MemoryStream(att.Content);
                        message.Attachments.Add(new Attachment(stream, att.FileName, att.ContentType));
                    }
                }

                await client.SendMailAsync(message, ct);
#pragma warning restore SYSLIB0014

                return new SmtpSendResult { Success = true, From = from, To = to, Subject = subject };
            }
            catch (Exception ex)
            {
                return new SmtpSendResult { Success = false, ErrorMessage = ex.Message, From = from, To = to, Subject = subject };
            }
        }

        /// <summary>
        /// Sends a bulk batch of emails.
        /// </summary>
        public async Task<SmtpBatchResult> SendBatchAsync(IConnectionHandle handle, string from,
            IReadOnlyList<SmtpEmailMessage> messages, CancellationToken ct = default)
        {
            var results = new List<SmtpSendResult>();
            var successCount = 0;

            foreach (var msg in messages)
            {
                if (ct.IsCancellationRequested) break;

                var result = await SendEmailAsync(handle, from, msg.To, msg.Subject, msg.Body,
                    msg.IsHtml, msg.Attachments, ct: ct);
                results.Add(result);
                if (result.Success) successCount++;

                // Small delay between sends to avoid rate limiting
                await Task.Delay(100, ct);
            }

            return new SmtpBatchResult
            {
                TotalSent = results.Count,
                SuccessCount = successCount,
                FailureCount = results.Count - successCount,
                Results = results
            };
        }
    }

    public sealed class SmtpConnectionInfo
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool UseSsl { get; set; }
    }

    public sealed record SmtpAttachment
    {
        public required string FileName { get; init; }
        public required byte[] Content { get; init; }
        public string ContentType { get; init; } = "application/octet-stream";
    }

    public sealed record SmtpEmailMessage
    {
        public required string To { get; init; }
        public required string Subject { get; init; }
        public required string Body { get; init; }
        public bool IsHtml { get; init; }
        public SmtpAttachment[]? Attachments { get; init; }
    }

    public sealed record SmtpSendResult
    {
        public bool Success { get; init; }
        public required string From { get; init; }
        public required string To { get; init; }
        public required string Subject { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record SmtpBatchResult
    {
        public int TotalSent { get; init; }
        public int SuccessCount { get; init; }
        public int FailureCount { get; init; }
        public List<SmtpSendResult> Results { get; init; } = new();
    }
}
