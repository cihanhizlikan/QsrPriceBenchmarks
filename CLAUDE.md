# CLAUDE.md — QSR Price Benchmarks

QSR Price Benchmarks scrapes quick-service-restaurant chains' own websites and their Tıkla Gelsin (TG) listings,
geocodes and matches the two sets of locations, stores every scrape run in a SQLite database the user chooses, and
exports price comparisons to Excel. One `Core` library is driven by two hosts — a console app (`qsr`) and a WPF
app (`qsr-ui`) — and shipped to non-technical users as a self-contained MSI. See `README.md`.

**The working method is Yalbuz's, adopted 2026-10-24**: worktrees, three package skills, a commit body as the
archive entry, one plan outside the repository, and an unattended `/sweep`. Where this file and Yalbuz's disagree,
this one is the ruling for this repository — the differences are deliberate and listed in
`working-agreement.md` under *Where this repository differs from Yalbuz*.

## Critical rules
- The assistant runs locally with **dotnet and git on PATH** — verify changes with `dotnet build`/`dotnet test`
  through `/wp-verify` instead of asking the maintainer to build and report back.
- **The SQLite database is the USER'S DATA, not disposable app state.** It holds every scrape run they collected,
  at a path they chose. So a schema change ships a data-preserving migration (`Database.MigrateLegacySchema` is
  the existing shape, `DatabaseMigrationTests` the guard), and nothing ever deletes or drops a table in a
  database the session did not create itself. This is the opposite of Yalbuz's `yalbuz.db` rule, on purpose.
- **Never work directly on `master`** — only branch off its current tip or merge a finished branch into it. If
  `master` is checked out and new work is requested, branch off it before touching anything.
- **Work in a worktree.** Any session about to change files opens one with `/wp-start` and works there — not in
  the primary checkout, even for a one-line fix. A session doing only a merge, or only revising the plan, stays
  in the primary checkout.
- **A work package runs through three skills, and they are not optional.** `/wp-start` opens its worktree
  (branching from LOCAL `master`), `/wp-verify` builds, counts warnings, runs the formatter and the suite with
  the measurement traps baked in, and `/wp-finish` writes the archive entry into the commit body, checks the size
  cap before committing, and stops without merging. A session invokes each at its moment; the maintainer does not
  have to type them.
- **Every change that ships bumps the version.** `Version` in `Directory.Build.props`, in the package's own commit,
  sized patch / minor / major by `working-agreement.md`'s *Versioning* section; docs, tests and workflow files
  ship nothing and do not bump. `/wp-finish` runs the check.
- **The suite touches the live internet.** `ChainWebsiteTests` and `TgMenuTests` (trait `Category=Integration`)
  scrape real sites, so a red there can be the site rather than the code. `/wp-verify` says how to tell which.
- **A session never pushes.**

## Reference
- @.claude/context/structure.md — solution layout, what lives where, the placement rule
- @.claude/context/working-agreement.md — the maintainer's durable expectations, branching, worktrees, the plan,
  committing, what makes a test evidence, and how a package is finished

[settled-decisions.md](.claude/context/settled-decisions.md) — **what has already been decided and must not be
re-argued.** A decision there is re-opened by a new fact, never by an argument.

**The plan is one file OUTSIDE the repository**: `C:\Users\ihizl\.claude\plans\qsr-price-benchmarks.md`. It is
named here because nothing else names it, and a session that writes findings anywhere else has built a second
register. Findings are work packages in it, carrying an Origin and a Status.

**There is no `handoff.md` and no `completed-work.md`, and their absence is deliberate** — the plan is the
handoff, and a package's archive entry lives in the body of the commit that carries it, so
`git log --grep=<term>` answers *"did this already land?"*. Do not create either.

C# conventions: [.claude/rules/coding-style.md](.claude/rules/coding-style.md) (loads for `*.cs` files).
