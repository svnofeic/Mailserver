# Umzug von SmarterMail

Diese Anleitung beschreibt den Wechsel von SmarterMail (Ausgangslage: Build 8853 auf demselben Windows-Server) zu diesem
Mailserver. Der Import arbeitet über IMAP; geprüft ist er gegen einen IMAP-Testserver, noch nicht gegen SmarterMail selbst –
deshalb immer zuerst den Probelauf ausführen. Übernommen werden **Postfächer mit allen Ordnern, Nachrichten, Gelesen/Markiert-Status,
Schlagworten und Empfangsdatum**. Die Nachrichten bleiben byte-genau erhalten.

## ⚠️ Zuerst: SmarterMail absichern

SmarterMail bis einschließlich Build 9406 hat eine kritische Lücke, über die Angreifer ohne Login Code auf dem Server
ausführen können (CVE-2025-52691, CVSS 10). Bis einschließlich Build 9510 lässt sich außerdem ohne Login das Admin-Konto
übernehmen (CVE-2026-23760); diese Lücke wird aktiv ausgenutzt. Build 8853 ist von beiden betroffen, und ohne gültigen
Upgrade-Schutz gibt es kein Update.

Bis zur Abschaltung von SmarterMail:

1. Die **Web-Oberfläche von SmarterMail aus dem Internet sperren** (Windows-Firewall bzw. IIS-Bindung für `webmail.…`,
   Ports 80/443 und ggf. 9998). Webmail ist dann nicht mehr erreichbar; Mailprogramme über IMAP/SMTP funktionieren weiter.
   Der Import braucht nur IMAP, nicht die Web-Oberfläche.
2. Prüfen, ob schon etwas passiert ist: unbekannte Administratoren in SmarterMail, unbekannte Dateien im SmarterMail-
   Programmordner (v. a. `.aspx`/`.ashx`), unbekannte Windows-Benutzer, geplante Aufgaben oder Dienste.
   Bei Verdacht: Server als kompromittiert betrachten und neu aufsetzen statt weiterzuverwenden.

## Was nicht automatisch übernommen wird

| Was | Vorgehen |
|---|---|
| Aliase und Weiterleitungen | in SmarterMail nachsehen, mit `mailadmin alias add` anlegen |
| Kontakte, Kalender, Aufgaben, Notizen | in SmarterMail-Webmail als `.vcf`/`.ics` exportieren (dieser Server hat kein CardDAV/CalDAV) |
| Regeln, Abwesenheitsnotizen, Signaturen im Webmail | neu einrichten (im Mailprogramm) |
| Mailinglisten | sind nicht Teil dieses Servers |
| DKIM-Schlüssel | werden neu erzeugt; der neue DKIM-Eintrag hat einen anderen Selector und kann vorab parallel gesetzt werden |
| Passwörter | lassen sich nicht auslesen – siehe nächster Abschnitt |

`plesk.localhost` ist eine interne Platzhalter-Domain von Plesk und muss nicht umziehen.

## Passwörter

SmarterMail gibt Passwörter nicht heraus, und IMAP erfordert für jedes Postfach die Anmeldung. Der Import verwendet das
Passwort, mit dem er sich bei SmarterMail anmeldet, auch für das neue Postfach. Pro Postfach gibt es zwei Wege:

- **Passwort ist bekannt** (z. B. eigene Postfächer): einfach eintragen. Für den Benutzer ändert sich nichts.
- **Passwort ist unbekannt:** als Administrator in SmarterMail ein neues Passwort setzen und es dem Benutzer mitteilen.
  Das am besten erst unmittelbar vor der Umstellung tun, weil seine Geräte ab dann das neue Passwort brauchen.

## Ablauf

### 1. Vorbereiten (ohne Ausfallzeit)

Den neuen Mailserver installieren (siehe README), aber **den Dienst noch nicht starten** – SmarterMail belegt die Ports.
Der Import schreibt direkt in den Datenordner und braucht den Dienst nicht.

Eine Datei `postfaecher.txt` anlegen, eine Zeile pro Postfach:

```
# adresse;passwort
sven@feicht.me;geheimes-passwort
info@klett.one;anderes-passwort
anna@web-waerts.de
```

Ohne Passwort fragt `mailadmin` es beim Import ab.

Probelauf – zeigt pro Postfach die Ordner, ihre Zuordnung und die Anzahl Nachrichten, ändert nichts:

```powershell
cd C:\Mailserver
.\mailadmin.exe import imap localhost postfaecher.txt --insecure-cert --dry-run
```

(`--insecure-cert`, weil das Zertifikat von SmarterMail auf den Webmail-Namen ausgestellt ist und nicht auf `localhost`.)

Dann der erste vollständige Import:

```powershell
.\mailadmin.exe import imap localhost postfaecher.txt --insecure-cert
```

Fehlende Domains und Postfächer werden dabei angelegt. Danach:

- Aliase anlegen: `mailadmin alias add info@feicht.me sven@feicht.me`
- DKIM-Einträge setzen: `mailadmin dns feicht.me` (für jede Domain) – der neue Selector stört SmarterMail nicht.
- TLS-Zertifikat für den Hostnamen bereitlegen (win-acme), Hostnamen in `appsettings.json` eintragen.

Den Import kann man beliebig oft wiederholen; es werden nur Nachrichten kopiert, die noch fehlen.

### 2. Umstellen (wenige Minuten)

1. **Port 25 von außen sperren:** alle eingehenden Firewall-Regeln für Port 25 deaktivieren – die von SmarterMail und die
   von `install.ps1` angelegte Regel "Mailserver SMTP 25". Fremde Mailserver bekommen keine Verbindung und versuchen es
   später erneut – dadurch geht nichts verloren.
2. Ggf. Passwörter in SmarterMail neu setzen (siehe oben) und in `postfaecher.txt` eintragen.
3. **Letzten Abgleich** starten – kopiert nur, was seit dem ersten Import dazugekommen ist:
   `.\mailadmin.exe import imap localhost postfaecher.txt --insecure-cert`
4. **SmarterMail-Dienst stoppen** und auf "Deaktiviert" stellen.
5. **Mailserver starten** (`Start-Service Mailserver`) und die Regel "Mailserver SMTP 25" wieder aktivieren
   (`Enable-NetFirewallRule -DisplayName "Mailserver SMTP 25"`). Die alten SmarterMail-Regeln bleiben aus.
6. Testen: Mail von außen empfangen, Mail nach außen senden (z. B. an mail-tester.com), Mailprogramm und Handy prüfen.

Bleibt der Hostname gleich (z. B. `mail.feicht.me`), müssen Mailprogramme nicht umgestellt werden – nur bei geänderten
Passwörtern das neue eintragen. Beim ersten Verbinden kann ein Mailprogramm die Ordner neu einlesen, weil die
Nachrichten auf dem neuen Server neue IDs haben.

### 3. Aufräumen

- `postfaecher.txt` löschen (enthält Passwörter im Klartext).
- SmarterMail-Daten noch einige Wochen als Sicherung aufbewahren, dann SmarterMail deinstallieren.
- Alte DKIM-Einträge von SmarterMail aus dem DNS entfernen, DMARC nach erfolgreichen Tests verschärfen.

### Zurück zu SmarterMail (falls nötig)

Mailserver stoppen, SmarterMail starten. Mails, die in der Zwischenzeit beim neuen Server eingegangen sind, liegen dann nur
dort (`data\mail`) und müssten per IMAP zurückkopiert werden.
