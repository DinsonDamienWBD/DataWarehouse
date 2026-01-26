using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace DataWarehouse.GUI;

/// <summary>
/// Entry point for the DataWarehouse GUI application.
/// </summary>
public static class Program
{
    /// <summary>
    /// Application entry point for Windows.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Create and run the MAUI application
        var mauiApp = MauiProgram.CreateMauiApp();

        // Initialize application (this will handle platform-specific bootstrap)
        // For Windows, this internally calls WinUI Application.Start
        mauiApp.Services.GetService(typeof(IApplication));

        return 0;
    }
}
