namespace DataWarehouse.Plugins.UltimateDataLineage.Strategies;

/// <summary>
/// Blast radius impact analysis strategy.
/// </summary>
public sealed class BlastRadiusStrategy : LineageStrategyBase
{
    public override string StrategyId => "impact-blast-radius";
    public override string DisplayName => "Blast Radius Analyzer";
    public override LineageCategory Category => LineageCategory.Impact;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = false, SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = true, SupportsVisualization = true,
        SupportsRealTime = false
    };
    public override string SemanticDescription =>
        "Blast radius analyzer that calculates the full extent of impact from changes, " +
        "considering direct dependencies, transitive dependencies, and criticality.";
    public override string[] Tags => ["impact", "blast-radius", "dependencies", "criticality"];
}

/// <summary>
/// DAG visualization export strategy.
/// </summary>
public sealed class DagVisualizationStrategy : LineageStrategyBase
{
    public override string StrategyId => "visualization-dag";
    public override string DisplayName => "DAG Visualization";
    public override LineageCategory Category => LineageCategory.Visualization;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = true, SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = false, SupportsVisualization = true,
        SupportsRealTime = false
    };
    public override string SemanticDescription =>
        "DAG visualization strategy that exports lineage graphs in formats suitable " +
        "for rendering (DOT, Mermaid, D3.js) with layout optimization.";
    public override string[] Tags => ["visualization", "dag", "graph", "export", "rendering"];
}

/// <summary>
/// Cryptographic provenance chain strategy.
/// </summary>
public sealed class CryptoProvenanceStrategy : LineageStrategyBase
{
    public override string StrategyId => "provenance-crypto";
    public override string DisplayName => "Cryptographic Provenance Chain";
    public override LineageCategory Category => LineageCategory.Provenance;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = true, SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = false, SupportsVisualization = false,
        SupportsRealTime = true
    };
    public override string SemanticDescription =>
        "Cryptographic provenance chain using hash chains and Merkle trees " +
        "for tamper-evident audit trails and data integrity verification.";
    public override string[] Tags => ["provenance", "cryptographic", "hash-chain", "merkle", "integrity"];

    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default)
    {
        // Retrieve the previous record's AfterHash to chain to
        var existingProvenance = GetProvenance(record.DataObjectId);
        var previousHash = existingProvenance.Count > 0
            ? existingProvenance[^1].AfterHash
            : null;

        // Compute BeforeHash from previous state + current record data
        var beforeHash = previousHash != null
            ? ComputeHash(previousHash + record.DataObjectId)
            : ComputeHash("genesis:" + record.DataObjectId);

        // AfterHash includes the actual change content for tamper detection
        var contentKey = string.Concat(record.DataObjectId, record.Timestamp.Ticks, record.Operation);
        var afterHash = ComputeHash(beforeHash + contentKey);

        return base.TrackAsync(record with
        {
            BeforeHash = beforeHash,
            AfterHash = afterHash
        }, ct);
    }

    private static string ComputeHash(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Audit trail strategy for compliance.
/// </summary>
public sealed class AuditTrailStrategy : LineageStrategyBase
{
    public override string StrategyId => "audit-trail";
    public override string DisplayName => "Audit Trail Tracker";
    public override LineageCategory Category => LineageCategory.Audit;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = true, SupportsSchemaEvolution = true,
        SupportsImpactAnalysis = false, SupportsVisualization = true,
        SupportsRealTime = true
    };
    public override string SemanticDescription =>
        "Comprehensive audit trail tracker capturing who accessed what data when, " +
        "with immutable logging for regulatory compliance.";
    public override string[] Tags => ["audit", "trail", "compliance", "access", "logging"];
}

/// <summary>
/// GDPR data subject lineage strategy.
/// </summary>
public sealed class GdprLineageStrategy : LineageStrategyBase
{
    public override string StrategyId => "compliance-gdpr";
    public override string DisplayName => "GDPR Data Subject Lineage";
    public override LineageCategory Category => LineageCategory.Compliance;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = true, SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = true, SupportsVisualization = true,
        SupportsRealTime = true
    };
    public override string SemanticDescription =>
        "GDPR-specific lineage tracking for data subject requests, enabling " +
        "right to be forgotten (RTBF) and data portability compliance.";
    public override string[] Tags => ["gdpr", "compliance", "data-subject", "rtbf", "portability"];
}

/// <summary>
/// ML pipeline lineage strategy.
/// </summary>
public sealed class MlPipelineLineageStrategy : LineageStrategyBase
{
    public override string StrategyId => "transformation-ml";
    public override string DisplayName => "ML Pipeline Lineage";
    public override LineageCategory Category => LineageCategory.Transformation;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = true, SupportsSchemaEvolution = true,
        SupportsImpactAnalysis = true, SupportsVisualization = true,
        SupportsRealTime = true
    };
    public override string SemanticDescription =>
        "ML pipeline lineage tracker for machine learning workflows, capturing " +
        "feature engineering, model training, and inference dependencies.";
    public override string[] Tags => ["ml", "machine-learning", "pipeline", "features", "model"];
}

/// <summary>
/// Schema evolution tracking strategy.
/// </summary>
public sealed class SchemaEvolutionStrategy : LineageStrategyBase
{
    public override string StrategyId => "origin-schema";
    public override string DisplayName => "Schema Evolution Tracker";
    public override LineageCategory Category => LineageCategory.Origin;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = false, SupportsSchemaEvolution = true,
        SupportsImpactAnalysis = true, SupportsVisualization = true,
        SupportsRealTime = false
    };
    public override string SemanticDescription =>
        "Schema evolution tracker that maintains history of schema changes, " +
        "compatibility analysis, and migration paths.";
    public override string[] Tags => ["schema", "evolution", "versioning", "compatibility", "migration"];
}

/// <summary>
/// External source origin tracking strategy.
/// </summary>
public sealed class ExternalSourceStrategy : LineageStrategyBase
{
    public override string StrategyId => "origin-external";
    public override string DisplayName => "External Source Tracker";
    public override LineageCategory Category => LineageCategory.Origin;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = false,
        SupportsTransformations = false, SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = true, SupportsVisualization = true,
        SupportsRealTime = true
    };
    public override string SemanticDescription =>
        "External source tracker for data ingested from third-party systems, " +
        "APIs, files, and streaming sources with SLA tracking.";
    public override string[] Tags => ["origin", "external", "ingestion", "source", "sla"];
}
