using System.Security.Cryptography;
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

Check("unchanged document remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument, baseDocument.Length)));
Check("matching prefix remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "OM", baseDocument.Length + 2)));
Check("case-insensitive SQL keyword prefix remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "om users", baseDocument.Length + 8)));
Check("complete suggestion remains stable", SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "OM users;", baseDocument.Length + 9)));
Check("divergent input requests a new suggestion", !SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument + "OX", baseDocument.Length + 2)));
Check("moving the caret requests a new suggestion", !SuggestionAcceptanceMatcher.IsFollowing(state, Probe(baseDocument, 2)));

var middleBase = "SELECT  WHERE active = 1";
var middleState = new SuggestionAcceptanceState(middleBase, 7, "* FROM users");
var middleCurrent = middleBase.Insert(7, "* FROM");
Check("text after the caret is preserved", SuggestionAcceptanceMatcher.IsFollowing(middleState, Probe(middleCurrent, 13)));

var newlineState = new SuggestionAcceptanceState("-- intent", 9, "\nSELECT 1;");
Check("CRLF input matches LF suggestion", SuggestionAcceptanceMatcher.IsFollowing(newlineState, Probe("-- intent\r\nSELECT", 17)));

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
}
finally
{
    foreach (var document in loggerLines) document.Dispose();
    Directory.Delete(loggerTestDirectory, recursive: true);
}

await using var mobileServer = new MobileServerService();
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
