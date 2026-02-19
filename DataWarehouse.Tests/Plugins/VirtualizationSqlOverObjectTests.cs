using Xunit;
using DataWarehouse.Plugins.Virtualization.SqlOverObject;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class VirtualizationSqlOverObjectTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        var plugin = new SqlOverObjectPlugin();

        Assert.Equal("com.datawarehouse.virtualization.sql", plugin.Id);
        Assert.Equal("SQL-over-Object Data Virtualization", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void SqlDialect_IsAnsiSql()
    {
        var plugin = new SqlOverObjectPlugin();

        Assert.Equal("ANSI-SQL:2011", plugin.SqlDialect);
    }

    [Fact]
    public void SupportedFormats_ContainsExpectedFormats()
    {
        var plugin = new SqlOverObjectPlugin();

        var formats = plugin.SupportedFormats;
        Assert.NotEmpty(formats);
        Assert.Contains(VirtualTableFormat.Csv, formats);
        Assert.Contains(VirtualTableFormat.Json, formats);
        Assert.Contains(VirtualTableFormat.Parquet, formats);
    }

    [Fact]
    public void GetStatistics_ReturnsZeroInitialCounts()
    {
        var plugin = new SqlOverObjectPlugin();

        var stats = plugin.GetStatistics();
        Assert.NotNull(stats);
        Assert.Equal(0, stats.TotalQueriesExecuted);
        Assert.Equal(0, stats.TotalBytesScanned);
        Assert.Equal(0, stats.RegisteredTables);
    }

    [Fact]
    public void GetJdbcOdbcMetadata_ReturnsCatalog()
    {
        var plugin = new SqlOverObjectPlugin();

        var metadata = plugin.GetJdbcOdbcMetadata();
        Assert.NotNull(metadata);
        Assert.Equal("datawarehouse", metadata.Catalog);
    }
}
