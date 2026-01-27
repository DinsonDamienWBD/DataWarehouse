// ComplianceModels.cs - Shared DTOs for compliance reporting
// Used by both CLI and GUI for feature parity

using System;
using System.Collections.Generic;

namespace DataWarehouse.Shared.Models;

#region GDPR Models

public class GdprComplianceReport
{
    public string ReportId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime ReportingPeriodStart { get; set; }
    public DateTime ReportingPeriodEnd { get; set; }
    public int TotalConsentRecords { get; set; }
    public int ActiveConsents { get; set; }
    public int WithdrawnConsents { get; set; }
    public int DataSubjectRequests { get; set; }
    public int DataBreaches { get; set; }
    public int RetentionPolicies { get; set; }
    public bool IsCompliant { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<ComplianceViolation> Violations { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class DataSubjectRequest
{
    public string RequestId { get; set; } = string.Empty;
    public string DataSubjectId { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty; // Access, Erasure, Portability, Rectification
    public DateTime RequestedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Processing, Completed, Rejected
    public DateTime? Deadline { get; set; }
}

public class ConsentRecord
{
    public string ConsentId { get; set; } = string.Empty;
    public string DataSubjectId { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string LawfulBasis { get; set; } = string.Empty;
    public DateTime GrantedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? WithdrawnAt { get; set; }
    public string Status { get; set; } = "Active"; // Active, Withdrawn, Expired
    public string CollectionMethod { get; set; } = string.Empty;
}

public class DataBreachIncident
{
    public string BreachId { get; set; } = string.Empty;
    public DateTime DiscoveredAt { get; set; }
    public DateTime ReportedAt { get; set; }
    public string Description { get; set; } = string.Empty;
    public int EstimatedAffectedSubjects { get; set; }
    public string[] AffectedDataCategories { get; set; } = Array.Empty<string>();
    public string Severity { get; set; } = "Low"; // Low, Medium, High, Critical
    public bool NotificationRequired { get; set; }
    public DateTime? NotificationDeadline { get; set; }
    public string Status { get; set; } = "Investigating";
}

public class PersonalDataInventory
{
    public string InventoryId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<DataCategory> DataCategories { get; set; } = new();
    public int TotalDataSubjects { get; set; }
    public int TotalDataProcessingActivities { get; set; }
}

public class DataCategory
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = "Low";
    public int RecordCount { get; set; }
    public List<string> ProcessingPurposes { get; set; } = new();
}

#endregion

#region HIPAA Models

public class HipaaAuditReport
{
    public string ReportId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime ReportingPeriodStart { get; set; }
    public DateTime ReportingPeriodEnd { get; set; }
    public int TotalPhiAccessEvents { get; set; }
    public int UniqueUsers { get; set; }
    public int UniquePatients { get; set; }
    public int ActiveAuthorizations { get; set; }
    public int BusinessAssociateAgreements { get; set; }
    public int DataBreaches { get; set; }
    public bool IsCompliant { get; set; }
    public EncryptionStatus EncryptionStatus { get; set; } = new();
    public List<SecurityRiskAssessment> RiskAssessments { get; set; } = new();
    public List<ComplianceViolation> Violations { get; set; } = new();
}

public class PhiAccessLog
{
    public string LogId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string PatientId { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string AccessType { get; set; } = string.Empty; // Read, Write, Delete
    public string Purpose { get; set; } = string.Empty;
    public DateTime AccessedAt { get; set; }
    public string? WorkstationId { get; set; }
    public string? IpAddress { get; set; }
    public bool AccessGranted { get; set; }
    public string? DenialReason { get; set; }
    public bool FlaggedForReview { get; set; }
}

public class BusinessAssociateAgreement
{
    public string BaaId { get; set; } = string.Empty;
    public string BusinessAssociateId { get; set; } = string.Empty;
    public string BusinessAssociateName { get; set; } = string.Empty;
    public string[] Services { get; set; } = Array.Empty<string>();
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string Status { get; set; } = "Active"; // Active, Terminated, Pending
    public DateTime RegisteredAt { get; set; }
}

public class EncryptionStatus
{
    public bool AtRestEnabled { get; set; }
    public bool InTransitEnabled { get; set; }
    public string Algorithm { get; set; } = string.Empty;
    public bool KeyManagementCompliant { get; set; }
    public int TotalEncryptedResources { get; set; }
    public int NonCompliantResources { get; set; }
    public DateTime LastVerified { get; set; }
}

public class SecurityRiskAssessment
{
    public string AssessmentId { get; set; } = string.Empty;
    public DateTime ConductedAt { get; set; }
    public string AssessedBy { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = "Low"; // Low, Medium, High, Critical
    public List<SecurityRisk> Risks { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

public class SecurityRisk
{
    public string RiskId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Severity { get; set; } = "Low";
    public string Category { get; set; } = string.Empty;
    public string MitigationStatus { get; set; } = "Open";
}

#endregion

#region SOC2 Models

public class Soc2ComplianceReport
{
    public string ReportId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime ReportingPeriodStart { get; set; }
    public DateTime ReportingPeriodEnd { get; set; }
    public string ReportType { get; set; } = "Type II"; // Type I or Type II
    public List<string> TrustServiceCategories { get; set; } = new(); // Security, Availability, Processing Integrity, Confidentiality, Privacy
    public int TotalControls { get; set; }
    public int PassingControls { get; set; }
    public int FailingControls { get; set; }
    public double ComplianceScore { get; set; }
    public bool IsCompliant { get; set; }
    public AuditReadinessScore AuditReadiness { get; set; } = new();
    public List<ControlAssessment> ControlAssessments { get; set; } = new();
}

public class TrustServiceCriteria
{
    public string CriteriaId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // CC1-CC9, A1, PI1, C1, P1-P8
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "Not Tested"; // Not Tested, Effective, Ineffective, Not Applicable
    public DateTime? LastTested { get; set; }
    public List<ControlEvidence> Evidence { get; set; } = new();
}

public class ControlEvidence
{
    public string EvidenceId { get; set; } = string.Empty;
    public string ControlId { get; set; } = string.Empty;
    public string EvidenceType { get; set; } = string.Empty; // Document, Log, Screenshot, Configuration
    public string Description { get; set; } = string.Empty;
    public DateTime CollectedAt { get; set; }
    public string CollectedBy { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class AuditEvent
{
    public string EventId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? IpAddress { get; set; }
    public Dictionary<string, object> Details { get; set; } = new();
}

public class AuditReadinessScore
{
    public double OverallScore { get; set; }
    public int TotalControls { get; set; }
    public int ReadyControls { get; set; }
    public int NotReadyControls { get; set; }
    public int EvidenceGaps { get; set; }
    public List<string> CriticalGaps { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public DateTime AssessedAt { get; set; }
}

public class ControlAssessment
{
    public string ControlId { get; set; } = string.Empty;
    public string ControlName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Status { get; set; } = "Not Tested";
    public DateTime? LastTested { get; set; }
    public string? TestResult { get; set; }
    public int EvidenceCount { get; set; }
    public List<string> Findings { get; set; } = new();
}

#endregion

#region Common Models

public class ComplianceViolation
{
    public string Code { get; set; } = string.Empty;
    public string Severity { get; set; } = "Low"; // Low, Medium, High, Critical
    public string Message { get; set; } = string.Empty;
    public string Regulation { get; set; } = string.Empty;
    public string RemediationAdvice { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

#endregion
