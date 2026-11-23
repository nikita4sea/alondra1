<#
.SYNOPSIS
  Snapshot matrix with geometry assertions (client spec §5.3 and §6).

.DESCRIPTION
  Renders demo data (fixed sample time, no network) at the spec's window sizes, in light and dark themes, at 100 %
  and 125 % display scale (150 % and 200 % spot checks), in these states: inbox with a selected unread message,
  plain-text message, rich HTML newsletter, blocked remote images, empty search, one pane at a time, and Settings.
  Every snapshot writes a geometry report (header and toolbar height, insets, toolbar center line and heights,
  overflow, overlaps, pane gutter, column alignment); any FAIL line fails the run. Pictures go to
  artifacts\snapshot-matrix\<time>\ for review.
#>
param([string]$Exe, [switch]$Quick)

$ErrorActionPreference = "Stop"
$windows = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $env:LOCALAPPDATA "dotnet"
if (Test-Path (Join-Path $localDotnet "dotnet.exe")) { $env:DOTNET_ROOT = $localDotnet }
if (-not $Exe) { $Exe = Get-ChildItem (Join-Path $windows "src\Alondra1.App\bin\Debug") -Recurse -Filter Alondra1.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName }
$fixtures = Join-Path $windows "tests\Alondra1.Tests\Fixtures\Mail"
$out = Join-Path $windows ("artifacts\snapshot-matrix\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force $out | Out-Null

$sizes = @(@(1280, 800), @(1600, 900), @(2048, 1080), @(800, 900))
$states = [ordered]@{
    "inbox"    = @("--open=Mail")
    "plain"    = @("--open-eml=$(Join-Path $fixtures 'M01-plain-long-url.eml')")
    "rich"     = @("--open-eml=$(Join-Path $fixtures 'M03-newsletter.eml')")
    "blocked"  = @("--open-eml=$(Join-Path $fixtures 'M05-remote-images.eml')")
    "nomatch"  = @("--open=Mail", "--search=zzzz-no-such-message")
    "settings" = @("--open=Mail", "--dialog=settings")
}
$runs = @()
foreach ($theme in "dark", "light") {
    foreach ($size in $sizes) {
        foreach ($state in $states.Keys) {
            if ($Quick -and $state -notin "inbox", "rich") { continue }
            $runs += @{ Theme = $theme; Width = $size[0]; Height = $size[1]; Scale = "1"; State = $state }
        }
    }
    foreach ($scale in "1.25", "1.5", "2") {
        if ($Quick -and $scale -ne "1.25") { continue }
        $runs += @{ Theme = $theme; Width = 1600; Height = 900; Scale = $scale; State = "inbox" }
        $runs += @{ Theme = $theme; Width = 1280; Height = 800; Scale = $scale; State = "rich" }
    }
}

$failures = @()
foreach ($run in $runs) {
    $name = "{0}-{1}x{2}-{3}-s{4}" -f $run.State, $run.Width, $run.Height, $run.Theme, ($run.Scale -replace "\.", "")
    $png = Join-Path $out "$name.png"
    $arguments = @("--demo", "--theme=$($run.Theme)", "--width=$($run.Width)", "--height=$($run.Height)", "--scale=$($run.Scale)", "--snapshot=$png", "--exit") + $states[$run.State]
    $process = Start-Process -FilePath $Exe -ArgumentList ($arguments | ForEach-Object { if ($_ -match " ") { "`"$_`"" } else { $_ } }) -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); $failures += "${name}: timed out"; continue }
    if (-not (Test-Path "$png")) { $failures += "${name}: no snapshot (exit $($process.ExitCode))"; continue }
    if ($run.State -eq "settings") { Write-Host "ok   $name (dialog)"; continue }
    $report = Get-Content "$png.txt" -Encoding UTF8
    $bad = $report | Where-Object { $_ -like "FAIL *" }
    if ($bad) { $failures += ($bad | ForEach-Object { "${name}: $_" }); Write-Host "FAIL $name"; $bad | ForEach-Object { Write-Host "     $_" } }
    else { Write-Host "ok   $name ($(($report | Where-Object { $_ -like 'single=*' }) -replace 'single=', 'panes: '))" }
}

$failures | Set-Content (Join-Path $out "failures.txt") -Encoding utf8
if ($failures.Count) { Write-Host "$($failures.Count) problem(s); see $out"; exit 1 }
Write-Host "All $($runs.Count) snapshots passed the geometry checks; pictures in $out"
