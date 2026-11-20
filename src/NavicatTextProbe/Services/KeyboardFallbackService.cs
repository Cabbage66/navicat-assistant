using System.Runtime.InteropServices;
using System.Text;
using NavicatTextProbe.Native;

namespace NavicatTextProbe.Services;

internal sealed class KeyboardFallbackService : IDisposable
{
    private const int VkBack = 0x08;
    private const int VkTab = 0x09;
    private const int VkReturn = 0x0D;
    private const int VkControl = 0x11;
    private const int VkV = 0x56;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private nint _hookHandle;

    public KeyboardFallbackService()
    {
        _callback = HookCallback;
    }

    public bool CaptureEnabled { get; set; }

    public event EventHandler<KeyboardFallbackInput>? InputCaptured;

    public bool Start()
    {
        if (_hookHandle != 0)
        {
            return true;
        }

        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _callback,
            0,
            0);
        return _hookHandle != 0;
    }

    public void Stop()
    {
        CaptureEnabled = false;
        if (_hookHandle != 0)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = 0;
        }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && CaptureEnabled && NavicatSelectionService.IsNavicatForeground() &&
            (wParam.ToInt32() == NativeMethods.WmKeyDown || wParam.ToInt32() == NativeMethods.WmSysKeyDown))
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var virtualKey = (int)data.VirtualKey;
            KeyboardFallbackInput? input = virtualKey switch
            {
                VkBack => new KeyboardFallbackInput(KeyboardFallbackInputKind.Backspace, string.Empty),
                VkTab => new KeyboardFallbackInput(KeyboardFallbackInputKind.Text, "\t"),
                VkReturn => new KeyboardFallbackInput(KeyboardFallbackInputKind.Text, "\n"),
                VkV when IsPressed(VkControl) => new KeyboardFallbackInput(KeyboardFallbackInputKind.Paste, string.Empty),
                _ when !IsPressed(VkControl) => TranslateKey(data),
                _ => null
            };

            if (input is not null)
            {
                InputCaptured?.Invoke(this, input);
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private static KeyboardFallbackInput? TranslateKey(KbdLlHookStruct data)
    {
        var state = new byte[256];
        if (!NativeMethods.GetKeyboardState(state))
        {
            return null;
        }

        var buffer = new StringBuilder(8);
        var count = NativeMethods.ToUnicode(
            data.VirtualKey,
            data.ScanCode == 0 ? NativeMethods.MapVirtualKey(data.VirtualKey, 0) : data.ScanCode,
            state,
            buffer,
            buffer.Capacity,
            0);
        return count > 0
            ? new KeyboardFallbackInput(KeyboardFallbackInputKind.Text, buffer.ToString(0, count))
            : null;
    }

    private static bool IsPressed(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

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

internal enum KeyboardFallbackInputKind
{
    Text,
    Backspace,
    Paste
}

internal sealed record KeyboardFallbackInput(KeyboardFallbackInputKind Kind, string Text);
