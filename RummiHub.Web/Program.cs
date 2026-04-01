using System.Text.Json;
using System.Text.Json.Serialization;
using RummiHub.Web.Components;
using RummiHub.Web.Services;
using RummiHub.Web.Hubs;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<IGameManager, GameManager>();

builder.Services.AddHttpClient("apiservice",
    client => client.BaseAddress = new Uri("https+http://apiservice"));

// Service singleton qui gère les abonnements Redis Pub/Sub et dispatche les résultats aux composants Blazor
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
