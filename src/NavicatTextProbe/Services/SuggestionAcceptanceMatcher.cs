using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed record SuggestionAcceptanceState(string BaseDocument, int CaretOffset, string SuggestedText);

internal static class SuggestionAcceptanceMatcher
{
    public static bool IsFollowing(SuggestionAcceptanceState? state, ProbeResult probe)
    {
        if (state is null || probe.Text.Length < state.BaseDocument.Length)
        {
            return false;
        }

        var insertedLength = probe.Text.Length - state.BaseDocument.Length;
        var expectedCaret = state.CaretOffset + insertedLength;
        if (probe.CaretOffset is int actualCaret && actualCaret != expectedCaret)
        {
            return false;
        }

        if (!probe.Text.AsSpan(0, state.CaretOffset).SequenceEqual(state.BaseDocument.AsSpan(0, state.CaretOffset)) ||
            !probe.Text.AsSpan(state.CaretOffset + insertedLength).SequenceEqual(state.BaseDocument.AsSpan(state.CaretOffset)))
        {
            return false;
        }

        var typedText = probe.Text.Substring(state.CaretOffset, insertedLength);
        var normalizedTyped = NormalizeLineEndings(typedText);
        var normalizedSuggestion = NormalizeLineEndings(state.SuggestedText);
        return normalizedSuggestion.StartsWith(normalizedTyped, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
