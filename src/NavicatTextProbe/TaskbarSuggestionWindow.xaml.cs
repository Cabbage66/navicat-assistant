using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace NavicatTextProbe;

public partial class TaskbarSuggestionWindow : Window
{
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public TaskbarSuggestionWindow()
    {
        InitializeComponent();
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
        Closed += (_, _) => _positionTimer.Stop();
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? HideRequested;

    public void UpdateSuggestion(SuggestionResult suggestion, DisplayStyle style)
    {
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
            actualWidthPixels = Math.Min((int)(340 * scale), availableWidthPixels);
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
        SetGeometry(workArea.Right - 352, workArea.Bottom - 46, 340, 42);
    }

    private void SetGeometry(double left, double top, double width, double height)
    {
        if (double.IsNaN(Left) || Math.Abs(Left - left) > 0.5) Left = left;
        if (double.IsNaN(Top) || Math.Abs(Top - top) > 0.5) Top = top;
        if (Math.Abs(Width - width) > 0.5) Width = width;
        if (Math.Abs(Height - height) > 0.5) Height = height;
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
        HideRequested?.Invoke(this, EventArgs.Empty);
}
