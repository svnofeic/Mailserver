using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Data;
using Mailserver.Core.Dkim;
using Mailserver.Core.Migration;
using Mailserver.Core.Queue;
using Mailserver.Core.Storage;
using Mailserver.Migration;
using Microsoft.Extensions.Configuration;

// mailadmin — command line administration. Reads the same appsettings.json as the service (next to the executable).

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();
var options = configuration.GetSection(MailserverOptions.SectionName).Get<MailserverOptions>() ?? new MailserverOptions();

var paths = new DataPaths(options.DataDirectory);
paths.EnsureCreated();
var database = new Database(paths);
database.Migrate();
var accounts = new AccountStore(database);
var mailboxes = new MailboxStore(database, paths);
var dkim = new DkimKeyStore(paths);
var queue = new OutboundQueue(database, paths);

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
                Console.WriteLine($"{account.Address,-45} {usage,8:F1} MB / {quota,-10} {(account.Enabled ? "" : "(deaktiviert)")}");
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
                  user list [domain]

                  alias add <adresse> <ziel>[,<ziel>...]  Ziele dürfen auch externe Adressen sein (Weiterleitung)
                  alias remove <adresse> | alias list

                  queue list | queue retry

                  import imap <host> <datei> [--port 993] [--starttls] [--insecure-cert] [--dry-run]
                      Übernimmt Postfächer von einem anderen IMAP-Server (z. B. SmarterMail). <datei> enthält pro Zeile
                      "adresse;passwort" (ohne Passwort wird es abgefragt). Fehlende Domains und Postfächer werden
                      angelegt, das Passwort wird übernommen. Mehrfach ausführbar: es kommen nur neue Nachrichten dazu.
                """);
            return a.Length == 0 ? 0 : 1;
    }
}

async Task<int> ImportAsync(string[] a)
{
    if (a is not ["import", "imap", var host, var file, ..])
    {
        return Fail("Aufruf: mailadmin import imap <host> <datei> [--port 993] [--starttls] [--insecure-cert] [--dry-run]");
    }

    var dryRun = a.Contains("--dry-run");
    var source = new ImapSource(
        host,
        int.Parse(Option(a, "--port") ?? "993"),
        a.Contains("--starttls") ? MailKit.Security.SecureSocketOptions.StartTls : MailKit.Security.SecureSocketOptions.SslOnConnect,
        a.Contains("--insecure-cert"));

    var users = new List<(EmailAddress Address, string Password)>();
    foreach (var line in File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
    {
        var separator = line.IndexOf(';');
        var address = EmailAddress.Parse(separator < 0 ? line : line[..separator]);
        users.Add((address, separator < 0 ? ReadHidden($"Passwort für {address}: ") : line[(separator + 1)..]));
    }

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

void PrintDns(string name)
{
    var domain = accounts.GetDomain(name) ?? throw new InvalidOperationException($"Domain {name} ist nicht angelegt.");
    var host = options.Hostname;
    Console.WriteLine();
    Console.WriteLine("Benötigte DNS-Einträge:");
    Console.WriteLine($"  {host,-40} A      <öffentliche IPv4 des Servers>");
    Console.WriteLine($"  {domain.Name,-40} MX 10  {host}.");
    Console.WriteLine($"  {domain.Name,-40} TXT    \"v=spf1 mx -all\"");
    Console.WriteLine($"  {"_dmarc." + domain.Name,-40} TXT    \"v=DMARC1; p=none; rua=mailto:postmaster@{domain.Name}\"");
    if (domain.DkimSelector is { } selector && dkim.HasKey(domain.Name, selector))
    {
        Console.WriteLine($"  {selector + "._domainkey." + domain.Name,-40} TXT    \"{dkim.GetDnsRecord(domain.Name, selector)}\"");
        Console.WriteLine("    (Manche DNS-Anbieter verlangen, den DKIM-Wert in Stücke zu höchstens 255 Zeichen zu teilen.)");
    }

    Console.WriteLine($"  PTR (beim VPS-Anbieter): <IPv4>  ->  {host}");
    Console.WriteLine("  DMARC nach erfolgreichen Tests auf p=quarantine bzw. p=reject verschärfen.");
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
