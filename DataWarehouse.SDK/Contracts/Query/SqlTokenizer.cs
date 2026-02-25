namespace DataWarehouse.SDK.Contracts.Query;

/// <summary>
/// Token types produced by the SQL tokenizer.
/// </summary>
public enum SqlTokenType
{
    // Literals
    Identifier,
    QuotedIdentifier,
    Number,
    StringLiteral,

    // Operators
    Equals,
    NotEquals,       // <> or !=
    LessThan,
    GreaterThan,
    LessOrEqual,
    GreaterOrEqual,
    Plus,
    Minus,
    Asterisk,
    Slash,
    Percent,

    // Punctuation
    Dot,
    Comma,
    LeftParen,
    RightParen,
    Semicolon,

    // Keywords
    Select,
    From,
    Where,
    Join,
    Left,
    Right,
    Inner,
    Full,
    Cross,
    On,
    Group,
    By,
    Order,
    Asc,
    Desc,
    Limit,
    Offset,
    Having,
    Distinct,
    As,
    And,
    Or,
    Not,
    In,
    Between,
    Like,
    Is,
    Null,
    Exists,
    Case,
    When,
    Then,
    Else,
    End,
    Cast,
    With,
    Union,
    All,
    Insert,
    Update,
    Delete,
    Set,
    Values,
    Into,
    True,
    False,
    Escape,
    Outer,

    // End of input
    Eof
}

/// <summary>
/// A single token produced by the SQL tokenizer.
/// </summary>
public sealed record SqlToken(SqlTokenType Type, string Value, int Line, int Column);

/// <summary>
/// Tokenizes SQL text into a stream of typed tokens.
/// Handles quoted identifiers, string literals, numeric literals, operators, and keywords.
/// </summary>
public static class SqlTokenizer
{
    private static readonly Dictionary<string, SqlTokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = SqlTokenType.Select,
        ["FROM"] = SqlTokenType.From,
        ["WHERE"] = SqlTokenType.Where,
        ["JOIN"] = SqlTokenType.Join,
        ["LEFT"] = SqlTokenType.Left,
        ["RIGHT"] = SqlTokenType.Right,
        ["INNER"] = SqlTokenType.Inner,
        ["FULL"] = SqlTokenType.Full,
        ["CROSS"] = SqlTokenType.Cross,
        ["ON"] = SqlTokenType.On,
        ["GROUP"] = SqlTokenType.Group,
        ["BY"] = SqlTokenType.By,
        ["ORDER"] = SqlTokenType.Order,
        ["ASC"] = SqlTokenType.Asc,
        ["DESC"] = SqlTokenType.Desc,
        ["LIMIT"] = SqlTokenType.Limit,
        ["OFFSET"] = SqlTokenType.Offset,
        ["HAVING"] = SqlTokenType.Having,
        ["DISTINCT"] = SqlTokenType.Distinct,
        ["AS"] = SqlTokenType.As,
        ["AND"] = SqlTokenType.And,
        ["OR"] = SqlTokenType.Or,
        ["NOT"] = SqlTokenType.Not,
        ["IN"] = SqlTokenType.In,
        ["BETWEEN"] = SqlTokenType.Between,
        ["LIKE"] = SqlTokenType.Like,
        ["IS"] = SqlTokenType.Is,
        ["NULL"] = SqlTokenType.Null,
        ["EXISTS"] = SqlTokenType.Exists,
        ["CASE"] = SqlTokenType.Case,
        ["WHEN"] = SqlTokenType.When,
        ["THEN"] = SqlTokenType.Then,
        ["ELSE"] = SqlTokenType.Else,
        ["END"] = SqlTokenType.End,
        ["CAST"] = SqlTokenType.Cast,
        ["WITH"] = SqlTokenType.With,
        ["UNION"] = SqlTokenType.Union,
        ["ALL"] = SqlTokenType.All,
        ["INSERT"] = SqlTokenType.Insert,
        ["UPDATE"] = SqlTokenType.Update,
        ["DELETE"] = SqlTokenType.Delete,
        ["SET"] = SqlTokenType.Set,
        ["VALUES"] = SqlTokenType.Values,
        ["INTO"] = SqlTokenType.Into,
        ["TRUE"] = SqlTokenType.True,
        ["FALSE"] = SqlTokenType.False,
        ["ESCAPE"] = SqlTokenType.Escape,
        ["OUTER"] = SqlTokenType.Outer,
    };

    /// <summary>
    /// Tokenizes SQL text into a list of tokens.
    /// </summary>
    public static IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var tokens = new List<SqlToken>();
        var span = sql.AsSpan();
        int pos = 0;
        int line = 1;
        int col = 1;

        while (pos < span.Length)
        {
            char c = span[pos];

            // Skip whitespace
            if (char.IsWhiteSpace(c))
            {
                if (c == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
                pos++;
                continue;
            }

            // Skip single-line comments (-- ...)
            if (c == '-' && pos + 1 < span.Length && span[pos + 1] == '-')
            {
                while (pos < span.Length && span[pos] != '\n')
                {
                    pos++;
                }
                continue;
            }

            // Skip multi-line comments (/* ... */)
            if (c == '/' && pos + 1 < span.Length && span[pos + 1] == '*')
            {
                pos += 2;
                col += 2;
                while (pos < span.Length)
                {
                    if (span[pos] == '*' && pos + 1 < span.Length && span[pos + 1] == '/')
                    {
                        pos += 2;
                        col += 2;
                        break;
                    }
                    if (span[pos] == '\n')
                    {
                        line++;
                        col = 1;
                    }
                    else
                    {
                        col++;
                    }
                    pos++;
                }
                continue;
            }

            int startCol = col;

            // String literal ('...')
            if (c == '\'')
            {
                int start = pos + 1;
                pos++;
                col++;
                var sb = new System.Text.StringBuilder();
                while (pos < span.Length)
                {
                    if (span[pos] == '\'')
                    {
                        // Escaped single quote ''
                        if (pos + 1 < span.Length && span[pos + 1] == '\'')
                        {
                            sb.Append('\'');
                            pos += 2;
                            col += 2;
                            continue;
                        }
                        break;
                    }
                    if (span[pos] == '\n')
                    {
                        line++;
                        col = 1;
                    }
                    else
                    {
                        col++;
                    }
                    sb.Append(span[pos]);
                    pos++;
                }
                if (pos >= span.Length)
                    throw new SqlParseException("Unterminated string literal", line, startCol, null);
                pos++; // skip closing quote
                col++;
                tokens.Add(new SqlToken(SqlTokenType.StringLiteral, sb.ToString(), line, startCol));
                continue;
            }

            // Quoted identifier ("..." or [...])
            if (c == '"' || c == '[')
            {
                char closeChar = c == '"' ? '"' : ']';
                int start = pos + 1;
                pos++;
                col++;
                var sb = new System.Text.StringBuilder();
                while (pos < span.Length && span[pos] != closeChar)
                {
                    sb.Append(span[pos]);
                    pos++;
                    col++;
                }
                if (pos >= span.Length)
                    throw new SqlParseException($"Unterminated quoted identifier", line, startCol, null);
                pos++; // skip closing quote/bracket
                col++;
                tokens.Add(new SqlToken(SqlTokenType.QuotedIdentifier, sb.ToString(), line, startCol));
                continue;
            }

            // Numeric literal (integer or decimal)
            if (char.IsDigit(c))
            {
                int start = pos;
                bool hasDot = false;
                while (pos < span.Length && (char.IsDigit(span[pos]) || (span[pos] == '.' && !hasDot)))
                {
                    if (span[pos] == '.') hasDot = true;
                    pos++;
                    col++;
                }
                tokens.Add(new SqlToken(SqlTokenType.Number, sql.Substring(start, pos - start), line, startCol));
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_')
            {
                int start = pos;
                while (pos < span.Length && (char.IsLetterOrDigit(span[pos]) || span[pos] == '_'))
                {
                    pos++;
                    col++;
                }
                string word = sql.Substring(start, pos - start);
                if (Keywords.TryGetValue(word, out var kwType))
                {
                    tokens.Add(new SqlToken(kwType, word, line, startCol));
                }
                else
                {
                    tokens.Add(new SqlToken(SqlTokenType.Identifier, word, line, startCol));
                }
                continue;
            }

            // Two-character operators
            if (pos + 1 < span.Length)
            {
                char next = span[pos + 1];
                if (c == '<' && next == '>')
                {
                    tokens.Add(new SqlToken(SqlTokenType.NotEquals, "<>", line, startCol));
                    pos += 2; col += 2; continue;
                }
                if (c == '!' && next == '=')
                {
                    tokens.Add(new SqlToken(SqlTokenType.NotEquals, "!=", line, startCol));
                    pos += 2; col += 2; continue;
                }
                if (c == '<' && next == '=')
                {
                    tokens.Add(new SqlToken(SqlTokenType.LessOrEqual, "<=", line, startCol));
                    pos += 2; col += 2; continue;
                }
                if (c == '>' && next == '=')
                {
                    tokens.Add(new SqlToken(SqlTokenType.GreaterOrEqual, ">=", line, startCol));
                    pos += 2; col += 2; continue;
                }
            }

            // Single-character operators and punctuation
            switch (c)
            {
                case '=':
                    tokens.Add(new SqlToken(SqlTokenType.Equals, "=", line, startCol));
                    pos++; col++; continue;
                case '<':
                    tokens.Add(new SqlToken(SqlTokenType.LessThan, "<", line, startCol));
                    pos++; col++; continue;
                case '>':
                    tokens.Add(new SqlToken(SqlTokenType.GreaterThan, ">", line, startCol));
                    pos++; col++; continue;
                case '+':
                    tokens.Add(new SqlToken(SqlTokenType.Plus, "+", line, startCol));
                    pos++; col++; continue;
                case '-':
                    tokens.Add(new SqlToken(SqlTokenType.Minus, "-", line, startCol));
                    pos++; col++; continue;
                case '*':
                    tokens.Add(new SqlToken(SqlTokenType.Asterisk, "*", line, startCol));
                    pos++; col++; continue;
                case '/':
                    tokens.Add(new SqlToken(SqlTokenType.Slash, "/", line, startCol));
                    pos++; col++; continue;
                case '%':
                    tokens.Add(new SqlToken(SqlTokenType.Percent, "%", line, startCol));
                    pos++; col++; continue;
                case '.':
                    tokens.Add(new SqlToken(SqlTokenType.Dot, ".", line, startCol));
                    pos++; col++; continue;
                case ',':
                    tokens.Add(new SqlToken(SqlTokenType.Comma, ",", line, startCol));
                    pos++; col++; continue;
                case '(':
                    tokens.Add(new SqlToken(SqlTokenType.LeftParen, "(", line, startCol));
                    pos++; col++; continue;
                case ')':
                    tokens.Add(new SqlToken(SqlTokenType.RightParen, ")", line, startCol));
                    pos++; col++; continue;
                case ';':
                    tokens.Add(new SqlToken(SqlTokenType.Semicolon, ";", line, startCol));
                    pos++; col++; continue;
                default:
                    throw new SqlParseException($"Unexpected character '{c}'", line, col, c.ToString());
            }
        }

        tokens.Add(new SqlToken(SqlTokenType.Eof, "", line, col));
        return tokens;
    }
}
