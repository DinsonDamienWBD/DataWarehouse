using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Security.Cryptography.X509Certificates;

namespace DataWarehouse.Plugins.AedsCore.Extensions;

/// <summary>
/// Verification result from code signing check.
/// </summary>
/// <param name="Valid">Whether signature is valid.</param>
/// <param name="Reason">Validation reason or error message.</param>
/// <param name="KeyId">Signing key ID.</param>
public record VerificationResult(bool Valid, string Reason, string KeyId);

/// <summary>
/// Code Signing Plugin: Release key verification for Execute actions.
/// </summary>
/// <remarks>
/// <para>
/// All cryptographic verification is delegated to UltimateEncryption via message bus
/// (Rule 15: Capability Delegation). This plugin owns the AEDS code-signing policy
/// (release key requirements, certificate chain validation) but delegates the actual
/// signature math to the encryption capability owner.
/// </para>
/// <para>
/// <strong>Supported Algorithms:</strong> Ed25519, RSA-PSS-SHA256, ECDSA-P256-SHA256.
/// All verified via <c>encryption.verify-signature</c> bus topic.
/// </para>
/// </remarks>
public sealed class CodeSigningPlugin : SecurityPluginBase
{
    private IDisposable? _subscription;

    /// <inheritdoc />
    public override string Id => "aeds.code-signing";

    /// <inheritdoc />
    public override string Name => "CodeSigningPlugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SecurityDomain => "CodeSigning";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Verifies release key signature on manifest.
    /// </summary>
    /// <param name="manifest">Intent manifest to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result.</returns>
    public async Task<VerificationResult> VerifyReleaseKeyAsync(IntentManifest manifest, CancellationToken ct = default)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        if (!manifest.Signature.IsReleaseKey && manifest.Action == ActionPrimitive.Execute)
        {
            return new VerificationResult(false, "Execute action requires release signing key", manifest.Signature.KeyId);
        }

        // Validate algorithm is supported
        var algorithm = manifest.Signature.Algorithm;
        if (algorithm is not ("Ed25519" or "RSA-PSS-SHA256" or "ECDSA-P256-SHA256"))
        {
            return new VerificationResult(false, $"Unsupported algorithm: {algorithm}", manifest.Signature.KeyId);
        }

        try
        {
            var signatureBytes = Convert.FromBase64String(manifest.Signature.Value);

            // Compute canonical form (must match signing process in IntentManifestSignerPlugin)
            var canonicalData = manifest.ManifestId +
                                manifest.CreatedAt.ToUnixTimeMilliseconds() +
                                manifest.Payload.ContentHash;
            var dataToVerify = System.Text.Encoding.UTF8.GetBytes(canonicalData);

            // Delegate ALL verification to UltimateEncryption via message bus (Rule 15)
            return await DelegateVerifyAsync(manifest.Signature.KeyId, algorithm, dataToVerify, signatureBytes, ct);
        }
        catch (Exception ex)
        {
            return new VerificationResult(false, $"Signature verification failed: {ex.Message}", manifest.Signature.KeyId);
        }
    }

    /// <summary>
    /// Delegates signature verification to UltimateEncryption via message bus (Rule 15).
    /// All cryptographic operations are owned by UltimateEncryption — this plugin never
    /// imports keys or performs signature math directly.
    /// </summary>
    /// <param name="keyId">The public key ID (resolved by UltimateKeyManagement).</param>
    /// <param name="algorithm">The signing algorithm used.</param>
    /// <param name="data">The canonical data that was signed.</param>
    /// <param name="signature">The signature bytes to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result.</returns>
    private async Task<VerificationResult> DelegateVerifyAsync(
        string keyId,
        string algorithm,
        byte[] data,
        byte[] signature,
        CancellationToken ct)
    {
        if (MessageBus == null)
            return new VerificationResult(false, "UltimateEncryption unavailable (no message bus)", keyId);

        try
        {
            var request = new PluginMessage
            {
                Type = "encryption.verify-signature",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["keyId"] = keyId,
                    ["data"] = Convert.ToBase64String(data),
                    ["signature"] = Convert.ToBase64String(signature),
                    ["algorithm"] = algorithm
                }
            };

            var response = await MessageBus.SendAsync(
                "encryption.verify-signature",
                request,
                TimeSpan.FromSeconds(10),
                ct);

            if (!response.Success)
            {
                return new VerificationResult(
                    false,
                    $"Verification failed: {response.ErrorMessage ?? "UltimateEncryption unavailable"}",
                    keyId);
            }

            if (response.Payload is Dictionary<string, object> payload &&
                payload.TryGetValue("valid", out var validObj) &&
                validObj is bool isValid)
            {
                return new VerificationResult(
                    isValid,
                    isValid ? $"{algorithm} signature valid" : $"{algorithm} signature invalid",
                    keyId);
            }

            return new VerificationResult(false, "Invalid response from UltimateEncryption", keyId);
        }
        catch (Exception ex)
        {
            return new VerificationResult(false, $"Verification error: {ex.Message}", keyId);
        }
    }

    /// <summary>
    /// Validates X.509 certificate chain for release key trust.
    /// </summary>
    /// <param name="certificateChain">Array of Base64-encoded certificates (leaf to root).</param>
    /// <returns>True if chain is valid and trusted.</returns>
    public bool ValidateCertificateChain(string[] certificateChain)
    {
        if (certificateChain == null || certificateChain.Length == 0)
            return false;

        try
        {
            var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EndCertificateOnly;

            var leafCertBytes = Convert.FromBase64String(certificateChain[0]);
            var leafCert = X509CertificateLoader.LoadCertificate(leafCertBytes);

            var valid = chain.Build(leafCert);
            if (!valid)
            {
                // Log each chain status element so the caller can distinguish
                // misconfigured trust store from a legitimately rejected certificate.
                var statuses = chain.ChainStatus
                    .Select(s => $"{s.Status}: {s.StatusInformation?.Trim()}")
                    .ToArray();
                System.Diagnostics.Debug.WriteLine(
                    $"[CodeSigningPlugin] Certificate chain validation failed: {string.Join("; ", statuses)}");
            }
            return valid;
        }
        catch (FormatException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CodeSigningPlugin] Certificate Base64 decode failed: {ex.Message}");
            return false;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CodeSigningPlugin] Certificate load failed: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CodeSigningPlugin] Unexpected error in ValidateCertificateChain: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Handles incoming verification requests via message bus.
    /// </summary>
    private async Task<MessageResponse> HandleVerificationRequestAsync(PluginMessage message)
    {
        try
        {
            if (message.Payload is not Dictionary<string, object> payload)
                return MessageResponse.Error("Invalid payload format");

            if (!payload.TryGetValue("manifest", out var manifestObj) || manifestObj is not IntentManifest manifest)
                return MessageResponse.Error("Missing or invalid manifest in request");

            var result = await VerifyReleaseKeyAsync(manifest);

            return MessageResponse.Ok(new Dictionary<string, object>
            {
                ["valid"] = result.Valid,
                ["reason"] = result.Reason,
                ["keyId"] = result.KeyId
            });
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Verification failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        if (MessageBus != null)
        {
            _subscription = MessageBus.Subscribe("aeds.manifest.verify", HandleVerificationRequestAsync);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task StopAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _subscription?.Dispose();
        }
        base.Dispose(disposing);
    }
}
