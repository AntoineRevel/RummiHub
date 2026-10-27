using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using RummiHub.Core.Contracts;
using RummiHub.Web.Components;
using RummiHub.Web.Services;
using RummiHub.Web.Hubs;

var builder = WebApplication.CreateBuilder(args);

// No "Server: Kestrel" header: it only tells a scanner what runs behind Caddy.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// Set by deploy/docker-compose.yml. Caddy is the only public listener and terminates TLS.
var trustForwardedHeaders = string.Equals(
    builder.Configuration["ASPNETCORE_FORWARDEDHEADERS_ENABLED"],
    "true",
    StringComparison.OrdinalIgnoreCase);

if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        // The process is not published. Every peer is the reverse proxy on the docker network.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    Directory.CreateDirectory(keysPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("RummiHub");
}

var solverKey = builder.Configuration["SolverApi:Key"]?.Trim();
if (string.IsNullOrEmpty(solverKey) && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "SolverApi:Key is required outside Development. Set SolverApi__Key to the same secret on webfrontend and apiservice.");
}

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

builder.AddRedisClient("cache");

// Secure flag whenever the request is HTTPS (behind Caddy too, once forwarded headers
// have set the scheme). Plain-HTTP local runs keep a cookie the browser sends back.
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.Configure<GameOptions>(builder.Configuration.GetSection("GameOptions"));
builder.Services.Configure<SolverApiOptions>(builder.Configuration.GetSection(SolverApiOptions.SectionName));
builder.Services.Configure<SeedCatalogOptions>(builder.Configuration.GetSection(SeedCatalogOptions.SectionName));
var seedOptions = builder.Configuration.GetSection(SeedCatalogOptions.SectionName).Get<SeedCatalogOptions>()
                  ?? new SeedCatalogOptions();
if (seedOptions.IsRemote)
{
    if (string.IsNullOrWhiteSpace(seedOptions.RemoteBaseAddress) || !SeedCatalogApi.IsKeyStrong(seedOptions.ApiKey))
    {
        throw new InvalidOperationException(
            "SeedCatalog:Mode is Remote, so SeedCatalog:RemoteBaseAddress and a SeedCatalog:ApiKey of at least 32 characters are required.");
    }

    var remoteBase = seedOptions.RemoteBaseAddress.Trim();
    if (!remoteBase.EndsWith('/'))
        remoteBase += "/";

    builder.Services.AddHttpClient(RemoteSeedCatalog.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(remoteBase);
            client.Timeout = TimeSpan.FromSeconds(4);
            client.DefaultRequestHeaders.Add(SeedCatalogApi.KeyHeader, seedOptions.ApiKey.Trim());
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
    builder.Services.AddSingleton<ISeedCatalog, RemoteSeedCatalog>();
}
else
{
    builder.Services.AddSingleton<ISeedCatalog>(sp =>
    {
        var environment = sp.GetRequiredService<IHostEnvironment>();
        var path = SeedCatalogOptions.ResolvePath(seedOptions, environment);
        var predefined = sp.GetRequiredService<IOptions<GameOptions>>().Value.PredefinedIds;
        return new FileSeedCatalog(path, predefined, sp.GetRequiredService<ILogger<FileSeedCatalog>>());
    });
}

builder.Services.AddSingleton<SeedCatalogRateGate>();
var benchAccess = SolverBenchAccess.From(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(benchAccess);
builder.Services.AddSingleton<IGameManager, GameManager>();

builder.Services.AddTransient<SolverApiKeyHandler>();
// Aspire injects service discovery for https+http://apiservice. Compose sets a plain
// ApiService:BaseAddress (http://apiservice:8080) and the matching services__ endpoint.
var apiBaseAddress = builder.Configuration["ApiService:BaseAddress"];
if (string.IsNullOrWhiteSpace(apiBaseAddress))
    apiBaseAddress = "https+http://apiservice";

builder.Services.AddHttpClient("apiservice",
        client => client.BaseAddress = new Uri(apiBaseAddress))
    .AddHttpMessageHandler<SolverApiKeyHandler>();

// Singleton service that manages Redis Pub/Sub subscriptions and dispatches results to Blazor components
builder.Services.AddSingleton<SolveResultSubscriber>();

var app = builder.Build();

if (trustForwardedHeaders)
    app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Behind Caddy, HTTP→HTTPS happens at the proxy. Redirecting here would also send the
// in-process SignalR client (plain HTTP to loopback) at a TLS port nothing listens on.
if (!trustForwardedHeaders)
    app.UseHttpsRedirection();

// The router is static, so reaching /bench always goes through an HTTP request: a 404 here
// means the Bench component is never created and no benchmark can be started.
if (!benchAccess.Enabled)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments(SolverBenchAccess.PagePath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    });
}

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<GameHub>("/hubs/game");
if (!seedOptions.IsRemote && SeedCatalogApi.IsKeyStrong(seedOptions.ApiKey))
    SeedCatalogApi.Map(app, seedOptions.ApiKey);
else if (!seedOptions.IsRemote && !string.IsNullOrWhiteSpace(seedOptions.ApiKey))
    app.Logger.LogWarning("Seed catalog API is off: SeedCatalog:ApiKey must be at least 32 characters.");
app.MapDefaultEndpoints();

app.Run();
