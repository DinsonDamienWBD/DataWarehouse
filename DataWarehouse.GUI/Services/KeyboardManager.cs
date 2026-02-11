using Microsoft.Extensions.Logging;

namespace DataWarehouse.GUI.Services;

/// <summary>
/// Keyboard shortcut definition.
/// </summary>
public sealed class KeyboardShortcut
{
    /// <summary>
    /// Gets or sets the key code.
    /// </summary>
    public string Key { get; init; } = "";

    /// <summary>
    /// Gets or sets whether Control/Command is required.
    /// </summary>
    public bool RequiresCtrl { get; init; }

    /// <summary>
    /// Gets or sets whether Shift is required.
    /// </summary>
    public bool RequiresShift { get; init; }

    /// <summary>
    /// Gets or sets whether Alt/Option is required.
    /// </summary>
    public bool RequiresAlt { get; init; }

    /// <summary>
    /// Gets or sets the action identifier.
    /// </summary>
    public string ActionId { get; init; } = "";

    /// <summary>
    /// Gets or sets the human-readable description.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Gets the shortcut display string (e.g., "Ctrl+K").
    /// </summary>
    public string DisplayString
    {
        get
        {
            var parts = new List<string>();
            if (RequiresCtrl) parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl");
            if (RequiresAlt) parts.Add(OperatingSystem.IsMacOS() ? "Option" : "Alt");
            if (RequiresShift) parts.Add("Shift");
            parts.Add(Key.ToUpperInvariant());
            return string.Join("+", parts);
        }
    }

    /// <summary>
    /// Checks if the keyboard event matches this shortcut.
    /// </summary>
    public bool Matches(string key, bool ctrlKey, bool shiftKey, bool altKey)
    {
        return Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
               RequiresCtrl == ctrlKey &&
               RequiresShift == shiftKey &&
               RequiresAlt == altKey;
    }
}

/// <summary>
/// Event arguments for keyboard shortcut events.
/// </summary>
public sealed class ShortcutEventArgs : EventArgs
{
    /// <summary>
    /// Gets the triggered shortcut.
    /// </summary>
    public required KeyboardShortcut Shortcut { get; init; }

    /// <summary>
    /// Gets or sets whether the event was handled.
    /// </summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Manages global keyboard shortcuts for the application.
/// This is a UI-only service - delegates actions to Shared commands.
/// </summary>
public sealed class KeyboardManager
{
    private readonly ILogger<KeyboardManager> _logger;
    private readonly Dictionary<string, KeyboardShortcut> _shortcuts = new();
    private readonly Dictionary<string, Func<Task>> _handlers = new();

    /// <summary>
    /// Event raised when a registered shortcut is triggered.
    /// </summary>
    public event EventHandler<ShortcutEventArgs>? ShortcutTriggered;

    /// <summary>
    /// Gets all registered shortcuts.
    /// </summary>
    public IReadOnlyList<KeyboardShortcut> Shortcuts => _shortcuts.Values.ToList();

    /// <summary>
    /// Initializes a new instance of the KeyboardManager class.
    /// </summary>
    /// <param name="logger">Logger for keyboard-related operations.</param>
    public KeyboardManager(ILogger<KeyboardManager> logger)
    {
        _logger = logger;
        RegisterDefaultShortcuts();
    }

    /// <summary>
    /// Registers a keyboard shortcut.
    /// </summary>
    /// <param name="shortcut">The shortcut definition.</param>
    /// <param name="handler">The async handler to execute when the shortcut is triggered.</param>
    public void RegisterShortcut(KeyboardShortcut shortcut, Func<Task>? handler = null)
    {
        _shortcuts[shortcut.ActionId] = shortcut;

        if (handler != null)
        {
            _handlers[shortcut.ActionId] = handler;
        }

        _logger.LogDebug("Registered shortcut: {Display} -> {Action}",
            shortcut.DisplayString, shortcut.ActionId);
    }

    /// <summary>
    /// Unregisters a keyboard shortcut.
    /// </summary>
    /// <param name="actionId">The action identifier to unregister.</param>
    public void UnregisterShortcut(string actionId)
    {
        _shortcuts.Remove(actionId);
        _handlers.Remove(actionId);

        _logger.LogDebug("Unregistered shortcut: {Action}", actionId);
    }

    /// <summary>
    /// Sets the handler for a shortcut action.
    /// </summary>
    /// <param name="actionId">The action identifier.</param>
    /// <param name="handler">The async handler to execute.</param>
    public void SetHandler(string actionId, Func<Task> handler)
    {
        _handlers[actionId] = handler;
    }

    /// <summary>
    /// Handles a keyboard event from the UI.
    /// </summary>
    /// <param name="key">The key that was pressed.</param>
    /// <param name="ctrlKey">Whether Ctrl/Cmd was pressed.</param>
    /// <param name="shiftKey">Whether Shift was pressed.</param>
    /// <param name="altKey">Whether Alt/Option was pressed.</param>
    /// <returns>True if the event was handled.</returns>
    public async Task<bool> HandleKeyDownAsync(string key, bool ctrlKey, bool shiftKey, bool altKey)
    {
        foreach (var shortcut in _shortcuts.Values)
        {
            if (shortcut.Matches(key, ctrlKey, shiftKey, altKey))
            {
                _logger.LogDebug("Shortcut triggered: {Action}", shortcut.ActionId);

                var args = new ShortcutEventArgs { Shortcut = shortcut };
                ShortcutTriggered?.Invoke(this, args);

                if (!args.Handled && _handlers.TryGetValue(shortcut.ActionId, out var handler))
                {
                    await handler();
                    return true;
                }

                return args.Handled;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets shortcuts grouped by category.
    /// </summary>
    /// <returns>Dictionary of category to shortcuts.</returns>
    public Dictionary<string, List<KeyboardShortcut>> GetShortcutsByCategory()
    {
        return _shortcuts.Values
            .GroupBy(s => GetCategory(s.ActionId))
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Registers the default application shortcuts.
    /// </summary>
    private void RegisterDefaultShortcuts()
    {
        // Command palette - the most important shortcut
        RegisterShortcut(new KeyboardShortcut
        {
            Key = "k",
            RequiresCtrl = true,
            ActionId = "command-palette",
            Description = "Open command palette"
        });

        // Navigation shortcuts
        RegisterShortcut(new KeyboardShortcut
        {
            Key = "1",
            RequiresCtrl = true,
            ActionId = "nav-dashboard",
            Description = "Go to Dashboard"
        });

        RegisterShortcut(new KeyboardShortcut
        {
            Key = "2",
            RequiresCtrl = true,
            ActionId = "nav-storage",
            Description = "Go to Storage Browser"
        });

        RegisterShortcut(new KeyboardShortcut
        {
            Key = "3",
            RequiresCtrl = true,
            ActionId = "nav-config",
            Description = "Go to Configuration"
        });

        // File operations
        RegisterShortcut(new KeyboardShortcut
        {
            Key = "n",
            RequiresCtrl = true,
            ActionId = "file-new",
            Description = "New object"
        });

        RegisterShortcut(new KeyboardShortcut
        {
            Key = "o",
            RequiresCtrl = true,
            ActionId = "file-open",
            Description = "Open object"
        });

        RegisterShortcut(new KeyboardShortcut
        {
            Key = "s",
            RequiresCtrl = true,
            ActionId = "file-save",
            Description = "Save current"
        });

        RegisterShortcut(new KeyboardShortcut
        {
            Key = "u",
            RequiresCtrl = true,
            ActionId = "file-upload",
            Description = "Upload files"
        });

        // Search
        RegisterShortcut(new KeyboardShortcut
        {
            Key = "f",
            RequiresCtrl = true,
            ActionId = "search",
            Description = "Search"
        });

        RegisterShortcut(new KeyboardShortcut
        {
            Key = "f",
            RequiresCtrl = true,
            RequiresShift = true,
            ActionId = "search-advanced",
            Description = "Advanced search"
        });

        // View
        RegisterShortcut(new KeyboardShortcut
        {
            Key = "r",
            RequiresCtrl = true,
            ActionId = "refresh",
            Description = "Refresh"
        });

        RegisterShortcut(new KeyboardShortcut
        {
            Key = "l",
            RequiresCtrl = true,
            RequiresShift = true,
            ActionId = "toggle-sidebar",
            Description = "Toggle sidebar"
        });

        RegisterShortcut(new KeyboardShortcut
        {
            Key = "t",
            RequiresCtrl = true,
            RequiresShift = true,
            ActionId = "toggle-theme",
            Description = "Toggle dark/light theme"
        });

        // Help
        RegisterShortcut(new KeyboardShortcut
        {
            Key = "?",
            RequiresShift = true,
            ActionId = "help",
            Description = "Show help"
        });

        RegisterShortcut(new KeyboardShortcut
        {
            Key = "Escape",
            ActionId = "escape",
            Description = "Close dialog/cancel"
        });
    }

    /// <summary>
    /// Gets the category for an action ID.
    /// </summary>
    private static string GetCategory(string actionId)
    {
        if (actionId.StartsWith("nav-")) return "Navigation";
        if (actionId.StartsWith("file-")) return "File";
        if (actionId.StartsWith("search")) return "Search";
        if (actionId.StartsWith("toggle-")) return "View";
        if (actionId == "command-palette") return "General";
        if (actionId == "refresh") return "View";
        if (actionId == "help" || actionId == "escape") return "General";
        return "Other";
    }
}
