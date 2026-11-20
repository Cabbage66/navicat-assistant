using System.Windows.Automation;
using System.Windows.Automation.Text;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed class DingTalkWebDocumentContextService
{
    private const int MaximumTextCharacters = 5_000_000;
    private const int ContextCharacters = 160;
    private const int MaximumDocumentElements = 40;

    public DocumentContextResult Capture(ForegroundWindowContext foreground)
    {
        try
        {
            var root = AutomationElement.FromHandle(foreground.WindowHandle);
            var candidates = new List<WebTextCandidate>();
            var diagnostics = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            TryReadFocusChain(root, foreground.ProcessId, candidates, diagnostics, seen);
            ScanDocumentElements(root, candidates, diagnostics, seen);

            var best = candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.Text.Length)
                .FirstOrDefault();
            if (best is null)
            {
                return Unavailable(
                    foreground,
                    "钉钉云文档已在前台，但浏览器没有暴露可读取的文档文本。",
                    string.Join(" | ", diagnostics.Take(5)));
            }

            var browser = DocumentContextProbeService.BrowserDisplayName(foreground.ProcessName);
            return new DocumentContextResult
            {
                Status = DocumentContextStatus.Captured,
                ApplicationName = $"钉钉云文档（{browser}）",
                ProcessName = foreground.ProcessName,
                ProcessId = foreground.ProcessId,
                WindowHandle = foreground.WindowHandle,
                WindowTitle = foreground.WindowTitle,
                DocumentName = RemoveBrowserSuffix(foreground.WindowTitle),
                DocumentIdentity = ContentHasher.Sha256($"dingtalk|{foreground.WindowTitle}|{foreground.WindowHandle}"),
                Source = best.Source,
                Text = best.Text,
                ContentHash = ContentHasher.Sha256(best.Text),
                CaretOffset = best.CaretOffset,
                ContextBefore = Tail(best.PrefixText, ContextCharacters),
                ContextAfter = Head(best.SuffixText, ContextCharacters),
                CaretAvailable = best.CaretAvailable,
                SelectedText = best.SelectedText,
                SelectionAvailable = best.SelectionAvailable,
                HasSelection = best.HasSelection,
                IsPartial = true,
                Message = best.CaretAvailable
                    ? "已读取钉钉网页编辑区以及浏览器暴露的光标和选区；网页正文可能是当前已加载部分。"
                    : "已读取钉钉网页可访问性文本，但浏览器没有可靠暴露当前光标或选区。",
                Diagnostics = $"candidate={best.Source}; text={best.Text.Length}; exact={best.ReassemblesExactly}; " +
                              string.Join(" | ", diagnostics.Take(3))
            };
        }
        catch (Exception ex)
        {
            return Unavailable(
                foreground,
                "钉钉云文档已在前台，但 UI Automation 读取失败。",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryReadFocusChain(
        AutomationElement root,
        int processId,
        List<WebTextCandidate> candidates,
        List<string> diagnostics,
        HashSet<string> seen)
    {
        AutomationElement? current;
        try
        {
            current = AutomationElement.FocusedElement;
        }
        catch
        {
            return;
        }

        for (var depth = 0; current is not null && depth < 20; depth++)
        {
            var currentProcessId = SafeGet(() => current.Current.ProcessId, -1);
            if (currentProcessId != processId)
            {
                break;
            }

            if (SafeGet(() => current.Current.ControlType == ControlType.Document, false))
            {
                TryAddCandidate(current, true, candidates, diagnostics, seen);
            }

            if (SafeGet(() => current.Equals(root), false))
            {
                break;
            }

            try
            {
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            catch
            {
                break;
            }
        }
    }

    private static void ScanDocumentElements(
        AutomationElement root,
        List<WebTextCandidate> candidates,
        List<string> diagnostics,
        HashSet<string> seen)
    {
        if (SafeGet(() => root.Current.ControlType == ControlType.Document, false))
        {
            TryAddCandidate(root, false, candidates, diagnostics, seen);
        }

        AutomationElementCollection documents;
        try
        {
            documents = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        }
        catch (Exception ex)
        {
            diagnostics.Add($"scan:{ex.GetType().Name}:{ex.Message}");
            return;
        }

        var count = Math.Min(documents.Count, MaximumDocumentElements);
        for (var index = 0; index < count; index++)
        {
            TryAddCandidate(documents[index], false, candidates, diagnostics, seen);
        }
    }

    private static void TryAddCandidate(
        AutomationElement element,
        bool isFocusChain,
        List<WebTextCandidate> candidates,
        List<string> diagnostics,
        HashSet<string> seen)
    {
        var runtimeId = SafeGet(
            () => string.Join('.', element.GetRuntimeId()),
            $"fallback:{SafeGet(() => element.Current.NativeWindowHandle, 0)}:{SafeGet(() => element.Current.AutomationId, string.Empty)}");
        if (!seen.Add(runtimeId))
        {
            return;
        }

        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                patternObject is not TextPattern pattern)
            {
                return;
            }

            var documentRange = pattern.DocumentRange;
            var text = documentRange.GetText(MaximumTextCharacters) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var automationId = SafeGet(() => element.Current.AutomationId, string.Empty);
            var className = SafeGet(() => element.Current.ClassName, string.Empty);
            var prefixText = string.Empty;
            var suffixText = string.Empty;
            var selectedText = string.Empty;
            var caretOffset = (int?)null;
            var caretAvailable = false;
            var selectionAvailable = false;
            var hasSelection = false;
            var reassemblesExactly = false;
            var selectionRangeInside = false;

            TextPatternRange[] selectionRanges;
            try
            {
                selectionRanges = pattern.GetSelection();
            }
            catch
            {
                selectionRanges = [];
            }

            if (selectionRanges.Length > 0)
            {
                var selectionRange = selectionRanges[0];
                var startsInside = documentRange.CompareEndpoints(
                    TextPatternRangeEndpoint.Start,
                    selectionRange,
                    TextPatternRangeEndpoint.Start) <= 0;
                var endsInside = documentRange.CompareEndpoints(
                    TextPatternRangeEndpoint.End,
                    selectionRange,
                    TextPatternRangeEndpoint.End) >= 0;
                if (startsInside && endsInside)
                {
                    selectionRangeInside = true;
                    hasSelection = selectionRange.CompareEndpoints(
                        TextPatternRangeEndpoint.Start,
                        selectionRange,
                        TextPatternRangeEndpoint.End) != 0;
                    selectedText = selectionRange.GetText(MaximumTextCharacters) ?? string.Empty;

                    var prefixRange = documentRange.Clone();
                    prefixRange.MoveEndpointByRange(
                        TextPatternRangeEndpoint.End,
                        selectionRange,
                        TextPatternRangeEndpoint.Start);
                    prefixText = prefixRange.GetText(MaximumTextCharacters) ?? string.Empty;

                    var suffixRange = documentRange.Clone();
                    suffixRange.MoveEndpointByRange(
                        TextPatternRangeEndpoint.Start,
                        selectionRange,
                        TextPatternRangeEndpoint.End);
                    suffixText = suffixRange.GetText(MaximumTextCharacters) ?? string.Empty;

                    reassemblesExactly = string.Equals(
                        prefixText + selectedText + suffixText,
                        text,
                        StringComparison.Ordinal);
                    caretAvailable = reassemblesExactly;
                    caretOffset = reassemblesExactly ? prefixText.Length : null;
                    selectionAvailable = !hasSelection || selectedText.Length > 0;
                }
            }

            var score = Math.Min(text.Length, 20_000);
            if (automationId.Equals("RootWebArea", StringComparison.OrdinalIgnoreCase)) score += 2_000;
            if (isFocusChain) score += 4_000;
            if (selectionRangeInside) score += 5_000;
            if (reassemblesExactly) score += 20_000;
            candidates.Add(new WebTextCandidate(
                text,
                prefixText,
                suffixText,
                selectedText,
                caretOffset,
                caretAvailable,
                selectionAvailable,
                hasSelection,
                reassemblesExactly,
                score,
                $"UIA TextPattern {automationId}/{className}"));
        }
        catch (Exception ex)
        {
            diagnostics.Add($"candidate:{ex.GetType().Name}:{ex.Message}");
        }
    }

    private static DocumentContextResult Unavailable(
        ForegroundWindowContext foreground,
        string message,
        string? diagnostics = null) => new()
    {
        Status = DocumentContextStatus.Unavailable,
        ApplicationName = $"钉钉云文档（{DocumentContextProbeService.BrowserDisplayName(foreground.ProcessName)}）",
        ProcessName = foreground.ProcessName,
        ProcessId = foreground.ProcessId,
        WindowHandle = foreground.WindowHandle,
        WindowTitle = foreground.WindowTitle,
        DocumentName = RemoveBrowserSuffix(foreground.WindowTitle),
        DocumentIdentity = ContentHasher.Sha256($"dingtalk|{foreground.WindowTitle}|{foreground.WindowHandle}"),
        Source = "Browser UI Automation",
        Message = message,
        Diagnostics = diagnostics,
        IsPartial = true
    };

    private static string RemoveBrowserSuffix(string title)
    {
        var separator = title.LastIndexOf(" - ", StringComparison.Ordinal);
        return separator > 0 ? title[..separator] : title;
    }

    private static T SafeGet<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    private static string Head(string text, int maximumCharacters)
        => text.Length <= maximumCharacters ? text : text[..maximumCharacters];

    private static string Tail(string text, int maximumCharacters)
        => text.Length <= maximumCharacters ? text : text[^maximumCharacters..];

    private sealed record WebTextCandidate(
        string Text,
        string PrefixText,
        string SuffixText,
        string SelectedText,
        int? CaretOffset,
        bool CaretAvailable,
        bool SelectionAvailable,
        bool HasSelection,
        bool ReassemblesExactly,
        int Score,
        string Source);
}
