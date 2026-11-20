using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal static class WordExercisePresentation
{
    public static string ToDisplayType(string questionType) => questionType switch
    {
        "choice" => "选择题",
        "fill_blank" => "填空题",
        "short_answer" => "简答题",
        _ => "题目"
    };

    public static string SelectTaskbarAnswer(
        SuggestionResult result,
        string questionType,
        string fullAnswer,
        string insertText,
        bool continuation)
    {
        var normalizedType = WordExerciseContextBuilder.NormalizeQuestionType(questionType);
        var modelDisplay = result.DisplayText.Trim();
        var nextText = insertText.Trim();
        var completeAnswer = fullAnswer.Trim();

        if (normalizedType == "choice")
        {
            return !string.IsNullOrWhiteSpace(modelDisplay)
                ? modelDisplay
                : !string.IsNullOrWhiteSpace(completeAnswer) ? completeAnswer : nextText;
        }

        if (string.Equals(result.AnswerState, "partial", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(nextText))
        {
            return nextText;
        }

        if (continuation && !string.IsNullOrWhiteSpace(nextText))
        {
            return nextText;
        }

        if (normalizedType == "fill_blank")
        {
            return !string.IsNullOrWhiteSpace(nextText)
                ? nextText
                : !string.IsNullOrWhiteSpace(modelDisplay) ? modelDisplay : completeAnswer;
        }

        if (string.Equals(result.AnswerState, "complete", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(nextText) ? nextText : "答案已基本完整";
        }

        if (string.Equals(result.AnswerState, "empty", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(completeAnswer)
                ? completeAnswer
                : !string.IsNullOrWhiteSpace(nextText) ? nextText : modelDisplay;
        }

        return !string.IsNullOrWhiteSpace(modelDisplay)
            ? modelDisplay
            : !string.IsNullOrWhiteSpace(nextText) ? nextText : completeAnswer;
    }

    public static SuggestionResult AdvanceFollowingSuggestion(
        SuggestionResult current,
        WordExerciseQuestionState question,
        SuggestionAcceptanceProgress progress)
    {
        var body = progress.IsComplete
            ? "当前建议已写完"
            : progress.RemainingText;
        return current with
        {
            Timestamp = DateTimeOffset.Now,
            Mode = SuggestionMode.ExerciseContinuation,
            DisplayText = BuildDisplayText(
                body,
                progress.RemainingText,
                question.QuestionType,
                question.QuestionNumber,
                continuation: true),
            InsertText = progress.RemainingText,
            SameAsCurrentQuestion = true,
            Explanation = progress.IsComplete
                ? "已输入完当前建议，Assistant 正在结合最新作答分析下一个得分点。"
                : current.Explanation
        };
    }

    public static string BuildDisplayText(
        string displayText,
        string insertText,
        string questionType,
        string questionNumber,
        bool continuation) =>
        BuildDisplayTextCore(
            displayText,
            insertText,
            questionType,
            questionNumber,
            continuation,
            maximumLength: 100);

    public static string BuildExpandedDisplayText(
        string displayText,
        string insertText,
        string questionType,
        string questionNumber,
        bool continuation) =>
        BuildDisplayTextCore(
            displayText,
            insertText,
            questionType,
            questionNumber,
            continuation,
            maximumLength: null);

    private static string BuildDisplayTextCore(
        string displayText,
        string insertText,
        string questionType,
        string questionNumber,
        bool continuation,
        int? maximumLength)
    {
        var typeText = ToDisplayType(questionType);
        var number = WordExerciseContextBuilder.NormalizeQuestionNumber(questionNumber);
        var prefix = $"{typeText}｜{number}.";
        var answer = DocumentContextFormatter.Compact(
            string.IsNullOrWhiteSpace(displayText) ? insertText : displayText);
        string[] repeatedNumberPrefixes =
        [
            $"第{number}题：",
            $"第{number}题:",
            $"第{number}题.",
            $"第{number}题。",
            $"第{number}题",
            $"【{number}】",
            $"[{number}]",
            $"{number}.",
            $"{number}。",
            $"{number}、",
            $"{number}．",
            $"{number}：",
            $"{number}:"
        ];
        foreach (var repeatedNumber in repeatedNumberPrefixes)
        {
            if (answer.StartsWith(repeatedNumber, StringComparison.OrdinalIgnoreCase))
            {
                answer = answer[repeatedNumber.Length..].TrimStart();
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = continuation ? "答案已较完整，暂时无需续写" : "暂时没有可靠答案";
        }
        if (maximumLength is int limit)
        {
            var maximumAnswerLength = Math.Max(1, limit - prefix.Length);
            if (answer.Length > maximumAnswerLength)
            {
                answer = answer[..Math.Max(1, maximumAnswerLength - 1)] + "…";
            }
        }
        return prefix + answer;
    }
}
