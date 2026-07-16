using System.Diagnostics;
using System.Windows;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal sealed class NavicatSelectionService
{
    private const byte VkControl = 0x11;
    private const byte VkC = 0x43;

    public async Task<(bool Success, string Text, string Error)> CaptureAsync(CancellationToken cancellationToken)
    {
        if (!IsNavicatForeground())
        {
            return (false, string.Empty, "请先在 Navicat 中选中要分析的 SQL，再按诊断快捷键。");
        }

        // Let the analyze hotkey's Ctrl/Alt/E key-up messages finish before sending Ctrl+C.
        await Task.Delay(100, cancellationToken);
        if (!IsNavicatForeground())
        {
            return (false, string.Empty, "快捷键触发后 Navicat 不再是前台窗口，未读取选区。");
        }

        NativeMethods.keybd_event(VkControl, 0, 0, 0);
        NativeMethods.keybd_event(VkC, 0, 0, 0);
        NativeMethods.keybd_event(VkC, 0, NativeMethods.KeyeventfKeyup, 0);
        NativeMethods.keybd_event(VkControl, 0, NativeMethods.KeyeventfKeyup, 0);
        await Task.Delay(180, cancellationToken);

        try
        {
            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
            return string.IsNullOrWhiteSpace(text)
                ? (false, string.Empty, "没有读取到选中的文本。请确认已在 Navicat 编辑器中选中 SQL。")
                : (true, text, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, $"读取剪贴板失败：{ex.Message}");
        }
    }

    public static bool IsNavicatForeground()
    {
        var handle = NativeMethods.GetForegroundWindow();
        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            return string.Equals(
                Process.GetProcessById((int)processId).ProcessName,
                "navicat",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
