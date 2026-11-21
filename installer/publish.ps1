$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $dotnet = Join-Path $env:LOCALAPPDATA "dotnet\dotnet.exe"
} else {
    $dotnet = $dotnet.Source
}
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Alondra.App\Alondra.App.csproj"
$out = Join-Path $root "artifacts\win-x64"
# Start clean so files from an older build are not packaged.
if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
& $dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 is required to compile Alondra-setup.exe." }
& $iscc (Join-Path $PSScriptRoot "alondra.iss")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Created $(Join-Path $root 'artifacts\Alondra-setup.exe')"
