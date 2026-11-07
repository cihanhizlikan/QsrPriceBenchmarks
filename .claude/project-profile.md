# Project profile — QSR Price Benchmarks

**What this file is.** The four workflow skills — `/sweep`, `/wp-start`, `/wp-verify`, `/wp-finish` — are written
to be copied unchanged between C# repositories. Everything they need to know that is true of ONE repository lives
here, as named keys, and the skills read it by key. **Copy the skills, not this file**: a new repository gets its
own profile, filled in from its own tree. Adopted from Yalbuz on 2026-11-07.

**An absent key is not a default.** A skill step whose key is missing, `none`, or names something that does not
exist reports that step **UNAVAILABLE (profile: `<key>`)** and does nothing else. It never reports "measured
clean" — a step that read nothing has measured nothing. A run that finds no profile at all does preflight only and
ends, listing the keys it would have needed.

**Values are facts about this repository, so they decay like any other.** The sweep's context-drift step checks
that every path below still resolves.

## Projects

| Key | Value |
|---|---|
| `solution` | `QsrPriceBenchmarks.sln` — the five C# projects. The WiX `Installer/` project is deliberately outside it and is built by the `Installer` publish profile (`Installer/README.md`) |
| `production_projects` | `Core/`, `Cli/`, `Ui/` |
| `test_projects` | `Tests/` (`Tests/Hermetic/` offline; the files at `Tests/` root scrape live sites) · `UiTests/` |
| `fixture_excludes` | none |
| `markup_globs` | `*.xaml` — markup constructs and binds types no `.cs` file names; remember `Ui/App.xaml`'s `StartupUri` and resource dictionaries |
| `co_located_products` | none |
| `host_types` | WPF desktop (`Ui/`); console (`Cli/`) — decides which global exception handlers must exist |
| `derivation_traps` | **SQL views and tables are reached by NAME inside strings** — `Repository` and `ExcelExporter` read views `Database` creates; a view named in a string is alive. **A chain in the registry (`Core/Models/ChainConfig.cs`) is configuration, not dead code**, even when no test names it |

## Conventions the build or suite already enforces

| Key | Value |
|---|---|
| `one_type_per_file` | **NOT enforced** — `Core/Models/Rows.cs` holds several types, so the type inventory is a declaration search: `grep -rnE '^\s*(public\|internal\|private\|file)?\s*(sealed \|static \|abstract \|partial \|readonly \|record )*(class\|record\|struct\|interface\|enum) [A-Z]'` |
| `namespace_matches_folder` | reviewer-carried — `.claude/context/structure.md`, *Folder*; nothing checks it |
| `line_endings` | index **LF**, working tree **CRLF** (`* text=auto`, `core.autocrlf` on); nothing in the suite checks them. After any `sed -i` under MSYS: `sed -i 's/\r*$/\r/' <file>`; `git diff --stat` showing every line changed is the tell of a flip |
| `enforced_guards` | no colour outside the theme dictionary (`Tests/Hermetic/ThemeColourTests.cs`); every package version central (`Directory.Packages.props`, `NU1008` an error); the schema version a database must carry (`SchemaVersionTests`) |
| `warnings_as_errors` | **no** — "enforced by the build" means the zero-warning baseline `/wp-verify` holds, not a failing build |
| `analyzer_config` | `.editorconfig` (every decline carries its reason inline), `Directory.Build.props` (`AnalysisLevel=10.0`, `AnalysisMode=Default`, Roslynator opted in one rule at a time), `Directory.Packages.props`, `global.json` |

## Documents the steps read

| Key | Value |
|---|---|
| `context_docs` | `CLAUDE.md`, `.claude/context/*.md`, `.claude/rules/*.md`, `.claude/skills/*/SKILL.md`, this file |
| `structure_doc` | `.claude/context/structure.md` — the placement rule (subject, reach, folder) and its *Worked examples* table |
| `naming_rule` | `.claude/rules/coding-style.md` rule 12, *call a spade a spade* |
| `reviewer_carried_rules` | `.claude/rules/coding-style.md` — the paragraph naming the rules no analyzer carries |
| `settled_decisions` | `.claude/context/settled-decisions.md` |
| `config_behaviours` | every check takes exactly one of three behaviours — **refuse to start** (a CLI exit code with a message, a window that will not open), **refuse the OPERATION with a reason** (the run does not start; the user is told why), or **report and change nothing**. An argument that would overwrite or delete a scrape run refuses; one that only narrows output reports |
| `config_coverage_test` | none — no test holds the configuration surface |
| `effective_config` | there is no configuration file. The surface is `Cli/CliOptions.cs` (arguments), `Core/Services/PipelineOptions.cs`, the chain registry in `Core/Models/ChainConfig.cs`, the `QSR_BROWSER_CHANNEL` environment variable and the `browser-channel.txt` marker `Core/Scraping/BrowserSession.cs` reads, and the UI's option view models |
| `never_a_correction` | **a schema change** — it bumps `Database.SchemaVersion`, so existing databases are refused and must be set aside (allowed since 2026-10-29, never unambiguous): always a package |

## The plan

| Key | Value |
|---|---|
| `plan_rule` | the file `CLAUDE.md` NAMES; never the newest under `~/.claude/plans/`. See *One plan, and which file it is* in `.claude/context/working-agreement.md` |
| `plan_known_path` | `C:\Users\ihizl\.claude\plans\qsr-price-benchmarks.md` (2026-11-07) |
| `history_paths` | the repository moved from `C:\Users\ihizl\source\repos\QsrPriceBenchmarks` to `D:\source\repos\QsrPriceBenchmarks`, so session history from before the move is under the OTHER project folder in `~/.claude/projects/` |
| `offsite_register` | none — anything this machine cannot check is a plan row |

## Verification

| Key | Value |
|---|---|
| `secrets_file` | none |
| `secrets_absent_effect` | none |
| `live_tests` | `Category=Integration` — `Tests/ChainWebsiteTests.cs`, `Tests/TgMenuTests.cs` and the other `Tests/` root files scrape real sites. Hermetic half alone: `dotnet test --no-build --filter "Category!=Integration"` |
| `build_output_language` | Turkish (`Hata`/`Uyarı`, `Başarılı`/`Başarısız`/`Atlanan`); diagnostic tokens stay English |
| `baseline_skips` | **1** — `UiTests/WindowCapture.cs` (`QSR_CAPTURE_DIR`); point it at `TestResults/artifacts`, which is gitignored |
| `warning_baseline` | **0** distinct site+code |

## The surfaces people read

| Key | Value |
|---|---|
| `user_docs` | `README.md`, `Installer/README.md`, and the strings the UI and CLI show (`--help` text, status and error messages) |
| `cultures` | `en` only |
| `wording_rules` | none beyond step 9's own: plain and short; cut a sentence that restates its heading. Every command, flag, path and file name in a README must exist today |
| `ui_register` | none — declined 2026-10-25 (`settled_decisions`, *No UI Register yet*) |
| `run_command` | none safe for a session: the app opens the maintainer's OWN database under `%LOCALAPPDATA%\QsrPriceBenchmarks`, which a session never touches unasked. Use `render_harnesses` |
| `render_harnesses` | `UiTests/WindowCapture.cs` — every tab against a throwaway database (it asserts the user's own is untouched): `QSR_CAPTURE_DIR="$(cygpath -w "$PWD")\\TestResults\\artifacts" dotnet test UiTests --no-build` |
| `theme_rules` | `Ui/Themes/Dark.xaml` is the one colour source (enforced, see `enforced_guards`); chain brand colours in `Core/Models/BrandPalette.cs` are data, and the initials-badge palette in `Ui/ViewModels/ChainViewModel.cs` stays in code by settled decision |
