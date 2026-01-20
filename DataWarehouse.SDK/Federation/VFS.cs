namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;

/// <summary>
/// Type of node in the virtual filesystem.
/// </summary>
public enum VfsNodeType
{
    /// <summary>Directory/container.</summary>
    Directory = 0,
    /// <summary>File/object.</summary>
    File = 1,
    /// <summary>Symbolic link to another path.</summary>
    SymLink = 2,
    /// <summary>Mount point for another namespace.</summary>
    Mount = 3
}

/// <summary>
/// Virtual path in the federated namespace.
/// </summary>
public readonly struct VfsPath : IEquatable<VfsPath>, IComparable<VfsPath>
{
    private readonly string _path;

    /// <summary>Root path.</summary>
    public static VfsPath Root { get; } = new("/");

    /// <summary>Gets the raw path string.</summary>
    public string Value => _path ?? "/";

    /// <summary>Gets whether this is the root path.</summary>
    public bool IsRoot => _path == "/" || string.IsNullOrEmpty(_path);

    /// <summary>Gets the path segments.</summary>
    public string[] Segments => IsRoot
        ? Array.Empty<string>()
        : _path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Gets the filename/last segment.</summary>
    public string Name => IsRoot ? "/" : Segments.LastOrDefault() ?? "/";

    /// <summary>Gets the parent path.</summary>
    public VfsPath Parent
    {
        get
        {
            if (IsRoot) return Root;
            var lastSlash = _path.LastIndexOf('/');
            return lastSlash <= 0 ? Root : new VfsPath(_path[..lastSlash]);
        }
    }

    /// <summary>Gets the depth of this path.</summary>
    public int Depth => Segments.Length;

    public VfsPath(string path)
    {
        _path = Normalize(path);
    }

    /// <summary>
    /// Normalizes a path string.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        // Ensure starts with /
        if (!path.StartsWith('/'))
            path = "/" + path;

        // Remove trailing slash (except for root)
        if (path.Length > 1 && path.EndsWith('/'))
            path = path.TrimEnd('/');

        // Collapse multiple slashes
        while (path.Contains("//"))
            path = path.Replace("//", "/");

        // Handle . and ..
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();

        foreach (var seg in segments)
        {
            if (seg == ".")
                continue;
            if (seg == "..")
            {
                if (stack.Count > 0)
                    stack.Pop();
                continue;
            }
            stack.Push(seg);
        }

        return stack.Count == 0 ? "/" : "/" + string.Join("/", stack.Reverse());
    }

    /// <summary>
    /// Combines this path with a child segment.
    /// </summary>
    public VfsPath Combine(string child)
    {
        if (string.IsNullOrEmpty(child)) return this;
        if (child.StartsWith('/')) return new VfsPath(child);
        return new VfsPath(_path + "/" + child);
    }

    /// <summary>
    /// Gets a relative path from this path to another.
    /// </summary>
    public string GetRelativeTo(VfsPath basePath)
    {
        if (!_path.StartsWith(basePath._path))
            return _path;

        var relative = _path[(basePath._path.Length)..];
        return string.IsNullOrEmpty(relative) ? "." : relative.TrimStart('/');
    }

    /// <summary>
    /// Checks if this path starts with another path.
    /// </summary>
    public bool StartsWith(VfsPath other)
    {
        if (other.IsRoot) return true;
        return _path.StartsWith(other._path + "/") || _path == other._path;
    }

    /// <summary>
    /// Parses a path from string.
    /// </summary>
    public static VfsPath Parse(string path) => new(path);

    public override string ToString() => _path ?? "/";
    public override int GetHashCode() => (_path ?? "/").GetHashCode();
    public override bool Equals(object? obj) => obj is VfsPath other && Equals(other);
    public bool Equals(VfsPath other) => _path == other._path;
    public int CompareTo(VfsPath other) => string.Compare(_path, other._path, StringComparison.Ordinal);

    public static bool operator ==(VfsPath left, VfsPath right) => left.Equals(right);
    public static bool operator !=(VfsPath left, VfsPath right) => !left.Equals(right);
    public static VfsPath operator /(VfsPath left, string right) => left.Combine(right);
}

/// <summary>
/// A node in the virtual filesystem.
/// </summary>
public sealed class VfsNode
{
    /// <summary>Virtual path to this node.</summary>
    public VfsPath Path { get; init; }

    /// <summary>Node type.</summary>
    public VfsNodeType Type { get; init; }

    /// <summary>Object ID for file nodes.</summary>
    public ObjectId? ObjectId { get; set; }

    /// <summary>Target path for symlinks.</summary>
    public VfsPath? LinkTarget { get; set; }

    /// <summary>Owning node ID.</summary>
    public string OwnerNodeId { get; set; } = string.Empty;

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last modification timestamp.</summary>
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Size in bytes (for files).</summary>
    public long SizeBytes { get; set; }

    /// <summary>Content type (for files).</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Child nodes (for directories).</summary>
    public Dictionary<string, VfsNode> Children { get; } = new();

    /// <summary>Required capability permissions.</summary>
    public CapabilityPermissions RequiredPermissions { get; set; } = CapabilityPermissions.Read;

    /// <summary>
    /// Gets the name of this node.
    /// </summary>
    public string Name => Path.Name;

    /// <summary>
    /// Checks if this is a directory.
    /// </summary>
    public bool IsDirectory => Type == VfsNodeType.Directory;

    /// <summary>
    /// Checks if this is a file.
    /// </summary>
    public bool IsFile => Type == VfsNodeType.File;
}

/// <summary>
/// Interface for virtual filesystem operations.
/// </summary>
public interface IVirtualFilesystem
{
    /// <summary>Gets the local node ID owning this VFS.</summary>
    string LocalNodeId { get; }

    /// <summary>Gets a node at a path.</summary>
    Task<VfsNode?> GetNodeAsync(VfsPath path, CancellationToken ct = default);

    /// <summary>Creates a directory.</summary>
    Task<VfsNode> CreateDirectoryAsync(VfsPath path, CancellationToken ct = default);

    /// <summary>Creates a file node linked to an object.</summary>
    Task<VfsNode> CreateFileAsync(VfsPath path, ObjectId objectId, long size, string contentType, CancellationToken ct = default);

    /// <summary>Deletes a node.</summary>
    Task<bool> DeleteAsync(VfsPath path, bool recursive = false, CancellationToken ct = default);

    /// <summary>Moves/renames a node.</summary>
    Task<bool> MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct = default);

    /// <summary>Creates a symbolic link.</summary>
    Task<VfsNode> CreateLinkAsync(VfsPath linkPath, VfsPath targetPath, CancellationToken ct = default);

    /// <summary>Lists children of a directory.</summary>
    Task<IReadOnlyList<VfsNode>> ListAsync(VfsPath path, CancellationToken ct = default);

    /// <summary>Checks if a path exists.</summary>
    Task<bool> ExistsAsync(VfsPath path, CancellationToken ct = default);

    /// <summary>Resolves a path (following symlinks).</summary>
    Task<VfsPath> ResolveAsync(VfsPath path, int maxDepth = 10, CancellationToken ct = default);
}

/// <summary>
/// A virtual namespace that can be mounted into the VFS.
/// </summary>
public sealed class VfsNamespace
{
    /// <summary>Namespace identifier.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Mount point in the global VFS.</summary>
    public VfsPath MountPoint { get; set; } = VfsPath.Root;

    /// <summary>Owning node ID.</summary>
    public string OwnerNodeId { get; set; } = string.Empty;

    /// <summary>Root node of this namespace.</summary>
    public VfsNode Root { get; init; }

    /// <summary>Whether this namespace is read-only.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Required capability to access.</summary>
    public string? RequiredCapabilityId { get; set; }

    public VfsNamespace()
    {
        Root = new VfsNode
        {
            Path = VfsPath.Root,
            Type = VfsNodeType.Directory,
            OwnerNodeId = OwnerNodeId
        };
    }
}

/// <summary>
/// Virtual filesystem implementation.
/// </summary>
public sealed class VirtualFilesystem : IVirtualFilesystem
{
    private readonly VfsNode _root;
    private readonly ConcurrentDictionary<string, VfsNamespace> _namespaces = new();
    private readonly ReaderWriterLockSlim _lock = new();

    /// <inheritdoc />
    public string LocalNodeId { get; }

    public VirtualFilesystem(string localNodeId)
    {
        LocalNodeId = localNodeId;
        _root = new VfsNode
        {
            Path = VfsPath.Root,
            Type = VfsNodeType.Directory,
            OwnerNodeId = localNodeId
        };
    }

    /// <inheritdoc />
    public Task<VfsNode?> GetNodeAsync(VfsPath path, CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            return Task.FromResult(GetNodeInternal(path));
        }
        finally { _lock.ExitReadLock(); }
    }

    private VfsNode? GetNodeInternal(VfsPath path)
    {
        if (path.IsRoot) return _root;

        var current = _root;
        foreach (var segment in path.Segments)
        {
            if (!current.Children.TryGetValue(segment, out var child))
                return null;
            current = child;
        }
        return current;
    }

    /// <inheritdoc />
    public Task<VfsNode> CreateDirectoryAsync(VfsPath path, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            var node = EnsureDirectoryInternal(path);
            return Task.FromResult(node);
        }
        finally { _lock.ExitWriteLock(); }
    }

    private VfsNode EnsureDirectoryInternal(VfsPath path)
    {
        if (path.IsRoot) return _root;

        var current = _root;
        foreach (var segment in path.Segments)
        {
            if (!current.Children.TryGetValue(segment, out var child))
            {
                var childPath = current.Path.Combine(segment);
                child = new VfsNode
                {
                    Path = childPath,
                    Type = VfsNodeType.Directory,
                    OwnerNodeId = LocalNodeId
                };
                current.Children[segment] = child;
            }
            current = child;
        }
        return current;
    }

    /// <inheritdoc />
    public Task<VfsNode> CreateFileAsync(VfsPath path, ObjectId objectId, long size, string contentType, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            // Ensure parent directory exists
            var parent = EnsureDirectoryInternal(path.Parent);

            var node = new VfsNode
            {
                Path = path,
                Type = VfsNodeType.File,
                ObjectId = objectId,
                OwnerNodeId = LocalNodeId,
                SizeBytes = size,
                ContentType = contentType
            };

            parent.Children[path.Name] = node;
            return Task.FromResult(node);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(VfsPath path, bool recursive = false, CancellationToken ct = default)
    {
        if (path.IsRoot) return Task.FromResult(false);

        _lock.EnterWriteLock();
        try
        {
            var parent = GetNodeInternal(path.Parent);
            if (parent == null) return Task.FromResult(false);

            if (!parent.Children.TryGetValue(path.Name, out var node))
                return Task.FromResult(false);

            if (node.IsDirectory && node.Children.Count > 0 && !recursive)
                return Task.FromResult(false);

            parent.Children.Remove(path.Name);
            return Task.FromResult(true);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <inheritdoc />
    public Task<bool> MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct = default)
    {
        if (source.IsRoot) return Task.FromResult(false);

        _lock.EnterWriteLock();
        try
        {
            var sourceParent = GetNodeInternal(source.Parent);
            if (sourceParent == null) return Task.FromResult(false);

            if (!sourceParent.Children.TryGetValue(source.Name, out var node))
                return Task.FromResult(false);

            var destParent = EnsureDirectoryInternal(destination.Parent);

            // Remove from source
            sourceParent.Children.Remove(source.Name);

            // Update path and add to destination
            var movedNode = new VfsNode
            {
                Path = destination,
                Type = node.Type,
                ObjectId = node.ObjectId,
                LinkTarget = node.LinkTarget,
                OwnerNodeId = node.OwnerNodeId,
                CreatedAt = node.CreatedAt,
                ModifiedAt = DateTimeOffset.UtcNow,
                SizeBytes = node.SizeBytes,
                ContentType = node.ContentType,
                Metadata = new Dictionary<string, string>(node.Metadata),
                RequiredPermissions = node.RequiredPermissions
            };

            // Copy children for directories
            if (node.IsDirectory)
            {
                foreach (var child in node.Children)
                {
                    movedNode.Children[child.Key] = child.Value;
                }
            }

            destParent.Children[destination.Name] = movedNode;
            return Task.FromResult(true);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <inheritdoc />
    public Task<VfsNode> CreateLinkAsync(VfsPath linkPath, VfsPath targetPath, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            var parent = EnsureDirectoryInternal(linkPath.Parent);

            var node = new VfsNode
            {
                Path = linkPath,
                Type = VfsNodeType.SymLink,
                LinkTarget = targetPath,
                OwnerNodeId = LocalNodeId
            };

            parent.Children[linkPath.Name] = node;
            return Task.FromResult(node);
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VfsNode>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            var node = GetNodeInternal(path);
            if (node == null || !node.IsDirectory)
                return Task.FromResult<IReadOnlyList<VfsNode>>(Array.Empty<VfsNode>());

            return Task.FromResult<IReadOnlyList<VfsNode>>(node.Children.Values.ToList());
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(VfsPath path, CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            return Task.FromResult(GetNodeInternal(path) != null);
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <inheritdoc />
    public Task<VfsPath> ResolveAsync(VfsPath path, int maxDepth = 10, CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            return Task.FromResult(ResolveInternal(path, maxDepth));
        }
        finally { _lock.ExitReadLock(); }
    }

    private VfsPath ResolveInternal(VfsPath path, int maxDepth)
    {
        if (maxDepth <= 0) return path;

        var node = GetNodeInternal(path);
        if (node == null) return path;

        if (node.Type == VfsNodeType.SymLink && node.LinkTarget.HasValue)
        {
            var target = node.LinkTarget.Value;
            // Handle relative links
            if (!target.Value.StartsWith('/'))
                target = path.Parent.Combine(target.Value);

            return ResolveInternal(target, maxDepth - 1);
        }

        return path;
    }

    /// <summary>
    /// Mounts a namespace at a path.
    /// </summary>
    public void MountNamespace(VfsNamespace ns, VfsPath mountPoint)
    {
        _lock.EnterWriteLock();
        try
        {
            ns.MountPoint = mountPoint;
            _namespaces[ns.Id] = ns;

            // Create mount point node
            var parent = EnsureDirectoryInternal(mountPoint.Parent);
            var mountNode = new VfsNode
            {
                Path = mountPoint,
                Type = VfsNodeType.Mount,
                OwnerNodeId = ns.OwnerNodeId,
                Metadata = { ["namespace_id"] = ns.Id }
            };
            parent.Children[mountPoint.Name] = mountNode;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Unmounts a namespace.
    /// </summary>
    public bool UnmountNamespace(string namespaceId)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_namespaces.TryRemove(namespaceId, out var ns))
                return false;

            var parent = GetNodeInternal(ns.MountPoint.Parent);
            parent?.Children.Remove(ns.MountPoint.Name);
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Enumerates all nodes matching a pattern.
    /// </summary>
    public async IAsyncEnumerable<VfsNode> EnumerateAsync(
        VfsPath basePath,
        string? pattern = null,
        bool recursive = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var nodes = await ListAsync(basePath, ct);

        foreach (var node in nodes)
        {
            if (ct.IsCancellationRequested) yield break;

            if (pattern == null || MatchesPattern(node.Name, pattern))
                yield return node;

            if (recursive && node.IsDirectory)
            {
                await foreach (var child in EnumerateAsync(node.Path, pattern, true, ct))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        // Simple wildcard matching
        if (pattern == "*") return true;
        if (pattern.StartsWith('*') && pattern.EndsWith('*'))
            return name.Contains(pattern[1..^1]);
        if (pattern.StartsWith('*'))
            return name.EndsWith(pattern[1..]);
        if (pattern.EndsWith('*'))
            return name.StartsWith(pattern[..^1]);
        return name == pattern;
    }
}
