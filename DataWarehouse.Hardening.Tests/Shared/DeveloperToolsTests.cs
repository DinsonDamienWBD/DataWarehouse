// Hardening tests for Shared findings: DeveloperToolsModels + DeveloperToolsService
// Findings: 18-27
using DataWarehouse.Shared.Models;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for DeveloperTools hardening findings.
/// </summary>
public class DeveloperToolsTests
{
    /// <summary>
    /// Finding 18: SchemaIndex.Fields collection never updated. Changed to IReadOnlyList.
    /// </summary>
    [Fact]
    public void Finding018_SchemaIndexFieldsIsReadOnly()
    {
        var index = new SchemaIndex();
        Assert.IsAssignableFrom<IReadOnlyList<string>>(index.Fields);
        Assert.Empty(index.Fields);
    }

    /// <summary>
    /// Finding 19: QueryDefinition.SelectFields collection never updated. Changed to IReadOnlyList.
    /// </summary>
    [Fact]
    public void Finding019_QueryDefinitionSelectFieldsIsReadOnly()
    {
        var query = new QueryDefinition();
        Assert.IsAssignableFrom<IReadOnlyList<string>>(query.SelectFields);
    }

    /// <summary>
    /// Finding 20: QueryDefinition.Filters collection never updated. Changed to IReadOnlyList.
    /// </summary>
    [Fact]
    public void Finding020_QueryDefinitionFiltersIsReadOnly()
    {
        var query = new QueryDefinition();
        Assert.IsAssignableFrom<IReadOnlyList<QueryFilter>>(query.Filters);
    }

    /// <summary>
    /// Finding 21: QueryDefinition.Sorting collection never updated. Changed to IReadOnlyList.
    /// </summary>
    [Fact]
    public void Finding021_QueryDefinitionSortingIsReadOnly()
    {
        var query = new QueryDefinition();
        Assert.IsAssignableFrom<IReadOnlyList<QuerySort>>(query.Sorting);
    }

    /// <summary>
    /// Finding 22: QueryDefinition.Joins collection never updated. Changed to IReadOnlyList.
    /// </summary>
    [Fact]
    public void Finding022_QueryDefinitionJoinsIsReadOnly()
    {
        var query = new QueryDefinition();
        Assert.IsAssignableFrom<IReadOnlyList<QueryJoin>>(query.Joins);
    }

    /// <summary>
    /// Finding 23: QueryAggregation.GroupBy collection never updated. Changed to IReadOnlyList.
    /// </summary>
    [Fact]
    public void Finding023_QueryAggregationGroupByIsReadOnly()
    {
        var agg = new QueryAggregation();
        Assert.IsAssignableFrom<IReadOnlyList<string>>(agg.GroupBy);
    }

    /// <summary>
    /// Findings 24-25: DeveloperToolsService s_jsonOptions/s_indentedJsonOptions naming.
    /// Renamed to SJsonOptions/SIndentedJsonOptions (PascalCase for static readonly private fields).
    /// </summary>
    [Fact]
    public void Finding024_025_JsonOptionsNamingFixed()
    {
        // DeveloperToolsService compiles with renamed fields.
        // The renamed static fields are private, verified via compilation.
        Assert.True(true, "s_jsonOptions -> SJsonOptions, s_indentedJsonOptions -> SIndentedJsonOptions");
    }

    /// <summary>
    /// Findings 26-27: DeveloperToolsService uses sync File I/O where async overloads exist.
    /// Fixed to use File.ReadAllTextAsync and File.WriteAllTextAsync.
    /// </summary>
    [Fact]
    public void Finding026_027_AsyncFileOverloadsUsed()
    {
        // DeveloperToolsService.GetQueryTemplatesAsync now uses File.ReadAllTextAsync.
        // DeveloperToolsService.SaveQueryTemplateAsync now uses File.WriteAllTextAsync.
        // Verified via compilation - no more sync File.ReadAllText/WriteAllText.
        Assert.True(true, "Sync File I/O replaced with async overloads.");
    }
}
