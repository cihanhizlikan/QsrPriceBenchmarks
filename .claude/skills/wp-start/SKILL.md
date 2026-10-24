---
name: wp-start
description: Open a work package — create its git worktree, branch from LOCAL master, and confirm a clean base. Invoke this whenever the maintainer issues a package to build, before touching any file.
---

# Opening a work package

**Run this before the first edit of any package.** `CLAUDE.md` requires a worktree for any session that changes
files; this is how one is opened correctly.

## The steps

Run from the **primary checkout** (`C:\Users\ihizl\source\repos\QsrPriceBenchmarks`), which stays on `master`
and is nobody's workspace.

```bash
git status --porcelain          # must be empty; if not, stop and report
git branch --show-current       # must be master
git worktree add .claude/worktrees/<slug> -b feature/<slug> master
git -C .claude/worktrees/<slug> log --oneline -1    # must equal master's tip
```

Name the branch for the task: `feature/<slug>` or `fix/<slug>`. A sweep step uses `sweep-<n>-<subject>` for both
the slug and the branch. **A worktree path or branch name that already exists fails `git worktree add`** — pick a
new slug rather than removing somebody's leftovers.

## Why `git worktree add … master` and not `EnterWorktree`

**`EnterWorktree` branches from `origin/master`, and local `master` is routinely ahead of it** because a session
never pushes. Naming `master` makes the base local by construction. `EnterWorktree` also cannot be called twice
in a session.

## What bites once you are in there

- **Work from the worktree path, with absolute paths.** A tool that resolves against the primary checkout edits
  `master`'s files.
- **The first build in a fresh worktree restores packages** and takes longer; that is not a hang.
- **The live tests install their own Playwright browser.** `Tests/Fixtures/PlaywrightFixture.cs` runs the
  install on first use, so the first live run in a fresh worktree downloads Chromium (~150 MB) once and is
  slow; that is not a hang.
- **Use `awk 'NR==n'` to read a line, never `sed -n 'a,bp'`** — MSYS `sed` has been off by one against `grep -n`.

## What this skill does NOT do

It does not choose the package. **Work is ISSUED, never taken** — if no package has been named, ask.
