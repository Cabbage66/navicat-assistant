using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal sealed record CaretLocation(PixelRect Bounds, string Source, double Confidence);

internal static class ForegroundCaretLocator
{
    public static CaretLocation? Locate(ForegroundWindowContext foreground)
    {
        if (foreground.WindowHandle == nint.Zero || foreground.ProcessId <= 0)
        {
            return null;
        }

        return TryLocateWithTextPattern(foreground) ?? TryLocateWithGuiThreadInfo(foreground);
    }

    private static CaretLocation? TryLocateWithTextPattern(ForegroundWindowContext foreground)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            for (var depth = 0; element is not null && depth < 12; depth++)
            {
                if (element.Current.ProcessId != foreground.ProcessId)
                {
                    return null;
                }

                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var rawPattern) &&
                    rawPattern is TextPattern pattern)
                {
                    var ranges = pattern.GetSelection();
                    if (ranges.Length > 0 && string.IsNullOrEmpty(ranges[0].GetText(1)))
                    {
                        var location = LocateDegenerateRange(ranges[0]);
                        if (location is not null)
                        {
                            return location;
                        }
                    }
                }

                element = TreeWalker.RawViewWalker.GetParent(element);
            }
        }
        catch
        {
            // UI Automation providers may disappear or reject cross-process calls.
        }

        return null;
    }

    private static CaretLocation? LocateDegenerateRange(TextPatternRange caretRange)
    {
        var forward = caretRange.Clone();
        try
        {
            if (forward.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.End,
                    TextUnit.Character,
                    1) != 0)
            {
                var rectangles = forward.GetBoundingRectangles();
                var rectangle = rectangles.FirstOrDefault(value =>
                    value.Width > 0 && value.Height > 0 && IsFinite(value));
                if (rectangle.Width > 0 && rectangle.Height > 0)
                {
                    return new CaretLocation(
                        ToCaretRect(rectangle.Left, rectangle.Top, rectangle.Height),
                        "UI Automation TextPattern.GetSelection",
                        0.82);
                }
            }
        }
        catch
        {
            // Try expanding backwards when the caret is at the document end.
        }

        var backward = caretRange.Clone();
        try
        {
            if (backward.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.Start,
                    TextUnit.Character,
                    -1) != 0)
            {
                var rectangles = backward.GetBoundingRectangles();
                var rectangle = rectangles.LastOrDefault(value =>
                    value.Width > 0 && value.Height > 0 && IsFinite(value));
                if (rectangle.Width > 0 && rectangle.Height > 0)
                {
                    return new CaretLocation(
                        ToCaretRect(rectangle.Right, rectangle.Top, rectangle.Height),
                        "UI Automation TextPattern.GetSelection",
                        0.8);
                }
            }
        }
        catch
        {
            // The provider does not expose a usable range rectangle.
        }

        return null;
    }

    private static CaretLocation? TryLocateWithGuiThreadInfo(ForegroundWindowContext foreground)
    {
        try
        {
            var threadId = NativeMethods.GetWindowThreadProcessId(foreground.WindowHandle, out _);
            if (threadId == 0)
            {
                return null;
            }

            var info = new NativeMethods.GuiThreadInfo
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
            };
            if (!NativeMethods.GetGUIThreadInfo(threadId, ref info) || info.CaretWindow == nint.Zero)
            {
                return null;
            }

            var caretRoot = NativeMethods.GetAncestor(info.CaretWindow, NativeMethods.GaRoot);
            if (caretRoot != nint.Zero && caretRoot != foreground.RootWindowHandle)
            {
                return null;
            }

            var rectangle = info.CaretRectangle;
            _ = NativeMethods.MapWindowPoints(info.CaretWindow, nint.Zero, ref rectangle, 2);
            var width = Math.Max(2, rectangle.Width);
            var height = Math.Max(1, rectangle.Height);
            if (height > 300)
            {
                return null;
            }

            return new CaretLocation(
                new PixelRect(rectangle.Left, rectangle.Top, width, height),
                "Win32 GetGUIThreadInfo.rcCaret",
                0.9);
        }
        catch
        {
            return null;
        }
    }

    private static PixelRect ToCaretRect(double x, double y, double height) => new(
        (int)Math.Round(x),
        (int)Math.Round(y),
        2,
        Math.Max(1, (int)Math.Ceiling(height)));

    private static bool IsFinite(System.Windows.Rect rectangle) =>
        double.IsFinite(rectangle.Left) &&
        double.IsFinite(rectangle.Top) &&
        double.IsFinite(rectangle.Width) &&
        double.IsFinite(rectangle.Height);
}
