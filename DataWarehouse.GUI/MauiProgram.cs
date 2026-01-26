using DataWarehouse.GUI.Services;
using DataWarehouse.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace DataWarehouse.GUI;

/// <summary>
/// MAUI application builder and configuration.
/// Configures Blazor Hybrid for cross-platform GUI rendering.
/// </summary>
public static class MauiProgram
{
    /// <summary>
    /// Creates and configures the MAUI application host.
    /// </summary>
    /// <returns>The configured MAUI application.</returns>
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Add Blazor WebView
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        // builder.Logging.AddDebug(); // Requires Microsoft.Extensions.Logging.Debug package
#endif

        // Configure logging
        builder.Logging.AddConsole();

        // Register DataWarehouse.Shared services (business logic layer)
        RegisterSharedServices(builder.Services);

        // Register GUI-specific services (thin UI layer)
        RegisterGuiServices(builder.Services);

        return builder.Build();
    }

    /// <summary>
    /// Registers DataWarehouse.Shared services that contain all business logic.
    /// The GUI is a THIN UI LAYER - all business logic lives in Shared.
    /// </summary>
    private static void RegisterSharedServices(IServiceCollection services)
    {
        // Core Shared services - these contain ALL business logic
        services.AddSingleton<CapabilityManager>();
        services.AddSingleton<MessageBridge>();
        services.AddSingleton<InstanceManager>();

        // Command infrastructure from Shared
        // Commands are discovered from Shared.CommandRegistry
    }

    /// <summary>
    /// Registers GUI-specific services that handle UI rendering only.
    /// These services delegate ALL business operations to Shared.
    /// </summary>
    private static void RegisterGuiServices(IServiceCollection services)
    {
        // UI Services - these ONLY handle presentation, not business logic
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<KeyboardManager>();
        services.AddSingleton<GuiRenderer>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<NavigationService>();
    }
}
