<#
.SYNOPSIS
    Installs and configures the Desktop Net Blocker Background Service and Desktop Application.
.DESCRIPTION
    Must be executed with elevated Administrator privileges on Windows 11.
#>

[CmdletBinding()]
param (
    [string]$InstallDir = "C:\Program Files\DesktopNetBlocker",
    [string]$ServiceName = "DesktopNetBlockerService",
    [switch]$SkipBuild
)

# 1. Require Administrator Privileges
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsPrincipal]::Administrator)) {
    Write-Error "ERROR: This script must be run as an Administrator! Right-click and select 'Run as Administrator'."
    exit 1
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Desktop Net Blocker - Native Windows 11 Service Installer" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 2. Build / Publish binaries if not skipped
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$solutionDir = Split-Path -Parent $scriptDir

if (-not $SkipBuild) {
    Write-Host "`n[1/6] Building and publishing application components..." -ForegroundColor Yellow
    
    $publishServiceDir = Join-Path $solutionDir "publish\Service"
    $publishUiDir = Join-Path $solutionDir "publish\UI"
    
    & dotnet publish "$solutionDir\src\DesktopNetBlocker.Service\DesktopNetBlocker.Service.csproj" -c Release -o $publishServiceDir --self-contained false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish DesktopNetBlocker.Service."
        exit 1
    }

    & dotnet publish "$solutionDir\src\DesktopNetBlocker.UI\DesktopNetBlocker.UI.csproj" -c Release -o $publishUiDir --self-contained false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish DesktopNetBlocker.UI."
        exit 1
    }
}

# 3. Create Installation Directory
Write-Host "`n[2/6] Copying binaries to $InstallDir..." -ForegroundColor Yellow
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

$publishServiceDir = Join-Path $solutionDir "publish\Service"
$publishUiDir = Join-Path $solutionDir "publish\UI"

if (Test-Path $publishServiceDir) {
    Copy-Item -Path "$publishServiceDir\*" -Destination $InstallDir -Recurse -Force
}
if (Test-Path $publishUiDir) {
    Copy-Item -Path "$publishUiDir\*" -Destination $InstallDir -Recurse -Force
}

# 4. Stop and Remove existing service if already installed
Write-Host "`n[3/6] Checking for existing Windows Service..." -ForegroundColor Yellow
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Stopping and removing previous service instance..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName
    Start-Sleep -Seconds 2
}

# 5. Register and Configure Windows Background Service
Write-Host "`n[4/6] Registering $ServiceName as an Automatic Windows Service..." -ForegroundColor Yellow
$serviceExePath = Join-Path $InstallDir "DesktopNetBlocker.Service.exe"

& sc.exe create $ServiceName binPath= "`"$serviceExePath`"" start= auto DisplayName= "Desktop Net Blocker Enforcement Service"
& sc.exe description $ServiceName "Maintains system-wide delayed impulse control and network DNS filtering restrictions."

# Set auto-recovery on failure: restart after 5s, 10s, 60s
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/60000

# 6. Configure Windows Firewall Anti-Bypass Rules
Write-Host "`n[5/6] Enforcing Windows Firewall anti-bypass rules..." -ForegroundColor Yellow
& netsh advfirewall firewall add rule name="DesktopNetBlocker-BlockOutboundDNS" dir=out action=block protocol=UDP remoteport=53 program="!$serviceExePath"
& netsh advfirewall firewall add rule name="DesktopNetBlocker-BlockOutboundDNS-TCP" dir=out action=block protocol=TCP remoteport=53 program="!$serviceExePath"
& netsh advfirewall firewall add rule name="DesktopNetBlocker-BlockDoT" dir=out action=block protocol=TCP remoteport=853
& netsh advfirewall firewall add rule name="DesktopNetBlocker-BlockDoH" dir=out action=block protocol=TCP remoteport=443 remoteip="1.1.1.1,1.0.0.1,8.8.8.8,8.8.4.4,9.9.9.9,149.112.112.112"

# 7. Start Service & Create Shortcuts
Write-Host "`n[6/6] Starting background service and creating desktop shortcut..." -ForegroundColor Yellow
Start-Service -Name $ServiceName

$uiExePath = Join-Path $InstallDir "DesktopNetBlocker.UI.exe"
$desktopDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
$shortcutPath = Join-Path $desktopDir "Desktop Net Blocker.lnk"

$wscript = New-Object -ComObject WScript.Shell
$shortcut = $wscript.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $uiExePath
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = "Desktop Net Blocker - Impulse Control & Network Restriction"
$shortcut.Save()

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " INSTALLATION COMPLETED SUCCESSFULLY!" -ForegroundColor Green
Write-Host " - Background Service: Running & set to Start Automatically" -ForegroundColor Green
Write-Host " - Desktop Application: $shortcutPath" -ForegroundColor Green
Write-Host " - Database Location: %ProgramData%\DesktopNetBlocker\blocker.db" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
