# Mailserver

Ein eigener Mailserver in C#/.NET 10 für Windows Server. Er ersetzt SmarterMail schrittweise.

## Stand

| Meilenstein | Inhalt | Status |
|---|---|---|
| **M1 – SMTP** | Empfang auf Port 25, Submission auf 587/465 mit Login, Zustellung nach außen mit MX-Lookup und Wiederholungen, DKIM-Signatur, Aliase und Weiterleitungen, Unzustellbarkeitsmeldungen, Quota, Schutz vor Brute-Force, Verwaltungs-Tool `mailadmin` | ✅ fertig |
| **M2 – IMAP** | IMAP-Server (Port 993 und 143 mit STARTTLS) für Outlook, Thunderbird und Smartphones: Ordner inkl. Unterordnern und Umlauten, Flags, Suche, Kopieren/Verschieben, Push über IDLE | ✅ fertig |
| **M3 – Spamschutz & Regeln** | SPF, DKIM und DMARC für eingehende Mails, DNS-Blacklists, Greylisting, Spam-Score mit Junk-Ordner. Eigene Regeln pro Postfach, Domain oder global, z. B. "Betreff enthält … → Spam / endgültig löschen / Ordner". Spam-Protokoll mit Benutzer-Feedback und Auswertung (`mailadmin spamlog`). Anleitung: [docs/spamschutz-und-regeln.md](docs/spamschutz-und-regeln.md) | ✅ fertig |
| **M4 – Migration** | `mailadmin import imap`: Postfächer aus SmarterMail (oder jedem IMAP-Server) mit Ordnern, Flags und Datum, wiederholbar für den letzten Abgleich. `mailadmin export`: Sicherung als .eml/.vcf/.ics (inkl. Kontakte und Kalender per CalDAV/CardDAV) und `import export` zum Einspielen auf einem neuen Server. Anleitungen: [docs/umzug-smartermail.md](docs/umzug-smartermail.md), [docs/sicherung-export.md](docs/sicherung-export.md) | ✅ fertig |
| **M5 – Weboberfläche** | Für Benutzer: **Webmail** (lesen, schreiben mit Formatierungs-Editor, antworten, weiterleiten, Anhänge, Entwürfe, Ordner verwalten; sichere HTML-Anzeige, externe Bilder blockiert), Übersicht, eigene Regeln, Spam-Verlauf mit „Absender erlauben/sperren“, Passwort. Für Admins: Domains (DNS/DKIM), Postfächer, Aliase, alle Regeln, Warteschlange, kompletter Verlauf mit CSV-Export, Spam-Statistik, Einstellungen ohne Neustart. Anleitung: [docs/weboberflaeche.md](docs/weboberflaeche.md) | ✅ fertig |
| **M6 – Komfort** | Autodiscover/Autoconfig für Mailprogramme, MTA-STS, Monitoring, Zwei-Faktor-Anmeldung | offen |

> **Umzug von SmarterMail:** siehe [docs/umzug-smartermail.md](docs/umzug-smartermail.md). Beide Server können nicht
> gleichzeitig dieselben Ports (25, 587, 465, 143, 993) belegen; der Import läuft deshalb, bevor der neue Dienst startet.
>
> **Server mit Plesk:** Was in Plesk umgestellt werden muss (Mail-Dienst, Webmail, DNS, Zertifikate, Mails von Websites),
> steht in [docs/plesk.md](docs/plesk.md).

## Aufbau

```
src/
  Mailserver.Core      Domains/Konten/Aliase (SQLite), Postfach-Speicher (.eml-Dateien), Warteschlange,
                       DKIM, Routing, Bounces, Zertifikate, Login-Sperren
  Mailserver.AntiSpam  SPF, DKIM- und DMARC-Prüfung, DNS-Blacklists, Greylisting, Spam-Score
  Mailserver.Smtp      SMTP-Server (Port 25 und 587/465) und Zustell-Dienst (MX bzw. Smarthost)
  Mailserver.Imap      IMAP-Server (Port 993 und 143): Protokoll, MIME-Struktur, Sitzungen, IDLE
  Mailserver.Service   Windows-Dienst (Mailserver.exe)
  Mailserver.Migration Import von anderen IMAP-Servern (SmarterMail)
  Mailserver.Web       Weboberfläche mit Webmail für Benutzer und Verwaltung für Admins (HTTPS, Port 9443)
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

### Sicherheitsregeln

- **Port 25** nimmt nur Mails für eigene Postfächer und Aliase an. Weiterleiten an fremde Server ("Open Relay") ist ausgeschlossen, und ein Login ist dort nicht möglich.
- Mails, die auf Port 25 ohne Login eine eigene Domain als Absender angeben, werden abgelehnt (Schutz vor Spoofing).
- **Port 587/465** verlangt TLS und einen Login. Ohne Zertifikat bleiben diese Ports aus.
- Ein Benutzer darf nur mit der eigenen Adresse oder einem eigenen Alias senden.
- Nach 10 Fehl-Logins innerhalb von 15 Minuten wird die IP-Adresse 30 Minuten gesperrt.
- Ausgehende Mails werden mit DKIM signiert und bekommen Message-ID und Date, falls diese fehlen.
- Weiterleitungen an externe Adressen gehen mit dem Alias als Absender raus, damit SPF beim Empfänger besteht.
- Unzustellbarkeitsmeldungen werden nie selbst wieder zurückgeschickt, dadurch entstehen keine Mail-Schleifen. Mails mit zu vielen `Received`-Headern werden abgelehnt.
- **IMAP** nimmt Passwörter nur über TLS an: auf 993 sofort, auf 143 erst nach STARTTLS. Fehl-Logins zählen gemeinsam mit SMTP für die IP-Sperre, nach einem Fehlversuch antwortet der Server erst nach einer Sekunde.
- Pro IP-Adresse sind höchstens 30 gleichzeitige IMAP-Verbindungen erlaubt. Vor dem Login sind nur kleine Datenblöcke zulässig, damit niemand den Speicher füllen kann.

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

**Fertiges Paket:** GitHub → Actions → neuester Lauf von „CI“ → Artifacts → `mailserver-win-x64` (enthält
`install.ps1` und die Anleitungen). Zum Ausprobieren neben SmarterMail: [docs/testbetrieb.md](docs/testbetrieb.md).

Oder selbst bauen, auf einem Entwicklungsrechner mit .NET 10 SDK:

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

### Mailprogramme einrichten

| | Server | Port | Verschlüsselung |
|---|---|---|---|
| Posteingang (IMAP) | `mail.example.de` | 993 | SSL/TLS |
| Postausgang (SMTP) | `mail.example.de` | 587 | STARTTLS |

Benutzername ist immer die vollständige E-Mail-Adresse. Ordner für Gesendet, Entwürfe, Papierkorb und Spam erkennen die
Programme automatisch (SPECIAL-USE).

Unterstützte IMAP-Erweiterungen: LITERAL+, SASL-IR, ID, ENABLE, IDLE, NAMESPACE, UNSELECT, UIDPLUS, MOVE, CHILDREN, SPECIAL-USE.
Noch nicht enthalten sind CONDSTORE/QRESYNC (schnellere Synchronisation großer Postfächer), QUOTA, geteilte Ordner und Sieve-Filter.

### Wichtige Einstellungen (`appsettings.json`)

| Schlüssel | Bedeutung |
|---|---|
| `Mailserver:Hostname` | Öffentlicher Name; muss zum PTR-Eintrag und zum Zertifikat passen |
| `Mailserver:Tls:PfxPath` / `PfxPassword` | PFX-Datei; ist sie leer, werden die Windows-Zertifikatsspeicher „My“ und „WebHosting“ (Plesk) nach einem Zertifikat für `Hostname` durchsucht (auch alternative Namen und Wildcards) |
| `Mailserver:Smtp:ListenAddresses`, `Mailserver:Imap:ListenAddresses` | z. B. `["0.0.0.0", "::"]` für IPv4 und IPv6 |
| `Mailserver:Smtp:RelayNetworks` | Netze, die über Port 25 ohne Anmeldung versenden dürfen, z. B. `["127.0.0.1/32"]` für Websites auf demselben Server (Standard: leer) |
| `Mailserver:Imap:Port` / `TlsPort` | 143 (STARTTLS) und 993 (TLS); `0` schaltet einen Port ab |
| `Mailserver:Imap:MaxConnectionsPerIp` | Gleichzeitige IMAP-Verbindungen pro IP (Standard 30) |
| `Mailserver:Delivery:SmartHost` | Optionaler Relay-Server (`Host`, `Port`, `Username`, `Password`, `Security`) |
| `Mailserver:Delivery:MaxQueueLifetime` | Wie lange eine Mail zugestellt werden soll, bevor sie zurückgeht (Standard 5 Tage) |
| `Mailserver:Security:*` | Login-Sperren, Spoofing-Schutz, Hop-Limit |
| `Mailserver:Web:*` | Weboberfläche: `HttpsPort` (9443), `ListenAddresses`, `Enabled`, `SessionTimeout` |
| `Mailserver:Spam:*` | Spamfilter: Schwellen für Junk/Löschen, Blacklists, Greylisting, vertrauenswürdige Netze |

### Verwaltung

```
mailadmin domain add|list|remove        mailadmin user add|passwd|quota|enable|disable|remove|list
mailadmin dns <domain>                  mailadmin alias add|remove|list
mailadmin dkim rotate|activate          mailadmin queue list|retry
mailadmin user admin <adresse> on|off   Zugang zum Admin-Bereich der Weboberfläche
mailadmin import imap <host> <datei> [--port 993] [--starttls] [--insecure-cert] [--dry-run]
mailadmin export <host> <datei> <zielordner> [--insecure-cert] [--dav <url>]   Sicherung (siehe docs/sicherung-export.md)
mailadmin import export <exportordner> [<datei>] [--dry-run]                  Sicherung einspielen
mailadmin rule add|list|remove|enable|disable|test   (siehe docs/spamschutz-und-regeln.md)
mailadmin spamlog list|show|stats|export|cleanup     Spam-Protokoll auswerten (siehe docs/spamschutz-und-regeln.md)
```

Logs: Ausgabe in der Konsole und Warnungen sowie Fehler in der Windows-Ereignisanzeige (Quelle "Mailserver").

## Entwicklung

```bash
dotnet build Mailserver.slnx
dotnet test Mailserver.slnx
```

Die Integrationstests starten den kompletten Server auf freien lokalen Ports und spielen echte SMTP- und IMAP-Sitzungen durch.
SMTP: Empfang, Relay-Sperre, Spoofing-Schutz, Login, DKIM-Prüfung, Zustellung, Bounce, Wiederholung und Weiterleitung, mit einem
simulierten fremden Mailserver als Gegenstelle. IMAP: MailKit als echter Client (Ordner, ENVELOPE/BODYSTRUCTURE, Anhänge, Flags,
Suche, APPEND, COPY/MOVE, EXPUNGE, IDLE, mehrere Sitzungen gleichzeitig) sowie Tests auf Protokollebene (Literale, Fehlerantworten,
Sperre nach Fehl-Logins).
