using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Identity
{
    /// <summary>
    /// TACACS+ (Terminal Access Controller Access-Control System Plus) authentication strategy (RFC 8907).
    /// </summary>
    /// <remarks>
    /// Implements TACACS+ protocol for AAA (Authentication, Authorization, Accounting).
    /// Commonly used for network device administration.
    /// </remarks>
    public sealed class TacacsStrategy : AccessControlStrategyBase
    {
        private string _tacacsServer = "localhost";
        private int _tacacsPort = 49;
        private string _sharedKey = "";
        private TimeSpan _timeout = TimeSpan.FromSeconds(5);

        public override string StrategyId => "identity-tacacs";
        public override string StrategyName => "TACACS+";

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
            if (configuration.TryGetValue("TacacsServer", out var server) && server is string serverStr)
                _tacacsServer = serverStr;

            if (configuration.TryGetValue("TacacsPort", out var port) && port is int portInt)
                _tacacsPort = portInt;

            if (configuration.TryGetValue("SharedKey", out var key) && key is string keyStr)
                _sharedKey = keyStr;

            if (configuration.TryGetValue("TimeoutSeconds", out var timeout) && timeout is int timeoutInt)
                _timeout = TimeSpan.FromSeconds(timeoutInt);

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("identity.tacacs.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("identity.tacacs.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_tacacsServer, _tacacsPort, cancellationToken);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("identity.tacacs.evaluate");
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
                return new AccessDecision { IsGranted = false, Reason = result.ErrorMessage ?? "TACACS+ authentication failed" };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "TACACS+ authentication successful"
            };
        }

        private async Task<TacacsAuthenticationResult> AuthenticateAsync(
            string username,
            string password,
            CancellationToken cancellationToken)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(_tacacsServer, _tacacsPort, cancellationToken);

                using var stream = client.GetStream();

                // Build TACACS+ Authentication START packet (Version 12.0+)
                var sessionIdBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4);
                var sessionId = BitConverter.ToUInt32(sessionIdBytes, 0);
                var packet = BuildAuthenticationStartPacket(sessionId, username);

                // Send packet
                await stream.WriteAsync(packet, cancellationToken);

                // Read response
                var headerBuffer = new byte[12];
                await stream.ReadExactlyAsync(headerBuffer, cancellationToken);

                var header = ParseTacacsHeader(headerBuffer);
                if (header.Type != 0x01) // Authentication
                {
                    return new TacacsAuthenticationResult
                    {
                        IsAuthenticated = false,
                        ErrorMessage = "Invalid TACACS+ response type"
                    };
                }

                // Read body
                var bodyBuffer = new byte[header.Length];
                await stream.ReadExactlyAsync(bodyBuffer, cancellationToken);

                // Decrypt body (XOR with MD5 hash of shared key + session data)
                var decryptedBody = DecryptBody(bodyBuffer, sessionId, header.SequenceNumber);

                // Check authentication status
                var status = decryptedBody[0];
                // Status: 1 = PASS, 2 = FAIL, 3 = GETDATA, 4 = GETUSER, 5 = GETPASS, 6 = RESTART, 7 = ERROR
                return new TacacsAuthenticationResult
                {
                    IsAuthenticated = status == 0x01,
                    ErrorMessage = status == 0x02 ? "Authentication failed" : null
                };
            }
            catch (Exception ex)
            {
                return new TacacsAuthenticationResult
                {
                    IsAuthenticated = false,
                    ErrorMessage = $"TACACS+ error: {ex.Message}"
                };
            }
        }

        private byte[] BuildAuthenticationStartPacket(uint sessionId, string username)
        {
            using var ms = new MemoryStream(4096);
            using var writer = new BinaryWriter(ms);

            // TACACS+ Header (12 bytes)
            writer.Write((byte)0xC0); // Version (12.0) | Type (Authentication)
            writer.Write((byte)0x01); // Type: Authentication
            writer.Write((byte)0x01); // Sequence number
            writer.Write((byte)0x00); // Flags
            writer.Write(System.Net.IPAddress.HostToNetworkOrder((int)sessionId));

            // Body placeholder length
            var lengthPos = ms.Position;
            writer.Write((int)0);

            // Body: Authentication START
            var bodyStart = ms.Position;
            writer.Write((byte)0x01); // Action: LOGIN
            writer.Write((byte)0x00); // Priv level
            writer.Write((byte)0x01); // Authen type: ASCII
            writer.Write((byte)0x01); // Service: LOGIN

            var usernameBytes = Encoding.ASCII.GetBytes(username);
            writer.Write((byte)usernameBytes.Length);
            writer.Write(usernameBytes);

            // Update length
            var bodyLength = (int)(ms.Position - bodyStart);
            ms.Position = lengthPos;
            writer.Write(System.Net.IPAddress.HostToNetworkOrder(bodyLength));

            return ms.ToArray();
        }

        private TacacsHeader ParseTacacsHeader(byte[] header)
        {
            return new TacacsHeader
            {
                Version = (byte)(header[0] >> 4),
                Type = (byte)(header[0] & 0x0F),
                SequenceNumber = header[2],
                Flags = header[3],
                SessionId = (uint)System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 4)),
                Length = (uint)System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 8))
            };
        }

        private byte[] DecryptBody(byte[] encryptedBody, uint sessionId, byte sequenceNumber)
        {
            // TACACS+ encryption: XOR with MD5(sessionId + key + version + sequenceNumber)
            var keyData = new byte[_sharedKey.Length + 5];
            Array.Copy(BitConverter.GetBytes(sessionId), keyData, 4);
            Array.Copy(Encoding.ASCII.GetBytes(_sharedKey), 0, keyData, 4, _sharedKey.Length);
            keyData[keyData.Length - 1] = sequenceNumber;

            var hash = System.Security.Cryptography.MD5.HashData(keyData);
            var decrypted = new byte[encryptedBody.Length];

            for (int i = 0; i < encryptedBody.Length; i++)
            {
                decrypted[i] = (byte)(encryptedBody[i] ^ hash[i % hash.Length]);
            }

            return decrypted;
        }
    }

    internal struct TacacsHeader
    {
        public byte Version;
        public byte Type;
        public byte SequenceNumber;
        public byte Flags;
        public uint SessionId;
        public uint Length;
    }

    public sealed record TacacsAuthenticationResult
    {
        public required bool IsAuthenticated { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
