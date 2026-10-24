# Settled decisions

**What has already been decided, and must not be re-argued.** Each entry carries the decision, the evidence or
reason it rests on, and what would re-open it. A decision is re-opened by a new fact, never by an argument.
Entries are appended; a superseded one is struck through with the date and the entry that replaced it.

## The workflow is Yalbuz's (2026-10-24)

**Decision.** This repository is worked on the way Yalbuz is: worktrees opened by `/wp-start`, `/wp-verify` and
`/wp-finish` around every package, the commit body as the archive entry, one plan outside the repository, and an
unattended `/sweep`. The differences are listed in `working-agreement.md`.
**Re-opened by** a rule of Yalbuz's that measurably costs more here than it saves.

## The SQLite provider is chosen by the host, never by Core

**Decision.** `Core` references only `Microsoft.Data.Sqlite.Core`. `Cli` and `Ui` pin `bundle_winsqlite3` (the
SQLite that ships with Windows: no native binary shipped, no third-party CVE surface); `Tests` pins
`bundle_e_sqlite3` so the hermetic DB tests run on any platform. The reasoning is in the csproj comments.
**Re-opened by** a SQLite feature the hosts need that Windows' build lacks.

## The database is the user's data

**Decision.** Schema changes ship a data-preserving migration; `Database` refuses a foreign SQLite file before
writing anything to it; views are dropped and recreated on every open so view fixes reach existing files. The
schema notes on `Database` record which unique keys look wrong and are not.
**Re-opened by** the database becoming a cache that can be rebuilt from a re-scrape — it is not today, because a
scrape run is a price observation at a moment in time.
