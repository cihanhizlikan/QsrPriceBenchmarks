---
name: wp-finish
description: Close a work package — write its archive entry into the commit body, bump the version when the change ships, check the size cap BEFORE committing, commit, and report what is waiting to merge. Invoke when a package's work is done and verified.
---

# Closing a work package

**A package is not finished when its tests pass.** Run `/wp-verify` first; this is what happens after it is green.

## 1. Draft the message in a file

The commit body **is** the archive entry — there is no archive file. Subject with a Conventional-Commits tag
(`feat`, `fix`, `refactor`, `test`, `docs`, `chore`), blank line, then:

- **what changed**
- **why it was not the obvious thing** — the alternative considered and the reason it lost
- **the evidence** — the numbers `/wp-verify` produced

## 2. Version: bump it, or say why not

**Every change that ships bumps `Version` in `Directory.Build.props`** — `working-agreement.md`'s *Versioning*
section says what ships and how big a bump each kind of change is. The candidate set, and whether it is bumped:

```bash
git diff --name-only master -- Core Cli Ui Installer Directory.Build.props build-installer.ps1 ':(exclude)*.md'
git diff master -- Directory.Build.props | grep '<Version>'
```

`:(exclude)*.md` because a README under `Installer/` ships nothing. **Read what the first command lists rather
than counting it** — on the history it was tried against (2026-10-25) it caught a "test:" commit that had also
removed a method from `Core/Db/Repository.cs`, which did ship.

- **First command lists files** → read what changed in them. If any changes build output, edit `<Version>` by the
  table's size, the second command must then show the bump, and the body carries
  `Version: 1.6.0 → 1.6.1 (patch): <why this size>`. A comment- or whitespace-only change in those files ships
  nothing.
- **First command lists nothing** (or nothing that ships) → no bump, and the body says
  `No version bump: nothing that ships changed.`
- **Bump from `master`'s current version**, not the one the branch started from — if `master` moved, re-read it.

## 3. Measure the body BEFORE committing

```bash
tail -n +3 /tmp/qsr-msg.txt | grep -v '^Co-Authored-By' | wc -c    # must be <= ~1500
```

**Draft to about 1,200 bytes.** Check before `git commit`, not after — amending down is the slow way round. When
it is over, cut argument, never evidence; long reasoning belongs in `settled-decisions.md`.

## 4. Commit

```bash
git add -A
git commit -F /tmp/qsr-msg.txt
git show --stat --format="" HEAD
```

End the message with the `Co-Authored-By` trailer the session's attribution instructions give.

## 5. Confirm the tree is clean

```bash
git status --porcelain                                    # empty
```

## 6. Report, and stop

Say the branch name, the commit, and what was verified. Then **stop**.

- **Merging is the maintainer's**, and only when they name the branch. **A session NEVER pushes.**
- **The package's plan row stays** until its branch is merged AND that merge has been checked; it says
  *committed, awaiting merge*.
- **`/sweep` is the one exception to this section** — it merges and continues on its own authority, and says so
  in its own file.
