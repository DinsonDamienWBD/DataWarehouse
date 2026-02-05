using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Legacy
{
    public class CobolCopybookConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "cobol-copybook";
        public override string DisplayName => "COBOL Copybook";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to COBOL data files using copybook definitions";
        public override string[] Tags => new[] { "cobol", "copybook", "legacy", "mainframe", "files" };

        public CobolCopybookConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var filePath = config.ConnectionString;
            if (!System.IO.File.Exists(filePath))
                throw new InvalidOperationException($"COBOL data file not found: {filePath}");
            var fileStream = System.IO.File.OpenRead(filePath);
            return Task.FromResult<IConnectionHandle>(new DefaultConnectionHandle(fileStream, new Dictionary<string, object> { ["file"] = filePath, ["protocol"] = "COBOL Copybook" }));
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var stream = handle.GetConnection<System.IO.FileStream>();
            return Task.FromResult(stream.CanRead);
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { handle.GetConnection<System.IO.FileStream>().Close(); return Task.CompletedTask; }
        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) => Task.FromResult(new ConnectionHealth(handle.GetConnection<System.IO.FileStream>().CanRead, "COBOL file readable", TimeSpan.Zero, DateTimeOffset.UtcNow));
        public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default) => throw new NotSupportedException("Requires COBOL parser");
        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default) => throw new NotSupportedException("Requires COBOL parser");
    }
}
