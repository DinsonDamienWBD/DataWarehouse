using DataWarehouse.GUI.Services;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace DataWarehouse.GUI;

/// <summary>
/// Main application class for DataWarehouse GUI.
/// Initializes the application and sets up the main window.
/// </summary>
public partial class App : Application
{
    private readonly ThemeManager _themeManager;

    /// <summary>
    /// Initializes a new instance of the App class.
    /// </summary>
    /// <param name="themeManager">The theme manager service for handling application themes.</param>
    public App(ThemeManager themeManager)
    {
        _themeManager = themeManager;

        InitializeComponent();

        // Apply the initial theme based on system preference
        _themeManager.ApplySystemTheme();

        // Subscribe to theme changes
        _themeManager.ThemeChanged += OnThemeChanged;

        MainPage = new MainPage();
    }

    /// <summary>
    /// Creates a new window for the application.
    /// </summary>
    /// <param name="activationState">The activation state of the window.</param>
    /// <returns>A new Window instance configured for DataWarehouse.</returns>
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);

        if (window != null)
        {
            window.Title = "DataWarehouse";
            window.MinimumWidth = 1024;
            window.MinimumHeight = 768;

            // Set initial window size
            window.Width = 1400;
            window.Height = 900;
        }

        return window!;
    }

    /// <summary>
    /// Handles theme change events.
    /// </summary>
    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        // Update application theme
        if (Current?.Resources != null)
        {
            Current.UserAppTheme = e.NewTheme switch
            {
                Services.AppTheme.Dark => Microsoft.Maui.ApplicationModel.AppTheme.Dark,
                Services.AppTheme.Light => Microsoft.Maui.ApplicationModel.AppTheme.Light,
                _ => Microsoft.Maui.ApplicationModel.AppTheme.Unspecified
            };
        }
    }

    /// <summary>
    /// Called when the application is starting.
    /// </summary>
    protected override void OnStart()
    {
        base.OnStart();
    }

    /// <summary>
    /// Called when the application is sleeping.
    /// </summary>
    protected override void OnSleep()
    {
        base.OnSleep();
    }

    /// <summary>
    /// Called when the application is resuming.
    /// </summary>
    protected override void OnResume()
    {
        base.OnResume();
    }
}
