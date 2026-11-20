namespace NavicatTextProbe.Models;

public enum ProbeStatus
{
    Captured,
    NavicatNotForeground,
    NoReadableText,
    Failed
}

public sealed record ElementDiagnostic(
    int Depth,
    string ControlType,
    string ClassName,
    string AutomationId,
    string FrameworkId,
    string Name,
    IReadOnlyList<string> Patterns);

public sealed record ProbeResult
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public ProbeStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public long WindowHandle { get; init; }
    public string WindowTitle { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string ElementDescription { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public int? CaretLine { get; init; }
    public int? CaretColumn { get; init; }
    public int? CaretOffset { get; init; }
    public int? CaretPrefixLength { get; init; }
    public int? SelectionLength { get; init; }
    public bool IsTruncated { get; init; }
    public string? Diagnostics { get; init; }
    public IReadOnlyList<ElementDiagnostic> Elements { get; init; } = [];

    public static ProbeResult Failed(string message, string diagnostics) => new()
    {
        Status = ProbeStatus.Failed,
        Message = message,
        Diagnostics = diagnostics
    };
}
