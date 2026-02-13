using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Infrastructure
{
    /// <summary>
    /// Exception for security-related operation failures (authentication, authorization).
    /// </summary>
    public sealed class SecurityOperationException : DataWarehouseException
    {
        /// <summary>The principal ID involved.</summary>
        public string? PrincipalId { get; init; }

        /// <summary>The resource ID involved.</summary>
        public string? ResourceId { get; init; }

        /// <summary>The required permission.</summary>
        public string? RequiredPermission { get; init; }

        public SecurityOperationException(ErrorCode errorCode, string message, string? correlationId = null)
            : base(errorCode, message, correlationId)
        {
        }

        public static SecurityOperationException AccessDenied(string principalId, string resourceId, string permission) =>
            new(ErrorCode.Forbidden, $"Access denied: '{principalId}' lacks '{permission}' permission on '{resourceId}'")
            {
                PrincipalId = principalId,
                ResourceId = resourceId,
                RequiredPermission = permission
            };

        public static SecurityOperationException AuthenticationFailed(string reason) =>
            new(ErrorCode.Unauthorized, $"Authentication failed: {reason}");

        public static SecurityOperationException TokenExpired() =>
            new(ErrorCode.Unauthorized, "Security token has expired");

        public static SecurityOperationException PathTraversalAttempt(string path) =>
            new(ErrorCode.Forbidden, $"Path traversal attack detected: {path}");
    }

    /// <summary>
    /// Exception thrown when corruption is detected and fail-closed mode seals the affected block.
    /// Prevents any reads or writes on potentially compromised data until manual intervention.
    /// </summary>
    public sealed class FailClosedCorruptionException : DataWarehouseException
    {
        /// <summary>The block ID that has been sealed due to corruption.</summary>
        public Guid BlockId { get; }

        /// <summary>The version of the object that was affected.</summary>
        public int Version { get; }

        /// <summary>The expected hash from the manifest.</summary>
        public string ExpectedHash { get; }

        /// <summary>The actual hash computed from the corrupted data.</summary>
        public string ActualHash { get; }

        /// <summary>The storage instance where corruption was detected.</summary>
        public string AffectedInstance { get; }

        /// <summary>List of affected shard indices, if applicable.</summary>
        public IReadOnlyList<int>? AffectedShards { get; }

        /// <summary>Timestamp when the corruption was detected and block was sealed.</summary>
        public DateTimeOffset SealedAt { get; }

        /// <summary>Incident ID for tracking and audit purposes.</summary>
        public Guid IncidentId { get; }

        public FailClosedCorruptionException(
            Guid blockId,
            int version,
            string expectedHash,
            string actualHash,
            string affectedInstance,
            IReadOnlyList<int>? affectedShards = null,
            Guid? incidentId = null,
            string? correlationId = null)
            : base(
                ErrorCode.DataCorruption,
                $"FAIL CLOSED: Corruption detected in block {blockId} version {version}. " +
                $"Block has been sealed - no reads or writes permitted. " +
                $"Expected hash: {expectedHash}, Actual hash: {actualHash}. " +
                $"Manual intervention required. Incident ID: {incidentId ?? Guid.NewGuid()}",
                correlationId)
        {
            BlockId = blockId;
            Version = version;
            ExpectedHash = expectedHash;
            ActualHash = actualHash;
            AffectedInstance = affectedInstance;
            AffectedShards = affectedShards;
            SealedAt = DateTimeOffset.UtcNow;
            IncidentId = incidentId ?? Guid.NewGuid();
        }

        public static FailClosedCorruptionException Create(
            Guid blockId,
            int version,
            string expectedHash,
            string actualHash,
            string affectedInstance,
            IReadOnlyList<int>? affectedShards = null)
        {
            return new FailClosedCorruptionException(
                blockId,
                version,
                expectedHash,
                actualHash,
                affectedInstance,
                affectedShards);
        }
    }
}
