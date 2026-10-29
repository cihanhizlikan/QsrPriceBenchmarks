using System.Collections.ObjectModel;
using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.Ui.ViewModels;

/// <summary>
/// One chain's run, apart from every other chain's (plan WP-C2): its own log, progress bar, options, tab ticks and
/// Cancel, so the user can switch chains mid-scrape to start, watch or cancel another. The Run Pipeline tab shows
/// the selected chain's session; the sidebar card shows each session's status. A run takes its options when it
/// starts, so changing them while it runs affects only the next one.
/// </summary>
public sealed class ChainSessionViewModel(ChainConfig chain) : ViewModelBase
{
    private bool _isRunning;
    private int _progressDone;
    private int _progressTotal;
    private string _progressText = "";
    private string _outcome = "";
    private bool _forceCrawl;
    private bool _geocode;
    private bool _geocodeRematch;
    private bool _showNoTabsHint;

    public ChainConfig Chain { get; } = chain;

    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>The tabs Step 2 may scrape; ticks survive switching chains and are kept across tab-list refreshes.</summary>
    public ObservableCollection<TabOptionViewModel> ScrapeTabs { get; } = [];

    /// <summary>Set while this chain's run is in progress; null otherwise.</summary>
    internal CancellationTokenSource? Cancellation { get; private set; }

    public bool IsRunning
    {
        get
        {
            return _isRunning;
        }

        private set
        {
            Set(ref _isRunning, value);
            Notify(nameof(IsRematchEnabled));
            Notify(nameof(ProgressVisible));
            Notify(nameof(StatusText));
        }
    }

    public int ProgressDone
    {
        get
        {
            return _progressDone;
        }

        private set
        {
            Set(ref _progressDone, value);
        }
    }

    public int ProgressTotal
    {
        get
        {
            return _progressTotal;
        }

        private set
        {
            Set(ref _progressTotal, value);
            Notify(nameof(ProgressVisible));
        }
    }

    public string ProgressText
    {
        get
        {
            return _progressText;
        }

        private set
        {
            Set(ref _progressText, value);
            Notify(nameof(StatusText));
        }
    }

    /// <summary>The bar shows only while a run is active and a phase has reported a total.</summary>
    public bool ProgressVisible
    {
        get
        {
            return _isRunning && _progressTotal > 0;
        }
    }

    /// <summary>
    /// The line under the chain's name in the sidebar: what its run is doing now, how its last run ended, or
    /// null before its first run (the line is then collapsed).
    /// </summary>
    public string? StatusText
    {
        get
        {
            if (!_isRunning)
            {
                return _outcome.Length > 0 ? _outcome : null;
            }
            return _progressText.Length > 0 ? _progressText : "Running…";
        }
    }

    // Step flags — Rematch is a modifier of Geocode, enforced in the setters.
    public bool ForceCrawl
    {
        get
        {
            return _forceCrawl;
        }

        set
        {
            Set(ref _forceCrawl, value);
            NotifyRunModeChanged();
        }
    }

    public bool Geocode
    {
        get
        {
            return _geocode;
        }

        set
        {
            Set(ref _geocode, value);
            // Rematch is meaningless on its own, so clearing Geocode also clears Rematch and disables its checkbox.
            if (!value)
            {
                Set(ref _geocodeRematch, false, nameof(GeocodeRematch));
            }
            Notify(nameof(IsRematchEnabled));
            NotifyRunModeChanged();
        }
    }

    public bool GeocodeRematch
    {
        get
        {
            return _geocodeRematch;
        }

        // Rematch can only be turned on when Geocode is already on.
        set
        {
            Set(ref _geocodeRematch, value && Geocode);
            NotifyRunModeChanged();
        }
    }

    /// <summary>Rematch checkbox is only enabled while Geocode is checked.</summary>
    public bool IsRematchEnabled
    {
        get
        {
            return Geocode && !_isRunning;
        }
    }

    /// <summary>True when at least one step flag is set — a selective run, which never reaches Step 2.</summary>
    public bool IsSelective
    {
        get
        {
            return ForceCrawl || Geocode || GeocodeRematch;
        }
    }

    /// <summary>Tab checkboxes are shown only for a full run, the one mode that reaches Step 2.</summary>
    public bool ShowTabSelection
    {
        get
        {
            return !IsSelective;
        }
    }

    /// <summary>
    /// True while the chain has no discovered tabs in the current database: the Run Pipeline tab shows an
    /// explanatory hint instead of an empty list — the first Step 2 run discovers the tabs as it scrapes them.
    /// </summary>
    public bool ShowNoTabsHint
    {
        get
        {
            return _showNoTabsHint;
        }

        private set
        {
            Set(ref _showNoTabsHint, value);
        }
    }

    /// <summary>Clears the log and progress and marks the run started; returns its cancellation token.</summary>
    internal CancellationToken BeginRun()
    {
        LogLines.Clear();
        ProgressDone = 0;
        ProgressTotal = 0;
        ProgressText = "";
        _outcome = "";
        Cancellation = new CancellationTokenSource();
        IsRunning = true;
        return Cancellation.Token;
    }

    /// <summary>Marks the run ended with <paramref name="outcome"/> ("Done", "Cancelled", "Failed"), shown in the sidebar.</summary>
    internal void EndRun(string outcome)
    {
        _outcome = outcome;
        ProgressTotal = 0;
        ProgressText = "";
        Cancellation?.Dispose();
        Cancellation = null;
        IsRunning = false;
    }

    /// <summary>Takes a progress report from the pipeline (on the UI thread).</summary>
    internal void ReportProgress(int done, int total, string phase)
    {
        ProgressTotal = total;
        ProgressDone = done;
        ProgressText = total > 0 ? $"{phase}  {done} / {total}" : "";
    }

    /// <summary>
    /// Replaces the tab list with <paramref name="names"/> from the database, keeping the tick of every tab already
    /// listed; a tab new to the list comes ticked.
    /// </summary>
    internal void SetTabs(IReadOnlyList<string> names)
    {
        Dictionary<string, bool> ticks = ScrapeTabs.ToDictionary(t => t.Name, t => t.IsSelected, StringComparer.Ordinal);
        ScrapeTabs.Clear();
        foreach (string name in names)
        {
            ScrapeTabs.Add(new TabOptionViewModel(name) { IsSelected = ticks.GetValueOrDefault(name, true) });
        }
        ShowNoTabsHint = ScrapeTabs.Count == 0;
    }

    private void NotifyRunModeChanged()
    {
        Notify(nameof(IsSelective));
        Notify(nameof(ShowTabSelection));
    }
}
