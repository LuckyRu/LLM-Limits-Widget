[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$NoGhost
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repositoryRoot "spikes\FloatingOverlay\FloatingOverlay.csproj"
$outputDirectory = Join-Path $repositoryRoot "spikes\FloatingOverlay\bin\Release\net10.0-windows"
$executablePath = Join-Path $outputDirectory "LLMLimitsWidget.FloatingOverlay.exe"

Get-Process -Name "LLMLimitsWidget.FloatingOverlay" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

if (-not $SkipBuild) {
    dotnet build $projectPath --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Widget executable was not found: $executablePath"
}

$arguments = @()
if ($NoGhost) {
    $arguments += "--no-ghost"
}

$startInfo = @{
    FilePath = $executablePath
    ArgumentList = $arguments
    WorkingDirectory = $outputDirectory
    WindowStyle = "Hidden"
    PassThru = $true
}
$process = Start-Process @startInfo

Write-Output "LLM Limits Widget started as PID $($process.Id)."
