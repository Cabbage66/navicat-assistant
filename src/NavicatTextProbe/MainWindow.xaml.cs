using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NavicatTextProbe.Models;
using NavicatTextProbe.Services;
using Color = System.Windows.Media.Color;

namespace NavicatTextProbe;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _probeTimer;
    private readonly NavicatProbeService _probeService = new();
    private readonly ProbeLogger _logger;
    private readonly SettingsService _settingsService;
    private readonly DeepSeekClient _deepSeekClient = new();
    private readonly NavicatSelectionService _selectionService = new();
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly KeyboardFallbackService _keyboardFallbackService = new();
    private readonly MobileServerService _mobileServer = new();
    private readonly SuggestionOverlayWindow _overlayWindow = new();
    private readonly TaskbarSuggestionWindow _taskbarWindow = new();
    private readonly TrayIconService _trayIcon;
    private readonly StringBuilder _fallbackText = new();
    private readonly Dictionary<string, SuggestionResult> _candidateCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _candidateCacheOrder = new();
    private CancellationTokenSource? _predictionCancellation;
    private ProbeResult? _lastCapturedProbe;
    private SuggestionResult _currentSuggestion = SuggestionResult.Info("等待 Navicat SQL……");
    private bool _isMonitoring = true;
    private bool _captureInProgress;
    private bool _exitRequested;
    private bool _fallbackSessionInitialized;
    private bool _sqlViewDirty;
    private bool _suggestionViewDirty;
    private int _fallbackCaret;
    private string? _lastContentHash;
    private string? _lastStateKey;
    private string? _lastPredictionContext;
    private string? _predictionInFlightContext;
    private string? _manualCandidateContext;
    private string? _manualCandidateRequestProbeKey;
    private string? _apiKey;
    private SuggestionAcceptanceState? _suggestionAcceptance;
    private ProbeResult? _pendingCaptureLog;
    private DateTimeOffset _pendingCaptureChangedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHealthSnapshotAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCompletionRequestStarted = DateTimeOffset.MinValue;
    private bool _candidateRequestInProgress;

    public MainWindow()
    {
        InitializeComponent();
        var assistantRoot = WorkspacePaths.FindAssistantRoot();
        _logger = new ProbeLogger(Path.Combine(assistantRoot, "logs"));
        _settingsService = new SettingsService(assistantRoot);
        _trayIcon = new TrayIconService();
        _apiKey = SafeReadApiKey();

        LogPathText.Text = $"日志：{Path.GetFileName(_logger.LogPath)}";
        _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _probeTimer.Tick += ProbeTimer_OnTick;

        SourceInitialized += (_, _) => RegisterHotkeys();
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;

        _overlayWindow.SettingsRequested += (_, _) => OpenSettings();
        _overlayWindow.HideRequested += (_, _) => SetOverlayEnabled(false);
        _overlayWindow.LocationChanged += (_, _) =>
        {
            if (_overlayWindow.IsLoaded)
            {
                _settingsService.Current.OverlayLeft = _overlayWindow.Left;
                _settingsService.Current.OverlayTop = _overlayWindow.Top;
            }
        };
        _taskbarWindow.SettingsRequested += (_, _) => OpenSettings();
        _taskbarWindow.HideRequested += (_, _) => SetTaskbarEnabled(false);

        _hotkeyService.Pressed += HotkeyService_OnPressed;
        _keyboardFallbackService.InputCaptured += KeyboardFallbackService_OnInputCaptured;
        _mobileServer.SettingsChanged += MobileServer_OnSettingsChanged;

        _trayIcon.OpenRequested += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayIcon.SettingsRequested += (_, _) => Dispatcher.Invoke(OpenSettings);
        _trayIcon.ToggleOverlayRequested += (_, _) => Dispatcher.Invoke(() => SetOverlayEnabled(!_settingsService.Current.OverlayEnabled));
        _trayIcon.ToggleTaskbarRequested += (_, _) => Dispatcher.Invoke(() => SetTaskbarEnabled(!_settingsService.Current.TaskbarPanelEnabled));
        _trayIcon.ToggleMobileRequested += (_, _) => Dispatcher.Invoke(() => ToggleMobileAsync().Forget());
        _trayIcon.ExitRequested += (_, _) => Dispatcher.Invoke(() => ExitAsync().Forget());
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger.WriteSessionStarted();
        _logger.WriteConfiguration(_settingsService.Current);
        _logger.WriteHealthSnapshot();
        _lastHealthSnapshotAt = DateTimeOffset.UtcNow;
        _probeTimer.Start();
        await ApplySettingsAsync();
        await RefreshFirewallStatusAsync();
        UpdateSuggestion(_apiKey is null
            ? SuggestionResult.Info("请先打开设置，保存撤销旧 Key 后生成的新 DeepSeek API Key")
            : _currentSuggestion);
    }

    private async void ProbeTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_isMonitoring || _captureInProgress)
        {
            return;
        }

        _captureInProgress = true;
        try
        {
            var result = await Task.Run(() => _probeService.CaptureForegroundNavicat());
            ApplyProbeResult(result);
            if (DateTimeOffset.UtcNow - _lastHealthSnapshotAt >= TimeSpan.FromMinutes(1))
            {
                _logger.WriteHealthSnapshot();
                _lastHealthSnapshotAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            ApplyProbeResult(ProbeResult.Failed("探测器内部错误", ex.ToString()));
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private void ApplyProbeResult(ProbeResult result)
    {
        var stateKey = $"{result.Status}|{result.ProcessId}|{result.Source}|{result.Message}|{result.CaretOffset}";
        var stateChanged = !string.Equals(_lastStateKey, stateKey, StringComparison.Ordinal);
        _lastStateKey = stateKey;

        switch (result.Status)
        {
            case ProbeStatus.Captured:
                _keyboardFallbackService.Stop();
                _fallbackSessionInitialized = false;
                _fallbackText.Clear();
                if (stateChanged)
                {
                    StatusDot.Fill = Brush(39, 174, 96);
                    StatusText.Text = "已读取 Navicat 当前 SQL 文档全文";
                    DetailText.Text =
                        $"进程：{result.ProcessName}（PID {result.ProcessId}） | {result.Source} | " +
                        $"光标：第 {result.CaretLine?.ToString() ?? "?"} 行，第 {result.CaretColumn?.ToString() ?? "?"} 列";
                }
                _lastCapturedProbe = result;

                var contentChanged = !string.Equals(_lastContentHash, result.ContentHash, StringComparison.Ordinal);
                if (contentChanged)
                {
                    _lastContentHash = result.ContentHash;
                    if (IsVisible)
                    {
                        SqlTextBox.Text = result.Text;
                        _sqlViewDirty = false;
                    }
                    else
                    {
                        _sqlViewDirty = true;
                    }
                    LengthText.Text = result.IsTruncated ? $"至少 {result.Text.Length:N0} 字符" : $"{result.Text.Length:N0} 字符";
                    _pendingCaptureLog = result;
                    _pendingCaptureChangedAt = DateTimeOffset.UtcNow;
                }
                else if (stateChanged)
                {
                    _logger.WriteState(result);
                }

                if (_pendingCaptureLog is not null &&
                    string.Equals(_pendingCaptureLog.ContentHash, result.ContentHash, StringComparison.Ordinal))
                {
                    _pendingCaptureLog = result;
                }
                FlushPendingCaptureLog(force: false);

                SchedulePrediction(result);
                break;

            case ProbeStatus.NavicatNotForeground:
                FlushPendingCaptureLog(force: true);
                _keyboardFallbackService.Stop();
                if (stateChanged)
                {
                    StatusDot.Fill = Brush(243, 156, 18);
                    StatusText.Text = "等待 Navicat 成为前台窗口……";
                    DetailText.Text = "切回 Navicat 查询编辑器继续输入；主窗口可以关闭到系统托盘。";
                }
                if (stateChanged) _logger.WriteState(result);
                break;

            case ProbeStatus.NoReadableText:
                FlushPendingCaptureLog(force: true);
                _keyboardFallbackService.CaptureEnabled =
                    _settingsService.Current.KeyboardFallbackEnabled && _keyboardFallbackService.Start();
                if (stateChanged)
                {
                    StatusDot.Fill = Brush(192, 57, 43);
                    StatusText.Text = "Navicat 全文接口暂时不可读";
                    DetailText.Text = _keyboardFallbackService.CaptureEnabled
                        ? "键盘兜底已临时接管：只在内存恢复输入，不写原始按键日志。"
                        : "键盘兜底默认关闭；可在设置中主动允许。";
                }
                if (_keyboardFallbackService.CaptureEnabled) InitializeFallbackSession();
                if (stateChanged) _logger.WriteState(result);
                break;

            default:
                FlushPendingCaptureLog(force: true);
                _keyboardFallbackService.Stop();
                if (stateChanged)
                {
                    StatusDot.Fill = Brush(192, 57, 43);
                    StatusText.Text = result.Message;
                    DetailText.Text = result.Diagnostics ?? "未知错误";
                }
                if (stateChanged) _logger.WriteState(result);
                break;
        }
    }

    private void FlushPendingCaptureLog(bool force)
    {
        if (_pendingCaptureLog is null ||
            (!force && DateTimeOffset.UtcNow - _pendingCaptureChangedAt < TimeSpan.FromMilliseconds(1400)))
        {
            return;
        }

        _logger.WriteCapture(_pendingCaptureLog);
        _pendingCaptureLog = null;
    }

    private void SchedulePrediction(ProbeResult probe, bool immediate = false)
    {
        if (!_settingsService.Current.AiEnabled || string.IsNullOrWhiteSpace(probe.Text))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            UpdateSuggestion(SuggestionResult.Info("未配置 DeepSeek API Key；全文探测仍在正常工作"));
            return;
        }

        var contextKey = $"{probe.ContentHash}|{probe.CaretOffset}|completion";
        var probeKey = BuildProbeKey(probe);
        if (_candidateRequestInProgress && string.Equals(probeKey, _manualCandidateRequestProbeKey, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(contextKey, _predictionInFlightContext, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(contextKey, _lastPredictionContext, StringComparison.Ordinal))
        {
            return;
        }

        var immediateGuardApplied = false;
        var immediateGuard = SqlContextAnalyzer.BuildImmediateGuardedSuggestion(probe);
        if (immediateGuard is not null && !SqlContextAnalyzer.IsCompatible(probe, _currentSuggestion))
        {
            RememberSuggestionForAcceptance(probe, immediateGuard);
            UpdateSuggestion(immediateGuard);
            immediateGuardApplied = true;
        }

        if (!immediateGuardApplied && IsFollowingCurrentSuggestion(probe))
        {
            _lastPredictionContext = contextKey;
            _predictionCancellation?.Cancel();
            return;
        }

        _suggestionAcceptance = null;
        _lastPredictionContext = contextKey;
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = new CancellationTokenSource();
        _predictionInFlightContext = contextKey;
        RunCompletionAsync(probe, contextKey, immediate, _predictionCancellation.Token).Forget();
    }

    private async Task RunCompletionAsync(ProbeResult probe, string contextKey, bool immediate, CancellationToken cancellationToken)
    {
        try
        {
            var caret = Math.Clamp(probe.CaretOffset ?? probe.Text.Length, 0, probe.Text.Length);
            var newLine = caret > 0 && probe.Text[caret - 1] == '\n';
            var delay = immediate ? 0 : newLine ? 120 : _settingsService.Current.PredictionDebounceMilliseconds;
            if (!newLine && !immediate)
            {
                var earliest = _lastCompletionRequestStarted.AddMilliseconds(
                    _settingsService.Current.PredictionMinimumIntervalMilliseconds);
                var intervalDelay = (int)Math.Ceiling((earliest - DateTimeOffset.UtcNow).TotalMilliseconds);
                delay = Math.Max(delay, Math.Max(0, intervalDelay));
            }
            if (delay > 0) await Task.Delay(delay, cancellationToken);

            _lastCompletionRequestStarted = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var result = await _deepSeekClient.CompleteAsync(
                _settingsService.Current,
                _apiKey!,
                PromptTemplates.BuildCompletionPrompt(probe),
                cancellationToken);
            result = SqlContextAnalyzer.GuardAutomaticResult(probe, result);
            stopwatch.Stop();
            if (!cancellationToken.IsCancellationRequested && string.Equals(contextKey, _lastPredictionContext, StringComparison.Ordinal))
            {
                _logger.WriteSuggestion(newLine ? "new_line" : "completion", probe.Text.Length, stopwatch.ElapsedMilliseconds, result);
                if (result.Mode == SuggestionMode.Error && _currentSuggestion.Mode == SuggestionMode.Completion)
                {
                    SuggestionExplanationText.Text = $"本次更新失败，已保留上一条建议：{result.DisplayText}";
                    return;
                }

                RememberSuggestionForAcceptance(probe, result);
                _manualCandidateContext = null;
                UpdateSuggestion(result);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke superseded this request.
        }
        finally
        {
            if (string.Equals(_predictionInFlightContext, contextKey, StringComparison.Ordinal))
            {
                _predictionInFlightContext = null;
            }
        }
    }

    private async Task CycleCandidatesAsync()
    {
        if (_candidateRequestInProgress)
        {
            SuggestionExplanationText.Text = "正在生成 3 个候选，请稍候；完成后再按一次会切换到下一个。";
            return;
        }

        var probe = await Task.Run(() => _probeService.CaptureForegroundNavicat(forceRefresh: true));
        if (probe.Status != ProbeStatus.Captured || string.IsNullOrWhiteSpace(probe.Text))
        {
            UpdateSuggestion(SuggestionResult.Error(
                "无法按当前光标生成候选",
                "请先让 Navicat 查询编辑器位于前台，再按候选快捷键。"));
            return;
        }

        _lastCapturedProbe = probe;
        var probeKey = BuildProbeKey(probe);
        var candidateContext = probeKey + "|continuation_candidates";
        if (string.Equals(candidateContext, _manualCandidateContext, StringComparison.Ordinal) &&
            _currentSuggestion.HasCandidates)
        {
            var next = _currentSuggestion.SelectCandidate(_currentSuggestion.ActiveCandidateIndex + 1);
            RememberSuggestionForAcceptance(probe, next);
            UpdateSuggestion(next);
            _logger.WriteSuggestion("candidate_cycle", probe.Text.Length, 0, next);
            return;
        }

        if (_candidateCache.TryGetValue(candidateContext, out var cached) && cached.HasCandidates)
        {
            _manualCandidateContext = candidateContext;
            _lastPredictionContext = $"{probe.ContentHash}|{probe.CaretOffset}|completion";
            var first = cached.SelectCandidate(0);
            RememberSuggestionForAcceptance(probe, first);
            UpdateSuggestion(first);
            _logger.WriteSuggestion("candidate_cache", probe.Text.Length, 0, first);
            return;
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            UpdateSuggestion(SuggestionResult.Error("请先在设置中保存新的 DeepSeek API Key"));
            return;
        }

        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = new CancellationTokenSource();
        _predictionInFlightContext = null;
        var token = _predictionCancellation.Token;
        _candidateRequestInProgress = true;
        _manualCandidateRequestProbeKey = probeKey;
        SuggestionExplanationText.Text = "正在按当前精确光标生成 3 个候选；生成期间保留现有建议，避免悬浮窗闪烁。";

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await _deepSeekClient.CompleteAsync(
                _settingsService.Current,
                _apiKey,
                PromptTemplates.BuildCandidateContinuationPrompt(probe),
                token,
                maxTokens: 1800,
                temperature: 0.35);
            result = SqlContextAnalyzer.RankCandidates(probe, result);
            stopwatch.Stop();
            if (token.IsCancellationRequested)
            {
                return;
            }

            _logger.WriteSuggestion("manual_candidates", probe.Text.Length, stopwatch.ElapsedMilliseconds, result);
            if (result.Mode == SuggestionMode.Error)
            {
                if (_currentSuggestion.Mode == SuggestionMode.Completion)
                {
                    SuggestionExplanationText.Text = $"候选生成失败，已保留上一条建议：{result.DisplayText}";
                }
                else
                {
                    UpdateSuggestion(result);
                }
                return;
            }

            if (!result.HasCandidates)
            {
                UpdateSuggestion(SuggestionResult.Error("模型没有返回可用候选", "请继续输入少量 SQL 后再按一次快捷键。"));
                return;
            }

            AddCandidateCache(candidateContext, result);
            _manualCandidateContext = candidateContext;
            _lastPredictionContext = $"{probe.ContentHash}|{probe.CaretOffset}|completion";
            var first = result.SelectCandidate(0);
            RememberSuggestionForAcceptance(probe, first);
            UpdateSuggestion(first);
        }
        catch (OperationCanceledException)
        {
            // The SQL changed while candidates were being generated.
        }
        finally
        {
            if (string.Equals(_manualCandidateRequestProbeKey, probeKey, StringComparison.Ordinal))
            {
                _candidateRequestInProgress = false;
                _manualCandidateRequestProbeKey = null;
            }
        }
    }

    private void AddCandidateCache(string key, SuggestionResult result)
    {
        if (!_candidateCache.ContainsKey(key))
        {
            _candidateCacheOrder.Enqueue(key);
        }

        _candidateCache[key] = result;
        while (_candidateCacheOrder.Count > 20)
        {
            _candidateCache.Remove(_candidateCacheOrder.Dequeue());
        }
    }

    private static string BuildProbeKey(ProbeResult probe)
    {
        var contentHash = string.IsNullOrWhiteSpace(probe.ContentHash)
            ? ContentHasher.Sha256(probe.Text)
            : probe.ContentHash;
        return $"{contentHash}|{probe.CaretOffset ?? probe.Text.Length}";
    }

    private async Task AnalyzeSelectionAsync(bool copyFromNavicat)
    {
        string selectedSql;
        if (copyFromNavicat)
        {
            var capture = await _selectionService.CaptureAsync(CancellationToken.None);
            if (!capture.Success)
            {
                UpdateSuggestion(SuggestionResult.Error(capture.Error));
                return;
            }

            selectedSql = capture.Text;
        }
        else
        {
            try
            {
                selectedSql = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
            }
            catch (Exception ex)
            {
                UpdateSuggestion(SuggestionResult.Error("读取剪贴板失败", ex.Message));
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedSql))
            {
                UpdateSuggestion(SuggestionResult.Info("请先复制一段 SQL，再点击“分析剪贴板”"));
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            UpdateSuggestion(SuggestionResult.Error("请先在设置中保存新的 DeepSeek API Key"));
            return;
        }

        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _predictionCancellation = new CancellationTokenSource();
        var token = _predictionCancellation.Token;
        UpdateSuggestion(SuggestionResult.Loading("正在诊断选中的 SQL……"));
        try
        {
            var fullDocument = _lastCapturedProbe?.Text ?? selectedSql;
            var stopwatch = Stopwatch.StartNew();
            var result = await _deepSeekClient.CompleteAsync(
                _settingsService.Current,
                _apiKey,
                PromptTemplates.BuildDiagnosisPrompt(selectedSql, fullDocument),
                token,
                maxTokens: 2200);
            stopwatch.Stop();
            if (!token.IsCancellationRequested)
            {
                _logger.WriteSuggestion("selection_diagnosis", selectedSql.Length, stopwatch.ElapsedMilliseconds, result);
                UpdateSuggestion(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request.
        }
    }

    private void UpdateSuggestion(SuggestionResult suggestion)
    {
        if (SameSuggestionPayload(_currentSuggestion, suggestion))
        {
            return;
        }

        if (suggestion.Mode != SuggestionMode.Completion)
        {
            _suggestionAcceptance = null;
        }

        _currentSuggestion = suggestion;
        if (IsVisible)
        {
            UpdateMainSuggestionView(suggestion);
            _suggestionViewDirty = false;
        }
        else
        {
            _suggestionViewDirty = true;
        }

        _overlayWindow.UpdateSuggestion(suggestion, _settingsService.Current.OverlayStyle);
        _taskbarWindow.UpdateSuggestion(suggestion, _settingsService.Current.TaskbarStyle);
        _mobileServer.UpdateSuggestion(suggestion, _settingsService.Current);
    }

    private void UpdateMainSuggestionView(SuggestionResult suggestion)
    {
        SuggestionModeText.Text = suggestion.HasCandidates
            ? $"候选 {suggestion.ActiveCandidateIndex + 1}/{suggestion.Candidates.Count}"
            : suggestion.Mode switch
        {
            SuggestionMode.Completion => "补全",
            SuggestionMode.Diagnosis => "诊断",
            SuggestionMode.Error => "错误",
            _ => "状态"
        };
        SuggestionDisplayText.Text = string.IsNullOrWhiteSpace(suggestion.DisplayText) ? "当前没有足够可靠的建议" : suggestion.DisplayText;
        SuggestionExplanationText.Text = string.IsNullOrWhiteSpace(suggestion.Explanation)
            ? suggestion.IsLoading ? "输入变化时，旧请求会自动取消。" : "短建议显示在悬浮窗；完整内容可在此处和手机端查看。"
            : suggestion.Explanation;
        SuggestionInsertTextBox.Text = string.IsNullOrWhiteSpace(suggestion.InsertText) ? "—" : suggestion.InsertText;
        IssuesText.Text = suggestion.HasCandidates
            ? $"再次按 {_settingsService.Current.CycleCandidatesHotkey} 切换下一个候选；当前为第 {suggestion.ActiveCandidateIndex + 1} 个，共 {suggestion.Candidates.Count} 个。"
            : suggestion.Issues.Count == 0 ? string.Empty : "问题：" + string.Join("；", suggestion.Issues);
    }

    private static bool SameSuggestionPayload(SuggestionResult left, SuggestionResult right)
    {
        if (left.Mode != right.Mode || left.IsLoading != right.IsLoading ||
            left.ActiveCandidateIndex != right.ActiveCandidateIndex ||
            !string.Equals(left.DisplayText, right.DisplayText, StringComparison.Ordinal) ||
            !string.Equals(left.InsertText, right.InsertText, StringComparison.Ordinal) ||
            !string.Equals(left.Explanation, right.Explanation, StringComparison.Ordinal) ||
            !left.Issues.SequenceEqual(right.Issues, StringComparer.Ordinal) ||
            left.Candidates.Count != right.Candidates.Count)
        {
            return false;
        }

        return left.Candidates.Zip(right.Candidates).All(pair =>
            pair.First.Rank == pair.Second.Rank &&
            string.Equals(pair.First.DisplayText, pair.Second.DisplayText, StringComparison.Ordinal) &&
            string.Equals(pair.First.InsertText, pair.Second.InsertText, StringComparison.Ordinal) &&
            string.Equals(pair.First.Explanation, pair.Second.Explanation, StringComparison.Ordinal));
    }

    private void RememberSuggestionForAcceptance(ProbeResult probe, SuggestionResult result)
    {
        if (result.Mode != SuggestionMode.Completion)
        {
            _suggestionAcceptance = null;
            return;
        }

        var suggestedText = string.IsNullOrWhiteSpace(result.InsertText)
            ? result.DisplayText
            : result.InsertText;
        if (string.IsNullOrEmpty(suggestedText))
        {
            _suggestionAcceptance = null;
            return;
        }

        var caret = Math.Clamp(probe.CaretOffset ?? probe.Text.Length, 0, probe.Text.Length);
        _suggestionAcceptance = new SuggestionAcceptanceState(probe.Text, caret, suggestedText);
    }

    private bool IsFollowingCurrentSuggestion(ProbeResult probe)
        => SuggestionAcceptanceMatcher.IsFollowing(_suggestionAcceptance, probe);

    private async Task ApplySettingsAsync()
    {
        var settings = _settingsService.Current;
        _trayIcon.UpdateState(settings);
        var needsKeyboardFallback = settings.KeyboardFallbackEnabled &&
                                    _lastStateKey?.StartsWith(ProbeStatus.NoReadableText.ToString(), StringComparison.Ordinal) == true;
        if (needsKeyboardFallback)
        {
            _keyboardFallbackService.CaptureEnabled = _keyboardFallbackService.Start();
        }
        else
        {
            _keyboardFallbackService.Stop();
        }

        _overlayWindow.UpdateSuggestion(_currentSuggestion, settings.OverlayStyle);
        if (settings.OverlayLeft is double left && settings.OverlayTop is double top)
        {
            _overlayWindow.Left = left;
            _overlayWindow.Top = top;
        }
        else if (double.IsNaN(_overlayWindow.Left) || double.IsNaN(_overlayWindow.Top))
        {
            var workArea = SystemParameters.WorkArea;
            _overlayWindow.Left = Math.Max(workArea.Left, workArea.Right - _overlayWindow.Width - 36);
            _overlayWindow.Top = workArea.Top + 72;
        }
        if (settings.OverlayEnabled)
        {
            if (!_overlayWindow.IsVisible) _overlayWindow.Show();
        }
        else
        {
            _overlayWindow.Hide();
        }

        _taskbarWindow.UpdateSuggestion(_currentSuggestion, settings.TaskbarStyle);
        if (settings.TaskbarPanelEnabled)
        {
            if (!_taskbarWindow.IsVisible) _taskbarWindow.Show();
            _taskbarWindow.Reposition();
        }
        else
        {
            _taskbarWindow.Hide();
        }

        if (settings.MobileServerEnabled)
        {
            if (!await _mobileServer.StartAsync(settings) && !string.IsNullOrWhiteSpace(_mobileServer.LastError))
            {
                _logger.WriteDiagnostic("mobile_server", _mobileServer.LastError);
            }
        }
        else
        {
            await _mobileServer.StopAsync();
        }

        RegisterHotkeys();
        UpdateChannelStatus();
    }

    private void RegisterHotkeys()
    {
        if (new WindowInteropHelper(this).Handle == 0) return;
        var settings = _settingsService.Current;
        var errors = _hotkeyService.Register(new WindowInteropHelper(this).Handle, new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.ToggleOverlay] = settings.ToggleOverlayHotkey,
            [HotkeyAction.ToggleTaskbar] = settings.ToggleTaskbarHotkey,
            [HotkeyAction.ToggleMobile] = settings.ToggleMobileHotkey,
            [HotkeyAction.CycleCandidates] = settings.CycleCandidatesHotkey,
            [HotkeyAction.AnalyzeSelection] = settings.AnalyzeSelectionHotkey
        });
        HotkeyStatusText.Text = errors.Count == 0
            ? $"续写候选：{settings.CycleCandidatesHotkey} | 诊断：{settings.AnalyzeSelectionHotkey}"
            : string.Join("；", errors);
    }

    private void HotkeyService_OnPressed(object? sender, HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleOverlay: SetOverlayEnabled(!_settingsService.Current.OverlayEnabled); break;
            case HotkeyAction.ToggleTaskbar: SetTaskbarEnabled(!_settingsService.Current.TaskbarPanelEnabled); break;
            case HotkeyAction.ToggleMobile: ToggleMobileAsync().Forget(); break;
            case HotkeyAction.CycleCandidates: CycleCandidatesAsync().Forget(); break;
            case HotkeyAction.AnalyzeSelection: AnalyzeSelectionAsync(true).Forget(); break;
        }
    }

    private void KeyboardFallbackService_OnInputCaptured(object? sender, KeyboardFallbackInput input)
    {
        Dispatcher.BeginInvoke(() =>
        {
            InitializeFallbackSession();
            var text = input.Kind == KeyboardFallbackInputKind.Paste ? SafeClipboardText() : input.Text;
            if (input.Kind == KeyboardFallbackInputKind.Backspace)
            {
                if (_fallbackCaret > 0)
                {
                    _fallbackText.Remove(_fallbackCaret - 1, 1);
                    _fallbackCaret--;
                }
            }
            else if (!string.IsNullOrEmpty(text))
            {
                _fallbackText.Insert(_fallbackCaret, text);
                _fallbackCaret += text.Length;
            }

            if (_fallbackText.Length > 100_000)
            {
                var remove = _fallbackText.Length - 100_000;
                _fallbackText.Remove(0, remove);
                _fallbackCaret = Math.Max(0, _fallbackCaret - remove);
            }

            var snapshot = CreateFallbackProbe();
            SqlTextBox.Text = snapshot.Text;
            LengthText.Text = $"{snapshot.Text.Length:N0} 字符（键盘兜底内存）";
            SchedulePrediction(snapshot, input.Text == "\n");
        });
    }

    private void InitializeFallbackSession()
    {
        if (_fallbackSessionInitialized) return;
        _fallbackSessionInitialized = true;
        _fallbackText.Clear();
        if (_lastCapturedProbe is not null)
        {
            _fallbackText.Append(_lastCapturedProbe.Text);
            _fallbackCaret = Math.Clamp(_lastCapturedProbe.CaretOffset ?? _fallbackText.Length, 0, _fallbackText.Length);
        }
        else
        {
            _fallbackCaret = 0;
        }
    }

    private ProbeResult CreateFallbackProbe()
    {
        var text = _fallbackText.ToString();
        return new ProbeResult
        {
            Status = ProbeStatus.Captured,
            Message = "键盘兜底内存快照",
            Source = "键盘兜底（未写按键日志）",
            Text = text,
            ContentHash = ContentHasher.Sha256(text),
            CaretOffset = _fallbackCaret,
            CaretPrefixLength = _fallbackCaret
        };
    }

    private void MobileServer_OnSettingsChanged(object? sender, MobileSettingsPatch patch)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var settings = _settingsService.Current;
            if (patch.OverlayEnabled is bool overlay) settings.OverlayEnabled = overlay;
            if (patch.TaskbarPanelEnabled is bool taskbar) settings.TaskbarPanelEnabled = taskbar;
            if (patch.OverlayFontSize is double of) settings.OverlayStyle.FontSize = of;
            if (patch.OverlayLineHeight is double ol) settings.OverlayStyle.LineHeight = ol;
            if (!string.IsNullOrWhiteSpace(patch.OverlayBackgroundColor)) settings.OverlayStyle.BackgroundColor = patch.OverlayBackgroundColor;
            if (!string.IsNullOrWhiteSpace(patch.OverlayFontColor)) settings.OverlayStyle.FontColor = patch.OverlayFontColor;
            if (patch.OverlayOpacity is double oo) settings.OverlayStyle.BackgroundOpacity = oo;
            if (patch.TaskbarFontSize is double tf) settings.TaskbarStyle.FontSize = tf;
            if (patch.TaskbarLineHeight is double tl) settings.TaskbarStyle.LineHeight = tl;
            if (!string.IsNullOrWhiteSpace(patch.TaskbarBackgroundColor)) settings.TaskbarStyle.BackgroundColor = patch.TaskbarBackgroundColor;
            if (!string.IsNullOrWhiteSpace(patch.TaskbarFontColor)) settings.TaskbarStyle.FontColor = patch.TaskbarFontColor;
            if (patch.TaskbarOpacity is double to) settings.TaskbarStyle.BackgroundOpacity = to;
            if (patch.MobileFontSize is double mf) settings.MobileStyle.FontSize = mf;
            if (patch.MobileLineHeight is double ml) settings.MobileStyle.LineHeight = ml;
            if (!string.IsNullOrWhiteSpace(patch.MobileBackgroundColor)) settings.MobileStyle.BackgroundColor = patch.MobileBackgroundColor;
            if (!string.IsNullOrWhiteSpace(patch.MobileFontColor)) settings.MobileStyle.FontColor = patch.MobileFontColor;
            if (patch.MobileOpacity is double mo) settings.MobileStyle.BackgroundOpacity = mo;
            _settingsService.Save(settings);
            ApplySettingsAsync().Forget();
        });
    }

    private void SetOverlayEnabled(bool enabled)
    {
        _settingsService.Current.OverlayEnabled = enabled;
        _settingsService.Save(_settingsService.Current);
        ApplySettingsAsync().Forget();
    }

    private void SetTaskbarEnabled(bool enabled)
    {
        _settingsService.Current.TaskbarPanelEnabled = enabled;
        _settingsService.Save(_settingsService.Current);
        ApplySettingsAsync().Forget();
    }

    private async Task ToggleMobileAsync()
    {
        _settingsService.Current.MobileServerEnabled = !_settingsService.Current.MobileServerEnabled;
        _settingsService.Save(_settingsService.Current);
        await ApplySettingsAsync();
    }

    private void UpdateChannelStatus()
    {
        var settings = _settingsService.Current;
        OverlayStatusText.Text = $"悬浮窗：{(settings.OverlayEnabled ? "开启" : "关闭")}（{settings.ToggleOverlayHotkey}）";
        TaskbarStatusText.Text = $"任务栏内建议：{(settings.TaskbarPanelEnabled ? "开启" : "关闭")}（{settings.ToggleTaskbarHotkey}）";
        if (_mobileServer.IsRunning)
        {
            var urls = _mobileServer.GetAccessUrls(settings);
            var local = urls.First();
            var lan = urls.FirstOrDefault(url => !url.Contains("127.0.0.1", StringComparison.Ordinal));
            MobileStatusText.Text = lan is null
                ? $"手机页面：已开启，但没有找到可用局域网地址。\n电脑：{local}"
                : $"手机页面：已开启（{settings.ToggleMobileHotkey}）\n手机首选：{lan}\n电脑本机：{local}";
        }
        else
        {
            MobileStatusText.Text = settings.MobileServerEnabled
                ? $"手机页面：启动失败 — {_mobileServer.LastError}"
                : $"手机页面：关闭（{settings.ToggleMobileHotkey}）";
        }
    }

    private void OpenSettings()
    {
        ShowMainWindow();
        var dialog = new SettingsWindow(_settingsService.Current, !string.IsNullOrWhiteSpace(_apiKey)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.UpdatedSettings is null) return;
        try
        {
            if (dialog.ClearApiKey) WindowsCredentialService.DeleteApiKey();
            if (!string.IsNullOrWhiteSpace(dialog.NewApiKey)) WindowsCredentialService.SaveApiKey(dialog.NewApiKey);
            _apiKey = SafeReadApiKey();
            _lastPredictionContext = null;
            _settingsService.Save(dialog.UpdatedSettings);
            ApplySettingsAsync().Forget();
            if (_lastCapturedProbe is not null) SchedulePrediction(_lastCapturedProbe, true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "设置保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        if (_sqlViewDirty && _lastCapturedProbe is not null)
        {
            SqlTextBox.Text = _lastCapturedProbe.Text;
            _sqlViewDirty = false;
        }
        if (_suggestionViewDirty)
        {
            UpdateMainSuggestionView(_currentSuggestion);
            _suggestionViewDirty = false;
        }
        Activate();
    }

    private void ToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isMonitoring = !_isMonitoring;
        ToggleButton.Content = _isMonitoring ? "暂停探测" : "继续探测";
        if (_isMonitoring)
        {
            _probeTimer.Start();
            StatusText.Text = "正在等待 Navicat……";
            StatusDot.Fill = Brush(243, 156, 18);
        }
        else
        {
            _probeTimer.Stop();
            _keyboardFallbackService.Stop();
            StatusText.Text = "探测已暂停";
            StatusDot.Fill = Brush(127, 140, 141);
        }
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void AnalyzeButton_OnClick(object sender, RoutedEventArgs e) => AnalyzeSelectionAsync(false).Forget();

    private void OpenLocalMobilePageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_mobileServer.IsRunning)
        {
            UpdateSuggestion(SuggestionResult.Info("请先开启手机局域网页服务"));
            return;
        }

        var localUrl = _mobileServer.GetAccessUrls(_settingsService.Current).First()
            .Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = localUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UpdateSuggestion(SuggestionResult.Error("无法打开默认浏览器", ex.Message));
        }
    }

    private void CopyPhoneUrlButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_mobileServer.IsRunning)
        {
            UpdateSuggestion(SuggestionResult.Info("请先开启手机局域网页服务"));
            return;
        }

        var url = _mobileServer.GetAccessUrls(_settingsService.Current)
            .FirstOrDefault(candidate => !candidate.Contains("127.0.0.1", StringComparison.Ordinal));
        if (url is null)
        {
            UpdateSuggestion(SuggestionResult.Error("没有找到真实局域网地址"));
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(url);
            _trayIcon.ShowBalloon("手机地址已复制", "请发到手机并确保手机与电脑在同一局域网。若打不开，再点击“修复手机访问”。");
        }
        catch (Exception ex)
        {
            UpdateSuggestion(SuggestionResult.Error("复制手机地址失败", ex.Message));
        }
    }

    private async void FirewallFixButton_OnClick(object sender, RoutedEventArgs e)
    {
        FirewallFixButton.IsEnabled = false;
        FirewallStatusText.Text = "防火墙：等待 Windows 管理员权限确认……";
        var result = await FirewallService.EnsureRuleAsync(_settingsService.Current.MobilePort);
        FirewallStatusText.Text = $"防火墙：{result.Message}";
        FirewallStatusText.Foreground = result.Success ? Brush(39, 174, 96) : Brush(192, 57, 43);
        FirewallFixButton.IsEnabled = true;
    }

    private async Task RefreshFirewallStatusAsync()
    {
        try
        {
            var enabled = await FirewallService.IsRuleEnabledAsync(_settingsService.Current.MobilePort);
            FirewallStatusText.Text = enabled
                ? "防火墙：已允许同一局域网访问"
                : "防火墙：尚未放行；手机打不开时点击“修复手机访问”";
            FirewallStatusText.Foreground = enabled ? Brush(39, 174, 96) : Brush(192, 57, 43);
        }
        catch (Exception ex)
        {
            FirewallStatusText.Text = $"防火墙检测失败：{ex.Message}";
        }
    }

    private void OpenLogsButton_OnClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_logger.LogDirectory);
        Process.Start(new ProcessStartInfo { FileName = _logger.LogDirectory, UseShellExecute = true });
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        Hide();
        _trayIcon.ShowBalloon("Navicat SQL Copilot 仍在运行", "双击托盘图标可重新打开；右键可切换输出或退出。");
    }

    private async Task ExitAsync()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        _probeTimer.Stop();
        _settingsService.Save(_settingsService.Current);
        await _mobileServer.StopAsync();
        Close();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        FlushPendingCaptureLog(force: true);
        _predictionCancellation?.Cancel();
        _predictionCancellation?.Dispose();
        _hotkeyService.Dispose();
        _keyboardFallbackService.Dispose();
        _deepSeekClient.Dispose();
        _trayIcon.Dispose();
        _overlayWindow.Close();
        _taskbarWindow.Close();
        _logger.Dispose();
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromRgb(red, green, blue));

    private static string? SafeReadApiKey()
    {
        try { return WindowsCredentialService.ReadApiKey(); }
        catch { return null; }
    }

    private static string SafeClipboardText()
    {
        try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty; }
        catch { return string.Empty; }
    }
}

internal static class TaskExtensions
{
    public static async void Forget(this Task task)
    {
        try { await task; }
        catch { /* UI-triggered background work reports its own state. */ }
    }
}
