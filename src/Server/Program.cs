using Npgsql;
using Server.Api;
using Server.Auth;
using Server.Config;
using Server.Db;
using Server.Ingest;
using Shared;

var builder = WebApplication.CreateBuilder(args);

// Machine-specific overrides (e.g. AgentConfig:ProjectRoot) and the machine
// registry (machines.json, written by tools/FleetSim prepare) live in
// git-ignored files so local paths and keys never end up in the repository.
// ConfigDir points the container at its mounted folder; on the host both
// files sit beside appsettings.json. Arrays merge by index, so machines.json
// must repeat the appsettings.json entry at index 0 (see MachineRegistryMergeTests).
var configDir = builder.Configuration["ConfigDir"] ?? builder.Environment.ContentRootPath;
builder.Configuration.AddJsonFile(Path.Combine(configDir, "appsettings.Local.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(Path.Combine(configDir, "machines.json"), optional: true, reloadOnChange: false);

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

builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.Section));
builder.Services.Configure<StatusOptions>(builder.Configuration.GetSection(StatusOptions.Section));
builder.Services.Configure<AgentConfigOptions>(builder.Configuration.GetSection(AgentConfigOptions.Section));
builder.Services.AddSingleton<IngestThrottle>();   // one semaphore for the whole app
builder.Services.AddSingleton<ApiKeyAuth>();
builder.Services.AddSingleton<MachineRegistry>();
builder.Services.AddSingleton<ISampleStore, PostgresSampleStore>();

var app = builder.Build();

await SchemaInitializer.ApplyAsync(
    app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger, app.Lifetime.ApplicationStopping);

// The portal: one static page in wwwroot that calls the query API.
app.UseDefaultFiles();
app.UseStaticFiles();

// Ingest guards run as middleware on that one path, before the body is read:
// a saturated server or an unknown key is answered without parsing a byte.
app.UseWhen(ctx => ctx.Request.Path == Routes.Samples, ingest =>
{
    ingest.UseMiddleware<IngestThrottle>();
    ingest.UseMiddleware<ApiKeyAuth>();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapIngest();
app.MapQueryApi();

app.Run();
