namespace NavicatTextProbe.Models;

public sealed record DisplayStyle
{
    public double FontSize { get; set; } = 16;
    public double LineHeight { get; set; } = 24;
    public string FontColor { get; set; } = "#F8F9F9";
    public string BackgroundColor { get; set; } = "#17202A";
    public double BackgroundOpacity { get; set; } = 0.82;
}

public sealed record AppSettings
{
    public int SettingsVersion { get; set; }
    public bool AssistantEnabled { get; set; } = true;
    public bool AiEnabled { get; set; } = true;
    public string ApiBaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-flash";
    public bool VisionEnabled { get; set; } = true;
    public string VisionApiBaseUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4";
    public string VisionModel { get; set; } = "glm-4.6v";
    public string CustomGlobalPrompt { get; set; } = string.Empty;
    public int PredictionDebounceMilliseconds { get; set; } = 850;
    public int PredictionMinimumIntervalMilliseconds { get; set; } = 1400;
    public bool TaskbarPanelEnabled { get; set; } = true;
    public double TaskbarWidth { get; set; } = 640;
    public bool MobileServerEnabled { get; set; }
    public bool KeyboardFallbackEnabled { get; set; }
    public int MobilePort { get; set; } = 17832;
    public string MobileAccessToken { get; set; } = string.Empty;
    public string ToggleAssistantHotkey { get; set; } = "Ctrl+Alt+S";
    public string CycleCandidatesHotkey { get; set; } = "Ctrl+Alt+R";
    public string AnalyzeSelectionHotkey { get; set; } = "Ctrl+Alt+E";
    public string WordExerciseHotkey { get; set; } = "Ctrl+Alt+A";
    public string VisionScreenshotHotkey { get; set; } = "Ctrl+Shift+C";
    public DisplayStyle TaskbarStyle { get; set; } = new()
    {
        FontSize = 11,
        LineHeight = 15,
        BackgroundColor = "#202020",
        BackgroundOpacity = 0.96
    };
    public DisplayStyle MobileStyle { get; set; } = new()
    {
        FontSize = 18,
        LineHeight = 28,
        FontColor = "#7DF2B1",
        BackgroundColor = "#151D27",
        BackgroundOpacity = 0.96
    };
}
