using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Privacy
{
    /// <summary>
    /// T124.2: Data Pseudonymization Strategy
    /// Reversible transformation of personal data using pseudonyms while maintaining data utility.
    /// Unlike anonymization, pseudonymized data can be re-identified with the key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pseudonymization Techniques:
    /// - Token Replacement: Replace identifiers with random tokens
    /// - Encrypted Tokens: Use encryption for reversible pseudonymization
    /// - Hash-Based: Use keyed hashing (HMAC) for consistent pseudonyms
    /// - Format-Preserving: Maintain original data format in pseudonym
    /// - Structured Tokens: Generate tokens with embedded metadata
    /// </para>
    /// <para>
    /// Key Management:
    /// - Secure key storage with rotation support
    /// - Key separation by data domain
    /// - Multi-tenant key isolation
    /// - Key escrow for authorized re-identification
    /// </para>
    /// </remarks>
    public sealed class DataPseudonymizationStrategy : ComplianceStrategyBase
    {
        private readonly ConcurrentDictionary<string, PseudonymDomain> _domains = new();
        private readonly ConcurrentDictionary<string, PseudonymMapping> _mappings = new();
        private readonly ConcurrentDictionary<string, byte[]> _domainKeys = new();
        private readonly ConcurrentBag<PseudonymizationAuditEntry> _auditLog = new();
        private readonly SemaphoreSlim _keyLock = new(1, 1);

        private byte[]? _masterKey;
        private int _tokenLength = 16;
        private bool _enableReversal = true;

        /// <inheritdoc/>
        public override string StrategyId => "data-pseudonymization";

        /// <inheritdoc/>
        public override string StrategyName => "Data Pseudonymization";

        /// <inheritdoc/>
        public override string Framework => "DataPrivacy";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("TokenLength", out var lenObj) && lenObj is int len)
                _tokenLength = Math.Max(8, Math.Min(64, len));

            if (configuration.TryGetValue("EnableReversal", out var revObj) && revObj is bool rev)
                _enableReversal = rev;

            // Generate or load master key
            _masterKey = new byte[32];
            RandomNumberGenerator.Fill(_masterKey);

            InitializeDefaultDomains();
            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Registers a pseudonymization domain.
        /// </summary>
        public void RegisterDomain(PseudonymDomain domain)
        {
            ArgumentNullException.ThrowIfNull(domain);
            _domains[domain.DomainId] = domain;

            // Generate domain-specific key
            var domainKey = new byte[32];
            RandomNumberGenerator.Fill(domainKey);
            _domainKeys[domain.DomainId] = domainKey;
        }

        /// <summary>
        /// Pseudonymizes a value within a domain.
        /// </summary>
        public PseudonymizeResult Pseudonymize(string value, string domainId, string? fieldType = null)
        {
            if (!_domains.TryGetValue(domainId, out var domain))
            {
                return new PseudonymizeResult
                {
                    Success = false,
                    ErrorMessage = $"Domain not found: {domainId}"
                };
            }

            // Check for existing mapping
            var mappingKey = $"{domainId}:{value}";
            if (_mappings.TryGetValue(mappingKey, out var existing))
            {
                return new PseudonymizeResult
                {
                    Success = true,
                    OriginalValue = value,
                    Pseudonym = existing.Pseudonym,
                    DomainId = domainId,
                    IsNewMapping = false
                };
            }

            // Generate pseudonym based on technique
            var pseudonym = domain.Technique switch
            {
                PseudonymizationTechnique.TokenReplacement => GenerateToken(domain),
                PseudonymizationTechnique.EncryptedToken => GenerateEncryptedToken(value, domainId),
                PseudonymizationTechnique.HashBased => GenerateHashBasedPseudonym(value, domainId),
                PseudonymizationTechnique.FormatPreserving => GenerateFormatPreservingPseudonym(value, fieldType ?? "text"),
                PseudonymizationTechnique.StructuredToken => GenerateStructuredToken(value, domain, fieldType),
                _ => GenerateToken(domain)
            };

            // Store mapping for reversibility
            if (_enableReversal)
            {
                var mapping = new PseudonymMapping
                {
                    MappingId = Guid.NewGuid().ToString(),
                    DomainId = domainId,
                    OriginalValue = value,
                    Pseudonym = pseudonym,
                    CreatedAt = DateTime.UtcNow,
                    FieldType = fieldType
                };
                _mappings[mappingKey] = mapping;
            }

            // Audit log
            _auditLog.Add(new PseudonymizationAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                DomainId = domainId,
                Action = PseudonymAction.Pseudonymize,
                FieldType = fieldType,
                Timestamp = DateTime.UtcNow
            });

            return new PseudonymizeResult
            {
                Success = true,
                OriginalValue = value,
                Pseudonym = pseudonym,
                DomainId = domainId,
                IsNewMapping = true
            };
        }

        /// <summary>
        /// Pseudonymizes multiple values in batch.
        /// </summary>
        public async Task<BatchPseudonymizeResult> PseudonymizeBatchAsync(
            IEnumerable<PseudonymizeRequest> requests,
            CancellationToken ct = default)
        {
            var results = new List<PseudonymizeResult>();

            foreach (var request in requests)
            {
                ct.ThrowIfCancellationRequested();
                var result = Pseudonymize(request.Value, request.DomainId, request.FieldType);
                results.Add(result);
            }

            return new BatchPseudonymizeResult
            {
                Success = results.All(r => r.Success),
                Results = results,
                TotalProcessed = results.Count,
                NewMappingsCreated = results.Count(r => r.IsNewMapping)
            };
        }

        /// <summary>
        /// Reverses a pseudonym back to the original value (requires authorization).
        /// </summary>
        public ReversePseudonymResult ReversePseudonym(
            string pseudonym,
            string domainId,
            ReversalAuthorization authorization)
        {
            if (!_enableReversal)
            {
                return new ReversePseudonymResult
                {
                    Success = false,
                    ErrorMessage = "Reversal is disabled for this configuration"
                };
            }

            // Validate authorization
            if (!ValidateAuthorization(authorization, domainId))
            {
                _auditLog.Add(new PseudonymizationAuditEntry
                {
                    EntryId = Guid.NewGuid().ToString(),
                    DomainId = domainId,
                    Action = PseudonymAction.ReversalDenied,
                    Timestamp = DateTime.UtcNow,
                    ActorId = authorization.RequesterId
                });

                return new ReversePseudonymResult
                {
                    Success = false,
                    ErrorMessage = "Authorization failed"
                };
            }

            // Find mapping
            var mapping = _mappings.Values.FirstOrDefault(m =>
                m.DomainId == domainId && m.Pseudonym == pseudonym);

            if (mapping == null)
            {
                // Try to decrypt if using encrypted tokens
                if (_domains.TryGetValue(domainId, out var domain) &&
                    domain.Technique == PseudonymizationTechnique.EncryptedToken)
                {
                    var decrypted = DecryptToken(pseudonym, domainId);
                    if (decrypted != null)
                    {
                        _auditLog.Add(new PseudonymizationAuditEntry
                        {
                            EntryId = Guid.NewGuid().ToString(),
                            DomainId = domainId,
                            Action = PseudonymAction.Reverse,
                            Timestamp = DateTime.UtcNow,
                            ActorId = authorization.RequesterId,
                            Reason = authorization.Reason
                        });

                        return new ReversePseudonymResult
                        {
                            Success = true,
                            Pseudonym = pseudonym,
                            OriginalValue = decrypted,
                            DomainId = domainId
                        };
                    }
                }

                return new ReversePseudonymResult
                {
                    Success = false,
                    ErrorMessage = "Mapping not found"
                };
            }

            // Audit log
            _auditLog.Add(new PseudonymizationAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                DomainId = domainId,
                Action = PseudonymAction.Reverse,
                Timestamp = DateTime.UtcNow,
                ActorId = authorization.RequesterId,
                Reason = authorization.Reason
            });

            return new ReversePseudonymResult
            {
                Success = true,
                Pseudonym = pseudonym,
                OriginalValue = mapping.OriginalValue,
                DomainId = domainId
            };
        }

        /// <summary>
        /// Deletes all mappings for a specific original value (for right to erasure).
        /// </summary>
        public DeleteMappingsResult DeleteMappings(string originalValue, string? domainId = null)
        {
            var deleted = 0;
            var keysToRemove = _mappings.Keys
                .Where(k => {
                    if (!_mappings.TryGetValue(k, out var m)) return false;
                    if (m.OriginalValue != originalValue) return false;
                    if (domainId != null && m.DomainId != domainId) return false;
                    return true;
                })
                .ToList();

            foreach (var key in keysToRemove)
            {
                if (_mappings.TryRemove(key, out _))
                    deleted++;
            }

            _auditLog.Add(new PseudonymizationAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                DomainId = domainId ?? "ALL",
                Action = PseudonymAction.DeleteMappings,
                Timestamp = DateTime.UtcNow,
                Details = $"Deleted {deleted} mappings"
            });

            return new DeleteMappingsResult
            {
                Success = true,
                MappingsDeleted = deleted
            };
        }

        /// <summary>
        /// Rotates the key for a domain, re-pseudonymizing all existing mappings.
        /// </summary>
        public async Task<KeyRotationResult> RotateKeyAsync(string domainId, CancellationToken ct = default)
        {
            await _keyLock.WaitAsync(ct);
            try
            {
                if (!_domains.TryGetValue(domainId, out var domain))
                {
                    return new KeyRotationResult
                    {
                        Success = false,
                        ErrorMessage = $"Domain not found: {domainId}"
                    };
                }

                // Generate new key
                var newKey = new byte[32];
                RandomNumberGenerator.Fill(newKey);

                // Re-pseudonymize existing mappings
                var affected = _mappings.Values.Where(m => m.DomainId == domainId).ToList();
                foreach (var mapping in affected)
                {
                    ct.ThrowIfCancellationRequested();

                    var oldKey = $"{domainId}:{mapping.OriginalValue}";
                    _mappings.TryRemove(oldKey, out _);

                    // Update domain key first
                    _domainKeys[domainId] = newKey;

                    // Re-generate pseudonym
                    var newPseudonym = domain.Technique switch
                    {
                        PseudonymizationTechnique.EncryptedToken => GenerateEncryptedToken(mapping.OriginalValue, domainId),
                        PseudonymizationTechnique.HashBased => GenerateHashBasedPseudonym(mapping.OriginalValue, domainId),
                        _ => GenerateToken(domain)
                    };

                    var newMapping = mapping with { Pseudonym = newPseudonym, CreatedAt = DateTime.UtcNow };
                    _mappings[oldKey] = newMapping;
                }

                _auditLog.Add(new PseudonymizationAuditEntry
                {
                    EntryId = Guid.NewGuid().ToString(),
                    DomainId = domainId,
                    Action = PseudonymAction.KeyRotation,
                    Timestamp = DateTime.UtcNow,
                    Details = $"Rotated key, affected {affected.Count} mappings"
                });

                return new KeyRotationResult
                {
                    Success = true,
                    DomainId = domainId,
                    MappingsUpdated = affected.Count,
                    RotatedAt = DateTime.UtcNow
                };
            }
            finally
            {
                _keyLock.Release();
            }
        }

        /// <summary>
        /// Gets all registered domains.
        /// </summary>
        public IReadOnlyList<PseudonymDomain> GetDomains()
        {
            return _domains.Values.ToList();
        }

        /// <summary>
        /// Gets audit log entries.
        /// </summary>
        public IReadOnlyList<PseudonymizationAuditEntry> GetAuditLog(string? domainId = null, int count = 100)
        {
            var query = _auditLog.AsEnumerable();
            if (!string.IsNullOrEmpty(domainId))
                query = query.Where(e => e.DomainId == domainId);

            return query.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if pseudonymization is applied where needed
            if (context.Attributes.TryGetValue("RequiresPseudonymization", out var reqObj) && reqObj is true)
            {
                if (!context.Attributes.TryGetValue("PseudonymizationApplied", out var appObj) || appObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PSEUDO-001",
                        Description = "Required pseudonymization not applied",
                        Severity = ViolationSeverity.High,
                        Remediation = "Apply pseudonymization before processing or sharing data",
                        RegulatoryReference = "GDPR Article 4(5)"
                    });
                }
            }

            // Check key rotation
            if (context.Attributes.TryGetValue("PseudonymDomainId", out var domainObj) && domainObj is string domainId)
            {
                if (_domains.TryGetValue(domainId, out var domain))
                {
                    if (domain.LastKeyRotation.HasValue &&
                        DateTime.UtcNow - domain.LastKeyRotation.Value > TimeSpan.FromDays(90))
                    {
                        recommendations.Add($"Domain '{domainId}' key has not been rotated in over 90 days");
                    }
                }
            }

            // Check reversal authorization
            if (context.OperationType == "pseudonym-reversal")
            {
                if (!context.Attributes.TryGetValue("ReversalAuthorized", out var authObj) || authObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PSEUDO-002",
                        Description = "Pseudonym reversal without proper authorization",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain proper authorization before reversing pseudonyms"
                    });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["DomainCount"] = _domains.Count,
                    ["ActiveMappings"] = _mappings.Count,
                    ["ReversalEnabled"] = _enableReversal
                }
            });
        }

        private string GenerateToken(PseudonymDomain domain)
        {
            var bytes = new byte[_tokenLength / 2];
            RandomNumberGenerator.Fill(bytes);
            var token = Convert.ToHexString(bytes).ToLowerInvariant();

            return domain.TokenPrefix != null
                ? $"{domain.TokenPrefix}_{token}"
                : token;
        }

        private string GenerateEncryptedToken(string value, string domainId)
        {
            if (!_domainKeys.TryGetValue(domainId, out var key))
                return GenerateToken(_domains[domainId]);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(value);
            var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            var result = new byte[aes.IV.Length + encrypted.Length];
            Array.Copy(aes.IV, 0, result, 0, aes.IV.Length);
            Array.Copy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

            return Convert.ToBase64String(result);
        }

        private string? DecryptToken(string token, string domainId)
        {
            try
            {
                if (!_domainKeys.TryGetValue(domainId, out var key))
                    return null;

                var data = Convert.FromBase64String(token);
                if (data.Length < 16)
                    return null;

                using var aes = Aes.Create();
                aes.Key = key;

                var iv = new byte[16];
                Array.Copy(data, 0, iv, 0, 16);
                aes.IV = iv;

                using var decryptor = aes.CreateDecryptor();
                var decrypted = decryptor.TransformFinalBlock(data, 16, data.Length - 16);

                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return null;
            }
        }

        private string GenerateHashBasedPseudonym(string value, string domainId)
        {
            if (!_domainKeys.TryGetValue(domainId, out var key))
                return GenerateToken(_domains[domainId]);

            using var hmac = new HMACSHA256(key);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash)[.._tokenLength].ToLowerInvariant();
        }

        private string GenerateFormatPreservingPseudonym(string value, string fieldType)
        {
            return fieldType.ToLowerInvariant() switch
            {
                "email" => GenerateFormatPreservingEmail(value),
                "phone" => GenerateFormatPreservingPhone(value),
                "ssn" => GenerateFormatPreservingSsn(value),
                "creditcard" => GenerateFormatPreservingCreditCard(value),
                _ => GenerateFormatPreservingText(value)
            };
        }

        private string GenerateFormatPreservingEmail(string value)
        {
            var parts = value.Split('@');
            if (parts.Length != 2)
                return $"pseudo{Random.Shared.Next(1000, 9999)}@example.com";

            var localPart = GenerateRandomString(parts[0].Length);
            return $"{localPart}@{parts[1]}";
        }

        private string GenerateFormatPreservingPhone(string value)
        {
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length == 10)
                return $"555{Random.Shared.Next(100, 999)}{Random.Shared.Next(1000, 9999)}";
            return $"555-{Random.Shared.Next(100, 999)}-{Random.Shared.Next(1000, 9999)}";
        }

        private string GenerateFormatPreservingSsn(string value)
        {
            return $"{Random.Shared.Next(100, 999)}-{Random.Shared.Next(10, 99)}-{Random.Shared.Next(1000, 9999)}";
        }

        private string GenerateFormatPreservingCreditCard(string value)
        {
            // Preserve format but replace with invalid card number
            return $"4000-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(1000, 9999)}";
        }

        private string GenerateFormatPreservingText(string value)
        {
            return GenerateRandomString(value.Length);
        }

        private string GenerateStructuredToken(string value, PseudonymDomain domain, string? fieldType)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var typeCode = (fieldType?.ToUpperInvariant()[..Math.Min(3, fieldType.Length)] ?? "GEN");
            var randomPart = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

            return $"{domain.TokenPrefix ?? "PSU"}_{typeCode}_{timestamp:X}_{randomPart}";
        }

        private static string GenerateRandomString(int length)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            return new string(Enumerable.Range(0, length)
                .Select(_ => chars[Random.Shared.Next(chars.Length)])
                .ToArray());
        }

        private bool ValidateAuthorization(ReversalAuthorization auth, string domainId)
        {
            if (!_domains.TryGetValue(domainId, out var domain))
                return false;

            // Check authorization level
            if (domain.RequiredAuthLevel > auth.AuthLevel)
                return false;

            // Check if requester has required role
            if (domain.AuthorizedRoles != null && domain.AuthorizedRoles.Length > 0)
            {
                if (auth.RequesterRoles == null ||
                    !auth.RequesterRoles.Intersect(domain.AuthorizedRoles).Any())
                    return false;
            }

            // Check reason requirement
            if (domain.RequireReason && string.IsNullOrEmpty(auth.Reason))
                return false;

            return true;
        }

        private void InitializeDefaultDomains()
        {
            RegisterDomain(new PseudonymDomain
            {
                DomainId = "user-identity",
                Name = "User Identity",
                Description = "Pseudonymization domain for user identifiers",
                Technique = PseudonymizationTechnique.EncryptedToken,
                TokenPrefix = "USR",
                RequiredAuthLevel = 2,
                AuthorizedRoles = new[] { "DataProtectionOfficer", "SecurityAdmin" },
                RequireReason = true
            });

            RegisterDomain(new PseudonymDomain
            {
                DomainId = "customer-data",
                Name = "Customer Data",
                Description = "Pseudonymization domain for customer information",
                Technique = PseudonymizationTechnique.HashBased,
                TokenPrefix = "CUS",
                RequiredAuthLevel = 1,
                AuthorizedRoles = new[] { "DataProtectionOfficer", "CustomerService" }
            });

            RegisterDomain(new PseudonymDomain
            {
                DomainId = "research-subjects",
                Name = "Research Subjects",
                Description = "Pseudonymization domain for research participant data",
                Technique = PseudonymizationTechnique.TokenReplacement,
                TokenPrefix = "RES",
                RequiredAuthLevel = 3,
                RequireReason = true
            });
        }
    }

    #region Types

    /// <summary>
    /// Pseudonymization technique types.
    /// </summary>
    public enum PseudonymizationTechnique
    {
        TokenReplacement,
        EncryptedToken,
        HashBased,
        FormatPreserving,
        StructuredToken
    }

    /// <summary>
    /// Pseudonymization domain configuration.
    /// </summary>
    public sealed record PseudonymDomain
    {
        public required string DomainId { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required PseudonymizationTechnique Technique { get; init; }
        public string? TokenPrefix { get; init; }
        public int RequiredAuthLevel { get; init; }
        public string[]? AuthorizedRoles { get; init; }
        public bool RequireReason { get; init; }
        public DateTime? LastKeyRotation { get; init; }
    }

    /// <summary>
    /// Pseudonym mapping record.
    /// </summary>
    public sealed record PseudonymMapping
    {
        public required string MappingId { get; init; }
        public required string DomainId { get; init; }
        public required string OriginalValue { get; init; }
        public required string Pseudonym { get; init; }
        public required DateTime CreatedAt { get; init; }
        public string? FieldType { get; init; }
    }

    /// <summary>
    /// Pseudonymize request for batch processing.
    /// </summary>
    public sealed record PseudonymizeRequest
    {
        public required string Value { get; init; }
        public required string DomainId { get; init; }
        public string? FieldType { get; init; }
    }

    /// <summary>
    /// Pseudonymize result.
    /// </summary>
    public sealed record PseudonymizeResult
    {
        public required bool Success { get; init; }
        public string? OriginalValue { get; init; }
        public string? Pseudonym { get; init; }
        public string? DomainId { get; init; }
        public string? ErrorMessage { get; init; }
        public bool IsNewMapping { get; init; }
    }

    /// <summary>
    /// Batch pseudonymize result.
    /// </summary>
    public sealed record BatchPseudonymizeResult
    {
        public required bool Success { get; init; }
        public required IReadOnlyList<PseudonymizeResult> Results { get; init; }
        public int TotalProcessed { get; init; }
        public int NewMappingsCreated { get; init; }
    }

    /// <summary>
    /// Authorization for pseudonym reversal.
    /// </summary>
    public sealed record ReversalAuthorization
    {
        public required string RequesterId { get; init; }
        public required int AuthLevel { get; init; }
        public string[]? RequesterRoles { get; init; }
        public string? Reason { get; init; }
        public string? ApprovalId { get; init; }
    }

    /// <summary>
    /// Reverse pseudonym result.
    /// </summary>
    public sealed record ReversePseudonymResult
    {
        public required bool Success { get; init; }
        public string? Pseudonym { get; init; }
        public string? OriginalValue { get; init; }
        public string? DomainId { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Delete mappings result.
    /// </summary>
    public sealed record DeleteMappingsResult
    {
        public required bool Success { get; init; }
        public int MappingsDeleted { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Key rotation result.
    /// </summary>
    public sealed record KeyRotationResult
    {
        public required bool Success { get; init; }
        public string? DomainId { get; init; }
        public int MappingsUpdated { get; init; }
        public DateTime RotatedAt { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Pseudonymization action types.
    /// </summary>
    public enum PseudonymAction
    {
        Pseudonymize,
        Reverse,
        ReversalDenied,
        DeleteMappings,
        KeyRotation
    }

    /// <summary>
    /// Pseudonymization audit entry.
    /// </summary>
    public sealed record PseudonymizationAuditEntry
    {
        public required string EntryId { get; init; }
        public required string DomainId { get; init; }
        public required PseudonymAction Action { get; init; }
        public string? FieldType { get; init; }
        public string? ActorId { get; init; }
        public string? Reason { get; init; }
        public string? Details { get; init; }
        public required DateTime Timestamp { get; init; }
    }

    #endregion
}
