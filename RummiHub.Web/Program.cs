using RummiHub.Web.Components;
using RummiHub.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Redis pour Pub/Sub (réception des notifications de résultat)
builder.AddRedisClient("cache");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Client HTTP nommé "apiservice" → Aspire injecte automatiquement l'URL via la découverte de services
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

app.MapDefaultEndpoints();

app.Run();
