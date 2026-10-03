# Weboberfläche

Der Mailserver bringt eine Weboberfläche mit – für **Benutzer** (eigenes Postfach) und für **Administratoren** (der ganze
Server). Sie läuft im selben Windows-Dienst, braucht keine weitere Software und verwendet dasselbe TLS-Zertifikat wie SMTP/IMAP.

**Adresse:** `https://<hostname>:8443` (z. B. `https://mail.feicht.me:8443`). Port 443 ist auf Windows-Servern meist durch IIS
belegt; der Port lässt sich unter `Mailserver:Web:HttpsPort` ändern. Anmelden mit E-Mail-Adresse und Postfach-Passwort.

## Für Benutzer

| Seite | Inhalt |
|---|---|
| **Übersicht** | Speicherbelegung, Ordner mit Anzahl (ungelesen), zuletzt eingegangene Mails, Daten zur Einrichtung des Mailprogramms |
| **Regeln** | eigene Regeln anlegen, bearbeiten, (de)aktivieren, löschen – z. B. „Betreff enthält … → Spam / endgültig löschen / Ordner“. Regeln des Administrators werden zur Info angezeigt. |
| **Spam-Verlauf** | jede Mail von außen mit Spam-Score, Ergebnis (Posteingang, Spam, gelöscht), greifender Regel und den einzelnen Tests. Ein Klick auf **„Absender erlauben“** bzw. **„Als Spam einstufen“** legt sofort eine passende Regel an. |
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
| **Postfächer** | anlegen, Passwort setzen, Speicherlimit, aktivieren/deaktivieren, Admin-Recht, löschen; Ordnerübersicht |
| **Aliase** | Aliase und Weiterleitungen anlegen und löschen |
| **Alle Regeln** | Regeln für alle Postfächer, einzelne Domains oder Postfächer |
| **Warteschlange** | noch nicht zugestellte ausgehende Mails, sofort erneut versuchen, einzelne entfernen |
| **Verlauf** | alles, was der Server getan hat: Prüfung und Zustellung eingehender Mails, Versand durch Benutzer, Zustellung nach außen (inkl. Fehler), Rückmeldungen der Benutzer, fehlgeschlagene Anmeldungen (SMTP, IMAP, Web). Filter nach Zeitraum, Bereich, Ergebnis, Adresse/Betreff, IP und Score; Detailansicht aller Schritte einer Mail; **CSV-Export** |
| **Spam-Statistik** | Score-Verteilung, Häufigkeit jedes Tests in normalen Mails, Spam, Fehlalarmen und übersehenem Spam, Regel-Treffer, häufigste Spam-Absender und abgelehnte IPs, Optimierungshinweise |
| **Einstellungen** | Spamfilter (Schwellen, DMARC, SPF, vertrauenswürdige Netze, Blacklists), Greylisting, Verlauf (Aufbewahrung, Betreffzeilen), Login-Sperren, Zustellung inkl. Relay-Server. **Gilt nach wenigen Sekunden ohne Neustart.** |

Einstellungen aus der Weboberfläche werden in `data\settings.json` gespeichert und haben Vorrang vor `appsettings.json`.
Hostname, Ports, Zertifikat und maximale Mailgröße stehen weiterhin nur in `appsettings.json` und brauchen einen Neustart.

Schutz gegen Aussperren: Das eigene Admin-Postfach kann man sich nicht selbst deaktivieren, löschen oder die Admin-Rechte
entziehen; die Domain des eigenen Postfachs kann nicht gelöscht werden.

## Sicherheit

- Nur HTTPS; Cookies sind `Secure`, `HttpOnly`, `SameSite=Strict`, Formulare sind gegen CSRF geschützt.
- Fehlgeschlagene Anmeldungen zählen zur gemeinsamen IP-Sperre von SMTP und IMAP und erscheinen im Verlauf.
- Die Sitzung endet nach 60 Minuten Inaktivität (`Mailserver:Web:SessionTimeout`) und sofort, wenn Passwort,
  Aktiv-Status oder Admin-Recht des Postfachs geändert werden.
- Strenge Content-Security-Policy, kein JavaScript, keine externen Ressourcen, Clickjacking-Schutz, HSTS.
- Die Schlüssel für Sitzungs-Cookies liegen in `data\keys` (unter Windows zusätzlich mit DPAPI geschützt).

Wer die Weboberfläche nicht aus dem Internet erreichbar machen will: Firewall-Regel „Mailserver Web 8443“ auf bestimmte IPs
beschränken, `Mailserver:Web:ListenAddresses` auf `127.0.0.1` setzen (dann nur per RDP auf dem Server) oder
`Mailserver:Web:Enabled` auf `false`.

## Grenzen

- Kein Webmail (Mails lesen/schreiben im Browser) – dafür bleiben Outlook, Thunderbird und Smartphone-Apps.
- Keine Zwei-Faktor-Anmeldung.
