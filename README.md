# Mailserver

Ein eigener Mailserver in C#/.NET 10 für Windows Server. Er ersetzt SmarterMail schrittweise.

## Stand

| Meilenstein | Inhalt | Status |
|---|---|---|
| **M1 – SMTP** | Empfang auf Port 25, Submission auf 587/465 mit Login, Zustellung nach außen mit MX-Lookup und Wiederholungen, DKIM-Signatur, Aliase und Weiterleitungen, Unzustellbarkeitsmeldungen, Quota, Schutz vor Brute-Force, Verwaltungs-Tool `mailadmin` | ✅ fertig |
| **M2 – IMAP** | IMAP-Server (Port 993) für Outlook, Thunderbird und Smartphones, mit Ordnern, Flags und IDLE | ⏳ als Nächstes |
| **M3 – Spamschutz** | SPF-, DKIM- und DMARC-Prüfung eingehender Mails, DNS-Blacklists, Greylisting, Junk-Ordner, Rate-Limits | offen |
| **M4 – Migration** | Import aus SmarterMail per IMAP (Postfächer und Ordner) | offen |
| **M5 – Komfort** | Web-Oberfläche zur Verwaltung, Autodiscover/Autoconfig, MTA-STS, Monitoring | offen |

> **Wichtig:** Ohne IMAP (M2) kann noch niemand seine Mails abrufen. Bis M2 und M4 fertig sind, läuft SmarterMail weiter.
> Beide Server können nicht gleichzeitig Port 25 belegen. Zum Testen kann man den neuen Server auf einem anderen Server
> betreiben oder in `appsettings.json` andere Ports setzen.

## Aufbau

```
src/
  Mailserver.Core      Domains/Konten/Aliase (SQLite), Postfach-Speicher (.eml-Dateien), Warteschlange,
                       DKIM, Routing, Bounces, Zertifikate, Login-Sperren
  Mailserver.Smtp      SMTP-Server (Port 25 und 587/465) und Zustell-Dienst (MX bzw. Smarthost)
  Mailserver.Service   Windows-Dienst (Mailserver.exe)
  Mailserver.Admin     Kommandozeilen-Verwaltung (mailadmin.exe)
tests/
  Mailserver.Tests     Unit- und Integrationstests mit echten SMTP-Sitzungen
scripts/
  publish.ps1          Erzeugt ein eigenständiges Windows-Paket (keine .NET-Installation nötig)
  install.ps1          Installiert bzw. aktualisiert den Windows-Dienst und die Firewall-Regeln
```

Daten (Standard: `C:\Mailserver\data`):

- `mailserver.db`: SQLite-Datenbank mit Domains, Konten, Aliasen, Ordnern, Nachrichten-Index und Warteschlange
- `mail\<KontoId>\<JJJJMM>\*.eml`: die Nachrichten als normale `.eml`-Dateien
- `queue\*.eml`: ausgehende Nachrichten, die noch nicht zugestellt sind
- `dkim\<domain>.<selector>.pem`: die DKIM-Schlüssel

Für ein Backup sichert man den ganzen `data`-Ordner. Die Datenbank sollte man dabei per `sqlite3 .backup` oder bei gestopptem Dienst sichern.

### Sicherheitsregeln im SMTP-Teil

- **Port 25** nimmt nur Mails für eigene Postfächer und Aliase an. Weiterleiten an fremde Server ("Open Relay") ist ausgeschlossen, und ein Login ist dort nicht möglich.
- Mails, die auf Port 25 ohne Login eine eigene Domain als Absender angeben, werden abgelehnt (Schutz vor Spoofing).
- **Port 587/465** verlangt TLS und einen Login. Ohne Zertifikat bleiben diese Ports aus.
- Ein Benutzer darf nur mit der eigenen Adresse oder einem eigenen Alias senden.
- Nach 10 Fehl-Logins innerhalb von 15 Minuten wird die IP-Adresse 30 Minuten gesperrt.
- Ausgehende Mails werden mit DKIM signiert und bekommen Message-ID und Date, falls diese fehlen.
- Weiterleitungen an externe Adressen gehen mit dem Alias als Absender raus, damit SPF beim Empfänger besteht.
- Unzustellbarkeitsmeldungen werden nie selbst wieder zurückgeschickt, dadurch entstehen keine Mail-Schleifen. Mails mit zu vielen `Received`-Headern werden abgelehnt.

## Voraussetzungen

1. **VPS:** Port 25 ausgehend ist freigeschaltet, und der **PTR-Eintrag** (Reverse DNS) der IP zeigt auf den Hostnamen, z. B. `mail.example.de`.
   Ist Port 25 gesperrt, trägt man einen Relay-Server unter `Delivery:SmartHost` ein.
2. **TLS-Zertifikat** für den Hostnamen, z. B. kostenlos über [win-acme](https://www.win-acme.com/):
   - **Variante 1:** win-acme legt das Zertifikat im Windows-Zertifikatsspeicher ab (Standard). Der Server findet es dann über den Hostnamen und übernimmt Verlängerungen automatisch.
   - **Variante 2:** win-acme exportiert eine PFX-Datei. Deren Pfad gehört nach `Tls:PfxPath`.
   - Für die Prüfung durch Let's Encrypt (HTTP-01) muss Port 80 kurz erreichbar sein, oder man nutzt die DNS-Prüfung.
3. **Windows Server 2016:** Der Support von Microsoft endet am **12.01.2027**. Ein Upgrade auf Server 2022/2025 ist dringend empfohlen.
   Auf 2016 sollten TLS 1.0 und 1.1 systemweit abgeschaltet werden, z. B. mit *IIS Crypto* über die Vorlage "Best Practices". TLS 1.3 gibt es auf 2016 nicht.

## Installation

Auf einem Entwicklungsrechner mit .NET 10 SDK:

```powershell
.\scripts\publish.ps1          # führt die Tests aus und erzeugt .\publish\
```

Den Ordner `publish` auf den Server kopieren und dort in einer PowerShell als Administrator ausführen:

```powershell
.\install.ps1 -Package C:\Temp\publish     # installiert nach C:\Mailserver
notepad C:\Mailserver\appsettings.json     # Hostname und Tls eintragen
```

Domain und Postfächer anlegen:

```powershell
cd C:\Mailserver
.\mailadmin.exe domain add example.de          # erzeugt den DKIM-Schlüssel und zeigt alle DNS-Einträge
.\mailadmin.exe user add max@example.de        # fragt das Passwort ab
.\mailadmin.exe alias add info@example.de max@example.de,anna@example.de
Start-Service Mailserver
```

Danach die DNS-Einträge setzen, die `mailadmin dns example.de` ausgibt (MX, SPF, DKIM, DMARC), und mit
[mail-tester.com](https://www.mail-tester.com/) prüfen.

### Wichtige Einstellungen (`appsettings.json`)

| Schlüssel | Bedeutung |
|---|---|
| `Mailserver:Hostname` | Öffentlicher Name; muss zum PTR-Eintrag und zum Zertifikat passen |
| `Mailserver:Tls:PfxPath` / `PfxPassword` | PFX-Datei; ist sie leer, wird der Windows-Zertifikatsspeicher nach `Hostname` durchsucht |
| `Mailserver:Smtp:ListenAddresses` | z. B. `["0.0.0.0", "::"]` für IPv4 und IPv6 |
| `Mailserver:Delivery:SmartHost` | Optionaler Relay-Server (`Host`, `Port`, `Username`, `Password`, `Security`) |
| `Mailserver:Delivery:MaxQueueLifetime` | Wie lange eine Mail zugestellt werden soll, bevor sie zurückgeht (Standard 5 Tage) |
| `Mailserver:Security:*` | Login-Sperren, Spoofing-Schutz, Hop-Limit |

### Verwaltung

```
mailadmin domain add|list|remove        mailadmin user add|passwd|quota|enable|disable|remove|list
mailadmin dns <domain>                  mailadmin alias add|remove|list
mailadmin dkim rotate|activate          mailadmin queue list|retry
```

Logs: Ausgabe in der Konsole und Warnungen sowie Fehler in der Windows-Ereignisanzeige (Quelle "Mailserver").

## Entwicklung

```bash
dotnet build Mailserver.slnx
dotnet test Mailserver.slnx
```

Die Integrationstests starten den kompletten Server auf freien lokalen Ports und spielen echte SMTP-Sitzungen durch. Dazu gehören Empfang, Relay-Sperre, Spoofing-Schutz, Login, DKIM-Prüfung, Zustellung, Bounce, Wiederholung und Weiterleitung. Ein simulierter fremder Mailserver dient dabei als Gegenstelle.
