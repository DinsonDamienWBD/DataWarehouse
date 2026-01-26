using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.FederatedQuery
{
    /// <summary>
    /// Query engine that parses and executes federated queries across multiple data sources.
    /// Supports SQL-like syntax for unified querying.
    /// </summary>
    public sealed class QueryEngine
    {
        private readonly DataSourceRegistry _registry;
        private static readonly Regex SelectFromRegex = new(
            @"SELECT\s+(?<columns>.*?)\s+FROM\s+(?<source>\S+)(?:\s+WHERE\s+(?<where>.*?))?(?:\s+JOIN\s+(?<join>.*?))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex SearchRegex = new(
            @"datawarehouse\.search\s*\(\s*['""](?<query>[^'""]+)['""]\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public QueryEngine(DataSourceRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <summary>
        /// Parses a query string into a FederatedQuery object.
        /// </summary>
        public FederatedQuery ParseQuery(string queryText)
        {
            if (string.IsNullOrWhiteSpace(queryText))
            {
                return new FederatedQuery { QueryText = queryText };
            }

            var query = new FederatedQuery { QueryText = queryText };

            // Check for simple search syntax
            var searchMatch = SearchRegex.Match(queryText);
            if (searchMatch.Success)
            {
                var searchTerm = searchMatch.Groups["query"].Value;
                return new FederatedQuery
                {
                    QueryText = searchTerm,
                    IncludeFileMetadata = true,
                    IncludeRecords = true,
                    IncludeFileContent = true,
                    Limit = 100
                };
            }

            // Parse SQL-like SELECT ... FROM ... WHERE syntax
            var selectMatch = SelectFromRegex.Match(queryText);
            if (selectMatch.Success)
            {
                var source = selectMatch.Groups["source"].Value;
                var whereClause = selectMatch.Groups["where"].Value;

                // Determine source type
                if (source.StartsWith("collections.", StringComparison.OrdinalIgnoreCase))
                {
                    var collectionName = source.Substring("collections.".Length);
                    query.TargetSources.Add(collectionName);
                    query.IncludeRecords = true;
                    query.IncludeFileMetadata = false;
                }
                else if (source.Equals("files", StringComparison.OrdinalIgnoreCase))
                {
                    query.IncludeFileMetadata = true;
                    query.IncludeRecords = false;
                }

                // Parse WHERE clause
                if (!string.IsNullOrWhiteSpace(whereClause))
                {
                    ParseWhereClause(whereClause, query);
                }
            }

            return query;
        }

        /// <summary>
        /// Creates a query execution plan without executing it.
        /// </summary>
        public QueryExecutionPlan CreateExecutionPlan(FederatedQuery query)
        {
            var plan = new QueryExecutionPlan();
            var steps = new List<string>();
            double totalCost = 0;

            // Determine which sources to query
            var targetSources = DetermineTargetSources(query);
            plan.TargetSources.AddRange(targetSources.Select(s => s.Name));

            foreach (var source in targetSources)
            {
                var adapter = _registry.GetAdapter(source.Name);
                if (adapter == null)
                {
                    plan.Warnings.Add($"No adapter found for source: {source.Name}");
                    continue;
                }

                var cost = adapter.EstimateQueryCost(query);
                totalCost += cost;

                steps.Add($"Query {source.Type} source '{source.Name}' (cost: {cost:F2})");

                // Check if source has schema
                if (source.Schema != null)
                {
                    plan.UsesIndexes = plan.UsesIndexes || source.Schema.SupportsFullText;
                }
            }

            if (targetSources.Count > 1)
            {
                steps.Add("Merge and rank results from all sources");
                totalCost += 0.5; // Cost of merging
            }

            if (query.Limit < 100)
            {
                steps.Add($"Limit results to top {query.Limit}");
            }

            plan.QuerySteps.AddRange(steps);
            plan.EstimatedCost = totalCost;

            // Add warnings
            if (query.IncludeFileContent && targetSources.Any(s => s.Type == DataSourceType.FileContent))
            {
                plan.Warnings.Add("File content search may be slow on large datasets");
            }

            if (targetSources.Count == 0)
            {
                plan.Warnings.Add("No data sources match the query criteria");
            }

            return plan;
        }

        /// <summary>
        /// Executes a federated query across all relevant data sources.
        /// </summary>
        public async Task<QueryResult> ExecuteQueryAsync(
            FederatedQuery query,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var statistics = new QueryStatistics();

            try
            {
                // Determine which sources to query
                var targetSources = DetermineTargetSources(query);
                statistics.SourcesQueried = targetSources.Count;

                if (targetSources.Count == 0)
                {
                    return new QueryResult
                    {
                        Success = true,
                        ExecutionTime = sw.Elapsed,
                        Statistics = statistics,
                        ResultSet = new UnifiedResultSet()
                    };
                }

                // Query all sources in parallel
                var queryTasks = new List<Task<SourceQueryResult>>();
                foreach (var source in targetSources)
                {
                    var adapter = _registry.GetAdapter(source.Name);
                    if (adapter != null)
                    {
                        queryTasks.Add(QuerySourceAsync(source.Name, adapter, query, ct));
                    }
                }

                var sourceResults = await Task.WhenAll(queryTasks);

                // Merge and rank results
                var allResults = new List<UnifiedResult>();
                var resultsBySource = new Dictionary<DataSourceType, int>();
                long recordsScanned = 0;

                foreach (var sourceResult in sourceResults)
                {
                    allResults.AddRange(sourceResult.Results);
                    recordsScanned += sourceResult.RecordsScanned;
                    statistics.TimeBySource[sourceResult.SourceName] = sourceResult.Duration;

                    foreach (var result in sourceResult.Results)
                    {
                        if (!resultsBySource.ContainsKey(result.SourceType))
                        {
                            resultsBySource[result.SourceType] = 0;
                        }
                        resultsBySource[result.SourceType]++;
                    }
                }

                // Sort by score and apply limit
                var rankedResults = allResults
                    .OrderByDescending(r => r.Score)
                    .ThenByDescending(r => r.CreatedAt ?? DateTime.MinValue)
                    .Take(query.Limit)
                    .ToList();

                statistics.RecordsScanned = recordsScanned;
                statistics.ResultsReturned = rankedResults.Count;

                sw.Stop();

                return new QueryResult
                {
                    Success = true,
                    ExecutionTime = sw.Elapsed,
                    Statistics = statistics,
                    ResultSet = new UnifiedResultSet
                    {
                        Results = rankedResults,
                        TotalCount = allResults.Count,
                        SourcesQueried = targetSources.Count,
                        ResultsBySource = resultsBySource
                    },
                    ExecutionPlan = CreateExecutionPlan(query)
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new QueryResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ExecutionTime = sw.Elapsed,
                    Statistics = statistics
                };
            }
        }

        private async Task<SourceQueryResult> QuerySourceAsync(
            string sourceName,
            IDataSourceAdapter adapter,
            FederatedQuery query,
            CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var results = await adapter.QueryAsync(query, ct);
                sw.Stop();

                return new SourceQueryResult
                {
                    SourceName = sourceName,
                    Results = results,
                    Duration = sw.Elapsed,
                    RecordsScanned = results.Count,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new SourceQueryResult
                {
                    SourceName = sourceName,
                    Results = new List<UnifiedResult>(),
                    Duration = sw.Elapsed,
                    RecordsScanned = 0,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private List<DataSource> DetermineTargetSources(FederatedQuery query)
        {
            var sources = new List<DataSource>();

            // If specific sources are targeted, use those
            if (query.TargetSources.Count > 0)
            {
                foreach (var sourceName in query.TargetSources)
                {
                    var source = _registry.GetSource(sourceName);
                    if (source != null && source.IsAvailable)
                    {
                        sources.Add(source);
                    }
                }
                return sources;
            }

            // Otherwise, query all available sources based on flags
            var availableSources = _registry.GetAvailableSources();

            if (query.IncludeFileMetadata)
            {
                sources.AddRange(availableSources.Where(s => s.Type == DataSourceType.FileMetadata));
            }

            if (query.IncludeFileContent)
            {
                sources.AddRange(availableSources.Where(s => s.Type == DataSourceType.FileContent));
            }

            if (query.IncludeRecords)
            {
                sources.AddRange(availableSources.Where(s =>
                    s.Type == DataSourceType.DatabaseCollection ||
                    s.Type == DataSourceType.ObjectStore));
            }

            return sources;
        }

        private void ParseWhereClause(string whereClause, FederatedQuery query)
        {
            // Simple WHERE clause parsing
            // Supports basic conditions like: field > value, field = 'value', field LIKE 'value'

            // Parse date conditions
            var dateRegex = new Regex(@"created_date\s*>\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
            var dateMatch = dateRegex.Match(whereClause);
            if (dateMatch.Success && DateTime.TryParse(dateMatch.Groups[1].Value, out var dateFrom))
            {
                query.DateFrom = dateFrom;
            }

            var dateBeforeRegex = new Regex(@"created_date\s*<\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
            var dateBeforeMatch = dateBeforeRegex.Match(whereClause);
            if (dateBeforeMatch.Success && DateTime.TryParse(dateBeforeMatch.Groups[1].Value, out var dateTo))
            {
                query.DateTo = dateTo;
            }

            // Parse other conditions
            var conditionRegex = new Regex(@"(\w+)\s*(=|>|<|>=|<=|LIKE)\s*['""]?([^'""]+?)['""]?(?:\s+AND|\s+OR|$)", RegexOptions.IgnoreCase);
            var matches = conditionRegex.Matches(whereClause);

            foreach (Match match in matches)
            {
                var field = match.Groups[1].Value;
                var op = match.Groups[2].Value;
                var value = match.Groups[3].Value.Trim();

                if (field.Equals("created_date", StringComparison.OrdinalIgnoreCase))
                    continue; // Already handled

                // Add to filters
                query.Filters[field] = new FilterCondition
                {
                    Field = field,
                    Operator = op,
                    Value = value
                };
            }
        }

        private sealed class SourceQueryResult
        {
            public required string SourceName { get; init; }
            public required List<UnifiedResult> Results { get; init; }
            public TimeSpan Duration { get; init; }
            public long RecordsScanned { get; init; }
            public bool Success { get; init; }
            public string? ErrorMessage { get; init; }
        }

        private sealed class FilterCondition
        {
            public required string Field { get; init; }
            public required string Operator { get; init; }
            public required object Value { get; init; }
        }
    }
}
