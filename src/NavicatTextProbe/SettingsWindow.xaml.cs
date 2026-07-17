using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using NavicatTextProbe.Models;

namespace NavicatTextProbe;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _workingCopy;

    public SettingsWindow(AppSettings settings, bool hasSavedApiKey, bool hasSavedVisionApiKey)
    {
        InitializeComponent();
        Icon = Imaging.CreateBitmapSourceFromHIcon(
            System.Drawing.SystemIcons.Application.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        _workingCopy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
        ApiKeyStatusText.Text = hasSavedApiKey
            ? "Windows 凭据管理器中已经保存了一个 API Key。"
            : "尚未保存 API Key。请先撤销聊天中暴露的旧 Key，再在这里填写新 Key。";
        VisionApiKeyStatusText.Text = hasSavedVisionApiKey
            ? "Windows 凭据管理器中已经保存了智谱 API Key。"
            : "尚未保存智谱 API Key。明文暴露过的 Key 必须先在智谱控制台吊销并重建。";
        PopulateControls();
    }

    public AppSettings? UpdatedSettings { get; private set; }

    public string NewApiKey => ApiKeyPasswordBox.Password.Trim();

    public bool ClearApiKey => ClearApiKeyCheckBox.IsChecked == true;

    public string NewVisionApiKey => VisionApiKeyPasswordBox.Password.Trim();

    public bool ClearVisionApiKey => ClearVisionApiKeyCheckBox.IsChecked == true;

    private void PopulateControls()
    {
        AiEnabledCheckBox.IsChecked = _workingCopy.AiEnabled;
        ApiBaseUrlTextBox.Text = _workingCopy.ApiBaseUrl;
        ModelTextBox.Text = _workingCopy.Model;
        VisionEnabledCheckBox.IsChecked = _workingCopy.VisionEnabled;
        VisionApiBaseUrlTextBox.Text = _workingCopy.VisionApiBaseUrl;
        VisionModelTextBox.Text = _workingCopy.VisionModel;
        GlobalPromptTextBox.Text = _workingCopy.CustomGlobalPrompt;
        DebounceTextBox.Text = _workingCopy.PredictionDebounceMilliseconds.ToString();
        MinimumIntervalTextBox.Text = _workingCopy.PredictionMinimumIntervalMilliseconds.ToString();

        AssistantEnabledCheckBox.IsChecked = _workingCopy.AssistantEnabled;
        TaskbarEnabledCheckBox.IsChecked = _workingCopy.TaskbarPanelEnabled;
        TaskbarWidthTextBox.Text = _workingCopy.TaskbarWidth.ToString("0.##");
        TaskbarFontSizeTextBox.Text = _workingCopy.TaskbarStyle.FontSize.ToString("0.##");
        TaskbarLineHeightTextBox.Text = _workingCopy.TaskbarStyle.LineHeight.ToString("0.##");
        TaskbarOpacityTextBox.Text = _workingCopy.TaskbarStyle.BackgroundOpacity.ToString("0.##");
        TaskbarBackgroundTextBox.Text = _workingCopy.TaskbarStyle.BackgroundColor;
        TaskbarForegroundTextBox.Text = _workingCopy.TaskbarStyle.FontColor;

        MobileEnabledCheckBox.IsChecked = _workingCopy.MobileServerEnabled;
        MobilePortTextBox.Text = _workingCopy.MobilePort.ToString();
        MobileTokenTextBox.Text = _workingCopy.MobileAccessToken;
        MobileFontSizeTextBox.Text = _workingCopy.MobileStyle.FontSize.ToString("0.##");
        MobileLineHeightTextBox.Text = _workingCopy.MobileStyle.LineHeight.ToString("0.##");
        MobileOpacityTextBox.Text = _workingCopy.MobileStyle.BackgroundOpacity.ToString("0.##");
        MobileBackgroundTextBox.Text = _workingCopy.MobileStyle.BackgroundColor;
        MobileForegroundTextBox.Text = _workingCopy.MobileStyle.FontColor;
        KeyboardFallbackCheckBox.IsChecked = _workingCopy.KeyboardFallbackEnabled;
        AssistantHotkeyTextBox.Text = _workingCopy.ToggleAssistantHotkey;
        AnalyzeHotkeyTextBox.Text = _workingCopy.AnalyzeSelectionHotkey;
        CandidateHotkeyTextBox.Text = _workingCopy.CycleCandidatesHotkey;
        WordExerciseHotkeyTextBox.Text = _workingCopy.WordExerciseHotkey;
        VisionScreenshotHotkeyTextBox.Text = _workingCopy.VisionScreenshotHotkey;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DebounceTextBox.Text, out var debounce) ||
            !int.TryParse(MinimumIntervalTextBox.Text, out var minimumInterval) ||
            !int.TryParse(MobilePortTextBox.Text, out var port) ||
            !double.TryParse(TaskbarWidthTextBox.Text, out var taskbarWidth) ||
            !double.TryParse(TaskbarFontSizeTextBox.Text, out var taskbarFontSize) ||
            !double.TryParse(TaskbarLineHeightTextBox.Text, out var taskbarLineHeight) ||
            !double.TryParse(TaskbarOpacityTextBox.Text, out var taskbarOpacity) ||
            !double.TryParse(MobileFontSizeTextBox.Text, out var mobileFontSize) ||
            !double.TryParse(MobileLineHeightTextBox.Text, out var mobileLineHeight) ||
            !double.TryParse(MobileOpacityTextBox.Text, out var mobileOpacity))
        {
            System.Windows.MessageBox.Show(this, "宽度、字号、行高、透明度、端口和等待时间必须填写有效数字。", "设置无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _workingCopy.AiEnabled = AiEnabledCheckBox.IsChecked == true;
        _workingCopy.ApiBaseUrl = ApiBaseUrlTextBox.Text.Trim();
        _workingCopy.Model = ModelTextBox.Text.Trim();
        _workingCopy.VisionEnabled = VisionEnabledCheckBox.IsChecked == true;
        _workingCopy.VisionApiBaseUrl = VisionApiBaseUrlTextBox.Text.Trim();
        _workingCopy.VisionModel = VisionModelTextBox.Text.Trim();
        _workingCopy.CustomGlobalPrompt = GlobalPromptTextBox.Text;
        _workingCopy.PredictionDebounceMilliseconds = debounce;
        _workingCopy.PredictionMinimumIntervalMilliseconds = minimumInterval;
        _workingCopy.AssistantEnabled = AssistantEnabledCheckBox.IsChecked == true;
        _workingCopy.TaskbarPanelEnabled = TaskbarEnabledCheckBox.IsChecked == true;
        _workingCopy.TaskbarWidth = taskbarWidth;
        _workingCopy.TaskbarStyle.FontSize = taskbarFontSize;
        _workingCopy.TaskbarStyle.LineHeight = taskbarLineHeight;
        _workingCopy.TaskbarStyle.BackgroundOpacity = taskbarOpacity;
        _workingCopy.TaskbarStyle.BackgroundColor = TaskbarBackgroundTextBox.Text.Trim();
        _workingCopy.TaskbarStyle.FontColor = TaskbarForegroundTextBox.Text.Trim();
        _workingCopy.MobileServerEnabled = MobileEnabledCheckBox.IsChecked == true;
        _workingCopy.MobilePort = port;
        _workingCopy.MobileAccessToken = MobileTokenTextBox.Text.Trim();
        _workingCopy.MobileStyle.FontSize = mobileFontSize;
        _workingCopy.MobileStyle.LineHeight = mobileLineHeight;
        _workingCopy.MobileStyle.BackgroundOpacity = mobileOpacity;
        _workingCopy.MobileStyle.BackgroundColor = MobileBackgroundTextBox.Text.Trim();
        _workingCopy.MobileStyle.FontColor = MobileForegroundTextBox.Text.Trim();
        _workingCopy.KeyboardFallbackEnabled = KeyboardFallbackCheckBox.IsChecked == true;
        _workingCopy.ToggleAssistantHotkey = AssistantHotkeyTextBox.Text.Trim();
        _workingCopy.AnalyzeSelectionHotkey = AnalyzeHotkeyTextBox.Text.Trim();
        _workingCopy.CycleCandidatesHotkey = CandidateHotkeyTextBox.Text.Trim();
        _workingCopy.WordExerciseHotkey = WordExerciseHotkeyTextBox.Text.Trim();
        _workingCopy.VisionScreenshotHotkey = VisionScreenshotHotkeyTextBox.Text.Trim();

        UpdatedSettings = _workingCopy;
        DialogResult = true;
    }
}
