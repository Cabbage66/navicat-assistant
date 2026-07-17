using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed class DeepSeekClient : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<SuggestionResult> CompleteAsync(
        AppSettings settings,
        string apiKey,
        string userPrompt,
        CancellationToken cancellationToken,
        int maxTokens = 500,
        double temperature = 0.2,
        string? systemPrompt = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return SuggestionResult.Error("尚未配置 DeepSeek API Key");
        }

        var endpoint = settings.ApiBaseUrl.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt ?? PromptTemplates.BuildSystemPrompt(settings) },
                new { role = "user", content = userPrompt }
            },
            thinking = new { type = "disabled" },
            temperature = Math.Clamp(temperature, 0, 1),
            max_tokens = Math.Clamp(maxTokens, 100, 4000),
            response_format = new { type = "json_object" },
            stream = false
        });

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return SuggestionResult.Error(
                    $"DeepSeek 请求失败：{(int)response.StatusCode}",
                    CompactError(responseBody));
            }

            using var envelope = JsonDocument.Parse(responseBody);
            var content = envelope.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                return SuggestionResult.Error("DeepSeek 返回了空内容");
            }

            var payload = JsonSerializer.Deserialize<SuggestionPayload>(content, _jsonOptions);
            if (payload is null)
            {
                return SuggestionResult.Error("无法解析 DeepSeek 的 JSON 结果");
            }

            var mode = payload.Mode?.Trim().ToLowerInvariant() switch
            {
                "diagnosis" => SuggestionMode.Diagnosis,
                "exercise_answer" => SuggestionMode.ExerciseAnswer,
                "exercise_continuation" => SuggestionMode.ExerciseContinuation,
                "not_exercise" => SuggestionMode.Information,
                _ => SuggestionMode.Completion
            };
            var issues = (payload.Issues ?? [])
                .OrderBy(IssuePriority)
                .ToArray();
            var displayText = mode == SuggestionMode.Diagnosis && issues.Length > 0
                ? CompactDisplay(issues[0])
                : CompactDisplay(payload.DisplayText);
            if (string.IsNullOrWhiteSpace(displayText) &&
                mode is SuggestionMode.ExerciseAnswer or SuggestionMode.ExerciseContinuation)
            {
                displayText = CompactDisplay(string.IsNullOrWhiteSpace(payload.FullAnswer)
                    ? payload.InsertText
                    : payload.FullAnswer);
            }
            var candidates = (payload.Candidates ?? [])
                .Where(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.InsertText) ||
                    !string.IsNullOrWhiteSpace(candidate.DisplayText))
                .OrderBy(candidate => candidate.Rank <= 0 ? int.MaxValue : candidate.Rank)
                .ThenByDescending(candidate => candidate.Confidence)
                .Select((candidate, index) => new SuggestionCandidate
                {
                    Rank = index + 1,
                    DisplayText = CompactDisplay(candidate.DisplayText),
                    InsertText = candidate.InsertText?.TrimEnd() ?? string.Empty,
                    Explanation = candidate.Explanation?.Trim() ?? string.Empty,
                    Confidence = Math.Clamp(candidate.Confidence, 0, 1)
                })
                .GroupBy(candidate => NormalizeSuggestion(candidate.InsertText.Length > 0
                    ? candidate.InsertText
                    : candidate.DisplayText), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(3)
                .Select((candidate, index) => candidate with { Rank = index + 1 })
                .ToArray();

            if (candidates.Length > 0)
            {
                var first = candidates[0];
                displayText = first.DisplayText;
            }

            var usage = envelope.RootElement.TryGetProperty("usage", out var usageElement)
                ? usageElement
                : default;
            var result = new SuggestionResult
            {
                Mode = mode,
                DisplayText = displayText,
                InsertText = candidates.Length > 0 ? candidates[0].InsertText : payload.InsertText?.TrimEnd() ?? string.Empty,
                Explanation = candidates.Length > 0 ? candidates[0].Explanation : payload.Explanation?.Trim() ?? string.Empty,
                QuestionType = mode is SuggestionMode.ExerciseAnswer or SuggestionMode.ExerciseContinuation
                    ? NormalizeQuestionType(payload.QuestionType)
                    : string.Empty,
                IsQuestion = payload.IsQuestion ??
                    mode is SuggestionMode.ExerciseAnswer or SuggestionMode.ExerciseContinuation,
                ContextComplete = payload.ContextComplete ?? true,
                NeedsMoreContext = NormalizeNeedsMoreContext(payload.NeedsMoreContext),
                SameAsCurrentQuestion = payload.SameAsCurrentQuestion ?? true,
                QuestionNumber = payload.QuestionNumber?.Trim() ?? string.Empty,
                ActiveSubquestionNumber = payload.ActiveSubquestionNumber?.Trim() ?? string.Empty,
                QuestionStartBlockId = payload.QuestionStartBlockId?.Trim() ?? string.Empty,
                QuestionEndBlockId = payload.QuestionEndBlockId?.Trim() ?? string.Empty,
                QuestionStartAnchor = payload.QuestionStartAnchor?.Trim() ?? string.Empty,
                QuestionEndAnchor = payload.QuestionEndAnchor?.Trim() ?? string.Empty,
                QuestionText = payload.QuestionText?.Trim() ?? string.Empty,
                AnswerState = NormalizeAnswerState(payload.AnswerState),
                DetectedUserAnswer = payload.DetectedUserAnswer?.Trim() ?? string.Empty,
                FullAnswer = payload.FullAnswer?.Trim() ?? string.Empty,
                Issues = issues,
                Confidence = candidates.Length > 0 ? candidates[0].Confidence : Math.Clamp(payload.Confidence, 0, 1),
                Candidates = candidates,
                PromptTokens = ReadUsageInt(usage, "prompt_tokens"),
                PromptCacheHitTokens = ReadUsageInt(usage, "prompt_cache_hit_tokens"),
                PromptCacheMissTokens = ReadUsageInt(usage, "prompt_cache_miss_tokens"),
                CompletionTokens = ReadUsageInt(usage, "completion_tokens")
            };
            return candidates.Length > 0 ? result.SelectCandidate(0) : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SuggestionResult.Error("连接 DeepSeek 失败", ex.Message);
        }
    }

    private static string CompactDisplay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var singleLine = string.Join(' ', text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return singleLine.Length <= 100 ? singleLine : singleLine[..97] + "…";
    }

    private static string CompactError(string responseBody)
    {
        var text = responseBody.ReplaceLineEndings(" ").Trim();
        return text.Length <= 500 ? text : text[..500] + "…";
    }

    private static string NormalizeSuggestion(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string NormalizeQuestionType(string? questionType) => questionType?.Trim().ToLowerInvariant() switch
    {
        "choice" => "choice",
        "fill_blank" or "fillblank" => "fill_blank",
        "short_answer" or "shortanswer" => "short_answer",
        _ => "unknown"
    };

    private static string NormalizeNeedsMoreContext(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "before" => "before",
        "after" => "after",
        "both" => "both",
        _ => "none"
    };

    private static string NormalizeAnswerState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "empty" => "empty",
        "partial" => "partial",
        "complete" => "complete",
        _ => "uncertain"
    };

    private static int ReadUsageInt(JsonElement usage, string propertyName) =>
        usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty(propertyName, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : 0;

    private static int IssuePriority(string issue)
    {
        if (issue.Contains("P0", StringComparison.OrdinalIgnoreCase)) return 0;
        if (issue.Contains("P1", StringComparison.OrdinalIgnoreCase)) return 1;
        if (issue.Contains("P2", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record SuggestionPayload
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("display_text")]
        public string? DisplayText { get; init; }

        [JsonPropertyName("insert_text")]
        public string? InsertText { get; init; }

        [JsonPropertyName("explanation")]
        public string? Explanation { get; init; }

        [JsonPropertyName("question_type")]
        public string? QuestionType { get; init; }

        [JsonPropertyName("is_question")]
        public bool? IsQuestion { get; init; }

        [JsonPropertyName("context_complete")]
        public bool? ContextComplete { get; init; }

        [JsonPropertyName("needs_more_context")]
        public string? NeedsMoreContext { get; init; }

        [JsonPropertyName("same_as_current_question")]
        public bool? SameAsCurrentQuestion { get; init; }

        [JsonPropertyName("question_number")]
        public string? QuestionNumber { get; init; }

        [JsonPropertyName("active_subquestion_number")]
        public string? ActiveSubquestionNumber { get; init; }

        [JsonPropertyName("question_start_block_id")]
        public string? QuestionStartBlockId { get; init; }

        [JsonPropertyName("question_end_block_id")]
        public string? QuestionEndBlockId { get; init; }

        [JsonPropertyName("question_start_anchor")]
        public string? QuestionStartAnchor { get; init; }

        [JsonPropertyName("question_end_anchor")]
        public string? QuestionEndAnchor { get; init; }

        [JsonPropertyName("question_text")]
        public string? QuestionText { get; init; }

        [JsonPropertyName("answer_state")]
        public string? AnswerState { get; init; }

        [JsonPropertyName("detected_user_answer")]
        public string? DetectedUserAnswer { get; init; }

        [JsonPropertyName("full_answer")]
        public string? FullAnswer { get; init; }

        [JsonPropertyName("issues")]
        public IReadOnlyList<string>? Issues { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }

        [JsonPropertyName("candidates")]
        public IReadOnlyList<CandidatePayload>? Candidates { get; init; }
    }

    private sealed record CandidatePayload
    {
        [JsonPropertyName("rank")]
        public int Rank { get; init; }

        [JsonPropertyName("display_text")]
        public string? DisplayText { get; init; }

        [JsonPropertyName("insert_text")]
        public string? InsertText { get; init; }

        [JsonPropertyName("explanation")]
        public string? Explanation { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }
    }
}
