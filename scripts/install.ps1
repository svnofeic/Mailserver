<#
.SYNOPSIS
    Installs or updates the mail server as a Windows service. Run in an elevated PowerShell on the server.
.DESCRIPTION
    - copies the package to the install directory (an existing appsettings.json and the data folder are kept)
    - registers the "Mailserver" service with automatic start and restart on failure
    - opens the firewall for SMTP (25), submission (587), SMTPS (465), IMAP (143), IMAPS (993) and the web interface (9443)
    - restricts the data folder to SYSTEM and Administrators
.EXAMPLE
    .\install.ps1                              # from the unpacked package folder
.EXAMPLE
    .\install.ps1 -Package C:\Temp\publish
#>
#Requires -RunAsAdministrator
param(
    # Defaults to the folder this script is in (it ships inside the package).
    [string]$Package = $PSScriptRoot,
    [string]$InstallDir = 'C:\Mailserver',
    [string]$ServiceName = 'Mailserver'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $Package 'Mailserver.exe'))) {
    throw "No Mailserver.exe in '$Package'. Run install.ps1 from the unpacked package folder or pass -Package <folder>."
}

# A server started by hand in a console window keeps its files locked.
$console = Get-Process -Name 'Mailserver' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) -and $_.SessionId -ne 0 }
if ($console) {
    throw "Mailserver.exe is running in a console window (PID $($console.Id -join ', ')). Stop it with Ctrl+C first."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasRunning = $service -and $service.Status -ne 'Stopped'
if ($wasRunning) {
    Write-Host "Stopping $ServiceName ..."
    Stop-Service -Name $ServiceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if ((Resolve-Path $Package).Path.TrimEnd('\') -eq (Resolve-Path $InstallDir).Path.TrimEnd('\')) {
    throw "The package folder is the install folder. Unpack the new package elsewhere (e.g. C:\Temp\mailserver) and run install.ps1 there."
}
$keepConfig = Test-Path (Join-Path $InstallDir 'appsettings.json')
Get-ChildItem -Path $Package | Where-Object { -not ($keepConfig -and $_.Name -eq 'appsettings.json') } |
    Copy-Item -Destination $InstallDir -Recurse -Force
if ($keepConfig) { Write-Host 'Existing appsettings.json kept. Compare it with the new one in the package for new settings.' }

$dataDir = Join-Path $InstallDir 'data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
icacls $dataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

if (-not $service) {
    New-Service -Name $ServiceName -DisplayName 'Mailserver (SMTP/IMAP/Web)' -StartupType Automatic `
        -BinaryPathName "`"$(Join-Path $InstallDir 'Mailserver.exe')`"" `
        -Description 'Eigener Mailserver: SMTP, IMAP und Weboberfläche.' | Out-Null
    # Restart after 1 minute on crashes; reset the failure counter after one day.
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
}

foreach ($rule in @(
        @{ Name = 'Mailserver SMTP 25'; Port = 25 },
        @{ Name = 'Mailserver Submission 587'; Port = 587 },
        @{ Name = 'Mailserver SMTPS 465'; Port = 465 },
        @{ Name = 'Mailserver IMAP 143'; Port = 143 },
        @{ Name = 'Mailserver IMAPS 993'; Port = 993 },
        @{ Name = 'Mailserver Web 9443'; Port = 9443 })) {
    if (-not (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol TCP -LocalPort $rule.Port -Action Allow | Out-Null
    }
}

Write-Host "Installed to $InstallDir."
if ($wasRunning) {
    Start-Service -Name $ServiceName
    Write-Host "Updated; $ServiceName was running and has been started again."
} else {
    Write-Host "Next: edit $InstallDir\appsettings.json (Hostname, Tls), then: Start-Service $ServiceName"
}
Write-Host "Web interface: https://<hostname>:9443  (grant admin rights first: mailadmin user admin <address> on)"
