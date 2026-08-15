[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [switch]$BuildInstaller
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "spikes\FloatingOverlay\FloatingOverlay.csproj"
$publishDirectory = Join-Path $repositoryRoot "artifacts\publish\$Runtime"

dotnet publish $projectPath --configuration Release --runtime $Runtime --self-contained:$SelfContained --output $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

$bridgePath = Join-Path $publishDirectory "claude-statusline-bridge\LLMLimitsWidget.ClaudeStatusLineBridge.exe"
if (-not (Test-Path -LiteralPath $bridgePath)) {
    throw "Claude statusLine bridge was not published: $bridgePath"
}

Write-Output "Portable release is ready: $publishDirectory"

if ($BuildInstaller) {
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path -LiteralPath $iscc)) {
        throw "Inno Setup 6 was not found. Install it or build the portable release only."
    }

    & $iscc (Join-Path $repositoryRoot "packaging\LLMLimitsWidget.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "Installer build failed with exit code $LASTEXITCODE."
    }
}
