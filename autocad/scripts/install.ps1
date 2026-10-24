<#
.SYNOPSIS
  Builds the AutoCAD MCP add-in and bridge, installs the autoloader bundle, and registers the server with Claude Code.
.EXAMPLE
  pwsh scripts/install.ps1            # read-only install
  pwsh scripts/install.ps1 -AllowWrite -AllowScripts
#>
param(
    [switch]$AllowWrite,
    [switch]$AllowScripts,
    [switch]$SkipClaude
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\AcadMcp.bundle'
$contents = Join-Path $bundle 'Contents'
$bridgeDir = Join-Path $contents 'Bridge'

if (Get-Process acad -ErrorAction SilentlyContinue) {
    Write-Warning 'AutoCAD is running. Close it first so the add-in DLL can be replaced.'
    exit 1
}

dotnet publish "$root/src/AcadMcp.Addin" -c Release -o "$root/dist/addin" --nologo
dotnet publish "$root/src/AcadMcp.Bridge" -c Release -o "$root/dist/bridge" --nologo

New-Item -ItemType Directory -Force $contents, $bridgeDir | Out-Null
Copy-Item "$root/bundle/AcadMcp.bundle/PackageContents.xml" $bundle -Force
Copy-Item "$root/dist/addin/*" $contents -Recurse -Force
Copy-Item "$root/dist/bridge/*" $bridgeDir -Recurse -Force
Write-Host "Installed bundle to $bundle"

# Config lives next to the Revit server's: %APPDATA%\ClaudeMcp\acad\config.json
$cfgDir = Join-Path $env:APPDATA 'ClaudeMcp\acad'
$cfg = Join-Path $cfgDir 'config.json'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
if (-not (Test-Path $cfg) -or $AllowWrite -or $AllowScripts) {
    @{ allowWrite = [bool]$AllowWrite; allowScripts = [bool]$AllowScripts } | ConvertTo-Json | Set-Content $cfg -Encoding utf8
}
Write-Host "Config: $cfg"
Get-Content $cfg

$bridgeDll = Join-Path $bridgeDir 'AcadMcp.Bridge.dll'
if (-not $SkipClaude -and (Get-Command claude -ErrorAction SilentlyContinue)) {
    claude mcp remove autocad --scope user 2>$null | Out-Null
    claude mcp add autocad --scope user -- dotnet "$bridgeDll"
    Write-Host 'Registered "autocad" MCP server with Claude Code (user scope).'
} else {
    Write-Host "Register manually:  claude mcp add autocad --scope user -- dotnet `"$bridgeDll`""
}
Write-Host 'Start AutoCAD 2027; the command line should show "AcadMcp ... ready for Claude".'
