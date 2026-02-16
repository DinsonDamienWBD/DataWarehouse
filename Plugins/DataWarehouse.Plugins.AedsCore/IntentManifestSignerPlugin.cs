using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.AedsCore;

/// <summary>
/// Intent Manifest Signer Plugin: Provides cryptographic signing capability for AEDS manifests.
/// Supports Ed25519, RSA-PSS-SHA256, and ECDSA-P256-SHA256 algorithms.
/// </summary>
/// <remarks>
/// <para>
/// This plugin prepares the canonical form of an Intent Manifest and delegates all cryptographic
/// signing operations to UltimateEncryption via message bus (Rule 15: Capability Delegation).
/// Keys are managed by UltimateKeyManagement — this plugin never handles raw key material.
/// </para>
/// <para>
/// <strong>Supported Algorithms:</strong>
/// <list type="bullet">
/// <item><description>Ed25519: Delegated to UltimateEncryption via <c>encryption.sign</c></description></item>
/// <item><description>RSA-PSS-SHA256: Delegated to UltimateEncryption via <c>encryption.sign</c></description></item>
/// <item><description>ECDSA-P256-SHA256: Delegated to UltimateEncryption via <c>encryption.sign</c></description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Canonical Form:</strong> The signature is computed over UTF-8 bytes of:
/// ManifestId + CreatedAt.ToUnixTimeMilliseconds() + Payload.ContentHash
/// </para>
/// </remarks>
public sealed class IntentManifestSignerPlugin : FeaturePluginBase
{
    private readonly ILogger<IntentManifestSignerPlugin> _logger;
    private IDisposable? _subscription;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntentManifestSignerPlugin"/> class.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    public IntentManifestSignerPlugin(ILogger<IntentManifestSignerPlugin> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Id => "aeds.manifest-signer";

    /// <inheritdoc />
    public override string Name => "Intent Manifest Signer";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string FeatureCategory => "Security";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Signs an intent manifest using the specified algorithm.
    /// </summary>
    /// <param name="manifest">The manifest to sign.</param>
    /// <param name="keyId">The key ID to use for signing.</param>
    /// <param name="algorithm">The signing algorithm (Ed25519, RSA-PSS-SHA256, ECDSA-P256-SHA256).</param>
    /// <param name="isReleaseKey">Whether this is a release signing key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A ManifestSignature containing the signature and metadata.</returns>
    /// <exception cref="ArgumentNullException">Thrown when manifest is null.</exception>
    /// <exception cref="ArgumentException">Thrown when keyId or algorithm is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when signing fails.</exception>
    public async Task<ManifestSignature> SignManifestAsync(
        IntentManifest manifest,
        string keyId,
        string algorithm,
        bool isReleaseKey,
        CancellationToken ct = default)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("Key ID cannot be null or empty", nameof(keyId));

        if (string.IsNullOrWhiteSpace(algorithm))
            throw new ArgumentException("Algorithm cannot be null or empty", nameof(algorithm));

        _logger.LogInformation(
            "Signing manifest {ManifestId} with algorithm {Algorithm} and key {KeyId}",
            manifest.ManifestId,
            algorithm,
            keyId);

        try
        {
            // Compute canonical form for signing
            var canonicalData = ComputeCanonicalForm(manifest);
            var canonicalBytes = System.Text.Encoding.UTF8.GetBytes(canonicalData);

            // Validate algorithm before delegating
            var normalizedAlgorithm = algorithm.ToUpperInvariant();
            if (normalizedAlgorithm is not ("ECDSA-P256-SHA256" or "RSA-PSS-SHA256" or "ED25519"))
                throw new ArgumentException($"Unsupported signing algorithm: {algorithm}", nameof(algorithm));

            // Delegate ALL signing to UltimateEncryption via message bus (Rule 15)
            var signatureBytes = await DelegateSignAsync(keyId, algorithm, canonicalBytes, ct);

            var signatureValue = Convert.ToBase64String(signatureBytes);

            _logger.LogInformation(
                "Successfully signed manifest {ManifestId} with {Algorithm}",
                manifest.ManifestId,
                algorithm);

            return new ManifestSignature
            {
                KeyId = keyId,
                Algorithm = algorithm,
                Value = signatureValue,
                IsReleaseKey = isReleaseKey,
                CertificateChain = null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to sign manifest {ManifestId} with algorithm {Algorithm}",
                manifest.ManifestId,
                algorithm);
            throw new InvalidOperationException($"Signing failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Computes the canonical form of the manifest for signing.
    /// </summary>
    /// <param name="manifest">The manifest to canonicalize.</param>
    /// <returns>Canonical string representation.</returns>
    private static string ComputeCanonicalForm(IntentManifest manifest)
    {
        var createdAtUnix = manifest.CreatedAt.ToUnixTimeMilliseconds();
        return $"{manifest.ManifestId}{createdAtUnix}{manifest.Payload.ContentHash}";
    }

    /// <summary>
    /// Delegates signing to UltimateEncryption via message bus (Rule 15: Capability Delegation).
    /// All cryptographic operations are owned by UltimateEncryption — this plugin never handles raw keys.
    /// </summary>
    /// <param name="keyId">The key ID for signing (resolved by UltimateKeyManagement).</param>
    /// <param name="algorithm">The signing algorithm.</param>
    /// <param name="data">The canonical data to sign.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Signature bytes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when message bus is unavailable or signing fails.</exception>
    private async Task<byte[]> DelegateSignAsync(string keyId, string algorithm, byte[] data, CancellationToken ct)
    {
        if (MessageBus == null)
            throw new InvalidOperationException("Message bus not available — cannot delegate signing to UltimateEncryption");

        var request = new PluginMessage
        {
            Type = "encryption.sign",
            SourcePluginId = Id,
            Payload = new Dictionary<string, object>
            {
                ["keyId"] = keyId,
                ["data"] = Convert.ToBase64String(data),
                ["algorithm"] = algorithm
            }
        };

        var response = await MessageBus.SendAsync("encryption.sign", request, TimeSpan.FromSeconds(10), ct);

        if (!response.Success)
        {
            throw new InvalidOperationException(
                $"Signing failed (algorithm: {algorithm}, key: {keyId}): {response.ErrorMessage ?? "UltimateEncryption unavailable"}");
        }

        if (response.Payload is not Dictionary<string, object> payload ||
            !payload.TryGetValue("signature", out var signatureObj) ||
            signatureObj is not string signatureBase64)
        {
            throw new InvalidOperationException("Invalid response from UltimateEncryption signing service");
        }

        _logger.LogDebug("Signature created via UltimateEncryption delegation ({Algorithm}, key {KeyId})", algorithm, keyId);
        return Convert.FromBase64String(signatureBase64);
    }

    /// <summary>
    /// Handles incoming signing requests via message bus.
    /// </summary>
    /// <param name="message">The signing request message.</param>
    /// <returns>Response containing the signature.</returns>
    private async Task<MessageResponse> HandleSigningRequestAsync(PluginMessage message)
    {
        try
        {
            if (message.Payload == null)
                return MessageResponse.Error("Missing payload in signing request");

            var payload = message.Payload as Dictionary<string, object>;
            if (payload == null)
                return MessageResponse.Error("Invalid payload format");

            if (!payload.TryGetValue("manifest", out var manifestObj) || manifestObj is not IntentManifest manifest)
                return MessageResponse.Error("Missing or invalid manifest in request");

            if (!payload.TryGetValue("keyId", out var keyIdObj) || keyIdObj is not string keyId)
                return MessageResponse.Error("Missing or invalid keyId in request");

            if (!payload.TryGetValue("algorithm", out var algorithmObj) || algorithmObj is not string algorithm)
                return MessageResponse.Error("Missing or invalid algorithm in request");

            var isReleaseKey = payload.TryGetValue("isReleaseKey", out var releaseKeyObj) &&
                               releaseKeyObj is bool b && b;

            var signature = await SignManifestAsync(manifest, keyId, algorithm, isReleaseKey);

            return MessageResponse.Ok(signature);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling signing request");
            return MessageResponse.Error($"Signing failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting {PluginName}", Name);

        // Subscribe to manifest signing requests
        if (MessageBus != null)
        {
            _subscription = MessageBus.Subscribe("aeds.manifest.sign", HandleSigningRequestAsync);
            _logger.LogInformation("Subscribed to aeds.manifest.sign topic");
        }
        else
        {
            _logger.LogWarning("Message bus not available; signing requests will not be handled");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task StopAsync()
    {
        _logger.LogInformation("Stopping {PluginName}", Name);

        _subscription?.Dispose();
        _subscription = null;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _subscription?.Dispose();
        }
        base.Dispose(disposing);
    }
}
