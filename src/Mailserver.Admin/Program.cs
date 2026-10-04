using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Data;
using Mailserver.Core.Dkim;
using Mailserver.Core.Migration;
using Mailserver.Core.Queue;
using Mailserver.Core.Rules;
using Mailserver.Core.SpamLogging;
using Mailserver.Core.Storage;
using Mailserver.Migration;
using Microsoft.Extensions.Configuration;

// mailadmin — command line administration. Reads the same appsettings.json as the service (next to the executable).

// The export runs on the old server and needs neither configuration nor database.
if (args is ["export", ..])
{
    try
    {
        return await ExportAsync(args);
    }
    catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Fehler: {ex.Message}");
        return 1;
    }
}

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();
var bootstrap = configuration.GetSection(MailserverOptions.SectionName).Get<MailserverOptions>() ?? new MailserverOptions();
var paths = new DataPaths(bootstrap.DataDirectory);

// Settings saved in the web interface override appsettings.json, as in the service.
var options = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile(paths.SettingsFile, optional: true)
    .Build()
    .GetSection(MailserverOptions.SectionName).Get<MailserverOptions>() ?? bootstrap;

paths.EnsureCreated();
var database = new Database(paths);
database.Migrate();
var accounts = new AccountStore(database);
var mailboxes = new MailboxStore(database, paths);
var dkim = new DkimKeyStore(paths);
var queue = new OutboundQueue(database, paths);
var rules = new RuleStore(database);
var spamLog = new SpamLog(database, Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System);
var mailboxSettings = new MailboxSettingsStore(database, accounts, TimeProvider.System);

try
{
    return args is ["import", ..] ? await ImportAsync(args) : Run(args);
}
catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine($"Fehler: {ex.Message}");
    return 1;
}

int Run(string[] a)
{
    switch (a)
    {
        case ["domain", "add", var name, ..]:
        {
            var selector = Option(a, "--selector") ?? $"mail{DateTime.UtcNow:yyyyMM}";
            var domain = accounts.AddDomain(name, selector);
            dkim.GenerateKey(domain.Name, selector);
            Console.WriteLine($"Domain {domain.Name} angelegt, DKIM-Selector '{selector}'.");
            PrintDns(domain.Name);
            return 0;
        }

        case ["domain", "list"]:
            foreach (var d in accounts.ListDomains())
            {
                Console.WriteLine($"{d.Name,-40} DKIM: {d.DkimSelector ?? "-"}");
            }

            return 0;

        case ["domain", "remove", var name]:
            return Confirm($"Domain {name} inkl. ALLER Postfächer löschen?") && accounts.RemoveDomain(name) ? Ok("Gelöscht.") : 1;

        case ["dkim", "rotate", var name]:
        {
            var selector = Option(a, "--selector") ?? $"mail{DateTime.UtcNow:yyyyMMdd}";
            dkim.GenerateKey(name, selector);
            Console.WriteLine($"Neuer Schlüssel erzeugt. Erst den DNS-Eintrag anlegen, dann aktivieren mit:  mailadmin dkim activate {name} {selector}");
            Console.WriteLine($"{selector}._domainkey.{EmailAddress.NormalizeDomain(name)}  TXT  \"{dkim.GetDnsRecord(name, selector)}\"");
            return 0;
        }

        case ["dkim", "activate", var name, var selector]:
            if (!dkim.HasKey(name, selector))
            {
                return Fail($"Kein Schlüssel für Selector {selector} vorhanden.");
            }

            accounts.SetDkimSelector(name, selector);
            return Ok($"DKIM-Selector für {name} ist jetzt '{selector}'.");

        case ["dns", var name]:
            PrintDns(name);
            return 0;

        case ["tls"]:
            return CheckTls();

        case ["user", "add", var address, ..]:
        {
            var account = accounts.AddAccount(EmailAddress.Parse(address), Option(a, "--password") ?? ReadNewPassword(),
                ParseQuota(Option(a, "--quota-mb")));
            mailboxes.EnsureDefaultFolders(account.Id);
            return Ok($"Postfach {account.Address} angelegt.");
        }

        case ["user", "passwd", var address, ..]:
            return accounts.SetPassword(EmailAddress.Parse(address), Option(a, "--password") ?? ReadNewPassword())
                ? Ok("Passwort geändert.")
                : Fail("Postfach nicht gefunden.");

        case ["user", "quota", var address, var megabytes]:
            return accounts.SetQuota(EmailAddress.Parse(address), ParseQuota(megabytes)) ? Ok("Quota gesetzt.") : Fail("Postfach nicht gefunden.");

        case ["user", "admin", var address, "on" or "off"]:
            return accounts.SetAdmin(EmailAddress.Parse(address), a[3] == "on")
                ? Ok(a[3] == "on" ? $"{address} darf jetzt den Admin-Bereich der Weboberfläche nutzen." : "Admin-Recht entzogen.")
                : Fail("Postfach nicht gefunden.");

        case ["user", "disable" or "enable", var address]:
            return accounts.SetEnabled(EmailAddress.Parse(address), a[1] == "enable") ? Ok("Gespeichert.") : Fail("Postfach nicht gefunden.");

        case ["user", "remove", var address]:
            return Confirm($"Postfach {address} inkl. aller Nachrichten löschen?") && accounts.RemoveAccount(EmailAddress.Parse(address))
                ? Ok("Gelöscht. (Nachrichtendateien bleiben bis zur Bereinigung unter data/mail liegen.)")
                : 1;

        case ["user", "list", ..]:
            foreach (var account in accounts.ListAccounts(a.Length > 2 ? a[2] : null))
            {
                var usage = mailboxes.GetUsage(account.Id) / 1024.0 / 1024.0;
                var quota = account.QuotaBytes > 0 ? $"{account.QuotaBytes / 1024 / 1024} MB" : "unbegrenzt";
                Console.WriteLine($"{account.Address,-45} {usage,8:F1} MB / {quota,-10} {(account.Enabled ? "" : "(deaktiviert)")}{(account.IsAdmin ? " [Admin]" : "")}");
            }

            return 0;

        case ["alias", "add", var address, var targets]:
        {
            var alias = accounts.AddAlias(EmailAddress.Parse(address), targets.Split(',', StringSplitOptions.TrimEntries).Select(EmailAddress.Parse));
            return Ok($"Alias {alias.Address} -> {string.Join(", ", alias.Targets)}");
        }

        case ["alias", "remove", var address]:
            return accounts.RemoveAlias(EmailAddress.Parse(address)) ? Ok("Gelöscht.") : Fail("Alias nicht gefunden.");

        case ["alias", "list"]:
            foreach (var alias in accounts.ListAliases())
            {
                Console.WriteLine($"{alias.Address,-45} -> {string.Join(", ", alias.Targets)}");
            }

            return 0;

        case ["forward", var address, ..]:
        {
            var account = accounts.FindAccount(EmailAddress.Parse(address)) ?? throw new InvalidOperationException($"Postfach {address} gibt es nicht.");
            if (a.Length > 2)
            {
                var targets = a[2] is "off" or "aus" ? [] : MailboxSettingsStore.ParseTargets(a[2]);
                mailboxSettings.SetForwarding(account, new Forwarding(targets, !a.Contains("--no-copy")));
            }

            var forwarding = mailboxSettings.Get(account.Id).Forwarding;
            return Ok(forwarding.IsActive
                ? $"{account.Address} leitet weiter an {string.Join(", ", forwarding.Targets)}{(forwarding.KeepCopy ? " (Kopie bleibt im Postfach)" : " (ohne Kopie)")}."
                : $"{account.Address}: keine Weiterleitung.");
        }

        case ["autoreply", var address, ..]:
        {
            var account = accounts.FindAccount(EmailAddress.Parse(address)) ?? throw new InvalidOperationException($"Postfach {address} gibt es nicht.");
            var current = mailboxSettings.Get(account.Id).AutoReply;
            if (a.Length > 2 && a[2] is "off" or "aus")
            {
                mailboxSettings.SetAutoReply(account.Id, current with { Enabled = false });
            }
            else if (a.Length > 2 && a[2] is "on" or "an")
            {
                var text = Option(a, "--text-file") is { } file ? File.ReadAllText(file) : Option(a, "--text") ?? current.Body;
                mailboxSettings.SetAutoReply(account.Id, new AutoReply(true, Option(a, "--subject") ?? current.Subject, text,
                    Option(a, "--from") is { } from ? DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture) : current.From,
                    Option(a, "--until") is { } until ? DateOnly.Parse(until, System.Globalization.CultureInfo.InvariantCulture) : current.Until,
                    Option(a, "--interval") is { } interval ? int.Parse(interval) : current.IntervalDays));
            }

            var reply = mailboxSettings.Get(account.Id).AutoReply;
            Console.WriteLine($"Abwesenheitsnotiz für {account.Address}: {(reply.Enabled ? "an" : "aus")}" +
                              (reply.From is { } f ? $", ab {f:dd.MM.yyyy}" : "") + (reply.Until is { } u ? $", bis {u:dd.MM.yyyy}" : "") +
                              $", je Absender alle {reply.IntervalDays} Tage");
            if (reply.Body.Length > 0)
            {
                Console.WriteLine($"Betreff: {(reply.Subject.Length > 0 ? reply.Subject : "Automatische Antwort: <Betreff der Mail>")}");
                Console.WriteLine(reply.Body);
            }

            return 0;
        }

        case ["rule", "add", var scope, .. var definition]:
        {
            var parsed = RuleParser.Parse(definition);
            CheckScope(scope);
            var rule = rules.Add(scope, parsed.Name, parsed.Conditions, parsed.Action, parsed.Argument, parsed.MatchAll, parsed.Stop, parsed.Priority);
            return Ok($"Regel {rule.Id} angelegt: {rule.Name}");
        }

        case ["rule", "list", ..]:
            foreach (var rule in rules.List(a.Length > 2 ? a[2] : null))
            {
                Console.WriteLine($"[{rule.Id}] {rule.Scope,-30} Prio {rule.Priority,-4} {(rule.Enabled ? "aktiv  " : "inaktiv")} {rule.Name}");
                foreach (var condition in rule.Conditions)
                {
                    Console.WriteLine($"      {(rule.MatchAll ? "und" : "oder")} {condition}");
                }

                Console.WriteLine($"      dann {RuleActionText.Describe(rule.Action, rule.Argument)}{(rule.Stop ? "" : " (weiter prüfen)")}");
            }

            return 0;

        case ["rule", "remove", var id]:
            return rules.Remove(long.Parse(id)) ? Ok("Regel gelöscht.") : Fail("Regel nicht gefunden.");

        case ["rule", "enable" or "disable", var id]:
            return rules.SetEnabled(long.Parse(id), a[1] == "enable") ? Ok("Gespeichert.") : Fail("Regel nicht gefunden.");

        case ["rule", "test", var address, var file, ..]:
        {
            var applicable = rules.GetRulesFor(EmailAddress.Parse(address));
            var score = double.Parse(Option(a, "--score") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            var message = MimeKit.MimeMessage.Load(file);
            var decision = RuleEngine.Decide(applicable, new RuleSubject(message, score), score >= options.Spam.JunkThreshold);
            Console.WriteLine($"Betreff:  {message.Subject}");
            Console.WriteLine($"Treffer:  {(decision.MatchedRules.Count == 0 ? "keine Regel" : string.Join(", ", decision.MatchedRules))}");
            Console.WriteLine(decision.Discard ? "Ergebnis: wird endgültig gelöscht" : $"Ergebnis: Ordner {decision.Folder}" +
                (decision.Flags.Count > 0 ? $", Flags {string.Join(' ', decision.Flags)}" : ""));
            return 0;
        }

        case ["spamlog", "list", ..]:
            PrintEntries(spamLog.Query(new SpamLogQuery(
                Since: ParseSince(Option(a, "--since") ?? "24h"),
                Stage: Option(a, "--stage"),
                Action: Option(a, "--action"),
                ClientIp: Option(a, "--ip"),
                Search: Option(a, "--search"),
                MinScore: Option(a, "--min-score") is { } min ? double.Parse(min, System.Globalization.CultureInfo.InvariantCulture) : null,
                Limit: int.Parse(Option(a, "--limit") ?? "50"))));
            return 0;

        case ["spamlog", "show", var key]:
        {
            var bySession = spamLog.Query(new SpamLogQuery(Session: key));
            var entries = bySession.Count > 0 ? bySession : spamLog.Query(new SpamLogQuery(Search: key.Trim('<', '>')));
            foreach (var entry in entries.OrderBy(e => e.Time))
            {
                Console.WriteLine($"{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {entry.Stage}/{entry.Action}");
                foreach (var (label, value) in new[]
                         {
                             ("Sitzung", entry.Session), ("IP", entry.ClientIp), ("Reverse DNS", entry.ReverseDns), ("HELO", entry.Helo),
                             ("MAIL FROM", entry.MailFrom), ("Empfänger", entry.Recipient), ("From", entry.HeaderFrom), ("Betreff", entry.Subject),
                             ("Message-ID", entry.MessageId), ("Score", entry.Score?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
                             ("Tests", entry.Tests), ("SPF", entry.Spf), ("DKIM", entry.Dkim), ("DMARC", entry.Dmarc), ("Ordner", entry.Folder),
                             ("Regeln", entry.Rules), ("Hinweis", entry.Detail),
                         }.Where(x => x.Item2 is not null))
                {
                    Console.WriteLine($"    {label + ":",-13} {value}");
                }
            }

            return entries.Count > 0 ? 0 : Fail("Keine Einträge gefunden.");
        }

        case ["spamlog", "stats", ..]:
        {
            var since = ParseSince(Option(a, "--since") ?? "7d");
            PrintStatistics(SpamLogReport.Build(spamLog.Query(new SpamLogQuery(Since: since)), since, options.Spam.JunkThreshold));
            return 0;
        }

        case ["spamlog", "export", var file, ..]:
        {
            var entries = spamLog.Query(new SpamLogQuery(Since: ParseSince(Option(a, "--since") ?? "30d")));
            File.WriteAllText(file, SpamLogReport.ToCsv(entries), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return Ok($"{entries.Count} Einträge nach {file} exportiert.");
        }

        case ["spamlog", "cleanup"]:
            return Ok($"{spamLog.Cleanup()} Einträge gelöscht, die älter als {options.Spam.Log.RetentionDays} Tage waren.");

        case ["queue", "list"]:
            foreach (var entry in queue.List())
            {
                Console.WriteLine($"{entry.Created:yyyy-MM-dd HH:mm} <{entry.Sender}> -> {entry.Recipient}  Versuche: {entry.Attempts}  " +
                                  $"nächster: {entry.NextAttempt.ToLocalTime():HH:mm}  {entry.LastError}");
            }

            return 0;

        case ["queue", "retry"]:
            return Ok($"{queue.RetryAll()} Einträge werden beim nächsten Durchlauf erneut versucht.");

        default:
            Console.WriteLine(
                """
                mailadmin – Verwaltung des Mailservers

                  domain add <domain> [--selector <s>]    Domain anlegen (erzeugt DKIM-Schlüssel, zeigt DNS-Einträge)
                  domain list | domain remove <domain>
                  dns <domain>                            Benötigte DNS-Einträge anzeigen
                  dkim rotate <domain> [--selector <s>]   Neuen DKIM-Schlüssel erzeugen
                  dkim activate <domain> <selector>       DKIM-Selector umstellen

                  user add <adresse> [--password <pw>] [--quota-mb <n>]
                  user passwd <adresse> [--password <pw>]
                  user quota <adresse> <MB>               0 = unbegrenzt
                  user enable|disable|remove <adresse>
                  user admin <adresse> on|off             Zugang zum Admin-Bereich der Weboberfläche
                  user list [domain]

                  alias add <adresse> <ziel>[,<ziel>...]  Ziele dürfen auch externe Adressen sein (Weiterleitung)
                  alias remove <adresse> | alias list

                  forward <adresse> [<ziel>[,<ziel>...] [--no-copy] | off]
                      Leitet alle eingehenden Mails (ohne Spam) weiter; ohne Ziel wird der Stand angezeigt.
                  autoreply <adresse> [on --text "…" | --text-file <datei>] [--subject "…"] [--from 2026-10-10] [--until 2026-10-24]
                            [--interval 7] | autoreply <adresse> off
                      Abwesenheitsnotiz; jeder Absender bekommt sie höchstens alle <interval> Tage.

                  queue list | queue retry

                  rule add <bereich> --if <feld> <operator> <wert> ... --then <aktion>
                      bereich: * (alle Postfächer), eine Domain oder eine Adresse. Beispiele:
                      rule add * --if betreff enthält "Sie haben gewonnen" --then löschen
                      rule add max@example.de --if von endet "@newsletter.example" --then verschieben "Newsletter"
                      rule add example.de --if score über 12 --then löschen
                      rule add max@example.de --if von endet "@kunde.de" --then kein-spam
                  rule list [bereich] | rule remove <id> | rule enable|disable <id>
                  rule test <adresse> <datei.eml> [--score <n>]   zeigt, was mit einer Nachricht passieren würde

                  spamlog list [--since 24h] [--action spam|accepted|rejected|deferred|delivered|discarded|marked-spam|marked-ham]
                               [--stage connect|sender|recipient|data|delivery|feedback] [--ip <ip>] [--search <text>]
                               [--min-score <n>] [--limit 50]
                  spamlog show <sitzung|message-id>      alle Einträge zu einer Mail
                  spamlog stats [--since 7d]             Auswertung mit Hinweisen zur Optimierung
                  spamlog export <datei.csv> [--since 30d]
                  spamlog cleanup

                  export <host> <datei> <zielordner> [--port 993] [--starttls | --no-tls] [--insecure-cert] [--dav <url>] [--no-dav]
                      Sichert Postfächer von einem IMAP-Server (z. B. SmarterMail) als .eml-Dateien mit Ordnern, Flags und
                      Datum, dazu Kontakte (.vcf) und Kalender/Aufgaben (.ics) per CardDAV/CalDAV. <datei> wie bei
                      "import imap". Mehrfach ausführbar: es kommen nur neue Nachrichten dazu. Ohne --dav wird
                      https://<host>/ und http://<host>:9998/ (SmarterMail) versucht.

                  import export <exportordner> [<datei>] [--dry-run]
                      Spielt eine mit "export" erstellte Sicherung in diesen Server ein. Fehlende Postfächer werden mit dem
                      Passwort aus <datei> ("adresse;passwort") angelegt oder es wird abgefragt.

                  tls                                   zeigt, welches TLS-Zertifikat verwendet wird bzw. warum keines passt

                  import imap <host> <datei> [--port 993] [--starttls | --no-tls] [--insecure-cert] [--dry-run]
                      Übernimmt Postfächer von einem anderen IMAP-Server (z. B. SmarterMail). Port 143: --port 143 --starttls
                      (oder --no-tls, nur für localhost). <datei> enthält pro Zeile
                      "adresse;passwort" (ohne Passwort wird es abgefragt). Fehlende Domains und Postfächer werden
                      angelegt, das Passwort wird übernommen. Mehrfach ausführbar: es kommen nur neue Nachrichten dazu.
                """);
            return a.Length == 0 ? 0 : 1;
    }
}

async Task<int> ImportAsync(string[] a)
{
    if (a is ["import", "export", ..])
    {
        return await ImportExportAsync(a);
    }

    if (a is not ["import", "imap", var host, var file, ..])
    {
        return Fail("Aufruf: mailadmin import imap <host> <datei> [--port 993] [--starttls | --no-tls] [--insecure-cert] [--dry-run]");
    }

    var dryRun = a.Contains("--dry-run");
    var source = new ImapSource(
        host,
        int.Parse(Option(a, "--port") ?? "993"),
        SourceSecurity(a, host),
        a.Contains("--insecure-cert"));

    var users = ReadAccountFile(file);
    var importer = new ImapImporter(accounts, mailboxes, new ImportLog(database));
    var errors = 0;
    foreach (var (address, password) in users)
    {
        Console.WriteLine($"{address}{(dryRun ? " (Probelauf)" : "")}");
        if (!accounts.IsLocalDomain(address.Domain) && !dryRun)
        {
            var selector = $"mail{DateTime.UtcNow:yyyyMM}";
            var domain = accounts.AddDomain(address.Domain, selector);
            dkim.GenerateKey(domain.Name, selector);
            Console.WriteLine($"  Domain {domain.Name} angelegt. DNS-Einträge: mailadmin dns {domain.Name}");
        }

        var result = await importer.ImportAsync(source, address, password, dryRun, Console.WriteLine);
        if (result.Error is not null)
        {
            Console.Error.WriteLine($"  FEHLER: {result.Error}");
            errors++;
            continue;
        }

        errors += result.Failed > 0 ? 1 : 0;
        Console.WriteLine($"  {(result.AccountCreated ? "Postfach angelegt, " : "")}{result.Imported} Nachrichten übernommen.");
    }

    Console.WriteLine(errors == 0 ? "Fertig." : $"Fertig, {errors} Postfach/Postfächer mit Fehlern – Ausgabe oben prüfen.");
    if (!dryRun)
    {
        Console.WriteLine($"Hinweis: {file} enthält Passwörter im Klartext – nach der Migration löschen.");
    }

    return errors == 0 ? 0 : 1;
}

async Task<int> ImportExportAsync(string[] a)
{
    if (a is not ["import", "export", var directory, ..])
    {
        return Fail("Aufruf: mailadmin import export <exportordner> [<datei>] [--dry-run]");
    }

    var dryRun = a.Contains("--dry-run");
    var passwords = a.Length > 3 && !a[3].StartsWith("--")
        ? ReadAccountFile(a[3]).ToDictionary(u => u.Address.ToString(), u => u.Password, StringComparer.OrdinalIgnoreCase)
        : [];
    var found = ExportImporter.FindAccounts(directory);
    if (found.Count == 0)
    {
        return Fail($"In {directory} wurde keine Sicherung gefunden (Unterordner mit {ExportManifest.FileName}).");
    }

    var importer = new ExportImporter(accounts, mailboxes, new ImportLog(database));
    var errors = 0;
    foreach (var (address, accountDirectory) in found)
    {
        Console.WriteLine($"{address}{(dryRun ? " (Probelauf)" : "")}");
        if (!accounts.IsLocalDomain(address.Domain) && !dryRun)
        {
            var selector = $"mail{DateTime.UtcNow:yyyyMM}";
            var domain = accounts.AddDomain(address.Domain, selector);
            dkim.GenerateKey(domain.Name, selector);
            Console.WriteLine($"  Domain {domain.Name} angelegt. DNS-Einträge: mailadmin dns {domain.Name}");
        }

        var password = passwords.GetValueOrDefault(address.ToString());
        if (password is null && !dryRun && accounts.FindAccount(address) is null)
        {
            password = ReadHidden($"Neues Passwort für {address}: ");
        }

        var result = await importer.ImportAsync(accountDirectory, password, dryRun, Console.WriteLine);
        if (result.Error is not null)
        {
            Console.Error.WriteLine($"  FEHLER: {result.Error}");
            errors++;
            continue;
        }

        errors += result.Failed > 0 ? 1 : 0;
        Console.WriteLine($"  {(result.AccountCreated ? "Postfach angelegt, " : "")}{result.Imported} Nachrichten übernommen.");
    }

    Console.WriteLine(errors == 0 ? "Fertig." : $"Fertig, {errors} Postfach/Postfächer mit Fehlern – Ausgabe oben prüfen.");
    return errors == 0 ? 0 : 1;
}

int CheckTls()
{
    var hostname = options.Tls.StoreSubject ?? options.Hostname;
    Console.WriteLine($"Gesucht: Zertifikat für {hostname} (Mailserver:Hostname bzw. Tls:StoreSubject)");
    if (hostname.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("  Hinweis: Hostname ist noch der Platzhalter aus appsettings.json.");
    }

    if (!string.IsNullOrEmpty(options.Tls.PfxPath))
    {
        var path = Path.IsPathRooted(options.Tls.PfxPath) ? options.Tls.PfxPath : Path.Combine(AppContext.BaseDirectory, options.Tls.PfxPath);
        Console.WriteLine($"PFX-Datei: {path}");
        try
        {
            using var pfx = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(path, options.Tls.PfxPassword);
            Console.WriteLine($"  {pfx.Subject}, gültig bis {pfx.NotAfter:dd.MM.yyyy}, " +
                              $"{(pfx.MatchesHostname(hostname) ? "passt" : $"gilt NICHT für {hostname}")}");
            return pfx.MatchesHostname(hostname) ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return Fail($"  PFX-Datei lässt sich nicht laden: {ex.Message}");
        }
    }

    if (!OperatingSystem.IsWindows())
    {
        return Fail("Der Zertifikatsspeicher wird nur unter Windows durchsucht; sonst Tls:PfxPath setzen.");
    }

    var candidates = Mailserver.Core.Security.CertificateProvider.Inspect(hostname);
    foreach (var c in candidates.OrderBy(c => c.Problem is null ? 0 : 1).ThenBy(c => c.Store))
    {
        Console.WriteLine();
        Console.WriteLine($"  [{c.Store}] {string.Join(", ", c.Names.DefaultIfEmpty(c.Certificate.Subject))}");
        Console.WriteLine($"      gültig bis {c.Certificate.NotAfter:dd.MM.yyyy}, Aussteller {c.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, true)}, {c.Certificate.Thumbprint}");
        Console.WriteLine($"      {(c.Problem is null ? "-> verwendbar" : $"nicht verwendbar: {c.Problem}")}");
    }

    var usable = candidates.Where(c => c.Problem is null).OrderByDescending(c => c.Certificate.NotAfter).FirstOrDefault();
    Console.WriteLine();
    if (usable is not null)
    {
        Console.WriteLine($"Verwendet wird: [{usable.Store}] {usable.Certificate.Thumbprint}, gültig bis {usable.Certificate.NotAfter:dd.MM.yyyy}");
        return 0;
    }

    Console.WriteLine($"Kein verwendbares Zertifikat für {hostname} ({candidates.Count} Zertifikate in My und WebHosting geprüft).");
    Console.WriteLine("Möglichkeiten: in Plesk ein Let's-Encrypt-Zertifikat ausstellen, das " + hostname + " enthält; mit win-acme ein");
    Console.WriteLine("Zertifikat erzeugen; oder eine vorhandene PFX-Datei unter Mailserver:Tls:PfxPath/PfxPassword eintragen.");
    return 1;
}

void PrintDns(string name)
{
    var domain = accounts.GetDomain(name) ?? throw new InvalidOperationException($"Domain {name} ist nicht angelegt.");
    var selector = domain.DkimSelector is { } sel && dkim.HasKey(domain.Name, sel) ? sel : null;
    Console.WriteLine();
    Console.WriteLine("Benötigte DNS-Einträge:");
    foreach (var record in DnsRecommendations.For(domain.Name, options.Hostname, selector, selector is null ? null : dkim.GetDnsRecord(domain.Name, selector)))
    {
        Console.WriteLine($"  {record.Name,-40} {record.Type,-5} {(record.Type == "TXT" ? $"\"{record.Value}\"" : record.Value)}");
        if (record.Hint is not null)
        {
            Console.WriteLine($"      ({record.Hint})");
        }
    }
}

void PrintEntries(IReadOnlyList<SpamLogEntry> entries)
{
    foreach (var e in entries.OrderBy(e => e.Time))
    {
        var who = e.Stage == SpamLogStage.Feedback ? e.Recipient : e.MailFrom is { Length: > 0 } from ? from : e.ClientIp;
        var score = e.Score is { } value ? value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5) : "     ";
        var what = e.Stage switch
        {
            SpamLogStage.Delivery => $"{e.Recipient} → {(e.Folder ?? "-")}{(e.Rules is null ? "" : $" [{e.Rules}]")}",
            SpamLogStage.Data => $"{e.Subject} [{e.Tests}]",
            _ => e.Detail ?? e.Subject,
        };
        Console.WriteLine($"{e.Time.ToLocalTime():MM-dd HH:mm} {e.Stage,-9} {e.Action,-13} {score}  {who}  {what}");
    }

    Console.WriteLine($"({entries.Count} Einträge; Details: mailadmin spamlog show <sitzung>)");
}

void PrintStatistics(SpamLogStatistics s)
{
    Console.WriteLine($"Spam-Auswertung seit {s.Since.ToLocalTime():yyyy-MM-dd HH:mm}");
    Console.WriteLine();
    Console.WriteLine($"  Abgelehnt (Blacklist):        {s.RejectedConnections,6}");
    Console.WriteLine($"  Abgelehnt (SPF):              {s.RejectedSenders,6}");
    Console.WriteLine($"  Abgelehnt (DMARC):            {s.RejectedMessages,6}");
    Console.WriteLine($"  Greylisting zurückgestellt:   {s.GreylistDeferrals,6}");
    Console.WriteLine($"  Angenommene Nachrichten:      {s.Messages,6}   davon Spam {s.Spam} ({(s.Messages == 0 ? 0 : 100.0 * s.Spam / s.Messages):0}%)");
    Console.WriteLine($"  Zugestellt: Posteingang {s.DeliveredInbox}, Junk {s.DeliveredJunk}, andere Ordner {s.DeliveredOther}; verworfen {s.Discarded}; nicht weitergeleitet {s.NotForwarded}");
    Console.WriteLine();
    Console.WriteLine("  Score-Verteilung:");
    var max = Math.Max(1, s.ScoreBuckets.Max(b => b.Count));
    foreach (var (bucket, count) in s.ScoreBuckets)
    {
        Console.WriteLine($"    {bucket,-6} {count,6}  {new string('#', (int)Math.Ceiling(30.0 * count / max) * Math.Sign(count))}");
    }

    if (s.Tests.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Test                    Gewicht  in Ham  in Spam  Fehlalarm  übersehen");
        foreach (var t in s.Tests)
        {
            Console.WriteLine($"    {t.Name,-22} {t.Weight,6:0.0}  {t.InHam,6}  {t.InSpam,7}  {t.InFalsePositives,9}  {t.InFalseNegatives,9}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  Feedback der Benutzer: {s.FalsePositives.Count} Fehlalarm(e) (aus Junk geholt), {s.FalseNegatives.Count} übersehen (nach Junk verschoben)");
    foreach (var e in s.FalsePositives.Take(10))
    {
        Console.WriteLine($"    Fehlalarm  {e.Score,5:0.0}  {e.HeaderFrom}  {e.Subject}  [{e.Tests}]");
    }

    foreach (var e in s.FalseNegatives.Take(10))
    {
        Console.WriteLine($"    übersehen  {e.Score,5:0.0}  {e.HeaderFrom}  {e.Subject}  [{e.Tests}]");
    }

    if (s.Rules.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Regeln (Treffer):");
        foreach (var (rule, count) in s.Rules)
        {
            Console.WriteLine($"    {count,6}  {rule}");
        }
    }

    if (s.TopSpamSenders.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Häufigste Spam-Absender: " + string.Join(", ", s.TopSpamSenders.Select(x => $"{x.Domain} ({x.Count})")));
    }

    if (s.TopRejectedIps.Count > 0)
    {
        Console.WriteLine("  Häufigste abgelehnte IPs: " + string.Join(", ", s.TopRejectedIps.Select(x => $"{x.Ip} ({x.Count})")));
    }

    Console.WriteLine();
    Console.WriteLine(s.Hints.Count == 0 ? "  Keine Auffälligkeiten." : "  Hinweise:");
    foreach (var hint in s.Hints)
    {
        Console.WriteLine($"    • {hint}");
    }
}

static DateTimeOffset ParseSince(string value)
{
    var unit = value[^1];
    if (unit is 'm' or 'h' or 'd' && int.TryParse(value[..^1], out var amount))
    {
        return DateTimeOffset.UtcNow - (unit == 'm' ? TimeSpan.FromMinutes(amount) : unit == 'h' ? TimeSpan.FromHours(amount) : TimeSpan.FromDays(amount));
    }

    return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var date)
        ? date
        : throw new ArgumentException($"Ungültige Zeitangabe '{value}' – z. B. 30m, 24h, 7d oder 2026-10-01.");
}

void CheckScope(string scope)
{
    if (scope == RuleStore.GlobalScope)
    {
        return;
    }

    var exists = scope.Contains('@') ? accounts.FindAccount(EmailAddress.Parse(scope)) is not null : accounts.IsLocalDomain(scope);
    if (!exists)
    {
        throw new ArgumentException($"{scope} ist weder Postfach noch Domain dieses Servers.");
    }
}

static async Task<int> ExportAsync(string[] a)
{
    if (a is not ["export", var host, var file, var target, ..] || target.StartsWith("--"))
    {
        return Fail("Aufruf: mailadmin export <host> <datei> <zielordner> [--port 993] [--starttls | --no-tls] [--insecure-cert] [--dav <url>] [--no-dav]");
    }

    var insecure = a.Contains("--insecure-cert");
    var source = new ImapSource(
        host,
        int.Parse(Option(a, "--port") ?? "993"),
        SourceSecurity(a, host),
        insecure);
    var davServers = a.Contains("--no-dav") ? []
        : Option(a, "--dav") is { } davUrl ? [new Uri(davUrl)]
        : new[] { new Uri($"https://{host}/"), new Uri($"http://{host}:9998/") };

    Directory.CreateDirectory(target);
    var exporter = new MailboxExporter();
    var davExporter = new DavExporter();
    var errors = 0;
    foreach (var (address, password) in ReadAccountFile(file))
    {
        Console.WriteLine(address);
        var result = await exporter.ExportAsync(source, address, password, target, Console.WriteLine);
        if (result.Error is not null)
        {
            Console.Error.WriteLine($"  FEHLER: {result.Error}");
            errors++;
            continue;
        }

        errors += result.Failed > 0 ? 1 : 0;
        Console.WriteLine($"  {result.Exported} Nachrichten neu gesichert, {result.Total} insgesamt in {result.Directory}");

        DavExportResult? dav = null;
        foreach (var server in davServers)
        {
            dav = await davExporter.ExportAsync(server, address, password, target, insecure, Console.WriteLine);
            if (dav.ServerFound)
            {
                break;
            }
        }

        if (dav is null)
        {
            continue;
        }

        if (!dav.ServerFound)
        {
            Console.Error.WriteLine($"  WARNUNG: Kontakte/Kalender nicht gesichert, kein CalDAV/CardDAV unter " +
                                    $"{string.Join(" oder ", davServers.Select(s => s.ToString()))} gefunden (Adresse mit --dav angeben).");
            continue;
        }

        foreach (var warning in dav.Warnings)
        {
            Console.Error.WriteLine($"  WARNUNG: {warning}");
        }

        Console.WriteLine($"  {dav.Contacts} Kontakte, {dav.CalendarItems} Termine/Aufgaben gesichert.");
    }

    Console.WriteLine(errors == 0 ? $"Fertig. Sicherung in {Path.GetFullPath(target)}" : $"Fertig, {errors} Postfach/Postfächer mit Fehlern – Ausgabe oben prüfen.");
    Console.WriteLine($"Hinweis: {file} enthält Passwörter im Klartext – danach löschen.");
    return errors == 0 ? 0 : 1;
}

/// <summary>
/// --starttls (port 143) or implicit TLS (default, 993). --no-tls is only allowed for this machine, where the password
/// never leaves the server — for a local SmarterMail that offers neither.
/// </summary>
static MailKit.Security.SecureSocketOptions SourceSecurity(string[] a, string host)
{
    if (a.Contains("--no-tls"))
    {
        return host is "localhost" or "127.0.0.1" or "::1"
            ? MailKit.Security.SecureSocketOptions.None
            : throw new ArgumentException("--no-tls ist nur für localhost erlaubt – sonst gingen Passwörter unverschlüsselt über das Netz.");
    }

    return a.Contains("--starttls") ? MailKit.Security.SecureSocketOptions.StartTls : MailKit.Security.SecureSocketOptions.SslOnConnect;
}

static List<(EmailAddress Address, string Password)> ReadAccountFile(string file)
{
    var users = new List<(EmailAddress Address, string Password)>();
    foreach (var line in File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
    {
        var separator = line.IndexOf(';');
        var address = EmailAddress.Parse(separator < 0 ? line : line[..separator]);
        users.Add((address, separator < 0 ? ReadHidden($"Passwort für {address}: ") : line[(separator + 1)..]));
    }

    return users;
}

static string? Option(string[] a, string name)
{
    var index = Array.IndexOf(a, name);
    return index >= 0 && index + 1 < a.Length ? a[index + 1] : null;
}

static long ParseQuota(string? megabytes) => megabytes is null ? 0 : long.Parse(megabytes) * 1024 * 1024;

static string ReadNewPassword()
{
    var first = ReadHidden("Passwort: ");
    if (first.Length < 10)
    {
        throw new ArgumentException("Das Passwort muss mindestens 10 Zeichen lang sein.");
    }

    return first == ReadHidden("Wiederholen: ") ? first : throw new ArgumentException("Die Passwörter stimmen nicht überein.");
}

static string ReadHidden(string prompt)
{
    Console.Write(prompt);
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? "";
    }

    var password = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return password.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (password.Length > 0)
            {
                password.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password.Append(key.KeyChar);
        }
    }
}

static bool Confirm(string question)
{
    Console.Write($"{question} [j/N] ");
    return Console.ReadLine()?.Trim().ToLowerInvariant() is "j" or "ja" or "y" or "yes";
}

static int Ok(string message)
{
    Console.WriteLine(message);
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
