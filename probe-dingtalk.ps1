[CmdletBinding()]
param(
    [ValidateRange(0, 1440)]
    [int]$DurationMinutes = 15,

    [ValidateRange(0, 86400)]
    [int]$DurationSeconds = 0,

    [ValidateRange(250, 10000)]
    [int]$IntervalMilliseconds = 750,

    [switch]$AllowAnyBrowserTab,

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$engine = Join-Path $PSScriptRoot "scripts\document-accessibility-probe.ps1"
$browserProcessPatterns = @(
    "(?i)^(chrome|msedge|firefox|brave|vivaldi|opera|qqbrowser|360chrome|360chromex|360se|chromium|arc)$"
)
$processNamePatterns = @(
    "(?i)^(dingtalk|dingtalkapp|dingtalklauncher|dingtalkshell|dingtalkhelper)$"
)
$windowTitlePatterns = @(
    "(?i)(钉钉(?:云)?文档|DingTalk\s*Docs?|AliDocs)"
)
$windowTitleProcessNamePatterns = $browserProcessPatterns

if ($AllowAnyBrowserTab) {
    $processNamePatterns += $browserProcessPatterns
    $windowTitlePatterns = @()
    $windowTitleProcessNamePatterns = @()
    Write-Warning "已允许读取任意前台浏览器标签页。探测期间请只把无敏感信息的钉钉测试文档切到前台，不要切换到邮箱、密码页或其他私密页面。"
}

& $engine `
    -TargetKey "dingtalk" `
    -TargetDisplayName "钉钉云文档" `
    -ProcessNamePatterns $processNamePatterns `
    -WindowTitlePatterns $windowTitlePatterns `
    -WindowTitleProcessNamePatterns $windowTitleProcessNamePatterns `
    -RepositoryRoot $PSScriptRoot `
    -DurationMinutes $DurationMinutes `
    -DurationSeconds $DurationSeconds `
    -IntervalMilliseconds $IntervalMilliseconds `
    -ValidateOnly:$ValidateOnly
