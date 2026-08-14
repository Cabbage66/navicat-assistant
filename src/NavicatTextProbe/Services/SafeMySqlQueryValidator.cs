using System.Text;

namespace NavicatTextProbe.Services;

internal static class SafeMySqlQueryValidator
{
    private static readonly HashSet<string> AllowedFirstTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "SHOW", "DESCRIBE", "DESC", "EXPLAIN"
    };

    private static readonly HashSet<string> ForbiddenTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "REPLACE", "MERGE", "CREATE", "ALTER", "DROP",
        "TRUNCATE", "RENAME", "GRANT", "REVOKE", "SET", "CALL", "DO", "HANDLER", "LOAD",
        "IMPORT", "INSTALL", "UNINSTALL", "LOCK", "UNLOCK", "START", "BEGIN", "COMMIT",
        "ROLLBACK", "SAVEPOINT", "RELEASE", "PREPARE", "EXECUTE", "DEALLOCATE", "KILL",
        "SHUTDOWN", "RESET", "PURGE", "FLUSH", "OPTIMIZE", "ANALYZE", "REPAIR", "INTO",
        "OUTFILE", "DUMPFILE", "SLEEP", "BENCHMARK", "GET_LOCK", "RELEASE_LOCK", "LOAD_FILE"
    };

    public static bool TryValidate(string sql, out string normalizedSql, out string error)
    {
        normalizedSql = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(sql))
        {
            error = "模型没有生成查询语句。";
            return false;
        }
        if (sql.Length > 20_000)
        {
            error = "只读工具查询超过 20,000 字符本地上限。";
            return false;
        }
        if (sql.Contains("/*!", StringComparison.Ordinal) ||
            sql.Contains("/*M!", StringComparison.OrdinalIgnoreCase))
        {
            error = "不允许 MySQL/MariaDB 可执行版本注释。";
            return false;
        }

        if (!TryTokenize(sql, out var tokens, out var semicolonCount, out var hasNonTrailingSemicolon, out error))
        {
            return false;
        }

        if (tokens.Count == 0 || !AllowedFirstTokens.Contains(tokens[0]))
        {
            error = "只允许 SELECT、WITH...SELECT、SHOW、DESCRIBE 或 EXPLAIN。";
            return false;
        }

        if (semicolonCount > 1 || hasNonTrailingSemicolon)
        {
            error = "数据库增强只允许一条 SQL 语句。";
            return false;
        }

        var allowShowCreate = tokens.Count >= 3 &&
                              tokens[0].Equals("SHOW", StringComparison.OrdinalIgnoreCase) &&
                              tokens[1].Equals("CREATE", StringComparison.OrdinalIgnoreCase) &&
                              tokens[2] is "TABLE" or "VIEW" or "DATABASE" or "SCHEMA";
        var forbidden = tokens.FirstOrDefault(token =>
            ForbiddenTokens.Contains(token) &&
            !(allowShowCreate && token.Equals("CREATE", StringComparison.OrdinalIgnoreCase)));
        if (forbidden is not null)
        {
            error = $"查询包含被禁止的关键字：{forbidden}。";
            return false;
        }

        if (tokens[0].Equals("WITH", StringComparison.OrdinalIgnoreCase) &&
            !tokens.Contains("SELECT", StringComparer.OrdinalIgnoreCase))
        {
            error = "WITH 查询必须以 SELECT 返回数据。";
            return false;
        }

        normalizedSql = sql.Trim();
        if (normalizedSql.EndsWith(';'))
        {
            normalizedSql = normalizedSql[..^1].TrimEnd();
        }
        return true;
    }

    private static bool TryTokenize(
        string sql,
        out List<string> tokens,
        out int semicolonCount,
        out bool hasNonTrailingSemicolon,
        out string error)
    {
        var parsedTokens = new List<string>();
        tokens = parsedTokens;
        semicolonCount = 0;
        hasNonTrailingSemicolon = false;
        error = string.Empty;
        var builder = new StringBuilder();
        var state = LexState.Normal;

        void FlushToken()
        {
            if (builder.Length == 0) return;
            parsedTokens.Add(builder.ToString().ToUpperInvariant());
            builder.Clear();
        }

        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            switch (state)
            {
                case LexState.SingleQuote:
                    if (character == '\\') index++;
                    else if (character == '\'' && next == '\'') index++;
                    else if (character == '\'') state = LexState.Normal;
                    continue;
                case LexState.DoubleQuote:
                    if (character == '\\') index++;
                    else if (character == '"' && next == '"') index++;
                    else if (character == '"') state = LexState.Normal;
                    continue;
                case LexState.Backtick:
                    if (character == '`' && next == '`') index++;
                    else if (character == '`') state = LexState.Normal;
                    continue;
                case LexState.LineComment:
                    if (character is '\r' or '\n') state = LexState.Normal;
                    continue;
                case LexState.BlockComment:
                    if (character == '*' && next == '/')
                    {
                        state = LexState.Normal;
                        index++;
                    }
                    continue;
            }

            if (character == '\'' || character == '"' || character == '`')
            {
                FlushToken();
                state = character switch
                {
                    '\'' => LexState.SingleQuote,
                    '"' => LexState.DoubleQuote,
                    _ => LexState.Backtick
                };
            }
            else if (character == '#')
            {
                FlushToken();
                state = LexState.LineComment;
            }
            else if (character == '-' && next == '-' &&
                     (index + 2 >= sql.Length || char.IsWhiteSpace(sql[index + 2])))
            {
                FlushToken();
                state = LexState.LineComment;
                index++;
            }
            else if (character == '/' && next == '*')
            {
                FlushToken();
                state = LexState.BlockComment;
                index++;
            }
            else if (character == ';')
            {
                FlushToken();
                semicolonCount++;
                if (sql[(index + 1)..].Trim().Length > 0)
                {
                    hasNonTrailingSemicolon = true;
                }
            }
            else if (char.IsLetterOrDigit(character) || character is '_' or '$')
            {
                builder.Append(character);
            }
            else
            {
                FlushToken();
            }
        }

        FlushToken();
        if (state is LexState.SingleQuote or LexState.DoubleQuote or LexState.Backtick or LexState.BlockComment)
        {
            error = "查询包含未闭合的字符串、标识符或注释。";
            return false;
        }
        return true;
    }

    private enum LexState
    {
        Normal,
        SingleQuote,
        DoubleQuote,
        Backtick,
        LineComment,
        BlockComment
    }
}
