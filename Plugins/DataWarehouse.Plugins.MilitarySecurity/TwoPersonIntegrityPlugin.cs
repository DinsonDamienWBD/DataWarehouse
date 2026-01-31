using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Plugins.MilitarySecurity;

/// <summary>
/// Two-Person Integrity (TPI) enforcement plugin.
/// Requires two authorized individuals to approve sensitive operations.
/// Prevents single-person compromise of critical security operations.
/// </summary>
public class TwoPersonIntegrityPlugin : TwoPersonIntegrityPluginBase
{
    private readonly Dictionary<string, OperationRecord> _operationRecords = new();
    private readonly Dictionary<string, AuthorizerInfo> _authorizedPersonnel = new();
    private readonly List<TpiAuditEntry> _auditLog = new();
    private TimeSpan _operationExpiration = TimeSpan.FromHours(24);

    /// <inheritdoc />
    public override string Id => "datawarehouse.milsec.two-person-integrity";

    /// <inheritdoc />
    public override string Name => "Two-Person Integrity";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <summary>
    /// Registers an authorized person who can initiate or authorize TPI operations.
    /// </summary>
    /// <param name="personId">Unique person identifier.</param>
    /// <param name="name">Person's name.</param>
    /// <param name="publicKey">Public key for signature verification.</param>
    /// <param name="authorizedOperations">Operation types this person can authorize.</param>
    public void RegisterAuthorizedPerson(
        string personId,
        string name,
        byte[] publicKey,
        string[] authorizedOperations)
    {
        _authorizedPersonnel[personId] = new AuthorizerInfo
        {
            PersonId = personId,
            Name = name,
            PublicKey = publicKey,
            AuthorizedOperations = authorizedOperations.ToHashSet(),
            RegisteredAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Removes an authorized person from the TPI system.
    /// </summary>
    /// <param name="personId">Person identifier to remove.</param>
    /// <returns>True if person was removed.</returns>
    public bool RemoveAuthorizedPerson(string personId)
    {
        return _authorizedPersonnel.Remove(personId);
    }

    /// <summary>
    /// Sets the operation expiration time.
    /// </summary>
    /// <param name="expiration">Time before pending operations expire.</param>
    public void SetOperationExpiration(TimeSpan expiration)
    {
        _operationExpiration = expiration;
    }

    /// <inheritdoc />
    protected override Task StoreOperationAsync(string operationId, string operationType, string initiatorId)
    {
        // Validate initiator is authorized
        if (!_authorizedPersonnel.TryGetValue(initiatorId, out var initiator))
        {
            throw new UnauthorizedAccessException($"Person {initiatorId} is not authorized for TPI operations");
        }

        if (!initiator.AuthorizedOperations.Contains(operationType) &&
            !initiator.AuthorizedOperations.Contains("*"))
        {
            throw new UnauthorizedAccessException(
                $"Person {initiatorId} is not authorized for operation type {operationType}");
        }

        // Store operation record
        _operationRecords[operationId] = new OperationRecord
        {
            OperationId = operationId,
            OperationType = operationType,
            InitiatorId = initiatorId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(_operationExpiration)
        };

        // Audit log
        _auditLog.Add(new TpiAuditEntry
        {
            EntryId = Guid.NewGuid().ToString(),
            OperationId = operationId,
            Action = "INITIATED",
            PersonId = initiatorId,
            Timestamp = DateTimeOffset.UtcNow,
            Details = $"Operation type: {operationType}"
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task<bool> ValidateAuthorizerAsync(string operationId, string authorizerId, byte[] proof)
    {
        // Check operation exists
        if (!_operationRecords.TryGetValue(operationId, out var record))
        {
            return Task.FromResult(false);
        }

        // Check operation hasn't expired
        if (DateTimeOffset.UtcNow > record.ExpiresAt)
        {
            _auditLog.Add(new TpiAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                OperationId = operationId,
                Action = "EXPIRED",
                PersonId = authorizerId,
                Timestamp = DateTimeOffset.UtcNow,
                Details = "Operation expired before authorization"
            });
            return Task.FromResult(false);
        }

        // Check authorizer is registered
        if (!_authorizedPersonnel.TryGetValue(authorizerId, out var authorizer))
        {
            _auditLog.Add(new TpiAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                OperationId = operationId,
                Action = "UNAUTHORIZED",
                PersonId = authorizerId,
                Timestamp = DateTimeOffset.UtcNow,
                Details = "Authorizer not registered"
            });
            return Task.FromResult(false);
        }

        // Check authorizer is not the initiator
        if (authorizerId == record.InitiatorId)
        {
            _auditLog.Add(new TpiAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                OperationId = operationId,
                Action = "SELF_AUTH_REJECTED",
                PersonId = authorizerId,
                Timestamp = DateTimeOffset.UtcNow,
                Details = "Self-authorization attempted and rejected"
            });
            return Task.FromResult(false);
        }

        // Check authorizer is authorized for this operation type
        if (!authorizer.AuthorizedOperations.Contains(record.OperationType) &&
            !authorizer.AuthorizedOperations.Contains("*"))
        {
            _auditLog.Add(new TpiAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                OperationId = operationId,
                Action = "UNAUTHORIZED_OP_TYPE",
                PersonId = authorizerId,
                Timestamp = DateTimeOffset.UtcNow,
                Details = $"Not authorized for operation type: {record.OperationType}"
            });
            return Task.FromResult(false);
        }

        // Verify cryptographic proof
        if (authorizer.PublicKey.Length > 0 && proof.Length > 0)
        {
            try
            {
                // Verify signature over operation ID
                var dataToSign = System.Text.Encoding.UTF8.GetBytes(operationId);
                using var rsa = RSA.Create();
                rsa.ImportRSAPublicKey(authorizer.PublicKey, out _);

                var isValid = rsa.VerifyData(dataToSign, proof, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (!isValid)
                {
                    _auditLog.Add(new TpiAuditEntry
                    {
                        EntryId = Guid.NewGuid().ToString(),
                        OperationId = operationId,
                        Action = "INVALID_PROOF",
                        PersonId = authorizerId,
                        Timestamp = DateTimeOffset.UtcNow,
                        Details = "Cryptographic proof verification failed"
                    });
                    return Task.FromResult(false);
                }
            }
            catch
            {
                // Signature verification failed
                return Task.FromResult(false);
            }
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    protected override Task RecordAuthorizationAsync(string operationId, string authorizerId)
    {
        if (_operationRecords.TryGetValue(operationId, out var record))
        {
            record.AuthorizerId = authorizerId;
            record.AuthorizedAt = DateTimeOffset.UtcNow;
        }

        _auditLog.Add(new TpiAuditEntry
        {
            EntryId = Guid.NewGuid().ToString(),
            OperationId = operationId,
            Action = "AUTHORIZED",
            PersonId = authorizerId,
            Timestamp = DateTimeOffset.UtcNow,
            Details = "Second-person authorization completed"
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets all pending operations awaiting authorization.
    /// </summary>
    public IReadOnlyList<AuthorizationStatus> GetPendingOperations()
    {
        var pending = new List<AuthorizationStatus>();

        foreach (var (id, record) in _operationRecords)
        {
            if (record.AuthorizerId == null && DateTimeOffset.UtcNow < record.ExpiresAt)
            {
                if (_pendingOperations.TryGetValue(id, out var status))
                {
                    pending.Add(status);
                }
            }
        }

        return pending;
    }

    /// <summary>
    /// Gets the complete TPI audit log.
    /// </summary>
    public IReadOnlyList<TpiAuditEntry> GetAuditLog() => _auditLog;

    /// <summary>
    /// Cancels a pending operation.
    /// </summary>
    /// <param name="operationId">Operation ID to cancel.</param>
    /// <param name="cancelledBy">Person cancelling the operation.</param>
    /// <returns>True if operation was cancelled.</returns>
    public bool CancelOperation(string operationId, string cancelledBy)
    {
        if (!_operationRecords.Remove(operationId))
            return false;

        _pendingOperations.Remove(operationId);

        _auditLog.Add(new TpiAuditEntry
        {
            EntryId = Guid.NewGuid().ToString(),
            OperationId = operationId,
            Action = "CANCELLED",
            PersonId = cancelledBy,
            Timestamp = DateTimeOffset.UtcNow,
            Details = "Operation cancelled before completion"
        });

        return true;
    }

    private class OperationRecord
    {
        public string OperationId { get; set; } = "";
        public string OperationType { get; set; } = "";
        public string InitiatorId { get; set; } = "";
        public string? AuthorizerId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset? AuthorizedAt { get; set; }
    }

    private class AuthorizerInfo
    {
        public string PersonId { get; set; } = "";
        public string Name { get; set; } = "";
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
        public HashSet<string> AuthorizedOperations { get; set; } = new();
        public DateTimeOffset RegisteredAt { get; set; }
    }
}

/// <summary>
/// Audit entry for TPI operations.
/// </summary>
public class TpiAuditEntry
{
    /// <summary>
    /// Unique entry identifier.
    /// </summary>
    public string EntryId { get; set; } = "";

    /// <summary>
    /// Associated operation ID.
    /// </summary>
    public string OperationId { get; set; } = "";

    /// <summary>
    /// Action performed (INITIATED, AUTHORIZED, REJECTED, EXECUTED, CANCELLED, EXPIRED).
    /// </summary>
    public string Action { get; set; } = "";

    /// <summary>
    /// Person who performed the action.
    /// </summary>
    public string PersonId { get; set; } = "";

    /// <summary>
    /// Timestamp of the action.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Additional details about the action.
    /// </summary>
    public string Details { get; set; } = "";
}
