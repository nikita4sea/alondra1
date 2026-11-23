<#
.SYNOPSIS
  Milestone completion check: uninstall Alondra1, install a fresh build, import the test setup, and check it.

.DESCRIPTION
  Runs the steps in the client spec (section 8.2) and stops at the first failure:
    1. close the app            2. back up %LOCALAPPDATA%\alondra1 (the last 3 backups are kept)
    3. uninstall, then delete the data folder (skipped with -KeepData)
    4. build the installer      5. install silently and check the install
    6. regenerate and import the test configuration and passwords (skipped with -KeepData)
    7. sync both test accounts  8. send and receive between them, restart, snapshot every module
  The summary goes to apps\windows\artifacts\reinstall\<time>\result.txt together with the snapshots.
  Passwords are read from the -PasswordFile file by the app itself and never printed.

.EXAMPLE
  ./scripts/reinstall-local.ps1
  ./scripts/reinstall-local.ps1 -KeepData      # upgrade check: keep data and settings
#>
param(
    [string]$TestConfig,
    [string]$PasswordFile,
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
$windows = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $windows)
if (-not $TestConfig) { $TestConfig = Join-Path $repo ".import\alondra1-test-config.json" }
if (-not $PasswordFile) { $PasswordFile = Join-Path $repo ".test" }

$data = Join-Path $env:LOCALAPPDATA "alondra1"
$defaultProgramDir = Join-Path $env:LOCALAPPDATA "Programs\Alondra1"
$shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Alondra1.lnk"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F4C2A91-6B1E-4D77-9C3A-1E5B7A0D4F22}_is1"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $windows "artifacts\reinstall\$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
$result = Join-Path $out "result.txt"

# The installed copy is found through its uninstall entry (same AppId), so installs in an older folder are found too.
function Get-InstallDir {
    $entry = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
    if ($entry) { return $entry."Inno Setup: App Path" }
    return $null
}
$exe = Join-Path $defaultProgramDir "Alondra1.exe"

function Note([string]$text) {
    $line = "$(Get-Date -Format 'HH:mm:ss') $text"
    Write-Host $line
    Add-Content -LiteralPath $result -Value $line -Encoding utf8
}

function Fail([string]$text) {
    Note "FAILED: $text"
    throw $text
}

function Invoke-App([string[]]$arguments, [int]$timeoutSeconds = 300) {
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
        $process.Kill()
        Fail "Alondra1 $($arguments -join ' ') did not finish in $timeoutSeconds s"
    }
    return $process.ExitCode
}

# Builds the test configuration from the test-account file: the accounts only, everything else at the app's
# current defaults. No passwords are written.
function New-TestConfig([string]$source, [string]$target) {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $source) {
        $parts = $line.Split("=", 2)
        if ($parts.Length -eq 2 -and $parts[0].Trim()) { $values[$parts[0].Trim().ToUpperInvariant()] = $parts[1].Trim() }
    }
    function Server([string]$key) {
        $value = $values[$key]
        $colon = $value.LastIndexOf(":")
        return @{ host = $value.Substring(0, $colon); port = [int]$value.Substring($colon + 1) }
    }
    $smtp = Server "SMTP"
    $accounts = @()
    $plan = @(
        @{ user = 1; protocol = "imap"; key = "IMAP"; id = "test-imap-1"; name = "Dev (IMAP)" },
        @{ user = 2; protocol = "pop3"; key = "POP3"; id = "test-pop3-2"; name = "User (POP3)" }
    )
    foreach ($item in $plan) {
        $address = $values["USER$($item.user)"]
        if (-not $address) { continue }
        $incoming = Server $item.key
        $accounts += [ordered]@{
            id = $item.id; kind = "manual"; mode = "online"; displayName = $item.name; address = $address
            incoming = [ordered]@{ protocol = $item.protocol; host = $incoming.host; port = $incoming.port; security = "implicitTls"; username = $address; authentication = "password" }
            outgoing = [ordered]@{ host = $smtp.host; port = $smtp.port; security = "implicitTls"; username = $address; authentication = "password" }
        }
    }
    $config = [ordered]@{ schemaVersion = 1; accounts = $accounts }
    $config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $target -Encoding utf8
    return $accounts.Count
}

Note "Alondra1 reinstall check ($(if ($KeepData) { 'upgrade, data kept' } else { 'clean install' }))"

# 1. Close the app.
Get-Process Alondra1 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Note "1. app closed"

# 2. Back up the data folder; keep the last three backups.
if (Test-Path $data) {
    $backup = "$data-backup-$stamp"
    Copy-Item -LiteralPath $data -Destination $backup -Recurse
    Get-ChildItem (Split-Path $data) -Directory -Filter "alondra1-backup-*" | Sort-Object Name -Descending | Select-Object -Skip 3 |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    Note "2. data backed up to $backup"
} else {
    Note "2. no data folder to back up"
}

# 3. Uninstall, then remove the data folder so the install starts clean.
$installed = Get-InstallDir
if ($installed -and (Test-Path (Join-Path $installed "unins000.exe"))) {
    $p = Start-Process -FilePath (Join-Path $installed "unins000.exe") -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru -Wait
    # The uninstaller copies itself to a temporary folder and finishes there; wait for its files to go.
    $installedExe = Join-Path $installed "Alondra1.exe"
    for ($i = 0; $i -lt 60 -and ((Test-Path $installedExe) -or (Test-Path $uninstallKey)); $i++) { Start-Sleep -Milliseconds 500 }
    if (Test-Path $installedExe) { Fail "uninstall left $installedExe" }
    if (Test-Path $shortcut) { Fail "uninstall left the Start menu shortcut" }
    if (Test-Path $uninstallKey) { Fail "uninstall left the Settings > Apps entry" }
    # Files from releases before the rename (for example Alondra.exe) are not in the current uninstall log.
    if (Test-Path $installed) {
        Remove-Item -LiteralPath $installed -Recurse -Force
        Note "3. removed leftover files in $installed"
    }
    Note "3. uninstalled from $installed (program files, shortcut, and Apps entry removed)"
} else {
    Note "3. not installed; nothing to uninstall"
}
if (-not $KeepData -and (Test-Path $data)) {
    Remove-Item -LiteralPath $data -Recurse -Force
    Note "3. data folder removed"
}

# 4. Build the installer with the local .NET SDK when there is one.
$localDotnet = Join-Path $env:LOCALAPPDATA "dotnet"
if (Test-Path (Join-Path $localDotnet "dotnet.exe")) {
    $env:DOTNET_ROOT = $localDotnet
    $env:PATH = "$localDotnet;$env:PATH"
}
& (Join-Path $windows "installer\publish.ps1")
if ($LASTEXITCODE -ne 0) { Fail "publish.ps1 failed ($LASTEXITCODE)" }
$setup = Join-Path $windows "artifacts\Alondra1-setup.exe"
if (-not (Test-Path $setup)) { Fail "no installer at $setup" }
Note "4. installer built: $((Get-Item $setup).Length / 1MB -as [int]) MB"

# 5. Install silently and check the result.
$p = Start-Process -FilePath $setup -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -PassThru -Wait
if ($p.ExitCode -ne 0) { Fail "setup exited with $($p.ExitCode)" }
$installed = Get-InstallDir
if (-not $installed) { Fail "setup did not register in Settings > Apps" }
$exe = Join-Path $installed "Alondra1.exe"
if (-not (Test-Path $exe)) { Fail "setup did not install $exe" }
if (-not $KeepData -and $installed.TrimEnd("\") -ne $defaultProgramDir) { Fail "a clean install went to $installed instead of $defaultProgramDir" }
if (-not (Test-Path $shortcut)) { Fail "setup did not create the Start menu shortcut" }
if (-not (Test-Path $uninstallKey)) { Fail "setup did not register in Settings > Apps" }
$version = (Get-Item $exe).VersionInfo.ProductVersion
$expected = ([xml](Get-Content (Join-Path $windows "src\Alondra1.App\Alondra1.App.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version.StartsWith($expected)) { Fail "installed version $version does not match the build ($expected)" }
Note "5. installed $version"

# 6. Import the test configuration and passwords.
if (-not $KeepData) {
    if (-not (Test-Path $PasswordFile)) { Fail "test-account file not found: $PasswordFile" }
    $count = New-TestConfig $PasswordFile $TestConfig
    Note "6. test configuration regenerated ($count accounts)"
    $code = Invoke-App @("--import-config=`"$TestConfig`"", "--import-passwords=`"$PasswordFile`"", "--exit") 120
    if ($code -ne 0) { Fail "import exited with $code; see $data\logs\setup.log" }
    Note "6. configuration and passwords imported"
} else {
    Note "6. import skipped (-KeepData)"
}

# 7. First run: sync both accounts.
$code = Invoke-App @("--sync", "--open=Mail", "--snapshot=`"$out\mail-synced.png`"", "--exit")
$syncLine = Get-Content (Join-Path $data "logs\check.log") -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -match " sync: " } | Select-Object -Last 1
if ($code -ne 0) { Fail "sync failed ($code): $syncLine" }
$config = Get-Content (Join-Path $data "config.json") -Raw | ConvertFrom-Json
foreach ($account in $config.accounts | Where-Object { $_.incoming.protocol -eq "imap" }) {
    if (-not $account.folders.inbox) { Fail "$($account.displayName): folders were not learned" }
}
Note "7. $($syncLine -replace '^\S+ ', '')"

# 8. Checks on the installed copy.
$code = Invoke-App @("--send-check", "--exit") 240
$sendLine = Get-Content (Join-Path $data "logs\check.log") -Encoding UTF8 | Where-Object { $_ -match "send-check" } | Select-Object -Last 1
if ($code -ne 0) { Fail "send and receive failed: $sendLine" }
Note "8. $($sendLine -replace '^\S+ ', '')"

$first = $config.accounts[0].displayName
$null = Invoke-App @("--account=`"$first`"", "--open=Mail", "--snapshot=`"$out\mail-account.png`"", "--exit")
$null = Invoke-App @("--open=Mail", "--snapshot=`"$out\mail-restart.png`"", "--exit")
$restored = (Get-Content "$out\mail-restart.png.txt" | Where-Object { $_ -like "account=*" }) -replace "^account=", ""
if ($restored -ne $first) { Fail "after a restart Mail showed '$restored' instead of '$first'" }
Note "8. restart kept the selected account ($first)"
$null = Invoke-App @("--account=All accounts", "--exit")

foreach ($module in "Mail", "Calendar", "To-do", "Notes", "Contacts") {
    $file = Join-Path $out ("module-" + ($module -replace "[^A-Za-z]", "") + ".png")
    $code = Invoke-App @("--open=$module", "--snapshot=`"$file`"", "--exit")
    if ($code -ne 0 -or -not (Test-Path $file)) { Fail "snapshot of $module failed ($code)" }
}
Note "8. snapshots of Mail, Calendar, To-do, Notes, and Contacts in $out"

# The message reader on the installed copy: fixtures M01-M12 in the real WebView2 (scripts, requests, attachments).
& (Join-Path $PSScriptRoot "check-reader.ps1") -Exe $exe 6>&1 | ForEach-Object { Add-Content -LiteralPath $result -Value "   $_" -Encoding utf8 }
if ($LASTEXITCODE -ne 0) { Fail "reader check failed on the installed copy" }
Note "8. reader check passed on the installed copy (fixtures M01-M12)"
Note "PASSED"
