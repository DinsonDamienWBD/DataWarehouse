// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using DataWarehouse.Shared.Commands;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Formats CommandResult data into various output formats (JSON, YAML, CSV, Table).
/// This is the Shared layer formatter - CLI uses ConsoleRenderer to render these.
/// </summary>
public sealed class OutputFormatter
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly ISerializer _yamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Formats a CommandResult to the specified output format.
    /// </summary>
    /// <param name="result">The command result to format.</param>
    /// <param name="format">The desired output format.</param>
    /// <returns>Formatted string representation.</returns>
    public string Format(CommandResult result, OutputFormat format)
    {
        if (result.Data == null)
        {
            return result.Message ?? (result.Success ? "OK" : result.Error ?? "Error");
        }

        return format switch
        {
            OutputFormat.Json => FormatJson(result),
            OutputFormat.Yaml => FormatYaml(result),
            OutputFormat.Csv => FormatCsv(result),
            OutputFormat.Table => FormatPlainText(result),
            _ => FormatJson(result)
        };
    }

    /// <summary>
    /// Formats result data as JSON.
    /// </summary>
    public string FormatJson(CommandResult result)
    {
        var output = new
        {
            success = result.Success,
            message = result.Message,
            error = result.Error,
            data = result.Data
        };

        return JsonSerializer.Serialize(output, _jsonOptions);
    }

    /// <summary>
    /// Formats result data as YAML.
    /// </summary>
    public string FormatYaml(CommandResult result)
    {
        var output = new Dictionary<string, object?>
        {
            ["success"] = result.Success,
            ["message"] = result.Message,
            ["error"] = result.Error,
            ["data"] = result.Data
        };

        return _yamlSerializer.Serialize(output);
    }

    /// <summary>
    /// Formats result data as CSV.
    /// </summary>
    public string FormatCsv(CommandResult result)
    {
        if (result.Data is not IEnumerable<object> items)
        {
            // Single object - convert to single row
            return FormatObjectAsCsv(result.Data);
        }

        var sb = new StringBuilder();
        var first = true;

        foreach (var item in items)
        {
            if (first)
            {
                // Write header row
                sb.AppendLine(GetCsvHeader(item));
                first = false;
            }

            sb.AppendLine(GetCsvRow(item));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats result data as plain text.
    /// </summary>
    public string FormatPlainText(CommandResult result)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(result.Message))
        {
            sb.AppendLine(result.Message);
            sb.AppendLine();
        }

        if (result.Data != null)
        {
            // Use JSON for plain text output (readable)
            sb.Append(JsonSerializer.Serialize(result.Data, _jsonOptions));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets column names from an object for table rendering.
    /// </summary>
    public static IEnumerable<string> GetColumns(object obj)
    {
        var type = obj.GetType();
        return type.GetProperties()
            .Where(p => p.CanRead && !p.PropertyType.IsGenericType)
            .Select(p => p.Name);
    }

    /// <summary>
    /// Gets column values from an object for table rendering.
    /// </summary>
    public static IEnumerable<string> GetValues(object obj)
    {
        var type = obj.GetType();
        return type.GetProperties()
            .Where(p => p.CanRead && !p.PropertyType.IsGenericType)
            .Select(p =>
            {
                var value = p.GetValue(obj);
                return FormatValue(value);
            });
    }

    private static string FormatObjectAsCsv(object? obj)
    {
        if (obj == null)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine(GetCsvHeader(obj));
        sb.AppendLine(GetCsvRow(obj));
        return sb.ToString();
    }

    private static string GetCsvHeader(object? obj)
    {
        if (obj == null)
            return "";

        var props = obj.GetType().GetProperties()
            .Where(p => p.CanRead && !p.PropertyType.IsGenericType);

        return string.Join(",", props.Select(p => EscapeCsv(p.Name)));
    }

    private static string GetCsvRow(object? obj)
    {
        if (obj == null)
            return "";

        var props = obj.GetType().GetProperties()
            .Where(p => p.CanRead && !p.PropertyType.IsGenericType);

        return string.Join(",", props.Select(p => EscapeCsv(FormatValue(p.GetValue(obj)))));
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "",
            DateTime dt => dt.ToString("O"),
            TimeSpan ts => ts.ToString(),
            bool b => b ? "true" : "false",
            long bytes when IsLikelyBytes(bytes) => FormatBytes(bytes),
            _ => value.ToString() ?? ""
        };
    }

    private static bool IsLikelyBytes(long value)
    {
        // Heuristic: if value is large and divisible by 1024, likely bytes
        return value > 1024 && value % 1024 == 0;
    }

    /// <summary>
    /// Formats bytes into human-readable format.
    /// </summary>
    public static string FormatBytes(long bytes)
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

    /// <summary>
    /// Formats a duration in seconds to human-readable format.
    /// </summary>
    public static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);

        if (ts.TotalDays >= 1)
            return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";

        return $"{ts.TotalSeconds:F1}s";
    }

    /// <summary>
    /// Formats a percentage with color hints for CLI.
    /// </summary>
    public static (string value, string color) FormatPercentage(double percent)
    {
        var color = percent switch
        {
            < 50 => "green",
            < 80 => "yellow",
            _ => "red"
        };

        return ($"{percent:F1}%", color);
    }
}
