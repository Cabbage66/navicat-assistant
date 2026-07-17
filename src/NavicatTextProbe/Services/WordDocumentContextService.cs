using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal sealed class WordDocumentContextService : IDisposable
{
    private const int MainTextStory = 1;
    private const int ContextCharacters = 160;
    private const int MaximumAttempts = 3;
    private readonly BlockingCollection<WorkItem> _workItems = new();
    private readonly Thread _thread;
    private object? _application;
    private bool _disposed;

    public WordDocumentContextService()
    {
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Assistant Word document probe"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<DocumentContextResult> CaptureAsync(ForegroundWindowContext foreground)
    {
        if (_disposed)
        {
            return Task.FromResult(Unavailable(foreground, "Word 探测器已经停止。"));
        }

        var completion = new TaskCompletionSource<DocumentContextResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _workItems.Add(new WorkItem(foreground, completion));
        }
        catch (InvalidOperationException)
        {
            completion.TrySetResult(Unavailable(foreground, "Word 探测器已经停止。"));
        }

        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workItems.CompleteAdding();
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _workItems.Dispose();
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var workItem in _workItems.GetConsumingEnumerable())
            {
                try
                {
                    workItem.Completion.TrySetResult(CaptureWithRetry(workItem.Foreground));
                }
                catch (Exception ex)
                {
                    workItem.Completion.TrySetResult(Unavailable(
                        workItem.Foreground,
                        "无法读取 Word 活动文档。",
                        DescribeException(ex)));
                }
            }
        }
        finally
        {
            ReleaseComObject(_application);
            _application = null;
        }
    }

    private DocumentContextResult CaptureWithRetry(ForegroundWindowContext foreground)
    {
        DocumentContextResult? lastSnapshot = null;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                _application ??= NativeMethods.GetActiveComObject("Word.Application");
                var snapshot = CaptureSnapshot(foreground);
                lastSnapshot = snapshot;
                if (snapshot.Status == DocumentContextStatus.Captured && !snapshot.IsPartial)
                {
                    return snapshot;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (attempt < MaximumAttempts)
            {
                Thread.Sleep(25);
            }
        }

        if (lastSnapshot is not null)
        {
            return lastSnapshot;
        }

        ReleaseComObject(_application);
        _application = null;
        return Unavailable(
            foreground,
            "Word 已在前台，但暂时无法读取正文、光标或选区。",
            lastError is null ? null : DescribeException(lastError));
    }

    private DocumentContextResult CaptureSnapshot(ForegroundWindowContext foreground)
    {
        object? protectedViewWindow = null;
        object? document = null;
        object? activeWindow = null;
        object? selection = null;
        object? content = null;
        try
        {
            dynamic application = _application!;
            try
            {
                protectedViewWindow = (object?)application.ActiveProtectedViewWindow;
            }
            catch
            {
                protectedViewWindow = null;
            }

            if (protectedViewWindow is not null)
            {
                document = (object?)((dynamic)protectedViewWindow).Document;
            }
            else
            {
                object? documents = null;
                try
                {
                    documents = (object?)application.Documents;
                    var documentCount = documents is null
                        ? 0
                        : Convert.ToInt32(((dynamic)documents).Count);
                    if (documentCount <= 0)
                    {
                        return Unavailable(foreground, "Word 当前没有打开的文档。");
                    }
                }
                finally
                {
                    ReleaseComObject(documents);
                }
                document = (object?)application.ActiveDocument;
            }

            if (document is null)
            {
                return Unavailable(foreground, "无法取得 Word 活动文档。");
            }

            try
            {
                activeWindow = (object?)((dynamic)document).ActiveWindow;
            }
            catch
            {
                activeWindow = (object?)application.ActiveWindow;
            }
            if (activeWindow is null)
            {
                return Unavailable(foreground, "无法取得 Word 活动文档窗口。");
            }

            var wordWindowHandle = new nint(Convert.ToInt64(((dynamic)activeWindow).Hwnd));
            var wordRootWindowHandle = NativeMethods.GetAncestor(wordWindowHandle, NativeMethods.GaRoot);
            if (wordRootWindowHandle == nint.Zero)
            {
                wordRootWindowHandle = wordWindowHandle;
            }
            if (foreground.RootWindowHandle != wordRootWindowHandle &&
                foreground.WindowHandle != wordWindowHandle)
            {
                return Unavailable(foreground, "Word 活动文档已经切换，等待下一次读取。");
            }

            try
            {
                selection = (object?)((dynamic)activeWindow).Selection;
            }
            catch
            {
                selection = (object?)application.Selection;
            }
            if (selection is null)
            {
                return Unavailable(foreground, "无法取得 Word 当前光标或选区。");
            }

            content = (object?)((dynamic)document).Content;
            if (content is null)
            {
                return Unavailable(foreground, "无法取得 Word 文档正文。");
            }

            var fullText = Convert.ToString(((dynamic)content).Text) ?? string.Empty;
            var contentStart = Convert.ToInt32(((dynamic)content).Start);
            var contentEnd = Convert.ToInt32(((dynamic)content).End);
            var selectionStart = Convert.ToInt32(((dynamic)selection).Start);
            var selectionEnd = Convert.ToInt32(((dynamic)selection).End);
            var storyType = Convert.ToInt32(((dynamic)selection).StoryType);
            var isMainStory = storyType == MainTextStory &&
                              selectionStart >= contentStart &&
                              selectionEnd <= contentEnd;

            var prefixText = string.Empty;
            var selectedText = string.Empty;
            var suffixText = string.Empty;
            var reassemblesExactly = false;
            if (isMainStory)
            {
                prefixText = ReadRangeText(document, contentStart, selectionStart);
                if (selectionEnd > selectionStart)
                {
                    selectedText = ReadRangeText(document, selectionStart, selectionEnd);
                }
                suffixText = ReadRangeText(document, selectionEnd, contentEnd);
                reassemblesExactly = string.Equals(
                    prefixText + selectedText + suffixText,
                    fullText,
                    StringComparison.Ordinal);
            }
            else if (selectionEnd > selectionStart)
            {
                object? selectionRange = null;
                try
                {
                    selectionRange = (object?)((dynamic)selection).Range;
                    selectedText = Convert.ToString(((dynamic)selectionRange!).Text) ?? string.Empty;
                }
                finally
                {
                    ReleaseComObject(selectionRange);
                }
            }

            var documentName = Convert.ToString(((dynamic)document).Name) ?? string.Empty;
            string documentFullName;
            try
            {
                documentFullName = Convert.ToString(((dynamic)document).FullName) ?? documentName;
            }
            catch
            {
                documentFullName = documentName;
            }
            var isPartial = !isMainStory || !reassemblesExactly;
            return new DocumentContextResult
            {
                Status = DocumentContextStatus.Captured,
                ApplicationName = "Microsoft Word",
                ProcessName = foreground.ProcessName,
                ProcessId = foreground.ProcessId,
                WindowHandle = foreground.WindowHandle,
                WindowTitle = foreground.WindowTitle,
                DocumentName = documentName,
                DocumentIdentity = ContentHasher.Sha256(
                    $"word|{documentFullName}|{wordWindowHandle.ToInt64()}"),
                Source = "Word COM Document.Content + Range + Selection",
                Text = fullText,
                ContentHash = ContentHasher.Sha256(fullText),
                CaretOffset = isMainStory && reassemblesExactly ? prefixText.Length : null,
                ContextBefore = Tail(prefixText, ContextCharacters),
                ContextAfter = Head(suffixText, ContextCharacters),
                CaretAvailable = isMainStory && reassemblesExactly,
                SelectedText = selectedText,
                SelectionAvailable = true,
                HasSelection = selectionEnd > selectionStart,
                IsPartial = isPartial,
                Message = isPartial
                    ? "Word 正文已读取，但当前光标不在主正文或快照发生变化。"
                    : "已读取 Word 正文、光标和选区。",
                Diagnostics = $"story={storyType}; selection={selectionStart}-{selectionEnd}; exact={reassemblesExactly}"
            };
        }
        finally
        {
            ReleaseComObject(content);
            ReleaseComObject(selection);
            ReleaseComObject(activeWindow);
            ReleaseComObject(document);
            ReleaseComObject(protectedViewWindow);
        }
    }

    private static string ReadRangeText(object document, int start, int end)
    {
        object? range = null;
        try
        {
            range = (object?)((dynamic)document).Range(start, end);
            return range is null ? string.Empty : Convert.ToString(((dynamic)range).Text) ?? string.Empty;
        }
        finally
        {
            ReleaseComObject(range);
        }
    }

    private static DocumentContextResult Unavailable(
        ForegroundWindowContext foreground,
        string message,
        string? diagnostics = null) => new()
    {
        Status = DocumentContextStatus.Unavailable,
        ApplicationName = "Microsoft Word",
        ProcessName = foreground.ProcessName,
        ProcessId = foreground.ProcessId,
        WindowHandle = foreground.WindowHandle,
        WindowTitle = foreground.WindowTitle,
        Source = "Word COM",
        Message = message,
        Diagnostics = diagnostics,
        IsPartial = true
    };

    private static string DescribeException(Exception exception)
        => $"{exception.GetType().Name}: {exception.Message}";

    private static string Head(string text, int maximumCharacters)
        => text.Length <= maximumCharacters ? text : text[..maximumCharacters];

    private static string Tail(string text, int maximumCharacters)
        => text.Length <= maximumCharacters ? text : text[^maximumCharacters..];

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            _ = Marshal.ReleaseComObject(value);
        }
        catch
        {
            // Releasing an Office RCW is best effort during a read-only snapshot.
        }
    }

    private sealed record WorkItem(
        ForegroundWindowContext Foreground,
        TaskCompletionSource<DocumentContextResult> Completion);
}
