using Npgsql;
using Server.Auth;
using Server.Db;
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

// One pooled data source for the whole app; hand-written SQL through Npgsql.
builder.Services.AddSingleton(NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Monitoring")
    ?? throw new InvalidOperationException("ConnectionStrings:Monitoring is missing")));

builder.Services.AddSingleton<MachineRegistry>();
builder.Services.AddSingleton<ISampleStore, PostgresSampleStore>();

var app = builder.Build();

await SchemaInitializer.ApplyAsync(
    app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger, app.Lifetime.ApplicationStopping);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapIngest();

app.Run();
