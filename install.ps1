<#
================================================================================
  Orion Copy Watchdog - one-shot install for a WINDOWS host.

      Right-click PowerShell -> Run as administrator
      cd <this folder>
      Copy-Item watchdog.example.yml watchdog.yml ; notepad watchdog.yml
      .\install.ps1

  Idempotent: re-running reapplies config without destroying watchdog.yml.
  Existing watchdog.yml is never overwritten (a .bak is kept if migration is
  ever needed). Flags:

      -NoBuild      skip dotnet publish (re-apply config + restart service)
      -Status       show service + monitored state and exit
      -Uninstall    stop + remove the Windows service (leaves data + config)

  Prerequisites: .NET 8 SDK (installed automatically via winget when missing).
================================================================================
#>
[CmdletBinding()]
param(
    [switch] $NoBuild,
    [switch] $Status,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Ok  ($m) { Write-Host "[ok]   $m" -ForegroundColor Green }
function Inf ($m) { Write-Host "[info] $m" -ForegroundColor Cyan }
function Wrn ($m) { Write-Host "[warn] $m" -ForegroundColor Yellow }
function Die ($m) { Write-Host "[err]  $m" -ForegroundColor Red; exit 1 }

$ServiceName = 'OrionCopyWatchdog'
$InstallDir  = Join-Path $env:ProgramFiles 'OrionCopyWatchdog'
$BinDir      = Join-Path $InstallDir 'bin'
$ExePath     = Join-Path $BinDir 'orion-watchdog.exe'
$ConfigDir   = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'OrionCopyWatchdog'
$ConfigPath  = Join-Path $ConfigDir 'watchdog.yml'

# -- admin check ---------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Die "Run this from an ELEVATED PowerShell (Run as administrator)." }

if ($Status) {
    Inf "windows service:"
    Get-Service -Name $ServiceName -ErrorAction SilentlyContinue |
        Select-Object Name,Status,StartType | Format-Table -AutoSize
    if (Test-Path $ExePath) {
        Inf "monitored state:"
        & $ExePath --config $ConfigPath status
    } else {
        Wrn "not installed (no binary at $ExePath)"
    }
    exit 0
}

if ($Uninstall) {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -ne $null) {
        if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue }
        & sc.exe delete $ServiceName | Out-Null
        Ok "service removed (config + data left in $ConfigDir and $InstallDir)"
    } else {
        Inf "service not installed; nothing to remove"
    }
    exit 0
}

# -- prerequisites (.NET 8 SDK to build; runtime is enough to run) ------------
$dotnetOk = $false
try {
    $sdks = & dotnet --list-sdks 2>$null
    if ($sdks -match '8\.') { $dotnetOk = $true }
} catch { $dotnetOk = $false }

if (-not $dotnetOk -and -not $NoBuild) {
    Inf ".NET 8 SDK not found; installing via winget..."
    try {
        & winget install --silent --accept-source-agreements --accept-package-agreements Microsoft.DotNet.SDK.8 2>$null
        $env:PATH = [Environment]::GetEnvironmentVariable('PATH','Machine') + ';' +
                    [Environment]::GetEnvironmentVariable('PATH','User')
        $sdks = & dotnet --list-sdks 2>$null
        if ($sdks -match '8\.') { $dotnetOk = $true }
    } catch { $dotnetOk = $false }
    if (-not $dotnetOk) {
        Die ("Could not install .NET 8 SDK automatically. Install it from " +
             "https://dotnet.microsoft.com/download/dotnet/8.0 and re-run .\install.ps1")
    }
}

# -- build / publish ------------------------------------------------------------
if (-not $NoBuild) {
    Inf "publishing orion-watchdog (Release)..."
    & dotnet publish src/OrionCopy.Watchdog/OrionCopy.Watchdog.csproj -c Release -o $BinDir
    if ($LASTEXITCODE -ne 0) { Die "dotnet publish failed." }
    Ok "published to $BinDir"
} elseif (-not (Test-Path $ExePath)) {
    Die "No binary at $ExePath and -NoBuild was given. Run without -NoBuild first."
}

# -- configuration (never overwrite an existing watchdog.yml) ------------------
New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
$srcExample = Join-Path $PSScriptRoot 'watchdog.example.yml'
if (-not (Test-Path $ConfigPath)) {
    Copy-Item -LiteralPath $srcExample -Destination $ConfigPath
    Ok "created $ConfigPath -- EDIT IT for your servers, then re-run .\install.ps1"
} else {
    Inf "keeping existing $ConfigPath"
}

# Tighten permissions: SYSTEM + Administrators only.
try {
    $acl = Get-Acl -LiteralPath $ConfigPath
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRule($rule) | Out-Null }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        'SYSTEM','FullControl','Allow')))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        'Administrators','FullControl','Allow')))
    Set-Acl -LiteralPath $ConfigPath -AclObject $acl
    Ok "locked down $ConfigPath (SYSTEM + Administrators only)"
} catch {
    Wrn "could not tighten permissions on $ConfigPath : $($_.Exception.Message)"
}

# -- validate before installing -------------------------------------------------
Inf "validating configuration..."
& $ExePath --config $ConfigPath validate-config
if ($LASTEXITCODE -ne 0) {
    Die ("Fix the errors above in $ConfigPath (see watchdog.example.yml + examples\), " +
         "then re-run .\install.ps1")
}

# -- install as a Windows Service ----------------------------------------------
$binPath = "`"$ExePath`" --config `"$ConfigPath`" start"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -eq $null) {
    & sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "Orion Copy Watchdog" | Out-Null
    Ok "created Windows service '$ServiceName'"
} else {
    & sc.exe config $ServiceName binPath= $binPath start= auto | Out-Null
    Inf "updated existing Windows service '$ServiceName'"
}
# Restart on failure: 3 restarts (1m apart), reset counter daily. systemd keeps
# the watchdog alive on Linux; this is the Windows equivalent -- the watchdog
# itself then keeps your Orion services healthy.
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

Restart-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq 'Running') { Ok "service '$ServiceName' is running" }
else { Die "service installed but not running. Check Event Viewer + logs." }

Inf "next steps:"
Inf "  - status:  & `"$ExePath`" --config `"$ConfigPath`" status"
Inf "  - logs:    $ConfigDir\logs  (or the log_file path in watchdog.yml)"
Inf "  - edit:    notepad `"$ConfigPath`"; then .\install.ps1 -NoBuild"
