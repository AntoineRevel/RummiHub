# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Running the Application

This project uses **.NET Aspire** as its orchestrator. Always run via Aspire — it manages Redis, all services, and their dependencies automatically.

```bash
aspire run          # start the full application
```

If prompted about an existing instance, stop it. Restart is only required when `RummiHub.AppHost/AppHost.cs` changes.

## Building and Testing

```bash
dotnet build                                                        # build solution
dotnet test                                                         # run all tests
dotnet test --filter "FullyQualifiedName~BaseSolverTests"          # run a single test class
dotnet test --filter "FullyQualifiedName~GetRuns_WithJoker"        # run a single test
```

Tests use **xUnit** with **FluentAssertions**. There are no integration tests — all tests in `RummiHub.Tests` are unit tests covering `Core` models, the solver primitives, and `GameEngine` logic.

## Architecture

### Service Topology

```
RummiHub.AppHost (Aspire orchestrator)
├── RummiHub.ApiService        — Minimal API (HTTP)
├── RummiHub.Solver.Worker     — Background workers (x2 replicas)
├── RummiHub.Web               — Blazor Server + SignalR hub
└── Redis Stack                — Message bus + result cache
```

`RummiHub.Core` is a shared class library referenced by all services. `RummiHub.ServiceDefaults` provides common Aspire telemetry/health wiring via `builder.AddServiceDefaults()`.

### Solve Pipeline (the critical path)

The solver is fully asynchronous and distributed across services:

1. **Web → API** (`GameEngine.SubmitAndWaitForResult`): POST `/solve` with `SolveRequest` (hand tiles, board, `hasPlayed` flag, `Priority` flag).
2. **API** (`Program.cs /solve`): Computes a 16-char `contentHash` (SHA-256 of canonical JSON — `Priority` is **not** part of the hash), checks Redis JSON cache (`solve-result:{hash}`), acquires a dedup lock (`solve-lock:{hash}` SET NX), enqueues into `solve-stream` (or `solve-stream-priority` when `Priority` is set), then publishes to `solve:enqueue` Pub/Sub to wake workers. Returns **202** with the `contentHash`.
3. **Worker** (`SolverWorker`): Consumer group `"solvers"` reads one entry at a time, **draining `solve-stream-priority` before `solve-stream`** so move validations jump ahead of slow full-rack hint solves. Recovers pending (unACKed) messages from both streams on startup. Runs `GraphStrategy.GetSolverResult`, writes result as RedisJSON to `solve-result:{hash}` with 1h TTL, publishes to `solve:done:{hash}`, ACKs the stream entry, and removes the dedup lock.
4. **Web** (`SolveResultSubscriber`): Subscribes to `solve:done:{hash}` Pub/Sub *before* checking cache ("subscribe-then-check" pattern to eliminate race conditions). Resolves a `TaskCompletionSource<SolveResponse>` when the notification arrives.

### Domain Model (RummiHub.Core)

- **`Tile`** — bit-packed `byte`: bits 0–3 = value (1–13), bits 4–5 = `TileColor` enum (Blue/Red/Black/Mango), bit 6 = `IsJoker`. Serialized as `{value, color, isJoker}` JSON via `TileJsonConverter`.
- **`Set`** — mutable tile collection. **`Tiles` never contains jokers** — jokers are tracked separately via `Jokers: int`. Use `GetAllTilesIncludingJokers()` when you need the full list.
- **`ValidSet`** — an array of tiles that forms a valid run or group. **May contain joker tiles** in the array (unlike `Set`).
- **`Solution`** — the board state: `Runs: List<ValidSet>` + `Groups: List<ValidSet>`. `ToSet()` flattens it to a `Set` for solvers. `AddSolution()` merges another solution into this one.
- **`SolveRequest`** / **`SolveResponse`** — the contract between API and worker, and between Web and API.

### Solver Architecture (RummiHub.Solver.Worker)

- **`IStrategy`** wraps one or more `ISolver` implementations and returns `SolverResult`.
- **`ISolver.SearchSolution()`** is synchronous and returns a `SolverResult`. `ISolver.MinScore = 30` (first-play threshold).
- **`GraphStrategy`** (active default): uses `GraphSolver` when `hasPlayed` is true, `GraphFirstSolver` for first play. `GraphSolver` does a parallel BFS using `RummiNode` / `ConcurrentBag`, expanding the tree level by level, then picks the leaf node with the highest `(PlayerTilePlayed, Score)`.
- **`ParallelStrategy`** (alternative): races `IncrementalComplexSolver` vs `ParallelCombinationsSolver`, cancels the slower one.
- **`BaseSolver`** — abstract base with `GetRuns()` and `GetGroups()` generators and tile marking/unmarking logic. Tiles must be **sorted** before being passed in. The solver input tile array merges board tiles and player tiles, tracked by an `isPlayerTile[]` boolean array.

### Web / Game Layer (RummiHub.Web)

- **`GameHub`** (SignalR) — the single entry point for all game actions. All event names are string constants in `HubEvents`.
- **`Game.ActionLock`** (`SemaphoreSlim(1,1)`) — serializes all mutating hub calls per game instance. Always `await game.ActionLock.WaitAsync()` before mutating game state.
- **`GameManager`** — singleton `ConcurrentDictionary<string, Game>` holding all active games. Players disconnecting from started games are **not removed** (reconnection is supported via `UpdatePlayerConnection`).
- **`GameEngine`** — per-game engine (server-side only, lives inside `Game.Engine`). Manages tile pool, player racks, board state, and turn order. Delegates solve calls to the API via `SolveForCurrentPlayer` (full rack, low priority) / `ValidateHumanPlay` (selected tiles, `Priority = true`).
- **Optimal play hint**: At the start of each turn, `StartOptimalSolveForCurrentPlayerIfNeeded` fires a background solve for the current player's full rack. After the player acts (draw or play), the hub sends `HubEvents.OptimalPlay` privately to that player with what the solver would have played. The actual move validation goes through the priority stream, so it never queues behind the slow hint solve.
- **`SolveResultSubscriber`** — singleton that manages Redis Pub/Sub subscriptions for pending solve results. Multiple waiters for the same hash are stored in a `List<TaskCompletionSource>`.
- **Blazor pages**: `GameSetup` (create/configure game) → `Lobby` (wait for players, host starts) → `GamePage` (active game). `GamePage` uses a code-behind (`GamePage.razor.cs`) and connects to `GameHub` via its own `HubConnection`.

### JSON Serialization

`RummiJsonOptions.Default` is the **single shared `JsonSerializerOptions`** used across API, Worker, and Web. It sets camelCase naming and `JsonStringEnumConverter` for enums, and the `TileJsonConverter` is registered on the `Tile` type itself via `[JsonConverter]`. Always use `RummiJsonOptions.Default` when manually serializing/deserializing domain objects.

### Adding a New Solver Strategy

1. Implement `IStrategy` in `RummiHub.Solver.Worker/Strategies/`.
2. Optionally implement `ISolver` in `RummiHub.Solver.Worker/Solver/` with a static `Create(Set board, Set rack)` factory.
3. Wire it in `SolverWorker.Solve()` by replacing `new GraphStrategy()`.

### Debugging with Aspire

Use Aspire's built-in diagnostic tools before making changes:
- Structured logs, console logs, and traces are captured for all resources.
- RedisInsight is available on port 41245 for inspecting Redis state directly.
- Use the Aspire MCP tools (`list resources`, `list structured logs`, `list traces`) to inspect live state.
