using System.Globalization;
using System.Windows.Input;
using System.Windows.Interop;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal enum HotkeyAction
{
    ToggleAssistant,
    CycleCandidates,
    AnalyzeSelection
}

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private readonly Dictionary<int, HotkeyAction> _registrations = [];
    private HwndSource? _source;
    private nint _windowHandle;

    public event EventHandler<HotkeyAction>? Pressed;

    public IReadOnlyList<string> Register(nint windowHandle, IReadOnlyDictionary<HotkeyAction, string> shortcuts)
    {
        DisposeRegistrations();
        _windowHandle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WindowProc);

        var errors = new List<string>();
        var id = 7300;
        foreach (var pair in shortcuts)
        {
            if (!TryParse(pair.Value, out var modifiers, out var virtualKey))
            {
                errors.Add($"快捷键格式无效：{pair.Value}");
                continue;
            }

            if (!NativeMethods.RegisterHotKey(windowHandle, id, modifiers | ModNoRepeat, virtualKey))
            {
                errors.Add($"快捷键已被其他程序占用：{pair.Value}");
                continue;
            }

            _registrations[id] = pair.Key;
            id++;
        }

        return errors;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && _registrations.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(this, action);
        }

        return 0;
    }

    private static bool TryParse(string shortcut, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return false;
        }

        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        foreach (var modifier in parts[..^1])
        {
            switch (modifier.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        var keyText = parts[^1].Trim();
        if (keyText.Length == 1 && char.IsLetterOrDigit(keyText[0]))
        {
            virtualKey = char.ToUpper(keyText[0], CultureInfo.InvariantCulture);
            return modifiers != 0;
        }

        if (Enum.TryParse<Key>(keyText, true, out var key) && key != Key.None)
        {
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            return modifiers != 0 && virtualKey != 0;
        }

        return false;
    }

    private void DisposeRegistrations()
    {
        if (_source is not null)
        {
            _source.RemoveHook(WindowProc);
            _source = null;
        }

        foreach (var id in _registrations.Keys)
        {
            _ = NativeMethods.UnregisterHotKey(_windowHandle, id);
        }

        _registrations.Clear();
    }

    public void Dispose() => DisposeRegistrations();
}
