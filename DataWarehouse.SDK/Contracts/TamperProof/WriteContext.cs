// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Text.Json.Serialization;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Required context for all write operations in tamper-proof storage.
/// Like a git commit, every write MUST have an author and comment.
/// This context is stored in the manifest and blockchain anchor for audit trail.
/// </summary>
public class WriteContext
{
    /// <summary>
    /// Author/principal performing the write. REQUIRED.
    /// Can be username, service account, or system identifier.
    /// Maximum length: 256 characters.
    /// </summary>
    public required string Author { get; init; }

    /// <summary>
    /// Comment explaining what is being written and why. REQUIRED.
    /// Like a git commit message - should explain the purpose.
    /// Maximum length: 4096 characters.
    /// </summary>
    public required string Comment { get; init; }

    /// <summary>
    /// Optional session identifier for correlation across operations.
    /// Useful for grouping related operations within a user session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Optional source system identifier.
    /// Identifies which application or service initiated the write.
    /// </summary>
    public string? SourceSystem { get; init; }

    /// <summary>
    /// Optional client IP address for attribution.
    /// Captured from the request context.
    /// </summary>
    public string? ClientIp { get; init; }

    /// <summary>
    /// Optional user agent string for attribution.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Optional correlation ID for distributed tracing.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Optional additional metadata for the write operation.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Timestamp when this context was created.
    /// Auto-populated if not set.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Maximum allowed length for Author field.
    /// </summary>
    public const int MaxAuthorLength = 256;

    /// <summary>
    /// Maximum allowed length for Comment field.
    /// </summary>
    public const int MaxCommentLength = 4096;

    /// <summary>
    /// Validates the write context for completeness and correctness.
    /// </summary>
    /// <returns>List of validation errors, empty if valid.</returns>
    public virtual IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Author))
        {
            errors.Add("Author is required and cannot be empty or whitespace.");
        }
        else if (Author.Length > MaxAuthorLength)
        {
            errors.Add($"Author cannot exceed {MaxAuthorLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(Comment))
        {
            errors.Add("Comment is required and cannot be empty or whitespace.");
        }
        else if (Comment.Length > MaxCommentLength)
        {
            errors.Add($"Comment cannot exceed {MaxCommentLength} characters.");
        }

        return errors;
    }

    /// <summary>
    /// Throws if validation fails.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when validation fails.</exception>
    public void ValidateOrThrow()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException($"WriteContext validation failed: {string.Join("; ", errors)}");
        }
    }

    /// <summary>
    /// Creates a record for storage in manifests.
    /// </summary>
    public WriteContextRecord ToRecord()
    {
        return new WriteContextRecord
        {
            Author = Author,
            Comment = Comment,
            SessionId = SessionId,
            SourceSystem = SourceSystem,
            ClientIp = ClientIp,
            UserAgent = UserAgent,
            CorrelationId = CorrelationId,
            Timestamp = Timestamp,
            Metadata = Metadata is not null ? new Dictionary<string, object>(Metadata) : null
        };
    }

    /// <summary>
    /// Creates a copy of this context with updated timestamp.
    /// </summary>
    public WriteContext WithCurrentTimestamp()
    {
        return new WriteContext
        {
            Author = Author,
            Comment = Comment,
            SessionId = SessionId,
            SourceSystem = SourceSystem,
            ClientIp = ClientIp,
            UserAgent = UserAgent,
            CorrelationId = CorrelationId,
            Metadata = Metadata,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Context for correction operations.
/// Extends WriteContext with correction-specific information.
/// </summary>
public class CorrectionContext : WriteContext
{
    /// <summary>
    /// Reason for the correction. REQUIRED.
    /// Should explain what was wrong with the original data.
    /// Maximum length: 4096 characters.
    /// </summary>
    public required string CorrectionReason { get; init; }

    /// <summary>
    /// Reference to the original object being corrected.
    /// </summary>
    public required Guid OriginalObjectId { get; init; }

    /// <summary>
    /// Version of the original object being corrected.
    /// If null, corrects the latest version.
    /// </summary>
    public int? OriginalVersion { get; init; }

    /// <summary>
    /// Maximum allowed length for CorrectionReason field.
    /// </summary>
    public const int MaxCorrectionReasonLength = 4096;

    /// <summary>
    /// Validates the correction context.
    /// </summary>
    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(base.Validate());

        if (string.IsNullOrWhiteSpace(CorrectionReason))
        {
            errors.Add("CorrectionReason is required and cannot be empty or whitespace.");
        }
        else if (CorrectionReason.Length > MaxCorrectionReasonLength)
        {
            errors.Add($"CorrectionReason cannot exceed {MaxCorrectionReasonLength} characters.");
        }

        if (OriginalObjectId == Guid.Empty)
        {
            errors.Add("OriginalObjectId is required.");
        }

        return errors;
    }

    /// <summary>
    /// Creates a correction record for storage.
    /// </summary>
    public CorrectionContextRecord ToCorrectionRecord()
    {
        return new CorrectionContextRecord
        {
            Author = Author,
            Comment = Comment,
            SessionId = SessionId,
            SourceSystem = SourceSystem,
            ClientIp = ClientIp,
            UserAgent = UserAgent,
            CorrelationId = CorrelationId,
            Timestamp = Timestamp,
            Metadata = Metadata is not null ? new Dictionary<string, object>(Metadata) : null,
            CorrectionReason = CorrectionReason,
            OriginalObjectId = OriginalObjectId,
            OriginalVersion = OriginalVersion
        };
    }
}

/// <summary>
/// Immutable record of write context stored in manifests.
/// </summary>
public class WriteContextRecord
{
    /// <summary>Author/principal who performed the write.</summary>
    public required string Author { get; init; }

    /// <summary>Mandatory comment explaining the write.</summary>
    public required string Comment { get; init; }

    /// <summary>Session identifier for correlation.</summary>
    public string? SessionId { get; init; }

    /// <summary>Source system or application.</summary>
    public string? SourceSystem { get; init; }

    /// <summary>Client IP address.</summary>
    public string? ClientIp { get; init; }

    /// <summary>User agent string.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Correlation ID for distributed tracing.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>UTC timestamp of the write.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Computes a hash of this record for integrity verification.
    /// </summary>
    public string ComputeHash()
    {
        var data = $"{Author}|{Comment}|{Timestamp:O}|{SessionId}|{SourceSystem}|{ClientIp}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// Immutable record of correction context stored in manifests.
/// </summary>
public class CorrectionContextRecord : WriteContextRecord
{
    /// <summary>Reason for the correction.</summary>
    public required string CorrectionReason { get; init; }

    /// <summary>Original object that was corrected.</summary>
    public required Guid OriginalObjectId { get; init; }

    /// <summary>Version of the original object that was corrected.</summary>
    public int? OriginalVersion { get; init; }
}

/// <summary>
/// Builder for creating WriteContext instances with fluent API.
/// </summary>
public class WriteContextBuilder
{
    private string? _author;
    private string? _comment;
    private string? _sessionId;
    private string? _sourceSystem;
    private string? _clientIp;
    private string? _userAgent;
    private string? _correlationId;
    private Dictionary<string, object>? _metadata;

    /// <summary>
    /// Sets the author.
    /// </summary>
    public WriteContextBuilder WithAuthor(string author)
    {
        _author = author;
        return this;
    }

    /// <summary>
    /// Sets the comment.
    /// </summary>
    public WriteContextBuilder WithComment(string comment)
    {
        _comment = comment;
        return this;
    }

    /// <summary>
    /// Sets the session ID.
    /// </summary>
    public WriteContextBuilder WithSessionId(string sessionId)
    {
        _sessionId = sessionId;
        return this;
    }

    /// <summary>
    /// Sets the source system.
    /// </summary>
    public WriteContextBuilder WithSourceSystem(string sourceSystem)
    {
        _sourceSystem = sourceSystem;
        return this;
    }

    /// <summary>
    /// Sets the client IP.
    /// </summary>
    public WriteContextBuilder WithClientIp(string clientIp)
    {
        _clientIp = clientIp;
        return this;
    }

    /// <summary>
    /// Sets the user agent.
    /// </summary>
    public WriteContextBuilder WithUserAgent(string userAgent)
    {
        _userAgent = userAgent;
        return this;
    }

    /// <summary>
    /// Sets the correlation ID.
    /// </summary>
    public WriteContextBuilder WithCorrelationId(string correlationId)
    {
        _correlationId = correlationId;
        return this;
    }

    /// <summary>
    /// Adds metadata.
    /// </summary>
    public WriteContextBuilder WithMetadata(string key, object value)
    {
        _metadata ??= new Dictionary<string, object>();
        _metadata[key] = value;
        return this;
    }

    /// <summary>
    /// Builds the WriteContext.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when required fields are missing.</exception>
    public WriteContext Build()
    {
        if (string.IsNullOrWhiteSpace(_author))
        {
            throw new InvalidOperationException("Author is required.");
        }

        if (string.IsNullOrWhiteSpace(_comment))
        {
            throw new InvalidOperationException("Comment is required.");
        }

        return new WriteContext
        {
            Author = _author,
            Comment = _comment,
            SessionId = _sessionId,
            SourceSystem = _sourceSystem,
            ClientIp = _clientIp,
            UserAgent = _userAgent,
            CorrelationId = _correlationId,
            Metadata = _metadata
        };
    }
}
