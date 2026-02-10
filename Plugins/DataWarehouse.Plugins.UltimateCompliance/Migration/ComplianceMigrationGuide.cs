using System;
using System.Collections.Generic;

namespace DataWarehouse.Plugins.UltimateCompliance.Migration
{
    /// <summary>
    /// Migration guide for transitioning from individual compliance plugins
    /// to the unified UltimateCompliance plugin.
    /// </summary>
    public static class ComplianceMigrationGuide
    {
        /// <summary>
        /// Maps legacy plugin IDs to new UltimateCompliance strategy IDs.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> PluginToStrategyMapping = new Dictionary<string, string>
        {
            // Privacy Compliance
            ["gdpr-compliance"] = "gdpr-compliance",
            ["ccpa-compliance"] = "ccpa-compliance",
            ["hipaa-privacy"] = "hipaa-privacy-rule",

            // Financial Compliance
            ["sox-compliance"] = "sox-compliance",
            ["pci-dss"] = "pci-dss-compliance",
            ["glba-compliance"] = "glba-compliance",

            // Security Frameworks
            ["iso27001"] = "iso-27001-isms",
            ["nist-800-53"] = "nist-800-53-controls",
            ["cis-controls"] = "cis-controls-v8",

            // Regional Compliance
            ["gdpr-eu"] = "gdpr-compliance",
            ["lgpd-brazil"] = "lgpd-compliance",
            ["pdpa-singapore"] = "pdpa-singapore",

            // Industry-Specific
            ["finra-compliance"] = "finra-worm-compliance",
            ["sec-17a-4"] = "sec-17a-4-worm",
            ["fedramp"] = "fedramp-compliance"
        };

        /// <summary>
        /// Deprecation notices for individual compliance plugins.
        /// </summary>
        public const string DeprecationNotice = @"
DEPRECATION NOTICE: Individual Compliance Plugins

The following individual compliance plugins are deprecated as of this release
and have been consolidated into the UltimateCompliance plugin:

- DataWarehouse.Plugins.GdprCompliance
- DataWarehouse.Plugins.HipaaCompliance
- DataWarehouse.Plugins.SoxCompliance
- DataWarehouse.Plugins.PciDssCompliance
- DataWarehouse.Plugins.Iso27001Compliance
- DataWarehouse.Plugins.NistCompliance
- DataWarehouse.Plugins.CcpaCompliance

MIGRATION PATH:
1. Uninstall individual compliance plugins
2. Install DataWarehouse.Plugins.UltimateCompliance
3. Update configuration to use strategy IDs (see PluginToStrategyMapping)
4. Test compliance checks with new unified plugin

BENEFITS OF MIGRATION:
- Unified compliance management across all frameworks
- Cross-framework control mapping
- Advanced features: continuous monitoring, automated remediation, gap analysis
- Better performance with shared compliance infrastructure
- WORM and Innovation strategies for advanced use cases

TIMELINE:
- Current Release: Both individual and UltimateCompliance plugins supported
- Next Release: Individual plugins will show deprecation warnings
- Two Releases: Individual plugins will be removed

For questions or migration assistance, contact: compliance-support@datawarehouse.com
";

        /// <summary>
        /// Configuration migration examples.
        /// </summary>
        public const string ConfigurationMigrationExample = @"
CONFIGURATION MIGRATION EXAMPLE

BEFORE (Individual Plugin):
{
  ""plugins"": {
    ""gdpr-compliance"": {
      ""enabled"": true,
      ""dataRetentionDays"": 365
    }
  }
}

AFTER (UltimateCompliance):
{
  ""plugins"": {
    ""ultimate-compliance"": {
      ""enabled"": true,
      ""strategies"": [
        {
          ""strategyId"": ""gdpr-compliance"",
          ""configuration"": {
            ""dataRetentionDays"": 365
          }
        }
      ]
    }
  }
}
";

        /// <summary>
        /// Gets the recommended strategy ID for a legacy plugin ID.
        /// </summary>
        public static string? GetMigratedStrategyId(string legacyPluginId)
        {
            PluginToStrategyMapping.TryGetValue(legacyPluginId, out var strategyId);
            return strategyId;
        }

        /// <summary>
        /// Checks if a plugin ID is deprecated.
        /// </summary>
        public static bool IsDeprecated(string pluginId)
        {
            return PluginToStrategyMapping.ContainsKey(pluginId);
        }
    }
}
