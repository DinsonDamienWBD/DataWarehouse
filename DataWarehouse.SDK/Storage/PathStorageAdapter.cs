using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Storage;

/// <summary>
/// Translates URI-based storage operations (<see cref="IStorageProvider"/>) to
/// key-based object operations (<see cref="IObjectStorageCore"/>) per AD-04.
/// </summary>
/// <remarks>
/// <para>URI to key translation rules:</para>
/// <list type="bullet">
///   <item><c>scheme://host/path/to/file.ext</c> becomes <c>path/to/file.ext</c></item>
///   <item><c>file:///C:/data/file.ext</c> becomes <c>data/file.ext</c> (drive letter stripped)</item>
///   <item>Backslashes are normalized to forward slashes</item>
///   <item>Path traversal sequences (../, ..\) are rejected</item>
/// </list>
/// </remarks>
public sealed class PathStorageAdapter : IListableStorage
{
    private readonly IObjectStorageCore _core;
    private readonly string _scheme;

    /// <summary>Creates a new PathStorageAdapter wrapping the given object storage core.</summary>
    public PathStorageAdapter(IObjectStorageCore core, string scheme)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _scheme = scheme ?? throw new ArgumentNullException(nameof(scheme));
    }

    /// <summary>Gets the URI scheme this adapter handles.</summary>
    public string Scheme => _scheme;

    /// <summary>Saves data to a URI location.</summary>
    public async Task SaveAsync(Uri uri, Stream data)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(data);
        var key = UriToKey(uri);
        await _core.StoreAsync(key, data);
    }

    /// <summary>Loads data from a URI location.</summary>
    public async Task<Stream> LoadAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var key = UriToKey(uri);
        return await _core.RetrieveAsync(key);
    }

    /// <summary>Deletes data at a URI location.</summary>
    public async Task DeleteAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var key = UriToKey(uri);
        await _core.DeleteAsync(key);
    }

    /// <summary>Checks if data exists at a URI location.</summary>
    public async Task<bool> ExistsAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var key = UriToKey(uri);
        return await _core.ExistsAsync(key);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StorageListItem> ListFilesAsync(
        string prefix = "",
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var keyPrefix = string.IsNullOrEmpty(prefix) ? null : NormalizePath(prefix);

        await foreach (var metadata in _core.ListAsync(keyPrefix, ct))
        {
            yield return new StorageListItem(
                KeyToUri(metadata.Key),
                metadata.Size);
        }
    }

    #region StorageAddress Support (HAL-05)

    /// <summary>
    /// Converts a StorageAddress to a normalized file path.
    /// For FilePathAddress: returns the normalized path directly.
    /// For other variants: uses ToKey() and applies key-to-path translation.
    /// </summary>
    public static string ToNormalizedPath(StorageAddress address)
    {
        return address switch
        {
            FilePathAddress fp => NormalizePath(fp.Path),
            _ => NormalizePath(address.ToKey())
        };
    }

    /// <summary>
    /// Creates a StorageAddress from a file system path.
    /// Convenience method for callers transitioning from string paths to StorageAddress.
    /// </summary>
    public static StorageAddress FromPath(string path) => StorageAddress.FromFilePath(path);

    #endregion

    #region URI/Key Translation

    /// <summary>Converts a URI to a storage key.</summary>
    internal static string UriToKey(Uri uri)
    {
        string path;
        if (uri.IsAbsoluteUri)
        {
            path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        }
        else
        {
            path = uri.OriginalString;
        }
        return NormalizePath(path);
    }

    /// <summary>Converts a storage key back to a URI with the adapter's scheme.</summary>
    public Uri KeyToUri(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new Uri($"{_scheme}://storage/{key}");
    }

    /// <summary>Normalizes a path string into a valid storage key.</summary>
    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/');

        if (normalized.Contains("../") || normalized.Contains("/..") || normalized == "..")
        {
            throw new ArgumentException(
                $"Path traversal detected in storage path: '{path}'",
                nameof(path));
        }

        normalized = normalized.TrimStart('/');

        if (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        {
            normalized = normalized.Substring(2).TrimStart('/');
        }

        if (normalized.StartsWith("./"))
        {
            normalized = normalized.Substring(2);
        }

        return normalized;
    }

    #endregion
}
