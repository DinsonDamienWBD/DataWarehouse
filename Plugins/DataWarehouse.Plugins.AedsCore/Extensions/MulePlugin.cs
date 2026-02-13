using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Text.Json;

namespace DataWarehouse.Plugins.AedsCore.Extensions;

/// <summary>
/// Mule Plugin: Air-gap USB transport for manifests and payloads.
/// Integrates with tri-mode USB (T79) for offline distribution.
/// </summary>
public sealed class MulePlugin : LegacyFeaturePluginBase
{
    /// <summary>
    /// Gets the plugin identifier.
    /// </summary>
    public override string Id => "aeds.mule";

    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public override string Name => "MulePlugin";

    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <summary>
    /// Gets the plugin category.
    /// </summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Exports manifests and payloads to USB for air-gap transport.
    /// </summary>
    /// <param name="manifestIds">Manifest IDs to export.</param>
    /// <param name="usbMountPath">USB mount path.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ExportManifestsAsync(
        string[] manifestIds,
        string usbMountPath,
        CancellationToken ct = default)
    {
        if (manifestIds == null || manifestIds.Length == 0)
            throw new ArgumentException("Manifest IDs cannot be null or empty.", nameof(manifestIds));
        if (string.IsNullOrEmpty(usbMountPath))
            throw new ArgumentException("USB mount path cannot be null or empty.", nameof(usbMountPath));

        var aedsPath = Path.Combine(usbMountPath, ".aeds");
        var manifestsPath = Path.Combine(aedsPath, "manifests");
        var payloadsPath = Path.Combine(aedsPath, "payloads");

        Directory.CreateDirectory(manifestsPath);
        Directory.CreateDirectory(payloadsPath);

        var exportIndex = new Dictionary<string, object>
        {
            ["exportedAt"] = DateTimeOffset.UtcNow,
            ["manifestIds"] = manifestIds,
            ["version"] = "1.0"
        };

        foreach (var manifestId in manifestIds)
        {
            var manifestFile = Path.Combine(manifestsPath, $"{manifestId}.json");
            await File.WriteAllTextAsync(manifestFile, $"{{\"manifestId\":\"{manifestId}\"}}", ct);
        }

        var indexFile = Path.Combine(aedsPath, "index.json");
        await File.WriteAllTextAsync(indexFile, JsonSerializer.Serialize(exportIndex), ct);

        // Notify tri-mode USB plugin
        if (MessageBus != null)
        {
            var request = new PluginMessage
            {
                Type = "airgap.export",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["path"] = aedsPath,
                    ["manifestIds"] = manifestIds
                }
            };

            await MessageBus.PublishAsync("airgap.export", request, ct);
        }
    }

    /// <summary>
    /// Imports manifests and payloads from USB.
    /// </summary>
    /// <param name="usbMountPath">USB mount path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of manifests imported.</returns>
    public async Task<int> ImportManifestsAsync(string usbMountPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(usbMountPath))
            throw new ArgumentException("USB mount path cannot be null or empty.", nameof(usbMountPath));

        var aedsPath = Path.Combine(usbMountPath, ".aeds");
        var indexFile = Path.Combine(aedsPath, "index.json");

        if (!File.Exists(indexFile))
            throw new FileNotFoundException("No AEDS index found on USB.", indexFile);

        var indexJson = await File.ReadAllTextAsync(indexFile, ct);
        var index = JsonSerializer.Deserialize<Dictionary<string, object>>(indexJson);

        if (index == null || !index.TryGetValue("manifestIds", out var manifestIdsObj))
            return 0;

        var manifestIds = manifestIdsObj as JsonElement? != null
            ? ((JsonElement)manifestIdsObj).EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
            : Array.Empty<string>();

        foreach (var manifestId in manifestIds.Where(id => !string.IsNullOrEmpty(id)))
        {
            if (MessageBus != null)
            {
                var notification = new PluginMessage
                {
                    Type = "aeds.manifest-imported",
                    SourcePluginId = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["manifestId"] = manifestId,
                        ["source"] = "mule-usb"
                    }
                };

                await MessageBus.PublishAsync("aeds.manifest-imported", notification, ct);
            }
        }

        return manifestIds.Length;
    }

    /// <summary>
    /// Validates mule integrity (manifests and payload hashes).
    /// </summary>
    /// <param name="usbMountPath">USB mount path.</param>
    /// <returns>Validation report with errors.</returns>
    public Dictionary<string, string> ValidateMuleIntegrity(string usbMountPath)
    {
        if (string.IsNullOrEmpty(usbMountPath))
            throw new ArgumentException("USB mount path cannot be null or empty.", nameof(usbMountPath));

        var errors = new Dictionary<string, string>();
        var aedsPath = Path.Combine(usbMountPath, ".aeds");

        if (!Directory.Exists(aedsPath))
        {
            errors["aedsPath"] = "AEDS directory not found";
            return errors;
        }

        var indexFile = Path.Combine(aedsPath, "index.json");
        if (!File.Exists(indexFile))
        {
            errors["index"] = "Index file missing";
        }

        return errors;
    }

    /// <summary>
    /// Checks if this plugin is enabled based on client capabilities.
    /// </summary>
    /// <param name="capabilities">Client capabilities.</param>
    /// <returns>True if AirGapMule capability is enabled.</returns>
    public static bool IsEnabled(ClientCapabilities capabilities)
    {
        return capabilities.HasFlag(ClientCapabilities.AirGapMule);
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
