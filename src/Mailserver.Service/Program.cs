using Mailserver.Core;
using Mailserver.Imap;
using Mailserver.Smtp;
using Mailserver.Web;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A Windows service starts in C:\Windows\System32; configuration lives next to the executable.
    ContentRootPath = AppContext.BaseDirectory,
});

// Settings changed in the web interface are stored in data\settings.json and override appsettings.json while running.
var dataPaths = new DataPaths(builder.Configuration[$"{MailserverOptions.SectionName}:DataDirectory"] ?? "data");
dataPaths.EnsureCreated();
builder.Configuration.AddJsonFile(dataPaths.SettingsFile, optional: true, reloadOnChange: true);

builder.Services.AddWindowsService(options => options.ServiceName = "Mailserver");
// Stopping (e.g. for an update) must not take longer than Windows waits: all parts stop at the same time, and whatever is
// still busy after 20 seconds (a delivery, an IMAP fetch at another provider) is abandoned and done again after the start.
builder.Services.Configure<HostOptions>(host =>
{
    host.ShutdownTimeout = TimeSpan.FromSeconds(20);
    host.ServicesStopConcurrently = true;
});
builder.Services.AddMailserver(builder.Configuration);
builder.Services.AddImapServer();
builder.AddMailserverWeb();

var app = builder.Build();
app.UseMailserverWeb();
app.Logger.LogInformation("Mailserver version {Version}", BuildInfo.Version);
app.Run();
