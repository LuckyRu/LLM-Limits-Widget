[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$OutputName = "",
    [string]$InstallerSuffix = "",
    [string]$Version = "0.1.0",
    [switch]$SelfContained,
    [switch]$BuildInstaller
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "spikes\FloatingOverlay\FloatingOverlay.csproj"
$bridgeProjectPath = Join-Path $repositoryRoot "src\LLMLimitsWidget.ClaudeStatusLineBridge\LLMLimitsWidget.ClaudeStatusLineBridge.csproj"
$publishDirectory = Join-Path $repositoryRoot "artifacts\publish\$OutputName"
$bridgePublishDirectory = Join-Path $publishDirectory "claude-statusline-bridge"
$runtimeInstallerPath = Join-Path $repositoryRoot "artifacts\runtime\windowsdesktop-runtime-win-x64.exe"

if ([string]::IsNullOrWhiteSpace($OutputName)) {
    $OutputName = $Runtime
}

if ([string]::IsNullOrWhiteSpace($InstallerSuffix)) {
    $InstallerSuffix = $OutputName
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

$selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()

dotnet publish $projectPath `
    --configuration Release `
    --runtime $Runtime `
    --self-contained $selfContainedValue `
    --output $publishDirectory `
    -p:UseSharedCompilation=false `
    -p:SkipClaudeStatusLineBridgeBuild=true
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

$null = New-Item -ItemType Directory -Path $bridgePublishDirectory -Force
dotnet publish $bridgeProjectPath `
    --configuration Release `
    --runtime $Runtime `
    --self-contained $selfContainedValue `
    --output $bridgePublishDirectory `
    -p:UseSharedCompilation=false `
    -p:SkipClaudeStatusLineBridgeBuild=true
if ($LASTEXITCODE -ne 0) {
    throw "Claude statusLine bridge publish failed with exit code $LASTEXITCODE."
}

$bridgePath = Join-Path $publishDirectory "claude-statusline-bridge\LLMLimitsWidget.ClaudeStatusLineBridge.exe"
if (-not (Test-Path -LiteralPath $bridgePath)) {
    throw "Claude statusLine bridge was not published: $bridgePath"
}

Write-Output "Release is ready: $publishDirectory (self-contained=$selfContainedValue)"

if ($BuildInstaller) {
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path -LiteralPath $iscc)) {
        throw "Inno Setup 6 was not found. Install it or build the portable release only."
    }

    $requiresRuntime = if ($SelfContained.IsPresent) { "0" } else { "1" }
    if ($requiresRuntime -eq "1" -and -not (Test-Path -LiteralPath $runtimeInstallerPath)) {
        throw "The framework-dependent installer requires the .NET runtime bootstrapper: $runtimeInstallerPath"
    }

    $isccArguments = @(
        "/DMyAppVersion=$Version",
        "/DPublishDir=$publishDirectory",
        "/DInstallerSuffix=$InstallerSuffix",
        "/DRequiresRuntime=$requiresRuntime"
    )
    if ($requiresRuntime -eq "1") {
        $isccArguments += "/DRuntimeInstallerPath=$runtimeInstallerPath"
    }
    $isccArguments += (Join-Path $repositoryRoot "packaging\LLMLimitsWidget.iss")

    & $iscc @isccArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Installer build failed with exit code $LASTEXITCODE."
    }
}
