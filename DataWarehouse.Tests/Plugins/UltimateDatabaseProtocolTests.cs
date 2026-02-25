using DataWarehouse.Plugins.UltimateDatabaseProtocol;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDatabaseProtocolTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDatabaseProtocolPlugin();
        plugin.Id.Should().Be("com.datawarehouse.protocol.ultimate");
        plugin.Name.Should().Be("Ultimate Database Protocol");
        plugin.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void Registry_ShouldSupportRegistrationAndLookup()
    {
        var registry = new DatabaseProtocolStrategyRegistry();
        registry.GetAllStrategies().Should().BeEmpty();

        // Auto-discover strategies from the plugin assembly
        registry.DiscoverStrategies(typeof(UltimateDatabaseProtocolPlugin).Assembly);
        registry.GetAllStrategies().Should().NotBeEmpty("strategies are discovered via reflection");
    }

    [Fact]
    public void ProtocolCapabilities_StandardRelational_ShouldHaveExpectedDefaults()
    {
        var caps = ProtocolCapabilities.StandardRelational;
        caps.SupportsTransactions.Should().BeTrue();
        caps.SupportsPreparedStatements.Should().BeTrue();
        caps.SupportsSsl.Should().BeTrue();
        caps.SupportsCursors.Should().BeTrue();
        caps.SupportsBatch.Should().BeTrue();
        caps.SupportsBulkOperations.Should().BeTrue();
        caps.SupportsQueryCancellation.Should().BeTrue();
        caps.SupportedAuthMethods.Should().Contain(AuthenticationMethod.ScramSha256);
    }

    [Fact]
    public void ProtocolCapabilities_StandardNoSql_ShouldDifferFromRelational()
    {
        var noSql = ProtocolCapabilities.StandardNoSql;
        noSql.SupportsTransactions.Should().BeFalse();
        noSql.SupportsPreparedStatements.Should().BeFalse();
        noSql.SupportsMultiplexing.Should().BeTrue();
        noSql.SupportsCompression.Should().BeTrue();
        noSql.SupportsNotifications.Should().BeTrue();
    }

    [Fact]
    public void ProtocolStatistics_Empty_ShouldHaveZeroCounts()
    {
        var stats = ProtocolStatistics.Empty;
        stats.ConnectionsEstablished.Should().Be(0);
        stats.QueriesExecuted.Should().Be(0);
        stats.TransactionsStarted.Should().Be(0);
        stats.BytesSent.Should().Be(0);
        stats.BytesReceived.Should().Be(0);
        stats.StartTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ProtocolInfo_ShouldBeConstructable()
    {
        var info = new ProtocolInfo
        {
            ProtocolName = "TestProtocol",
            ProtocolVersion = "1.0",
            DefaultPort = 5432,
            Family = ProtocolFamily.Relational,
            Capabilities = ProtocolCapabilities.StandardRelational
        };
        info.ProtocolName.Should().Be("TestProtocol");
        info.DefaultPort.Should().Be(5432);
        info.Family.Should().Be(ProtocolFamily.Relational);
        info.MaxPacketSize.Should().Be(1024 * 1024);
    }

    [Fact]
    public void ConnectionParameters_ShouldHaveDefaults()
    {
        var p = new ConnectionParameters { Host = "localhost" };
        p.Host.Should().Be("localhost");
        p.ConnectionTimeoutMs.Should().Be(30000);
        p.CommandTimeout.Should().Be(60);
        p.AuthMethod.Should().Be(AuthenticationMethod.ClearText);
        p.UseSsl.Should().BeFalse();
    }

    [Fact]
    public void QueryResult_ShouldRepresentSuccessAndFailure()
    {
        var success = new QueryResult { Success = true, RowsAffected = 5 };
        success.Success.Should().BeTrue();
        success.RowsAffected.Should().Be(5);
        success.ErrorMessage.Should().BeNull();

        var failure = new QueryResult { Success = false, ErrorMessage = "timeout" };
        failure.Success.Should().BeFalse();
        failure.ErrorMessage.Should().Be("timeout");
    }

    [Fact]
    public void ColumnMetadata_ShouldStoreTypeInformation()
    {
        var col = new ColumnMetadata
        {
            Name = "id",
            DataType = "integer",
            Ordinal = 0,
            IsNullable = false,
            Precision = 10
        };
        col.Name.Should().Be("id");
        col.DataType.Should().Be("integer");
        col.IsNullable.Should().BeFalse();
    }

    [Fact]
    public void ProtocolFamily_ShouldCoverAllCategories()
    {
        var families = Enum.GetValues<ProtocolFamily>();
        families.Should().Contain(ProtocolFamily.Relational);
        families.Should().Contain(ProtocolFamily.NoSQL);
        families.Should().Contain(ProtocolFamily.Graph);
        families.Should().Contain(ProtocolFamily.TimeSeries);
        families.Should().Contain(ProtocolFamily.Search);
        families.Should().Contain(ProtocolFamily.Embedded);
        families.Should().Contain(ProtocolFamily.Messaging);
    }

    [Fact]
    public void Registry_ShouldFilterByFamily()
    {
        var registry = new DatabaseProtocolStrategyRegistry();
        registry.DiscoverStrategies(typeof(UltimateDatabaseProtocolPlugin).Assembly);

        var relational = registry.GetStrategiesByFamily(ProtocolFamily.Relational);
        relational.Should().NotBeNull();

        foreach (var strategy in relational)
        {
            strategy.ProtocolInfo.Family.Should().Be(ProtocolFamily.Relational);
        }
    }
}
