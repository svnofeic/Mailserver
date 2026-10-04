# Weboberfläche

Der Mailserver bringt eine Weboberfläche mit – für **Benutzer** (eigenes Postfach) und für **Administratoren** (der ganze
Server). Sie läuft im selben Windows-Dienst, braucht keine weitere Software und verwendet dasselbe TLS-Zertifikat wie SMTP/IMAP.

**Adresse:** `https://<hostname>:9443` (z. B. `https://mail.feicht.me:9443`). Port 443 ist auf Windows-Servern meist durch IIS
belegt, 8443 und 8880 durch Plesk; der Port lässt sich unter `Mailserver:Web:HttpsPort` ändern. Anmelden mit E-Mail-Adresse und Postfach-Passwort.

## Für Benutzer

| Seite | Inhalt |
|---|---|
| **Mail** | Webmail: Ordner (inkl. Anlegen, Umbenennen, Löschen), Nachrichtenliste mit Suche und Mehrfachauswahl, Lesen mit Anhängen, Antworten, Allen antworten, Weiterleiten (mit Anhängen), neue Mail mit Formatierungs-Editor und Anhängen, Entwürfe, Löschen, Verschieben, Spam/Kein Spam. Nach der Anmeldung landet man hier. |
| **Übersicht** | Speicherbelegung, Ordner mit Anzahl (ungelesen), zuletzt eingegangene Mails, Daten zur Einrichtung des Mailprogramms |
| **Regeln** | eigene Regeln anlegen, bearbeiten, (de)aktivieren, löschen – z. B. „Betreff enthält … → Spam / endgültig löschen / Ordner“. Regeln des Administrators werden zur Info angezeigt. |
| **Spam-Verlauf** | jede Mail von außen mit Spam-Score, Ergebnis (Posteingang, Spam, gelöscht), greifender Regel und den einzelnen Tests. Ein Klick auf **„Absender erlauben“** bzw. **„Als Spam einstufen“** legt sofort eine passende Regel an. |
| **Abwesenheit** | Automatische **Weiterleitung** an bis zu 10 Adressen (wahlweise mit oder ohne Kopie im Postfach) und **Abwesenheitsnotiz** mit Zeitraum, Betreff und Text. Solange etwas davon aktiv ist, erinnert ein Hinweis auf jeder Seite daran. |
| **Passwort** | Passwort ändern (gilt auch für IMAP/SMTP). Alle anderen angemeldeten Browser werden abgemeldet. |

## Für Administratoren

Admin-Rechte vergibt man auf der Kommandozeile oder im Admin-Bereich selbst:

```powershell
mailadmin user admin sven@feicht.me on
```

| Seite | Inhalt |
|---|---|
| **Admin** | Kennzahlen (Domains, Postfächer, Warteschlange; letzte 24 h: eingegangen, Spam, abgelehnt, Greylisting, versendet, Zustellfehler, Fehl-Logins), Hinweise zum Spamfilter, Zertifikat mit Ablaufwarnung |
| **Domains** | anlegen (mit DKIM-Schlüssel), löschen, **benötigte DNS-Einträge** zum Kopieren, DKIM-Schlüsselwechsel |
| **Postfächer** | anlegen, Passwort setzen, Speicherlimit, aktivieren/deaktivieren, Admin-Recht, löschen; Ordnerübersicht; Weiterleitung und Abwesenheitsnotiz für jedes Postfach (z. B. Vertretung bei Krankheit). Die Liste zeigt „Weiterleitung“ / „abwesend“ / „Versand gesperrt“ an. Karte **Versand**: Verbrauch der letzten Stunde/24 h, eigene Limits für das Postfach (leer = Standard, 0 = ohne Limit), „Versand freigeben“ nach einer Sperre. |
| **Zertifikat** | Verwendetes TLS-Zertifikat (Namen, Aussteller, Ablauf). **Let's Encrypt**: automatisch ausstellen und verlängern – E-Mail, Hostnamen, Testmodus, optional Challenge-Ordner für IIS; „Jetzt ausstellen“, letzter Versuch mit Fehlermeldung. Ohne Zertifikat läuft die Weboberfläche mit einem Notfall-Zertifikat (Browser-Warnung), damit man hier eines anfordern kann. |
| **Aliase** | Aliase und Weiterleitungen anlegen und löschen |
| **Alle Regeln** | Regeln für alle Postfächer, einzelne Domains oder Postfächer |
| **Warteschlange** | noch nicht zugestellte ausgehende Mails, sofort erneut versuchen, einzelne entfernen |
| **Verlauf** | alles, was der Server getan hat: Prüfung und Zustellung eingehender Mails, Versand durch Benutzer, Zustellung nach außen (inkl. Fehler), Rückmeldungen der Benutzer, fehlgeschlagene Anmeldungen (SMTP, IMAP, Web). Filter nach Zeitraum, Bereich, Ergebnis, Adresse/Betreff, IP und Score; Detailansicht aller Schritte einer Mail; **CSV-Export** |
| **Spam-Statistik** | Score-Verteilung, Häufigkeit jedes Tests in normalen Mails, Spam, Fehlalarmen und übersehenem Spam, Regel-Treffer, häufigste Spam-Absender und abgelehnte IPs, Optimierungshinweise |
| **Datensicherung** | nächtliche Sicherung einrichten (Zielordner, auch Netzwerkfreigabe mit Anmeldung, Uhrzeit, Aufbewahrung), „Jetzt sichern“, Verlauf und vorhandene Sicherungen – siehe [datensicherung.md](datensicherung.md) |
| **Diagnose** | prüft auf einen Blick, ob alles für eine zuverlässige Zustellung stimmt: MX, SPF, DKIM und DMARC jeder Domain, Reverse DNS und Blacklists (Spamhaus, SpamCop, Barracuda, PSBL) der Server-IP, offene Ports, ausgehender Port 25, Zertifikat, Warteschlange, Speicherplatz, Datensicherung, Virenscanner und neue Versionen – jeweils mit Hinweis, was zu tun ist. Gleiches auf der Kommandozeile: `mailadmin diagnose` |
| **Einstellungen** | Spamfilter (Schwellen, DMARC, SPF, vertrauenswürdige Netze, Blacklists), Greylisting, Verlauf (Aufbewahrung, Betreffzeilen), **Virenschutz** (Scanner, blockierte Dateitypen, Umgang mit Makro-Dokumenten und Scanner-Ausfall, Knopf „EICAR-Testdatei scannen“), **Versandlimits**, Login-Sperren, Zustellung inkl. Relay-Server. **Gilt nach wenigen Sekunden ohne Neustart.** |

Einstellungen aus der Weboberfläche werden in `data\settings.json` gespeichert und haben Vorrang vor `appsettings.json`.
Hostname, Ports, Zertifikat und maximale Mailgröße stehen weiterhin nur in `appsettings.json` und brauchen einen Neustart.

Schutz gegen Aussperren: Das eigene Admin-Postfach kann man sich nicht selbst deaktivieren, löschen oder die Admin-Rechte
entziehen; die Domain des eigenen Postfachs kann nicht gelöscht werden.

## Weiterleitung und Abwesenheitsnotiz – wie sie arbeiten

**Weiterleitung**
- Weitergeleitet wird jede eingehende Mail, die nicht als Spam erkannt oder per Regel in den Spam-Ordner sortiert wurde.
  Spam bleibt im Spam-Ordner, auch wenn „ohne Kopie“ eingestellt ist – das schützt den Ruf des Servers beim Empfänger.
- Als Absender im Umschlag steht das weiterleitende Postfach (wie bei Aliasen), daher besteht die SPF-Prüfung beim
  Empfänger; die DKIM-Signatur des ursprünglichen Absenders bleibt erhalten. Unzustellbarkeitsmeldungen kommen ins Postfach.
- Ziele auf diesem Server bekommen die Mail direkt. Deren eigene Weiterleitung greift dabei nicht noch einmal – zwei
  Postfächer, die sich gegenseitig weiterleiten, erzeugen also keine Endlosschleife.

**Abwesenheitsnotiz** (nach RFC 3834)
- Gilt im eingestellten Zeitraum (beide Daten einschließlich, ohne Datum sofort bzw. bis zum Ausschalten). Nach dem
  Bis-Datum schaltet sie sich selbst aus; Betreff und Text bleiben für das nächste Mal gespeichert.
- Jeder Absender bekommt sie höchstens einmal pro eingestelltem Zeitraum (Standard 7 Tage). Beim Ausschalten, beim
  automatischen Ablauf und bei einem neuen Zeitraum wird das vergessen – bei der nächsten Abwesenheit bekommt jeder sie wieder.
- Keine Antwort auf Spam, Newsletter und Mailinglisten (List-Id, Precedence: bulk), automatische Mails (Auto-Submitted,
  andere Abwesenheitsnotizen), Systemadressen (mailer-daemon, noreply …) und Mails, in denen die Adresse nur in BCC steht.
- Die Antwort geht an den tatsächlichen Absender (Return-Path), trägt `Auto-Submitted: auto-replied`, ist DKIM-signiert
  und hat einen leeren Umschlag-Absender – sie kann daher keine Schleife mit einem anderen Autoresponder auslösen.
- Im Verlauf (Admin) erscheinen beide als eigene Einträge: „weitergeleitet“ und „Abwesenheitsnotiz“.

Auch per Kommandozeile:

```powershell
.\mailadmin.exe forward sven@feicht.me sven2707@live.com            # mit Kopie im Postfach
.\mailadmin.exe forward sven@feicht.me sven2707@live.com --no-copy
.\mailadmin.exe forward sven@feicht.me off
.\mailadmin.exe autoreply sven@feicht.me on --subject "Im Urlaub" --text "Bin bis 18.10. nicht erreichbar." --until 2026-10-18
.\mailadmin.exe autoreply sven@feicht.me off
```

## Sicherheit

- Nur HTTPS; Cookies sind `Secure`, `HttpOnly`, `SameSite=Strict`, Formulare sind gegen CSRF geschützt.
- Fehlgeschlagene Anmeldungen zählen zur gemeinsamen IP-Sperre von SMTP und IMAP und erscheinen im Verlauf.
- Die Sitzung endet nach 60 Minuten Inaktivität (`Mailserver:Web:SessionTimeout`) und sofort, wenn Passwort,
  Aktiv-Status oder Admin-Recht des Postfachs geändert werden.
- Strenge Content-Security-Policy: JavaScript nur vom eigenen Server (einzig der Mail-Editor), keine externen Ressourcen,
  Clickjacking-Schutz, HSTS.
- Die Schlüssel für Sitzungs-Cookies liegen in `data\keys` (unter Windows zusätzlich mit DPAPI geschützt).

Wer die Weboberfläche nicht aus dem Internet erreichbar machen will: Firewall-Regel „Mailserver Web 9443“ auf bestimmte IPs
beschränken, `Mailserver:Web:ListenAddresses` auf `127.0.0.1` setzen (dann nur per RDP auf dem Server) oder
`Mailserver:Web:Enabled` auf `false`.

## Webmail

Für unterwegs, wenn das eigene Gerät nicht greifbar ist. Alles läuft über denselben Speicher wie IMAP – gelesene, verschobene
oder gelöschte Mails sind sofort auch in Outlook, Thunderbird und am Handy so.

- **Sichere Anzeige:** HTML-Mails werden serverseitig bereinigt (Skripte, Formulare, Event-Handler, `javascript:`-Links, eingebettete
  Frames werden entfernt) und zusätzlich in einem abgeschotteten Rahmen ohne Skriptausführung angezeigt.
- **Externe Bilder** sind standardmäßig blockiert, weil sie dem Absender verraten, dass und wann die Mail gelesen wurde. Ein Klick auf
  „Bilder anzeigen“ lädt sie für diese Mail nach. In der Mail eingebettete Bilder werden immer angezeigt.
- **Anhänge** werden immer als Download ausgeliefert, nie im Browser geöffnet.
- **Senden** geht denselben Weg wie aus dem Mailprogramm: nur mit eigener Adresse oder eigenem Alias als Absender, DKIM-Signatur,
  Kopie im Ordner „Gesendet“, Eintrag im Verlauf. Höchstens 100 Empfänger pro Nachricht; Anhänge bis zu ¾ der maximalen Mailgröße.
- **Spam/Kein Spam** verschiebt die Mail und zählt als Rückmeldung für die Spam-Statistik.
- **Editor:** fett, kursiv, unterstrichen, durchgestrichen, Überschrift, Listen, Zitat, Einzug, Links, Textfarbe, Formatierung
  entfernen, rückgängig/wiederholen (auch Strg+B/I/U/Z). Formatierte Mails gehen als HTML mit zusätzlicher Textversion raus; der Server
  bereinigt das HTML vor dem Versand. Beim Antworten und Weiterleiten wird die Originalnachricht mit ihrer Formatierung zitiert.
  Der Editor ist ein kleines eigenes Skript, das vom Mailserver selbst kommt (keine fremden Bibliotheken, kein CDN); die
  Sicherheitsrichtlinie der Seite erlaubt nur Skripte vom eigenen Server. Ohne JavaScript bleibt ein einfaches Textfeld.
- **Ordner verwalten** (Link unter der Ordnerliste): Ordner und Unterordner anlegen, umbenennen und löschen. Systemordner
  (Posteingang, Gesendet, Entwürfe, Papierkorb, Spam) sind geschützt. Beim Löschen wandern enthaltene Mails in den Papierkorb;
  Unterordner müssen zuerst gelöscht werden. Beim Umbenennen werden Regeln, die Mails in den Ordner verschieben, automatisch angepasst.

## Grenzen

- Bilder lassen sich nicht in den Text einfügen, nur als Anhang senden (eingefügte Bilder werden beim Senden entfernt).
- Keine Zwei-Faktor-Anmeldung.
