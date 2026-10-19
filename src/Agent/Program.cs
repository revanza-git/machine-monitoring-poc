using Agent;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton<Outbox>();
builder.Services.AddHostedService<SamplingLoop>();

var host = builder.Build();

var outbox = host.Services.GetRequiredService<Outbox>();
host.Services.GetRequiredService<ILogger<Program>>()
    .LogInformation("Outbox {Path}: install_id {InstallId}, {Depth} rows waiting", outbox.Path, outbox.InstallId, outbox.Depth());

host.Run();
