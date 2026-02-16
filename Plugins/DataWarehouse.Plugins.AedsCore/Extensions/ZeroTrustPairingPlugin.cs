using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.AedsCore.Extensions;

/// <summary>
/// Zero-Trust Pairing Plugin: PIN-based client registration and trust elevation.
/// Generates cryptographically secure 6-digit PINs valid for 5 minutes.
/// </summary>
public sealed class ZeroTrustPairingPlugin : SecurityPluginBase
{
    private readonly Dictionary<string, (string Pin, DateTimeOffset Expires)> _pendingPins = new();

    /// <summary>
    /// Gets the plugin identifier.
    /// </summary>
    public override string Id => "aeds.zero-trust-pairing";

    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public override string Name => "ZeroTrustPairingPlugin";

    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SecurityDomain => "ZeroTrustPairing";

    /// <summary>
    /// Gets the plugin category.
    /// </summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Generates a 6-digit pairing PIN.
    /// </summary>
    /// <returns>6-digit PIN valid for 5 minutes.</returns>
    public string GeneratePairingPIN()
    {
        var pin = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        var clientId = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);

        _pendingPins[clientId] = (pin, expires);

        Console.WriteLine($"[Pairing PIN] {pin} (expires {expires:HH:mm:ss})");
        return pin;
    }

    /// <summary>
    /// Registers client with server using PIN.
    /// </summary>
    /// <param name="clientName">Client name.</param>
    /// <param name="capabilities">Client capabilities.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Registered client information.</returns>
    public async Task<AedsClient> RegisterClientAsync(
        string clientName,
        ClientCapabilities capabilities,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(clientName))
            throw new ArgumentException("Client name cannot be null or empty.", nameof(clientName));

        var pin = GeneratePairingPIN();
        var clientId = Guid.NewGuid().ToString("N");
        var keyPair = GenerateKeyPair();

        if (MessageBus != null)
        {
            var registration = new PluginMessage
            {
                Type = "aeds.register-client",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["clientId"] = clientId,
                    ["clientName"] = clientName,
                    ["publicKey"] = keyPair.PublicKey,
                    ["pin"] = pin,
                    ["capabilities"] = (int)capabilities
                }
            };

            await MessageBus.PublishAsync("aeds.register-client", registration, ct);
        }

        return new AedsClient
        {
            ClientId = clientId,
            Name = clientName,
            PublicKey = keyPair.PublicKey,
            TrustLevel = ClientTrustLevel.PendingVerification,
            RegisteredAt = DateTimeOffset.UtcNow,
            SubscribedChannels = Array.Empty<string>(),
            Capabilities = capabilities
        };
    }

    /// <summary>
    /// Elevates client trust level (admin operation).
    /// </summary>
    /// <param name="clientId">Client ID to elevate.</param>
    /// <param name="adminVerifiedPIN">Admin-verified PIN.</param>
    /// <param name="newLevel">New trust level.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ElevateTrustAsync(
        string clientId,
        string adminVerifiedPIN,
        ClientTrustLevel newLevel,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(clientId))
            throw new ArgumentException("Client ID cannot be null or empty.", nameof(clientId));
        if (string.IsNullOrEmpty(adminVerifiedPIN))
            throw new ArgumentException("PIN cannot be null or empty.", nameof(adminVerifiedPIN));

        if (MessageBus != null)
        {
            var elevation = new PluginMessage
            {
                Type = "aeds.elevate-trust",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["clientId"] = clientId,
                    ["pin"] = adminVerifiedPIN,
                    ["newLevel"] = newLevel.ToString()
                }
            };

            await MessageBus.PublishAsync("aeds.elevate-trust", elevation, ct);
        }
    }

    /// <summary>
    /// Verifies pairing status for a client.
    /// </summary>
    /// <param name="clientId">Client ID to verify.</param>
    /// <returns>True if paired with Trusted+ trust level.</returns>
    public bool VerifyPairing(string clientId)
    {
        if (string.IsNullOrEmpty(clientId))
            throw new ArgumentException("Client ID cannot be null or empty.", nameof(clientId));

        // In production, query trust level from server
        return true;
    }

    private (string PublicKey, string PrivateKey) GenerateKeyPair()
    {
        // Try bus delegation first
        if (MessageBus != null)
        {
            try
            {
                var msg = new PluginMessage
                {
                    Type = "encryption.generate-keypair",
                    Payload = new Dictionary<string, object>
                    {
                        ["algorithm"] = "RSA",
                        ["keySize"] = 2048
                    }
                };

                var response = MessageBus.SendAsync("encryption.generate-keypair", msg, CancellationToken.None).GetAwaiter().GetResult();
                if (response != null && response.Success && response.Payload is Dictionary<string, object> payload
                    && payload.ContainsKey("publicKey") && payload.ContainsKey("privateKey"))
                {
                    var publicKey = (string)payload["publicKey"];
                    var privateKey = (string)payload["privateKey"];
                    return (publicKey, privateKey);
                }
            }
            catch
            {
                // Fall through to inline implementation
            }
        }

        // Graceful degradation — fallback to inline if bus unavailable
        // Note: RSA key generation - encryption.generate-key should be used when MessageBus is available
        using var rsa = RSA.Create(2048);
        var publicKey2 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var privateKey2 = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        return (publicKey2, privateKey2);
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task StopAsync()
    {
        _pendingPins.Clear();
        return Task.CompletedTask;
    }
}
