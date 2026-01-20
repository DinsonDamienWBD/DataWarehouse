using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.SDK.Federation;

// ============================================================================
// SCENARIO 1: Cloud Share (U1 → DWH → U2)
// Extends: VirtualFilesystem, CapabilityIssuer, NamespaceProjectionService
// ============================================================================

#region CloudShare Manager

/// <summary>
/// Manages cloud share lifecycle for secure file sharing.
/// </summary>
public sealed class CloudShareManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ShareDescriptor> _shares = new();
    private readonly ConcurrentDictionary<string, ShareAccessLog> _accessLogs = new();
    private readonly Timer _expiryTimer;
    private readonly CloudShareConfig _config;
    private volatile bool _disposed;

    public event EventHandler<ShareEventArgs>? ShareCreated;
    public event EventHandler<ShareEventArgs>? ShareAccessed;
    public event EventHandler<ShareEventArgs>? ShareExpired;

    public CloudShareManager(CloudShareConfig? config = null)
    {
        _config = config ?? new CloudShareConfig();
        _expiryTimer = new Timer(CleanupExpiredShares, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Creates a new share for a file or directory.
    /// </summary>
    public ShareLink CreateShare(CreateShareRequest request)
    {
        var shareId = GenerateShareId();
        var token = GenerateShareToken();

        var descriptor = new ShareDescriptor
        {
            ShareId = shareId,
            Token = token,
            OwnerId = request.OwnerId,
            ResourcePath = request.ResourcePath,
            ResourceType = request.ResourceType,
            Permissions = request.Permissions,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt ?? DateTime.UtcNow.Add(_config.DefaultShareDuration),
            MaxAccessCount = request.MaxAccessCount,
            CurrentAccessCount = 0,
            PasswordHash = request.Password != null ? HashPassword(request.Password) : null,
            AllowedEmails = request.AllowedEmails?.ToList(),
            RequireSignIn = request.RequireSignIn
        };

        _shares[shareId] = descriptor;
        _accessLogs[shareId] = new ShareAccessLog { ShareId = shareId };

        ShareCreated?.Invoke(this, new ShareEventArgs { Share = descriptor });

        return new ShareLink
        {
            ShareId = shareId,
            Token = token,
            Url = $"{_config.BaseUrl}/share/{shareId}?token={token}",
            ExpiresAt = descriptor.ExpiresAt
        };
    }

    /// <summary>
    /// Accesses a share with validation.
    /// </summary>
    public async Task<ShareAccessResult> AccessShareAsync(AccessShareRequest request, CancellationToken ct = default)
    {
        if (!_shares.TryGetValue(request.ShareId, out var share))
            return new ShareAccessResult { Success = false, Error = "Share not found" };

        // Validate token
        if (share.Token != request.Token)
            return new ShareAccessResult { Success = false, Error = "Invalid token" };

        // Check expiry
        if (share.ExpiresAt < DateTime.UtcNow)
        {
            await ExpireShareAsync(request.ShareId, ct);
            return new ShareAccessResult { Success = false, Error = "Share has expired" };
        }

        // Check access count
        if (share.MaxAccessCount.HasValue && share.CurrentAccessCount >= share.MaxAccessCount)
            return new ShareAccessResult { Success = false, Error = "Maximum access count reached" };

        // Validate password
        if (share.PasswordHash != null)
        {
            if (request.Password == null || !VerifyPassword(request.Password, share.PasswordHash))
                return new ShareAccessResult { Success = false, Error = "Invalid password" };
        }

        // Validate email restriction
        if (share.AllowedEmails?.Count > 0 && !share.AllowedEmails.Contains(request.UserEmail ?? ""))
            return new ShareAccessResult { Success = false, Error = "Email not authorized" };

        // Check sign-in requirement
        if (share.RequireSignIn && string.IsNullOrEmpty(request.UserId))
            return new ShareAccessResult { Success = false, Error = "Sign-in required" };

        // Record access
        share.CurrentAccessCount++;
        share.LastAccessedAt = DateTime.UtcNow;

        if (_accessLogs.TryGetValue(request.ShareId, out var log))
        {
            log.Records.Add(new ShareAccessRecord
            {
                AccessedAt = DateTime.UtcNow,
                UserId = request.UserId,
                UserEmail = request.UserEmail,
                IpAddress = request.IpAddress,
                UserAgent = request.UserAgent
            });
        }

        ShareAccessed?.Invoke(this, new ShareEventArgs { Share = share });

        return new ShareAccessResult
        {
            Success = true,
            ResourcePath = share.ResourcePath,
            ResourceType = share.ResourceType,
            Permissions = share.Permissions,
            ProjectionToken = GenerateProjectionToken(share)
        };
    }

    /// <summary>
    /// Gets share by ID.
    /// </summary>
    public ShareDescriptor? GetShare(string shareId) =>
        _shares.TryGetValue(shareId, out var share) ? share : null;

    /// <summary>
    /// Lists shares owned by a user.
    /// </summary>
    public IReadOnlyList<ShareDescriptor> ListUserShares(string ownerId) =>
        _shares.Values.Where(s => s.OwnerId == ownerId).ToList();

    /// <summary>
    /// Revokes a share.
    /// </summary>
    public bool RevokeShare(string shareId, string requesterId)
    {
        if (_shares.TryGetValue(shareId, out var share) && share.OwnerId == requesterId)
        {
            _shares.TryRemove(shareId, out _);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gets access logs for a share.
    /// </summary>
    public ShareAccessLog? GetAccessLog(string shareId) =>
        _accessLogs.TryGetValue(shareId, out var log) ? log : null;

    private async Task ExpireShareAsync(string shareId, CancellationToken ct)
    {
        if (_shares.TryRemove(shareId, out var share))
        {
            share.ExpiredAt = DateTime.UtcNow;
            ShareExpired?.Invoke(this, new ShareEventArgs { Share = share });
        }
    }

    private void CleanupExpiredShares(object? state)
    {
        if (_disposed) return;

        foreach (var kvp in _shares)
        {
            if (kvp.Value.ExpiresAt < DateTime.UtcNow)
            {
                _ = ExpireShareAsync(kvp.Key, CancellationToken.None);
            }
        }
    }

    private string GenerateShareId() => Guid.NewGuid().ToString("N")[..12];
    private string GenerateShareToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private string GenerateProjectionToken(ShareDescriptor share) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{share.ShareId}:{share.ResourcePath}:{DateTime.UtcNow.Ticks}"));

    private string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(password + _config.PasswordSalt));
        return Convert.ToBase64String(hash);
    }

    private bool VerifyPassword(string password, string hash) => HashPassword(password) == hash;

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _expiryTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

#endregion

#region VFS Projection

/// <summary>
/// Creates user-specific VFS projections per share.
/// </summary>
public sealed class VfsProjectionService
{
    private readonly ConcurrentDictionary<string, VfsProjection> _projections = new();

    /// <summary>
    /// Creates a VFS projection for a share.
    /// </summary>
    public VfsProjection CreateProjection(string shareId, string basePath, SharePermissions permissions)
    {
        var projection = new VfsProjection
        {
            ProjectionId = Guid.NewGuid().ToString("N"),
            ShareId = shareId,
            BasePath = basePath,
            Permissions = permissions,
            CreatedAt = DateTime.UtcNow,
            MountPoints = new List<ProjectionMount>
            {
                new() { VirtualPath = "/", RealPath = basePath, ReadOnly = !permissions.HasFlag(SharePermissions.Write) }
            }
        };

        _projections[projection.ProjectionId] = projection;
        return projection;
    }

    /// <summary>
    /// Resolves a virtual path to real path within projection.
    /// </summary>
    public string? ResolvePath(string projectionId, string virtualPath)
    {
        if (!_projections.TryGetValue(projectionId, out var projection))
            return null;

        // Validate path doesn't escape projection
        var normalized = NormalizePath(virtualPath);
        if (normalized.Contains(".."))
            return null;

        return Path.Combine(projection.BasePath, normalized.TrimStart('/'));
    }

    /// <summary>
    /// Checks if operation is allowed in projection.
    /// </summary>
    public bool IsOperationAllowed(string projectionId, FileOperation operation)
    {
        if (!_projections.TryGetValue(projectionId, out var projection))
            return false;

        return operation switch
        {
            FileOperation.Read => projection.Permissions.HasFlag(SharePermissions.Read),
            FileOperation.Write => projection.Permissions.HasFlag(SharePermissions.Write),
            FileOperation.Delete => projection.Permissions.HasFlag(SharePermissions.Delete),
            FileOperation.List => projection.Permissions.HasFlag(SharePermissions.Read),
            _ => false
        };
    }

    private string NormalizePath(string path) => path.Replace('\\', '/');
}

#endregion

#region Types

public sealed class CloudShareConfig
{
    public string BaseUrl { get; set; } = "https://share.datawarehouse.local";
    public TimeSpan DefaultShareDuration { get; set; } = TimeSpan.FromDays(7);
    public string PasswordSalt { get; set; } = "DataWarehouse_Share_Salt_2026";
    public int MaxSharesPerUser { get; set; } = 100;
}

public sealed class CreateShareRequest
{
    public required string OwnerId { get; init; }
    public required string ResourcePath { get; init; }
    public ShareResourceType ResourceType { get; init; } = ShareResourceType.File;
    public SharePermissions Permissions { get; init; } = SharePermissions.Read;
    public DateTime? ExpiresAt { get; init; }
    public int? MaxAccessCount { get; init; }
    public string? Password { get; init; }
    public IEnumerable<string>? AllowedEmails { get; init; }
    public bool RequireSignIn { get; init; }
}

public sealed class ShareDescriptor
{
    public string ShareId { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public string OwnerId { get; init; } = string.Empty;
    public string ResourcePath { get; init; } = string.Empty;
    public ShareResourceType ResourceType { get; init; }
    public SharePermissions Permissions { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? ExpiredAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public int? MaxAccessCount { get; init; }
    public int CurrentAccessCount { get; set; }
    public string? PasswordHash { get; init; }
    public List<string>? AllowedEmails { get; init; }
    public bool RequireSignIn { get; init; }
}

public sealed class ShareLink
{
    public string ShareId { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }
}

public sealed class AccessShareRequest
{
    public required string ShareId { get; init; }
    public required string Token { get; init; }
    public string? Password { get; init; }
    public string? UserId { get; init; }
    public string? UserEmail { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
}

public sealed class ShareAccessResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ResourcePath { get; init; }
    public ShareResourceType ResourceType { get; init; }
    public SharePermissions Permissions { get; init; }
    public string? ProjectionToken { get; init; }
}

public sealed class ShareAccessLog
{
    public string ShareId { get; init; } = string.Empty;
    public List<ShareAccessRecord> Records { get; } = new();
}

public sealed class ShareAccessRecord
{
    public DateTime AccessedAt { get; init; }
    public string? UserId { get; init; }
    public string? UserEmail { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
}

public sealed class ShareEventArgs : EventArgs
{
    public ShareDescriptor Share { get; init; } = null!;
}

public sealed class VfsProjection
{
    public string ProjectionId { get; init; } = string.Empty;
    public string ShareId { get; init; } = string.Empty;
    public string BasePath { get; init; } = string.Empty;
    public SharePermissions Permissions { get; init; }
    public DateTime CreatedAt { get; init; }
    public List<ProjectionMount> MountPoints { get; init; } = new();
}

public sealed class ProjectionMount
{
    public string VirtualPath { get; init; } = string.Empty;
    public string RealPath { get; init; } = string.Empty;
    public bool ReadOnly { get; init; }
}

public enum ShareResourceType { File, Directory }

[Flags]
public enum SharePermissions
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    All = Read | Write | Delete
}

public enum FileOperation { Read, Write, Delete, List, Create }

#endregion
