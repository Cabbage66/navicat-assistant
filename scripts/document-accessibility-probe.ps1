[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$TargetKey,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$TargetDisplayName,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$ProcessNamePatterns,

    [string[]]$WindowTitlePatterns = @(),

    [string[]]$WindowTitleProcessNamePatterns = @(),

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$RepositoryRoot,

    [ValidateRange(0, 1440)]
    [int]$DurationMinutes = 15,

    [ValidateRange(0, 86400)]
    [int]$DurationSeconds = 0,

    [ValidateRange(250, 10000)]
    [int]$IntervalMilliseconds = 750,

    [ValidateRange(1000, 5000000)]
    [int]$MaxTextCharacters = 5000000,

    [ValidateRange(20, 5000)]
    [int]$MaxScannedElements = 1200,

    [ValidateRange(20, 1000)]
    [int]$MaxLoggedElements = 300,

    [long]$WindowHandleOverride = 0,

    [switch]$ValidateOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

try {
    Add-Type -AssemblyName UIAutomationClient -ErrorAction Stop
    Add-Type -AssemblyName UIAutomationTypes -ErrorAction Stop
} catch {
    throw "无法加载 Windows UI Automation。请使用 Windows PowerShell 5.1 运行此脚本。$([Environment]::NewLine)$($_.Exception.Message)"
}

if ($null -eq ("Assistant.DocumentProbe.NativeMethods" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Assistant.DocumentProbe
{
    public sealed class WindowTextReadResult
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public long ReportedLength { get; set; }
        public bool IsTruncated { get; set; }
        public string Error { get; set; }

        public WindowTextReadResult()
        {
            Text = String.Empty;
            Error = String.Empty;
        }
    }

    public sealed class TextPositionResult
    {
        public int Line { get; set; }
        public int Column { get; set; }
    }

    public sealed class ControlSelectionReadResult
    {
        public bool Success { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }

        public ControlSelectionReadResult()
        {
            Source = String.Empty;
            Error = String.Empty;
        }
    }

    public static class NativeMethods
    {
        private const uint WmGetText = 0x000D;
        private const uint WmGetTextLength = 0x000E;
        private const uint EmGetSel = 0x00B0;
        private const uint SmtoBlock = 0x0001;
        private const uint SmtoAbortIfHung = 0x0002;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
        private static extern int GetWindowTextLength(IntPtr windowHandle);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr windowHandle,
            uint message,
            UIntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeoutMilliseconds,
            out UIntPtr result);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutText(
            IntPtr windowHandle,
            uint message,
            UIntPtr wParam,
            [Out] StringBuilder lParam,
            uint flags,
            uint timeoutMilliseconds,
            out UIntPtr result);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutPointers(
            IntPtr windowHandle,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeoutMilliseconds,
            out UIntPtr result);

        public static string ReadWindowTitle(IntPtr windowHandle)
        {
            int length = GetWindowTextLength(windowHandle);
            if (length <= 0)
            {
                return String.Empty;
            }

            StringBuilder buffer = new StringBuilder(length + 1);
            GetWindowText(windowHandle, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        public static WindowTextReadResult ReadControlText(IntPtr windowHandle, int maxCharacters)
        {
            WindowTextReadResult read = new WindowTextReadResult();
            if (windowHandle == IntPtr.Zero || maxCharacters <= 0)
            {
                read.Error = "invalid_handle_or_limit";
                return read;
            }

            try
            {
                const uint timeoutMilliseconds = 400;
                const uint flags = SmtoBlock | SmtoAbortIfHung;
                UIntPtr rawLength;
                if (SendMessageTimeout(
                        windowHandle,
                        WmGetTextLength,
                        UIntPtr.Zero,
                        IntPtr.Zero,
                        flags,
                        timeoutMilliseconds,
                        out rawLength) == IntPtr.Zero ||
                    rawLength == UIntPtr.Zero)
                {
                    read.Error = "WM_GETTEXTLENGTH_unavailable_or_empty";
                    return read;
                }

                ulong reported = rawLength.ToUInt64();
                read.ReportedLength = reported > Int64.MaxValue ? Int64.MaxValue : (long)reported;
                int requested = (int)Math.Min(reported, (ulong)maxCharacters);
                StringBuilder buffer = new StringBuilder(requested + 1);
                UIntPtr copiedLength;
                if (SendMessageTimeoutText(
                        windowHandle,
                        WmGetText,
                        new UIntPtr((uint)(requested + 1)),
                        buffer,
                        flags,
                        timeoutMilliseconds,
                        out copiedLength) == IntPtr.Zero)
                {
                    read.Error = "WM_GETTEXT_timeout_or_failure";
                    return read;
                }

                read.Text = buffer.ToString();
                ulong expected = Math.Min(reported, (ulong)maxCharacters);
                read.IsTruncated = reported > (ulong)maxCharacters ||
                                   copiedLength.ToUInt64() < expected ||
                                   (ulong)read.Text.Length < expected;
                read.Success = read.Text.Length > 0;
                if (!read.Success)
                {
                    read.Error = "WM_GETTEXT_returned_empty";
                }

                return read;
            }
            catch (Exception ex)
            {
                read.Error = ex.GetType().Name + ": " + ex.Message;
                return read;
            }
        }

        public static ControlSelectionReadResult ReadControlSelection(IntPtr windowHandle)
        {
            ControlSelectionReadResult read = new ControlSelectionReadResult();
            if (windowHandle == IntPtr.Zero)
            {
                read.Error = "invalid_handle";
                return read;
            }

            const uint timeoutMilliseconds = 400;
            const uint flags = SmtoBlock | SmtoAbortIfHung;
            IntPtr startPointer = IntPtr.Zero;
            IntPtr endPointer = IntPtr.Zero;
            try
            {
                UIntPtr ignored;
                startPointer = Marshal.AllocHGlobal(sizeof(int));
                endPointer = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(startPointer, -1);
                Marshal.WriteInt32(endPointer, -1);
                if (SendMessageTimeoutPointers(
                        windowHandle,
                        EmGetSel,
                        startPointer,
                        endPointer,
                        flags,
                        timeoutMilliseconds,
                        out ignored) != IntPtr.Zero)
                {
                    int start = Marshal.ReadInt32(startPointer);
                    int end = Marshal.ReadInt32(endPointer);
                    if (start >= 0 && end >= 0)
                    {
                        read.Success = true;
                        read.Start = start;
                        read.End = end;
                        read.Source = "EM_GETSEL";
                        return read;
                    }
                }

                read.Error = "native_selection_messages_unavailable";
                return read;
            }
            catch (Exception ex)
            {
                read.Error = ex.GetType().Name + ": " + ex.Message;
                return read;
            }
            finally
            {
                if (startPointer != IntPtr.Zero) Marshal.FreeHGlobal(startPointer);
                if (endPointer != IntPtr.Zero) Marshal.FreeHGlobal(endPointer);
            }
        }

        public static string Sha256(string text)
        {
            if (text == null)
            {
                text = String.Empty;
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                StringBuilder result = new StringBuilder(hash.Length * 2);
                for (int index = 0; index < hash.Length; index++)
                {
                    result.Append(hash[index].ToString("X2"));
                }

                return result.ToString();
            }
        }

        public static TextPositionResult GetLineColumn(string text, int offset)
        {
            if (text == null)
            {
                text = String.Empty;
            }

            int boundedOffset = Math.Max(0, Math.Min(offset, text.Length));
            int line = 1;
            int lastLineBreak = -1;
            for (int index = 0; index < boundedOffset; index++)
            {
                if (text[index] == '\n')
                {
                    line++;
                    lastLineBreak = index;
                }
            }

            return new TextPositionResult
            {
                Line = line,
                Column = boundedOffset - lastLineBreak
            };
        }
    }
}
'@
}

function Test-AnyPattern {
    param(
        [AllowEmptyString()]
        [string]$Value,
        [string[]]$Patterns
    )

    foreach ($pattern in $Patterns) {
        if ($Value -match $pattern) {
            return $true
        }
    }

    return $false
}

function Get-SafeUiaProperty {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [System.Windows.Automation.AutomationProperty]$Property,
        $Fallback
    )

    try {
        $value = $Element.GetCurrentPropertyValue($Property, $true)
        if ([object]::ReferenceEquals($value, [System.Windows.Automation.AutomationElement]::NotSupported)) {
            return $Fallback
        }

        return $value
    } catch {
        return $Fallback
    }
}

function Get-RuntimeId {
    param([System.Windows.Automation.AutomationElement]$Element)

    try {
        return (($Element.GetRuntimeId() | ForEach-Object { $_.ToString() }) -join ".")
    } catch {
        $handle = Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::NativeWindowHandleProperty) 0
        $automationId = Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) ""
        return "fallback:$handle`:$automationId"
    }
}

function Get-CompactName {
    param([AllowEmptyString()][string]$Name)

    if ([string]::IsNullOrEmpty($Name)) {
        return ""
    }

    if ($Name.Length -le 240 -and -not $Name.Contains("`r") -and -not $Name.Contains("`n")) {
        return $Name
    }

    $firstLine = ($Name -split "`r?`n" | Where-Object { $_.Length -gt 0 } | Select-Object -First 1)
    if ($null -eq $firstLine) {
        $firstLine = ""
    }
    if ($firstLine.Length -gt 160) {
        $firstLine = $firstLine.Substring(0, 157) + "..."
    }

    return ("[已省略可能重复的正文，共 {0:N0} 字符] {1}" -f $Name.Length, $firstLine).TrimEnd()
}

function Convert-Rect {
    param($Rectangle)

    if ($null -eq $Rectangle) {
        return $null
    }

    try {
        if ($Rectangle.IsEmpty) {
            return $null
        }

        return [pscustomobject][ordered]@{
            x = $Rectangle.X
            y = $Rectangle.Y
            width = $Rectangle.Width
            height = $Rectangle.Height
        }
    } catch {
        return $null
    }
}

function Get-ElementEntry {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [int]$Depth,
        [string]$Relation
    )

    $controlTypeValue = Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) $null
    $controlType = ""
    if ($null -ne $controlTypeValue) {
        try { $controlType = $controlTypeValue.ProgrammaticName } catch { $controlType = $controlTypeValue.ToString() }
    }

    $rawName = [string](Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::NameProperty) "")
    $patterns = @()
    try {
        $patterns = @($Element.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName })
    } catch {
        $patterns = @()
    }

    $rectangle = Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::BoundingRectangleProperty) $null
    $descriptor = [pscustomobject][ordered]@{
        depth = $Depth
        relation = $Relation
        runtimeId = Get-RuntimeId $Element
        processId = [int](Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::ProcessIdProperty) 0)
        controlType = $controlType
        className = [string](Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::ClassNameProperty) "")
        automationId = [string](Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) "")
        frameworkId = [string](Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::FrameworkIdProperty) "")
        name = Get-CompactName $rawName
        nameLength = $rawName.Length
        nativeWindowHandle = [int](Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::NativeWindowHandleProperty) 0)
        hasKeyboardFocus = [bool](Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty) $false)
        isKeyboardFocusable = [bool](Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::IsKeyboardFocusableProperty) $false)
        isOffscreen = [bool](Get-SafeUiaProperty $Element ([System.Windows.Automation.AutomationElement]::IsOffscreenProperty) $false)
        boundingRectangle = Convert-Rect $rectangle
        patterns = $patterns
    }

    return [pscustomobject]@{
        Element = $Element
        RawName = $rawName
        Descriptor = $descriptor
        RuntimeId = $descriptor.runtimeId
        Relation = $Relation
        ControlType = $controlType
        NativeWindowHandle = $descriptor.nativeWindowHandle
    }
}

function Get-RangePrefixLength {
    param(
        [System.Windows.Automation.Text.TextPatternRange]$DocumentRange,
        [System.Windows.Automation.Text.TextPatternRange]$Range,
        [System.Windows.Automation.Text.TextPatternRangeEndpoint]$Endpoint,
        [int]$CharacterLimit
    )

    try {
        $prefix = $DocumentRange.Clone()
        $prefix.MoveEndpointByRange(
            [System.Windows.Automation.Text.TextPatternRangeEndpoint]::End,
            $Range,
            $Endpoint)
        $text = $prefix.GetText($CharacterLimit)
        if ($null -eq $text) {
            $text = ""
        }

        return [pscustomobject]@{
            offset = $text.Length
            isCapped = $text.Length -ge $CharacterLimit
        }
    } catch {
        return [pscustomobject]@{
            offset = $null
            isCapped = $false
        }
    }
}

function Get-RangeRectangles {
    param([System.Windows.Automation.Text.TextPatternRange]$Range)

    try {
        $raw = @($Range.GetBoundingRectangles())
        $rectangles = New-Object System.Collections.ArrayList
        for ($index = 0; $index + 3 -lt $raw.Count -and $rectangles.Count -lt 8; $index += 4) {
            [void]$rectangles.Add([pscustomobject][ordered]@{
                x = $raw[$index]
                y = $raw[$index + 1]
                width = $raw[$index + 2]
                height = $raw[$index + 3]
            })
        }

        return @($rectangles)
    } catch {
        return @()
    }
}

function New-CaretSnapshot {
    param(
        [string]$Text,
        $SelectionStart,
        $SelectionEnd,
        [bool]$OffsetsAreCapped,
        [string]$Source = "TextPattern.GetSelection"
    )

    if ($null -eq $SelectionStart -or $null -eq $SelectionEnd) {
        return $null
    }

    $ambiguous = $SelectionStart -ne $SelectionEnd
    $estimatedOffset = [int]$SelectionEnd
    $boundedOffset = [Math]::Max(0, [Math]::Min($estimatedOffset, $Text.Length))
    $contextStart = [Math]::Max(0, $boundedOffset - 160)
    $contextEnd = [Math]::Min($Text.Length, $boundedOffset + 160)
    $position = [Assistant.DocumentProbe.NativeMethods]::GetLineColumn($Text, $boundedOffset)

    return [pscustomobject][ordered]@{
        estimatedOffset = $estimatedOffset
        offsetCandidates = if ($ambiguous) { @([int]$SelectionStart, [int]$SelectionEnd) } else { @([int]$SelectionStart) }
        isAmbiguousBecauseSelectionDirectionIsUnavailable = $ambiguous
        isOffsetCapped = $OffsetsAreCapped
        line = $position.Line
        column = $position.Column
        contextBefore = $Text.Substring($contextStart, $boundedOffset - $contextStart)
        contextAfter = $Text.Substring($boundedOffset, $contextEnd - $boundedOffset)
        source = $Source
    }
}

function New-TextCandidate {
    param(
        $Entry,
        [string]$Source,
        [string]$Completeness,
        [string]$Text,
        [bool]$IsAtCharacterLimit,
        [int]$Score,
        $Caret,
        [object[]]$Selections
    )

    if ($null -eq $Text) {
        $Text = ""
    }

    return [pscustomobject][ordered]@{
        score = $Score
        source = $Source
        providerCompleteness = $Completeness
        runtimeId = $Entry.RuntimeId
        relation = $Entry.Relation
        controlType = $Entry.ControlType
        className = $Entry.Descriptor.className
        automationId = $Entry.Descriptor.automationId
        frameworkId = $Entry.Descriptor.frameworkId
        textLength = $Text.Length
        textHash = [Assistant.DocumentProbe.NativeMethods]::Sha256($Text)
        isAtCharacterLimit = $IsAtCharacterLimit
        caret = $Caret
        selections = @($Selections)
        text = $Text
    }
}

function Read-ElementCandidates {
    param(
        $Entry,
        [int]$CharacterLimit,
        [System.Collections.ArrayList]$Candidates,
        [hashtable]$ReadWindowHandles
    )

    $relationBonus = if ($Entry.Relation -eq "focus-chain") { 200 } else { 0 }
    $editorBonus = if ($Entry.ControlType -match "\.(Document|Edit)$") { 100 } else { 0 }
    $rawPattern = $null
    try {
        if ($Entry.Element.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$rawPattern)) {
            $pattern = [System.Windows.Automation.TextPattern]$rawPattern
            $documentRange = $pattern.DocumentRange
            $text = $documentRange.GetText($CharacterLimit)
            if ($null -eq $text) { $text = "" }

            $selections = New-Object System.Collections.ArrayList
            $firstStart = $null
            $firstEnd = $null
            $offsetsCapped = $false
            try {
                $selectionRanges = @($pattern.GetSelection())
                for ($index = 0; $index -lt $selectionRanges.Count -and $index -lt 8; $index++) {
                    $selectionRange = $selectionRanges[$index]
                    $selectedText = $selectionRange.GetText($CharacterLimit)
                    if ($null -eq $selectedText) { $selectedText = "" }
                    $start = Get-RangePrefixLength $documentRange $selectionRange ([System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start) $CharacterLimit
                    $end = Get-RangePrefixLength $documentRange $selectionRange ([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End) $CharacterLimit
                    if ($index -eq 0) {
                        $firstStart = $start.offset
                        $firstEnd = $end.offset
                        $offsetsCapped = $start.isCapped -or $end.isCapped
                    }

                    [void]$selections.Add([pscustomobject][ordered]@{
                        index = $index
                        startOffset = $start.offset
                        endOffset = $end.offset
                        offsetsAreCapped = $start.isCapped -or $end.isCapped
                        textLength = $selectedText.Length
                        textHash = [Assistant.DocumentProbe.NativeMethods]::Sha256($selectedText)
                        isAtCharacterLimit = $selectedText.Length -ge $CharacterLimit
                        boundingRectangles = Get-RangeRectangles $selectionRange
                        text = $selectedText
                    })
                }
            } catch {
                # Some providers expose DocumentRange but throw for GetSelection.
            }

            $caret = New-CaretSnapshot $text $firstStart $firstEnd $offsetsCapped
            [void]$Candidates.Add((New-TextCandidate `
                $Entry `
                "UI Automation TextPattern.DocumentRange" `
                "provider_document_range; whether off-screen content is virtualized must be verified" `
                $text `
                ($text.Length -ge $CharacterLimit) `
                (1000 + $relationBonus + $editorBonus) `
                $caret `
                @($selections)))
        }
    } catch {
        # Continue with other read-only providers on this element.
    }

    $rawPattern = $null
    try {
        if ($Entry.Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$rawPattern)) {
            $text = [string]([System.Windows.Automation.ValuePattern]$rawPattern).Current.Value
            if ($text.Length -gt $CharacterLimit) { $text = $text.Substring(0, $CharacterLimit) }
            if ($text.Length -gt 0) {
                [void]$Candidates.Add((New-TextCandidate `
                    $Entry `
                    "UI Automation ValuePattern.Value" `
                    "control_value; full-document coverage is provider-dependent" `
                    $text `
                    ($text.Length -ge $CharacterLimit) `
                    (800 + $relationBonus + $editorBonus) `
                    $null `
                    @()))
            }
        }
    } catch {
        # Continue probing.
    }

    $looksLikeTextContainer = $Entry.Relation -eq "focus-chain" -or $Entry.ControlType -match "\.(Document|Edit)$"
    if ($looksLikeTextContainer -and $Entry.RawName.Length -gt 0) {
        $nameText = $Entry.RawName
        if ($nameText.Length -gt $CharacterLimit) { $nameText = $nameText.Substring(0, $CharacterLimit) }
        [void]$Candidates.Add((New-TextCandidate `
            $Entry `
            "UI Automation AutomationElement.Name" `
            "accessible_name; may be a label, viewport text, or a provider-capped payload" `
            $nameText `
            ($nameText.Length -ge $CharacterLimit -or $nameText.Length -eq 4096) `
            (400 + $relationBonus + $editorBonus) `
            $null `
            @()))
    }

    $handle = [int]$Entry.NativeWindowHandle
    $shouldReadWindowText = $handle -ne 0 -and
        ($Entry.Relation -eq "focus-chain" -or $Entry.ControlType -match "\.(Document|Edit)$")
    if ($shouldReadWindowText -and -not $ReadWindowHandles.ContainsKey($handle)) {
        $ReadWindowHandles[$handle] = $true
        $nativeRead = [Assistant.DocumentProbe.NativeMethods]::ReadControlText([IntPtr]$handle, $CharacterLimit)
        if ($nativeRead.Success) {
            $nativeSelections = New-Object System.Collections.ArrayList
            $nativeCaret = $null
            $nativeSelection = [Assistant.DocumentProbe.NativeMethods]::ReadControlSelection([IntPtr]$handle)
            if ($nativeSelection.Success) {
                $selectionStart = [Math]::Min($nativeSelection.Start, $nativeSelection.End)
                $selectionEnd = [Math]::Max($nativeSelection.Start, $nativeSelection.End)
                $selectionWithinCapturedText = $selectionStart -le $nativeRead.Text.Length -and
                    $selectionEnd -le $nativeRead.Text.Length
                $selectedText = ""
                if ($selectionWithinCapturedText) {
                    $selectedText = $nativeRead.Text.Substring($selectionStart, $selectionEnd - $selectionStart)
                }
                $offsetsCapped = $nativeRead.IsTruncated -and -not $selectionWithinCapturedText
                [void]$nativeSelections.Add([pscustomobject][ordered]@{
                    index = 0
                    startOffset = $selectionStart
                    endOffset = $selectionEnd
                    rangeLength = $selectionEnd - $selectionStart
                    offsetsAreCapped = $offsetsCapped
                    textAvailableWithinCapturedText = $selectionWithinCapturedText
                    textLength = $selectedText.Length
                    textHash = [Assistant.DocumentProbe.NativeMethods]::Sha256($selectedText)
                    isAtCharacterLimit = $false
                    boundingRectangles = @()
                    text = $selectedText
                })
                $nativeCaret = New-CaretSnapshot `
                    $nativeRead.Text `
                    $selectionStart `
                    $selectionEnd `
                    $offsetsCapped `
                    ("Win32 " + $nativeSelection.Source)
            }
            [void]$Candidates.Add((New-TextCandidate `
                $Entry `
                "Win32 WM_GETTEXT" `
                "native_control_text; full-document coverage is control-dependent" `
                $nativeRead.Text `
                $nativeRead.IsTruncated `
                (900 + $relationBonus + $editorBonus) `
                $nativeCaret `
                @($nativeSelections)))
        }
    }
}

function Add-UniqueEntry {
    param(
        [System.Collections.ArrayList]$Entries,
        [hashtable]$SeenRuntimeIds,
        $Entry
    )

    if (-not $SeenRuntimeIds.ContainsKey($Entry.RuntimeId)) {
        $SeenRuntimeIds[$Entry.RuntimeId] = $true
        [void]$Entries.Add($Entry)
    }
}

function Complete-Candidates {
    param([System.Collections.ArrayList]$Candidates)

    $sorted = @($Candidates | Sort-Object `
        @{ Expression = { $_.score }; Descending = $true }, `
        @{ Expression = { $_.textLength }; Descending = $true })
    $contentOwners = @{}
    for ($index = 0; $index -lt $sorted.Count; $index++) {
        $candidate = $sorted[$index]
        $candidate | Add-Member -NotePropertyName candidateIndex -NotePropertyValue $index
        $contentKey = "{0}:{1}" -f $candidate.textHash, $candidate.textLength
        if ($candidate.textLength -gt 0 -and $contentOwners.ContainsKey($contentKey)) {
            $candidate | Add-Member -NotePropertyName textOmittedAsDuplicateOfCandidate -NotePropertyValue $contentOwners[$contentKey]
            $candidate.PSObject.Properties.Remove("text")
        } else {
            $contentOwners[$contentKey] = $index
        }
    }

    return @($sorted)
}

function Get-CaptureSignature {
    param($Capture)

    $parts = New-Object System.Collections.ArrayList
    [void]$parts.Add($Capture.windowHandle.ToString())
    [void]$parts.Add([Assistant.DocumentProbe.NativeMethods]::Sha256($Capture.windowTitle))
    [void]$parts.Add($Capture.focusedRuntimeId)
    [void]$parts.Add($Capture.scan.returnedElementCount.ToString())
    foreach ($candidate in $Capture.candidates) {
        $caretOffset = ""
        if ($null -ne $candidate.caret) { $caretOffset = [string]$candidate.caret.estimatedOffset }
        $selectionSignature = (($candidate.selections | ForEach-Object {
            "{0}:{1}:{2}" -f $_.startOffset, $_.endOffset, $_.textHash
        }) -join ",")
        [void]$parts.Add(("{0}|{1}|{2}|{3}|{4}" -f `
            $candidate.source,
            $candidate.runtimeId,
            $candidate.textHash,
            $caretOffset,
            $selectionSignature))
    }

    return [Assistant.DocumentProbe.NativeMethods]::Sha256(($parts -join "`n"))
}

function Capture-TargetWindow {
    param(
        [IntPtr]$WindowHandle,
        [string]$ProcessName,
        [int]$ProcessId,
        [string]$WindowTitle
    )

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
    $rootEntry = Get-ElementEntry $root 0 "root"
    $focused = $null
    try { $focused = [System.Windows.Automation.AutomationElement]::FocusedElement } catch { $focused = $null }

    $entries = New-Object System.Collections.ArrayList
    $seenRuntimeIds = @{}
    Add-UniqueEntry $entries $seenRuntimeIds $rootEntry

    $focusEntries = New-Object System.Collections.ArrayList
    $focusReachedRoot = $false
    if ($null -ne $focused) {
        $current = $focused
        for ($depth = 0; $null -ne $current -and $depth -lt 24; $depth++) {
            $entry = Get-ElementEntry $current $depth "focus-chain"
            [void]$focusEntries.Add($entry)
            if ($entry.RuntimeId -eq $rootEntry.RuntimeId) {
                $focusReachedRoot = $true
                break
            }

            try { $current = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($current) } catch { $current = $null }
        }
    }

    $focusedProcessId = 0
    if ($focusEntries.Count -gt 0) { $focusedProcessId = $focusEntries[0].Descriptor.processId }
    $focusIsSafe = $focusReachedRoot -or $focusedProcessId -eq $ProcessId
    if ($focusIsSafe) {
        foreach ($entry in $focusEntries) {
            Add-UniqueEntry $entries $seenRuntimeIds $entry
        }
    }

    $conditions = [System.Windows.Automation.Condition[]]@(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::IsTextPatternAvailableProperty,
            $true)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::IsValuePatternAvailableProperty,
            $true)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Document)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text))
    )
    $condition = New-Object System.Windows.Automation.OrCondition -ArgumentList (,$conditions)
    $descendants = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $returnedElementCount = $descendants.Count
    $inspectedElementCount = [Math]::Min($returnedElementCount, $MaxScannedElements)
    for ($index = 0; $index -lt $inspectedElementCount; $index++) {
        try {
            $entry = Get-ElementEntry $descendants[$index] (1000 + $index) "descendant-scan"
            Add-UniqueEntry $entries $seenRuntimeIds $entry
        } catch {
            # UI trees can mutate while they are being enumerated.
        }
    }

    $candidates = New-Object System.Collections.ArrayList
    $readWindowHandles = @{}
    foreach ($entry in $entries) {
        if ($candidates.Count -ge 80) { break }
        Read-ElementCandidates $entry $MaxTextCharacters $candidates $readWindowHandles
    }

    $textFragments = New-Object System.Collections.ArrayList
    $fragmentCharacters = 0
    foreach ($entry in $entries) {
        if ($entry.ControlType -notmatch "\.Text$" -or $entry.RawName.Length -eq 0) { continue }
        if ($fragmentCharacters -ge $MaxTextCharacters -or $textFragments.Count -ge 4000) { break }
        $remaining = $MaxTextCharacters - $fragmentCharacters
        $fragment = $entry.RawName
        if ($fragment.Length -gt $remaining) { $fragment = $fragment.Substring(0, $remaining) }
        [void]$textFragments.Add($fragment)
        $fragmentCharacters += $fragment.Length + 1
    }
    if ($textFragments.Count -gt 0) {
        $fragmentText = $textFragments -join "`n"
        $fragmentEntry = [pscustomobject]@{
            RuntimeId = $rootEntry.RuntimeId
            Relation = "descendant-text-aggregation"
            ControlType = "ControlType.Text[]"
            Descriptor = $rootEntry.Descriptor
        }
        [void]$candidates.Add((New-TextCandidate `
            $fragmentEntry `
            "UI Automation descendant Text names" `
            "diagnostic tree aggregation; may include UI chrome and only materialized viewport content" `
            $fragmentText `
            ($returnedElementCount -gt $inspectedElementCount -or $fragmentCharacters -ge $MaxTextCharacters) `
            300 `
            $null `
            @()))
    }

    $completedCandidates = @(Complete-Candidates $candidates)
    $diagnostics = @($entries | Select-Object -First $MaxLoggedElements | ForEach-Object { $_.Descriptor })
    $patternCounts = @($diagnostics | ForEach-Object { $_.patterns } | Group-Object | Sort-Object Name | ForEach-Object {
        [pscustomobject][ordered]@{ pattern = $_.Name; count = $_.Count }
    })
    $warnings = New-Object System.Collections.ArrayList
    if (-not $focusIsSafe) { [void]$warnings.Add("FocusedElement changed to another window during capture; focus-chain content was excluded.") }
    if ($returnedElementCount -gt $inspectedElementCount) { [void]$warnings.Add("The matching UIA tree exceeded the scan limit; element diagnostics are partial.") }
    if ($completedCandidates.Count -eq 0) { [void]$warnings.Add("No readable text provider was found in this snapshot.") }
    if ($completedCandidates.Count -gt 0 -and $completedCandidates[0].isAtCharacterLimit) { [void]$warnings.Add("The highest-ranked candidate reached a local character limit and is incomplete.") }

    $focusedRuntimeId = ""
    $focusedDescriptor = $null
    if ($focusIsSafe -and $focusEntries.Count -gt 0) {
        $focusedRuntimeId = $focusEntries[0].RuntimeId
        $focusedDescriptor = $focusEntries[0].Descriptor
    }
    $bestCandidateIndex = $null
    $bestCandidateSource = ""
    if ($completedCandidates.Count -gt 0) {
        $bestCandidateIndex = 0
        $bestCandidateSource = $completedCandidates[0].source
    }

    $capture = [pscustomobject][ordered]@{
        target = $TargetKey
        processName = $ProcessName
        processId = $ProcessId
        windowHandle = $WindowHandle.ToInt64()
        windowTitle = $WindowTitle
        rootElement = $rootEntry.Descriptor
        focusedElement = $focusedDescriptor
        focusedRuntimeId = $focusedRuntimeId
        focusChainReachedRoot = $focusReachedRoot
        scan = [pscustomobject][ordered]@{
            returnedElementCount = $returnedElementCount
            inspectedElementCount = $inspectedElementCount
            uniqueElementCount = $entries.Count
            loggedElementCount = $diagnostics.Count
            textFragmentCount = $textFragments.Count
            hitElementLimit = $returnedElementCount -gt $inspectedElementCount
        }
        patternCounts = $patternCounts
        likelyBestCandidateIndex = $bestCandidateIndex
        likelyBestCandidateSource = $bestCandidateSource
        candidates = $completedCandidates
        warnings = @($warnings)
        elements = $diagnostics
    }

    return [pscustomobject]@{
        Capture = $capture
        Signature = Get-CaptureSignature $capture
    }
}

function Write-JsonLogEntry {
    param(
        [string]$Path,
        [ref]$Sequence,
        [string]$EventType,
        $Data,
        [System.Text.Encoding]$Encoding
    )

    $Sequence.Value = $Sequence.Value + 1
    $record = [ordered]@{
        sequence = $Sequence.Value
        timestamp = [DateTimeOffset]::Now.ToString("o")
        eventType = $EventType
        data = $Data
    }
    $json = $record | ConvertTo-Json -Depth 18 -Compress
    [System.IO.File]::AppendAllText($Path, $json + [Environment]::NewLine, $Encoding)
}

foreach ($pattern in @($ProcessNamePatterns) + @($WindowTitlePatterns) + @($WindowTitleProcessNamePatterns)) {
    try { [void][regex]::new($pattern) } catch { throw "无效的目标匹配正则：$pattern" }
}

if ($ValidateOnly) {
    $rootAvailable = $null -ne [System.Windows.Automation.AutomationElement]::RootElement
    if (-not $rootAvailable) {
        throw "Windows UI Automation 根元素不可用。"
    }
    Write-Host "$TargetDisplayName 探测脚本依赖检查通过。" -ForegroundColor Green
    return
}

$logDirectory = Join-Path $RepositoryRoot "logs"
[System.IO.Directory]::CreateDirectory($logDirectory) | Out-Null
$logPath = Join-Path $logDirectory ("document-probe-{0}-{1}.jsonl" -f $TargetKey, [DateTime]::Now.ToString("yyyyMMdd-HHmmss"))
$latestPath = Join-Path $logDirectory ("latest-{0}-probe.txt" -f $TargetKey)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($logPath, "", $utf8NoBom)
[System.IO.File]::WriteAllText($latestPath, $logPath, $utf8NoBom)

$sequence = 0L
$lastForegroundSignature = ""
$lastCaptureSignature = ""
$lastHeartbeatAt = [DateTimeOffset]::MinValue
$captureCount = 0
$errorCount = 0
$endedNormally = $false
$stopAt = $null
if ($DurationSeconds -gt 0) {
    $stopAt = [DateTimeOffset]::Now.AddSeconds($DurationSeconds)
} elseif ($DurationMinutes -gt 0) {
    $stopAt = [DateTimeOffset]::Now.AddMinutes($DurationMinutes)
}

Write-JsonLogEntry $logPath ([ref]$sequence) "session_started" ([ordered]@{
    target = $TargetKey
    targetDisplayName = $TargetDisplayName
    processNamePatterns = $ProcessNamePatterns
    windowTitlePatterns = $WindowTitlePatterns
    windowTitleProcessNamePatterns = $WindowTitleProcessNamePatterns
    intervalMilliseconds = $IntervalMilliseconds
    durationMinutes = $DurationMinutes
    durationSecondsOverride = $DurationSeconds
    maxTextCharacters = $MaxTextCharacters
    maxScannedElements = $MaxScannedElements
    windowHandleOverride = $WindowHandleOverride
    fullDocumentLogging = $true
    readOnly = $true
    clipboardUsed = $false
    keyboardInputInjected = $false
    networkUsed = $false
    powerShellVersion = $PSVersionTable.PSVersion.ToString()
    operatingSystem = [Environment]::OSVersion.ToString()
    note = "候选正文、光标上下文和选区会写入本机日志；脚本不会修改文档、剪贴板或向网络发送内容。"
}) $utf8NoBom

Write-Host ""
Write-Host "$TargetDisplayName 只读探测已开始。" -ForegroundColor Green
Write-Host "日志：$logPath"
Write-Warning "日志会包含探测到的文档全文、光标上下文和选区，请勿在测试文档中放入密码、密钥或其他敏感信息。"
if ($DurationSeconds -gt 0) {
    Write-Host "请切换到目标文档并依次移动光标、选择文字；脚本将在 $DurationSeconds 秒后停止，也可提前按 Ctrl+C。"
} elseif ($DurationMinutes -eq 0) {
    Write-Host "请切换到目标文档并依次移动光标、选择文字；完成后回到此窗口按 Ctrl+C。"
} else {
    Write-Host "请切换到目标文档并依次移动光标、选择文字；脚本将在 $DurationMinutes 分钟后停止，也可提前按 Ctrl+C。"
}

try {
    while ($null -eq $stopAt -or [DateTimeOffset]::Now -lt $stopAt) {
        if ($WindowHandleOverride -ne 0) {
            $windowHandle = [IntPtr]$WindowHandleOverride
        } else {
            $windowHandle = [Assistant.DocumentProbe.NativeMethods]::GetForegroundWindow()
        }
        if ($windowHandle -eq [IntPtr]::Zero) {
            Start-Sleep -Milliseconds $IntervalMilliseconds
            continue
        }

        [uint32]$rawProcessId = 0
        [void][Assistant.DocumentProbe.NativeMethods]::GetWindowThreadProcessId($windowHandle, [ref]$rawProcessId)
        $processName = ""
        if ($rawProcessId -ne 0) {
            try { $processName = (Get-Process -Id ([int]$rawProcessId) -ErrorAction Stop).ProcessName } catch { $processName = "" }
        }
        $windowTitle = [Assistant.DocumentProbe.NativeMethods]::ReadWindowTitle($windowHandle)
        $processMatches = Test-AnyPattern $processName $ProcessNamePatterns
        $titleMatches = Test-AnyPattern $windowTitle $WindowTitlePatterns
        $titleProcessMatches = $WindowTitleProcessNamePatterns.Count -eq 0 -or
            (Test-AnyPattern $processName $WindowTitleProcessNamePatterns)
        $isTarget = $processMatches -or ($titleMatches -and $titleProcessMatches)
        $foregroundSignature = "{0}:{1}:{2}" -f $windowHandle.ToInt64(), $rawProcessId, $isTarget

        if (-not $isTarget) {
            if ($foregroundSignature -ne $lastForegroundSignature) {
                Write-JsonLogEntry $logPath ([ref]$sequence) "foreground_non_target" ([ordered]@{
                    observedProcessName = $processName
                    note = "Window title and content are intentionally omitted for non-target applications."
                }) $utf8NoBom
                Write-Host ("[{0}] 等待 {1} 前台窗口；当前进程：{2}" -f [DateTime]::Now.ToString("HH:mm:ss"), $TargetDisplayName, $processName)
                $lastForegroundSignature = $foregroundSignature
            }
            Start-Sleep -Milliseconds $IntervalMilliseconds
            continue
        }

        $lastForegroundSignature = $foregroundSignature
        try {
            $result = Capture-TargetWindow $windowHandle $processName ([int]$rawProcessId) $windowTitle
            if ($result.Signature -ne $lastCaptureSignature) {
                Write-JsonLogEntry $logPath ([ref]$sequence) "capture" $result.Capture $utf8NoBom
                $lastCaptureSignature = $result.Signature
                $captureCount++
                $bestLength = 0
                $caretOffset = "unknown"
                $selectionLength = 0
                if ($result.Capture.candidates.Count -gt 0) {
                    $best = $result.Capture.candidates[0]
                    $bestLength = $best.textLength
                    if ($null -ne $best.caret) { $caretOffset = [string]$best.caret.estimatedOffset }
                    if ($best.selections.Count -gt 0) { $selectionLength = $best.selections[0].textLength }
                }
                Write-Host ("[{0}] 已记录变化 #{1}：候选 {2}，最佳长度 {3:N0}，光标 {4}，选区 {5:N0}" -f `
                    [DateTime]::Now.ToString("HH:mm:ss"),
                    $captureCount,
                    $result.Capture.candidates.Count,
                    $bestLength,
                    $caretOffset,
                    $selectionLength) -ForegroundColor Cyan
                $lastHeartbeatAt = [DateTimeOffset]::Now
            } elseif ([DateTimeOffset]::Now - $lastHeartbeatAt -ge [TimeSpan]::FromSeconds(5)) {
                Write-JsonLogEntry $logPath ([ref]$sequence) "heartbeat" ([ordered]@{
                    targetForeground = $true
                    captureSignature = $lastCaptureSignature
                    captureCount = $captureCount
                    errorCount = $errorCount
                }) $utf8NoBom
                $lastHeartbeatAt = [DateTimeOffset]::Now
            }
        } catch {
            $errorCount++
            Write-JsonLogEntry $logPath ([ref]$sequence) "capture_error" ([ordered]@{
                processName = $processName
                processId = [int]$rawProcessId
                exceptionType = $_.Exception.GetType().FullName
                message = $_.Exception.Message
                stackTrace = $_.ScriptStackTrace
            }) $utf8NoBom
            Write-Warning "本次探测失败，已记录错误：$($_.Exception.Message)"
        }

        Start-Sleep -Milliseconds $IntervalMilliseconds
    }
    $endedNormally = $true
} catch [System.Management.Automation.PipelineStoppedException] {
    # Ctrl+C is an expected way to stop the probe.
} finally {
    try {
        Write-JsonLogEntry $logPath ([ref]$sequence) "session_ended" ([ordered]@{
            reason = if ($endedNormally) { "duration_elapsed" } else { "user_or_host_stopped" }
            captureCount = $captureCount
            errorCount = $errorCount
        }) $utf8NoBom
    } catch {
        # Capture entries are appended synchronously, so an abrupt host stop only risks this final marker.
    }
}

Write-Host ""
Write-Host "$TargetDisplayName 探测已停止，共记录 $captureCount 次内容变化。" -ForegroundColor Green
Write-Host "请保留日志，之后可让我分析：$logPath"
