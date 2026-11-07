using QsrPriceBenchmarks.Ui;
using QsrPriceBenchmarks.Ui.ViewModels;
using Xunit;
using static QsrPriceBenchmarks.UiTests.Dispatching;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// What the main window must be in once it has started. Drives the view model's
/// own <c>InitialiseAsync</c> — what <c>MainWindow.OnContentRendered</c> runs —
/// without showing the window, so an ordinary test run puts nothing on screen.
/// </summary>
public sealed class StartupTests
{
    private const string Stuck = "InitialiseAsync did not finish";

    [Fact]
    public void The_First_Chain_In_The_Sidebar_Is_Selected_After_Startup()
    {
        UserDatabaseFile userDatabaseBefore = UserDatabaseFile.State();
        bool hadSelectionBeforeStartup = true;
        ChainViewModel? first = null;
        ChainViewModel? selected = null;
        bool canRun = false;

        using (ThrowawayDatabase throwaway = new())
        {
            WpfThread.Run(() =>
            {
                MainWindow window = new();
                MainViewModel model = (MainViewModel)window.DataContext;
                // Before anything touches a database: the default is the user's own.
                model.DbPath = throwaway.Path;
                hadSelectionBeforeStartup = model.SelectedChain is not null;

                Task startup = model.InitialiseAsync();
                PumpUntil(() => startup.IsCompleted, Stuck);
                startup.GetAwaiter().GetResult();

                first = model.Chains.FirstOrDefault();
                selected = model.SelectedChain;
                canRun = model.RunCommand.CanExecute(null);
                window.Close();
            });
        }

        Assert.False(hadSelectionBeforeStartup, "the test must start from no selection, or it proves nothing");
        Assert.NotNull(first);
        Assert.Same(first, selected);
        Assert.True(canRun, "with a chain selected, Run must be available");
        Assert.Equal(userDatabaseBefore, UserDatabaseFile.State());
    }
}
