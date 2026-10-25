---
name: hearthpulse-parallel-work
description: Use when a Hearthpulse task is big enough to split across parallel Claude sessions or branches - several features at once, a feature touching Core + WPF + Avalonia + tests, an integration/merge round, or when the user says "раздели на сессии", "параллельно", "веди подсессии", "собери ветки", "порядок слияния". Covers worktree-per-branch, file ownership and the shared-file rule, handover notes, the reviewer session, merge order and the known conflict hotspots (DevRender.cs, OffscreenRenderer.cs, OverlayWindow.xaml, OverlayView.axaml, AppHost.cs, MovablePanel.cs, Views.cs).
---

# Running a big Hearthpulse task as parallel sessions

The user works this way and values it: one coordinating session, several feature sessions each in its own worktree
and branch, a read-only reviewer session, then an integration round. It worked for the October 2026 round
(counters-panel, oidc-login, mulligan-guide-v2, my-stats, deck-panel-opponent-hand, card-pool: 6 branches, one trial
merge, 738 tests, no lost work). This skill is the recipe that round settled on.

Related: [hearthpulse-review](../hearthpulse-review/SKILL.md) (what the reviewer checks),
[hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md) (PNG evidence),
[hearthpulse-testing](../hearthpulse-testing/SKILL.md) (test counts and known failures).

## 1. Split by files, not by idea

Two sessions must never edit the same lines. Before starting sessions, write down for each one:

- the branch (`feat/<feature>`) and its base SHA on `main`;
- the files it **owns** (new partials, new controls, new Core folders, new tests);
- the shared files it may touch and **how** (one call, one property, one XAML block);
- what it hands to others (an API contract, DB columns, a public hook).

Contracts between sessions go into the shared notes folder as their own file (examples from the last round:
`my-stats-history-fields.md` for `games` table columns, `player-games-upload-contract.md`, the mulligan API change in
`mulligan-v2-handover.md`). A session that needs another's work reads the contract, not the other branch.

## 2. Each feature lives in its own partial files

The codebase is already cut so that a feature mostly adds files. Follow the existing pattern instead of growing the
big files:

| Layer | Feature file pattern (existing examples) |
|---|---|
| Core | its own folder or file: `Tracking/Counters/`, `CardPool/`, `Stats/`, `Accounts/`; partial `Tracking/GameSession.CardPool.cs` |
| Overlay VM | One `src/Hearthpulse.Presentation/Overlay/OverlayViewModel.<Feature>.cs`; local `.Platform.cs` only for framework differences, WPF `.Focus.cs` for its existing feature |
| Overlay view | an extracted UserControl: `Overlay/MulliganPanel`, `OpponentHandPanel`, `CardPoolPanel`, `CardPoolBrowser` (`.xaml` / `.axaml`) |
| Main VM | One `src/Hearthpulse.Presentation/Main/MainViewModel.<Feature>.cs`; small local adapters and WPF-only `.Constructed`, `.Theme` |
| Composition | `App.CardPool.cs` (WPF), `AppHost.CardPool.cs` (Avalonia) |
| Render scenes | `DevRender.<Feature>.cs` (WPF), `OffscreenRenderer.<Feature>.cs` (Avalonia), `Desktop/CardPoolRender.cs` |

The shared file then gets one line: `UpdateCardPool(...)` in `OverlayViewModel.cs`, `_parser.AddListener(_cardPool);`
in `GameSession.cs`, one `await RenderXAsync(...)` in `DevRender.cs`.
Do not assign separate WPF and Avalonia owners to the same common VM behavior: they now edit the same source.
Assign the shared partial to one owner, and coordinate platform views/adapters separately. Changes to
`Hearthpulse.Presentation.projitems` and the host import/flavor properties belong to the integration owner;
preserve CLR namespaces and assembly identities. The shared directory is not a separate DLL.

## 3. Conflict hotspots (measured over the 9 merges into main)

| Merges touching it | File | What collides |
|---|---|---|
| 6 | `src/Hearthpulse.App/DevRender.cs`, `src/Hearthpulse.Desktop/OffscreenRenderer.cs` | every feature adds a render moment - keep both sides |
| 6 | `src/Hearthpulse.App/Overlay/OverlayWindow.xaml`, `src/Hearthpulse.UI/Overlay/OverlayView.axaml` | new `MovablePanel` blocks next to each other |
| 6 | `src/Hearthpulse.UI/AppHost.cs` | service wiring |
| 5 | `src/Hearthpulse.App/Main/MainWindow.xaml`, `src/Hearthpulse.App/App.xaml.cs` | settings blocks, wiring |
| 4 | `src/Hearthpulse.UI/Main/MainView.axaml`, `src/Hearthpulse.Core/Tracking/GameSession.cs` | listener registration |
| 3 | `OverlayViewModel.cs` (historical copies, now `Presentation/Overlay/OverlayViewModel.cs`), `Core/Tracking/Views.cs`, `Core/AppSettings.cs` | one-line hooks, record members |
| 2 | `OverlayModels.cs` (now shared under Presentation), platform `MovablePanel.cs`, `UI/Themes/Theme.axaml`, `OverlayView.axaml.cs`, tests `.csproj` | |

Traps that a text merge does not show:

- **Semantic conflicts** - two branches fixed `MovablePanel.KeepOnScreen` differently; the textual combine did not
  compile and needed a hand-written formula. When two sessions touch the same method, assign it to one of them.
- **Duplicate tokens** - two branches added the same `OverlayBadgeShade` brush to `Theme.axaml`; keep one.
- **Records** - `Views.cs` is a file of positional records; a property merged "below" can land in the next record
  (the `CardPool` property had to go inside `ConstructedView`, not into `DeckMetaStat`).
- **API drift without a conflict** - `Desktop/CardPoolRender.cs` still called the old `ConstructedMeta.LoadAsync`
  after mulligan changed it: no conflict markers, CS1739 at build. Always build after each merge.
- Dead code left on purpose for the merge (`MulliganTile`) must be deleted at merge time, as the handover says.

## 4. Session setup

- Feature branches in sibling worktrees: `git worktree add ../hp-<feature> -b feat/<feature> <main-sha>`.
  Resolve the repository with `git rev-parse --show-toplevel`; inspect `git worktree list` and the intended sibling
  path before creating a checkout instead of assuming historical worktrees exist. Claude Code sessions get
  their own `.claude/worktrees/<topic>-<hex>` on `claude/<topic>-<hex>`.
- Build outputs of parallel sessions must not collide: `--artifacts-path` with a **short** path
  (see [hearthpulse-testing](../hearthpulse-testing/SKILL.md) - long paths break `e_sqlite3` loading).
- Each session runs the app only with `HEARTHPULSE_DATA` on its own copy of the data folder.
- The brief for each session names: branch and base SHA, owned files, allowed shared-file touches, the contract
  files to read/write, "commit to your branch, do not push or merge", and the evidence expected in the handover.

## 5. Handover note (one per feature session)

Written into the shared notes folder, short, in this order:

1. Branch, worktree, commits (SHA + one line), "not pushed, not merged".
2. What was built, where (Core / WPF / Avalonia / tests).
3. **Shared-file touch points** - every line or block outside owned files, so the integrator knows what to keep.
4. Contracts or API changes other sessions must follow.
5. Evidence: `dotnet build` 0 errors / 0 warnings; tests `X passed, none failed`; real-log replay
   (`*-real-log-run.txt` / `*-live-check.txt`); PNGs from both UIs, before/after where the look changed.
6. Review findings closed / still open, and deliberate deviations from the spec.

## 6. Reviewer session

A separate read-only session reviews each branch from its commits only (`git diff main...<sha>`, no checkout, no
edits) and writes `<repo>/../reference/reviews/HeartLens-feat-<branch>.md` with
findings `B#` blocker / `M#` major / `m#` minor. Checklist: [hearthpulse-review](../hearthpulse-review/SKILL.md).
Authors close findings in a commit such as "Address the card pool review: ..." and list closed/open IDs in the
handover. Check that the sibling reference folder exists first; if absent, use the agreed workspace report
location and record it in the handover. A blocker or open major stops the merge.

## 7. Integration round

1. Create a trial branch from fresh `main` (last round: `review-integration`, worktree `hp-review-integration`).
2. Merge branches one by one, **fewest conflicts first, the most invasive last** (last round: counters-panel ->
   oidc-login -> mulligan-guide-v2 -> my-stats -> deck-panel-opponent-hand -> card-pool). Message:
   `Merge feat/<branch> (<short-sha>) into review-integration`.
3. After every merge: restore, `dotnet build Hearthpulse.slnx --no-restore` (0 errors, 0 warnings), tests. For shared
   presentation changes compile both flavors and run the additional Windows presentation contract suite.
4. Record actual passed/failed/skipped counts and inspect discovery differences against the base and branch
   changes. Counts can change when cases are consolidated or data theories grow; inspect which assertions survived
   instead of treating historical arithmetic as an acceptance gate.
5. A fix needed only because of the combination is its own commit, named as such ("... (fix folded into the
   card-pool merge)"). Better: ask the owning session to rebase/merge fresh `main` and fix it in its branch.
6. Write the round up (`reference\reviews\INTEGRATION.md` style): heads, per-branch test counts, open majors,
   per-file conflict recipes, recommended merge order.
7. Merging into `main` and pushing happen only on the user's explicit word.

## 8. Coordinating session duties

- Keep the plan: which session owns what, which contract is pending.
- Do not edit a branch another session is working in; send it a message instead.
- When a session finishes, check its evidence (diff, tests, PNGs) before calling it done.
- Long-running branches (`quality-hardening`, `upstream-heartlens-sync`) touch code everywhere: merge them early or
  rebase feature branches after them, and describe stable concepts in notes rather than line numbers.
