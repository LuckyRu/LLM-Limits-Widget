[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutputPath = "artifacts\release-notes.md"
)

$ErrorActionPreference = "Stop"

function Invoke-GitText {
    param([string[]]$Arguments)

    $output = @(& git @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE.`n$($output -join "`n")"
    }

    return @($output | ForEach-Object { [string]$_ })
}

$versionNumber = $Version.TrimStart('v')
$tag = "v$versionNumber"
$target = $tag

$null = & git rev-parse --verify "$tag^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) {
    $target = "HEAD"
}

$tags = Invoke-GitText @('tag', '--sort=-version:refname', '--list', 'v*')
$previousTag = $tags |
    Where-Object { $_ -and $_ -ne $tag } |
    Select-Object -First 1

if ($previousTag) {
    $range = "$previousTag..$target"
    $rangeDescription = "$previousTag..$target"
} else {
    $range = $target
    $rangeDescription = $target
}

$subjects = Invoke-GitText @('log', '--no-merges', '--format=%s', $range) |
    Where-Object { $_ -and $_.Trim() }

$newFeatures = [System.Collections.Generic.List[string]]::new()
$fixesAndImprovements = [System.Collections.Generic.List[string]]::new()
$technicalChanges = [System.Collections.Generic.List[string]]::new()

foreach ($subject in $subjects) {
    $match = [regex]::Match(
        $subject.Trim(),
        '^(?<type>[a-z]+)(?:\([^)]+\))?(?<breaking>!)?:\s*(?<message>.+)$'
    )

    if (-not $match.Success) {
        $technicalChanges.Add($subject.Trim())
        continue
    }

    $type = $match.Groups['type'].Value.ToLowerInvariant()
    $message = $match.Groups['message'].Value.Trim().TrimEnd('.') + '.'
    if ($match.Groups['breaking'].Success) {
        $message = "BREAKING: $message"
    }

    switch ($type) {
        'feat' { $newFeatures.Add($message); break }
        'fix' { $fixesAndImprovements.Add($message); break }
        'perf' { $fixesAndImprovements.Add($message); break }
        'revert' { $fixesAndImprovements.Add($message); break }
        default { $technicalChanges.Add($message); break }
    }
}

function FormatSection {
    param(
        [string]$Title,
        [System.Collections.Generic.List[string]]$Items,
        [string]$EmptyMessage
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("## $Title")
    if ($Items.Count -eq 0) {
        $lines.Add("- $EmptyMessage")
    } else {
        foreach ($item in $Items) {
            $lines.Add("- $item")
        }
    }
    $lines.Add('')
    return $lines
}

$notes = [System.Collections.Generic.List[string]]::new()
$notes.Add("# LLM Limits Widget $tag")
$notes.Add('')
$notes.Add("Изменения сформированы из диапазона ``$rangeDescription`` по правилам продуктового changelog.")
$notes.Add('')
foreach ($line in (FormatSection -Title 'Новые возможности' -Items $newFeatures -EmptyMessage 'Новых пользовательских возможностей в этом выпуске нет.')) {
    $notes.Add([string]$line)
}
foreach ($line in (FormatSection -Title 'Исправления и улучшения' -Items $fixesAndImprovements -EmptyMessage 'Исправлений и пользовательских улучшений в этом выпуске нет.')) {
    $notes.Add([string]$line)
}
foreach ($line in (FormatSection -Title 'Технические изменения' -Items $technicalChanges -EmptyMessage 'Отдельных технических изменений не зафиксировано.')) {
    $notes.Add([string]$line)
}
$notes.Add('## Артефакты')
$notes.Add("- ``LLM-Limits-Widget-$versionNumber-win-x64-self-contained.zip`` — portable, .NET включён.")
$notes.Add("- ``LLM-Limits-Widget-$versionNumber-win-x64-framework-dependent.zip`` — portable, требует .NET 10 Desktop Runtime.")
$notes.Add("- ``LLM-Limits-Widget-Setup-$versionNumber-self-contained.exe`` — автономный установщик.")
$notes.Add("- ``LLM-Limits-Widget-Setup-$versionNumber-framework-dependent.exe`` — установщик с проверкой и установкой .NET Runtime.")
$notes.Add('')
$notes.Add('Формат коммитов: `feat` — новая возможность, `fix`/`perf` — исправление или улучшение, остальные типы считаются техническими изменениями.')

$absoluteOutputPath = Join-Path (Get-Location) $OutputPath
$outputDirectory = Split-Path -Parent $absoluteOutputPath
$null = New-Item -ItemType Directory -Path $outputDirectory -Force
Set-Content -LiteralPath $absoluteOutputPath -Value ($notes -join "`r`n") -Encoding utf8
Write-Output "Product changelog generated: $absoluteOutputPath"
