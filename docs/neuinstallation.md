# Windows neu installieren und Mailserver zurückspielen

Ablauf, wenn das Betriebssystem auf **demselben Server** neu installiert wird (z. B. Windows Server 2016 → 2022/2025,
ohne Plesk). Die IP-Adresse bleibt gleich, daher ändern sich MX-Eintrag und PTR nicht.

> **Wichtig:** Nach der Neuinstallation gibt es SmarterMail nicht mehr. Alles muss vorher im neuen Mailserver
> stecken – und die Sicherung muss **außerhalb des Servers** liegen (heruntergeladen oder auf einem externen Speicher).

## Wie lange darf der Server weg sein?

Fremde Mailserver, die während der Neuinstallation niemanden erreichen, versuchen es bis zu 4–5 Tage lang erneut.
Eingehende Mails gehen also nicht verloren, wenn der Server innerhalb von **1–2 Tagen** wieder läuft. Mails, die Benutzer
in dieser Zeit verschicken wollen, bleiben im Postausgang ihres Mailprogramms.

## A. Vorbereiten (Tage vorher, ohne Ausfall)

1. Alle Postfächer importieren und prüfen (`mailadmin import imap …`, siehe [umzug-smartermail.md](umzug-smartermail.md)).
2. Aliase, Weiterleitungen und Abwesenheitsnotizen aus SmarterMail im neuen Mailserver anlegen.
3. DKIM-Einträge aller Domains ins DNS eintragen (`mailadmin dns <domain>` zeigt sie) – MX/SPF/DMARC noch nicht ändern.
4. Für alles andere auf dem Server sorgen: Websites und Datenbanken aus Plesk (Plesk-Datensicherung herunterladen),
   eigene Dateien, Lizenzschlüssel.
5. Beim VPS-Anbieter klären, ob es einen **Snapshot** gibt – die einfachste Rückfalloption, falls etwas schiefgeht.

## B. Am Tag der Neuinstallation – sichern

In einer **PowerShell als Administrator**:

```powershell
# 1. Ab jetzt keine neuen Mails mehr annehmen – Absender stellen sie später zu
#    (eine Block-Regel hat in der Windows-Firewall Vorrang vor allen Freigaben)
New-NetFirewallRule -DisplayName "SMTP 25 gesperrt (Umzug)" -Direction Inbound -Protocol TCP -LocalPort 25 -Action Block

# 2. Letzter Abgleich: holt nur, was seit dem letzten Import neu ist
cd C:\Mailserver
.\mailadmin.exe import imap localhost postfaecher.txt --port 143 --starttls --insecure-cert

# 3. Zusätzlich alles in offenen Formaten sichern, inklusive Kontakte und Kalender
.\mailadmin.exe export localhost postfaecher.txt C:\Sicherung\Export --port 143 --starttls --insecure-cert

# 4. Mailserver anhalten und seine Daten sichern
Stop-Service Mailserver
robocopy C:\Mailserver\data C:\Sicherung\Mailserver\data /E /R:1 /W:1 /XD keys
Copy-Item C:\Mailserver\appsettings.json, C:\Mailserver\postfaecher.txt C:\Sicherung\Mailserver\

# 5. SmarterMail-Rohdaten als zusätzliche Sicherheit
Stop-Service SmarterMail
robocopy "C:\SmarterMail" "C:\Sicherung\SmarterMail" /E /R:1 /W:1
```

Danach den Ordner `C:\Sicherung` **vom Server herunterladen** (z. B. als ZIP) und prüfen, dass er vollständig ankommt.
`postfaecher.txt` enthält ggf. Passwörter – nach der Umstellung löschen.

| Was | Warum |
|---|---|
| `data\mailserver.db` | Postfächer (mit Passwörtern), Ordner, Aliase, Regeln, Weiterleitungen, Verlauf |
| `data\mail\` | alle Mails als Dateien |
| `data\dkim\` | DKIM-Schlüssel – ohne sie passen die DNS-Einträge nicht mehr |
| `data\settings.json` | in der Weboberfläche geänderte Einstellungen |
| `data\keys\` | **nicht** sichern: an die alte Windows-Installation gebunden; wird neu erzeugt (alle Benutzer melden sich einmal neu an) |

## C. Windows neu installieren

1. Windows Server 2022 oder 2025 installieren, alle Updates einspielen.
2. Starkes Administrator-Passwort, RDP nur wenn nötig (am besten auf die eigene IP beschränken).
3. `C:\Sicherung` wieder auf den Server kopieren.

## D. Mailserver installieren und Daten zurückspielen

```powershell
# Paket laden und installieren
[Net.ServicePointManager]::SecurityProtocol = 'Tls12'
New-Item -ItemType Directory C:\Temp -Force | Out-Null
Invoke-WebRequest https://github.com/svnofeic/Mailserver/releases/download/latest/mailserver-win-x64.zip -OutFile C:\Temp\mailserver.zip -UseBasicParsing
Expand-Archive C:\Temp\mailserver.zip C:\Temp\mailserver
powershell -ExecutionPolicy Bypass -File C:\Temp\mailserver\install.ps1

# Daten zurückspielen (ohne keys)
robocopy C:\Sicherung\Mailserver\data C:\Mailserver\data /E /R:1 /W:1
```

Dann `notepad C:\Mailserver\appsettings.json`:

- `"Hostname": "mail.feicht.me"`
- **Standardports**: Smtp `25`, `587`, `465`; Imap `143`, `993` (die Testports 2525 … aus dem Parallelbetrieb zurücksetzen)
- `"Tls"`: `"StoreSubject": null` (der Umweg über webmail.feicht.me ist nicht mehr nötig)
- optional `"Web": { "HttpsPort": 443 }` – ohne IIS ist Port 443 frei; dann die Firewall-Regel anpassen

## E. Zertifikat (Let's Encrypt, eingebaut)

Der Mailserver holt und verlängert sein Zertifikat selbst. Einmalig, **bevor** der Dienst gestartet wird:

```powershell
cd C:\Mailserver
.\mailadmin.exe tls acme --email sven@feicht.me --hosts "mail.feicht.me, webmail.feicht.me"
```

- Jeder Name muss per DNS auf den Server zeigen; Port 80 muss von außen erreichbar sein (die Firewall-Regel
  „Mailserver Let's Encrypt 80“ legt `install.ps1` an). Der Mailserver öffnet Port 80 nur für die Prüfung.
- Zum Ausprobieren erst mit `--staging` (Testzertifikat, keine Limits bei Fehlversuchen), danach ohne.
- Verlängert wird automatisch 30 Tage vor Ablauf, ohne Neustart. Später lässt sich alles unter
  *Verwaltung → Zertifikat* in der Weboberfläche ändern („Speichern und jetzt ausstellen“).
- Prüfen: `.\mailadmin.exe tls` zeigt „Verwendet wird: Let's Encrypt“.

Alternativ geht weiterhin win-acme (Zertifikat im Windows-Speicher) oder eine PFX-Datei (`Tls:PfxPath`).

## F. Starten und prüfen

```powershell
Start-Service Mailserver
Set-Service Mailserver -StartupType Automatic
Get-NetFirewallRule -DisplayName "Mailserver*" | Select DisplayName, Enabled
```

- Weboberfläche `https://mail.feicht.me:9443` (bzw. ohne Port bei 443): Anmeldung, Postfächer, Ordner, Mails.
- Mail von außen empfangen (z. B. von GMX/Gmail an die eigene Adresse) – die während der Pause zurückgehaltenen Mails
  treffen nach und nach ein.
- Mail nach außen senden, Ergebnis bei [mail-tester.com](https://www.mail-tester.com/) prüfen.
- Mailprogramme: Server `mail.feicht.me`, IMAP 993 (SSL/TLS), SMTP 587 (STARTTLS), Benutzername = Adresse,
  Passwort wie bisher. Wo noch ein anderer Servername oder Port eingetragen ist, ändern.

## G. DNS aufräumen (nach 1–2 Tagen stabilem Betrieb)

Für jede Domain mit `mailadmin dns <domain>` vergleichen: MX, SPF (`v=spf1 mx a -all`), DMARC und den neuen DKIM-Eintrag
behalten, den alten DKIM-Eintrag von SmarterMail/Plesk (z. B. `default._domainkey`) löschen.

## Datensicherung im Betrieb

Den Ordner `C:\Mailserver\data` (ohne `keys`) regelmäßig außerhalb des Servers sichern, z. B. nächtlich per geplanter
Aufgabe mit `robocopy` auf einen externen Speicher, oder mit `mailadmin export` in offene Formate.
