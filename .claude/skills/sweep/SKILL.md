---
name: sweep
description: A sequenced quality audit of the whole repository — structure, test health, dependencies, dead code, configuration, robustness, maintainability, context-document drift, the user-facing docs and UI drift. It runs UNATTENDED: it never stops and never asks, merging each step and writing anything that needs a decision into the plan instead. Project-specific values come from .claude/project-profile.md. The maintainer starts it and reads the report when they return.
disable-model-invocation: true
---

# The sweep

**One audit, ten steps, in an order that is itself the deliverable.** It corrects what is unambiguous, merges each
step, and hands everything else to the plan as a work package.

**What this is not.** `csharp-refactor-harden` and `csharp-refactor-streamline` own LOOPS — one rule ID standing
over many sites, each triaged take-or-decline — and a robustness or maintainability finding of that shape is
handed to them. **The line is not a count.** A step CORRECTS a site whose repair is unambiguous and whose subject
is its own; the moment a finding needs a POLICY over sites it is a loop however few sites it has.
`csharp-refactor-modularize` owns anything crossing a project boundary. **All three are PLUGIN skills** (under
`anthropic-skills`), so a bare `/csharp-refactor-…` may not resolve; qualify it with the plugin name rather than
concluding the skill is gone.

## The profile, and why this file names no project

**Everything true of one repository lives in `.claude/project-profile.md`, as named keys** — its projects, the
conventions its build already enforces, the documents the steps read, its plan, its secrets file, its user docs,
its UI register. This file refers to them as `profile:<key>`. **That is what makes the skill copyable**: a new
repository gets its own profile, never an edited copy of this file.

**An absent key is UNAVAILABLE, never clean.** A step whose input key is missing, `none`, or names something that
does not exist reports `UNAVAILABLE (profile: <key>)` on its report line and does nothing else. This is the
failure the profile exists to prevent: a step that reads a structure document finds nothing in a repository that
has none, and "found nothing" reads exactly like "measured clean". **No profile at all: run preflight only, list
the keys the steps would have needed, and end.**

Examples in this file marked *(illustrative)* come from the repository this skill was written in. They show a
SHAPE to look for; they are not a list to check.

## Invocation

**The maintainer starts a sweep; a session never does** — hence `disable-model-invocation: true`. Starting one is a
decision to spend a session auditing, and work is ISSUED, never taken. A session may propose one, with a reason.

## It runs UNATTENDED, and that is the whole design

**A sweep is started when the maintainer is leaving — for work, for the night — and expects to come back to
finished work.** Everything below follows from that, and it overrides the ordinary cadence rather than bending it.

- **NEVER stop to ask. Never.** Not for a decision, a preference, permission, or to report progress. Do not call
  `AskUserQuestion`. Do not end a turn with a question. **There is nobody there**, so a question is not caution —
  it is the run halting for hours and delivering nothing. A run that reported after step 1 *(illustrative,
  2026-09-08)* had to be restarted by hand the next morning.
- **Anything that WOULD have been a question is written into the plan and SKIPPED.** A stop becomes a plan row
  plus a continue: what was measured, the options, which one is recommended and why — then move on and do not
  come back to it.
- **Merge, delete and continue.** Each step's branch is merged into `main` and deleted by the sweep itself, without
  asking, before the next step opens. **This is an explicit exception to three ordinary rules** — *merging is the
  maintainer's*, *a merge instruction is per-moment*, and *work whose choices have not been seen is not merged* —
  granted for this skill by the maintainer (*"it should merge, delete & publish as it completes its steps"*). A
  session running this skill does not re-derive those rules and stop. **The third has a limit**: a sweep merges
  work nobody has reviewed, and only the unambiguity bar keeps that honest — a step CORRECTS when the repair is
  unambiguous and the suite proves it, and hands over everything else. Widening that bar turns the exception into
  something it was never granted for. **`/wp-finish` says "Report, and stop"**; the exception reaches that
  sentence too — a step uses it for the commit body and the size cap, then keeps going.
- **Publish as you go.** A register the run revises is republished when the step that revised it finishes.
  **"Publish" means republishing a register, never `git push`** — a session never pushes.
- **Report only at the END**, in one message covering every step. A mid-run progress report is a stop wearing a
  report's clothes.

### Ending a run is allowed; WAITING is not

A sweep may **finish early** — a red baseline, a tree that will not build, a session out of room — and that is a
complete, honest outcome: record why in the plan, say so in the report, exit. What it may never do is **sit
there** having asked something.

### The ways a run goes wrong mid-flight, and the one legal move for each

Each gets one answer, because an unattended run that has to invent one will invent a stop.

- **An action is REFUSED** — a permission classifier declines a `git rm`, a delete, a navigation. **Treat it
  exactly as a BLOCKED detector**: the exact refused command into the plan, and carry on without it. **Never route
  around a refusal** — re-issuing the refused operation in a different form (narrower, reworded, split up, through
  another tool) is taking the decision the refusal made. **The one distinction**: a command refused because it
  broke a STATED rule of this repository (a glob `rm -rf` where the rules require literal names) is replaced by
  the form the rule requires — that is following the rule, not routing around the refusal. If unsure which it
  is, it is the first kind. **Retrying a FAILURE is not routing around a refusal**: a `git worktree remove` that
  ended in `Permission denied` on an empty directory is retried by the same literal path, because nothing
  refused it.
- **The suite goes RED inside a step.** The step does not merge. **Capture the failing test NAME before re-running
  anything** — a failure that was tailed rather than captured leaves no name, and an intermittent failure without
  a name cannot be fixed. Then run that test ISOLATED several times:
  - fails isolated and the step's changes touch its subject → revert the step's changes, hand the finding over;
  - passes every isolated run → a load-sensitive test; re-run the full suite once, and if it is green the step
    merges and the test becomes a plan row for step 2 of the next run, carrying the captured failure message;
  - fails isolated and the step did not touch it → the tree moved under the run; that ends it as a red baseline does.
  - **it is one of `profile:live_tests`** and the step's diff cannot reach what it scrapes → the site changed; the
    step merges on a green remainder, and the test's name and message go into the plan.
- **A register publish is REFUSED** because a live version exists this session has not read. **Re-read the live
  artifact in full and merge the edits onto THAT.** Never retry the same bytes, and never publish from a copy
  saved before the refusal.
- **The browser, a navigation or starting the app is denied, or a screenshot times out.** That instrument is
  UNAVAILABLE for this run, named once in the report with the exact call. **Fall back to `profile:render_harnesses`**
  — committed tests that write the surfaces to files — and render those. Never call a step clean that rendered
  nothing.
- **Plan mode switches on mid-run.** It permits editing the plan file only, so no step can continue. **Record it as
  the reason the run ended**, leave no step half-applied (a worktree with uncommitted work is committed on its
  branch and left UNMERGED, and the report names it), deliver the report, and end. Never revert scaffolding
  partially to make room.
- **The session is running out of room.** The likeliest ending, and not a failure. **Finish the step in hand rather
  than opening the next**, and write into the plan which step stopped it and what the next one was.

### How a finding reaches the plan without blocking

**Only a plan-mode round trip reaches the maintainer's plan panel, and it needs approval** — which is a stop.

1. **Resolve the plan at preflight, from `profile:plan_rule` and `profile:plan_known_path`.** If the named file does
   not exist, look for it before concluding there is none: search `~/.claude/file-history/` for the plan's first
   line, and check the other project folders `profile:history_paths` names — a repository that MOVED leaves its
   history under the old path. **If it is still not found, the run continues and the report's FIRST line says
   "plan not found"**, naming every place searched. **Never create a fresh plan file and present it as the plan**:
   a run that did *(illustrative, 2026-11-06)* produced *"what I see is not the original plan"* — a second
   register, the failure every retired handoff file died of.
2. **APPEND to the plan FILE as each step ends.** Durable: if the session dies at step six, five steps' findings are
   on disk. **Append rather than rewrite** — the plan lives outside the repository, so a concurrent write is a
   silent overwrite and there is no reflog to recover the loser from.
3. **Deliver the final report, THEN make the one plan-mode round trip** — in that order. The round trip may block
   for as long as the maintainer is away; a report placed after it was lost behind a pending approval
   *(illustrative, 2026-09-11)*, and the report is the one thing they read. The round trip is where the wholesale
   revision happens — retiring what landed — after reconciling against `git log --oneline` (what landed) and
   `git worktree list` (what another session has in flight). **Every row appended this run must survive it**: a
   maintainer who cannot see the new packages in their panel has been handed nothing.

---

# How a run works

## The order is the deliverable, and a step ends in a merge

**A step fixes what it can and hands over what it cannot. The line is the step's own subject**: a defect inside it
is corrected on that step's branch and merged before the next step opens; anything reaching past it is a work
package, however small. **The order is real work, not a reporting convention**: structure moves files that dead
code and maintainability would otherwise write findings against, and a finding raised before the move names paths
that stop existing. **A step that produced only prose has not finished** — either it found nothing and says so, or
it left work behind.

## Scope: what changed since the last sweep

**Candidate sets are built from what changed since the last sweep's final merge**, not from the whole tree every
time — repeated whole-tree passes over unchanged code are most of why some steps measure clean run after run.

- **The baseline**: every sweep merge subject starts `merge: sweep <YYYY-MM-DD> step <n>`, so the last one is
  `git log --grep='^merge: sweep ' -1 --format='%H %ad %s' --date=short`. Report it. **No marker found → the scope
  is the whole tree, and the report says so.**
- **Changed files**: `git diff --name-only <baseline>..HEAD`.
- **Whole tree regardless, because their subject is not a diff**: step 2's repeated suite runs, step 3
  (dependencies age without a commit), step 8's mechanical path check, and any step whose detector is newly LIVE
  this run — a rule switched on since the baseline has never seen the old code.

## Preflight

1. **Clean tree, green build, green suite.** A sweep on a red tree measures nothing. **If it is not green, the run
   ENDS** — write what is broken into the plan, say so in the report, exit. Do not repair it: a sweep that fixes its
   own baseline has changed the tree it was about to measure. **Green is necessary and not sufficient** — step 2
   exists because a suite can be green precisely because some tests cannot fail. **A red that is only
   `profile:live_tests`** — tests that reach a real external site — **on a green remainder is the site, not the
   tree**: re-run it once, record it by name in the plan, and the run goes on.
2. **The profile.** Read `.claude/project-profile.md`; list every key a step needs that is absent.
3. **Detector liveness.** Every detector a step names must be **proven to emit a value on this repository, by
   running it**. Each lands in exactly one state:
   - **LIVE** — ran, produced a parseable value.
   - **UNAVAILABLE** — did not run here, this time. Reported once with the exact failing command.
   - **BLOCKED** — exists, should work, failed for a fixable repo-specific reason. Into the plan with the exact
     command; carry on without it.

   **An ANALYZER RULE is a detector, and a rule that is off reads exactly like a rule at zero.** For every rule ID a
   step names, read its effective severity from `profile:analyzer_config` first. A rule that is not configured on
   is UNAVAILABLE until a probe proves otherwise: in a THROWAWAY worktree, enable it at `warning`, add a file that
   breaks it, build, confirm the ID appears, discard the worktree. **Probe the SHAPE the rule documents, not a
   guess at it** — CA1065 permits `InvalidOperationException` in a property getter, and RCS1236 fires on
   `if (!(e is X)) throw;` but not on `if (e is X) return; throw;` *(illustrative, both measured 2026-11-06)*.
   Two rules were reported "measured" for five runs while switched off *(illustrative)*.
   **The NuGet audit is a named detector**: `dotnet list package --vulnerable --include-transitive` and
   `--deprecated`. It found a High-severity advisory a sweep was not looking for *(illustrative)*.

   **An inferred zero is not a measurement, and neither is an unexamined non-zero.**

## Derive, never cache

**Derive every list at the start of every run, from the tree, and write none of them down** — entry points, type
counts, DI counts, never-touch lists. Every cached list decays; one *(illustrative)* named a hosted service that
had never existed, which makes live code look dead, and a DI count went 33 → 73 → 57 in three days.

The ways a derivation goes quietly wrong — shapes that look DEAD and are alive. Check every one before believing a
"no caller" result:

- **DI registration through a builder**: `AddHealthChecks()`, `checks.Add(...)`, assembly scanning — an
  `Add*` grep cannot see them.
- **A folder is not a role**: a `Controllers/` folder also holds response types; never promote a type by folder.
- **Reached by convention, not by name**: a Blazor component without `@page`, a controller action (routing), an
  xUnit test class (discovery), an interface implementation the framework calls through DI
  (`IClaimsTransformation.TransformAsync`), a `[ModuleInitializer]`.
- **An EXTENSION-METHOD HOLDER**: callers name the METHOD, never the type. **Count members, not the type.**
- **MARKUP** (`profile:markup_globs`): a XAML markup extension is written without its `Extension` suffix
  (`FooExtension` is `{res:Foo}`), and an ATTACHED PROPERTY's markup names the property, so neither CLR accessor
  appears anywhere in code — deleting `SetX` silently disables the binding with every test green
  *(illustrative: a type-ahead on two comboboxes)*. A `.cs`-only candidate set is blind to all of it.
- **Test classes and fixtures**: filter test classes out before reading anything (one run's 232 singletons were
  231 test classes), and exclude `profile:fixture_excludes` — sample source is uncalled by design.
- **The type inventory is the file listing ONLY when `profile:one_type_per_file` is enforced.** Otherwise enumerate
  types by parsing declarations, or files holding several types shrink the candidate set silently.
- **Read what the matches ARE, not how many there are**: a doc comment or a context file naming a type in prose
  pads a count without moving the fact.
- **This repository's own traps** are `profile:derivation_traps` — read them before believing a "no caller" result.

## What a finding looks like

**Hand over a DECISION, never an inventory.** Triage before the maintainer sees anything: read each site, mark it
take-or-decline with its reason, and lead with the count that SURVIVED.

**Each triaged item lands in exactly one place:**

- **Corrected in this step** — inside the step's subject, the repair unambiguous, the suite proves it. The commit
  body is the record.
- **A work package in the plan** — needs a judgement the sweep cannot make, or reaches outside this step's subject.
- **A settled decision** (`profile:settled_decisions`) — refused on evidence, with the measurement and what would
  re-open it.
- **An item on `profile:offsite_register`** — sound, but the CHECK that would settle it needs a network or machine
  this session cannot reach. Give it a Do, a Pass and a Capture. No such key → a plan row saying so.

**When it is not obvious, it is a package rather than a correction.** Narrowing scope to avoid a decision counts as
deciding, and widening it to avoid asking is the same move in reverse. **Neither is a reason to ask.**

Each item handed over carries: **What** (one sentence); **Where** (a path and line that resolve today); **Why it
is not the obvious thing**; **What would falsify it**; **Origin: `finding`, Status: not yet committed to build**.
**Those five fields are for a handed-over item, never a commit body**, which `/wp-finish` caps at about 1,500 bytes:
a step that corrected a dozen sites writes what changed, why, and the evidence ONCE.

**A measured "nothing here" is a complete deliverable.** It opens no branch and writes no plan row; its record is
its report line. **Every number states what produced it** — which projects, which analysis mode, which scope.

---

# The ten steps

## 1. Structure and naming
**First, because every later finding is written against the tree this step would move.**

*Do not re-detect* anything in `profile:enforced_guards` — the build or suite already refuses it, and reporting it
is noise. **But only what the profile lists**: a convention the profile does not name as enforced is checked here,
not assumed.
*The candidate set*: **`profile:structure_doc`'s placement rule**, read against its own worked examples, over the
types in scope; and **`profile:naming_rule`** — names a reader would have to open the file to understand. Either
key absent → that half is UNAVAILABLE, never clean.

## 2. Test-suite health
**Second, out of blast-radius order, deliberately: every step after this leans on the suite.**

*The rule it enforces*: a test asserts a requirement, never a preference — *would a redesign nobody would call a
regression turn this red?* Over every project in `profile:test_projects`.

*The candidate set, derived — searches, not judgements:*
- **A test that cannot fail by construction** — no assertion; the only assertion inside a loop that can iterate
  zero times; a literal compared with itself.
- **A preference-assertion** — a hex colour, a px value, a ratio, a markup attribute name.
- **An unbounded wait** — no timeout, no cancellation. It hangs the suite instead of naming an assertion.
- **A name that no longer describes the body** — the tell for a test edited until it passed.
- **Load-sensitive tests** — run the WHOLE suite at least three times; any test that fails once is captured by
  name and then run isolated ten times. Passes isolated, fails under load → a timing assumption, handed over with
  its failure message. Any `load_sensitive` rows the plan already carries are re-checked first. **Not
  `profile:live_tests`**: a test that reaches a real site is intermittent because the site is, so this search runs
  over the remainder.
- **Uncovered production code** — `dotnet test --collect:"XPlat Code Coverage"` when a coverage collector is
  referenced. An uncovered branch in changed code is where a test is missing; an uncovered MEMBER with no test
  caller is handed to step 4 as a dead-code candidate. Coverage of UI markup that the project verifies by
  rendering is not a finding.

*Then revert-confirm-red the survivors*, each against the ACTUAL defect it claims to catch. **Mutation testing
(Stryker.NET) automates exactly that**, scoped to changed files; it is a third-party download, so it runs only if
already installed and is otherwise UNAVAILABLE — never installed by an unattended run.

## 3. Dependency hygiene
**After test health, because a version bump changes runtime behaviour and needs a suite already shown to fail when
it should. Before everything that reads an analyzer**, because an analyzer package bump changes what steps 6 and 7
measure.

- **Vulnerable and deprecated** — from preflight's audit. A vulnerable package is a correction if a fixed version
  exists in the same major; otherwise a package.
- **Skew** — the same package at different versions across projects, and framework packages (`Microsoft.Extensions.*`,
  `Microsoft.AspNetCore.*`) at mixed servicing versions. Aligning to the highest version already in use within
  the same major is a correction.
- **Floating toolchain** — no `global.json` means the SDK, and with it every analyzer count, differs between
  machines; no `Directory.Packages.props` means skew has nowhere to be prevented. Both are packages, not
  corrections: adopting either is a decision about the repository.
- **Outdated** — `dotnet list package --outdated`. Patch and minor updates within the current major are
  corrections; a MAJOR update is a package with the breaking-change notes named.
- **`dotnet list package` restores**, which rewrites gitignored `obj/` folders — run it from the step's worktree,
  not the primary checkout.

## 4. Dead code
Needs the net step 2 just checked. **Refactor residue is the subject** — a helper left behind by a change, a seam
nothing uses, a name that stopped being true. No analyzer finds it.

**The MAIN pass is member-level, production references only.** A count that is solution-wide answers *is anybody
using this?* when the question is *is the PRODUCT using this?* — and those differ by exactly the class this step
exists to find. **A production member whose only caller is a test is dead production code.** Count production
(`profile:production_projects`) and test references separately, never as one number. The type-level singleton pass
is the cheap second pass, not the first: in the runs it led, it found nothing four times while member-level triage
found the real cases *(illustrative)*.

**Residue vs a deliberate test SEAM: the seam says so** — its name (`…ForTests`) or its doc comment names the test.
Residue claims a production purpose and has no production caller. **Read the name and the doc comment before the
count**, then put every candidate through *Derive, never cache*.

**Signature extraction has a hole**: a member whose RETURN TYPE contains parentheses — any tuple — defeats a
`…Name(` pattern. Take the FIRST identifier followed by `(` on the declaration line, not the last token before it.

*Before configuration, because removing code is what orphans a configuration knob.*

## 5. Configuration knobs
**Triggered, not routine.** It runs when step 4 removed code, or when a validator, a range check or an options type
changed since the baseline. Otherwise its line says *not triggered* and names the check that decided it.

When it runs: demote or remove what step 4 orphaned, and review **validation DESIGN** — what should be refused,
where, and which behaviour each check takes, per `profile:config_behaviours`: **the behaviour is chosen by what is
LOST, never by how wrong the value looks**. Not which keys exist — `profile:config_coverage_test` holds that.

**Never decide anything from a committed placeholder configuration file.** Read `profile:effective_config` — the
effective configuration and the registration code. A sweep that closed a finding from the placeholder file was
reverted the same day *(illustrative)*.

## 6. Robustness and resilience
The audit half of `csharp-refactor-harden`. The surface is settled by now, so a guard goes where it belongs.

**First, read the build**: every robustness rule that `profile:analyzer_config` holds at `warning` starts at zero
and needs no search — a non-zero is new and the build names it. Preflight proved which are LIVE. **Then the shapes
no analyzer catches:**

- **Exceptions that are lost, not thrown.** A task discarded with `_ = SomethingAsync()` — `VSTHRD110` ignores the
  discard deliberately, so no rule sees it. For each site, read what the discarded method CATCHES: if it catches
  only listed types, everything else lands in a task nobody observes. An `HttpClient` timeout throws
  `TaskCanceledException`, which a `catch (HttpRequestException)` does not take. `Task.Run` whose result is
  dropped is the same shape.
- **Global handlers, per host type** (`profile:host_types`). WPF: `Application.DispatcherUnhandledException` sees
  only the UI thread — `TaskScheduler.UnobservedTaskException` and `AppDomain.CurrentDomain.UnhandledException`
  must exist too, or a discarded task's exception vanishes. ASP.NET Core hosted services: the
  `BackgroundServiceExceptionBehavior` actually configured, and whether a background loop catches per iteration.
  Console: `AppDomain.CurrentDomain.UnhandledException` before anything starts. **A missing handler is a
  correction only when the project already has a logging path to route it to**; otherwise a package.
- **A broad catch outside a genuine boundary**, **a missing timeout or cancellation check**, **an undisposed
  `IDisposable`**, **an external call with no bounded failure path**, **input reaching a query or a path unvalidated**.
  For disposal, a blanket repair is wrong: **disposing what DI or `IHttpClientFactory` owns causes
  `ObjectDisposedException` in production**. A rule `profile:analyzer_config` declines with a reason (`CA2000`
  here, *illustrative*) is read only by enabling it in a throwaway worktree, never reported from its silence.
- **The reviewer-carried robustness rules** in `profile:reviewer_carried_rules` — exception filters over
  catch-and-retest is the usual one, because **a `when` filter that does not match never unwinds the stack**.
  Where an analyzer covers only some spellings of a rule, search for the others.

`co_located_products` the profile puts in scope for robustness are in scope here.

## 7. Maintainability and performance
The audit half of `csharp-refactor-streamline`. **Late, because it operates on code whose shape and guards are now
settled.** Its ordering is this skill's own; the robustness veto under *Rules that bind every step* is where the
two refactoring skills agree.

**A performance finding needs a measurement**, and two need no profiler: **a stated complexity change**
(`O(n·m)` → `O(n+m)`, named in the commit) and **counters** (`dotnet-counters`, `dotnet-gcdump`) for allocation.
Anything else is speculation and is dropped.

**Complexity detectors**: `CA1502`/`CA1506`, when `profile:analyzer_config` holds them on — lifting one site's
suppression and building makes the analyzer state the number. **Never `cut` a warning line before deduplicating
it**: one site can carry two codes. `Microsoft.CodeAnalysis.Metrics` is UNAVAILABLE on modern projects (5.6.0
ships `Metrics.exe` without its BuildHost; 3.3.4 cannot load a modern project); under MSYS, `/t:Metrics` is
path-mangled — use `-t:Metrics`.

**A suppression justification that STATES a number the analyzer computes is a cached derived value**, and
re-stating drifted numbers took most of this step's corrections in eight runs *(illustrative)*. **The correction
is to remove the number**, keeping the reason — the analyzer states the current figure whenever the suppression
is lifted. That is a mechanical sub-step, done first and fast, so the step's time goes to maintainability.

## 8. Context-document drift
**After every step that moves code, before the user-facing words**, because steps 1–7 change what these documents
describe. Over `profile:context_docs`.

- **Mechanical, whole tree**: every backticked repository path must resolve. Triage each miss before calling it
  drift: **gitignored** (`git check-ignore -q <path>`) — expected; **deleted on purpose and named as history** — the
  surrounding sentence says so; **relative to a folder the sentence names** — resolve it there. What is left is
  drift, and the fix is the path the file moved to (`git log --follow --diff-filter=R`). A first run found 17 misses
  and 3 real ones *(illustrative)* — so the count is never the finding.
- **Claims of STATE**: a sentence saying a rule "is off", "is worth its own package", "has not been split yet", or
  stating a count. Read each against the file that decides it. **A claim decays by being OVERTAKEN, not
  contradicted** — the code moved and the sentence stayed true-looking — so nothing else will notice.
- **This skill and the profile are in scope.** The profile's paths are checked like any other.

## 9. The user-facing docs, in every culture
**The words a reader meets** — `profile:user_docs` in `profile:cultures`. Late because it describes what every step
above would change.

1. **Structure and order** — does the section sequence follow how somebody approaches the product, or where each
   feature happened to be added?
2. **Wording** — concise and plain; cut a sentence that restates its heading and a qualifier that rules nothing out.
   Apply `profile:wording_rules`, and **search for the CONSTRUCTION a rule forbids, across every string, not the
   paragraph somebody complained about last time.**
3. **Every culture, together** — a key-parity test guards the SET of keys, never what they say, so this is the only
   place the texts are compared as prose. **No culture is the draft.**

## 10. UI drift, against the register
**Last, and a LOOK rather than a measurement** — step 9's wording can change layout. `profile:ui_register` is the
authority; none → the DRIFT half is UNAVAILABLE. **Publish only to the register THIS repository's profile names**,
never one remembered from another repository.

**No register, but `profile:render_harnesses`** → still render and look, and claim no drift: with no authority a
surface cannot disagree with anything. What a look can still find is a plain DEFECT — clipped, overlapping,
unreadable, a control that is not there — corrected or handed over like any finding; `profile:theme_rules` says
what is the theme's and what is data.

- **Take the register's `read` rows as the checklist** — a `build` row is enforced by something; a `read` row is
  the half nothing watches.
- **Look at the COUNTS first**: they decay by being overtaken. A stale row can HIDE built work — re-proposing
  something half-built is the cost.
- **Check the INSTRUMENT before believing a drift report** — three reported drifts that were all the probe
  *(illustrative)*.
- **A surface that disagrees with a row is DRIFT**; a proposal that disagrees with one is an argument with a
  decision, re-opened only by a new fact.
- **Render and look** — no test may pin a colour, spacing or attribute. Run the app with `profile:run_command`
  from the worktree; **a browser pane can start at a 0×0 viewport, so set an explicit size before measuring**.
  Instrument denied → `profile:render_harnesses`, per *mid-flight*.
- **Where a change moves a rule, revise the register in the same breath.** Reading the live register in full is the
  publish precondition; a refusal means any earlier copy is stale.

---

# Rules that bind every step

**Order is not priority.** Robustness outranks maintainability and performance wherever they meet, by veto:

> No simplification or optimization may delete a failure path, widen a `catch`, drop or lengthen a timeout, remove
> a cancellation check, or make a previously-isolated failure propagate.

A step-7 finding that would do any of those is recorded `waived (robustness)` and dropped. The mirror holds: a guard
from step 6 that pushes a method past readable size is a named cost, not a free win.

**Check the INSTRUMENT before believing what it says.** A probe is written in a minute and is wrong as easily as
the thing it measures, so **a confident negative is the shape to distrust first** — read WHAT a search matched
before concluding anything from HOW MANY. A search once concluded four skills did not exist because it never
looked in the plugin directory; a bare ` error ` grep scored 8 on a build that exited 0 by matching a parameter
name inside localized warning prose *(both illustrative)*.

**`profile:never_a_correction` is never a step's correction**, however unambiguous it looks: it is always a package.

**Never re-raise a settled decision.** `profile:settled_decisions` carries what earlier runs ruled. A ruling is
re-opened by a new fact, never by an argument. Where a context document and a settled decision disagree in tone,
the decision is the ruling and the document is drift — step 8's.

**Co-located products** (`profile:co_located_products`) are in scope exactly where the profile says, and nowhere
else. A repository-wide guard that hard-codes one product's folders is where a convention the other product does
not hold sits unnoticed.

**Line endings are `profile:line_endings`.** Under MSYS, `sed -i` rewrites every line it reads; repair and check
lines == CRs after any use on a CRLF tree. Make and unmake a multi-line break with an editor tool, never `sed`
or `perl -0pi`.

---

# Finishing

**Each step with something to correct opens its own branch with `/wp-start`**, is verified through `/wp-verify`,
closed through `/wp-finish`, merged, and deleted; then the next opens. **A step with nothing to correct opens no
branch.**

- **Branch `sweep-<n>-<subject>`; merge subject `merge: sweep <YYYY-MM-DD> step <n> — <subject>`.** The subject
  prefix is how the NEXT run finds its baseline; a merge without it makes the next run's scope the whole tree.
- **`/wp-start` runs from the PRIMARY checkout**, on a clean `main`; return there between steps, and merge there.
  **A session that already sits in an app-made worktree uses `/wp-start`'s variant for that case** — a hook refuses
  edits to any worktree the session did not start in.
- **`dotnet build-server shutdown` before `git worktree remove`**, or the build host holds the directory. **A husk
  — an EMPTY directory left by a removal that reported `Permission denied` — is retried by its literal path, not
  reported**; one still holding files is the build host's, and is left and said.
- **Never `EnterWorktree`**: it branches from `origin/main`, routinely behind local `main` because a session never
  pushes, and it cannot be called twice in one session.
- **Corrections travel in the step's own commits**; everything handed over is an ordinary work package; anything
  refused on evidence goes to `profile:settled_decisions`.
- **When the last step closes**, the plan is revised in the one round trip described above — AFTER the report.

## The report, which is the one thing the maintainer reads

One message, at the end, **before** the closing plan-mode round trip, in a fixed shape so two runs can be compared:

- **The run's own facts, first**: the plan path resolved (or **"plan not found"** as the first line, with every
  place searched); the scope baseline (commit and date, or "whole tree — no marker"); each detector as LIVE,
  UNAVAILABLE or BLOCKED; every profile key that was absent; anything REFUSED, with the exact command.
- **One line per step, all ten, in order** — a step that found nothing still gets its line, saying **what it
  measured**. Each says which it did: corrected (the commit), handed over (the plan row), refused on evidence (the
  settled entry), measured clean, not triggered, or UNAVAILABLE (the key or the instrument).
- **What the maintainer has to do** — the rows needing a decision, each with its recommendation already in the
  plan, and anything now waiting on `profile:offsite_register`.
- **What was left behind** — a husk, an unmerged branch, a step that ended the run early and why.

**A recommendation is not permission**: nothing in the report is a request to act; it is a record of what was done
and what is now the maintainer's.
