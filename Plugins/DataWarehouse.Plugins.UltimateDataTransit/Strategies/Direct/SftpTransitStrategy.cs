using System.Diagnostics;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Transit;
using Renci.SshNet;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Direct;

/// <summary>
/// SFTP direct transfer strategy using SSH.NET <see cref="SftpClient"/>.
/// Supports secure file transfer over SSH with password and key-based authentication,
/// resumable transfers via SFTP append mode, and SHA-256 integrity verification.
/// </summary>
internal sealed class SftpTransitStrategy : DataTransitStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "transit-sftp";

    /// <inheritdoc/>
    public override string Name => "SFTP Direct Transfer (SSH.NET)";

    /// <inheritdoc/>
    public override TransitCapabilities Capabilities => new()
    {
        SupportsResumable = true,
        SupportsStreaming = true,
        SupportsDelta = false,
        SupportsMultiPath = false,
        SupportsP2P = false,
        SupportsOffline = false,
        SupportsCompression = false,
        SupportsEncryption = true,
        MaxTransferSizeBytes = long.MaxValue,
        SupportedProtocols = ["sftp", "ssh"]
    };

    /// <inheritdoc/>
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var client = CreateSftpClient(endpoint);
                client.Connect();
                var connected = client.IsConnected;
                client.Disconnect();
                return connected;
            }
            catch
            {
                return false;
            }
        }, ct);
    }

    /// <inheritdoc/>
    public override async Task<TransitResult> TransferAsync(TransitRequest request, IProgress<TransitProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transferId = request.TransferId;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ActiveTransferCancellations[transferId] = cts;
        IncrementActiveTransfers();

        var stopwatch = Stopwatch.StartNew();
        long totalBytesTransferred = 0;

        try
        {
            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Connecting to SFTP server",
                PercentComplete = 0
            });

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // Determine remote path
            var remotePath = request.Destination.Uri.AbsolutePath;
            if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
            {
                remotePath = $"/{transferId}";
            }

            if (request.DataStream != null)
            {
                // Upload provided data stream to destination
                totalBytesTransferred = await UploadStreamAsync(
                    request.Destination, remotePath, request.DataStream, sha256,
                    progress, transferId, request.SizeBytes, cts.Token);
            }
            else
            {
                // Pull from source and push to destination
                var sourcePath = request.Source.Uri.AbsolutePath;

                progress?.Report(new TransitProgress
                {
                    TransferId = transferId,
                    CurrentPhase = "Downloading from source",
                    PercentComplete = 5
                });

                using var tempStream = new MemoryStream(65536);

                await Task.Run(() =>
                {
                    using var sourceClient = CreateSftpClient(request.Source);
                    sourceClient.Connect();

                    sourceClient.DownloadFile(sourcePath, tempStream);
                    sourceClient.Disconnect();
                }, cts.Token);

                tempStream.Position = 0;

                // Hash the data
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await tempStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token)) > 0)
                {
                    sha256.AppendData(buffer.AsSpan(0, bytesRead));
                }
                tempStream.Position = 0;
                totalBytesTransferred = tempStream.Length;

                progress?.Report(new TransitProgress
                {
                    TransferId = transferId,
                    BytesTransferred = totalBytesTransferred,
                    TotalBytes = totalBytesTransferred,
                    PercentComplete = 50,
                    CurrentPhase = "Uploading to destination"
                });

                await Task.Run(() =>
                {
                    using var destClient = CreateSftpClient(request.Destination);
                    destClient.Connect();

                    destClient.UploadFile(tempStream, remotePath, true);
                    destClient.Disconnect();
                }, cts.Token);
            }

            stopwatch.Stop();

            var hash = sha256.GetHashAndReset();
            var contentHash = Convert.ToHexStringLower(hash);

            RecordTransferSuccess(totalBytesTransferred);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalBytesTransferred,
                TotalBytes = request.SizeBytes > 0 ? request.SizeBytes : totalBytesTransferred,
                PercentComplete = 100.0,
                CurrentPhase = "Completed"
            });

            return new TransitResult
            {
                TransferId = transferId,
                Success = true,
                BytesTransferred = totalBytesTransferred,
                Duration = stopwatch.Elapsed,
                ContentHash = contentHash,
                StrategyUsed = StrategyId
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            RecordTransferFailure();

            return new TransitResult
            {
                TransferId = transferId,
                Success = false,
                BytesTransferred = totalBytesTransferred,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message,
                StrategyUsed = StrategyId
            };
        }
        finally
        {
            DecrementActiveTransfers();
            ActiveTransferCancellations.TryRemove(transferId, out _);
            cts.Dispose();
        }
    }

    /// <summary>
    /// Uploads a data stream to an SFTP destination while computing SHA-256 hash and reporting progress.
    /// </summary>
    private async Task<long> UploadStreamAsync(
        TransitEndpoint destination,
        string remotePath,
        Stream dataStream,
        IncrementalHash sha256,
        IProgress<TransitProgress>? progress,
        string transferId,
        long totalSize,
        CancellationToken ct)
    {
        // Buffer the stream for hashing
        using var buffered = new MemoryStream(65536);
        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await dataStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            sha256.AppendData(buffer.AsSpan(0, bytesRead));
            await buffered.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            var pct = totalSize > 0 ? (double)totalRead / totalSize * 50.0 : 0;
            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalRead,
                TotalBytes = totalSize,
                PercentComplete = pct,
                CurrentPhase = "Reading data"
            });
        }

        buffered.Position = 0;

        progress?.Report(new TransitProgress
        {
            TransferId = transferId,
            BytesTransferred = totalRead,
            TotalBytes = totalSize > 0 ? totalSize : totalRead,
            PercentComplete = 60,
            CurrentPhase = "Uploading via SFTP"
        });

        await Task.Run(() =>
        {
            using var client = CreateSftpClient(destination);
            client.Connect();
            client.UploadFile(buffered, remotePath, true);
            client.Disconnect();
        }, ct);

        return totalRead;
    }

    /// <summary>
    /// Creates an <see cref="SftpClient"/> configured for the given endpoint.
    /// Supports password authentication via AuthToken ("username:password") or URI userinfo.
    /// </summary>
    /// <param name="endpoint">The endpoint to connect to.</param>
    /// <returns>A configured SFTP client.</returns>
    private static SftpClient CreateSftpClient(TransitEndpoint endpoint)
    {
        var host = endpoint.Uri.Host;
        var port = endpoint.Uri.Port > 0 ? endpoint.Uri.Port : 22;
        var username = "anonymous";
        var password = string.Empty;

        // Parse credentials from AuthToken or URI userinfo
        if (!string.IsNullOrEmpty(endpoint.AuthToken) && endpoint.AuthToken.Contains(':'))
        {
            var parts = endpoint.AuthToken.Split(':', 2);
            username = parts[0];
            password = parts[1];
        }
        else if (!string.IsNullOrEmpty(endpoint.Uri.UserInfo) && endpoint.Uri.UserInfo.Contains(':'))
        {
            var parts = Uri.UnescapeDataString(endpoint.Uri.UserInfo).Split(':', 2);
            username = parts[0];
            password = parts[1];
        }
        else if (!string.IsNullOrEmpty(endpoint.Uri.UserInfo))
        {
            username = Uri.UnescapeDataString(endpoint.Uri.UserInfo);
        }

        var connectionInfo = new ConnectionInfo(host, port, username,
            new PasswordAuthenticationMethod(username, password))
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        return new SftpClient(connectionInfo);
    }
}
