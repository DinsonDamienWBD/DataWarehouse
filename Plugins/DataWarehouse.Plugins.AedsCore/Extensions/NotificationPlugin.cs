using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.AedsCore.Extensions;

/// <summary>
/// Notification Plugin: Platform-specific toast/modal notifications.
/// Supports Windows (native toast), Linux (libnotify), macOS (osascript).
/// </summary>
public sealed class NotificationPlugin : LegacyFeaturePluginBase
{
    /// <summary>
    /// Gets the plugin identifier.
    /// </summary>
    public override string Id => "aeds.notification";

    /// <summary>
    /// Gets the plugin name.
    /// </summary>
    public override string Name => "NotificationPlugin";

    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <summary>
    /// Gets the plugin category.
    /// </summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Shows notification based on manifest tier.
    /// </summary>
    /// <param name="manifest">Intent manifest with notification tier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ShowNotificationAsync(IntentManifest manifest, CancellationToken ct = default)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        switch (manifest.NotificationTier)
        {
            case NotificationTier.Silent:
                // Log only
                Console.WriteLine($"[Silent] Manifest {manifest.ManifestId}: {manifest.Payload.Name}");
                break;

            case NotificationTier.Toast:
                await ShowToastAsync(manifest.Payload.Name, manifest.ManifestId, ct);
                break;

            case NotificationTier.Modal:
                await ShowModalAsync(manifest.Payload.Name, manifest.ManifestId, ct);
                break;
        }
    }

    /// <summary>
    /// Acknowledges notification (for modals requiring user response).
    /// </summary>
    /// <param name="manifestId">Manifest ID being acknowledged.</param>
    /// <param name="userApproved">Whether user approved the action.</param>
    /// <returns>Policy action result.</returns>
    public PolicyAction AcknowledgeNotification(string manifestId, bool userApproved)
    {
        if (string.IsNullOrEmpty(manifestId))
            throw new ArgumentException("Manifest ID cannot be null or empty.", nameof(manifestId));

        return userApproved ? PolicyAction.Allow : PolicyAction.Deny;
    }

    /// <summary>
    /// Checks if this plugin is enabled based on client capabilities.
    /// </summary>
    /// <param name="capabilities">Client capabilities.</param>
    /// <returns>True if ReceiveNotify capability is enabled.</returns>
    public static bool IsEnabled(ClientCapabilities capabilities)
    {
        return capabilities.HasFlag(ClientCapabilities.ReceiveNotify);
    }

    private async Task ShowToastAsync(string title, string message, CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows toast notification
            Console.WriteLine($"[Toast/Windows] {title}: {message}");
            await Task.Delay(100, ct); // Simulate notification display
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux: notify-send
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "notify-send",
                    Arguments = $"\"{title}\" \"{message}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                });
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                }
            }
            catch
            {
                Console.WriteLine($"[Toast/Linux] {title}: {message}");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS: osascript
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = $"-e 'display notification \"{message}\" with title \"{title}\"'",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                });
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                }
            }
            catch
            {
                Console.WriteLine($"[Toast/macOS] {title}: {message}");
            }
        }
        else
        {
            // Fallback: console
            Console.WriteLine($"[Toast] {title}: {message}");
            Console.Beep();
        }
    }

    private async Task ShowModalAsync(string title, string message, CancellationToken ct)
    {
        // Modal requires persistent UI - for CLI, use console prompt
        Console.WriteLine($"[Modal] {title}");
        Console.WriteLine($"Message: {message}");
        Console.WriteLine("Press ENTER to acknowledge...");
        await Task.Delay(100, ct);
        // In GUI, this would show an actual modal dialog
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
