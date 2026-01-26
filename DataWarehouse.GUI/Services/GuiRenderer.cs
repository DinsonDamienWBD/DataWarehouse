using DataWarehouse.Shared;
using DataWarehouse.Shared.Models;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.GUI.Services;

/// <summary>
/// Represents a rendered output element for the UI.
/// </summary>
public sealed class RenderedOutput
{
    /// <summary>
    /// Gets or sets the output type for styling.
    /// </summary>
    public RenderedOutputType Type { get; init; } = RenderedOutputType.Text;

    /// <summary>
    /// Gets or sets the rendered content.
    /// </summary>
    public string Content { get; init; } = "";

    /// <summary>
    /// Gets or sets the CSS class to apply.
    /// </summary>
    public string CssClass { get; init; } = "";

    /// <summary>
    /// Gets or sets any child elements.
    /// </summary>
    public List<RenderedOutput> Children { get; init; } = new();

    /// <summary>
    /// Gets or sets additional metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Types of rendered output elements.
/// </summary>
public enum RenderedOutputType
{
    /// <summary>
    /// Plain text content.
    /// </summary>
    Text,

    /// <summary>
    /// Heading/title element.
    /// </summary>
    Heading,

    /// <summary>
    /// Success message.
    /// </summary>
    Success,

    /// <summary>
    /// Warning message.
    /// </summary>
    Warning,

    /// <summary>
    /// Error message.
    /// </summary>
    Error,

    /// <summary>
    /// Informational message.
    /// </summary>
    Info,

    /// <summary>
    /// Table/grid data.
    /// </summary>
    Table,

    /// <summary>
    /// List of items.
    /// </summary>
    List,

    /// <summary>
    /// Progress indicator.
    /// </summary>
    Progress,

    /// <summary>
    /// Code/preformatted text.
    /// </summary>
    Code,

    /// <summary>
    /// Key-value pair.
    /// </summary>
    KeyValue,

    /// <summary>
    /// Chart/visualization.
    /// </summary>
    Chart
}

/// <summary>
/// Renders command results and data for GUI display.
/// This service transforms Shared command results into UI elements.
/// All business logic lives in Shared - this only handles presentation.
/// </summary>
public sealed class GuiRenderer
{
    private readonly ILogger<GuiRenderer> _logger;

    /// <summary>
    /// Initializes a new instance of the GuiRenderer class.
    /// </summary>
    /// <param name="logger">Logger for rendering operations.</param>
    public GuiRenderer(ILogger<GuiRenderer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Renders a Shared Message response for GUI display.
    /// </summary>
    /// <param name="message">The message from Shared services.</param>
    /// <returns>Rendered output suitable for the UI.</returns>
    public RenderedOutput RenderMessage(Message message)
    {
        if (message.Type == MessageType.Error)
        {
            return new RenderedOutput
            {
                Type = RenderedOutputType.Error,
                Content = message.Error ?? "An unknown error occurred",
                CssClass = "output-error"
            };
        }

        var children = new List<RenderedOutput>();

        // Render the data payload
        foreach (var kvp in message.Data)
        {
            children.Add(RenderDataItem(kvp.Key, kvp.Value));
        }

        return new RenderedOutput
        {
            Type = RenderedOutputType.Text,
            Content = "",
            CssClass = "output-container",
            Children = children
        };
    }

    /// <summary>
    /// Renders a command execution result.
    /// </summary>
    /// <param name="command">The command that was executed.</param>
    /// <param name="success">Whether the command succeeded.</param>
    /// <param name="data">The result data.</param>
    /// <param name="errors">Any errors that occurred.</param>
    /// <returns>Rendered output suitable for the UI.</returns>
    public RenderedOutput RenderCommandResult(
        string command,
        bool success,
        Dictionary<string, object>? data,
        IEnumerable<string>? errors = null)
    {
        var children = new List<RenderedOutput>();

        // Header with command name
        children.Add(new RenderedOutput
        {
            Type = RenderedOutputType.Heading,
            Content = command,
            CssClass = "output-heading"
        });

        // Status indicator
        children.Add(new RenderedOutput
        {
            Type = success ? RenderedOutputType.Success : RenderedOutputType.Error,
            Content = success ? "Command completed successfully" : "Command failed",
            CssClass = success ? "output-success" : "output-error"
        });

        // Render errors if any
        if (errors != null)
        {
            foreach (var error in errors)
            {
                children.Add(new RenderedOutput
                {
                    Type = RenderedOutputType.Error,
                    Content = error,
                    CssClass = "output-error-item"
                });
            }
        }

        // Render data
        if (data != null && data.Count > 0)
        {
            children.Add(new RenderedOutput
            {
                Type = RenderedOutputType.Heading,
                Content = "Results",
                CssClass = "output-section-heading"
            });

            foreach (var kvp in data)
            {
                children.Add(RenderDataItem(kvp.Key, kvp.Value));
            }
        }

        return new RenderedOutput
        {
            Type = RenderedOutputType.Text,
            Content = "",
            CssClass = "output-container",
            Children = children
        };
    }

    /// <summary>
    /// Renders instance capabilities as a visual element.
    /// </summary>
    /// <param name="capabilities">The capabilities to render.</param>
    /// <returns>Rendered output for capabilities display.</returns>
    public RenderedOutput RenderCapabilities(InstanceCapabilities capabilities)
    {
        var children = new List<RenderedOutput>();

        // Instance info
        children.Add(new RenderedOutput
        {
            Type = RenderedOutputType.Heading,
            Content = $"Instance: {capabilities.Name}",
            CssClass = "capabilities-heading"
        });

        children.Add(new RenderedOutput
        {
            Type = RenderedOutputType.KeyValue,
            Content = $"Version: {capabilities.Version}",
            CssClass = "capabilities-item"
        });

        // Feature flags as a list
        var features = new List<RenderedOutput>();
        AddFeatureItem(features, "Encryption", capabilities.SupportsEncryption);
        AddFeatureItem(features, "Compression", capabilities.SupportsCompression);
        AddFeatureItem(features, "Metadata", capabilities.SupportsMetadata);
        AddFeatureItem(features, "Versioning", capabilities.SupportsVersioning);
        AddFeatureItem(features, "Deduplication", capabilities.SupportsDeduplication);
        AddFeatureItem(features, "RAID", capabilities.SupportsRaid);
        AddFeatureItem(features, "Replication", capabilities.SupportsReplication);
        AddFeatureItem(features, "Backup", capabilities.SupportsBackup);
        AddFeatureItem(features, "Tiering", capabilities.SupportsTiering);
        AddFeatureItem(features, "Search", capabilities.SupportsSearch);

        children.Add(new RenderedOutput
        {
            Type = RenderedOutputType.Heading,
            Content = "Features",
            CssClass = "capabilities-section-heading"
        });

        children.Add(new RenderedOutput
        {
            Type = RenderedOutputType.List,
            CssClass = "capabilities-features",
            Children = features
        });

        // Storage backends
        if (capabilities.StorageBackends.Count > 0)
        {
            children.Add(new RenderedOutput
            {
                Type = RenderedOutputType.Heading,
                Content = "Storage Backends",
                CssClass = "capabilities-section-heading"
            });

            children.Add(new RenderedOutput
            {
                Type = RenderedOutputType.List,
                CssClass = "capabilities-backends",
                Children = capabilities.StorageBackends.Select(b => new RenderedOutput
                {
                    Type = RenderedOutputType.Text,
                    Content = b,
                    CssClass = "capabilities-backend-item"
                }).ToList()
            });
        }

        // Loaded plugins
        if (capabilities.LoadedPlugins.Count > 0)
        {
            children.Add(new RenderedOutput
            {
                Type = RenderedOutputType.Heading,
                Content = $"Plugins ({capabilities.LoadedPlugins.Count})",
                CssClass = "capabilities-section-heading"
            });

            children.Add(new RenderedOutput
            {
                Type = RenderedOutputType.List,
                CssClass = "capabilities-plugins",
                Children = capabilities.LoadedPlugins.Take(10).Select(p => new RenderedOutput
                {
                    Type = RenderedOutputType.Text,
                    Content = p,
                    CssClass = "capabilities-plugin-item"
                }).ToList()
            });

            if (capabilities.LoadedPlugins.Count > 10)
            {
                children.Add(new RenderedOutput
                {
                    Type = RenderedOutputType.Info,
                    Content = $"...and {capabilities.LoadedPlugins.Count - 10} more",
                    CssClass = "capabilities-more"
                });
            }
        }

        return new RenderedOutput
        {
            Type = RenderedOutputType.Text,
            CssClass = "capabilities-container",
            Children = children
        };
    }

    /// <summary>
    /// Renders a progress indicator.
    /// </summary>
    /// <param name="message">Progress message.</param>
    /// <param name="percentage">Completion percentage (0-100).</param>
    /// <param name="isIndeterminate">Whether progress is indeterminate.</param>
    /// <returns>Rendered progress output.</returns>
    public RenderedOutput RenderProgress(string message, int percentage, bool isIndeterminate = false)
    {
        return new RenderedOutput
        {
            Type = RenderedOutputType.Progress,
            Content = message,
            CssClass = isIndeterminate ? "progress-indeterminate" : "progress-determinate",
            Metadata = new Dictionary<string, object>
            {
                ["percentage"] = percentage,
                ["indeterminate"] = isIndeterminate
            }
        };
    }

    /// <summary>
    /// Renders storage statistics as a chart-ready format.
    /// </summary>
    /// <param name="usage">Current usage in bytes.</param>
    /// <param name="capacity">Total capacity in bytes.</param>
    /// <returns>Rendered chart output.</returns>
    public RenderedOutput RenderStorageChart(long usage, long capacity)
    {
        var percentage = capacity > 0 ? (double)usage / capacity * 100 : 0;

        return new RenderedOutput
        {
            Type = RenderedOutputType.Chart,
            Content = $"{percentage:F1}% used",
            CssClass = "chart-storage",
            Metadata = new Dictionary<string, object>
            {
                ["usage"] = usage,
                ["capacity"] = capacity,
                ["percentage"] = percentage,
                ["usageFormatted"] = FormatBytes(usage),
                ["capacityFormatted"] = FormatBytes(capacity)
            }
        };
    }

    /// <summary>
    /// Renders a data item based on its type.
    /// </summary>
    private RenderedOutput RenderDataItem(string key, object? value)
    {
        if (value == null)
        {
            return new RenderedOutput
            {
                Type = RenderedOutputType.KeyValue,
                Content = $"{FormatKey(key)}: null",
                CssClass = "output-kv"
            };
        }

        // Handle collections
        if (value is IEnumerable<object> enumerable && value is not string)
        {
            var items = enumerable.Select((item, index) => RenderDataItem($"[{index}]", item)).ToList();
            return new RenderedOutput
            {
                Type = RenderedOutputType.List,
                Content = FormatKey(key),
                CssClass = "output-list",
                Children = items
            };
        }

        // Handle dictionaries
        if (value is IDictionary<string, object> dict)
        {
            var items = dict.Select(kvp => RenderDataItem(kvp.Key, kvp.Value)).ToList();
            return new RenderedOutput
            {
                Type = RenderedOutputType.List,
                Content = FormatKey(key),
                CssClass = "output-dict",
                Children = items
            };
        }

        // Handle simple values
        return new RenderedOutput
        {
            Type = RenderedOutputType.KeyValue,
            Content = $"{FormatKey(key)}: {FormatValue(value)}",
            CssClass = "output-kv"
        };
    }

    /// <summary>
    /// Adds a feature item to the features list.
    /// </summary>
    private static void AddFeatureItem(List<RenderedOutput> features, string name, bool supported)
    {
        features.Add(new RenderedOutput
        {
            Type = supported ? RenderedOutputType.Success : RenderedOutputType.Warning,
            Content = $"{name}: {(supported ? "Enabled" : "Not Available")}",
            CssClass = supported ? "feature-enabled" : "feature-disabled"
        });
    }

    /// <summary>
    /// Formats a key for display.
    /// </summary>
    private static string FormatKey(string key)
    {
        // Convert camelCase/PascalCase to Title Case with spaces
        var result = string.Concat(key.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
        return char.ToUpperInvariant(result[0]) + result[1..];
    }

    /// <summary>
    /// Formats a value for display.
    /// </summary>
    private static string FormatValue(object value)
    {
        return value switch
        {
            bool b => b ? "Yes" : "No",
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            long bytes when IsLikelyBytes(bytes) => FormatBytes(bytes),
            double d => d.ToString("N2"),
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>
    /// Heuristically determines if a number likely represents bytes.
    /// </summary>
    private static bool IsLikelyBytes(long value)
    {
        // Values over 1KB are likely bytes
        return value > 1024;
    }

    /// <summary>
    /// Formats a byte count for display.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:F1} {sizes[order]}";
    }
}
