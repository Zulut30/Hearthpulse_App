---
name: hearthpulse-visual-check
description: Use whenever a Hearthpulse change affects what the player sees and you need proof - render the WPF and Avalonia UIs off-screen with --render-ui (always on a copy of the data folder via HEARTHPULSE_DATA), list or add render scenes (DevRender*.cs, OffscreenRenderer*.cs), compare before/after PNGs, render theme presets, and inspect PNGs for clipping, overlap, contrast, spacing and parity. Triggers - "покажи скриншот", "отрендерь", "проверь вёрстку", "before/after", "PNG", "how does the overlay look", any UI change before calling it done.
---

# Visual check with `--render-ui`

Both apps can render their main window and overlay to PNG without a game running, then exit. That is the only way
to *see* a UI change from a session - use it for every visual change, in both UIs. Token/style rules:
[hearthpulse-design](../hearthpulse-design/SKILL.md). Adding a panel or page (and its scene):
[hearthpulse-overlay-panel](../hearthpulse-overlay-panel/SKILL.md), [hearthpulse-main-page](../hearthpulse-main-page/SKILL.md).

## Never on the real data folder

Render startup suppresses live trackers, account restoration, update checks and changes to Hearthstone's
`log.config`/`client.config` in both hosts. It still reads history, may save settings and loads data/art caches.
The user's real app may be running at the same time. **Always** set `HEARTHPULSE_DATA` to a copy, excluding
`account.dat` (the signed-in session). This keeps render/cache writes isolated; it is not an offline guarantee.

## Render

Bundled script (copies the data once to `%TEMP%\hpd`, builds into the short `%TEMP%\hpa`, sets the env vars for the
child only):

```
python -B -I .claude/skills/hearthpulse-visual-check/scripts/render.py <out-dir> both
python -B -I .claude/skills/hearthpulse-visual-check/scripts/render.py <out-dir> wpf --theme Frost
```

Manual equivalent (Git Bash):

```
export HEARTHPULSE_DATA="$TEMP/hpd"          # a copy: settings.json, history.db, cache/
dotnet restore src/Hearthpulse.App --source https://api.nuget.org/v3/index.json --artifacts-path "$TEMP/hpa"
dotnet restore src/Hearthpulse.Desktop --source https://api.nuget.org/v3/index.json --artifacts-path "$TEMP/hpa"
dotnet run --project src/Hearthpulse.App --no-restore --artifacts-path "$TEMP/hpa" -- --render-ui <out>/wpf
dotnet run --project src/Hearthpulse.Desktop --no-restore --artifacts-path "$TEMP/hpa" -- --render-ui <out>/ava
```

- Takes 1-3 minutes per UI (it waits 2.5 s per page and for card art); run it in the background.
- `--artifacts-path` must be short; parallel sessions should each use their own.
- WPF is Windows only and prints nothing (WinExe) - inspect the exit code and current PNG list. Avalonia prints `wrote <path>` and
  "`<scene>`: no kept Battlegrounds log reaches this moment, skipped".
- Language: the data copy's `settings.json` decides (`"Language": "ru"` or `"en"`; `"auto"` = the machine's). Render
  each language from its own copy (`--data %TEMP%\hpd-ru`, `--data %TEMP%\hpd-en`); see `docs/localization.md`.
- No other arguments exist (no scene filter, no size). Theme: `HEARTHPULSE_THEME=<Tavern|Frost|Fel|Arcane|Ember|Tide|Shadow>`,
  honoured only while rendering, WPF only (the Avalonia app has themes, but its renderer stays Tavern).
- Data: main pages from `history.db`; overlay moments replayed from the **kept Hearthstone logs**
  (`<install>/Logs`, resolved from the configured install); counters fall back to a synthetic scene; meta and card pool come
  from the network/cache.

The script renders into a fresh temporary directory for each UI and publishes that run's PNGs, removing old PNGs
from the matching output subdirectory. Exit 0 requires a successful child process and seven nonempty mandatory
files: `main-overview`, `main-battlegrounds`, `main-standard`, `main-wild`, `main-history`, `main-settings`, and
`overlay-layout` (all `.png`). Missing log-dependent scenes are optional; missing baseline pages are failures.
Partial current output is kept for diagnosis. The manual commands above do not enforce this validation, so use a
fresh output folder and check these files yourself when bypassing the wrapper.

## Scenes (renderer code is authoritative)

| PNG | WPF | Avalonia | Source |
|---|---|---|---|
| `main-overview`, `-battlegrounds`, `-standard`, `-wild`, `-history`, `-settings` | 1260x840 window | same | `DevRender.cs` / `OffscreenRenderer.cs` page loop (Arena is not in the loop) |
| `main-settings-general`, `-overlay`, `-tracker`, `-account`, `-data`, `-about` | yes | yes | each Settings tab (`DevRender.Settings.cs` / `OffscreenRenderer.Settings.cs`) |
| `main-settings-counters` | yes | yes | Tracker tab, scrolled to the Battlegrounds counters |
| `main-constructed-tiers`, `main-constructed-top` | yes | - (no meta tabs) | `ShowConstructedTab` |
| `main-battlegrounds-heroes`, `-comps`, `-trinkets`, `-cards` | yes | yes | BG tier list tabs, scrolled to the tabs; comps/trinkets show their empty state until the stats pool runs (`DevRender.BgMeta.cs` / `OffscreenRenderer.BgMeta.cs`) |
| `main-battlegrounds-full`, `-constructed-full`, `-arena-full` | page content only | full window, tall | `DevRender.MyStats.cs` / `OffscreenRenderer.MyStats.cs` |
| `main-battlegrounds-hover`, `main-constructed-hover` | if the chart has points | same | My Stats chart hover |
| `main-compact`, `main-*-compact` | - | 420 wide | compact layout |
| `overlay-pick`, `-trinket`, `-discover`, `-game`, `-bg-counters` | 1920x1080 | same | latest kept BG log; each skipped if never reached |
| `overlay-counters` + `-panel`, `-tooltip`, `-tooltip-counted`, `-open` | 2x crops | 1x crops + `*-full` | `CounterScene` (log or synthetic) |
| `overlay-mulligan`, `overlay-mulligan-4` | if a log has a 3- / 4-card mulligan | same | `MulliganReplay.LatestAsync(..., offered)` |
| `overlay-odds`, `overlay-odds-panel` | yes (panel 2x) | yes (panel 1x) | `CombatOddsScene.Synthetic` (made-up fight with a lethal risk; `DevRender.CombatOdds.cs` / `OffscreenRenderer.CombatOdds.cs`) |
| `overlay-constructed`, `-deck-preview`, `-hand-preview` | yes | yes | `DevRender.Constructed.cs` / `OffscreenRenderer.Constructed.cs` |
| `overlay-card-pool`, `-picks`, `-hover`, `-large`, `-runes`, `-sections`, `-uncomputed`, `-browser`, `-browser-filtered` | yes | yes | `App.CardPool.cs` / `Desktop/CardPoolRender.cs` (wiki pools of BE_298 from the API cache; `-picks` = the pick hint with made-up totals, `DiscoverPicks.RenderSample`; the compact 300-wide panel beside the Discover options or a hand card - `-large` is the leftmost of four hand cards, `-uncomputed` a request at its own spot; no board scenes since the pool shows for hand cards only) |
| `overlay-layout` | always last | same | layout mode: every `MovablePanel` as a labelled placeholder at its default spot |

The total PNG count depends on the data and scenes in that commit; historical totals are not a success gate.
Report optional scenes skipped because no log reaches the moment, and investigate a scene that disappeared.

## Add a scene

Keep scenes inside the existing `try` so `Quit()`/exit still runs, and put them before `overlay-layout`. Fatal
startup/render errors and missing mandatory scenes must produce a nonzero exit. A scene that requires an
unavailable log moment may be explicitly reported as skipped; do not turn a mandatory scene failure into success.

- **Main page, WPF**: in `RenderUiAsync` (`src/Hearthpulse.App/DevRender.cs`) or a feature partial
  `DevRender.<Feature>.cs`: show the page/tab (`_main.ShowPage("Standard")`, or a new public helper on
  `MainWindow` like `ShowConstructedTab`), `await Task.Delay(2500)`, `_main.UpdateLayout()`,
  `Save(_main, Path.Combine(folder, "main-x.png"))`.
- **Main page, Avalonia**: `OffscreenRenderer.cs` `RenderAsync` or `OffscreenRenderer.<Feature>.cs`:
  `view.ShowPage(...)` (or a new public method on `MainView`), then `await Save(main, path)`.
- **Overlay, both**: build `LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true,
  GameType = ..., Constructed = view /* or Battlegrounds = ... */ }`, feed it to the overlay view model
  (`Update(snapshot, cards, settings, deck, null)`), then WPF `_overlay.RenderToFileAsync(path, 1920, 1080, hoverSlot)`,
  Avalonia `overlayView.Fit(1920, 1080)` + `Save(overlay, path)`. Prefer a real-log replay with a synthetic fallback
  (pattern: `CounterScene.FromLatestLog` / `CounterScene.Synthetic` in `src/Hearthpulse.Core/Tracking/Counters/CounterScene.cs`).
- Same PNG name in both UIs. Background colours come from theme resources, never `Color.FromRgb(...)` in render code.
- `DevRender.cs` and `OffscreenRenderer.cs` are the top merge-conflict files: add one call there and put the scene
  body in your own partial ([hearthpulse-parallel-work](../hearthpulse-parallel-work/SKILL.md)).

## Before / after

1. Render the base first: before editing, or from a clean worktree of the base commit
   (`git worktree add <short path> <base-sha>`, render there with the same data copy).
2. Render your branch into another folder with the **same** data copy (otherwise data differences show up as changes).
3. Compare:
   ```
   python -I .claude/skills/hearthpulse-visual-check/scripts/png_diff.py <before>/wpf <after>/wpf <out>/diff-wpf
   ```
   It lists new/removed scenes and the share of pixels whose maximum RGBA-channel difference exceeds 24 (small
   antialiasing noise is ignored), and writes `before | after | diff` sheets - Read the
   sheet to see the change. Unexpected changes outside your area are regressions until explained.
4. The same script on `<out>/wpf` vs `<out>/ava` is a quick **parity** check: expect differences from fonts,
   Avalonia's nav group markers and crop scale (WPF counter crops are 2x, `*-full` pages differ in size), not missing
   blocks.

## What to look at in each PNG

Open the PNGs with the Read tool (they are images). For each one touched by the change:

- **Clipping**: text cut at the right edge or ellipsized where it should wrap; numbers like "1,204" or long card
  names - check both English and Russian UI text, plus card/hero/deck names in the card locale. Cyrillic text can
  be longer; also check tooltips leaving the 1920x1080 surface and the bottom 120 px reserved for the floating live bar.
- **Overlap**: panels on top of each other in `overlay-layout.png` - fine only between panels that never show in the
  same mode (e.g. `card-pool` vs `bgbar`); text over art; the live bar covering content.
- **Contrast**: parchment `Text` on wood surfaces; `Muted` for secondary text (`Faint` only for separators, glyphs and disabled things); semantic colours
  (`Positive`/`Negative`/`Tier*`/`Rarity*`) still distinct.
- **Spacing**: the same margins as neighbouring cards (24 between cards, page margin 40), aligned baselines in rows,
  segment bars right-aligned in headers, icons centred in their Scallop.
- **States**: empty state (no games, no data), loading, long lists, hover (`*-hover`), compact width (Avalonia
  `*-compact`, below 900 px).
- **Themes**: WPF with at least one non-default preset (`--theme Frost` or `Arcane`): gold accents must recolour,
  meaning colours must not. A hex literal in a view shows up here as a Tavern-gold spot in a blue theme.
- **Parity**: the same feature, the same information in both UIs (allowed gaps are listed in
  [hearthpulse-design](../hearthpulse-design/SKILL.md)).

Report what you rendered (UIs, theme, data copy), which PNGs you inspected, and what you could not render (e.g. no
kept log reaches the Discover moment) - do not claim a scene looks right if it was skipped.

## Script regression checks

`python -B -I -m unittest discover -s tools/skill_checks -v` uses Pillow synthetic images and mocked child
processes to check RGB/alpha differences, noise thresholds, current-scene validation, stale output, failed
processes and data/output isolation. It does not launch either GUI or access the network. These checks do not
replace opening the actual rendered PNGs.
