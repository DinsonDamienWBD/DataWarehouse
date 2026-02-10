using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Features
{
    /// <summary>
    /// Real-time threat intelligence feed aggregation and correlation engine.
    /// Supports threat indicator matching (IP, domain, hash, email), feed aggregation, and IOC correlation.
    /// </summary>
    public sealed class ThreatIntelligenceEngine
    {
        private readonly ConcurrentDictionary<string, ThreatFeed> _feeds = new();
        private readonly ConcurrentDictionary<string, ThreatIndicator> _indicators = new();
        private readonly ConcurrentQueue<ThreatMatch> _recentMatches = new();
        private readonly int _maxMatchHistorySize = 1000;

        /// <summary>
        /// Registers a threat intelligence feed.
        /// </summary>
        /// <param name="feed">Threat feed to register.</param>
        public void RegisterFeed(ThreatFeed feed)
        {
            if (feed == null)
                throw new ArgumentNullException(nameof(feed));

            if (string.IsNullOrWhiteSpace(feed.FeedId))
                throw new ArgumentException("Feed ID cannot be null or empty", nameof(feed));

            _feeds[feed.FeedId] = feed;
        }

        /// <summary>
        /// Adds threat indicators from a feed.
        /// </summary>
        /// <param name="feedId">Feed identifier.</param>
        /// <param name="indicators">Threat indicators to add.</param>
        public void AddIndicators(string feedId, IEnumerable<ThreatIndicator> indicators)
        {
            if (string.IsNullOrWhiteSpace(feedId))
                throw new ArgumentException("Feed ID cannot be null or empty", nameof(feedId));

            if (!_feeds.ContainsKey(feedId))
                throw new InvalidOperationException($"Feed '{feedId}' is not registered");

            foreach (var indicator in indicators)
            {
                indicator.FeedId = feedId;
                indicator.AddedAt = DateTime.UtcNow;
                _indicators[indicator.Value] = indicator;
            }
        }

        /// <summary>
        /// Checks if an IP address matches any threat indicators.
        /// </summary>
        /// <param name="ipAddress">IP address to check.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Threat assessment result.</returns>
        public Task<ThreatAssessment> CheckIpAddressAsync(string ipAddress, CancellationToken cancellationToken = default)
        {
            return CheckIndicatorAsync(ipAddress, ThreatIndicatorType.IpAddress, cancellationToken);
        }

        /// <summary>
        /// Checks if a domain matches any threat indicators.
        /// </summary>
        /// <param name="domain">Domain to check.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Threat assessment result.</returns>
        public Task<ThreatAssessment> CheckDomainAsync(string domain, CancellationToken cancellationToken = default)
        {
            return CheckIndicatorAsync(domain, ThreatIndicatorType.Domain, cancellationToken);
        }

        /// <summary>
        /// Checks if a file hash matches any threat indicators.
        /// </summary>
        /// <param name="hash">File hash to check.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Threat assessment result.</returns>
        public Task<ThreatAssessment> CheckFileHashAsync(string hash, CancellationToken cancellationToken = default)
        {
            return CheckIndicatorAsync(hash, ThreatIndicatorType.FileHash, cancellationToken);
        }

        /// <summary>
        /// Checks if an email address matches any threat indicators.
        /// </summary>
        /// <param name="email">Email address to check.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Threat assessment result.</returns>
        public Task<ThreatAssessment> CheckEmailAsync(string email, CancellationToken cancellationToken = default)
        {
            return CheckIndicatorAsync(email, ThreatIndicatorType.Email, cancellationToken);
        }

        /// <summary>
        /// Performs comprehensive threat assessment correlating multiple indicators.
        /// </summary>
        /// <param name="context">Threat context with multiple indicators.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Correlated threat assessment.</returns>
        public async Task<ThreatAssessment> CorrelateThreatsAsync(
            ThreatContext context,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var assessments = new List<ThreatAssessment>();

            // Check all indicators in parallel
            var tasks = new List<Task<ThreatAssessment>>();

            if (!string.IsNullOrEmpty(context.IpAddress))
                tasks.Add(CheckIpAddressAsync(context.IpAddress, cancellationToken));

            if (!string.IsNullOrEmpty(context.Domain))
                tasks.Add(CheckDomainAsync(context.Domain, cancellationToken));

            if (!string.IsNullOrEmpty(context.FileHash))
                tasks.Add(CheckFileHashAsync(context.FileHash, cancellationToken));

            if (!string.IsNullOrEmpty(context.Email))
                tasks.Add(CheckEmailAsync(context.Email, cancellationToken));

            assessments.AddRange(await Task.WhenAll(tasks));

            // Correlate findings
            var allMatches = assessments.SelectMany(a => a.Matches).ToList();
            var uniqueFeeds = allMatches.Select(m => m.FeedId).Distinct().Count();
            var maxSeverity = allMatches.Any()
                ? allMatches.Max(m => m.Severity)
                : ThreatSeverity.None;

            // Calculate correlation score
            var correlationScore = CalculateCorrelationScore(allMatches, uniqueFeeds);
            var isThreat = correlationScore >= 50.0 || maxSeverity >= ThreatSeverity.High;

            return new ThreatAssessment
            {
                IsThreat = isThreat,
                Severity = maxSeverity,
                ThreatScore = correlationScore,
                Matches = allMatches.ToArray(),
                CorrelatedFeeds = uniqueFeeds,
                AssessedAt = DateTime.UtcNow,
                Details = isThreat
                    ? $"Correlated {allMatches.Count} threat indicators across {uniqueFeeds} feeds (score: {correlationScore:F1})"
                    : "No threat indicators matched"
            };
        }

        private Task<ThreatAssessment> CheckIndicatorAsync(
            string value,
            ThreatIndicatorType type,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Task.FromResult(new ThreatAssessment
                {
                    IsThreat = false,
                    Severity = ThreatSeverity.None,
                    ThreatScore = 0.0,
                    Matches = Array.Empty<ThreatMatch>(),
                    CorrelatedFeeds = 0,
                    AssessedAt = DateTime.UtcNow,
                    Details = "Empty or null value provided"
                });
            }

            var matches = new List<ThreatMatch>();

            // Direct match
            if (_indicators.TryGetValue(value, out var indicator) && indicator.Type == type)
            {
                var match = CreateMatch(indicator, value, 100.0);
                matches.Add(match);
                RecordMatch(match);
            }

            // Fuzzy matching for domains (check subdomains)
            if (type == ThreatIndicatorType.Domain)
            {
                var domainMatches = _indicators.Values
                    .Where(i => i.Type == ThreatIndicatorType.Domain &&
                                (value.EndsWith("." + i.Value, StringComparison.OrdinalIgnoreCase) ||
                                 i.Value.EndsWith("." + value, StringComparison.OrdinalIgnoreCase)))
                    .Select(i => CreateMatch(i, value, 85.0))
                    .ToList();

                matches.AddRange(domainMatches);
                foreach (var match in domainMatches)
                    RecordMatch(match);
            }

            // Calculate aggregate threat assessment
            var maxSeverity = matches.Any() ? matches.Max(m => m.Severity) : ThreatSeverity.None;
            var maxConfidence = matches.Any() ? matches.Max(m => m.Confidence) : 0.0;
            var uniqueFeeds = matches.Select(m => m.FeedId).Distinct().Count();
            var threatScore = CalculateThreatScore(matches, uniqueFeeds);

            return Task.FromResult(new ThreatAssessment
            {
                IsThreat = matches.Any(),
                Severity = maxSeverity,
                ThreatScore = threatScore,
                Matches = matches.ToArray(),
                CorrelatedFeeds = uniqueFeeds,
                AssessedAt = DateTime.UtcNow,
                Details = matches.Any()
                    ? $"Matched {matches.Count} indicators from {uniqueFeeds} feeds (confidence: {maxConfidence:F0}%)"
                    : "No threat indicators matched"
            });
        }

        private ThreatMatch CreateMatch(ThreatIndicator indicator, string matchedValue, double confidence)
        {
            return new ThreatMatch
            {
                IndicatorValue = indicator.Value,
                IndicatorType = indicator.Type,
                MatchedValue = matchedValue,
                Confidence = confidence,
                Severity = indicator.Severity,
                ThreatType = indicator.ThreatType,
                FeedId = indicator.FeedId,
                Description = indicator.Description,
                MatchedAt = DateTime.UtcNow
            };
        }

        private void RecordMatch(ThreatMatch match)
        {
            _recentMatches.Enqueue(match);

            // Prune old matches
            while (_recentMatches.Count > _maxMatchHistorySize)
            {
                _recentMatches.TryDequeue(out _);
            }
        }

        private double CalculateThreatScore(List<ThreatMatch> matches, int uniqueFeeds)
        {
            if (!matches.Any())
                return 0.0;

            // Base score from highest severity
            var severityScore = matches.Max(m => (int)m.Severity) * 20.0;

            // Confidence bonus
            var avgConfidence = matches.Average(m => m.Confidence);
            var confidenceBonus = avgConfidence * 0.3;

            // Multi-feed correlation bonus
            var correlationBonus = uniqueFeeds > 1 ? Math.Min(uniqueFeeds * 5.0, 20.0) : 0.0;

            return Math.Min(severityScore + confidenceBonus + correlationBonus, 100.0);
        }

        private double CalculateCorrelationScore(List<ThreatMatch> matches, int uniqueFeeds)
        {
            if (!matches.Any())
                return 0.0;

            var baseScore = CalculateThreatScore(matches, uniqueFeeds);

            // Additional correlation bonus for multiple indicator types
            var indicatorTypes = matches.Select(m => m.IndicatorType).Distinct().Count();
            var typeBonus = indicatorTypes > 1 ? Math.Min(indicatorTypes * 10.0, 30.0) : 0.0;

            return Math.Min(baseScore + typeBonus, 100.0);
        }

        /// <summary>
        /// Gets all registered threat feeds.
        /// </summary>
        /// <returns>Collection of registered feeds.</returns>
        public IReadOnlyCollection<ThreatFeed> GetFeeds()
        {
            return _feeds.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets recent threat matches.
        /// </summary>
        /// <param name="maxCount">Maximum number of matches to return.</param>
        /// <returns>Recent threat matches.</returns>
        public IReadOnlyCollection<ThreatMatch> GetRecentMatches(int maxCount = 100)
        {
            return _recentMatches.Take(maxCount).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets count of indicators by feed.
        /// </summary>
        /// <returns>Dictionary of feed IDs to indicator counts.</returns>
        public Dictionary<string, int> GetIndicatorCountsByFeed()
        {
            return _indicators.Values
                .GroupBy(i => i.FeedId)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        /// <summary>
        /// Removes expired indicators based on feed TTL.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Number of indicators removed.</returns>
        public Task<int> RemoveExpiredIndicatorsAsync(CancellationToken cancellationToken = default)
        {
            var removed = 0;
            var now = DateTime.UtcNow;

            var expiredIndicators = _indicators.Values
                .Where(i => _feeds.TryGetValue(i.FeedId, out var feed) &&
                            feed.TimeToLive.HasValue &&
                            (now - i.AddedAt) > feed.TimeToLive.Value)
                .ToList();

            foreach (var indicator in expiredIndicators)
            {
                if (_indicators.TryRemove(indicator.Value, out _))
                    removed++;
            }

            return Task.FromResult(removed);
        }
    }

    #region Supporting Types

    /// <summary>
    /// Threat intelligence feed.
    /// </summary>
    public sealed class ThreatFeed
    {
        /// <summary>
        /// Feed identifier.
        /// </summary>
        public required string FeedId { get; init; }

        /// <summary>
        /// Feed display name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Feed provider.
        /// </summary>
        public required string Provider { get; init; }

        /// <summary>
        /// Feed reliability score (0-100).
        /// </summary>
        public required double ReliabilityScore { get; init; }

        /// <summary>
        /// Time-to-live for indicators from this feed.
        /// </summary>
        public TimeSpan? TimeToLive { get; init; }

        /// <summary>
        /// Feed update frequency.
        /// </summary>
        public TimeSpan? UpdateFrequency { get; init; }
    }

    /// <summary>
    /// Threat indicator (IOC - Indicator of Compromise).
    /// </summary>
    public sealed class ThreatIndicator
    {
        /// <summary>
        /// Indicator value (IP, domain, hash, email).
        /// </summary>
        public required string Value { get; init; }

        /// <summary>
        /// Indicator type.
        /// </summary>
        public required ThreatIndicatorType Type { get; init; }

        /// <summary>
        /// Threat severity.
        /// </summary>
        public required ThreatSeverity Severity { get; init; }

        /// <summary>
        /// Threat type classification.
        /// </summary>
        public required string ThreatType { get; init; }

        /// <summary>
        /// Feed that provided this indicator.
        /// </summary>
        public required string FeedId { get; set; }

        /// <summary>
        /// Indicator description.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// When indicator was added.
        /// </summary>
        public DateTime AddedAt { get; set; }
    }

    /// <summary>
    /// Threat indicator type.
    /// </summary>
    public enum ThreatIndicatorType
    {
        /// <summary>IP address.</summary>
        IpAddress,

        /// <summary>Domain name.</summary>
        Domain,

        /// <summary>File hash (MD5, SHA1, SHA256).</summary>
        FileHash,

        /// <summary>Email address.</summary>
        Email,

        /// <summary>URL.</summary>
        Url
    }

    /// <summary>
    /// Threat severity level.
    /// </summary>
    public enum ThreatSeverity
    {
        /// <summary>No threat.</summary>
        None = 0,

        /// <summary>Low severity threat.</summary>
        Low = 1,

        /// <summary>Medium severity threat.</summary>
        Medium = 2,

        /// <summary>High severity threat.</summary>
        High = 3,

        /// <summary>Critical severity threat.</summary>
        Critical = 4
    }

    /// <summary>
    /// Threat match result.
    /// </summary>
    public sealed class ThreatMatch
    {
        /// <summary>
        /// Indicator value that matched.
        /// </summary>
        public required string IndicatorValue { get; init; }

        /// <summary>
        /// Indicator type.
        /// </summary>
        public required ThreatIndicatorType IndicatorType { get; init; }

        /// <summary>
        /// Value that was matched against.
        /// </summary>
        public required string MatchedValue { get; init; }

        /// <summary>
        /// Match confidence (0-100).
        /// </summary>
        public required double Confidence { get; init; }

        /// <summary>
        /// Threat severity.
        /// </summary>
        public required ThreatSeverity Severity { get; init; }

        /// <summary>
        /// Threat type.
        /// </summary>
        public required string ThreatType { get; init; }

        /// <summary>
        /// Feed that provided the indicator.
        /// </summary>
        public required string FeedId { get; init; }

        /// <summary>
        /// Match description.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// When the match occurred.
        /// </summary>
        public required DateTime MatchedAt { get; init; }
    }

    /// <summary>
    /// Threat assessment result.
    /// </summary>
    public sealed class ThreatAssessment
    {
        /// <summary>
        /// Whether a threat was detected.
        /// </summary>
        public required bool IsThreat { get; init; }

        /// <summary>
        /// Maximum severity of matched threats.
        /// </summary>
        public required ThreatSeverity Severity { get; init; }

        /// <summary>
        /// Aggregate threat score (0-100).
        /// </summary>
        public required double ThreatScore { get; init; }

        /// <summary>
        /// Matched threat indicators.
        /// </summary>
        public required ThreatMatch[] Matches { get; init; }

        /// <summary>
        /// Number of unique feeds that matched.
        /// </summary>
        public required int CorrelatedFeeds { get; init; }

        /// <summary>
        /// Assessment timestamp.
        /// </summary>
        public required DateTime AssessedAt { get; init; }

        /// <summary>
        /// Human-readable details.
        /// </summary>
        public required string Details { get; init; }
    }

    /// <summary>
    /// Threat context for correlation.
    /// </summary>
    public sealed class ThreatContext
    {
        /// <summary>
        /// IP address to check.
        /// </summary>
        public string? IpAddress { get; init; }

        /// <summary>
        /// Domain to check.
        /// </summary>
        public string? Domain { get; init; }

        /// <summary>
        /// File hash to check.
        /// </summary>
        public string? FileHash { get; init; }

        /// <summary>
        /// Email address to check.
        /// </summary>
        public string? Email { get; init; }
    }

    #endregion
}
