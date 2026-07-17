[CmdletBinding()]
param(
    [ValidateRange(0, 1440)]
    [int]$DurationMinutes = 15,

    [ValidateRange(0, 86400)]
    [int]$DurationSeconds = 0,

    [ValidateRange(250, 10000)]
    [int]$IntervalMilliseconds = 500,

    [switch]$AllowBackgroundCapture,

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
$engine = Join-Path $PSScriptRoot "scripts\word-document-com-probe.ps1"

& $engine `
    -RepositoryRoot $PSScriptRoot `
    -DurationMinutes $DurationMinutes `
    -DurationSeconds $DurationSeconds `
    -IntervalMilliseconds $IntervalMilliseconds `
    -AllowBackgroundCapture:$AllowBackgroundCapture `
    -ValidateOnly:$ValidateOnly
