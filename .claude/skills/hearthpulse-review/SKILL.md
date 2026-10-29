---
name: hearthpulse-review
description: Use when reviewing a Hearthpulse branch, PR or another session's work before merge - read-only review from commits (git diff main...sha), severity B/M/m, and the project checklist - correctness on real logs, no hidden information, licences (HDT/Firestone/AGPL), design tokens without hex, WPF/Avalonia parity, SQLite migrations, tests and Linux CI, performance and threading, docs/skills that promise more than the code. Also use it to self-review before handing work over. Triggers - "ревью", "review the branch", "проверь ветку", "готово к слиянию?", "check before merge".
---

# Reviewing a Hearthpulse branch

This is how the reviewer session worked in the October 2026 round (reports in
`<repo>/../reference/reviews/HeartLens-feat-<branch>.md`, integration summary
`INTEGRATION.md`). Typical findings with examples: [reference/typical-findings.md](reference/typical-findings.md).
How reviews fit into parallel work: [hearthpulse-parallel-work](../hearthpulse-parallel-work/SKILL.md).
Resolve `<repo>` with `git rev-parse --show-toplevel` and check the sibling reference folder before reading it.
It is optional external material; if absent, keep the report in the agreed workspace location and note that limit.

## Ground rules

- **Read-only branch review, from commits**: `git diff main...<sha>`, `git log main..<sha>`, `git show <sha>:<path>`. No
  checkout in someone else's worktree, no edits, no commits there. Uncommitted files in their worktree are listed
  separately ("not reviewed").
- For self-review of a requested local fix, include `git diff` and new untracked files and say that the working
  changes were reviewed. This does not require a commit or permission to publish.
- Build and test from a clean copy (`git archive <sha> | tar -x -C <tmp>` or your own worktree) with a short
  `--artifacts-path` and `HEARTHPULSE_DATA` on a scratch copy.
- Every finding is verifiable: `file:line`, what the code does, a reproduction (input -> wrong output), the fix, and
  which test should pin it. If you could not check something (Linux, macOS, a live game), say so.
- Severity: **Blocker** (wrong/unsafe, must not merge: hidden-information leak, data loss, licence violation,
  CI red), **Major** (feature wrong on real data, missing in one UI, steals game clicks, no tests for the feature),
  **Minor** (edge cases, wording, perf that does not hurt yet), **Nit** (not counted). IDs `B1`, `M1`, `m1`, numbering
  continues across re-reviews. Severity may move after a manual check - say why.

## Checklist

**1. Correctness on real data**
- Replay the change over available recorded fixtures or logs from the configured Hearthstone install. Report
  missing external fixtures instead of treating an unavailable real-log check as passed. Synthetic lines often
  have the wrong order (opening hand before `TURN=1`, the Coin as `GAME_005` while real logs use skins such as
  `JAIL_COIN3` with `COIN_CARD=1`).
- Game modes: does it behave in Arena, Brawl, Twist, Duos, practice, Wild? Is `GameType` checked, not only
  `FormatType`?
- Nulls and empties: `winrate:null` shown as 0 %, one bad JSON item emptying a whole list, "never throws" methods
  that leak `TaskCanceledException`, state never reset between games.
- Documented contracts (`/// <summary>`, docs) match the code (ordering, units, ranges).

**2. No hidden information** ([hearthpulse-tracking](../hearthpulse-tracking/SKILL.md))
- Any opponent card id, creator, or deck position shown or used that the player could not see: `CREATOR_DBID`,
  entity ids followed through a shuffle, `SETASIDE` paths, ids kept after `HIDE_ENTITY`, `CHANGE_ENTITY` taken as a
  reveal, fallback icons that look like a reveal. Check the tests do not pin the leak.
- Exception: the match page of a finished game may show what the log revealed by the end (post-game part of HPR3,
  `docs/match-replay.md`). It is a finding only when that data reaches anything shown during a game.


**3. Licences** ([hearthpulse-data-sources](../hearthpulse-data-sources/SKILL.md))
- grep characteristic names, UI strings and layout constants of the new code in
  `<repo>/../reference/repos/*` (HDT, Firestone, ideas/*), after checking it exists. Structure copied element by
  element from HDT/Firestone is a finding even without copied lines. AGPL (tavern-ledger) code = blocker. MIT code
  needs its notice. Quotes of third-party tooltips/docs verbatim - rewrite.
- Combat simulator: clean-room only.

**4. Design tokens** ([hearthpulse-design](../hearthpulse-design/SKILL.md))
- In added lines: `grep -nE "#[0-9A-Fa-f]{6}|FromRgb|FromArgb|Color\.Parse|Colors\.|Brushes\." ` outside
  `Themes/Theme.xaml` / `Theme.axaml` (render code included).
- New token in **both** theme files, with a fixed-prefix key if its colour carries meaning; no duplicate keys.
- Styles/classes from the table in the design skill; animations behind `SystemParameters.ClientAreaAnimation` (and
  none unguarded in Avalonia).

**5. Parity**
- The feature exists, is bound and is reachable in both UIs; same overlay Ids, same settings, same PNG scene names.
- Common VM behavior belongs in `src/Hearthpulse.Presentation`; local App/UI partials contain platform adapters
  and intentional differences. No duplicate business/presentation implementation introduced in a local partial.
- Shared-source moves preserve the old CLR namespaces, assemblies, public members/defaults and XAML names;
  review both conditional branches and platform hooks rather than assuming one compiled flavor covers both.
- Avalonia compact mode handled; platform-specific code (click-through, pointer, files) works or is clearly marked
  "compiled only" per OS.

**6. Data and migrations**
- New history columns only appended to `HistoryStore.AddedColumns`; insert/fill/query by column name; old rows NULL;
  `HistoryMigrationTests` extended; an old app can still read the new file.
- Settings: new properties have defaults; renamed/removed properties migrated; panel Ids unchanged.
- Files written atomically (temp + move); caches bounded; no secrets or e-mail in logs.

**7. Tests** ([hearthpulse-testing](../hearthpulse-testing/SKILL.md))
- Domain logic has tests in Core; Avalonia view model and binding behavior can use the existing UI reference and
  Headless support. Shared presentation changes also run `tests/Hearthpulse.Presentation.Tests` on Windows,
  referencing both compiled variants and preserving intentional platform differences. Rule behaviour has negative tests.
- Will cross-platform cases pass on ubuntu-latest, under ru-RU, in another time zone, without `D:\Hearthstone\Logs`,
  without the network? Platform-only cases in that suite use `[WindowsFact]` / `[UnixFact]`; the separate WPF
  presentation suite explicitly runs on Windows.
- Record actual passed/failed/skipped totals. Inspect discovered cases and assertions when counts differ from
  the chosen base; old totals or arithmetic alone cannot establish coverage.

**8. Performance and threading**
- Work per snapshot publish (up to 10/s) or per log line: rebuilding lists, scanning all entities, re-reading
  history, O(n^2) per mouse move. Cache by input.
- Endless loops/threads without `Dispose` or cancellation; UI state written off the UI thread; fire-and-forget
  tasks without logging; stale async results overwriting newer ones (use a generation counter).
- Network fan-out bounded; 404s cached.

**9. Build and the rest**
- `dotnet build Hearthpulse.slnx --no-restore`: **0 errors, 0 warnings** (a new CS0618 is a finding).
- New analyzer warnings (MA*, VSTHRD*): fixed, not silenced. A new `[SuppressMessage]` needs a `Justification` that
  holds up; a rule turned off or down in `.editorconfig` needs its reason line. `MA0004` belongs to Core only.
- `--render-ui` returns nonzero on fatal startup/render errors; the `render.py` wrapper also fails when mandatory
  scenes are missing. Optional scenes with unavailable log moments are explicitly skipped. New scenes exist in
  both renderers.
- Overlay interactive areas do not cover Discover cards, the hand or End Turn.
- Docs, `docs/*.md` and `.claude/skills/*` updated and not promising behaviour the code lacks.
- Dead code left for the merge (e.g. an unused model) is listed.

## Report template (write it in the language the user reads - the October reports are Russian)

```
# Ревью: <repo> / <branch> (на <sha>)
Ветка, коммиты сверх main (<base sha>); смотрел только закоммиченное (git diff main...<branch>).
## Сводка изменений          - Core / shared Presentation / platform App+UI / render / tests, 1 line each
## Проблемы
### Blocker  ### Major  ### Minor   - ID, file:line, what happens, reproduction, fix, test
## Лицензии
## Скрытая информация (и память)
## Дизайн и паритет
## Тесты
## Незакоммиченное в worktree
## Сборка и тесты              - head, build warnings, tests X/Y, known failures, what was not run (Linux, macOS, live game)
```

Re-review after fixes: append `## Дельта: <old>..<new>` with a status table of earlier IDs (закрыт / открыт /
частично), new problems, "проверено, проблем нет", and the counts (Blocker 0, Major 1, Minor 3). The verdict per
branch: ready / ready after X / too early - a blocker or open major stops the merge.
