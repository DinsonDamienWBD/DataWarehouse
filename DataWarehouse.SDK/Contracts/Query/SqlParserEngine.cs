using System.Collections.Immutable;

namespace DataWarehouse.SDK.Contracts.Query;

/// <summary>
/// Recursive-descent SQL parser. Converts a token stream from <see cref="SqlTokenizer"/>
/// into typed <see cref="SqlStatement"/> AST nodes.
///
/// Operator precedence (lowest to highest):
///   OR &lt; AND &lt; NOT &lt; comparison &lt; addition &lt; multiplication &lt; unary &lt; primary
/// </summary>
public sealed class SqlParserEngine : ISqlParser
{
    private IReadOnlyList<SqlToken> _tokens = Array.Empty<SqlToken>();
    private int _pos;

    // ─── ISqlParser ──────────────────────────────────────────

    public SqlStatement Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        _tokens = SqlTokenizer.Tokenize(sql);
        _pos = 0;
        var stmt = ParseStatement();
        // Allow optional trailing semicolon
        if (Current.Type == SqlTokenType.Semicolon)
            Advance();
        if (Current.Type != SqlTokenType.Eof)
            throw Error($"Unexpected token '{Current.Value}' after statement");
        return stmt;
    }

    public bool TryParse(string sql, out SqlStatement? result, out string? error)
    {
        try
        {
            result = Parse(sql);
            error = null;
            return true;
        }
        catch (SqlParseException ex)
        {
            result = null;
            error = ex.Message;
            return false;
        }
    }

    // ─── Statement dispatch ──────────────────────────────────

    private SqlStatement ParseStatement()
    {
        // Handle CTEs
        if (Current.Type == SqlTokenType.With)
            return ParseSelectWithCtes();

        return Current.Type switch
        {
            SqlTokenType.Select => ParseSelectStatement(ImmutableArray<CteDefinition>.Empty),
            _ => throw Error($"Expected SELECT, INSERT, UPDATE, or DELETE but found '{Current.Value}'")
        };
    }

    // ─── CTE ─────────────────────────────────────────────────

    private SelectStatement ParseSelectWithCtes()
    {
        Expect(SqlTokenType.With);
        var ctes = ImmutableArray.CreateBuilder<CteDefinition>();

        do
        {
            var name = ExpectIdentifierOrKeyword();

            // Optional column aliases: name(col1, col2)
            ImmutableArray<string>? columnAliases = null;
            if (Current.Type == SqlTokenType.LeftParen)
            {
                Advance();
                var cols = ImmutableArray.CreateBuilder<string>();
                cols.Add(ExpectIdentifierOrKeyword());
                while (Current.Type == SqlTokenType.Comma)
                {
                    Advance();
                    cols.Add(ExpectIdentifierOrKeyword());
                }
                Expect(SqlTokenType.RightParen);
                columnAliases = cols.ToImmutable();
            }

            Expect(SqlTokenType.As);
            Expect(SqlTokenType.LeftParen);
            var query = ParseSelectStatement(ImmutableArray<CteDefinition>.Empty);
            Expect(SqlTokenType.RightParen);

            ctes.Add(new CteDefinition(name, columnAliases, query));
        }
        while (Current.Type == SqlTokenType.Comma && AdvanceAndReturnTrue());

        return ParseSelectStatement(ctes.ToImmutable());
    }

    // ─── SELECT ──────────────────────────────────────────────

    private SelectStatement ParseSelectStatement(ImmutableArray<CteDefinition> ctes)
    {
        Expect(SqlTokenType.Select);

        // DISTINCT
        bool distinct = false;
        if (Current.Type == SqlTokenType.Distinct)
        {
            distinct = true;
            Advance();
        }

        // Column list
        var columns = ParseSelectColumns();

        // FROM
        TableReference? from = null;
        if (Current.Type == SqlTokenType.From)
        {
            Advance();
            from = ParseTableReference();
        }

        // JOINs (explicit and implicit comma-separated cross joins)
        var joins = ImmutableArray.CreateBuilder<JoinClause>();
        while (IsJoinKeyword(Current.Type) ||
               (from != null && Current.Type == SqlTokenType.Comma))
        {
            if (Current.Type == SqlTokenType.Comma)
            {
                // Comma-separated table = implicit CROSS JOIN
                Advance();
                var table = ParseTableReference();
                joins.Add(new JoinClause(JoinType.Cross, table, null));
            }
            else
            {
                joins.Add(ParseJoinClause());
            }
        }

        // WHERE
        Expression? where = null;
        if (Current.Type == SqlTokenType.Where)
        {
            Advance();
            where = ParseExpression();
        }

        // GROUP BY
        var groupBy = ImmutableArray<Expression>.Empty;
        if (Current.Type == SqlTokenType.Group)
        {
            Advance();
            Expect(SqlTokenType.By);
            groupBy = ParseExpressionList();
        }

        // HAVING
        Expression? having = null;
        if (Current.Type == SqlTokenType.Having)
        {
            Advance();
            having = ParseExpression();
        }

        // ORDER BY
        var orderBy = ImmutableArray<OrderByItem>.Empty;
        if (Current.Type == SqlTokenType.Order)
        {
            Advance();
            Expect(SqlTokenType.By);
            orderBy = ParseOrderByList();
        }

        // LIMIT
        int? limit = null;
        if (Current.Type == SqlTokenType.Limit)
        {
            Advance();
            limit = ExpectInteger();
        }

        // OFFSET
        int? offset = null;
        if (Current.Type == SqlTokenType.Offset)
        {
            Advance();
            offset = ExpectInteger();
        }

        return new SelectStatement(
            distinct, columns, from, joins.ToImmutable(),
            where, groupBy, having, orderBy,
            limit, offset, ctes);
    }

    // ─── Column list ─────────────────────────────────────────

    private ImmutableArray<SelectColumn> ParseSelectColumns()
    {
        var cols = ImmutableArray.CreateBuilder<SelectColumn>();
        cols.Add(ParseSelectColumn());
        while (Current.Type == SqlTokenType.Comma)
        {
            Advance();
            cols.Add(ParseSelectColumn());
        }
        return cols.ToImmutable();
    }

    private SelectColumn ParseSelectColumn()
    {
        var expr = ParseExpression();
        string? alias = null;

        if (Current.Type == SqlTokenType.As)
        {
            Advance();
            alias = ExpectIdentifierOrKeyword();
        }
        else if (Current.Type == SqlTokenType.Identifier || Current.Type == SqlTokenType.QuotedIdentifier)
        {
            // Implicit alias (no AS keyword) - but only if it's clearly an alias
            // Peek: if next token is comma, FROM, or Eof, this is an alias
            if (!IsClauseKeyword(Current.Type))
            {
                alias = Current.Value;
                Advance();
            }
        }

        return new SelectColumn(expr, alias);
    }

    // ─── Table reference ─────────────────────────────────────

    private TableReference ParseTableReference()
    {
        // Subquery as table reference
        if (Current.Type == SqlTokenType.LeftParen)
        {
            Advance();
            var subquery = ParseSelectStatement(ImmutableArray<CteDefinition>.Empty);
            Expect(SqlTokenType.RightParen);

            // Subquery must have alias
            string alias;
            if (Current.Type == SqlTokenType.As)
            {
                Advance();
                alias = ExpectIdentifierOrKeyword();
            }
            else
            {
                alias = ExpectIdentifierOrKeyword();
            }
            return new SubqueryTableReference(subquery, alias);
        }

        // Named table: [schema.]name [alias]
        var firstName = ExpectIdentifierOrKeyword();
        string? schema = null;
        string tableName;

        if (Current.Type == SqlTokenType.Dot)
        {
            Advance();
            schema = firstName;
            tableName = ExpectIdentifierOrKeyword();
        }
        else
        {
            tableName = firstName;
        }

        string? tableAlias = null;
        if (Current.Type == SqlTokenType.As)
        {
            Advance();
            tableAlias = ExpectIdentifierOrKeyword();
        }
        else if (Current.Type == SqlTokenType.Identifier || Current.Type == SqlTokenType.QuotedIdentifier)
        {
            // Implicit alias - only if not a keyword that starts a clause
            if (!IsClauseKeyword(Current.Type))
            {
                tableAlias = Current.Value;
                Advance();
            }
        }

        return new NamedTableReference(tableName, schema, tableAlias);
    }

    // ─── JOIN ────────────────────────────────────────────────

    private JoinClause ParseJoinClause()
    {
        var joinType = ParseJoinType();
        var table = ParseTableReference();
        Expression? onCondition = null;

        if (joinType != JoinType.Cross && Current.Type == SqlTokenType.On)
        {
            Advance();
            onCondition = ParseExpression();
        }

        return new JoinClause(joinType, table, onCondition);
    }

    private JoinType ParseJoinType()
    {
        var type = Current.Type;

        if (type == SqlTokenType.Inner)
        {
            Advance();
            Expect(SqlTokenType.Join);
            return JoinType.Inner;
        }
        if (type == SqlTokenType.Left)
        {
            Advance();
            if (Current.Type == SqlTokenType.Outer) Advance();
            Expect(SqlTokenType.Join);
            return JoinType.Left;
        }
        if (type == SqlTokenType.Right)
        {
            Advance();
            if (Current.Type == SqlTokenType.Outer) Advance();
            Expect(SqlTokenType.Join);
            return JoinType.Right;
        }
        if (type == SqlTokenType.Full)
        {
            Advance();
            if (Current.Type == SqlTokenType.Outer) Advance();
            Expect(SqlTokenType.Join);
            return JoinType.Full;
        }
        if (type == SqlTokenType.Cross)
        {
            Advance();
            Expect(SqlTokenType.Join);
            return JoinType.Cross;
        }
        if (type == SqlTokenType.Join)
        {
            Advance();
            return JoinType.Inner; // plain JOIN = INNER JOIN
        }

        throw Error($"Expected JOIN keyword but found '{Current.Value}'");
    }

    // ─── ORDER BY ────────────────────────────────────────────

    private ImmutableArray<OrderByItem> ParseOrderByList()
    {
        var items = ImmutableArray.CreateBuilder<OrderByItem>();
        items.Add(ParseOrderByItem());
        while (Current.Type == SqlTokenType.Comma)
        {
            Advance();
            items.Add(ParseOrderByItem());
        }
        return items.ToImmutable();
    }

    private OrderByItem ParseOrderByItem()
    {
        var expr = ParseExpression();
        bool ascending = true;
        if (Current.Type == SqlTokenType.Asc)
        {
            Advance();
        }
        else if (Current.Type == SqlTokenType.Desc)
        {
            ascending = false;
            Advance();
        }
        return new OrderByItem(expr, ascending);
    }

    // ─── Expressions (precedence climbing) ───────────────────

    private Expression ParseExpression() => ParseOr();

    private Expression ParseOr()
    {
        var left = ParseAnd();
        while (Current.Type == SqlTokenType.Or)
        {
            Advance();
            var right = ParseAnd();
            left = new BinaryExpression(left, BinaryOperator.Or, right);
        }
        return left;
    }

    private Expression ParseAnd()
    {
        var left = ParseNot();
        while (Current.Type == SqlTokenType.And)
        {
            Advance();
            var right = ParseNot();
            left = new BinaryExpression(left, BinaryOperator.And, right);
        }
        return left;
    }

    private Expression ParseNot()
    {
        if (Current.Type == SqlTokenType.Not)
        {
            Advance();
            var operand = ParseNot();
            return new UnaryExpression(UnaryOperator.Not, operand);
        }
        return ParseComparison();
    }

    private Expression ParseComparison()
    {
        var left = ParseAddition();

        // IS [NOT] NULL
        if (Current.Type == SqlTokenType.Is)
        {
            Advance();
            bool negated = false;
            if (Current.Type == SqlTokenType.Not)
            {
                negated = true;
                Advance();
            }
            Expect(SqlTokenType.Null);
            return new IsNullExpression(left, negated);
        }

        // [NOT] IN
        bool notPrefix = false;
        if (Current.Type == SqlTokenType.Not)
        {
            // Peek ahead to see if it's NOT IN, NOT BETWEEN, NOT LIKE, NOT EXISTS
            var next = Peek(1);
            if (next.Type == SqlTokenType.In || next.Type == SqlTokenType.Between || next.Type == SqlTokenType.Like)
            {
                notPrefix = true;
                Advance();
            }
        }

        if (Current.Type == SqlTokenType.In)
        {
            Advance();
            Expect(SqlTokenType.LeftParen);

            // Check if subquery or value list
            if (Current.Type == SqlTokenType.Select || Current.Type == SqlTokenType.With)
            {
                var subquery = ParseSelectStatement(ImmutableArray<CteDefinition>.Empty);
                Expect(SqlTokenType.RightParen);
                return new InExpression(left, null, subquery, notPrefix);
            }
            else
            {
                var values = ParseExpressionList();
                Expect(SqlTokenType.RightParen);
                return new InExpression(left, values, null, notPrefix);
            }
        }

        // [NOT] BETWEEN
        if (Current.Type == SqlTokenType.Between)
        {
            Advance();
            var low = ParseAddition();
            Expect(SqlTokenType.And);
            var high = ParseAddition();
            return new BetweenExpression(left, low, high, notPrefix);
        }

        // [NOT] LIKE
        if (Current.Type == SqlTokenType.Like)
        {
            Advance();
            var pattern = ParseAddition();
            Expression? escape = null;
            if (Current.Type == SqlTokenType.Escape)
            {
                Advance();
                escape = ParsePrimary();
            }
            return new LikeExpression(left, pattern, escape, notPrefix);
        }

        // Standard comparison operators
        if (IsComparisonOperator(Current.Type))
        {
            var op = MapBinaryOperator(Current.Type);
            Advance();
            var right = ParseAddition();
            return new BinaryExpression(left, op, right);
        }

        return left;
    }

    private Expression ParseAddition()
    {
        var left = ParseMultiplication();
        while (Current.Type == SqlTokenType.Plus || Current.Type == SqlTokenType.Minus)
        {
            var op = Current.Type == SqlTokenType.Plus ? BinaryOperator.Plus : BinaryOperator.Minus;
            Advance();
            var right = ParseMultiplication();
            left = new BinaryExpression(left, op, right);
        }
        return left;
    }

    private Expression ParseMultiplication()
    {
        var left = ParseUnary();
        while (Current.Type == SqlTokenType.Asterisk || Current.Type == SqlTokenType.Slash || Current.Type == SqlTokenType.Percent)
        {
            var op = Current.Type switch
            {
                SqlTokenType.Asterisk => BinaryOperator.Multiply,
                SqlTokenType.Slash => BinaryOperator.Divide,
                _ => BinaryOperator.Modulo
            };
            Advance();
            var right = ParseUnary();
            left = new BinaryExpression(left, op, right);
        }
        return left;
    }

    private Expression ParseUnary()
    {
        if (Current.Type == SqlTokenType.Minus)
        {
            Advance();
            var operand = ParsePrimary();
            return new UnaryExpression(UnaryOperator.Negate, operand);
        }
        return ParsePrimary();
    }

    // ─── Primary expressions ─────────────────────────────────

    private Expression ParsePrimary()
    {
        var token = Current;

        switch (token.Type)
        {
            // Numeric literal
            case SqlTokenType.Number:
                Advance();
                if (token.Value.Contains('.'))
                    return new LiteralExpression(decimal.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture), LiteralType.Decimal);
                return new LiteralExpression(long.Parse(token.Value, System.Globalization.CultureInfo.InvariantCulture), LiteralType.Integer);

            // String literal
            case SqlTokenType.StringLiteral:
                Advance();
                return new LiteralExpression(token.Value, LiteralType.String);

            // Boolean
            case SqlTokenType.True:
                Advance();
                return new LiteralExpression(true, LiteralType.Boolean);

            case SqlTokenType.False:
                Advance();
                return new LiteralExpression(false, LiteralType.Boolean);

            // NULL
            case SqlTokenType.Null:
                Advance();
                return new LiteralExpression(null, LiteralType.Null);

            // EXISTS (subquery)
            case SqlTokenType.Exists:
                Advance();
                Expect(SqlTokenType.LeftParen);
                var existsQuery = ParseSelectStatement(ImmutableArray<CteDefinition>.Empty);
                Expect(SqlTokenType.RightParen);
                return new ExistsExpression(existsQuery);

            // CASE expression
            case SqlTokenType.Case:
                return ParseCaseExpression();

            // CAST expression
            case SqlTokenType.Cast:
                return ParseCastExpression();

            // NOT (at primary level for things like NOT EXISTS)
            case SqlTokenType.Not:
                Advance();
                var notOperand = ParsePrimary();
                return new UnaryExpression(UnaryOperator.Not, notOperand);

            // Asterisk (wildcard)
            case SqlTokenType.Asterisk:
                Advance();
                return new WildcardExpression(null);

            // Parenthesized expression or subquery
            case SqlTokenType.LeftParen:
                Advance();
                if (Current.Type == SqlTokenType.Select || Current.Type == SqlTokenType.With)
                {
                    var subquery = ParseSelectStatement(ImmutableArray<CteDefinition>.Empty);
                    Expect(SqlTokenType.RightParen);
                    return new SubqueryExpression(subquery);
                }
                var inner = ParseExpression();
                Expect(SqlTokenType.RightParen);
                return new ParenthesizedExpression(inner);

            // Identifier (column, table.column, or function call)
            case SqlTokenType.Identifier:
            case SqlTokenType.QuotedIdentifier:
                return ParseIdentifierExpression();

            default:
                throw Error($"Expected expression but found '{token.Value}'");
        }
    }

    private Expression ParseIdentifierExpression()
    {
        var name = Current.Value;
        Advance();

        // Function call: name(...)
        if (Current.Type == SqlTokenType.LeftParen)
        {
            return ParseFunctionCall(name);
        }

        // table.column or table.*
        if (Current.Type == SqlTokenType.Dot)
        {
            Advance();
            if (Current.Type == SqlTokenType.Asterisk)
            {
                Advance();
                return new WildcardExpression(name);
            }
            var colName = ExpectIdentifierOrKeyword();

            // Could also be schema.table.column (rare but valid)
            // For simplicity we treat the first part as table qualifier
            return new ColumnReference(name, colName);
        }

        // Simple column reference
        return new ColumnReference(null, name);
    }

    private Expression ParseFunctionCall(string functionName)
    {
        Expect(SqlTokenType.LeftParen);

        // COUNT(*) special case
        if (Current.Type == SqlTokenType.Asterisk &&
            string.Equals(functionName, "COUNT", StringComparison.OrdinalIgnoreCase))
        {
            Advance();
            Expect(SqlTokenType.RightParen);
            return new FunctionCallExpression(functionName,
                ImmutableArray.Create<Expression>(new WildcardExpression(null)), false);
        }

        // Empty argument list
        if (Current.Type == SqlTokenType.RightParen)
        {
            Advance();
            return new FunctionCallExpression(functionName, ImmutableArray<Expression>.Empty, false);
        }

        // DISTINCT inside aggregate
        bool distinct = false;
        if (Current.Type == SqlTokenType.Distinct)
        {
            distinct = true;
            Advance();
        }

        var args = ParseExpressionList();
        Expect(SqlTokenType.RightParen);

        return new FunctionCallExpression(functionName, args, distinct);
    }

    // ─── CASE ────────────────────────────────────────────────

    private CaseExpression ParseCaseExpression()
    {
        Expect(SqlTokenType.Case);

        // Simple CASE (CASE expr WHEN ...) vs searched CASE (CASE WHEN cond ...)
        Expression? operand = null;
        if (Current.Type != SqlTokenType.When)
        {
            operand = ParseExpression();
        }

        var whenClauses = ImmutableArray.CreateBuilder<WhenClause>();
        while (Current.Type == SqlTokenType.When)
        {
            Advance();
            var condition = ParseExpression();
            Expect(SqlTokenType.Then);
            var result = ParseExpression();
            whenClauses.Add(new WhenClause(condition, result));
        }

        if (whenClauses.Count == 0)
            throw Error("CASE expression must have at least one WHEN clause");

        Expression? elseExpr = null;
        if (Current.Type == SqlTokenType.Else)
        {
            Advance();
            elseExpr = ParseExpression();
        }

        Expect(SqlTokenType.End);

        return new CaseExpression(operand, whenClauses.ToImmutable(), elseExpr);
    }

    // ─── CAST ────────────────────────────────────────────────

    private CastExpression ParseCastExpression()
    {
        Expect(SqlTokenType.Cast);
        Expect(SqlTokenType.LeftParen);
        var operand = ParseExpression();
        Expect(SqlTokenType.As);

        // Type name can be multi-word (e.g., "DOUBLE PRECISION", "CHARACTER VARYING")
        var typeName = ExpectIdentifierOrKeyword();

        // Handle type parameters like VARCHAR(255) or DECIMAL(10,2)
        if (Current.Type == SqlTokenType.LeftParen)
        {
            Advance();
            typeName += "(" + Current.Value;
            Advance();
            while (Current.Type == SqlTokenType.Comma)
            {
                Advance();
                typeName += "," + Current.Value;
                Advance();
            }
            Expect(SqlTokenType.RightParen);
            typeName += ")";
        }

        Expect(SqlTokenType.RightParen);
        return new CastExpression(operand, typeName);
    }

    // ─── Helpers ─────────────────────────────────────────────

    private ImmutableArray<Expression> ParseExpressionList()
    {
        var list = ImmutableArray.CreateBuilder<Expression>();
        list.Add(ParseExpression());
        while (Current.Type == SqlTokenType.Comma)
        {
            Advance();
            list.Add(ParseExpression());
        }
        return list.ToImmutable();
    }

    private SqlToken Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];

    private SqlToken Peek(int offset)
    {
        int idx = _pos + offset;
        return idx < _tokens.Count ? _tokens[idx] : _tokens[^1];
    }

    private SqlToken Advance()
    {
        var token = Current;
        if (_pos < _tokens.Count - 1) _pos++;
        return token;
    }

    private bool AdvanceAndReturnTrue()
    {
        Advance();
        return true;
    }

    private void Expect(SqlTokenType type)
    {
        if (Current.Type != type)
            throw Error($"Expected {type} but found '{Current.Value}'");
        Advance();
    }

    private string ExpectIdentifierOrKeyword()
    {
        var token = Current;
        // Accept identifiers, quoted identifiers, and keywords used as identifiers
        if (token.Type == SqlTokenType.Identifier || token.Type == SqlTokenType.QuotedIdentifier ||
            IsKeywordUsableAsIdentifier(token.Type))
        {
            Advance();
            return token.Value;
        }
        throw Error($"Expected identifier but found '{token.Value}'");
    }

    private int ExpectInteger()
    {
        if (Current.Type != SqlTokenType.Number)
            throw Error($"Expected integer but found '{Current.Value}'");
        var value = Current.Value;
        Advance();
        if (!int.TryParse(value, out int result))
            throw Error($"Expected integer but found '{value}'");
        return result;
    }

    private SqlParseException Error(string message)
    {
        var token = Current;
        return new SqlParseException(message, token.Line, token.Column, token.Value);
    }

    // ─── Token classification ────────────────────────────────

    private static bool IsJoinKeyword(SqlTokenType type) =>
        type is SqlTokenType.Join or SqlTokenType.Inner or SqlTokenType.Left
            or SqlTokenType.Right or SqlTokenType.Full or SqlTokenType.Cross;

    private static bool IsComparisonOperator(SqlTokenType type) =>
        type is SqlTokenType.Equals or SqlTokenType.NotEquals
            or SqlTokenType.LessThan or SqlTokenType.GreaterThan
            or SqlTokenType.LessOrEqual or SqlTokenType.GreaterOrEqual;

    private static BinaryOperator MapBinaryOperator(SqlTokenType type) => type switch
    {
        SqlTokenType.Equals => BinaryOperator.Equals,
        SqlTokenType.NotEquals => BinaryOperator.NotEquals,
        SqlTokenType.LessThan => BinaryOperator.LessThan,
        SqlTokenType.GreaterThan => BinaryOperator.GreaterThan,
        SqlTokenType.LessOrEqual => BinaryOperator.LessOrEqual,
        SqlTokenType.GreaterOrEqual => BinaryOperator.GreaterOrEqual,
        _ => throw new InvalidOperationException($"Not a comparison operator: {type}")
    };

    private static bool IsClauseKeyword(SqlTokenType type) =>
        type is SqlTokenType.From or SqlTokenType.Where or SqlTokenType.Group
            or SqlTokenType.Having or SqlTokenType.Order or SqlTokenType.Limit
            or SqlTokenType.Offset or SqlTokenType.Join or SqlTokenType.Inner
            or SqlTokenType.Left or SqlTokenType.Right or SqlTokenType.Full
            or SqlTokenType.Cross or SqlTokenType.On or SqlTokenType.Union
            or SqlTokenType.Eof or SqlTokenType.RightParen or SqlTokenType.Semicolon;

    private static bool IsKeywordUsableAsIdentifier(SqlTokenType type) =>
        // Many SQL keywords can appear as identifiers in certain contexts
        type is SqlTokenType.Select or SqlTokenType.From or SqlTokenType.Where
            or SqlTokenType.Group or SqlTokenType.By or SqlTokenType.Order
            or SqlTokenType.Asc or SqlTokenType.Desc or SqlTokenType.Limit
            or SqlTokenType.Offset or SqlTokenType.Having or SqlTokenType.Distinct
            or SqlTokenType.As or SqlTokenType.And or SqlTokenType.Or
            or SqlTokenType.Not or SqlTokenType.In or SqlTokenType.Between
            or SqlTokenType.Like or SqlTokenType.Is or SqlTokenType.Null
            or SqlTokenType.Exists or SqlTokenType.Case or SqlTokenType.When
            or SqlTokenType.Then or SqlTokenType.Else or SqlTokenType.End
            or SqlTokenType.Cast or SqlTokenType.With or SqlTokenType.Union
            or SqlTokenType.All or SqlTokenType.Insert or SqlTokenType.Update
            or SqlTokenType.Delete or SqlTokenType.Set or SqlTokenType.Values
            or SqlTokenType.Into or SqlTokenType.True or SqlTokenType.False
            or SqlTokenType.Join or SqlTokenType.Inner or SqlTokenType.Left
            or SqlTokenType.Right or SqlTokenType.Full or SqlTokenType.Cross
            or SqlTokenType.On or SqlTokenType.Escape or SqlTokenType.Outer;
}
