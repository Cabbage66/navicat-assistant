$ErrorActionPreference = "Stop"

$assistantRoot = $PSScriptRoot
$project = Join-Path $assistantRoot "src\NavicatTextProbe\NavicatTextProbe.csproj"
$output = Join-Path $assistantRoot "dist\NavicatTextProbe"
$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"

if (-not (Test-Path $dotnet)) {
    throw "Windows x64 dotnet was not found: $dotnet"
}

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --property:UseAppHost=false `
    --output $output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "Published successfully:" -ForegroundColor Green
Write-Host (Join-Path $output "NavicatTextProbe.dll")
