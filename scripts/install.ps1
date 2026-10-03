<#
.SYNOPSIS
    Installs or updates the mail server as a Windows service. Run in an elevated PowerShell on the server.
.DESCRIPTION
    - copies the package to the install directory (an existing appsettings.json and the data folder are kept)
    - registers the "Mailserver" service with automatic start and restart on failure
    - opens the firewall for SMTP (25), submission (587), SMTPS (465), IMAP (143) and IMAPS (993)
    - restricts the data folder to SYSTEM and Administrators
.EXAMPLE
    .\install.ps1 -Package C:\Temp\publish
#>
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)] [string]$Package,
    [string]$InstallDir = 'C:\Mailserver',
    [string]$ServiceName = 'Mailserver'
)

$ErrorActionPreference = 'Stop'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Write-Host "Stopping $ServiceName ..."
    Stop-Service -Name $ServiceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$keepConfig = Test-Path (Join-Path $InstallDir 'appsettings.json')
Get-ChildItem -Path $Package | Where-Object { -not ($keepConfig -and $_.Name -eq 'appsettings.json') } |
    Copy-Item -Destination $InstallDir -Recurse -Force
if ($keepConfig) { Write-Host 'Existing appsettings.json kept. Compare it with the new one in the package for new settings.' }

$dataDir = Join-Path $InstallDir 'data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
icacls $dataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

if (-not $service) {
    New-Service -Name $ServiceName -DisplayName 'Mailserver (SMTP/IMAP)' -StartupType Automatic `
        -BinaryPathName "`"$(Join-Path $InstallDir 'Mailserver.exe')`"" `
        -Description 'Eigener Mailserver: SMTP-Empfang, Submission, Zustellung und IMAP.' | Out-Null
    # Restart after 1 minute on crashes; reset the failure counter after one day.
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
}

foreach ($rule in @(
        @{ Name = 'Mailserver SMTP 25'; Port = 25 },
        @{ Name = 'Mailserver Submission 587'; Port = 587 },
        @{ Name = 'Mailserver SMTPS 465'; Port = 465 },
        @{ Name = 'Mailserver IMAP 143'; Port = 143 },
        @{ Name = 'Mailserver IMAPS 993'; Port = 993 })) {
    if (-not (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol TCP -LocalPort $rule.Port -Action Allow | Out-Null
    }
}

Write-Host "Installed to $InstallDir."
Write-Host "Next: edit $InstallDir\appsettings.json (Hostname, Tls), then: Start-Service $ServiceName"
