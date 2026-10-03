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
builder.Services.AddMailserver(builder.Configuration);
builder.Services.AddImapServer();
builder.AddMailserverWeb();

var app = builder.Build();
app.UseMailserverWeb();
app.Run();
