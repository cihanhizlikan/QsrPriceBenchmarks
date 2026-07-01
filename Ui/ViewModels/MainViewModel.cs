using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Export;
using QsrPriceBenchmarks.Core.Models;
using ChainRegistry = QsrPriceBenchmarks.Core.Models.Chains;
using QsrPriceBenchmarks.Core.Services;

namespace QsrPriceBenchmarks.Ui.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    // ── State ─────────────────────────────────────────────────────────────────
    private string       _dbPath      = DefaultDbPath();
    private ChainViewModel? _selected;
    private bool         _isRunning;
    private bool         _forceCrawl;
    private bool         _geocode;
    private bool         _geocodeRematch;
    private bool         _export;
    private string       _exportSince = "";
    private string       _deleteBeforeId = "";
    private string?      _lastOutputPath;
    private int          _progressDone;
    private int          _progressTotal;
    private string       _progressText = "";
    private CancellationTokenSource? _cts;

    // Export runs on its own cancellation source, independent of a pipeline run.
    private CancellationTokenSource? _exportCts;
    private int          _exportDone;
    private int          _exportTotal;
    private string       _exportText = "";

    // ── Observable collections ────────────────────────────────────────────────
    public ObservableCollection<ChainViewModel>    Chains    { get; } = new();
    public ObservableCollection<string>            LogLines  { get; } = new();
    public ObservableCollection<ScrapeRunViewModel> Runs     { get; } = new();
    public ObservableCollection<MapCellViewModel> MapCells { get; } = new();

    // Total LOCATIONS for the selected chain, shown atop the Locations tab.
    private int _locationTotal;
    public int LocationTotal
    {
        get => _locationTotal;
        private set { Set(ref _locationTotal, value); }
    }
    public ObservableCollection<TabOptionViewModel> ScrapeTabs { get; } = new();

    // ── Properties ────────────────────────────────────────────────────────────
    public string DbPath
    {
        get => _dbPath;
        set { Set(ref _dbPath, value); ReloadRunsCommand.Execute(null); _ = RebuildMapAsync(); }
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

    /// <summary>
    /// If the configured <see cref="DbPath"/> can't be opened (missing or
    /// unwritable directory, etc.), fall back to a brand-new database in the
    /// default writable location and switch <see cref="DbPath"/> to it, logging
    /// the change. Leaves <see cref="DbPath"/> untouched when it opens fine.
    /// </summary>
    private void EnsureDatabaseUsable()
    {
        try
        {
            using SqliteConnection probe = Database.Open(DbPath);
            return;
        }
        catch (Exception ex)
        {
            string fallback = DefaultDbPath();
            LogLines.Add($"  ⚠ Could not open database '{DbPath}' ({ex.Message}).");
            if (!string.Equals(fallback, DbPath, StringComparison.OrdinalIgnoreCase))
            {
                LogLines.Add($"  → Creating a new database at {fallback}");
                DbPath = fallback; // writable; Database.Open creates the folder + file
            }
        }
    }

    public ChainViewModel? SelectedChain
    {
        get => _selected;
        set { Set(ref _selected, value); RebuildScrapeTabs(); NotifyRunModeChanged(); RelayCommand.RaiseRequery(); _ = RebuildMapAsync(); }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set { Set(ref _isRunning, value); Notify(nameof(IsRematchEnabled)); Notify(nameof(ProgressVisible)); Notify(nameof(IsBusy)); RelayCommand.RaiseRequery(); }
    }

    private bool _isExporting;
    /// <summary>True while an Excel export is being prepared on a background thread.</summary>
    public bool IsExporting
    {
        get => _isExporting;
        private set { Set(ref _isExporting, value); Notify(nameof(IsBusy)); Notify(nameof(ExportProgressVisible)); RelayCommand.RaiseRequery(); }
    }

    /// <summary>
    /// True when any long operation (a pipeline run, a resume, or an export) is
    /// in progress. The Scrape Runs row actions (Export / Resume / Delete) bind
    /// their enabled state to this so none can be triggered while busy.
    /// </summary>
    public bool IsBusy => IsRunning || IsExporting;

    // ── Step-3 geocoding progress bar ─────────────────────────────────────────
    public int ProgressDone  { get => _progressDone;  private set { Set(ref _progressDone, value);  Notify(nameof(ProgressVisible)); } }
    public int ProgressTotal { get => _progressTotal; private set { Set(ref _progressTotal, value); Notify(nameof(ProgressVisible)); } }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    /// <summary>The bar shows only while a run is active and a phase has reported a total.</summary>
    public bool ProgressVisible => IsRunning && _progressTotal > 0;

    // ── Excel export progress bar (Scrape Runs tab) ───────────────────────────
    public int ExportDone  { get => _exportDone;  private set => Set(ref _exportDone, value); }
    public int ExportTotal { get => _exportTotal; private set => Set(ref _exportTotal, value); }
    public string ExportText { get => _exportText; private set => Set(ref _exportText, value); }

    /// <summary>The export bar/Cancel show for the whole duration of an export.</summary>
    public bool ExportProgressVisible => IsExporting;

    // Step flags — mutually exclusive pairs enforced via property setters
    public bool ForceCrawl      { get => _forceCrawl;      set { Set(ref _forceCrawl, value); NotifyRunModeChanged(); } }
    public bool Geocode
    {
        get => _geocode;
        set
        {
            Set(ref _geocode, value);
            // Rematch is a modifier of Geocode — it's meaningless on its own,
            // so clearing Geocode also clears Rematch and disables its checkbox.
            if (!value) Set(ref _geocodeRematch, false, nameof(GeocodeRematch));
            Notify(nameof(IsRematchEnabled));
            NotifyRunModeChanged();
        }
    }

    /// <summary>Rematch checkbox is only enabled while Geocode is checked.</summary>
    public bool IsRematchEnabled => Geocode && !IsRunning;

    public bool GeocodeRematch
    {
        get => _geocodeRematch;
        // Rematch can only be turned on when Geocode is already on.
        set { Set(ref _geocodeRematch, value && Geocode); NotifyRunModeChanged(); }
    }
    public bool Export
    {
        get => _export;
        set { Set(ref _export, value); if (!value) ExportSince = ""; NotifyRunModeChanged(); }
    }
    public string ExportSince { get => _exportSince; set { Set(ref _exportSince, value); NotifyRunModeChanged(); } }
    public string DeleteBeforeId { get => _deleteBeforeId; set => Set(ref _deleteBeforeId, value); }

    private ScrapeRunViewModel? _selectedRun;
    public ScrapeRunViewModel? SelectedRun { get => _selectedRun; set => Set(ref _selectedRun, value); }

    public string? LastOutputPath
    {
        get => _lastOutputPath;
        private set { Set(ref _lastOutputPath, value); RelayCommand.RaiseRequery(); }
    }

    // True when at least one step flag is set (selective run mode)
    public bool IsSelective => ForceCrawl || Geocode || GeocodeRematch || Export || !string.IsNullOrWhiteSpace(ExportSince);

    /// <summary>
    /// Tab checkboxes are shown only for a full run (no individual step picked)
    /// with a chain selected — that's the only mode that reaches Step 2.
    /// </summary>
    public bool ShowTabSelection => !IsSelective && SelectedChain is not null;

    private void NotifyRunModeChanged()
    {
        Notify(nameof(IsSelective));
        Notify(nameof(ShowTabSelection));
    }

    private void RebuildScrapeTabs()
    {
        ScrapeTabs.Clear();
        if (SelectedChain is null) return;
        foreach (var t in SelectedChain.Chain.PrimaryTabs)
            ScrapeTabs.Add(new TabOptionViewModel(t)); // ticked by default
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand      CancelCommand { get; }
    public RelayCommand      BrowseDbCommand { get; }
    public AsyncRelayCommand ReloadRunsCommand { get; }
    public RelayCommand      OpenUrlCommand { get; }
    public AsyncRelayCommand DeleteRunCommand { get; }
    public AsyncRelayCommand ExportRunCommand { get; }
    public RelayCommand      CancelExportCommand { get; }
    public AsyncRelayCommand ResumeRunCommand { get; }
    public AsyncRelayCommand DeleteBeforeIdCommand { get; }
    public RelayCommand      OpenOutputCommand { get; }
    public RelayCommand      OpenFolderCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainViewModel()
    {
        RunCommand          = new AsyncRelayCommand(_ => RunAsync(),     _ => !IsRunning && SelectedChain is not null);
        CancelCommand       = new RelayCommand(_ => RequestCancel(),
            _ => IsRunning && _cts is { IsCancellationRequested: false });
        BrowseDbCommand     = new RelayCommand(_ => BrowseDb(), _ => !IsRunning);
        ReloadRunsCommand   = new AsyncRelayCommand(_ => ReloadRunsAsync());
        OpenUrlCommand      = new RelayCommand(p => OpenUrl(p as string), p => p is string s && s.Length > 0);
        // Export/Delete of a completed run are allowed while a scrape run is in
        // progress (WAL makes concurrent read/write safe); only a second export
        // is blocked, since exports share one cancellation source and progress UI.
        DeleteRunCommand    = new AsyncRelayCommand(p  => DeleteRunAsync(p), _ => !IsExporting);
        ExportRunCommand    = new AsyncRelayCommand(p  => ExportRunAsync(p), _ => !IsExporting);
        CancelExportCommand = new RelayCommand(_ => CancelExport(),
            _ => IsExporting && _exportCts is { IsCancellationRequested: false });
        ResumeRunCommand    = new AsyncRelayCommand(p  => ResumeRunAsync(p), _ => !IsBusy);
        DeleteBeforeIdCommand = new AsyncRelayCommand(_ => DeleteBeforeIdAsync(), _ => !IsBusy);
        OpenOutputCommand   = new RelayCommand(_ => OpenOutput(), _ => !string.IsNullOrEmpty(LastOutputPath));
        OpenFolderCommand   = new RelayCommand(_ => OpenFolder());

        // Seed the chain list from the registry, sorted alphabetically by name.
        foreach (var chain in ChainRegistry.All.OrderBy(c => c.Name, StringComparer.CurrentCulture))
            Chains.Add(new ChainViewModel(chain, null)); // logo bytes loaded from DB on demand
    }

    // ── Initialisation (call after window loads — needs DB open) ──────────────
    public async Task InitialiseAsync()
    {
        await LoadChainLogosAsync();
        await ReloadRunsAsync();
        await RebuildMapAsync();
    }

    // ── Run pipeline ──────────────────────────────────────────────────────────
    private async Task RunAsync()
    {
        if (SelectedChain is null) return;
        IsRunning = true;
        LastOutputPath = null;
        LogLines.Clear();
        ProgressDone = 0; ProgressTotal = 0; ProgressText = "";

        // If the configured database can't be opened, create a fresh one in a
        // writable location and switch to it before the run touches it.
        EnsureDatabaseUsable();

        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(msg => LogLines.Add(msg));
        var progressCount = new Progress<StepProgress>(p =>
        {
            ProgressTotal = p.Total;
            ProgressDone  = p.Done;
            ProgressText  = p.Total > 0 ? $"{p.Phase}  {p.Done} / {p.Total}" : "";
        });
        // Fires (on the UI thread) as Step 1 commits location batches, so the
        // Locations tab can refresh live while a crawl is in progress.
        Progress<int> onLocationsChanged = new(_ => NotifyLocationsChanged());

        long? exportSinceId = null;
        if (!string.IsNullOrWhiteSpace(ExportSince) && long.TryParse(ExportSince, out var sid))
            exportSinceId = sid;

        // For a full run, scrape only the ticked tabs (all ticked by default).
        // Selective runs never reach Step 2, so leave Tabs null there.
        IReadOnlyCollection<string>? selectedTabs =
            IsSelective ? null : ScrapeTabs.Where(t => t.IsSelected).Select(t => t.Name).ToList();

        var opts = new PipelineOptions
        {
            Chain          = SelectedChain.Chain,
            ForceCrawl     = ForceCrawl,
            Geocode        = Geocode,
            GeocodeRematch = GeocodeRematch,
            Export         = Export,
            ExportSince    = exportSinceId,
            Tabs           = selectedTabs,
            OutputDir      = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DbPath))!, "output"),
        };

        var token = _cts.Token;
        try
        {
            var outPath = await Task.Run(() =>
                PipelineRunner.RunAsync(DbPath, opts, progress, token, progressCount, onLocationsChanged),
                token);
            LastOutputPath = outPath;
        }
        catch (OperationCanceledException)
        {
            LogLines.Add("  ✗ Cancelled.");
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            // A page/browser closed mid-flight because we cancelled — that
            // surfaces as a Playwright exception rather than OperationCanceled.
            // Treat it as a clean cancellation, not an error.
            _ = ex;
            LogLines.Add("  ✗ Cancelled.");
        }
        catch (Exception ex)
        {
            LogLines.Add($"  ✗ Error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            ProgressTotal = 0; ProgressText = "";
            _cts?.Dispose();
            _cts = null;
            await ReloadRunsAsync();
                await RebuildMapAsync();
        }
    }

    /// <summary>
    /// Signal cancellation and give immediate UI feedback. Repeated clicks are
    /// ignored — the message is logged and the token cancelled only once. The
    /// running pipeline observes the token at each step boundary; Playwright
    /// pages are force-closed when the task unwinds.
    /// </summary>
    private void RequestCancel()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        LogLines.Add("  ⚠ Cancelling … (finishing current operation)");
        _cts.Cancel();
        RelayCommand.RaiseRequery();   // disable the Cancel button immediately
    }

    // ── Locations map ─────────────────────────────────────────────────────────

    // Parsed-and-frozen district outlines, built once and reused across rebuilds.
    private static IReadOnlyList<(string Name, Geometry Shape, double Cx, double Cy)>? _mapGeo;
    private static IReadOnlyList<(string Name, Geometry Shape, double Cx, double Cy)> MapGeometries()
    {
        if (_mapGeo is null)
        {
            List<(string, Geometry, double, double)> list = new(TurkeyMap.Districts.Count);
            foreach ((string name, string d, double cx, double cy) in TurkeyMap.Districts)
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

        return Task.Run(() =>
        {
            // province match-key -> (total, district lines)
            Dictionary<string, (int Total, List<string> Districts)> byProvince = new();
            int locationTotal = 0;

            if (chainName is not null && (File.Exists(dbPath) || dbPath.StartsWith(":")))
            {
                try
                {
                    using SqliteConnection conn = Database.Open(dbPath);
                    long qsrId;
                    try
                    {
                        qsrId = Repository.QsrId(conn, chainName);
                    }
                    catch
                    {
                        qsrId = -1; // chain not seeded yet (no Step 1 data)
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
                            int dash = provinceName.IndexOf('-');
                            if (dash > 0)
                            {
                                provinceName = provinceName[..dash];
                            }

                            string key = TurkeyMap.Normalize(provinceName);
                            if (!byProvince.TryGetValue(key, out (int Total, List<string> Districts) agg))
                            {
                                agg = (0, new List<string>());
                            }
                            agg.Total += row.Count;
                            agg.Districts.Add($"{row.District} ({row.Count})");
                            byProvince[key] = agg;
                        }
                    }
                }
                catch
                {
                    // DB may not exist yet — show the empty (dimmed) map
                }
            }

            List<MapCellViewModel> cells = new();
            foreach ((string name, Geometry shape, double cx, double cy) in MapGeometries())
            {
                byProvince.TryGetValue(TurkeyMap.Normalize(name), out (int Total, List<string> Districts) agg);
                cells.Add(new MapCellViewModel
                {
                    ProvinceName  = name, // the map's proper Turkish district name
                    Shape     = shape,
                    DotLeft   = cx - MapCellViewModel.DotBox / 2,
                    DotTop    = cy - MapCellViewModel.DotBox / 2,
                    Total     = agg.Total,
                    Districts = (agg.Districts ?? new List<string>())
                                .OrderBy(d => d, StringComparer.CurrentCulture).ToList(),
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
    private Task ReloadRunsAsync() => Task.Run(() =>
    {
        if (!File.Exists(DbPath) && !DbPath.StartsWith(":")) return;
        try
        {
            using var conn = Database.Open(DbPath);
            var runs = Repository.ListScrapeRuns(conn);
            var qsrNames = ChainRegistry.All.ToDictionary(
                c => Repository.QsrId(conn, c.Name),
                c => c.Name);

            // Snapshot counts
            var counts = runs.ToDictionary(r => r.Id, r =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM MENU_SNAPSHOTS WHERE SCRAPE_RUN_ID = $id;";
                cmd.Parameters.AddWithValue("$id", r.Id);
                return Convert.ToInt32(cmd.ExecuteScalar());
            });

            Application.Current.Dispatcher.Invoke(() =>
            {
                Runs.Clear();
                foreach (var r in runs)
                {
                    var name = qsrNames.TryGetValue(r.QsrId, out var n) ? n : $"QSR #{r.QsrId}";
                    Runs.Add(new ScrapeRunViewModel(r, name, counts[r.Id]));
                }
            });
        }
        catch { /* DB may not exist yet */ }
    });

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
            LastOutputPath = outPath;
            // Revealing the finished file in Explorer is the success signal now;
            // export status is no longer written to the Run Pipeline log.
            RevealInExplorer(outPath);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before SaveAs — no file was written, so nothing to clean up.
        }
        catch (Exception ex)
        {
            // Surface failures here (not in the Run Pipeline log, which belongs to runs).
            MessageBox.Show($"Export failed:\n\n{ex.Message}",
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
        MessageBoxResult result = MessageBox.Show(
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
            MessageBox.Show($"Delete failed:\n\n{ex.Message}",
                "Delete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await ReloadRunsAsync();
    }

    private async Task DeleteBeforeIdAsync()
    {
        if (!long.TryParse(DeleteBeforeId, out var id)) return;
        var result = MessageBox.Show(
            $"Delete all scrape runs with ID < {id} and their snapshots?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        await Task.Run(() =>
        {
            using var conn = Database.Open(DbPath);
            Repository.DeleteScrapeRunsBefore(conn, id);
        });
        DeleteBeforeId = "";
        await ReloadRunsAsync();
    }

    // ── Logo management ───────────────────────────────────────────────────────
    private async Task LoadChainLogosAsync() => await Task.Run(() =>
    {
        if (!File.Exists(DbPath)) return;
        try
        {
            using var conn = Database.Open(DbPath);
            foreach (var vm in Chains)
            {
                var qsrId = Repository.QsrId(conn, vm.Name);
                var bytes = Repository.GetLogoBlob(conn, qsrId);
                if (bytes is not null)
                {
                    // Replace the ChainViewModel with a new one carrying the logo.
                    // WPF will re-render via INPC.
                    var idx = Chains.IndexOf(vm);
                    var updated = new ChainViewModel(vm.Chain, bytes);
                    Application.Current.Dispatcher.Invoke(() => Chains[idx] = updated);
                }
            }
        }
        catch { }
    });

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void BrowseDb()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title            = "Select or create database file",
            Filter           = "SQLite (*.sqlite)|*.sqlite|All files (*.*)|*.*",
            CheckFileExists  = false,
        };
        if (dlg.ShowDialog() == true)
            DbPath = dlg.FileName;
    }

    private void OpenOutput()
    {
        if (string.IsNullOrEmpty(LastOutputPath)) return;
        var dir = Path.GetDirectoryName(LastOutputPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
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
            LogLines.Add($"  ✗ Could not open folder: {ex.Message}");
        }
    }

    /// <summary>Open File Explorer with the given file selected/highlighted.</summary>
    private static void RevealInExplorer(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
            { UseShellExecute = true });
        }
        catch
        {
            // Best effort — a failure to reveal must never break the export.
        }
    }

    /// <summary>
    /// Resume an errored/interrupted run: continue Step 2 for the platform
    /// locations still missing from that run, then re-run Step 3. Mirrors the
    /// full-run command's cancellation and reload handling.
    /// </summary>
    private async Task ResumeRunAsync(object? parameter)
    {
        if (parameter is not ScrapeRunViewModel vm || IsRunning)
        {
            return;
        }

        ChainViewModel? chain = Chains.FirstOrDefault(c => c.Name == vm.QsrName);
        if (chain is null)
        {
            LogLines.Add($"  ✗ Cannot resume — chain '{vm.QsrName}' not found.");
            return;
        }

        IsRunning = true;
        LogLines.Clear();
        _cts = new CancellationTokenSource();
        Progress<string> progress = new(msg => LogLines.Add(msg));
        CancellationToken token = _cts.Token;

        try
        {
            LogLines.Add($"▶ Resuming run {vm.Id} ({vm.QsrName})");
            await Task.Run(() =>
                PipelineRunner.ResumeRunAsync(DbPath, chain.Chain, vm.Id, progress, token),
                token);
            LogLines.Add("  ✓ Resume complete.");
        }
        catch (OperationCanceledException)
        {
            LogLines.Add("  ✗ Cancelled.");
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            _ = ex;
            LogLines.Add("  ✗ Cancelled.");
        }
        catch (Exception ex)
        {
            LogLines.Add($"  ✗ Error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            await ReloadRunsAsync();
                await RebuildMapAsync();
        }
    }
}
