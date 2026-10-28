using Agent;
using Microsoft.Extensions.Hosting.WindowsServices;

if (ServiceInstaller.TryHandle(args))
    return;

// A service starts with System32 as its current directory, so under the
// Service Control Manager the content root (appsettings.json, data/) is the
// executable's folder. From a console it stays the current directory.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

// Console app by default; a Windows Service when the Service Control Manager
// starts it (AddWindowsService detects that and switches lifetime and logging).
builder.Services.AddWindowsService(o => o.ServiceName = ServiceInstaller.ServiceName);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton<Outbox>();
builder.Services.AddSingleton<ConfigCache>();
builder.Services.AddSingleton<LastError>();        // the heartbeat's last_error, written by all three loops
builder.Services.AddHostedService<SamplingLoop>();
builder.Services.AddHostedService<ProjectWalker>();
builder.Services.AddHostedService<Uploader>();

var host = builder.Build();

var outbox = host.Services.GetRequiredService<Outbox>();
host.Services.GetRequiredService<ILogger<Program>>()
    .LogInformation("Outbox {Path}: install_id {InstallId}, {Depth} rows waiting", outbox.Path, outbox.InstallId, outbox.Depth());

host.Run();
