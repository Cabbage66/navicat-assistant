using System.Text;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal static class WordExerciseContextBuilder
{
    internal const int InitialRadius = 300;
    internal const int FirstExpandedRadius = 1_000;
    internal const int FinalExpandedRadius = 3_000;
    internal const double MinimumExerciseConfidence = 0.45;

    public static WordExerciseRequestContext? Build(
        DocumentContextResult document,
        WordExerciseRequestMode mode,
        int radius,
        int expansionLevel,
        WordExerciseQuestionState? currentQuestion = null,
        string? previousSuggestion = null)
    {
        if (document.Status != DocumentContextStatus.Captured ||
            !document.CaretAvailable ||
            document.CaretOffset is not int rawCaret ||
            string.IsNullOrEmpty(document.Text))
        {
            return null;
        }

        var caret = Math.Clamp(rawCaret, 0, document.Text.Length);
        var start = Math.Max(0, caret - Math.Max(1, radius));
        var end = Math.Min(document.Text.Length, caret + Math.Max(1, radius));
        start = MoveOffLowSurrogate(document.Text, start);
        end = MovePastHighSurrogate(document.Text, end);
        var blocks = ReadBlocks(document.Text, start, end, caret);
        var baselineChange = currentQuestion is null
            ? string.Empty
            : DescribeBaselineChange(currentQuestion.BaselineDocumentText, document.Text);

        return new WordExerciseRequestContext
        {
            Mode = mode,
            DocumentIdentity = document.DocumentIdentity,
            DocumentName = document.DocumentName,
            ContentHash = document.ContentHash,
            DocumentLength = document.Text.Length,
            CaretOffset = caret,
            WindowStart = start,
            WindowEnd = end,
            Radius = radius,
            ExpansionLevel = expansionLevel,
            Blocks = blocks,
            BlocksWithCursor = BuildBlocksForPrompt(blocks, caret),
            SelectedText = Limit(document.HasSelection ? document.SelectedText : string.Empty, 3_000),
            BaselineChange = baselineChange,
            PreviousSuggestion = Limit(previousSuggestion ?? string.Empty, 2_000),
            CurrentQuestion = currentQuestion
        };
    }

    public static bool NeedsExpandedContext(
        WordExerciseRequestContext context,
        SuggestionResult result)
    {
        if (!result.ContextComplete ||
            !string.Equals(result.NeedsMoreContext, "none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (context.Mode == WordExerciseRequestMode.ContinueCurrentQuestion)
        {
            return false;
        }

        var startIndex = FindBlockIndex(context, result.QuestionStartBlockId);
        var endIndex = FindBlockIndex(context, result.QuestionEndBlockId);
        return startIndex == 0 && context.WindowStart > 0 ||
               endIndex == context.Blocks.Count - 1 && context.WindowEnd < context.DocumentLength;
    }

    public static bool TryCreateQuestionState(
        DocumentContextResult document,
        WordExerciseRequestContext context,
        SuggestionResult result,
        string baselineDocumentText,
        out WordExerciseQuestionState? state,
        out string error)
    {
        state = null;
        error = string.Empty;
        if (!result.IsQuestion)
        {
            error = "模型判断当前内容不是 SQL 数据库练习题。";
            return false;
        }
        if (result.Mode != SuggestionMode.ExerciseAnswer)
        {
            error = "模型返回的模式不是题目回答。";
            return false;
        }
        if (!result.ContextComplete)
        {
            error = "模型判断当前窗口没有包含完整题目。";
            return false;
        }
        if (result.Confidence < MinimumExerciseConfidence)
        {
            error = $"题目定位置信度过低（{result.Confidence:0.00}）。";
            return false;
        }

        var startIndex = FindBlockIndex(context, result.QuestionStartBlockId);
        var endIndex = FindBlockIndex(context, result.QuestionEndBlockId);
        if (startIndex < 0 || endIndex < 0 || startIndex > endIndex)
        {
            error = "模型返回了无效或倒置的题目块编号。";
            return false;
        }

        var start = context.Blocks[startIndex].Start;
        var end = context.Blocks[endIndex].End;
        if (start < context.WindowStart || end > context.WindowEnd || end <= start)
        {
            error = "模型返回的题目范围超出了本次上下文。";
            return false;
        }
        if (context.CaretOffset < start || context.CaretOffset > end)
        {
            error = "模型定位到的题目没有覆盖当前光标。";
            return false;
        }

        var exactQuestion = TrimDocumentNoise(document.Text[start..end]);
        if (string.IsNullOrWhiteSpace(exactQuestion))
        {
            error = "模型定位到的题目原文为空。";
            return false;
        }
        if (!ContainsBoundaryAnchor(exactQuestion, result.QuestionStartAnchor, atStart: true) ||
            !ContainsBoundaryAnchor(exactQuestion, result.QuestionEndAnchor, atStart: false))
        {
            error = "模型返回的题目边界锚点与 Word 原文不一致。";
            return false;
        }

        var questionNumber = NormalizeQuestionNumber(result.QuestionNumber);
        state = new WordExerciseQuestionState
        {
            DocumentIdentity = document.DocumentIdentity,
            QuestionNumber = questionNumber,
            ActiveSubquestionNumber = NormalizeSubquestionNumber(result.ActiveSubquestionNumber),
            QuestionType = NormalizeQuestionType(result.QuestionType),
            QuestionText = exactQuestion,
            QuestionIdentity = ContentHasher.Sha256(DocumentContextFormatter.Compact(exactQuestion)),
            QuestionStartAnchor = result.QuestionStartAnchor.Trim(),
            QuestionEndAnchor = result.QuestionEndAnchor.Trim(),
            LastKnownStart = start,
            LastKnownEnd = end,
            BaselineDocumentHash = ContentHasher.Sha256(baselineDocumentText),
            BaselineDocumentText = baselineDocumentText
        };
        return true;
    }

    internal static string NormalizeQuestionNumber(string? value)
    {
        var text = DocumentContextFormatter.Compact(value)
            .Trim('*', '_', '#', '[', ']', '【', '】', '(', ')', '（', '）');
        if (string.IsNullOrWhiteSpace(text))
        {
            return "当前题";
        }

        text = text.Trim().TrimEnd('.', '。', '、', '．', ':', '：');
        if (text.StartsWith('第') && text.EndsWith('题') && text.Length > 2)
        {
            text = text[1..^1].Trim();
        }
        string[] prefixes = ["题号", "简答题", "综合题", "选择题", "填空题", "简答", "综合"];
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && text.Length > prefix.Length)
            {
                text = text[prefix.Length..].TrimStart(' ', ':', '：');
                break;
            }
        }
        text = text.Trim().TrimEnd('.', '。', '、', '．', ':', '：');
        return string.IsNullOrWhiteSpace(text) ? "当前题" : text;
    }

    internal static string NormalizeSubquestionNumber(string? value)
    {
        var text = DocumentContextFormatter.Compact(value).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }
        return text.Trim('(', ')', '（', '）', '.', '。', '、', '．');
    }

    internal static string NormalizeQuestionType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "choice" => "choice",
        "fill_blank" or "fillblank" => "fill_blank",
        "short_answer" or "shortanswer" => "short_answer",
        _ => "unknown"
    };

    internal static bool IsSameObservation(
        DocumentContextResult requested,
        DocumentContextResult current) =>
        string.Equals(requested.DocumentIdentity, current.DocumentIdentity, StringComparison.Ordinal) &&
        string.Equals(requested.ContentHash, current.ContentHash, StringComparison.Ordinal) &&
        requested.CaretOffset == current.CaretOffset;

    internal static string DescribeBaselineChange(string baseline, string current)
    {
        if (string.Equals(baseline, current, StringComparison.Ordinal))
        {
            return "（识别题目后尚无文本变化）";
        }

        var prefix = 0;
        var sharedLength = Math.Min(baseline.Length, current.Length);
        while (prefix < sharedLength && baseline[prefix] == current[prefix]) prefix++;

        var suffix = 0;
        while (suffix < baseline.Length - prefix &&
               suffix < current.Length - prefix &&
               baseline[baseline.Length - 1 - suffix] == current[current.Length - 1 - suffix])
        {
            suffix++;
        }

        var removedLength = baseline.Length - prefix - suffix;
        var insertedLength = current.Length - prefix - suffix;
        var inserted = insertedLength <= 0 ? string.Empty : current.Substring(prefix, insertedLength);
        return $"变化起点（文档字符偏移）：{prefix}\n" +
               $"删除字符数：{removedLength}\n" +
               $"新增或替换后的文字：{(inserted.Length == 0 ? "（无）" : Limit(inserted, 1_200))}";
    }

    private static IReadOnlyList<WordExerciseTextBlock> ReadBlocks(
        string text,
        int windowStart,
        int windowEnd,
        int caret)
    {
        var blocks = new List<WordExerciseTextBlock>();
        var offset = windowStart;
        while (offset < windowEnd)
        {
            var start = offset;
            while (offset < windowEnd && !IsBlockSeparator(text[offset])) offset++;
            var contentEnd = offset;
            if (offset < windowEnd)
            {
                var separator = text[offset++];
                if (separator == '\r' && offset < windowEnd && text[offset] == '\n') offset++;
            }

            blocks.Add(new WordExerciseTextBlock
            {
                Id = $"B{blocks.Count + 1:000}",
                Start = start,
                ContentEnd = contentEnd,
                End = offset,
                Text = text[start..contentEnd],
                ContainsCaret = caret >= start && (caret < offset || offset == windowEnd && caret == offset)
            });
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new WordExerciseTextBlock
            {
                Id = "B001",
                Start = windowStart,
                ContentEnd = windowEnd,
                End = windowEnd,
                ContainsCaret = true
            });
        }
        return blocks;
    }

    private static string BuildBlocksForPrompt(IReadOnlyList<WordExerciseTextBlock> blocks, int caret)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            var text = SanitizeForPrompt(block.Text);
            if (block.ContainsCaret)
            {
                var relativeCaret = Math.Clamp(caret - block.Start, 0, block.Text.Length);
                var prefix = SanitizeForPrompt(block.Text[..relativeCaret]);
                var suffix = SanitizeForPrompt(block.Text[relativeCaret..]);
                text = prefix + "<CURSOR>" + suffix;
            }
            builder.Append('[').Append(block.Id).Append("] ").AppendLine(text);
        }
        return builder.ToString().TrimEnd();
    }

    private static string SanitizeForPrompt(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(char.IsControl(character) && character is not '\t' ? ' ' : character);
        }
        return builder.ToString().Replace("<CURSOR>", "‹文档中的 CURSOR 字样›", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockSeparator(char character) =>
        character is '\r' or '\n' or '\v' or '\f' or '\a';

    private static int FindBlockIndex(WordExerciseRequestContext context, string? blockId)
    {
        if (string.IsNullOrWhiteSpace(blockId)) return -1;
        for (var index = 0; index < context.Blocks.Count; index++)
        {
            if (string.Equals(context.Blocks[index].Id, blockId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool ContainsBoundaryAnchor(string question, string? anchor, bool atStart)
    {
        var compactAnchor = DocumentContextFormatter.Compact(anchor);
        if (compactAnchor.Length == 0)
        {
            return false;
        }

        const int boundaryAllowance = 80;
        var compactQuestion = DocumentContextFormatter.Compact(question);
        var boundary = atStart
            ? compactQuestion[..Math.Min(compactQuestion.Length, boundaryAllowance)]
            : compactQuestion[Math.Max(0, compactQuestion.Length - boundaryAllowance)..];
        return boundary.Contains(compactAnchor, StringComparison.Ordinal);
    }

    private static string TrimDocumentNoise(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && (char.IsWhiteSpace(text[start]) || char.IsControl(text[start]))) start++;
        while (end > start && (char.IsWhiteSpace(text[end - 1]) || char.IsControl(text[end - 1]))) end--;
        return text[start..end];
    }

    private static int MoveOffLowSurrogate(string text, int offset) =>
        offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]) ? offset - 1 : offset;

    private static int MovePastHighSurrogate(string text, int offset) =>
        offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset])
            ? offset + 1
            : offset;

    private static string Limit(string text, int maximumCharacters)
    {
        if (text.Length <= maximumCharacters) return text;
        var half = maximumCharacters / 2;
        return text[..half] + "\n…（中间已省略）…\n" + text[^half..];
    }
}
