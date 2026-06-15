using RummiHub.SolverBench;
using RummiHub.SolverBench.Bank;
using RummiHub.SolverBench.Comparison;
using RummiHub.SolverBench.Measure;
using RummiHub.SolverBench.Model;
using RummiHub.SolverBench.Report;
using RummiHub.SolverBench.Solvers;
using RummiHub.SolverBench.Trace;

// Headless, scriptable, reproducible solver-performance comparison for CI / objectivity.
// No external dependencies; everything runs in-memory. Verdicts match the /bench UI.

var cli = new ArgMap(args);

if (cli.Has("--help") || cli.Has("-h"))
{
    PrintHelp();
    return 0;
}

if (cli.Has("--list-solvers"))
{
    Console.WriteLine("First-play solvers:");
    foreach (var s in SolverRegistry.FirstPlay) Console.WriteLine($"  {s.Name}{(s.IsBaseline ? " (baseline)" : "")}");
    Console.WriteLine("During-game solvers:");
    foreach (var s in SolverRegistry.DuringGame) Console.WriteLine($"  {s.Name}{(s.IsBaseline ? " (baseline)" : "")}");
    return 0;
}

var seed = cli.Int("--seed", 12345);

// Deterministic bank seeding: capture structurally-interesting positions and exit.
if (cli.Has("--seed-bank"))
{
    var bankPath = cli.Str("--bank", BankPaths.Default());
    var traceOpts = new GameTraceOptions
    {
        PlayerCount = cli.Int("--players", 2),
        Games = cli.Int("--games", 1),
        MaxTurnsPerGame = cli.Int("--max-turns", 80),
        MaxPositions = cli.Int("--max-positions", int.MaxValue)
    };
    var positions = BenchFacade.TraceCorpus(seed, traceOpts);
    var added = new BenchFacade(new MoveBank(bankPath)).BankStructural(positions, cli.Int("--seed-bank", int.MaxValue));
    Console.WriteLine($"Seeded {added} structurally-interesting position(s) into {bankPath}");
    return 0;
}

var phase = cli.Str("--phase", "both").ToLowerInvariant();
var sourceKind = cli.Str("--source", "trace").ToLowerInvariant();
var bankDir = cli.Str("--bank", BankPaths.Default());

var options = new MeasureOptions
{
    Repetitions = cli.Int("--reps", 5),
    WarmupRuns = cli.Int("--warmup", 1),
    Timeout = TimeSpan.FromMilliseconds(cli.Int("--timeout-ms", 2000))
};

var wantFirst = phase is "first" or "both";
var wantDuring = phase is "during" or "both";

// ── solver selection ────────────────────────────────────────────────────────────────
var selected = new List<SolverDescriptor>();
var names = cli.Str("--solvers", "");
if (wantFirst) selected.AddRange(Pick(SolverRegistry.FirstPlay, names));
if (wantDuring) selected.AddRange(Pick(SolverRegistry.DuringGame, names));

if (selected.Count == 0)
{
    Console.Error.WriteLine("No solvers selected. Use --list-solvers to see available names.");
    return 1;
}

// ── corpus ──────────────────────────────────────────────────────────────────────────
var facade = new BenchFacade(new MoveBank(bankDir));
IReadOnlyList<BenchPosition> corpus;
string source;

if (sourceKind == "bank")
{
    corpus = facade.BankCorpus();
    source = $"bank '{bankDir}'";
}
else
{
    var traceOptions = new GameTraceOptions
    {
        PlayerCount = cli.Int("--players", 2),
        Games = cli.Int("--games", 1),
        MaxTurnsPerGame = cli.Int("--max-turns", 80),
        MaxPositions = cli.Int("--max-positions", int.MaxValue)
    };
    corpus = BenchFacade.TraceCorpus(seed, traceOptions);
    source = $"trace seed={seed}, games={traceOptions.Games}";
}

corpus = corpus.Where(p =>
    (p.Phase == BenchPhase.FirstPlay && wantFirst) ||
    (p.Phase == BenchPhase.DuringGame && wantDuring)).ToList();

if (corpus.Count == 0)
{
    Console.Error.WriteLine("Corpus is empty for the selected phase/source.");
    return 1;
}

// ── run ─────────────────────────────────────────────────────────────────────────────
Console.WriteLine($"Comparing {selected.Select(s => s.Name).Distinct().Count()} solver(s) " +
                  $"over {corpus.Count} position(s) from {source}.");
Console.WriteLine($"Methodology: sequential, warmup={options.WarmupRuns}, reps={options.Repetitions}, " +
                  $"timeout={options.Timeout.TotalMilliseconds:0}ms. Indicative (not BenchmarkDotNet).");
Console.WriteLine();

var result = BenchFacade.Run(corpus, selected, options, source);

Console.WriteLine(ReportFormatter.SummaryMarkdown(result));

if (result.IllegalCount > 0 || result.DivergenceCount > 0)
    Console.WriteLine($"⚠ {result.IllegalCount} illegal and {result.DivergenceCount} divergent row(s) — " +
                      "inspect with --md for detail.");

// ── exports ─────────────────────────────────────────────────────────────────────────
if (cli.Str("--csv", "") is { Length: > 0 } csvPath)
{
    File.WriteAllText(csvPath, ReportFormatter.DetailCsv(result));
    Console.WriteLine($"CSV written to {csvPath}");
}

if (cli.Str("--md", "") is { Length: > 0 } mdPath)
{
    File.WriteAllText(mdPath, ReportFormatter.FullMarkdown(result));
    Console.WriteLine($"Markdown written to {mdPath}");
}

if (cli.Has("--auto-bank"))
{
    var added = facade.AutoBankInteresting(corpus, result);
    Console.WriteLine($"Auto-banked {added} interesting position(s) into {bankDir}");
}

// Non-zero exit if any solver produced an illegal move — useful as a CI gate.
return result.IllegalCount > 0 ? 2 : 0;

static IEnumerable<SolverDescriptor> Pick(IReadOnlyList<SolverDescriptor> all, string csvNames)
{
    if (string.IsNullOrWhiteSpace(csvNames)) return all;
    var wanted = csvNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return all.Where(s => wanted.Contains(s.Name));
}

static void PrintHelp()
{
    Console.WriteLine("""
        RummiHub solver-performance comparator (headless runner)

        Usage: RummiHub.SolverBench.Runner [options]

          --source trace|bank   Corpus source (default: trace)
          --seed <int>          Trace seed (default: 12345)
          --games <int>         Games to chain into the trace (default: 1)
          --players <int>       Players per game (default: 2)
          --max-turns <int>     Turn cap per game (default: 80)
          --max-positions <int> Cap on captured positions
          --bank <dir>          Bank directory (default: <repo>/solver-bench/bank)
          --phase first|during|both   (default: both)
          --solvers a,b,c       Subset by name (default: all in phase; see --list-solvers)
          --reps <int>          Measured repetitions (default: 5)
          --warmup <int>        Warmup runs excluded from timing (default: 1)
          --timeout-ms <int>    Per-solve timeout (default: 2000)
          --csv <path>          Export detail rows as CSV
          --md <path>           Export summary + detail as Markdown
          --auto-bank           Persist interesting positions into the bank
          --list-solvers        Print available solvers and exit

        Exit codes: 0 ok · 1 usage · 2 an illegal move was produced.
        """);
}

/// <summary>Minimal flag/value parser (no external dependencies).</summary>
file sealed class ArgMap
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public ArgMap(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--") && !args[i].StartsWith('-')) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                _values[args[i]] = args[i + 1];
                i++;
            }
            else
            {
                _flags.Add(args[i]);
            }
        }
    }

    public bool Has(string key) => _flags.Contains(key) || _values.ContainsKey(key);
    public string Str(string key, string fallback) => _values.TryGetValue(key, out var v) ? v : fallback;
    public int Int(string key, int fallback) => _values.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;
}
