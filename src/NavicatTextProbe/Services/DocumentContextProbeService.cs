using System.Diagnostics;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal sealed record ForegroundWindowContext(
    nint WindowHandle,
    nint RootWindowHandle,
    int ProcessId,
    string ProcessName,
    string WindowTitle);

internal sealed class DocumentContextProbeService : IDisposable
{
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "firefox",
        "brave",
        "vivaldi",
        "opera",
        "chromium",
        "qqbrowser",
        "360chrome",
        "360chromex",
        "360se"
    };

    private readonly WordDocumentContextService _wordService = new();
    private readonly DingTalkWebDocumentContextService _dingTalkService = new();

    public Task<DocumentContextResult> CaptureForegroundAsync()
    {
        var foreground = ReadForegroundWindow();
        if (foreground.WindowHandle == nint.Zero)
        {
            return Task.FromResult(DocumentContextResult.NotTarget());
        }

        if (foreground.ProcessName.Equals("WINWORD", StringComparison.OrdinalIgnoreCase))
        {
            return _wordService.CaptureAsync(foreground);
        }

        if (IsDingTalkWebWindow(foreground.ProcessName, foreground.WindowTitle))
        {
            return Task.Run(() => _dingTalkService.Capture(foreground));
        }

        return Task.FromResult(DocumentContextResult.NotTarget());
    }

    internal static bool IsDingTalkWebWindow(string processName, string windowTitle)
    {
        if (!BrowserProcessNames.Contains(processName) || string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        return windowTitle.Contains("钉钉文档", StringComparison.OrdinalIgnoreCase) ||
               windowTitle.Contains("钉钉云文档", StringComparison.OrdinalIgnoreCase) ||
               windowTitle.Contains("DingTalk Docs", StringComparison.OrdinalIgnoreCase) ||
               windowTitle.Contains("AliDocs", StringComparison.OrdinalIgnoreCase);
    }

    internal static string BrowserDisplayName(string processName) => processName.ToLowerInvariant() switch
    {
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "firefox" => "Firefox",
        "brave" => "Brave",
        "vivaldi" => "Vivaldi",
        "opera" => "Opera",
        "qqbrowser" => "QQ浏览器",
        "360chrome" or "360chromex" or "360se" => "360浏览器",
        _ => processName
    };

    public void Dispose() => _wordService.Dispose();

    internal static ForegroundWindowContext ReadForegroundWindow()
    {
        var windowHandle = NativeMethods.GetForegroundWindow();
        if (windowHandle == nint.Zero)
        {
            return new ForegroundWindowContext(0, 0, 0, string.Empty, string.Empty);
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var rawProcessId);
        var processId = unchecked((int)rawProcessId);
        var processName = string.Empty;
        if (processId > 0)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                processName = process.ProcessName;
            }
            catch
            {
                // The foreground process may exit between the native calls.
            }
        }

        var rootWindowHandle = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot);
        return new ForegroundWindowContext(
            windowHandle,
            rootWindowHandle == nint.Zero ? windowHandle : rootWindowHandle,
            processId,
            processName,
            NativeMethods.ReadWindowTitle(windowHandle));
    }
}
