# Builds K30.exe the same way DicTray publishes its Windows helpers:
# .NET 10, self-contained, single file (no .NET install needed on the target machine).
#
#   ./build.ps1                      # publish to ./publish
#   ./build.ps1 -Install             # publish, then install to %LOCALAPPDATA%\Programs\K30Controller,
#                                    # register it to start with Windows, and (re)start it
#   ./build.ps1 -Dotnet <path>       # use a specific dotnet.exe (any .NET 10 SDK)
param(
    [string]$Out = (Join-Path $PSScriptRoot 'publish'),
    [string]$Dotnet = 'dotnet',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

& $Dotnet publish (Join-Path $PSScriptRoot 'src\K30.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $Out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if (-not $Install) { return }

$dest = Join-Path $env:LOCALAPPDATA 'Programs\K30Controller'
New-Item -ItemType Directory -Force $dest | Out-Null
Get-Process K30 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Copy-Item (Join-Path $Out '*') $dest -Force   # k30-config.json in $dest is kept as-is

$exe = Join-Path $dest 'K30.exe'
New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'K30Controller' `
    -Value "`"$exe`"" -PropertyType String -Force | Out-Null
Start-Process $exe -WorkingDirectory $dest
Write-Host "Installed to $dest and started. It will also start when you sign in."
