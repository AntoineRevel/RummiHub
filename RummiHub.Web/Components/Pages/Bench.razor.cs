using RummiHub.SolverBench;
using RummiHub.SolverBench.Bank;
using RummiHub.SolverBench.Comparison;
using RummiHub.SolverBench.Measure;
using RummiHub.SolverBench.Model;
using RummiHub.SolverBench.Solvers;
using RummiHub.SolverBench.Trace;

namespace RummiHub.Web.Components.Pages;

/// <summary>
/// Drives the in-memory <c>RummiHub.SolverBench</c> engine from the browser: pick solvers,
/// pick a corpus (a seeded trace or the file-backed bank), run the comparison, and present
/// sortable per-row and aggregate results. Runs entirely in-process — no Redis/SignalR/Aspire.
/// </summary>
public partial class Bench
{
    private enum SourceKind { Trace, Bank }

    private enum SortColumn { Solver, Found, Tiles, Score, Median, P95, Alloc, Cpu, CpuRatio, Optimum }

    // ── inputs ──────────────────────────────────────────────────────────────────────────
    private SourceKind _source = SourceKind.Trace;
    private int _seed = 12345;
    private int _games = 1;
    private int _maxPositions = 20;
    private int _players = 2;
    private int _reps = 3;
    private int _warmup = 1;
    private int _timeoutMs = 1500;

    private readonly Dictionary<string, bool> _firstSelected = new();
    private readonly Dictionary<string, bool> _duringSelected = new();

    // ── state ───────────────────────────────────────────────────────────────────────────
    private MoveBank _bank = null!;
    private BenchFacade _facade = null!;
    private IReadOnlyList<BenchPosition> _lastCorpus = [];
    private ComparisonResult? _result;
    private IReadOnlyList<BenchPosition> _bankEntries = [];
    private bool _running;
    private string? _error;
    private string? _info;

    private SortColumn _sortColumn = SortColumn.Median;
    private bool _sortAscending = true;

    // manual add
    private string _manualHand = "";
    private string _manualName = "";
    private string _manualTags = "";

    protected override void OnInitialized()
    {
        _bank = MoveBank.Default();
        _facade = new BenchFacade(_bank);
        foreach (var s in SolverRegistry.FirstPlay) _firstSelected[s.Name] = true;
        foreach (var s in SolverRegistry.DuringGame) _duringSelected[s.Name] = true;
        ReloadBank();
    }

    private MeasureOptions Options => new()
    {
        Repetitions = Math.Max(1, _reps),
        WarmupRuns = Math.Max(0, _warmup),
        Timeout = TimeSpan.FromMilliseconds(Math.Max(1, _timeoutMs))
    };

    private List<SolverDescriptor> SelectedSolvers()
    {
        var selected = new List<SolverDescriptor>();
        selected.AddRange(SolverRegistry.FirstPlay.Where(s => _firstSelected.GetValueOrDefault(s.Name)));
        selected.AddRange(SolverRegistry.DuringGame.Where(s => _duringSelected.GetValueOrDefault(s.Name)));
        return selected;
    }

    private async Task RunComparison()
    {
        _error = null;
        _info = null;
        var solvers = SelectedSolvers();
        if (solvers.Count == 0)
        {
            _error = "Select at least one solver.";
            return;
        }

        _running = true;
        StateHasChanged();

        try
        {
            var options = Options;
            var (corpus, source) = BuildCorpus(solvers);
            if (corpus.Count == 0)
            {
                _error = "The corpus is empty for the current selection.";
                return;
            }

            _lastCorpus = corpus;
            // Heavy CPU work off the render thread so the circuit stays responsive.
            _result = await Task.Run(() => BenchFacade.Run(corpus, solvers, options, source));
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _running = false;
        }
    }

    private (IReadOnlyList<BenchPosition> Corpus, string Source) BuildCorpus(List<SolverDescriptor> solvers)
    {
        var wantFirst = solvers.Any(s => s.Phase == BenchPhase.FirstPlay);
        var wantDuring = solvers.Any(s => s.Phase == BenchPhase.DuringGame);

        IReadOnlyList<BenchPosition> corpus = _source == SourceKind.Bank
            ? _facade.BankCorpus()
            : BenchFacade.TraceCorpus(_seed, new GameTraceOptions
            {
                PlayerCount = Math.Max(2, _players),
                Games = Math.Max(1, _games),
                MaxPositions = Math.Max(1, _maxPositions)
            });

        corpus = corpus.Where(p =>
            (p.Phase == BenchPhase.FirstPlay && wantFirst) ||
            (p.Phase == BenchPhase.DuringGame && wantDuring)).ToList();

        var source = _source == SourceKind.Bank
            ? $"bank ({corpus.Count} positions)"
            : $"trace seed={_seed}, games={_games} ({corpus.Count} positions)";
        return (corpus, source);
    }

    // ── sorting ───────────────────────────────────────────────────────────────────────
    private void SortBy(SortColumn column)
    {
        if (_sortColumn == column) _sortAscending = !_sortAscending;
        else { _sortColumn = column; _sortAscending = true; }
    }

    private IEnumerable<SolveMeasurement> SortedRows()
    {
        if (_result is null) return [];

        Func<SolveMeasurement, IComparable> key = _sortColumn switch
        {
            SortColumn.Solver => r => r.SolverName,
            SortColumn.Found => r => r.Found,
            SortColumn.Tiles => r => r.RackTilesPlayed,
            SortColumn.Score => r => r.Score,
            SortColumn.P95 => r => r.TimeMsP95,
            SortColumn.Alloc => r => r.AllocBytesPerSolve,
            SortColumn.Cpu => r => r.CpuMsTotal,
            SortColumn.CpuRatio => r => r.CpuRatio,
            SortColumn.Optimum => r => r.MatchesOptimum,
            _ => r => r.TimeMsMedian
        };

        var ordered = _sortAscending ? _result.Rows.OrderBy(key) : _result.Rows.OrderByDescending(key);
        // Stable secondary ordering keeps the table readable.
        return ordered.ThenBy(r => r.PositionId).ThenBy(r => r.SolverName);
    }

    private static string RowClass(SolveMeasurement r)
    {
        if (r.TimedOut) return "row-timeout";
        if (!r.Found) return "row-nomove";
        if (!r.Legal) return "row-illegal";
        if (!r.MatchesOptimum) return "row-diverge";
        if (r.OverScored) return "row-diverge";
        return "";
    }

    // ── bank actions ────────────────────────────────────────────────────────────────────
    private void ReloadBank() => _bankEntries = _bank.Load();

    private void AddManualPosition()
    {
        _error = null;
        _info = null;
        try
        {
            var tags = _manualTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var position = PositionParser.FirstPlayFromHand(_manualHand, _manualName, tags);
            _info = _bank.TryAdd(position)
                ? $"Added '{position.Name}' ({position.Id})."
                : $"Position {position.Id} is already in the bank.";
            _manualHand = _manualName = _manualTags = "";
            ReloadBank();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private void AddFromCorpus(string positionId)
    {
        var position = _lastCorpus.FirstOrDefault(p => p.Id == positionId);
        if (position is null) return;

        var curated = position with
        {
            Provenance = string.IsNullOrEmpty(position.Provenance) ? "manual" : position.Provenance + ";manual",
            Notes = "manually curated from a comparison run"
        };
        _info = _bank.TryAdd(curated) ? $"Added {curated.Id} to the bank." : $"{curated.Id} already in bank.";
        ReloadBank();
    }

    private void AutoBank()
    {
        if (_result is null) return;
        var added = _facade.AutoBankInteresting(_lastCorpus, _result);
        _info = $"Auto-banked {added} interesting position(s).";
        ReloadBank();
    }

    private void DeleteBankEntry(string id)
    {
        _bank.Delete(id);
        ReloadBank();
    }

    private async Task ReplayBankEntry(string id)
    {
        var entry = _bankEntries.FirstOrDefault(p => p.Id == id);
        if (entry is null) return;

        var solvers = SelectedSolvers().Where(s => s.Phase == entry.Phase).ToList();
        if (solvers.Count == 0)
        {
            _error = $"Select a {entry.Phase} solver to replay this entry.";
            return;
        }

        _running = true;
        _error = null;
        StateHasChanged();
        try
        {
            _lastCorpus = [entry];
            _result = await Task.Run(() => BenchFacade.Run([entry], solvers, Options, $"bank entry {id}"));
        }
        finally
        {
            _running = false;
        }
    }

    // ── display helpers ─────────────────────────────────────────────────────────────────
    private static string Ms(double v) => v.ToString("0.###");
    private static string Ratio(double v) => v.ToString("0.##");

    private static string Bytes(double v) => v switch
    {
        >= 1024 * 1024 => (v / (1024 * 1024)).ToString("0.##") + " MB",
        >= 1024 => (v / 1024).ToString("0.#") + " KB",
        _ => v.ToString("0") + " B"
    };
}
