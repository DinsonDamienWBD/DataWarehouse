using System.Collections.Immutable;

namespace DataWarehouse.SDK.Contracts.Query;

// ─────────────────────────────────────────────────────────────
// Statements
// ─────────────────────────────────────────────────────────────

/// <summary>Base type for all SQL statements.</summary>
public abstract record SqlStatement;

/// <summary>A fully parsed SELECT statement including CTEs, joins, grouping, ordering, and limits.</summary>
public sealed record SelectStatement(
    bool Distinct,
    ImmutableArray<SelectColumn> Columns,
    TableReference? From,
    ImmutableArray<JoinClause> Joins,
    Expression? Where,
    ImmutableArray<Expression> GroupBy,
    Expression? Having,
    ImmutableArray<OrderByItem> OrderBy,
    int? Limit,
    int? Offset,
    ImmutableArray<CteDefinition> Ctes
) : SqlStatement;

/// <summary>Placeholder for INSERT (not fully implemented yet).</summary>
public sealed record InsertStatement(
    string Table,
    string? Schema,
    ImmutableArray<string> Columns,
    ImmutableArray<ImmutableArray<Expression>> Values
) : SqlStatement;

/// <summary>Placeholder for UPDATE (not fully implemented yet).</summary>
public sealed record UpdateStatement(
    string Table,
    string? Schema,
    ImmutableArray<(string Column, Expression Value)> SetClauses,
    Expression? Where
) : SqlStatement;

/// <summary>Placeholder for DELETE (not fully implemented yet).</summary>
public sealed record DeleteStatement(
    string Table,
    string? Schema,
    Expression? Where
) : SqlStatement;

// ─────────────────────────────────────────────────────────────
// Select components
// ─────────────────────────────────────────────────────────────

/// <summary>A column in a SELECT list, optionally aliased.</summary>
public sealed record SelectColumn(Expression Expression, string? Alias);

/// <summary>An item in an ORDER BY clause.</summary>
public sealed record OrderByItem(Expression Expression, bool Ascending);

/// <summary>A Common Table Expression (WITH name AS (...)).</summary>
public sealed record CteDefinition(
    string Name,
    ImmutableArray<string>? ColumnAliases,
    SelectStatement Query
);

// ─────────────────────────────────────────────────────────────
// Table references
// ─────────────────────────────────────────────────────────────

/// <summary>Base type for table references in FROM and JOIN clauses.</summary>
public abstract record TableReference;

/// <summary>A named table reference, optionally schema-qualified and aliased.</summary>
public sealed record NamedTableReference(
    string TableName,
    string? Schema,
    string? Alias
) : TableReference;

/// <summary>A subquery used as a table reference (derived table).</summary>
public sealed record SubqueryTableReference(
    SelectStatement Subquery,
    string Alias
) : TableReference;

// ─────────────────────────────────────────────────────────────
// Joins
// ─────────────────────────────────────────────────────────────

/// <summary>The type of JOIN operation.</summary>
public enum JoinType
{
    Inner,
    Left,
    Right,
    Full,
    Cross
}

/// <summary>A JOIN clause linking two table references.</summary>
public sealed record JoinClause(
    JoinType JoinType,
    TableReference Table,
    Expression? OnCondition
);

// ─────────────────────────────────────────────────────────────
// Expressions
// ─────────────────────────────────────────────────────────────

/// <summary>Base type for all SQL expressions.</summary>
public abstract record Expression;

/// <summary>A literal value (string, integer, decimal, boolean, or null).</summary>
public sealed record LiteralExpression(object? Value, LiteralType LiteralType) : Expression;

/// <summary>The type of a literal value.</summary>
public enum LiteralType
{
    String,
    Integer,
    Decimal,
    Boolean,
    Null
}

/// <summary>A column reference, optionally table-qualified.</summary>
public sealed record ColumnReference(string? Table, string Column) : Expression;

/// <summary>A wildcard (*) or table-qualified wildcard (t.*).</summary>
public sealed record WildcardExpression(string? Table) : Expression;

/// <summary>A binary operation (left op right).</summary>
public sealed record BinaryExpression(Expression Left, BinaryOperator Operator, Expression Right) : Expression;

/// <summary>A unary operation (NOT, unary minus).</summary>
public sealed record UnaryExpression(UnaryOperator Operator, Expression Operand) : Expression;

/// <summary>Unary operators.</summary>
public enum UnaryOperator
{
    Not,
    Negate
}

/// <summary>A function call expression (e.g., COUNT(*), SUM(x), UPPER(name)).</summary>
public sealed record FunctionCallExpression(
    string FunctionName,
    ImmutableArray<Expression> Arguments,
    bool Distinct
) : Expression;

/// <summary>expr IN (value1, value2, ...) or expr IN (subquery).</summary>
public sealed record InExpression(
    Expression Operand,
    ImmutableArray<Expression>? Values,
    SelectStatement? Subquery,
    bool Negated
) : Expression;

/// <summary>expr BETWEEN low AND high.</summary>
public sealed record BetweenExpression(
    Expression Operand,
    Expression Low,
    Expression High,
    bool Negated
) : Expression;

/// <summary>expr LIKE pattern [ESCAPE escape].</summary>
public sealed record LikeExpression(
    Expression Operand,
    Expression Pattern,
    Expression? Escape,
    bool Negated
) : Expression;

/// <summary>expr IS [NOT] NULL.</summary>
public sealed record IsNullExpression(
    Expression Operand,
    bool Negated
) : Expression;

/// <summary>EXISTS (subquery).</summary>
public sealed record ExistsExpression(SelectStatement Subquery) : Expression;

/// <summary>A subquery used as a scalar expression: (SELECT ...).</summary>
public sealed record SubqueryExpression(SelectStatement Subquery) : Expression;

/// <summary>CASE [operand] WHEN ... THEN ... [ELSE ...] END.</summary>
public sealed record CaseExpression(
    Expression? Operand,
    ImmutableArray<WhenClause> WhenClauses,
    Expression? ElseExpression
) : Expression;

/// <summary>A WHEN-THEN pair inside a CASE expression.</summary>
public sealed record WhenClause(Expression Condition, Expression Result);

/// <summary>CAST(expr AS type).</summary>
public sealed record CastExpression(Expression Operand, string TargetType) : Expression;

/// <summary>A parenthesized expression (preserved for round-tripping).</summary>
public sealed record ParenthesizedExpression(Expression Inner) : Expression;

// ─────────────────────────────────────────────────────────────
// Binary operators
// ─────────────────────────────────────────────────────────────

/// <summary>All supported binary operators.</summary>
public enum BinaryOperator
{
    // Logical
    And,
    Or,

    // Comparison
    Equals,
    NotEquals,
    LessThan,
    GreaterThan,
    LessOrEqual,
    GreaterOrEqual,

    // Arithmetic
    Plus,
    Minus,
    Multiply,
    Divide,
    Modulo
}
