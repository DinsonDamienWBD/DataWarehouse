using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Plugins.MilitarySecurity;

/// <summary>
/// Cross-Domain Solution (CDS) plugin for controlled data transfer between security domains.
/// Implements guard functionality enforcing data flow policies between networks of different security levels.
/// Provides:
/// - Data diode functionality for controlled high-to-low and low-to-high transfers
/// - Content filtering and dirty word search to prevent unauthorized information disclosure
/// - Multi-level approval workflow with complete audit trail
/// - Transfer path validation for authorized domain-to-domain transfers
/// - Support for standard military networks (NIPRNET, SIPRNET, JWICS, NSANet, etc.)
///
/// This plugin is designed for environments requiring NSA Raise The Bar compliance and
/// implements CNSSI 4009 requirements for cross-domain data transfer.
///
/// Thread-safe implementation suitable for high-throughput transfer operations.
/// </summary>
/// <remarks>
/// Reference: NSA Raise The Bar for Cross Domain Solutions, CNSSI 4009, ICD 503.
/// </remarks>
public sealed partial class CrossDomainSolutionPlugin : SecurityProviderPluginBase, ICrossDomainSolution
{
    #region Fields

    /// <summary>
    /// Thread-safe collection of registered transfer paths between security domains.
    /// </summary>
    private readonly ConcurrentDictionary<string, List<TransferPath>> _transferPaths = new();

    /// <summary>
    /// Thread-safe collection of pending transfer approvals awaiting authorization.
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingTransfer> _pendingTransfers = new();

    /// <summary>
    /// Thread-safe audit log of all transfer operations.
    /// </summary>
    private readonly ConcurrentBag<CrossDomainAuditEntry> _auditLog = new();

    /// <summary>
    /// Dictionary mapping domain names to their classification levels.
    /// </summary>
    private readonly ConcurrentDictionary<string, ClassificationLevel> _domainLevels = new();

    /// <summary>
    /// Collection of dirty words/patterns that must be filtered during transfer.
    /// Key: pattern name, Value: compiled regex pattern.
    /// </summary>
    private readonly ConcurrentDictionary<string, Regex> _dirtyWordPatterns = new();

    /// <summary>
    /// Collection of authorized reviewers who can approve transfers.
    /// Key: reviewer ID, Value: reviewer information.
    /// </summary>
    private readonly ConcurrentDictionary<string, ReviewerInfo> _authorizedReviewers = new();

    /// <summary>
    /// Lock object for transfer operations requiring atomicity.
    /// </summary>
    private readonly object _transferLock = new();

    /// <summary>
    /// Counter for generating unique transfer IDs.
    /// </summary>
    private long _transferCounter;

    #endregion

    #region Plugin Identity

    /// <inheritdoc />
    public override string Id => "datawarehouse.milsec.cross-domain-solution";

    /// <inheritdoc />
    public override string Name => "Cross-Domain Solution";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="CrossDomainSolutionPlugin"/> class.
    /// Pre-configures standard military network domains and default transfer paths.
    /// </summary>
    public CrossDomainSolutionPlugin()
    {
        InitializeStandardDomains();
        InitializeDefaultDirtyWordPatterns();
    }

    #endregion

    #region ICrossDomainSolution Implementation

    /// <inheritdoc />
    /// <remarks>
    /// This method performs a complete cross-domain transfer with the following steps:
    /// 1. Validates the transfer path exists and is authorized
    /// 2. Validates the security label is appropriate for the target domain
    /// 3. Performs content filtering and dirty word search
    /// 4. If dual approval is required, creates a pending transfer for review
    /// 5. If all reviewers have approved, executes the transfer
    /// 6. Creates comprehensive audit trail entries for compliance
    /// </remarks>
    public async Task<TransferResult> TransferAsync(
        byte[] data,
        string sourceDomain,
        string targetDomain,
        SecurityLabel label,
        TransferPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrEmpty(sourceDomain);
        ArgumentException.ThrowIfNullOrEmpty(targetDomain);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(policy);

        var transferId = GenerateTransferId();
        var auditEntries = new List<string>();

        try
        {
            // Log transfer initiation
            LogAuditEntry(transferId, "TRANSFER_INITIATED", sourceDomain, targetDomain,
                $"Data size: {data.Length} bytes, Label: {label.Level}");
            auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Transfer initiated from {sourceDomain} to {targetDomain}");

            // Step 1: Validate transfer path exists and is authorized
            var validationResult = await ValidateTransferPathAsync(sourceDomain, targetDomain, label);
            if (!validationResult.IsValid)
            {
                LogAuditEntry(transferId, "PATH_VALIDATION_FAILED", sourceDomain, targetDomain, validationResult.Error!);
                auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Path validation failed: {validationResult.Error}");
                return new TransferResult(
                    Success: false,
                    TransferId: transferId,
                    AuditEntries: auditEntries.ToArray(),
                    ErrorMessage: validationResult.Error);
            }
            auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Transfer path validated successfully");

            // Step 2: Validate high-to-low or low-to-high transfer rules
            var directionResult = ValidateTransferDirection(sourceDomain, targetDomain, label);
            if (!directionResult.IsValid)
            {
                LogAuditEntry(transferId, "DIRECTION_VALIDATION_FAILED", sourceDomain, targetDomain, directionResult.Error!);
                auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Direction validation failed: {directionResult.Error}");
                return new TransferResult(
                    Success: false,
                    TransferId: transferId,
                    AuditEntries: auditEntries.ToArray(),
                    ErrorMessage: directionResult.Error);
            }
            auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Transfer direction validated: {directionResult.Direction}");

            // Step 3: Perform content filtering and dirty word search
            var filterResult = await PerformContentFilteringAsync(data, label, transferId);
            if (!filterResult.Passed)
            {
                LogAuditEntry(transferId, "CONTENT_FILTER_FAILED", sourceDomain, targetDomain,
                    $"Blocked terms found: {string.Join(", ", filterResult.BlockedTerms)}");
                auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Content filtering failed: {filterResult.BlockedTerms.Count} blocked terms found");

                if (!policy.AllowPartialTransfer)
                {
                    return new TransferResult(
                        Success: false,
                        TransferId: transferId,
                        AuditEntries: auditEntries.ToArray(),
                        ErrorMessage: $"Content contains {filterResult.BlockedTerms.Count} blocked terms. Transfer denied.");
                }
            }
            auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Content filtering completed. Warnings: {filterResult.Warnings.Count}");

            // Step 4: Check if approval workflow is required
            if (policy.RequireDualApproval || policy.RequiredReviewers.Length > 0)
            {
                var pendingTransfer = CreatePendingTransfer(
                    transferId, data, sourceDomain, targetDomain, label, policy, filterResult);

                _pendingTransfers[transferId] = pendingTransfer;

                LogAuditEntry(transferId, "AWAITING_APPROVAL", sourceDomain, targetDomain,
                    $"Pending approval from: {string.Join(", ", policy.RequiredReviewers)}");
                auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Transfer pending approval from {policy.RequiredReviewers.Length} reviewer(s)");

                return new TransferResult(
                    Success: true,
                    TransferId: transferId,
                    AuditEntries: auditEntries.ToArray(),
                    ErrorMessage: "Transfer pending approval. Use ApproveTransferAsync to complete.");
            }

            // Step 5: Execute the transfer (if no approval required)
            var executeResult = await ExecuteTransferAsync(transferId, data, sourceDomain, targetDomain, label);
            auditEntries.AddRange(executeResult.AdditionalAuditEntries);

            if (executeResult.Success)
            {
                LogAuditEntry(transferId, "TRANSFER_COMPLETED", sourceDomain, targetDomain,
                    $"Successfully transferred {data.Length} bytes");
                auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Transfer completed successfully");
            }

            return new TransferResult(
                Success: executeResult.Success,
                TransferId: transferId,
                AuditEntries: auditEntries.ToArray(),
                ErrorMessage: executeResult.Error);
        }
        catch (Exception ex)
        {
            LogAuditEntry(transferId, "TRANSFER_ERROR", sourceDomain, targetDomain, ex.Message);
            auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Transfer failed with error: {ex.Message}");

            return new TransferResult(
                Success: false,
                TransferId: transferId,
                AuditEntries: auditEntries.ToArray(),
                ErrorMessage: $"Transfer failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TransferPath>> GetAllowedPathsAsync(string sourceDomain)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDomain);

        if (_transferPaths.TryGetValue(sourceDomain.ToUpperInvariant(), out var paths))
        {
            return Task.FromResult<IReadOnlyList<TransferPath>>(paths.AsReadOnly());
        }

        return Task.FromResult<IReadOnlyList<TransferPath>>(Array.Empty<TransferPath>());
    }

    #endregion

    #region Configuration Methods

    /// <summary>
    /// Registers a security domain with its classification level.
    /// </summary>
    /// <param name="domainName">Name of the security domain (e.g., "SIPRNET", "NIPRNET").</param>
    /// <param name="level">Classification level of the domain.</param>
    /// <exception cref="ArgumentException">Thrown when domain name is null or empty.</exception>
    public void RegisterDomain(string domainName, ClassificationLevel level)
    {
        ArgumentException.ThrowIfNullOrEmpty(domainName);
        _domainLevels[domainName.ToUpperInvariant()] = level;

        LogAuditEntry("SYSTEM", "DOMAIN_REGISTERED", domainName, "",
            $"Domain registered at level: {level}");
    }

    /// <summary>
    /// Registers a transfer path between two security domains.
    /// </summary>
    /// <param name="path">The transfer path configuration.</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    public void RegisterTransferPath(TransferPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var sourceDomain = path.SourceDomain.ToUpperInvariant();

        _transferPaths.AddOrUpdate(
            sourceDomain,
            _ => new List<TransferPath> { path },
            (_, existing) =>
            {
                lock (existing)
                {
                    // Remove any existing path to the same target
                    existing.RemoveAll(p =>
                        p.TargetDomain.Equals(path.TargetDomain, StringComparison.OrdinalIgnoreCase));
                    existing.Add(path);
                    return existing;
                }
            });

        LogAuditEntry("SYSTEM", "PATH_REGISTERED", path.SourceDomain, path.TargetDomain,
            $"Max level: {path.MaxLevel}, Required approvals: {string.Join(", ", path.RequiredApprovals)}");
    }

    /// <summary>
    /// Registers an authorized reviewer who can approve cross-domain transfers.
    /// </summary>
    /// <param name="reviewerId">Unique identifier for the reviewer.</param>
    /// <param name="name">Name of the reviewer.</param>
    /// <param name="clearanceLevel">Clearance level of the reviewer.</param>
    /// <param name="authorizedDomains">Domains the reviewer is authorized to approve transfers for.</param>
    /// <exception cref="ArgumentException">Thrown when reviewerId is null or empty.</exception>
    public void RegisterReviewer(
        string reviewerId,
        string name,
        ClassificationLevel clearanceLevel,
        string[] authorizedDomains)
    {
        ArgumentException.ThrowIfNullOrEmpty(reviewerId);

        _authorizedReviewers[reviewerId] = new ReviewerInfo
        {
            ReviewerId = reviewerId,
            Name = name,
            ClearanceLevel = clearanceLevel,
            AuthorizedDomains = authorizedDomains.Select(d => d.ToUpperInvariant()).ToHashSet(),
            RegisteredAt = DateTimeOffset.UtcNow
        };

        LogAuditEntry("SYSTEM", "REVIEWER_REGISTERED", "", "",
            $"Reviewer {reviewerId} ({name}) registered with {clearanceLevel} clearance");
    }

    /// <summary>
    /// Adds a dirty word pattern that will be filtered during transfers.
    /// </summary>
    /// <param name="patternName">Name identifying the pattern.</param>
    /// <param name="regexPattern">Regular expression pattern to match.</param>
    /// <param name="options">Regular expression options.</param>
    /// <exception cref="ArgumentException">Thrown when pattern name is null or empty.</exception>
    public void AddDirtyWordPattern(
        string patternName,
        string regexPattern,
        RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Compiled)
    {
        ArgumentException.ThrowIfNullOrEmpty(patternName);
        ArgumentException.ThrowIfNullOrEmpty(regexPattern);

        _dirtyWordPatterns[patternName] = new Regex(regexPattern, options);

        LogAuditEntry("SYSTEM", "DIRTY_WORD_ADDED", "", "",
            $"Pattern '{patternName}' added to content filter");
    }

    /// <summary>
    /// Removes a dirty word pattern from the filter.
    /// </summary>
    /// <param name="patternName">Name of the pattern to remove.</param>
    /// <returns>True if the pattern was removed; false if not found.</returns>
    public bool RemoveDirtyWordPattern(string patternName)
    {
        return _dirtyWordPatterns.TryRemove(patternName, out _);
    }

    #endregion

    #region Approval Workflow

    /// <summary>
    /// Approves a pending cross-domain transfer.
    /// </summary>
    /// <param name="transferId">The transfer ID to approve.</param>
    /// <param name="reviewerId">ID of the reviewer providing approval.</param>
    /// <param name="comments">Optional comments from the reviewer.</param>
    /// <returns>Result of the approval operation.</returns>
    /// <exception cref="ArgumentException">Thrown when transfer ID or reviewer ID is null or empty.</exception>
    public async Task<ApprovalResult> ApproveTransferAsync(
        string transferId,
        string reviewerId,
        string? comments = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        ArgumentException.ThrowIfNullOrEmpty(reviewerId);

        if (!_pendingTransfers.TryGetValue(transferId, out var pending))
        {
            return new ApprovalResult(
                Success: false,
                TransferExecuted: false,
                ErrorMessage: "Transfer not found or already completed");
        }

        // Validate reviewer
        if (!_authorizedReviewers.TryGetValue(reviewerId, out var reviewer))
        {
            LogAuditEntry(transferId, "APPROVAL_DENIED", pending.SourceDomain, pending.TargetDomain,
                $"Reviewer {reviewerId} not authorized");
            return new ApprovalResult(
                Success: false,
                TransferExecuted: false,
                ErrorMessage: "Reviewer not authorized");
        }

        // Validate reviewer clearance
        if (reviewer.ClearanceLevel < pending.Label.Level)
        {
            LogAuditEntry(transferId, "APPROVAL_DENIED", pending.SourceDomain, pending.TargetDomain,
                $"Reviewer {reviewerId} lacks sufficient clearance");
            return new ApprovalResult(
                Success: false,
                TransferExecuted: false,
                ErrorMessage: "Reviewer lacks sufficient clearance for this data");
        }

        // Validate reviewer is authorized for these domains
        var sourceDomainUpper = pending.SourceDomain.ToUpperInvariant();
        var targetDomainUpper = pending.TargetDomain.ToUpperInvariant();

        if (!reviewer.AuthorizedDomains.Contains(sourceDomainUpper) &&
            !reviewer.AuthorizedDomains.Contains(targetDomainUpper) &&
            !reviewer.AuthorizedDomains.Contains("*"))
        {
            LogAuditEntry(transferId, "APPROVAL_DENIED", pending.SourceDomain, pending.TargetDomain,
                $"Reviewer {reviewerId} not authorized for these domains");
            return new ApprovalResult(
                Success: false,
                TransferExecuted: false,
                ErrorMessage: "Reviewer not authorized for source or target domain");
        }

        // Check if reviewer is in the required reviewers list
        if (pending.Policy.RequiredReviewers.Length > 0 &&
            !pending.Policy.RequiredReviewers.Contains(reviewerId))
        {
            LogAuditEntry(transferId, "APPROVAL_DENIED", pending.SourceDomain, pending.TargetDomain,
                $"Reviewer {reviewerId} not in required reviewers list");
            return new ApprovalResult(
                Success: false,
                TransferExecuted: false,
                ErrorMessage: "Reviewer not in required reviewers list for this transfer");
        }

        // Record the approval
        lock (pending.Approvals)
        {
            pending.Approvals[reviewerId] = new ApprovalRecord
            {
                ReviewerId = reviewerId,
                ReviewerName = reviewer.Name,
                ApprovedAt = DateTimeOffset.UtcNow,
                Comments = comments
            };
        }

        LogAuditEntry(transferId, "APPROVAL_GRANTED", pending.SourceDomain, pending.TargetDomain,
            $"Approved by {reviewerId}: {comments ?? "No comments"}");

        // Check if all required approvals have been obtained
        var allApproved = CheckAllApprovalsObtained(pending);

        if (allApproved)
        {
            // Execute the transfer
            var executeResult = await ExecuteTransferAsync(
                transferId, pending.Data, pending.SourceDomain, pending.TargetDomain, pending.Label);

            // Remove from pending
            _pendingTransfers.TryRemove(transferId, out _);

            if (executeResult.Success)
            {
                LogAuditEntry(transferId, "TRANSFER_EXECUTED", pending.SourceDomain, pending.TargetDomain,
                    "Transfer executed after all approvals obtained");
            }

            return new ApprovalResult(
                Success: executeResult.Success,
                TransferExecuted: true,
                ErrorMessage: executeResult.Error);
        }

        var remainingApprovals = GetRemainingApprovals(pending);
        return new ApprovalResult(
            Success: true,
            TransferExecuted: false,
            ErrorMessage: $"Approval recorded. Awaiting {remainingApprovals.Count} more approval(s): {string.Join(", ", remainingApprovals)}");
    }

    /// <summary>
    /// Rejects a pending cross-domain transfer.
    /// </summary>
    /// <param name="transferId">The transfer ID to reject.</param>
    /// <param name="reviewerId">ID of the reviewer rejecting the transfer.</param>
    /// <param name="reason">Reason for rejection.</param>
    /// <returns>True if rejection was recorded; false if transfer not found.</returns>
    public Task<bool> RejectTransferAsync(string transferId, string reviewerId, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(transferId);
        ArgumentException.ThrowIfNullOrEmpty(reviewerId);
        ArgumentException.ThrowIfNullOrEmpty(reason);

        if (!_pendingTransfers.TryRemove(transferId, out var pending))
        {
            return Task.FromResult(false);
        }

        LogAuditEntry(transferId, "TRANSFER_REJECTED", pending.SourceDomain, pending.TargetDomain,
            $"Rejected by {reviewerId}: {reason}");

        return Task.FromResult(true);
    }

    /// <summary>
    /// Gets information about a pending transfer.
    /// </summary>
    /// <param name="transferId">The transfer ID to query.</param>
    /// <returns>Pending transfer information, or null if not found.</returns>
    public PendingTransferInfo? GetPendingTransferInfo(string transferId)
    {
        if (!_pendingTransfers.TryGetValue(transferId, out var pending))
        {
            return null;
        }

        List<string> approvedBy;
        lock (pending.Approvals)
        {
            approvedBy = pending.Approvals.Keys.ToList();
        }

        return new PendingTransferInfo(
            TransferId: transferId,
            SourceDomain: pending.SourceDomain,
            TargetDomain: pending.TargetDomain,
            DataSize: pending.Data.Length,
            Label: pending.Label,
            CreatedAt: pending.CreatedAt,
            ApprovedBy: approvedBy.ToArray(),
            AwaitingApprovalFrom: GetRemainingApprovals(pending).ToArray(),
            ContentWarnings: pending.ContentWarnings.ToArray()
        );
    }

    /// <summary>
    /// Gets all pending transfers awaiting approval.
    /// </summary>
    /// <returns>List of pending transfer information.</returns>
    public IReadOnlyList<PendingTransferInfo> GetAllPendingTransfers()
    {
        var result = new List<PendingTransferInfo>();

        foreach (var (transferId, pending) in _pendingTransfers)
        {
            List<string> approvedBy;
            lock (pending.Approvals)
            {
                approvedBy = pending.Approvals.Keys.ToList();
            }

            result.Add(new PendingTransferInfo(
                TransferId: transferId,
                SourceDomain: pending.SourceDomain,
                TargetDomain: pending.TargetDomain,
                DataSize: pending.Data.Length,
                Label: pending.Label,
                CreatedAt: pending.CreatedAt,
                ApprovedBy: approvedBy.ToArray(),
                AwaitingApprovalFrom: GetRemainingApprovals(pending).ToArray(),
                ContentWarnings: pending.ContentWarnings.ToArray()
            ));
        }

        return result;
    }

    #endregion

    #region Audit Methods

    /// <summary>
    /// Gets the complete audit log for cross-domain transfer operations.
    /// </summary>
    /// <returns>Read-only list of audit entries.</returns>
    public IReadOnlyList<CrossDomainAuditEntry> GetAuditLog()
    {
        return _auditLog.OrderByDescending(e => e.Timestamp).ToList();
    }

    /// <summary>
    /// Gets audit entries for a specific transfer.
    /// </summary>
    /// <param name="transferId">The transfer ID to query.</param>
    /// <returns>Audit entries for the specified transfer.</returns>
    public IReadOnlyList<CrossDomainAuditEntry> GetTransferAuditLog(string transferId)
    {
        return _auditLog
            .Where(e => e.TransferId == transferId)
            .OrderBy(e => e.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Gets audit entries for transfers between specific domains.
    /// </summary>
    /// <param name="sourceDomain">Source domain filter (optional).</param>
    /// <param name="targetDomain">Target domain filter (optional).</param>
    /// <param name="since">Only return entries after this timestamp (optional).</param>
    /// <returns>Filtered audit entries.</returns>
    public IReadOnlyList<CrossDomainAuditEntry> QueryAuditLog(
        string? sourceDomain = null,
        string? targetDomain = null,
        DateTimeOffset? since = null)
    {
        var query = _auditLog.AsEnumerable();

        if (!string.IsNullOrEmpty(sourceDomain))
        {
            query = query.Where(e =>
                e.SourceDomain.Equals(sourceDomain, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(targetDomain))
        {
            query = query.Where(e =>
                e.TargetDomain.Equals(targetDomain, StringComparison.OrdinalIgnoreCase));
        }

        if (since.HasValue)
        {
            query = query.Where(e => e.Timestamp >= since.Value);
        }

        return query.OrderByDescending(e => e.Timestamp).ToList();
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Initializes standard military network domains with their classification levels.
    /// </summary>
    private void InitializeStandardDomains()
    {
        // Unclassified networks
        RegisterDomain("NIPRNET", ClassificationLevel.Cui);           // NIPRNet - Sensitive but Unclassified
        RegisterDomain("INTERNET", ClassificationLevel.Unclassified);  // Public Internet

        // Classified networks
        RegisterDomain("SIPRNET", ClassificationLevel.Secret);         // SIPRNet - Secret
        RegisterDomain("JWICS", ClassificationLevel.TopSecret);        // JWICS - Top Secret/SCI
        RegisterDomain("NSANET", ClassificationLevel.TsSci);           // NSANet - TS/SCI
        RegisterDomain("STONEGHOST", ClassificationLevel.TopSecret);   // Stone Ghost - Five Eyes

        // Coalition networks
        RegisterDomain("CENTRIXS", ClassificationLevel.Secret);        // CENTRIXS - Coalition Secret
        RegisterDomain("BICES", ClassificationLevel.Secret);           // BICES - NATO Secret

        // Configure default transfer paths
        // High-to-low transfers (require review)
        RegisterTransferPath(new TransferPath(
            SourceDomain: "SIPRNET",
            TargetDomain: "NIPRNET",
            MaxLevel: ClassificationLevel.Cui,
            RequiredApprovals: new[] { "SECURITY_OFFICER", "DATA_OWNER" }));

        RegisterTransferPath(new TransferPath(
            SourceDomain: "JWICS",
            TargetDomain: "SIPRNET",
            MaxLevel: ClassificationLevel.Secret,
            RequiredApprovals: new[] { "SECURITY_OFFICER", "DATA_OWNER" }));

        // Low-to-high transfers (less restrictive)
        RegisterTransferPath(new TransferPath(
            SourceDomain: "NIPRNET",
            TargetDomain: "SIPRNET",
            MaxLevel: ClassificationLevel.Secret,
            RequiredApprovals: Array.Empty<string>()));

        RegisterTransferPath(new TransferPath(
            SourceDomain: "SIPRNET",
            TargetDomain: "JWICS",
            MaxLevel: ClassificationLevel.TopSecret,
            RequiredApprovals: Array.Empty<string>()));
    }

    /// <summary>
    /// Initializes default dirty word patterns for content filtering.
    /// </summary>
    private void InitializeDefaultDirtyWordPatterns()
    {
        // Classification markings that might indicate spillage
        AddDirtyWordPattern("TS_SCI_MARKING", @"\bTS(/|//)SCI\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        AddDirtyWordPattern("TOP_SECRET", @"\bTOP\s*SECRET\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        AddDirtyWordPattern("SECRET_NOFORN", @"\bSECRET//NOFORN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Intelligence compartment codewords
        AddDirtyWordPattern("SI_GAMMA", @"\b(SI|GAMMA|TK|HCS)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        AddDirtyWordPattern("ORCON_NOFORN", @"\b(ORCON|NOFORN|PROPIN|REL\s+TO)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Special access program indicators
        AddDirtyWordPattern("SAP_INDICATOR", @"\bSAP\s*[-/]?\s*[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        AddDirtyWordPattern("WAIVED_SAP", @"\bWAIVED\s+SAP\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Specific program codewords (examples - real codewords would be classified)
        AddDirtyWordPattern("CODEWORD_PATTERN", @"\b[A-Z]{5,}\s+(ALPHA|BRAVO|CHARLIE)\b", RegexOptions.Compiled);
    }

    /// <summary>
    /// Generates a unique transfer ID.
    /// </summary>
    private string GenerateTransferId()
    {
        var counter = Interlocked.Increment(ref _transferCounter);
        return $"CDS-{DateTimeOffset.UtcNow:yyyyMMdd}-{counter:D8}";
    }

    /// <summary>
    /// Validates that a transfer path exists and is authorized.
    /// </summary>
    private Task<PathValidationResult> ValidateTransferPathAsync(
        string sourceDomain,
        string targetDomain,
        SecurityLabel label)
    {
        var sourceUpper = sourceDomain.ToUpperInvariant();
        var targetUpper = targetDomain.ToUpperInvariant();

        // Check if domains are registered
        if (!_domainLevels.ContainsKey(sourceUpper))
        {
            return Task.FromResult(new PathValidationResult(false, $"Source domain '{sourceDomain}' is not registered"));
        }

        if (!_domainLevels.ContainsKey(targetUpper))
        {
            return Task.FromResult(new PathValidationResult(false, $"Target domain '{targetDomain}' is not registered"));
        }

        // Check if transfer path exists
        if (!_transferPaths.TryGetValue(sourceUpper, out var paths))
        {
            return Task.FromResult(new PathValidationResult(false,
                $"No transfer paths defined from '{sourceDomain}'"));
        }

        var matchingPath = paths.FirstOrDefault(p =>
            p.TargetDomain.Equals(targetDomain, StringComparison.OrdinalIgnoreCase));

        if (matchingPath == null)
        {
            return Task.FromResult(new PathValidationResult(false,
                $"No authorized transfer path from '{sourceDomain}' to '{targetDomain}'"));
        }

        // Check if data classification is within path limits
        if (label.Level > matchingPath.MaxLevel)
        {
            return Task.FromResult(new PathValidationResult(false,
                $"Data classification ({label.Level}) exceeds maximum allowed ({matchingPath.MaxLevel}) for this path"));
        }

        return Task.FromResult(new PathValidationResult(true, null, matchingPath));
    }

    /// <summary>
    /// Validates transfer direction (high-to-low or low-to-high).
    /// </summary>
    private DirectionValidationResult ValidateTransferDirection(
        string sourceDomain,
        string targetDomain,
        SecurityLabel label)
    {
        var sourceLevel = _domainLevels[sourceDomain.ToUpperInvariant()];
        var targetLevel = _domainLevels[targetDomain.ToUpperInvariant()];

        string direction;
        if (sourceLevel > targetLevel)
        {
            direction = "HIGH_TO_LOW";

            // High-to-low transfers: data must be at or below target level
            if (label.Level > targetLevel)
            {
                return new DirectionValidationResult(false,
                    $"Cannot transfer {label.Level} data to {targetLevel} domain. " +
                    "Data must be downgraded or sanitized before transfer.",
                    direction);
            }
        }
        else if (sourceLevel < targetLevel)
        {
            direction = "LOW_TO_HIGH";
            // Low-to-high transfers are generally allowed
            // Data will be "marked up" to the target domain level
        }
        else
        {
            direction = "SAME_LEVEL";
            // Same-level transfers are allowed if path exists
        }

        return new DirectionValidationResult(true, null, direction);
    }

    /// <summary>
    /// Performs content filtering and dirty word search on transfer data.
    /// </summary>
    private Task<ContentFilterResult> PerformContentFilteringAsync(
        byte[] data,
        SecurityLabel label,
        string transferId)
    {
        var blockedTerms = new List<string>();
        var warnings = new List<string>();

        // Convert data to text for analysis
        string textContent;
        try
        {
            textContent = Encoding.UTF8.GetString(data);
        }
        catch
        {
            // Binary data - cannot perform text analysis
            LogAuditEntry(transferId, "CONTENT_FILTER_BINARY", "", "",
                "Binary data detected - text analysis skipped");
            return Task.FromResult(new ContentFilterResult(true, blockedTerms, warnings));
        }

        // Apply all dirty word patterns
        foreach (var (patternName, regex) in _dirtyWordPatterns)
        {
            var matches = regex.Matches(textContent);
            if (matches.Count > 0)
            {
                foreach (Match match in matches)
                {
                    var term = $"{patternName}: '{match.Value}'";

                    // Determine if this is a blocker or warning based on data classification
                    if (IsBlockingMatch(patternName, label))
                    {
                        blockedTerms.Add(term);
                    }
                    else
                    {
                        warnings.Add(term);
                    }
                }
            }
        }

        // Check for classification markings that don't match the label
        if (ClassificationMarkingMismatch(textContent, label, out var mismatchDetails))
        {
            blockedTerms.Add($"Classification mismatch: {mismatchDetails}");
        }

        var passed = blockedTerms.Count == 0;
        return Task.FromResult(new ContentFilterResult(passed, blockedTerms, warnings));
    }

    /// <summary>
    /// Determines if a pattern match should block the transfer.
    /// </summary>
    private static bool IsBlockingMatch(string patternName, SecurityLabel label)
    {
        // Higher classification patterns are blocking when transferring to lower domains
        return patternName switch
        {
            "TS_SCI_MARKING" => label.Level < ClassificationLevel.TsSci,
            "TOP_SECRET" => label.Level < ClassificationLevel.TopSecret,
            "SECRET_NOFORN" => label.Level < ClassificationLevel.Secret,
            "SI_GAMMA" => label.Level < ClassificationLevel.TsSci,
            "SAP_INDICATOR" => true, // Always block SAP indicators
            "WAIVED_SAP" => true,    // Always block SAP indicators
            _ => false
        };
    }

    /// <summary>
    /// Checks if the content contains classification markings that don't match the label.
    /// </summary>
    private bool ClassificationMarkingMismatch(string content, SecurityLabel label, out string details)
    {
        details = string.Empty;

        // Check for TOP SECRET markings in lower-classified data
        if (label.Level < ClassificationLevel.TopSecret &&
            TopSecretPattern().IsMatch(content))
        {
            details = "TOP SECRET marking found in data labeled below TOP SECRET";
            return true;
        }

        // Check for SECRET markings in lower-classified data
        if (label.Level < ClassificationLevel.Secret &&
            SecretPattern().IsMatch(content))
        {
            details = "SECRET marking found in data labeled below SECRET";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates a pending transfer record for approval workflow.
    /// </summary>
    private PendingTransfer CreatePendingTransfer(
        string transferId,
        byte[] data,
        string sourceDomain,
        string targetDomain,
        SecurityLabel label,
        TransferPolicy policy,
        ContentFilterResult filterResult)
    {
        return new PendingTransfer
        {
            TransferId = transferId,
            Data = data,
            DataHash = SHA256.HashData(data),
            SourceDomain = sourceDomain,
            TargetDomain = targetDomain,
            Label = label,
            Policy = policy,
            CreatedAt = DateTimeOffset.UtcNow,
            ContentWarnings = filterResult.Warnings,
            Approvals = new Dictionary<string, ApprovalRecord>()
        };
    }

    /// <summary>
    /// Checks if all required approvals have been obtained.
    /// </summary>
    private static bool CheckAllApprovalsObtained(PendingTransfer pending)
    {
        if (pending.Policy.RequiredReviewers.Length == 0)
        {
            // If dual approval is required but no specific reviewers, need at least 2
            if (pending.Policy.RequireDualApproval)
            {
                lock (pending.Approvals)
                {
                    return pending.Approvals.Count >= 2;
                }
            }
            return true;
        }

        lock (pending.Approvals)
        {
            return pending.Policy.RequiredReviewers.All(r => pending.Approvals.ContainsKey(r));
        }
    }

    /// <summary>
    /// Gets the list of reviewers who have not yet approved.
    /// </summary>
    private static List<string> GetRemainingApprovals(PendingTransfer pending)
    {
        if (pending.Policy.RequiredReviewers.Length == 0)
        {
            if (pending.Policy.RequireDualApproval)
            {
                lock (pending.Approvals)
                {
                    var remaining = 2 - pending.Approvals.Count;
                    return remaining > 0
                        ? Enumerable.Range(1, remaining).Select(i => $"Reviewer {i}").ToList()
                        : new List<string>();
                }
            }
            return new List<string>();
        }

        lock (pending.Approvals)
        {
            return pending.Policy.RequiredReviewers
                .Where(r => !pending.Approvals.ContainsKey(r))
                .ToList();
        }
    }

    /// <summary>
    /// Executes the actual data transfer between domains.
    /// </summary>
    private Task<ExecuteResult> ExecuteTransferAsync(
        string transferId,
        byte[] data,
        string sourceDomain,
        string targetDomain,
        SecurityLabel label)
    {
        var auditEntries = new List<string>();

        try
        {
            lock (_transferLock)
            {
                // Generate transfer hash for verification
                var transferHash = SHA256.HashData(data);
                var hashHex = Convert.ToHexString(transferHash);

                auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Data hash: {hashHex}");

                // In a real implementation, this would:
                // 1. Connect to the guard hardware/software
                // 2. Apply any required transformations
                // 3. Route data through approved channels
                // 4. Verify receipt on target domain

                auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Guard processing completed");
                auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Data transferred to {targetDomain}");

                LogAuditEntry(transferId, "DATA_TRANSFERRED", sourceDomain, targetDomain,
                    $"Hash: {hashHex}, Size: {data.Length} bytes, Label: {label.Level}");
            }

            return Task.FromResult(new ExecuteResult(true, null, auditEntries));
        }
        catch (Exception ex)
        {
            auditEntries.Add($"[{DateTimeOffset.UtcNow:O}] Transfer execution failed: {ex.Message}");
            return Task.FromResult(new ExecuteResult(false, ex.Message, auditEntries));
        }
    }

    /// <summary>
    /// Logs an audit entry for cross-domain operations.
    /// </summary>
    private void LogAuditEntry(
        string transferId,
        string action,
        string sourceDomain,
        string targetDomain,
        string details)
    {
        _auditLog.Add(new CrossDomainAuditEntry
        {
            EntryId = Guid.NewGuid().ToString(),
            TransferId = transferId,
            Action = action,
            SourceDomain = sourceDomain,
            TargetDomain = targetDomain,
            Timestamp = DateTimeOffset.UtcNow,
            Details = details
        });
    }

    [GeneratedRegex(@"\bTOP\s*SECRET\b|\bTS\s*[/\\]\s*SCI\b", RegexOptions.IgnoreCase)]
    private static partial Regex TopSecretPattern();

    [GeneratedRegex(@"\bSECRET\b(?!\s*[/\\])", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();

    #endregion

    #region Nested Types

    /// <summary>
    /// Internal record for path validation results.
    /// </summary>
    private sealed record PathValidationResult(
        bool IsValid,
        string? Error,
        TransferPath? Path = null);

    /// <summary>
    /// Internal record for direction validation results.
    /// </summary>
    private sealed record DirectionValidationResult(
        bool IsValid,
        string? Error,
        string Direction);

    /// <summary>
    /// Internal record for content filter results.
    /// </summary>
    private sealed record ContentFilterResult(
        bool Passed,
        List<string> BlockedTerms,
        List<string> Warnings);

    /// <summary>
    /// Internal record for transfer execution results.
    /// </summary>
    private sealed record ExecuteResult(
        bool Success,
        string? Error,
        List<string> AdditionalAuditEntries);

    /// <summary>
    /// Internal class for pending transfer state.
    /// </summary>
    private sealed class PendingTransfer
    {
        public string TransferId { get; init; } = "";
        public byte[] Data { get; init; } = Array.Empty<byte>();
        public byte[] DataHash { get; init; } = Array.Empty<byte>();
        public string SourceDomain { get; init; } = "";
        public string TargetDomain { get; init; } = "";
        public SecurityLabel Label { get; init; } = null!;
        public TransferPolicy Policy { get; init; } = null!;
        public DateTimeOffset CreatedAt { get; init; }
        public List<string> ContentWarnings { get; init; } = new();
        public Dictionary<string, ApprovalRecord> Approvals { get; init; } = new();
    }

    /// <summary>
    /// Internal class for approval records.
    /// </summary>
    private sealed class ApprovalRecord
    {
        public string ReviewerId { get; init; } = "";
        public string ReviewerName { get; init; } = "";
        public DateTimeOffset ApprovedAt { get; init; }
        public string? Comments { get; init; }
    }

    /// <summary>
    /// Internal class for reviewer information.
    /// </summary>
    private sealed class ReviewerInfo
    {
        public string ReviewerId { get; init; } = "";
        public string Name { get; init; } = "";
        public ClassificationLevel ClearanceLevel { get; init; }
        public HashSet<string> AuthorizedDomains { get; init; } = new();
        public DateTimeOffset RegisteredAt { get; init; }
    }

    #endregion
}

#region Public Types

/// <summary>
/// Result of an approval operation for a pending cross-domain transfer.
/// </summary>
/// <param name="Success">True if the approval was recorded successfully.</param>
/// <param name="TransferExecuted">True if the transfer was executed after this approval.</param>
/// <param name="ErrorMessage">Error or status message.</param>
public record ApprovalResult(
    bool Success,
    bool TransferExecuted,
    string? ErrorMessage);

/// <summary>
/// Information about a pending cross-domain transfer.
/// </summary>
/// <param name="TransferId">Unique transfer identifier.</param>
/// <param name="SourceDomain">Source security domain.</param>
/// <param name="TargetDomain">Target security domain.</param>
/// <param name="DataSize">Size of the data being transferred in bytes.</param>
/// <param name="Label">Security label of the data.</param>
/// <param name="CreatedAt">When the transfer was initiated.</param>
/// <param name="ApprovedBy">IDs of reviewers who have already approved.</param>
/// <param name="AwaitingApprovalFrom">IDs of reviewers whose approval is still needed.</param>
/// <param name="ContentWarnings">Warnings from content filtering.</param>
public record PendingTransferInfo(
    string TransferId,
    string SourceDomain,
    string TargetDomain,
    int DataSize,
    SecurityLabel Label,
    DateTimeOffset CreatedAt,
    string[] ApprovedBy,
    string[] AwaitingApprovalFrom,
    string[] ContentWarnings);

/// <summary>
/// Audit entry for cross-domain transfer operations.
/// Provides complete audit trail for compliance review.
/// </summary>
public class CrossDomainAuditEntry
{
    /// <summary>
    /// Unique entry identifier.
    /// </summary>
    public string EntryId { get; init; } = "";

    /// <summary>
    /// Associated transfer ID.
    /// </summary>
    public string TransferId { get; init; } = "";

    /// <summary>
    /// Action performed (TRANSFER_INITIATED, PATH_VALIDATION_FAILED, CONTENT_FILTER_FAILED,
    /// APPROVAL_GRANTED, APPROVAL_DENIED, TRANSFER_COMPLETED, TRANSFER_REJECTED, etc.).
    /// </summary>
    public string Action { get; init; } = "";

    /// <summary>
    /// Source security domain.
    /// </summary>
    public string SourceDomain { get; init; } = "";

    /// <summary>
    /// Target security domain.
    /// </summary>
    public string TargetDomain { get; init; } = "";

    /// <summary>
    /// Timestamp of the action.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Additional details about the action.
    /// </summary>
    public string Details { get; init; } = "";
}

#endregion
