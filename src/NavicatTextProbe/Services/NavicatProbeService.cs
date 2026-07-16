using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal sealed class NavicatProbeService
{
    private const int MaxTextCharacters = 5_000_000;
    private const int MaxScannedElements = 300;
    private const int MaxLoggedElements = 120;
    private static readonly TimeSpan IdleHeartbeatInterval = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly object _cacheLock = new();
    private ProbeResult? _cachedCapture;
    private nint _cachedWindowHandle;
    private uint _cachedInputTick;
    private DateTimeOffset _lastFullCaptureAt = DateTimeOffset.MinValue;

    public ProbeResult CaptureForegroundNavicat(bool forceRefresh = false)
    {
        _captureGate.Wait();
        try
        {
            return CaptureForegroundNavicatCore(forceRefresh);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private ProbeResult CaptureForegroundNavicatCore(bool forceRefresh)
    {
        var windowHandle = NativeMethods.GetForegroundWindow();
        if (windowHandle == nint.Zero)
        {
            return NotForeground("当前没有前台窗口。");
        }

        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var rawProcessId);
        if (rawProcessId == 0)
        {
            return NotForeground("无法识别当前前台进程。");
        }

        var processId = checked((int)rawProcessId);
        string processName;
        try
        {
            using var process = Process.GetProcessById(processId);
            processName = process.ProcessName;
        }
        catch
        {
            processName = string.Empty;
        }

        var windowTitle = NativeMethods.ReadWindowTitle(windowHandle);
        if (!IsNavicat(processName, windowTitle))
        {
            return NotForeground($"当前前台进程是 {processName}。", processName, processId, windowHandle, windowTitle);
        }

        var inputTick = NativeMethods.ReadLastInputTick();
        lock (_cacheLock)
        {
            if (!forceRefresh &&
                _cachedCapture is not null &&
                _cachedWindowHandle == windowHandle &&
                _cachedInputTick == inputTick &&
                DateTimeOffset.UtcNow - _lastFullCaptureAt < IdleHeartbeatInterval)
            {
                return _cachedCapture;
            }
        }

        try
        {
            var capture = CaptureNavicatWindow(windowHandle, processName, processId, windowTitle);
            if (capture.Status == ProbeStatus.Captured)
            {
                lock (_cacheLock)
                {
                    _cachedCapture = capture;
                    _cachedWindowHandle = windowHandle;
                    _cachedInputTick = inputTick;
                    _lastFullCaptureAt = DateTimeOffset.UtcNow;
                }
            }

            return capture;
        }
        catch (Exception ex)
        {
            return new ProbeResult
            {
                Status = ProbeStatus.Failed,
                Message = "访问 Navicat 的 UI Automation 接口失败。",
                ProcessName = processName,
                ProcessId = processId,
                WindowHandle = windowHandle,
                WindowTitle = windowTitle,
                Diagnostics = ex.ToString()
            };
        }
    }

    private static ProbeResult CaptureNavicatWindow(
        nint windowHandle,
        string processName,
        int processId,
        string windowTitle)
    {
        var root = AutomationElement.FromHandle(windowHandle);
        var focused = AutomationElement.FocusedElement;
        var diagnostics = new List<ElementDiagnostic>();
        var candidates = new List<TextCandidate>();
        var seenRuntimeIds = new HashSet<string>(StringComparer.Ordinal);

        if (focused is not null && SafeGet(() => focused.Current.ProcessId, -1) == processId)
        {
            ReadFocusChain(focused, root, diagnostics, candidates, seenRuntimeIds);
        }

        var bestFromFocus = ChooseBest(candidates);
        var focusLooksLikeEditor = bestFromFocus is not null &&
                                   bestFromFocus.IsFocusChain &&
                                   (bestFromFocus.ControlType.Contains("Document", StringComparison.OrdinalIgnoreCase) ||
                                    bestFromFocus.ControlType.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                                    bestFromFocus.ClassName.Contains("Memo", StringComparison.OrdinalIgnoreCase) ||
                                    bestFromFocus.ClassName.Contains("Editor", StringComparison.OrdinalIgnoreCase));

        var scannedCount = 0;
        if (!focusLooksLikeEditor)
        {
            scannedCount = ScanReadableDescendants(root, diagnostics, candidates, seenRuntimeIds);
        }

        var best = ChooseBest(candidates);
        if (best is null)
        {
            var summary = BuildDiagnosticSummary(diagnostics, scannedCount);
            return new ProbeResult
            {
                Status = ProbeStatus.NoReadableText,
                Message = "焦点控件及窗口内候选控件都没有暴露可读取的 TextPattern、ValuePattern 或编辑器 Name 全文。日志已记录控件信息。",
                ProcessName = processName,
                ProcessId = processId,
                WindowHandle = windowHandle,
                WindowTitle = windowTitle,
                Diagnostics = summary,
                Elements = diagnostics
            };
        }

        var caret = ReadNavicatCaret(root, best.Text);
        var caretPrefixLength = best.CaretPrefixLength ?? caret.Offset;

        return new ProbeResult
        {
            Status = ProbeStatus.Captured,
            Message = "已读取当前 SQL 文档。",
            ProcessName = processName,
            ProcessId = processId,
            WindowHandle = windowHandle,
            WindowTitle = windowTitle,
            Source = best.Source,
            ElementDescription = best.ElementDescription,
            Text = best.Text,
            ContentHash = ContentHasher.Sha256(best.Text),
            CaretLine = caret.Line,
            CaretColumn = caret.Column,
            CaretOffset = caret.Offset,
            CaretPrefixLength = caretPrefixLength,
            SelectionLength = best.SelectionLength,
            IsTruncated = best.IsTruncated,
            Diagnostics = BuildDiagnosticSummary(diagnostics, scannedCount),
            Elements = diagnostics
        };
    }

    private static CaretPosition ReadNavicatCaret(AutomationElement root, string text)
    {
        try
        {
            var status = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "StatusBar.Pane5"));
            if (status is null)
            {
                return CaretPosition.Unknown;
            }

            var statusText = status.Current.Name ?? string.Empty;
            var match = Regex.Match(
                statusText,
                @"Ln\s*(?<line>\d+)\s*,\s*Col\s*(?<column>\d+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success ||
                !int.TryParse(match.Groups["line"].Value, out var line) ||
                !int.TryParse(match.Groups["column"].Value, out var column))
            {
                return CaretPosition.Unknown;
            }

            return new CaretPosition(line, column, CalculateCaretOffset(text, line, column));
        }
        catch
        {
            return CaretPosition.Unknown;
        }
    }

    private static int? CalculateCaretOffset(string text, int line, int column)
    {
        if (line < 1 || column < 1)
        {
            return null;
        }

        var lineStart = 0;
        for (var currentLine = 1; currentLine < line; currentLine++)
        {
            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
            {
                return text.Length;
            }

            lineStart = newline + 1;
        }

        var lineEnd = text.IndexOfAny(['\r', '\n'], lineStart);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        return Math.Clamp(lineStart + column - 1, lineStart, lineEnd);
    }

    private static void ReadFocusChain(
        AutomationElement focused,
        AutomationElement root,
        List<ElementDiagnostic> diagnostics,
        List<TextCandidate> candidates,
        HashSet<string> seenRuntimeIds)
    {
        var current = focused;
        for (var depth = 0; current is not null && depth < 16; depth++)
        {
            AddElement(current, depth, true, diagnostics, candidates, seenRuntimeIds);

            var editorFound = candidates.Any(candidate =>
                candidate.IsFocusChain &&
                (candidate.ControlType.Contains("Document", StringComparison.OrdinalIgnoreCase) ||
                 candidate.ControlType.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                 candidate.ClassName.Contains("Memo", StringComparison.OrdinalIgnoreCase) ||
                 candidate.ClassName.Contains("Editor", StringComparison.OrdinalIgnoreCase)));
            if (editorFound)
            {
                break;
            }

            if (RuntimeIdOf(current) == RuntimeIdOf(root))
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

    private static int ScanReadableDescendants(
        AutomationElement root,
        List<ElementDiagnostic> diagnostics,
        List<TextCandidate> candidates,
        HashSet<string> seenRuntimeIds)
    {
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true),
            new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

        AutomationElementCollection elements;
        try
        {
            elements = root.FindAll(TreeScope.Descendants, condition);
        }
        catch
        {
            return 0;
        }

        var count = Math.Min(elements.Count, MaxScannedElements);
        for (var index = 0; index < count; index++)
        {
            AddElement(elements[index], 1000 + index, false, diagnostics, candidates, seenRuntimeIds);
        }

        return elements.Count;
    }

    private static void AddElement(
        AutomationElement element,
        int depth,
        bool isFocusChain,
        List<ElementDiagnostic> diagnostics,
        List<TextCandidate> candidates,
        HashSet<string> seenRuntimeIds)
    {
        var runtimeId = RuntimeIdOf(element);
        if (!seenRuntimeIds.Add(runtimeId))
        {
            return;
        }

        var diagnostic = DescribeElement(element, depth);
        if (diagnostics.Count < MaxLoggedElements)
        {
            diagnostics.Add(diagnostic);
        }

        var candidateCount = candidates.Count;
        TryReadNavicatEditorName(diagnostic, isFocusChain, candidates);
        if (candidates.Count == candidateCount)
        {
            TryReadTextPattern(element, diagnostic, isFocusChain, candidates);
            TryReadValuePattern(element, diagnostic, isFocusChain, candidates);
        }
    }

    private static void TryReadTextPattern(
        AutomationElement element,
        ElementDiagnostic diagnostic,
        bool isFocusChain,
        List<TextCandidate> candidates)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var rawPattern) ||
                rawPattern is not TextPattern pattern)
            {
                return;
            }

            var text = pattern.DocumentRange.GetText(MaxTextCharacters) ?? string.Empty;
            int? caretPrefixLength = null;
            int? selectionLength = null;

            try
            {
                var selections = pattern.GetSelection();
                if (selections.Length > 0)
                {
                    var selection = selections[0];
                    selectionLength = selection.GetText(MaxTextCharacters)?.Length ?? 0;

                    var beforeCaret = selection.Clone();
                    beforeCaret.MoveEndpointByRange(
                        TextPatternRangeEndpoint.Start,
                        pattern.DocumentRange,
                        TextPatternRangeEndpoint.Start);
                    caretPrefixLength = beforeCaret.GetText(MaxTextCharacters)?.Length ?? 0;
                }
            }
            catch
            {
                // Some providers expose DocumentRange but not a usable selection range.
            }

            candidates.Add(new TextCandidate(
                "UI Automation TextPattern.DocumentRange",
                text,
                caretPrefixLength,
                selectionLength,
                text.Length >= MaxTextCharacters,
                isFocusChain,
                diagnostic));
        }
        catch
        {
            // Keep probing the other patterns and controls.
        }
    }

    private static void TryReadValuePattern(
        AutomationElement element,
        ElementDiagnostic diagnostic,
        bool isFocusChain,
        List<TextCandidate> candidates)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawPattern) ||
                rawPattern is not ValuePattern pattern)
            {
                return;
            }

            var rawText = pattern.Current.Value ?? string.Empty;
            var truncated = rawText.Length > MaxTextCharacters;
            var text = truncated ? rawText[..MaxTextCharacters] : rawText;
            candidates.Add(new TextCandidate(
                "UI Automation ValuePattern.Value",
                text,
                null,
                null,
                truncated,
                isFocusChain,
                diagnostic));
        }
        catch
        {
            // Keep probing.
        }
    }

    private static void TryReadNavicatEditorName(
        ElementDiagnostic diagnostic,
        bool isFocusChain,
        List<TextCandidate> candidates)
    {
        var looksLikeNavicatEditor =
            diagnostic.ClassName.Contains("AutoCompleteMemo", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.ClassName.Contains("QueryMemo", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.ClassName.Contains("SqlEditor", StringComparison.OrdinalIgnoreCase);
        var looksLikeFocusedMultilineEditor =
            isFocusChain &&
            diagnostic.Name.Length >= 10 &&
            (diagnostic.Name.Contains('\r') || diagnostic.Name.Contains('\n'));

        if (!looksLikeNavicatEditor && !looksLikeFocusedMultilineEditor)
        {
            return;
        }

        var truncated = diagnostic.Name.Length > MaxTextCharacters;
        var text = truncated ? diagnostic.Name[..MaxTextCharacters] : diagnostic.Name;
        candidates.Add(new TextCandidate(
            "UI Automation AutomationElement.Name（Navicat 编辑器全文）",
            text,
            null,
            null,
            truncated,
            isFocusChain,
            diagnostic));
    }

    private static TextCandidate? ChooseBest(IEnumerable<TextCandidate> candidates) =>
        candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Text.Length)
            .FirstOrDefault();

    private static ElementDiagnostic DescribeElement(AutomationElement element, int depth)
    {
        var patterns = new List<string>();
        try
        {
            patterns.AddRange(element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName));
        }
        catch
        {
            // The element may have disappeared between tree enumeration and inspection.
        }

        return new ElementDiagnostic(
            depth,
            SafeGet(() => element.Current.ControlType.ProgrammaticName, string.Empty),
            SafeGet(() => element.Current.ClassName, string.Empty),
            SafeGet(() => element.Current.AutomationId, string.Empty),
            SafeGet(() => element.Current.FrameworkId, string.Empty),
            SafeGet(() => element.Current.Name, string.Empty),
            patterns);
    }

    private static string RuntimeIdOf(AutomationElement element)
    {
        try
        {
            return string.Join('.', element.GetRuntimeId());
        }
        catch
        {
            return $"fallback:{SafeGet(() => element.Current.NativeWindowHandle, 0)}:" +
                   $"{SafeGet(() => element.Current.AutomationId, string.Empty)}:" +
                   $"{SafeGet(() => element.Current.Name, string.Empty)}";
        }
    }

    private static string BuildDiagnosticSummary(IReadOnlyCollection<ElementDiagnostic> diagnostics, int scannedCount)
    {
        var patternSummary = diagnostics
            .SelectMany(element => element.Patterns)
            .GroupBy(pattern => pattern)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key} × {group.Count()}");

        return $"已记录 {diagnostics.Count} 个焦点链/候选控件；深度扫描返回 {scannedCount} 个候选。" +
               $" 可用模式：{string.Join("，", patternSummary)}";
    }

    private static bool IsNavicat(string processName, string windowTitle)
    {
        if (!string.IsNullOrWhiteSpace(processName))
        {
            return processName.Contains("navicat", StringComparison.OrdinalIgnoreCase);
        }

        // Only use the title when Windows did not let us resolve a process name.
        // SQL files and documentation in unrelated programs may themselves contain "Navicat".
        return windowTitle.Contains("navicat", StringComparison.OrdinalIgnoreCase);
    }

    private static ProbeResult NotForeground(
        string message,
        string processName = "",
        int processId = 0,
        nint windowHandle = default,
        string windowTitle = "") => new()
        {
            Status = ProbeStatus.NavicatNotForeground,
            Message = message,
            ProcessName = processName,
            ProcessId = processId,
            WindowHandle = windowHandle,
            WindowTitle = windowTitle
        };

    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private sealed record TextCandidate(
        string Source,
        string Text,
        int? CaretPrefixLength,
        int? SelectionLength,
        bool IsTruncated,
        bool IsFocusChain,
        ElementDiagnostic Element)
    {
        public string ControlType => Element.ControlType;

        public string ClassName => Element.ClassName;

        public string ElementDescription
        {
            get
            {
                var shortName = Element.Name.Length <= 80 &&
                                !Element.Name.Contains('\r') &&
                                !Element.Name.Contains('\n')
                    ? Element.Name
                    : string.Empty;
                var parts = new[] { Element.ControlType, Element.ClassName, Element.AutomationId, shortName }
                    .Where(part => !string.IsNullOrWhiteSpace(part));
                return string.Join(" / ", parts);
            }
        }

        public int Score
        {
            get
            {
                var score = Source.Contains("Navicat 编辑器全文", StringComparison.Ordinal)
                    ? 1200
                    : Source.Contains("TextPattern", StringComparison.Ordinal) ? 1000 : 600;
                if (IsFocusChain)
                {
                    score += 500;
                }

                if (ControlType.Contains("Document", StringComparison.OrdinalIgnoreCase))
                {
                    score += 400;
                }
                else if (ControlType.Contains("Edit", StringComparison.OrdinalIgnoreCase))
                {
                    score += 350;
                }

                var identity = $"{Element.Name} {Element.ClassName} {Element.AutomationId}";
                if (identity.Contains("query", StringComparison.OrdinalIgnoreCase) ||
                    identity.Contains("sql", StringComparison.OrdinalIgnoreCase) ||
                    identity.Contains("editor", StringComparison.OrdinalIgnoreCase) ||
                    identity.Contains("memo", StringComparison.OrdinalIgnoreCase))
                {
                    score += 250;
                }

                if (Text.Length > 0)
                {
                    score += Math.Min(200, (int)Math.Log10(Text.Length + 1) * 50);
                }

                return score;
            }
        }
    }

    private sealed record CaretPosition(int? Line, int? Column, int? Offset)
    {
        public static CaretPosition Unknown { get; } = new(null, null, null);
    }
}
