using System.Windows;
using System.Windows.Controls;
using QsrPriceBenchmarks.Ui.Dialogs;
using QsrPriceBenchmarks.Ui.ViewModels;

namespace QsrPriceBenchmarks.Ui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    // True while the log should stay pinned to the bottom. Set false the moment
    // the user scrolls up, and true again when they scroll back to the bottom —
    // so live log updates never yank the scrollbar away from what they're
    // reading, but still follow new output when they're already at the end.
    private bool _autoScrollLog = true;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        try
        {
            await _vm.InitialiseAsync();
        }
        catch (Exception ex)
        {
            App.LogCrash(ex, "OnContentRendered/InitialiseAsync");
            throw;
        }
    }

    /// <summary>
    /// Closing with chains still being scraped asks first: Yes cancels every run and closes (each run is finalized
    /// as it unwinds), No keeps the window open.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsAnyRunning)
        {
            MessageBoxResult answer = ThemedMessageBox.Show(
                "Chains are still being scraped. Cancel their runs and close?",
                "Close", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _vm.CancelAllRuns();
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Refresh the Scrape Runs list whenever that tab becomes active. Guards
    /// against selection-changed events bubbling up from inner selectors
    /// (DataGrid, ComboBox) by checking the event came from the TabControl.
    /// </summary>
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender))
        {
            return;
        }
        _vm.LocationsTabActive = LocationsTab.IsSelected;
        _vm.ScrapeRunsTabActive = ScrapeRunsTab.IsSelected;

        if (ScrapeRunsTab.IsSelected)
        {
            _vm.ReloadRunsCommand.Execute(null);
        }
        else if (LocationsTab.IsSelected)
        {
            _vm.RefreshLocations();
        }
    }

    private void LogScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        ScrollViewer scroller = (ScrollViewer)sender;

        // ExtentHeightChange == 0 means this fired because the USER moved the
        // scrollbar (no line was appended). Treat being within one line of the
        // bottom as "at the bottom" so a click on the down-arrow re-engages
        // following; scrolling up anywhere else disengages it.
        if (e.ExtentHeightChange == 0)
        {
            _autoScrollLog = scroller.VerticalOffset >= scroller.ScrollableHeight - 1.0;
            return;
        }

        // A line was appended (extent grew). Follow it only if the user hasn't
        // scrolled away from the bottom.
        if (_autoScrollLog)
        {
            scroller.ScrollToEnd();
        }
    }
}
