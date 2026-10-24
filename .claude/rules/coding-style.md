---
paths: ["**/*.cs"]
---

# C# Aesthetics (MANDATORY — apply to every edit)

Adopted from Yalbuz on 2026-10-24. **Rules 1, 2, 5, 7, 8, 10, 11 and 13 are build-enforced** by `.editorconfig`
(`EnforceCodeStyleInBuild` is on; rule 13 through Roslynator's `RCS1211`), and the solution builds at **zero
warnings**, so any warning is new and the change that raised it owns it. **Rules 3, 4, 6, 9 and 12 are
reviewer-carried**: no analyzer prefers `as` to a cast, exception filters to catch-and-retest, or an honest name to
a vague one, and nothing checks for a trailing blank line or a top-level-statements `Program`. A site that cannot
comply is suppressed AT the site with `[SuppressMessage(..., Justification = "...")]`, never in a global file.

1. **Never use `var`.** Explicit types, preferring target-typed `new()`: `StringBuilder builder = new();`
2. **Always use curly brackets** for all control flow, even single-line blocks.
3. **No trailing empty lines** at the end of `.cs` files.
4. **Console apps use a classic `Program` class with `Main`.**
5. **Pattern matching over `switch` statements** used for type or shape dispatch — `if (x is Foo foo)`. Switch
   *expressions* are pattern matching and stay welcome.
6. **`as`, never an explicit cast**, when you want the nullable reference; `if (x is Foo f)` when you test and
   use in one step.
7. **Collection expressions.** `[]`, not `new List<T>()`, `Array.Empty<T>()` or `new[] { … }`; `[.. source]` to
   materialise. `IReadOnlyDictionary<,>` cannot be built from one, so a dictionary stays a `new`.
8. **Range and index operators** where they read better: `text[..200]`, `url[(at + 1)..]`.
9. **Exception filters over catch-and-retest.** `catch (Exception error) when (error is IOException or
   HttpRequestException)`. Not only style: **a `when` filter that does not match never unwinds the stack**, so
   the exception stays where it was thrown for the debugger and the log.
10. **File-scoped namespaces**, and **block-bodied members** — `get { return _x; }`, not `=> _x`. Test helpers
    are exempt from the block-bodied half.
11. **Primary constructors wherever one is possible** — possible meaning the constructor's work is expressible as
    field initializers. A captured parameter is a hidden MUTABLE field, so keep an explicit `private readonly`
    field where immutability matters, and **check the parameter's name against every member's parameters before
    capturing**: a method parameter of the same name silently shadows it.
12. **Call a spade a spade, without being verbose.** A name says what the thing IS in the plainest true word, and
    each word must rule something out. Naming a product (`Playwright`, `Tg` for Tıkla Gelsin) is right.
13. **No `else` after a branch that exits.** When every preceding arm ends in `return`/`throw`, drop the `else`.
    An `else if` dispatch chain where one arm `break`s or `continue`s is not this rule's business.

Two interactions: rule 6 replaces a cast, not an `is`-pattern; rule 7 overrides rule 1 for collections only
(`List<string> x = [];`).
