<#
.SYNOPSIS
  Reader check in the real WebView2: renders the mail fixtures M01-M12 and checks what each page did.

.DESCRIPTION
  For every fixture, Alondra1 runs in demo mode with --open-eml, which shows the message through the normal reader
  pipeline and logs: whether HTML was used, blocked remote images, remote requests made, requests and navigations
  blocked, attachments listed, and whether any script changed the page title. Snapshots go to
  artifacts\reader-check\<time>\. The check fails when a script ran, when a remote request was made while images
  were blocked, or when an expected result is missing.

.PARAMETER Exe
  The Alondra1.exe to test (default: the Debug build; the reinstall check passes the installed copy).
#>
param([string]$Exe, [string]$Only)

$ErrorActionPreference = "Stop"
$windows = Split-Path -Parent $PSScriptRoot
# Debug builds need the .NET 10 runtime; use the per-user install when there is one.
$localDotnet = Join-Path $env:LOCALAPPDATA "dotnet"
if (Test-Path (Join-Path $localDotnet "dotnet.exe")) { $env:DOTNET_ROOT = $localDotnet }
if (-not $Exe) { $Exe = Get-ChildItem (Join-Path $windows "src\Alondra1.App\bin\Debug") -Recurse -Filter Alondra1.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName }
$fixtures = Join-Path $windows "tests\Alondra1.Tests\Fixtures\Mail"
$out = Join-Path $windows ("artifacts\reader-check\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force $out | Out-Null
$fixtureHome = Join-Path $env:TEMP "alondra1-fixtures"

# name, extra arguments, expectations (key = expected value in the log line)
$cases = @(
    @{ File = "M01-plain-long-url"; Expect = @{ html = "False" } },
    @{ File = "M02-alternative"; Expect = @{ html = "True" } },
    @{ File = "M03-newsletter"; Expect = @{ html = "True"; remoteRequests = "0" } },
    @{ File = "M04-cid-images"; Expect = @{ html = "True"; remoteRequests = "0"; attachments = "0" } },
    @{ File = "M05-remote-images"; Expect = @{ html = "True"; blockedImages = "3"; remoteRequests = "0" } },
    @{ File = "M05-remote-images"; Args = @("--load-images"); Name = "M05-remote-images-loaded"; Expect = @{ html = "True"; blockedImages = "0" } },
    @{ File = "M06-attachments"; Expect = @{ attachments = "2"; remoteRequests = "0" } },
    @{ File = "M07-quoted-printable-utf8"; Expect = @{ html = "True" } },
    @{ File = "M08-nested"; Expect = @{ html = "True"; attachments = "0" } },
    @{ File = "M09-overflow"; Expect = @{ html = "True" } },
    @{ File = "M10-malicious"; Expect = @{ html = "True"; remoteRequests = "0"; scriptRan = "False" } },
    @{ File = "M11-broken-remote"; Args = @("--load-images"); Name = "M11-broken-remote-loaded"; Expect = @{ html = "True" } },
    @{ File = "M12-malformed"; Expect = @{ html = "False" } }
)

$failures = 0
foreach ($case in $cases | Where-Object { -not $Only -or $_.File -like "*$Only*" }) {
    $name = if ($case.Name) { $case.Name } else { $case.File }
    $before = Get-ChildItem $fixtureHome -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
    $arguments = @("--demo", "--open-eml=$(Join-Path $fixtures ($case.File + '.eml'))", "--width=1400", "--height=900", "--snapshot=$(Join-Path $out ($name + '.png'))", "--exit") + @($case.Args | Where-Object { $_ })
    $process = Start-Process -FilePath $Exe -ArgumentList ($arguments | ForEach-Object { if ($_ -match " ") { "`"$_`"" } else { $_ } }) -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); Write-Host "FAIL ${name}: timed out"; $failures++; continue }
    $runHome = Get-ChildItem $fixtureHome -Directory | Where-Object { $before -notcontains $_.FullName } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $line = if ($runHome) { Get-Content (Join-Path $runHome.FullName "logs\check.log") -Encoding UTF8 -ErrorAction SilentlyContinue | Select-Object -Last 1 } else { $null }
    if (-not $line) { Write-Host "FAIL ${name}: no reader log (exit $($process.ExitCode))"; $failures++; continue }
    $values = @{}
    foreach ($match in [regex]::Matches($line, "(\w+)=(\S+)")) { $values[$match.Groups[1].Value] = $match.Groups[2].Value }
    $problems = @()
    if ($values["scriptRan"] -ne "False") { $problems += "a script ran" }
    if ($values["loaded"] -ne "True") { $problems += "page did not load" }
    foreach ($key in $case.Expect.Keys) {
        if ($values[$key] -ne $case.Expect[$key]) { $problems += "$key=$($values[$key]) (expected $($case.Expect[$key]))" }
    }
    if ($process.ExitCode -ne 0) { $problems += "exit code $($process.ExitCode)" }
    $summary = ($line -replace "^\S+ reader: ", "")
    Add-Content (Join-Path $out "result.txt") "$name : $summary" -Encoding utf8
    if ($problems.Count) { Write-Host "FAIL ${name}: $($problems -join '; ')"; $failures++ } else { Write-Host "ok   ${name}: $summary" }
    if ($runHome) { Remove-Item -LiteralPath $runHome.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failures) { Write-Host "$failures reader check(s) failed; snapshots in $out"; exit 1 }
Write-Host "All reader checks passed; snapshots in $out"
