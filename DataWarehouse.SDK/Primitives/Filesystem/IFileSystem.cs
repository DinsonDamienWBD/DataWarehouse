namespace DataWarehouse.SDK.Primitives.Filesystem;

/// <summary>
/// Provides a unified abstraction for filesystem operations across local and virtual filesystems.
/// </summary>
/// <remarks>
/// This interface enables storage-agnostic file operations, supporting both traditional filesystems
/// and virtual filesystem implementations (e.g., cloud storage, in-memory, distributed filesystems).
/// All operations are asynchronous and support cancellation for responsive applications.
/// </remarks>
public interface IFileSystem
{
    /// <summary>
    /// Reads the contents of a file asynchronously.
    /// </summary>
    /// <param name="path">The path to the file to read.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A stream containing the file contents.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when access to the file is denied.</exception>
    Task<Stream> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes data to a file asynchronously, creating or overwriting as needed.
    /// </summary>
    /// <param name="path">The path to the file to write.</param>
    /// <param name="content">The stream containing the data to write.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="UnauthorizedAccessException">Thrown when access to the file is denied.</exception>
    /// <exception cref="IOException">Thrown when an I/O error occurs during write.</exception>
    Task WriteAsync(string path, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file or directory asynchronously.
    /// </summary>
    /// <param name="path">The path to the file or directory to delete.</param>
    /// <param name="recursive">If true, deletes directories recursively. Default is false.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="FileNotFoundException">Thrown when the target does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when access is denied.</exception>
    Task DeleteAsync(string path, bool recursive = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a file or directory exists asynchronously.
    /// </summary>
    /// <param name="path">The path to check for existence.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if the path exists; otherwise, false.</returns>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all files and directories in the specified path asynchronously.
    /// </summary>
    /// <param name="path">The directory path to list.</param>
    /// <param name="pattern">Optional glob pattern for filtering (e.g., "*.txt"). Default is null (all entries).</param>
    /// <param name="recursive">If true, lists entries recursively. Default is false.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An enumerable of filesystem entries matching the criteria.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when the directory does not exist.</exception>
    IAsyncEnumerable<FileSystemEntry> ListAsync(
        string path,
        string? pattern = null,
        bool recursive = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves metadata for a file or directory asynchronously.
    /// </summary>
    /// <param name="path">The path to retrieve metadata for.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>Metadata information about the file or directory.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the path does not exist.</exception>
    Task<FileSystemEntry> GetMetadataAsync(string path, CancellationToken cancellationToken = default);
}
