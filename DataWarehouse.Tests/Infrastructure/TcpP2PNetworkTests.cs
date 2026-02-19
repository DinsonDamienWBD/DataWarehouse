using System;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts.Distributed;
using DataWarehouse.SDK.Infrastructure.Distributed;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for TcpP2PNetwork - mutual TLS P2P communication.
    /// Tests certificate validation, connection lifecycle, TLS enforcement.
    /// </summary>
    public class TcpP2PNetworkTests
    {
        #region Configuration Tests

        [Fact]
        public void Constructor_WithMtlsEnabled_RequiresServerCertificate()
        {
            var config = new TcpP2PNetworkConfig
            {
                LocalPeerId = "peer1",
                ListenPort = 0,
                EnableMutualTls = true,
                ClientCertificate = CreateSelfSignedCertificate("client")
                // Missing ServerCertificate
            };

            Assert.Throws<InvalidOperationException>(() => new TcpP2PNetwork(config));
        }

        [Fact]
        public void Constructor_WithMtlsEnabled_RequiresClientCertificate()
        {
            var config = new TcpP2PNetworkConfig
            {
                LocalPeerId = "peer1",
                ListenPort = 0,
                EnableMutualTls = true,
                ServerCertificate = CreateSelfSignedCertificate("server")
                // Missing ClientCertificate
            };

            Assert.Throws<InvalidOperationException>(() => new TcpP2PNetwork(config));
        }

        [Fact]
        public void Constructor_WithMtlsDisabled_DoesNotRequireCertificates()
        {
            var config = new TcpP2PNetworkConfig
            {
                LocalPeerId = "peer1",
                ListenPort = 0,
                EnableMutualTls = false
            };

            using var network = new TcpP2PNetwork(config);
            Assert.NotNull(network);
        }

        [Fact]
        public void Constructor_WithValidMtlsConfig_Succeeds()
        {
            var serverCert = CreateSelfSignedCertificate("server");
            var clientCert = CreateSelfSignedCertificate("client");

            var config = new TcpP2PNetworkConfig
            {
                LocalPeerId = "peer1",
                ListenPort = 0,
                EnableMutualTls = true,
                ServerCertificate = serverCert,
                ClientCertificate = clientCert,
                AllowSelfSignedCertificates = true
            };

            using var network = new TcpP2PNetwork(config);
            Assert.NotNull(network);
        }

        #endregion

        #region Peer Discovery Tests

        [Fact]
        public async Task DiscoverPeersAsync_InitiallyEmpty()
        {
            var config = CreateBasicConfig("peer1", 0);
            using var network = new TcpP2PNetwork(config);

            var peers = await network.DiscoverPeersAsync();

            Assert.NotNull(peers);
            Assert.Empty(peers);
        }

        [Fact]
        public void AddPeer_RegistersPeerSuccessfully()
        {
            var config = CreateBasicConfig("peer1", 0);
            using var network = new TcpP2PNetwork(config);

            var peer = new PeerInfo
            {
                PeerId = "peer2",
                Address = "127.0.0.1",
                Port = 5000,
                LastSeen = DateTimeOffset.UtcNow,
                Metadata = new System.Collections.Generic.Dictionary<string, string>()
            };

            network.AddPeer(peer);

            var peers = network.DiscoverPeersAsync().Result;
            Assert.Single(peers);
            Assert.Equal("peer2", peers[0].PeerId);
        }

        [Fact]
        public void AddPeer_RaisesOnPeerEvent()
        {
            var config = CreateBasicConfig("peer1", 0);
            using var network = new TcpP2PNetwork(config);

            PeerEvent? capturedEvent = null;
            network.OnPeerEvent += (evt) => capturedEvent = evt;

            var peer = new PeerInfo
            {
                PeerId = "peer2",
                Address = "127.0.0.1",
                Port = 5000,
                LastSeen = DateTimeOffset.UtcNow,
                Metadata = new System.Collections.Generic.Dictionary<string, string>()
            };

            network.AddPeer(peer);

            Assert.NotNull(capturedEvent);
            Assert.Equal(PeerEventType.PeerDiscovered, capturedEvent.EventType);
            Assert.Equal("peer2", capturedEvent.Peer.PeerId);
        }

        [Fact]
        public void RemovePeer_UnregistersPeerSuccessfully()
        {
            var config = CreateBasicConfig("peer1", 0);
            using var network = new TcpP2PNetwork(config);

            var peer = new PeerInfo
            {
                PeerId = "peer2",
                Address = "127.0.0.1",
                Port = 5000,
                LastSeen = DateTimeOffset.UtcNow,
                Metadata = new System.Collections.Generic.Dictionary<string, string>()
            };

            network.AddPeer(peer);
            network.RemovePeer("peer2");

            var peers = network.DiscoverPeersAsync().Result;
            Assert.Empty(peers);
        }

        [Fact]
        public void RemovePeer_RaisesOnPeerEvent()
        {
            var config = CreateBasicConfig("peer1", 0);
            using var network = new TcpP2PNetwork(config);

            var peer = new PeerInfo
            {
                PeerId = "peer2",
                Address = "127.0.0.1",
                Port = 5000,
                LastSeen = DateTimeOffset.UtcNow,
                Metadata = new System.Collections.Generic.Dictionary<string, string>()
            };

            network.AddPeer(peer);

            PeerEvent? capturedEvent = null;
            network.OnPeerEvent += (evt) => capturedEvent = evt;

            network.RemovePeer("peer2");

            Assert.NotNull(capturedEvent);
            Assert.Equal(PeerEventType.PeerLost, capturedEvent.EventType);
            Assert.Equal("peer2", capturedEvent.Peer.PeerId);
        }

        #endregion

        #region Communication Tests

        [Fact]
        public async Task SendToPeerAsync_WithUnknownPeer_ThrowsInvalidOperationException()
        {
            var config = CreateBasicConfig("peer1", 0);
            using var network = new TcpP2PNetwork(config);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await network.SendToPeerAsync("unknown", new byte[] { 1, 2, 3 });
            });
        }

        [Fact]
        public async Task BroadcastAsync_WithNoPeers_CompletesSuccessfully()
        {
            var config = CreateBasicConfig("peer1", 0);
            using var network = new TcpP2PNetwork(config);

            // Should not throw even with no peers
            var exception = await Record.ExceptionAsync(() => network.BroadcastAsync(new byte[] { 1, 2, 3 }));

            Assert.Null(exception);
        }

        [Fact]
        public async Task RequestFromPeerAsync_WithUnknownPeer_ThrowsInvalidOperationException()
        {
            var config = CreateBasicConfig("peer1", 0);
            using var network = new TcpP2PNetwork(config);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await network.RequestFromPeerAsync("unknown", new byte[] { 1, 2, 3 });
            });
        }

        #endregion

        #region TLS Configuration Tests

        [Fact]
        public void TcpP2PNetworkConfig_DefaultValues_SetCorrectly()
        {
            var config = new TcpP2PNetworkConfig
            {
                LocalPeerId = "peer1",
                ListenPort = 5000
            };

            Assert.Equal("peer1", config.LocalPeerId);
            Assert.Equal(5000, config.ListenPort);
            Assert.True(config.EnableMutualTls); // Default is true
            Assert.False(config.AllowSelfSignedCertificates); // Default is false
            Assert.Empty(config.PinnedCertificateThumbprints);
            Assert.Null(config.TrustedCaBundlePath);
        }

        [Fact]
        public void TcpP2PNetworkConfig_WithCertificatePinning_StoresThumbprints()
        {
            var cert = CreateSelfSignedCertificate("test");
            var thumbprint = cert.Thumbprint;

            var config = new TcpP2PNetworkConfig
            {
                LocalPeerId = "peer1",
                ListenPort = 5000,
                PinnedCertificateThumbprints = new System.Collections.Generic.HashSet<string> { thumbprint }
            };

            Assert.Contains(thumbprint, config.PinnedCertificateThumbprints);
        }

        [Fact]
        public void TcpP2PNetworkConfig_ClientOnlyMode_UsesZeroPort()
        {
            var config = new TcpP2PNetworkConfig
            {
                LocalPeerId = "peer1",
                ListenPort = 0, // Client-only mode
                EnableMutualTls = false
            };

            using var network = new TcpP2PNetwork(config);
            Assert.NotNull(network);
        }

        #endregion

        #region Disposal Tests

        [Fact]
        public void Dispose_CleansUpResources()
        {
            var config = CreateBasicConfig("peer1", 0);
            var network = new TcpP2PNetwork(config);

            var peer = new PeerInfo
            {
                PeerId = "peer2",
                Address = "127.0.0.1",
                Port = 5000,
                LastSeen = DateTimeOffset.UtcNow,
                Metadata = new System.Collections.Generic.Dictionary<string, string>()
            };

            network.AddPeer(peer);
            network.Dispose();

            // After disposal, operations should not throw but also not work
            var peers = network.DiscoverPeersAsync().Result;
            Assert.NotNull(peers);
        }

        [Fact]
        public void Dispose_MultipleCalls_DoesNotThrow()
        {
            var config = CreateBasicConfig("peer1", 0);
            var network = new TcpP2PNetwork(config);

            network.Dispose();

            var exception = Record.Exception(() => network.Dispose());

            Assert.Null(exception);
        }

        #endregion

        #region Helper Methods

        private static TcpP2PNetworkConfig CreateBasicConfig(string peerId, int port)
        {
            return new TcpP2PNetworkConfig
            {
                LocalPeerId = peerId,
                ListenPort = port,
                EnableMutualTls = false
            };
        }

        private static X509Certificate2 CreateSelfSignedCertificate(string subjectName)
        {
            using var rsa = RSA.Create(2048);

            var request = new CertificateRequest(
                $"CN={subjectName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: true));

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new System.Security.Cryptography.OidCollection
                    {
                        new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"), // Server auth
                        new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2")  // Client auth
                    },
                    critical: true));

            var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));

            // Export and re-import to get a certificate with private key
            var pfxBytes = certificate.Export(X509ContentType.Pfx);
            return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, keyStorageFlags: X509KeyStorageFlags.Exportable);
        }

        #endregion
    }
}
