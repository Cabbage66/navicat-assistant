namespace NavicatTextProbe.Models;

internal readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}

internal enum VisionCaptureStatus
{
    Captured,
    NoForegroundWindow,
    AssistantForeground,
    CaretUnavailable,
    CaptureFailed
}

internal sealed record VisionCaptureSnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public VisionCaptureStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public long WindowHandle { get; init; }
    public PixelRect WindowBounds { get; init; }
    public PixelRect CaretScreenBounds { get; init; }
    public string CaretSource { get; init; } = string.Empty;
    public double CaretConfidence { get; init; }
    public PixelRect DetailBounds { get; init; }
    public int DetailMarkerX { get; init; }
    public int DetailMarkerY { get; init; }
    public byte[] FullImageBytes { get; init; } = [];
    public string FullImageMediaType { get; init; } = "image/jpeg";
    public byte[] DetailImageBytes { get; init; } = [];
    public string DetailImageMediaType { get; init; } = "image/png";
    public string CaptureHash { get; init; } = string.Empty;

    public bool IsCaptured => Status == VisionCaptureStatus.Captured;

    public bool HasReliableCaret => CaretConfidence >= 0.5 && !CaretScreenBounds.IsEmpty;

    public static VisionCaptureSnapshot Failure(
        VisionCaptureStatus status,
        string message,
        int processId = 0,
        string? processName = null,
        long windowHandle = 0) => new()
    {
        Status = status,
        Message = message,
        ProcessId = processId,
        ProcessName = processName ?? string.Empty,
        WindowHandle = windowHandle
    };
}
