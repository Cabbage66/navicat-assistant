namespace NavicatTextProbe.Models;

internal enum DocumentContextStatus
{
    NotTargetApplication,
    Captured,
    Unavailable
}

internal sealed record DocumentContextResult
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public DocumentContextStatus Status { get; init; }
    public string ApplicationName { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public long WindowHandle { get; init; }
    public string WindowTitle { get; init; } = string.Empty;
    public string DocumentName { get; init; } = string.Empty;
    public string DocumentIdentity { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public int? CaretOffset { get; init; }
    public string ContextBefore { get; init; } = string.Empty;
    public string ContextAfter { get; init; } = string.Empty;
    public bool CaretAvailable { get; init; }
    public PixelRect? CaretScreenBounds { get; init; }
    public string CaretScreenSource { get; init; } = string.Empty;
    public string SelectedText { get; init; } = string.Empty;
    public bool SelectionAvailable { get; init; }
    public bool HasSelection { get; init; }
    public bool IsPartial { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Diagnostics { get; init; }

    public bool IsTargetApplication => Status != DocumentContextStatus.NotTargetApplication;

    public static DocumentContextResult NotTarget() => new()
    {
        Status = DocumentContextStatus.NotTargetApplication
    };
}
