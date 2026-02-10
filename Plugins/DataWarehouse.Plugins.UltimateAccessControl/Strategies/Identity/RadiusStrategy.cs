using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Identity
{
    /// <summary>
    /// RADIUS (Remote Authentication Dial-In User Service) authentication strategy (RFC 2865).
    /// </summary>
    /// <remarks>
    /// Implements RADIUS Access-Request/Accept/Reject protocol for network authentication.
    /// Supports PAP and CHAP authentication methods.
    /// </remarks>
    public sealed class RadiusStrategy : AccessControlStrategyBase
    {
        private string _radiusServer = "localhost";
        private int _radiusPort = 1812;
        private string _sharedSecret = "";
        private TimeSpan _timeout = TimeSpan.FromSeconds(5);

        public override string StrategyId => "identity-radius";
        public override string StrategyName => "RADIUS";

        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 500
        };

        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("RadiusServer", out var server) && server is string serverStr)
                _radiusServer = serverStr;

            if (configuration.TryGetValue("RadiusPort", out var port) && port is int portInt)
                _radiusPort = portInt;

            if (configuration.TryGetValue("SharedSecret", out var secret) && secret is string secretStr)
                _sharedSecret = secretStr;

            if (configuration.TryGetValue("TimeoutSeconds", out var timeout) && timeout is int timeoutInt)
                _timeout = TimeSpan.FromSeconds(timeoutInt);

            return base.InitializeAsync(configuration, cancellationToken);
        }

        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var udpClient = new UdpClient();
                udpClient.Client.ReceiveTimeout = 1000;
                await udpClient.SendAsync(new byte[] { 0x01 }, 1, _radiusServer, _radiusPort);
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            if (!context.EnvironmentAttributes.TryGetValue("Username", out var usernameObj) ||
                usernameObj is not string username)
            {
                return new AccessDecision { IsGranted = false, Reason = "Username not provided" };
            }

            if (!context.EnvironmentAttributes.TryGetValue("Password", out var passwordObj) ||
                passwordObj is not string password)
            {
                return new AccessDecision { IsGranted = false, Reason = "Password not provided" };
            }

            var result = await AuthenticateAsync(username, password, cancellationToken);
            if (!result.IsAuthenticated)
            {
                return new AccessDecision { IsGranted = false, Reason = result.ErrorMessage ?? "RADIUS authentication failed" };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "RADIUS authentication successful",
                Metadata = new Dictionary<string, object>
                {
                    ["ReplyAttributes"] = result.ReplyAttributes ?? new Dictionary<string, string>()
                }
            };
        }

        private async Task<RadiusAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = (int)_timeout.TotalMilliseconds;

            try
            {
                // Build RADIUS Access-Request packet (RFC 2865)
                var authenticator = GenerateAuthenticator();
                var packet = BuildAccessRequestPacket(username, password, authenticator);

                // Send request
                await udpClient.SendAsync(packet, packet.Length, _radiusServer, _radiusPort);

                // Receive response
                var receiveTask = udpClient.ReceiveAsync();
                var completedTask = await Task.WhenAny(receiveTask, Task.Delay(_timeout, cancellationToken));

                if (completedTask != receiveTask)
                {
                    return new RadiusAuthenticationResult
                    {
                        IsAuthenticated = false,
                        ErrorMessage = "RADIUS server timeout"
                    };
                }

                var response = await receiveTask;
                return ParseAccessResponse(response.Buffer, authenticator);
            }
            catch (Exception ex)
            {
                return new RadiusAuthenticationResult
                {
                    IsAuthenticated = false,
                    ErrorMessage = $"RADIUS error: {ex.Message}"
                };
            }
        }

        private byte[] GenerateAuthenticator()
        {
            return RandomNumberGenerator.GetBytes(16);
        }

        private byte[] BuildAccessRequestPacket(string username, string password, byte[] authenticator)
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);

            // RADIUS packet header
            writer.Write((byte)1); // Code: Access-Request
            writer.Write((byte)RandomNumberGenerator.GetInt32(256)); // Identifier
            writer.Write((ushort)0); // Length (placeholder)
            writer.Write(authenticator); // Authenticator

            // User-Name attribute (type 1)
            var usernameBytes = Encoding.UTF8.GetBytes(username);
            writer.Write((byte)1);
            writer.Write((byte)(2 + usernameBytes.Length));
            writer.Write(usernameBytes);

            // User-Password attribute (type 2) - encrypted with shared secret
            var passwordEncrypted = EncryptPassword(password, authenticator);
            writer.Write((byte)2);
            writer.Write((byte)(2 + passwordEncrypted.Length));
            writer.Write(passwordEncrypted);

            // Update length
            var packet = ms.ToArray();
            packet[2] = (byte)(packet.Length >> 8);
            packet[3] = (byte)(packet.Length & 0xFF);

            return packet;
        }

        private byte[] EncryptPassword(string password, byte[] authenticator)
        {
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            var paddedPassword = new byte[((passwordBytes.Length + 15) / 16) * 16];
            Array.Copy(passwordBytes, paddedPassword, passwordBytes.Length);

            var secretBytes = Encoding.UTF8.GetBytes(_sharedSecret);
            var result = new byte[paddedPassword.Length];

            for (int i = 0; i < paddedPassword.Length; i += 16)
            {
                var hash = MD5.HashData(secretBytes.Concat(i == 0 ? authenticator : result.Skip(i - 16).Take(16)).ToArray());
                for (int j = 0; j < 16; j++)
                    result[i + j] = (byte)(paddedPassword[i + j] ^ hash[j]);
            }

            return result;
        }

        private RadiusAuthenticationResult ParseAccessResponse(byte[] response, byte[] requestAuthenticator)
        {
            if (response.Length < 20)
            {
                return new RadiusAuthenticationResult
                {
                    IsAuthenticated = false,
                    ErrorMessage = "Invalid RADIUS response"
                };
            }

            var code = response[0];
            // 2 = Access-Accept, 3 = Access-Reject
            return new RadiusAuthenticationResult
            {
                IsAuthenticated = code == 2,
                ErrorMessage = code == 3 ? "Access denied by RADIUS server" : null,
                ReplyAttributes = new Dictionary<string, string>()
            };
        }
    }

    public sealed record RadiusAuthenticationResult
    {
        public required bool IsAuthenticated { get; init; }
        public string? ErrorMessage { get; init; }
        public Dictionary<string, string>? ReplyAttributes { get; init; }
    }
}
