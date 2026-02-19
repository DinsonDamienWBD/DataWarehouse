using Xunit;
using DataWarehouse.Plugins.UltimateDocGen;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDocGenTests
{
    [Fact]
    public void Plugin_HasCorrectIdentity()
    {
        using var plugin = new UltimateDocGenPlugin();

        Assert.Equal("com.datawarehouse.docgen.ultimate", plugin.Id);
        Assert.Equal("Ultimate DocGen", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void Registry_ContainsExpectedStrategies()
    {
        using var plugin = new UltimateDocGenPlugin();

        Assert.True(plugin.Registry.Count >= 10, "Should have at least 10 registered strategies");
        Assert.NotNull(plugin.Registry.Get("OpenApiDoc"));
        Assert.NotNull(plugin.Registry.Get("GraphQLSchemaDoc"));
        Assert.NotNull(plugin.Registry.Get("ChangeLogDoc"));
    }

    [Fact]
    public async Task OpenApiDocStrategy_GeneratesMarkdown()
    {
        var strategy = new OpenApiDocStrategy();
        var request = new DocGenRequest
        {
            OperationId = "test-001",
            SourceType = "api",
            Format = OutputFormat.Markdown
        };

        var result = await strategy.GenerateAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.Content);
        Assert.Contains("API Documentation", result.Content);
        Assert.Equal(OutputFormat.Markdown, result.Format);
    }

    [Fact]
    public void DocGenCharacteristics_HaveCorrectCategories()
    {
        var openApi = new OpenApiDocStrategy();
        var dbSchema = new DatabaseSchemaDocStrategy();
        var changelog = new ChangeLogDocStrategy();

        Assert.Equal(DocGenCategory.ApiDoc, openApi.Characteristics.Category);
        Assert.Equal(DocGenCategory.SchemaDoc, dbSchema.Characteristics.Category);
        Assert.Equal(DocGenCategory.ChangeLog, changelog.Characteristics.Category);
    }

    [Fact]
    public void DocGenStrategyRegistry_RegisterAndRetrieve()
    {
        var registry = new DocGenStrategyRegistry();
        var strategy = new HtmlOutputStrategy();

        registry.Register(strategy);

        Assert.Equal(1, registry.Count);
        Assert.NotNull(registry.Get("HtmlOutput"));
        Assert.Null(registry.Get("NonExistent"));
    }
}
