namespace NavicatTextProbe.Models;

internal sealed record AssistantRuntimeStatus
{
    public string MainLevel { get; init; } = "standby";
    public string MainText { get; init; } = "等待";
    public string DatabaseLevel { get; init; } = "off";
    public string DatabaseText { get; init; } = "数据库未使用";
    public string GuidedTypingLevel { get; init; } = "off";
    public string GuidedTypingText { get; init; } = "代打关闭";
}
