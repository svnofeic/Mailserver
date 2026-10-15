using System.Security;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailserver.Web;

/// <summary>
/// Settings for mail programs to pick up by themselves: a configuration profile for iPhone, iPad and Mac, the Thunderbird
/// "autoconfig" file and Outlook's "autodiscover" answer. None of them contains a password; the program asks for it.
/// </summary>
public static class ClientSetup
{
    private static string Escape(string text) => SecurityElement.Escape(text);

    /// <summary>The submission port offered to programs: 587 (STARTTLS) if open, else 465.</summary>
    private static (int Port, bool ImplicitTls) Submission(MailserverOptions options) =>
        options.Smtp.SubmissionPort > 0 ? (options.Smtp.SubmissionPort, false) : (options.Smtp.SubmissionTlsPort, true);

    /// <summary>Thunderbird's format (also read by K-9/Thunderbird for Android, FairEmail and Evolution).</summary>
    public static string Autoconfig(MailserverOptions options, string domain)
    {
        var host = Escape(options.Hostname);
        var outgoing = new StringBuilder();
        if (options.Smtp.SubmissionTlsPort > 0)
        {
            outgoing.Append(Server("outgoingServer", "smtp", host, options.Smtp.SubmissionTlsPort, "SSL"));
        }

        if (options.Smtp.SubmissionPort > 0)
        {
            outgoing.Append(Server("outgoingServer", "smtp", host, options.Smtp.SubmissionPort, "STARTTLS"));
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <clientConfig version="1.1">
              <emailProvider id="{Escape(domain)}">
                <domain>{Escape(domain)}</domain>
                <displayName>{Escape(domain)}</displayName>
                <displayShortName>{Escape(domain)}</displayShortName>
            {Server("incomingServer", "imap", host, options.Imap.TlsPort, "SSL")}{outgoing}  </emailProvider>
            </clientConfig>

            """;

        static string Server(string element, string type, string host, int port, string socket) => $"""
                <{element} type="{type}">
                  <hostname>{host}</hostname>
                  <port>{port}</port>
                  <socketType>{socket}</socketType>
                  <authentication>password-cleartext</authentication>
                  <username>%EMAILADDRESS%</username>
                </{element}>

            """;
    }

    /// <summary>Outlook's answer (POX schema 2006a) for <paramref name="address"/>.</summary>
    public static string Autodiscover(MailserverOptions options, string address)
    {
        var (port, implicitTls) = Submission(options);
        var host = Escape(options.Hostname);
        var login = Escape(address);
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006">
              <Response xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a">
                <Account>
                  <AccountType>email</AccountType>
                  <Action>settings</Action>
                  <Protocol>
                    <Type>IMAP</Type>
                    <Server>{host}</Server>
                    <Port>{options.Imap.TlsPort}</Port>
                    <LoginName>{login}</LoginName>
                    <DomainRequired>off</DomainRequired>
                    <SPA>off</SPA>
                    <SSL>on</SSL>
                    <AuthRequired>on</AuthRequired>
                  </Protocol>
                  <Protocol>
                    <Type>SMTP</Type>
                    <Server>{host}</Server>
                    <Port>{port}</Port>
                    <LoginName>{login}</LoginName>
                    <DomainRequired>off</DomainRequired>
                    <SPA>off</SPA>
                    <SSL>on</SSL>
                    <Encryption>{(implicitTls ? "SSL" : "TLS")}</Encryption>
                    <AuthRequired>on</AuthRequired>
                    <UsePOPAuth>on</UsePOPAuth>
                  </Protocol>
                </Account>
              </Response>
            </Autodiscover>

            """;
    }

    /// <summary>A configuration profile (Apple) that adds the mailbox to the Mail app; the password is asked for on installation.</summary>
    public static string AppleProfile(MailserverOptions options, Account account)
    {
        var (port, _) = Submission(options);
        var address = Escape(account.Address.ToString());
        var host = Escape(options.Hostname);
        // Stable identifiers: installing again replaces the profile instead of adding a second account.
        var id = $"{string.Join('.', options.Hostname.Split('.').Reverse())}.mail.{account.Id}";
        var uuid = new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(id)));
        var payloadUuid = new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(id + ".account")));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>PayloadContent</key>
              <array>
                <dict>
                  <key>PayloadType</key><string>com.apple.mail.managed</string>
                  <key>PayloadVersion</key><integer>1</integer>
                  <key>PayloadIdentifier</key><string>{Escape(id)}.account</string>
                  <key>PayloadUUID</key><string>{payloadUuid.ToString().ToUpperInvariant()}</string>
                  <key>PayloadDisplayName</key><string>{address}</string>
                  <key>EmailAccountDescription</key><string>{address}</string>
                  <key>EmailAccountType</key><string>EmailTypeIMAP</string>
                  <key>EmailAddress</key><string>{address}</string>
                  <key>IncomingMailServerHostName</key><string>{host}</string>
                  <key>IncomingMailServerPortNumber</key><integer>{options.Imap.TlsPort}</integer>
                  <key>IncomingMailServerUseSSL</key><true/>
                  <key>IncomingMailServerAuthentication</key><string>EmailAuthPassword</string>
                  <key>IncomingMailServerUsername</key><string>{address}</string>
                  <key>OutgoingMailServerHostName</key><string>{host}</string>
                  <key>OutgoingMailServerPortNumber</key><integer>{port}</integer>
                  <key>OutgoingMailServerUseSSL</key><true/>
                  <key>OutgoingMailServerAuthentication</key><string>EmailAuthPassword</string>
                  <key>OutgoingMailServerUsername</key><string>{address}</string>
                  <key>OutgoingPasswordSameAsIncomingPassword</key><true/>
                  <key>SMIMEEnabled</key><false/>
                </dict>{DavPayloads(options, account, id)}
              </array>
              <key>PayloadType</key><string>Configuration</string>
              <key>PayloadVersion</key><integer>1</integer>
              <key>PayloadIdentifier</key><string>{Escape(id)}</string>
              <key>PayloadUUID</key><string>{uuid.ToString().ToUpperInvariant()}</string>
              <key>PayloadDisplayName</key><string>E-Mail {address}</string>
              <key>PayloadDescription</key><string>Richtet das Postfach {address} in der Mail-App ein{(HasDav(options) ? ", dazu Kalender und Kontakte" : "")}. Das Passwort wird bei der Installation abgefragt.</string>
              <key>PayloadOrganization</key><string>{host}</string>
              <key>PayloadRemovalDisallowed</key><false/>
            </dict>
            </plist>

            """;
    }

    private static bool HasDav(MailserverOptions options) => options.Web.Enabled && options.Web.HttpsPort > 0;

    /// <summary>Calendar (CalDAV) and contacts (CardDAV) accounts for the profile, when the web interface runs with HTTPS.</summary>
    private static string DavPayloads(MailserverOptions options, Account account, string id)
    {
        if (!HasDav(options))
        {
            return "";
        }

        var address = Escape(account.Address.ToString());
        var host = Escape(options.Hostname);
        var principal = Escape($"{Dav.DavHandler.Root}principals/{account.Address}/");
        string Payload(string kind, string type, string description)
        {
            var uuid = new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes($"{id}.{kind}")));
            var prefix = kind == "caldav" ? "CalDAV" : "CardDAV";
            return $"""

                    <dict>
                      <key>PayloadType</key><string>{type}</string>
                      <key>PayloadVersion</key><integer>1</integer>
                      <key>PayloadIdentifier</key><string>{Escape(id)}.{kind}</string>
                      <key>PayloadUUID</key><string>{uuid.ToString().ToUpperInvariant()}</string>
                      <key>PayloadDisplayName</key><string>{description} {address}</string>
                      <key>{prefix}AccountDescription</key><string>{description} {address}</string>
                      <key>{prefix}HostName</key><string>{host}</string>
                      <key>{prefix}Port</key><integer>{options.Web.HttpsPort}</integer>
                      <key>{prefix}PrincipalURL</key><string>{principal}</string>
                      <key>{prefix}UseSSL</key><true/>
                      <key>{prefix}Username</key><string>{address}</string>
                    </dict>
                """;
        }

        return Payload("caldav", "com.apple.caldav.account", "Kalender") + Payload("carddav", "com.apple.carddav.account", "Kontakte");
    }

    /// <summary>
    /// Signs the profile with the server's certificate, so the iPhone shows it as "verified". Without a usable certificate
    /// (self-signed during tests) the plain profile is returned – it installs just the same, marked "not verified".
    /// </summary>
    public static byte[] Sign(byte[] profile, X509Certificate2? certificate)
    {
        if (certificate is not { HasPrivateKey: true })
        {
            return profile;
        }

        // With the intermediate certificate if the chain can be built, else the server certificate alone.
        foreach (var include in new[] { X509IncludeOption.ExcludeRoot, X509IncludeOption.EndCertOnly })
        {
            try
            {
                var signed = new SignedCms(new ContentInfo(profile), detached: false);
                signed.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate) { IncludeOption = include });
                return signed.Encode();
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
            }
        }

        return profile;
    }

    public static void Map(WebApplication app)
    {
        // Thunderbird asks https://autoconfig.<domain>/mail/config-v1.1.xml?emailaddress=… or the domain's /.well-known path.
        IResult Config(HttpContext context, string? emailaddress)
        {
            var services = context.RequestServices;
            var options = services.GetRequiredService<IOptions<MailserverOptions>>().Value;
            var domain = EmailAddress.TryParse(emailaddress ?? "", out var address) ? address.Domain : DomainFromHost(context.Request.Host.Host);
            if (domain is null || !services.GetRequiredService<AccountStore>().IsLocalDomain(domain))
            {
                domain = options.Hostname;
            }

            return Results.Text(Autoconfig(options, domain), "application/xml; charset=utf-8");
        }

        app.MapGet("/mail/config-v1.1.xml", (Delegate)Config).AllowAnonymous();
        app.MapGet("/.well-known/autoconfig/mail/config-v1.1.xml", (Delegate)Config).AllowAnonymous();

        // Outlook posts the address to /autodiscover/autodiscover.xml (found via the SRV record _autodiscover._tcp.<domain>).
        async Task<IResult> Discover(HttpContext context)
        {
            var services = context.RequestServices;
            string? requested = null;
            if (HttpMethods.IsPost(context.Request.Method))
            {
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var body = await reader.ReadToEndAsync(context.RequestAborted);
                    requested = System.Xml.Linq.XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == "EMailAddress")?.Value;
                }
                catch (System.Xml.XmlException)
                {
                }
            }

            if (!EmailAddress.TryParse(requested ?? "", out var address) ||
                !services.GetRequiredService<AccountStore>().IsLocalDomain(address.Domain))
            {
                // With a body: the error page would otherwise re-run this POST against itself.
                return Results.Content("Keine Adresse dieses Servers.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
            }

            services.GetRequiredService<ILoggerFactory>().CreateLogger("Mailserver.Web").LogInformation("Autodiscover for {Address}", address);
            return Results.Text(Autodiscover(services.GetRequiredService<IOptions<MailserverOptions>>().Value, address.ToString()),
                "application/xml; charset=utf-8");
        }

        // Routes ignore case, so this also answers /Autodiscover/Autodiscover.xml.
        app.MapMethods("/autodiscover/autodiscover.xml", [HttpMethods.Get, HttpMethods.Post], (Delegate)Discover).AllowAnonymous().DisableAntiforgery();
    }

    private static string? DomainFromHost(string host) =>
        host.StartsWith("autoconfig.", StringComparison.OrdinalIgnoreCase) ? host["autoconfig.".Length..] : host;
}
