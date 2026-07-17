using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed class SettingsService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsService(string assistantRoot)
    {
        ConfigDirectory = Path.Combine(assistantRoot, "config");
        SettingsPath = Path.Combine(ConfigDirectory, "settings.json");
        Directory.CreateDirectory(ConfigDirectory);
        Current = Load();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, _jsonOptions));
    }

    public string ConfigDirectory { get; }

    public string SettingsPath { get; }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        EnsureDefaults(settings);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, _jsonOptions));
        File.Move(temporaryPath, SettingsPath, true);
        Current = settings;
        Changed?.Invoke(this, Current);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    EnsureDefaults(settings, ReadLegacyOverlayEnabled(json));
                    return settings;
                }
            }
        }
        catch
        {
            // A malformed file should not prevent the assistant from starting.
        }

        var defaults = new AppSettings();
        EnsureDefaults(defaults, legacyOverlayEnabled: false);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(defaults, _jsonOptions));
        return defaults;
    }

    private static void EnsureDefaults(AppSettings settings, bool legacyOverlayEnabled = false)
    {
        if (settings.SettingsVersion < 2)
        {
            if (settings.PredictionDebounceMilliseconds == 650)
            {
                settings.PredictionDebounceMilliseconds = 850;
            }

            if (settings.PredictionMinimumIntervalMilliseconds <= 0)
            {
                settings.PredictionMinimumIntervalMilliseconds = 1400;
            }

            if (Math.Abs(settings.TaskbarStyle.FontSize - 14) < 0.01 &&
                Math.Abs(settings.TaskbarStyle.LineHeight - 21) < 0.01 &&
                string.Equals(settings.TaskbarStyle.BackgroundColor, "#17202A", StringComparison.OrdinalIgnoreCase))
            {
                settings.TaskbarStyle.FontSize = 11;
                settings.TaskbarStyle.LineHeight = 15;
                settings.TaskbarStyle.BackgroundColor = "#202020";
                settings.TaskbarStyle.BackgroundOpacity = 0.96;
            }

            settings.SettingsVersion = 2;
        }

        if (settings.SettingsVersion < 3)
        {
            // Version 3 removes the floating overlay. Preserve an existing visible output by
            // moving users who only enabled that overlay onto the taskbar panel.
            if (legacyOverlayEnabled)
            {
                settings.TaskbarPanelEnabled = true;
            }

            settings.SettingsVersion = 3;
        }

        if (settings.SettingsVersion < 4)
        {
            settings.WordExerciseHotkey = string.IsNullOrWhiteSpace(settings.WordExerciseHotkey)
                ? "Ctrl+Alt+A"
                : settings.WordExerciseHotkey;
            settings.SettingsVersion = 4;
        }

        settings.ApiBaseUrl = string.IsNullOrWhiteSpace(settings.ApiBaseUrl)
            ? "https://api.deepseek.com"
            : settings.ApiBaseUrl.TrimEnd('/');
        settings.Model = string.IsNullOrWhiteSpace(settings.Model)
            ? "deepseek-v4-flash"
            : settings.Model.Trim();
        settings.WordExerciseHotkey = string.IsNullOrWhiteSpace(settings.WordExerciseHotkey)
            ? "Ctrl+Alt+A"
            : settings.WordExerciseHotkey.Trim();
        settings.MobilePort = Math.Clamp(settings.MobilePort, 1024, 65535);
        settings.PredictionDebounceMilliseconds = Math.Clamp(settings.PredictionDebounceMilliseconds, 150, 5000);
        settings.PredictionMinimumIntervalMilliseconds = Math.Clamp(settings.PredictionMinimumIntervalMilliseconds, 500, 5000);
        settings.TaskbarWidth = Math.Clamp(settings.TaskbarWidth, 200, 1400);
        ClampStyle(settings.TaskbarStyle, 10, 36, 12, 54);
        ClampStyle(settings.MobileStyle, 10, 48, 12, 72);
        if (string.IsNullOrWhiteSpace(settings.MobileAccessToken) || settings.MobileAccessToken.Length < 20)
        {
            settings.MobileAccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        }
    }

    private static bool ReadLegacyOverlayEnabled(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("OverlayEnabled", out var value) &&
                   value.ValueKind is JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static void ClampStyle(DisplayStyle style, double minFont, double maxFont, double minLine, double maxLine)
    {
        style.FontSize = Math.Clamp(style.FontSize, minFont, maxFont);
        style.LineHeight = Math.Clamp(style.LineHeight, Math.Max(minLine, style.FontSize), maxLine);
        style.BackgroundOpacity = Math.Clamp(style.BackgroundOpacity, 0.05, 1);
        style.BackgroundColor = string.IsNullOrWhiteSpace(style.BackgroundColor) ? "#17202A" : style.BackgroundColor.Trim();
        style.FontColor = string.IsNullOrWhiteSpace(style.FontColor) ? "#F8F9F9" : style.FontColor.Trim();
    }
}
