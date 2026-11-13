# UI Register

**The settled decisions about what QSR Price Benchmarks' window looks like and how it behaves, and the measurements
they rest on.** Adopted 2026-11-07 at the maintainer's request, on Yalbuz's model, so that UI choices stop drifting.
Unlike Yalbuz's, this register is a file in the repository: a UI change and the row it moves land in the same commit.

## How to use it

- **Check a UI decision against this file BEFORE taking it, not afterwards.** If a surface disagrees with a row, that
  is DRIFT, and the surface is what gets fixed. If a proposal disagrees with a row, it is an argument with a decision.
  Like `settled-decisions.md`, a row is re-opened by a new fact, never by an argument.
- **Revise this file in the SAME commit as a change that moves a rule.** A rule the maintainer reverses keeps both
  arguments in its row: the old one struck through, the new one dated. It does not get a second row.
- **Every row has three columns.** *Scope* is `window` (`Ui/MainWindow.xaml` and its view models), `dialog`
  (`Ui/Dialogs/ThemedMessageBox`) or `both`. *Enforced* is `build` (a named test or analyzer goes red on a violation)
  or `read` (only a person looking can catch it). *Settled* names the decision: who made it, when, and the WP id or
  commit subject that `git log --grep` finds.
- **The `read` rows are the half that decays**, and they are the sweep's checklist (step 8). A `read` row decays by
  being OVERTAKEN, not by being contradicted. Counts are the most fragile part, so they are re-measured first.
- **The pictures are rendered, never drawn.** `UiTests/WindowCapture.cs` renders every tab and both dialogs to PNG
  from the real XAML, so they cannot drift from it:

  ```bash
  QSR_CAPTURE_DIR="$(cygpath -w "$PWD")\\TestResults\\artifacts" dotnet test UiTests --no-build
  ```

  It writes `run-pipeline`, `locations`, `scrape-runs`, `scrape-runs-filtered`, `scrape-runs-filter-list` (a
  `Popup` is a window of its own, so it is saved separately), `dialog-confirm` and `dialog-error`. It uses a
  throwaway database and asserts the maintainer's own is untouched. **No test may pin a colour, a spacing value or a
  markup attribute**: a rule here about how something LOOKS is `read`, and it is checked by looking.

## Colour

| Rule | Scope | Enforced | Settled |
|---|---|---|---|
| **Every colour is defined in `Ui/Themes/Dark.xaml`** and read from it as a resource. `Transparent` is the one literal allowed elsewhere. | both | build | `ThemeColourTests`, 2026-11-07; before that, sweep 2026-10-24 step 8 moved seven literals into tokens |
| **Inside the theme, every colour is a named `Color` token** with a `Brush` of the same name, and the theme's own styles read the brushes. The token list is the whole palette. | both | build | WP-U7, 2026-11-13; `ThemeColourTests` |
| **Dark only.** There is no light theme, no toggle and no `prefers`-style switch. | both | read | Measured since the first commit and never asked otherwise. Re-opened by a request for a light theme |
| **The palette is cmd.exe's (legacy conhost)**, so the log reads exactly as the CLI prints it: `ColorLightCyan` `#00FFFF` (ANSI 96), `ColorDimCyan` `#00B3B3` (ANSI 2;96), `ColorText` `#FFFFFF`, `ColorPurple` `#FF00FF` (ANSI 95). | both | read | The theme's own header comment. Re-tinting one of the four changes the log's agreement with the console |
| **`ColorChromeCyan` (`#33C4CC`) is for UI furniture only, never log text**, so the furniture stays visually layered behind the log. | window | read | The theme's header comment |
| **The log takes its five colours from the theme** (`BrushLightCyan`, `BrushDimCyan`, `BrushText`, `BrushPurple`, `BrushBg3`). A missing key throws; there is no colour of its own to fall back on. | window | read | WP-U1, 2026-10-25. Proved by re-tinting `ColorPurple`: the error line followed it |
| **The initials-badge palette stays in code** (`Ui/ViewModels/ChainViewModel.cs`): a per-chain identity colour chosen from data, as the chain brand colours in `Core/Models/BrandPalette.cs` are. It is not theme. | window | build | settled-decisions.md *The log reads the theme; the initials-badge palette stays in code*, 2026-10-25. `ThemeColourTests` exempts that one file by name |
| **Message boxes are drawn in the app's theme**, never by Windows. `System.Windows.MessageBox` is Win32 chrome that no brush can reach. The crash handler alone falls back to the native box, because the failure it reports may be what stops a WPF window rendering. | dialog | read | 2026-10-26, *message boxes are drawn in the app's theme* |

**Measured 2026-11-13:** 21 `Color` tokens in `Dark.xaml`, each with a `Brush` of the same name. The last two,
`ColorDangerBg` and `ColorCellSelected`, were literals inside styles until WP-U7.

## Type, space and shape

| Rule | Scope | Enforced | Settled |
|---|---|---|---|
| **The window's unsized text is 12 px, WPF's default, and stays so.** `Dark.xaml` has no implicit `Window` style: one keyed to `Window` never reaches `MainWindow`, a subclass. Applying a working one would have enlarged every unsized text to 13, a redesign nobody asked for. | window | read | WP-U3, 2026-10-26. Captures before and after were diffed pixel by pixel and showed no change |
| **Segoe UI for the chrome, Consolas for data**: the runs grid, the log, and the progress and export readouts. | window | read | Measured; set in the control styles and at each readout |

**Measured 2026-11-07, not decided:**

- **Type sizes**: 10 (section labels and a card's status line), 11 (version badge, log footer), 12 (default, grid,
  log, card name, badge initials), 13 (tabs, dialog text), 14 (map tooltip title), 16 (app title) and 22 (dialog
  icon).
- **Corner radii**: 3 (funnel), 4 (buttons), 5 (initials badge), 6 (cards, panels, tab tops, popup, tooltip,
  progress track) and 9 (pills: the version badge and the location count).
- **Sizes**: buttons are at least 32 px tall, grid rows at least 34. The window opens at 1200 × 820 and shrinks to
  no less than 900 × 620. The header is 48 px and the sidebar 220.
- **Disabled controls**: opacity 0.4.

Each figure is a measurement, not a rule. It becomes one when somebody decides it.

## The window's parts

| Rule | Scope | Enforced | Settled |
|---|---|---|---|
| **The window opens with the sidebar's first chain selected**, so Run is enabled and the tabs, map and Locations header are filled from the start. | window | build | 2026-10-25, *the app starts with the sidebar's first chain selected*; `StartupTests` |
| **A sidebar card is an initials badge, the chain's name and its status line, and nothing else.** There is no logo (removed, with the column that stored it) and no progress bar: the cards' thin bars "look ugly" (maintainer), so progress shows on the status line and in the Run Pipeline tab's bar. | window | read | WP-D1 and 2026-10-29, *Step 1 reports its progress; the sidebar cards lose their bars* |
| **Each chain has its own log, progress, options, tab ticks and Cancel.** Run and Cancel act on the chain on screen. Closing the window while chains are scraping asks first. | window | build for the sessions, read for the close | WP-C2, 2026-10-29; `ChainSessionsTests` covers two chains running at once, each with its own log and Cancel |
| **Every tab a page shows is listed ticked; a disabled tab is hidden**, and comes back ticked when a page shows it again. | window | build | 2026-10-28, settled-decisions.md *A tab no location READ this run showed is disabled*; `ScrapeTabListTests` |
| **The runs grid draws column headers only**: no row-header column and no corner cell. A selected row shows as its cells' highlight. | window | read | Sweep 2026-10-25 step 8 |
| **The grid opens newest first, and its sort is shown**: ▲ or ▼ on the sorting column only, one column at a time. A click sorts ascending and the next click descending. There are two states and no "unsorted", because the default order is itself a sort on ID, which opens showing ▼. | window | build | Maintainer, 2026-10-30; `ScrapeRunsFilterSortTests` |
| **A header's title sits left and its icons right; the funnel holds the edge, with the sort arrow just inside it.** The funnel always shows, so it owns the edge. The arrow is HIDDEN, not collapsed, while another column sorts, so its empty slot falls between title and funnel and nothing slides when the sort moves. A column without a filter keeps its arrow at the edge. ~~The arrow outermost~~ was reversed the same day: it left the funnel one slot in beside an empty edge. | window | read | Maintainer, 2026-10-30, *the filter funnel holds the header's edge* |
| **The Scrape Runs filters live in the column headers, Excel-style**: Chain and Finished each have a funnel that opens a checkbox per value under "(Select all)", and the funnel lights while its filter hides rows. There is no filter row under the toolbar and no "Only the selected chain". | window | build | WP-U5, maintainer, 2026-10-29; `ScrapeRunsFilterSortTests` |
| **A filter list's options carry no sort arrow.** The popup sits inside the header, so it carries its own string template to override the header's. | window | read | Sweep 2026-11-07 step 10; `scrape-runs-filter-list.png` |
| **An icon is a drawn `Path`, never an icon-font glyph.** The implicit `TextBlock` style sets Segoe UI, which has no funnel, so a glyph draws a box. | both | read | WP-U5 |
| **Run is the primary action** (`ButtonRun`, accent cyan, SemiBold). Export takes the same style in each finished run's row. | window | read | Measured |
| **A destructive action is red (`ButtonDanger`) and asks first.** Delete and Delete Before each raise a Yes/No confirmation. **Esc and closing the dialog give the cancelling answer**, so dismissing a delete confirmation never deletes. Enter takes the first button. The labels are English, like the rest of the window, not the Windows language. | both | build for the answers, read for the colour | 2026-10-26, *message boxes are drawn in the app's theme*; `ThemedMessageBoxTests` |
| **An export reveals its file in the Explorer window already open on its folder**, and opens one only when none is. The reveal is the export's only success signal, so it stays. | window | read | Maintainer, 2026-10-30. No test, by decision: a test would open Explorer windows on the desktop |
| **Anything new on the window goes into a view model of its own.** `MainViewModel` sits at the `CA1506` coupling threshold (95), and `RunsFilterViewModel` is the pattern. | window | build | `CA1506`, measured 2026-10-29 at 96 when the filter went inline |

## Motion

**Measured 2026-11-07, not decided:** there are three animations, and none has a branch for Windows' *Show
animations* setting being off:

- **The progress fill pulses** in opacity, between 0.6 and 1.0 over 0.9 s.
- **The header logo becomes three equaliser bars** while any chain runs, on a 0.5 s cycle.
- **A ring pulses out from each district marker** that has locations, over 2 s.

Whether they should honour that setting is a plan row.
