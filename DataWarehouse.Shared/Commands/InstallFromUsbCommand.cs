// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Services;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Command to install DataWarehouse from a USB/portable source directory.
/// Validates the source, copies the directory tree, remaps configuration paths,
/// and optionally registers as a system service.
/// </summary>
public sealed class InstallFromUsbCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "install.from-usb";

    /// <inheritdoc />
    public string Description => "Install DataWarehouse from a USB/portable source";

    /// <inheritdoc />
    public string Category => "deployment";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        // Extract parameters
        var source = parameters.TryGetValue("source", out var s) ? s?.ToString() : null;
        var target = parameters.TryGetValue("path", out var p) ? p?.ToString() : null;
        var copyData = !parameters.TryGetValue("copyData", out var cd) || cd is not false;
        var createService = parameters.TryGetValue("service", out var svc) && (svc is true || svc?.ToString() == "True");
        var autoStart = parameters.TryGetValue("autostart", out var a) && (a is true || a?.ToString() == "True");
        var adminPassword = parameters.TryGetValue("adminPassword", out var pwd) ? pwd?.ToString() : null;

        // Validate required parameters
        if (string.IsNullOrWhiteSpace(source))
        {
            return CommandResult.Fail("USB source path is required. Use --from-usb <source>.");
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return CommandResult.Fail("Target installation path is required. Use --path <directory>.");
        }

        // Create USB installer and validate source first
        var installer = new UsbInstaller();
        var validation = installer.ValidateUsbSource(source);

        if (!validation.IsValid)
        {
            var data = new Dictionary<string, object?>
            {
                ["valid"] = false,
                ["errors"] = validation.Errors,
                ["source"] = source
            };
            return CommandResult.Fail(
                $"USB source validation failed:\n" +
                string.Join("\n", validation.Errors.Select(e => $"  - {e}")));
        }

        // Configure and execute USB installation
        var config = new UsbInstallConfiguration
        {
            SourcePath = source,
            TargetPath = target,
            CopyData = copyData,
            CreateService = createService,
            AutoStart = autoStart,
            AdminPassword = adminPassword
        };

        var progressMessages = new List<string>();
        var progress = new Progress<UsbInstallProgress>(p =>
            progressMessages.Add($"[{p.PercentComplete}%] {p.Message}"));

        var result = await installer.InstallFromUsbAsync(config, progress, cancellationToken);

        // If service registration was requested, delegate to IServerHost
        if (result.Success && createService)
        {
            var host = ServerHostRegistry.Current;
            if (host != null)
            {
                var installConfig = new SDK.Hosting.InstallConfiguration
                {
                    InstallPath = target,
                    CreateService = true,
                    AutoStart = autoStart,
                    CreateDefaultAdmin = false
                };
                await host.InstallAsync(installConfig, null, cancellationToken);
            }
            else
            {
                result.Warnings.Add("Service registration requested but server host not available.");
            }
        }

        if (result.Success)
        {
            var data = new Dictionary<string, object?>
            {
                ["source"] = source,
                ["target"] = target,
                ["filesCopied"] = result.FilesCopied,
                ["bytesCopied"] = result.BytesCopied,
                ["sizeFormatted"] = FormatBytes(result.BytesCopied),
                ["dataCopied"] = copyData,
                ["serviceRegistered"] = createService,
                ["verificationPassed"] = result.Verification?.AllPassed ?? false,
                ["warnings"] = result.Warnings,
                ["steps"] = progressMessages
            };

            return CommandResult.Ok(data,
                $"Installed from USB ({FormatBytes(result.BytesCopied)}, {result.FilesCopied} files) to {target}.");
        }

        return CommandResult.Fail($"USB installation failed: {result.Message}");
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} bytes"
        };
    }
}
