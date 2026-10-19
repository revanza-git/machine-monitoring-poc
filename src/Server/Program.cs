using Server.Auth;
using Server.Ingest;
using Shared;

var builder = WebApplication.CreateBuilder(args);

// Same JSON shape as the agent: snake_case, from Shared.JsonDefaults.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonDefaults.Options.PropertyNamingPolicy;
    o.SerializerOptions.DictionaryKeyPolicy = JsonDefaults.Options.DictionaryKeyPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = JsonDefaults.Options.DefaultIgnoreCondition;
});

builder.Services.AddSingleton<MachineRegistry>();
builder.Services.AddSingleton<ISampleStore, InMemorySampleStore>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapIngest();

app.Run();
