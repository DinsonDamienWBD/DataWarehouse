using Microsoft.Extensions.Logging;

namespace DataWarehouse.GUI.Services;

/// <summary>
/// Service for displaying dialogs and user prompts.
/// This is a UI-only service - no business logic.
/// </summary>
public sealed class DialogService
{
    private readonly ILogger<DialogService> _logger;

    /// <summary>
    /// Initializes a new instance of the DialogService class.
    /// </summary>
    /// <param name="logger">Logger for dialog operations.</param>
    public DialogService(ILogger<DialogService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Shows an alert dialog (convenience alias for ShowAlertAsync).
    /// </summary>
    public Task AlertAsync(string title, string message) => ShowAlertAsync(title, message);

    /// <summary>
    /// Shows a confirmation dialog (convenience alias for ShowConfirmAsync).
    /// </summary>
    public Task<bool> ConfirmAsync(string title, string message) => ShowConfirmAsync(title, message);

    /// <summary>
    /// Shows a prompt dialog (convenience alias for ShowPromptAsync).
    /// </summary>
    public Task<string?> PromptAsync(string title, string message, string? placeholder = null)
        => ShowPromptAsync(title, message, placeholder: placeholder);

    /// <summary>
    /// Shows an alert dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Dialog message.</param>
    /// <param name="cancel">Cancel button text (default: OK).</param>
    public async Task ShowAlertAsync(string title, string message, string cancel = "OK")
    {
        try
        {
            if (Application.Current?.MainPage != null)
            {
                await Application.Current.MainPage.DisplayAlert(title, message, cancel);
            }
            else
            {
                _logger.LogWarning("Cannot show alert: MainPage is null");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show alert dialog");
        }
    }

    /// <summary>
    /// Shows a confirmation dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Dialog message.</param>
    /// <param name="accept">Accept button text (default: OK).</param>
    /// <param name="cancel">Cancel button text (default: Cancel).</param>
    /// <returns>True if user accepted, false otherwise.</returns>
    public async Task<bool> ShowConfirmAsync(string title, string message, string accept = "OK", string cancel = "Cancel")
    {
        try
        {
            if (Application.Current?.MainPage != null)
            {
                return await Application.Current.MainPage.DisplayAlert(title, message, accept, cancel);
            }
            else
            {
                _logger.LogWarning("Cannot show confirm dialog: MainPage is null");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show confirm dialog");
            return false;
        }
    }

    /// <summary>
    /// Shows an action sheet with multiple options.
    /// </summary>
    /// <param name="title">Sheet title.</param>
    /// <param name="cancel">Cancel button text.</param>
    /// <param name="destruction">Destruction button text (optional).</param>
    /// <param name="buttons">Array of action buttons.</param>
    /// <returns>The text of the selected button, or null if cancelled.</returns>
    public async Task<string?> ShowActionSheetAsync(string title, string cancel, string? destruction = null, params string[] buttons)
    {
        try
        {
            if (Application.Current?.MainPage != null)
            {
                return await Application.Current.MainPage.DisplayActionSheet(title, cancel, destruction, buttons);
            }
            else
            {
                _logger.LogWarning("Cannot show action sheet: MainPage is null");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show action sheet");
            return null;
        }
    }

    /// <summary>
    /// Shows a prompt dialog to get text input from user.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Dialog message.</param>
    /// <param name="accept">Accept button text (default: OK).</param>
    /// <param name="cancel">Cancel button text (default: Cancel).</param>
    /// <param name="placeholder">Input placeholder text.</param>
    /// <param name="initialValue">Initial input value.</param>
    /// <param name="maxLength">Maximum input length.</param>
    /// <param name="keyboard">Keyboard type to use.</param>
    /// <returns>The entered text, or null if cancelled.</returns>
    public async Task<string?> ShowPromptAsync(
        string title,
        string message,
        string accept = "OK",
        string cancel = "Cancel",
        string? placeholder = null,
        string? initialValue = null,
        int maxLength = -1,
        Keyboard? keyboard = null)
    {
        try
        {
            if (Application.Current?.MainPage != null)
            {
                return await Application.Current.MainPage.DisplayPromptAsync(
                    title,
                    message,
                    accept,
                    cancel,
                    placeholder,
                    maxLength,
                    keyboard ?? Keyboard.Default,
                    initialValue ?? string.Empty);
            }
            else
            {
                _logger.LogWarning("Cannot show prompt dialog: MainPage is null");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show prompt dialog");
            return null;
        }
    }
}
