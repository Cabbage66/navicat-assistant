[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$RepositoryRoot,

    [ValidateRange(0, 1440)]
    [int]$DurationMinutes = 15,

    [ValidateRange(0, 86400)]
    [int]$DurationSeconds = 0,

    [ValidateRange(250, 10000)]
    [int]$IntervalMilliseconds = 500,

    [ValidateRange(20, 2000)]
    [int]$ContextCharacters = 160,

    [ValidateRange(1000, 5000000)]
    [int]$MaxTextCharacters = 5000000,

    [switch]$AllowBackgroundCapture,

    [switch]$ValidateOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

if ($null -eq ("Assistant.WordDocumentProbe.NativeMethods" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace Assistant.WordDocumentProbe
{
    public static class NativeMethods
    {
        private const uint GaRoot = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

        public static long GetForegroundWindowHandle()
        {
            return GetForegroundWindow().ToInt64();
        }

        public static long GetRootWindowHandle(long windowHandle)
        {
            if (windowHandle == 0)
            {
                return 0;
            }

            return GetAncestor(new IntPtr(windowHandle), GaRoot).ToInt64();
        }

        public static int GetWindowProcessId(long windowHandle)
        {
            if (windowHandle == 0)
            {
                return 0;
            }

            uint processId;
            GetWindowThreadProcessId(new IntPtr(windowHandle), out processId);
            return unchecked((int)processId);
        }
    }
}
'@
}

function Get-Sha256 {
    param([AllowEmptyString()][string]$Text)

    if ($null -eq $Text) {
        $Text = ""
    }

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
        return ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace("-", "")
    } finally {
        $algorithm.Dispose()
    }
}

function Get-ComValue {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,
        $Fallback = $null
    )

    try {
        return & $Action
    } catch {
        return $Fallback
    }
}

function Get-LineColumn {
    param([AllowEmptyString()][string]$Text)

    if ($null -eq $Text) {
        $Text = ""
    }

    $line = 1
    $column = 1
    for ($index = 0; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        if ($character -eq "`r") {
            if ($index + 1 -lt $Text.Length -and $Text[$index + 1] -eq "`n") {
                $index++
            }
            $line++
            $column = 1
        } elseif ($character -eq "`n") {
            $line++
            $column = 1
        } else {
            $column++
        }
    }

    return [pscustomobject]@{
        Line = $line
        Column = $column
    }
}

function Get-TailText {
    param(
        [AllowEmptyString()][string]$Text,
        [int]$MaximumLength
    )

    if ([string]::IsNullOrEmpty($Text) -or $Text.Length -le $MaximumLength) {
        return $Text
    }

    return $Text.Substring($Text.Length - $MaximumLength)
}

function Get-HeadText {
    param(
        [AllowEmptyString()][string]$Text,
        [int]$MaximumLength
    )

    if ([string]::IsNullOrEmpty($Text) -or $Text.Length -le $MaximumLength) {
        return $Text
    }

    return $Text.Substring(0, $MaximumLength)
}

function Get-LoggedText {
    param(
        [AllowEmptyString()][string]$Text,
        [int]$MaximumLength
    )

    if ($null -eq $Text) {
        $Text = ""
    }

    if ($Text.Length -le $MaximumLength) {
        return [pscustomobject]@{
            Text = $Text
            IsTruncated = $false
        }
    }

    $headLength = [int][Math]::Floor($MaximumLength / 2)
    $tailLength = $MaximumLength - $headLength
    return [pscustomobject]@{
        Text = $Text.Substring(0, $headLength) + $Text.Substring($Text.Length - $tailLength)
        IsTruncated = $true
    }
}

function Get-ForegroundState {
    $windowHandle = [Assistant.WordDocumentProbe.NativeMethods]::GetForegroundWindowHandle()
    $rootWindowHandle = [Assistant.WordDocumentProbe.NativeMethods]::GetRootWindowHandle($windowHandle)
    $processId = [Assistant.WordDocumentProbe.NativeMethods]::GetWindowProcessId($windowHandle)
    $processName = ""
    if ($processId -gt 0) {
        try {
            $processName = [Diagnostics.Process]::GetProcessById($processId).ProcessName
        } catch {
            $processName = ""
        }
    }

    return [pscustomobject]@{
        WindowHandle = $windowHandle
        RootWindowHandle = $rootWindowHandle
        ProcessId = $processId
        ProcessName = $processName
    }
}

function Get-ActiveWordDocumentContext {
    param($Application)

    $protectedViewWindow = Get-ComValue -Action { $Application.ActiveProtectedViewWindow }
    $isProtectedView = $null -ne $protectedViewWindow
    if ($isProtectedView) {
        $document = $protectedViewWindow.Document
    } else {
        if ([int]$Application.Documents.Count -le 0) {
            throw "Word 当前没有打开的文档。"
        }
        $document = $Application.ActiveDocument
    }

    $window = Get-ComValue -Action { $document.ActiveWindow }
    if ($null -eq $window) {
        $window = Get-ComValue -Action { $Application.ActiveWindow }
    }
    if ($null -eq $window) {
        throw "无法取得 Word 活动文档窗口。"
    }

    $selection = Get-ComValue -Action { $window.Selection }
    if ($null -eq $selection) {
        $selection = Get-ComValue -Action { $Application.Selection }
    }
    if ($null -eq $selection) {
        throw "无法取得 Word 当前光标或选区。"
    }

    $windowHandle = [int64](Get-ComValue -Action { $window.Hwnd } -Fallback 0)
    return [pscustomobject]@{
        Document = $document
        Window = $window
        Selection = $selection
        WindowHandle = $windowHandle
        RootWindowHandle = [Assistant.WordDocumentProbe.NativeMethods]::GetRootWindowHandle($windowHandle)
        IsProtectedView = $isProtectedView
    }
}

function Get-StorySnapshots {
    param($Document)

    $snapshots = @()
    foreach ($story in $Document.StoryRanges) {
        $current = $story
        $guard = 0
        while ($null -ne $current -and $guard -lt 100) {
            $text = [string](Get-ComValue -Action { $current.Text } -Fallback "")
            $logged = Get-LoggedText -Text $text -MaximumLength $MaxTextCharacters
            $snapshots += [pscustomobject]@{
                StoryType = [int](Get-ComValue -Action { $current.StoryType } -Fallback 0)
                RangeStart = [int](Get-ComValue -Action { $current.Start } -Fallback 0)
                RangeEnd = [int](Get-ComValue -Action { $current.End } -Fallback 0)
                TextLength = $text.Length
                TextHash = Get-Sha256 -Text $text
                Text = $logged.Text
                IsTruncated = $logged.IsTruncated
            }
            $current = Get-ComValue -Action { $current.NextStoryRange }
            $guard++
        }
    }

    return @($snapshots)
}

function Get-WordSnapshot {
    param($Context)

    $document = $Context.Document
    $selection = $Context.Selection
    $content = $document.Content
    $fullText = [string](Get-ComValue -Action { $content.Text } -Fallback "")
    $contentStart = [int]$content.Start
    $contentEnd = [int]$content.End
    $selectionStart = [int]$selection.Start
    $selectionEnd = [int]$selection.End
    $storyType = [int](Get-ComValue -Action { $selection.StoryType } -Fallback 0)
    $isMainStory = $storyType -eq 1 -and $selectionStart -ge $contentStart -and $selectionEnd -le $contentEnd

    $prefixText = ""
    $selectedText = ""
    $suffixText = ""
    $reassemblesExactly = $false
    $plainSelectionStart = $null
    $plainSelectionEnd = $null
    $positionSource = "selection_object_coordinates"

    if ($isMainStory) {
        $prefixText = [string]$document.Range($contentStart, $selectionStart).Text
        if ($selectionEnd -gt $selectionStart) {
            $selectedText = [string]$document.Range($selectionStart, $selectionEnd).Text
        }
        $suffixText = [string]$document.Range($selectionEnd, $contentEnd).Text
        $plainSelectionStart = $prefixText.Length
        $plainSelectionEnd = $prefixText.Length + $selectedText.Length
        $reassemblesExactly = ($prefixText + $selectedText + $suffixText) -ceq $fullText
        $positionSource = "document_range_text_lengths"
    } elseif ($selectionEnd -gt $selectionStart) {
        $selectedText = [string](Get-ComValue -Action { $selection.Range.Text } -Fallback "")
    }

    $lineColumn = Get-LineColumn -Text $prefixText
    $loggedText = Get-LoggedText -Text $fullText -MaximumLength $MaxTextCharacters
    $documentName = [string](Get-ComValue -Action { $document.Name } -Fallback "")
    $fullName = [string](Get-ComValue -Action { $document.FullName } -Fallback "")
    $wordOpenXml = [string](Get-ComValue -Action { $document.WordOpenXML } -Fallback "")

    return [pscustomobject]@{
        Timestamp = [DateTimeOffset]::Now.ToString("o")
        DocumentName = $documentName
        DocumentExtension = [IO.Path]::GetExtension($documentName)
        FullNameHash = Get-Sha256 -Text $fullName
        SaveFormat = [int](Get-ComValue -Action { $document.SaveFormat } -Fallback -1)
        Saved = [bool](Get-ComValue -Action { $document.Saved } -Fallback $false)
        ReadOnly = [bool](Get-ComValue -Action { $document.ReadOnly } -Fallback $false)
        CompatibilityMode = [int](Get-ComValue -Action { $document.CompatibilityMode } -Fallback -1)
        TrackRevisions = [bool](Get-ComValue -Action { $document.TrackRevisions } -Fallback $false)
        IsProtectedView = [bool]$Context.IsProtectedView
        WindowHandle = [int64]$Context.WindowHandle
        RootWindowHandle = [int64]$Context.RootWindowHandle
        ContentRangeStart = $contentStart
        ContentRangeEnd = $contentEnd
        TextLength = $fullText.Length
        TextHash = Get-Sha256 -Text $fullText
        Text = $loggedText.Text
        IsTextTruncated = $loggedText.IsTruncated
        WordOpenXmlLength = $wordOpenXml.Length
        WordOpenXmlHash = Get-Sha256 -Text $wordOpenXml
        SelectionStoryType = $storyType
        SelectionStart = $selectionStart
        SelectionEnd = $selectionEnd
        HasSelection = $selectionEnd -gt $selectionStart
        PlainTextSelectionStart = $plainSelectionStart
        PlainTextSelectionEnd = $plainSelectionEnd
        SelectedText = $selectedText
        SelectedTextLength = $selectedText.Length
        ContextBefore = Get-TailText -Text $prefixText -MaximumLength $ContextCharacters
        ContextAfter = Get-HeadText -Text $suffixText -MaximumLength $ContextCharacters
        CaretLine = $lineColumn.Line
        CaretColumn = $lineColumn.Column
        PositionSource = $positionSource
        ReassemblesExactly = $reassemblesExactly
    }
}

function Get-ConsistentWordSnapshot {
    param(
        $Context,
        [ValidateRange(1, 10)]
        [int]$MaximumAttempts = 3
    )

    $lastCandidate = $null
    $lastError = $null
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            $candidate = Get-WordSnapshot -Context $Context
            $lastCandidate = $candidate
            $isConsistent = $candidate.SelectionStoryType -ne 1 -or $candidate.ReassemblesExactly
            if ($isConsistent) {
                $candidate | Add-Member -NotePropertyName CaptureAttempts -NotePropertyValue $attempt -Force
                $candidate | Add-Member -NotePropertyName SnapshotConsistent -NotePropertyValue $true -Force
                return $candidate
            }
        } catch {
            $lastError = $_
        }

        if ($attempt -lt $MaximumAttempts) {
            Start-Sleep -Milliseconds 25
        }
    }

    if ($null -ne $lastCandidate) {
        $lastCandidate | Add-Member -NotePropertyName CaptureAttempts -NotePropertyValue $MaximumAttempts -Force
        $lastCandidate | Add-Member -NotePropertyName SnapshotConsistent -NotePropertyValue $false -Force
        return $lastCandidate
    }

    if ($null -ne $lastError) {
        throw $lastError
    }

    throw "无法取得一致的 Word 文档快照。"
}

if ($ValidateOnly) {
    Write-Host "Word COM 探针脚本验证通过。"
    return
}

$logsDirectory = Join-Path $RepositoryRoot "logs"
[IO.Directory]::CreateDirectory($logsDirectory) | Out-Null
$sessionStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logPath = Join-Path $logsDirectory ("document-probe-word-{0}.jsonl" -f $sessionStamp)
$latestPath = Join-Path $logsDirectory "latest-word-probe.txt"
$utf8WithoutBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($latestPath, $logPath, $utf8WithoutBom)
$writer = New-Object IO.StreamWriter($logPath, $false, $utf8WithoutBom)
$writer.AutoFlush = $true

function Write-ProbeEvent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Type,
        [Parameter(Mandatory = $true)]
        $Payload
    )

    $event = [ordered]@{
        timestamp = [DateTimeOffset]::Now.ToString("o")
        type = $Type
        payload = $Payload
    }
    $writer.WriteLine(($event | ConvertTo-Json -Compress -Depth 10))
}

$duration = [TimeSpan]::Zero
if ($DurationSeconds -gt 0) {
    $duration = [TimeSpan]::FromSeconds($DurationSeconds)
} elseif ($DurationMinutes -gt 0) {
    $duration = [TimeSpan]::FromMinutes($DurationMinutes)
}
$startedAt = [DateTimeOffset]::Now
$deadline = if ($duration -eq [TimeSpan]::Zero) { [DateTimeOffset]::MaxValue } else { $startedAt.Add($duration) }
$application = $null
$lastStateKey = ""
$lastDocumentKey = ""
$lastStatusKey = ""
$lastErrorKey = ""
$lastHeartbeatAt = [DateTimeOffset]::MinValue

Write-ProbeEvent -Type "session_started" -Payload ([pscustomobject]@{
    target = "Microsoft Word"
    progId = "Word.Application"
    durationSeconds = if ($duration -eq [TimeSpan]::Zero) { 0 } else { [int]$duration.TotalSeconds }
    intervalMilliseconds = $IntervalMilliseconds
    allowBackgroundCapture = [bool]$AllowBackgroundCapture
    privacy = "日志可能包含完整文档正文、上下文和选区文本，仅保存在本机。"
})

Write-Host "Word COM 探针已启动。日志：$logPath"
Write-Host "请切换到 Word，测试输入、光标移动和文本选择；控制台不会打印正文。"
if ($AllowBackgroundCapture) {
    Write-Warning "已允许后台读取 Word 活动文档，仅应对无敏感信息的测试文档使用。"
}

try {
    while ([DateTimeOffset]::Now -lt $deadline) {
        try {
            if ($null -eq $application) {
                $application = [Runtime.InteropServices.Marshal]::GetActiveObject("Word.Application")
            }

            $context = Get-ActiveWordDocumentContext -Application $application
            $foreground = Get-ForegroundState
            $windowMatches = $foreground.ProcessName -ieq "WINWORD" -and (
                $foreground.WindowHandle -eq $context.WindowHandle -or
                $foreground.RootWindowHandle -eq $context.RootWindowHandle
            )
            if (-not $AllowBackgroundCapture -and -not $windowMatches) {
                $statusKey = "waiting_foreground|$($foreground.ProcessName)|$($foreground.RootWindowHandle)|$($context.RootWindowHandle)"
                if ($statusKey -ne $lastStatusKey) {
                    Write-ProbeEvent -Type "status" -Payload ([pscustomobject]@{
                        state = "waiting_for_foreground_word_document"
                        foregroundProcess = $foreground.ProcessName
                        foregroundWindowHandle = $foreground.WindowHandle
                        foregroundRootWindowHandle = $foreground.RootWindowHandle
                        wordWindowHandle = $context.WindowHandle
                        wordRootWindowHandle = $context.RootWindowHandle
                    })
                    Write-Host "等待 Word 文档切到前台……"
                    $lastStatusKey = $statusKey
                }
                Start-Sleep -Milliseconds $IntervalMilliseconds
                continue
            }

            $snapshot = Get-ConsistentWordSnapshot -Context $context
            $documentKey = "$($snapshot.FullNameHash)|$($snapshot.TextHash)"
            $stateKey = "$documentKey|$($snapshot.SelectionStoryType)|$($snapshot.SelectionStart)|$($snapshot.SelectionEnd)|$($snapshot.Saved)"

            if ($documentKey -ne $lastDocumentKey) {
                Write-ProbeEvent -Type "document" -Payload ([pscustomobject]@{
                    documentName = $snapshot.DocumentName
                    documentExtension = $snapshot.DocumentExtension
                    fullNameHash = $snapshot.FullNameHash
                    saveFormat = $snapshot.SaveFormat
                    saved = $snapshot.Saved
                    readOnly = $snapshot.ReadOnly
                    compatibilityMode = $snapshot.CompatibilityMode
                    trackRevisions = $snapshot.TrackRevisions
                    isProtectedView = $snapshot.IsProtectedView
                    contentRangeStart = $snapshot.ContentRangeStart
                    contentRangeEnd = $snapshot.ContentRangeEnd
                    textLength = $snapshot.TextLength
                    textHash = $snapshot.TextHash
                    text = $snapshot.Text
                    isTextTruncated = $snapshot.IsTextTruncated
                    wordOpenXmlLength = $snapshot.WordOpenXmlLength
                    wordOpenXmlHash = $snapshot.WordOpenXmlHash
                    stories = Get-StorySnapshots -Document $context.Document
                })
                $lastDocumentKey = $documentKey
            }

            if ($stateKey -ne $lastStateKey) {
                Write-ProbeEvent -Type "position" -Payload ([pscustomobject]@{
                    documentTextHash = $snapshot.TextHash
                    windowHandle = $snapshot.WindowHandle
                    selectionStoryType = $snapshot.SelectionStoryType
                    selectionStart = $snapshot.SelectionStart
                    selectionEnd = $snapshot.SelectionEnd
                    hasSelection = $snapshot.HasSelection
                    plainTextSelectionStart = $snapshot.PlainTextSelectionStart
                    plainTextSelectionEnd = $snapshot.PlainTextSelectionEnd
                    selectedText = $snapshot.SelectedText
                    selectedTextLength = $snapshot.SelectedTextLength
                    contextBefore = $snapshot.ContextBefore
                    contextAfter = $snapshot.ContextAfter
                    caretLine = $snapshot.CaretLine
                    caretColumn = $snapshot.CaretColumn
                    positionSource = $snapshot.PositionSource
                    reassemblesExactly = $snapshot.ReassemblesExactly
                    captureAttempts = $snapshot.CaptureAttempts
                    snapshotConsistent = $snapshot.SnapshotConsistent
                    saved = $snapshot.Saved
                })
                Write-Host ("已捕获 Word：正文 {0} 字符，Story {1}，范围 {2}-{3}，文本下标 {4}-{5}，Saved={6}" -f `
                    $snapshot.TextLength,
                    $snapshot.SelectionStoryType,
                    $snapshot.SelectionStart,
                    $snapshot.SelectionEnd,
                    $snapshot.PlainTextSelectionStart,
                    $snapshot.PlainTextSelectionEnd,
                    $snapshot.Saved)
                $lastStateKey = $stateKey
                $lastStatusKey = "capturing"
            }

            if (([DateTimeOffset]::Now - $lastHeartbeatAt).TotalSeconds -ge 60) {
                Write-ProbeEvent -Type "heartbeat" -Payload ([pscustomobject]@{
                    state = "capturing"
                    textLength = $snapshot.TextLength
                    textHash = $snapshot.TextHash
                    selectionStart = $snapshot.SelectionStart
                    selectionEnd = $snapshot.SelectionEnd
                })
                $lastHeartbeatAt = [DateTimeOffset]::Now
            }

            $lastErrorKey = ""
        } catch {
            $errorKey = $_.Exception.GetType().FullName + "|" + $_.Exception.Message
            if ($errorKey -ne $lastErrorKey) {
                Write-ProbeEvent -Type "error" -Payload ([pscustomobject]@{
                    errorType = $_.Exception.GetType().FullName
                    message = $_.Exception.Message
                    scriptStackTrace = $_.ScriptStackTrace
                })
                Write-Warning ("Word 探测暂不可用：{0}；位置：{1}" -f $_.Exception.Message, $_.ScriptStackTrace)
                $lastErrorKey = $errorKey
            }
            $application = $null
        }

        Start-Sleep -Milliseconds $IntervalMilliseconds
    }
} finally {
    Write-ProbeEvent -Type "session_ended" -Payload ([pscustomobject]@{
        elapsedSeconds = [int]([DateTimeOffset]::Now - $startedAt).TotalSeconds
    })
    $writer.Dispose()
    if ($null -ne $application -and [Runtime.InteropServices.Marshal]::IsComObject($application)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($application)
    }
    Write-Host "Word COM 探针已结束。日志：$logPath"
}
