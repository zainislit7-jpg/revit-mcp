# Builds and installs the Revit 2027 MCP add-in and bridge for the current user.
# Usage (from the repo root, in PowerShell):  ./install.ps1
# Re-run after pulling changes. Close Revit first so the add-in DLL is not locked.
$ErrorActionPreference = 'Stop'

$addinDir  = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027'
$addinBin  = Join-Path $addinDir 'ClaudeMcp'
$bridgeBin = Join-Path $env:LOCALAPPDATA 'ClaudeMcp\revit\bridge'
$configDir = Join-Path $env:APPDATA 'ClaudeMcp\revit'

dotnet publish src/RevitMcp.Addin  -c Release -o $addinBin
dotnet publish src/RevitMcp.Bridge -c Release -o $bridgeBin

# Manifest must name the DLL by absolute path.
$manifest = Get-Content src/RevitMcp.Addin/RevitMcp.addin -Raw
$manifest = $manifest -replace '<Assembly>.*</Assembly>', "<Assembly>$addinBin\RevitMcp.Addin.dll</Assembly>"
New-Item -ItemType Directory -Force $addinDir | Out-Null
Set-Content -Path (Join-Path $addinDir 'ClaudeMcp.addin') -Value $manifest -Encoding UTF8

# Read-only by default; only create the config if there isn't one.
New-Item -ItemType Directory -Force $configDir | Out-Null
$config = Join-Path $configDir 'config.json'
if (-not (Test-Path $config)) {
    Set-Content -Path $config -Value '{ "allowWrite": false, "allowScripts": false }' -Encoding UTF8
}

$bridgeDll = Join-Path $bridgeBin 'RevitMcp.Bridge.dll'
Write-Host ""
Write-Host "Installed. Start Revit 2027 and allow the 'Claude MCP' add-in when asked."
Write-Host "Register the server with Claude Code:"
Write-Host "  claude mcp add revit --scope user -- dotnet `"$bridgeDll`""
Write-Host "Settings: $config"
