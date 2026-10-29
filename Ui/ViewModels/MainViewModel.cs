using System.Diagnostics.CodeAnalysis;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Export;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Services;
using QsrPriceBenchmarks.Ui.Dialogs;

namespace QsrPriceBenchmarks.Ui.ViewModels;

[SuppressMessage("Reliability", "CA1001", Justification = "The one MainViewModel lives as long as the process; its CancellationTokenSources are released at exit.")]
public sealed class MainViewModel : ViewModelBase
{
    /// <summary>
    /// "vX.Y.Z" for the header badge, read from the assembly's informational
    /// version — which the build stamps from &lt;Version&gt; in
    /// Directory.Build.props, the single place the product version is bumped.
    /// Any "+buildmetadata" suffix the SDK appends is trimmed for display.
    /// </summary>
    public string VersionText { get; } = BuildVersionText();

    private static string BuildVersionText()
    {
        System.Reflection.Assembly assembly = typeof(MainViewModel).Assembly;
        string? version = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            version = assembly.GetName().Version?.ToString(3) ?? "?";
        }
        int metadataStart = version.IndexOf('+', StringComparison.Ordinal);
        if (metadataStart > 0)
        {
            version = version[..metadataStart];
        }
        return $"v{version}";
    }

    // ── State ─────────────────────────────────────────────────────────────────
    private string _dbPath = DefaultDbPath();
    private ChainViewModel? _selected;
    private string _deleteBeforeId = "";

    // Why the current database cannot be used as it is (an older version's file the user kept, another program's
    // file), or null. The readers — tab lists, map, runs list — check it instead of opening the file to be refused
    // again: after a "No" to the set-aside question each of them did, on every refresh (maintainer, 2026-10-29).
    private DatabaseProblem? _databaseProblem;

    // Export runs on its own cancellation source, independent of a pipeline run.
    private CancellationTokenSource? _exportCts;
    private int _exportDone;
    private int _exportTotal;
    private string _exportText = "";

    // Each chain's run state, by chain name (plan WP-C2). Runs of different chains go on at once; the shared
    // scheduler takes their requests to Tıkla Gelsin and Google in turn.
    private readonly Dictionary<string, ChainSessionViewModel> _sessions = new(StringComparer.Ordinal);
    private readonly RequestScheduler _scheduler = RequestScheduler.CreateDefault();

    // ── Observable collections ────────────────────────────────────────────────
    public ObservableCollection<ChainViewModel> Chains { get; } = [];
    public ObservableCollection<ScrapeRunViewModel> Runs { get; } = [];

    /// <summary>The Scrape Runs grid's filter switches and order, over <see cref="Runs"/>.</summary>
    public RunsFilterViewModel RunsFilter { get; }

    public ObservableCollection<MapCellViewModel> MapCells { get; } = [];

    // Total LOCATIONS for the selected chain, shown atop the Locations tab.
    private int _locationTotal;
    public int LocationTotal
    {
        get
        {
            return _locationTotal;
        }

        private set { Set(ref _locationTotal, value); }
    }

    /// <summary>The selected chain's run: what the Run Pipeline tab shows and acts on.</summary>
    public ChainSessionViewModel? SelectedSession
    {
        get
        {
            return _selected is null ? null : _sessions[_selected.Name];
        }
    }

    /// <summary>
    /// True while any chain's run is in progress — what the database path, Browse and "delete all before" wait for,
    /// and what the header's equalizer shows.
    /// </summary>
    public bool IsAnyRunning
    {
        get
        {
            return _sessions.Values.Any(s => s.IsRunning);
        }
    }

    /// <summary>
    /// A message about the app rather than one run (the database, the runs list), written to the log of
    /// the chain on screen — the log the user is looking at.
    /// </summary>
    private void Log(string line)
    {
        (SelectedSession ?? _sessions[Chains[0].Name]).LogLines.Add(line);
    }

    // ── Properties ────────────────────────────────────────────────────────────
    public string DbPath
    {
        get
        {
            return _dbPath;
        }

        set { Set(ref _dbPath, value); RecheckDatabase(); ReloadRunsCommand.Execute(null); _ = RebuildMapAsync(); foreach (ChainSessionViewModel s in _sessions.Values) { _ = RebuildScrapeTabsAsync(s); } }
    }

    /// <summary>
    /// Default database location: a per-user, always-writable app-data folder.
    /// Using an absolute path here (rather than a bare relative filename, which
    /// resolves against the process working directory — Program Files for an
    /// installed build) is what keeps a fresh install from failing with
    /// SQLITE_CANTOPEN when it tries to create the file.
    /// </summary>
    private static string DefaultDbPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QsrPriceBenchmarks");
        return Path.Combine(dir, "QsrPriceBenchmarks.sqlite");
    }

    /// <summary>Re-reads whether the current database can be used as it is (never throws, writes nothing).</summary>
    private void RecheckDatabase()
    {
        _databaseProblem = Database.ValidateFile(DbPath);
    }

    /// <summary>
    /// If the configured <see cref="DbPath"/> can't be opened (missing or
    /// unwritable directory, etc.), fall back to a brand-new database in the
    /// default writable location and switch <see cref="DbPath"/> to it, logging
    /// the change. Leaves <see cref="DbPath"/> untouched when it opens fine.
    /// An older version's database is never swapped out silently: the user is
    /// asked to set it aside, and false (nothing may run) when they decline.
    /// </summary>
    private bool EnsureDatabaseUsable()
    {
        try
        {
            using SqliteConnection probe = Database.Open(DbPath);
            return true;
        }
        catch (OutdatedDatabaseException outdated)
        {
            return OfferFreshDatabase(outdated.Path, outdated.FileVersion, outdated.Message);
        }
        catch (Exception ex)
        {
            string fallback = DefaultDbPath();
            Log($"  ⚠ Could not open database '{DbPath}' ({ex.Message}).");
            if (!string.Equals(fallback, DbPath, StringComparison.OrdinalIgnoreCase))
            {
                Log($"  → Creating a new database at {fallback}");
                DbPath = fallback; // writable; Database.Open creates the folder + file
            }
            return true;
        }
    }

    /// <summary>
    /// Every dialog this view model shows — questions and failures alike. Replaceable so UI tests can answer and
    /// read them without a modal window; the app always shows the themed message box.
    /// </summary>
    internal Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> ShowMessage { get; set; } =
        ThemedMessageBox.Show;

    /// <summary>
    /// The one way an older version's database is replaced: the user is asked, and on Yes the file is renamed
    /// aside (<see cref="Database.SetAside"/>, never deleted) so the next open starts a fresh database at the same
    /// path. Returns whether the path is now usable.
    /// </summary>
    private bool OfferFreshDatabase(string path, int fileVersion, string reason)
    {
        string question =
            $"{reason}\n\nStart a new, empty database here? The old file is kept beside it, renamed, and can " +
            "still be opened by the version that made it.";
        if (ShowMessage(question, "Older database", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            Log($"  ⚠ {reason}");
            Log("    Choose another database with Browse, or restart to be asked again.");
            return false;
        }

        try
        {
            string aside = Database.SetAside(path, fileVersion);
            if (string.Equals(Path.GetFullPath(DbPath), path, StringComparison.OrdinalIgnoreCase))
            {
                RecheckDatabase(); // the path now holds no file: usable again
            }
            Log($"  ✓ The older database was kept as '{aside}'; a new one starts at '{path}'.");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            // Typically the file is open in another window of the older version.
            Log($"  ✗ The older database could not be set aside: {error.Message}");
            return false;
        }
    }

    public ChainViewModel? SelectedChain
    {
        get
        {
            return _selected;
        }

        set { Set(ref _selected, value); Notify(nameof(SelectedSession)); _ = RebuildScrapeTabsAsync(SelectedSession); RelayCommand.RaiseRequery(); _ = RebuildMapAsync(); }
    }

    private bool _isExporting;
    /// <summary>True while an Excel export is being prepared on a background thread.</summary>
    public bool IsExporting
    {
        get
        {
            return _isExporting;
        }

        private set { Set(ref _isExporting, value); Notify(nameof(ExportProgressVisible)); RelayCommand.RaiseRequery(); }
    }

    // ── Excel export progress bar (Scrape Runs tab) ───────────────────────────
    public int ExportDone
    {
        get
        {
            return _exportDone;
        }

        private set
        {
            Set(ref _exportDone, value);
        }
    }
    public int ExportTotal
    {
        get
        {
            return _exportTotal;
        }

        private set
        {
            Set(ref _exportTotal, value);
        }
    }
    public string ExportText
    {
        get
        {
            return _exportText;
        }

        private set
        {
            Set(ref _exportText, value);
        }
    }

    /// <summary>The export bar/Cancel show for the whole duration of an export.</summary>
    public bool ExportProgressVisible
    {
        get
        {
            return IsExporting;
        }
    }

    public string DeleteBeforeId
    {
        get
        {
            return _deleteBeforeId;
        }

        set
        {
            Set(ref _deleteBeforeId, value);
        }
    }

    private ScrapeRunViewModel? _selectedRun;
    public ScrapeRunViewModel? SelectedRun
    {
        get
        {
            return _selectedRun;
        }

        set
        {
            Set(ref _selectedRun, value);
        }
    }

    /// <summary>
    /// Re-reads <paramref name="session"/>'s tab list from the database, keeping its ticks. The database is the
    /// ONLY source of tabs — nothing is hardcoded: Step 2 discovers them live from the platform's pages and
    /// disables the ones that vanish; an empty list just means no run has discovered this chain's tabs yet (the
    /// hint on the Run Pipeline tab says so).
    /// </summary>
    private async Task RebuildScrapeTabsAsync(ChainSessionViewModel? session)
    {
        if (session is null)
        {
            return;
        }
        if (_databaseProblem is not null)
        {
            session.SetTabs([]); // a refused file is never opened
            return;
        }

        ChainConfig chain = session.Chain;
        string dbPath = DbPath;
        List<string> tabNames;
        try
        {
            tabNames = await Task.Run(() =>
            {
                using SqliteConnection conn = Database.Open(dbPath);
                long qsrId = Repository.QsrId(conn, chain.Name);
                long platformId = Repository.PlatformId(conn, chain.PrimaryPlatform);
                return Repository.LoadScrapeTabs(conn, qsrId, platformId);
            });
        }
        catch (Exception)
        {
            tabNames = []; // unreadable database — same as "none yet"
        }
        session.SetTabs(tabNames);
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseDbCommand { get; }
    public AsyncRelayCommand ReloadRunsCommand { get; }
    public RelayCommand OpenUrlCommand { get; }
    public AsyncRelayCommand DeleteRunCommand { get; }
    public AsyncRelayCommand ExportRunCommand { get; }
    public RelayCommand CancelExportCommand { get; }
    public AsyncRelayCommand ResumeRunCommand { get; }
    public AsyncRelayCommand DeleteBeforeIdCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainViewModel()
    {
        // Run and Cancel act on the chain on screen; other chains' runs carry on regardless.
        RunCommand = new AsyncRelayCommand(_ => RunAsync(), _ => SelectedSession is { IsRunning: false }, allowsOverlap: true);
        CancelCommand = new RelayCommand(_ => RequestCancel(SelectedSession),
            _ => SelectedSession is { IsRunning: true, Cancellation.IsCancellationRequested: false });
        // The database stays put while any chain is being scraped into it.
        BrowseDbCommand = new RelayCommand(_ => BrowseDb(), _ => !IsAnyRunning);
        ReloadRunsCommand = new AsyncRelayCommand(_ => ReloadRunsAsync());
        OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string), p => p is string s && s.Length > 0);
        // Export/Delete of a completed run are allowed while a scrape run is in
        // progress (WAL makes concurrent read/write safe); only a second export
        // is blocked, since exports share one cancellation source and progress UI.
        DeleteRunCommand = new AsyncRelayCommand(p => DeleteRunAsync(p), _ => !IsExporting);
        ExportRunCommand = new AsyncRelayCommand(p => ExportRunAsync(p), _ => !IsExporting);
        CancelExportCommand = new RelayCommand(_ => CancelExport(),
            _ => IsExporting && _exportCts is { IsCancellationRequested: false });
        // A run resumes in its own chain's session, so only that chain must be idle.
        ResumeRunCommand = new AsyncRelayCommand(p => ResumeRunAsync(p),
            p => p is ScrapeRunViewModel run && _sessions.TryGetValue(run.QsrName, out ChainSessionViewModel? s)
                && !s.IsRunning,
            allowsOverlap: true);
        DeleteBeforeIdCommand = new AsyncRelayCommand(_ => DeleteBeforeIdAsync(), _ => !IsAnyRunning && !IsExporting);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());

        // Seed the chain list from the registry, sorted alphabetically by name, each with its own session.
        foreach (ChainConfig chain in ChainRegistry.All.OrderBy(c => c.Name, StringComparer.CurrentCulture))
        {
            ChainSessionViewModel session = new(chain);
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChainSessionViewModel.IsRunning))
                {
                    Notify(nameof(IsAnyRunning));
                    RelayCommand.RaiseRequery();
                }
            };
            _sessions[chain.Name] = session;
            Chains.Add(new ChainViewModel(chain, session));
        }

        // After the chains: the Chain filter offers them in the sidebar's order.
        RunsFilter = new RunsFilterViewModel(Runs, Chains.Select(c => c.Name));
    }

    // ── Initialisation (call after window loads — needs DB open) ──────────────
    public async Task InitialiseAsync()
    {
        // An older version's database is the first thing to settle: everything below reads it.
        if (Database.ValidateFile(DbPath) is { OlderVersion: int older } problem)
        {
            OfferFreshDatabase(Path.GetFullPath(DbPath), older, problem.Message);
        }
        RecheckDatabase();

        // Start with the sidebar's first chain selected: with none, Run is
        // disabled, the tab list and the map are empty, and nothing on screen
        // says why. ??= keeps a selection already made.
        SelectedChain ??= Chains.FirstOrDefault();
        await ReloadRunsAsync();
        await RebuildMapAsync();
    }

    // ── Run pipeline ──────────────────────────────────────────────────────────

    /// <summary>What one run hands the pipeline; <see cref="RunPipeline"/> takes it.</summary>
    internal sealed record PipelineCall(
        string DbPath, PipelineOptions Options, IProgress<string> Log, CancellationToken Token,
        IProgress<StepProgress> Progress, IProgress<int> LocationsChanged, IProgress<long> RunChanged,
        RequestGate Gate);

    /// <summary>
    /// Runs the pipeline for one session. Replaceable so UI tests can stand in a run that scrapes nothing; the
    /// app always runs <see cref="PipelineRunner.RunAsync"/>.
    /// </summary>
    internal Func<PipelineCall, Task> RunPipeline { get; set; } = call => PipelineRunner.RunAsync(
        call.DbPath, call.Options, call.Log, call.Token, call.Progress, call.LocationsChanged, call.RunChanged,
        call.Gate);

    private async Task RunAsync()
    {
        if (SelectedSession is not { IsRunning: false } session)
        {
            return;
        }

        CancellationToken token = session.BeginRun();

        // If the configured database can't be opened, create a fresh one in a
        // writable location and switch to it before the run touches it — or,
        // for an older version's database, stop unless the user sets it aside.
        if (!EnsureDatabaseUsable())
        {
            session.EndRun("");
            return;
        }

        // A run takes its options now: changing them while it runs affects only the next one.
        PipelineOptions opts = new()
        {
            Chain = session.Chain,
            ForceCrawl = session.ForceCrawl,
            Geocode = session.Geocode,
            GeocodeRematch = session.GeocodeRematch,
            // For a full run, every tab is scraped but the unticked ones. Selective runs never reach Step 2.
            Tabs = session.IsSelective ? null : [.. session.ScrapeTabs.Where(t => t.IsSelected).Select(t => t.Name)],
        };

        await RunInSessionAsync(session, token, (log, progress, locationsChanged, runChanged) =>
            RunPipeline(new PipelineCall(DbPath, opts, log, token, progress, locationsChanged, runChanged,
                _scheduler.For(session.Chain.Name))));
    }

    /// <summary>
    /// The life of one run in <paramref name="session"/>, shared by Run and Resume: progress and log go to that
    /// session (the Progress objects are made here, on the UI thread, so the pipeline's reports land on it), a
    /// cancellation is reported as one, and afterwards the runs list, map and tab list are re-read.
    /// </summary>
    private async Task RunInSessionAsync(
        ChainSessionViewModel session, CancellationToken token,
        Func<IProgress<string>, IProgress<StepProgress>, IProgress<int>, IProgress<long>, Task> pipeline)
    {
        Progress<string> log = new(session.LogLines.Add);
        Progress<StepProgress> progress = new(p => session.ReportProgress(p.Done, p.Total, p.Phase));
        // Fires as Step 1 commits location batches: the Locations tab refreshes live if this chain is on screen.
        Progress<int> locationsChanged = new(_ =>
        {
            if (ReferenceEquals(SelectedSession, session))
            {
                NotifyLocationsChanged();
            }
        });
        // Fires as Step 2 starts its run and records each page, so the Scrape Runs tab shows it growing.
        Progress<long> runChanged = new(_ => NotifyRunsChanged());

        string outcome = "Done";
        try
        {
            await Task.Run(() => pipeline(log, progress, locationsChanged, runChanged), token);
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled";
            session.LogLines.Add("  ✗ Cancelled.");
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            // A page/browser closed mid-flight because we cancelled — that
            // surfaces as a Playwright exception rather than OperationCanceled.
            // Treat it as a clean cancellation, not an error.
            _ = ex;
            outcome = "Cancelled";
            session.LogLines.Add("  ✗ Cancelled.");
        }
        catch (Exception ex)
        {
            outcome = "Failed";
            session.LogLines.Add($"  ✗ Error: {ex.Message}");
        }
        finally
        {
            session.EndRun(outcome);
            await ReloadRunsAsync();
            if (ReferenceEquals(SelectedSession, session))
            {
                await RebuildMapAsync();
            }
            // Tabs discovered (or disabled) by the run just finished must show up right away, not at next restart.
            await RebuildScrapeTabsAsync(session);
        }
    }

    /// <summary>
    /// Signal cancellation of <paramref name="session"/>'s run and give immediate feedback. Repeated clicks are
    /// ignored — the message is logged and the token cancelled only once. The running pipeline observes the token
    /// at each step boundary; Playwright pages are force-closed when the task unwinds.
    /// </summary>
    private static void RequestCancel(ChainSessionViewModel? session)
    {
        if (session?.Cancellation is not { IsCancellationRequested: false } cancellation)
        {
            return;
        }

        session.LogLines.Add("  ⚠ Cancelling … (finishing current operation)");
        cancellation.Cancel();
        RelayCommand.RaiseRequery();   // disable the Cancel button immediately
    }

    /// <summary>Cancels every chain's run — what closing the window with runs in progress does, once confirmed.</summary>
    public void CancelAllRuns()
    {
        foreach (ChainSessionViewModel session in _sessions.Values)
        {
            RequestCancel(session);
        }
    }

    // ── Locations map ─────────────────────────────────────────────────────────

    // Parsed-and-frozen district outlines, built once and reused across rebuilds.
    private static IReadOnlyList<(string Name, Geometry Shape, double Cx, double Cy)>? _mapGeo;
    private static IReadOnlyList<(string Name, Geometry Shape, double Cx, double Cy)> MapGeometries()
    {
        if (_mapGeo is null)
        {
            List<(string, Geometry, double, double)> list = new(TurkeyMap.Provinces.Count);
            foreach ((string name, string d, double cx, double cy) in TurkeyMap.Provinces)
            {
                Geometry g;
                try
                {
                    g = Geometry.Parse(d);
                    g.Freeze(); // frozen → safe to use on UI thread
                }
                catch
                {
                    g = Geometry.Empty;
                }
                list.Add((name, g, cx, cy));
            }
            _mapGeo = list;
        }
        return _mapGeo;
    }

    private int _mapRebuildGeneration;

    /// <summary>
    /// True while the Locations tab is the active one (set by the view on tab
    /// change). Gates the live map refresh so a crawl only rebuilds the map when
    /// the user is actually looking at it.
    /// </summary>
    public bool LocationsTabActive { get; set; }

    private DateTime _lastLiveMapRefreshUtc = DateTime.MinValue;

    /// <summary>
    /// Called (on the UI thread) as Step 1 commits location batches. Refreshes
    /// the map live when the Locations tab is active, throttled so a fast crawl
    /// doesn't rebuild on every district. The run's end triggers a final rebuild
    /// regardless, so a throttled-away update is never the last word.
    /// </summary>
    private void NotifyLocationsChanged()
    {
        if (!LocationsTabActive)
        {
            return;
        }
        DateTime nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastLiveMapRefreshUtc).TotalMilliseconds < 750)
        {
            return;
        }
        _lastLiveMapRefreshUtc = nowUtc;
        RefreshLocations();
    }

    /// <summary>
    /// Rebuild the Locations map/count from the current database state. Called
    /// when the Locations tab is activated so it reflects the latest committed
    /// data (including a crawl in progress) rather than waiting for a run to
    /// finish. The rebuild's generation guard makes repeated calls safe.
    /// </summary>
    public void RefreshLocations()
    {
        _ = RebuildMapAsync();
    }

    private Task RebuildMapAsync()
    {
        string? chainName = SelectedChain?.Name;
        string dbPath = DbPath;

        // Each request bumps the generation. A rebuild that finishes after a
        // newer one started must NOT apply its (now stale) result — otherwise an
        // early empty rebuild can overwrite the correct one and the map appears
        // blank until the user reselects the chain. The latest request wins
        // regardless of completion order.
        int generation = System.Threading.Interlocked.Increment(ref _mapRebuildGeneration);
        bool usable = _databaseProblem is null; // a refused file is never opened

        return Task.Run(() =>
        {
            // province match-key -> (total, district lines)
            Dictionary<string, (int Total, List<string> Districts)> byProvince = [];
            int locationTotal = 0;

            if (chainName is not null && usable && (File.Exists(dbPath) || dbPath.StartsWith(':')))
            {
                try
                {
                    using SqliteConnection conn = Database.Open(dbPath);
                    long qsrId;
                    try
                    {
                        qsrId = Repository.QsrId(conn, chainName);
                    }
                    catch (InvalidOperationException)
                    {
                        // Repository.QsrId's "QSR not found": the chain is not seeded
                        // yet (no Step 1 data). Any other failure is a real read error
                        // and reaches the catch below, which logs it.
                        qsrId = -1;
                    }

                    if (qsrId > 0)
                    {
                        locationTotal = Repository.CountLocations(conn, qsrId, activeOnly: true);

                        foreach (ProvinceDistrictCount row in Repository.GetLocationCountsByProvince(conn, qsrId))
                        {
                            // İstanbul is crawled as two halves (istanbul-avrupa /
                            // istanbul-asya). Collapse any such "province-suffix" slug to
                            // the segment before the first hyphen so both halves map
                            // to the single province on the map.
                            string provinceName = row.Province;
                            int dash = provinceName.IndexOf('-', StringComparison.Ordinal);
                            if (dash > 0)
                            {
                                provinceName = provinceName[..dash];
                            }

                            string key = TurkeyMap.Normalize(provinceName);
                            if (!byProvince.TryGetValue(key, out (int Total, List<string> Districts) agg))
                            {
                                agg = (0, []);
                            }
                            agg.Total += row.Count;
                            agg.Districts.Add($"{row.District} ({row.Count})");
                            byProvince[key] = agg;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // A missing file is already filtered out above, so anything
                    // caught here is a real read failure. Surface it in the log —
                    // a silently empty map hid the pre-v1.1 schema mismatch for
                    // days and looked exactly like "loading a DB doesn't refresh".
                    Application.Current.Dispatcher.Invoke(() =>
                        Log($"  ⚠ Locations map could not read '{dbPath}': {ex.Message}"));
                }
            }

            List<MapCellViewModel> cells = [];
            foreach ((string name, Geometry shape, double cx, double cy) in MapGeometries())
            {
                byProvince.TryGetValue(TurkeyMap.Normalize(name), out (int Total, List<string> Districts) agg);
                cells.Add(new MapCellViewModel
                {
                    ProvinceName = name, // the map's proper Turkish province name
                    Shape = shape,
                    DotLeft = cx - MapCellViewModel.DotBox / 2,
                    DotTop = cy - MapCellViewModel.DotBox / 2,
                    Total = agg.Total,
                    Districts = [.. (agg.Districts ?? []).OrderBy(d => d, StringComparer.CurrentCulture)],
                });
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                // A newer rebuild has superseded this one — discard stale result.
                if (generation != System.Threading.Volatile.Read(ref _mapRebuildGeneration))
                {
                    return;
                }

                LocationTotal = locationTotal;
                MapCells.Clear();
                foreach (MapCellViewModel cell in cells)
                {
                    MapCells.Add(cell);
                }
            });
        });
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // nothing we can do if the shell refuses
        }
    }

    // ── Scrape-runs management ────────────────────────────────────────────────

    /// <summary>
    /// True while the Scrape Runs tab is the active one (set by the view on tab change). Gates the live reload so
    /// a scrape only re-reads the runs when the user is looking at them; opening the tab reloads them anyway.
    /// </summary>
    public bool ScrapeRunsTabActive { get; set; }

    /// <summary>
    /// Called (on the UI thread) as Step 2 starts its run, records a page, and finishes. Not throttled: a page
    /// takes seconds to scrape, so this fires at most every few seconds, and the run's end reloads regardless.
    /// </summary>
    internal void NotifyRunsChanged()
    {
        if (ScrapeRunsTabActive)
        {
            _ = ReloadRunsAsync();
        }
    }

    private Task ReloadRunsAsync()
    {
        return Task.Run(() =>
    {
        if (_databaseProblem is not null)
        {
            // A refused file is never opened; its runs, or another file's, must not stay on screen.
            Application.Current.Dispatcher.Invoke(Runs.Clear);
            return;
        }
        if (!File.Exists(DbPath) && !DbPath.StartsWith(':'))
        {
            return;
        }

        try
        {
            using SqliteConnection conn = Database.Open(DbPath);
            List<ScrapeRunRow> runs = Repository.ListScrapeRuns(conn);
            Dictionary<long, string> qsrNames = ChainRegistry.All.ToDictionary(
                c => Repository.QsrId(conn, c.Name),
                c => c.Name);

            Dictionary<long, int> counts = Repository.CountSnapshotsByRun(conn);

            // An unfinished run is either being scraped (its lock is held, here or in another window) or crashed.
            HashSet<long> claimed = [.. runs.Where(r => r.FinishedAt is null && RunLock.IsHeld(conn, r.Id))
                .Select(r => r.Id)];

            Application.Current.Dispatcher.Invoke(() =>
            {
                // The grid reloads every few seconds during a scrape, so keep the user's selected row across the
                // rebuild — otherwise Export / Delete lose their target under the user's cursor.
                long? selectedId = SelectedRun?.Id;
                Runs.Clear();
                foreach (ScrapeRunRow r in runs)
                {
                    string name = qsrNames.TryGetValue(r.QsrId, out string? n) ? n : $"QSR #{r.QsrId}";
                    Runs.Add(new ScrapeRunViewModel(r, name, counts.GetValueOrDefault(r.Id), claimed.Contains(r.Id)));
                }
                SelectedRun = Runs.FirstOrDefault(r => r.Id == selectedId);
            });
        }
        catch (Exception ex)
        {
            // The missing-file case is filtered above; a failure here means the
            // file exists but can't be read as our schema — say so instead of
            // leaving the runs grid silently stale.
            Application.Current.Dispatcher.Invoke(() =>
                Log($"  ⚠ Scrape runs could not be read from '{DbPath}': {ex.Message}"));
        }
    });
    }

    private async Task ExportRunAsync(object? parameter)
    {
        if (parameter is not ScrapeRunViewModel vm || vm.IsErrored)
        {
            return;
        }

        string outputDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DbPath))!, "output");
        string dbPath = DbPath;
        string chain = vm.QsrName;

        _exportCts = new CancellationTokenSource();
        CancellationToken token = _exportCts.Token;
        ExportDone = 0;
        // Start at 1 (not 0): a ProgressBar with Maximum==0 renders as full, which
        // would flash a full bar before the real row count arrives. 0/1 reads empty.
        ExportTotal = 1;
        ExportText = "Preparing export …";
        IsExporting = true;

        Progress<StepProgress> progress = new(p =>
        {
            // Keep Maximum >= 1 so a zero-row export never renders a full bar.
            ExportTotal = Math.Max(1, p.Total);
            ExportDone = p.Done;
            ExportText = p.Total > 0 ? $"{p.Phase}  {p.Done:N0} / {p.Total:N0}" : p.Phase;
        });

        try
        {
            string outPath = await Task.Run(() =>
            {
                using SqliteConnection conn = Database.Open(dbPath);
                return ExcelExporter.Export(chain, conn, outputDir, null, progress, token);
            }, token);
            // Revealing the finished file in Explorer is the success signal now;
            // export status is no longer written to the Run Pipeline log.
            Explorer.Reveal(outPath);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before SaveAs — no file was written, so nothing to clean up.
        }
        catch (Exception ex)
        {
            // Surface failures here (not in the Run Pipeline log, which belongs to runs).
            ShowMessage($"Export failed:\n\n{ex.Message}",
                "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsExporting = false;
            ExportDone = 0;
            ExportTotal = 0;
            ExportText = "";
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    private void CancelExport()
    {
        if (_exportCts is null || _exportCts.IsCancellationRequested)
        {
            return;
        }
        ExportText = "Cancelling …";
        _exportCts.Cancel();
    }

    private async Task DeleteRunAsync(object? parameter)
    {
        if (parameter is not ScrapeRunViewModel vm || vm.IsErrored)
        {
            return;
        }
        MessageBoxResult result = ShowMessage(
            $"Delete run {vm.Id} ({vm.QsrName}, {vm.StartedAt}) and {vm.SnapshotCount:N0} snapshot(s)?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                using SqliteConnection conn = Database.Open(DbPath);
                Repository.DeleteScrapeRun(conn, vm.Id);
            });
        }
        catch (Exception ex)
        {
            // Can happen if a concurrent scrape held the write lock past the
            // busy_timeout. Surface it rather than letting it crash the app.
            ShowMessage($"Delete failed:\n\n{ex.Message}",
                "Delete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await ReloadRunsAsync();
    }

    private async Task DeleteBeforeIdAsync()
    {
        if (!long.TryParse(DeleteBeforeId, out long id))
        {
            return;
        }
        MessageBoxResult result = ShowMessage(
            $"Delete all scrape runs with ID < {id} and their snapshots?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                using SqliteConnection conn = Database.Open(DbPath);
                Repository.DeleteScrapeRunsBefore(conn, id);
            });
            DeleteBeforeId = "";
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
        {
            // A refused database (an older version's, another program's) or one locked past the busy timeout.
            // This had no catch: the failure reached the app's last-resort handler as "an unexpected error".
            ShowMessage($"Delete failed:\n\n{ex.Message}", "Delete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await ReloadRunsAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void BrowseDb()
    {
        // Start the dialog where the CURRENT database lives, with its file
        // name pre-selected — so "where am I now?" is answered by the dialog
        // itself, and picking a different folder is one navigation away.
        string currentDir = "";
        string currentFile = Database.DefaultFileName;
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(DbPath));
            if (dir is not null && Directory.Exists(dir))
            {
                currentDir = dir;
            }
            string file = Path.GetFileName(DbPath);
            if (!string.IsNullOrWhiteSpace(file))
            {
                currentFile = file;
            }
        }
        catch (Exception)
        {
            // Malformed DbPath — the dialog simply opens at its default folder.
        }

        Microsoft.Win32.OpenFileDialog dlg = new()
        {
            Title = "Select or create database file",
            Filter = "SQLite (*.sqlite)|*.sqlite|All files (*.*)|*.*",
            CheckFileExists = false,
            // Pre-filled so browsing to any folder and pressing Open creates
            // a database file there — no file has to exist yet.
            FileName = currentFile,
            InitialDirectory = currentDir,
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        // A pasted/typed folder path means "put the default file in there".
        string chosen = dlg.FileName;
        if (Directory.Exists(chosen))
        {
            chosen = Path.Combine(chosen, Database.DefaultFileName);
        }

        // Never trust a picked file blindly: a non-SQLite file or another
        // application's SQLite database is rejected here with a clear message,
        // and the previous database stays selected.
        DatabaseProblem? problem = Database.ValidateFile(chosen);
        if (problem is { OlderVersion: int older })
        {
            if (!OfferFreshDatabase(chosen, older, problem.Message))
            {
                return;
            }
        }
        else if (problem is not null)
        {
            ShowMessage(
                $"Cannot use '{chosen}'.\n\n{problem.Message}",
                "Database", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DbPath = chosen;
    }

    /// <summary>Open the output folder (creating it if needed) in File Explorer.</summary>
    private void OpenFolder()
    {
        try
        {
            string outputDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DbPath))!, "output");
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            Process.Start(new ProcessStartInfo("explorer.exe", outputDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"  ✗ Could not open folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Resume an errored/interrupted run: continue Step 2 for the platform
    /// locations still missing from that run, then re-run Step 3. Mirrors the
    /// full-run command's cancellation and reload handling.
    /// </summary>
    private async Task ResumeRunAsync(object? parameter)
    {
        if (parameter is not ScrapeRunViewModel vm
            || !_sessions.TryGetValue(vm.QsrName, out ChainSessionViewModel? session) || session.IsRunning)
        {
            return;
        }

        // The run resumes in its chain's own session, so its log and progress show on that chain.
        CancellationToken token = session.BeginRun();
        session.LogLines.Add($"▶ Resuming run {vm.Id} ({vm.QsrName})");
        await RunInSessionAsync(session, token, (log, progress, _, runChanged) =>
            PipelineRunner.ResumeRunAsync(DbPath, session.Chain, vm.Id, log, token, progressCount: progress,
                onRunChanged: runChanged, gate: _scheduler.For(session.Chain.Name)));
    }
}
