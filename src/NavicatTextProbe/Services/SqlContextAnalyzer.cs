using System.Text.RegularExpressions;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal enum SqlExpectedContinuationKind
{
    Unknown,
    CreateTableOpeningParenthesis,
    CreateTableElement,
    ConstraintType,
    ForeignKeyConstraint,
    ForeignKeyColumns,
    ReferencedTable
}

internal sealed record SqlCaretContext
{
    public SqlExpectedContinuationKind ExpectedKind { get; init; }
    public string CurrentLineBeforeCaret { get; init; } = string.Empty;
    public string CurrentStatement { get; init; } = string.Empty;
    public string? CurrentTable { get; init; }
    public string? ConstraintName { get; init; }
    public string? ForeignKeyColumn { get; init; }
    public string? LikelyParentTable { get; init; }
    public bool ParentTableWasFoundInDocument { get; init; }
    public IReadOnlyList<string> DefinedColumns { get; init; } = [];
    public IReadOnlyList<string> KnownTables { get; init; } = [];
    public string ExpectedContinuation { get; init; } = string.Empty;

    public string ToPromptFacts()
    {
        var fields = DefinedColumns.Count == 0 ? "未可靠识别" : string.Join(", ", DefinedColumns);
        var tables = KnownTables.Count == 0 ? "未可靠识别" : string.Join(", ", KnownTables);
        return $"""
            本地语法状态：{ExpectedKind}
            当前光标前这一行：{CurrentLineBeforeCaret}
            当前 CREATE TABLE：{CurrentTable ?? "未识别"}
            当前约束名：{ConstraintName ?? "无"}
            从约束名推断的外键列：{ForeignKeyColumn ?? "无"}
            已经定义的列（禁止重复推荐）：{fields}
            文档中已经出现的建表对象：{tables}
            可能的父表：{LikelyParentTable ?? "未识别"}（{(ParentTableWasFoundInDocument ? "来自文档，可信度较高" : "仅按命名推断，必须谨慎核对")}）
            此处最合理的续写类型：{ExpectedContinuation}
            """;
    }
}

internal static class SqlContextAnalyzer
{
    private static readonly Regex CreateTableRegex = new(
        @"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?:`[^`]+`|[A-Za-z_][\w$]*)\s*\.\s*)?(?<table>`[^`]+`|[A-Za-z_][\w$]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ConstraintAtCaretRegex = new(
        @"\bCONSTRAINT\s+(?<name>`[^`]+`|[A-Za-z_][\w$]*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CreateTableHeaderAtCaretRegex = new(
        @"^\s*CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?:`[^`]+`|[A-Za-z_][\w$]*)\s*\.\s*)?(?:`[^`]+`|[A-Za-z_][\w$]*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static SqlCaretContext Analyze(ProbeResult probe)
    {
        var caret = Math.Clamp(probe.CaretOffset ?? probe.Text.Length, 0, probe.Text.Length);
        var beforeCaret = probe.Text[..caret];
        var currentLineStart = beforeCaret.LastIndexOf('\n') + 1;
        var currentLine = beforeCaret[currentLineStart..].TrimEnd('\r');
        var statementStart = beforeCaret.LastIndexOf(';') + 1;
        var statement = beforeCaret[statementStart..];

        var knownTables = CreateTableRegex.Matches(probe.Text)
            .Select(match => Unquote(match.Groups["table"].Value))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var createMatches = CreateTableRegex.Matches(statement);
        var currentTable = createMatches.Count == 0
            ? null
            : Unquote(createMatches[^1].Groups["table"].Value);
        var columns = ExtractDefinedColumns(statement, createMatches.Count == 0 ? -1 : createMatches[^1].Index);

        var expectedKind = currentTable is null
            ? SqlExpectedContinuationKind.Unknown
            : SqlExpectedContinuationKind.CreateTableElement;
        var expected = currentTable is null
            ? "结合当前语句做保守的最短续写"
            : "继续当前 CREATE TABLE 的字段或表级约束，不得重复已经定义的列";
        string? constraintName = null;
        string? foreignKeyColumn = null;

        if (currentTable is not null && CreateTableHeaderAtCaretRegex.IsMatch(currentLine))
        {
            expectedKind = SqlExpectedContinuationKind.CreateTableOpeningParenthesis;
            expected = "CREATE TABLE 表名后缺少英文左括号 (；先开始字段列表，不得跳到字段或约束";
        }
        else
        {
            var constraintMatch = ConstraintAtCaretRegex.Match(currentLine);
            if (constraintMatch.Success)
            {
                constraintName = Unquote(constraintMatch.Groups["name"].Value);
                foreignKeyColumn = InferForeignKeyColumn(constraintName, columns);
                if (constraintName.StartsWith("fk_", StringComparison.OrdinalIgnoreCase))
                {
                    expectedKind = SqlExpectedContinuationKind.ForeignKeyConstraint;
                    expected = foreignKeyColumn is null
                        ? "约束名以 fk_ 开头；必须优先续写 FOREIGN KEY，不得重新定义字段"
                        : $"正在命名外键；必须优先续写 FOREIGN KEY ({foreignKeyColumn}) REFERENCES ...，不得重新定义字段";
                }
                else
                {
                    expectedKind = SqlExpectedContinuationKind.ConstraintType;
                    expected = "约束名之后必须续写 FOREIGN KEY、UNIQUE、CHECK 或 PRIMARY KEY 等约束类型";
                }
            }
            else if (Regex.IsMatch(currentLine, @"\bFOREIGN\s+KEY\s*\([^)]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                expectedKind = SqlExpectedContinuationKind.ForeignKeyColumns;
                expected = "补完 FOREIGN KEY 的本表列和右括号，然后写 REFERENCES";
            }
            else if (Regex.IsMatch(currentLine, @"\bREFERENCES\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                expectedKind = SqlExpectedContinuationKind.ReferencedTable;
                expected = "填写文档中真实存在的父表及被引用列，不得虚构对象";
            }
        }

        var (parentTable, foundInDocument) = InferParentTable(currentTable, foreignKeyColumn, knownTables);
        return new SqlCaretContext
        {
            ExpectedKind = expectedKind,
            CurrentLineBeforeCaret = currentLine,
            CurrentStatement = statement,
            CurrentTable = currentTable,
            ConstraintName = constraintName,
            ForeignKeyColumn = foreignKeyColumn,
            LikelyParentTable = parentTable,
            ParentTableWasFoundInDocument = foundInDocument,
            DefinedColumns = columns,
            KnownTables = knownTables,
            ExpectedContinuation = expected
        };
    }

    public static SuggestionResult? BuildImmediateGuardedSuggestion(ProbeResult probe)
    {
        var context = Analyze(probe);
        if (context.ExpectedKind == SqlExpectedContinuationKind.CreateTableOpeningParenthesis)
        {
            return new SuggestionResult
            {
                Mode = SuggestionMode.Completion,
                DisplayText = "表名后缺少英文左括号：(",
                InsertText = "(",
                Explanation = "本地语法检查已定位到当前光标：CREATE TABLE 的表名后要先用英文半角 ( 开始字段列表。",
                Confidence = 1
            };
        }

        if (context.ExpectedKind != SqlExpectedContinuationKind.ForeignKeyConstraint)
        {
            return null;
        }

        var column = context.ForeignKeyColumn;
        var insertText = column is null
            ? "FOREIGN KEY ("
            : $"FOREIGN KEY ({column}) REFERENCES ";
        return new SuggestionResult
        {
            Mode = SuggestionMode.Completion,
            DisplayText = insertText,
            InsertText = insertText,
            Explanation = column is null
                ? "本地语法守卫已识别 fk_ 约束名；下一步应声明外键列。"
                : $"本地语法守卫已识别：约束名指向已定义列 {column}，下一步应写 FOREIGN KEY 和父表。",
            Confidence = column is null ? 0.82 : 0.96
        };
    }

    public static SuggestionResult GuardAutomaticResult(ProbeResult probe, SuggestionResult result)
    {
        if (result.Mode != SuggestionMode.Completion)
        {
            return result;
        }

        var context = Analyze(probe);
        if (IsCompatible(context, result.InsertText, result.DisplayText))
        {
            return result;
        }

        return BuildImmediateGuardedSuggestion(probe) ?? result;
    }

    public static bool IsCompatible(ProbeResult probe, SuggestionResult result) =>
        result.Mode == SuggestionMode.Completion &&
        IsCompatible(Analyze(probe), result.InsertText, result.DisplayText);

    public static SuggestionResult RankCandidates(ProbeResult probe, SuggestionResult result)
    {
        var context = Analyze(probe);
        var candidates = result.Candidates.ToList();
        var guarded = BuildImmediateGuardedSuggestion(probe);
        if (guarded is not null && !candidates.Any(candidate =>
                IsSameSuggestion(candidate.InsertText, guarded.InsertText)))
        {
            candidates.Add(new SuggestionCandidate
            {
                DisplayText = guarded.DisplayText,
                InsertText = guarded.InsertText,
                Explanation = guarded.Explanation,
                Confidence = guarded.Confidence
            });
        }

        if (context.ExpectedKind == SqlExpectedContinuationKind.ForeignKeyConstraint)
        {
            foreach (var fallback in BuildForeignKeyCandidateFallbacks(context))
            {
                if (!candidates.Any(candidate => IsSameSuggestion(candidate.InsertText, fallback.InsertText)))
                {
                    candidates.Add(fallback);
                }
            }
        }

        var ranked = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.InsertText) || !string.IsNullOrWhiteSpace(candidate.DisplayText))
            .Where(candidate => context.ExpectedKind != SqlExpectedContinuationKind.ForeignKeyConstraint ||
                                Regex.IsMatch($"{candidate.InsertText} {candidate.DisplayText}", @"\bFOREIGN\s+KEY\b", RegexOptions.IgnoreCase))
            .GroupBy(candidate => Normalize(candidate.InsertText.Length > 0 ? candidate.InsertText : candidate.DisplayText), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select((candidate, originalIndex) => new
            {
                Candidate = candidate,
                Score = ScoreCandidate(context, probe, candidate, originalIndex)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.Confidence)
            .Take(3)
            .Select((item, index) => item.Candidate with { Rank = index + 1 })
            .ToArray();

        if (ranked.Length == 0)
        {
            return result;
        }

        return (result with { Candidates = ranked, ActiveCandidateIndex = 0 }).SelectCandidate(0);
    }

    private static IReadOnlyList<SuggestionCandidate> BuildForeignKeyCandidateFallbacks(SqlCaretContext context)
    {
        if (context.ForeignKeyColumn is not { Length: > 0 } column)
        {
            return [];
        }

        var cautiousExplanation = context.ParentTableWasFoundInDocument
            ? $"父表 {context.LikelyParentTable} 已在当前文档出现；先建立最小外键。"
            : $"父表名 {context.LikelyParentTable} 是按 user_id 和当前表前缀推断的，使用前请核对。";
        if (context.LikelyParentTable is not { Length: > 0 } parent)
        {
            return [];
        }

        var baseSql = $"FOREIGN KEY ({column}) REFERENCES {parent} (id)";
        return
        [
            new SuggestionCandidate
            {
                DisplayText = baseSql,
                InsertText = baseSql,
                Explanation = cautiousExplanation,
                Confidence = context.ParentTableWasFoundInDocument ? 0.94 : 0.72
            },
            new SuggestionCandidate
            {
                DisplayText = baseSql + " ON DELETE RESTRICT ON UPDATE CASCADE",
                InsertText = baseSql + " ON DELETE RESTRICT ON UPDATE CASCADE",
                Explanation = "禁止删除仍被引用的父记录，父表主键更新时同步外键；这是更稳妥的保护策略。",
                Confidence = context.ParentTableWasFoundInDocument ? 0.9 : 0.68
            },
            new SuggestionCandidate
            {
                DisplayText = baseSql + " ON DELETE CASCADE ON UPDATE CASCADE",
                InsertText = baseSql + " ON DELETE CASCADE ON UPDATE CASCADE",
                Explanation = "仅当子记录完全依附父记录生命周期时使用；删除父记录会连同子记录一起删除。",
                Confidence = context.ParentTableWasFoundInDocument ? 0.8 : 0.6
            }
        ];
    }

    private static bool IsCompatible(SqlCaretContext context, string insertText, string displayText)
    {
        var suggestion = $"{insertText} {displayText}";
        return context.ExpectedKind switch
        {
            SqlExpectedContinuationKind.CreateTableOpeningParenthesis =>
                insertText.TrimStart().StartsWith('('),
            SqlExpectedContinuationKind.ForeignKeyConstraint =>
                Regex.IsMatch(suggestion, @"\bFOREIGN\s+KEY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            SqlExpectedContinuationKind.ConstraintType =>
                Regex.IsMatch(suggestion, @"\b(FOREIGN\s+KEY|UNIQUE|CHECK|PRIMARY\s+KEY)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            _ => true
        };
    }

    private static double ScoreCandidate(SqlCaretContext context, ProbeResult probe, SuggestionCandidate candidate, int originalIndex)
    {
        var text = candidate.InsertText.Length > 0 ? candidate.InsertText : candidate.DisplayText;
        var score = candidate.Confidence * 20 - originalIndex;
        if (context.ExpectedKind == SqlExpectedContinuationKind.ForeignKeyConstraint)
        {
            score += Regex.IsMatch(text, @"\bFOREIGN\s+KEY\b", RegexOptions.IgnoreCase) ? 120 : -160;
            if (context.ForeignKeyColumn is { Length: > 0 } column &&
                Regex.IsMatch(text, $@"\(\s*`?{Regex.Escape(column)}`?\s*\)", RegexOptions.IgnoreCase))
            {
                score += 45;
            }
            if (Regex.IsMatch(text, @"\bREFERENCES\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                score -= 35;
            }
        }

        var firstToken = Regex.Match(text.TrimStart(), @"^`?(?<name>[A-Za-z_][\w$]*)`?").Groups["name"].Value;
        if (context.DefinedColumns.Contains(firstToken, StringComparer.OrdinalIgnoreCase))
        {
            score -= 140;
        }

        var normalized = Normalize(text);
        var caret = Math.Clamp(probe.CaretOffset ?? probe.Text.Length, 0, probe.Text.Length);
        if (normalized.Length >= 12 && Normalize(probe.Text[..caret]).Contains(normalized, StringComparison.OrdinalIgnoreCase))
        {
            score -= 100;
        }

        return score;
    }

    private static IReadOnlyList<string> ExtractDefinedColumns(string statement, int createTableIndex)
    {
        if (createTableIndex < 0)
        {
            return [];
        }

        var openParenthesis = statement.IndexOf('(', createTableIndex);
        if (openParenthesis < 0)
        {
            return [];
        }

        string[] constraintStarters = ["CONSTRAINT", "PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "KEY", "INDEX"];
        var columns = new List<string>();
        foreach (var rawLine in statement[(openParenthesis + 1)..].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimStart();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal) || line.StartsWith('#') ||
                line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith('*'))
            {
                continue;
            }

            if (constraintStarters.Any(starter => line.StartsWith(starter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var match = Regex.Match(line, @"^(?:`(?<quoted>[^`]+)`|(?<plain>[A-Za-z_][\w$]*))\s+(?<type>[A-Za-z]+)", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                continue;
            }

            var type = match.Groups["type"].Value;
            if (constraintStarters.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            columns.Add(match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value);
        }

        return columns.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? InferForeignKeyColumn(string constraintName, IReadOnlyList<string> columns)
    {
        var suffix = constraintName.StartsWith("fk_", StringComparison.OrdinalIgnoreCase)
            ? constraintName[3..]
            : constraintName;
        return columns
            .OrderByDescending(column => column.Length)
            .FirstOrDefault(column =>
                suffix.Equals(column, StringComparison.OrdinalIgnoreCase) ||
                suffix.EndsWith("_" + column, StringComparison.OrdinalIgnoreCase));
    }

    private static (string? Table, bool FoundInDocument) InferParentTable(
        string? currentTable,
        string? foreignKeyColumn,
        IReadOnlyList<string> knownTables)
    {
        if (foreignKeyColumn is null || !foreignKeyColumn.EndsWith("_id", StringComparison.OrdinalIgnoreCase))
        {
            return (null, false);
        }

        var entity = foreignKeyColumn[..^3];
        var singular = entity;
        var plural = entity.EndsWith('y') ? entity[..^1] + "ies" : entity + "s";
        var known = knownTables
            .Where(table => !table.Equals(currentTable, StringComparison.OrdinalIgnoreCase))
            .OrderBy(table => table.Length)
            .FirstOrDefault(table =>
                table.Equals(singular, StringComparison.OrdinalIgnoreCase) ||
                table.Equals(plural, StringComparison.OrdinalIgnoreCase) ||
                table.EndsWith("_" + singular, StringComparison.OrdinalIgnoreCase) ||
                table.EndsWith("_" + plural, StringComparison.OrdinalIgnoreCase));
        if (known is not null)
        {
            return (known, true);
        }

        if (currentTable is null)
        {
            return (null, false);
        }

        var lastSeparator = currentTable.LastIndexOf('_');
        return lastSeparator < 0
            ? (plural, false)
            : (currentTable[..(lastSeparator + 1)] + plural, false);
    }

    private static bool IsSameSuggestion(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string Unquote(string value) => value.Trim().Trim('`');
}
