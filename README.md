# QSR Price Benchmarks — v2 (Core + CLI + WPF)

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
Installer/  – WiX v5 MSI authoring (see build-installer.ps1)
```

## Build

```
dotnet restore
dotnet build
```

Playwright browser (one-time, after first restore — the live tests install it
themselves):
```
pwsh Core/bin/Debug/net10.0/playwright.ps1 install chromium
```

## Test

```
dotnet test                                   # everything, including live sites
dotnet test --filter "Category!=Integration"  # offline half only
```

The `Category=Integration` tests scrape the real chain websites and Tıkla
Gelsin, so they need the internet and can fail when a site changes.

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
Select a chain in the left sidebar, choose pipeline options,
click Run. The Scrape Runs tab handles run management and export;
the Locations tab maps where the chain's restaurants are.

## Installer (self-contained MSI)

To hand the app to someone who can't install a .NET runtime, build a
self-contained MSI that bundles .NET 10 **and** Chromium:

```powershell
pwsh ./build-installer.ps1
```

The single `Installer\bin\Release\QsrPriceBenchmarks-Setup.msi` installs with no
prerequisites on the target PC. See `Installer/README.md` for details.

## Architecture change from v1

`Action<string> log` → `IProgress<string>` across all Core methods.
Both CLI and UI construct a `Progress<string>` tailored to their output:
- CLI: wraps `Console.WriteLine` with ANSI colour formatting
- WPF: appends to `ObservableCollection<string>` on the UI thread
  (captured `SynchronizationContext` via `Progress<T>` handles dispatch)

`CancellationToken` added to all async Core methods — UI Cancel button
uses a `CancellationTokenSource`; CLI wires `Console.CancelKeyPress`.

## Logos

Chain logos are stored as `LOGO_BLOB` (nullable) in the `QSR` table.
When null, the UI shows a coloured initials badge (e.g. "BK", "PY").
The app currently has no control for storing a logo, so the badge is
what every chain shows.
