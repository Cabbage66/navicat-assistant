using System.Globalization;
using System.Runtime.InteropServices;
using NavicatTextProbe.Models;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal sealed record GuidedTypingSession(
    long RootWindowHandle,
    string ProcessName,
    string ContextId,
    string InsertText,
    string BaseDocument,
    int CaretOffset);

internal sealed record GuidedTypingStatus(
    bool IsActive,
    bool IsComplete,
    string Message,
    int ConsumedElements,
    int TotalElements);

internal sealed class GuidedTypingService : IDisposable
{
    private const int VkEscape = 0x1B;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkProcessKey = 0xE5;
    private const uint LlkhfInjected = 0x00000010;
    private static readonly nuint InjectionMarker = unchecked((nuint)0x41535354);
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly HashSet<uint> _pressedKeys = [];
    private readonly HashSet<uint> _suppressedKeys = [];
    private nint _hookHandle;
    private GuidedTypingSession? _session;
    private IReadOnlyList<string> _elements = [];
    private int _nextElement;
    private DateTimeOffset _lastInjectionAt = DateTimeOffset.MinValue;

    public GuidedTypingService()
    {
        _callback = HookCallback;
    }

    public event EventHandler<GuidedTypingStatus>? StatusChanged;

    public bool IsActive => _session is not null;

    public bool ValidateObservation(long rootWindowHandle, string text, int caretOffset)
    {
        if (_session is null) return true;
        if (_session.RootWindowHandle != rootWindowHandle)
        {
            Stop("目标窗口已变化，受控逐字代打已自动停止。");
            return false;
        }

        var progress = SuggestionAcceptanceMatcher.Match(
            new SuggestionAcceptanceState(
                _session.BaseDocument,
                _session.CaretOffset,
                _session.InsertText),
            new ProbeResult
            {
                Status = ProbeStatus.Captured,
                Text = text,
                CaretOffset = caretOffset
            });
        var observationIsCatchingUp = progress.IsFollowing &&
                                      progress.ConsumedSuggestedCharacters < ConsumedCharacterCount() &&
                                      DateTimeOffset.UtcNow - _lastInjectionAt < TimeSpan.FromMilliseconds(750);
        if ((!progress.IsFollowing || progress.ConsumedSuggestedCharacters < ConsumedCharacterCount()) &&
            !observationIsCatchingUp)
        {
            Stop("文档或光标已偏离当前答案，受控逐字代打已自动停止。");
            return false;
        }
        return true;
    }

    public GuidedTypingStatus Start(GuidedTypingSession session)
    {
        Stop();
        var normalized = session.InsertText.ReplaceLineEndings("\n");
        _elements = SplitTextElements(normalized);
        if (_elements.Count == 0)
        {
            return new GuidedTypingStatus(false, false, "当前建议没有可代打的 InsertText。", 0, 0);
        }

        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _callback, 0, 0);
        if (_hookHandle == 0)
        {
            return new GuidedTypingStatus(false, false, "无法安装受控代打键盘 Hook。", 0, _elements.Count);
        }

        _session = session with { InsertText = normalized };
        _nextElement = 0;
        _pressedKeys.Clear();
        _suppressedKeys.Clear();
        var status = CurrentStatus("受控逐字代打已开启；Esc 可立即退出。");
        StatusChanged?.Invoke(this, status);
        return status;
    }

    public void Stop(string message = "受控逐字代打已关闭。")
    {
        var wasActive = _session is not null;
        _session = null;
        _pressedKeys.Clear();
        _suppressedKeys.Clear();
        if (_hookHandle != 0)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = 0;
        }

        if (wasActive)
        {
            StatusChanged?.Invoke(this, new GuidedTypingStatus(
                false,
                false,
                message,
                _nextElement,
                _elements.Count));
        }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0 || _session is null)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        if ((data.Flags & LlkhfInjected) != 0 || data.ExtraInfo == InjectionMarker)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        if (message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp)
        {
            _pressedKeys.Remove(data.VirtualKey);
            return _suppressedKeys.Remove(data.VirtualKey)
                ? 1
                : NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        if (message is not NativeMethods.WmKeyDown and not NativeMethods.WmSysKeyDown)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        if (data.VirtualKey == VkEscape)
        {
            var status = EndSession(false, "已按 Esc 退出受控逐字代打。");
            StatusChanged?.Invoke(this, status);
            return 1;
        }

        if (!IsEligible(data.VirtualKey) || HasCommandModifier() || IsImeComposing())
        {
            return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        if (!_pressedKeys.Add(data.VirtualKey))
        {
            return 1;
        }

        if (!IsExpectedForeground(_session.RootWindowHandle))
        {
            var status = EndSession(false, "前台窗口已变化，受控逐字代打已自动停止。");
            StatusChanged?.Invoke(this, status);
            return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        var element = _elements[_nextElement];
        if (!TryInject(element))
        {
            var status = EndSession(false, "无法安全写入当前编辑器，代打已停止，本次按键已放行。");
            StatusChanged?.Invoke(this, status);
            return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        _suppressedKeys.Add(data.VirtualKey);
        _nextElement++;
        _lastInjectionAt = DateTimeOffset.UtcNow;
        if (_nextElement >= _elements.Count)
        {
            var status = EndSession(true, "当前答案已逐字输入完成，代打已自动关闭。");
            StatusChanged?.Invoke(this, status);
        }
        else
        {
            StatusChanged?.Invoke(this, CurrentStatus("正在受控逐字代打。"));
        }
        return 1;
    }

    private GuidedTypingStatus EndSession(bool complete, string message)
    {
        _session = null;
        return new GuidedTypingStatus(false, complete, message, _nextElement, _elements.Count);
    }

    private GuidedTypingStatus CurrentStatus(string message) => new(
        _session is not null,
        false,
        message,
        _nextElement,
        _elements.Count);

    private int ConsumedCharacterCount()
    {
        var count = 0;
        for (var index = 0; index < _nextElement && index < _elements.Count; index++)
        {
            count += _elements[index].Length;
        }
        return count;
    }

    private static bool IsExpectedForeground(long expectedRootHandle)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero) return false;
        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GaRoot);
        if (root == nint.Zero) root = foreground;
        return root.ToInt64() == expectedRootHandle;
    }

    private static bool HasCommandModifier() =>
        IsPressed(VkControl) || IsPressed(VkMenu) || IsPressed(VkLWin) || IsPressed(VkRWin);

    private static bool IsPressed(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsEligible(uint virtualKey)
    {
        if (virtualKey is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or 0x90 or 0x91 or VkProcessKey)
        {
            return false;
        }
        if (virtualKey is >= 0x21 and <= 0x28) return false;
        if (virtualKey is >= 0x70 and <= 0x87) return false;
        return true;
    }

    private static bool IsImeComposing()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero) return false;

        var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var guiInfo = new NativeMethods.GuiThreadInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };
        var focus = threadId != 0 && NativeMethods.GetGUIThreadInfo(threadId, ref guiInfo)
            ? guiInfo.FocusWindow
            : foreground;
        if (focus == nint.Zero) focus = foreground;

        var inputContext = NativeMethods.ImmGetContext(focus);
        if (inputContext == nint.Zero) return false;
        try
        {
            return NativeMethods.ImmGetCompositionString(
                inputContext,
                NativeMethods.GcsCompStr,
                nint.Zero,
                0) > 0;
        }
        finally
        {
            _ = NativeMethods.ImmReleaseContext(focus, inputContext);
        }
    }

    private static bool TryInject(string textElement)
    {
        if (textElement == "\n") return SendVirtualKey(0x0D);
        if (textElement == "\t") return SendVirtualKey(0x09);

        var inputs = new List<NativeMethods.NativeInput>(textElement.Length * 2);
        foreach (var character in textElement)
        {
            inputs.Add(CreateUnicodeInput(character, keyUp: false));
            inputs.Add(CreateUnicodeInput(character, keyUp: true));
        }
        var array = inputs.ToArray();
        return NativeMethods.SendInput((uint)array.Length, array, Marshal.SizeOf<NativeMethods.NativeInput>()) == (uint)array.Length;
    }

    private static bool SendVirtualKey(ushort virtualKey)
    {
        var inputs = new[]
        {
            CreateVirtualKeyInput(virtualKey, keyUp: false),
            CreateVirtualKeyInput(virtualKey, keyUp: true)
        };
        return NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.NativeInput>()) == 2;
    }

    private static NativeMethods.NativeInput CreateUnicodeInput(char character, bool keyUp) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.NativeInputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                ScanCode = character,
                Flags = NativeMethods.KeyeventfUnicode | (keyUp ? NativeMethods.KeyeventfKeyup : 0),
                ExtraInfo = InjectionMarker
            }
        }
    };

    private static NativeMethods.NativeInput CreateVirtualKeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.NativeInputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? NativeMethods.KeyeventfKeyup : 0,
                ExtraInfo = InjectionMarker
            }
        }
    };

    internal static IReadOnlyList<string> SplitTextElements(string value)
    {
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            result.Add(enumerator.GetTextElement());
        }
        return result;
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KbdLlHookStruct
    {
        public uint VirtualKey { get; init; }
        public uint ScanCode { get; init; }
        public uint Flags { get; init; }
        public uint Time { get; init; }
        public nuint ExtraInfo { get; init; }
    }
}
