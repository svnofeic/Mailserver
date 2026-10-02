using Mailserver.Smtp;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // A Windows service starts in C:\Windows\System32; configuration lives next to the executable.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(options => options.ServiceName = "Mailserver");
builder.Services.AddMailserver(builder.Configuration);

var host = builder.Build();
host.Run();
