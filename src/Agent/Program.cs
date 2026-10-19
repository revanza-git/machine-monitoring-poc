using Agent;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton<ISampleSink, ConsoleSink>();
builder.Services.AddHostedService<SamplingLoop>();

builder.Build().Run();
