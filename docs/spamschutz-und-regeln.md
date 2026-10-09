# Spamschutz und Regeln

## Was bei jeder eingehenden Mail passiert

Mails, die andere Server über Port 25 einliefern, durchlaufen diese Prüfungen:

| Zeitpunkt | Prüfung | Wirkung |
|---|---|---|
| Verbindung | **DNS-Blacklists** (Standard: Spamhaus ZEN, SpamCop) | Spamhaus-Treffer: Ablehnung (nur CSS, 127.0.0.3: +4 Punkte). SpamCop-Treffer: +3 Punkte |
| Verbindung | **Reverse DNS** der IP | fehlt: +1,5 |
| MAIL FROM | **SPF** (RFC 7208) | fail +3,5 · softfail +1,5 · fehlerhaft +1 · neutral +0,3 · kein SPF +0,5 |
| RCPT TO | **Greylisting** | unbekannte Absender müssen es nach 5 Minuten erneut versuchen (nicht bei SPF pass) |
| DATA | **DKIM** (RFC 6376) | ungültige Signatur +1 · keine Signatur +0,3 |
| DATA | **DMARC** (RFC 7489) | p=reject: Ablehnung · p=quarantine: +5 · p=none: +1,5 |
| DATA | **Absender-Fälschung** der eigenen Domains | +5 |
| DATA | HELO ohne Domain, fehlende Message-ID oder fehlendes Datum | +1 / +0,5 / +0,5 |

Ab **5 Punkten** (`Spam:JunkThreshold`) landet die Mail im Ordner **Junk**. Optional werden Mails ab einem höheren Wert
(`Spam:DeleteThreshold`, z. B. 15) gar nicht erst zugestellt. Weitergeleitet werden Spam-Mails nie, damit der eigene Server
nicht selbst auf Blacklists landet.

Jede Mail bekommt Kopfzeilen, an denen man das Ergebnis sieht:

```
Authentication-Results: mail.example.de;
	spf=pass smtp.mailfrom=news@shop.de;
	dkim=pass header.d=shop.de header.s=s1;
	dmarc=pass (p=reject) header.from=shop.de
X-Spam-Score: 0.0
X-Spam-Status: No, score=0.0 required=5.0
	tests=NONE
```

Solche Kopfzeilen, die ein Absender selbst mitschickt, werden entfernt. So kann niemand vorgaukeln, seine Mail sei bereits geprüft.

Verbindungen vom Server selbst (127.0.0.1) und aus `Spam:TrustedNetworks` werden nicht geprüft.

**Hinweis zu Spamhaus:** Spamhaus beantwortet Anfragen über große öffentliche DNS-Resolver (z. B. 8.8.8.8) nicht und liefert dann
Fehlercodes, die der Server ignoriert. Die Liste wirkt also nur, wenn der Server den DNS-Resolver des VPS-Anbieters oder einen
eigenen verwendet.

**Weiterleitungen von anderen Anbietern:** Leitet ein anderer Anbieter (z. B. IONOS, GMX) Mails hierher weiter, kommen sie von
dessen Servern. Steht einer davon auf einer Sperrliste, wird die Mail abgelehnt („blocked using zen.spamhaus.org“). Abhilfe:
die Adresse des Servers (steht in der Fehlermeldung bzw. im Verlauf, z. B. `82.165.159.39`) unter *Verwaltung → IP-Sperren* als
**nie sperren** eintragen – dann werden für ihn keine Sperrlisten abgefragt; die übrigen Prüfungen bleiben. Ganz ohne solche
Probleme: die Adresse unter *Fremde Konten* abrufen lassen statt weiterleiten. Im Verlauf steht bei einer Ablehnung, welche
Spamhaus-Liste getroffen hat (SBL, CSS, XBL, PBL, DROP).

## Eigene Regeln

Regeln gelten für **ein Postfach**, eine **ganze Domain** oder **alle Postfächer** (`*`). Bei jeder Zustellung werden zuerst die
globalen, dann die Domain- und zuletzt die Postfach-Regeln geprüft, innerhalb davon nach Priorität (kleinere Zahl zuerst).
Normalerweise ist nach der ersten passenden Regel Schluss. Mit `--continue` werden danach weitere Regeln geprüft.

```
mailadmin rule add <bereich> --if <feld> <operator> <wert> [--if ...] [--any] --then <aktion> [ordner]
```

| Feld | Bedeutung |
|---|---|
| `betreff` | Betreffzeile |
| `von` | Absender (Name und Adresse) |
| `an` | Empfänger in An und Cc |
| `text` | Nachrichtentext (bei HTML-Mails der Text ohne Formatierung) |
| `header:<Name>` | beliebige Kopfzeile, z. B. `header:List-Id` |
| `score` | Spam-Punktzahl, mit `über` / `unter` |

| Operator | Bedeutung |
|---|---|
| `enthält` / `enthält-nicht` | Wortgruppe kommt (nicht) vor – Groß-/Kleinschreibung und Zeilenumbrüche egal |
| `ist` / `beginnt` / `endet` | exakter Vergleich, Anfang, Ende |
| `regex` | regulärer Ausdruck |
| `über` / `unter` | für `score` |

| Aktion | Wirkung |
|---|---|
| `spam` | in den Ordner Junk |
| `löschen` | **endgültig löschen** – die Mail wird nirgends gespeichert |
| `verschieben <Ordner>` | in einen Ordner (wird bei Bedarf angelegt, Unterordner mit `/`) |
| `kein-spam` | immer in den Posteingang, auch wenn der Spamfilter anschlägt |
| `gelesen` | als gelesen markieren |
| `markieren` | mit Fähnchen markieren |

Mehrere `--if` müssen alle zutreffen; mit `--any` reicht eine.

### Beispiele

```powershell
# Betreff mit einer bestimmten Wortgruppe → Spam
mailadmin rule add max@example.de --if betreff enthält "exklusives Angebot" --then spam

# ... oder gleich endgültig löschen, für alle Postfächer
mailadmin rule add * --if betreff enthält "Sie haben gewonnen" --then löschen

# Newsletter in einen eigenen Ordner
mailadmin rule add max@example.de --if von endet "@newsletter.shop.de" --if header:List-Id enthält "shop" --any --then verschieben "Newsletter"

# Mails eines Kunden nie als Spam behandeln
mailadmin rule add example.de --if von endet "@kunde.de" --then kein-spam

# Sehr sicherer Spam wird verworfen
mailadmin rule add * --if score über 12 --then löschen

# Rechnungen: als gelesen markieren UND verschieben
mailadmin rule add max@example.de --if betreff enthält "Rechnung" --then gelesen --continue --priority 10
mailadmin rule add max@example.de --if betreff enthält "Rechnung" --then verschieben "Rechnungen" --priority 20
```

Verwalten und ausprobieren:

```powershell
mailadmin rule list [bereich]
mailadmin rule disable 3 | mailadmin rule enable 3 | mailadmin rule remove 3
mailadmin rule test max@example.de C:\Temp\beispiel.eml [--score 7]   # zeigt, was mit der Mail passieren würde
```

Regeln gelten für alle Mails an das Postfach, also auch für Mails von anderen Postfächern desselben Servers.
Änderungen wirken sofort, ein Neustart ist nicht nötig.

## Spam-Protokoll und Optimierung

Jede Entscheidung des Spamfilters wird in der Datenbank protokolliert (Tabelle `spam_log`), standardmäßig 90 Tage lang:

| Stufe | Was wird festgehalten |
|---|---|
| `connect` | Ablehnung wegen Blacklist (IP, Liste) |
| `sender` | Ablehnung wegen SPF (nur wenn `RejectSpfFail` aktiv ist) |
| `recipient` | Greylisting: Absender zurückgestellt |
| `data` | pro Mail: IP, Reverse DNS, HELO, Absender, Betreff, Message-ID, **Score mit allen Einzeltests**, SPF/DKIM/DMARC-Ergebnis – oder Ablehnung wegen DMARC |
| `delivery` | pro Empfänger: Zielordner, verworfen, **greifende Regeln**, Spam nicht weitergeleitet |
| `feedback` | ein Benutzer hat eine Mail **aus Junk herausgeholt** (`marked-ham`) oder **nach Junk verschoben** (`marked-spam`) – egal ob in Outlook, Thunderbird oder am Handy |

Das Feedback ist der wertvollste Teil: Wird eine Mail mit Score ≥ 5 aus Junk geholt, war es ein **Fehlalarm**; wird eine Mail mit
Score < 5 nach Junk verschoben, hat der Filter sie **übersehen**. Die Auswertung zeigt, welche Tests daran beteiligt waren.

```powershell
mailadmin spamlog stats --since 30d          # Übersicht, Score-Verteilung, Tests, Fehlalarme, Hinweise
mailadmin spamlog list --since 24h           # letzte Einträge
mailadmin spamlog list --action marked-ham   # alle Fehlalarme
mailadmin spamlog list --search shop.de      # nach Absender, Empfänger, Betreff oder Message-ID suchen
mailadmin spamlog list --min-score 5 --limit 200
mailadmin spamlog show <sitzung>             # alle Einträge zu einer Mail (Sitzung steht in der Liste bzw. Message-ID)
mailadmin spamlog export spam.csv --since 90d   # CSV (Semikolon, UTF-8) für Excel oder eigene Auswertungen
```

Typisches Vorgehen beim Optimieren:

1. Einige Wochen laufen lassen; Benutzer verschieben Fehlentscheidungen einfach im Mailprogramm.
2. `mailadmin spamlog stats --since 30d` ansehen:
   - viele Fehlalarme knapp über 5 Punkten → `Spam:JunkThreshold` etwas anheben,
   - viel übersehener Spam knapp unter 5 → Schwelle senken,
   - ein Test taucht in den meisten Fehlalarmen auf → seine Gewichtung prüfen,
   - übersehener Spam mit Score nahe 0 → Inhalts-Regeln anlegen (Betreff/Text),
   - Score-Verteilung zeigt sehr viele Mails ≥ 12 → `Spam:DeleteThreshold` oder eine Regel `score über 12 → löschen` erwägen.
3. Bekannte Absender, die immer wieder fälschlich in Junk landen: Regel `kein-spam` anlegen.

Einstellungen unter `Mailserver:Spam:Log`: `Enabled`, `RetentionDays` (Standard 90) und `IncludeSubject` (Betreffzeilen sind
personenbezogene Daten – bei Bedarf abschalten). Alte Einträge werden automatisch gelöscht; `mailadmin spamlog cleanup` tut das sofort.
