# Structure

## Solution layout

| Project | Folder | Output | Role |
|---|---|---|---|
| `QsrPriceBenchmarks.Core` | `Core/` | library, `net10.0` | everything that is not hosting: scraping, matching, the database, the export |
| `QsrPriceBenchmarks.Cli` | `Cli/` | `qsr.exe` | console host: parses arguments, wires `Console.CancelKeyPress`, prints coloured progress |
| `QsrPriceBenchmarks.Ui` | `Ui/` | `qsr-ui.exe`, `net10.0-windows`, WPF | desktop host: MVVM over the same `Core` calls |
| `QsrPriceBenchmarks.IntegrationTests` | `Tests/` | xUnit | `Tests/Hermetic/` runs offline; the files at `Tests/` root scrape live sites |
| `QsrPriceBenchmarks.Installer` | `Installer/` | WiX v5 MSI | harvests the self-contained publish of `Ui`; driven by `build-installer.ps1` |

`Directory.Build.props` carries the one product version every project — the MSI included — reads.

## Core, by folder

- `Db/` — `Database` (open, guard against a foreign file, schema, migrations, views) and `Repository` (every
  query). The SQLite *provider* is chosen by each host, never by `Core`; the csproj comments say why.
- `Scraping/` — the pipeline's steps: `ChainCrawler` (chain website), `TgScraper` (Tıkla Gelsin),
  `GeocodeAndMatchStep` with `Geocoder` and `Matcher`, and the parsers and browser plumbing they share.
- `Services/` — `PipelineRunner`, the single entry point both hosts call, and its option and progress types.
- `Export/` — `ExcelExporter`.
- `Models/` — chain configuration and row types.
- `Util/` — pure helpers: text normalisation, price parsing, log markup and its console colouring.

**Progress is `IProgress<string>` and cancellation is a `CancellationToken` on every async `Core` method**, so
neither host is known to `Core`.

## The placement rule

**A type lives in the project and folder its SUBJECT belongs to, and may reach only what that project may reach.**

- *Subject* — `Core` holds anything both hosts would otherwise duplicate; a host holds only what is about that
  host (argument parsing, WPF converters, view models). A type named for one host's concern sitting in `Core` is
  misplaced, and so is pipeline logic sitting in a host.
- *Reach* — `Core` references no host, no WPF and no SQLite bundle. `Cli` and `Ui` reference `Core` and never
  each other. `Tests` may reference `Core` and `Cli`.
- *Folder* — namespace follows folder. A folder that repeats its project's name (`Cli/Cli/`) adds a word that
  rules nothing out.

### Worked examples

| Case | Falls | Why |
|---|---|---|
| `ConsoleColouriser` in `Core/Util` | Core, provisionally | it turns `LogMarkup` into ANSI, which only the CLI prints — but `Tests` exercises it, and moving it needs the sweep's step 1 to read its callers |
| `LogLineColouriser` in `Ui/` root | Ui | WPF-only rendering of the same markup |
| `TurkeyMap` in `Ui/ViewModels` | open | it is geometry data, not a view model |

This table is where an open case is settled; add a row when one is.
