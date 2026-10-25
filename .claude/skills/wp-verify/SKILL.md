---
name: wp-verify
description: Verify a work package before reporting it — build, warning inventory, formatter and full test suite, with the measurement traps baked in. Invoke before claiming any change is green.
---

# Verifying a work package

**Every step here is written the way it is because the obvious way measures nothing.** Run all of it from the
worktree. Report one line at the end: the numbers, not an impression.

## 1. Build, into a file

```bash
dotnet build --no-incremental > /tmp/qsr-build.txt 2>&1; echo "exit=$?"
```

`--no-incremental` because an incremental build re-emits warnings only for projects it rebuilt, so it
under-reports. **This builds the solution, which includes the WiX installer project** — if that one alone fails
for want of the WiX SDK, build the four C# projects by name and say so.

## 2. Errors

```bash
grep -c ': error ' /tmp/qsr-build.txt
```

**`: error ` — with the colon — never `error CS`** (a NuGet `NU1605` is missed by that) **and never a bare
` error `**: the build output is localized (`Hata`/`Uyarı`), and a Turkish warning message can carry an English
parameter name `error` inside its prose. **The exit code is the primary signal and the grep is the diagnosis.**
An `MSB4018` about a locked file in `obj/` is a stale build node: `dotnet build-server shutdown` and retry.

## 3. Warning inventory, deduplicated by SITE AND CODE

```bash
grep -oE '[A-Za-z0-9_]+\.cs\([0-9]+,[0-9]+\): warning [A-Za-z]+[0-9]+' /tmp/qsr-build.txt | sort -u
```

**Never `cut` a warning line before deduplicating it** — two codes can sit at one site, and cutting collapses
them into a drop that did not happen. **The baseline is ZERO sites** (WP-S4): every rule `.editorconfig` turns
on was fixed or suppressed at its site with a reason when it landed. Zero is checkable — **any warning at all is
this package's to explain**, and the repair is the code or a site-level `SuppressMessage` with a justification,
never a raised baseline.

## 4. Formatter, into a file that you then READ

```bash
dotnet format --verify-no-changes --exclude Installer > /tmp/qsr-format.txt 2>&1; echo "exit=$?"
grep -cE 'WHITESPACE|ENDOFLINE|IDE[0-9]+|CS[0-9]+' /tmp/qsr-format.txt
```

**Never `dotnet format … | tail -3; echo $?`** — that reports `tail`'s exit code. **The baseline is exit 0 and
a count of 0** (WP-S3): alignment whitespace was formatted away when `.editorconfig` landed, so a non-zero result
is this package's slip. Run `dotnet format whitespace --exclude Installer` (and `style` for IDE fixes) to repair
it rather than hand-editing.

## 5. Line endings, if anything was edited with `sed`

`sed -i` under MSYS strips CRs. The index is LF under `text=auto`, so git normalises on add — but check
`git diff --stat`: a file showing every line changed has had its endings flipped, and that diff is noise.

## 6. The suite, captured to a file

```bash
dotnet test --no-build > /tmp/qsr-test.txt 2>&1; echo "exit=$?"
grep -cE '\[FAIL\]' /tmp/qsr-test.txt          # 0, or read the names below
grep -E '\[FAIL\]' /tmp/qsr-test.txt
tail -3 /tmp/qsr-test.txt
```

**Capture to a FILE and grep `[FAIL]`; never pipe the run straight to `tail`.** The summary says how many failed
and never which, and an intermittent failure you cannot name is one you cannot fix. Record the name BEFORE
re-running anything.

**The suite scrapes live sites.** The `Category=Integration` tests (`Tests/ChainWebsiteTests.cs`,
`Tests/TgMenuTests.cs`) need the internet and a Playwright browser. When one fails, run the hermetic half alone —

```bash
dotnet test --no-build --filter "Category!=Integration" > /tmp/qsr-hermetic.txt 2>&1; echo "exit=$?"
```

— and re-run the failing live test once. A live red that the package's diff cannot reach, on a hermetic green,
is the site; say so with the test's name rather than calling the package red or green.

**ONE skip is the baseline and is not drift**: `UiTests/WindowCapture.cs`, a capture harness that produces images
to LOOK at rather than something to assert, gated on `QSR_CAPTURE_DIR`. The suite reports two summary lines now,
one per test project; read both.

**A package that changes anything under `Ui/` runs the capture and looks at it** — no test sees a WPF layout:

```bash
QSR_CAPTURE_DIR="$(cygpath -w "$PWD")\\TestResults\\artifacts" dotnet test UiTests --no-build
```

It writes one PNG per tab to the gitignored `TestResults/artifacts`, against a throwaway database (it asserts
that the user's own database is untouched). The window appears on screen for a few seconds while it runs.

## 7. A failed build means the rest never compiled

When one project fails, everything downstream is skipped — **their silence is not a clean result.**

## Reporting

One line: build exit and errors, distinct warning sites against `master`'s, formatter exit and count, suite
passed/failed/skipped (and which half any failure was in). **Never `git checkout` a file to undo an experiment.**
