# Automatische Datensicherung

Der Mailserver sichert sich jede Nacht selbst – im laufenden Betrieb, ohne Ausfall. Einrichten unter
*Verwaltung → Datensicherung* (oder per `mailadmin backup --to <ordner>` für eine einmalige Sicherung).

## Wohin?

Möglichst **weg vom Server**, sonst hilft die Sicherung bei einem Festplattenschaden oder einer versehentlich
gelöschten VM nicht:

| Ziel | Beispiel | Hinweis |
|---|---|---|
| **Microsoft OneDrive** | privat oder Microsoft 365 | einmalig eine App-Registrierung bei Microsoft anlegen, siehe unten |
| **pCloud** | Konto in der EU oder den USA | Anmeldung mit E-Mail und Passwort (auch mit Zwei-Faktor-Code) |
| Netzwerkfreigabe mit Anmeldung | `\\u12345.your-storagebox.de\backup\mailserver` | z. B. Storage Box des Hosters; Benutzer und Passwort auf der Seite eintragen |
| Netzwerkfreigabe ohne Anmeldung | `\\nas\sicherung\mailserver` | das Computerkonto des Servers braucht Schreibrecht |
| zweite Festplatte | `D:\Sicherung\Mailserver` | schützt vor Fehlern, nicht vor Ausfall des ganzen Servers |

Bei OneDrive und pCloud lädt der Server direkt hoch – es braucht keinen zusätzlichen Platz auf dem Server und kein
Sync-Programm. Nach der ersten Sicherung werden nur noch neue Mails übertragen (der Server merkt sich, was schon oben
ist). Die Daten liegen dort unverschlüsselt, so wie auf dem Server: das Cloud-Konto daher mit Zwei-Faktor-Anmeldung schützen.

### OneDrive einrichten

Der Server meldet sich wie ein Mailprogramm bei Microsoft an. Dafür braucht er eine eigene App-Registrierung
(einmalig, kostenlos, etwa 5 Minuten):

1. <https://entra.microsoft.com> öffnen und mit dem Microsoft-Konto anmelden, in dessen OneDrive gesichert werden soll
   (bei Microsoft 365 mit einem Administrator). Lässt das Portal mit einem rein privaten Konto keine App-Registrierung zu,
   vorher unter <https://azure.microsoft.com/free> ein kostenloses Azure-Konto anlegen.
2. *Anwendungen → App-Registrierungen → Neue Registrierung*:
   - Name: `Mailserver-Sicherung`
   - Unterstützte Kontotypen: **„Konten in einem beliebigen Organisationsverzeichnis und persönliche Microsoft-Konten“**
   - Umleitungs-URI: leer lassen → *Registrieren*
3. Auf der Übersichtsseite die **Anwendungs-ID (Client)** kopieren.
4. *Authentifizierung* → ganz unten **„Öffentliche Clientflows zulassen“ auf „Ja“** → *Speichern*.
5. *API-Berechtigungen → Berechtigung hinzufügen → Microsoft Graph → Delegierte Berechtigungen*: `Files.ReadWrite`
   und `offline_access` hinzufügen. Bei Microsoft 365 anschließend *Administratorzustimmung erteilen*.

Dann in der Weboberfläche unter *Verwaltung → Datensicherung* „Microsoft OneDrive“ wählen, die Anwendungs-ID eintragen
und **„Mit OneDrive verbinden“** klicken. Die Seite zeigt einen Code: <https://microsoft.com/devicelogin> öffnen (auf
einem beliebigen Gerät), Code eingeben, anmelden und zustimmen. Nach ein paar Sekunden steht auf der Seite „verbunden
mit …“. Der Server bewahrt nur ein Zugangstoken auf (in `data\cloud`, verschlüsselt für diesen Rechner), kein Passwort.

Gelöschte Sicherungen landen im OneDrive-Papierkorb und zählen dort bis zu 30 Tage noch zum Speicherplatz.

### pCloud einrichten

*Verwaltung → Datensicherung* → „pCloud“ wählen, die Region des Kontos (Europa oder USA – steht in den
pCloud-Kontoeinstellungen) auswählen, E-Mail-Adresse und Passwort eingeben und **„Mit pCloud verbinden“**. Danach fragt
die Seite meist nach einem Code: Weil sich der Server zum ersten Mal von einem neuen Gerät und Ort (Rechenzentrum)
anmeldet, schickt pCloud einen **Bestätigungscode per E-Mail** an die Kontoadresse (ggf. im Spam-Ordner nachsehen).
Ist die Zwei-Faktor-Anmeldung eingeschaltet, ist es stattdessen der Code aus der Authenticator-App. Das Passwort muss im
zweiten Schritt nicht noch einmal eingegeben werden. Der Server speichert nicht das Passwort, sondern
ein Zugangstoken, das bis zu zwei Jahre gilt (solange es mindestens alle zwei Monate benutzt wird – die nächtliche
Sicherung tut das). Läuft es ab, schlägt die Sicherung fehl, die Admins bekommen eine Mail, und man verbindet einfach neu.

## Was wird gesichert?

```
<zielordner bzw. Cloud-Ordner>\
  snapshots\2026-10-05_030000\   eine Sicherung pro Nacht
      mailserver.db               Postfächer (mit Passwörtern), Ordner, Aliase, Regeln, Weiterleitungen, Verlauf
      dkim\  acme\  queue\        DKIM-Schlüssel, Let's-Encrypt-Zertifikat, noch nicht zugestellte Mails
      settings.json, appsettings.json
      manifest.json               Version, Datum, Anzahl Postfächer und Mails
  mail\                           alle Mails – nur neue werden jede Nacht dazukopiert
```

- Die Datenbank wird über die Sicherungsfunktion von SQLite kopiert und ist damit immer in sich stimmig.
- Ältere Sicherungen als die eingestellte Aufbewahrung (Standard 14 Tage) werden entfernt; die neueste bleibt immer.
  Mails, die auf dem Server gelöscht wurden, bleiben ebenso lange im Ordner `mail`, sodass jede vorhandene
  Sicherung vollständig zurückgespielt werden kann.
- **Nicht** gesichert werden `data\keys` (an die Windows-Installation gebunden; nach dem Zurückspielen melden sich alle
  Benutzer der Weboberfläche einmal neu an) und `data\cloud` (die Zugänge zu OneDrive/pCloud – nach einer
  Neuinstallation einmal neu verbinden).
- War der Server zur Sicherungszeit aus, holt er die Sicherung kurz nach dem Start nach. Schlägt sie fehl, versucht er
  es alle drei Stunden erneut, und alle Admins bekommen eine Mail. Die Admin-Übersicht warnt, wenn die letzte
  erfolgreiche Sicherung älter als zwei Tage ist.

## Zurückspielen

In einer PowerShell als Administrator, auch auf einem frisch installierten Server (nach `install.ps1`):

```powershell
Stop-Service Mailserver
cd C:\Mailserver

# aus einem Ordner
.\mailadmin.exe backup restore "D:\Sicherung\Mailserver"            # neueste Sicherung
.\mailadmin.exe backup restore "D:\Sicherung\Mailserver\snapshots\2026-10-03_030000"   # eine bestimmte

# aus OneDrive (auf einem neuen Server zuerst verbinden – die Verbindung wird nicht mitgesichert)
.\mailadmin.exe backup connect onedrive --client-id <anwendungs-id>
.\mailadmin.exe backup restore onedrive                              # oder: --snapshot 2026-10-03_030000

# aus pCloud
.\mailadmin.exe backup connect pcloud --email sven@example.de        # fragt Passwort und ggf. Code ab
.\mailadmin.exe backup restore pcloud

Start-Service Mailserver
```

Auf einer Netzwerkfreigabe mit Anmeldung vorher einmal verbinden: `net use \\server\freigabe /user:<benutzer>`.
Liegt die Sicherung in der Cloud in einem anderen Ordner als `Mailserver-Sicherung`, `--folder <ordner>` angeben.

Beim Zurückspielen werden Datenbank, Einstellungen, DKIM-Schlüssel, Zertifikat und Warteschlange ersetzt und die Mails
aus dem Ordner `mail` ergänzt. `appsettings.json` wird nicht überschrieben – die Kopie aus der Sicherung liegt im
Sicherungsordner, falls man sie braucht.

## Befehle

```
mailadmin backup [--to <ordner>]                     jetzt sichern
mailadmin backup list                                Verlauf und vorhandene Sicherungen
mailadmin backup connect onedrive --client-id <id>   OneDrive verbinden (Code im Browser bestätigen) und als Ziel wählen
mailadmin backup connect pcloud --email <adresse> [--region EU|US]   pCloud verbinden und als Ziel wählen
mailadmin backup disconnect onedrive|pcloud
mailadmin backup restore <ordner>|onedrive|pcloud [--snapshot <name>] [--folder <cloud-ordner>]   zurückspielen (Dienst vorher anhalten)
```
