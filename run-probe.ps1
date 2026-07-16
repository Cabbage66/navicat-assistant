$ErrorActionPreference = "Stop"

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$probe = Join-Path $PSScriptRoot "dist\Assistant\Assistant.dll"

if (-not (Test-Path $dotnet)) {
    throw "Windows x64 dotnet was not found: $dotnet"
}

if (-not (Test-Path $probe)) {
    throw "Published app was not found: $probe. Run build-and-publish.ps1 first."
}

Start-Process -FilePath $dotnet -ArgumentList @("`"$probe`"")
