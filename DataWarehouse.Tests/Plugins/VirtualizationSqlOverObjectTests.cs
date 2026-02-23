using Xunit;
using DataWarehouse.Plugins.UltimateDatabaseProtocol;
using DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Virtualization;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for SQL-over-Object protocol strategy (merged from SqlOverObject plugin into UltimateDatabaseProtocol).
/// Validates strategy identity, SQL capabilities, and virtual table management.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "UltimateDatabaseProtocol")]
[Trait("Strategy", "SqlOverObject")]
public class VirtualizationSqlOverObjectTests
{
    [Fact]
    public void Strategy_HasCorrectIdentity()
    {
        var strategy = new SqlOverObjectProtocolStrategy();

        Assert.Equal("sql-over-object", strategy.StrategyId);
        Assert.Equal("SQL-over-Object Data Virtualization Protocol", strategy.StrategyName);
    }

    [Fact]
    public void Strategy_ProtocolInfo_IsCorrect()
    {
        var strategy = new SqlOverObjectProtocolStrategy();

        Assert.Equal("SQL-over-Object Virtualization", strategy.ProtocolInfo.ProtocolName);
        Assert.Equal(DataWarehouse.Plugins.UltimateDatabaseProtocol.ProtocolFamily.Specialized, strategy.ProtocolInfo.Family);
        Assert.True(strategy.ProtocolInfo.Capabilities.SupportsStreaming);
        Assert.True(strategy.ProtocolInfo.Capabilities.SupportsPreparedStatements);
        Assert.False(strategy.ProtocolInfo.Capabilities.SupportsTransactions);
    }

    [Fact]
    public void Strategy_SupportedFormats_ContainsExpectedFormats()
    {
        var strategy = new SqlOverObjectProtocolStrategy();

        Assert.Contains("csv", strategy.SupportedFileFormats);
        Assert.Contains("json", strategy.SupportedFileFormats);
        Assert.Contains("parquet", strategy.SupportedFileFormats);
    }

    [Fact]
    public void Strategy_SqlDialect_IsAnsiSql()
    {
        var strategy = new SqlOverObjectProtocolStrategy();

        Assert.Equal("ANSI-SQL:2011", strategy.SupportedSqlDialect);
    }

    [Fact]
    public void Strategy_RegisterAndListTables()
    {
        var strategy = new SqlOverObjectProtocolStrategy();
        var schema = new SqlOverObjectProtocolStrategy.VirtualTableSchema
        {
            TableName = "test_table",
            Format = "csv",
            Columns = new()
            {
                new SqlOverObjectProtocolStrategy.VirtualColumn { Name = "id", DataType = "int" },
                new SqlOverObjectProtocolStrategy.VirtualColumn { Name = "name", DataType = "varchar" }
            }
        };

        strategy.RegisterTable("test_table", schema);
        var tables = strategy.ListTables();

        Assert.Contains("test_table", tables);
    }

    [Fact]
    public void Strategy_UnregisterTable()
    {
        var strategy = new SqlOverObjectProtocolStrategy();
        strategy.RegisterTable("temp_table", new SqlOverObjectProtocolStrategy.VirtualTableSchema { TableName = "temp_table" });

        var result = strategy.UnregisterTable("temp_table");

        Assert.True(result);
        Assert.DoesNotContain("temp_table", strategy.ListTables());
    }

    [Fact]
    public void Strategy_InferCsvSchema_ParsesHeaders()
    {
        var strategy = new SqlOverObjectProtocolStrategy();

        var schema = strategy.InferCsvSchema("orders", "id,product,quantity,price");

        Assert.Equal("orders", schema.TableName);
        Assert.Equal("csv", schema.Format);
        Assert.Equal(4, schema.Columns.Count);
        Assert.Equal("id", schema.Columns[0].Name);
        Assert.Equal("price", schema.Columns[3].Name);
    }

    [Fact]
    public void Strategy_InferJsonSchema_ParsesKeys()
    {
        var strategy = new SqlOverObjectProtocolStrategy();

        var schema = strategy.InferJsonSchema("events", """[{"id": 1, "name": "test", "active": true}]""");

        Assert.Equal("events", schema.TableName);
        Assert.Equal("json", schema.Format);
        Assert.Equal(3, schema.Columns.Count);
    }
}
