---
name: sweep
description: A sequenced quality audit of the whole repository — structure, tests, dead code, configuration, robustness, maintainability, the documentation and the UI's theme discipline. It runs UNATTENDED: it never stops and never asks, merging each step and writing anything that needs a decision into the plan instead. The maintainer starts it and reads the report when they return.
disable-model-invocation: true
---

# The sweep

**One audit, eight steps, in an order that is itself the deliverable.** Adopted from Yalbuz's `/sweep` on
2026-10-24 and adapted to this repository: `master` for `main`, no sinks, no web app, no registers, and a suite
that scrapes live sites.

**What this is not.** A finding that is a LOOP — one rule ID standing over many sites, each needing a
take-or-decline — is not a sweep correction however few sites it has; it is a work package for the plugin skills
`csharp-refactor-harden` (step 5) or `csharp-refactor-streamline` (step 6), qualified with their plugin name if a
bare name does not resolve. Anything crossing a project boundary belongs to `csharp-refactor-modularize`.

## Invocation

**The maintainer starts a sweep; a session never does** — hence `disable-model-invocation: true`. A session may
propose one, with a reason.

## It runs UNATTENDED, and that is the whole design

**A sweep is started when the maintainer is leaving and expects to come back to finished work.**

- **NEVER stop to ask.** Do not call `AskUserQuestion`; do not end a turn with a question. There is nobody there.
- **Anything that WOULD have been a question is written into the plan and SKIPPED** — what was measured, the
  options, the recommendation — and the sweep moves on and does not come back to it.
- **Merge, delete and continue.** Each step's branch is merged into `master` and deleted by the sweep itself,
  before the next step opens. **This is the explicit exception to `working-agreement.md`'s "merging is the
  maintainer's" and "a merge instruction is per-moment", granted for this skill** when the workflow was adopted
  from Yalbuz, where the maintainer granted it on 2026-09-08. `/wp-finish`'s *Report, and stop* is covered by the
  same exception: use it for the commit body and the size cap, then keep going.
  **The limit**: a sweep merges work nobody has reviewed, and what keeps that honest is the unambiguity bar — a
  step CORRECTS only when the repair is unambiguous and the suite proves it, and hands over everything else.
- **Never push.** Nothing here changes that.
- **Report only at the END**, in one message covering every step.

### Ending a run is allowed; WAITING is not

A sweep may **finish early** — a red baseline, a tree that will not build — record why in the plan, say so in the
report, exit. It may never sit there having asked something.

### The four ways a run goes wrong mid-flight

- **An action is REFUSED** (a permission prompt declines a command): the exact command into the plan, carry on
  without it. **Never route around a refusal.**
- **The suite goes RED inside a step.** The step does not merge. **Capture the failing test NAME before
  re-running anything.** If the step's changes caused it, revert them and hand the finding over. If it is a live
  test and the step's diff cannot reach it (see `/wp-verify` §6), note it and carry on. Otherwise the tree moved
  under the run, and that ends it.
- **The session is running out of room.** Finish the step in hand rather than opening the next, and write into
  the plan which step stopped it.
- **A step's merge conflicts.** It should not — steps run one at a time on a tree nobody else is touching — so a
  conflict means somebody is: abort the merge, leave the branch, record it, and end the run.

### How a finding reaches the plan without blocking

1. **APPEND to the plan file as each step ends** — `C:\Users\ihizl\.claude\plans\qsr-price-benchmarks.md`, the
   path `CLAUDE.md` names. Append rather than rewrite: the file is outside git. **State that path in the final
   report.** Create it if it does not exist, with a one-line heading.
2. **The wholesale revision is the very LAST act**, after every step has merged — retiring what landed —
   reconciled first against `git log --oneline` and `git worktree list`.

---

# How a run works

**A step fixes what it can and hands over what it cannot.** The line is the step's own subject: a defect inside
it is corrected on that step's branch and merged before the next step opens; anything reaching past it is a work
package, however small. **A step that produced only prose has not finished** — either it found nothing and says
so, or it left work behind.

## Preflight

1. **Clean tree, green build, green suite** on `master`, from the primary checkout. **If it is not green, the run
   ENDS**; do not repair the baseline. A red that is only a live test the site broke is recorded and does not end
   the run — the hermetic half must be green.
2. **Detector liveness.** Every tool a step names is **proven to emit a value on this repo, by running it**:
   - the analyzer build — `dotnet build Core/QsrPriceBenchmarks.Core.csproj --no-incremental
     -p:AnalysisMode=All > /tmp/qsr-all.txt 2>&1`, and the same for `Cli` and `Ui`; LIVE if it exits 0 and
     `CA1031`, `CA2000`, `CA1502`, `CA1506` each either appear or are proven able to (a site is shown);
   - the type inventory and reference counts step 3 uses;
   - for step 2, the four searches below.

   Each lands in one state: **LIVE**, **UNAVAILABLE** (did not run here; struck from the scorecard and reported
   once with the command), or **BLOCKED** (should work, failed for a fixable reason: plan row with the command,
   carry on). **An inferred zero is not a measurement.**

## Derive, never cache

**Derive every list at the start of every run, from the tree, and write none of them down.** The ways a derivation
goes quietly wrong here:

- **This repository does NOT enforce one type per file** — `Core/Models/Rows.cs` holds five. So the type inventory
  is a declaration search, not the file listing: `grep -rnE '^\s*(public|internal|private|file)?\s*(sealed
  |static |abstract |partial |readonly |record )*(class|record|struct|interface|enum) [A-Z]'`.
- **A WPF type is reached from XAML**, so a `.cs`-only count makes every converter, view model and code-behind
  look dead. **Search `*.xaml` too**, and remember `App.xaml`'s `StartupUri` and resource dictionaries.
- **An extension-method holder reads as dead by type name.** Count its members' callers, not the type.
- **A test class, a `Program.Main`, an xUnit fixture and a `[ModuleInitializer]` have no callers by design.**
- **SQL views and tables are reached by NAME inside strings** — `Repository` and `ExcelExporter` read views
  `Database` creates. A view nothing names is a candidate; one named in a string is alive.
- **A chain in the registry is configuration, not dead code**, even when no test names it.

## What a finding looks like

**Hand over a DECISION, never an inventory**: triage first, lead with the count that SURVIVED. Each item lands in
exactly one place:

- **Corrected in this step** — inside the step's subject, unambiguous, proven by the suite. The commit body is the
  record.
- **A work package in the plan** — needs a judgement, or reaches past the step. Carries **What**, **Where** (a
  path and line that resolve today), **Why it is not the obvious thing**, **What would falsify it**, and
  **Origin: `finding`, Status: not yet committed to build**.
- **A settled decision** — refused on evidence, into `settled-decisions.md` with the measurement and what would
  re-open it.

**When it is not obvious, it is a package rather than a correction.** **A measured "nothing here" is a complete
deliverable**: it opens no branch and writes no plan row. **Every number states what produced it.**

---

# The eight steps

## 1. Structure and naming
**First, because every later finding is written against the tree this step would move.** The candidate set:
`structure.md`'s placement rule — subject, reach and folder — read against its *Worked examples* table, and
`coding-style.md` rule 12, which no analyzer can check. Settle an open case by adding a row to the table.

## 2. Test-suite health
**Second, because every later step leans on the suite.** A test asserts a requirement, never a preference. Four
searches over `Tests/`:

- **cannot fail by construction** — no assertion; the only assertion inside a loop that can run zero times; a
  literal compared with itself;
- **a preference-assertion** — a colour, a spacing value, a markup attribute;
- **an unbounded wait** — no timeout, no cancellation;
- **a name that no longer describes what the body asserts.**

Then revert-confirm-red the survivors against the defect each claims to catch, and **run the hermetic half more
than once**. The live half is not this step's to judge intermittent: a site changes.

## 3. Dead code
**Refactor residue is the subject** — a helper left behind, a seam nothing uses. Build the type inventory, count
what reaches each type solution-wide (`.cs` AND `.xaml`), and put every singleton through the traps above.
**Before step 4, because removing code is what orphans a configuration knob.**

## 4. Configuration knobs
The surface: `Cli/CliOptions.cs` (arguments), `Core/Services/PipelineOptions.cs`, the chain registry in
`Core/Models/ChainConfig.cs`, the `QSR_BROWSER_CHANNEL` variable and `browser-channel.txt` marker read by
`BrowserSession`, and the UI's option view models. Demote or remove what step 3 orphaned, and review **validation
DESIGN**: every check takes exactly one of three behaviours — **refuse to start** (a CLI exit code with a message,
a UI that will not open), **refuse the OPERATION with a reason** (the run does not start; the user is told why),
or **report and change nothing**. **The behaviour is chosen by what is LOST, never by how wrong the value looks**:
an argument that would overwrite or delete a scrape run refuses; one that only narrows output reports.

## 5. Robustness and resilience
*Look for:* an over-broad `catch` outside a genuine top-level boundary; a missing timeout or cancellation check —
every Playwright and `HttpClient` call talks to a site that can hang; an undisposed `IDisposable` (connections,
pages, browser contexts); input reaching SQL or a path without validation.
*Detectors:* `CA1001` and `CA2000` for disposal — **but disposing what something else owns is the wrong repair**;
`CA1031` for a broad catch, each site asked *is this a deliberate error boundary?*; and **`coding-style.md` rule
9**, exception filters, which no analyzer carries.

## 6. Maintainability and performance
**Late, because it operates on code whose shape and guards are settled.** `CA1502`/`CA1506` are the complexity
instrument. A performance finding needs a measurement: **a stated complexity change** (`O(n·m)` → `O(n+m)`,
named in the commit) is evidence on its own; anything that is neither that nor a counter is speculation and is
dropped. **The robustness veto below binds this step hardest.**

## 7. The documentation
**The words a reader meets**: `README.md` and `Installer/README.md`, and the user-facing strings the UI and CLI
show (`--help` text, status and error messages). Late because they describe what steps 1–6 changed.

1. **Accuracy first.** Every command, flag, path and file name in a README must exist today — check each one
   against the tree, because a README rots by being overtaken.
2. **Structure.** What it is, how to build it, how to run it, how to ship it — in the order a reader needs.
3. **Wording.** Plain and short; cut a sentence that restates its heading.

## 8. UI theme discipline
**There is no UI Register here**, so there is no authority for a drift finding, and this step does not invent
one. What it can measure: **`Ui/Themes/Dark.xaml` is the only place a colour is defined** — a colour literal in
any other `.xaml` or in a `.cs` under `Ui/` is drift from the theme, and is corrected. Chain brand colours in
`Core/Models/BrandPalette.cs` are data, not theme, and are out of scope — and so is the initials-badge fallback
palette in `Ui/ViewModels/ChainViewModel.cs`, by settled decision (2026-10-25). Whether a register should exist was
answered on 2026-10-25 — not yet — and is in `settled-decisions.md`; do not re-raise it without the new fact that
entry names.

**Then LOOK at the window.** `UiTests/WindowCapture.cs` renders every tab against a throwaway database; run it
with `QSR_CAPTURE_DIR` set to `TestResults/artifacts` (gitignored) and read the PNGs. A layout defect found there
is drift from the theme or a plain bug, and is corrected or handed over like any other finding.

---

# Rules that bind every step

**Order is not priority.** Robustness outranks maintainability wherever they meet:

> No simplification or optimization may delete a failure path, widen a `catch`, drop or lengthen a timeout, remove
> a cancellation check, or make a previously-isolated failure propagate.

A step-6 finding that would do any of those is recorded `waived (robustness)` and dropped.

**Check the INSTRUMENT before believing what it says.** A confident negative is the shape to distrust first —
read WHAT a search matched before concluding anything from HOW MANY.

**Never re-raise a settled decision.** `settled-decisions.md` is the ruling.

**The database is the user's data** — a step never changes the schema without a migration and its test, and that
is never an unambiguous correction: it is a package.

# Finishing

**Each step that has something to correct** opens its own worktree and branch with `/wp-start`
(`sweep-<n>-<subject>`), is verified through `/wp-verify` and closed through `/wp-finish`, and then **the sweep
merges it into `master` from the primary checkout, removes the worktree, deletes the branch, and opens the next.**
A step with nothing to correct opens no branch.

- **`/wp-start` runs from the PRIMARY checkout and needs a clean `master`.** Return there between steps. **In a
  desktop-app session use `/wp-start`'s app variant instead** — branch inside the session's own worktree,
  `git switch --detach master` between steps — because a hook refuses edits to any worktree the session did not
  start in; the first run hit that refusal at step 1.
- **`dotnet build-server shutdown` before `git worktree remove`.** A husk is retried, not reported.
- **Never reach for `EnterWorktree`.**
- **Merge with `--no-ff`** and a subject `merge: sweep <date> step <n> — <what>`, so a step stays one
  revertable unit.
- **Each step that corrects shipped code bumps the version**, through `/wp-finish` like any package (maintainer,
  2026-10-25), so one sweep may advance several numbers and every merge to `master` is a complete versioned change.
  A step that corrects only docs or tests does not bump; its body says so.

When the last step closes, **the plan is revised** — what landed retired, every reference to a concept that no
longer exists struck.

## The report, which is the one thing the maintainer reads

One message, at the end, in a fixed shape:

- **The run's own facts, first:** the plan path; each detector LIVE, UNAVAILABLE or BLOCKED; anything REFUSED,
  with the exact command.
- **One line per step, all eight, in order** — corrected (naming the commit), handed over (naming the plan row),
  refused on evidence (naming the entry), or measured clean (saying what was measured).
- **What the maintainer has to do**, if anything — the rows that need a decision, recommendation already written.
- **What was left behind** — a husk, an undeletable branch, a step that ended the run early.

**A recommendation is not permission**: nothing in the report is a request to act.
