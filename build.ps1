# Builds K30.exe the same way DicTray publishes its Windows helpers:
# .NET 10, self-contained (no .NET install needed on the target machine), as a plain folder
# of files rather than PublishSingleFile — a single-file exe re-extracts its native libraries
# to a temp folder on every launch, which some antivirus (observed with Bitdefender) blocks
# right at boot, silently, with no crash and nothing in k30.log.
#
#   ./build.ps1                      # publish to ./publish
#   ./build.ps1 -Install             # publish, install to %LOCALAPPDATA%\Programs\K30Controller,
#                                    # register a Scheduled Task to start it at logon, and (re)start it
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
    -p:DebugType=None -p:DebugSymbols=false -o $Out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if (-not $Install) { return }

$dest = Join-Path $env:LOCALAPPDATA 'Programs\K30Controller'
New-Item -ItemType Directory -Force $dest | Out-Null
Get-Process K30 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Copy-Item (Join-Path $Out '*') $dest -Force   # k30-config.json in $dest is kept as-is

$exe = Join-Path $dest 'K30.exe'

# Started at logon via a Scheduled Task rather than the HKCU Run key: a Run-key app has to survive
# the full boot storm (every other autostart app, plus antivirus real-time scanning at its busiest)
# with no way to add a startup delay, and no time-limit worry. A logon task can wait a few seconds
# for that to settle, and never gets killed by the default 3-day scheduled-task execution limit.
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'K30Controller' -ErrorAction SilentlyContinue
$taskName = 'K30Controller'
$account = "$env:COMPUTERNAME\$env:USERNAME"
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $dest
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $account
$trigger.Delay = 'PT20S'
$principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Starts K30 Controller (Turing KeyDial K30 tray app) a few seconds after sign-in.' -ErrorAction Stop | Out-Null

Start-Process $exe -WorkingDirectory $dest
Write-Host "Installed to $dest and started. It will also start ~20s after you sign in (Task Scheduler: $taskName)."
