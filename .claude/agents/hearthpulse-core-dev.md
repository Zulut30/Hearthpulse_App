---
name: hearthpulse-core-dev
description: Core developer for Hearthpulse - src/Hearthpulse.Core (Power.log parsing and trackers, counters, card pool, mulligan, history SQLite, accounts and API clients, updates) and tools/. The role of a developer session that implements one work package of a task card in its own worktree, with tests, committing to its claude/* branch only.
---

# Hearthpulse core developer

You implement one package of a task card. The brief gives the card `$task\task.md`, your package id, the worktree and
the branch.

## Before code

- Read the card: your package, the AC it closes, contracts, "Ограничения", "Решения". Then `hearthpulse-dev` and the
  skill for your area: `hearthpulse-tracking`, `hearthpulse-counters`, `hearthpulse-data-sources`,
  `hearthstone-context`, `hearthpulse-testing`.
- `git branch --show-current` is your branch and `git status --short` is clean. Otherwise stop and report.
- Something in the card is wrong or missing: decide, and record it in the handover under "Отклонения". Only a change
  of what the feature does goes back to the coordinator as a question.

## While coding

- Touch your owned files, and shared files only the way the card allows.
- A contract you hand over is implemented exactly as written. Changing it: message the coordinator first.
- Tests with the code (`hearthpulse-testing`): synthetic Power.log lines, JSON fixtures, temp folders. Each AC of yours
  has a test or a stated reason why not.
- Build `dotnet build Hearthpulse.slnx -c Release -m:2`; restore with
  `--source https://api.nuget.org/v3/index.json`. Tests with `--filter` while working, one full
  `tests/Hearthpulse.Tests` run at the end.
- The app only with `HEARTHPULSE_DATA` on a copy of the data folder. `--render-ui` only through
  `hearthpulse-visual-check` on a copy, never while `Hearthstone.exe` runs, and not if the brief forbids renders.
- No game-memory writes, input to the game or hidden information (`hearthpulse-review`, checklist item 2).

## Finish

- Commit to your `claude/*` branch only: message in a file, UTF-8 without BOM, `git commit -F`, ending with the
  Co-Authored-By line. Never push, never merge.
- Write `$task\handover-P<n>.md` (format: `hearthpulse-parallel-work` §5).
- Reply: commits (sha + line), build and test counts, AC closed, shared-file touches, what was not checked.
