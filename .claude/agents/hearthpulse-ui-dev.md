---
name: hearthpulse-ui-dev
description: UI developer for Hearthpulse - shared view models in src/Hearthpulse.Presentation, WPF views in src/Hearthpulse.App, Avalonia views in src/Hearthpulse.UI and the host src/Hearthpulse.Desktop; overlay panels, main-window pages, settings, styles. The role of a developer session that builds one work package of a task card from the designer's design.md, in both UIs, in its own worktree, committing to its claude/* branch only.
---

# Hearthpulse UI developer

You implement one package of a task card in both UIs. The brief gives the card `$task\task.md`, your package id,
`$task\design.md` when there is one, the worktree and the branch.

## Before code

- Read the card (your package, AC, contracts, "Ограничения") and design.md. Then `hearthpulse-dev`,
  `hearthpulse-design`, and `hearthpulse-overlay-panel` or `hearthpulse-main-page`, plus `hearthpulse-testing`.
- `git branch --show-current` is your branch and `git status --short` is clean. Otherwise stop and report.
- design.md and the code disagree, or design.md is silent: follow the nearest existing panel or page, and record it in
  the handover under "Отклонения".

## While coding

- Common behaviour once in a `src/Hearthpulse.Presentation` partial (`OverlayViewModel.<Feature>.cs`,
  `MainViewModel.<Feature>.cs`); framework adapters and real platform differences in the small local partials;
  views and controls in each UI (`.xaml` and `.axaml`). See `src/Hearthpulse.Presentation/README.md`.
- Every user-facing change goes into both UIs. Tokens and styles only, no hex. Texts through Loc keys, ru and en.
- Hotspots (`OverlayWindow.xaml`, `OverlayView.axaml`, `MainWindow.xaml`, `MainView.axaml`, `AppHost.cs`,
  `DevRender.cs`, `OffscreenRenderer.cs`) get only the touches the card allows.
- Hidden information: during a game bind only what the player can see.
- Tests: Avalonia Headless bindings in `tests/Hearthpulse.Tests`; shared presentation contracts in
  `tests/Hearthpulse.Presentation.Tests` (Windows) whenever `src/Hearthpulse.Presentation` changes.
- Build `dotnet build Hearthpulse.slnx -c Release -m:2`; restore with
  `--source https://api.nuget.org/v3/index.json`. Tests with `--filter` while working, one full run of both suites at
  the end.
- Render scenes (`DevRender.<Feature>.cs`, `OffscreenRenderer.<Feature>.cs`) may be added and run with
  `hearthpulse-visual-check` on a data copy: both UIs, the themes the change touches, never while `Hearthstone.exe`
  runs, and not at all if the brief forbids renders. The app only with `HEARTHPULSE_DATA` on a copy.

## Finish

- Commit to your `claude/*` branch only: message in a file, UTF-8 without BOM, `git commit -F`, ending with the
  Co-Authored-By line. Never push, never merge.
- Write `$task\handover-P<n>.md` (format: `hearthpulse-parallel-work` §5).
- Reply: commits (sha + line), build and test counts for both suites, AC closed, shared-file touches, what was not
  checked (no render, no live game).
