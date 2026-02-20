using DataWarehouse.SDK.Contracts.Query;
using FluentAssertions;
using System.Collections.Immutable;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Integration tests for the SQL query engine pipeline: parse -> AST -> plan.
/// Tests the SqlParserEngine for SQL parsing correctness, error handling,
/// and AST construction across various SQL statement types.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Scope", "QueryEngine")]
public class QueryEngineIntegrationTests
{
    private readonly SqlParserEngine _parser = new();

    // ==================== Basic SELECT ====================

    [Fact]
    public void Parse_SimpleSelect_ReturnsSelectStatement()
    {
        var result = _parser.Parse("SELECT * FROM test_table");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.From.Should().BeOfType<NamedTableReference>();
        ((NamedTableReference)select.From!).TableName.Should().Be("test_table");
    }

    [Fact]
    public void Parse_SelectWithColumns_ParsesColumnList()
    {
        var result = _parser.Parse("SELECT id, name, age FROM users");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Columns.Length.Should().Be(3);
    }

    [Fact]
    public void Parse_SelectWithAlias_ParsesAliases()
    {
        var result = _parser.Parse("SELECT name AS full_name FROM users u");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Columns[0].Alias.Should().Be("full_name");
        ((NamedTableReference)select.From!).Alias.Should().Be("u");
    }

    // ==================== WHERE Clause ====================

    [Fact]
    public void Parse_SelectWithWhere_ParsesFilter()
    {
        var result = _parser.Parse("SELECT * FROM users WHERE age > 18");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Where.Should().NotBeNull();
    }

    [Fact]
    public void Parse_SelectWithComplexWhere_ParsesAndOr()
    {
        var result = _parser.Parse("SELECT * FROM users WHERE age > 18 AND status = 'active' OR role = 'admin'");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Where.Should().NotBeNull();
    }

    // ==================== JOIN Clause ====================

    [Fact]
    public void Parse_SelectWithJoin_ParsesJoinClause()
    {
        var result = _parser.Parse("SELECT u.name, o.total FROM users u JOIN orders o ON u.id = o.user_id");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Joins.Length.Should().Be(1);
    }

    [Fact]
    public void Parse_SelectWithLeftJoin_ParsesJoinType()
    {
        var result = _parser.Parse("SELECT u.name, o.total FROM users u LEFT JOIN orders o ON u.id = o.user_id");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Joins.Length.Should().Be(1);
    }

    // ==================== GROUP BY / HAVING ====================

    [Fact]
    public void Parse_SelectWithGroupBy_ParsesAggregation()
    {
        var result = _parser.Parse("SELECT department, COUNT(*) FROM employees GROUP BY department");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.GroupBy.Length.Should().Be(1);
    }

    [Fact]
    public void Parse_SelectWithGroupByHaving_ParsesHavingClause()
    {
        var result = _parser.Parse("SELECT department, COUNT(*) AS cnt FROM employees GROUP BY department HAVING COUNT(*) > 5");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.GroupBy.Length.Should().Be(1);
        select.Having.Should().NotBeNull();
    }

    // ==================== ORDER BY ====================

    [Fact]
    public void Parse_SelectWithOrderBy_ParsesSorting()
    {
        var result = _parser.Parse("SELECT * FROM users ORDER BY name ASC, age DESC");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.OrderBy.Length.Should().Be(2);
        select.OrderBy[0].Ascending.Should().BeTrue();
        select.OrderBy[1].Ascending.Should().BeFalse();
    }

    // ==================== LIMIT / OFFSET ====================

    [Fact]
    public void Parse_SelectWithLimit_ParsesPagination()
    {
        var result = _parser.Parse("SELECT * FROM users LIMIT 10");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Limit.Should().Be(10);
    }

    [Fact]
    public void Parse_SelectWithLimitOffset_ParsesBoth()
    {
        var result = _parser.Parse("SELECT * FROM users LIMIT 10 OFFSET 20");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Limit.Should().Be(10);
        select.Offset.Should().Be(20);
    }

    // ==================== DISTINCT ====================

    [Fact]
    public void Parse_SelectDistinct_SetsDistinctFlag()
    {
        var result = _parser.Parse("SELECT DISTINCT department FROM employees");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Distinct.Should().BeTrue();
    }

    // ==================== Subqueries / CTEs ====================

    [Fact]
    public void Parse_WithCte_ParsesCteDefinition()
    {
        var sql = "WITH active_users AS (SELECT * FROM users WHERE status = 'active') SELECT * FROM active_users";
        var result = _parser.Parse(sql);

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Ctes.Length.Should().Be(1);
        select.Ctes[0].Name.Should().Be("active_users");
    }

    // ==================== Aggregate Functions ====================

    [Fact]
    public void Parse_AggregateFunctions_ParsesCorrectly()
    {
        var result = _parser.Parse("SELECT COUNT(*), SUM(amount), AVG(price), MIN(id), MAX(id) FROM orders");

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Columns.Length.Should().Be(5);
    }

    // ==================== Error Handling ====================

    [Fact]
    public void Parse_InvalidSql_ThrowsSqlParseException()
    {
        Action act = () => _parser.Parse("SELECTX * FROM table");

        act.Should().Throw<SqlParseException>();
    }

    [Fact]
    public void Parse_EmptySql_ThrowsException()
    {
        Action act = () => _parser.Parse("");

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Parse_NullSql_ThrowsArgumentNullException()
    {
        Action act = () => _parser.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Parse_IncompleteSql_ThrowsSqlParseException()
    {
        Action act = () => _parser.Parse("SELECT FROM");

        act.Should().Throw<SqlParseException>();
    }

    [Fact]
    public void TryParse_InvalidSql_ReturnsFalseWithError()
    {
        var success = _parser.TryParse("SELECTX *", out var result, out var error);

        success.Should().BeFalse();
        result.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryParse_ValidSql_ReturnsTrueWithResult()
    {
        var success = _parser.TryParse("SELECT 1", out var result, out var error);

        success.Should().BeTrue();
        result.Should().NotBeNull();
        error.Should().BeNull();
    }

    // ==================== Complex Queries ====================

    [Fact]
    public void Parse_ComplexQuery_AllClausesCombined()
    {
        var sql = @"
            SELECT DISTINCT u.name, COUNT(o.id) AS order_count
            FROM users u
            JOIN orders o ON u.id = o.user_id
            WHERE u.status = 'active'
            GROUP BY u.name
            HAVING COUNT(o.id) > 5
            ORDER BY order_count DESC
            LIMIT 100
            OFFSET 0";

        var result = _parser.Parse(sql);

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Distinct.Should().BeTrue();
        select.Columns.Length.Should().Be(2);
        select.Joins.Length.Should().Be(1);
        select.Where.Should().NotBeNull();
        select.GroupBy.Length.Should().Be(1);
        select.Having.Should().NotBeNull();
        select.OrderBy.Length.Should().Be(1);
        select.Limit.Should().Be(100);
        select.Offset.Should().Be(0);
    }

    [Fact]
    public void Parse_NestedSubquery_InWhereClause()
    {
        var sql = "SELECT * FROM users WHERE id IN (SELECT user_id FROM orders WHERE total > 100)";

        var result = _parser.Parse(sql);

        result.Should().BeOfType<SelectStatement>();
        var select = (SelectStatement)result;
        select.Where.Should().NotBeNull();
    }

    // ==================== Query Planner ====================

    [Fact]
    public void CostBasedPlanner_SimpleSelect_ProducesPlan()
    {
        var sql = "SELECT * FROM test_table WHERE id > 10";
        var stmt = _parser.Parse(sql) as SelectStatement;

        var planner = new CostBasedQueryPlanner();
        var plan = planner.Plan(stmt!);

        plan.Should().NotBeNull();
    }

    [Fact]
    public void CostBasedPlanner_JoinQuery_ProducesPlan()
    {
        var sql = "SELECT u.name, o.total FROM users u JOIN orders o ON u.id = o.user_id";
        var stmt = _parser.Parse(sql) as SelectStatement;

        var planner = new CostBasedQueryPlanner();
        var plan = planner.Plan(stmt!);

        plan.Should().NotBeNull();
    }

    [Fact]
    public void CostBasedPlanner_AggregateQuery_ProducesPlan()
    {
        var sql = "SELECT department, COUNT(*) FROM employees GROUP BY department";
        var stmt = _parser.Parse(sql) as SelectStatement;

        var planner = new CostBasedQueryPlanner();
        var plan = planner.Plan(stmt!);

        plan.Should().NotBeNull();
    }

    // ==================== Unicode ====================

    [Fact]
    public void Parse_UnicodeIdentifiers_ParsesCorrectly()
    {
        // Table names with unicode should be handled
        var result = _parser.Parse("SELECT * FROM users WHERE name = 'J\u00f6rg M\u00fcller'");

        result.Should().BeOfType<SelectStatement>();
    }

    // ==================== Semicolons and Trailing ====================

    [Fact]
    public void Parse_TrailingSemicolon_ParsesCorrectly()
    {
        var result = _parser.Parse("SELECT 1;");

        result.Should().BeOfType<SelectStatement>();
    }
}
