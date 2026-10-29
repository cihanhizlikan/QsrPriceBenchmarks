using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace QsrPriceBenchmarks.Ui.ViewModels;

/// <summary>
/// The Scrape Runs grid's filter and order (maintainer, 2026-10-29). <see cref="View"/> is the runs collection's
/// default view — the one the DataGrid itself sorts on a header click — so the filter and the sort outlive the
/// reload after every scraped page, which replaces the rows, not the view. Newest first until a header is clicked.
/// </summary>
public sealed class RunsFilterViewModel : ViewModelBase
{
    private readonly Func<string?> _selectedChain;
    private bool _onlySelectedChain;
    private bool _showRunning = true;
    private bool _showUnfinished = true;
    private bool _showFinished = true;

    /// <param name="runs">The runs the grid shows; the filter and sort live on its default view.</param>
    /// <param name="selectedChain">The chain selected in the sidebar, which <see cref="OnlySelectedChain"/> follows.</param>
    public RunsFilterViewModel(ObservableCollection<ScrapeRunViewModel> runs, Func<string?> selectedChain)
    {
        _selectedChain = selectedChain;
        View = CollectionViewSource.GetDefaultView(runs);
        View.Filter = IsShown;
        View.SortDescriptions.Add(new SortDescription(nameof(ScrapeRunViewModel.Id), ListSortDirection.Descending));
    }

    public ICollectionView View { get; }

    /// <summary>Show only the runs of the chain selected in the sidebar, following it as the selection changes.</summary>
    public bool OnlySelectedChain
    {
        get
        {
            return _onlySelectedChain;
        }

        set
        {
            Set(ref _onlySelectedChain, value);
            View.Refresh();
        }
    }

    public bool ShowRunning
    {
        get
        {
            return _showRunning;
        }

        set
        {
            Set(ref _showRunning, value);
            View.Refresh();
        }
    }

    public bool ShowUnfinished
    {
        get
        {
            return _showUnfinished;
        }

        set
        {
            Set(ref _showUnfinished, value);
            View.Refresh();
        }
    }

    public bool ShowFinished
    {
        get
        {
            return _showFinished;
        }

        set
        {
            Set(ref _showFinished, value);
            View.Refresh();
        }
    }

    /// <summary>Called when the sidebar's selection changes: a chain-only view follows it.</summary>
    public void OnSelectedChainChanged()
    {
        if (_onlySelectedChain)
        {
            View.Refresh();
        }
    }

    private bool IsShown(object item)
    {
        if (item is not ScrapeRunViewModel run)
        {
            return false;
        }
        if (_onlySelectedChain && run.QsrName != _selectedChain())
        {
            return false;
        }
        return run.State switch
        {
            RunState.Running => _showRunning,
            RunState.Unfinished => _showUnfinished,
            _ => _showFinished,
        };
    }
}
