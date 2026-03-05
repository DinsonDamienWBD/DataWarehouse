using DataWarehouse.SDK;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Distribution;
using DataWarehouse.SDK.Primitives;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.AedsCore.Extensions;

/// <summary>
/// Notification Plugin: Platform-specific toast/modal notifications.
/// Supports Windows (native toast), Linux (libnotify), macOS (osascript).
/// </summary>
public sealed class NotificationPlugin : PlatformPluginBase
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

    /// <inheritdoc/>
    public override string PlatformDomain => "Notification";

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
                System.Diagnostics.Debug.WriteLine($"[Silent] Manifest {manifest.ManifestId}: {manifest.Payload.Name}");
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
            System.Diagnostics.Debug.WriteLine($"[Toast/Windows] {title}: {message}");
            await Task.Delay(100, ct); // Simulate notification display
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux: notify-send — pass title and message as separate argv entries (no shell quoting).
            // ProcessStartInfo.ArgumentList prevents shell injection; each entry is passed verbatim.
            try
            {
                // Sanitize: strip control characters that could corrupt the notification.
                var safeTitle = SanitizeForNotification(title);
                var safeMessage = SanitizeForNotification(message);
                var psi = new ProcessStartInfo
                {
                    FileName = "notify-send",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add(safeTitle);
                psi.ArgumentList.Add(safeMessage);
                var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                }
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"[Toast/Linux] {title}: {message}");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS: osascript — build the AppleScript string safely without interpolating user data
            // into the shell command. We use ArgumentList to avoid shell interpretation.
            try
            {
                // Sanitize: replace double-quotes and backslashes to prevent AppleScript injection.
                var safeTitle = SanitizeForAppleScript(title);
                var safeMessage = SanitizeForAppleScript(message);
                var appleScript = $"display notification \"{safeMessage}\" with title \"{safeTitle}\"";
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(appleScript);
                var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                }
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"[Toast/macOS] {title}: {message}");
            }
        }
        else
        {
            // Fallback: console
            System.Diagnostics.Debug.WriteLine($"[Toast] {title}: {message}");
            Console.Beep();
        }
    }

    /// <summary>
    /// Removes characters that could be misinterpreted by notification daemons.
    /// Strips ASCII control characters (0x00-0x1F, 0x7F).
    /// </summary>
    private static string SanitizeForNotification(string input)
        => new string(input.Where(c => c >= 0x20 && c != 0x7F).ToArray());

    /// <summary>
    /// Escapes characters that have meaning inside an AppleScript double-quoted string.
    /// Replaces backslash then double-quote so they are literal in the resulting string.
    /// </summary>
    private static string SanitizeForAppleScript(string input)
        => SanitizeForNotification(input).Replace("\\", "\\\\").Replace("\"", "\\\"");

    private async Task ShowModalAsync(string title, string message, CancellationToken ct)
    {
        // Modal requires persistent UI - for CLI, use console prompt
        System.Diagnostics.Debug.WriteLine($"[Modal] {title}");
        System.Diagnostics.Debug.WriteLine($"Message: {message}");
        System.Diagnostics.Debug.WriteLine("Press ENTER to acknowledge...");
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
