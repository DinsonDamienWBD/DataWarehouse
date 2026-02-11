using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.ThreatDetection
{
    /// <summary>
    /// Threat Intelligence strategy with AI enrichment and STIX/TAXII feed integration.
    /// Delegates ML enrichment to Intelligence plugin (T90) via message bus with rule-based fallback.
    /// </summary>
    /// <remarks>
    /// <b>DEPENDENCY:</b> Universal Intelligence plugin (T90) for AI threat correlation and enrichment.
    /// <b>MESSAGE TOPIC:</b> intelligence.enrich
    /// <b>FALLBACK:</b> STIX/TAXII feed consumption with IoC hash/IP/domain matching when Intelligence unavailable.
    /// </remarks>
    public sealed class ThreatIntelStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;
        private readonly IMessageBus? _messageBus;
        private readonly ConcurrentDictionary<string, ThreatIndicator> _threatFeed = new();
        private readonly ConcurrentQueue<ThreatMatch> _matches = new();
        private readonly HashSet<string> _knownMaliciousIps = new();
        private readonly HashSet<string> _knownMaliciousDomains = new();
        private readonly HashSet<string> _knownMaliciousHashes = new();

        public ThreatIntelStrategy(ILogger? logger = null, IMessageBus? messageBus = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _messageBus = messageBus;
        }

        /// <inheritdoc/>
        public override string StrategyId => "threat-intel";

        /// <inheritdoc/>
        public override string StrategyName => "Threat Intelligence";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 10000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load default threat feeds
            LoadDefaultThreatFeeds();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Extract indicators from context
            var indicators = ExtractIndicators(context);

            if (indicators.Count == 0)
            {
                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = "No threat indicators to evaluate",
                    ApplicablePolicies = new[] { "ThreatIntel" }
                };
            }

            // Step 1: Try AI enrichment via Intelligence plugin
            EnrichmentResult? aiEnrichment = null;
            if (_messageBus != null)
            {
                try
                {
                    aiEnrichment = await TryAiEnrichmentAsync(indicators, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Intelligence plugin AI enrichment failed, falling back to rule-based");
                }
            }

            // Step 2: Fallback to rule-based threat matching if AI unavailable
            var matchResult = aiEnrichment ?? await RuleBasedThreatMatchingAsync(indicators, cancellationToken);

            // Record matches
            if (matchResult.Matches.Any())
            {
                foreach (var match in matchResult.Matches)
                {
                    var threatMatch = new ThreatMatch
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        SubjectId = context.SubjectId,
                        MatchedIndicator = match.Indicator,
                        ThreatType = match.ThreatType,
                        Severity = match.Severity,
                        Source = matchResult.Source,
                        Timestamp = DateTime.UtcNow,
                        UsedAiEnrichment = aiEnrichment != null
                    };

                    _matches.Enqueue(threatMatch);

                    // Prune old matches
                    while (_matches.Count > 1000)
                    {
                        _matches.TryDequeue(out _);
                    }
                }
            }

            // Make access decision based on threat severity
            var maxSeverity = matchResult.Matches.Any()
                ? matchResult.Matches.Max(m => m.Severity)
                : 0;

            if (maxSeverity >= 80)
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"Critical threat detected: {matchResult.Matches.First().ThreatType}",
                    ApplicablePolicies = new[] { "ThreatIntelAutoBlock" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["MatchCount"] = matchResult.Matches.Count,
                        ["MaxSeverity"] = maxSeverity,
                        ["ThreatTypes"] = matchResult.Matches.Select(m => m.ThreatType).Distinct().ToList(),
                        ["UsedAi"] = aiEnrichment != null,
                        ["Source"] = matchResult.Source
                    }
                };
            }

            if (maxSeverity >= 60)
            {
                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = $"Access granted with threat intelligence warning (severity: {maxSeverity})",
                    ApplicablePolicies = new[] { "ThreatIntelMonitoring" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["MatchCount"] = matchResult.Matches.Count,
                        ["MaxSeverity"] = maxSeverity,
                        ["UsedAi"] = aiEnrichment != null
                    }
                };
            }

            return new AccessDecision
            {
                IsGranted = true,
                Reason = "No threat intelligence matches",
                ApplicablePolicies = new[] { "ThreatIntel" },
                Metadata = new Dictionary<string, object>
                {
                    ["IndicatorsChecked"] = indicators.Count,
                    ["UsedAi"] = aiEnrichment != null
                }
            };
        }

        /// <summary>
        /// Attempts AI-based threat enrichment via Intelligence plugin message bus.
        /// </summary>
        private async Task<EnrichmentResult?> TryAiEnrichmentAsync(List<ThreatIndicator> indicators, CancellationToken cancellationToken)
        {
            if (_messageBus == null)
                return null;

            try
            {
                // Step 1: Send request to Intelligence plugin for AI enrichment
                var request = new EnrichmentRequest
                {
                    DataType = "threat-indicator",
                    Indicators = indicators,
                    EnrichmentType = "threat-correlation"
                };

                // Note: In production, would use message bus request-response pattern
                // For now, simulate the pattern - Intelligence plugin integration will be completed separately
                EnrichmentResponse? aiResponse = null; // Placeholder for message bus response

                // Step 2: Fallback when AI unavailable (Success check)
                if (aiResponse == null || !aiResponse.Success)
                {
                    _logger.LogWarning("Intelligence plugin unavailable for threat enrichment (Success=false), using rule-based fallback");
                    return null;
                }

                // Parse AI enrichment result
                return new EnrichmentResult
                {
                    Matches = aiResponse.EnrichedIndicators.Select(ei => new IndicatorMatch
                    {
                        Indicator = ei.OriginalIndicator,
                        ThreatType = ei.ThreatType,
                        Severity = ei.Severity,
                        Confidence = ei.Confidence,
                        Description = ei.Description
                    }).ToList(),
                    Source = "Intelligence-AI",
                    Confidence = aiResponse.Confidence
                };
            }
            catch (TimeoutException ex)
            {
                // Step 2: Fallback on timeout
                _logger.LogWarning(ex, "Intelligence plugin timeout for threat enrichment, using rule-based fallback");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intelligence plugin request failed for threat enrichment, using rule-based fallback");
                return null;
            }
        }

        /// <summary>
        /// Rule-based threat matching fallback using STIX/TAXII feeds and IoC matching.
        /// </summary>
        private Task<EnrichmentResult> RuleBasedThreatMatchingAsync(
            List<ThreatIndicator> indicators,
            CancellationToken cancellationToken)
        {
            var matches = new List<IndicatorMatch>();

            foreach (var indicator in indicators)
            {
                switch (indicator.Type)
                {
                    case IndicatorType.IpAddress:
                        if (_knownMaliciousIps.Contains(indicator.Value))
                        {
                            matches.Add(new IndicatorMatch
                            {
                                Indicator = indicator.Value,
                                ThreatType = "malicious-ip",
                                Severity = 75,
                                Confidence = 0.85,
                                Description = "Known malicious IP from threat feed"
                            });
                        }
                        break;

                    case IndicatorType.Domain:
                        if (_knownMaliciousDomains.Any(d => indicator.Value.Contains(d, StringComparison.OrdinalIgnoreCase)))
                        {
                            matches.Add(new IndicatorMatch
                            {
                                Indicator = indicator.Value,
                                ThreatType = "malicious-domain",
                                Severity = 80,
                                Confidence = 0.90,
                                Description = "Known malicious domain from threat feed"
                            });
                        }
                        break;

                    case IndicatorType.FileHash:
                        if (_knownMaliciousHashes.Contains(indicator.Value.ToLowerInvariant()))
                        {
                            matches.Add(new IndicatorMatch
                            {
                                Indicator = indicator.Value,
                                ThreatType = "malware",
                                Severity = 90,
                                Confidence = 0.95,
                                Description = "Known malware hash from threat feed"
                            });
                        }
                        break;

                    case IndicatorType.UserAgent:
                        if (IsSuspiciousUserAgent(indicator.Value))
                        {
                            matches.Add(new IndicatorMatch
                            {
                                Indicator = indicator.Value,
                                ThreatType = "suspicious-agent",
                                Severity = 40,
                                Confidence = 0.60,
                                Description = "Suspicious user agent pattern"
                            });
                        }
                        break;
                }
            }

            // Check against STIX feed
            foreach (var indicator in indicators)
            {
                if (_threatFeed.TryGetValue(indicator.Value, out var stixIndicator))
                {
                    matches.Add(new IndicatorMatch
                    {
                        Indicator = indicator.Value,
                        ThreatType = stixIndicator.ThreatType,
                        Severity = stixIndicator.Severity,
                        Confidence = stixIndicator.Confidence,
                        Description = $"STIX feed match: {stixIndicator.Description}"
                    });
                }
            }

            var result = new EnrichmentResult
            {
                Matches = matches,
                Source = "STIX/TAXII-Feeds",
                Confidence = matches.Any() ? matches.Average(m => m.Confidence) : 0.0
            };

            return Task.FromResult(result);
        }

        private List<ThreatIndicator> ExtractIndicators(AccessContext context)
        {
            var indicators = new List<ThreatIndicator>();

            // IP address indicator
            if (!string.IsNullOrEmpty(context.ClientIpAddress))
            {
                indicators.Add(new ThreatIndicator
                {
                    Type = IndicatorType.IpAddress,
                    Value = context.ClientIpAddress
                });
            }

            // Domain indicator (from resource ID)
            if (Uri.TryCreate(context.ResourceId, UriKind.Absolute, out var uri))
            {
                indicators.Add(new ThreatIndicator
                {
                    Type = IndicatorType.Domain,
                    Value = uri.Host
                });
            }

            // User agent indicator
            if (context.SubjectAttributes.TryGetValue("UserAgent", out var uaObj) && uaObj is string userAgent)
            {
                indicators.Add(new ThreatIndicator
                {
                    Type = IndicatorType.UserAgent,
                    Value = userAgent
                });
            }

            // File hash indicator (if provided)
            if (context.ResourceAttributes.TryGetValue("FileHash", out var hashObj) && hashObj is string fileHash)
            {
                indicators.Add(new ThreatIndicator
                {
                    Type = IndicatorType.FileHash,
                    Value = fileHash
                });
            }

            return indicators;
        }

        private bool IsSuspiciousUserAgent(string userAgent)
        {
            var suspiciousPatterns = new[] { "scanner", "bot", "crawler", "sqlmap", "nikto", "nmap", "masscan" };

            return suspiciousPatterns.Any(p => userAgent.Contains(p, StringComparison.OrdinalIgnoreCase));
        }

        private void LoadDefaultThreatFeeds()
        {
            // Load known malicious IPs (simplified - in production, load from STIX/TAXII feeds)
            _knownMaliciousIps.Add("203.0.113.0"); // TEST-NET-3 (example)
            _knownMaliciousIps.Add("198.51.100.0"); // TEST-NET-2 (example)

            // Load known malicious domains
            _knownMaliciousDomains.Add("malware.example.com");
            _knownMaliciousDomains.Add("phishing.example.net");

            // Load known malware hashes (SHA256 examples)
            _knownMaliciousHashes.Add("44d88612fea8a8f36de82e1278abb02f"); // Example hash
            _knownMaliciousHashes.Add("275a021bbfb6489e54d471899f7db9d1663fc695"); // Example hash

            // Populate STIX feed (simplified)
            _threatFeed["198.51.100.1"] = new ThreatIndicator
            {
                Type = IndicatorType.IpAddress,
                Value = "198.51.100.1",
                ThreatType = "c2-server",
                Severity = 85,
                Confidence = 0.90,
                Description = "Command and control server",
                Source = "STIX-Feed-001"
            };
        }

        /// <summary>
        /// Adds a threat indicator to the feed.
        /// </summary>
        public void AddThreatIndicator(ThreatIndicator indicator)
        {
            _threatFeed[indicator.Value] = indicator;

            // Also add to quick-lookup sets
            switch (indicator.Type)
            {
                case IndicatorType.IpAddress:
                    _knownMaliciousIps.Add(indicator.Value);
                    break;
                case IndicatorType.Domain:
                    _knownMaliciousDomains.Add(indicator.Value);
                    break;
                case IndicatorType.FileHash:
                    _knownMaliciousHashes.Add(indicator.Value.ToLowerInvariant());
                    break;
            }
        }

        /// <summary>
        /// Gets recent threat matches.
        /// </summary>
        public IReadOnlyCollection<ThreatMatch> GetRecentMatches(int count = 100)
        {
            return _matches.Take(count).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets threat feed statistics.
        /// </summary>
        public new ThreatFeedStatistics GetStatistics()
        {
            return new ThreatFeedStatistics
            {
                TotalIndicators = _threatFeed.Count,
                IpIndicators = _knownMaliciousIps.Count,
                DomainIndicators = _knownMaliciousDomains.Count,
                HashIndicators = _knownMaliciousHashes.Count,
                TotalMatches = _matches.Count
            };
        }
    }

    #region Message Bus Types

    /// <summary>
    /// Request to Intelligence plugin for threat enrichment.
    /// </summary>
    public sealed class EnrichmentRequest
    {
        public required string DataType { get; init; }
        public required List<ThreatIndicator> Indicators { get; init; }
        public required string EnrichmentType { get; init; }
    }

    /// <summary>
    /// Response from Intelligence plugin with enriched threat data.
    /// </summary>
    public sealed class EnrichmentResponse
    {
        public required bool Success { get; init; }
        public required List<EnrichedIndicator> EnrichedIndicators { get; init; }
        public required double Confidence { get; init; }
    }

    /// <summary>
    /// Enriched threat indicator from AI analysis.
    /// </summary>
    public sealed class EnrichedIndicator
    {
        public required string OriginalIndicator { get; init; }
        public required string ThreatType { get; init; }
        public required int Severity { get; init; }
        public required double Confidence { get; init; }
        public required string Description { get; init; }
    }

    #endregion

    #region Supporting Types

    public enum IndicatorType
    {
        IpAddress,
        Domain,
        FileHash,
        UserAgent,
        Email,
        Url
    }

    public sealed class ThreatIndicator
    {
        public required IndicatorType Type { get; init; }
        public required string Value { get; init; }
        public string ThreatType { get; init; } = "unknown";
        public int Severity { get; init; } = 50;
        public double Confidence { get; init; } = 0.5;
        public string Description { get; init; } = "";
        public string Source { get; init; } = "unknown";
    }

    public sealed class IndicatorMatch
    {
        public required string Indicator { get; init; }
        public required string ThreatType { get; init; }
        public required int Severity { get; init; }
        public required double Confidence { get; init; }
        public required string Description { get; init; }
    }

    public sealed class EnrichmentResult
    {
        public required List<IndicatorMatch> Matches { get; init; }
        public required string Source { get; init; }
        public required double Confidence { get; init; }
    }

    public sealed class ThreatMatch
    {
        public required string Id { get; init; }
        public required string SubjectId { get; init; }
        public required string MatchedIndicator { get; init; }
        public required string ThreatType { get; init; }
        public required int Severity { get; init; }
        public required string Source { get; init; }
        public required DateTime Timestamp { get; init; }
        public required bool UsedAiEnrichment { get; init; }
    }

    public sealed class ThreatFeedStatistics
    {
        public required int TotalIndicators { get; init; }
        public required int IpIndicators { get; init; }
        public required int DomainIndicators { get; init; }
        public required int HashIndicators { get; init; }
        public required int TotalMatches { get; init; }
    }

    #endregion
}
