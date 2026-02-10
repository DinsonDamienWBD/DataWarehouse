using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.Plugins.UltimateCompliance.Features
{
    /// <summary>
    /// Maps controls across compliance frameworks (GDPR, ISO 27001, NIST 800-53, SOX, etc.)
    /// and provides overlap analysis.
    /// </summary>
    public sealed class CrossFrameworkMapper
    {
        private readonly Dictionary<string, List<FrameworkControl>> _controlMappings = new();
        private readonly HashSet<ControlEquivalence> _equivalences = new();

        /// <summary>
        /// Initializes the mapper with standard framework mappings.
        /// </summary>
        public CrossFrameworkMapper()
        {
            InitializeStandardMappings();
        }

        /// <summary>
        /// Adds a control equivalence mapping between two frameworks.
        /// </summary>
        public void AddEquivalence(string framework1, string control1, string framework2, string control2, double similarityScore = 1.0)
        {
            var equivalence = new ControlEquivalence
            {
                Framework1 = framework1,
                Control1 = control1,
                Framework2 = framework2,
                Control2 = control2,
                SimilarityScore = similarityScore
            };

            _equivalences.Add(equivalence);

            AddControlToMapping(framework1, control1, framework2, control2);
            AddControlToMapping(framework2, control2, framework1, control1);
        }

        /// <summary>
        /// Gets equivalent controls across frameworks.
        /// </summary>
        public IReadOnlyList<FrameworkControl> GetEquivalentControls(string framework, string control)
        {
            var key = $"{framework}:{control}";
            if (_controlMappings.TryGetValue(key, out var mappings))
            {
                return mappings;
            }
            return Array.Empty<FrameworkControl>();
        }

        /// <summary>
        /// Analyzes control overlap between two frameworks.
        /// </summary>
        public OverlapAnalysis AnalyzeOverlap(string framework1, string framework2)
        {
            var framework1Controls = _equivalences
                .Where(e => e.Framework1 == framework1 || e.Framework2 == framework1)
                .Select(e => e.Framework1 == framework1 ? e.Control1 : e.Control2)
                .Distinct()
                .ToList();

            var framework2Controls = _equivalences
                .Where(e => e.Framework1 == framework2 || e.Framework2 == framework2)
                .Select(e => e.Framework1 == framework2 ? e.Control1 : e.Control2)
                .Distinct()
                .ToList();

            var mappedControls = _equivalences
                .Where(e => (e.Framework1 == framework1 && e.Framework2 == framework2) ||
                           (e.Framework1 == framework2 && e.Framework2 == framework1))
                .ToList();

            var overlapPercentage = framework1Controls.Count > 0
                ? (double)mappedControls.Count / framework1Controls.Count * 100.0
                : 0.0;

            return new OverlapAnalysis
            {
                Framework1 = framework1,
                Framework2 = framework2,
                Framework1ControlCount = framework1Controls.Count,
                Framework2ControlCount = framework2Controls.Count,
                MappedControlCount = mappedControls.Count,
                OverlapPercentage = overlapPercentage,
                AverageSimilarity = mappedControls.Any() ? mappedControls.Average(m => m.SimilarityScore) : 0.0
            };
        }

        /// <summary>
        /// Gets all frameworks that have mappings to a given framework.
        /// </summary>
        public IReadOnlyList<string> GetRelatedFrameworks(string framework)
        {
            return _equivalences
                .Where(e => e.Framework1 == framework || e.Framework2 == framework)
                .Select(e => e.Framework1 == framework ? e.Framework2 : e.Framework1)
                .Distinct()
                .OrderBy(f => f)
                .ToList();
        }

        private void AddControlToMapping(string sourceFramework, string sourceControl, string targetFramework, string targetControl)
        {
            var key = $"{sourceFramework}:{sourceControl}";
            if (!_controlMappings.ContainsKey(key))
            {
                _controlMappings[key] = new List<FrameworkControl>();
            }

            _controlMappings[key].Add(new FrameworkControl
            {
                Framework = targetFramework,
                ControlId = targetControl
            });
        }

        private void InitializeStandardMappings()
        {
            // GDPR to ISO 27001 mappings
            AddEquivalence("GDPR", "Art.32", "ISO27001", "A.8", 0.9);
            AddEquivalence("GDPR", "Art.33", "ISO27001", "A.16.1.2", 0.95);
            AddEquivalence("GDPR", "Art.35", "ISO27001", "A.18.1.5", 0.85);

            // GDPR to NIST 800-53 mappings
            AddEquivalence("GDPR", "Art.32", "NIST800-53", "SC-8", 0.9);
            AddEquivalence("GDPR", "Art.32", "NIST800-53", "SC-13", 0.85);
            AddEquivalence("GDPR", "Art.33", "NIST800-53", "IR-6", 0.95);

            // ISO 27001 to NIST 800-53 mappings
            AddEquivalence("ISO27001", "A.8", "NIST800-53", "SC-8", 0.9);
            AddEquivalence("ISO27001", "A.9.4.1", "NIST800-53", "AC-2", 0.85);
            AddEquivalence("ISO27001", "A.12.4.1", "NIST800-53", "AU-2", 0.9);

            // SOX to ISO 27001 mappings
            AddEquivalence("SOX", "404", "ISO27001", "A.12.4", 0.8);
            AddEquivalence("SOX", "302", "ISO27001", "A.18.1", 0.75);

            // HIPAA to NIST 800-53 mappings
            AddEquivalence("HIPAA", "164.312(a)(1)", "NIST800-53", "AC-1", 0.9);
            AddEquivalence("HIPAA", "164.312(b)", "NIST800-53", "AU-2", 0.85);
        }
    }

    /// <summary>
    /// Represents a framework control identifier.
    /// </summary>
    public sealed class FrameworkControl
    {
        public required string Framework { get; init; }
        public required string ControlId { get; init; }
    }

    /// <summary>
    /// Represents an equivalence mapping between two controls.
    /// </summary>
    public sealed class ControlEquivalence
    {
        public required string Framework1 { get; init; }
        public required string Control1 { get; init; }
        public required string Framework2 { get; init; }
        public required string Control2 { get; init; }
        public required double SimilarityScore { get; init; }

        public override bool Equals(object? obj)
        {
            if (obj is not ControlEquivalence other)
                return false;

            return (Framework1 == other.Framework1 && Control1 == other.Control1 &&
                   Framework2 == other.Framework2 && Control2 == other.Control2) ||
                   (Framework1 == other.Framework2 && Control1 == other.Control2 &&
                   Framework2 == other.Framework1 && Control2 == other.Control1);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Framework1, Control1, Framework2, Control2);
        }
    }

    /// <summary>
    /// Analysis of control overlap between two frameworks.
    /// </summary>
    public sealed class OverlapAnalysis
    {
        public required string Framework1 { get; init; }
        public required string Framework2 { get; init; }
        public required int Framework1ControlCount { get; init; }
        public required int Framework2ControlCount { get; init; }
        public required int MappedControlCount { get; init; }
        public required double OverlapPercentage { get; init; }
        public required double AverageSimilarity { get; init; }
    }
}
