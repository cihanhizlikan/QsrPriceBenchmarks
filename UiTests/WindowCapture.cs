using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Ui;
using QsrPriceBenchmarks.Ui.ViewModels;
using Xunit;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// Renders <c>MainWindow</c> to PNG, one image per tab, so a person can LOOK at it.
///
/// <para>
/// <b>It makes no claim about how the window looks, and that is the point.</b> No
/// unit test can see a WPF layout, and a test may not pin a colour or a spacing
/// value (working-agreement.md), so the capture stops at producing the image. The
/// one thing it does assert is a requirement of the harness itself: <b>the user's
/// own database is never touched</b>. The view model's default path is the real
/// per-user database, and showing the window runs <c>InitialiseAsync</c> against
/// whatever <c>DbPath</c> holds — so the path is repointed at a throwaway database
/// BEFORE the window is shown, and the real file is checked afterwards.
/// </para>
/// <para>
/// <b>Gated on <c>QSR_CAPTURE_DIR</c>, and SKIPPED when it is unset</b>, which is
/// every ordinary run: a capture that did not happen must not read as one that
/// did. The suite therefore reports one skip, and that one is this. The variable's
/// value is the directory; point it at <c>TestResults/artifacts</c>, which is
/// gitignored. Showing the window means it appears on screen for a few seconds.
/// </para>
/// </summary>
public sealed class WindowCapture(ITestOutputHelper output)
{
    private const string DirectoryVariable = "QSR_CAPTURE_DIR";
    private const string Chain = "Burger King";

    [Fact]
    public void Every_Tab_Against_A_Throwaway_Database()
    {
        string into = Environment.GetEnvironmentVariable(DirectoryVariable)?.Trim() ?? string.Empty;
        Assert.SkipWhen(into.Length == 0, $"Set {DirectoryVariable} to a directory to capture the main window.");

        Directory.CreateDirectory(into);
        FileState userDatabaseBefore = FileState.Of(UserDatabasePath());
        string throwaway = Path.Combine(Path.GetTempPath(), $"qsr_capture_{Guid.NewGuid():N}.sqlite");
        List<string> written = [];
        try
        {
            Seed(throwaway);
            WpfThread.Run(() => Shoot(into, throwaway, written));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string file in new[] { throwaway, throwaway + "-wal", throwaway + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // best effort: a leftover temp file is harmless
                }
            }
        }

        Assert.Equal(userDatabaseBefore, FileState.Of(UserDatabasePath()));
        output.WriteLine($"Wrote {written.Count} capture(s) to {Path.GetFullPath(into)}:");
        foreach (string file in written)
        {
            output.WriteLine($"  {file}");
        }
    }

    /// <summary>
    /// A fresh database (schema and chains seeded by <see cref="Database.Open"/>)
    /// with three locations and one finished run, so the map and the runs grid
    /// have something on them.
    /// </summary>
    private static void Seed(string path)
    {
        using SqliteConnection conn = Database.Open(path);
        long qsrId = Repository.QsrId(conn, Chain);
        Repository.UpsertLocation(conn, "istanbul-avrupa", "besiktas", "bk-besiktas", qsrId);
        Repository.UpsertLocation(conn, "ankara", "cankaya", "bk-cankaya", qsrId);
        Repository.UpsertLocation(conn, "izmir", "konak", "bk-konak", qsrId);
        long runId = Repository.StartScrapeRun(conn, qsrId);
        Repository.FinishScrapeRun(conn, runId);
    }

    private static void Shoot(string into, string throwaway, List<string> written)
    {
        MainWindow window = new()
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
        };
        MainViewModel model = (MainViewModel)window.DataContext;
        // BEFORE Show: OnContentRendered initialises against whatever this holds.
        model.DbPath = throwaway;
        model.SelectedChain = model.Chains.First(c => c.Name == Chain);

        window.Show();
        Settle(window, () => model.Runs.Count > 0 && model.MapCells.Count > 0);
        written.Add(Save(window, into, "run-pipeline.png"));

        Select(window, "LocationsTab");
        Settle(window, () => model.MapCells.Count > 0);
        written.Add(Save(window, into, "locations.png"));

        Select(window, "ScrapeRunsTab");
        Settle(window, () => model.Runs.Count > 0);
        written.Add(Save(window, into, "scrape-runs.png"));

        window.Close();
    }

    private static void Select(Window window, string tabName)
    {
        TabItem tab = window.FindName(tabName) as TabItem
            ?? throw new InvalidOperationException($"MainWindow has no TabItem named {tabName}.");
        tab.IsSelected = true;
    }

    /// <summary>
    /// Pumps until <paramref name="ready"/> holds or the bound expires, on a
    /// monotonic clock. Expiring is not an error: the capture then shows whatever
    /// the window reached, which is more useful than no image.
    /// </summary>
    private static void Settle(Window window, Func<bool> ready)
    {
        long started = Stopwatch.GetTimestamp();
        do
        {
            DispatcherFrame frame = new();
            DispatcherTimer settled = new(
                TimeSpan.FromMilliseconds(50),
                DispatcherPriority.Background,
                (_, _) => { frame.Continue = false; },
                window.Dispatcher);
            settled.Start();
            Dispatcher.PushFrame(frame);
            settled.Stop();
        }
        while (!ready() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20));

        // One more turn so a bound collection that just filled has laid out.
        DispatcherFrame last = new();
        DispatcherTimer tick = new(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => { last.Continue = false; }, window.Dispatcher);
        tick.Start();
        Dispatcher.PushFrame(last);
        tick.Stop();
    }

    /// <summary>
    /// Over a ground painted from the window's own Background: a
    /// <see cref="RenderTargetBitmap"/> starts transparent, and a dark window
    /// composited on an image viewer's white looks like a contrast defect that is
    /// not there.
    /// </summary>
    private static string Save(Window window, string into, string file)
    {
        // The CONTENT's size, not the window's: ActualWidth/Height include the
        // title bar and borders, which a RenderTargetBitmap does not draw, so
        // sizing from them left a blank band along the bottom of every image.
        FrameworkElement content = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("MainWindow's content is not a FrameworkElement.");
        int width = (int)Math.Ceiling(content.ActualWidth);
        int height = (int)Math.Ceiling(content.ActualHeight);
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);

        DrawingVisual ground = new();
        using (DrawingContext context = ground.RenderOpen())
        {
            context.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(ground);
        bitmap.Render(content);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(into, file));
        encoder.Save(stream);
        return file;
    }

    /// <summary>The same path MainViewModel defaults to.</summary>
    private static string UserDatabasePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QsrPriceBenchmarks", Database.DefaultFileName);
    }

    /// <summary>Whether a file exists, and if so its size and last write.</summary>
    private readonly record struct FileState(bool Exists, long Length, DateTime LastWriteUtc)
    {
        public static FileState Of(string path)
        {
            FileInfo info = new(path);
            return info.Exists ? new FileState(true, info.Length, info.LastWriteTimeUtc) : default;
        }
    }
}
