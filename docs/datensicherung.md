# Automatische Datensicherung

Der Mailserver sichert sich jede Nacht selbst – im laufenden Betrieb, ohne Ausfall. Einrichten unter
*Verwaltung → Datensicherung* (oder per `mailadmin backup --to <ordner>` für eine einmalige Sicherung).

## Wohin?

Möglichst **weg vom Server**, sonst hilft die Sicherung bei einem Festplattenschaden oder einer versehentlich
gelöschten VM nicht:

| Ziel | Beispiel | Hinweis |
|---|---|---|
| Netzwerkfreigabe mit Anmeldung | `\\u12345.your-storagebox.de\backup\mailserver` | z. B. Storage Box des Hosters; Benutzer und Passwort auf der Seite eintragen |
| Netzwerkfreigabe ohne Anmeldung | `\\nas\sicherung\mailserver` | das Computerkonto des Servers braucht Schreibrecht |
| zweite Festplatte | `D:\Sicherung\Mailserver` | schützt vor Fehlern, nicht vor Ausfall des ganzen Servers |

Zusätzlich lässt sich der Zielordner mit einem Sync-Programm (z. B. OneDrive, rclone) in die Cloud spiegeln.

## Was wird gesichert?

```
<zielordner>\
  snapshots\2026-10-05_030000\   eine Sicherung pro Nacht
      mailserver.db               Postfächer (mit Passwörtern), Ordner, Aliase, Regeln, Weiterleitungen, Verlauf
      dkim\  acme\  queue\        DKIM-Schlüssel, Let's-Encrypt-Zertifikat, noch nicht zugestellte Mails
      settings.json, appsettings.json
      manifest.json               Version, Datum, Anzahl Postfächer und Mails
  mail\                           alle Mails – nur neue werden jede Nacht dazukopiert
```

- Die Datenbank wird über die Sicherungsfunktion von SQLite kopiert und ist damit immer in sich stimmig.
- Ältere Sicherungen als die eingestellte Aufbewahrung (Standard 14 Tage) werden entfernt; die neueste bleibt immer.
  Mails, die auf dem Server gelöscht wurden, bleiben ebenso lange im Ordner `mail`, sodass jede vorhandene
  Sicherung vollständig zurückgespielt werden kann.
- **Nicht** gesichert wird `data\keys` (an die Windows-Installation gebunden; nach dem Zurückspielen melden sich alle
  Benutzer der Weboberfläche einmal neu an).
- War der Server zur Sicherungszeit aus, holt er die Sicherung kurz nach dem Start nach. Schlägt sie fehl, versucht er
  es alle drei Stunden erneut, und alle Admins bekommen eine Mail. Die Admin-Übersicht warnt, wenn die letzte
  erfolgreiche Sicherung älter als zwei Tage ist.

## Zurückspielen

In einer PowerShell als Administrator, auch auf einem frisch installierten Server (nach `install.ps1`):

```powershell
Stop-Service Mailserver
cd C:\Mailserver
.\mailadmin.exe backup restore "D:\Sicherung\Mailserver"            # neueste Sicherung
.\mailadmin.exe backup restore "D:\Sicherung\Mailserver\snapshots\2026-10-03_030000"   # eine bestimmte
Start-Service Mailserver
```

Auf einer Netzwerkfreigabe mit Anmeldung vorher einmal verbinden: `net use \\server\freigabe /user:<benutzer>`.

Beim Zurückspielen werden Datenbank, Einstellungen, DKIM-Schlüssel, Zertifikat und Warteschlange ersetzt und die Mails
aus dem Ordner `mail` ergänzt. `appsettings.json` wird nicht überschrieben – die Kopie aus der Sicherung liegt im
Sicherungsordner, falls man sie braucht.

## Befehle

```
mailadmin backup [--to <ordner>]       jetzt sichern
mailadmin backup list                  Verlauf und vorhandene Sicherungen
mailadmin backup restore <ordner>      zurückspielen (Dienst vorher anhalten)
```
