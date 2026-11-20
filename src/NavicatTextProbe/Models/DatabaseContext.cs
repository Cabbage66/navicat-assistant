namespace NavicatTextProbe.Models;

internal sealed record DatabaseQueryPlan(
    bool IsUsable,
    string Sql,
    string Reason,
    string Error)
{
    public static DatabaseQueryPlan Failure(string error) => new(false, string.Empty, string.Empty, error);
}

internal sealed record DatabaseCatalogSnapshot
{
    public string Summary { get; init; } = string.Empty;
    public int SchemaCount { get; init; }
    public int TableCount { get; init; }
    public int ColumnCount { get; init; }
    public bool WasTruncated { get; init; }
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record DatabaseToolRequest(string Sql, string Purpose);

internal sealed record DatabaseAssistedCompletionResult(
    SuggestionResult Suggestion,
    int ToolCallCount,
    int SuccessfulQueryCount,
    string Message);

internal sealed record DatabaseTestResult(
    bool IsSuccess,
    string Message,
    string ServerVersion,
    string Database,
    bool TlsActive);

internal sealed record DatabaseContextResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public string SchemaSummary { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public string QueryReason { get; init; } = string.Empty;
    public string RowsSummary { get; init; } = string.Empty;
    public int RowCount { get; init; }
    public bool WasTruncated { get; init; }

    public static DatabaseContextResult Failure(string message, string schemaSummary = "") => new()
    {
        Message = message,
        SchemaSummary = schemaSummary
    };
}
