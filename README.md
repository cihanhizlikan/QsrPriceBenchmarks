# QSR Price Benchmarks — v2 (Core + CLI + WPF)

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

Playwright browser (one-time, after first restore):
```
pwsh Core/bin/Debug/net10.0/playwright.ps1 install chromium
```

## Usage

**CLI** — same contract as v1:
```
qsr --qsr burger-king               # full run
qsr --qsr popeyes --geocode-rematch
qsr --qsr arbys --export-since 5
qsr --list-scrape-runs
qsr --delete-scrape-run 3
```

**UI**
```
qsr-ui
```
Select a chain in the left sidebar, choose pipeline options,
click Run. The Scrape Runs tab handles run management.

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
Use the "Upload Logo" button in the UI to store a PNG/JPG per chain.
