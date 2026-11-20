using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed record SuggestionAcceptanceState(string BaseDocument, int CaretOffset, string SuggestedText);

internal sealed record SuggestionAcceptanceProgress(
    bool IsFollowing,
    int ConsumedSuggestedCharacters,
    string RemainingText)
{
    public bool IsComplete => IsFollowing && RemainingText.Length == 0;

    public static SuggestionAcceptanceProgress NotFollowing { get; } = new(false, 0, string.Empty);
}

internal static class SuggestionAcceptanceMatcher
{
    public static SuggestionAcceptanceProgress Match(
        SuggestionAcceptanceState? state,
        ProbeResult probe)
    {
        if (state is null || probe.Text.Length < state.BaseDocument.Length)
        {
            return SuggestionAcceptanceProgress.NotFollowing;
        }

        var insertedLength = probe.Text.Length - state.BaseDocument.Length;
        var expectedCaret = state.CaretOffset + insertedLength;
        if (probe.CaretOffset is int actualCaret && actualCaret != expectedCaret)
        {
            return SuggestionAcceptanceProgress.NotFollowing;
        }

        if (!probe.Text.AsSpan(0, state.CaretOffset).SequenceEqual(state.BaseDocument.AsSpan(0, state.CaretOffset)) ||
            !probe.Text.AsSpan(state.CaretOffset + insertedLength).SequenceEqual(state.BaseDocument.AsSpan(state.CaretOffset)))
        {
            return SuggestionAcceptanceProgress.NotFollowing;
        }

        var typedText = probe.Text.Substring(state.CaretOffset, insertedLength);
        var normalizedTyped = NormalizeLineEndings(typedText);
        var normalizedSuggestion = NormalizeLineEndings(state.SuggestedText);
        if (!normalizedSuggestion.StartsWith(normalizedTyped, StringComparison.OrdinalIgnoreCase))
        {
            return SuggestionAcceptanceProgress.NotFollowing;
        }

        var consumedCharacters = RawOffsetForNormalizedLength(
            state.SuggestedText,
            normalizedTyped.Length);
        return new SuggestionAcceptanceProgress(
            true,
            consumedCharacters,
            state.SuggestedText[consumedCharacters..]);
    }

    public static bool IsFollowing(SuggestionAcceptanceState? state, ProbeResult probe) =>
        Match(state, probe).IsFollowing;

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static int RawOffsetForNormalizedLength(string text, int normalizedLength)
    {
        var rawOffset = 0;
        var normalizedOffset = 0;
        while (rawOffset < text.Length && normalizedOffset < normalizedLength)
        {
            if (text[rawOffset] == '\r' &&
                rawOffset + 1 < text.Length &&
                text[rawOffset + 1] == '\n')
            {
                rawOffset += 2;
            }
            else
            {
                rawOffset++;
            }
            normalizedOffset++;
        }
        return rawOffset;
    }
}
