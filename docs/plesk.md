# Betrieb neben Plesk

Plesk verwaltet auf dem Server neben Websites auch Mail, Webmail und DNS. Solange dort SmarterMail eingetragen ist, legt
Plesk bei jeder neuen Domain oder jedem neuen Postfach Konten in SmarterMail an, startet den Dienst bei Bedarf wieder und
schreibt eigene DNS-Einträge. Damit der neue Mailserver sauber läuft, muss Plesk an diesen Stellen „stillgelegt“ werden.

## 1. Ports – wer belegt was?

| Port | Plesk / IIS / SmarterMail | Neuer Mailserver |
|---|---|---|
| 25, 465, 587 | SmarterMail (SMTP) | SMTP |
| 143, 993 (110, 995) | SmarterMail (IMAP, POP3) | IMAP (POP3 gibt es nicht) |
| 80, 443 | IIS (Websites, Webmail-Subdomains) | – |
| 8443, 8880 | Plesk-Oberfläche | – |
| 9998 | SmarterMail-Weboberfläche | – |
| **9443** | – | Weboberfläche / Webmail |

Auf einer IP-Adresse kann immer nur **ein** Dienst einen Port belegen. Solange SmarterMail läuft, startet der neue
Mailserver auf 25/587/465/143/993 nicht. Zum Testen vor dem Umzug die Ports in `appsettings.json` umstellen
(z. B. 2525, 2587, 2465, 2143, 2993) oder nur an eine zweite IP-Adresse binden (`ListenAddresses`).

## 2. Mail-Dienst in Plesk abschalten

1. **Werkzeuge & Einstellungen → Mail-Servereinstellungen**: Haken „Mail-Dienst aktivieren“ bei den Domains bzw. in den
   **Service-Paketen** (Registerkarte *Mail*) entfernen, damit neue Abonnements keine Postfächer in SmarterMail anlegen.
2. Pro Domain: **Websites & Domains → Domain → Mail → Mail-Einstellungen → „Mail-Dienst aktivieren“** ausschalten.
   *Wichtig:* Plesk passt dabei die DNS-Zone an (MX- und `mail.`-Einträge können verschwinden) – danach Schritt 4 prüfen.
   Erst ausschalten, **nachdem** die Postfächer importiert sind (`mailadmin import imap`), denn Plesk löscht dabei unter
   Umständen die Postfächer in SmarterMail.
3. **Werkzeuge & Einstellungen → Server-Komponenten**: Mailserver auf „keiner“ bzw. „Nicht installiert“ stellen, sofern
   angeboten; über **Plesk-Installer → Komponenten hinzufügen/entfernen** kann SmarterMail entfernt werden. Sonst reicht:
   ```powershell
   Stop-Service SmarterMail
   Set-Service SmarterMail -StartupType Disabled
   ```
4. **Werkzeuge & Einstellungen → Services Monitoring / Dienste-Verwaltung**: Überwachung für SmarterMail ausschalten,
   sonst startet Plesk den Dienst wieder. Nach **jedem Plesk-Update** kurz prüfen, ob SmarterMail wieder läuft.

## 3. Webmail

**Werkzeuge & Einstellungen → Webmail** bzw. pro Domain **Mail-Einstellungen → Webmail: „Kein Webmail“**.
Wer die gewohnte Adresse `webmail.<domain>` behalten will: in Plesk eine Subdomain `webmail.<domain>` als
**Weiterleitung** (Hosting-Typ „Weiterleitung“, 301) auf `https://mail.feicht.me:9443` anlegen.

Die SmarterMail-Weboberfläche (9998) muss gesperrt bleiben – Build 8853 hat bekannte, aktiv ausgenutzte Lücken.

## 4. DNS (wenn Plesk die Zonen verwaltet)

Plesk ist bei den meisten Installationen auch DNS-Server bzw. pflegt die Zone. Pro Domain unter
**Websites & Domains → DNS-Einstellungen** prüfen:

| Eintrag | Soll |
|---|---|
| `MX` | `mail.feicht.me` (bzw. der gemeinsame Hostname), Priorität 10 |
| `A mail.<…>` | IP des Servers |
| `TXT @` (SPF) | `v=spf1 mx a -all` – nur **ein** SPF-Eintrag pro Domain |
| `TXT default._domainkey` | Plesk-/SmarterMail-DKIM-Eintrag löschen, wenn er nicht mehr signiert wird |
| `TXT <selector>._domainkey` | neuer DKIM-Schlüssel aus `mailadmin dns <domain>` |
| `TXT _dmarc` | aus `mailadmin dns <domain>` |

`mailadmin dns <domain>` gibt alle empfohlenen Einträge aus. Das **DNS-Template** unter *Werkzeuge & Einstellungen →
DNS-Template* gleich mit anpassen, damit neue Domains die richtigen Einträge bekommen. Den **PTR-Eintrag** (Reverse DNS)
setzt der VPS-Anbieter; er muss auf `Mailserver:Hostname` zeigen.

## 5. Zertifikate

Der Mailserver sucht ohne `PfxPath` in den Windows-Speichern **My** und **WebHosting** (dort legt Plesk seine
Let's-Encrypt-Zertifikate ab) nach einem gültigen Zertifikat für `Mailserver:Hostname` – auch über alternative Namen und
Wildcards – und nimmt das neueste. Verlängerungen durch Plesk werden ohne Neustart übernommen.

* In Plesk für die Domain ein Let's-Encrypt-Zertifikat ausstellen und dabei **„mail.<domain>“ mit einschließen**.
* Der Mailserver liefert genau **ein** Zertifikat aus. Alle Mailprogramme sollten deshalb denselben Servernamen verwenden
  (z. B. `mail.feicht.me` auch für Postfächer von klett.one und web-waerts.de). Wer `mail.klett.one` usw. behalten will,
  braucht ein Zertifikat, das alle diese Namen enthält (z. B. per win-acme als PFX, `Mailserver:Tls:PfxPath`).
* Der Dienst läuft als LocalSystem und darf damit die privaten Schlüssel der Plesk-Zertifikate lesen.
* **Let's Encrypt im Mailserver** (*Verwaltung → Zertifikat*): Plesk kann ein **Platzhalter-Zertifikat** (`*.feicht.me`)
  nur über DNS-Einträge verlängern. Liegt das DNS nicht bei Plesk (z. B. bei Alfahosting), verlängert es sich nicht
  automatisch. Dann besser im Mailserver ein normales Zertifikat für die konkreten Namen ausstellen
  (z. B. `mail.feicht.me, webmail.feicht.me`, kein `*`). Port 80 teilt sich der Mailserver dabei mit dem IIS: Er meldet
  sich beim Windows-Dienst http.sys nur für `/.well-known/acme-challenge/` an (wie win-acme), alles andere beantwortet
  weiter der IIS. Der *Challenge-Ordner* bleibt leer. Der Mailserver ruft die Prüf-URL vor Let's Encrypt selbst ab und
  meldet sofort, wenn dort ein anderer Webserver antwortet (z. B. „Microsoft-IIS/10.0 (404)“); nur dann den Webroot der
  Website, die `mail.<domain>` auf Port 80 beantwortet, als Challenge-Ordner eintragen.

## 6. Mails von Websites (Kontaktformulare, WordPress, Shops)

PHP-`mail()` schickt auf Windows/Plesk an `localhost:25`. Der neue Mailserver nimmt dort – wie jeder Server im Internet –
nur Mails für eigene Postfächer an und lehnt eigene Absender-Domains ohne Anmeldung ab. Zwei Lösungen:

* **Empfohlen:** Die Website meldet sich an (WordPress-Plugin „WP Mail SMTP“ o. ä.): Server `mail.feicht.me`, Port 587,
  STARTTLS, ein eigenes Postfach wie `website@feicht.me`.
* **Für Altanwendungen:** lokale Adressen freigeben:
  ```json
  "Smtp": { "RelayNetworks": [ "127.0.0.1/32", "::1/128" ] }
  ```
  Diese Adressen dürfen dann ohne Anmeldung über Port 25 an beliebige Empfänger und mit beliebigem Absender versenden,
  ohne Spamprüfung; die Mails werden DKIM-signiert und im Verlauf als „Relay ohne Anmeldung“ protokolliert.
  **Nur Adressen eintragen, die vollständig unter eigener Kontrolle stehen** – eine gehackte Website kann sonst
  ungebremst Spam versenden. Niemals öffentliche Netze eintragen.

## 7. Firewall

`scripts/install.ps1` legt Regeln in der Windows-Firewall an. Ist die **Plesk-Firewall** aktiv (*Werkzeuge & Einstellungen
→ Firewall*), überschreibt sie beim Anwenden die Windows-Regeln: dort dieselben Ports (25, 465, 587, 143, 993, 9443)
freigeben. Die Ports 110/995 (POP3) und 9998 (SmarterMail-Web) können geschlossen werden.

## 8. Datensicherung

Die Plesk-Datensicherung kennt den neuen Mailserver nicht. `C:\Mailserver\data` (Datenbank, Mails, DKIM-Schlüssel,
`settings.json`, `keys`) muss separat gesichert werden, z. B. per geplanter Aufgabe mit Robocopy auf ein externes Ziel.
Die Datenbank vorher kurz konsistent machen, indem der Dienst gestoppt oder nachts gesichert wird.

## Reihenfolge für den Umzugstag

1. Neuen Mailserver installieren, Domains/Postfächer anlegen, mit Testports prüfen.
2. Postfächer importieren (`mailadmin import imap`), DNS-Einträge (DKIM, SPF, DMARC) vorbereiten.
3. SmarterMail stoppen und deaktivieren, Services Monitoring aus.
4. Ports in `appsettings.json` auf die Standardwerte, Dienst starten, letzten Abgleich-Import ausführen.
5. Mail-Dienst und Webmail in Plesk pro Domain abschalten, danach DNS-Zonen kontrollieren.
6. Senden und Empfangen testen (z. B. mail-tester.com), Mailprogramme auf `mail.feicht.me` umstellen.
