namespace NavicatTextProbe.Models;

internal enum WordExerciseRequestMode
{
    LocateAndAnswer,
    ContinueCurrentQuestion
}

internal sealed record WordExerciseTextBlock
{
    public string Id { get; init; } = string.Empty;
    public int Start { get; init; }
    public int ContentEnd { get; init; }
    public int End { get; init; }
    public string Text { get; init; } = string.Empty;
    public bool ContainsCaret { get; init; }
}

internal sealed record WordExerciseRequestContext
{
    public WordExerciseRequestMode Mode { get; init; }
    public string DocumentIdentity { get; init; } = string.Empty;
    public string DocumentName { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public int DocumentLength { get; init; }
    public int CaretOffset { get; init; }
    public int WindowStart { get; init; }
    public int WindowEnd { get; init; }
    public int Radius { get; init; }
    public int ExpansionLevel { get; init; }
    public string BlocksWithCursor { get; init; } = string.Empty;
    public string SelectedText { get; init; } = string.Empty;
    public string BaselineChange { get; init; } = string.Empty;
    public string PreviousSuggestion { get; init; } = string.Empty;
    public IReadOnlyList<WordExerciseTextBlock> Blocks { get; init; } = [];
    public WordExerciseQuestionState? CurrentQuestion { get; init; }

    public int ContextCharacterCount => WindowEnd - WindowStart;
}

internal sealed record WordExerciseQuestionState
{
    public string DocumentIdentity { get; init; } = string.Empty;
    public string QuestionNumber { get; init; } = string.Empty;
    public string ActiveSubquestionNumber { get; init; } = string.Empty;
    public string QuestionType { get; init; } = "unknown";
    public string QuestionText { get; init; } = string.Empty;
    public string QuestionIdentity { get; init; } = string.Empty;
    public string QuestionStartAnchor { get; init; } = string.Empty;
    public string QuestionEndAnchor { get; init; } = string.Empty;
    public int LastKnownStart { get; init; }
    public int LastKnownEnd { get; init; }
    public string BaselineDocumentHash { get; init; } = string.Empty;
    public string BaselineDocumentText { get; init; } = string.Empty;

    public string DisplayLabel => string.IsNullOrWhiteSpace(QuestionNumber) ||
                                  QuestionNumber.Equals("当前题", StringComparison.Ordinal)
        ? "当前题"
        : $"第 {QuestionNumber} 题";
}
