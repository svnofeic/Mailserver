# Testbetrieb neben SmarterMail

Der neue Mailserver läuft zum Testen **parallel** zu SmarterMail auf demselben Server – auf eigenen Ports, sodass
SmarterMail und der Mailempfang nicht berührt werden. Echter Empfang von außen (Port 25) lässt sich erst nach der
Umstellung oder auf einem eigenen Server testen (siehe unten).

| | SmarterMail (bleibt) | Neuer Mailserver (Test) |
|---|---|---|
| SMTP Empfang | 25 | 2525 |
| SMTP Versand (Mailprogramm) | 587 / 465 | 2587 / 2465 |
| IMAP | 143 / 993 | 2143 / 2993 |
| Weboberfläche | – | 9443 |

## 0. Vorher

1. VPS-Snapshot beim Anbieter.
2. Sicherung mit `mailadmin export` (siehe [sicherung-export.md](sicherung-export.md)) – das ist zugleich der erste Test.

## 1. Paket holen und installieren

1. Das Paket herunterladen: <https://github.com/svnofeic/Mailserver/releases/download/latest/mailserver-win-x64.zip>
   (ca. 50 MB, keine Anmeldung nötig).
2. Auf den Server kopieren, nach `C:\Temp\mailserver` entpacken. In einer **PowerShell als Administrator**:

   ```powershell
   cd C:\Temp\mailserver
   Get-ChildItem -Recurse | Unblock-File
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   Set-Service Mailserver -StartupType Manual    # während des Tests nicht automatisch starten
   ```

   **Den Dienst noch nicht starten** – erst die Ports umstellen.

## 2. Einstellungen für den Test

`notepad C:\Mailserver\appsettings.json` – nur diese Werte ändern:

```json
"Hostname": "mail.feicht.me",
...
"Smtp": {
  "InboundPort": 2525,
  "SubmissionPort": 2587,
  "SubmissionTlsPort": 2465,
  ...
},
"Imap": {
  "Port": 2143,
  "TlsPort": 2993,
  ...
},
```

Firewall für die Testports öffnen (9443 hat `install.ps1` schon freigegeben; ist die Plesk-Firewall aktiv, dort ebenso):

```powershell
New-NetFirewallRule -DisplayName "Mailserver Test" -Direction Inbound -Protocol TCP -LocalPort 2587,2465,2993 -Action Allow
```

Das Zertifikat sucht der Server unter dem Hostnamen in den Speichern „My“ und „WebHosting“ (Plesk). Es muss
`mail.feicht.me` enthalten – das von SmarterMail bzw. Plesk für mail.feicht.me genutzte Zertifikat passt in der Regel.

## 3. Domain und Postfach anlegen

```powershell
cd C:\Mailserver
.\mailadmin.exe domain add feicht.me
.\mailadmin.exe user add test@feicht.me
.\mailadmin.exe user admin test@feicht.me on
```

`domain add` zeigt die DNS-Einträge. **Jetzt nur den DKIM-Eintrag** (`mail<datum>._domainkey`) in der DNS-Zone anlegen –
MX, SPF und DMARC bleiben unverändert, sonst geht Post an den neuen Server. Der zusätzliche DKIM-Eintrag stört
SmarterMail nicht.

Hinweis: Für den neuen Server ist feicht.me jetzt eine eigene Domain. Mails, die **über den neuen Server** an
@feicht.me-Adressen gehen, landen in dessen Postfächern, nicht in SmarterMail. Für alle anderen ändert sich nichts.

## 4. Erster Start im Konsolenfenster

```powershell
cd C:\Mailserver
.\Mailserver.exe
```

Die Meldungen erscheinen direkt im Fenster. Wichtig:

* `Using TLS certificate CN=… valid until …` – Zertifikat gefunden.
* `No TLS certificate available for …` – kein passendes Zertifikat. `.\mailadmin.exe tls` listet alle Zertifikate und
  den Grund, warum keines passt (siehe unten „Zertifikat besorgen“).
* Fehler wie „Only one usage of each socket address“ bzw. „address already in use“ – ein Port ist belegt (meist noch ein Standardport in der appsettings.json).

Läuft alles, mit `Strg+C` beenden und als Dienst starten: `Start-Service Mailserver`. Spätere Meldungen stehen in der
Ereignisanzeige unter *Windows-Protokolle → Anwendung*, Quelle „Mailserver“.

### Zertifikat besorgen

Zeigt `mailadmin tls` kein Zertifikat für `mail.feicht.me`, ist das von SmarterMail genutzte Zertifikat nicht im
Windows-Speicher (SmarterMail verwaltet seine Let's-Encrypt-Zertifikate selbst). Zwei Wege:

* **Plesk:** *Websites & Domains → feicht.me → SSL/TLS-Zertifikate → Let's Encrypt* und dabei den Namen
  `mail.feicht.me` mit einschließen (je nach Plesk-Version „Mail“ / „Webmail“ absichern oder als zusätzlichen Namen).
  Das Zertifikat landet im Speicher „WebHosting“ und wird automatisch gefunden.
* **win-acme** (`wacs.exe`): Zertifikat für `mail.feicht.me` erstellen, Speicherort „Windows Certificate Store“.
  Die Prüfung läuft über Port 80; IIS beantwortet sie, wenn `mail.feicht.me` auf den Server zeigt.

Danach `.\mailadmin.exe tls` erneut ausführen – erscheint „Verwendet wird …“, den Mailserver neu starten.

## 5. Testliste

| # | Test | Erwartung |
|---|---|---|
| 1 | `https://mail.feicht.me:9443` öffnen, als test@feicht.me anmelden | Übersicht, Webmail und (als Admin) der Admin-Bereich |
| 2 | Eigenes Postfach importieren: `postfaecher.txt` mit einer Zeile, dann `.\mailadmin.exe import imap localhost postfaecher.txt --insecure-cert` | Ordner und Mails im Webmail sichtbar, Gelesen-Status stimmt |
| 3 | Thunderbird/Outlook: IMAP `mail.feicht.me` Port 2993 SSL/TLS, SMTP Port 2587 STARTTLS, Benutzer = Adresse | Ordner synchron, keine Zertifikatswarnung |
| 4 | Mail an eine externe Adresse (GMX, Gmail) senden | kommt an, nicht im Spam; in den Kopfzeilen `dkim=pass`, `spf=pass` |
| 5 | Mail an die Adresse von [mail-tester.com](https://www.mail-tester.com/) | Ergebnis 9/10 oder besser |
| 6 | Mail an test@feicht.me über den neuen Server | kommt im Posteingang an |
| 7 | Regel anlegen: Betreff enthält „Testregel“ → Spam; Mail mit diesem Betreff an sich selbst | landet im Spam-Ordner, im Spam-Verlauf sichtbar |
| 8 | Webmail: Mail mit Formatierung und Anhang schreiben, Ordner anlegen, Mail verschieben | funktioniert, auch auf dem Handy-Browser |
| 9 | Admin: Verlauf, Warteschlange, Einstellungen ansehen | Einträge zu den Tests vorhanden |
| 10 | Sicherung einspielen: `.\mailadmin.exe import export D:\Sicherung\Mail --dry-run` | zeigt Postfächer und Ordner der Sicherung |

Nicht testbar im Parallelbetrieb: Empfang von fremden Servern (die nutzen immer Port 25) und damit Spamfilter,
Greylisting und SPF/DKIM-Prüfung eingehender Mails.

## 6. Echter Empfang testen (optional)

Auf einem **neuen Server** (z. B. dem künftigen Windows Server 2022/2025) mit den Standardports und einer Test-Subdomain:
`MX test.feicht.me → mail2.feicht.me` (A-Eintrag auf die neue IP), `mailadmin domain add test.feicht.me`. Dann kommen
Mails an `…@test.feicht.me` von überall an, ohne die echten Domains zu berühren. Vorher beim Anbieter prüfen, ob
ausgehender Port 25 freigeschaltet ist (bei neuen VPS oft gesperrt) und den PTR-Eintrag setzen lassen.

## Rückmeldung

Bei Problemen helfen: die Konsolenausgabe bzw. Einträge aus der Ereignisanzeige, die Ausgabe von `mailadmin`, bei
Zustellproblemen der Bericht von mail-tester.com oder die Kopfzeilen der empfangenen Mail.

## Neue Version einspielen

Jede neue Version liegt automatisch als Download bereit:
<https://github.com/svnofeic/Mailserver/releases/tag/latest>. In einer **PowerShell als Administrator**:

```powershell
C:\Mailserver\update.ps1
```

Das Skript lädt das neueste Paket, vergleicht die Version (`C:\Mailserver\version.txt`), stoppt den Dienst, ersetzt die
Programmdateien, behält `appsettings.json` und den Ordner `data` und startet den Dienst wieder, falls er lief.
Läuft der Server im Konsolenfenster, dieses vorher mit `Strg+C` beenden. Die laufende Version steht in der
Weboberfläche unten in der Seitenleiste.

**Beim ersten Mal** gibt es `update.ps1` noch nicht im Installationsordner – dann einmalig:

```powershell
[Net.ServicePointManager]::SecurityProtocol = 'Tls12'
Invoke-WebRequest https://github.com/svnofeic/Mailserver/releases/download/latest/update.ps1 -OutFile C:\Mailserver\update.ps1 -UseBasicParsing
powershell -ExecutionPolicy Bypass -File C:\Mailserver\update.ps1
```

## Testbetrieb beenden

`Stop-Service Mailserver`. Für die echte Umstellung später die Ports in `appsettings.json` wieder auf 25/587/465/143/993
setzen und `Set-Service Mailserver -StartupType Automatic` (siehe [umzug-smartermail.md](umzug-smartermail.md)).
