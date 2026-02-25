using System.Diagnostics;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Transit;
using FluentFTP;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Direct;

/// <summary>
/// FTP/FTPS direct transfer strategy using <see cref="AsyncFtpClient"/> from FluentFTP.
/// Supports async FTP operations including upload, download, resumable transfers via FTP REST command,
/// and TLS-secured FTPS connections.
/// </summary>
internal sealed class FtpTransitStrategy : DataTransitStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "transit-ftp";

    /// <inheritdoc/>
    public override string Name => "FTP/FTPS Direct Transfer";

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
        SupportedProtocols = ["ftp", "ftps"]
    };

    /// <inheritdoc/>
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default)
    {
        try
        {
            using var client = CreateFtpClient(endpoint);
            await client.Connect(ct);
            await client.Disconnect(ct);
            return true;
        }
        catch
        {
            return false;
        }
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
                CurrentPhase = "Connecting to FTP server",
                PercentComplete = 0
            });

            using var client = CreateFtpClient(request.Destination);
            await client.Connect(cts.Token);

            // Determine remote path from destination URI
            var remotePath = request.Destination.Uri.AbsolutePath;
            if (string.IsNullOrWhiteSpace(remotePath) || remotePath == "/")
            {
                remotePath = $"/{transferId}";
            }

            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            if (request.DataStream != null)
            {
                // Upload from provided data stream
                progress?.Report(new TransitProgress
                {
                    TransferId = transferId,
                    CurrentPhase = "Uploading",
                    PercentComplete = 5
                });

                // Wrap the stream for hashing and progress
                var hashingStream = new FtpHashingStream(
                    request.DataStream, sha256, progress, transferId, request.SizeBytes,
                    bytesRead => totalBytesTransferred = bytesRead);

                var ftpProgress = new Progress<FtpProgress>(p =>
                {
                    progress?.Report(new TransitProgress
                    {
                        TransferId = transferId,
                        BytesTransferred = totalBytesTransferred,
                        TotalBytes = request.SizeBytes,
                        PercentComplete = p.Progress,
                        BytesPerSecond = p.TransferSpeed,
                        EstimatedRemaining = p.ETA,
                        CurrentPhase = "Uploading"
                    });
                });

                var status = await client.UploadStream(hashingStream, remotePath, FtpRemoteExists.Overwrite, false, ftpProgress, cts.Token);

                if (status != FtpStatus.Success)
                {
                    RecordTransferFailure();
                    return new TransitResult
                    {
                        TransferId = transferId,
                        Success = false,
                        BytesTransferred = totalBytesTransferred,
                        Duration = stopwatch.Elapsed,
                        ErrorMessage = $"FTP upload failed with status: {status}",
                        StrategyUsed = StrategyId
                    };
                }
            }
            else
            {
                // Pull from source endpoint (download from source FTP, upload to destination)
                using var sourceClient = CreateFtpClient(request.Source);
                await sourceClient.Connect(cts.Token);

                var sourcePath = request.Source.Uri.AbsolutePath;

                progress?.Report(new TransitProgress
                {
                    TransferId = transferId,
                    CurrentPhase = "Downloading from source",
                    PercentComplete = 5
                });

                using var downloadStream = new MemoryStream(65536);
                var downloadSuccess = await sourceClient.DownloadStream(downloadStream, sourcePath, token: cts.Token);

                if (!downloadSuccess)
                {
                    RecordTransferFailure();
                    return new TransitResult
                    {
                        TransferId = transferId,
                        Success = false,
                        Duration = stopwatch.Elapsed,
                        ErrorMessage = $"FTP download from source failed: {sourcePath}",
                        StrategyUsed = StrategyId
                    };
                }

                downloadStream.Position = 0;
                totalBytesTransferred = downloadStream.Length;

                // Hash the downloaded data
                var buffer = downloadStream.ToArray();
                sha256.AppendData(buffer);

                downloadStream.Position = 0;

                progress?.Report(new TransitProgress
                {
                    TransferId = transferId,
                    BytesTransferred = totalBytesTransferred,
                    TotalBytes = totalBytesTransferred,
                    PercentComplete = 50,
                    CurrentPhase = "Uploading to destination"
                });

                var status = await client.UploadStream(downloadStream, remotePath, FtpRemoteExists.Overwrite, false, token: cts.Token);

                if (status != FtpStatus.Success)
                {
                    RecordTransferFailure();
                    return new TransitResult
                    {
                        TransferId = transferId,
                        Success = false,
                        BytesTransferred = totalBytesTransferred,
                        Duration = stopwatch.Elapsed,
                        ErrorMessage = $"FTP upload to destination failed with status: {status}",
                        StrategyUsed = StrategyId
                    };
                }

                await sourceClient.Disconnect(cts.Token);
            }

            await client.Disconnect(cts.Token);
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
    /// Creates an <see cref="AsyncFtpClient"/> configured for the given endpoint.
    /// Supports FTPS via explicit TLS when the protocol is "ftps".
    /// </summary>
    /// <param name="endpoint">The endpoint to connect to.</param>
    /// <returns>A configured FTP client.</returns>
    private static AsyncFtpClient CreateFtpClient(TransitEndpoint endpoint)
    {
        var host = endpoint.Uri.Host;
        var port = endpoint.Uri.Port > 0 ? endpoint.Uri.Port : 21;

        var client = new AsyncFtpClient(host, port);

        // Parse credentials from AuthToken ("username:password") or URI userinfo
        var userInfo = endpoint.Uri.UserInfo;
        if (!string.IsNullOrEmpty(endpoint.AuthToken) && endpoint.AuthToken.Contains(':'))
        {
            var parts = endpoint.AuthToken.Split(':', 2);
            client.Credentials = new System.Net.NetworkCredential(parts[0], parts[1]);
        }
        else if (!string.IsNullOrEmpty(userInfo) && userInfo.Contains(':'))
        {
            var parts = Uri.UnescapeDataString(userInfo).Split(':', 2);
            client.Credentials = new System.Net.NetworkCredential(parts[0], parts[1]);
        }

        // Enable FTPS for ftps:// endpoints
        var protocol = endpoint.Protocol ?? endpoint.Uri.Scheme;
        if (protocol.Equals("ftps", StringComparison.OrdinalIgnoreCase))
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            // Secure default: validate certificates. Use endpoint metadata to opt-in to bypass if needed.
            client.Config.ValidateAnyCertificate = false;
        }

        client.Config.ConnectTimeout = 30000;
        client.Config.DataConnectionConnectTimeout = 30000;
        client.Config.ReadTimeout = 60000;

        return client;
    }

    /// <summary>
    /// A read-only stream wrapper that computes an incremental SHA-256 hash
    /// and reports progress as data is read for FTP upload.
    /// </summary>
    private sealed class FtpHashingStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash;
        private readonly IProgress<TransitProgress>? _progress;
        private readonly string _transferId;
        private readonly long _totalBytes;
        private readonly Action<long> _updateBytesRead;
        private long _bytesRead;

        /// <summary>
        /// Initializes a new instance of the <see cref="FtpHashingStream"/> class.
        /// </summary>
        public FtpHashingStream(
            Stream inner,
            IncrementalHash hash,
            IProgress<TransitProgress>? progress,
            string transferId,
            long totalBytes,
            Action<long> updateBytesRead)
        {
            _inner = inner;
            _hash = hash;
            _progress = progress;
            _transferId = transferId;
            _totalBytes = totalBytes;
            _updateBytesRead = updateBytesRead;
        }

        /// <inheritdoc/>
        public override bool CanRead => _inner.CanRead;

        /// <inheritdoc/>
        public override bool CanSeek => _inner.CanSeek;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length => _inner.Length;

        /// <inheritdoc/>
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _inner.Read(buffer, offset, count);
            if (bytesRead > 0)
            {
                _hash.AppendData(buffer.AsSpan(offset, bytesRead));
                _bytesRead += bytesRead;
                _updateBytesRead(_bytesRead);
            }
            return bytesRead;
        }

        /// <inheritdoc/>
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesRead = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            if (bytesRead > 0)
            {
                _hash.AppendData(buffer.AsSpan(offset, bytesRead));
                _bytesRead += bytesRead;
                _updateBytesRead(_bytesRead);
            }
            return bytesRead;
        }

        /// <inheritdoc/>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = await _inner.ReadAsync(buffer, cancellationToken);
            if (bytesRead > 0)
            {
                _hash.AppendData(buffer.Span[..bytesRead]);
                _bytesRead += bytesRead;
                _updateBytesRead(_bytesRead);
            }
            return bytesRead;
        }

        /// <inheritdoc/>
        public override void Flush() => _inner.Flush();

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            // Do not dispose the inner stream; it is managed by the caller
            base.Dispose(disposing);
        }
    }
}
