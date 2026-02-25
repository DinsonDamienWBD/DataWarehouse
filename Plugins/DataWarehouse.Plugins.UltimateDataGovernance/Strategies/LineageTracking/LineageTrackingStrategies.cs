namespace DataWarehouse.Plugins.UltimateDataGovernance.Strategies.LineageTracking;

public sealed class ColumnLevelLineageStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "column-level-lineage";
    public override string DisplayName => "Column-Level Lineage";
    public override GovernanceCategory Category => GovernanceCategory.LineageTracking;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Track data lineage at column/field level with transformation history";
    public override string[] Tags => ["lineage", "column-level", "transformation"];
}

public sealed class TableLevelLineageStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "table-level-lineage";
    public override string DisplayName => "Table-Level Lineage";
    public override GovernanceCategory Category => GovernanceCategory.LineageTracking;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Track data lineage at table/dataset level";
    public override string[] Tags => ["lineage", "table-level", "dataset"];
}

public sealed class ImpactAnalysisStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "impact-analysis";
    public override string DisplayName => "Impact Analysis";
    public override GovernanceCategory Category => GovernanceCategory.LineageTracking;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = false, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Analyze impact of changes on downstream data consumers";
    public override string[] Tags => ["lineage", "impact-analysis", "dependencies"];
}

public sealed class DependencyMappingStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "dependency-mapping";
    public override string DisplayName => "Dependency Mapping";
    public override GovernanceCategory Category => GovernanceCategory.LineageTracking;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Map data dependencies and relationships across systems";
    public override string[] Tags => ["lineage", "dependencies", "relationships"];
}

public sealed class LineageVisualizationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "lineage-visualization";
    public override string DisplayName => "Lineage Visualization";
    public override GovernanceCategory Category => GovernanceCategory.LineageTracking;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = false, SupportsRealTime = true, SupportsAudit = false, SupportsVersioning = false };
    public override string SemanticDescription => "Visual representation of data lineage and flow";
    public override string[] Tags => ["lineage", "visualization", "graph"];
}

public sealed class AutomatedLineageCaptureStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "automated-lineage-capture";
    public override string DisplayName => "Automated Lineage Capture";
    public override GovernanceCategory Category => GovernanceCategory.LineageTracking;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = false };
    public override string SemanticDescription => "Automatically capture lineage from ETL processes and queries";
    public override string[] Tags => ["lineage", "automation", "capture"];
}

public sealed class LineageSearchStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "lineage-search";
    public override string DisplayName => "Lineage Search";
    public override GovernanceCategory Category => GovernanceCategory.LineageTracking;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = false, SupportsVersioning = false };
    public override string SemanticDescription => "Search and query lineage metadata and relationships";
    public override string[] Tags => ["lineage", "search", "query"];
}

public sealed class CrossPlatformLineageStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "cross-platform-lineage";
    public override string DisplayName => "Cross-Platform Lineage";
    public override GovernanceCategory Category => GovernanceCategory.LineageTracking;
    public override DataGovernanceCapabilities Capabilities => new() { SupportsAsync = true, SupportsBatch = true, SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true };
    public override string SemanticDescription => "Track lineage across multiple platforms and data stores";
    public override string[] Tags => ["lineage", "cross-platform", "federation"];
}
