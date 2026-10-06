# Removes build artifacts so the next build or publish starts from scratch.
#
#   scripts\clean.ps1            # bin + obj
#   scripts\clean.ps1 -All       # also .vs and the single-file runtime cache
#   scripts\clean.ps1 -DryRun    # report what would be removed, remove nothing
param(
    [switch]$All,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

$targets = @(
    (Join-Path $root 'screenshot\bin'),
    (Join-Path $root 'screenshot\obj')
)
if ($All) {
    $targets += (Join-Path $root '.vs')                        # Visual Studio state
    $targets += (Join-Path $env:TEMP '.net\screenshot')        # single-file native-lib extract
    $targets += (Join-Path $env:TEMP 'screenshot_selftest.txt')
}

$total = 0
foreach ($t in $targets) {
    if (-not (Test-Path $t)) {
        Write-Host ("absent       : {0}" -f $t)
        continue
    }

    $size = (Get-ChildItem $t -Recurse -File -Force -ErrorAction SilentlyContinue |
             Measure-Object Length -Sum).Sum
    if ($null -eq $size) { $size = 0 }

    if ($DryRun) {
        Write-Host ("would remove : {0}  ({1} MB)" -f $t, [Math]::Round($size / 1MB, 2))
        $total += $size
        continue
    }

    try {
        Remove-Item -LiteralPath $t -Recurse -Force -ErrorAction Stop
        Write-Host ("removed      : {0}  ({1} MB)" -f $t, [Math]::Round($size / 1MB, 2))
        $total += $size
    }
    catch {
        # A single locked file (e.g. .vs while Visual Studio is open) must not
        # abort the whole clean — report what is left and keep going.
        $left = (Get-ChildItem $t -Recurse -File -Force -ErrorAction SilentlyContinue |
                 Measure-Object Length -Sum).Sum
        if ($null -eq $left) { $left = 0 }
        Write-Warning ("partially removed {0} — {1} MB left (file in use?): {2}" -f `
            $t, [Math]::Round($left / 1MB, 2), $_.Exception.Message)
        $total += ($size - $left)
    }
}

$verb = if ($DryRun) { 'Reclaimable' } else { 'Freed' }
Write-Host ("{0}: {1} MB" -f $verb, [Math]::Round($total / 1MB, 2)) -ForegroundColor Green
