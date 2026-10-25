---
name: wp-start
description: Open a work package — create its git worktree, branch from LOCAL main, and confirm a clean base. Invoke this whenever the maintainer issues a package to build, before touching any file.
---

# Opening a work package

**Run this before the first edit of any package.** `CLAUDE.md` requires a worktree for any session that changes
files; this is how one is opened correctly.

## The steps

Run from the **primary checkout** (`C:\Users\ihizl\source\repos\QsrPriceBenchmarks`), which stays on `main`
and is nobody's workspace.

```bash
git status --porcelain          # must be empty; if not, stop and report
git branch --show-current       # must be main
git worktree add .claude/worktrees/<slug> -b feature/<slug> main
git -C .claude/worktrees/<slug> log --oneline -1    # must equal main's tip
```

Name the branch for the task: `feature/<slug>` or `fix/<slug>`. A sweep step uses `sweep-<n>-<subject>` for both
the slug and the branch. **A worktree path or branch name that already exists fails `git worktree add`** — pick a
new slug rather than removing somebody's leftovers.

## In the Claude desktop app: the session already HAS a worktree

**A desktop-app session starts inside a worktree the app made for it** (under `.claude/worktrees/`, on a
`claude/…` branch), and **a hook refuses edits to any other worktree's files**. So the recipe above produces a
worktree this session cannot edit — measured 2026-10-24, sweep step 1: the `Edit` was refused with *"Do not write
to other worktrees' files from this session"*. Do not route around that; branch inside the worktree you are in:

```bash
git status --porcelain                         # must be empty
git switch -c feature/<slug> main            # local main, never origin/main
git log --oneline -1                           # must equal main's tip
```

- **Merge from the primary checkout**, where `main` is checked out:
  `git -C <primary checkout> merge --no-ff feature/<slug>`. A session cannot check `main` out itself, because the
  primary checkout already has it.
- **Between packages, `git switch --detach main`** in the session's worktree, then delete the merged branch by
  its literal name and open the next one from `main` again.
- **When the run is over, fast-forward the session's own branch** (`git switch claude/… && git merge --ff-only
  main`) so the app shows the session's branch level with what landed.

Isolation is the same either way — one writer per worktree, `main` touched only by merges — which is what the
worktree rule exists for.

## Why `git worktree add … main` and not `EnterWorktree`

**`EnterWorktree` branches from `origin/main`, and local `main` is routinely ahead of it** because a session
never pushes. Naming `main` makes the base local by construction. `EnterWorktree` also cannot be called twice
in a session.

## What bites once you are in there

- **Work from the worktree path, with absolute paths.** A tool that resolves against the primary checkout edits
  `main`'s files.
- **The first build in a fresh worktree restores packages** and takes longer; that is not a hang.
- **The live tests install their own Playwright browser.** `Tests/Fixtures/PlaywrightFixture.cs` runs the
  install on first use, so the first live run in a fresh worktree downloads Chromium (~150 MB) once and is
  slow; that is not a hang.
- **Use `awk 'NR==n'` to read a line, never `sed -n 'a,bp'`** — MSYS `sed` has been off by one against `grep -n`.

## What this skill does NOT do

It does not choose the package. **Work is ISSUED, never taken** — if no package has been named, ask.
