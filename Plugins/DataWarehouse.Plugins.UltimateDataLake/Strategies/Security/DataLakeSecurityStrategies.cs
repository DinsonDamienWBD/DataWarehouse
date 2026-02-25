using DataWarehouse.SDK.Contracts.DataLake;

namespace DataWarehouse.Plugins.UltimateDataLake.Strategies.Security;

/// <summary>
/// Row-Level Security Strategy - Fine-grained row access control.
/// Implements 112.6: Data lake security.
/// </summary>
public sealed class RowLevelSecurityStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "row-level-security";
    public override string DisplayName => "Row-Level Security";
    public override DataLakeCategory Category => DataLakeCategory.Security;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["delta", "iceberg", "parquet"]
    };
    public override string SemanticDescription =>
        "Row-level security (RLS) enabling fine-grained access control at the row level based on user attributes, " +
        "groups, or custom predicates. Users only see rows they are authorized to access.";
    public override string[] Tags => ["row-level-security", "rls", "fine-grained", "predicate"];
}

/// <summary>
/// Column-Level Security Strategy - Column masking and access.
/// Implements 112.6: Data lake security.
/// </summary>
public sealed class ColumnLevelSecurityStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "column-level-security";
    public override string DisplayName => "Column-Level Security";
    public override DataLakeCategory Category => DataLakeCategory.Security;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = true,
        SupportsTimeTravel = true,
        SupportedFormats = ["delta", "iceberg", "parquet"]
    };
    public override string SemanticDescription =>
        "Column-level security with access control and dynamic data masking. Hide, mask, or redact " +
        "sensitive columns based on user permissions and data classification.";
    public override string[] Tags => ["column-level-security", "cls", "masking", "redaction"];
}

/// <summary>
/// Data Masking Strategy - Dynamic data protection.
/// Implements 112.6: Data lake security.
/// </summary>
public sealed class DataMaskingStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "data-masking";
    public override string DisplayName => "Dynamic Data Masking";
    public override DataLakeCategory Category => DataLakeCategory.Security;
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
        "Dynamic data masking with multiple masking functions: full mask, partial mask (showing last 4 digits), " +
        "hash, tokenization, and custom masking functions for PII and sensitive data protection.";
    public override string[] Tags => ["masking", "pii", "tokenization", "hash", "redaction"];
}

/// <summary>
/// Encryption at Rest Strategy - Data lake encryption.
/// Implements 112.6: Data lake security.
/// </summary>
public sealed class EncryptionAtRestStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "encryption-at-rest";
    public override string DisplayName => "Encryption at Rest";
    public override DataLakeCategory Category => DataLakeCategory.Security;
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
        "Encryption at rest using server-side encryption (SSE), client-side encryption (CSE), or " +
        "transparent data encryption (TDE) with key management integration.";
    public override string[] Tags => ["encryption", "sse", "cse", "kms", "at-rest"];
}

/// <summary>
/// RBAC Strategy - Role-based access control.
/// Implements 112.6: Data lake security.
/// </summary>
public sealed class RbacStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "rbac";
    public override string DisplayName => "Role-Based Access Control";
    public override DataLakeCategory Category => DataLakeCategory.Security;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = false,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Role-based access control (RBAC) for managing permissions through roles assigned to users and groups. " +
        "Supports role hierarchies, permission inheritance, and separation of duties.";
    public override string[] Tags => ["rbac", "roles", "permissions", "groups", "hierarchy"];
}

/// <summary>
/// ABAC Strategy - Attribute-based access control.
/// Implements 112.6: Data lake security.
/// </summary>
public sealed class AbacStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "abac";
    public override string DisplayName => "Attribute-Based Access Control";
    public override DataLakeCategory Category => DataLakeCategory.Security;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsAcid = true,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = false,
        SupportedFormats = ["*"]
    };
    public override string SemanticDescription =>
        "Attribute-based access control (ABAC) using subject attributes (user department, clearance), " +
        "resource attributes (sensitivity, classification), and environmental attributes (time, location).";
    public override string[] Tags => ["abac", "attributes", "policies", "dynamic", "context-aware"];
}

/// <summary>
/// Audit Logging Strategy - Security audit trail.
/// Implements 112.6: Data lake security.
/// </summary>
public sealed class AuditLoggingStrategy : DataLakeStrategyBase
{
    public override string StrategyId => "audit-logging";
    public override string DisplayName => "Audit Logging";
    public override DataLakeCategory Category => DataLakeCategory.Security;
    public override DataLakeCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsAcid = true,
        SupportsSchemaEvolution = false,
        SupportsTimeTravel = true,
        SupportedFormats = ["json", "parquet"]
    };
    public override string SemanticDescription =>
        "Comprehensive audit logging capturing all data access, modifications, and administrative actions. " +
        "Supports compliance requirements (SOX, HIPAA, GDPR) with tamper-proof log storage.";
    public override string[] Tags => ["audit", "logging", "compliance", "tamper-proof", "forensics"];
}
