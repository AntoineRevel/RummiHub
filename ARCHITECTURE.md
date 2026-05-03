# Architecture RummiHub

Application orchestrée par **.NET Aspire** (`RummiHub.AppHost/AppHost.cs`).

## Vue d'ensemble

```mermaid
flowchart LR
    User([Joueur])

    subgraph Aspire["RummiHub.AppHost (Aspire)"]
        Web["RummiHub.Web<br/>Blazor Server + SignalR<br/>(GameHub)"]
        Api["RummiHub.ApiService<br/>Minimal API<br/>POST /solve · GET /solve/{hash}"]
        Worker["RummiHub.Solver.Worker<br/>BackgroundService<br/>(2 replicas)"]
        Redis[("Redis Stack<br/>JSON · Streams · Pub/Sub")]
    end

    User <-->|HTTPS / WebSocket SignalR| Web
    Web -->|HTTP: POST /solve| Api
    Api -->|XADD solve-stream<br/>SET solve-lock:{hash}| Redis
    Api -->|PUBLISH solve:enqueue| Redis
    Redis -->|XREADGROUP solvers| Worker
    Worker -->|JSON.SET solve-result:{hash}| Redis
    Worker -->|PUBLISH solve:done:{hash}| Redis
    Redis -->|SUBSCRIBE solve:done:{hash}| Web
    Web -->|SignalR push| User
```

## Composants

| Projet | Rôle |
|---|---|
| `RummiHub.AppHost` | Orchestrateur Aspire : déclare Redis, l'API, le worker (×2) et le front. |
| `RummiHub.Web` | Blazor Server, hub SignalR `GameHub`, `GameManager`, `SolveResultSubscriber`. |
| `RummiHub.ApiService` | Reçoit les demandes de résolution, déduplique par hash de contenu, enfile dans Redis. |
| `RummiHub.Solver.Worker` | Consomme le stream Redis, exécute la stratégie (`GraphStrategy`), publie le résultat. |
| `RummiHub.Core` | Modèles partagés (`Tile`, `Set`, `Solution`), contrats (`SolveRequest/Response`, `ContentHasher`). |
| `RummiHub.ServiceDefaults` | OpenTelemetry, health checks, service discovery. |
| Redis Stack | Cache (`solve-result:{hash}`), verrou (`solve-lock:{hash}`), stream (`solve-stream`), pub/sub. |

## Flux d'une résolution

1. Le joueur déclenche un solve depuis le front Blazor.
2. `Web` appelle `POST /solve` sur `ApiService`.
3. `ApiService` calcule le `contentHash`, vérifie le cache Redis JSON ; sinon pose un verrou `SET NX` puis `XADD` dans `solve-stream` et `PUBLISH solve:enqueue`.
4. Un `SolverWorker` lit le stream via le consumer group `solvers`, résout, écrit `solve-result:{hash}` (TTL 1 h), `PUBLISH solve:done:{hash}`, `XACK` puis libère le verrou.
5. `SolveResultSubscriber` (côté Web) reçoit la notification et pousse le résultat aux clients via SignalR.
