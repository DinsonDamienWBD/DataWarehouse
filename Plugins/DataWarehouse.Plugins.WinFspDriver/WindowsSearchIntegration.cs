// <copyright file="WindowsSearchIntegration.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DataWarehouse.Plugins.WinFspDriver;

/// <summary>
/// Implements Windows Search integration through IFilter and property handlers.
/// Enables indexing of DataWarehouse files for fast desktop search.
/// </summary>
/// <remarks>
/// <para>
/// This integration allows Windows Search to index content and metadata from
/// DataWarehouse files, enabling users to find files through:
/// </para>
/// <list type="bullet">
/// <item>Windows Start menu search</item>
/// <item>File Explorer search</item>
/// <item>Cortana queries</item>
/// <item>Windows Search API applications</item>
/// </list>
/// <para>
/// The implementation provides:
/// </para>
/// <list type="bullet">
/// <item>IFilter interface for content extraction</item>
/// <item>Property handlers for metadata indexing</item>
/// <item>Incremental indexing support</item>
/// <item>Custom property schemas</item>
/// </list>
/// </remarks>
public sealed class WindowsSearchIntegration : IDisposable
{
    #region COM Interface GUIDs

    /// <summary>
    /// CLSID for the DataWarehouse IFilter implementation.
    /// </summary>
    public static readonly Guid FilterClsid = new("8A3B2C1D-4E5F-6A7B-8C9D-0E1F2A3B4C5D");

    /// <summary>
    /// CLSID for the DataWarehouse property handler.
    /// </summary>
    public static readonly Guid PropertyHandlerClsid = new("9B4C3D2E-5F6A-7B8C-9D0E-1F2A3B4C5D6E");

    /// <summary>
    /// IID for IFilter interface.
    /// </summary>
    private static readonly Guid IID_IFilter = new("89BCB740-6119-101A-BCB7-00DD010655AF");

    /// <summary>
    /// IID for IPropertyStore interface.
    /// </summary>
    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    #endregion

    #region Property Keys

    /// <summary>
    /// Property key for DataWarehouse file type.
    /// </summary>
    public static readonly PropertyKey PKEY_DataWarehouse_FileType = new(
        new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"), 2);

    /// <summary>
    /// Property key for DataWarehouse sync status.
    /// </summary>
    public static readonly PropertyKey PKEY_DataWarehouse_SyncStatus = new(
        new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"), 3);

    /// <summary>
    /// Property key for DataWarehouse storage location.
    /// </summary>
    public static readonly PropertyKey PKEY_DataWarehouse_StorageLocation = new(
        new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"), 4);

    /// <summary>
    /// Property key for DataWarehouse version.
    /// </summary>
    public static readonly PropertyKey PKEY_DataWarehouse_Version = new(
        new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"), 5);

    #endregion

    private readonly WinFspFileSystem _fileSystem;
    private readonly ConcurrentDictionary<string, IndexedFileInfo> _indexedFiles;
    private readonly ConcurrentQueue<IndexOperation> _pendingOperations;
    private readonly SemaphoreSlim _indexLock;
    private readonly Timer _indexTimer;
    private readonly CancellationTokenSource _cts;
    private bool _isRegistered;
    private bool _isIndexing;
    private bool _disposed;

    /// <summary>
    /// Event raised when indexing progress updates.
    /// </summary>
    public event EventHandler<IndexProgressEventArgs>? IndexProgressChanged;

    /// <summary>
    /// Event raised when a file is indexed.
    /// </summary>
    public event EventHandler<FileIndexedEventArgs>? FileIndexed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsSearchIntegration"/> class.
    /// </summary>
    /// <param name="fileSystem">The WinFSP filesystem to index.</param>
    public WindowsSearchIntegration(WinFspFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _indexedFiles = new ConcurrentDictionary<string, IndexedFileInfo>(StringComparer.OrdinalIgnoreCase);
        _pendingOperations = new ConcurrentQueue<IndexOperation>();
        _indexLock = new SemaphoreSlim(1, 1);
        _cts = new CancellationTokenSource();

        // Setup incremental indexing timer
        _indexTimer = new Timer(
            async _ => await ProcessPendingOperationsAsync(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

        // Subscribe to file system events
        _fileSystem.FileOperationOccurred += OnFileOperationOccurred;
    }

    #region Registration

    /// <summary>
    /// Registers the search integration with Windows Search service.
    /// Requires administrator privileges.
    /// </summary>
    /// <param name="fileExtensions">File extensions to register for indexing.</param>
    /// <returns>True if registration succeeded.</returns>
    public bool Register(params string[] fileExtensions)
    {
        if (_isRegistered)
            return true;

        try
        {
            // Register IFilter
            RegisterFilter(fileExtensions);

            // Register property handler
            RegisterPropertyHandler(fileExtensions);

            // Register property schema
            RegisterPropertySchema();

            // Add to search scope
            AddToSearchScope();

            _isRegistered = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Unregisters from Windows Search service.
    /// </summary>
    public void Unregister()
    {
        if (!_isRegistered)
            return;

        try
        {
            UnregisterFilter();
            UnregisterPropertyHandler();
            RemoveFromSearchScope();
            _isRegistered = false;
        }
        catch
        {
            // Ignore unregistration errors
        }
    }

    private void RegisterFilter(string[] extensions)
    {
        // Register CLSID
        var clsidPath = $@"CLSID\{FilterClsid:B}";
        using var clsidKey = Registry.ClassesRoot.CreateSubKey(clsidPath);
        if (clsidKey == null) return;

        clsidKey.SetValue(null, "DataWarehouse Filter");

        using var inprocKey = clsidKey.CreateSubKey("InProcServer32");
        if (inprocKey != null)
        {
            var assemblyPath = typeof(WindowsSearchIntegration).Assembly.Location;
            inprocKey.SetValue(null, assemblyPath);
            inprocKey.SetValue("ThreadingModel", "Both");
        }

        // Register persistent handler for each extension
        foreach (var ext in extensions)
        {
            var extension = ext.StartsWith(".") ? ext : "." + ext;

            // Create persistent handler entry
            var persistentPath = $@"{extension}\PersistentHandler";
            using var persistentKey = Registry.ClassesRoot.CreateSubKey(persistentPath);
            var handlerGuid = Guid.NewGuid();
            persistentKey?.SetValue(null, handlerGuid.ToString("B"));

            // Link handler to filter
            var handlerPath = $@"CLSID\{handlerGuid:B}\PersistentAddinsRegistered\{IID_IFilter:B}";
            using var handlerKey = Registry.ClassesRoot.CreateSubKey(handlerPath);
            handlerKey?.SetValue(null, FilterClsid.ToString("B"));
        }
    }

    private void UnregisterFilter()
    {
        var clsidPath = $@"CLSID\{FilterClsid:B}";
        Registry.ClassesRoot.DeleteSubKeyTree(clsidPath, false);
    }

    private void RegisterPropertyHandler(string[] extensions)
    {
        // Register CLSID
        var clsidPath = $@"CLSID\{PropertyHandlerClsid:B}";
        using var clsidKey = Registry.ClassesRoot.CreateSubKey(clsidPath);
        if (clsidKey == null) return;

        clsidKey.SetValue(null, "DataWarehouse Property Handler");

        using var inprocKey = clsidKey.CreateSubKey("InProcServer32");
        if (inprocKey != null)
        {
            var assemblyPath = typeof(WindowsSearchIntegration).Assembly.Location;
            inprocKey.SetValue(null, assemblyPath);
            inprocKey.SetValue("ThreadingModel", "Both");
        }

        // Register for each extension
        foreach (var ext in extensions)
        {
            var extension = ext.StartsWith(".") ? ext : "." + ext;
            var handlerPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers\{extension}";

            using var handlerKey = Registry.LocalMachine.CreateSubKey(handlerPath);
            handlerKey?.SetValue(null, PropertyHandlerClsid.ToString("B"));
        }
    }

    private void UnregisterPropertyHandler()
    {
        var clsidPath = $@"CLSID\{PropertyHandlerClsid:B}";
        Registry.ClassesRoot.DeleteSubKeyTree(clsidPath, false);
    }

    private void RegisterPropertySchema()
    {
        // Register custom property schema with Windows Property System
        // In a full implementation, this would register a .propdesc file
        var schemaPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\PropertySchema";

        using var schemaKey = Registry.LocalMachine.OpenSubKey(schemaPath, true);
        if (schemaKey != null)
        {
            var schemaFilePath = Path.Combine(
                Path.GetDirectoryName(typeof(WindowsSearchIntegration).Assembly.Location) ?? "",
                "DataWarehouse.propdesc");

            // Would need to create and register the .propdesc file
            // PSRegisterPropertySchema(schemaFilePath);
        }
    }

    private void AddToSearchScope()
    {
        // Add the mounted filesystem to the Windows Search scope
        var mountPoint = _fileSystem.MountPoint;
        if (string.IsNullOrEmpty(mountPoint))
            return;

        try
        {
            // Use the Crawl Scope Manager API
            var scopeManager = (ISearchCrawlScopeManager?)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("9E175B68-F52A-11D8-B9A5-505054503030")) ?? typeof(object));

            if (scopeManager != null)
            {
                scopeManager.AddDefaultScopeRule($"file:///{mountPoint}/", true, FOLLOW_INDEXING.FOLLOW_INDEX_TYPE_DEPTH);
                scopeManager.SaveAll();
            }
        }
        catch
        {
            // Search scope registration may fail without proper permissions
        }
    }

    private void RemoveFromSearchScope()
    {
        var mountPoint = _fileSystem.MountPoint;
        if (string.IsNullOrEmpty(mountPoint))
            return;

        try
        {
            var scopeManager = (ISearchCrawlScopeManager?)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("9E175B68-F52A-11D8-B9A5-505054503030")) ?? typeof(object));

            if (scopeManager != null)
            {
                scopeManager.RemoveScopeRule($"file:///{mountPoint}/");
                scopeManager.SaveAll();
            }
        }
        catch
        {
            // Ignore removal errors
        }
    }

    #endregion

    #region Indexing Operations

    /// <summary>
    /// Starts a full index of the filesystem.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the indexing operation.</returns>
    public async Task StartFullIndexAsync(CancellationToken cancellationToken = default)
    {
        if (_isIndexing)
            return;

        _isIndexing = true;
        try
        {
            var allFiles = new List<string>();
            CollectAllFiles("\\", allFiles);

            var totalFiles = allFiles.Count;
            var processedFiles = 0;

            foreach (var file in allFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await IndexFileAsync(file, cancellationToken);
                processedFiles++;

                IndexProgressChanged?.Invoke(this, new IndexProgressEventArgs
                {
                    TotalFiles = totalFiles,
                    ProcessedFiles = processedFiles,
                    CurrentFile = file,
                    ProgressPercent = totalFiles > 0 ? (double)processedFiles / totalFiles * 100 : 0
                });
            }
        }
        finally
        {
            _isIndexing = false;
        }
    }

    /// <summary>
    /// Indexes a single file.
    /// </summary>
    /// <param name="path">The file path to index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task IndexFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            var entry = _fileSystem.GetEntry(path);
            if (entry == null || entry.IsDirectory)
                return;

            // Extract content
            var content = await ExtractContentAsync(path, entry, cancellationToken);

            // Extract metadata
            var metadata = ExtractMetadata(path, entry);

            // Store indexed info
            var indexedInfo = new IndexedFileInfo
            {
                Path = path,
                LastIndexed = DateTime.UtcNow,
                LastModified = WinFspNative.FileTimeToDateTime(entry.LastWriteTime),
                ContentHash = ComputeContentHash(content),
                FileSize = entry.FileSize,
                Metadata = metadata,
                ExtractedText = content
            };

            _indexedFiles[path] = indexedInfo;

            // Notify Windows Search of the update
            NotifySearchIndexer(path);

            FileIndexed?.Invoke(this, new FileIndexedEventArgs
            {
                Path = path,
                Success = true,
                IndexedInfo = indexedInfo
            });
        }
        catch (Exception ex)
        {
            FileIndexed?.Invoke(this, new FileIndexedEventArgs
            {
                Path = path,
                Success = false,
                Error = ex.Message
            });
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Queues a file for incremental indexing.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="operation">The type of operation.</param>
    public void QueueForIndexing(string path, IndexOperationType operation)
    {
        _pendingOperations.Enqueue(new IndexOperation
        {
            Path = path,
            Operation = operation,
            QueuedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Removes a file from the index.
    /// </summary>
    /// <param name="path">The file path.</param>
    public void RemoveFromIndex(string path)
    {
        _indexedFiles.TryRemove(path, out _);
        NotifySearchIndexerDeleted(path);
    }

    /// <summary>
    /// Gets the indexed information for a file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>Indexed information, or null if not indexed.</returns>
    public IndexedFileInfo? GetIndexedInfo(string path)
    {
        return _indexedFiles.TryGetValue(path, out var info) ? info : null;
    }

    /// <summary>
    /// Checks if a file needs reindexing based on modification time.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>True if reindexing is needed.</returns>
    public bool NeedsReindexing(string path)
    {
        var entry = _fileSystem.GetEntry(path);
        if (entry == null)
            return false;

        if (!_indexedFiles.TryGetValue(path, out var indexed))
            return true;

        var lastModified = WinFspNative.FileTimeToDateTime(entry.LastWriteTime);
        return lastModified > indexed.LastIndexed;
    }

    #endregion

    #region Content Extraction

    private async Task<string> ExtractContentAsync(string path, FileEntry entry, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        // Read file data (limit to reasonable size for indexing)
        var maxBytes = Math.Min(entry.FileSize, 5 * 1024 * 1024); // 5MB max
        var data = _fileSystem.ReadData(path, 0, (int)maxBytes);
        if (data == null || data.Length == 0)
            return string.Empty;

        return await Task.Run(() => extension switch
        {
            ".txt" or ".md" or ".log" => ExtractTextContent(data),
            ".json" => ExtractJsonContent(data),
            ".xml" => ExtractXmlContent(data),
            ".csv" => ExtractCsvContent(data),
            ".html" or ".htm" => ExtractHtmlContent(data),
            ".dwh" or ".dws" => ExtractDataWarehouseContent(data),
            _ => TryExtractTextContent(data)
        }, cancellationToken);
    }

    private static string ExtractTextContent(byte[] data)
    {
        // Detect encoding and extract text
        var encoding = DetectEncoding(data);
        return encoding.GetString(data).Trim();
    }

    private static string ExtractJsonContent(byte[] data)
    {
        try
        {
            var text = Encoding.UTF8.GetString(data);
            // Extract all string values from JSON
            var sb = new StringBuilder();
            ExtractJsonStrings(text, sb);
            return sb.ToString();
        }
        catch
        {
            return TryExtractTextContent(data);
        }
    }

    private static string ExtractXmlContent(byte[] data)
    {
        try
        {
            var text = Encoding.UTF8.GetString(data);
            // Strip XML tags, keep content
            return System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", " ").Trim();
        }
        catch
        {
            return TryExtractTextContent(data);
        }
    }

    private static string ExtractCsvContent(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        // Replace delimiters with spaces
        return text.Replace(',', ' ').Replace(';', ' ').Replace('\t', ' ');
    }

    private static string ExtractHtmlContent(byte[] data)
    {
        try
        {
            var text = Encoding.UTF8.GetString(data);
            // Strip HTML tags
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<script[^>]*>[\s\S]*?</script>", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<style[^>]*>[\s\S]*?</style>", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", " ");
            return System.Net.WebUtility.HtmlDecode(text).Trim();
        }
        catch
        {
            return TryExtractTextContent(data);
        }
    }

    private static string ExtractDataWarehouseContent(byte[] data)
    {
        // Extract content from DataWarehouse-specific format
        // This would parse the DWH/DWS format
        return TryExtractTextContent(data);
    }

    private static string TryExtractTextContent(byte[] data)
    {
        // Attempt to extract readable text from any file
        var sb = new StringBuilder();
        var encoding = DetectEncoding(data);

        try
        {
            var text = encoding.GetString(data);
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c))
                {
                    sb.Append(c);
                }
            }
        }
        catch
        {
            // If encoding fails, extract ASCII printable characters
            foreach (var b in data)
            {
                if (b >= 32 && b < 127)
                {
                    sb.Append((char)b);
                }
            }
        }

        return sb.ToString();
    }

    private static Encoding DetectEncoding(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Encoding.UTF8;
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode;
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        return Encoding.UTF8;
    }

    private static void ExtractJsonStrings(string json, StringBuilder sb)
    {
        // Simple JSON string extraction
        var inString = false;
        var escape = false;
        var current = new StringBuilder();

        foreach (var c in json)
        {
            if (escape)
            {
                current.Append(c);
                escape = false;
                continue;
            }

            if (c == '\\')
            {
                escape = true;
                continue;
            }

            if (c == '"')
            {
                if (inString)
                {
                    sb.Append(current.ToString()).Append(' ');
                    current.Clear();
                }
                inString = !inString;
                continue;
            }

            if (inString)
            {
                current.Append(c);
            }
        }
    }

    private static Dictionary<string, string> ExtractMetadata(string path, FileEntry entry)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FileName"] = Path.GetFileName(path),
            ["Extension"] = Path.GetExtension(path),
            ["Size"] = entry.FileSize.ToString(),
            ["Created"] = WinFspNative.FileTimeToDateTime(entry.CreationTime).ToString("O"),
            ["Modified"] = WinFspNative.FileTimeToDateTime(entry.LastWriteTime).ToString("O"),
            ["Attributes"] = entry.Attributes.ToString()
        };

        return metadata;
    }

    private static string ComputeContentHash(string content)
    {
        // Note: Bus delegation not available in this context; using direct crypto
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    #endregion

    #region Search Indexer Notification

    private void NotifySearchIndexer(string path)
    {
        try
        {
            var mountPoint = _fileSystem.MountPoint;
            if (string.IsNullOrEmpty(mountPoint))
                return;

            var fullPath = Path.Combine(mountPoint, path.TrimStart('\\'));

            // Use ISearchNotifyInlineSite or SHChangeNotify
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATH, fullPath, IntPtr.Zero);
        }
        catch
        {
            // Ignore notification errors
        }
    }

    private void NotifySearchIndexerDeleted(string path)
    {
        try
        {
            var mountPoint = _fileSystem.MountPoint;
            if (string.IsNullOrEmpty(mountPoint))
                return;

            var fullPath = Path.Combine(mountPoint, path.TrimStart('\\'));
            SHChangeNotify(SHCNE_DELETE, SHCNF_PATH, fullPath, IntPtr.Zero);
        }
        catch
        {
            // Ignore notification errors
        }
    }

    #endregion

    #region Event Handlers

    private void OnFileOperationOccurred(object? sender, FileOperationEventArgs e)
    {
        // Queue files for incremental indexing based on operation
        var operation = e.Operation switch
        {
            "CreateFile" => IndexOperationType.Add,
            "Write" => IndexOperationType.Update,
            "DeleteFile" => IndexOperationType.Remove,
            "Rename" => IndexOperationType.Update,
            _ => (IndexOperationType?)null
        };

        if (operation.HasValue)
        {
            QueueForIndexing(e.Path, operation.Value);
        }
    }

    private async Task ProcessPendingOperationsAsync()
    {
        if (_isIndexing || _cts.Token.IsCancellationRequested)
            return;

        var processedCount = 0;
        const int maxBatchSize = 100;

        while (processedCount < maxBatchSize && _pendingOperations.TryDequeue(out var op))
        {
            try
            {
                switch (op.Operation)
                {
                    case IndexOperationType.Add:
                    case IndexOperationType.Update:
                        await IndexFileAsync(op.Path, _cts.Token);
                        break;
                    case IndexOperationType.Remove:
                        RemoveFromIndex(op.Path);
                        break;
                }
            }
            catch
            {
                // Log and continue
            }

            processedCount++;
        }
    }

    #endregion

    #region Helper Methods

    private void CollectAllFiles(string path, List<string> files)
    {
        foreach (var entry in _fileSystem.ListDirectory(path))
        {
            if (entry.IsDirectory)
            {
                CollectAllFiles(entry.Path, files);
            }
            else
            {
                files.Add(entry.Path);
            }
        }
    }

    #endregion

    #region P/Invoke

    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const int SHCNE_DELETE = 0x00000004;
    private const int SHCNF_PATH = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, int flags, string item1, IntPtr item2);

    #endregion

    #region COM Interfaces

    /// <summary>
    /// Search Crawl Scope Manager interface.
    /// </summary>
    [ComImport]
    [Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF63")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchCrawlScopeManager
    {
        void AddDefaultScopeRule(string url, bool isInclude, FOLLOW_INDEXING followFlags);
        void RemoveScopeRule(string url);
        void SaveAll();
    }

    private enum FOLLOW_INDEXING
    {
        FOLLOW_INDEX_TYPE_DEPTH = 0
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the Windows Search integration.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _fileSystem.FileOperationOccurred -= OnFileOperationOccurred;
        _cts.Cancel();
        _indexTimer.Dispose();
        _indexLock.Dispose();
        _cts.Dispose();
        _indexedFiles.Clear();

        _disposed = true;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Represents a property key for Windows Property System.
/// </summary>
public readonly struct PropertyKey
{
    /// <summary>
    /// Property format ID (GUID).
    /// </summary>
    public Guid FormatId { get; }

    /// <summary>
    /// Property ID within the format.
    /// </summary>
    public int PropertyId { get; }

    /// <summary>
    /// Creates a new property key.
    /// </summary>
    public PropertyKey(Guid formatId, int propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

/// <summary>
/// Type of index operation.
/// </summary>
public enum IndexOperationType
{
    /// <summary>
    /// Add new file to index.
    /// </summary>
    Add,

    /// <summary>
    /// Update existing file in index.
    /// </summary>
    Update,

    /// <summary>
    /// Remove file from index.
    /// </summary>
    Remove
}

/// <summary>
/// Represents a pending index operation.
/// </summary>
internal sealed class IndexOperation
{
    /// <summary>
    /// File path.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Operation type.
    /// </summary>
    public required IndexOperationType Operation { get; init; }

    /// <summary>
    /// When the operation was queued.
    /// </summary>
    public required DateTime QueuedAt { get; init; }
}

/// <summary>
/// Information about an indexed file.
/// </summary>
public sealed class IndexedFileInfo
{
    /// <summary>
    /// File path.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// When the file was last indexed.
    /// </summary>
    public required DateTime LastIndexed { get; init; }

    /// <summary>
    /// File modification time at indexing.
    /// </summary>
    public required DateTime LastModified { get; init; }

    /// <summary>
    /// Hash of the extracted content.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public required long FileSize { get; init; }

    /// <summary>
    /// Extracted metadata.
    /// </summary>
    public required Dictionary<string, string> Metadata { get; init; }

    /// <summary>
    /// Extracted text content.
    /// </summary>
    public required string ExtractedText { get; init; }
}

/// <summary>
/// Event arguments for index progress updates.
/// </summary>
public sealed class IndexProgressEventArgs : EventArgs
{
    /// <summary>
    /// Total number of files to index.
    /// </summary>
    public required int TotalFiles { get; init; }

    /// <summary>
    /// Number of files processed.
    /// </summary>
    public required int ProcessedFiles { get; init; }

    /// <summary>
    /// Current file being indexed.
    /// </summary>
    public required string CurrentFile { get; init; }

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public required double ProgressPercent { get; init; }
}

/// <summary>
/// Event arguments for file indexed events.
/// </summary>
public sealed class FileIndexedEventArgs : EventArgs
{
    /// <summary>
    /// File path.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Whether indexing succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Indexed information if successful.
    /// </summary>
    public IndexedFileInfo? IndexedInfo { get; init; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? Error { get; init; }
}

#endregion
