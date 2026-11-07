---
name: sweep
description: QSR Price Benchmarks' sequenced quality audit — structure, test health, dependencies and the installer, dead code, robustness and input validation, maintainability, context-document drift, and what users read and see. It runs UNATTENDED: it never stops and never asks, merging each step and writing anything that needs a decision into the plan instead. The maintainer starts it and reads the report when they return.
disable-model-invocation: true
---

# The sweep

**One audit, eight steps, in an order that is itself the deliverable.** It corrects what is unambiguous, merges
each step, and hands everything else to the plan as a work package.

**This skill is THIS repository's own** (2026-11-07). It began as Yalbuz's portable core reading a profile file; the
maintainer chose a sweep tailored to each C# project and kept in that project, so every fact below is written in
place and nothing is copied back. A shape that would help another repository is carried there by hand.

**What this is not.** `csharp-refactor-harden` and `csharp-refactor-streamline` own LOOPS — one rule ID standing
over many sites, each triaged take-or-decline — and a robustness or maintainability finding of that shape is
handed to them. **The line is not a count**: a step CORRECTS a site whose repair is unambiguous and whose subject
is its own; the moment a finding needs a POLICY over sites it is a loop however few sites it has.
`csharp-refactor-modularize` owns anything crossing a project boundary. All three are plugin skills (under
`anthropic-skills`); qualify the name rather than concluding the skill is gone.

## The repository, in the terms the steps use

- **Production**: `Core/` (library, `net10.0`), `Cli/` (`qsr.exe`), `Ui/` (`qsr-ui.exe`, WPF). **Tests**: `Tests/`
  (`Tests/Hermetic/` offline; the files at `Tests/` root scrape live sites) and `UiTests/` (a real WPF window on one
  STA dispatcher, `WpfThread`). **Installer**: `Installer/`, WiX, deliberately OUTSIDE `QsrPriceBenchmarks.sln` and
  built only by the `Installer` publish profile.
- **The live tests** are `Category=Integration` (`Tests/ChainWebsiteTests.cs`, `Tests/TgMenuTests.cs` and the other
  `Tests/` root files). They scrape chain websites and Tıkla Gelsin, so a red there can be the SITE. The hermetic
  half alone: `dotnet test --no-build --filter "Category!=Integration"`.
- **Already enforced, so never re-detected**: the zero-warning build (`/wp-verify`; warnings are not errors, the
  baseline is 0 site+code); no colour outside `Ui/Themes/Dark.xaml` (`Tests/Hermetic/ThemeColourTests.cs`); every
  package version central (`Directory.Packages.props`, `NU1008` an error); the schema version a database must carry
  (`SchemaVersionTests`); the SDK band (`global.json`).
- **Analyzer configuration**: `.editorconfig` (every decline carries its reason inline), `Directory.Build.props`
  (`AnalysisLevel=10.0`, `AnalysisMode=Default`, Roslynator opted in one rule at a time).
- **Never a correction, always a package: a schema change.** It bumps `Database.SchemaVersion`, so existing
  databases are refused and must be set aside — allowed since 2026-10-29, never unambiguous.
- **The maintainer's own database** is under `%LOCALAPPDATA%\QsrPriceBenchmarks`. A sweep never starts the app
  against it and never opens it; the window is looked at through `UiTests/WindowCapture.cs`, which uses a
  throwaway database and asserts the user's is untouched.
- **Build output is Turkish** (`Hata`/`Uyarı`, `Başarılı`/`Başarısız`/`Atlanan`); diagnostic tokens stay English.
- **Line endings**: index LF, working tree CRLF (`* text=auto`, `core.autocrlf` on). After any `sed -i` under MSYS,
  `sed -i 's/\r*$/\r/' <file>`; `git diff --stat` showing every line changed is the tell of a flip. Make and unmake a
  multi-line break with the Edit tool, never `sed` or `perl -0pi`.
- **Session tooling**: no Python on this machine — use `node`. A Bash heredoc strips backslashes inside a script, so
  write a script with the Write tool into the scratchpad and run it from there.

## Invocation

**The maintainer starts a sweep; a session never does** — hence `disable-model-invocation: true`. Starting one is a
decision to spend a session auditing, and work is ISSUED, never taken. A session may propose one, with a reason.

## It runs UNATTENDED, and that is the whole design

**A sweep is started when the maintainer is leaving and expects to come back to finished work.**

- **NEVER stop to ask.** Not for a decision, a preference, permission, or to report progress. Do not call
  `AskUserQuestion`; do not end a turn with a question. There is nobody there, so a question is the run halting for
  hours and delivering nothing.
- **Anything that WOULD have been a question is written into the plan and SKIPPED**: what was measured, the
  options, which one is recommended and why — then move on.
- **Merge, delete and continue.** Each step's branch is merged into `main` and deleted by the sweep itself before the
  next step opens. **This is an explicit exception** to *merging is the maintainer's*, *a merge instruction is
  per-moment* and *work whose choices have not been seen is not merged*, granted for this skill by the maintainer.
  **Its limit is the unambiguity bar**: a step CORRECTS when the repair is unambiguous and the suite proves it, and
  hands over everything else. `/wp-finish`'s "Report, and stop" is covered too — a step uses that skill for the
  commit body and the size cap, then keeps going.
- **Never `git push`.** A session never pushes; the maintainer pushes after reading the report.
- **Report only at the END**, in one message covering every step.

### Ending a run is allowed; WAITING is not

A sweep may **finish early** — a red baseline, a tree that will not build, a session out of room. Record why in the
plan, say so in the report, exit. It may never sit there having asked something.

### The ways a run goes wrong mid-flight, and the one legal move for each

- **An action is REFUSED** (a permission classifier declines a delete, a navigation). Treat it as a BLOCKED
  detector: the exact refused command into the plan, carry on without it. **Never route around a refusal** by
  re-issuing it in another form. The one exception: a command refused for breaking a STATED rule here (a glob
  `rm -rf` where literal names are required) is replaced by the form the rule requires. **Retrying a FAILURE is not
  routing around a refusal**: a `git worktree remove` that ended in `Permission denied` on an empty directory is
  retried by the same literal path.
- **The suite goes RED inside a step.** The step does not merge. **Capture the failing test NAME before re-running
  anything**, then run it isolated several times:
  - fails isolated and the step's changes touch its subject → revert the step's changes, hand the finding over;
  - passes every isolated run → load-sensitive; re-run the full suite once, and if green the step merges and the
    test becomes a plan row carrying the captured message;
  - fails isolated and the step did not touch it → the tree moved under the run; that ends it as a red baseline does;
  - **it is a live test** and the step's diff cannot reach what it scrapes → the site changed; the step merges on a
    green hermetic half, and the test's name and message go into the plan.
- **The window capture times out or cannot open a window** (no interactive desktop). Step 8's look is UNAVAILABLE
  for this run, named once with the exact command. Never call a step clean that rendered nothing.
- **Plan mode switches on mid-run.** No step can continue: commit any uncommitted work on its branch and leave it
  UNMERGED, record plan mode as the reason the run ended, deliver the report, end.
- **The session is running out of room.** The likeliest ending, and not a failure. Finish the step in hand rather
  than opening the next, and write into the plan which step stopped it and what the next one was.

### How a finding reaches the plan without blocking

1. **The plan is `C:\Users\ihizl\.claude\plans\qsr-price-benchmarks.md`** — the file `CLAUDE.md` names; never the
   newest file under `~/.claude/plans/`, which holds other projects' plans. If it does not exist, search
   `~/.claude/file-history/` for its first line and the old project folder in `~/.claude/projects/` (the repository
   moved from `C:\Users\ihizl\source\repos\QsrPriceBenchmarks` to `D:\source\repos\QsrPriceBenchmarks`). **Still not
   found: the run continues and the report's FIRST line says "plan not found"**, naming every place searched. Never
   create a fresh plan and present it as the plan.
2. **APPEND to the plan file as each step ends**, under one heading for the run — durable if the session dies, and
   an append cannot silently overwrite a concurrent write the way a rewrite can (the plan is outside git).
3. **Deliver the final report, THEN revise the plan wholesale** — retiring what landed, after reconciling against
   `git log --oneline` and `git worktree list`. Every row appended this run must survive the revision. The plan
   carries no commit hashes and no test counts.

---

# How a run works

## The order is the deliverable, and a step ends in a merge

**A step fixes what it can and hands over what it cannot; the line is the step's own subject.** A defect inside it
is corrected on that step's branch and merged before the next step opens; anything reaching past it is a work
package, however small. The order is real work: structure moves files that dead code and maintainability would
otherwise write findings against. **A step that produced only prose has not finished** — either it found nothing
and says so, or it left work behind.

## Scope: what changed since the last sweep

- **The baseline**: `git log --grep='^merge: sweep ' -1 --format='%H %ad %s' --date=short`. Report it. No marker →
  the scope is the whole tree, and the report says so.
- **Changed files**: `git diff --name-only <baseline>..HEAD`.
- **Whole tree regardless**: step 2's repeated suite runs, step 3 (dependencies age without a commit), step 7's path
  check, and any step whose detector is newly LIVE — a rule switched on since the baseline has never seen old code.

## Preflight

1. **Clean tree, green build, green suite** — in `.claude/worktrees/verify-main` (`git -C
   .claude/worktrees/verify-main checkout --detach main`), never the primary checkout, where a running Debug
   `qsr-ui.exe` locks `Ui\bin\Debug`. **Not green → the run ENDS**: write what is broken into the plan, report, exit;
   never repair the baseline. **A red that is only live tests on a green hermetic half is the site**: re-run it once,
   record it by name in the plan, and go on. **One skip is the baseline** — `UiTests/WindowCapture.cs`, gated on
   `QSR_CAPTURE_DIR`.
2. **Detector liveness.** Every detector a step names is **proven to emit a value here, by running it**, and lands
   in one state: **LIVE** (ran, parseable value), **UNAVAILABLE** (did not run here; the exact command), or
   **BLOCKED** (should work, failed for a fixable repository reason; into the plan, carry on).

   **An ANALYZER RULE is a detector, and a rule that is off reads exactly like a rule at zero.** Read its effective
   severity in `.editorconfig` first. A rule not configured on is UNAVAILABLE until probed: in a THROWAWAY
   worktree, enable it at `warning`, add a file that breaks it, build, confirm the ID appears, discard. **Probe the
   SHAPE the rule documents** — CA1065 permits `InvalidOperationException` in a getter; RCS1236 fires on
   `if (!(e is X)) throw;` but not on `if (e is X) return; throw;`. The robustness rules held at warning
   (`CA1031`, `CA2016`, `CA2201`, `CA2008`, `CA1065`, `CA2219`, the Security category, `RCS1236`, `RCS1075`) and the
   complexity pair (`CA1502`/`CA1506`, proven by lowering their thresholds in a temporary
   `Core/Directory.Build.targets`) are each probed once per run.

   **The NuGet audit is a detector**: `dotnet list package --vulnerable --include-transitive`, `--deprecated` and
   `--outdated`, over the solution AND `Installer/QsrPriceBenchmarks.Installer.wixproj` (outside the solution).
   **Coverage**: `dotnet test --collect:"Code Coverage;Format=cobertura"` (the Microsoft collector the test SDK
   carries; it covered both test projects on 2026-11-07). **`CA2000` is declined by settled decision** — read only
   by enabling it in a throwaway worktree, never from its silence.

   **An inferred zero is not a measurement, and neither is an unexamined non-zero.**

## Derive, never cache

**Derive every list at the start of every run, from the tree, and write none down** — type counts, entry points,
member lists. Every cached list decays. The shapes here that look DEAD and are alive:

- **XAML.** A command, converter, view model property or resource reached only from `*.xaml` — `Ui/App.xaml`'s
  `StartupUri` and merged dictionaries, `Ui/Themes/Dark.xaml`'s implicit styles and templates, `{Binding}` paths, the
  window's `BindingProxy`. A `.cs`-only count calls all of it dead. The reverse holds too: a member nothing binds
  is dead even though WPF *could* bind it (`OpenUrlCommand`, removed 2026-11-07).
- **SQL by name.** `Repository` and `ExcelExporter` read views and tables `Database` creates, named inside strings; a
  view named in a string is alive.
- **The chain registry** (`Core/Models/ChainConfig.cs`) is configuration, not dead code, even when no test names a
  chain.
- **xUnit discovery.** Test classes and fixtures (`PlaywrightFixture`, `WpfThread`) are reached by the framework;
  filter test classes out before reading anything.
- **Several types per file.** `Core/Models/Rows.cs` holds many, so the type inventory is a declaration search, not
  the file listing: `grep -rnE '^\s*(public|internal|private|file)?\s*(sealed |static |abstract |partial |readonly |record )*(class|record|struct|interface|enum) [A-Z]'`.
- **Read what the matches ARE, not how many there are** — a doc comment naming a type pads a count without moving
  the fact.

## What a finding looks like

**Hand over a DECISION, never an inventory.** Read each site, mark it take-or-decline with its reason, and lead
with the count that SURVIVED. Each triaged item lands in exactly one place:

- **Corrected in this step** — inside the step's subject, the repair unambiguous, the suite proves it. The commit
  body is the record.
- **A work package in the plan** — needs a judgement the sweep cannot make, reaches outside the step, or needs a
  machine or network this session lacks.
- **A settled decision** (`.claude/context/settled-decisions.md`) — refused on evidence, with the measurement and
  what would re-open it.

**When it is not obvious, it is a package rather than a correction.** Narrowing scope to avoid a decision counts as
deciding. A package carries **What** (one sentence), **Where** (a path and line that resolve today), **Why it is not
the obvious thing**, **What would falsify it**, and **Origin: `finding`, Status: not yet committed to build**. Those
five fields are for a package, never a commit body, which `/wp-finish` caps at about 1,500 bytes.

**A measured "nothing here" is a complete deliverable**: no branch, no plan row, its record is its report line.
**Every number states what produced it** — which projects, which mode, which scope.

---

# The eight steps

## 1. Structure and naming
**First, because every later finding is written against the tree this step would move.**

- **Placement**: `.claude/context/structure.md`'s rule — subject, reach, folder — read against its own *Worked
  examples* table, over the types in scope. `Core` references no host, no WPF and no SQLite bundle; `Cli` and `Ui`
  never reference each other; namespace follows folder (nothing checks it). A type named for one host sitting in
  `Core`, or pipeline logic in a host, is misplaced. A settled case gets a row in that table.
- **Naming**: `.claude/rules/coding-style.md` rule 12 — a name a reader would have to open the file to understand.
- **Every `Core` async method takes a `CancellationToken` and reports through `IProgress<string>`** (structure.md);
  a new one that does not is this step's.

## 2. Test-suite health
**Second, deliberately: every step after this leans on the suite.** Over `Tests/` and `UiTests/`.

*The rule it enforces*: a test asserts a requirement, never a preference — *would a redesign nobody would call a
regression turn this red?*

- **Cannot fail by construction** — no assertion (an expression-bodied test or a does-not-throw test is not this);
  the only assertion inside a loop that can iterate zero times; a literal compared with itself.
- **A preference-assertion** — a hex colour, a pixel value, a markup attribute. `ThemeColourTests` is the exception:
  it asserts WHERE colours live, not what they are.
- **An unbounded wait** — a dispatcher pump, a `Task.Wait`, a Playwright call with no timeout or token.
- **A name that no longer describes the body** — the tell for a test edited until it passed.
- **Load-sensitive tests** — run the WHOLE hermetic half and `UiTests` at least three times; a test that fails once
  is captured by name and run isolated ten times. Passes isolated, fails under load → a timing assumption, handed
  over with its message. Re-check any load-sensitive rows the plan carries first. **Never the live tests** — they
  are intermittent because the sites are.
- **A database test that "re-opens" a file** must open it at a path this process has not opened
  (`TestData.MovedAsIfRestarted`): `Database.Open` prepares a file once per process, so a same-path re-open skips
  what it claims to test.
- **Uncovered production code** (preflight's coverage). An uncovered branch in CHANGED code is a missing test; an
  uncovered member with no test caller goes to step 4 as a candidate. Live-only paths (the scrapers' network
  branches), the CLI's console shell and XAML verified by rendering are not findings.

*Then revert-confirm-red the survivors*, each against the ACTUAL defect it claims to catch, the break made and
unmade with the Edit tool.

## 3. Dependencies and the installer
**After test health, because a version bump changes runtime behaviour. Before every step that reads an analyzer**,
because Roslynator or an SDK bump changes what steps 5 and 6 measure. Run from the step's worktree:
`dotnet list package` restores, rewriting `obj/`.

- **Vulnerable or deprecated** — a fixed version in the same major is a correction; otherwise a package.
- **Outdated** — a patch or minor within the current major is a correction, taken together in one step; a MAJOR is a
  package naming its breaking-change notes. `Directory.Packages.props`' header comment states the rule and the
  pins' reasons; keep it true.
- **The SQLite provider is chosen per host** (settled): `Cli` and `Ui` ship `SQLitePCLRaw.bundle_winsqlite3`, the
  tests use `bundle_e_sqlite3`. **The two bundles must sit on the SAME `SQLitePCLRaw.core` major** that
  `Microsoft.Data.Sqlite.Core` was built against — read `Tests/obj/project.assets.json` for the resolved
  `SQLitePCLRaw.core`, never the pin alone. `bundle_winsqlite3` has no 3.x, so a `bundle_e_sqlite3` 3.x "update" is
  never a correction.
- **Microsoft.Playwright** — its version decides the Chromium revision the MSI bundles. A Playwright bump is proven
  by the full suite (the live tests drive the new browser) AND by building the MSI —
  `dotnet publish Ui/QsrPriceBenchmarks.Ui.csproj -p:PublishProfile=Installer`, never `build-installer.ps1` — and
  confirming the `chromium-<rev>` folder in the publish output matches the new version.
- **WiX** (`WixToolset.Sdk` on the wixproj's `Sdk=` attribute, `WixToolset.UI.wixext`) is outside the solution, so
  audit it explicitly. Any WiX change is proven by building the MSI; a major is always a package needing an
  install-and-uninstall check on this machine.
- **The SDK** — `global.json` pins the feature band with `latestPatch`; a new feature band is a package (it moves
  every analyzer count).

## 4. Dead code
Needs the net step 2 just checked. **Refactor residue is the subject** — a helper left behind by a change, a seam
nothing uses, a name that stopped being true.

**The MAIN pass is member-level, production references only**: count references in `Core/`, `Cli/`, `Ui/` (`.cs`
AND `.xaml`) separately from `Tests/` and `UiTests/`. **A production member whose only caller is a test is dead
production code** — unless it is a deliberate test SEAM, which says so in its name or doc comment. Read the name and
the doc comment before the count, then put every candidate through *Derive, never cache*. The type-level pass is the
cheap second pass. **Signature extraction**: take the FIRST identifier followed by `(` on the declaration line — a
tuple return type defeats a last-token pattern.

**Then what the removal orphaned** — the configuration surface, which here is code, not a file:
`Cli/CliOptions.cs` (arguments and `--help`), `Core/Services/PipelineOptions.cs`, the chain registry, the
`QSR_BROWSER_CHANNEL` variable and the `browser-channel.txt` marker `Core/Scraping/BrowserSession.cs` reads, and the
UI's option view models. An option nothing reads any more is removed with its help text (a removed CLI option is a
MAJOR bump).

## 5. Robustness and input validation
The audit half of `csharp-refactor-harden`. **First read the build**: the robustness rules preflight proved LIVE
start at zero, so a non-zero is new and the build names it. **Then the shapes no analyzer catches:**

- **Exceptions that are lost.** A discarded task (`_ = RebuildMapAsync()` and the like in `Ui/`) — `VSTHRD110`
  ignores the discard deliberately. For each, read what the method CATCHES: everything else lands in a task nobody
  observes. `Task.Run` with a dropped result is the same shape.
- **Global handlers.** `Ui/App.xaml.cs` registers `DispatcherUnhandledException`,
  `AppDomain.CurrentDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`, each writing the crash
  log; a new host path that bypasses them is a finding. **The CLI registers no `AppDomain` handler by settled
  decision** — its `Main` boundary exits 1 with the reason.
- **External calls with a bounded failure path** — every Playwright navigation and wait has a timeout and the run's
  token; every `HttpClient` call (`TgScraper`) a timeout; the shared browser's lock (`BrowserSession`) a bound. An
  `HttpClient` timeout throws `TaskCanceledException`, which `catch (HttpRequestException)` does not take.
- **A broad catch outside a genuine boundary**, a missing cancellation check in a scrape loop, input reaching SQL
  unparameterised or a file path unvalidated.
- **Exception filters over catch-and-retest** (coding-style rule 9): `RCS1236` catches `if (!(e is X)) throw;` but
  not `if (e is X) return; throw;` — search for the second spelling. A log-and-rethrow is not a retest.
- **Input validation design** — every check takes exactly one behaviour: **refuse to start** (a CLI exit code with a
  message, a window that will not open), **refuse the OPERATION with a reason** (the run does not start; the user is
  told why), or **report and change nothing**. **The behaviour is chosen by what is LOST, never by how wrong the
  value looks**: an argument that would overwrite or delete a scrape run refuses; one that only narrows output
  reports. Read the code that decides — `CliOptions.Parse`, `PipelineRunner`'s option checks, the view models'
  `CanExecute` — never a sample or a help string.
- **A missing handler or guard is a correction only where the project already has the path to route it to** (the
  crash log, the progress reporter, an exit code); otherwise a package.

## 6. Maintainability and performance
The audit half of `csharp-refactor-streamline`. **Late, because the code's shape and guards are now settled.**

- **Complexity**: `CA1502`/`CA1506` are on at their thresholds and suppressed at a few sites with a reason.
  Lifting one site's suppression and building makes the analyzer state the current number. **A justification that
  STATES the number is a cached value — the correction is to remove the number**, keeping the reason; do that
  first and fast. Never `cut` a warning line before deduplicating it by site and code.
- **`MainViewModel` sits at the `CA1506` coupling threshold** (95): anything new on the window belongs in its own
  view model (`RunsFilterViewModel` is the pattern). A UI test method that drives much of the window hits the same
  rule — split it by what it asserts.
- **Duplication** — the same helper written out in several files (the UI tests' dispatcher pump was six copies, plan
  WP-T4). Identical copies are a correction; copies that differ in behaviour are a package.
- **A performance finding needs a measurement**: a stated complexity change named in the commit, or counters
  (`dotnet-counters`, `dotnet-gcdump`). Anything else is speculation and is dropped. The scrape is network-bound;
  a CPU micro-optimisation in it is not a finding.

## 7. Context-document drift
**After every step that moves code, before the user-facing words.** Over `CLAUDE.md`, `.claude/context/*.md`,
`.claude/rules/*.md` and `.claude/skills/*/SKILL.md` — this file included.

- **Mechanical, whole tree**: every backticked repository path must resolve. Triage each miss: gitignored
  (`git check-ignore -q <path>`), deleted on purpose and named as history, or relative to a folder the sentence
  names. What is left is drift; the fix is the path it moved to (`git log --follow --diff-filter=R`).
- **Claims of STATE** — a sentence saying a rule "is off", stating a count or a version, or "has not been split
  yet". Read each against the file that decides it. **A claim decays by being OVERTAKEN, not contradicted.**
- **The facts this skill states in *The repository, in the terms the steps use*** are checked like any other claim.

## 8. What users read and see
**Last, because it describes what every step above would change, and its wording can change the layout it then
looks at.** English is the only language; there is no translation to compare.

- **The words**: `README.md`, `Installer/README.md`, the CLI's `--help` (`Cli/CliOptions.cs`) and the strings the UI
  and CLI show (status lines, error and refusal messages). Every command, flag, path and file name a README names
  must exist today; `--help` must list every option `CliOptions.Parse` accepts. Plain and short; cut a sentence that
  restates its heading. **Search for the construction a rule forbids across every string**, not the one somebody
  complained about last time.
- **The window**: there is no UI register (declined 2026-10-25), so there is no drift to claim — only plain
  DEFECTS: clipped, overlapping, unreadable, a control that is not there. Render every tab with
  `QSR_CAPTURE_DIR="$(cygpath -w "$PWD")\\TestResults\\artifacts" dotnet test UiTests --no-build` and LOOK at the
  PNGs. **A `Popup` is a window of its own**: the capture saves its content apart (`scrape-runs-filter-list.png`).
  `Ui/Themes/Dark.xaml` is the one colour source; chain brand colours (`Core/Models/BrandPalette.cs`) are data, and
  the initials-badge palette in `Ui/ViewModels/ChainViewModel.cs` stays in code by settled decision. No test may pin
  a colour, spacing or attribute.

---

# Rules that bind every step

**Robustness outranks maintainability and performance, by veto:**

> No simplification or optimization may delete a failure path, widen a `catch`, drop or lengthen a timeout, remove
> a cancellation check, or make a previously-isolated failure propagate.

A step-6 finding that would do any of those is recorded `waived (robustness)` and dropped.

**Check the INSTRUMENT before believing what it says.** A confident negative is the shape to distrust first — read
WHAT a search matched before concluding anything from HOW MANY. Grep a build for `: error ` (with the colon), never
a bare ` error `: Turkish warning prose can carry the English word.

**Never re-raise a settled decision.** `.claude/context/settled-decisions.md` is re-opened by a new fact, never by
an argument. Where a context document and a settled decision disagree, the decision rules and the document is
step 7's drift.

**Every step that corrects shipped code bumps `Version`** in `Directory.Build.props`, sized by
`working-agreement.md`'s *Versioning*, so one sweep may advance several numbers and `main` is never between versions.

---

# Finishing

**Each step with something to correct opens its own branch with `/wp-start`**, is verified through `/wp-verify`,
closed through `/wp-finish`, merged, and deleted; then the next opens. **A step with nothing to correct opens no
branch.**

- **Branch `sweep-<n>-<subject>`; merge subject `merge: sweep <YYYY-MM-DD> step <n> — <subject>`.** The prefix is how
  the NEXT run finds its baseline.
- **Merge from the primary checkout** (`git merge --no-ff`), then verify the merged `main` in
  `.claude/worktrees/verify-main`, not in the primary checkout.
- **`dotnet build-server shutdown` before `git worktree remove`.** A husk — an EMPTY directory left by a removal that
  reported `Permission denied` — is retried by its literal path; one still holding files is left and said.
- **Delete branches by their literal name.** Never `EnterWorktree`: it branches from `origin/main`.
- **When the last step closes**: verify the merged `main` once more (build, warnings, formatter, suite), deliver the
  report, then revise the plan.

## The report, which is the one thing the maintainer reads

One message, at the end, in a fixed shape so two runs can be compared:

- **The run's own facts, first**: the plan path (or **"plan not found"** as the first line, with every place
  searched); the scope baseline (commit and date, or "whole tree — no marker"); each detector as LIVE, UNAVAILABLE or
  BLOCKED; anything REFUSED, with the exact command.
- **One line per step, all eight, in order** — a step that found nothing says **what it measured**. Each says which
  it did: corrected (the commit and the version), handed over (the plan row), refused on evidence (the settled
  entry), measured clean, or UNAVAILABLE (the instrument).
- **What the maintainer has to do** — the rows needing a decision, each with its recommendation already in the plan,
  and whether `main` is ahead of `origin/main` (a sweep never pushes).
- **What was left behind** — a husk, an unmerged branch, a step that ended the run early and why.

**A recommendation is not permission**: nothing in the report is a request to act.
