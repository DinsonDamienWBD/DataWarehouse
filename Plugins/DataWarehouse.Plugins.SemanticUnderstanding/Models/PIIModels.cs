namespace DataWarehouse.Plugins.SemanticUnderstanding.Models
{
    /// <summary>
    /// Result of PII (Personally Identifiable Information) detection.
    /// </summary>
    public sealed class PIIDetectionResult
    {
        /// <summary>All detected PII items.</summary>
        public List<DetectedPII> PIIItems { get; init; } = new();

        /// <summary>Total PII count.</summary>
        public int TotalCount => PIIItems.Count;

        /// <summary>Count by PII type.</summary>
        public Dictionary<PIIType, int> CountByType =>
            PIIItems.GroupBy(p => p.Type).ToDictionary(g => g.Key, g => g.Count());

        /// <summary>Overall risk level.</summary>
        public PIIRiskLevel RiskLevel { get; init; }

        /// <summary>Risk score (0-100).</summary>
        public int RiskScore { get; init; }

        /// <summary>Whether AI was used for detection.</summary>
        public bool UsedAI { get; init; }

        /// <summary>Processing duration.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Compliance implications.</summary>
        public List<ComplianceImplication> ComplianceImplications { get; init; } = new();

        /// <summary>Recommended redactions.</summary>
        public List<RedactionRecommendation> RedactionRecommendations { get; init; } = new();
    }

    /// <summary>
    /// A detected PII item.
    /// </summary>
    public sealed class DetectedPII
    {
        /// <summary>The PII text (possibly masked for sensitive types).</summary>
        public required string Text { get; init; }

        /// <summary>Original unmasked text (only available before redaction).</summary>
        public string? OriginalText { get; init; }

        /// <summary>PII type classification.</summary>
        public PIIType Type { get; init; }

        /// <summary>Confidence score (0-1).</summary>
        public float Confidence { get; init; }

        /// <summary>Start position in source text.</summary>
        public int StartPosition { get; init; }

        /// <summary>Length in source text.</summary>
        public int Length { get; init; }

        /// <summary>Surrounding context.</summary>
        public string? Context { get; init; }

        /// <summary>Risk level for this specific PII.</summary>
        public PIIRiskLevel RiskLevel { get; init; }

        /// <summary>Detection method used.</summary>
        public PIIDetectionMethod DetectionMethod { get; init; }

        /// <summary>Associated regulations.</summary>
        public List<string> Regulations { get; init; } = new();

        /// <summary>Additional metadata.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Types of PII.
    /// </summary>
    public enum PIIType
    {
        /// <summary>Unknown PII type.</summary>
        Unknown = 0,

        // Identity
        /// <summary>Full name.</summary>
        FullName,
        /// <summary>First name only.</summary>
        FirstName,
        /// <summary>Last name only.</summary>
        LastName,
        /// <summary>Username or alias.</summary>
        Username,

        // Government IDs
        /// <summary>Social Security Number (US).</summary>
        SSN,
        /// <summary>National ID number.</summary>
        NationalId,
        /// <summary>Passport number.</summary>
        PassportNumber,
        /// <summary>Driver's license number.</summary>
        DriversLicense,
        /// <summary>Tax ID / EIN.</summary>
        TaxId,

        // Contact
        /// <summary>Email address.</summary>
        Email,
        /// <summary>Phone number.</summary>
        PhoneNumber,
        /// <summary>Physical address.</summary>
        Address,
        /// <summary>ZIP/postal code.</summary>
        PostalCode,

        // Financial
        /// <summary>Credit card number.</summary>
        CreditCard,
        /// <summary>Bank account number.</summary>
        BankAccount,
        /// <summary>Bank routing number.</summary>
        RoutingNumber,
        /// <summary>IBAN number.</summary>
        IBAN,
        /// <summary>SWIFT/BIC code.</summary>
        SWIFT,

        // Health
        /// <summary>Medical record number.</summary>
        MedicalRecordNumber,
        /// <summary>Health insurance ID.</summary>
        HealthInsuranceId,
        /// <summary>Medical condition.</summary>
        MedicalCondition,
        /// <summary>Prescription/medication.</summary>
        Medication,

        // Biometric
        /// <summary>Biometric data reference.</summary>
        BiometricData,
        /// <summary>Fingerprint reference.</summary>
        Fingerprint,
        /// <summary>Facial recognition data.</summary>
        FacialData,

        // Demographic
        /// <summary>Date of birth.</summary>
        DateOfBirth,
        /// <summary>Age.</summary>
        Age,
        /// <summary>Gender.</summary>
        Gender,
        /// <summary>Race or ethnicity.</summary>
        RaceEthnicity,
        /// <summary>Religion.</summary>
        Religion,
        /// <summary>Sexual orientation.</summary>
        SexualOrientation,
        /// <summary>Political affiliation.</summary>
        PoliticalAffiliation,

        // Digital
        /// <summary>IP address.</summary>
        IPAddress,
        /// <summary>MAC address.</summary>
        MACAddress,
        /// <summary>Device identifier.</summary>
        DeviceId,
        /// <summary>Cookie/tracking ID.</summary>
        TrackingId,
        /// <summary>Password or credential.</summary>
        Password,
        /// <summary>API key or token.</summary>
        APIKey,

        // Employment
        /// <summary>Employee ID.</summary>
        EmployeeId,
        /// <summary>Salary information.</summary>
        Salary,
        /// <summary>Employment history.</summary>
        EmploymentHistory,

        // Legal
        /// <summary>Criminal record reference.</summary>
        CriminalRecord,
        /// <summary>Court case number.</summary>
        CourtCaseNumber,

        // Location
        /// <summary>GPS coordinates.</summary>
        GPSCoordinates,
        /// <summary>Location history.</summary>
        LocationHistory,

        // Custom
        /// <summary>Custom PII type.</summary>
        Custom
    }

    /// <summary>
    /// PII risk levels.
    /// </summary>
    public enum PIIRiskLevel
    {
        /// <summary>No significant risk.</summary>
        None = 0,
        /// <summary>Low risk PII.</summary>
        Low = 1,
        /// <summary>Medium risk PII.</summary>
        Medium = 2,
        /// <summary>High risk PII.</summary>
        High = 3,
        /// <summary>Critical risk PII (requires immediate attention).</summary>
        Critical = 4
    }

    /// <summary>
    /// PII detection method.
    /// </summary>
    public enum PIIDetectionMethod
    {
        /// <summary>Pattern/regex matching.</summary>
        Pattern,
        /// <summary>Dictionary/list lookup.</summary>
        Dictionary,
        /// <summary>Machine learning model.</summary>
        MachineLearning,
        /// <summary>AI/LLM detection.</summary>
        AI,
        /// <summary>Context-based inference.</summary>
        ContextInference,
        /// <summary>Checksum validation.</summary>
        ChecksumValidation
    }

    /// <summary>
    /// Compliance implication for detected PII.
    /// </summary>
    public sealed class ComplianceImplication
    {
        /// <summary>Regulation/standard name.</summary>
        public required string Regulation { get; init; }

        /// <summary>Specific requirement violated or applicable.</summary>
        public string? Requirement { get; init; }

        /// <summary>Severity level.</summary>
        public ComplianceSeverity Severity { get; init; }

        /// <summary>PII types triggering this implication.</summary>
        public List<PIIType> TriggeringTypes { get; init; } = new();

        /// <summary>Recommended action.</summary>
        public string? RecommendedAction { get; init; }
    }

    /// <summary>
    /// Compliance severity levels.
    /// </summary>
    public enum ComplianceSeverity
    {
        /// <summary>Informational.</summary>
        Info,
        /// <summary>Advisory warning.</summary>
        Warning,
        /// <summary>Compliance violation.</summary>
        Violation,
        /// <summary>Critical compliance breach.</summary>
        Critical
    }

    /// <summary>
    /// Redaction recommendation.
    /// </summary>
    public sealed class RedactionRecommendation
    {
        /// <summary>Start position in text.</summary>
        public int StartPosition { get; init; }

        /// <summary>Length of text to redact.</summary>
        public int Length { get; init; }

        /// <summary>Suggested replacement text.</summary>
        public required string Replacement { get; init; }

        /// <summary>PII type being redacted.</summary>
        public PIIType PIIType { get; init; }

        /// <summary>Redaction style.</summary>
        public RedactionStyle Style { get; init; }

        /// <summary>Priority (higher = more important to redact).</summary>
        public int Priority { get; init; }
    }

    /// <summary>
    /// Redaction styles.
    /// </summary>
    public enum RedactionStyle
    {
        /// <summary>Replace with [REDACTED].</summary>
        Marker,
        /// <summary>Replace with asterisks.</summary>
        Asterisks,
        /// <summary>Replace with type label (e.g., [SSN]).</summary>
        TypeLabel,
        /// <summary>Replace with masked version (e.g., ***-**-1234).</summary>
        PartialMask,
        /// <summary>Remove entirely.</summary>
        Remove,
        /// <summary>Replace with placeholder (e.g., John Doe).</summary>
        Placeholder
    }

    /// <summary>
    /// Custom PII pattern definition.
    /// </summary>
    public sealed class CustomPIIPattern
    {
        /// <summary>Unique identifier.</summary>
        public required string Id { get; init; }

        /// <summary>Human-readable name.</summary>
        public required string Name { get; init; }

        /// <summary>Regex pattern for matching.</summary>
        public required string Pattern { get; init; }

        /// <summary>Validation function (optional).</summary>
        public Func<string, bool>? Validator { get; init; }

        /// <summary>Associated PII type.</summary>
        public PIIType Type { get; init; } = PIIType.Custom;

        /// <summary>Risk level for matches.</summary>
        public PIIRiskLevel RiskLevel { get; init; } = PIIRiskLevel.Medium;

        /// <summary>Associated regulations.</summary>
        public List<string> Regulations { get; init; } = new();

        /// <summary>Preferred redaction style.</summary>
        public RedactionStyle RedactionStyle { get; init; } = RedactionStyle.Marker;
    }

    /// <summary>
    /// Configuration for PII detection.
    /// </summary>
    public sealed class PIIDetectionConfig
    {
        /// <summary>PII types to detect (empty = all).</summary>
        public List<PIIType> EnabledTypes { get; init; } = new();

        /// <summary>Minimum confidence threshold.</summary>
        public float MinConfidenceThreshold { get; init; } = 0.7f;

        /// <summary>Enable AI-enhanced detection.</summary>
        public bool EnableAI { get; init; } = true;

        /// <summary>Include context in results.</summary>
        public bool IncludeContext { get; init; } = true;

        /// <summary>Context window size (characters).</summary>
        public int ContextWindowSize { get; init; } = 50;

        /// <summary>Generate redaction recommendations.</summary>
        public bool GenerateRedactions { get; init; } = true;

        /// <summary>Check compliance implications.</summary>
        public bool CheckCompliance { get; init; } = true;

        /// <summary>Regulations to check against.</summary>
        public List<string> ComplianceRegulations { get; init; } = new()
        {
            "GDPR", "CCPA", "HIPAA", "PCI-DSS", "SOC2"
        };

        /// <summary>Custom PII patterns.</summary>
        public List<CustomPIIPattern> CustomPatterns { get; init; } = new();
    }
}
