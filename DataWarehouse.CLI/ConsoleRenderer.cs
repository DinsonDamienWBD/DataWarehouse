// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Commands;
using DataWarehouse.Shared.Services;
using Spectre.Console;

namespace DataWarehouse.CLI;

/// <summary>
/// Renders CommandResult objects to the console using Spectre.Console.
/// This is the presentation layer - all business logic is in Shared.
/// </summary>
public sealed class ConsoleRenderer
{
    private readonly OutputFormatter _formatter = new();

    /// <summary>
    /// Renders a command result to the console.
    /// </summary>
    /// <param name="result">The command result to render.</param>
    /// <param name="format">Output format preference.</param>
    public void Render(CommandResult result, OutputFormat format = OutputFormat.Table)
    {
        if (!result.Success)
        {
            RenderError(result);
            return;
        }

        // For non-table formats, use OutputFormatter
        if (format != OutputFormat.Table)
        {
            var output = _formatter.Format(result, format);
            AnsiConsole.WriteLine(output);
            return;
        }

        // Render based on data type
        switch (result.DataType)
        {
            case ResultDataType.Table:
                RenderTable(result);
                break;

            case ResultDataType.Tree:
            case ResultDataType.KeyValue:
                RenderTree(result);
                break;

            case ResultDataType.None:
                if (!string.IsNullOrEmpty(result.Message))
                {
                    AnsiConsole.MarkupLine($"[green]{EscapeMarkup(result.Message)}[/]");
                }
                break;

            default:
                RenderObject(result);
                break;
        }
    }

    /// <summary>
    /// Renders an error result.
    /// </summary>
    private static void RenderError(CommandResult result)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {EscapeMarkup(result.Error ?? "Unknown error")}");

        if (result.Exception != null)
        {
            AnsiConsole.WriteException(result.Exception, ExceptionFormats.ShortenEverything);
        }
    }

    /// <summary>
    /// Renders tabular data.
    /// </summary>
    private static void RenderTable(CommandResult result)
    {
        if (!string.IsNullOrEmpty(result.Message))
        {
            AnsiConsole.MarkupLine($"[bold]{EscapeMarkup(result.Message)}[/]\n");
        }

        if (result.Data is not IEnumerable<object> items)
        {
            RenderObject(result);
            return;
        }

        var itemList = items.ToList();
        if (itemList.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No data to display.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);

        // Get columns from first item
        var firstItem = itemList[0];
        var columns = OutputFormatter.GetColumns(firstItem).ToList();

        foreach (var col in columns)
        {
            table.AddColumn(FormatColumnName(col));
        }

        // Add rows
        foreach (var item in itemList)
        {
            var values = OutputFormatter.GetValues(item).ToList();
            var formattedValues = new List<string>();

            for (int i = 0; i < columns.Count && i < values.Count; i++)
            {
                var value = values[i];
                var colName = columns[i].ToLowerInvariant();

                // Apply color coding based on column semantics
                if (colName.Contains("status"))
                {
                    value = ColorizeStatus(value);
                }
                else if (colName.Contains("health") || colName == "ishealthy")
                {
                    value = ColorizeHealth(value);
                }
                else if (colName.Contains("enabled") || colName == "isenabled")
                {
                    value = ColorizeEnabled(value);
                }
                else if (colName.Contains("percent") || colName.Contains("utilization"))
                {
                    value = ColorizePercent(value);
                }
                else if (colName.Contains("severity"))
                {
                    value = ColorizeSeverity(value);
                }

                formattedValues.Add(value);
            }

            table.AddRow(formattedValues.ToArray());
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Renders tree-structured data.
    /// </summary>
    private static void RenderTree(CommandResult result)
    {
        if (!string.IsNullOrEmpty(result.Message))
        {
            AnsiConsole.MarkupLine($"[bold]{EscapeMarkup(result.Message)}[/]\n");
        }

        if (result.Data == null)
        {
            return;
        }

        // Build tree from object properties
        var tree = new Tree($"[bold green]{EscapeMarkup(result.Message ?? "Result")}[/]");
        AddTreeNodes(tree, result.Data);
        AnsiConsole.Write(tree);
    }

    private static void AddTreeNodes(IHasTreeNodes parent, object obj)
    {
        var type = obj.GetType();
        var props = type.GetProperties().Where(p => p.CanRead);

        foreach (var prop in props)
        {
            var value = prop.GetValue(obj);

            if (value == null)
                continue;

            var propType = prop.PropertyType;

            if (propType.IsPrimitive || propType == typeof(string) || propType == typeof(DateTime))
            {
                var displayValue = FormatPropertyValue(prop.Name, value);
                parent.AddNode($"[bold]{FormatColumnName(prop.Name)}:[/] {displayValue}");
            }
            else if (!propType.IsGenericType)
            {
                var node = parent.AddNode($"[bold]{FormatColumnName(prop.Name)}[/]");
                AddTreeNodes(node, value);
            }
        }
    }

    /// <summary>
    /// Renders a generic object as a panel.
    /// </summary>
    private static void RenderObject(CommandResult result)
    {
        if (result.Data == null)
        {
            if (!string.IsNullOrEmpty(result.Message))
            {
                AnsiConsole.MarkupLine($"[green]{EscapeMarkup(result.Message)}[/]");
            }
            return;
        }

        var type = result.Data.GetType();
        var props = type.GetProperties().Where(p => p.CanRead && !p.PropertyType.IsGenericType).ToList();

        if (props.Count == 0)
        {
            // Anonymous or simple object - use JSON
            var json = System.Text.Json.JsonSerializer.Serialize(result.Data,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            AnsiConsole.WriteLine(json);
            return;
        }

        var lines = new List<string>();

        foreach (var prop in props)
        {
            var value = prop.GetValue(result.Data);
            var displayValue = FormatPropertyValue(prop.Name, value);
            lines.Add($"[bold]{FormatColumnName(prop.Name)}:[/] {displayValue}");
        }

        var panel = new Panel(new Markup(string.Join("\n", lines)))
        {
            Header = new PanelHeader(result.Message ?? "Result"),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);
    }

    /// <summary>
    /// Renders a status message with a spinner.
    /// </summary>
    public static async Task<T> WithStatusAsync<T>(string message, Func<Task<T>> action)
    {
        return await AnsiConsole.Status()
            .StartAsync(message, async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                return await action();
            });
    }

    /// <summary>
    /// Renders progress for a multi-step operation.
    /// </summary>
    public static async Task WithProgressAsync(string[] steps, Func<string, Task> stepAction)
    {
        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                foreach (var step in steps)
                {
                    var task = ctx.AddTask(step);
                    await stepAction(step);

                    while (!task.IsFinished)
                    {
                        task.Increment(10);
                        await Task.Delay(50);
                    }
                }
            });
    }

    /// <summary>
    /// Prompts for confirmation.
    /// </summary>
    public static bool Confirm(string message, bool defaultValue = false)
    {
        return AnsiConsole.Confirm(message, defaultValue);
    }

    /// <summary>
    /// Prompts for text input.
    /// </summary>
    public static string Prompt(string message, string? defaultValue = null)
    {
        var prompt = new TextPrompt<string>(message);

        if (defaultValue != null)
        {
            prompt.DefaultValue(defaultValue);
        }

        return AnsiConsole.Prompt(prompt);
    }

    private static string FormatColumnName(string name)
    {
        // Convert PascalCase to Title Case with spaces
        return System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
    }

    private static string FormatPropertyValue(string propName, object? value)
    {
        if (value == null)
            return "[grey]null[/]";

        var propLower = propName.ToLowerInvariant();

        // Special formatting based on property name
        if (propLower.Contains("capacity") || propLower.Contains("size") || propLower.Contains("used") || propLower.Contains("memory") || propLower.Contains("disk"))
        {
            if (value is long bytes)
            {
                return $"[cyan]{OutputFormatter.FormatBytes(bytes)}[/]";
            }
        }

        if ((propLower.Contains("duration") || propLower.Contains("time")) && value is double seconds)
        {
            return $"[cyan]{OutputFormatter.FormatDuration(seconds)}[/]";
        }

        if (propLower.Contains("percent") || propLower.Contains("utilization"))
        {
            if (value is double percent)
            {
                var (formatted, color) = OutputFormatter.FormatPercentage(percent);
                return $"[{color}]{formatted}[/]";
            }
        }

        if (propLower.Contains("status"))
        {
            return ColorizeStatus(value.ToString() ?? "");
        }

        if (propLower == "ishealthy" || propLower.Contains("healthy"))
        {
            return ColorizeHealth(value.ToString() ?? "");
        }

        if (propLower == "isenabled" || propLower.Contains("enabled"))
        {
            return ColorizeEnabled(value.ToString() ?? "");
        }

        return $"[cyan]{EscapeMarkup(value.ToString() ?? "")}[/]";
    }

    private static string ColorizeStatus(string value)
    {
        var lower = value.ToLowerInvariant();
        var color = lower switch
        {
            "healthy" or "active" or "running" or "online" or "verified" or "completed" or "success" => "green",
            "degraded" or "warning" or "rebuilding" or "initializing" or "inprogress" => "yellow",
            "unhealthy" or "offline" or "failed" or "error" or "critical" => "red",
            _ => "white"
        };

        return $"[{color}]{EscapeMarkup(value)}[/]";
    }

    private static string ColorizeHealth(string value)
    {
        var lower = value.ToLowerInvariant();
        var color = lower == "true" || lower == "healthy" ? "green" : "red";
        var display = lower == "true" ? "Healthy" : lower == "false" ? "Unhealthy" : value;
        return $"[{color}]{display}[/]";
    }

    private static string ColorizeEnabled(string value)
    {
        var lower = value.ToLowerInvariant();
        var color = lower == "true" || lower == "enabled" ? "green" : "grey";
        var display = lower == "true" ? "Enabled" : lower == "false" ? "Disabled" : value;
        return $"[{color}]{display}[/]";
    }

    private static string ColorizePercent(string value)
    {
        if (double.TryParse(value.TrimEnd('%'), out var percent))
        {
            var (_, color) = OutputFormatter.FormatPercentage(percent);
            return $"[{color}]{value}[/]";
        }
        return value;
    }

    private static string ColorizeSeverity(string value)
    {
        var lower = value.ToLowerInvariant();
        var color = lower switch
        {
            "critical" => "red",
            "warning" => "yellow",
            "info" => "blue",
            _ => "white"
        };
        return $"[{color}]{EscapeMarkup(value)}[/]";
    }

    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}
