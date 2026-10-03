<#
.SYNOPSIS
    Downloads the newest package from GitHub and installs it (stops and restarts the service).
    Run in an elevated PowerShell on the server.
.EXAMPLE
    C:\Mailserver\update.ps1
.EXAMPLE
    C:\Mailserver\update.ps1 -Force      # reinstall even if the version is unchanged
#>
#Requires -RunAsAdministrator
param(
    [string]$Url = 'https://github.com/svnofeic/Mailserver/releases/download/latest/mailserver-win-x64.zip',
    [string]$InstallDir = 'C:\Mailserver',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the progress bar slows down downloads in Windows PowerShell 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$work = Join-Path $env:TEMP ("mailserver-update-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$package = Join-Path $work 'package'
New-Item -ItemType Directory -Force -Path $package | Out-Null
try {
    $zip = Join-Path $work 'mailserver-win-x64.zip'
    Write-Host "Lade $Url ..."
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
    Unblock-File $zip
    Expand-Archive -Path $zip -DestinationPath $package -Force

    $installed = Get-Content (Join-Path $InstallDir 'version.txt') -ErrorAction SilentlyContinue | Select-Object -First 1
    $available = Get-Content (Join-Path $package 'version.txt') -ErrorAction SilentlyContinue | Select-Object -First 1
    Write-Host "Installiert: $(if ($installed) { $installed } else { 'unbekannt' })"
    Write-Host "Verfügbar:   $available"
    if ($installed -and $installed -eq $available -and -not $Force) {
        Write-Host 'Bereits aktuell. (Mit -Force trotzdem neu installieren.)'
        return
    }

    & (Join-Path $package 'install.ps1') -Package $package -InstallDir $InstallDir
    Write-Host "Aktualisiert auf $available."
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
