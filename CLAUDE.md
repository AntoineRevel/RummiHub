# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Running the Application

This project uses **.NET Aspire** as its orchestrator. Always run via Aspire — it manages Redis, all services, and their dependencies automatically.

```bash
aspire run          # start the full application
```

If prompted about an existing instance, stop it. Restart is only required when `RummiHub.AppHost/AppHost.cs` changes.

**Cloud sessions (claude.ai/code)**: `.claude/hooks/session-start.sh` provisions the container (the cloud counterpart of `.devcontainer/`): .NET 10 SDK, Aspire CLI, NuGet restore, dev cert, Docker daemon, Python Playwright. Docker pulls `docker.io` images through the `mirror.gcr.io` mirror because anonymous Docker Hub pulls are rate-limited there. Use `aspire run --non-interactive`, in the background.

## Building and Testing

```bash
dotnet build                                                        # build solution
dotnet test                                                         # run all tests
dotnet test --filter "FullyQualifiedName~BaseSolverTests"          # run a single test class
dotnet test --filter "FullyQualifiedName~GetRuns_WithJoker"        # run a single test
```

Tests use **xUnit** with **FluentAssertions**. There are no integration tests — all tests in `RummiHub.Tests` are unit tests covering `Core` models, the solver primitives, and `GameEngine` logic.

## Shipping a feature

When a feature is ready, finish with both of these. Do this for every new feature, without waiting to be asked again.

1. Branch from `origin/master` (`cursor/<topic>`), commit, push, and open a pull request to `master`. Do not push to `master` directly.
2. Deploy that same branch to the test site:

```bash
gh workflow run deploy-test.yml --ref <branch>
```

That runs `.github/workflows/deploy-test.yml` on the real branch: the tests, then https://test.rummisolve.com at that commit. There is no `test` branch. The commit must not change `deploy/` — the server rejects the deploy otherwise. Do not push to `master`.

## Architecture

### Service Topology

```
RummiHub.AppHost (Aspire orchestrator)
├── RummiHub.ApiService        — Minimal API (HTTP)
├── RummiHub.Solver.Worker     — Background workers (x2 replicas)
├── RummiHub.Web               — Blazor Server + SignalR hub
└── Redis 8 (RedisJSON)        — Message bus + result cache
```

`RummiHub.Core` is a shared class library referenced by all services. `RummiHub.ServiceDefaults` provides common Aspire telemetry/health wiring via `builder.AddServiceDefaults()`.

### Solve Pipeline (the critical path)

The solver is fully asynchronous and distributed across services:

1. **Web → API** (`GameEngine.SubmitAndWaitForResult`): POST `/solve` with `SolveRequest` (hand tiles, board, `hasPlayed` flag, `Priority` flag).
2. **API** (`Program.cs /solve`): Computes a 16-char `contentHash` (SHA-256 of canonical JSON — `Priority` is **not** part of the hash), checks Redis JSON cache (`solve-result:{hash}`), acquires a dedup lock (`solve-lock:{hash}` SET NX), enqueues into `solve-stream` (or `solve-stream-priority` when `Priority` is set) with the request's W3C `traceparent`, then publishes to `solve:enqueue` Pub/Sub to wake workers. Returns **202** with the `contentHash`.
3. **Worker** (`SolverWorker`): Consumer group `"solvers"` reads one entry at a time, **draining `solve-stream-priority` before `solve-stream`** so move validations jump ahead of slow full-rack hint solves. Recovers pending (unACKed) messages from both streams on startup. Runs `DynamicStrategy.GetSolverResult`, writes result as Redis JSON (`JSON.SET`) to `solve-result:{hash}` with 1h TTL, publishes to `solve:done:{hash}`, ACKs the stream entry, and removes the dedup lock. Each entry runs in a `process <stream>` span parented on that `traceparent`, so a solve is one trace from the hub call to the worker. `RedisPollingFilter` keeps the idle polling (`XREADGROUP`, `XAUTOCLAIM`) out of the export.
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
- **`DynamicStrategy`** (active default): runs the polynomial-time `DynamicSolver` and **trusts its "no move" verdict** (a first play short of 30 included — returned as-is with its best score, never re-searched). A move it finds is **validated** with `SolutionValidator`; only a validation failure or an unexpected engine exception **falls back to `GraphStrategy`**, whose exponential search can take tens of seconds on a crowded table. This is the fast, exact path.
- **`DynamicSolver`** — exact solver based on the Den Hertog & Hulshof (2006) dynamic-programming formulation. Sweeps values 1→13 once, carrying a bounded per-color run state (open runs of length 1/2/≥3) plus a joker budget; groups are resolved locally per value. Because only two copies of each tile exist, the state space is bounded by a constant, making the search **polynomial** instead of exponential. `Create(board, rack)` (during game, forces all board tiles, maximizes rack tiles then score) and `CreateFirst(rack)` (first play, maximizes score, gated at 30). Measured ~300× faster than `GraphSolver` on heavy tables while returning the identical optimum.
- **`GraphStrategy`** (fallback / reference): uses `GraphSolver` when `hasPlayed` is true, `GraphFirstSolver` for first play. `GraphSolver` does a parallel BFS using `RummiNode` / `ConcurrentBag`, expanding the tree level by level, then picks the leaf node with the highest `(PlayerTilePlayed, Score)`. Note: its `GetRuns` can let a joker represent a value above 13 (an over-scoring bug `DynamicSolver` does not have).
- **`ParallelStrategy`** (alternative): races `IncrementalComplexSolver` vs `ParallelCombinationsSolver`, cancels the slower one.
- **`BaseSolver`** — abstract base with `GetRuns()` and `GetGroups()` generators and tile marking/unmarking logic. Tiles must be **sorted** before being passed in. The solver input tile array merges board tiles and player tiles, tracked by an `isPlayerTile[]` boolean array.
- **`BoardStabilizer`** — post-processes every during-game solve in `SolverWorker.Solve` (never first plays, which merge alongside the board). Solvers rebuild the board from scratch; the stabilizer searches for the **smallest set of existing board sets to break** such that (broken sets + played tiles) recomposes exactly (tested via micro `DynamicSolver` sub-solves), and returns every other set untouched. Same tiles played — only the arrangement is stabilized; falls back to the raw solver arrangement when no local rearrangement exists or budgets (attempts/time) run out. Requires the structured board from `SolveRequest.Board` (which is also part of the content hash, so caching stays correct).

### Web / Game Layer (RummiHub.Web)

- **`GameHub`** (SignalR) — the single entry point for all game actions. All event names are string constants in `HubEvents`.
- **`Game.ActionLock`** (`SemaphoreSlim(1,1)`) — serializes all mutating hub calls per game instance. Always `await game.ActionLock.WaitAsync()` before mutating game state.
- **`GameManager`** — singleton `ConcurrentDictionary<string, Game>` holding all active games. Players disconnecting from started games are **not removed** (reconnection is supported via `UpdatePlayerConnection`).
- **`GameEngine`** — per-game engine (server-side only, lives inside `Game.Engine`). Its `GameId` is the `Game.Id` and names its solve requests (`game-<id>-t<turn>-p<seat>`, `validate-<id>-t<turn>-p<seat>`). Manages tile pool, player racks, board state, and turn order. Delegates solve calls to the API via `SolveForCurrentPlayer` (full rack, low priority) / `ValidateHumanPlay` (selected tiles, `Priority = true`).
- **Optimal play hint**: At the start of each turn, `StartOptimalSolveForCurrentPlayerIfNeeded` fires a background solve for the current player's full rack. Nothing is shown until that player clicks Hint, which selects those tiles into the play zone the same way a manual pick would. AI seats play that result automatically. `HubEvents.OptimalReady` carries the tiles. The actual move validation goes through the priority stream, so it never queues behind the slow hint solve.
- **`SolveResultSubscriber`** — singleton that manages Redis Pub/Sub subscriptions for pending solve results. Multiple waiters for the same hash are stored in a `List<TaskCompletionSource>`.
- **Tile animations** (`wwwroot/js/tile-animations.js`, tempo in `Rendering/BoardPacing.cs`) — a FLIP pass after every render, driven by `data-anim-*` attributes in `GamePage.razor`. A board change is told in three acts so a tile at rest never covers another: the placed tiles already on screen (in the play zone, or board tiles changing set) lift in place → ONE glide where the board makes room while every placed tile (those out of the mover's chip included) travels to hover above its own gap → they drop in one after the other. Rows are spaced so lifted and hovering tiles rest in the lane between rows, never over the row above. The hand (rack, play zone) animates at once, never queued behind the board; a drawn tile flies out of the pool chip. `BoardPacing.EstimateMs` mirrors the story (conservatively) so AI moves wait for it, then for `BoardPacing.ReadingMs` (a reading window that grows with the sets the move touched). After every move the table gets the rack tiles it played (`PlayedIndices`, gold border) and the sets it changed (`ModifiedSetIndices`, gold wash); a state that leaves the board as is (a draw) keeps them lit. `animate()` returns how long the board keeps moving: for someone else's move, `GamePage` shows the new board (and tile counts) at once but holds the turn display — current player, banner, my rack — until that move has played out (`_turnAwaitingBoard`). Hub events apply on the renderer's sync context, in arrival order.
- **Blazor pages**: `GameSetup` (create/configure game) → `Lobby` (wait for players, host starts) → `GamePage` (active game). `GamePage` uses a code-behind (`GamePage.razor.cs`) and connects to `GameHub` via its own `HubConnection`.

### JSON Serialization

`RummiJsonOptions.Default` is the **single shared `JsonSerializerOptions`** used across API, Worker, and Web. It sets camelCase naming and `JsonStringEnumConverter` for enums, and the `TileJsonConverter` is registered on the `Tile` type itself via `[JsonConverter]`. Always use `RummiJsonOptions.Default` when manually serializing/deserializing domain objects.

### Adding a New Solver Strategy

1. Implement `IStrategy` in `RummiHub.Solver.Worker/Strategies/`.
2. Optionally implement `ISolver` in `RummiHub.Solver.Worker/Solver/` with a static `Create(Set board, Set rack)` factory.
3. Wire it in `SolverWorker.Solve()` by replacing `new DynamicStrategy()`.

### Solver Performance Bench (RummiHub.SolverBench)

A self-contained, in-memory benchmark for comparing solver **performance** (no Redis/SignalR/Aspire, no new NuGet). The exact solvers return the same optimal move, so the criterion is time/memory/CPU, not move quality.

- **`GameTraceGenerator`** — a light, standalone game loop (Core models + the 30-rule) piloted by `DynamicSolver`. It does **not** use `RummiHub.Web/GameEngine`. Seeded ⇒ reproducible. Captures each real position (board + hand + `hasPlayed`), tagged by phase.
- **`SolverRegistry`** — the à-la-carte solver catalogue by phase (mirrors `ConcreteSolverTests`). Each solver gets a **copy** of the sets.
- **`SolveMeasurer`** — honest sequential measurement: clean GC + warmup, median/p95 time, `GC.GetTotalAllocatedBytes(precise)` allocations, GC counts, `Process.TotalProcessorTime` + CPU/wall ratio, per-solve `CancellationTokenSource` timeout. Measurements are **indicative, not BenchmarkDotNet**.
- **`MoveEvaluator`** — normalizes joker values/score so `SolutionValidator` judges the tiles fairly; also flags over-scoring (the `GraphSolver` "joker > 13" bug).
- **`BenchComparer`** — runs the corpus × solvers, checks legality independently and divergence vs the DynamicSolver baseline (during-game: rack-tiles-played; first-play: existence).
- **`MoveBank`** (`solver-bench/bank/`) — one JSON per position via `RummiJsonOptions.Default`; reload as a non-regression corpus; enrich manually or automatically (interest heuristics).
- **UI**: Blazor page `/bench` (`RummiHub.Web`). **Headless**: `RummiHub.SolverBench.Runner` (CSV/Markdown export, same verdicts). See `solver-bench/README.md`.

### Debugging with Aspire

Use Aspire's built-in diagnostic tools before making changes:
- Structured logs, console logs, and traces are captured for all resources.
- RedisInsight is available on port 41245 for inspecting Redis state directly.
- Use the Aspire MCP tools (`list resources`, `list structured logs`, `list traces`) to inspect live state.
- Production runs the same dashboard as the `aspire-dashboard` Compose service. On the VPS, read its JSON API at `http://127.0.0.1:18888/api/telemetry/` with `DASHBOARD_API_KEY` from `deploy/.env`. See `deploy/README.md` « Traces et métriques ».
