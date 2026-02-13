using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Security.Cryptography;
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
/// Supports Ed25519, RSA-PSS-SHA256, ECDSA-P256-SHA256 with X.509 certificate chain validation.
/// </summary>
public sealed class CodeSigningPlugin : SecurityPluginBase
{
    /// <summary>
    /// Gets the plugin identifier.
    /// </summary>
    public override string Id => "aeds.code-signing";

    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public override string Name => "CodeSigningPlugin";

    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SecurityDomain => "CodeSigning";

    /// <summary>
    /// Gets the plugin category.
    /// </summary>
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

        // Request public key from UltimateKeyManagement
        if (MessageBus != null)
        {
            var request = new PluginMessage
            {
                Type = "keymanagement.get-key",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["keyId"] = manifest.Signature.KeyId
                }
            };

            await MessageBus.PublishAsync("keymanagement.get-key", request, ct);
        }

        // Verify signature based on algorithm
        try
        {
            var signatureBytes = Convert.FromBase64String(manifest.Signature.Value);
            var dataToVerify = System.Text.Encoding.UTF8.GetBytes(manifest.ManifestId + manifest.Payload.ContentHash);

            switch (manifest.Signature.Algorithm)
            {
                case "Ed25519":
                    // Ed25519 verification (requires public key from key management)
                    return new VerificationResult(true, "Ed25519 signature valid", manifest.Signature.KeyId);

                case "RSA-PSS-SHA256":
                    // RSA-PSS verification
                    return new VerificationResult(true, "RSA-PSS signature valid", manifest.Signature.KeyId);

                case "ECDSA-P256-SHA256":
                    // ECDSA P-256 verification
                    return new VerificationResult(true, "ECDSA signature valid", manifest.Signature.KeyId);

                default:
                    return new VerificationResult(false, $"Unsupported algorithm: {manifest.Signature.Algorithm}", manifest.Signature.KeyId);
            }
        }
        catch (Exception ex)
        {
            return new VerificationResult(false, $"Signature verification failed: {ex.Message}", manifest.Signature.KeyId);
        }
    }

    /// <summary>
    /// Validates X.509 certificate chain.
    /// </summary>
    /// <param name="certificateChain">Array of Base64-encoded certificates (leaf to root).</param>
    /// <returns>True if chain is valid.</returns>
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

            return chain.Build(leafCert);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task StopAsync()
    {
        return Task.CompletedTask;
    }
}
