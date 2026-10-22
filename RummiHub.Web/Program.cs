using System.Text.Json;
using System.Text.Json.Serialization;
using RummiHub.Core.Contracts;
using RummiHub.Web.Components;
using RummiHub.Web.Services;
using RummiHub.Web.Hubs;

var builder = WebApplication.CreateBuilder(args);

var solverKey = builder.Configuration["SolverApi:Key"]?.Trim();
if (string.IsNullOrEmpty(solverKey) && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "SolverApi:Key is required outside Development. Set SolverApi__Key to the same secret on webfrontend and apiservice.");
}

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

builder.AddRedisClient("cache");

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
builder.Services.AddSingleton<ISeedStore, AppSettingsSeedStore>();
builder.Services.AddSingleton<IGameManager, GameManager>();

builder.Services.AddTransient<SolverApiKeyHandler>();
builder.Services.AddHttpClient("apiservice",
        client => client.BaseAddress = new Uri("https+http://apiservice"))
    .AddHttpMessageHandler<SolverApiKeyHandler>();

// Singleton service that manages Redis Pub/Sub subscriptions and dispatches results to Blazor components
builder.Services.AddSingleton<SolveResultSubscriber>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<GameHub>("/hubs/game");
app.MapDefaultEndpoints();

app.Run();
