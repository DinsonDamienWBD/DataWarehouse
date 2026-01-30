using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Security
{
    /// <summary>
    /// Security classification levels based on US Department of Defense (DoD) standards.
    /// Defines hierarchical access control levels for classified government/military data.
    /// Implements the US DoD 5200.1-R and Executive Order 13526 classification system.
    /// </summary>
    public enum ClassificationLevel
    {
        /// <summary>
        /// Unclassified (U) - Information that is publicly releasable or does not require protection.
        /// </summary>
        Unclassified = 0,

        /// <summary>
        /// Controlled Unclassified Information (CUI) - Unclassified information that requires safeguarding.
        /// Governed by 32 CFR Part 2002 and NIST SP 800-171.
        /// </summary>
        Cui = 1,

        /// <summary>
        /// Confidential (C) - Unauthorized disclosure could reasonably be expected to cause damage to national security.
        /// Requires security clearance and need-to-know basis for access.
        /// </summary>
        Confidential = 2,

        /// <summary>
        /// Secret (S) - Unauthorized disclosure could reasonably be expected to cause serious damage to national security.
        /// Requires security clearance and compartmented access in some cases.
        /// </summary>
        Secret = 3,

        /// <summary>
        /// Top Secret (TS) - Unauthorized disclosure could reasonably be expected to cause exceptionally grave damage to national security.
        /// Highest level of classification, requires extensive background investigation.
        /// </summary>
        TopSecret = 4,

        /// <summary>
        /// Top Secret/Sensitive Compartmented Information (TS/SCI) - Top Secret information that requires special handling and access controls.
        /// Includes Compartmented Information requiring additional clearances and indoctrination.
        /// </summary>
        TsSci = 5
    }

    /// <summary>
    /// Represents a Sensitive Compartmented Information (SCI) compartment.
    /// Compartments provide an additional layer of access control beyond classification levels.
    /// Each compartment represents a specific program or intelligence source that requires separate authorization.
    /// </summary>
    /// <param name="Code">Compartment code identifier (e.g., "SI", "TK", "G", "HCS").</param>
    /// <param name="Name">Human-readable name of the compartment (e.g., "Special Intelligence").</param>
    /// <param name="RequiredClearances">Array of clearance levels required to access this compartment.</param>
    public record Compartment(string Code, string Name, string[] RequiredClearances);

    /// <summary>
    /// Security label for classified data implementing the US DoD mandatory access control framework.
    /// Represents the complete security marking including classification level, compartments, and handling caveats.
    /// Immutable to prevent tampering after creation.
    /// </summary>
    /// <param name="Level">The classification level of the data.</param>
    /// <param name="Compartments">Array of SCI compartment codes (e.g., ["SI", "TK"]) for TS/SCI data.</param>
    /// <param name="Caveats">Dissemination and handling caveats (e.g., ["NOFORN", "REL TO USA, AUS", "ORCON"]).</param>
    /// <param name="ClassificationAuthority">Authority who classified the data (person, organization, or automated system).</param>
    /// <param name="ClassifiedOn">Timestamp when the data was classified.</param>
    /// <param name="DeclassifyOn">Optional declassification date or event (null for permanent classification or "Originating Agency's Determination Required").</param>
    public record SecurityLabel(
        ClassificationLevel Level,
        string[] Compartments,
        string[] Caveats,
        string ClassificationAuthority,
        DateTimeOffset ClassifiedOn,
        DateTimeOffset? DeclassifyOn
    );

    /// <summary>
    /// Result of a security label validation operation.
    /// </summary>
    /// <param name="IsValid">True if the label is valid and properly formatted.</param>
    /// <param name="Errors">Array of error messages if validation failed; empty if valid.</param>
    public record ValidationResult(bool IsValid, string[] Errors);

    /// <summary>
    /// Bell-LaPadula Mandatory Access Control (MAC) implementation.
    /// Implements the US DoD mandatory security model preventing unauthorized information disclosure.
    /// Core principles:
    /// - Simple Security Property: No read up (subject cannot read objects at higher classification)
    /// - *-Property: No write down (subject cannot write to objects at lower classification)
    /// - Discretionary Security Property: Access must also satisfy discretionary controls
    /// Reference: Bell, D. E. and LaPadula, L. J. (1973). "Secure Computer Systems: Mathematical Foundations"
    /// </summary>
    public interface IMandatoryAccessControl
    {
        /// <summary>
        /// Checks if a subject can read an object under Bell-LaPadula simple security property (no read up).
        /// Subject's clearance level must dominate (be greater than or equal to) the object's classification.
        /// Subject must have access to all compartments marked on the object.
        /// </summary>
        /// <param name="subjectLabel">Security label of the subject (user/process) requesting read access.</param>
        /// <param name="objectLabel">Security label of the object (data) to be read.</param>
        /// <returns>True if read access is permitted under MAC rules; false otherwise.</returns>
        Task<bool> CanReadAsync(SecurityLabel subjectLabel, SecurityLabel objectLabel);

        /// <summary>
        /// Checks if a subject can write to an object under Bell-LaPadula *-property (no write down).
        /// Object's classification must dominate (be greater than or equal to) the subject's current level.
        /// Prevents classified data from leaking to lower classification levels.
        /// </summary>
        /// <param name="subjectLabel">Security label of the subject (user/process) requesting write access.</param>
        /// <param name="objectLabel">Security label of the object (data) to be written.</param>
        /// <returns>True if write access is permitted under MAC rules; false otherwise.</returns>
        Task<bool> CanWriteAsync(SecurityLabel subjectLabel, SecurityLabel objectLabel);

        /// <summary>
        /// Computes the effective security label for a subject based on their clearance and requested compartments.
        /// Takes into account the subject's maximum clearance level and authorized compartment access.
        /// Used to determine what classification level a subject is currently operating at.
        /// </summary>
        /// <param name="subjectId">Unique identifier of the subject (user/process).</param>
        /// <param name="requestedCompartments">Array of compartment codes the subject is requesting access to.</param>
        /// <returns>The effective security label for the subject with validated compartment access.</returns>
        Task<SecurityLabel> GetEffectiveLabelAsync(string subjectId, string[] requestedCompartments);

        /// <summary>
        /// Validates that a security label is properly formed and authorized.
        /// Checks:
        /// - Classification level is valid
        /// - Compartments exist and are properly formatted
        /// - Caveats are recognized and valid
        /// - Classification authority is authorized
        /// - Dates are logical (DeclassifyOn >= ClassifiedOn)
        /// </summary>
        /// <param name="label">The security label to validate.</param>
        /// <returns>Validation result indicating success or specific errors found.</returns>
        Task<ValidationResult> ValidateLabelAsync(SecurityLabel label);
    }

    /// <summary>
    /// Multi-Level Security (MLS) system for handling multiple classification levels within a single system.
    /// Implements trusted computing base functions for systems accredited to process multiple security levels.
    /// Required for systems operating in System High mode or Multi-Level Secure mode.
    /// Reference: TCSEC (Orange Book) B1-A1 requirements, Common Criteria EAL4-EAL7.
    /// </summary>
    public interface IMultiLevelSecurity
    {
        /// <summary>
        /// Gets all classification levels this system is accredited to handle.
        /// System must be formally certified for each level (DIACAP/RMF process).
        /// </summary>
        IReadOnlyList<ClassificationLevel> SupportedLevels { get; }

        /// <summary>
        /// Gets the system high classification level - the highest level of data the system is authorized to process.
        /// All users must have clearance up to system high to access the system (system high mode).
        /// All data processed is treated at this level unless MLS separation is enforced.
        /// </summary>
        ClassificationLevel SystemHighLevel { get; }

        /// <summary>
        /// Performs trusted downgrade of classified data to a lower classification level.
        /// This is a privileged operation requiring special authorization (typically dual-person integrity).
        /// Must be performed by an Original Classification Authority (OCA) or designated declassification authority.
        /// Creates an audit trail entry for compliance and review.
        /// </summary>
        /// <param name="data">The classified data to downgrade.</param>
        /// <param name="currentLabel">Current security label of the data.</param>
        /// <param name="targetLevel">Target classification level (must be lower than current).</param>
        /// <param name="authorizationCode">Authorization code from designated authority proving approval.</param>
        /// <returns>Downgraded data with new classification markings.</returns>
        /// <exception cref="UnauthorizedAccessException">If authorization is invalid or target level is invalid.</exception>
        Task<byte[]> DowngradeAsync(byte[] data, SecurityLabel currentLabel, ClassificationLevel targetLevel, string authorizationCode);

        /// <summary>
        /// Sanitizes classified data for release at a lower classification level.
        /// Removes or redacts portions of data that are classified above the target level.
        /// Uses automated content analysis to identify and remove/redact sensitive portions.
        /// Unlike downgrade, this modifies the data content to make it appropriate for lower level.
        /// </summary>
        /// <param name="data">The source data at higher classification.</param>
        /// <param name="sourceLabel">Source security label.</param>
        /// <param name="targetLevel">Target classification level for sanitized output.</param>
        /// <returns>Sanitized data appropriate for target classification level with portions redacted.</returns>
        Task<byte[]> SanitizeAsync(byte[] data, SecurityLabel sourceLabel, ClassificationLevel targetLevel);
    }

    /// <summary>
    /// Transfer path configuration between security domains.
    /// Defines the rules governing data transfer between two security enclaves.
    /// </summary>
    /// <param name="SourceDomain">Source security domain identifier (e.g., "NIPRNET", "SIPRNET").</param>
    /// <param name="TargetDomain">Target security domain identifier.</param>
    /// <param name="MaxLevel">Maximum classification level allowed on this transfer path.</param>
    /// <param name="RequiredApprovals">Array of approval authorities required for transfer on this path.</param>
    public record TransferPath(string SourceDomain, string TargetDomain, ClassificationLevel MaxLevel, string[] RequiredApprovals);

    /// <summary>
    /// Policy controlling cross-domain data transfer operations.
    /// Defines the procedural and technical requirements for a transfer.
    /// </summary>
    /// <param name="RequireDualApproval">True if two-person integrity is required for transfer approval.</param>
    /// <param name="AllowPartialTransfer">True if partial data transfer is permitted (false requires all-or-nothing).</param>
    /// <param name="RequiredReviewers">Array of reviewer IDs who must approve the transfer.</param>
    public record TransferPolicy(bool RequireDualApproval, bool AllowPartialTransfer, string[] RequiredReviewers);

    /// <summary>
    /// Result of a cross-domain transfer operation.
    /// Provides complete audit trail and status information.
    /// </summary>
    /// <param name="Success">True if transfer completed successfully.</param>
    /// <param name="TransferId">Unique identifier for this transfer operation for audit purposes.</param>
    /// <param name="AuditEntries">Array of audit log entries created during transfer.</param>
    /// <param name="ErrorMessage">Error message if transfer failed; null if successful.</param>
    public record TransferResult(bool Success, string TransferId, string[] AuditEntries, string? ErrorMessage);

    /// <summary>
    /// Cross-Domain Solution (CDS) for controlled data transfer between security domains.
    /// Implements guard functionality to enforce data flow policies between networks of different security levels.
    /// Required for any data transfer between:
    /// - Classified and unclassified networks (e.g., SIPRNET to NIPRNET)
    /// - Different classification levels
    /// - Different security enclaves or coalitions
    /// Reference: NSA Raise The Bar for Cross Domain Solutions, CNSSI 4009.
    /// </summary>
    public interface ICrossDomainSolution
    {
        /// <summary>
        /// Transfers data between security domains with mandatory review and approval.
        /// Implements data diode, content filtering, dirty word search, and approval workflows.
        /// All transfers are logged for audit and compliance review.
        /// </summary>
        /// <param name="data">The data to transfer between domains.</param>
        /// <param name="sourceDomain">Source security domain (e.g., "SIPRNET", "JWICS").</param>
        /// <param name="targetDomain">Target security domain (e.g., "NIPRNET", "SIPRNET").</param>
        /// <param name="label">Security label of the data being transferred.</param>
        /// <param name="policy">Transfer policy specifying approval and review requirements.</param>
        /// <returns>Transfer result with status and audit trail.</returns>
        /// <exception cref="UnauthorizedAccessException">If transfer violates domain separation policy.</exception>
        Task<TransferResult> TransferAsync(
            byte[] data,
            string sourceDomain,
            string targetDomain,
            SecurityLabel label,
            TransferPolicy policy);

        /// <summary>
        /// Gets the list of allowed transfer paths from a source domain.
        /// Not all domain-to-domain transfers are permitted - this returns approved paths only.
        /// For example, high-to-low transfers may be permitted with review, but low-to-high may be restricted.
        /// </summary>
        /// <param name="sourceDomain">Source security domain to query transfer paths from.</param>
        /// <returns>Read-only list of approved transfer paths from this domain.</returns>
        Task<IReadOnlyList<TransferPath>> GetAllowedPathsAsync(string sourceDomain);
    }

    /// <summary>
    /// Status of a two-person integrity operation.
    /// Tracks the authorization state of an operation requiring dual approval.
    /// </summary>
    /// <param name="OperationId">Unique identifier of the operation.</param>
    /// <param name="InitiatorApproved">True if the initiator has started the operation.</param>
    /// <param name="AuthorizerApproved">True if the authorizer has approved the operation.</param>
    /// <param name="InitiatorId">ID of the person who initiated the operation.</param>
    /// <param name="AuthorizerId">ID of the person who authorized the operation; null if not yet authorized.</param>
    /// <param name="ExpiresAt">Timestamp when the authorization expires; null if no expiration.</param>
    public record AuthorizationStatus(
        string OperationId,
        bool InitiatorApproved,
        bool AuthorizerApproved,
        string? InitiatorId,
        string? AuthorizerId,
        DateTimeOffset? ExpiresAt);

    /// <summary>
    /// Two-Person Integrity (TPI) enforcement for critical security operations.
    /// Requires two authorized individuals to approve sensitive operations to prevent unauthorized actions.
    /// Critical for operations such as:
    /// - Classification downgrades
    /// - Cryptographic key generation/destruction
    /// - Emergency override of security controls
    /// - Nuclear command and control (positive control)
    /// Reference: DoD Directive 5210.41, NIST SP 800-53 control AC-5.
    /// </summary>
    public interface ITwoPersonIntegrity
    {
        /// <summary>
        /// Begins an operation that requires two-person integrity approval.
        /// Creates a pending operation requiring a second authorized person to complete.
        /// The initiator cannot also be the authorizer (prevents single-person bypass).
        /// </summary>
        /// <param name="operationType">Type of operation being initiated (for audit and authorization purposes).</param>
        /// <param name="initiatorId">ID of the person initiating the operation.</param>
        /// <returns>Unique operation ID to be used for subsequent authorization and execution.</returns>
        Task<string> BeginOperationAsync(string operationType, string initiatorId);

        /// <summary>
        /// Provides second-person authorization for a pending operation.
        /// Validates that:
        /// - Authorizer is different from initiator
        /// - Authorizer has appropriate clearance and authorization
        /// - Authorization proof is cryptographically valid
        /// - Operation has not expired
        /// </summary>
        /// <param name="operationId">The operation ID returned from BeginOperationAsync.</param>
        /// <param name="authorizerId">ID of the person authorizing the operation (must differ from initiator).</param>
        /// <param name="authorizationProof">Cryptographic proof of authorization (e.g., signed token, hardware key signature).</param>
        /// <returns>True if authorization is accepted; false otherwise.</returns>
        Task<bool> AuthorizeOperationAsync(string operationId, string authorizerId, byte[] authorizationProof);

        /// <summary>
        /// Executes an operation only after dual authorization is complete.
        /// Validates that both initiator and authorizer have approved before executing the operation.
        /// Throws exception if authorization is incomplete or expired.
        /// </summary>
        /// <typeparam name="T">Return type of the operation.</typeparam>
        /// <param name="operationId">The operation ID that has been dual-authorized.</param>
        /// <param name="operation">The operation to execute after validation.</param>
        /// <returns>Result of the operation execution.</returns>
        /// <exception cref="UnauthorizedAccessException">If dual authorization is not complete or has expired.</exception>
        Task<T> ExecuteWithDualAuthAsync<T>(string operationId, Func<Task<T>> operation);

        /// <summary>
        /// Gets the current authorization status of an operation.
        /// Used to monitor pending operations and verify completion status.
        /// </summary>
        /// <param name="operationId">The operation ID to query.</param>
        /// <returns>Current authorization status including initiator, authorizer, and expiration.</returns>
        Task<AuthorizationStatus> GetAuthorizationStatusAsync(string operationId);
    }

    /// <summary>
    /// Need-to-Know audit entry for access tracking.
    /// Records who accessed what resource and why for compliance review.
    /// </summary>
    /// <param name="SubjectId">ID of the subject who accessed the resource.</param>
    /// <param name="Action">Action performed (Grant, Revoke, Access, Deny).</param>
    /// <param name="Timestamp">When the action occurred.</param>
    /// <param name="PerformedBy">Who performed the action (for Grant/Revoke) or the subject themselves (for Access).</param>
    /// <param name="Reason">Reason for the action or access denial.</param>
    public record NeedToKnowAuditEntry(
        string SubjectId,
        string Action,
        DateTimeOffset Timestamp,
        string PerformedBy,
        string? Reason);

    /// <summary>
    /// Need-to-Know enforcement for compartmented information access control.
    /// Implements the principle that access to classified information requires both:
    /// 1. Appropriate security clearance (handled by MAC)
    /// 2. Legitimate need-to-know for job function
    /// Need-to-know is a discretionary control managed by information owners and security officers.
    /// All access must be justified, approved, documented, and regularly reviewed.
    /// Reference: Executive Order 13526 Section 4.1, NIST SP 800-53 control AC-4.
    /// </summary>
    public interface INeedToKnowEnforcement
    {
        /// <summary>
        /// Checks if a subject has established need-to-know for a specific resource.
        /// Validates:
        /// - Subject has been granted need-to-know access
        /// - Grant has not expired
        /// - Purpose aligns with approved justification
        /// Need-to-know is independent of clearance level - both must be satisfied.
        /// </summary>
        /// <param name="subjectId">ID of the subject requesting access.</param>
        /// <param name="resourceId">ID of the resource being accessed.</param>
        /// <param name="purpose">Stated purpose for access (validated against approved justifications).</param>
        /// <returns>True if subject has need-to-know; false otherwise.</returns>
        Task<bool> HasNeedToKnowAsync(string subjectId, string resourceId, string purpose);

        /// <summary>
        /// Grants need-to-know access for a subject to a specific resource.
        /// Must be performed by information owner, security officer, or delegated authority.
        /// Creates audit trail entry for compliance review.
        /// </summary>
        /// <param name="subjectId">ID of the subject being granted access.</param>
        /// <param name="resourceId">ID of the resource for which access is granted.</param>
        /// <param name="grantedBy">ID of the authority granting the access.</param>
        /// <param name="duration">Optional duration for time-limited access; null for indefinite (subject to periodic review).</param>
        /// <returns>Task representing the asynchronous operation.</returns>
        Task GrantNeedToKnowAsync(string subjectId, string resourceId, string grantedBy, TimeSpan? duration);

        /// <summary>
        /// Revokes need-to-know access for a subject.
        /// Required when:
        /// - Subject changes job function
        /// - Project completes
        /// - Security violation occurs
        /// - Periodic review determines access no longer needed
        /// Creates audit trail entry documenting revocation and reason.
        /// </summary>
        /// <param name="subjectId">ID of the subject whose access is being revoked.</param>
        /// <param name="resourceId">ID of the resource for which access is revoked.</param>
        /// <param name="revokedBy">ID of the authority revoking the access.</param>
        /// <param name="reason">Justification for revocation (required for audit).</param>
        /// <returns>Task representing the asynchronous operation.</returns>
        Task RevokeNeedToKnowAsync(string subjectId, string resourceId, string revokedBy, string reason);

        /// <summary>
        /// Gets the complete audit trail for a resource's need-to-know access.
        /// Used for compliance review and security audits.
        /// Shows all grants, revocations, and access attempts.
        /// </summary>
        /// <param name="resourceId">ID of the resource to audit.</param>
        /// <returns>Read-only list of audit entries in chronological order.</returns>
        Task<IReadOnlyList<NeedToKnowAuditEntry>> GetAuditTrailAsync(string resourceId);
    }

    /// <summary>
    /// Secure data destruction methods approved for classified media sanitization.
    /// Reference: DoD 5220.22-M, NIST SP 800-88.
    /// </summary>
    public enum DestructionMethod
    {
        /// <summary>
        /// DoD 5220.22-M standard (3-pass overwrite).
        /// Pass 1: Write binary 0, Pass 2: Write binary 1, Pass 3: Write random.
        /// Suitable for Confidential and Secret data on magnetic media.
        /// </summary>
        DoD5220_22M,

        /// <summary>
        /// DoD 5220.22-M Enhanced (7-pass overwrite).
        /// More thorough variant with additional passes for higher assurance.
        /// Suitable for Top Secret data on magnetic media.
        /// </summary>
        DoD5220_22M_ECE,

        /// <summary>
        /// Gutmann method (35-pass overwrite).
        /// Extremely thorough sanitization for maximum assurance.
        /// Overkill for most scenarios but provides highest confidence.
        /// </summary>
        Gutmann,

        /// <summary>
        /// NIST SP 800-88 Clear - applies logical techniques to sanitize data.
        /// Suitable for data reuse within same security domain.
        /// Not sufficient for releasing media to lower classification.
        /// </summary>
        NIST800_88_Clear,

        /// <summary>
        /// NIST SP 800-88 Purge - cryptographic erase or degaussing.
        /// Renders target data recovery infeasible using state-of-the-art laboratory techniques.
        /// Suitable for releasing media to unclassified use or different security domain.
        /// </summary>
        NIST800_88_Purge,

        /// <summary>
        /// Physical destruction (shredding, incineration, pulverizing, disintegration).
        /// Required for Top Secret/SCI media or when media cannot be sanitized by other means.
        /// Particle size requirements: less than 2mm for disintegration.
        /// </summary>
        Physical
    }

    /// <summary>
    /// Certificate proving secure destruction of classified data.
    /// Required for compliance and audit trail.
    /// Immutable record that cannot be altered after issuance.
    /// </summary>
    /// <param name="CertificateId">Unique certificate identifier for audit tracking.</param>
    /// <param name="ResourceId">ID of the resource that was destroyed.</param>
    /// <param name="Method">Destruction method used.</param>
    /// <param name="DestroyedAt">Timestamp when destruction was completed.</param>
    /// <param name="AuthorizedBy">ID of the authority who authorized the destruction.</param>
    /// <param name="VerificationHash">Cryptographic hash proving destruction (hash of final overwrite pass or destruction process log).</param>
    public record DestructionCertificate(
        string CertificateId,
        string ResourceId,
        DestructionMethod Method,
        DateTimeOffset DestroyedAt,
        string AuthorizedBy,
        byte[] VerificationHash
    );

    /// <summary>
    /// Secure data destruction (sanitization) for classified media and storage.
    /// Implements approved methods for destroying classified information when no longer needed.
    /// Required when:
    /// - Classified data reaches declassification date
    /// - Media is being repurposed to lower classification
    /// - Media is being disposed or transferred outside secure facility
    /// - Emergency destruction is required (destruction before compromise)
    /// Reference: DoD 5220.22-M, NIST SP 800-88, NSA/CSS Storage Device Declassification Manual.
    /// </summary>
    public interface ISecureDestruction
    {
        /// <summary>
        /// Securely destroys data using specified method meeting DoD/NIST standards.
        /// Creates immutable certificate proving destruction for compliance audit.
        /// For physical media, this coordinates with hardware destruction processes.
        /// For cloud/virtual storage, this implements cryptographic erasure.
        /// </summary>
        /// <param name="resourceId">ID of the resource to destroy.</param>
        /// <param name="method">Destruction method to use (must be appropriate for data classification).</param>
        /// <param name="authorizedBy">ID of the authority authorizing destruction (typically information owner or security officer).</param>
        /// <returns>Certificate proving destruction was completed per specified method.</returns>
        /// <exception cref="UnauthorizedAccessException">If authorizer is not authorized for destruction.</exception>
        /// <exception cref="InvalidOperationException">If resource cannot be destroyed by specified method.</exception>
        Task<DestructionCertificate> DestroyAsync(string resourceId, DestructionMethod method, string authorizedBy);

        /// <summary>
        /// Verifies that destruction was completed as certified.
        /// Validates the destruction certificate and confirms resource is no longer accessible.
        /// Used during compliance audits and security reviews.
        /// </summary>
        /// <param name="certificateId">ID of the destruction certificate to verify.</param>
        /// <returns>True if destruction is verified and certificate is valid; false otherwise.</returns>
        Task<bool> VerifyDestructionAsync(string certificateId);
    }
}
