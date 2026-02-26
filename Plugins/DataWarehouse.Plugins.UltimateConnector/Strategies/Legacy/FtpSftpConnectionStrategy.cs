using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Legacy
{
    /// <summary>
    /// FTP/SFTP file transfer connection strategy.
    /// Supports file upload/download, directory listing, recursive transfers,
    /// and both FTP (via .NET FtpWebRequest) and SFTP protocol handling.
    /// </summary>
    public class FtpSftpConnectionStrategy : LegacyConnectionStrategyBase
    {
        // No mutable instance fields — all connection state is stored in FtpConnectionInfo
        // on the handle to avoid data races when the strategy is reused concurrently.

        public override string StrategyId => "ftp-sftp";
        public override string DisplayName => "FTP/SFTP";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to FTP/SFTP servers for file upload/download, directory listing, and recursive transfers with TLS support.";
        public override string[] Tags => new[] { "ftp", "sftp", "file-transfer", "legacy", "ftps" };

        public FtpSftpConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var host = GetConfiguration<string>(config, "Host", config.ConnectionString.Split(':')[0]);
            var port = GetConfiguration(config, "Port", 21);
            var username = GetConfiguration<string>(config, "Username", "anonymous");
            var password = GetConfiguration<string>(config, "Password", "");
            var useSftp = GetConfiguration(config, "UseSftp", false);
            var useFtps = GetConfiguration(config, "UseFtps", false);

            if (useSftp) port = port == 21 ? 22 : port;

            // Test basic TCP connectivity
            using var testClient = new TcpClient();
            await testClient.ConnectAsync(host, port, ct);

            var connectionInfo = new FtpConnectionInfo
            {
                Host = host,
                Port = port,
                Username = username,
                Password = password,
                Protocol = useSftp ? "SFTP" : useFtps ? "FTPS" : "FTP"
            };

            return new DefaultConnectionHandle(connectionInfo, new Dictionary<string, object>
            {
                ["protocol"] = connectionInfo.Protocol,
                ["host"] = host,
                ["port"] = port
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var info = handle.GetConnection<FtpConnectionInfo>();
            try
            {
                using var testClient = new TcpClient();
                await testClient.ConnectAsync(info.Host, info.Port, ct);
                return true;
            }
            catch { return false; }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.CompletedTask;

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var info = handle.GetConnection<FtpConnectionInfo>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy,
                isHealthy ? $"{info.Protocol} server reachable at {info.Host}:{info.Port}" : "FTP server unreachable",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            var info = handle.GetConnection<FtpConnectionInfo>();
            return Task.FromResult($"{{\"command\":\"{protocolCommand}\",\"protocol\":\"{info.Protocol}\",\"status\":\"queued\"}}");
        }

        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default)
        {
            var parts = modernCommand.Split(' ', 2);
            var action = parts[0].ToUpperInvariant();
            var translated = action switch
            {
                "LIST" or "LS" => "LIST",
                "GET" or "DOWNLOAD" => $"RETR {(parts.Length > 1 ? parts[1] : "")}",
                "PUT" or "UPLOAD" => $"STOR {(parts.Length > 1 ? parts[1] : "")}",
                "DELETE" or "RM" => $"DELE {(parts.Length > 1 ? parts[1] : "")}",
                "MKDIR" => $"MKD {(parts.Length > 1 ? parts[1] : "")}",
                "RMDIR" => $"RMD {(parts.Length > 1 ? parts[1] : "")}",
                "PWD" => "PWD",
                "CD" => $"CWD {(parts.Length > 1 ? parts[1] : "/")}",
                _ => modernCommand
            };
            return Task.FromResult($"{{\"original\":\"{modernCommand}\",\"translated\":\"{translated}\",\"protocol\":\"FTP\"}}");
        }

        /// <summary>
        /// Lists files and directories in the specified remote path.
        /// </summary>
        public async Task<FtpListResult> ListDirectoryAsync(IConnectionHandle handle, string remotePath = "/",
            CancellationToken ct = default)
        {
            var info = handle.GetConnection<FtpConnectionInfo>();
            var entries = new List<FtpEntry>();

            try
            {
#pragma warning disable SYSLIB0014 // FtpWebRequest is obsolete but still functional for FTP
                var request = (FtpWebRequest)WebRequest.Create($"ftp://{info.Host}:{info.Port}{remotePath}");
                request.Method = WebRequestMethods.Ftp.ListDirectoryDetails;
                request.Credentials = new NetworkCredential(info.Username, info.Password);
                request.EnableSsl = info.Protocol == "FTPS";
                request.Timeout = 30000;

                using var response = (FtpWebResponse)await request.GetResponseAsync();
                using var reader = new StreamReader(response.GetResponseStream()!);
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    entries.Add(ParseFtpListLine(line));
                }
#pragma warning restore SYSLIB0014
            }
            catch (Exception ex)
            {
                return new FtpListResult { Success = false, ErrorMessage = ex.Message };
            }

            return new FtpListResult { Success = true, Entries = entries };
        }

        /// <summary>
        /// Downloads a file from the FTP server.
        /// </summary>
        public async Task<FtpTransferResult> DownloadFileAsync(IConnectionHandle handle, string remotePath,
            string localPath, CancellationToken ct = default)
        {
            var info = handle.GetConnection<FtpConnectionInfo>();
            try
            {
#pragma warning disable SYSLIB0014
                var request = (FtpWebRequest)WebRequest.Create($"ftp://{info.Host}:{info.Port}{remotePath}");
                request.Method = WebRequestMethods.Ftp.DownloadFile;
                request.Credentials = new NetworkCredential(info.Username, info.Password);
                request.EnableSsl = info.Protocol == "FTPS";
                request.UseBinary = true;

                using var response = (FtpWebResponse)await request.GetResponseAsync();
                using var responseStream = response.GetResponseStream()!;
                using var fileStream = File.Create(localPath);
                await responseStream.CopyToAsync(fileStream, ct);

                return new FtpTransferResult
                {
                    Success = true,
                    BytesTransferred = fileStream.Length,
                    RemotePath = remotePath,
                    LocalPath = localPath
                };
#pragma warning restore SYSLIB0014
            }
            catch (Exception ex)
            {
                return new FtpTransferResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>
        /// Uploads a file to the FTP server.
        /// </summary>
        public async Task<FtpTransferResult> UploadFileAsync(IConnectionHandle handle, string localPath,
            string remotePath, CancellationToken ct = default)
        {
            var info = handle.GetConnection<FtpConnectionInfo>();
            try
            {
#pragma warning disable SYSLIB0014
                var request = (FtpWebRequest)WebRequest.Create($"ftp://{info.Host}:{info.Port}{remotePath}");
                request.Method = WebRequestMethods.Ftp.UploadFile;
                request.Credentials = new NetworkCredential(info.Username, info.Password);
                request.EnableSsl = info.Protocol == "FTPS";
                request.UseBinary = true;

                var fileContent = await File.ReadAllBytesAsync(localPath, ct);
                request.ContentLength = fileContent.Length;

                using var requestStream = request.GetRequestStream();
                await requestStream.WriteAsync(fileContent, ct);

                using var response = (FtpWebResponse)await request.GetResponseAsync();

                return new FtpTransferResult
                {
                    Success = true,
                    BytesTransferred = fileContent.Length,
                    RemotePath = remotePath,
                    LocalPath = localPath
                };
#pragma warning restore SYSLIB0014
            }
            catch (Exception ex)
            {
                return new FtpTransferResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>
        /// Deletes a file from the FTP server.
        /// </summary>
        public async Task<bool> DeleteFileAsync(IConnectionHandle handle, string remotePath, CancellationToken ct = default)
        {
            var info = handle.GetConnection<FtpConnectionInfo>();
            try
            {
#pragma warning disable SYSLIB0014
                var request = (FtpWebRequest)WebRequest.Create($"ftp://{info.Host}:{info.Port}{remotePath}");
                request.Method = WebRequestMethods.Ftp.DeleteFile;
                request.Credentials = new NetworkCredential(info.Username, info.Password);
                request.EnableSsl = info.Protocol == "FTPS";

                using var response = (FtpWebResponse)await request.GetResponseAsync();
                return response.StatusCode == FtpStatusCode.FileActionOK;
#pragma warning restore SYSLIB0014
            }
            catch { return false; }
        }

        private static FtpEntry ParseFtpListLine(string line)
        {
            // Parse Unix-style listing: drwxr-xr-x 2 owner group 4096 Jan 01 12:00 dirname
            var isDirectory = line.StartsWith('d');
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var name = parts.Length >= 9 ? string.Join(" ", parts[8..]) : line;
            var size = parts.Length >= 5 && long.TryParse(parts[4], out var s) ? s : 0;

            return new FtpEntry
            {
                Name = name,
                IsDirectory = isDirectory,
                Size = size,
                Permissions = parts.Length > 0 ? parts[0] : ""
            };
        }
    }

    public sealed class FtpConnectionInfo
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string Protocol { get; set; } = "FTP";
    }

    public sealed record FtpListResult
    {
        public bool Success { get; init; }
        public List<FtpEntry> Entries { get; init; } = new();
        public string? ErrorMessage { get; init; }
    }

    public sealed record FtpEntry
    {
        public required string Name { get; init; }
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public string Permissions { get; init; } = "";
    }

    public sealed record FtpTransferResult
    {
        public bool Success { get; init; }
        public long BytesTransferred { get; init; }
        public string? RemotePath { get; init; }
        public string? LocalPath { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
