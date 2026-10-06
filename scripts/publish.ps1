# Publishes the tray app as a single, self-contained executable, using the
# publish profile in screenshot\Properties\PublishProfiles.
#
#   scripts\publish.ps1                      # Release, win-x64
#   scripts\publish.ps1 -Clean -SelfTest     # wipe artifacts, publish, verify capture
#   scripts\publish.ps1 -Run                 # publish and launch
#   scripts\publish.ps1 -Profile win-arm64   # other RID (needs a matching profile)
#
# WPF cannot be trimmed or AOT-compiled (XAML/BAML resolves through reflection),
# so self-contained single-file is the only "static" packaging form.
param(
    [string]$Configuration = 'Release',
    [string]$Profile = 'win-x64',
    [switch]$Clean,
    [switch]$SelfTest,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'screenshot\screenshot.csproj'

if ($Clean) {
    & (Join-Path $PSScriptRoot 'clean.ps1')
}

Write-Host ("Publishing {0} ({1})..." -f $Profile, $Configuration) -ForegroundColor Cyan
& dotnet publish $project -c $Configuration -p:PublishProfile=$Profile --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# Locate the freshly published single-file exe.
$exe = Get-ChildItem (Join-Path $root 'screenshot\bin') -Recurse -File -Filter 'screenshot.exe' |
       Where-Object { $_.FullName -like '*\publish\*' } |
       Sort-Object LastWriteTime -Descending |
       Select-Object -First 1
if (-not $exe) { throw "Publish reported success but no screenshot.exe was found under a publish folder." }

$mb    = [Math]::Round($exe.Length / 1MB, 2)
$files = (Get-ChildItem $exe.Directory -File).Count
Write-Host ("Published: {0}" -f $exe.FullName) -ForegroundColor Green
Write-Host ("         {0} MB, {1} file(s) in output" -f $mb, $files)

if ($SelfTest) {
    Write-Host "Self-test:" -ForegroundColor Cyan
    $log = Join-Path $env:TEMP 'screenshot_selftest.txt'
    Remove-Item $log -ErrorAction SilentlyContinue
    Start-Process $exe.FullName -ArgumentList '--selftest' -Wait
    Start-Sleep -Milliseconds 400
    if (Test-Path $log) { Get-Content $log } else { Write-Warning "No self-test log produced." }
}

if ($Run) {
    Start-Process $exe.FullName
    Write-Host "Launched (running in tray)." -ForegroundColor Green
}
