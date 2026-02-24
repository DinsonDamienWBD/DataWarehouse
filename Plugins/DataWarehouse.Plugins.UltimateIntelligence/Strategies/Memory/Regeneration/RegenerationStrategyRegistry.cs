using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Regeneration;

/// <summary>
/// Registry for regeneration strategies with automatic format detection and strategy selection.
/// Provides centralized management of all regeneration strategies with capability assessment.
/// </summary>
public sealed class RegenerationStrategyRegistry
{
    private readonly BoundedDictionary<string, IAdvancedRegenerationStrategy> _strategies = new BoundedDictionary<string, IAdvancedRegenerationStrategy>(1000);
    private readonly BoundedDictionary<string, List<string>> _formatToStrategies = new BoundedDictionary<string, List<string>>(1000);
    private readonly object _registrationLock = new();

    /// <summary>
    /// Gets the singleton instance of the registry.
    /// </summary>
    public static RegenerationStrategyRegistry Instance { get; } = new();

    /// <summary>
    /// Initializes a new registry with default strategies.
    /// </summary>
    public RegenerationStrategyRegistry()
    {
        RegisterDefaultStrategies();
    }

    /// <summary>
    /// Registers a regeneration strategy.
    /// </summary>
    /// <param name="strategy">The strategy to register.</param>
    public void Register(IAdvancedRegenerationStrategy strategy)
    {
        lock (_registrationLock)
        {
            _strategies[strategy.StrategyId] = strategy;

            foreach (var format in strategy.SupportedFormats)
            {
                var normalizedFormat = format.ToLowerInvariant();
                if (!_formatToStrategies.ContainsKey(normalizedFormat))
                {
                    _formatToStrategies[normalizedFormat] = new List<string>();
                }
                if (!_formatToStrategies[normalizedFormat].Contains(strategy.StrategyId))
                {
                    _formatToStrategies[normalizedFormat].Add(strategy.StrategyId);
                }
            }
        }
    }

    /// <summary>
    /// Unregisters a regeneration strategy.
    /// </summary>
    /// <param name="strategyId">The strategy ID to unregister.</param>
    /// <returns>True if the strategy was removed, false otherwise.</returns>
    public bool Unregister(string strategyId)
    {
        lock (_registrationLock)
        {
            if (_strategies.TryRemove(strategyId, out var strategy))
            {
                foreach (var format in strategy.SupportedFormats)
                {
                    var normalizedFormat = format.ToLowerInvariant();
                    if (_formatToStrategies.TryGetValue(normalizedFormat, out var strategies))
                    {
                        strategies.Remove(strategyId);
                    }
                }
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Gets a strategy by its ID.
    /// </summary>
    /// <param name="strategyId">The strategy ID.</param>
    /// <returns>The strategy, or null if not found.</returns>
    public IAdvancedRegenerationStrategy? GetStrategy(string strategyId)
    {
        return _strategies.GetValueOrDefault(strategyId);
    }

    /// <summary>
    /// Gets all strategies that support a given format.
    /// </summary>
    /// <param name="format">The format to find strategies for.</param>
    /// <returns>List of strategies supporting the format.</returns>
    public IReadOnlyList<IAdvancedRegenerationStrategy> GetStrategiesForFormat(string format)
    {
        var normalizedFormat = format.ToLowerInvariant();
        if (_formatToStrategies.TryGetValue(normalizedFormat, out var strategyIds))
        {
            return strategyIds
                .Select(id => _strategies.GetValueOrDefault(id))
                .Where(s => s != null)
                .Cast<IAdvancedRegenerationStrategy>()
                .ToList();
        }
        return Array.Empty<IAdvancedRegenerationStrategy>();
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    /// <returns>All registered strategies.</returns>
    public IReadOnlyList<IAdvancedRegenerationStrategy> GetAllStrategies()
    {
        return _strategies.Values.ToList();
    }

    /// <summary>
    /// Gets all supported formats across all strategies.
    /// </summary>
    /// <returns>Set of supported formats.</returns>
    public IReadOnlySet<string> GetSupportedFormats()
    {
        return _formatToStrategies.Keys.ToHashSet();
    }

    /// <summary>
    /// Auto-detects the format of encoded content and returns the best matching strategy.
    /// </summary>
    /// <param name="context">The encoded context to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The best matching strategy and format, or null if none found.</returns>
    public async Task<(IAdvancedRegenerationStrategy? Strategy, string Format)?> AutoDetectStrategyAsync(
        EncodedContext context,
        CancellationToken ct = default)
    {
        var data = context.EncodedData;
        var detectedFormat = DetectFormat(data);

        if (!string.IsNullOrEmpty(detectedFormat))
        {
            var strategies = GetStrategiesForFormat(detectedFormat);
            if (strategies.Count > 0)
            {
                // Assess capabilities to find best strategy
                var assessments = new List<(IAdvancedRegenerationStrategy Strategy, RegenerationCapability Capability)>();

                foreach (var strategy in strategies)
                {
                    var capability = await strategy.AssessCapabilityAsync(context, ct);
                    if (capability.CanRegenerate)
                    {
                        assessments.Add((strategy, capability));
                    }
                }

                if (assessments.Count > 0)
                {
                    var best = assessments
                        .OrderByDescending(a => a.Capability.ExpectedAccuracy)
                        .ThenByDescending(a => a.Capability.AssessmentConfidence)
                        .First();

                    return (best.Strategy, detectedFormat);
                }
            }
        }

        // Fallback: assess all strategies
        var allAssessments = new List<(IAdvancedRegenerationStrategy Strategy, RegenerationCapability Capability, string Format)>();

        foreach (var strategy in _strategies.Values)
        {
            var capability = await strategy.AssessCapabilityAsync(context, ct);
            if (capability.CanRegenerate)
            {
                allAssessments.Add((strategy, capability, capability.DetectedContentType));
            }
        }

        if (allAssessments.Count > 0)
        {
            var best = allAssessments
                .OrderByDescending(a => a.Capability.ExpectedAccuracy)
                .ThenByDescending(a => a.Capability.AssessmentConfidence)
                .First();

            return (best.Strategy, best.Format);
        }

        return null;
    }

    /// <summary>
    /// Gets capability assessments from all strategies for the given context.
    /// </summary>
    /// <param name="context">The encoded context to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of strategy capabilities ordered by expected accuracy.</returns>
    public async Task<IReadOnlyList<(IAdvancedRegenerationStrategy Strategy, RegenerationCapability Capability)>> AssessAllStrategiesAsync(
        EncodedContext context,
        CancellationToken ct = default)
    {
        var results = new List<(IAdvancedRegenerationStrategy Strategy, RegenerationCapability Capability)>();

        var tasks = _strategies.Values.Select(async strategy =>
        {
            try
            {
                var capability = await strategy.AssessCapabilityAsync(context, ct);
                return (Strategy: strategy, Capability: capability);
            }
            catch
            {
                Debug.WriteLine($"Caught exception in RegenerationStrategyRegistry.cs");
                return (Strategy: strategy, Capability: new RegenerationCapability { CanRegenerate = false });
            }
        });

        var assessments = await Task.WhenAll(tasks);

        return assessments
            .Where(a => a.Capability.CanRegenerate)
            .OrderByDescending(a => a.Capability.ExpectedAccuracy)
            .ThenByDescending(a => a.Capability.AssessmentConfidence)
            .ToList();
    }

    /// <summary>
    /// Detects the format of the given data.
    /// </summary>
    /// <param name="data">The data to analyze.</param>
    /// <returns>Detected format, or empty string if unknown.</returns>
    public static string DetectFormat(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return "";

        // JSON detection
        if ((data.TrimStart().StartsWith("{") || data.TrimStart().StartsWith("[")) &&
            (data.Contains("\":") || data.Contains("\":")))
            return "json";

        // XML detection
        if (data.TrimStart().StartsWith("<?xml") || data.TrimStart().StartsWith("<") && data.Contains("</"))
            return "xml";

        // SQL detection
        if (Regex.IsMatch(data, @"^\s*(SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP)\b", RegexOptions.IgnoreCase))
            return "sql";

        // Markdown detection
        if (Regex.IsMatch(data, @"^#{1,6}\s+", RegexOptions.Multiline) ||
            (data.Contains("```") && data.Contains("```")))
            return "markdown";

        // CSV/TSV detection
        if (Regex.IsMatch(data, @"^[^,\t\n]+[,\t][^,\t\n]+([,\t][^,\t\n]+)*\n", RegexOptions.Multiline))
        {
            var firstLine = data.Split('\n')[0];
            return firstLine.Contains('\t') ? "tsv" : "csv";
        }

        // YAML detection
        if (Regex.IsMatch(data, @"^\s*\w+:\s*", RegexOptions.Multiline) &&
            !data.TrimStart().StartsWith("{"))
            return "yaml";

        // TOML detection
        if (Regex.IsMatch(data, @"^\s*\[\[?\w+", RegexOptions.Multiline) &&
            Regex.IsMatch(data, @"\w+\s*=\s*""[^""]*"""))
            return "toml";

        // INI detection
        if (Regex.IsMatch(data, @"^\s*\[\w+\]\s*$", RegexOptions.Multiline) &&
            Regex.IsMatch(data, @"^\s*\w+\s*=", RegexOptions.Multiline))
            return "ini";

        // .env detection
        if (Regex.IsMatch(data, @"^[A-Z_][A-Z0-9_]*=", RegexOptions.Multiline))
            return "env";

        // Protobuf detection
        if (Regex.IsMatch(data, @"\bmessage\s+\w+\s*\{") ||
            Regex.IsMatch(data, @"\b(string|int32|int64|bool)\s+\w+\s*=\s*\d+"))
            return "protobuf";

        // Cypher/Graph detection
        if (Regex.IsMatch(data, @"\(\w+\)-\[.*?\]->") ||
            Regex.IsMatch(data, @"\.addV\(|\.addE\("))
            return "cypher";

        // Time series detection
        if (Regex.IsMatch(data, @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}") &&
            Regex.IsMatch(data, @",\s*[\d.]+\s*$", RegexOptions.Multiline))
            return "timeseries";

        // Code detection
        if (Regex.IsMatch(data, @"\bfunction\s+\w+|def\s+\w+|class\s+\w+|public\s+(class|void|static)"))
        {
            // Try to detect specific language
            if (Regex.IsMatch(data, @"\bnamespace\s+\w+|using\s+System")) return "csharp";
            if (Regex.IsMatch(data, @"^def\s+\w+\s*\(", RegexOptions.Multiline)) return "python";
            if (Regex.IsMatch(data, @"\bconst\s+\w+\s*=|let\s+\w+\s*=")) return "javascript";
            if (Regex.IsMatch(data, @"^package\s+\w+", RegexOptions.Multiline)) return "go";
            if (Regex.IsMatch(data, @"\bfn\s+\w+\s*\(")) return "rust";
            return "code";
        }

        return "";
    }

    private void RegisterDefaultStrategies()
    {
        Register(new SqlRegenerationStrategy());
        Register(new JsonSchemaRegenerationStrategy());
        Register(new TabularDataRegenerationStrategy());
        Register(new XmlDocumentRegenerationStrategy());
        Register(new MarkdownDocumentRegenerationStrategy());
        Register(new CodeRegenerationStrategy());
        Register(new TimeSeriesRegenerationStrategy());
        Register(new GraphDataRegenerationStrategy());
        Register(new ProtobufRegenerationStrategy());
        Register(new AvroRegenerationStrategy());
        Register(new ParquetRegenerationStrategy());
        Register(new ConfigurationRegenerationStrategy());
    }

    /// <summary>
    /// Gets registry statistics.
    /// </summary>
    public RegistryStatistics GetStatistics()
    {
        return new RegistryStatistics
        {
            RegisteredStrategies = _strategies.Count,
            SupportedFormats = _formatToStrategies.Count,
            StrategyDetails = _strategies.Values.Select(s => new StrategyDetail
            {
                StrategyId = s.StrategyId,
                DisplayName = s.DisplayName,
                SupportedFormats = s.SupportedFormats,
                ExpectedAccuracy = s.ExpectedAccuracy
            }).ToList()
        };
    }
}

/// <summary>
/// Statistics for the regeneration strategy registry.
/// </summary>
public sealed record RegistryStatistics
{
    /// <summary>Number of registered strategies.</summary>
    public int RegisteredStrategies { get; init; }

    /// <summary>Number of supported formats.</summary>
    public int SupportedFormats { get; init; }

    /// <summary>Details for each registered strategy.</summary>
    public List<StrategyDetail> StrategyDetails { get; init; } = new();
}

/// <summary>
/// Details for a single strategy.
/// </summary>
public sealed record StrategyDetail
{
    /// <summary>Strategy ID.</summary>
    public string StrategyId { get; init; } = "";

    /// <summary>Display name.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Supported formats.</summary>
    public string[] SupportedFormats { get; init; } = Array.Empty<string>();

    /// <summary>Expected accuracy.</summary>
    public double ExpectedAccuracy { get; init; }
}
