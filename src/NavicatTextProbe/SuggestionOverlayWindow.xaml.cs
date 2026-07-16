using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NavicatTextProbe.Models;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace NavicatTextProbe;

public partial class SuggestionOverlayWindow : Window
{
    public SuggestionOverlayWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? HideRequested;

    public void UpdateSuggestion(SuggestionResult suggestion, DisplayStyle style)
    {
        SuggestionText.Text = string.IsNullOrWhiteSpace(suggestion.DisplayText)
            ? "当前上下文没有足够可靠的建议"
            : suggestion.DisplayText;
        ModeText.Text = suggestion.HasCandidates
            ? $"候选 {suggestion.ActiveCandidateIndex + 1}/{suggestion.Candidates.Count}"
            : suggestion.Mode switch
        {
            SuggestionMode.Diagnosis => "修正",
            SuggestionMode.Error => "错误",
            SuggestionMode.Information => "状态",
            _ => "补全"
        };
        ModeText.Foreground = new SolidColorBrush(suggestion.Mode switch
        {
            SuggestionMode.Diagnosis => Color.FromRgb(245, 176, 65),
            SuggestionMode.Error => Color.FromRgb(236, 112, 99),
            _ => Color.FromRgb(88, 214, 141)
        });

        SuggestionText.FontSize = style.FontSize;
        SuggestionText.LineHeight = style.LineHeight;
        SuggestionText.Foreground = ParseBrush(style.FontColor, Colors.White);
        SuggestionBorder.Background = ParseBrush(
            style.BackgroundColor,
            Color.FromRgb(23, 32, 42),
            style.BackgroundOpacity);
    }

    private static SolidColorBrush ParseBrush(string value, System.Windows.Media.Color fallback, double opacity = 1)
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

    private void SuggestionBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OpenSettingsMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void HideMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        HideRequested?.Invoke(this, EventArgs.Empty);
}
