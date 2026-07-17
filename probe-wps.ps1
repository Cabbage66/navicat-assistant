[CmdletBinding()]
param(
    [ValidateRange(0, 1440)]
    [int]$DurationMinutes = 15,

    [ValidateRange(0, 86400)]
    [int]$DurationSeconds = 0,

    [ValidateRange(250, 10000)]
    [int]$IntervalMilliseconds = 750,

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$engine = Join-Path $PSScriptRoot "scripts\document-accessibility-probe.ps1"

& $engine `
    -TargetKey "wps" `
    -TargetDisplayName "WPS 文档" `
    -ProcessNamePatterns @(
        "(?i)^(wps|et|wpp|wpspdf|wpsoffice)$"
    ) `
    -RepositoryRoot $PSScriptRoot `
    -DurationMinutes $DurationMinutes `
    -DurationSeconds $DurationSeconds `
    -IntervalMilliseconds $IntervalMilliseconds `
    -ValidateOnly:$ValidateOnly
