using System.Text;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal static class DocumentContextFormatter
{
    private const int CaretPreviewCharacters = 28;
    private const int SelectionPreviewCharacters = 24;

    public static string BuildTaskbarText(DocumentContextResult result)
    {
        var application = Compact(result.ApplicationName);
        if (string.IsNullOrEmpty(application))
        {
            application = "未知软件";
        }

        var caret = result.CaretAvailable
            ? BuildCaretPreview(result.ContextBefore, result.ContextAfter, CaretPreviewCharacters)
            : "（未识别到光标）";
        var selection = result.SelectionAvailable
            ? result.HasSelection
                ? Limit(Compact(result.SelectedText), SelectionPreviewCharacters)
                : "（无）"
            : "（未识别到选区）";

        if (result.HasSelection && string.IsNullOrEmpty(selection))
        {
            selection = "（选区为空）";
        }

        return $"1.当前软件：{application} ｜ 2.光标内容：{caret} ｜ 3.选中内容：{selection}";
    }

    internal static string BuildCaretPreview(string before, string after, int maximumCharacters)
    {
        var compactBefore = Compact(before);
        var compactAfter = Compact(after);
        if (string.IsNullOrEmpty(compactBefore) && string.IsNullOrEmpty(compactAfter))
        {
            return "▏（空）";
        }

        var beforeBudget = maximumCharacters / 2;
        var afterBudget = maximumCharacters - beforeBudget;
        var beforePart = Tail(compactBefore, beforeBudget);
        var afterPart = Head(compactAfter, afterBudget);
        if (compactBefore.Length > beforePart.Length)
        {
            beforePart = "…" + beforePart;
        }
        if (compactAfter.Length > afterPart.Length)
        {
            afterPart += "…";
        }

        return beforePart + "▏" + afterPart;
    }

    internal static string Compact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var character in text)
        {
            var isSpace = char.IsWhiteSpace(character) || char.IsControl(character);
            if (isSpace)
            {
                if (!previousWasSpace && result.Length > 0)
                {
                    result.Append(' ');
                }
                previousWasSpace = true;
                continue;
            }

            result.Append(character);
            previousWasSpace = false;
        }

        return result.ToString().Trim();
    }

    private static string Limit(string text, int maximumCharacters)
        => text.Length <= maximumCharacters ? text : text[..maximumCharacters] + "…";

    private static string Head(string text, int maximumCharacters)
        => text.Length <= maximumCharacters ? text : text[..maximumCharacters];

    private static string Tail(string text, int maximumCharacters)
        => text.Length <= maximumCharacters ? text : text[^maximumCharacters..];
}
