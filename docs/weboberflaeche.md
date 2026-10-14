# Weboberfläche

Der Mailserver bringt eine Weboberfläche mit – für **Benutzer** (eigenes Postfach) und für **Administratoren** (der ganze
Server). Sie läuft im selben Windows-Dienst, braucht keine weitere Software und verwendet dasselbe TLS-Zertifikat wie SMTP/IMAP.

**Adresse:** `https://<hostname>:9443` (z. B. `https://mail.feicht.me:9443`). Port 443 ist auf Windows-Servern meist durch IIS
belegt, 8443 und 8880 durch Plesk; der Port lässt sich unter `Mailserver:Web:HttpsPort` ändern. Anmelden mit E-Mail-Adresse und Postfach-Passwort.

**Design:** dunkel mit Glas-Effekt als Standard; unten in der Seitenleiste lässt sich auf **Hell** umschalten (wird pro
Browser gemerkt). Die Admin-Übersicht zeigt Kennzahlen mit Trend gegenüber der Vorwoche, Verläufe über 14 Tage
(Mailverkehr, Spam, abgelehnte Verbindungen, Fehl-Logins), die Verteilung des Eingangs, die letzte Aktivität, die
häufigsten Spam-Absender und abgelehnten Adressen, den Systemzustand (Zertifikat, Sicherung, Virenschutz,
Warteschlange) sowie **neue Mails pro Postfach**: alle Postfächer mit der Zahl ungelesener Mails je Ordner (Posteingang
zuerst, Spam und Papierkorb zuletzt) und „Öffnen“ zum direkten Wechsel ins Postfach. Alle Diagramme werden auf dem Server gezeichnet – kein JavaScript, keine externen Dienste.

## Als App installieren

Webmail und Verwaltung lassen sich als App installieren (Progressive Web App) – mit eigenem Symbol, ohne Adressleiste,
mit der Zahl ungelesener Mails am App-Symbol (Android, Windows, macOS) und einer Seite „Keine Verbindung“, wenn der Server
nicht erreichbar ist.

| Gerät | So geht's |
|---|---|
| Android (Chrome) | Weboberfläche öffnen → Menü ⋮ → **App installieren** |
| iPhone / iPad (Safari) | Weboberfläche öffnen → Teilen-Symbol → **Zum Home-Bildschirm** |
| Windows / macOS (Edge, Chrome) | Symbol **App installieren** rechts in der Adressleiste |

- Voraussetzung ist ein **gültiges Zertifikat** für den Hostnamen (z. B. Let's Encrypt unter Verwaltung → Zertifikat);
  mit einem selbst ausgestellten Zertifikat bieten die Browser die Installation nicht an.
- **Aktualisieren:** In der App oben auf der Seite nach unten ziehen, bis der Kreis mit dem Pfeil farbig wird, und
  loslassen – wie im Browser. Mit ungespeicherten Eingaben (z. B. einer angefangenen Mail) passiert dabei nichts, damit
  nichts verloren geht. Mailliste und Übersichten laden sich außerdem von selbst neu, wenn man nach über einer Minute
  in die App zurückkehrt.
- Gedrückt halten auf dem App-Symbol zeigt Verknüpfungen: Neue Mail, Übersicht, Server-Übersicht.
- Unter Windows, macOS und Android kann die App als Mailprogramm für **mailto:-Links** eingetragen werden; der Browser
  fragt beim ersten Mal nach. Empfänger, Cc, Betreff und Text aus dem Link stehen dann schon in „Neue Mail“.
- Zwischengespeichert werden nur die Design-Dateien und die Offline-Seite – keine Mails. Nach dem Abmelden bleibt auf
  dem Gerät nichts Persönliches zurück.

### Push-Benachrichtigungen

Unter **Benachrichtigungen** → **Auf diesem Gerät einschalten** meldet sich das Gerät für Benachrichtigungen über neue
Mails an – auch wenn Browser bzw. App gerade geschlossen sind. Die Benachrichtigung zeigt Absender und Betreff; ein Tipp
darauf öffnet die Mail.

- Benachrichtigt wird nur über Mails, die ungelesen im **Posteingang** landen – nicht über Spam und nicht über Mails,
  die eine Regel in einen anderen Ordner verschiebt oder als gelesen markiert.
- **iPhone/iPad:** erst die App zum Home-Bildschirm hinzufügen (siehe oben, ab iOS 16.4), dann in der App einschalten.
  Im normalen Safari-Tab bietet Apple keine Benachrichtigungen an.
- Jedes Gerät wird einzeln eingeschaltet und erscheint in der Geräteliste; dort lässt es sich wieder entfernen.
  **Test senden** schickt eine Probe an alle eingeschalteten Geräte.
- Die Benachrichtigungen laufen über den Push-Dienst des Browser-Herstellers (Google, Apple, Mozilla, Microsoft).
  Der Inhalt ist Ende-zu-Ende verschlüsselt (RFC 8291) – der Push-Dienst sieht weder Absender noch Betreff.
  Der Server braucht dafür ausgehende HTTPS-Verbindungen (Port 443).
- Auch hier ist ein **gültiges Zertifikat** Voraussetzung. Der Server-Schlüssel (VAPID) liegt in `data\push` und wird
  mitgesichert; geht er verloren, müssen die Geräte einmal neu eingeschaltet werden.
- **Test senden** wartet auf die Antwort der Push-Dienste und zeigt sie sofort an; der letzte Fehler steht außerdem beim
  Gerät. Häufige Ursachen: *nicht erreichbar* → ausgehende Verbindungen (Port 443) in der Firewall erlauben;
  *antwortet 401/403* → Uhrzeit des Servers prüfen, sonst das Gerät entfernen und neu einschalten.

## Für Benutzer

| Seite | Inhalt |
|---|---|
| **Mail** | Webmail: Ordner (inkl. Anlegen, Umbenennen, Löschen), Nachrichtenliste mit Suche und Mehrfachauswahl („Alle“ wählt die ganze Seite), Seitenauswahl mit direkt anklickbaren Seitenzahlen und „Gehe zu Seite“, **Papierkorb/Spam leeren** mit einem Klick, Lesen mit Anhängen, Antworten, Allen antworten, Weiterleiten (mit Anhängen), neue Mail mit Formatierungs-Editor und Anhängen (einzeln hinzufügen, hineinziehen, wieder entfernen; beim Weiterleiten einzelne Anhänge abwählen), Entwürfe, Löschen, Verschieben, Spam/Kein Spam. Nach der Anmeldung landet man hier. |
| **Übersicht** | Speicherbelegung, Ordner mit Anzahl (ungelesen), zuletzt eingegangene Mails, Daten zur Einrichtung des Mailprogramms |
| **Regeln** | eigene Regeln anlegen, bearbeiten, (de)aktivieren, löschen – z. B. „Betreff enthält … → Spam / endgültig löschen / Ordner“. Regeln des Administrators werden zur Info angezeigt. |
| **Spam-Verlauf** | jede Mail von außen mit Spam-Score, Ergebnis (Posteingang, Spam, gelöscht), greifender Regel und den einzelnen Tests. Ein Klick auf **„Absender erlauben“** bzw. **„Als Spam einstufen“** legt sofort eine passende Regel an. |
| **Abwesenheit** | Automatische **Weiterleitung** an bis zu 10 Adressen (wahlweise mit oder ohne Kopie im Postfach) und **Abwesenheitsnotiz** mit Zeitraum, Betreff und Text. Solange etwas davon aktiv ist, erinnert ein Hinweis auf jeder Seite daran. |
| **Fremde Konten** | Adressen bei anderen Anbietern (GMX, eigener Provider …) einbinden: Mails abrufen und mit der Adresse senden (siehe unten) |
| **Signatur** | eine Signatur pro Absenderadresse (eigene Adresse, Aliase, fremde Konten) – wahlweise auch bei Antworten und Weiterleitungen (siehe unten) |
| **Benachrichtigungen** | Push-Benachrichtigungen bei neuer Mail pro Gerät ein- und ausschalten, Test senden (siehe oben) |
| **Passwort** | Passwort ändern (gilt auch für IMAP/SMTP). Alle anderen angemeldeten Browser werden abgemeldet. **Ersatz-Adresse** für „Passwort vergessen“ eintragen (siehe unten). |
| **Sicherheit** | **Zwei-Faktor-Anmeldung** mit Authenticator-App einrichten, Wiederherstellungscodes, vertrauenswürdige Geräte (siehe unten) |
| **Einrichtung** | **Profil für iPhone, iPad und Mac** zum Herunterladen (richtet das Postfach mit einem Tipp ein) und die Daten für alle anderen Mailprogramme (siehe unten) |

## Für Administratoren

Admin-Rechte vergibt man auf der Kommandozeile oder im Admin-Bereich selbst:

```powershell
mailadmin user admin sven@feicht.me on
```

| Seite | Inhalt |
|---|---|
| **Admin** | Kennzahlen (Domains, Postfächer, Warteschlange; letzte 24 h: eingegangen, Spam, abgelehnt, Greylisting, versendet, Zustellfehler, Fehl-Logins), Hinweise zum Spamfilter, Zertifikat mit Ablaufwarnung |
| **Domains** | anlegen (mit DKIM-Schlüssel), löschen, **benötigte DNS-Einträge** zum Kopieren, DKIM-Schlüsselwechsel |
| **Postfächer** | anlegen, Passwort setzen, Speicherlimit, aktivieren/deaktivieren, Admin-Recht, löschen; Ordnerübersicht; Weiterleitung und Abwesenheitsnotiz für jedes Postfach (z. B. Vertretung bei Krankheit). Die Liste zeigt „Weiterleitung“ / „abwesend“ / „Versand gesperrt“ an. Karte **Versand**: Verbrauch der letzten Stunde/24 h, eigene Limits für das Postfach (leer = Standard, 0 = ohne Limit), „Versand freigeben“ nach einer Sperre. **Als Benutzer anmelden** bzw. „Öffnen“ in der Liste: siehe unten. |
| **Zertifikat** | Verwendetes TLS-Zertifikat (Namen, Aussteller, Ablauf). **Let's Encrypt**: automatisch ausstellen und verlängern – E-Mail, Hostnamen, Testmodus, optional Challenge-Ordner (unter Windows meist unnötig, Port 80 wird über http.sys mit dem IIS geteilt); „Jetzt ausstellen“, letzter Versuch mit Fehlermeldung. Ohne Zertifikat läuft die Weboberfläche mit einem Notfall-Zertifikat (Browser-Warnung), damit man hier eines anfordern kann. |
| **Aliase** | Aliase und Weiterleitungen anlegen und löschen |
| **Alle Regeln** | Regeln für alle Postfächer, einzelne Domains oder Postfächer |
| **Warteschlange** | noch nicht zugestellte ausgehende Mails, sofort erneut versuchen, einzelne entfernen |
| **Verlauf** | alles, was der Server getan hat: Prüfung und Zustellung eingehender Mails, Versand durch Benutzer, Zustellung nach außen (inkl. Fehler), Rückmeldungen der Benutzer, fehlgeschlagene Anmeldungen (SMTP, IMAP, Web). Filter nach Zeitraum, Bereich, Ergebnis, Adresse/Betreff, IP und Score; Detailansicht aller Schritte einer Mail; **CSV-Export** |
| **Spam-Statistik** | Score-Verteilung, Häufigkeit jedes Tests in normalen Mails, Spam, Fehlalarmen und übersehenem Spam, Regel-Treffer, häufigste Spam-Absender und abgelehnte IPs, Optimierungshinweise |
| **IP-Sperren** | Adressen, die gerade wegen Fehl-Logins gesperrt sind (freigeben oder dauerhaft sperren), die Adressen mit den meisten Fehl-Logins der letzten 24 Stunden samt versuchten Benutzernamen, dauerhafte bzw. befristete Sperren und Ausnahmen („nie sperren“). Die eigene Adresse lässt sich nicht sperren. |
| **Datensicherung** | nächtliche Sicherung einrichten (Zielordner, Netzwerkfreigabe mit Anmeldung, **Microsoft OneDrive** oder **pCloud** mit „Verbinden“; Uhrzeit, Aufbewahrung), „Jetzt sichern“, Verlauf und vorhandene Sicherungen – siehe [datensicherung.md](datensicherung.md) |
| **Diagnose** | prüft auf einen Blick, ob alles für eine zuverlässige Zustellung stimmt: MX, SPF, DKIM und DMARC jeder Domain, Reverse DNS und Blacklists (Spamhaus, SpamCop, Barracuda, PSBL) der Server-IP, offene Ports, ausgehender Port 25, Zertifikat, Warteschlange, Speicherplatz, Datensicherung, Virenscanner und neue Versionen – jeweils mit Hinweis, was zu tun ist. Gleiches auf der Kommandozeile: `mailadmin diagnose` |
| **Einstellungen** | Spamfilter (Schwellen, DMARC, SPF, vertrauenswürdige Netze, Blacklists), Greylisting, Verlauf (Aufbewahrung, Betreffzeilen), **Virenschutz** (Scanner, blockierte Dateitypen, Umgang mit Makro-Dokumenten und Scanner-Ausfall, Knopf „EICAR-Testdatei scannen“), **Versandlimits**, Login-Sperren inkl. automatischer Sperre für mehrere Tage, Zustellung inkl. Relay-Server. **Gilt nach wenigen Sekunden ohne Neustart.** |

Einstellungen aus der Weboberfläche werden in `data\settings.json` gespeichert und haben Vorrang vor `appsettings.json`.
Hostname, Ports, Zertifikat und maximale Mailgröße stehen weiterhin nur in `appsettings.json` und brauchen einen Neustart.

Schutz gegen Aussperren: Das eigene Admin-Postfach kann man sich nicht selbst deaktivieren, löschen oder die Admin-Rechte
entziehen; die Domain des eigenen Postfachs kann nicht gelöscht werden.

## Passwort vergessen

Auf der Anmeldeseite führt **„Passwort vergessen?“** zu einem Formular: Postfach-Adresse eingeben, der Link zum Setzen eines
neuen Passworts geht an die **Ersatz-Adresse** des Postfachs (eine externe Adresse, z. B. bei GMX oder Gmail).

- **Ersatz-Adresse eintragen:** Benutzer unter *Passwort* (mit dem aktuellen Passwort); sie gilt erst, wenn der
  Bestätigungslink an diese Adresse angeklickt wurde. Administratoren tragen sie unter *Postfächer → Postfach* ein – dann
  gilt sie sofort.
- **Der Link** gilt 30 Minuten und nur einmal; ein neu angeforderter Link macht ältere ungültig. Gespeichert wird nur eine
  Prüfsumme. Erst „Passwort setzen“ ändert etwas – das bloße Öffnen (auch durch Link-Scanner von Mailanbietern) nicht.
- Das Formular antwortet **immer gleich** – es verrät nicht, ob es ein Postfach oder eine Ersatz-Adresse gibt. Höchstens
  3 Anfragen pro Postfach und 10 pro IP-Adresse und Stunde.
- Nach dem Zurücksetzen enden alle Sitzungen, Mailprogramme brauchen das neue Passwort, und ins Postfach geht eine
  Info-Mail. Anfragen und Zurücksetzen stehen im **Verlauf** (Anmeldungen).
- **Administratoren** sind ausgenommen (wer ihre Ersatz-Adresse übernimmt, hätte sonst den Server). Ein vergessenes
  Admin-Passwort setzt man auf dem Server: `mailadmin user passwd <adresse>`.
- Die Links in den Mails zeigen auf `https://<Hostname>:<HttpsPort>`; eine andere Adresse (z. B. `https://webmail.feicht.me`)
  steht in `appsettings.json` unter `Mailserver:Web:PublicUrl`.

## Angemeldet bleiben und Zwei-Faktor-Anmeldung

**Angemeldet bleiben:** Mit dem Haken auf der Anmeldeseite gilt die Anmeldung **30 Tage** und verlängert sich bei jeder
Nutzung – wer die Weboberfläche bzw. die App regelmäßig öffnet, muss sich praktisch nie neu anmelden. Ohne Haken endet die
Sitzung nach 60 Minuten ohne Aktivität oder beim Schließen des Browsers. Eine Passwortänderung, das Ein- oder Ausschalten
der Zwei-Faktor-Anmeldung und „Abmelden“ beenden die Anmeldung trotzdem sofort.

**Zwei-Faktor-Anmeldung** (unter *Sicherheit*): Nach dem Passwort fragt die Weboberfläche einen 6-stelligen Code aus einer
Authenticator-App ab (Passwörter-App des iPhones, Google oder Microsoft Authenticator, 1Password …). Ein gestohlenes Passwort
allein reicht dann nicht mehr.

- **Einrichten:** *Sicherheit → Einrichten*, QR-Code mit der App scannen (auf dem iPhone selbst: den Link antippen), den
  angezeigten Code eingeben. Danach erscheinen **10 Wiederherstellungscodes** – ausdrucken oder notieren, sie werden nur
  einmal angezeigt. Jeder ersetzt einmal den App-Code, falls das Handy fehlt.
- **Diesem Gerät 30 Tage vertrauen:** Haken bei der Code-Abfrage – dieser Browser fragt dann 30 Tage lang keinen Code ab
  (das Passwort bzw. „Angemeldet bleiben“ gilt weiter). Die Liste der Geräte steht unter *Sicherheit*, einzelne lassen sich
  entfernen. Eine Passwortänderung beendet das Vertrauen für alle Geräte.
- **Mailprogramme** (IMAP/SMTP: Outlook, Thunderbird, iPhone-Mail) sind nicht betroffen und melden sich weiter nur mit dem Passwort an.
- **Handy verloren:** Wiederherstellungscode verwenden, oder der Administrator schaltet sie unter *Postfächer → Postfach →
  Zurücksetzen* ab; auf dem Server: `mailadmin user 2fa <adresse> off`.
- Falsche Codes zählen wie falsche Passwörter zur IP-Sperre und stehen im Verlauf („Web-2FA“). Jeder Code gilt nur einmal.
  Das Geheimnis der App liegt verschlüsselt in der Datenbank (Schlüssel in `data\secrets`, Teil der Datensicherung).

## Mailprogramm einrichten

Unter **Einrichtung** gibt es:

- **iPhone, iPad, Mac:** „Profil laden“ in **Safari** antippen (nicht in der App vom Home-Bildschirm), dann *Einstellungen →
  Profil geladen → Installieren* und das Passwort eingeben. Das Profil enthält kein Passwort und ist mit dem Zertifikat des
  Servers signiert („Überprüft“). Erneutes Installieren ersetzt es.
- **Alle anderen:** Server, Ports und Benutzername zum Abtippen.

Automatische Erkennung für die Administratoren (wird auf der Seite für Admins mit den passenden Werten angezeigt):

- **Outlook** (klassisch) findet die Einstellungen per DNS-Eintrag je Domain:
  `_autodiscover._tcp.<domain>  SRV  0 0 9443 mail.feicht.me`. Outlook fragt dann einmal, ob es die Einstellungen von
  diesem Server übernehmen darf („Zulassen“, ggf. „Nicht mehr fragen“). Der Mailserver beantwortet
  `/autodiscover/autodiscover.xml` nur für eigene Domains.
- **Thunderbird** (und K-9/Thunderbird für Android, FairEmail) fragt `https://autoconfig.<domain>/mail/config-v1.1.xml` bzw.
  `https://<domain>/.well-known/autoconfig/mail/config-v1.1.xml` – auf Port 443, den der IIS belegt. Die Seite bietet die
  Datei je Domain zum Herunterladen an; in Plesk im Dateimanager der Website unter `.well-known/autoconfig/mail/` ablegen.
  Ohne die Datei probiert Thunderbird `mail.<domain>` mit den üblichen Ports durch – das klappt meist ebenfalls.

## Als Benutzer anmelden (Postfach öffnen)

Unter **Postfächer** öffnet „Öffnen“ bzw. auf der Seite eines Postfachs **Als Benutzer anmelden** das Webmail dieses
Postfachs so, wie der Benutzer es sieht – ohne sein Passwort, etwa um bei einem Problem zu helfen oder eine Mail zu suchen.

- Ein farbiger Balken zeigt die ganze Zeit, in wessen Postfach man arbeitet; **Zurück zu meinem Konto** kehrt zum eigenen
  Konto zurück, ohne neue Anmeldung.
- Alles geschieht wirklich in diesem Postfach: Geöffnete Mails gelten als gelesen, Gelöschtes ist gelöscht, Gesendetes
  geht mit der Adresse des Benutzers hinaus.
- Während der Übernahme gibt es keine Verwaltungsrechte. Passwort ändern und Push-Benachrichtigungen einschalten sind
  gesperrt, damit nichts davon den Besuch überdauert.
- Jede Übernahme steht im **Verlauf** (Anmeldungen, „Postfach von Admin geöffnet … durch …“) und im Windows-Ereignisprotokoll.
- Verliert der Administrator seine Rechte oder ändert sich das Passwort eines der beiden Konten, endet die Sitzung sofort.

## Fremde Konten

Unter *Fremde Konten* lassen sich bis zu 10 E-Mail-Adressen bei anderen Anbietern mit dem eigenen Postfach verbinden.

- **Abrufen:** Alle 5 Minuten holt der Server neue Mails aus dem Posteingang beim Anbieter (IMAP) und legt sie in einem
  eigenen Ordner ab (Standard: die Adresse; wahlweise der Posteingang). Beim Anbieter bleiben die Mails liegen – es wird
  nur übernommen, was seit dem letzten Abruf neu ist. Beim Einrichten lässt sich wählen, ob auch die schon vorhandenen
  Mails übernommen werden. **Jetzt abrufen** holt sofort.
- Gelesen/ungelesen wird beim Abruf übernommen; neue ungelesene Mails lösen eine Push-Benachrichtigung aus. Der
  Virenschutz prüft auch abgerufene Mails, der Spamfilter nicht (das hat der Anbieter schon getan).
- **Senden:** Ist ein Postausgangsserver (SMTP) eingetragen, steht die Adresse beim Schreiben als Absender zur Auswahl.
  Die Mail geht dann über den Server des Anbieters hinaus – so passen SPF und DKIM der fremden Domain, und die Mail
  landet beim Empfänger nicht im Spam. Die Kopie liegt in *Gesendet* des eigenen Postfachs. Antworten auf Mails im
  Ordner eines fremden Kontos werden automatisch von dieser Adresse geschrieben.
- Beim Speichern meldet sich der Server einmal per IMAP und SMTP beim Anbieter an; falsche Angaben fallen so sofort auf.
  Leere Servernamen werden als `imap.<domain>` / `smtp.<domain>` angenommen, der Benutzername als die Adresse.
- **Bekannte Anbieter** – T-Online/Magenta, GMX, WEB.DE, Gmail, iCloud, Yahoo, freenet, Posteo, mailbox.org: Die
  Serverdaten trägt der Server selbst ein, es reichen Adresse und Passwort. Was beim Anbieter vorher nötig ist:

  | Anbieter | Vorher erledigen |
  |---|---|
  | T-Online | ein eigenes **E-Mail-Passwort** festlegen (E-Mail Center → Einstellungen → Passwörter); das normale Login-Passwort geht nicht |
  | GMX, WEB.DE | E-Mail → Einstellungen → POP3/IMAP Abruf → „POP3 und IMAP Zugriff erlauben“ |
  | Gmail, Yahoo | App-Passwort erzeugen (bei Google erst die Bestätigung in zwei Schritten einschalten) |
  | iCloud | app-spezifisches Passwort unter account.apple.com |

  Schlägt die Prüfung fehl, zeigt die Fehlermeldung den passenden Hinweis. Andere Anbieter: übliche Einstellungen sind
  IMAP Port 993 mit SSL/TLS und SMTP Port 465 mit SSL/TLS oder 587 mit STARTTLS.
- **Outlook.com, Hotmail, Live, MSN:** Microsoft erlaubt dafür keine Passwörter mehr. Adresse eintragen, Passwort leer
  lassen, *Prüfen und speichern* – die Seite zeigt einen Code. <https://microsoft.com/devicelogin> öffnen (auf einem
  beliebigen Gerät), Code eingeben, mit der Outlook-Adresse anmelden und zustimmen. Nach ein paar Sekunden ist die
  Adresse eingerichtet. Gespeichert wird kein Passwort, sondern ein Zugangsschlüssel von Microsoft (verschlüsselt).
  Ändert sich das Microsoft-Passwort oder widerruft Microsoft den Zugang, steht beim Konto ein Hinweis – dann
  **Neu anmelden**.
- Dafür braucht der Server **einmalig eine App-Registrierung bei Microsoft** (kostenlos, ca. 5 Minuten; ein Administrator
  trägt die Anwendungs-ID unten auf der Seite *Fremde Konten* ein – oder es wird die der OneDrive-Sicherung genutzt):
  1. <https://entra.microsoft.com> → *Anwendungen → App-Registrierungen → Neue Registrierung*, Name z. B. `Mailserver`,
     Kontotypen **„Konten in einem beliebigen Organisationsverzeichnis und persönliche Microsoft-Konten“**, Umleitungs-URI
     leer. (Lässt das Portal mit einem privaten Konto keine Registrierung zu: vorher ein kostenloses Azure-Konto anlegen.)
  2. *Authentifizierung* → **„Öffentliche Clientflows zulassen“ auf „Ja“** → Speichern.
  3. *API-Berechtigungen → Berechtigung hinzufügen → Microsoft Graph → Delegierte Berechtigungen*:
     `IMAP.AccessAsUser.All`, `SMTP.Send` und `offline_access`.
  4. Die **Anwendungs-ID (Client)** von der Übersichtsseite kopieren und unter *Fremde Konten → Outlook.com / Hotmail
     (Administrator)* eintragen. Wird dieselbe Registrierung auch für OneDrive genutzt, zusätzlich `Files.ReadWrite`.
- Die Passwörter liegen verschlüsselt in der Datenbank; der Schlüssel liegt in `data\secrets` und ist Teil der
  Datensicherung. Fehler beim Abruf (z. B. geändertes Passwort) stehen in der Liste beim jeweiligen Konto.
- Mailprogramme (Outlook, Apple Mail …) sehen den Ordner per IMAP ebenfalls. Mit der fremden Adresse senden geht dort aber
  nur, wenn das Mailprogramm selbst den Server des Anbieters verwendet.

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
- Die Sitzung endet nach 60 Minuten Inaktivität (`Mailserver:Web:SessionTimeout`) – mit „Angemeldet bleiben“ nach 30 Tagen
  ohne Nutzung – und sofort, wenn Passwort, Aktiv-Status, Admin-Recht oder Zwei-Faktor-Anmeldung des Postfachs geändert werden.
- Optional Zwei-Faktor-Anmeldung mit Authenticator-App (siehe oben).
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
- **Signatur:** Unter *Signatur* je Absenderadresse ein Text. Er steht beim Schreiben unter dem Text, bei Antworten und
  Weiterleitungen über dem Zitat (lässt sich je Adresse abschalten). Wählt man beim Schreiben einen anderen Absender,
  tauscht der Editor die Signatur aus. Das übliche Trennzeichen „-- “ wird automatisch davorgesetzt.
- **Ordner verwalten** (Link unter der Ordnerliste): Ordner und Unterordner anlegen, umbenennen und löschen. Systemordner
  (Posteingang, Gesendet, Entwürfe, Papierkorb, Spam) sind geschützt. Beim Löschen wandern enthaltene Mails in den Papierkorb;
  Unterordner müssen zuerst gelöscht werden. Beim Umbenennen werden Regeln, die Mails in den Ordner verschieben, automatisch angepasst.

## Grenzen

- Bilder lassen sich nicht in den Text einfügen, nur als Anhang senden (eingefügte Bilder werden beim Senden entfernt).
