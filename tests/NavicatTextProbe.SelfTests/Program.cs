using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;
using NavicatTextProbe;
using NavicatTextProbe.Native;
using NavicatTextProbe.Models;
using NavicatTextProbe.Services;

var failures = new List<string>();

void Check(string name, bool condition)
{
    if (!condition) failures.Add(name);
}

var baseDocument = "SELECT * FR";
var state = new SuggestionAcceptanceState(baseDocument, baseDocument.Length, "OM users;");
ProbeResult Probe(string text, int caret) => new() { Text = text, CaretOffset = caret };

Check("document preview compacts line breaks and Word control markers",
    DocumentContextFormatter.Compact("  第一段\r\n\t第二段\a  ") == "第一段 第二段");
var documentPreview = DocumentContextFormatter.BuildTaskbarText(new DocumentContextResult
{
    Status = DocumentContextStatus.Captured,
    ApplicationName = "Microsoft Word",
    ContextBefore = "光标前面的文字",
    ContextAfter = "光标后面的文字",
    CaretAvailable = true,
    SelectedText = "被选中的文字",
    SelectionAvailable = true,
    HasSelection = true
});
Check("document preview contains the three requested labeled values",
    documentPreview.Contains("1.当前软件：Microsoft Word", StringComparison.Ordinal) &&
    documentPreview.Contains("2.光标内容：光标前面的文字▏光标后面的文字", StringComparison.Ordinal) &&
    documentPreview.Contains("3.选中内容：被选中的文字", StringComparison.Ordinal));
Check("document preview reports an unavailable browser selection honestly",
    DocumentContextFormatter.BuildTaskbarText(new DocumentContextResult
    {
        Status = DocumentContextStatus.Captured,
        ApplicationName = "钉钉云文档（Chrome）"
    }).Contains("（未识别到选区）", StringComparison.Ordinal));
Check("DingTalk browser matching requires both a browser and a document title",
    DocumentContextProbeService.IsDingTalkWebWindow("chrome", "测试 · 钉钉文档 - Google Chrome") &&
    !DocumentContextProbeService.IsDingTalkWebWindow("chrome", "Gmail - Google Chrome") &&
    !DocumentContextProbeService.IsDingTalkWebWindow("notepad", "测试 · 钉钉文档"));

DocumentContextResult WordDocumentAt(string text, int caret, string identity = "word-exercise-self-test") => new()
{
    Status = DocumentContextStatus.Captured,
    ApplicationName = "Microsoft Word",
    ProcessName = "WINWORD",
    DocumentName = "SQL练习.docx",
    DocumentIdentity = identity,
    Text = text,
    ContentHash = ContentHasher.Sha256(text),
    CaretOffset = caret,
    CaretAvailable = true,
    SelectionAvailable = true
};

var multiPartExerciseDocument = string.Join("\r",
[
    "简答 3. 请说明第三范式。",
    "答：非主属性不能传递依赖于主键。",
    "简答 4.（10 分）",
    "给定查询：",
    "SELECT * FROM orders",
    "WHERE user_id = 100 AND status = 1",
    "ORDER BY created_at DESC;",
    "(1) 为该查询设计一个联合索引，写出列顺序。（4 分）INDEX(user_id, status, created_at)",
    "(2) 说明你这样排序的理由。（3 分）",
    "(3) 另举两种会导致该索引失效的写法。（3 分）",
    "参考答案与评分要点",
    "简答 4：INDEX(user_id, status, created_at)"
]).Replace("\r(2)", "\v(2)", StringComparison.Ordinal)
  .Replace("\r(3)", "\v(3)", StringComparison.Ordinal);
var multiPartCaret = multiPartExerciseDocument.IndexOf("(2) 说明", StringComparison.Ordinal) + 4;
var multiPartDocument = WordDocumentAt(multiPartExerciseDocument, multiPartCaret);
Check("late Word results are valid only for the same document hash and caret",
    WordExerciseContextBuilder.IsSameObservation(multiPartDocument, multiPartDocument with { }) &&
    !WordExerciseContextBuilder.IsSameObservation(
        multiPartDocument,
        multiPartDocument with { CaretOffset = multiPartCaret + 1 }) &&
    !WordExerciseContextBuilder.IsSameObservation(
        multiPartDocument,
        multiPartDocument with { ContentHash = "new-content" }));
var locateContext = WordExerciseContextBuilder.Build(
    multiPartDocument,
    WordExerciseRequestMode.LocateAndAnswer,
    WordExerciseContextBuilder.FinalExpandedRadius,
    expansionLevel: 2)!;

Check("Word AI locator context carries exactly one real cursor marker",
    locateContext.BlocksWithCursor.Split("<CURSOR>", StringSplitOptions.None).Length == 2);
Check("Word soft line breaks become separately addressable source blocks",
    locateContext.Blocks.Any(block => block.Text.StartsWith("(2)", StringComparison.Ordinal)) &&
    locateContext.Blocks.Any(block => block.Text.StartsWith("(3)", StringComparison.Ordinal)));
Check("initial Word context radius is defined as 300 characters per side",
    WordExerciseContextBuilder.InitialRadius == 300 &&
    WordExerciseContextBuilder.FirstExpandedRadius == 1_000 &&
    WordExerciseContextBuilder.FinalExpandedRadius == 3_000);

var literalCursorText = "第1题 文档原文中写着 <CURSOR>\r答：";
var literalCursorContext = WordExerciseContextBuilder.Build(
    WordDocumentAt(literalCursorText, literalCursorText.Length),
    WordExerciseRequestMode.LocateAndAnswer,
    WordExerciseContextBuilder.InitialRadius,
    expansionLevel: 0)!;
Check("a literal cursor token in Word text cannot create a second locator marker",
    literalCursorContext.BlocksWithCursor.Split("<CURSOR>", StringSplitOptions.None).Length == 2 &&
    literalCursorContext.BlocksWithCursor.Contains("文档中的 CURSOR 字样", StringComparison.Ordinal));

var questionStartBlock = locateContext.Blocks.Single(block =>
    block.Text.Contains("简答 4.（10 分）", StringComparison.Ordinal));
var questionEndBlock = locateContext.Blocks.Single(block =>
    block.Text.Contains("(3) 另举两种", StringComparison.Ordinal));
var locatedPayload = new SuggestionResult
{
    Mode = SuggestionMode.ExerciseAnswer,
    IsQuestion = true,
    ContextComplete = true,
    NeedsMoreContext = "none",
    SameAsCurrentQuestion = true,
    QuestionNumber = "第4题",
    ActiveSubquestionNumber = "(2)",
    QuestionType = "short_answer",
    QuestionStartBlockId = questionStartBlock.Id,
    QuestionEndBlockId = questionEndBlock.Id,
    QuestionStartAnchor = "简答 4.（10 分）",
    QuestionEndAnchor = "(3) 另举两种会导致该索引失效的写法。（3 分）",
    QuestionText = "这是模型改写过的文本，不能作为原文真值。",
    AnswerState = "partial",
    DetectedUserAnswer = "INDEX(user_id, status, created_at)",
    DisplayText = "(1) INDEX(user_id,status,created_at)；(2) 等值列在前",
    InsertText = "等值查询列在前，排序列放在最后。",
    FullAnswer = "(1) INDEX(user_id,status,created_at)；(2) 等值列在前；(3) 对索引列做函数、隐式类型转换。",
    Explanation = "联合索引先匹配等值条件，再利用 created_at 的有序性。",
    Confidence = 0.96
};
Check("validated AI blocks reconstruct one complete top-level question",
    WordExerciseContextBuilder.TryCreateQuestionState(
        multiPartDocument,
        locateContext,
        locatedPayload,
        multiPartExerciseDocument,
        out var locatedQuestion,
        out _));
Check("a multi-part answer keeps the parent number and current child separately",
    locatedQuestion is not null &&
    locatedQuestion.QuestionNumber == "4" &&
    locatedQuestion.ActiveSubquestionNumber == "2" &&
    locatedQuestion.QuestionType == "short_answer");
Check("canonical question text comes from Word and owns the shared SQL plus every child",
    locatedQuestion is not null &&
    locatedQuestion.QuestionText.StartsWith("简答 4.", StringComparison.Ordinal) &&
    locatedQuestion.QuestionText.Contains("SELECT * FROM orders", StringComparison.Ordinal) &&
    locatedQuestion.QuestionText.Contains("(1) 为该查询", StringComparison.Ordinal) &&
    locatedQuestion.QuestionText.Contains("(2) 说明", StringComparison.Ordinal) &&
    locatedQuestion.QuestionText.Contains("(3) 另举两种", StringComparison.Ordinal) &&
    !locatedQuestion.QuestionText.Contains("模型改写过的文本", StringComparison.Ordinal) &&
    !locatedQuestion.QuestionText.Contains("参考答案与评分要点", StringComparison.Ordinal));

Check("invalid model block IDs are rejected",
    !WordExerciseContextBuilder.TryCreateQuestionState(
        multiPartDocument,
        locateContext,
        locatedPayload with { QuestionEndBlockId = "B999" },
        multiPartExerciseDocument,
        out _,
        out _));
Check("anchors that are not at the returned Word boundaries are rejected",
    !WordExerciseContextBuilder.TryCreateQuestionState(
        multiPartDocument,
        locateContext,
        locatedPayload with { QuestionStartAnchor = "ORDER BY created_at DESC;" },
        multiPartExerciseDocument,
        out _,
        out _));
Check("low-confidence question locations are rejected",
    !WordExerciseContextBuilder.TryCreateQuestionState(
        multiPartDocument,
        locateContext,
        locatedPayload with { Confidence = 0.2 },
        multiPartExerciseDocument,
        out _,
        out _));
Check("a completion payload cannot masquerade as a Word locator result",
    !WordExerciseContextBuilder.TryCreateQuestionState(
        multiPartDocument,
        locateContext,
        locatedPayload with { Mode = SuggestionMode.Completion },
        multiPartExerciseDocument,
        out _,
        out _));

var longExerciseDocument = new string('前', 420) + "\r简答 9. 请分析以下查询。\r" +
                           new string('题', 320) + "\r答：\r" + new string('后', 420);
var longExerciseCaret = longExerciseDocument.IndexOf("请分析", StringComparison.Ordinal) + 3;
var narrowContext = WordExerciseContextBuilder.Build(
    WordDocumentAt(longExerciseDocument, longExerciseCaret),
    WordExerciseRequestMode.LocateAndAnswer,
    radius: 80,
    expansionLevel: 0)!;
var edgePayload = locatedPayload with
{
    QuestionStartBlockId = narrowContext.Blocks[0].Id,
    QuestionEndBlockId = narrowContext.Blocks[^1].Id
};
Check("a model range touching a cropped window requests adaptive expansion",
    narrowContext.WindowStart > 0 &&
    narrowContext.WindowEnd < narrowContext.DocumentLength &&
    WordExerciseContextBuilder.NeedsExpandedContext(narrowContext, edgePayload));
Check("an explicit incomplete-context response requests adaptive expansion",
    WordExerciseContextBuilder.NeedsExpandedContext(
        narrowContext,
        edgePayload with { ContextComplete = false, NeedsMoreContext = "both" }));

var editedMultiPartDocument = multiPartExerciseDocument.Replace(
    "(2) 说明你这样排序的理由。（3 分）",
    "(2) 说明你这样排序的理由。（3 分）等值条件列在前，",
    StringComparison.Ordinal);
var editedMultiPartCaret = editedMultiPartDocument.IndexOf("等值条件列在前，", StringComparison.Ordinal) +
                           "等值条件列在前，".Length;
var continuationContext = WordExerciseContextBuilder.Build(
    WordDocumentAt(editedMultiPartDocument, editedMultiPartCaret),
    WordExerciseRequestMode.ContinueCurrentQuestion,
    WordExerciseContextBuilder.InitialRadius,
    expansionLevel: 0,
    locatedQuestion,
    "等值查询列在前，排序列放在最后。")!;
Check("continuation context sends the stored complete parent question",
    continuationContext.CurrentQuestion?.QuestionText.Contains("(3) 另举两种", StringComparison.Ordinal) == true);
Check("continuation context describes writing added since the hotkey baseline",
    continuationContext.BaselineChange.Contains("等值条件列在前", StringComparison.Ordinal));
Check("continuation context still has exactly one cursor in the current local window",
    continuationContext.BlocksWithCursor.Split("<CURSOR>", StringSplitOptions.None).Length == 2);

var locatePrompt = PromptTemplates.BuildWordExercisePrompt(locateContext);
var continuationPrompt = PromptTemplates.BuildWordExercisePrompt(continuationContext);
var exerciseSystemPrompt = PromptTemplates.BuildExerciseSystemPrompt(new AppSettings());
Check("Word locator prompt uses the one-call locate-and-answer protocol",
    locatePrompt.Contains("locate_and_answer", StringComparison.Ordinal) &&
    locatePrompt.Contains(questionStartBlock.Id, StringComparison.Ordinal) &&
    locatePrompt.Contains("本次 Word 原文窗口", StringComparison.Ordinal) &&
    locatePrompt.Split("<CURSOR>", StringSplitOptions.None).Length == 2);
Check("continuation prompt includes both the saved parent and baseline change",
    continuationPrompt.Contains("continue_current_question", StringComparison.Ordinal) &&
    continuationPrompt.Contains("完整顶层题目", StringComparison.Ordinal) &&
    continuationPrompt.Contains("等值条件列在前", StringComparison.Ordinal));
Check("exercise system prompt makes top-level multi-part ownership a hard rule",
    exerciseSystemPrompt.Contains("公共说明、表结构、业务背景、SQL、数据样例", StringComparison.Ordinal) &&
    exerciseSystemPrompt.Contains("question_number=\"4\"", StringComparison.Ordinal) &&
    exerciseSystemPrompt.Contains("反例", StringComparison.Ordinal) &&
    exerciseSystemPrompt.Contains("全部子问", StringComparison.Ordinal));
Check("parenthesized numbers are independent only when no shared parent stem exists",
    exerciseSystemPrompt.Contains("不存在共享上级题干", StringComparison.Ordinal));
Check("exercise prompt requires same-question verification before continuation",
    exerciseSystemPrompt.Contains("same_as_current_question=false", StringComparison.Ordinal));
Check("partial-answer prompt forbids echoing text the user already wrote",
    exerciseSystemPrompt.Contains("绝不能复述 detected_user_answer", StringComparison.Ordinal) &&
    exerciseSystemPrompt.Contains("光标后下一步应写什么", StringComparison.Ordinal));
Check("question-number normalization is owned by the program",
    WordExerciseContextBuilder.NormalizeQuestionNumber("第4题") == "4" &&
    WordExerciseContextBuilder.NormalizeQuestionNumber("**第4题。**") == "4" &&
    WordExerciseContextBuilder.NormalizeQuestionNumber("简答 4.") == "4" &&
    WordExerciseContextBuilder.NormalizeSubquestionNumber("（2）") == "2");

var deepSeekListener = new TcpListener(IPAddress.Loopback, 0);
deepSeekListener.Start();
var deepSeekPort = ((IPEndPoint)deepSeekListener.LocalEndpoint).Port;
var fakeDeepSeekServer = Task.Run(async () =>
{
    using var connection = await deepSeekListener.AcceptTcpClientAsync();
    await using var stream = connection.GetStream();
    using var requestBytes = new MemoryStream();
    var buffer = new byte[4096];
    var headerEnd = -1;
    var contentLength = 0;
    var isChunked = false;
    while (headerEnd < 0)
    {
        var count = await stream.ReadAsync(buffer);
        if (count == 0) break;
        requestBytes.Write(buffer, 0, count);
        var bytes = requestBytes.ToArray();
        headerEnd = Encoding.ASCII.GetString(bytes).IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd >= 0)
        {
            var headers = Encoding.ASCII.GetString(bytes, 0, headerEnd);
            var lengthHeader = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (lengthHeader is not null)
            {
                _ = int.TryParse(lengthHeader["Content-Length:".Length..].Trim(), out contentLength);
            }
            isChunked = headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase);
            headerEnd += 4;
        }
    }
    while (headerEnd >= 0 && requestBytes.Length - headerEnd < contentLength)
    {
        var count = await stream.ReadAsync(buffer);
        if (count == 0) break;
        requestBytes.Write(buffer, 0, count);
    }
    while (headerEnd >= 0 && isChunked &&
           requestBytes.ToArray().AsSpan(headerEnd).IndexOf("\r\n0\r\n\r\n"u8) < 0)
    {
        var count = await stream.ReadAsync(buffer);
        if (count == 0) break;
        requestBytes.Write(buffer, 0, count);
    }

    var inner = JsonSerializer.Serialize(new
    {
        mode = "exercise_answer",
        is_question = true,
        context_complete = true,
        needs_more_context = "none",
        same_as_current_question = true,
        question_number = "4",
        active_subquestion_number = "2",
        question_type = "short_answer",
        question_start_block_id = "B003",
        question_end_block_id = "B010",
        question_start_anchor = "简答 4.（10 分）",
        question_end_anchor = "(3) 另举两种会导致该索引失效的写法。（3 分）",
        question_text = "完整顶层题目原文",
        answer_state = "partial",
        detected_user_answer = "等值条件列在前",
        display_text = "(2) 等值条件列在前，排序列在后",
        insert_text = "排序列 created_at 放在最后。",
        full_answer = "(1) INDEX(user_id,status,created_at)；(2) 等值列在前；(3) 避免函数和隐式转换。",
        explanation = "覆盖三个小问的完整解析。",
        issues = Array.Empty<string>(),
        confidence = 0.96
    });
    var envelope = JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content = inner } } },
        usage = new
        {
            prompt_tokens = 321,
            prompt_cache_hit_tokens = 200,
            prompt_cache_miss_tokens = 121,
            completion_tokens = 88
        }
    });
    var responseBody = Encoding.UTF8.GetBytes(envelope);
    var responseHeader = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(responseHeader);
    await stream.WriteAsync(responseBody);
    await stream.FlushAsync();
});
SuggestionResult exerciseApiResult;
using (var fakeDeepSeekClient = new DeepSeekClient())
{
    exerciseApiResult = await fakeDeepSeekClient.CompleteAsync(
        new AppSettings
        {
            ApiBaseUrl = $"http://127.0.0.1:{deepSeekPort}",
            Model = "self-test"
        },
        "self-test-key",
        locatePrompt,
        CancellationToken.None,
        systemPrompt: exerciseSystemPrompt);
}
await fakeDeepSeekServer;
deepSeekListener.Stop();
var exerciseApiMapped =
    exerciseApiResult.Mode == SuggestionMode.ExerciseAnswer &&
    exerciseApiResult.IsQuestion &&
    exerciseApiResult.QuestionNumber == "4" &&
    exerciseApiResult.ActiveSubquestionNumber == "2" &&
    exerciseApiResult.QuestionType == "short_answer" &&
    exerciseApiResult.AnswerState == "partial" &&
    exerciseApiResult.FullAnswer.Contains("(3)", StringComparison.Ordinal) &&
    exerciseApiResult.PromptTokens == 321 &&
    exerciseApiResult.PromptCacheHitTokens == 200 &&
    exerciseApiResult.PromptCacheMissTokens == 121 &&
    exerciseApiResult.CompletionTokens == 88;
if (!exerciseApiMapped)
{
    Console.Error.WriteLine("Exercise API mapping diagnostic: " + JsonSerializer.Serialize(exerciseApiResult));
}
Check("DeepSeek exercise JSON maps locator, answer-state and token-usage fields", exerciseApiMapped);

var realPartialAnswer = new SuggestionResult
{
    Mode = SuggestionMode.ExerciseAnswer,
    QuestionType = "short_answer",
    AnswerState = "partial",
    DisplayText = "1NF:每一列的字段都不能由多个组成 2FN：",
    InsertText = "在满足 1NF 的基础上，非主属性必须完全依赖于整个候选键。",
    FullAnswer = "1NF 要求属性原子性；2NF 要求完全函数依赖；3NF 要求不存在传递依赖。"
};
var realPartialTaskbarAnswer = WordExercisePresentation.SelectTaskbarAnswer(
    realPartialAnswer,
    "short_answer",
    realPartialAnswer.FullAnswer,
    realPartialAnswer.InsertText,
    continuation: false);
Check("partial short answers show the unseen continuation instead of echoing existing writing",
    realPartialTaskbarAnswer == realPartialAnswer.InsertText &&
    !realPartialTaskbarAnswer.Contains("每一列的字段", StringComparison.Ordinal));
Check("choice questions retain the useful option text rather than reducing the answer to one letter",
    WordExercisePresentation.SelectTaskbarAnswer(
        new SuggestionResult
        {
            QuestionType = "choice",
            AnswerState = "empty",
            DisplayText = "B. DDL",
            InsertText = "B"
        },
        "choice",
        "B",
        "B",
        continuation: false) == "B. DDL");
Check("completed short answers without another scoring point receive an explicit status",
    WordExercisePresentation.SelectTaskbarAnswer(
        new SuggestionResult { QuestionType = "short_answer", AnswerState = "complete" },
        "short_answer",
        "完整参考答案",
        string.Empty,
        continuation: true) == "答案已基本完整");

var exerciseDockAnswer = WordExercisePresentation.BuildDisplayText(
    new string('答', 140),
    string.Empty,
    "short_answer",
    "第4题",
    continuation: true);
Check("long Word answers use program-owned type/number formatting and stay taskbar-safe",
    exerciseDockAnswer.StartsWith("简答题｜4.", StringComparison.Ordinal) &&
    exerciseDockAnswer.Length <= 100 &&
    exerciseDockAnswer.EndsWith('…'));
Check("a model-repeated top-level number is removed before program formatting",
    WordExercisePresentation.BuildDisplayText(
        "第4题：(2) 等值列在前",
        string.Empty,
        "short_answer",
        "4",
        continuation: false) == "简答题｜4.(2) 等值列在前");

var exerciseAcceptance = new SuggestionAcceptanceState(
    multiPartExerciseDocument,
    multiPartCaret,
    "等值查询列在前，排序列放在最后。");
var matchingExerciseInput = multiPartExerciseDocument.Insert(multiPartCaret, "等值查询列在前");
var exerciseProgress = SuggestionAcceptanceMatcher.Match(
        exerciseAcceptance,
        new ProbeResult
        {
            Text = matchingExerciseInput,
            CaretOffset = multiPartCaret + "等值查询列在前".Length
        });
Check("typing the offered Word continuation exposes the untyped suffix locally",
    exerciseProgress.IsFollowing &&
    exerciseProgress.RemainingText == "，排序列放在最后。");
var exerciseQuestionState = new WordExerciseQuestionState
{
    QuestionNumber = "3",
    QuestionType = "short_answer"
};
var advancedExerciseSuggestion = WordExercisePresentation.AdvanceFollowingSuggestion(
    realPartialAnswer,
    exerciseQuestionState,
    exerciseProgress);
Check("the Word taskbar advances locally to the untyped suffix",
    advancedExerciseSuggestion.InsertText == "，排序列放在最后。" &&
    advancedExerciseSuggestion.DisplayText == "简答题｜3.，排序列放在最后。" &&
    !advancedExerciseSuggestion.DisplayText.Contains("每一列的字段", StringComparison.Ordinal));
var completedExerciseSuggestion = WordExercisePresentation.AdvanceFollowingSuggestion(
    realPartialAnswer,
    exerciseQuestionState,
    new SuggestionAcceptanceProgress(true, realPartialAnswer.InsertText.Length, string.Empty));
Check("finishing a Word suggestion leaves a stable completion status while the next point is analyzed",
    completedExerciseSuggestion.InsertText.Length == 0 &&
    completedExerciseSuggestion.DisplayText == "简答题｜3.当前建议已写完");

Check("unchanged document remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument, baseDocument.Length)));
Check("matching prefix remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "OM", baseDocument.Length + 2)));
var sqlProgress = SuggestionAcceptanceMatcher.Match(state, Probe(baseDocument + "OM", baseDocument.Length + 2));
Check("matching SQL input reports consumed and remaining suggestion text",
    sqlProgress.ConsumedSuggestedCharacters == 2 && sqlProgress.RemainingText == " users;");
Check("case-insensitive SQL keyword prefix remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "om users", baseDocument.Length + 8)));
Check("complete suggestion remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "OM users;", baseDocument.Length + 9)));
Check("a completely typed suggestion is marked complete",
    SuggestionAcceptanceMatcher.Match(state, Probe(baseDocument + "OM users;", baseDocument.Length + 9)).IsComplete);
Check("divergent input requests a new suggestion", !SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "OX", baseDocument.Length + 2)));
Check("moving the caret requests a new suggestion", !SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument, 2)));

var middleBase = "SELECT  WHERE active = 1";
var middleState = new SuggestionAcceptanceState(middleBase, 7, "* FROM users");
var middleCurrent = middleBase.Insert(7, "* FROM");
Check("text after the caret is preserved", SuggestionAcceptanceMatcher.IsFollowing(middleState, Probe(middleCurrent, 13)));

var newlineState = new SuggestionAcceptanceState("-- intent", 9, "\nSELECT 1;");
Check("CRLF input matches LF suggestion", SuggestionAcceptanceMatcher.IsFollowing(newlineState, Probe("-- intent\r\nSELECT", 17)));
Check("line-ending normalization still returns the correct raw remaining suffix",
    SuggestionAcceptanceMatcher.Match(newlineState, Probe("-- intent\r\nSELECT", 17)).RemainingText == " 1;");

var commentProbe = new ProbeResult
{
    Text = "-- 查询所有活跃用户\nSELECT ",
    CaretOffset = "-- 查询所有活跃用户\nSELECT ".Length,
    CaretLine = 2,
    CaretColumn = 8
};
var completionPrompt = PromptTemplates.BuildCompletionPrompt(commentProbe);
Check("line comment is labeled as an AI intent hint", completionPrompt.Contains("-- 查询所有活跃用户", StringComparison.Ordinal));

var unfinishedBlock = new ProbeResult { Text = "/* 只查询今天的数据", CaretOffset = 11 };
Check("unfinished block comment is retained", PromptTemplates.BuildCompletionPrompt(unfinishedBlock).Contains("只查询今天的数据", StringComparison.Ordinal));

var diagnosisPrompt = PromptTemplates.BuildDiagnosisPrompt("SELECT", "SELECT");
Check("diagnosis asks for every issue", diagnosisPrompt.Contains("所有能够确认", StringComparison.Ordinal));
Check("diagnosis requests priority order", diagnosisPrompt.Contains("P0 → P1 → P2", StringComparison.Ordinal));
var longDiagnosisDocument = "DIAGNOSIS_DOCUMENT_START\n" + new string('d', 150_000) + "\nDIAGNOSIS_DOCUMENT_END";
var longDiagnosisPrompt = PromptTemplates.BuildDiagnosisPrompt("SELECT 1;", longDiagnosisDocument);
Check("diagnosis also receives a formerly truncated document in full",
    longDiagnosisPrompt.Contains("DIAGNOSIS_DOCUMENT_START", StringComparison.Ordinal) &&
    longDiagnosisPrompt.Contains("DIAGNOSIS_DOCUMENT_END", StringComparison.Ordinal));

var foreignKeyDocument = """
    CREATE TABLE yangzhuoran_exam_addresses(
      id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
      user_id BIGINT UNSIGNED NOT NULL,
      receiver_name VARCHAR(30) NOT NULL,
      receiver_mobile VARCHAR(20) NOT NULL,
      address_text VARCHAR(200) NOT NULL,
      is_default INT UNSIGNED DEFAULT 0,
      created_at DATETIME NOT NULL DEFAULT CURRENT_TIME,
      PRIMARY KEY (id),
      CONSTRAINT fk_user_id 
    """;
var foreignKeyProbe = new ProbeResult
{
    Text = foreignKeyDocument,
    CaretOffset = foreignKeyDocument.Length,
    CaretLine = 10,
    CaretColumn = 25,
    ContentHash = "foreign-key-test"
};
var foreignKeyContext = SqlContextAnalyzer.Analyze(foreignKeyProbe);
Check("fk constraint name enters foreign-key continuation state", foreignKeyContext.ExpectedKind == SqlExpectedContinuationKind.ForeignKeyConstraint);
Check("fk column is inferred from an already defined column", foreignKeyContext.ForeignKeyColumn == "user_id");
Check("defined is_default column is remembered", foreignKeyContext.DefinedColumns.Contains("is_default"));
var immediateForeignKey = SqlContextAnalyzer.BuildImmediateGuardedSuggestion(foreignKeyProbe);
Check("local guard immediately suggests FOREIGN KEY", immediateForeignKey?.InsertText.StartsWith("FOREIGN KEY (user_id) REFERENCES", StringComparison.Ordinal) == true);
Check("repeated is_default suggestion is rejected", !SqlContextAnalyzer.IsCompatible(foreignKeyProbe, new SuggestionResult
{
    Mode = SuggestionMode.Completion,
    InsertText = "is_default TINYINT NOT NULL DEFAULT 0,"
}));

var missingCreateTableParenthesisDocument = """
    CREATE TABLE yangzhuoran_exam_
      id BIGINT UNSIGNED AUTO_INCREMENT NOT NULL,
    )
    """;
var missingCreateTableParenthesisCaret = missingCreateTableParenthesisDocument.IndexOf('\n');
var missingCreateTableParenthesisProbe = new ProbeResult
{
    Text = missingCreateTableParenthesisDocument,
    CaretOffset = missingCreateTableParenthesisCaret,
    CaretLine = 1,
    CaretColumn = 31
};
var missingCreateTableParenthesisContext = SqlContextAnalyzer.Analyze(missingCreateTableParenthesisProbe);
var immediateCreateTableParenthesis = SqlContextAnalyzer.BuildImmediateGuardedSuggestion(missingCreateTableParenthesisProbe);
Check("caret after CREATE TABLE name detects the missing opening parenthesis",
    missingCreateTableParenthesisContext.ExpectedKind == SqlExpectedContinuationKind.CreateTableOpeningParenthesis);
Check("missing CREATE TABLE parenthesis is shown clearly but inserts only SQL",
    immediateCreateTableParenthesis?.DisplayText.Contains("缺少英文左括号", StringComparison.Ordinal) == true &&
    immediateCreateTableParenthesis.InsertText == "(");
Check("opening-parenthesis guard accepts only a parenthesis continuation",
    SqlContextAnalyzer.IsCompatible(missingCreateTableParenthesisProbe, immediateCreateTableParenthesis!) &&
    !SqlContextAnalyzer.IsCompatible(missingCreateTableParenthesisProbe, new SuggestionResult
    {
        Mode = SuggestionMode.Completion,
        InsertText = "id BIGINT"
    }));

var unrankedCandidates = new SuggestionResult
{
    Mode = SuggestionMode.Completion,
    Candidates =
    [
        new SuggestionCandidate { Rank = 1, InsertText = "is_default TINYINT NOT NULL DEFAULT 0,", DisplayText = "is_default ...", Confidence = 0.99 },
        new SuggestionCandidate { Rank = 2, InsertText = "FOREIGN KEY (user_id) REFERENCES yangzhuoran_exam_users (id)", DisplayText = "FOREIGN KEY ...", Confidence = 0.8 }
    ]
};
var rankedCandidates = SqlContextAnalyzer.RankCandidates(foreignKeyProbe, unrankedCandidates);
Check("foreign key candidate outranks repeated field", rankedCandidates.InsertText.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase));
Check("foreign key context always has three fallback candidates", rankedCandidates.Candidates.Count == 3);
Check("all strict foreign-key candidates stay syntax-compatible", rankedCandidates.Candidates.All(candidate => candidate.InsertText.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)));
Check("candidate cycling wraps locally", rankedCandidates.SelectCandidate(3).ActiveCandidateIndex == 0);

var candidatePrompt = PromptTemplates.BuildCandidateContinuationPrompt(foreignKeyProbe);
Check("candidate prompt requests exactly three choices", candidatePrompt.Contains("恰好 3", StringComparison.Ordinal));
Check("candidate prompt carries inferred foreign-key state", candidatePrompt.Contains("ForeignKeyConstraint", StringComparison.Ordinal));
Check("candidate prompt forbids repeating is_default", candidatePrompt.Contains("is_default", StringComparison.Ordinal));

var formerlyTruncatedDocument = "FULL_DOCUMENT_START\n" + new string('x', 150_000) + "\nFULL_DOCUMENT_END";
var formerlyTruncatedPrompt = PromptTemplates.BuildCompletionPrompt(new ProbeResult
{
    Text = formerlyTruncatedDocument,
    CaretOffset = formerlyTruncatedDocument.Length / 2
});
Check("documents below the DeepSeek-safe limit are sent in full",
    formerlyTruncatedPrompt.Contains("FULL_DOCUMENT_START", StringComparison.Ordinal) &&
    formerlyTruncatedPrompt.Contains("FULL_DOCUMENT_END", StringComparison.Ordinal) &&
    !formerlyTruncatedPrompt.Contains("模型安全上下文而省略", StringComparison.Ordinal));

var oversizedDocument = "OVERSIZED_START\n" +
                        new string('l', 499_980) +
                        "\nCARET_ZONE\n" +
                        new string('r', 499_980) +
                        "\nOVERSIZED_END";
var oversizedCaret = oversizedDocument.IndexOf("CARET_ZONE", StringComparison.Ordinal) + "CARET_ZONE".Length;
var oversizedPrompt = PromptTemplates.BuildCompletionPrompt(new ProbeResult
{
    Text = oversizedDocument,
    CaretOffset = oversizedCaret
});
Check("oversized completion context keeps the exact cursor", oversizedPrompt.Contains("CARET_ZONE<CURSOR>", StringComparison.Ordinal));
Check("oversized completion context is cropped on both sides",
    !oversizedPrompt.Contains("OVERSIZED_START", StringComparison.Ordinal) &&
    !oversizedPrompt.Contains("OVERSIZED_END", StringComparison.Ordinal) &&
    oversizedPrompt.Contains("/* 前方", StringComparison.Ordinal) &&
    oversizedPrompt.Contains("/* 后方", StringComparison.Ordinal));
Check("completion context uses the 800k-character DeepSeek-safe budget",
    PromptTemplates.CompletionDocumentCharacterLimit == 800_000 && oversizedPrompt.Length < 805_000);

var settingsTestRoot = Path.Combine(Path.GetTempPath(), "Assistant-Settings-SelfTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(settingsTestRoot, "config"));
File.WriteAllText(Path.Combine(settingsTestRoot, "config", "settings.json"), """
    {
      "SettingsVersion": 2,
      "OverlayEnabled": true,
      "TaskbarPanelEnabled": false,
      "TaskbarWidth": 9999,
      "MobileAccessToken": "self-test-access-token-12345"
    }
    """);
try
{
    var migratedSettings = new SettingsService(settingsTestRoot);
    Check("legacy overlay users migrate to the taskbar channel", migratedSettings.Current.TaskbarPanelEnabled);
    Check("taskbar width is clamped to the supported range", migratedSettings.Current.TaskbarWidth == 1400);
    Check("global assistant hotkey defaults to Ctrl+Alt+S", migratedSettings.Current.ToggleAssistantHotkey == "Ctrl+Alt+S");
    Check("Word identify/switch hotkey migrates to Ctrl+Alt+A",
        migratedSettings.Current.SettingsVersion == 4 && migratedSettings.Current.WordExerciseHotkey == "Ctrl+Alt+A");
    Check("removed overlay settings are not written back",
        !File.ReadAllText(migratedSettings.SettingsPath).Contains("OverlayEnabled", StringComparison.Ordinal));
}
finally
{
    Directory.Delete(settingsTestRoot, recursive: true);
}

long taskbarExtendedStyle = 0;
Exception? taskbarStyleError = null;
var taskbarStyleThread = new Thread(() =>
{
    try
    {
        var taskbarWindow = new TaskbarSuggestionWindow();
        var handle = new WindowInteropHelper(taskbarWindow).EnsureHandle();
        taskbarExtendedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        taskbarWindow.Close();
    }
    catch (Exception ex)
    {
        taskbarStyleError = ex;
    }
});
taskbarStyleThread.SetApartmentState(ApartmentState.STA);
taskbarStyleThread.Start();
taskbarStyleThread.Join();
Check("taskbar tool-window style can be created without showing UI", taskbarStyleError is null);
Check("taskbar suggestion is excluded from app switching",
    (taskbarExtendedStyle & NativeMethods.WsExToolWindow) != 0 &&
    (taskbarExtendedStyle & NativeMethods.WsExNoActivate) != 0 &&
    (taskbarExtendedStyle & NativeMethods.WsExAppWindow) == 0);

var nativeEditorText = new string('w', 12_000);
string nativeReadText = string.Empty;
var nativeReadSucceeded = false;
var nativeReadTruncated = true;
Exception? nativeReadError = null;
var nativeReadThread = new Thread(() =>
{
    try
    {
        using var textBox = new System.Windows.Forms.TextBox
        {
            Multiline = true,
            Text = nativeEditorText
        };
        nativeReadSucceeded = NativeMethods.TryReadControlText(
            textBox.Handle,
            20_000,
            out nativeReadText,
            out nativeReadTruncated);
    }
    catch (Exception ex)
    {
        nativeReadError = ex;
    }
});
nativeReadThread.SetApartmentState(ApartmentState.STA);
nativeReadThread.Start();
nativeReadThread.Join();
Check("native editor text reader handles content beyond the 4096-character UIA cap",
    nativeReadError is null && nativeReadSucceeded && !nativeReadTruncated && nativeReadText == nativeEditorText);
Check("UI Automation Name payloads at 4096 characters are treated as incomplete",
    !NavicatProbeService.IsLikelyUiAutomationNameTruncated(4095) &&
    NavicatProbeService.IsLikelyUiAutomationNameTruncated(4096));

var longUnicodeText = new string('a', 4095) + "😀" + new string('中', 5000);
var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(longUnicodeText)));
Check("chunked content hash matches SHA256 for Unicode text", ContentHasher.Sha256(longUnicodeText) == expectedHash);

var loggerTestDirectory = Path.Combine(Path.GetTempPath(), "NavicatTextProbe-SelfTests", Guid.NewGuid().ToString("N"));
string loggerTestPath;
using (var logger = new ProbeLogger(loggerTestDirectory))
{
    loggerTestPath = logger.LogPath;
    logger.WriteSessionStarted();
    logger.WriteState(new ProbeResult
    {
        Status = ProbeStatus.Captured,
        Text = "state records must not repeat SQL"
    });
    logger.WriteCapture(new ProbeResult
    {
        Status = ProbeStatus.Captured,
        Text = "SELECT 1;",
        ContentHash = "logger-test",
        Elements =
        [
            new ElementDiagnostic(0, "Edit", "TEEAutoCompleteMemo", "", "Win32",
                "SELECT 1;\n" + new string('x', 500), [])
        ]
    });
    logger.WriteSuggestion(
        "word_exercise_locate_300",
        locateContext.ContextCharacterCount,
        123,
        locatedPayload with
        {
            PromptTokens = 321,
            PromptCacheHitTokens = 200,
            PromptCacheMissTokens = 121,
            CompletionTokens = 88
        },
        locateContext);
    for (var index = 0; index < 200; index++)
    {
        logger.WriteDiagnostic("bounded-queue-stress", index.ToString());
    }
}

var loggerLines = File.ReadAllLines(loggerTestPath)
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .Select(line => JsonDocument.Parse(line))
    .ToArray();
try
{
    Check("bounded background logger drains entries on dispose", loggerLines.Any(document => document.RootElement.GetProperty("eventType").GetString() == "session_ended"));
    var stateData = loggerLines.First(document => document.RootElement.GetProperty("eventType").GetString() == "state").RootElement.GetProperty("data");
    Check("state logs omit full SQL text", !stateData.TryGetProperty("Text", out _) && !stateData.TryGetProperty("text", out _));
    var captureData = loggerLines.First(document => document.RootElement.GetProperty("eventType").GetString() == "capture").RootElement.GetProperty("data");
    Check("debounced capture logs retain full SQL", captureData.GetProperty("Text").GetString() == "SELECT 1;");
    Check("element diagnostics do not duplicate full editor text", captureData.GetProperty("Elements")[0].GetProperty("Name").GetString()!.Length < 240);
    var wordSuggestionData = loggerLines.First(document =>
        document.RootElement.GetProperty("eventType").GetString() == "suggestion" &&
        document.RootElement.GetProperty("data").GetProperty("trigger").GetString() == "word_exercise_locate_300").RootElement.GetProperty("data");
    Check("Word AI logs include adaptive-window and token metadata",
        wordSuggestionData.GetProperty("wordRadius").GetInt32() == WordExerciseContextBuilder.FinalExpandedRadius &&
        wordSuggestionData.GetProperty("wordExpansionLevel").GetInt32() == 2 &&
        wordSuggestionData.GetProperty("PromptTokens").GetInt32() == 321 &&
        wordSuggestionData.GetProperty("PromptCacheHitTokens").GetInt32() == 200);
    Check("Word AI logs omit full canonical questions and detected user answers",
        !wordSuggestionData.TryGetProperty("QuestionText", out _) &&
        !wordSuggestionData.TryGetProperty("DetectedUserAnswer", out _) &&
        wordSuggestionData.GetProperty("questionTextCharacters").GetInt32() > 0 &&
        wordSuggestionData.GetProperty("detectedUserAnswerCharacters").GetInt32() > 0);
}
finally
{
    foreach (var document in loggerLines) document.Dispose();
    Directory.Delete(loggerTestDirectory, recursive: true);
}

await using var mobileServer = new MobileServerService();
var mobilePage = (string)typeof(MobileServerService)
    .GetMethod("BuildPage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
    .Invoke(null, null)!;
Check("mobile page exposes the full Word question, full answer and current continuation",
    mobilePage.Contains("id=\"question\"", StringComparison.Ordinal) &&
    mobilePage.Contains("id=\"fullAnswer\"", StringComparison.Ordinal) &&
    mobilePage.Contains("当前光标续写", StringComparison.Ordinal) &&
    mobilePage.Contains("识别/切换 Word 当前题目快捷键", StringComparison.Ordinal));
var accessUrls = mobileServer.GetAccessUrls(new AppSettings { MobileAccessToken = "self-test-token", MobilePort = 17832 });
Check("localhost is the first desktop URL", new Uri(accessUrls[0]).Host == "127.0.0.1");
var preferredPhoneHost = accessUrls.Skip(1).Select(url => new Uri(url).Host).FirstOrDefault();
Check("APIPA is never selected as the preferred phone URL", preferredPhoneHost is null || !preferredPhoneHost.StartsWith("169.254.", StringComparison.Ordinal));

if (failures.Count == 0)
{
    Console.WriteLine($"Preferred phone host: {preferredPhoneHost ?? "none"}");
    Console.WriteLine("All self-tests passed.");
    return 0;
}

foreach (var failure in failures) Console.Error.WriteLine($"FAILED: {failure}");
return 1;
