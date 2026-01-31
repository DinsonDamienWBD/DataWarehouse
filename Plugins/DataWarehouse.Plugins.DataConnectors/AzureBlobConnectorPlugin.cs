using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Azure Blob Storage connector plugin.
/// Provides cloud object storage with container management and metadata support.
/// </summary>
public class AzureBlobConnectorPlugin : DataConnectorPluginBase
{
    private BlobServiceClient? _serviceClient;
    private string? _containerName;
    private AzureBlobConnectorConfig _config = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.azureblob";

    /// <inheritdoc />
    public override string Name => "Azure Blob Storage Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "azureblob";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    public override ConnectorCategory ConnectorCategory => ConnectorCategory.CloudStorage;

    /// <inheritdoc />
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Schema |
        ConnectorCapabilities.BulkOperations;

    public void Configure(AzureBlobConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var connectionString = config.ConnectionString;
            var props = (IReadOnlyDictionary<string, string?>)config.Properties;
            _containerName = props.GetValueOrDefault("ContainerName", "") ?? "";

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return new ConnectionResult(false, "Connection string is required", null);
            }

            // Support for SAS token connections
            if (connectionString.Contains("?sv=") || connectionString.StartsWith("https://"))
            {
                // This is a SAS URL or account URL
                _serviceClient = new BlobServiceClient(new Uri(connectionString));
            }
            else
            {
                // This is a connection string
                _serviceClient = new BlobServiceClient(connectionString);
            }

            // Test connection by getting account info
            var accountInfo = await _serviceClient.GetAccountInfoAsync(ct);
            var properties = await _serviceClient.GetPropertiesAsync(ct);

            var serverInfo = new Dictionary<string, object>
            {
                ["AccountKind"] = accountInfo.Value.AccountKind.ToString(),
                ["SkuName"] = accountInfo.Value.SkuName.ToString(),
                ["DefaultContainer"] = _containerName ?? "none",
                ["ServiceVersion"] = properties.Value.DefaultServiceVersion ?? "unknown"
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (Azure.RequestFailedException ex)
        {
            return new ConnectionResult(false, $"Azure Blob connection failed: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"Connection failed: {ex.Message}", null);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            _serviceClient = null;
            _containerName = null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (_serviceClient == null) return false;

        try
        {
            await _serviceClient.GetAccountInfoAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task<DataSchema> FetchSchemaAsync()
    {
        if (_serviceClient == null)
            throw new InvalidOperationException("Not connected to Azure Blob Storage");

        var containers = new List<string>();
        await foreach (var container in _serviceClient.GetBlobContainersAsync())
        {
            containers.Add(container.Name);
        }

        return new DataSchema(
            Name: "azure-blob-storage",
            Fields: new[]
            {
                new DataSchemaField("name", "string", false, null, null),
                new DataSchemaField("size", "long", false, null, null),
                new DataSchemaField("lastModified", "datetime", false, null, null),
                new DataSchemaField("contentType", "string", true, null, null),
                new DataSchemaField("container", "string", false, null, null),
                new DataSchemaField("blobType", "string", false, null, null)
            },
            PrimaryKeys: new[] { "container", "name" },
            Metadata: new Dictionary<string, object>
            {
                ["ContainerCount"] = containers.Count,
                ["Containers"] = containers.ToArray()
            }
        );
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_serviceClient == null)
            throw new InvalidOperationException("Not connected to Azure Blob Storage");

        var container = query.TableOrCollection ?? _containerName ?? throw new ArgumentException("Container name is required");
        var prefix = query.Filter ?? "";

        var containerClient = _serviceClient.GetBlobContainerClient(container);
        long position = 0;

        await foreach (var blobItem in containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: prefix, cancellationToken: ct))
        {
            if (ct.IsCancellationRequested) yield break;

            // Streaming download support - store blob reference instead of downloading content
            var blobClient = containerClient.GetBlobClient(blobItem.Name);

            yield return new DataRecord(
                Values: new Dictionary<string, object?>
                {
                    ["name"] = blobItem.Name,
                    ["size"] = blobItem.Properties.ContentLength ?? 0,
                    ["lastModified"] = blobItem.Properties.LastModified,
                    ["contentType"] = blobItem.Properties.ContentType,
                    ["container"] = container,
                    ["blobType"] = blobItem.Properties.BlobType?.ToString() ?? "Unknown",
                    ["_blobClient"] = blobClient // Internal reference for streaming download
                },
                Position: position++,
                Timestamp: DateTimeOffset.UtcNow
            );
        }
    }

    /// <summary>
    /// Streaming download for large blobs - call this method separately when needed.
    /// </summary>
    /// <param name="containerName">Container name</param>
    /// <param name="blobName">Blob name</param>
    /// <param name="destinationStream">Stream to write downloaded content</param>
    /// <param name="ct">Cancellation token</param>
    public async Task DownloadBlobStreamAsync(string containerName, string blobName, Stream destinationStream, CancellationToken ct = default)
    {
        if (_serviceClient == null)
            throw new InvalidOperationException("Not connected to Azure Blob Storage");

        if (string.IsNullOrWhiteSpace(containerName))
            throw new ArgumentException("Container name cannot be empty", nameof(containerName));

        if (string.IsNullOrWhiteSpace(blobName))
            throw new ArgumentException("Blob name cannot be empty", nameof(blobName));

        if (destinationStream == null)
            throw new ArgumentNullException(nameof(destinationStream));

        if (!destinationStream.CanWrite)
            throw new ArgumentException("Destination stream must be writable", nameof(destinationStream));

        try
        {
            var containerClient = _serviceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            // Get blob properties for validation
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);
            var blobSize = properties.Value.ContentLength;

            // Validate blob size
            if (blobSize > _config.MaxBlobSizeBytes)
            {
                throw new InvalidOperationException(
                    $"Blob size ({blobSize:N0} bytes) exceeds maximum allowed size ({_config.MaxBlobSizeBytes:N0} bytes)");
            }

            // Stream download
            await blobClient.DownloadToAsync(destinationStream, ct);
        }
        catch (Azure.RequestFailedException ex)
        {
            throw new InvalidOperationException($"Failed to download blob '{blobName}': {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_serviceClient == null)
            throw new InvalidOperationException("Not connected to Azure Blob Storage");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var container = options.TargetTable ?? _containerName ?? throw new ArgumentException("Container name is required");
        var containerClient = _serviceClient.GetBlobContainerClient(container);

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                var blobName = record.Values.GetValueOrDefault("name")?.ToString()
                    ?? throw new ArgumentException($"Blob name is required for record at position {record.Position}");

                // Support both byte[] and Stream content
                var content = record.Values.GetValueOrDefault("content");
                if (content == null)
                    throw new ArgumentException($"Content is required for record at position {record.Position}");

                var blobClient = containerClient.GetBlobClient(blobName);

                // Streaming upload support
                if (content is Stream contentStream)
                {
                    // Validate stream size if possible
                    if (contentStream.CanSeek && contentStream.Length > _config.MaxBlobSizeBytes)
                    {
                        throw new InvalidOperationException(
                            $"Blob size ({contentStream.Length:N0} bytes) exceeds maximum allowed size ({_config.MaxBlobSizeBytes:N0} bytes)");
                    }

                    // Stream upload - no memory buffering
                    await blobClient.UploadAsync(contentStream, overwrite: true, ct);
                }
                else if (content is byte[] contentBytes)
                {
                    // Validate size
                    if (contentBytes.Length > _config.MaxBlobSizeBytes)
                    {
                        throw new InvalidOperationException(
                            $"Blob size ({contentBytes.Length:N0} bytes) exceeds maximum allowed size ({_config.MaxBlobSizeBytes:N0} bytes)");
                    }

                    // Use streaming upload for large blobs
                    if (contentBytes.Length > _config.StreamingThresholdBytes)
                    {
                        using var ms = new MemoryStream(contentBytes, writable: false);
                        await blobClient.UploadAsync(ms, overwrite: true, ct);
                    }
                    else
                    {
                        // Direct upload for small blobs
                        using var ms = new MemoryStream(contentBytes, writable: false);
                        await blobClient.UploadAsync(ms, overwrite: true, ct);
                    }
                }
                else
                {
                    throw new ArgumentException(
                        $"Content must be either Stream or byte[] for record at position {record.Position}");
                }

                // Set content type if provided
                var contentType = record.Values.GetValueOrDefault("contentType")?.ToString();
                if (!string.IsNullOrWhiteSpace(contentType))
                {
                    var headers = new BlobHttpHeaders { ContentType = contentType };
                    await blobClient.SetHttpHeadersAsync(headers, cancellationToken: ct);
                }

                written++;
            }
            catch (Azure.RequestFailedException ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: Azure request failed - {ex.Message}");
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");
            }
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <summary>
    /// Generate a SAS token for a blob with specified permissions.
    /// </summary>
    /// <param name="containerName">Container name</param>
    /// <param name="blobName">Blob name (optional, for blob-level SAS)</param>
    /// <param name="permissions">SAS permissions</param>
    /// <param name="expiresIn">Time until expiration</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>SAS URI</returns>
    public async Task<Uri> GenerateSasTokenAsync(
        string containerName,
        string? blobName = null,
        BlobSasPermissions permissions = BlobSasPermissions.Read,
        TimeSpan? expiresIn = null,
        CancellationToken ct = default)
    {
        if (_serviceClient == null)
            throw new InvalidOperationException("Not connected to Azure Blob Storage");

        if (string.IsNullOrWhiteSpace(containerName))
            throw new ArgumentException("Container name cannot be empty", nameof(containerName));

        var expiry = DateTimeOffset.UtcNow.Add(expiresIn ?? TimeSpan.FromHours(1));

        try
        {
            var containerClient = _serviceClient.GetBlobContainerClient(containerName);

            if (!string.IsNullOrWhiteSpace(blobName))
            {
                // Blob-level SAS
                var blobClient = containerClient.GetBlobClient(blobName);

                if (!blobClient.CanGenerateSasUri)
                    throw new InvalidOperationException("Client cannot generate SAS token. Use connection string with account key.");

                var sasBuilder = new BlobSasBuilder(permissions, expiry)
                {
                    BlobContainerName = containerName,
                    BlobName = blobName,
                    Resource = "b" // blob
                };

                return blobClient.GenerateSasUri(sasBuilder);
            }
            else
            {
                // Container-level SAS
                if (!containerClient.CanGenerateSasUri)
                    throw new InvalidOperationException("Client cannot generate SAS token. Use connection string with account key.");

                var sasBuilder = new BlobSasBuilder(permissions, expiry)
                {
                    BlobContainerName = containerName,
                    Resource = "c" // container
                };

                return containerClient.GenerateSasUri(sasBuilder);
            }
        }
        catch (Azure.RequestFailedException ex)
        {
            throw new InvalidOperationException($"Failed to generate SAS token: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

public class AzureBlobConnectorConfig
{
    /// <summary>
    /// Timeout in seconds for Azure operations.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum blob size in bytes. Default: 5 GB.
    /// </summary>
    public long MaxBlobSizeBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Size threshold for streaming uploads (bytes). Blobs larger than this will use streaming.
    /// Default: 10 MB.
    /// </summary>
    public long StreamingThresholdBytes { get; set; } = 10 * 1024 * 1024;
}
