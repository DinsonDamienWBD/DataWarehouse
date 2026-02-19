using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DataWarehouse.CLI.Integration;

/// <summary>
/// CLI scripting engine for .dw script files with variable substitution,
/// conditionals, loops, error handling, and output capture.
/// </summary>
public sealed class CliScriptingEngine
{
    private readonly ConcurrentDictionary<string, object?> _variables = new();
    private readonly List<ScriptExecutionResult> _executionLog = new();
    private readonly Func<string, Dictionary<string, object?>, CancellationToken, Task<ScriptCommandResult>> _commandExecutor;

    public CliScriptingEngine(Func<string, Dictionary<string, object?>, CancellationToken, Task<ScriptCommandResult>> commandExecutor)
    {
        _commandExecutor = commandExecutor;
    }

    /// <summary>
    /// Executes a .dw script file.
    /// </summary>
    public async Task<ScriptExecutionResult> ExecuteFileAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return new ScriptExecutionResult { Success = false, Error = $"Script file not found: {filePath}" };

        var script = await File.ReadAllTextAsync(filePath, ct);
        return await ExecuteScriptAsync(script, ct);
    }

    /// <summary>
    /// Executes a script from string content.
    /// </summary>
    public async Task<ScriptExecutionResult> ExecuteScriptAsync(string script, CancellationToken ct = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var lines = script.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var outputs = new List<string>();
        var errors = new List<string>();
        var commandsExecuted = 0;

        try
        {
            var i = 0;
            while (i < lines.Length && !ct.IsCancellationRequested)
            {
                var line = lines[i].Trim();

                // Skip comments and empty lines
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//"))
                {
                    i++;
                    continue;
                }

                // Variable assignment: $var = value
                if (TryParseVariableAssignment(line, out var varName, out var varValue))
                {
                    _variables[varName!] = SubstituteVariables(varValue!);
                    i++;
                    continue;
                }

                // If/else conditional
                if (line.StartsWith("if ", StringComparison.OrdinalIgnoreCase))
                {
                    var (skipTo, blockOutputs) = await ExecuteConditionalAsync(lines, i, ct);
                    outputs.AddRange(blockOutputs);
                    i = skipTo;
                    continue;
                }

                // Foreach loop
                if (line.StartsWith("foreach ", StringComparison.OrdinalIgnoreCase))
                {
                    var (skipTo, loopOutputs) = await ExecuteForEachAsync(lines, i, ct);
                    outputs.AddRange(loopOutputs);
                    i = skipTo;
                    continue;
                }

                // Try/catch error handling
                if (line.Equals("try", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("try:", StringComparison.OrdinalIgnoreCase))
                {
                    var (skipTo, tryOutputs, tryErrors) = await ExecuteTryCatchAsync(lines, i, ct);
                    outputs.AddRange(tryOutputs);
                    errors.AddRange(tryErrors);
                    i = skipTo;
                    continue;
                }

                // Output capture: $result = $(command args)
                if (TryParseOutputCapture(line, out var captureVar, out var captureCmd))
                {
                    var result = await ExecuteCommandAsync(captureCmd!, ct);
                    _variables[captureVar!] = result.Output;
                    commandsExecuted++;
                    i++;
                    continue;
                }

                // Regular command execution
                var substituted = SubstituteVariables(line);
                var cmdResult = await ExecuteCommandAsync(substituted, ct);
                commandsExecuted++;

                if (cmdResult.Success)
                    outputs.Add(cmdResult.Output ?? "");
                else
                    errors.Add(cmdResult.Error ?? $"Command failed: {substituted}");

                i++;
            }

            var result2 = new ScriptExecutionResult
            {
                Success = errors.Count == 0,
                CommandsExecuted = commandsExecuted,
                Outputs = outputs,
                Errors = errors,
                Duration = DateTimeOffset.UtcNow - startTime,
                Variables = new Dictionary<string, object?>(_variables)
            };
            _executionLog.Add(result2);
            return result2;
        }
        catch (Exception ex)
        {
            return new ScriptExecutionResult
            {
                Success = false,
                Error = ex.Message,
                CommandsExecuted = commandsExecuted,
                Outputs = outputs,
                Errors = errors,
                Duration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    /// <summary>
    /// Sets a script variable.
    /// </summary>
    public void SetVariable(string name, object? value) => _variables[name] = value;

    /// <summary>
    /// Gets a script variable.
    /// </summary>
    public object? GetVariable(string name) => _variables.TryGetValue(name, out var val) ? val : null;

    /// <summary>
    /// Gets execution log.
    /// </summary>
    public IReadOnlyList<ScriptExecutionResult> GetExecutionLog() => _executionLog.AsReadOnly();

    private string SubstituteVariables(string input)
    {
        return Regex.Replace(input, @"\$\{?(\w+)\}?", match =>
        {
            var name = match.Groups[1].Value;
            return _variables.TryGetValue(name, out var val) ? val?.ToString() ?? "" : match.Value;
        });
    }

    private static bool TryParseVariableAssignment(string line, out string? name, out string? value)
    {
        var match = Regex.Match(line, @"^\$(\w+)\s*=\s*(.+)$");
        if (match.Success)
        {
            name = match.Groups[1].Value;
            value = match.Groups[2].Value.Trim();
            return true;
        }
        name = null; value = null;
        return false;
    }

    private static bool TryParseOutputCapture(string line, out string? name, out string? command)
    {
        var match = Regex.Match(line, @"^\$(\w+)\s*=\s*\$\((.+)\)$");
        if (match.Success)
        {
            name = match.Groups[1].Value;
            command = match.Groups[2].Value.Trim();
            return true;
        }
        name = null; command = null;
        return false;
    }

    private async Task<(int SkipTo, List<string> Outputs)> ExecuteConditionalAsync(
        string[] lines, int startIndex, CancellationToken ct)
    {
        var outputs = new List<string>();
        var ifLine = lines[startIndex].Trim();
        var condition = ifLine[3..].Trim().TrimEnd(':');
        var conditionResult = EvaluateCondition(SubstituteVariables(condition));

        var i = startIndex + 1;
        var inElse = false;
        var depth = 0;

        while (i < lines.Length)
        {
            var line = lines[i].Trim();

            if (line.StartsWith("if ", StringComparison.OrdinalIgnoreCase)) depth++;

            if (line.Equals("else", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("else:", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0)
                {
                    inElse = true;
                    i++;
                    continue;
                }
            }

            if (line.Equals("endif", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0) return (i + 1, outputs);
                depth--;
                i++;
                continue;
            }

            var shouldExecute = (conditionResult && !inElse) || (!conditionResult && inElse);
            if (shouldExecute && !string.IsNullOrWhiteSpace(line))
            {
                var substituted = SubstituteVariables(line);
                var result = await ExecuteCommandAsync(substituted, ct);
                if (result.Output != null) outputs.Add(result.Output);
            }

            i++;
        }

        return (i, outputs);
    }

    private async Task<(int SkipTo, List<string> Outputs)> ExecuteForEachAsync(
        string[] lines, int startIndex, CancellationToken ct)
    {
        var outputs = new List<string>();
        var foreachLine = lines[startIndex].Trim();
        var match = Regex.Match(foreachLine, @"foreach\s+\$(\w+)\s+in\s+(.+?)(?:\s*:)?$", RegexOptions.IgnoreCase);

        if (!match.Success) return (startIndex + 1, outputs);

        var iterVar = match.Groups[1].Value;
        var collection = SubstituteVariables(match.Groups[2].Value.Trim());
        var items = collection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Find the end of the loop
        var bodyStart = startIndex + 1;
        var bodyEnd = bodyStart;
        var depth = 0;
        while (bodyEnd < lines.Length)
        {
            var line = lines[bodyEnd].Trim();
            if (line.StartsWith("foreach ", StringComparison.OrdinalIgnoreCase)) depth++;
            if (line.Equals("endforeach", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0) break;
                depth--;
            }
            bodyEnd++;
        }

        // Execute body for each item
        foreach (var item in items)
        {
            _variables[iterVar] = item;
            for (var j = bodyStart; j < bodyEnd; j++)
            {
                var line = lines[j].Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

                var substituted = SubstituteVariables(line);
                var result = await ExecuteCommandAsync(substituted, ct);
                if (result.Output != null) outputs.Add(result.Output);
            }
        }

        return (bodyEnd + 1, outputs);
    }

    private async Task<(int SkipTo, List<string> Outputs, List<string> Errors)> ExecuteTryCatchAsync(
        string[] lines, int startIndex, CancellationToken ct)
    {
        var outputs = new List<string>();
        var errors = new List<string>();

        var i = startIndex + 1;
        var inCatch = false;
        var tryFailed = false;

        while (i < lines.Length)
        {
            var line = lines[i].Trim();

            if (line.Equals("catch", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("catch:", StringComparison.OrdinalIgnoreCase))
            {
                inCatch = true;
                i++;
                continue;
            }

            if (line.Equals("endtry", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                return (i + 1, outputs, errors);
            }

            if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            {
                if (!inCatch && !tryFailed)
                {
                    var substituted = SubstituteVariables(line);
                    var result = await ExecuteCommandAsync(substituted, ct);
                    if (!result.Success)
                    {
                        tryFailed = true;
                        _variables["error"] = result.Error ?? "Unknown error";
                    }
                    else if (result.Output != null)
                    {
                        outputs.Add(result.Output);
                    }
                }
                else if (inCatch && tryFailed)
                {
                    var substituted = SubstituteVariables(line);
                    var result = await ExecuteCommandAsync(substituted, ct);
                    if (result.Output != null) outputs.Add(result.Output);
                }
            }

            i++;
        }

        return (i, outputs, errors);
    }

    private static bool EvaluateCondition(string condition)
    {
        // Simple condition evaluation: "value1 == value2", "value1 != value2", "value1 > value2"
        var match = Regex.Match(condition, @"(.+?)\s*(==|!=|>=|<=|>|<)\s*(.+)");
        if (!match.Success) return !string.IsNullOrWhiteSpace(condition) && condition != "false" && condition != "0";

        var left = match.Groups[1].Value.Trim().Trim('"');
        var op = match.Groups[2].Value;
        var right = match.Groups[3].Value.Trim().Trim('"');

        // Try numeric comparison
        if (double.TryParse(left, out var leftNum) && double.TryParse(right, out var rightNum))
        {
            return op switch
            {
                "==" => Math.Abs(leftNum - rightNum) < 0.001,
                "!=" => Math.Abs(leftNum - rightNum) >= 0.001,
                ">" => leftNum > rightNum,
                "<" => leftNum < rightNum,
                ">=" => leftNum >= rightNum,
                "<=" => leftNum <= rightNum,
                _ => false
            };
        }

        // String comparison
        return op switch
        {
            "==" => left == right,
            "!=" => left != right,
            _ => false
        };
    }

    private async Task<ScriptCommandResult> ExecuteCommandAsync(string command, CancellationToken ct)
    {
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new ScriptCommandResult { Success = true };

        var cmdName = parts[0];
        var argsStr = parts.Length > 1 ? parts[1] : "";
        var parameters = ParseParameters(argsStr);

        return await _commandExecutor(cmdName, parameters, ct);
    }

    private static Dictionary<string, object?> ParseParameters(string argsStr)
    {
        var parameters = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(argsStr)) return parameters;

        var parts = argsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("--"))
            {
                var key = parts[i][2..];
                if (i + 1 < parts.Length && !parts[i + 1].StartsWith("--"))
                    parameters[key] = parts[++i];
                else
                    parameters[key] = true;
            }
            else
            {
                parameters[$"arg{i}"] = parts[i];
            }
        }
        return parameters;
    }
}

/// <summary>
/// CLI connection profile manager for managing named connection profiles.
/// </summary>
public sealed class CliProfileManager
{
    private readonly ConcurrentDictionary<string, ConnectionProfile> _profiles = new();
    private string? _activeProfile;
    private readonly string _profilesPath;

    public CliProfileManager(string? profilesPath = null)
    {
        _profilesPath = profilesPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".datawarehouse", "profiles.json");
    }

    /// <summary>
    /// Creates or updates a connection profile.
    /// </summary>
    public ConnectionProfile SaveProfile(string name, string endpoint, string? authToken = null,
        Dictionary<string, string>? settings = null)
    {
        var profile = new ConnectionProfile
        {
            Name = name,
            Endpoint = endpoint,
            AuthToken = authToken,
            Settings = settings ?? new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _profiles[name] = profile;
        return profile;
    }

    /// <summary>
    /// Gets a profile by name.
    /// </summary>
    public ConnectionProfile? GetProfile(string name) =>
        _profiles.TryGetValue(name, out var profile) ? profile : null;

    /// <summary>
    /// Lists all profiles.
    /// </summary>
    public IReadOnlyList<ConnectionProfile> ListProfiles() => _profiles.Values.ToList().AsReadOnly();

    /// <summary>
    /// Switches the active profile.
    /// </summary>
    public bool SwitchProfile(string name)
    {
        if (!_profiles.ContainsKey(name)) return false;
        _activeProfile = name;
        return true;
    }

    /// <summary>
    /// Gets the active profile.
    /// </summary>
    public ConnectionProfile? GetActiveProfile() =>
        _activeProfile != null ? GetProfile(_activeProfile) : null;

    /// <summary>
    /// Deletes a profile.
    /// </summary>
    public bool DeleteProfile(string name)
    {
        if (_activeProfile == name) _activeProfile = null;
        return _profiles.TryRemove(name, out _);
    }

    /// <summary>
    /// Gets the active profile name, checking environment variable override.
    /// </summary>
    public string? GetActiveProfileName()
    {
        // Environment variable overrides configured profile
        var envProfile = Environment.GetEnvironmentVariable("DW_PROFILE");
        return envProfile ?? _activeProfile;
    }

    /// <summary>
    /// Persists profiles to disk.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_profilesPath);
        if (dir != null) Directory.CreateDirectory(dir);

        var data = System.Text.Json.JsonSerializer.Serialize(
            new ProfileStore { Profiles = _profiles.Values.ToList(), ActiveProfile = _activeProfile },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(_profilesPath, data, ct);
    }

    /// <summary>
    /// Loads profiles from disk.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_profilesPath)) return;

        var json = await File.ReadAllTextAsync(_profilesPath, ct);
        var store = System.Text.Json.JsonSerializer.Deserialize<ProfileStore>(json);
        if (store == null) return;

        foreach (var profile in store.Profiles)
            _profiles[profile.Name] = profile;

        _activeProfile = store.ActiveProfile;
    }
}

/// <summary>
/// Pipe support service for stdin/stdout integration with structured output.
/// </summary>
public sealed class PipeSupport
{
    /// <summary>
    /// Reads structured data from stdin.
    /// </summary>
    public async Task<string?> ReadStdinAsync(CancellationToken ct = default)
    {
        if (Console.IsInputRedirected)
        {
            using var reader = new StreamReader(Console.OpenStandardInput());
            return await reader.ReadToEndAsync(ct);
        }
        return null;
    }

    /// <summary>
    /// Writes structured output to stdout for piping.
    /// </summary>
    public static void WriteStructuredOutput(object data, OutputMode mode)
    {
        var output = mode switch
        {
            OutputMode.Json => System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            OutputMode.Csv => ConvertToCsv(data),
            OutputMode.Plain => data.ToString(),
            _ => System.Text.Json.JsonSerializer.Serialize(data)
        };
        Console.Out.Write(output);
    }

    /// <summary>
    /// Checks if output is being piped.
    /// </summary>
    public static bool IsOutputPiped => Console.IsOutputRedirected;

    /// <summary>
    /// Checks if input is being piped.
    /// </summary>
    public static bool IsInputPiped => Console.IsInputRedirected;

    private static string ConvertToCsv(object data)
    {
        if (data is System.Collections.IEnumerable enumerable)
        {
            var sb = new System.Text.StringBuilder();
            var first = true;
            foreach (var item in enumerable)
            {
                var props = item?.GetType().GetProperties() ?? Array.Empty<System.Reflection.PropertyInfo>();
                if (first)
                {
                    sb.AppendLine(string.Join(",", props.Select(p => p.Name)));
                    first = false;
                }
                sb.AppendLine(string.Join(",", props.Select(p => $"\"{p.GetValue(item)}\"")));
            }
            return sb.ToString();
        }
        return data?.ToString() ?? "";
    }
}

#region Models

public sealed record ScriptCommandResult
{
    public bool Success { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
}

public sealed record ScriptExecutionResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int CommandsExecuted { get; init; }
    public List<string> Outputs { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public TimeSpan Duration { get; init; }
    public Dictionary<string, object?> Variables { get; init; } = new();
}

public sealed record ConnectionProfile
{
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public string? AuthToken { get; init; }
    public Dictionary<string, string> Settings { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ProfileStore
{
    public List<ConnectionProfile> Profiles { get; init; } = new();
    public string? ActiveProfile { get; init; }
}

public enum OutputMode { Json, Csv, Plain, Table }

#endregion
