# Creates the local runtime secret files that compose.yaml mounts (secrets/ is ignored by git).
# Existing files are kept unless -Force is given, so a running database keeps its password.
#   powershell -ExecutionPolicy Bypass -File scripts/new-dev-secrets.ps1
param([switch]$Force)
$ErrorActionPreference = "Stop"
$dir = Join-Path (Split-Path -Parent $PSScriptRoot) "secrets"
New-Item -ItemType Directory -Force $dir | Out-Null

function New-Secret([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    # URL-safe base64 without padding: no quotes, spaces, or newlines to trip up readers.
    return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$files = [ordered]@{
    "postgres_password" = { New-Secret 32 }
    "token_key"         = { New-Secret 48 }   # signs access tokens
    "otp_key"           = { New-Secret 48 }   # HMAC key for one-time codes and refresh tokens
    "smtp_password"     = { "" }               # fill in for a real SMTP relay; empty for mailpit
    "token_key_previous" = { "" }              # empty unless rotating the token key (docs/runbooks/production.md)
    # age PUBLIC key(s) for encrypted backups. Generate the pair OFFLINE with `age-keygen -o backup-identity.txt`,
    # keep that identity file away from this server, and put its "age1..." public key here.
    "backup_recipients" = { "# Replace with your age public key (age1...). See docs/runbooks/backup-restore.md." }
}
foreach ($name in $files.Keys) {
    $path = Join-Path $dir $name
    if ((Test-Path $path) -and -not $Force) { Write-Host "kept     secrets/$name"; continue }
    [System.IO.File]::WriteAllText($path, (& $files[$name]))
    Write-Host "created  secrets/$name"
}
