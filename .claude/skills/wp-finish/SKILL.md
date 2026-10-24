---
name: wp-finish
description: Close a work package — write its archive entry into the commit body, check the size cap BEFORE committing, commit, and report what is waiting to merge. Invoke when a package's work is done and verified.
---

# Closing a work package

**A package is not finished when its tests pass.** Run `/wp-verify` first; this is what happens after it is green.

## 1. Draft the message in a file

The commit body **is** the archive entry — there is no archive file. Subject with a Conventional-Commits tag
(`feat`, `fix`, `refactor`, `test`, `docs`, `chore`), blank line, then:

- **what changed**
- **why it was not the obvious thing** — the alternative considered and the reason it lost
- **the evidence** — the numbers `/wp-verify` produced

## 2. Measure the body BEFORE committing

```bash
tail -n +3 /tmp/qsr-msg.txt | grep -v '^Co-Authored-By' | wc -c    # must be <= ~1500
```

**Draft to about 1,200 bytes.** Check before `git commit`, not after — amending down is the slow way round. When
it is over, cut argument, never evidence; long reasoning belongs in `settled-decisions.md`.

## 3. Commit

```bash
git add -A
git commit -F /tmp/qsr-msg.txt
git show --stat --format="" HEAD
```

End the message with the `Co-Authored-By` trailer the session's attribution instructions give.

## 4. Confirm the tree is clean

```bash
git status --porcelain                                    # empty
```

## 5. Report, and stop

Say the branch name, the commit, and what was verified. Then **stop**.

- **Merging is the maintainer's**, and only when they name the branch. **A session NEVER pushes.**
- **The package's plan row stays** until its branch is merged AND that merge has been checked; it says
  *committed, awaiting merge*.
- **`/sweep` is the one exception to this section** — it merges and continues on its own authority, and says so
  in its own file.
