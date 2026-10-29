using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace QsrPriceBenchmarks.Ui.ViewModels;

/// <summary>
/// The Scrape Runs grid's filters and order. Two Excel-style column filters (maintainer, 2026-10-29): <see cref="Chain"/>
/// in the Chain header, and <see cref="State"/> — running, unfinished, finished — in the Finished header, the
/// column that shows those states. <see cref="View"/> is the runs collection's default view, the one the DataGrid
/// sorts on a header click, so the filters and sort outlive the reload after every scraped page, which replaces the
/// rows, not the view. Newest first until a header is clicked.
/// </summary>
public sealed class RunsFilterViewModel
{
    /// <param name="runs">The runs the grid shows; the filters and sort live on its default view.</param>
    /// <param name="chainNames">Every chain, in the sidebar's order: the Chain filter's values.</param>
    public RunsFilterViewModel(ObservableCollection<ScrapeRunViewModel> runs, IEnumerable<string> chainNames)
    {
        Chain = new ColumnFilterViewModel("Chain", chainNames);
        State = new ColumnFilterViewModel("Finished", Enum.GetNames<RunState>());
        View = CollectionViewSource.GetDefaultView(runs);
        View.Filter = IsShown;
        View.SortDescriptions.Add(new SortDescription(nameof(ScrapeRunViewModel.Id), ListSortDirection.Descending));
        Chain.Changed += (_, _) => View.Refresh();
        State.Changed += (_, _) => View.Refresh();
    }

    public ColumnFilterViewModel Chain { get; }

    public ColumnFilterViewModel State { get; }

    public ICollectionView View { get; }

    private bool IsShown(object item)
    {
        return item is ScrapeRunViewModel run && Chain.Allows(run.QsrName) && State.Allows(run.State.ToString());
    }
}
