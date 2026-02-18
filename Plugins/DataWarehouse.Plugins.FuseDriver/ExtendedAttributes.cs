// <copyright file="ExtendedAttributes.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// Manages extended attributes (xattr) for the FUSE filesystem.
/// Supports POSIX xattr namespaces: user, security, system, trusted.
/// </summary>
public sealed class ExtendedAttributes
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte[]>> _attributes = new();
    private readonly object _syncLock = new();

    /// <summary>
    /// The user namespace prefix for general-purpose extended attributes.
    /// </summary>
    public const string UserNamespace = "user.";

    /// <summary>
    /// The security namespace prefix for security-related attributes (SELinux, etc.).
    /// </summary>
    public const string SecurityNamespace = "security.";

    /// <summary>
    /// The system namespace prefix for system-managed attributes (ACLs, etc.).
    /// </summary>
    public const string SystemNamespace = "system.";

    /// <summary>
    /// The trusted namespace prefix for attributes only accessible by privileged processes.
    /// </summary>
    public const string TrustedNamespace = "trusted.";

    /// <summary>
    /// macOS-specific namespace for Finder info.
    /// </summary>
    public const string MacOsFinderNamespace = "com.apple.";

    /// <summary>
    /// Maximum size of an extended attribute value.
    /// </summary>
    public const int MaxValueSize = 65536; // 64 KB

    /// <summary>
    /// Maximum length of an extended attribute name.
    /// </summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// Gets the extended attribute for the specified path and name.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="name">The attribute name.</param>
    /// <returns>The attribute value, or null if not found.</returns>
    public byte[]? GetAttribute(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);

        if (!ValidateAttributeName(name, out _))
        {
            return null;
        }

        var normalizedPath = NormalizePath(path);

        if (_attributes.TryGetValue(normalizedPath, out var attrs) &&
            attrs.TryGetValue(name, out var value))
        {
            // Return a copy to prevent external modification
            return (byte[])value.Clone();
        }

        return null;
    }

    /// <summary>
    /// Gets the extended attribute as a string.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="encoding">The string encoding (defaults to UTF-8).</param>
    /// <returns>The attribute value as a string, or null if not found.</returns>
    public string? GetAttributeString(string path, string name, Encoding? encoding = null)
    {
        var value = GetAttribute(path, name);
        if (value == null)
            return null;

        encoding ??= Encoding.UTF8;
        return encoding.GetString(value);
    }

    /// <summary>
    /// Sets the extended attribute for the specified path.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">The attribute value.</param>
    /// <param name="flags">The operation flags.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int SetAttribute(string path, string name, byte[] value, XattrFlags flags = XattrFlags.None)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!ValidateAttributeName(name, out var errorCode))
        {
            return errorCode;
        }

        if (value.Length > MaxValueSize)
        {
            return -FuseErrno.E2BIG;
        }

        var normalizedPath = NormalizePath(path);

        lock (_syncLock)
        {
            var attrs = _attributes.GetOrAdd(normalizedPath, _ => new ConcurrentDictionary<string, byte[]>());
            var exists = attrs.ContainsKey(name);

            // Check flags
            if (flags == XattrFlags.Create && exists)
            {
                return -FuseErrno.EEXIST;
            }

            if (flags == XattrFlags.Replace && !exists)
            {
                return -FuseErrno.ENODATA;
            }

            // Store a copy to prevent external modification
            attrs[name] = (byte[])value.Clone();
        }

        return 0;
    }

    /// <summary>
    /// Sets the extended attribute as a string.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">The attribute value as a string.</param>
    /// <param name="flags">The operation flags.</param>
    /// <param name="encoding">The string encoding (defaults to UTF-8).</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int SetAttributeString(string path, string name, string value,
        XattrFlags flags = XattrFlags.None, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        return SetAttribute(path, name, encoding.GetBytes(value), flags);
    }

    /// <summary>
    /// Lists all extended attribute names for the specified path.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>A list of attribute names.</returns>
    public IReadOnlyList<string> ListAttributes(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalizedPath = NormalizePath(path);

        if (_attributes.TryGetValue(normalizedPath, out var attrs))
        {
            return attrs.Keys.ToList();
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Lists extended attribute names as a null-separated byte buffer.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>A byte buffer with null-separated attribute names.</returns>
    public byte[] ListAttributesBuffer(string path)
    {
        var names = ListAttributes(path);

        if (names.Count == 0)
            return Array.Empty<byte>();

        using var ms = new MemoryStream(4096);

        foreach (var name in names)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte(0); // Null terminator
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Removes the extended attribute from the specified path.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="name">The attribute name.</param>
    /// <returns>0 on success, or a negative errno value on failure.</returns>
    public int RemoveAttribute(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);

        if (!ValidateAttributeName(name, out _))
        {
            return -FuseErrno.ENODATA;
        }

        var normalizedPath = NormalizePath(path);

        if (_attributes.TryGetValue(normalizedPath, out var attrs))
        {
            if (attrs.TryRemove(name, out _))
            {
                // Clean up empty dictionary
                if (attrs.IsEmpty)
                {
                    _attributes.TryRemove(normalizedPath, out _);
                }

                return 0;
            }
        }

        return -FuseErrno.ENODATA;
    }

    /// <summary>
    /// Removes all extended attributes from the specified path.
    /// </summary>
    /// <param name="path">The file path.</param>
    public void RemoveAllAttributes(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalizedPath = NormalizePath(path);
        _attributes.TryRemove(normalizedPath, out _);
    }

    /// <summary>
    /// Copies all extended attributes from source to destination.
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="destPath">The destination file path.</param>
    public void CopyAttributes(string sourcePath, string destPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destPath);

        var normalizedSource = NormalizePath(sourcePath);
        var normalizedDest = NormalizePath(destPath);

        if (!_attributes.TryGetValue(normalizedSource, out var sourceAttrs))
        {
            return;
        }

        var destAttrs = _attributes.GetOrAdd(normalizedDest, _ => new ConcurrentDictionary<string, byte[]>());

        foreach (var kvp in sourceAttrs)
        {
            destAttrs[kvp.Key] = (byte[])kvp.Value.Clone();
        }
    }

    /// <summary>
    /// Renames a path's extended attributes.
    /// </summary>
    /// <param name="oldPath">The old file path.</param>
    /// <param name="newPath">The new file path.</param>
    public void RenamePath(string oldPath, string newPath)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);

        var normalizedOld = NormalizePath(oldPath);
        var normalizedNew = NormalizePath(newPath);

        if (_attributes.TryRemove(normalizedOld, out var attrs))
        {
            _attributes[normalizedNew] = attrs;
        }
    }

    /// <summary>
    /// Gets the namespace from an attribute name.
    /// </summary>
    /// <param name="name">The attribute name.</param>
    /// <returns>The namespace, or null if not a namespaced attribute.</returns>
    public static string? GetNamespace(string name)
    {
        if (name.StartsWith(UserNamespace, StringComparison.Ordinal))
            return UserNamespace;
        if (name.StartsWith(SecurityNamespace, StringComparison.Ordinal))
            return SecurityNamespace;
        if (name.StartsWith(SystemNamespace, StringComparison.Ordinal))
            return SystemNamespace;
        if (name.StartsWith(TrustedNamespace, StringComparison.Ordinal))
            return TrustedNamespace;
        if (name.StartsWith(MacOsFinderNamespace, StringComparison.Ordinal))
            return MacOsFinderNamespace;

        return null;
    }

    /// <summary>
    /// Checks if the attribute namespace requires elevated privileges.
    /// </summary>
    /// <param name="name">The attribute name.</param>
    /// <returns>True if the namespace requires root/admin privileges.</returns>
    public static bool RequiresPrivilege(string name)
    {
        var ns = GetNamespace(name);
        return ns == TrustedNamespace || ns == SecurityNamespace || ns == SystemNamespace;
    }

    /// <summary>
    /// Checks if the user has permission to access the attribute.
    /// </summary>
    /// <param name="name">The attribute name.</param>
    /// <param name="uid">The user ID.</param>
    /// <param name="write">True if checking write access.</param>
    /// <returns>True if access is permitted.</returns>
    public static bool CheckAccess(string name, uint uid, bool write)
    {
        // Root can access everything
        if (uid == 0)
            return true;

        var ns = GetNamespace(name);

        // User namespace is always accessible
        if (ns == UserNamespace || ns == null)
            return true;

        // macOS namespace is accessible for reading
        if (ns == MacOsFinderNamespace && !write)
            return true;

        // Privileged namespaces require root
        return false;
    }

    /// <summary>
    /// Gets the total size of all extended attributes for a path.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The total size in bytes.</returns>
    public long GetTotalSize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalizedPath = NormalizePath(path);

        if (_attributes.TryGetValue(normalizedPath, out var attrs))
        {
            long total = 0;
            foreach (var kvp in attrs)
            {
                total += Encoding.UTF8.GetByteCount(kvp.Key) + 1; // Name + null
                total += kvp.Value.Length;
            }

            return total;
        }

        return 0;
    }

    /// <summary>
    /// Gets all extended attributes for a path as a dictionary.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>A dictionary of attribute names and values.</returns>
    public IReadOnlyDictionary<string, byte[]> GetAllAttributes(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalizedPath = NormalizePath(path);

        if (_attributes.TryGetValue(normalizedPath, out var attrs))
        {
            // Return a copy
            return attrs.ToDictionary(kvp => kvp.Key, kvp => (byte[])kvp.Value.Clone());
        }

        return new Dictionary<string, byte[]>();
    }

    /// <summary>
    /// Sets multiple extended attributes at once.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="attributes">The attributes to set.</param>
    public void SetAllAttributes(string path, IReadOnlyDictionary<string, byte[]> attributes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(attributes);

        var normalizedPath = NormalizePath(path);

        lock (_syncLock)
        {
            var attrs = _attributes.GetOrAdd(normalizedPath, _ => new ConcurrentDictionary<string, byte[]>());

            foreach (var kvp in attributes)
            {
                if (ValidateAttributeName(kvp.Key, out _) && kvp.Value.Length <= MaxValueSize)
                {
                    attrs[kvp.Key] = (byte[])kvp.Value.Clone();
                }
            }
        }
    }

    private static string NormalizePath(string path)
    {
        // Normalize path separators and remove trailing slashes
        var normalized = path.Replace('\\', '/');
        while (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    private static bool ValidateAttributeName(string name, out int errorCode)
    {
        errorCode = 0;

        if (string.IsNullOrEmpty(name))
        {
            errorCode = -FuseErrno.EINVAL;
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            errorCode = -FuseErrno.ENAMETOOLONG;
            return false;
        }

        // Check for null bytes
        if (name.Contains('\0'))
        {
            errorCode = -FuseErrno.EINVAL;
            return false;
        }

        return true;
    }
}

/// <summary>
/// Flags for extended attribute operations.
/// </summary>
[Flags]
public enum XattrFlags
{
    /// <summary>
    /// No flags (create or replace).
    /// </summary>
    None = 0,

    /// <summary>
    /// Create only - fail if attribute exists (XATTR_CREATE).
    /// </summary>
    Create = 1,

    /// <summary>
    /// Replace only - fail if attribute doesn't exist (XATTR_REPLACE).
    /// </summary>
    Replace = 2
}

/// <summary>
/// FUSE error numbers.
/// </summary>
public static class FuseErrno
{
    /// <summary>
    /// Operation not permitted.
    /// </summary>
    public const int EPERM = 1;

    /// <summary>
    /// No such file or directory.
    /// </summary>
    public const int ENOENT = 2;

    /// <summary>
    /// I/O error.
    /// </summary>
    public const int EIO = 5;

    /// <summary>
    /// No such device or address.
    /// </summary>
    public const int ENXIO = 6;

    /// <summary>
    /// Bad file descriptor.
    /// </summary>
    public const int EBADF = 9;

    /// <summary>
    /// Out of memory.
    /// </summary>
    public const int ENOMEM = 12;

    /// <summary>
    /// Permission denied.
    /// </summary>
    public const int EACCES = 13;

    /// <summary>
    /// Bad address.
    /// </summary>
    public const int EFAULT = 14;

    /// <summary>
    /// Resource busy.
    /// </summary>
    public const int EBUSY = 16;

    /// <summary>
    /// File exists.
    /// </summary>
    public const int EEXIST = 17;

    /// <summary>
    /// Cross-device link.
    /// </summary>
    public const int EXDEV = 18;

    /// <summary>
    /// Not a directory.
    /// </summary>
    public const int ENOTDIR = 20;

    /// <summary>
    /// Is a directory.
    /// </summary>
    public const int EISDIR = 21;

    /// <summary>
    /// Invalid argument.
    /// </summary>
    public const int EINVAL = 22;

    /// <summary>
    /// File table overflow.
    /// </summary>
    public const int ENFILE = 23;

    /// <summary>
    /// Too many open files.
    /// </summary>
    public const int EMFILE = 24;

    /// <summary>
    /// No space left on device.
    /// </summary>
    public const int ENOSPC = 28;

    /// <summary>
    /// Read-only file system.
    /// </summary>
    public const int EROFS = 30;

    /// <summary>
    /// Too many links.
    /// </summary>
    public const int EMLINK = 31;

    /// <summary>
    /// File name too long.
    /// </summary>
    public const int ENAMETOOLONG = 36;

    /// <summary>
    /// Directory not empty.
    /// </summary>
    public const int ENOTEMPTY = 39;

    /// <summary>
    /// Function not implemented.
    /// </summary>
    public const int ENOSYS = 38;

    /// <summary>
    /// No data available (for xattr).
    /// </summary>
    public const int ENODATA = 61;

    /// <summary>
    /// Argument list too long.
    /// </summary>
    public const int E2BIG = 7;

    /// <summary>
    /// Value too large for defined data type.
    /// </summary>
    public const int EOVERFLOW = 75;

    /// <summary>
    /// Result too large / buffer too small.
    /// </summary>
    public const int ERANGE = 34;

    /// <summary>
    /// Operation not supported.
    /// </summary>
    public const int ENOTSUP = 95;

    /// <summary>
    /// Connection refused.
    /// </summary>
    public const int ECONNREFUSED = 111;

    /// <summary>
    /// Operation would block.
    /// </summary>
    public const int EWOULDBLOCK = 11;

    /// <summary>
    /// Resource temporarily unavailable.
    /// </summary>
    public const int EAGAIN = EWOULDBLOCK;

    /// <summary>
    /// Interrupted system call.
    /// </summary>
    public const int EINTR = 4;

    /// <summary>
    /// No locks available.
    /// </summary>
    public const int ENOLCK = 37;

    /// <summary>
    /// Deadlock would result.
    /// </summary>
    public const int EDEADLK = 35;

    /// <summary>
    /// Stale file handle.
    /// </summary>
    public const int ESTALE = 116;

    /// <summary>
    /// Converts a .NET exception to an appropriate errno value.
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns>A negative errno value.</returns>
    public static int FromException(Exception ex)
    {
        return ex switch
        {
            FileNotFoundException => -ENOENT,
            DirectoryNotFoundException => -ENOENT,
            PathTooLongException => -ENAMETOOLONG,
            UnauthorizedAccessException => -EACCES,
            IOException ioex when ioex.HResult == unchecked((int)0x80070070) => -ENOSPC, // Disk full
            IOException ioex when ioex.HResult == unchecked((int)0x80070020) => -EBUSY, // Sharing violation
            IOException => -EIO,
            NotSupportedException => -ENOTSUP,
            NotImplementedException => -ENOSYS,
            ArgumentNullException => -EINVAL,
            ArgumentOutOfRangeException => -EINVAL,
            ArgumentException => -EINVAL,
            OutOfMemoryException => -ENOMEM,
            _ => -EIO
        };
    }
}
