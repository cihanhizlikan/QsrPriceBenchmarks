# Settled decisions

**What has already been decided, and must not be re-argued.** Each entry carries the decision, the evidence or
reason it rests on, and what would re-open it. A decision is re-opened by a new fact, never by an argument.
Entries are appended; a superseded one is struck through with the date and the entry that replaced it.

## The workflow is Yalbuz's (2026-10-24)

**Decision.** This repository is worked on the way Yalbuz is: worktrees opened by `/wp-start`, `/wp-verify` and
`/wp-finish` around every package, the commit body as the archive entry, one plan outside the repository, and an
unattended `/sweep`. The differences are listed in `working-agreement.md`.
**Re-opened by** a rule of Yalbuz's that measurably costs more here than it saves.
**The default branch is `main`**, as in Yalbuz (maintainer, 2026-10-26): `main` was created on origin from
`master`'s tip (`40b1742`), made the default, and `master` deleted. Until then this repository's branch was the
one difference the two working agreements had on it.

## The SQLite provider is chosen by the host, never by Core

**Decision.** `Core` references only `Microsoft.Data.Sqlite.Core`. `Cli` and `Ui` pin `bundle_winsqlite3` (the
SQLite that ships with Windows: no native binary shipped, no third-party CVE surface); `Tests` pins
`bundle_e_sqlite3` so the hermetic DB tests run on any platform. The reasoning is in the csproj comments.
**Re-opened by** a SQLite feature the hosts need that Windows' build lacks.

## ~~The database is the user's data~~ — superseded 2026-10-29 by *The program's shape outranks the database*

~~**Decision.** Schema changes ship a data-preserving migration; `Database` refuses a foreign SQLite file before
writing anything to it; views are dropped and recreated on every open so view fixes reach existing files. The
schema notes on `Database` record which unique keys look wrong and are not.
**Re-opened by** the database becoming a cache that can be rebuilt from a re-scrape — it is not today, because a
scrape run is a price observation at a moment in time.~~

## Analyzers are opted in one rule at a time; AnalysisMode=All and CA2000 are declined (2026-10-24)

**Decision.** `AnalysisMode` stays `Default` and the rules worth having are switched on individually in
`.editorconfig`. **Measured**: `AnalysisMode=All` over Core, Cli and Ui gave 349 sites, led by CA2007 (107 —
`ConfigureAwait(false)`, wrong for a WPF app whose continuations must return to the UI thread) and CA1062 (39 —
null guards that nullable reference types already check). CA2000 is off because it cannot see ownership
transfers (a browser context the session disposes), so it could not be held at zero.
**Re-opened by** a defect one of the declined rules would have caught.

## No UI Register yet; a capture harness instead (2026-10-25)

**Decision.** This repository does not keep Yalbuz's UI Register. It has one WPF window, no UI decision has been
argued twice, and a register nobody consults decays — the failure `handoff.md` and `completed-work.md` died of in
Yalbuz. What a register's `read` rows would need first is the ability to LOOK at the window, so that is what was
built: `UiTests/WindowCapture.cs` renders every tab to PNG against a throwaway database, gated on
`QSR_CAPTURE_DIR`. Sweep step 8 uses it.
**Re-opened by** the same UI decision being argued a second time, or a second window.

## The log reads the theme; the initials-badge palette stays in code (2026-10-25)

**Decision.** `Ui/LogLineColouriser` takes its five brushes from `Ui/Themes/Dark.xaml` (`BrushLightCyan`,
`BrushDimCyan`, `BrushText`, `BrushPurple`, `BrushBg3`), so the theme is the one source of the app's colours; a
missing key throws instead of falling back to a colour of its own. `Ui/ViewModels/ChainViewModel`'s initials-badge
fallback palette and its text colours stay in code: they are per-chain identity colours chosen from data, the same
kind of thing as `Core/Models/BrandPalette.cs`, not theme. **Measured**: changing only the theme's `ColorPurple`
turned the log's error line from magenta to orange in the window capture.
**Re-opened by** a second theme (a light one, say), which would make the badge text colours theme-dependent.

## The version moves with every change that ships (2026-10-25)

**Decision** (maintainer). `Version` in `Directory.Build.props` is bumped in the commit of every change that alters
the built assemblies or the MSI, sized patch / minor / major by `working-agreement.md`'s *Versioning* section — not
cut by hand at release time, which for 1.6.0 meant tracing 24 commits after the fact. Two choices inside it:
- **Changes that ship nothing do not bump** (docs, tests, `.claude/`, formatting), so every version number means a
  different installer. Re-opened by a need to version something other than the installer.
- **Every sweep step that corrects shipped code bumps**, rather than the sweep bumping once at the end, so `main`
  is never between versions. Re-opened by sweeps producing so many numbers that they stop meaning anything.

## A Step 2 run scrapes every tab a page shows, except the ones unticked (2026-10-28)

**Decision.** The UI's tab selection is read as EXCLUSIONS — the known tabs left unticked — not as the list to
scrape. Each page's own tab bar decides which tabs exist and in what order. **Measured**: on the maintainer's
database the first Arby's run visited 177 locations, discovered 12 tabs and stored 0 prices, because a fresh
database knows no tabs, the UI passed an empty selection, and the old code scraped exactly that list; the
"menu items from the next run on" hint described this as intended. A tab TG adds mid-run was likewise never
scraped until the run after. The scraper also used to read the stored list's FIRST tab from the server payload
whatever the page showed first; it now reads every tab from the live DOM in page order, the first one falling
back to the payload. `Tests/TgAllTabsTests.cs` asserts every tab of a live page per chain yields items and that
no tab repeats the previous one's products (red when the click is disabled).
**Re-opened by** a need to scrape only an explicit allow-list, e.g. a chain whose new tabs are noise.

## A tab no location READ this run showed is disabled, unread pages notwithstanding (2026-10-28)

**Decision** (maintainer's rule, the relaxation is the assistant's). A finished, non-resumed run that saw any tab
bar sets ACTIVE = 0 on every known tab no page it READ showed — never deleting it — and the next page that shows
it re-enables it, ticked. Until then a single unreadable page blocked all disabling, and over hundreds of live
pages one timeout is routine, so a vanished tab practically stayed forever. A tab disabled too early costs
nothing lasting: it is re-enabled on sight, and a disabled tab is never in the exclusion set, so it is scraped
the moment it reappears.
**Re-opened by** a tab that exists only on a few locations being disabled and re-enabled run after run.

## The program's shape outranks the database (2026-10-29)

**Decision** (maintainer). A schema change may discard existing databases instead of migrating them: "ideal
program shape at all costs; deleting the whole db and rescanning is acceptable". It replaces *The database is the
user's data*, re-opened by the maintainer accepting the cost that entry named: a scrape run is a price observation
at a moment in time, so a rescan restores today's prices but never the history `PREV_PRICE` / `PRICE_CHANGE`
compare against. What stays:
- **The app never deletes a database file on its own.** A file the current schema cannot use is refused with a
  plain message naming it; starting fresh is the user's act. (The assistant's addition, not the maintainer's
  words: a silent delete would take history the user may still want to export first.)
- **A session never deletes or alters the maintainer's own database file** unless told to; tests keep using
  throwaway databases.
- `Database` still refuses a FOREIGN SQLite file before writing to it — that guard protects other programs' data.
- Whether the existing migration code (`MigrateLegacySchema`, `MigrateSchema`, `DatabaseMigrationTests`) now goes
  is a package, not part of this decision.
**Re-opened by** a user other than the maintainer losing price history they relied on.

## A run is claimed by an open file handle, not by anything in the database (2026-10-29, WP-C1)

**Decision.** While a scrape works on a run it holds `<database>.run-<id>.lock` open with no sharing
(`Core/Db/RunLock.cs`); the run row is committed only once claimed and finished before release. Windows closes the
handle however the process ends, so a crash never leaves a run claimed. **Refused**: a flag or PID in the database
(a crash leaves it set; a lease stays stuck until expiry; PIDs are reused) and SQLite `locking_mode=EXCLUSIVE`
(blocks the app's own short connections and every reader). Per RUN, not per database, so two windows may still
scrape different chains. **Measured**: a `cmd` process holding the lock file, killed, left the file on disk and the
run claimable; with the claim made the file's existence instead, that test went red.
**Re-opened by** a database on a network share, where handle semantics differ — WAL already rules that out.
