using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataPrivacy.Strategies.DifferentialPrivacy;

/// <summary>
/// Enhanced epsilon/delta tracking with privacy budget accounting.
/// Tracks cumulative privacy loss across queries using advanced composition theorems.
/// </summary>
public sealed class EpsilonDeltaTrackingStrategy : DataPrivacyStrategyBase
{
    private readonly BoundedDictionary<string, PrivacyBudget> _budgets = new BoundedDictionary<string, PrivacyBudget>(1000);
    private readonly BoundedDictionary<string, List<PrivacyQuery>> _queryHistory = new BoundedDictionary<string, List<PrivacyQuery>>(1000);
    // P2-2517: Per-dataset lock serialises budget check + update so concurrent queries cannot both
    // pass the budget check and overshoot the epsilon/delta totals.
    private readonly BoundedDictionary<string, object> _budgetLocks = new BoundedDictionary<string, object>(1000);

    public override string StrategyId => "epsilon-delta-tracking";
    public override string DisplayName => "Epsilon-Delta Tracking";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new()
    {
        SupportsAsync = true, SupportsBatch = true,
        SupportsReversible = false, SupportsFormatPreserving = false
    };
    public override string SemanticDescription =>
        "Track epsilon and delta privacy parameters across queries with composition accounting";
    public override string[] Tags => ["epsilon", "delta", "tracking", "composition", "accounting"];

    /// <summary>
    /// Initializes a privacy budget for a dataset.
    /// </summary>
    public PrivacyBudget InitializeBudget(string datasetId, double totalEpsilon, double totalDelta)
    {
        // Cat 14 (finding 2519): validate DP parameters — zero/negative epsilon produces nonsensical results.
        if (totalEpsilon <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalEpsilon), totalEpsilon,
                "Differential privacy epsilon must be positive (ε > 0).");
        if (totalDelta < 0)
            throw new ArgumentOutOfRangeException(nameof(totalDelta), totalDelta,
                "Differential privacy delta must be non-negative (δ ≥ 0).");

        var budget = new PrivacyBudget
        {
            DatasetId = datasetId,
            TotalEpsilon = totalEpsilon,
            TotalDelta = totalDelta,
            ConsumedEpsilon = 0,
            ConsumedDelta = 0,
            RemainingEpsilon = totalEpsilon,
            RemainingDelta = totalDelta,
            QueryCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            IsExhausted = false
        };
        _budgets[datasetId] = budget;
        return budget;
    }

    /// <summary>
    /// Records a privacy-consuming query and updates the budget using advanced composition.
    /// </summary>
    public PrivacyQueryResult ConsumePrivacy(string datasetId, double queryEpsilon, double queryDelta, string queryDescription)
    {
        // P2-2517: Serialise entire check+update under a per-dataset lock to prevent concurrent
        // callers from both passing the budget check and overspending the privacy budget.
        var datasetLock = _budgetLocks.GetOrAdd(datasetId, _ => new object());
        lock (datasetLock)
        {

        if (!_budgets.TryGetValue(datasetId, out var budget))
            return new PrivacyQueryResult { Allowed = false, Reason = "No budget initialized" };

        if (budget.IsExhausted)
            return new PrivacyQueryResult { Allowed = false, Reason = "Privacy budget exhausted" };

        // Use advanced composition theorem for tighter bounds
        var composedEpsilon = ComputeAdvancedComposition(
            budget.ConsumedEpsilon, queryEpsilon, budget.QueryCount + 1, budget.TotalDelta);

        if (composedEpsilon > budget.TotalEpsilon)
            return new PrivacyQueryResult
            {
                Allowed = false,
                Reason = $"Query would exceed epsilon budget: {composedEpsilon:F4} > {budget.TotalEpsilon:F4}"
            };

        var newDelta = budget.ConsumedDelta + queryDelta;
        if (newDelta > budget.TotalDelta)
            return new PrivacyQueryResult
            {
                Allowed = false,
                Reason = $"Query would exceed delta budget: {newDelta:F8} > {budget.TotalDelta:F8}"
            };

        // Record the query
        var query = new PrivacyQuery
        {
            QueryId = Guid.NewGuid().ToString("N")[..12],
            DatasetId = datasetId,
            Epsilon = queryEpsilon,
            Delta = queryDelta,
            Description = queryDescription,
            Timestamp = DateTimeOffset.UtcNow
        };

        // P2-2516: Use GetOrAdd to ensure a single canonical list; then mutate under lock.
        // AddOrUpdate add-factory may be called multiple times under contention, dropping entries.
        var historyList = _queryHistory.GetOrAdd(datasetId, _ => new List<PrivacyQuery>());
        lock (historyList) { historyList.Add(query); }

        // Update budget
        var updatedBudget = budget with
        {
            ConsumedEpsilon = composedEpsilon,
            ConsumedDelta = newDelta,
            RemainingEpsilon = budget.TotalEpsilon - composedEpsilon,
            RemainingDelta = budget.TotalDelta - newDelta,
            QueryCount = budget.QueryCount + 1,
            IsExhausted = composedEpsilon >= budget.TotalEpsilon * 0.99
        };
        _budgets[datasetId] = updatedBudget;

        return new PrivacyQueryResult
        {
            Allowed = true,
            QueryId = query.QueryId,
            ConsumedEpsilon = composedEpsilon,
            ConsumedDelta = newDelta,
            RemainingEpsilon = updatedBudget.RemainingEpsilon,
            RemainingDelta = updatedBudget.RemainingDelta,
            BudgetUtilization = composedEpsilon / budget.TotalEpsilon
        };

        } // end lock(datasetLock)
    }

    /// <summary>
    /// Gets current budget status for a dataset.
    /// </summary>
    public PrivacyBudget? GetBudget(string datasetId) =>
        _budgets.TryGetValue(datasetId, out var budget) ? budget : null;

    /// <summary>
    /// Gets query history for a dataset.
    /// </summary>
    public IReadOnlyList<PrivacyQuery> GetQueryHistory(string datasetId) =>
        _queryHistory.TryGetValue(datasetId, out var history) ? history.AsReadOnly() : Array.Empty<PrivacyQuery>();

    /// <summary>
    /// Resets budget (for periodic renewal).
    /// </summary>
    public bool ResetBudget(string datasetId)
    {
        if (!_budgets.TryGetValue(datasetId, out var budget)) return false;
        _budgets[datasetId] = budget with
        {
            ConsumedEpsilon = 0,
            ConsumedDelta = 0,
            RemainingEpsilon = budget.TotalEpsilon,
            RemainingDelta = budget.TotalDelta,
            QueryCount = 0,
            IsExhausted = false
        };
        return true;
    }

    /// <summary>
    /// Advanced composition theorem for tighter epsilon bounds.
    /// Uses the strong composition theorem: epsilon_total = sqrt(2k * ln(1/delta')) * epsilon + k * epsilon * (e^epsilon - 1)
    /// </summary>
    private static double ComputeAdvancedComposition(double currentEpsilon, double queryEpsilon, int queryCount, double totalDelta)
    {
        if (queryCount <= 1) return queryEpsilon;

        // Simple sequential composition as baseline
        var sequentialBound = currentEpsilon + queryEpsilon;

        // Advanced composition (strong composition theorem)
        var deltaPrime = totalDelta / (2.0 * queryCount);
        if (deltaPrime <= 0) return sequentialBound;

        var advancedBound = Math.Sqrt(2.0 * queryCount * Math.Log(1.0 / deltaPrime)) * queryEpsilon
                          + queryCount * queryEpsilon * (Math.Exp(queryEpsilon) - 1.0);

        // Use the tighter bound
        return Math.Min(sequentialBound, advancedBound);
    }
}

/// <summary>
/// Federated analytics strategy for computing on encrypted/masked data.
/// Supports secure aggregation without exposing individual records.
/// </summary>
public sealed class FederatedAnalyticsStrategy : DataPrivacyStrategyBase
{
    private readonly BoundedDictionary<string, FederatedSession> _sessions = new BoundedDictionary<string, FederatedSession>(1000);

    public override string StrategyId => "federated-analytics";
    public override string DisplayName => "Federated Analytics";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new()
    {
        SupportsAsync = true, SupportsBatch = true,
        SupportsReversible = false, SupportsFormatPreserving = false
    };
    public override string SemanticDescription =>
        "Federated analytics for computing on encrypted or masked data across distributed sources";
    public override string[] Tags => ["federated", "analytics", "secure-aggregation", "distributed"];

    /// <summary>
    /// Creates a federated analytics session.
    /// </summary>
    public FederatedSession CreateSession(string queryId, FederatedQueryType queryType, string[] participantIds)
    {
        var session = new FederatedSession
        {
            SessionId = Guid.NewGuid().ToString("N")[..12],
            QueryId = queryId,
            QueryType = queryType,
            ParticipantIds = participantIds,
            Contributions = new BoundedDictionary<string, double>(1000),
            Status = FederatedSessionStatus.Collecting,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _sessions[session.SessionId] = session;
        return session;
    }

    /// <summary>
    /// Submits a participant's contribution to a federated session.
    /// </summary>
    public FederatedContributionResult SubmitContribution(string sessionId, string participantId, double value)
    {
        // Cat 14 (finding 2520): reject NaN/Infinity — these corrupt aggregate results silently.
        if (!double.IsFinite(value))
            return new FederatedContributionResult { Accepted = false, Reason = $"Contribution value must be finite; got {value}" };

        if (!_sessions.TryGetValue(sessionId, out var session))
            return new FederatedContributionResult { Accepted = false, Reason = "Session not found" };

        if (session.Status != FederatedSessionStatus.Collecting)
            return new FederatedContributionResult { Accepted = false, Reason = "Session not collecting" };

        if (!session.ParticipantIds.Contains(participantId))
            return new FederatedContributionResult { Accepted = false, Reason = "Not a participant" };

        session.Contributions[participantId] = value;

        var allReceived = session.ParticipantIds.All(p => session.Contributions.ContainsKey(p));
        if (allReceived)
        {
            session = session with { Status = FederatedSessionStatus.Aggregating };
            _sessions[sessionId] = session;
        }

        return new FederatedContributionResult
        {
            Accepted = true,
            ContributionsReceived = session.Contributions.Count,
            TotalExpected = session.ParticipantIds.Length,
            IsComplete = allReceived
        };
    }

    /// <summary>
    /// Computes the federated aggregate with noise for privacy.
    /// </summary>
    public FederatedResult ComputeAggregate(string sessionId, double noiseEpsilon = 1.0)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return new FederatedResult { Success = false, Error = "Session not found" };

        if (session.Contributions.Count < session.ParticipantIds.Length)
            return new FederatedResult { Success = false, Error = "Not all contributions received" };

        var values = session.Contributions.Values.ToList();
        double rawResult = session.QueryType switch
        {
            FederatedQueryType.Sum => values.Sum(),
            FederatedQueryType.Average => values.Average(),
            FederatedQueryType.Count => values.Count,
            FederatedQueryType.Min => values.Min(),
            FederatedQueryType.Max => values.Max(),
            _ => values.Sum()
        };

        // Add calibrated Laplace noise for differential privacy
        var sensitivity = session.QueryType switch
        {
            FederatedQueryType.Sum => values.Max() - values.Min(),
            FederatedQueryType.Average => (values.Max() - values.Min()) / values.Count,
            FederatedQueryType.Count => 1.0,
            _ => 1.0
        };

        var noise = GenerateLaplaceNoise(sensitivity / noiseEpsilon);
        var noisyResult = rawResult + noise;

        _sessions[sessionId] = session with { Status = FederatedSessionStatus.Completed };

        return new FederatedResult
        {
            Success = true,
            RawResult = rawResult,
            NoisyResult = noisyResult,
            NoiseAdded = noise,
            Epsilon = noiseEpsilon,
            ParticipantCount = values.Count
        };
    }

    private static double GenerateLaplaceNoise(double scale)
    {
        var u = Random.Shared.NextDouble() - 0.5;
        return -scale * Math.Sign(u) * Math.Log(1.0 - 2.0 * Math.Abs(u));
    }
}

/// <summary>
/// Synthetic data generation strategy for testing with privacy-safe data.
/// </summary>
public sealed class SyntheticDataGenerationStrategy : DataPrivacyStrategyBase
{
    public override string StrategyId => "synthetic-data-generation";
    public override string DisplayName => "Synthetic Data Generation";
    public override PrivacyCategory Category => PrivacyCategory.DifferentialPrivacy;
    public override DataPrivacyCapabilities Capabilities => new()
    {
        SupportsAsync = true, SupportsBatch = true,
        SupportsReversible = false, SupportsFormatPreserving = true
    };
    public override string SemanticDescription =>
        "Generate synthetic test data that preserves statistical properties while ensuring privacy";
    public override string[] Tags => ["synthetic", "data-generation", "testing", "privacy-safe"];

    /// <summary>
    /// Generates synthetic data based on statistical properties of the original.
    /// </summary>
    public SyntheticDataResult Generate(SyntheticDataRequest request)
    {
        var records = new List<Dictionary<string, object>>();

        for (int i = 0; i < request.RecordCount; i++)
        {
            var record = new Dictionary<string, object>();
            foreach (var column in request.ColumnSpecs)
            {
                record[column.Name] = column.Type switch
                {
                    SyntheticColumnType.Numeric => GenerateNumeric(column.Mean, column.StdDev),
                    SyntheticColumnType.Categorical => GenerateCategorical(column.Categories),
                    SyntheticColumnType.DateTime => GenerateDateTime(column.MinDate, column.MaxDate),
                    SyntheticColumnType.Text => GenerateText(column.TextPattern, column.TextLength),
                    SyntheticColumnType.Boolean => Random.Shared.NextDouble() < (column.TrueProbability ?? 0.5),
                    _ => DBNull.Value
                };
            }
            records.Add(record);
        }

        return new SyntheticDataResult
        {
            Records = records,
            RecordCount = records.Count,
            ColumnCount = request.ColumnSpecs.Count,
            GeneratedAt = DateTimeOffset.UtcNow,
            Seed = request.Seed
        };
    }

    private static double GenerateNumeric(double? mean, double? stdDev)
    {
        var m = mean ?? 0;
        var s = stdDev ?? 1;
        // Box-Muller transform for normal distribution
        var u1 = Random.Shared.NextDouble();
        var u2 = Random.Shared.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return m + s * z;
    }

    private static string GenerateCategorical(string[]? categories)
    {
        if (categories == null || categories.Length == 0) return "unknown";
        return categories[Random.Shared.Next(categories.Length)];
    }

    private static DateTimeOffset GenerateDateTime(DateTimeOffset? min, DateTimeOffset? max)
    {
        var minTicks = (min ?? DateTimeOffset.UtcNow.AddYears(-1)).Ticks;
        var maxTicks = (max ?? DateTimeOffset.UtcNow).Ticks;
        var ticks = minTicks + (long)(Random.Shared.NextDouble() * (maxTicks - minTicks));
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static string GenerateText(string? pattern, int? length)
    {
        var len = length ?? 10;
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Range(0, len).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }
}

/// <summary>
/// PII detection strategy with confidence scoring for identifying personal data.
/// </summary>
public sealed class PiiDetectionStrategy : DataPrivacyStrategyBase
{
    // P2-2518: Pre-compile all Regex patterns at class init to avoid per-call allocation
    // inside the nested loop over all patterns × all input rows.
    private static readonly Dictionary<string, PiiPattern[]> PiiPatterns = new()
    {
        ["email"] = new[] { new PiiPattern(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", 0.95, PiiType.Email) },
        ["phone"] = new[] { new PiiPattern(@"\+?\d{1,3}[-.\s]?\(?\d{1,4}\)?[-.\s]?\d{3,4}[-.\s]?\d{4}", 0.85, PiiType.Phone) },
        ["ssn"] = new[] { new PiiPattern(@"\d{3}-\d{2}-\d{4}", 0.90, PiiType.SSN) },
        ["credit_card"] = new[] { new PiiPattern(@"\d{4}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}", 0.88, PiiType.CreditCard) },
        ["ip_address"] = new[] { new PiiPattern(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", 0.75, PiiType.IpAddress) },
        ["name"] = new[] { new PiiPattern(@"^[A-Z][a-z]+ [A-Z][a-z]+$", 0.60, PiiType.PersonName) }
    };

    // Pre-compiled Regex per pattern, keyed by pattern string.
    private static readonly Dictionary<string, System.Text.RegularExpressions.Regex> CompiledPatterns =
        PiiPatterns.Values
            .SelectMany(arr => arr)
            .GroupBy(p => p.Pattern)
            .ToDictionary(
                g => g.Key,
                g => new System.Text.RegularExpressions.Regex(
                    g.Key,
                    System.Text.RegularExpressions.RegexOptions.Compiled |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    public override string StrategyId => "pii-detection";
    public override string DisplayName => "PII Detection";
    // Cat 15 (finding 2521): PII detection is classification, not differential privacy.
    public override PrivacyCategory Category => PrivacyCategory.DataClassification;
    public override DataPrivacyCapabilities Capabilities => new()
    {
        SupportsAsync = true, SupportsBatch = true,
        SupportsReversible = false, SupportsFormatPreserving = false
    };
    public override string SemanticDescription =>
        "Detect personally identifiable information with confidence scoring and classification";
    public override string[] Tags => ["pii", "detection", "classification", "confidence"];

    /// <summary>
    /// Scans text for PII with confidence scoring.
    /// </summary>
    public PiiScanResult Scan(string text, double minConfidence = 0.5)
    {
        var detections = new List<PiiDetection>();

        foreach (var (category, patterns) in PiiPatterns)
        {
            foreach (var pattern in patterns)
            {
                var regex = CompiledPatterns[pattern.Pattern];
                var matches = regex.Matches(text);

                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (pattern.BaseConfidence >= minConfidence)
                    {
                        // Adjust confidence based on context
                        var contextConfidence = AdjustConfidenceByContext(text, match.Index, pattern);

                        detections.Add(new PiiDetection
                        {
                            Type = pattern.PiiType,
                            Value = match.Value,
                            Position = match.Index,
                            Length = match.Length,
                            Confidence = contextConfidence,
                            Category = category
                        });
                    }
                }
            }
        }

        // Column name heuristics for structured data
        var columnHints = DetectColumnNameHints(text);
        detections.AddRange(columnHints.Where(h => h.Confidence >= minConfidence));

        return new PiiScanResult
        {
            Detections = detections.OrderByDescending(d => d.Confidence).ToList(),
            TotalDetections = detections.Count,
            HighConfidenceCount = detections.Count(d => d.Confidence >= 0.8),
            ScannedAt = DateTimeOffset.UtcNow,
            TextLength = text.Length
        };
    }

    /// <summary>
    /// Scans a dictionary of column-value pairs for PII.
    /// </summary>
    public PiiColumnScanResult ScanColumns(Dictionary<string, string> columns, double minConfidence = 0.5)
    {
        var results = new Dictionary<string, PiiScanResult>();
        var piiColumns = new List<string>();

        foreach (var (columnName, value) in columns)
        {
            var scan = Scan(value, minConfidence);

            // Boost confidence based on column name
            var nameBoost = GetColumnNameConfidenceBoost(columnName);
            if (nameBoost > 0)
            {
                scan = scan with
                {
                    Detections = scan.Detections.Select(d => d with { Confidence = Math.Min(1.0, d.Confidence + nameBoost) }).ToList()
                };
            }

            results[columnName] = scan;
            if (scan.TotalDetections > 0)
                piiColumns.Add(columnName);
        }

        return new PiiColumnScanResult
        {
            ColumnResults = results,
            PiiColumnNames = piiColumns,
            TotalColumnsScanned = columns.Count,
            ColumnsWithPii = piiColumns.Count
        };
    }

    private static double AdjustConfidenceByContext(string text, int position, PiiPattern pattern)
    {
        // Check surrounding context for confidence adjustment
        var start = Math.Max(0, position - 20);
        var end = Math.Min(text.Length, position + 20);
        var context = text[start..end].ToLowerInvariant();

        var boost = 0.0;
        if (context.Contains("email") || context.Contains("mail")) boost += 0.05;
        if (context.Contains("phone") || context.Contains("tel")) boost += 0.05;
        if (context.Contains("ssn") || context.Contains("social")) boost += 0.05;
        if (context.Contains("card") || context.Contains("credit")) boost += 0.05;

        return Math.Min(1.0, pattern.BaseConfidence + boost);
    }

    private static List<PiiDetection> DetectColumnNameHints(string text)
    {
        var hints = new List<PiiDetection>();
        var piiColumnNames = new Dictionary<string, PiiType>
        {
            ["first_name"] = PiiType.PersonName, ["last_name"] = PiiType.PersonName,
            ["email"] = PiiType.Email, ["phone"] = PiiType.Phone,
            ["address"] = PiiType.Address, ["dob"] = PiiType.DateOfBirth,
            ["date_of_birth"] = PiiType.DateOfBirth, ["birth_date"] = PiiType.DateOfBirth
        };

        foreach (var (name, type) in piiColumnNames)
        {
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                hints.Add(new PiiDetection
                {
                    Type = type,
                    Value = name,
                    Position = text.IndexOf(name, StringComparison.OrdinalIgnoreCase),
                    Length = name.Length,
                    Confidence = 0.70,
                    Category = "column_name_hint"
                });
            }
        }

        return hints;
    }

    private static double GetColumnNameConfidenceBoost(string columnName)
    {
        var lower = columnName.ToLowerInvariant();
        if (lower.Contains("email") || lower.Contains("ssn") || lower.Contains("phone")) return 0.15;
        if (lower.Contains("name") || lower.Contains("address")) return 0.10;
        if (lower.Contains("dob") || lower.Contains("birth")) return 0.10;
        return 0;
    }
}

#region Models

public sealed record PrivacyBudget
{
    public required string DatasetId { get; init; }
    public double TotalEpsilon { get; init; }
    public double TotalDelta { get; init; }
    public double ConsumedEpsilon { get; init; }
    public double ConsumedDelta { get; init; }
    public double RemainingEpsilon { get; init; }
    public double RemainingDelta { get; init; }
    public int QueryCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsExhausted { get; init; }
}

public sealed record PrivacyQuery
{
    public required string QueryId { get; init; }
    public required string DatasetId { get; init; }
    public double Epsilon { get; init; }
    public double Delta { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record PrivacyQueryResult
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }
    public string? QueryId { get; init; }
    public double ConsumedEpsilon { get; init; }
    public double ConsumedDelta { get; init; }
    public double RemainingEpsilon { get; init; }
    public double RemainingDelta { get; init; }
    public double BudgetUtilization { get; init; }
}

public sealed record FederatedSession
{
    public required string SessionId { get; init; }
    public required string QueryId { get; init; }
    public FederatedQueryType QueryType { get; init; }
    public required string[] ParticipantIds { get; init; }
    public BoundedDictionary<string, double> Contributions { get; init; } = new BoundedDictionary<string, double>(1000);
    public FederatedSessionStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public enum FederatedQueryType { Sum, Average, Count, Min, Max }
public enum FederatedSessionStatus { Collecting, Aggregating, Completed, Failed }

public sealed record FederatedContributionResult
{
    public bool Accepted { get; init; }
    public string? Reason { get; init; }
    public int ContributionsReceived { get; init; }
    public int TotalExpected { get; init; }
    public bool IsComplete { get; init; }
}

public sealed record FederatedResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public double RawResult { get; init; }
    public double NoisyResult { get; init; }
    public double NoiseAdded { get; init; }
    public double Epsilon { get; init; }
    public int ParticipantCount { get; init; }
}

public sealed record SyntheticDataRequest
{
    public int RecordCount { get; init; } = 100;
    public List<SyntheticColumnSpec> ColumnSpecs { get; init; } = new();
    public int? Seed { get; init; }
}

public sealed record SyntheticColumnSpec
{
    public required string Name { get; init; }
    public SyntheticColumnType Type { get; init; }
    public double? Mean { get; init; }
    public double? StdDev { get; init; }
    public string[]? Categories { get; init; }
    public DateTimeOffset? MinDate { get; init; }
    public DateTimeOffset? MaxDate { get; init; }
    public string? TextPattern { get; init; }
    public int? TextLength { get; init; }
    public double? TrueProbability { get; init; }
}

public enum SyntheticColumnType { Numeric, Categorical, DateTime, Text, Boolean }

public sealed record SyntheticDataResult
{
    public List<Dictionary<string, object>> Records { get; init; } = new();
    public int RecordCount { get; init; }
    public int ColumnCount { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public int? Seed { get; init; }
}

public sealed record PiiPattern(string Pattern, double BaseConfidence, PiiType PiiType);

public enum PiiType
{
    Email, Phone, SSN, CreditCard, IpAddress, PersonName,
    Address, DateOfBirth, Passport, DriversLicense, BankAccount
}

public sealed record PiiDetection
{
    public PiiType Type { get; init; }
    public required string Value { get; init; }
    public int Position { get; init; }
    public int Length { get; init; }
    public double Confidence { get; init; }
    public required string Category { get; init; }
}

public sealed record PiiScanResult
{
    public List<PiiDetection> Detections { get; init; } = new();
    public int TotalDetections { get; init; }
    public int HighConfidenceCount { get; init; }
    public DateTimeOffset ScannedAt { get; init; }
    public int TextLength { get; init; }
}

public sealed record PiiColumnScanResult
{
    public Dictionary<string, PiiScanResult> ColumnResults { get; init; } = new();
    public List<string> PiiColumnNames { get; init; } = new();
    public int TotalColumnsScanned { get; init; }
    public int ColumnsWithPii { get; init; }
}

#endregion
