# QSR Price Benchmarks

Scrapes quick-service-restaurant chains' own websites and their Tıkla Gelsin
listings, matches the two sets of locations, keeps every scrape run in a SQLite
database, and exports price comparisons to Excel.

## Solution structure

```
QsrPriceBenchmarks.sln
Core/       – class library: all scraping, DB, export, matching logic
Cli/        – console exe  (AssemblyName: qsr)
Ui/         – WPF exe      (AssemblyName: qsr-ui), net10.0-windows
Tests/      – xUnit tests  (hermetic unit tests + live integration tests)
UiTests/    – xUnit tests that need a real WPF window, net10.0-windows
Installer/  – WiX v5 MSI authoring (built by the Ui project's Installer publish profiles)
```

## Build

```
dotnet restore
dotnet build
```

The scrapers drive a Chromium build Playwright manages. Nothing needs installing by
hand: run the live tests once and they install it into your per-user Playwright cache,
which a development build of the app uses too:
```
dotnet test --filter Category=Integration
```

## Test

```
dotnet test                                   # everything, including live sites
dotnet test --filter "Category!=Integration"  # offline half only
```

The `Category=Integration` tests scrape the real chain websites and Tıkla
Gelsin, so they need the internet and can fail when a site changes.

One test is always reported as skipped: the window capture in `UiTests/`,
which renders every tab of the app to PNG so a UI change can be looked at. It
runs only when the `QSR_CAPTURE_DIR` environment variable holds an absolute
path to write the images into (the repository's `TestResults\artifacts` is
gitignored), and it works against a throwaway database, never yours. With the
variable set, run:
```
dotnet test UiTests
```

## Usage

**CLI** (`qsr --help` lists every option):
```
qsr --chain burger-king               # full run
qsr --chain popeyes --geocode-rematch
qsr --chain arbys --export-since 5
qsr --list-scrape-runs
qsr --delete-scrape-run 3
```

Maintenance commands (`--list-scrape-runs`, `--delete-scrape-run`,
`--delete-scrape-runs-before-id`) run alone and cannot be combined.

**UI**
```
qsr-ui
```
Select a chain in the left sidebar, choose its options, click Run. Each chain
runs on its own, with its own log, progress bar and Cancel: switch to another
chain mid-run to start or watch it too, and its card in the sidebar shows how
it is doing. Chains share Tıkla Gelsin and Google politely: two chains scrape
Tıkla Gelsin at once and any more take turns, and Google takes no more lookups
from several chains than it did from one. The Scrape Runs
tab handles run management and export; the Locations tab maps where the
chain's restaurants are.

## Your database

Every scrape run is kept in the SQLite file you choose (the DB box; by default
one in your local app-data folder). A new version that changes the database's
shape cannot open an older file: it asks once whether to start a new database
there, and on Yes renames the old file aside (`<name>.v<version>-<timestamp>.sqlite`)
— never deleting it — so its runs can still be exported by the version that
made them.

## Installer (self-contained MSI)

To hand the app to someone who can't install a .NET runtime, build a
self-contained MSI that bundles .NET 10 **and** Chromium. In Visual Studio:
right-click **QsrPriceBenchmarks.Ui → Publish**, choose the **Installer**
profile, and press **Publish**. From a terminal:

```
dotnet publish Ui -p:PublishProfile=Installer
```

The single `Installer\bin\Release\QsrPriceBenchmarks-Setup.msi` installs with no
prerequisites on the target PC. The **InstallerSystemEdge** profile builds a
much smaller MSI that uses the PC's Microsoft Edge instead of bundling Chromium.
See `Installer/README.md` for details.

## Architecture

Progress is `IProgress<string>` on every Core method.
Both CLI and UI construct a `Progress<string>` tailored to their output:
- CLI: wraps `Console.WriteLine` with ANSI colour formatting
- WPF: appends to `ObservableCollection<string>` on the UI thread
  (captured `SynchronizationContext` via `Progress<T>` handles dispatch)

Every async Core method takes a `CancellationToken` — each chain's Cancel button
uses its own `CancellationTokenSource`; CLI wires `Console.CancelKeyPress`.

