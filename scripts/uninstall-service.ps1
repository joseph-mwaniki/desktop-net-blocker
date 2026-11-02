<#
.SYNOPSIS
    Uninstalls Desktop Net Blocker, restores network adapter DNS settings, removes firewall rules, and cleans hosts file.
.DESCRIPTION
    Must be executed with elevated Administrator privileges on Windows 11.
#>

[CmdletBinding()]
param (
    [string]$ServiceName = "DesktopNetBlockerService",
    [string]$InstallDir = "C:\Program Files\DesktopNetBlocker",
    [switch]$PurgeData
)

# 1. Require Administrator Privileges
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsPrincipal]::Administrator)) {
    Write-Error "ERROR: This script must be run as an Administrator! Right-click and select 'Run as Administrator'."
    exit 1
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Desktop Net Blocker - Uninstallation & Network Recovery" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 2. Stop and Delete Windows Service
Write-Host "`n[1/5] Stopping and removing background Windows service..." -ForegroundColor Yellow
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq 'Running') {
        Stop-Service -Name $ServiceName -Force
    }
    & sc.exe delete $ServiceName
    Start-Sleep -Seconds 2
    Write-Host "Windows service removed." -ForegroundColor Green
} else {
    Write-Host "Service $ServiceName was not registered."
}

# 3. Restore Network Adapters DNS Settings to DHCP
Write-Host "`n[2/5] Restoring network adapters DNS to automatic DHCP..." -ForegroundColor Yellow
try {
    Get-NetAdapter | Where-Object { $_.Virtual -ne $true } | ForEach-Object {
        Write-Host "Resetting adapter: $($_.Name)"
        Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ResetServerAddresses -ErrorAction SilentlyContinue
    }
    Write-Host "Network DNS settings restored." -ForegroundColor Green
} catch {
    Write-Warning "Could not reset all adapter DNS settings automatically: $($_.Exception.Message)"
}

# 4. Remove Windows Firewall Anti-Bypass Rules
Write-Host "`n[3/5] Cleaning Windows Firewall rules..." -ForegroundColor Yellow
& netsh advfirewall firewall delete rule name="DesktopNetBlocker-BlockOutboundDNS" | Out-Null
& netsh advfirewall firewall delete rule name="DesktopNetBlocker-BlockOutboundDNS-TCP" | Out-Null
& netsh advfirewall firewall delete rule name="DesktopNetBlocker-BlockDoT" | Out-Null
& netsh advfirewall firewall delete rule name="DesktopNetBlocker-BlockDoH" | Out-Null
Write-Host "Firewall rules removed." -ForegroundColor Green

# 5. Clean Managed Section from Hosts File
Write-Host "`n[4/5] Cleaning managed entries from hosts file..." -ForegroundColor Yellow
$hostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
if (Test-Path $hostsPath) {
    try {
        $content = Get-Content $hostsPath -Raw
        $beginMarker = "# BEGIN DESKTOP_NET_BLOCKER"
        $endMarker = "# END DESKTOP_NET_BLOCKER"

        $beginIdx = $content.IndexOf($beginMarker)
        $endIdx = $content.IndexOf($endMarker)

        if ($beginIdx -ge 0 -and $endIdx -ge 0 -and $endIdx -gt $beginIdx) {
            $prefix = $content.Substring(0, $beginIdx).TrimEnd()
            $suffix = $content.Substring($endIdx + $endMarker.Length).TrimStart()
            $clean = $prefix + $(if ($suffix) { "`r`n" + $suffix } else { "" })
            Set-Content -Path $hostsPath -Value $clean -Force
            Write-Host "Cleaned managed block from $hostsPath." -ForegroundColor Green
        }
    } catch {
        Write-Warning "Could not update hosts file automatically: $($_.Exception.Message)"
    }
}

# 6. Remove Desktop Shortcuts & Binaries
Write-Host "`n[5/5] Removing application files and shortcuts..." -ForegroundColor Yellow
$desktopDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory)
$shortcutPath = Join-Path $desktopDir "Desktop Net Blocker.lnk"
if (Test-Path $shortcutPath) {
    Remove-Item -Path $shortcutPath -Force
}

if (Test-Path $InstallDir) {
    Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($PurgeData) {
    $programData = Join-Path $env:ProgramData "DesktopNetBlocker"
    if (Test-Path $programData) {
        Remove-Item -Path $programData -Recurse -Force
        Write-Host "Purged application database." -ForegroundColor Yellow
    }
} else {
    Write-Host "Application database preserved at: $env:ProgramData\DesktopNetBlocker (Pass -PurgeData to delete)" -ForegroundColor Gray
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " UNINSTALLATION AND NETWORK RESTORATION COMPLETE!" -ForegroundColor Green
Write-Host " All network settings have been restored to their defaults." -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
