using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;

namespace DataWarehouse.GUI.Services;

/// <summary>
/// Application theme enumeration.
/// </summary>
public enum AppTheme
{
    /// <summary>
    /// Follow system theme settings.
    /// </summary>
    System,

    /// <summary>
    /// Light theme with bright backgrounds.
    /// </summary>
    Light,

    /// <summary>
    /// Dark theme with dark backgrounds.
    /// </summary>
    Dark
}

/// <summary>
/// Event arguments for theme change events.
/// </summary>
public sealed class ThemeChangedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the previous theme.
    /// </summary>
    public AppTheme PreviousTheme { get; init; }

    /// <summary>
    /// Gets the new theme.
    /// </summary>
    public AppTheme NewTheme { get; init; }
}

/// <summary>
/// Manages application themes (Dark/Light/System).
/// This is a UI-only service - no business logic.
/// </summary>
public sealed class ThemeManager
{
    private readonly ILogger<ThemeManager> _logger;
    private AppTheme _currentTheme = AppTheme.System;
    private readonly string _settingsPath;

    /// <summary>
    /// Event raised when the theme changes.
    /// </summary>
    public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;

    /// <summary>
    /// Gets the current application theme.
    /// </summary>
    public AppTheme CurrentTheme => _currentTheme;

    /// <summary>
    /// Gets the effective theme (resolves System to actual Light/Dark).
    /// </summary>
    public AppTheme EffectiveTheme
    {
        get
        {
            if (_currentTheme == AppTheme.System)
            {
                var systemTheme = Application.Current?.RequestedTheme;
                return systemTheme == Microsoft.Maui.ApplicationModel.AppTheme.Dark
                    ? AppTheme.Dark
                    : AppTheme.Light;
            }
            return _currentTheme;
        }
    }

    /// <summary>
    /// Initializes a new instance of the ThemeManager class.
    /// </summary>
    /// <param name="logger">Logger for theme-related operations.</param>
    public ThemeManager(ILogger<ThemeManager> logger)
    {
        _logger = logger;
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse",
            "GUI",
            "theme.json");

        LoadThemeSettings();
    }

    /// <summary>
    /// Sets the application theme.
    /// </summary>
    /// <param name="theme">The theme to apply.</param>
    public void SetTheme(AppTheme theme)
    {
        if (_currentTheme == theme)
        {
            return;
        }

        var previousTheme = _currentTheme;
        _currentTheme = theme;

        _logger.LogInformation("Theme changed from {Previous} to {New}", previousTheme, theme);

        SaveThemeSettings();

        ThemeChanged?.Invoke(this, new ThemeChangedEventArgs
        {
            PreviousTheme = previousTheme,
            NewTheme = theme
        });
    }

    /// <summary>
    /// Applies the system theme preference.
    /// </summary>
    public void ApplySystemTheme()
    {
        if (_currentTheme == AppTheme.System)
        {
            // Trigger re-evaluation of effective theme
            ThemeChanged?.Invoke(this, new ThemeChangedEventArgs
            {
                PreviousTheme = AppTheme.System,
                NewTheme = AppTheme.System
            });
        }
    }

    /// <summary>
    /// Toggles between light and dark themes.
    /// If current theme is System, switches to the opposite of the effective theme.
    /// </summary>
    public void ToggleTheme()
    {
        var effective = EffectiveTheme;
        SetTheme(effective == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
    }

    /// <summary>
    /// Gets CSS classes for the current theme.
    /// </summary>
    /// <returns>CSS class string for the current theme.</returns>
    public string GetThemeCssClass()
    {
        return EffectiveTheme == AppTheme.Dark ? "theme-dark" : "theme-light";
    }

    /// <summary>
    /// Gets theme-specific colors for UI elements.
    /// </summary>
    /// <returns>Dictionary of color names to hex values.</returns>
    public Dictionary<string, string> GetThemeColors()
    {
        return EffectiveTheme == AppTheme.Dark
            ? new Dictionary<string, string>
            {
                ["background"] = "#0F172A",
                ["surface"] = "#1E293B",
                ["text"] = "#F8FAFC",
                ["textSecondary"] = "#94A3B8",
                ["primary"] = "#2563EB",
                ["secondary"] = "#7C3AED",
                ["accent"] = "#10B981",
                ["border"] = "#334155"
            }
            : new Dictionary<string, string>
            {
                ["background"] = "#F8FAFC",
                ["surface"] = "#FFFFFF",
                ["text"] = "#0F172A",
                ["textSecondary"] = "#64748B",
                ["primary"] = "#2563EB",
                ["secondary"] = "#7C3AED",
                ["accent"] = "#10B981",
                ["border"] = "#E2E8F0"
            };
    }

    /// <summary>
    /// Loads theme settings from persistent storage.
    /// </summary>
    private void LoadThemeSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<ThemeSettings>(json);
                if (settings != null && Enum.TryParse<AppTheme>(settings.Theme, out var theme))
                {
                    _currentTheme = theme;
                    _logger.LogDebug("Loaded theme setting: {Theme}", theme);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load theme settings, using default");
        }
    }

    /// <summary>
    /// Saves theme settings to persistent storage.
    /// </summary>
    private void SaveThemeSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var settings = new ThemeSettings { Theme = _currentTheme.ToString() };
            var json = System.Text.Json.JsonSerializer.Serialize(settings);
            File.WriteAllText(_settingsPath, json);

            _logger.LogDebug("Saved theme setting: {Theme}", _currentTheme);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save theme settings");
        }
    }

    private sealed class ThemeSettings
    {
        public string Theme { get; set; } = "System";
    }
}
