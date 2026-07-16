using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace NavicatTextProbe;

public partial class TaskbarSuggestionWindow : Window
{
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private HwndSource? _windowSource;
    private nint _windowHandle;
    private double _preferredWidth = 640;

    public TaskbarSuggestionWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ConfigureToolWindow();
        _positionTimer.Tick += (_, _) =>
        {
            if (IsVisible) Reposition();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Reposition();
                _positionTimer.Start();
            }
            else
            {
                _positionTimer.Stop();
            }
        };
        StateChanged += (_, _) =>
        {
            if (IsVisible && WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
                Reposition();
            }
        };
        Closed += (_, _) =>
        {
            _positionTimer.Stop();
            _windowSource?.RemoveHook(WindowProc);
            _windowSource = null;
        };
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? HideAssistantRequested;

    public void UpdateSuggestion(SuggestionResult suggestion, DisplayStyle style, double preferredWidth)
    {
        _preferredWidth = Math.Clamp(preferredWidth, 200, 1400);
        var displayText = string.IsNullOrWhiteSpace(suggestion.DisplayText)
            ? "当前没有可靠建议"
            : suggestion.DisplayText;
        SuggestionText.Text = suggestion.HasCandidates
            ? $"{suggestion.ActiveCandidateIndex + 1}/{suggestion.Candidates.Count}  {displayText}"
            : displayText;
        SuggestionText.FontSize = style.FontSize;
        SuggestionText.LineHeight = style.LineHeight;
        SuggestionText.MaxHeight = style.LineHeight * 2 + 1;
        SuggestionText.Foreground = ParseBrush(style.FontColor, Colors.White);
        SuggestionBorder.Background = ParseBrush(
            style.BackgroundColor,
            Color.FromRgb(32, 32, 32),
            style.BackgroundOpacity);
        if (IsVisible) Reposition();
    }

    public void Reposition()
    {
        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == 0 || !NativeMethods.GetWindowRect(taskbar, out var taskbarRect) || taskbarRect.Width <= taskbarRect.Height)
        {
            RepositionFallback();
            return;
        }

        var dpi = NativeMethods.GetDpiForWindow(taskbar);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        var tray = NativeMethods.FindWindowEx(taskbar, 0, "TrayNotifyWnd", null);
        var rightPixels = taskbarRect.Right - (int)(190 * scale);
        if (tray != 0 && NativeMethods.GetWindowRect(tray, out var trayRect) && trayRect.Left > taskbarRect.Left)
        {
            rightPixels = trayRect.Left;
        }

        var paddingPixels = (int)(6 * scale);
        var minimumLeftPixels = taskbarRect.Left + (int)(180 * scale);
        var rebar = NativeMethods.FindWindowEx(taskbar, 0, "ReBarWindow32", null);
        var taskSwitch = rebar == 0 ? 0 : NativeMethods.FindWindowEx(rebar, 0, "MSTaskSwWClass", null);
        var taskList = taskSwitch == 0 ? 0 : NativeMethods.FindWindowEx(taskSwitch, 0, "MSTaskListWClass", null);
        if (taskList != 0 && NativeMethods.GetWindowRect(taskList, out var taskListRect))
        {
            minimumLeftPixels = Math.Max(minimumLeftPixels, taskListRect.Right + paddingPixels);
        }

        var availableWidthPixels = rightPixels - minimumLeftPixels - paddingPixels;
        int actualWidthPixels;
        int leftPixels;
        if (availableWidthPixels >= (int)(200 * scale))
        {
            actualWidthPixels = Math.Min((int)(_preferredWidth * scale), availableWidthPixels);
            leftPixels = rightPixels - actualWidthPixels - paddingPixels;
        }
        else
        {
            actualWidthPixels = (int)(200 * scale);
            leftPixels = rightPixels - actualWidthPixels - paddingPixels;
        }

        SetGeometry(
            leftPixels / scale,
            (taskbarRect.Top + (int)(2 * scale)) / scale,
            actualWidthPixels / scale,
            Math.Max(30, (taskbarRect.Height - (int)(4 * scale)) / scale));
    }

    private void RepositionFallback()
    {
        var workArea = SystemParameters.WorkArea;
        var width = Math.Min(_preferredWidth, Math.Max(200, workArea.Width - 12));
        SetGeometry(workArea.Right - width - 6, workArea.Bottom - 46, width, 42);
    }

    private void SetGeometry(double left, double top, double width, double height)
    {
        if (double.IsNaN(Left) || Math.Abs(Left - left) > 0.5) Left = left;
        if (double.IsNaN(Top) || Math.Abs(Top - top) > 0.5) Top = top;
        if (Math.Abs(Width - width) > 0.5) Width = width;
        if (Math.Abs(Height - height) > 0.5) Height = height;
        KeepAboveWindowsWithoutActivation();
    }

    private void ConfigureToolWindow()
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        if (_windowHandle == 0)
        {
            return;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle).ToInt64();
        extendedStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        extendedStyle &= ~NativeMethods.WsExAppWindow;
        _ = NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle, new nint(extendedStyle));

        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowProc);
        KeepAboveWindowsWithoutActivation();
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmMouseActivate)
        {
            handled = true;
            return NativeMethods.MaNoActivate;
        }

        return 0;
    }

    private void KeepAboveWindowsWithoutActivation()
    {
        if (_windowHandle == 0 || !IsVisible)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            _windowHandle,
            NativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpShowWindow);
    }

    private static SolidColorBrush ParseBrush(string value, Color fallback, double opacity = 1)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Opacity = Math.Clamp(opacity, 0.05, 1);
            return brush;
        }
        catch
        {
            var brush = new SolidColorBrush(fallback);
            brush.Opacity = Math.Clamp(opacity, 0.05, 1);
            return brush;
        }
    }

    private void OpenSettingsMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void HideMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        HideAssistantRequested?.Invoke(this, EventArgs.Empty);
}
