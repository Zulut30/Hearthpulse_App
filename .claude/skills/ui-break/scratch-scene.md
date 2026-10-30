# The scratch scene

> Hearthpulse's replacement for the throwaway route/page of [jakubkrehel/skills](https://github.com/jakubkrehel/skills)
> `skills/break/SKILL.md`, `skills/state-machine/SKILL.md` and `skills/variant/SKILL.md` at `592aedc` (MIT, © 2026 Jakub
> Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md)): a temporary `--render-ui` scene instead of a web page.
> Shared by `ui-break`, `ui-states` and `ui-variant`.

A desktop app has no scratch URL. The harness is a temporary render scene in each UI that builds the real component,
feeds it fixture data and writes one PNG per case. `hearthpulse-visual-check` owns rendering; this file only says how
the scratch scene differs from a real one.

## Files

| UI | Scene body | One call from |
| --- | --- | --- |
| WPF | `src/Hearthpulse.App/DevRender.Scratch<Name>.cs` (`partial class App`) | `RenderUiAsync` in `DevRender.cs` |
| Avalonia | `src/Hearthpulse.Desktop/OffscreenRenderer.Scratch<Name>.cs` | `RenderAsync` in `OffscreenRenderer.cs` |

Put the call inside the existing `try`, before `overlay-layout`. Catch and print the scratch scene's own errors so a
failure there cannot fail the mandatory scenes. Name every PNG `scratch-<component>-<case>.png`, the same names in
both UIs.

## The real component, untouched

Build the component the product ships, from the project, and change nothing in it:

- **An overlay panel:** feed `LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true,
  GameType = …, Constructed = … }` (or `Battlegrounds = …`) through the overlay view model's `Update(...)`, then save
  with WPF `_overlay.RenderToFileAsync(path, 1920, 1080, 0)` / Avalonia `overlayView.Fit(1920, 1080)` + `Save(...)`,
  exactly as a real scene does.
- **A main-window piece** (a card, a row template, an extracted control): create it, set its `DataContext` to the
  shared view model or display records from `src/Hearthpulse.Presentation`, host it in a container of the width it has
  in production, and save it with the helpers the renderer already has (`Save`, `SaveFull` in WPF; `Save` in
  Avalonia).
- **A whole page:** `ShowPage(...)` on the real window, as the page loop does.

Labels, container widths and fixture data are all a scratch scene adds. No styles, fonts, colours or themes of its own:
a component seen under any of those is a different component.

## Data at the boundary

Feed data where the component receives it: a `LiveSnapshot`, a view model's input records, a copy of `AppSettings`.
Never edit the component to accept fixtures; if there is no way in without an edit, say so and ask before adding a
seam.

Make fixtures look like the product: real card ids and names from the card cache, the deck sizes and game counts
players have, real-length Russian and English text. Three rows of "Test" make every case look fine.

An in-game fixture holds only what the player could see at that moment. If the component then shows something the
player could not see, that is a `B` finding about the component, not a fixture problem.

## Themes and languages are runs, not scene content

Do not re-theme or translate inside the scene. Render again instead: WPF `--theme <Preset>`, a ru and an en data copy
(`--data`), as `hearthpulse-visual-check` describes.

## Run

1. Check the game is closed: `Get-Process Hearthstone -ErrorAction SilentlyContinue` prints nothing. If it runs, stop
   and say so.
2. Render both UIs in the background:
   `python -B -I .claude/skills/hearthpulse-visual-check/scripts/render.py <out-dir> both`.
3. Open the `scratch-*` PNGs with the Read tool.

## Never committed, removed in one step

The scratch scene is a harness, not a feature. It never goes into a commit: stage files by explicit path, and check
`git status` before every commit. Remove it only on the owner's word: delete the two `*.Scratch<Name>.cs` files and the
two call lines, then `git grep -n "Scratch"` and `git status` must show nothing of it.
