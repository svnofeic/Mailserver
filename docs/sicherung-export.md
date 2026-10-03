# Sicherung und Umzug auf einen neuen Server

`mailadmin export` sichert Postfächer von einem IMAP-Server – SmarterMail oder dieser Mailserver selbst – in offene
Formate, die auch ohne SmarterMail lesbar bleiben:

| Inhalt | Format | Ablage |
|---|---|---|
| E-Mails | eine `.eml`-Datei pro Nachricht, Dateidatum = Empfangsdatum | `<ziel>\<adresse>\Mail\<Ordner>\` |
| Ordner, Gelesen/Markiert/Beantwortet, Schlagwörter | `export.json` | `<ziel>\<adresse>\export.json` |
| Kontakte (CardDAV) | ein `.vcf` pro Adressbuch | `<ziel>\<adresse>\Kontakte\` |
| Kalender und Aufgaben (CalDAV) | ein `.ics` pro Kalender | `<ziel>\<adresse>\Kalender\` |

`.eml`-Dateien öffnen Outlook, Thunderbird und Windows Mail per Doppelklick; `.vcf` und `.ics` lassen sich in jedes
Adressbuch und jeden Kalender importieren. Nicht enthalten sind SmarterMail-Regeln, Signaturen, Notizen und
Einstellungen – diese vor dem Abschalten per Screenshot festhalten.

## 1. Sichern auf dem alten Server

`mailadmin.exe` braucht dafür keine Installation und keine Datenbank: den veröffentlichten Ordner (oder nur
`mailadmin.exe` mit seinen Dateien) auf den alten Server kopieren.

Datei `postfaecher.txt` wie beim Import anlegen (eine Zeile `adresse;passwort` pro Postfach, ohne Passwort wird gefragt).
Dann:

```powershell
.\mailadmin.exe export localhost postfaecher.txt D:\Sicherung\Mail --insecure-cert
```

* Ausgabe pro Postfach: Ordner, Anzahl neu gesicherter Nachrichten, Kontakte und Termine.
* **Mehrfach ausführbar:** Ein zweiter Lauf holt nur neue Nachrichten und aktualisiert Gelesen/Markiert. So kann man
  früh sichern und kurz vor dem Abschalten noch einmal.
* Bricht die Verbindung ab, einfach erneut starten.

### Kontakte und Kalender

Ohne Angabe sucht `export` den CalDAV/CardDAV-Zugang unter `https://<host>/` und unter `http://<host>:9998/` (eingebauter
Webserver von SmarterMail). Läuft SmarterMail-Webmail unter einer anderen Adresse, diese angeben:

```powershell
.\mailadmin.exe export localhost postfaecher.txt D:\Sicherung\Mail --insecure-cert --dav https://webmail.feicht.me/
```

Erscheint „Kontakte/Kalender nicht gesichert“, ist CalDAV/CardDAV für die Domain in SmarterMail abgeschaltet (in den
Domain-Einstellungen bzw. Funktionen aktivieren) oder die Adresse stimmt nicht. Notfalls Kontakte und Kalender pro
Benutzer in SmarterMail-Webmail als `.vcf`/`.ics` exportieren. `--no-dav` sichert nur die Mails.

### Was ist mit dem SmarterMail-Archiv?

Das Archiv muss nicht übernommen werden; es enthält nur den Mailverkehr ab dem Einschalten, nicht die Postfächer.
Wer einzelne Jahrgänge aufheben möchte, kopiert die gewünschten Ordner einfach mit.

## 2. Sicherung prüfen

* Stichproben: einige `.eml`-Dateien per Doppelklick öffnen, auch mit Anhängen.
* Anzahl vergleichen: die Ausgabe von `export` mit der Anzahl in SmarterMail-Webmail.
* Die Sicherung **vom Server herunterladen** (z. B. als ZIP). Sie enthält alle Mails im Klartext – sicher aufbewahren.
* `postfaecher.txt` danach löschen.

## 3. Einspielen auf dem neuen Server

Mailserver auf dem neuen Server installieren (siehe README), Sicherungsordner hinüberkopieren, dann:

```powershell
cd C:\Mailserver
.\mailadmin.exe import export D:\Sicherung\Mail --dry-run      # zeigt nur, was passieren würde
.\mailadmin.exe import export D:\Sicherung\Mail postfaecher.txt
```

* Fehlende Domains und Postfächer werden angelegt. Das Passwort kommt aus `postfaecher.txt`; fehlt die Datei, wird es
  pro Postfach abgefragt.
* Ordner werden wie beim IMAP-Import zugeordnet („Sent Items“ → Gesendet, „Deleted Items“ → Papierkorb usw.).
* Gelöschte, aber noch nicht entfernte Nachrichten (`\Deleted`) werden nicht eingespielt.
* **Wiederholbar:** Bereits vorhandene Nachrichten werden übersprungen – auch solche, die vorher direkt per
  `import imap` übernommen wurden.

Kontakte (`.vcf`) und Kalender (`.ics`) importiert jeder Benutzer in sein Mailprogramm bzw. Handy, da der Mailserver
selbst keine Kontakte und Kalender verwaltet.

## Ablauf beim Umzug auf einen neuen Server

1. VPS-Snapshot des alten Servers.
2. `mailadmin export` auf dem alten Server, Sicherung herunterladen und prüfen.
3. Neuen Server (Windows Server 2022/2025, ohne Plesk) aufsetzen, Mailserver installieren, Zertifikat einrichten
   (win-acme, siehe README).
4. `mailadmin import export` – damit sind alle Postfächer mit Inhalt da. Aliase und Regeln anlegen,
   `mailadmin dns <domain>` für DKIM.
5. Am Umstelltag: auf dem alten Server Port 25 sperren, `mailadmin export` noch einmal laufen lassen (holt nur das Neue),
   den neuen Teil hinüberkopieren und `mailadmin import export` wiederholen.
6. DNS umstellen (A-Eintrag von `mail.…`, MX, SPF mit neuer IP), PTR beim Anbieter setzen. Den alten Server erst nach
   einigen Tagen abschalten – solange alte DNS-Einträge noch zwischengespeichert sind, kann dort noch Post ankommen
   (bei Bedarf Schritt 5 wiederholen).

Auf einem Server ohne Plesk und ohne IIS ist Port 443 frei: Die Weboberfläche kann dann mit
`Mailserver:Web:HttpsPort = 443` unter `https://mail.feicht.me` laufen (Firewall-Regel entsprechend anpassen).
