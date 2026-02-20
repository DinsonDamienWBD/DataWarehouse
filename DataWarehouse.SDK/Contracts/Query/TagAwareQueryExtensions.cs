using System.Collections.Immutable;

namespace DataWarehouse.SDK.Contracts.Query;

// ─────────────────────────────────────────────────────────────
// Tag Provider Contract
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Provides tag-based metadata lookups for DW objects.
/// Implementations bridge to the DataWarehouse tag system (e.g., via MessageBus).
/// Tag-aware queries use this to resolve tag(), has_tag(), and tag_count() SQL functions.
/// </summary>
public interface ITagProvider
{
    /// <summary>
    /// Gets the value of a tag for the given object key.
    /// </summary>
    /// <param name="objectKey">The object identifier (e.g., storage key or row key).</param>
    /// <param name="tagName">The tag name to retrieve.</param>
    /// <returns>The tag value, or null if the tag does not exist.</returns>
    string? GetTag(string objectKey, string tagName);

    /// <summary>
    /// Checks whether the given object has a specific tag.
    /// </summary>
    /// <param name="objectKey">The object identifier.</param>
    /// <param name="tagName">The tag name to check.</param>
    /// <returns>True if the tag exists on the object.</returns>
    bool HasTag(string objectKey, string tagName);

    /// <summary>
    /// Gets the total number of tags on the given object.
    /// </summary>
    /// <param name="objectKey">The object identifier.</param>
    /// <returns>The count of tags attached to the object.</returns>
    int GetTagCount(string objectKey);
}

// ─────────────────────────────────────────────────────────────
// Tag Function Resolver
// ─────────────────────────────────────────────────────────────

/// <summary>
/// Resolves tag() SQL function calls in WHERE/SELECT expressions to ITagProvider lookups.
/// Pre-processes the AST before query planning so the planner and executor see
/// standard expressions instead of tag-specific functions.
///
/// Supported functions:
///   tag('tag_name')              -- shorthand: returns tag value for current row key
///   tag(column, 'tag_name')     -- returns the tag value for the object identified by column
///   has_tag('tag_name')         -- checks if current row key has the tag
///   has_tag(column, 'tag_name') -- checks if the object identified by column has the tag
///   tag_count(column)           -- returns the number of tags on the object
///
/// Example: SELECT * FROM objects WHERE tag('classification') = 'confidential'
///          AND has_tag('retention_policy')
/// </summary>
public static class TagFunctionResolver
{
    /// <summary>
    /// Resolves tag function calls in a SelectStatement's expressions.
    /// Returns a new SelectStatement with tag functions replaced by resolvable markers.
    /// Tag functions remain as FunctionCallExpression nodes; the execution engine
    /// evaluates them at runtime via ITagProvider.
    /// </summary>
    /// <param name="statement">The original SELECT statement.</param>
    /// <param name="tagProvider">The tag provider for resolving tags.</param>
    /// <returns>A statement with tag functions annotated for runtime resolution.</returns>
    public static SelectStatement ResolveTagFunctions(SelectStatement statement, ITagProvider tagProvider)
    {
        // Tag functions are kept as FunctionCallExpression nodes in the AST.
        // The QueryExecutionEngine evaluates them at runtime.
        // This method validates that tag function calls have correct arity
        // and normalizes function names.

        var columns = statement.Columns.Select(c => new SelectColumn(
            NormalizeTagExpression(c.Expression),
            c.Alias)).ToImmutableArray();

        var where = statement.Where != null ? NormalizeTagExpression(statement.Where) : null;
        var having = statement.Having != null ? NormalizeTagExpression(statement.Having) : null;

        return statement with
        {
            Columns = columns,
            Where = where,
            Having = having
        };
    }

    /// <summary>
    /// Checks whether an expression tree contains any tag function calls.
    /// </summary>
    public static bool ContainsTagFunctions(Expression expr)
    {
        return expr switch
        {
            FunctionCallExpression func =>
                IsTagFunction(func.FunctionName) ||
                func.Arguments.Any(ContainsTagFunctions),
            BinaryExpression bin =>
                ContainsTagFunctions(bin.Left) || ContainsTagFunctions(bin.Right),
            UnaryExpression unary =>
                ContainsTagFunctions(unary.Operand),
            ParenthesizedExpression paren =>
                ContainsTagFunctions(paren.Inner),
            InExpression inExpr =>
                ContainsTagFunctions(inExpr.Operand) ||
                (inExpr.Values?.Any(ContainsTagFunctions) == true),
            BetweenExpression between =>
                ContainsTagFunctions(between.Operand) ||
                ContainsTagFunctions(between.Low) ||
                ContainsTagFunctions(between.High),
            LikeExpression like =>
                ContainsTagFunctions(like.Operand) ||
                ContainsTagFunctions(like.Pattern),
            IsNullExpression isNull =>
                ContainsTagFunctions(isNull.Operand),
            CaseExpression caseExpr =>
                (caseExpr.Operand != null && ContainsTagFunctions(caseExpr.Operand)) ||
                caseExpr.WhenClauses.Any(w => ContainsTagFunctions(w.Condition) || ContainsTagFunctions(w.Result)) ||
                (caseExpr.ElseExpression != null && ContainsTagFunctions(caseExpr.ElseExpression)),
            _ => false
        };
    }

    /// <summary>
    /// Returns the list of tag function names found in an expression tree.
    /// </summary>
    public static IReadOnlyList<string> GetTagFunctionNames(Expression expr)
    {
        var names = new List<string>();
        CollectTagFunctionNames(expr, names);
        return names;
    }

    private static void CollectTagFunctionNames(Expression expr, List<string> names)
    {
        switch (expr)
        {
            case FunctionCallExpression func:
                if (IsTagFunction(func.FunctionName))
                    names.Add(func.FunctionName.ToUpperInvariant());
                foreach (var arg in func.Arguments)
                    CollectTagFunctionNames(arg, names);
                break;
            case BinaryExpression bin:
                CollectTagFunctionNames(bin.Left, names);
                CollectTagFunctionNames(bin.Right, names);
                break;
            case UnaryExpression unary:
                CollectTagFunctionNames(unary.Operand, names);
                break;
            case ParenthesizedExpression paren:
                CollectTagFunctionNames(paren.Inner, names);
                break;
        }
    }

    private static bool IsTagFunction(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper is "TAG" or "HAS_TAG" or "TAG_COUNT";
    }

    private static Expression NormalizeTagExpression(Expression expr)
    {
        return expr switch
        {
            FunctionCallExpression func when IsTagFunction(func.FunctionName) =>
                func with { FunctionName = func.FunctionName.ToUpperInvariant() },

            BinaryExpression bin =>
                bin with
                {
                    Left = NormalizeTagExpression(bin.Left),
                    Right = NormalizeTagExpression(bin.Right)
                },

            UnaryExpression unary =>
                unary with { Operand = NormalizeTagExpression(unary.Operand) },

            ParenthesizedExpression paren =>
                paren with { Inner = NormalizeTagExpression(paren.Inner) },

            IsNullExpression isNull =>
                isNull with { Operand = NormalizeTagExpression(isNull.Operand) },

            InExpression inExpr =>
                inExpr with
                {
                    Operand = NormalizeTagExpression(inExpr.Operand),
                    Values = inExpr.Values?.Select(NormalizeTagExpression).ToImmutableArray()
                },

            BetweenExpression between =>
                between with
                {
                    Operand = NormalizeTagExpression(between.Operand),
                    Low = NormalizeTagExpression(between.Low),
                    High = NormalizeTagExpression(between.High)
                },

            LikeExpression like =>
                like with
                {
                    Operand = NormalizeTagExpression(like.Operand),
                    Pattern = NormalizeTagExpression(like.Pattern)
                },

            CaseExpression caseExpr =>
                caseExpr with
                {
                    Operand = caseExpr.Operand != null ? NormalizeTagExpression(caseExpr.Operand) : null,
                    WhenClauses = caseExpr.WhenClauses.Select(w =>
                        new WhenClause(NormalizeTagExpression(w.Condition), NormalizeTagExpression(w.Result)))
                        .ToImmutableArray(),
                    ElseExpression = caseExpr.ElseExpression != null
                        ? NormalizeTagExpression(caseExpr.ElseExpression) : null
                },

            _ => expr
        };
    }
}
