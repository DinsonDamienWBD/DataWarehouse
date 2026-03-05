using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Branching;

/// <summary>
/// Status of a data branch.
/// </summary>
public enum BranchStatus
{
    /// <summary>Active branch accepting operations.</summary>
    Active,
    /// <summary>Branch is locked/read-only.</summary>
    Locked,
    /// <summary>Branch is being merged.</summary>
    Merging,
    /// <summary>Branch is archived.</summary>
    Archived,
    /// <summary>Branch is marked for deletion.</summary>
    Deleted
}

/// <summary>
/// Type of permission for branch access control.
/// </summary>
[Flags]
public enum BranchPermission
{
    /// <summary>No permissions.</summary>
    None = 0,
    /// <summary>Can read branch data.</summary>
    Read = 1,
    /// <summary>Can write to branch.</summary>
    Write = 2,
    /// <summary>Can create child branches.</summary>
    Branch = 4,
    /// <summary>Can merge branches.</summary>
    Merge = 8,
    /// <summary>Can delete branch.</summary>
    Delete = 16,
    /// <summary>Can manage permissions.</summary>
    Admin = 32,
    /// <summary>Full access.</summary>
    Full = Read | Write | Branch | Merge | Delete | Admin
}

/// <summary>
/// Status of a pull request.
/// </summary>
public enum PullRequestStatus
{
    /// <summary>Pull request is open.</summary>
    Open,
    /// <summary>Pull request is under review.</summary>
    InReview,
    /// <summary>Pull request is approved.</summary>
    Approved,
    /// <summary>Pull request is rejected.</summary>
    Rejected,
    /// <summary>Pull request is merged.</summary>
    Merged,
    /// <summary>Pull request is closed without merge.</summary>
    Closed
}

/// <summary>
/// Type of conflict during merge.
/// </summary>
public enum ConflictType
{
    /// <summary>Same block modified in both branches.</summary>
    ContentModification,
    /// <summary>Block deleted in one branch, modified in another.</summary>
    DeleteModify,
    /// <summary>Conflicting metadata changes.</summary>
    MetadataConflict,
    /// <summary>Schema version incompatibility.</summary>
    SchemaConflict
}

/// <summary>
/// Conflict resolution strategy.
/// </summary>
public enum ConflictResolutionStrategy
{
    /// <summary>Keep source branch version.</summary>
    KeepSource,
    /// <summary>Keep target branch version.</summary>
    KeepTarget,
    /// <summary>Keep newer modification.</summary>
    KeepNewer,
    /// <summary>Keep older modification.</summary>
    KeepOlder,
    /// <summary>Manual resolution required.</summary>
    Manual,
    /// <summary>Merge both changes (if possible).</summary>
    MergeBoth
}

/// <summary>
/// Represents a data block with copy-on-write semantics.
/// </summary>
public sealed class DataBlock
{
    /// <summary>Reference count backing field for atomic operations.</summary>
    private int _referenceCount = 1;

    /// <summary>Unique block identifier.</summary>
    public required string BlockId { get; init; }

    /// <summary>SHA-256 content hash for deduplication.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Block data (null if using CoW reference).</summary>
    public byte[]? Data { get; set; }

    /// <summary>Reference to parent block for CoW.</summary>
    public string? ParentBlockId { get; init; }

    /// <summary>Block size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>When block was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Reference count for garbage collection.</summary>
    public int ReferenceCount
    {
        get => _referenceCount;
        set => _referenceCount = value;
    }

    /// <summary>Atomically increments the reference count.</summary>
    public int IncrementReferenceCount() => Interlocked.Increment(ref _referenceCount);

    /// <summary>Atomically decrements the reference count.</summary>
    public int DecrementReferenceCount() => Interlocked.Decrement(ref _referenceCount);

    /// <summary>Whether this block is a CoW reference (no local data).</summary>
    public bool IsCoWReference => Data == null && ParentBlockId != null;
}

/// <summary>
/// Represents a data branch.
/// </summary>
public sealed class DataBranch
{
    /// <summary>Unique branch identifier.</summary>
    public required string BranchId { get; init; }

    /// <summary>Human-readable branch name.</summary>
    public required string Name { get; init; }

    /// <summary>Object this branch belongs to.</summary>
    public required string ObjectId { get; init; }

    /// <summary>Parent branch ID (null for root/main).</summary>
    public string? ParentBranchId { get; init; }

    /// <summary>Version ID where branch was created.</summary>
    public string? BranchPoint { get; init; }

    /// <summary>Current branch status.</summary>
    public BranchStatus Status { get; set; } = BranchStatus.Active;

    /// <summary>When branch was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When branch was last modified.</summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    /// <summary>User who created the branch.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Description of the branch purpose.</summary>
    public string? Description { get; init; }

    /// <summary>Block IDs in this branch (HashSet for O(1) lookup).</summary>
    public HashSet<string> BlockIds { get; } = [];

    /// <summary>Tags applied to this branch.</summary>
    public Dictionary<string, string> Tags { get; } = [];

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, object> Metadata { get; } = [];

    /// <summary>Number of commits/versions on this branch.</summary>
    public long CommitCount { get; set; }
}

/// <summary>
/// Information about a branch for external API.
/// </summary>
public sealed record BranchInfo
{
    /// <summary>Unique branch identifier.</summary>
    public required string BranchId { get; init; }

    /// <summary>Human-readable branch name.</summary>
    public required string Name { get; init; }

    /// <summary>Object this branch belongs to.</summary>
    public required string ObjectId { get; init; }

    /// <summary>Parent branch name.</summary>
    public string? ParentBranch { get; init; }

    /// <summary>Current branch status.</summary>
    public BranchStatus Status { get; init; }

    /// <summary>When branch was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>When branch was last modified.</summary>
    public DateTime ModifiedAt { get; init; }

    /// <summary>User who created the branch.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Total size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Number of blocks.</summary>
    public int BlockCount { get; init; }

    /// <summary>Number of commits.</summary>
    public long CommitCount { get; init; }

    /// <summary>Whether branch is the main/default branch.</summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// Represents a conflict between branches.
/// </summary>
public sealed class MergeConflict
{
    /// <summary>Unique conflict identifier.</summary>
    public required string ConflictId { get; init; }

    /// <summary>Type of conflict.</summary>
    public ConflictType Type { get; init; }

    /// <summary>Block ID where conflict occurred.</summary>
    public required string BlockId { get; init; }

    /// <summary>Source branch version.</summary>
    public byte[]? SourceData { get; init; }

    /// <summary>Target branch version.</summary>
    public byte[]? TargetData { get; init; }

    /// <summary>Common ancestor version.</summary>
    public byte[]? AncestorData { get; init; }

    /// <summary>Resolution if resolved.</summary>
    public byte[]? ResolvedData { get; set; }

    /// <summary>Resolution strategy used.</summary>
    public ConflictResolutionStrategy? Resolution { get; set; }

    /// <summary>User who resolved conflict.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>When conflict was resolved.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Whether conflict is resolved.</summary>
    public bool IsResolved => Resolution.HasValue;
}

/// <summary>
/// Result of a merge operation.
/// </summary>
public sealed class MergeResult
{
    /// <summary>Whether merge succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Whether there were conflicts.</summary>
    public bool HasConflicts { get; init; }

    /// <summary>List of conflicts if any.</summary>
    public List<MergeConflict> Conflicts { get; init; } = [];

    /// <summary>Resulting merge commit ID.</summary>
    public string? MergeCommitId { get; init; }

    /// <summary>Status message.</summary>
    public string? Message { get; init; }

    /// <summary>Number of blocks merged.</summary>
    public int BlocksMerged { get; init; }

    /// <summary>Time taken for merge in milliseconds.</summary>
    public double MergeTimeMs { get; init; }
}

/// <summary>
/// Represents a diff between two branches.
/// </summary>
public sealed class BranchDiff
{
    /// <summary>Source branch ID.</summary>
    public required string SourceBranchId { get; init; }

    /// <summary>Target branch ID.</summary>
    public required string TargetBranchId { get; init; }

    /// <summary>Blocks only in source.</summary>
    public List<string> AddedBlocks { get; init; } = [];

    /// <summary>Blocks only in target.</summary>
    public List<string> RemovedBlocks { get; init; } = [];

    /// <summary>Blocks modified between branches.</summary>
    public List<string> ModifiedBlocks { get; init; } = [];

    /// <summary>Size difference in bytes.</summary>
    public long SizeDifference { get; init; }

    /// <summary>Whether branches are identical.</summary>
    public bool IsIdentical => AddedBlocks.Count == 0 && RemovedBlocks.Count == 0 && ModifiedBlocks.Count == 0;

    /// <summary>Common ancestor branch ID.</summary>
    public string? CommonAncestor { get; init; }

    /// <summary>Diff summary.</summary>
    public string? Summary { get; init; }
}

/// <summary>
/// Represents a pull request for merge review.
/// </summary>
public sealed class PullRequest
{
    /// <summary>Unique pull request identifier.</summary>
    public required string PullRequestId { get; init; }

    /// <summary>Title of the pull request.</summary>
    public required string Title { get; init; }

    /// <summary>Description of changes.</summary>
    public string? Description { get; init; }

    /// <summary>Source branch to merge from.</summary>
    public required string SourceBranchId { get; init; }

    /// <summary>Target branch to merge into.</summary>
    public required string TargetBranchId { get; init; }

    /// <summary>Current status.</summary>
    public PullRequestStatus Status { get; set; } = PullRequestStatus.Open;

    /// <summary>User who created the PR.</summary>
    public required string CreatedBy { get; init; }

    /// <summary>When PR was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>When PR was last updated.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Reviewers assigned to the PR.</summary>
    public List<string> Reviewers { get; init; } = [];

    /// <summary>Approvals received.</summary>
    public List<PullRequestApproval> Approvals { get; } = [];

    /// <summary>Comments on the PR.</summary>
    public List<PullRequestComment> Comments { get; } = [];

    /// <summary>Labels/tags for categorization.</summary>
    public List<string> Labels { get; init; } = [];

    /// <summary>Merge result if merged.</summary>
    public MergeResult? MergeResult { get; set; }
}

/// <summary>
/// Approval on a pull request.
/// </summary>
public sealed record PullRequestApproval
{
    /// <summary>User who approved.</summary>
    public required string ApprovedBy { get; init; }

    /// <summary>When approval was given.</summary>
    public DateTime ApprovedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Optional comment with approval.</summary>
    public string? Comment { get; init; }
}

/// <summary>
/// Comment on a pull request.
/// </summary>
public sealed record PullRequestComment
{
    /// <summary>Comment identifier.</summary>
    public required string CommentId { get; init; }

    /// <summary>User who commented.</summary>
    public required string Author { get; init; }

    /// <summary>Comment text.</summary>
    public required string Text { get; init; }

    /// <summary>When comment was posted.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Block ID if comment is on specific block.</summary>
    public string? BlockId { get; init; }
}

/// <summary>
/// Branch permission entry for access control.
/// </summary>
public sealed record BranchPermissionEntry
{
    /// <summary>Principal (user or role) ID.</summary>
    public required string PrincipalId { get; init; }

    /// <summary>Whether this is a role (vs user).</summary>
    public bool IsRole { get; init; }

    /// <summary>Permissions granted.</summary>
    public BranchPermission Permissions { get; init; }

    /// <summary>When permission was granted.</summary>
    public DateTime GrantedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Who granted the permission.</summary>
    public string? GrantedBy { get; init; }
}

/// <summary>
/// Branch tree node for visualization.
/// </summary>
public sealed class BranchTreeNode
{
    /// <summary>Branch information.</summary>
    public required BranchInfo Branch { get; init; }

    /// <summary>Child branches.</summary>
    public List<BranchTreeNode> Children { get; init; } = [];

    /// <summary>Depth in tree (0 for root).</summary>
    public int Depth { get; init; }

    /// <summary>Visual representation prefix.</summary>
    public string? TreePrefix { get; init; }
}

/// <summary>
/// Garbage collection statistics.
/// </summary>
public sealed record GarbageCollectionStats
{
    /// <summary>Number of blocks scanned.</summary>
    public long BlocksScanned { get; init; }

    /// <summary>Number of orphaned blocks found.</summary>
    public long OrphanedBlocks { get; init; }

    /// <summary>Bytes reclaimed.</summary>
    public long BytesReclaimed { get; init; }

    /// <summary>Number of branches cleaned.</summary>
    public int BranchesCleaned { get; init; }

    /// <summary>Time taken in milliseconds.</summary>
    public double DurationMs { get; init; }

    /// <summary>When GC was run.</summary>
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Errors encountered.</summary>
    public List<string> Errors { get; init; } = [];
}

/// <summary>
/// Options for garbage collection.
/// </summary>
public sealed class GarbageCollectionOptions
{
    /// <summary>Minimum age of deleted branches to collect (default: 7 days).</summary>
    public TimeSpan MinimumAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Whether to perform dry run only.</summary>
    public bool DryRun { get; init; }

    /// <summary>Maximum blocks to process per run.</summary>
    public int MaxBlocksPerRun { get; init; } = 100_000;

    /// <summary>Whether to compact storage after collection.</summary>
    public bool Compact { get; init; } = true;

    /// <summary>Whether to run async in background.</summary>
    public bool RunAsync { get; init; } = true;
}

/// <summary>
/// Options for creating a branch.
/// </summary>
public sealed class CreateBranchOptions
{
    /// <summary>Description of the branch.</summary>
    public string? Description { get; init; }

    /// <summary>User creating the branch.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Initial tags.</summary>
    public Dictionary<string, string>? Tags { get; init; }

    /// <summary>Copy permissions from parent.</summary>
    public bool InheritPermissions { get; init; } = true;
}

/// <summary>
/// Interface for data branching strategies (T82).
/// Provides Git-for-Data functionality including branching, merging, diff, and version control.
/// </summary>
public interface IBranchingStrategy : IDataManagementStrategy
{
    // 82.1 - Branch Creation
    /// <summary>Creates a new branch from the specified point.</summary>
    Task<BranchInfo> CreateBranchAsync(string objectId, string branchName, string? fromBranch = null, string? fromVersion = null, CreateBranchOptions? options = null, CancellationToken ct = default);

    /// <summary>Gets branch information.</summary>
    Task<BranchInfo?> GetBranchAsync(string objectId, string branchName, CancellationToken ct = default);

    /// <summary>Lists all branches for an object.</summary>
    Task<IEnumerable<BranchInfo>> ListBranchesAsync(string objectId, bool includeDeleted = false, CancellationToken ct = default);

    /// <summary>Deletes a branch (soft delete).</summary>
    Task<bool> DeleteBranchAsync(string objectId, string branchName, CancellationToken ct = default);

    // 82.2 - Copy-on-Write Engine
    /// <summary>Writes data to a branch using copy-on-write semantics.</summary>
    Task<string> WriteBlockAsync(string objectId, string branchName, byte[] data, CancellationToken ct = default);

    /// <summary>Reads data from a branch.</summary>
    Task<byte[]?> ReadBlockAsync(string objectId, string branchName, string blockId, CancellationToken ct = default);

    /// <summary>Gets CoW statistics for an object.</summary>
    Task<(long UniqueBlocks, long TotalReferences, long SavedBytes)> GetCoWStatsAsync(string objectId, CancellationToken ct = default);

    // 82.3 - Branch Registry (covered by Get/List methods above)

    // 82.4 - Diff Engine
    /// <summary>Calculates differences between two branches.</summary>
    Task<BranchDiff> DiffBranchesAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct = default);

    // 82.5 - Merge Engine
    /// <summary>Merges source branch into target branch.</summary>
    Task<MergeResult> MergeBranchAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct = default);

    /// <summary>Performs a three-way merge with explicit base.</summary>
    Task<MergeResult> ThreeWayMergeAsync(string objectId, string sourceBranch, string targetBranch, string baseBranch, CancellationToken ct = default);

    // 82.6 - Conflict Resolution
    /// <summary>Gets unresolved conflicts for a merge.</summary>
    Task<IEnumerable<MergeConflict>> GetConflictsAsync(string objectId, string mergeId, CancellationToken ct = default);

    /// <summary>Resolves a conflict with the specified strategy.</summary>
    Task<bool> ResolveConflictAsync(string objectId, string conflictId, ConflictResolutionStrategy strategy, byte[]? manualResolution = null, string? resolvedBy = null, CancellationToken ct = default);

    /// <summary>Resolves all conflicts with automatic strategy.</summary>
    Task<int> AutoResolveConflictsAsync(string objectId, string mergeId, ConflictResolutionStrategy defaultStrategy, CancellationToken ct = default);

    // 82.7 - Branch Visualization
    /// <summary>Gets the branch tree structure for visualization.</summary>
    Task<BranchTreeNode> GetBranchTreeAsync(string objectId, CancellationToken ct = default);

    /// <summary>Gets ASCII art representation of branch tree.</summary>
    Task<string> GetBranchTreeTextAsync(string objectId, CancellationToken ct = default);

    // 82.8 - Pull Requests
    /// <summary>Creates a pull request for merge review.</summary>
    Task<PullRequest> CreatePullRequestAsync(string objectId, string title, string sourceBranch, string targetBranch, string createdBy, string? description = null, CancellationToken ct = default);

    /// <summary>Gets a pull request.</summary>
    Task<PullRequest?> GetPullRequestAsync(string objectId, string pullRequestId, CancellationToken ct = default);

    /// <summary>Lists pull requests.</summary>
    Task<IEnumerable<PullRequest>> ListPullRequestsAsync(string objectId, PullRequestStatus? status = null, CancellationToken ct = default);

    /// <summary>Approves a pull request.</summary>
    Task<bool> ApprovePullRequestAsync(string objectId, string pullRequestId, string approver, string? comment = null, CancellationToken ct = default);

    /// <summary>Merges a pull request.</summary>
    Task<MergeResult> MergePullRequestAsync(string objectId, string pullRequestId, string mergedBy, CancellationToken ct = default);

    /// <summary>Closes a pull request without merging.</summary>
    Task<bool> ClosePullRequestAsync(string objectId, string pullRequestId, string closedBy, string? reason = null, CancellationToken ct = default);

    // 82.9 - Branch Permissions
    /// <summary>Sets permissions for a branch.</summary>
    Task<bool> SetBranchPermissionsAsync(string objectId, string branchName, string principalId, BranchPermission permissions, bool isRole = false, string? grantedBy = null, CancellationToken ct = default);

    /// <summary>Gets permissions for a branch.</summary>
    Task<IEnumerable<BranchPermissionEntry>> GetBranchPermissionsAsync(string objectId, string branchName, CancellationToken ct = default);

    /// <summary>Checks if principal has permission on branch.</summary>
    Task<bool> HasPermissionAsync(string objectId, string branchName, string principalId, BranchPermission permission, CancellationToken ct = default);

    /// <summary>Removes permissions for a principal.</summary>
    Task<bool> RemovePermissionsAsync(string objectId, string branchName, string principalId, CancellationToken ct = default);

    // 82.10 - Garbage Collection
    /// <summary>Runs garbage collection to reclaim space from deleted branches.</summary>
    Task<GarbageCollectionStats> RunGarbageCollectionAsync(string objectId, GarbageCollectionOptions? options = null, CancellationToken ct = default);

    /// <summary>Gets estimated garbage for potential collection.</summary>
    Task<(long OrphanedBlocks, long ReclaimableBytes)> EstimateGarbageAsync(string objectId, CancellationToken ct = default);
}

/// <summary>
/// Abstract base class for data branching strategies.
/// Implements T82 Data Branching / Git-for-Data functionality.
/// </summary>
public abstract class BranchingStrategyBase : DataManagementStrategyBase, IBranchingStrategy
{
    /// <summary>Default branch name.</summary>
    protected const string DefaultBranchName = "main";

    /// <inheritdoc/>
    public override DataManagementCategory Category => DataManagementCategory.Branching;

    #region 82.1 - Branch Creation

    /// <inheritdoc/>
    public async Task<BranchInfo> CreateBranchAsync(string objectId, string branchName, string? fromBranch = null, string? fromVersion = null, CreateBranchOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await CreateBranchCoreAsync(objectId, branchName, fromBranch ?? DefaultBranchName, fromVersion, options ?? new CreateBranchOptions(), ct);
            sw.Stop();
            RecordWrite(0, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<BranchInfo?> GetBranchAsync(string objectId, string branchName, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        return await GetBranchCoreAsync(objectId, branchName, ct);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<BranchInfo>> ListBranchesAsync(string objectId, bool includeDeleted = false, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return await ListBranchesCoreAsync(objectId, includeDeleted, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteBranchAsync(string objectId, string branchName, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        if (branchName.Equals(DefaultBranchName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot delete the default branch.");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await DeleteBranchCoreAsync(objectId, branchName, ct);
            sw.Stop();
            RecordDelete(sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    #endregion

    #region 82.2 - Copy-on-Write Engine

    /// <inheritdoc/>
    public async Task<string> WriteBlockAsync(string objectId, string branchName, byte[] data, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentNullException.ThrowIfNull(data);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await WriteBlockCoreAsync(objectId, branchName, data, ct);
            sw.Stop();
            RecordWrite(data.Length, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<byte[]?> ReadBlockAsync(string objectId, string branchName, string blockId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await ReadBlockCoreAsync(objectId, branchName, blockId, ct);
            sw.Stop();
            RecordRead(result?.Length ?? 0, sw.Elapsed.TotalMilliseconds, hit: result != null);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<(long UniqueBlocks, long TotalReferences, long SavedBytes)> GetCoWStatsAsync(string objectId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return await GetCoWStatsCoreAsync(objectId, ct);
    }

    #endregion

    #region 82.4 - Diff Engine

    /// <inheritdoc/>
    public async Task<BranchDiff> DiffBranchesAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);

        return await DiffBranchesCoreAsync(objectId, sourceBranch, targetBranch, ct);
    }

    #endregion

    #region 82.5 - Merge Engine

    /// <inheritdoc/>
    public async Task<MergeResult> MergeBranchAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await MergeBranchCoreAsync(objectId, sourceBranch, targetBranch, ct);
            sw.Stop();
            RecordWrite(0, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<MergeResult> ThreeWayMergeAsync(string objectId, string sourceBranch, string targetBranch, string baseBranch, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        return await ThreeWayMergeCoreAsync(objectId, sourceBranch, targetBranch, baseBranch, ct);
    }

    #endregion

    #region 82.6 - Conflict Resolution

    /// <inheritdoc/>
    public async Task<IEnumerable<MergeConflict>> GetConflictsAsync(string objectId, string mergeId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mergeId);

        return await GetConflictsCoreAsync(objectId, mergeId, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> ResolveConflictAsync(string objectId, string conflictId, ConflictResolutionStrategy strategy, byte[]? manualResolution = null, string? resolvedBy = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conflictId);

        if (strategy == ConflictResolutionStrategy.Manual && manualResolution == null)
            throw new ArgumentException("Manual resolution requires resolution data.", nameof(manualResolution));

        return await ResolveConflictCoreAsync(objectId, conflictId, strategy, manualResolution, resolvedBy, ct);
    }

    /// <inheritdoc/>
    public async Task<int> AutoResolveConflictsAsync(string objectId, string mergeId, ConflictResolutionStrategy defaultStrategy, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mergeId);

        return await AutoResolveConflictsCoreAsync(objectId, mergeId, defaultStrategy, ct);
    }

    #endregion

    #region 82.7 - Branch Visualization

    /// <inheritdoc/>
    public async Task<BranchTreeNode> GetBranchTreeAsync(string objectId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return await GetBranchTreeCoreAsync(objectId, ct);
    }

    /// <inheritdoc/>
    public async Task<string> GetBranchTreeTextAsync(string objectId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return await GetBranchTreeTextCoreAsync(objectId, ct);
    }

    #endregion

    #region 82.8 - Pull Requests

    /// <inheritdoc/>
    public async Task<PullRequest> CreatePullRequestAsync(string objectId, string title, string sourceBranch, string targetBranch, string createdBy, string? description = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);

        return await CreatePullRequestCoreAsync(objectId, title, sourceBranch, targetBranch, createdBy, description, ct);
    }

    /// <inheritdoc/>
    public async Task<PullRequest?> GetPullRequestAsync(string objectId, string pullRequestId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pullRequestId);

        return await GetPullRequestCoreAsync(objectId, pullRequestId, ct);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<PullRequest>> ListPullRequestsAsync(string objectId, PullRequestStatus? status = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return await ListPullRequestsCoreAsync(objectId, status, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> ApprovePullRequestAsync(string objectId, string pullRequestId, string approver, string? comment = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pullRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);

        return await ApprovePullRequestCoreAsync(objectId, pullRequestId, approver, comment, ct);
    }

    /// <inheritdoc/>
    public async Task<MergeResult> MergePullRequestAsync(string objectId, string pullRequestId, string mergedBy, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pullRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mergedBy);

        return await MergePullRequestCoreAsync(objectId, pullRequestId, mergedBy, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> ClosePullRequestAsync(string objectId, string pullRequestId, string closedBy, string? reason = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pullRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(closedBy);

        return await ClosePullRequestCoreAsync(objectId, pullRequestId, closedBy, reason, ct);
    }

    #endregion

    #region 82.9 - Branch Permissions

    /// <inheritdoc/>
    public async Task<bool> SetBranchPermissionsAsync(string objectId, string branchName, string principalId, BranchPermission permissions, bool isRole = false, string? grantedBy = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        return await SetBranchPermissionsCoreAsync(objectId, branchName, principalId, permissions, isRole, grantedBy, ct);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<BranchPermissionEntry>> GetBranchPermissionsAsync(string objectId, string branchName, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        return await GetBranchPermissionsCoreAsync(objectId, branchName, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> HasPermissionAsync(string objectId, string branchName, string principalId, BranchPermission permission, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        return await HasPermissionCoreAsync(objectId, branchName, principalId, permission, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> RemovePermissionsAsync(string objectId, string branchName, string principalId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        return await RemovePermissionsCoreAsync(objectId, branchName, principalId, ct);
    }

    #endregion

    #region 82.10 - Garbage Collection

    /// <inheritdoc/>
    public async Task<GarbageCollectionStats> RunGarbageCollectionAsync(string objectId, GarbageCollectionOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RunGarbageCollectionCoreAsync(objectId, options ?? new GarbageCollectionOptions(), ct);
            sw.Stop();
            RecordDelete(sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<(long OrphanedBlocks, long ReclaimableBytes)> EstimateGarbageAsync(string objectId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        return await EstimateGarbageCoreAsync(objectId, ct);
    }

    #endregion

    #region Abstract Core Methods

    /// <summary>Core implementation for creating a branch.</summary>
    protected abstract Task<BranchInfo> CreateBranchCoreAsync(string objectId, string branchName, string fromBranch, string? fromVersion, CreateBranchOptions options, CancellationToken ct);

    /// <summary>Core implementation for getting branch info.</summary>
    protected abstract Task<BranchInfo?> GetBranchCoreAsync(string objectId, string branchName, CancellationToken ct);

    /// <summary>Core implementation for listing branches.</summary>
    protected abstract Task<IEnumerable<BranchInfo>> ListBranchesCoreAsync(string objectId, bool includeDeleted, CancellationToken ct);

    /// <summary>Core implementation for deleting a branch.</summary>
    protected abstract Task<bool> DeleteBranchCoreAsync(string objectId, string branchName, CancellationToken ct);

    /// <summary>Core implementation for writing a block with CoW.</summary>
    protected abstract Task<string> WriteBlockCoreAsync(string objectId, string branchName, byte[] data, CancellationToken ct);

    /// <summary>Core implementation for reading a block.</summary>
    protected abstract Task<byte[]?> ReadBlockCoreAsync(string objectId, string branchName, string blockId, CancellationToken ct);

    /// <summary>Core implementation for getting CoW statistics.</summary>
    protected abstract Task<(long UniqueBlocks, long TotalReferences, long SavedBytes)> GetCoWStatsCoreAsync(string objectId, CancellationToken ct);

    /// <summary>Core implementation for diffing branches.</summary>
    protected abstract Task<BranchDiff> DiffBranchesCoreAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct);

    /// <summary>Core implementation for merging branches.</summary>
    protected abstract Task<MergeResult> MergeBranchCoreAsync(string objectId, string sourceBranch, string targetBranch, CancellationToken ct);

    /// <summary>Core implementation for three-way merge.</summary>
    protected abstract Task<MergeResult> ThreeWayMergeCoreAsync(string objectId, string sourceBranch, string targetBranch, string baseBranch, CancellationToken ct);

    /// <summary>Core implementation for getting conflicts.</summary>
    protected abstract Task<IEnumerable<MergeConflict>> GetConflictsCoreAsync(string objectId, string mergeId, CancellationToken ct);

    /// <summary>Core implementation for resolving a conflict.</summary>
    protected abstract Task<bool> ResolveConflictCoreAsync(string objectId, string conflictId, ConflictResolutionStrategy strategy, byte[]? manualResolution, string? resolvedBy, CancellationToken ct);

    /// <summary>Core implementation for auto-resolving conflicts.</summary>
    protected abstract Task<int> AutoResolveConflictsCoreAsync(string objectId, string mergeId, ConflictResolutionStrategy defaultStrategy, CancellationToken ct);

    /// <summary>Core implementation for getting branch tree.</summary>
    protected abstract Task<BranchTreeNode> GetBranchTreeCoreAsync(string objectId, CancellationToken ct);

    /// <summary>Core implementation for getting branch tree text.</summary>
    protected abstract Task<string> GetBranchTreeTextCoreAsync(string objectId, CancellationToken ct);

    /// <summary>Core implementation for creating a pull request.</summary>
    protected abstract Task<PullRequest> CreatePullRequestCoreAsync(string objectId, string title, string sourceBranch, string targetBranch, string createdBy, string? description, CancellationToken ct);

    /// <summary>Core implementation for getting a pull request.</summary>
    protected abstract Task<PullRequest?> GetPullRequestCoreAsync(string objectId, string pullRequestId, CancellationToken ct);

    /// <summary>Core implementation for listing pull requests.</summary>
    protected abstract Task<IEnumerable<PullRequest>> ListPullRequestsCoreAsync(string objectId, PullRequestStatus? status, CancellationToken ct);

    /// <summary>Core implementation for approving a pull request.</summary>
    protected abstract Task<bool> ApprovePullRequestCoreAsync(string objectId, string pullRequestId, string approver, string? comment, CancellationToken ct);

    /// <summary>Core implementation for merging a pull request.</summary>
    protected abstract Task<MergeResult> MergePullRequestCoreAsync(string objectId, string pullRequestId, string mergedBy, CancellationToken ct);

    /// <summary>Core implementation for closing a pull request.</summary>
    protected abstract Task<bool> ClosePullRequestCoreAsync(string objectId, string pullRequestId, string closedBy, string? reason, CancellationToken ct);

    /// <summary>Core implementation for setting branch permissions.</summary>
    protected abstract Task<bool> SetBranchPermissionsCoreAsync(string objectId, string branchName, string principalId, BranchPermission permissions, bool isRole, string? grantedBy, CancellationToken ct);

    /// <summary>Core implementation for getting branch permissions.</summary>
    protected abstract Task<IEnumerable<BranchPermissionEntry>> GetBranchPermissionsCoreAsync(string objectId, string branchName, CancellationToken ct);

    /// <summary>Core implementation for checking permissions.</summary>
    protected abstract Task<bool> HasPermissionCoreAsync(string objectId, string branchName, string principalId, BranchPermission permission, CancellationToken ct);

    /// <summary>Core implementation for removing permissions.</summary>
    protected abstract Task<bool> RemovePermissionsCoreAsync(string objectId, string branchName, string principalId, CancellationToken ct);

    /// <summary>Core implementation for running garbage collection.</summary>
    protected abstract Task<GarbageCollectionStats> RunGarbageCollectionCoreAsync(string objectId, GarbageCollectionOptions options, CancellationToken ct);

    /// <summary>Core implementation for estimating garbage.</summary>
    protected abstract Task<(long OrphanedBlocks, long ReclaimableBytes)> EstimateGarbageCoreAsync(string objectId, CancellationToken ct);

    #endregion

    #region Utility Methods

    /// <summary>Computes SHA-256 hash of data.</summary>
    protected static string ComputeHash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Generates a unique identifier.</summary>
    protected static string GenerateId() => Guid.NewGuid().ToString("N")[..12];

    #endregion
}
