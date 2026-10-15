<#
.SYNOPSIS
    Installs or updates the mail server as a Windows service. Run in an elevated PowerShell on the server.
.DESCRIPTION
    - copies the package to the install directory (an existing appsettings.json and the data folder are kept)
    - registers the "Mailserver" service with automatic start and restart on failure
    - opens the firewall for SMTP (25), submission (587), SMTPS (465), IMAP (143), IMAPS (993), the web interface (9443) and Let's Encrypt (80)
    - restricts the data folder to SYSTEM and Administrators
    On an update the service is stopped (and its process really ended), the old program files are kept aside, and if
    copying fails they are put back. On an update the service is started again in any case (also when an earlier update
    left it stopped; not when it is disabled), and the script checks that the web interface answers. Everything is written to <InstallDir>\logs\update-<time>.log.
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

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if ((Resolve-Path $Package).Path.TrimEnd('\') -eq (Resolve-Path $InstallDir).Path.TrimEnd('\')) {
    throw "The package folder is the install folder. Unpack the new package elsewhere (e.g. C:\Temp\mailserver) and run install.ps1 there."
}

$logDir = Join-Path $InstallDir 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("update-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
Start-Transcript -Path $log -Append | Out-Null
$started = Get-Date

function Get-ServerProcess {
    # Only the copies from this installation; another program with the same name is left alone.
    Get-Process -Name 'Mailserver', 'mailadmin' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
}

function Stop-Server {
    $current = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($current -and $current.Status -ne 'Stopped') {
        Write-Host "Stoppe $ServiceName ..."
        # -NoWait: Stop-Service would otherwise block without a time limit while the service finishes deliveries.
        Stop-Service -Name $ServiceName -NoWait -ErrorAction Continue
        $deadline = (Get-Date).AddSeconds(120)
        while ((Get-Service -Name $ServiceName).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
        }

        if ((Get-Service -Name $ServiceName).Status -ne 'Stopped') {
            Write-Warning "$ServiceName hat nach 2 Minuten nicht angehalten; der Prozess wird beendet."
        }
    }

    # "Stopped" does not mean the process is gone yet; while it runs, its files are locked.
    $running = Get-ServerProcess | Where-Object { $_.ProcessName -eq 'Mailserver' }
    if ($running) {
        $running | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
        $running = Get-ServerProcess | Where-Object { $_.ProcessName -eq 'Mailserver' }
        if ($running) {
            Write-Warning "Mailserver.exe (PID $($running.Id -join ', ')) läuft noch und wird beendet."
            $running | Stop-Process -Force
            Start-Sleep -Seconds 3
        }
    }
}

function Copy-Files([string]$From, [string]$To, [string[]]$ExcludeFiles = @(), [string[]]$ExcludeDirs = @()) {
    # robocopy retries files that are still locked for a moment (e.g. by a virus scanner) instead of failing at once.
    $arguments = @($From, $To, '/E', '/R:10', '/W:3', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($ExcludeFiles.Count -gt 0) { $arguments += '/XF'; $arguments += $ExcludeFiles }
    if ($ExcludeDirs.Count -gt 0) { $arguments += '/XD'; $arguments += $ExcludeDirs }
    & robocopy.exe @arguments | Out-Host
    # robocopy: 0-7 = success (files copied, skipped, extra); 8 and above = errors.
    if ($LASTEXITCODE -ge 8) { throw "Kopieren nach $To fehlgeschlagen (robocopy $LASTEXITCODE)." }
}

function Get-WebPort {
    try {
        $settings = Get-Content (Join-Path $InstallDir 'appsettings.json') -Raw | ConvertFrom-Json
        if ($settings.Mailserver.Web.HttpsPort) { return [int]$settings.Mailserver.Web.HttpsPort }
    } catch { }
    return 9443
}

function Test-Port([int]$Port, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $client.Connect('127.0.0.1', $Port)
            return $true
        } catch {
            Start-Sleep -Seconds 2
        } finally {
            $client.Dispose()
        }
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Show-RecentErrors {
    try {
        Get-EventLog -LogName Application -Source $ServiceName -After $started -EntryType Error, Warning -Newest 10 -ErrorAction Stop |
            ForEach-Object { Write-Host "  $($_.TimeGenerated.ToString('HH:mm:ss')) $($_.EntryType): $($_.Message.Split("`n")[0])" }
    } catch {
        Write-Host '  (keine Einträge im Ereignisprotokoll)'
    }
}

function Start-Server {
    Write-Host "Starte $ServiceName ..."
    try {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(90))
    } catch {
        Write-Warning "$ServiceName ist nicht gestartet: $($_.Exception.Message)"
        Write-Host 'Letzte Meldungen im Ereignisprotokoll (Anwendung, Quelle Mailserver):'
        Show-RecentErrors
        return $false
    }

    $port = Get-WebPort
    if (Test-Port -Port $port -Seconds 60) {
        Write-Host "$ServiceName läuft, Weboberfläche antwortet auf Port $port."
        return $true
    }

    Write-Warning "$ServiceName läuft, aber die Weboberfläche antwortet nicht auf Port $port."
    Show-RecentErrors
    return $false
}

$backup = $null
$ok = $false
try {
    # A server or mailadmin started by hand in a console window keeps its files locked.
    $console = Get-ServerProcess | Where-Object { $_.SessionId -ne 0 }
    if ($console) {
        throw "$($console[0].ProcessName).exe läuft in einem Konsolenfenster (PID $($console.Id -join ', ')). Bitte zuerst beenden (Strg+C)."
    }

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    # An existing installation runs again afterwards - even if an earlier, failed update left the service stopped.
    $startAfterwards = $service -and $service.StartType -ne 'Disabled'
    Stop-Server

    $keepConfig = Test-Path (Join-Path $InstallDir 'appsettings.json')
    if (Test-Path (Join-Path $InstallDir 'Mailserver.exe')) {
        # The current program files, to put back if copying the new ones fails half-way.
        $backup = Join-Path $env:TEMP ("mailserver-previous-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Write-Host "Sichere die bisherigen Programmdateien nach $backup ..."
        Copy-Files -From $InstallDir -To $backup -ExcludeDirs @((Join-Path $InstallDir 'data'), $logDir)
    }

    Write-Host "Kopiere die neue Version nach $InstallDir ..."
    try {
        Copy-Files -From $Package -To $InstallDir -ExcludeFiles $(if ($keepConfig) { @('appsettings.json') } else { @() })
    } catch {
        if ($backup) {
            Write-Warning "Kopieren fehlgeschlagen - die bisherige Version wird wiederhergestellt."
            Stop-Server
            Copy-Files -From $backup -To $InstallDir
        }
        throw
    }
    if ($keepConfig) { Write-Host 'Bestehende appsettings.json behalten. Neue Einstellungen stehen in der appsettings.json des Pakets.' }

    $dataDir = Join-Path $InstallDir 'data'
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    icacls $dataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

    if (-not $service) {
        New-Service -Name $ServiceName -DisplayName 'Mailserver (SMTP/IMAP/Web)' -StartupType Automatic `
            -BinaryPathName "`"$(Join-Path $InstallDir 'Mailserver.exe')`"" `
            -Description 'Eigener Mailserver: SMTP, IMAP und Weboberfläche.' | Out-Null
    }
    # Restart after 1 minute on crashes (also when the service ends with an error); reset the counter after one day.
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    sc.exe failureflag $ServiceName 1 | Out-Null

    foreach ($rule in @(
            @{ Name = 'Mailserver SMTP 25'; Port = 25 },
            @{ Name = 'Mailserver Submission 587'; Port = 587 },
            @{ Name = 'Mailserver SMTPS 465'; Port = 465 },
            @{ Name = 'Mailserver IMAP 143'; Port = 143 },
            @{ Name = 'Mailserver IMAPS 993'; Port = 993 },
            @{ Name = 'Mailserver Web 9443'; Port = 9443 },
            # Let's Encrypt checks on port 80; the mail server only listens there while a certificate is being issued.
            @{ Name = "Mailserver Let's Encrypt 80"; Port = 80 })) {
        if (-not (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol TCP -LocalPort $rule.Port -Action Allow | Out-Null
        }
    }

    Write-Host "Installiert nach $InstallDir."
    $ok = $true
}
finally {
    # Whatever happened: an installed service runs again afterwards (new version, or the old one restored).
    if ($startAfterwards) {
        $running = Start-Server
        if ($ok -and $running) { Write-Host "Aktualisiert; $ServiceName läuft wieder." }
    } elseif ($ok) {
        Write-Host "Nächster Schritt: $InstallDir\appsettings.json bearbeiten (Hostname, Tls), dann: Start-Service $ServiceName"
        Write-Host "Weboberfläche: https://<hostname>:9443  (Admin-Recht zuerst vergeben: mailadmin user admin <adresse> on)"
    }

    if ($backup -and $ok) { Remove-Item -Recurse -Force $backup -ErrorAction SilentlyContinue }
    Write-Host "Protokoll: $log"
    Stop-Transcript | Out-Null
}
