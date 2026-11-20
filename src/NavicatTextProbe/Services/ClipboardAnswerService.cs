using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed record ClipboardAnswer(string Text, string Kind)
{
    public int CharacterCount => Text.Length;
}

internal static class ClipboardAnswerService
{
    internal const int MaximumClipboardCharacters = 200_000;
    internal const int MaximumQuestionCharacters = 200_000;

    public static bool TryNormalizeQuestion(string? value, out string question, out string error)
    {
        question = value?.Trim() ?? string.Empty;
        if (question.Length == 0)
        {
            error = "剪贴板中没有可识别的文本题目。";
            return false;
        }
        if (question.Length > MaximumQuestionCharacters)
        {
            error = $"剪贴板题目超过 {MaximumQuestionCharacters:N0} 字符，请缩小复制范围后重试。";
            question = string.Empty;
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static ClipboardAnswer? Select(SuggestionResult result, bool sqlAnswer)
    {
        if (result.Mode == SuggestionMode.Error || result.IsLoading)
        {
            return null;
        }

        var answer = FirstNonEmpty(result.FullAnswer, result.InsertText);
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        var normalized = StripOuterCodeFence(answer).Trim();
        if (normalized.Length == 0)
        {
            return null;
        }
        if (normalized.Length > MaximumClipboardCharacters)
        {
            normalized = normalized[..MaximumClipboardCharacters];
        }

        var kind = sqlAnswer
            ? "完整 SQL"
            : result.QuestionType switch
            {
                "sql" => "完整 SQL",
                "choice" => "选择题完整答案",
                "fill_blank" => "填空题完整答案",
                "short_answer" => "简答题完整答案",
                _ => "完整答案"
            };
        return new ClipboardAnswer(normalized, kind);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string StripOuterCodeFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return text;
        }
        text = text[(firstLineEnd + 1)..];
        var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0 && string.IsNullOrWhiteSpace(text[(closingFence + 3)..]))
        {
            text = text[..closingFence];
        }
        return text.Trim();
    }
}
