# Working Agreement

How this project is worked on: the maintainer's durable expectations first, then branching, worktrees, the one
plan, committing, what makes a test evidence, the findings register, and how a package is finished. **Adopted
from Yalbuz on 2026-10-24**; the incidents that bought most of these rules are recorded in Yalbuz's
`settled-decisions.md`, not repeated here. This file is loaded into every session, so every paragraph is paid for
every time — a new rule arrives with at most one sentence of example, and its story goes to
`settled-decisions.md`.

## Where this repository differs from Yalbuz

- **The default branch is `master`**, not `main`. Every recipe here names `master`.
- **The database is the user's data** (see `CLAUDE.md`). Schema changes migrate; nothing drops their tables.
- **There is no web app, no sink and no secrets file.** No gitignored settings file has to be copied into a
  worktree, and nothing a test or a run does publishes anywhere.
- **The suite scrapes live sites.** A red in `Tests/ChainWebsiteTests.cs` or `Tests/TgMenuTests.cs` is first
  checked against the site, then against the code.
- **There is no UI Register and no Office Run Acceptance register.** UI decisions and anything needing an
  environment this machine lacks are plan rows; the UI Register was declined for now on 2026-10-25 (see
  `settled-decisions.md`). What there IS instead: `UiTests/WindowCapture.cs`, which renders the window to PNG so a
  UI change can be looked at — `/wp-verify` says how.
- **Line endings**: `.gitattributes` says `* text=auto` and `core.autocrlf` is on, so the index holds LF and the
  working tree CRLF. Nothing in the suite checks endings; `git diff --stat` showing every line of a file changed
  is the tell of an ending flip.

## The maintainer's working style

- **Quality outranks compatibility.** No legacy fallbacks, no deprecation shims, and never keep a wrong name or a
  wrong shape because correcting it is a large diff — *except* where it would lose the user's data, which is the
  one compatibility this product owes.
- **Verification is the assistant's job.** Verify what is reachable from this machine without being asked.
- **Evidence, not reasoning — for claims and for decisions.** A fix is not finished because the code looks right.
  - **Prefer a real detector over an inferred zero.** A grep that finds nothing has measured the grep.
  - **A measured "nothing to do here" is a deliverable.** Never manufacture work to look busy.
  - **A hypothesis assembled from reading the code is a candidate, never a conclusion** — when the evidence is one
    run away, run it.
  - **Say so when the instrument, not the system, is what answered.**
- **Take a position, and argue it.** An open question wants a recommendation and its reasoning, not a menu; say
  what would change it, and flag any part that is an addition of your own.
- **Hand over a DECISION, never an inventory.** Triage analyzer and warning loops before they are seen: each site
  read and marked take-or-decline with its reason, and the count that SURVIVES leads. **Every number states what
  produced it** — which projects, which analysis mode.
- **Correct a misused technical term, every time, in one clause, and carry on.** The bar is a term with a settled
  meaning outside this project; the project's own names for its own things are not corrected.
- **Say plainly when what was supplied is not what was asked for**, before building on a substitute.
- **Justify dependencies unprompted** — maintenance status and the first-party alternative.
- **What may run on this machine.** The maintainer reviews commands; don't assume blanket trust. **Never hand over
  a PowerShell script to run** — Defender quarantines loose scripts. `build-installer.ps1` is the maintainer's to
  run, not a session's. Say beforehand when a check needs something started, and ask.
- **Never spawn a background task or a second session for an out-of-scope finding** — write it into the plan as a
  work package. The interruption is the problem, not the finding.
- **Work arrives in packages, and each one stops for a merge.** Finish one, report, and wait.

## Branching
- **Never check out `master` to work on it.** Its two uses are branching a new task off its tip and merging a
  finished branch into it.
- Branch a new task from the current `master`, never from the last active branch — unless the maintainer names a
  branch to continue, or the task is a fix spun directly out of in-progress work on a feature branch (then branch
  from that, and say so).
- Name branches `feature/<slug>` or `fix/<slug>`; a sweep uses `sweep-<n>-<subject>`.

## Worktrees

- **Any session about to change files enters a worktree first**, under `.claude/worktrees/`, opened by
  `/wp-start` from the primary checkout. The primary checkout stays on `master` and is nobody's workspace. **A
  desktop-app session is already in one** and may not edit any other, so it branches inside its own worktree —
  `/wp-start` says how.
- **`/wp-start` names local `master` rather than using `EnterWorktree`**, which branches from `origin/master` —
  routinely behind, because a session never pushes — and can be called only once per session.
- **`dotnet build-server shutdown` before `git worktree remove`**, or the build host holds the directory. A
  removal that ends in `Permission denied` on an EMPTY directory is a husk: retry the `rm -rf` moments later.
- **Delete branches by their literal name**, never `$(git branch --show-current)`.

## What the maintainer keeps

- **Work is ISSUED, never taken.** A session that finishes reports and stops.
- **Merging is the maintainer's**, and a session merges only when told which branch. **Pushing is theirs too, and
  a session NEVER pushes.** A merge instruction is per-moment, never standing.
  **`/sweep` is the ONE exception**: it merges each step and deletes the branch on its own authority, granted in
  `.claude/skills/sweep/SKILL.md` under *It runs unattended*. Read the limits there.
- **Being able to ask is not permission to decide.** The bar is *"might a reviewer have chosen differently?"*, and
  **narrowing scope to avoid a decision counts as deciding.** Disclosure is not blessing.

## One plan, and which file it is

**The plan is `C:\Users\ihizl\.claude\plans\qsr-price-benchmarks.md`** — outside the repository, so git does not
protect it: a concurrent write is a silent overwrite. **Never find it by globbing `~/.claude/plans/`**, which holds
other projects' plans and delegated agents' notes beside it.

- A session does not revise the plan unasked, except `/sweep`, which APPENDS as each step ends.
- **Reconcile before rewriting it**: `git log --oneline` for what landed, `git worktree list` for what is in
  flight.
- **The plan carries no commit hashes and no test counts** — both change without the plan being touched.

## Committing
- All changes happen on the task branch. Never commit to `master` directly.
- Commit after a coherent unit of work. Run `dotnet format` before committing (`/wp-verify` does).
- Subject tags: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`.
- **No separate `docs:` commit for a package's own archive entry** — it is that commit's body.

## Test loop after committing
Run the suite after each commit. If it fails: assume the CODE broke something real and fix that first. Modify or
remove a test only when it demonstrably tests superseded behaviour, and say so in the commit message. Never weaken
a test to make the suite pass without stating why. After four failed attempts, stop and report.

## What makes a test evidence
- **Revert-confirm-red on every new test**, against the ACTUAL defect it claims to catch, not a convenient one.
- **Confirm the break landed** — grep the changed line — before believing the red or the green.
- **Make and unmake a break with the Edit tool**, never `sed`/`perl -0pi`, and never `git checkout` a file to undo
  one: it discards uncommitted work.
- **Run the suite more than once when a change touches timing, threading or a live site.**
- **Bound every wait** that guards against something never finishing.
- **A test asserts a requirement, never a preference** — *"if I change the visual style tomorrow, did the
  application regress? Obviously not."* No test pins a colour, spacing value or markup attribute.
- **Render UI changes and look at them.** No unit test sees a WPF binding mistake.

## One register: findings are work packages too
There is no separate list of parked findings. Anything worth keeping is a work package in the plan with two
fields: **Origin** — `finding` or a maintainer request — and **Status** — findings default to *not yet committed
to build*. A finding already refused on evidence is not a package: it goes to `settled-decisions.md` with the
measurement that refused it and what would re-open it.

## Finishing a work package
`/wp-finish` runs this:

1. **The archive prose is the commit's MESSAGE BODY** — what changed, why it was not the obvious thing, and the
   evidence — capped at about 1,500 bytes. Long reasoning goes to `settled-decisions.md`.
2. **Remove its plan row only once its branch is merged and the merge checked.** Until then the row says
   *committed, awaiting merge*.

## Delivering a build
The MSI is built by the maintainer with `build-installer.ps1`. A source zip is `git archive --format=zip -o
<outside the repo>/qsr-<branch>.zip HEAD` — it archives the committed tree, so ignored paths are excluded by
construction; uncommitted work is not in it.
