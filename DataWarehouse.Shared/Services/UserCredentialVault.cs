// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.Shared.Models;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// In-memory implementation of <see cref="IUserCredentialVault"/> with encryption.
/// For production use, replace with a persistent store (database, Key Vault, etc.).
/// </summary>
/// <remarks>
/// Security features:
/// - Encrypts credentials at rest using AES-256-GCM
/// - Uses user-specific encryption keys derived from a master key
/// - Audits all credential access
/// - Never logs or exposes API keys
/// </remarks>
public sealed class UserCredentialVault : IUserCredentialVault, IDisposable
{
    // Storage: userId -> providerId -> encrypted credential
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte[]>> _userCredentials = new();

    // Organization credentials: orgId -> providerId -> encrypted credential
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte[]>> _organizationCredentials = new();

    // Instance-level credentials: providerId -> encrypted credential
    private readonly ConcurrentDictionary<string, byte[]> _instanceCredentials = new();

    // User to organization mapping
    private readonly ConcurrentDictionary<string, string> _userOrganizations = new();

    // Audit log
    private readonly ConcurrentQueue<CredentialAccessAudit> _auditLog = new();
    private const int MaxAuditEntries = 10000;

    // Encryption
    private readonly byte[] _masterKey;
    private readonly ICredentialEncryption _encryption;

    // OAuth token refresh handler
    private readonly Func<string, OAuthToken, CancellationToken, Task<OAuthToken>>? _tokenRefreshHandler;

    /// <summary>
    /// Event raised when a credential is accessed (for external audit logging).
    /// </summary>
    public event EventHandler<CredentialAccessAudit>? CredentialAccessed;

    /// <summary>
    /// Creates a new credential vault with a random master key.
    /// </summary>
    public UserCredentialVault()
        : this(GenerateRandomKey())
    {
    }

    /// <summary>
    /// Creates a new credential vault with the specified master key.
    /// </summary>
    /// <param name="masterKey">Master encryption key (must be 32 bytes for AES-256).</param>
    /// <param name="tokenRefreshHandler">Optional handler for OAuth token refresh.</param>
    public UserCredentialVault(
        byte[] masterKey,
        Func<string, OAuthToken, CancellationToken, Task<OAuthToken>>? tokenRefreshHandler = null)
    {
        if (masterKey == null || masterKey.Length != 32)
            throw new ArgumentException("Master key must be 32 bytes for AES-256", nameof(masterKey));

        _masterKey = masterKey;
        _encryption = new AesGcmCredentialEncryption();
        _tokenRefreshHandler = tokenRefreshHandler;
    }

    /// <inheritdoc />
    public async Task StoreCredentialAsync(
        string userId,
        string providerId,
        AICredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(credential);

        var targetStorage = GetStorageForScope(credential.Scope, userId, credential.OrganizationId);
        var key = GetStorageKey(credential.Scope, userId, credential.OrganizationId);

        try
        {
            var derivedKey = DeriveKey(key);
            var encryptedData = await _encryption.EncryptAsync(credential, derivedKey, cancellationToken);

            targetStorage[providerId] = encryptedData;

            // If this is a personal credential and we have an org ID, record the mapping
            if (credential.Scope == CredentialScope.Personal && !string.IsNullOrEmpty(credential.OrganizationId))
            {
                _userOrganizations[userId] = credential.OrganizationId;
            }

            AuditAccess(credential.Id, userId, "store", providerId, true);
        }
        catch (Exception ex)
        {
            AuditAccess(credential.Id, userId, "store", providerId, false, ex.Message);
            throw new CredentialVaultException($"Failed to store credential for provider '{providerId}'", ex);
        }
    }

    /// <inheritdoc />
    public async Task<AICredential?> GetCredentialAsync(
        string userId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(providerId);

        // Try personal credentials first
        var userStorage = GetUserStorage(userId);
        if (userStorage.TryGetValue(providerId, out var encryptedData))
        {
            try
            {
                var derivedKey = DeriveKey(userId);
                var credential = await _encryption.DecryptAsync(encryptedData, derivedKey, cancellationToken);
                credential.RecordUsage();
                AuditAccess(credential.Id, userId, "read", providerId, true);
                return credential;
            }
            catch (Exception ex)
            {
                AuditAccess("unknown", userId, "read", providerId, false, ex.Message);
                throw new CredentialVaultException($"Failed to decrypt credential for provider '{providerId}'", ex);
            }
        }

        AuditAccess("not_found", userId, "read", providerId, false, "Credential not found");
        return null;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteCredentialAsync(
        string userId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(providerId);

        var userStorage = GetUserStorage(userId);
        var removed = userStorage.TryRemove(providerId, out _);

        AuditAccess("deleted", userId, "delete", providerId, removed,
            removed ? null : "Credential not found");

        return await Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task<IEnumerable<string>> ListProvidersAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var userStorage = GetUserStorage(userId);
        var providers = userStorage.Keys.ToList();

        AuditAccess("list", userId, "list_providers", null, true);

        return Task.FromResult<IEnumerable<string>>(providers);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AICredential>> ListCredentialsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var userStorage = GetUserStorage(userId);
        var credentials = new List<AICredential>();
        var derivedKey = DeriveKey(userId);

        foreach (var kvp in userStorage)
        {
            try
            {
                var credential = await _encryption.DecryptAsync(kvp.Value, derivedKey, cancellationToken);
                // Return redacted version
                credentials.Add(credential.ToRedacted());
            }
            catch
            {
                // Skip credentials that fail to decrypt
            }
        }

        AuditAccess("list", userId, "list_credentials", null, true);

        return credentials;
    }

    /// <inheritdoc />
    public async Task RotateCredentialAsync(
        string userId,
        string providerId,
        AICredential newCredential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(newCredential);

        var userStorage = GetUserStorage(userId);
        if (!userStorage.ContainsKey(providerId))
        {
            throw new CredentialNotFoundException(userId, providerId);
        }

        // Get old credential for audit
        var oldCredential = await GetCredentialAsync(userId, providerId, cancellationToken);

        // Store new credential
        newCredential.LastRotatedAt = DateTime.UtcNow;
        await StoreCredentialAsync(userId, providerId, newCredential, cancellationToken);

        AuditAccess(newCredential.Id, userId, "rotate", providerId, true,
            context: new Dictionary<string, object>
            {
                ["previous_id"] = oldCredential?.Id ?? "unknown",
                ["rotated_at"] = DateTime.UtcNow
            });
    }

    /// <inheritdoc />
    public async Task<AICredential> RefreshOAuthTokenAsync(
        string userId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var credential = await GetCredentialAsync(userId, providerId, cancellationToken)
            ?? throw new CredentialNotFoundException(userId, providerId);

        if (credential.OAuthToken == null)
        {
            throw new TokenRefreshException(providerId, "Credential does not have an OAuth token");
        }

        if (!credential.OAuthToken.CanRefresh)
        {
            throw new TokenRefreshException(providerId, "OAuth token does not have a refresh token");
        }

        if (_tokenRefreshHandler == null)
        {
            throw new TokenRefreshException(providerId, "No token refresh handler configured");
        }

        try
        {
            var newToken = await _tokenRefreshHandler(providerId, credential.OAuthToken, cancellationToken);

            // Create updated credential with new token
            var updatedCredential = new AICredential
            {
                Id = credential.Id,
                ProviderId = credential.ProviderId,
                Scope = credential.Scope,
                DisplayName = credential.DisplayName,
                OAuthToken = newToken,
                TenantId = credential.TenantId,
                OrganizationId = credential.OrganizationId,
                Endpoint = credential.Endpoint,
                ApiVersion = credential.ApiVersion,
                Region = credential.Region,
                ExpiresAt = newToken.ExpiresAt,
                CreatedAt = credential.CreatedAt,
                LastUsedAt = DateTime.UtcNow,
                LastRotatedAt = DateTime.UtcNow,
                IsEnabled = credential.IsEnabled,
                Priority = credential.Priority,
                Metadata = credential.Metadata
            };

            await StoreCredentialAsync(userId, providerId, updatedCredential, cancellationToken);

            AuditAccess(credential.Id, userId, "token_refresh", providerId, true);

            return updatedCredential;
        }
        catch (Exception ex) when (ex is not TokenRefreshException)
        {
            AuditAccess(credential.Id, userId, "token_refresh", providerId, false, ex.Message);
            throw new TokenRefreshException(providerId, ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public async Task<CredentialValidationResult> ValidateCredentialAsync(
        string userId,
        string providerId,
        CancellationToken cancellationToken = default)
    {
        var credential = await GetCredentialAsync(userId, providerId, cancellationToken);

        if (credential == null)
        {
            return CredentialValidationResult.Failure("Credential not found");
        }

        var errors = new List<string>();
        var warnings = new List<string>();
        var needsRefresh = false;
        var expiringSoon = false;

        // Check enabled status
        if (!credential.IsEnabled)
        {
            errors.Add("Credential is disabled");
        }

        // Check expiration
        if (credential.IsExpired)
        {
            errors.Add("Credential has expired");
        }
        else if (credential.ExpiresAt.HasValue)
        {
            var timeToExpiry = credential.ExpiresAt.Value - DateTime.UtcNow;
            if (timeToExpiry < TimeSpan.FromHours(24))
            {
                expiringSoon = true;
                warnings.Add($"Credential expires in {timeToExpiry.TotalHours:F1} hours");
            }
        }

        // Check authentication method
        if (!credential.HasApiKey && credential.OAuthToken == null)
        {
            errors.Add("Credential has no authentication method (API key or OAuth token)");
        }

        // Check OAuth token
        if (credential.OAuthToken != null)
        {
            if (credential.OAuthToken.IsExpired)
            {
                if (credential.OAuthToken.CanRefresh)
                {
                    needsRefresh = true;
                    warnings.Add("OAuth token has expired but can be refreshed");
                }
                else
                {
                    errors.Add("OAuth token has expired and cannot be refreshed");
                }
            }
            else if (credential.OAuthToken.ExpiresAt.HasValue)
            {
                var tokenExpiry = credential.OAuthToken.ExpiresAt.Value - DateTime.UtcNow;
                if (tokenExpiry < TimeSpan.FromMinutes(5))
                {
                    needsRefresh = true;
                    warnings.Add("OAuth token is about to expire");
                }
            }
        }

        AuditAccess(credential.Id, userId, "validate", providerId, errors.Count == 0);

        return new CredentialValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
            NeedsRefresh = needsRefresh,
            ExpiringSoon = expiringSoon
        };
    }

    /// <inheritdoc />
    public async Task<AICredential?> GetEffectiveCredentialAsync(
        string userId,
        string providerId,
        string? organizationId = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Try personal credential
        var personalCredential = await GetCredentialAsync(userId, providerId, cancellationToken);
        if (personalCredential != null && personalCredential.IsUsable)
        {
            return personalCredential;
        }

        // 2. Try organization credential
        var orgId = organizationId ?? GetUserOrganization(userId);
        if (!string.IsNullOrEmpty(orgId))
        {
            var orgCredential = await GetOrganizationCredentialAsync(orgId, providerId, cancellationToken);
            if (orgCredential != null && orgCredential.IsUsable)
            {
                AuditAccess(orgCredential.Id, userId, "read_org", providerId, true,
                    context: new Dictionary<string, object> { ["organization_id"] = orgId });
                return orgCredential;
            }
        }

        // 3. Try instance credential
        var instanceCredential = await GetInstanceCredentialAsync(providerId, cancellationToken);
        if (instanceCredential != null && instanceCredential.IsUsable)
        {
            AuditAccess(instanceCredential.Id, userId, "read_instance", providerId, true);
            return instanceCredential;
        }

        return null;
    }

    /// <summary>
    /// Stores an organization-level credential.
    /// </summary>
    public async Task StoreOrganizationCredentialAsync(
        string organizationId,
        string providerId,
        AICredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(credential);

        var orgStorage = GetOrganizationStorage(organizationId);
        var derivedKey = DeriveKey($"org:{organizationId}");

        var encryptedData = await _encryption.EncryptAsync(credential, derivedKey, cancellationToken);
        orgStorage[providerId] = encryptedData;

        AuditAccess(credential.Id, $"org:{organizationId}", "store_org", providerId, true);
    }

    /// <summary>
    /// Stores an instance-level credential.
    /// </summary>
    public async Task StoreInstanceCredentialAsync(
        string providerId,
        AICredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(credential);

        var derivedKey = DeriveKey("instance");
        var encryptedData = await _encryption.EncryptAsync(credential, derivedKey, cancellationToken);
        _instanceCredentials[providerId] = encryptedData;

        AuditAccess(credential.Id, "instance", "store_instance", providerId, true);
    }

    /// <summary>
    /// Associates a user with an organization.
    /// </summary>
    public void SetUserOrganization(string userId, string organizationId)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(organizationId);
        _userOrganizations[userId] = organizationId;
    }

    /// <summary>
    /// Gets the organization ID for a user.
    /// </summary>
    public string? GetUserOrganization(string userId)
    {
        return _userOrganizations.TryGetValue(userId, out var orgId) ? orgId : null;
    }

    /// <summary>
    /// Gets recent audit log entries.
    /// </summary>
    public IEnumerable<CredentialAccessAudit> GetAuditLog(int maxEntries = 100)
    {
        return _auditLog.TakeLast(Math.Min(maxEntries, _auditLog.Count)).ToList();
    }

    /// <summary>
    /// Clears all stored credentials (for testing only).
    /// </summary>
    public void Clear()
    {
        _userCredentials.Clear();
        _organizationCredentials.Clear();
        _instanceCredentials.Clear();
        _userOrganizations.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Clear sensitive data
        CryptographicOperations.ZeroMemory(_masterKey);
        Clear();
    }

    #region Private Methods

    private ConcurrentDictionary<string, byte[]> GetUserStorage(string userId)
    {
        return _userCredentials.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte[]>());
    }

    private ConcurrentDictionary<string, byte[]> GetOrganizationStorage(string organizationId)
    {
        return _organizationCredentials.GetOrAdd(organizationId, _ => new ConcurrentDictionary<string, byte[]>());
    }

    private ConcurrentDictionary<string, byte[]> GetStorageForScope(
        CredentialScope scope,
        string userId,
        string? organizationId)
    {
        return scope switch
        {
            CredentialScope.Personal => GetUserStorage(userId),
            CredentialScope.Organization when !string.IsNullOrEmpty(organizationId)
                => GetOrganizationStorage(organizationId),
            _ => GetUserStorage(userId)
        };
    }

    private string GetStorageKey(CredentialScope scope, string userId, string? organizationId)
    {
        return scope switch
        {
            CredentialScope.Personal => userId,
            CredentialScope.Organization => $"org:{organizationId}",
            CredentialScope.Instance => "instance",
            _ => userId
        };
    }

    private async Task<AICredential?> GetOrganizationCredentialAsync(
        string organizationId,
        string providerId,
        CancellationToken cancellationToken)
    {
        var orgStorage = GetOrganizationStorage(organizationId);
        if (orgStorage.TryGetValue(providerId, out var encryptedData))
        {
            var derivedKey = DeriveKey($"org:{organizationId}");
            return await _encryption.DecryptAsync(encryptedData, derivedKey, cancellationToken);
        }
        return null;
    }

    private async Task<AICredential?> GetInstanceCredentialAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        if (_instanceCredentials.TryGetValue(providerId, out var encryptedData))
        {
            var derivedKey = DeriveKey("instance");
            return await _encryption.DecryptAsync(encryptedData, derivedKey, cancellationToken);
        }
        return null;
    }

    private byte[] DeriveKey(string context)
    {
        // Use HKDF to derive a unique key for each user/context
        var contextBytes = Encoding.UTF8.GetBytes(context);
        var derivedKey = new byte[32];

        // Simple key derivation using HMAC-SHA256
        using var hmac = new HMACSHA256(_masterKey);
        var hash = hmac.ComputeHash(contextBytes);
        Array.Copy(hash, derivedKey, 32);

        return derivedKey;
    }

    private void AuditAccess(
        string credentialId,
        string userId,
        string accessType,
        string? providerId,
        bool success,
        string? errorMessage = null,
        Dictionary<string, object>? context = null)
    {
        var audit = new CredentialAccessAudit
        {
            CredentialId = credentialId,
            UserId = userId,
            AccessType = accessType,
            ProviderId = providerId,
            Success = success,
            ErrorMessage = errorMessage,
            Context = context
        };

        _auditLog.Enqueue(audit);

        // Trim audit log if too large
        while (_auditLog.Count > MaxAuditEntries)
        {
            _auditLog.TryDequeue(out _);
        }

        // Raise event for external logging
        CredentialAccessed?.Invoke(this, audit);
    }

    private static byte[] GenerateRandomKey()
    {
        var key = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(key);
        return key;
    }

    #endregion
}

/// <summary>
/// Interface for credential encryption implementations.
/// </summary>
internal interface ICredentialEncryption
{
    Task<byte[]> EncryptAsync(AICredential credential, byte[] key, CancellationToken ct);
    Task<AICredential> DecryptAsync(byte[] encryptedData, byte[] key, CancellationToken ct);
}

/// <summary>
/// AES-GCM based credential encryption.
/// </summary>
internal sealed class AesGcmCredentialEncryption : ICredentialEncryption
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public Task<byte[]> EncryptAsync(AICredential credential, byte[] key, CancellationToken ct)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(credential);

        var nonce = new byte[NonceSize];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(nonce);

        var ciphertext = new byte[jsonBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, jsonBytes, ciphertext, tag);

        // Combine: nonce + tag + ciphertext
        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        Array.Copy(nonce, 0, result, 0, NonceSize);
        Array.Copy(tag, 0, result, NonceSize, TagSize);
        Array.Copy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);

        return Task.FromResult(result);
    }

    public Task<AICredential> DecryptAsync(byte[] encryptedData, byte[] key, CancellationToken ct)
    {
        if (encryptedData.Length < NonceSize + TagSize)
            throw new CryptographicException("Invalid encrypted data");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[encryptedData.Length - NonceSize - TagSize];

        Array.Copy(encryptedData, 0, nonce, 0, NonceSize);
        Array.Copy(encryptedData, NonceSize, tag, 0, TagSize);
        Array.Copy(encryptedData, NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        var credential = JsonSerializer.Deserialize<AICredential>(plaintext)
            ?? throw new CryptographicException("Failed to deserialize credential");

        return Task.FromResult(credential);
    }
}
