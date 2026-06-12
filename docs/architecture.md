# Architecture RummiHub

RummiHub est orchestré par **.NET Aspire** (`RummiHub.AppHost`), qui démarre et relie quatre ressources : le frontend Blazor Server, l'API minimale, les workers de résolution (×2 réplicas) et Redis Stack. `RummiHub.Core` est la bibliothèque partagée (modèle de domaine et contrats) référencée par les trois services.

## Topologie des services

```mermaid
flowchart TB
    subgraph AppHost["RummiHub.AppHost — Orchestrateur .NET Aspire"]
        direction TB

        subgraph Web["RummiHub.Web (Blazor Server)"]
            Pages["Pages Blazor<br/>GameSetup → Lobby → GamePage"]
            Hub["GameHub (SignalR)"]
            GM["GameManager<br/>(ConcurrentDictionary&lt;string, Game&gt;)"]
            Engine["GameEngine<br/>(pioche, racks, plateau, tours)"]
            Subscriber["SolveResultSubscriber<br/>(Pub/Sub → TaskCompletionSource)"]
        end

        subgraph Api["RummiHub.ApiService (Minimal API)"]
            Solve["POST /solve<br/>hash SHA-256 → cache → lock → stream"]
        end

        subgraph Workers["RummiHub.Solver.Worker (×2 réplicas)"]
            SW["SolverWorker<br/>consumer group « solvers »"]
            Strategy["GraphStrategy<br/>GraphSolver / GraphFirstSolver"]
        end

        subgraph Redis["Redis Stack"]
            Stream[("solve-stream<br/>(Stream)")]
            Cache[("solve-result:{hash}<br/>(JSON, TTL 1h)")]
            Lock[("solve-lock:{hash}<br/>(SET NX)")]
            PubSub[("Pub/Sub<br/>solve:enqueue · solve:done:{hash}")]
        end
    end

    Core["RummiHub.Core<br/>Tile · Set · ValidSet · Solution<br/>SolveRequest / SolveResponse · RummiJsonOptions"]

    Browser["Navigateur<br/>(joueurs)"]

    Browser <-->|"SignalR (HubEvents)"| Hub
    Browser --> Pages
    Hub --> GM
    GM --> Engine
    Engine -->|"POST /solve (HTTP)"| Solve
    Engine --> Subscriber

    Solve -->|"1 — vérifie le cache"| Cache
    Solve -->|"2 — dédup"| Lock
    Solve -->|"3 — enqueue"| Stream
    Solve -->|"4 — réveille les workers"| PubSub

    PubSub -.->|"solve:enqueue"| SW
    Stream -->|"XREADGROUP"| SW
    SW --> Strategy
    SW -->|"écrit le résultat"| Cache
    SW -->|"publie solve:done:{hash}"| PubSub
    PubSub -.->|"solve:done:{hash}"| Subscriber

    Web -.->|référence| Core
    Api -.->|référence| Core
    Workers -.->|référence| Core

    style Redis fill:#fde8e8,stroke:#c0392b
    style Core fill:#eee,stroke:#888,stroke-dasharray: 5 5
    style Browser fill:#e8f4fd,stroke:#2980b9
```

## Pipeline de résolution (chemin critique)

Le solveur est entièrement asynchrone et distribué. L'API répond **202** avec le `contentHash` ; le Web s'abonne au canal `solve:done:{hash}` **avant** de revérifier le cache (« subscribe-then-check ») pour éliminer toute race condition avec les workers.

```mermaid
sequenceDiagram
    autonumber
    participant W as RummiHub.Web<br/>(GameEngine)
    participant A as ApiService<br/>(POST /solve)
    participant R as Redis Stack
    participant K as Solver.Worker<br/>(×2)

    W->>W: SubmitAndWaitForResult(SolveRequest)
    W->>A: POST /solve {hand, board, hasPlayed}
    A->>A: contentHash = SHA-256(JSON canonique)[0..16]
    A->>R: GET solve-result:{hash} (cache JSON)
    alt résultat en cache
        R-->>A: SolveResponse
        A-->>W: 200 + résultat
    else cache vide
        A->>R: SET solve-lock:{hash} NX (dédup)
        A->>R: XADD solve-stream
        A->>R: PUBLISH solve:enqueue
        A-->>W: 202 + contentHash
        W->>R: SUBSCRIBE solve:done:{hash}
        Note over W,R: « subscribe-then-check » :<br/>s'abonne AVANT de revérifier le cache<br/>(élimine la race condition)
        R-->>K: réveil (solve:enqueue)
        K->>R: XREADGROUP solve-stream (groupe « solvers »)
        K->>K: GraphStrategy.GetSolverResult()<br/>BFS parallèle (RummiNode)
        K->>R: JSON.SET solve-result:{hash} (TTL 1h)
        K->>R: PUBLISH solve:done:{hash}
        K->>R: XACK + DEL solve-lock:{hash}
        R-->>W: notification solve:done:{hash}
        W->>R: GET solve-result:{hash}
        W->>W: TaskCompletionSource.SetResult(SolveResponse)
    end
```

## Points clés

- **Dédup des solves** : un même état de jeu (même `contentHash`) n'est calculé qu'une fois, quel que soit le nombre de demandeurs — verrou `solve-lock:{hash}` (SET NX) + cache `solve-result:{hash}`.
- **Résilience des workers** : consumer group Redis Streams ; au démarrage, un worker récupère les messages pending (non-ACKés) d'un worker tombé.
- **Concurrence côté jeu** : toutes les mutations d'une partie passent par `Game.ActionLock` (`SemaphoreSlim(1,1)`), via le `GameHub` SignalR.
- **Sérialisation** : `RummiJsonOptions.Default` est l'unique `JsonSerializerOptions` partagé entre API, workers et Web (camelCase, enums en string, `TileJsonConverter`).
