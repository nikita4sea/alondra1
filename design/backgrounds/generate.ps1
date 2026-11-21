# Rebuilds Alondra's built-in backgrounds (original, procedurally drawn artwork) into the app's Assets folder.
# Windows PowerShell 5.1:  powershell -File design/backgrounds/generate.ps1
$ErrorActionPreference = "Stop"
Add-Type -Path (Join-Path $PSScriptRoot "Backgrounds.cs") -ReferencedAssemblies System.Drawing
$out = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "src\Alondra.App\Assets\Backgrounds"
[AlondraBackgrounds]::All($out)
Get-ChildItem $out | Select-Object Name, Length
