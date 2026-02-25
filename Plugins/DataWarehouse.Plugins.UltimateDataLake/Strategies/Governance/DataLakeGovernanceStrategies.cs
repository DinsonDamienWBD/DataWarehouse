using DataWarehouse.SDK.Contracts.DataLake;

namespace DataWarehouse.Plugins.UltimateDataLake.Strategies.Governance;

/// <summary>
/// Data Quality Rules Strategy - Define and enforce quality rules.
/// Implements 112.7: Data lake governance.
/// </summary>
public sealed class DataQualityRulesStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "data-quality-rules";
    public override string DisplayName => "Data Quality Rules";
    public override DataLakeCategory Category => DataLakeCategory.Governance;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = false,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = false,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Define and enforce data quality rules: completeness, uniqueness, validity, consistency, timeliness, and accuracy. " +
        "Supports rule templates, custom SQL expressions, and anomaly detection.";
    public override string[] Tags => ["quality-rules", "validation", "completeness", "uniqueness", "accuracy"];
}

/// <summary>
/// Data Profiling Strategy - Automated data profiling.
/// Implements 112.7: Data lake governance.
/// </summary>
public sealed class DataProfilingStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "data-profiling";
    public override string DisplayName => "Data Profiling";
    public override DataLakeCategory Category => DataLakeCategory.Governance;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = false,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = false,
        SupportedFormats = ["parquet", "delta", "csv", "json"]
    };
    public override string SemanticDescription =>
        "Automated data profiling to understand data characteristics: distributions, patterns, outliers, " +
        "null rates, cardinality, and statistical summaries for data discovery and quality assessment.";
    public override string[] Tags => ["profiling", "statistics", "patterns", "distributions", "discovery"];
}

/// <summary>
/// Data Retention Policy Strategy - Lifecycle and retention management.
/// Implements 112.7: Data lake governance.
/// </summary>
public sealed class DataRetentionPolicyStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "data-retention-policy";
    public override string DisplayName => "Data Retention Policy";
    public override DataLakeCategory Category => DataLakeCategory.Governance;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = true,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Data retention policy management with automated archival, deletion, and legal hold capabilities. " +
        "Supports compliance requirements with configurable retention periods by data classification.";
    public override string[] Tags => ["retention", "lifecycle", "archival", "legal-hold", "compliance"];
}

/// <summary>
/// PII Detection Strategy - Automated sensitive data discovery.
/// Implements 112.7: Data lake governance.
/// </summary>
public sealed class PiiDetectionStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "pii-detection";
    public override string DisplayName => "PII Detection";
    public override DataLakeCategory Category => DataLakeCategory.Governance;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = false,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = false,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Automated PII and sensitive data detection using pattern matching, ML classifiers, and Named Entity Recognition. " +
        "Identifies SSN, credit cards, emails, phone numbers, addresses, and custom patterns.";
    public override string[] Tags => ["pii", "sensitive-data", "detection", "classification", "ml"];
}

/// <summary>
/// Data Stewardship Strategy - Data ownership and stewardship.
/// Implements 112.7: Data lake governance.
/// </summary>
public sealed class DataStewardshipStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "data-stewardship";
    public override string DisplayName => "Data Stewardship";
    public override DataLakeCategory Category => DataLakeCategory.Governance;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = true,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Data stewardship management for assigning data owners, stewards, and custodians. " +
        "Tracks responsibilities, approval workflows, and data domain governance.";
    public override string[] Tags => ["stewardship", "ownership", "workflow", "domain", "responsibility"];
}

/// <summary>
/// Compliance Automation Strategy - Regulatory compliance automation.
/// Implements 112.7: Data lake governance.
/// </summary>
public sealed class ComplianceAutomationStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "compliance-automation";
    public override string DisplayName => "Compliance Automation";
    public override DataLakeCategory Category => DataLakeCategory.Governance;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = true,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Automated compliance for GDPR, CCPA, HIPAA, SOX, and other regulations. Includes data subject requests, " +
        "consent management, right to erasure, and compliance reporting.";
    public override string[] Tags => ["compliance", "gdpr", "ccpa", "hipaa", "sox", "automation"];
}

/// <summary>
/// Data Contract Strategy - Producer-consumer data contracts.
/// Implements 112.7: Data lake governance.
/// </summary>
public sealed class DataContractStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "data-contract";
    public override string DisplayName => "Data Contracts";
    public override DataLakeCategory Category => DataLakeCategory.Governance;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["yaml", "json"]
    };
    public override string SemanticDescription =>
        "Data contracts defining producer-consumer agreements including schema specifications, quality SLAs, " +
        "freshness requirements, and change notification policies.";
    public override string[] Tags => ["data-contract", "sla", "schema", "producer", "consumer"];
}
