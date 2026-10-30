---
name: ui-break
description: Render one Hearthpulse component under every hostile scenario that can reach it (long Russian names, zero and huge lists, small sizes, overlay scale, error states) in a scratch render scene, in both UIs, and report what visibly broke. Owner-invoked.
disable-model-invocation: true
---

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/break/SKILL.md` at `592aedc`, MIT,
> © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: renamed from
> `break`; the harness page became a scratch `--render-ui` scene with one PNG per scenario; scenarios for WPF/Avalonia,
> Russian text and the overlay.

# Break

This skill takes one component and renders it under every scenario that can actually reach it, in both UIs. The PNGs
are the deliverable: a visual report the owner reads scenario by scenario, with the breaks named.

It observes rather than judges. A finding here is something that visibly broke in a PNG, named in the vocabulary of
the domain skill that owns the fix. Reviewing code against the rules is `interface-review` and `better-interface`;
exploring alternatives is `ui-variant`; working through a component's product states is `ui-states`.

Where `ui-variant` insists on the real page, this skill isolates on purpose. You are not judging how the component
looks in context; you are checking whether it defends itself when the content is worst-case.

The whole run is build, render once, look once, report. Nothing in it justifies debugging the renderer or a second
pass.

## Owner's rules

These are the owner's rules and CLAUDE.md; a session brief may narrow them (for example, no render):

- **Everything moves and resizes.** Every overlay element sits in a `MovablePanel` that is a direct child of the
  overlay `Surface`, so the player can drag, resize, fade and reset it (`hearthpulse-overlay-panel`). Main-window
  content follows the window from its minimum size up (WPF `MinWidth="1040"`, Avalonia compact below 900 px).
- **All seven themes.** Tavern, Frost, Fel, Arcane, Ember, Tide, Shadow: colours only from tokens
  (`hearthpulse-design`), and a look at WPF under at least one non-default preset.
- **Render, but never while the game runs.** Renders go through `hearthpulse-visual-check` on a copy of the data
  folder. First check that `Hearthstone.exe` is not running: `Get-Process Hearthstone -ErrorAction SilentlyContinue`
  prints nothing. If it runs, skip the render and mark those checks `Not verified: Hearthstone running`.
- **Both UIs.** The WPF app and the Avalonia preview get the same change with the same Loc keys in ru and en; the
  only allowed gaps are the ones `hearthpulse-design` lists.
- **During a game, only what the player can see.** The exceptions are the match page after the game
  (`docs/match-replay.md`) and BG opponents' MMR (`docs/bg-opponent-mmr.md`).

## 1. Scope one component

One component per run. "The Settings page" is not a component; the counters card in it is, and so is the
`MulliganPanel` or a deck row template. Where the request spans several, list the candidates and ask which one.

Restate it in one sentence: what it accepts, what it renders and where it lives (main window or overlay, which page or
panel Id).

## 2. Infer the scenarios from the component

Stress only what varies. A scenario earns a slot when the component accepts something that can take that shape in
real use. Read the component, its view model and the records it renders first.

[scenarios.md](scenarios.md) holds the axes, the values on each and the cue that says whether an axis applies. Keep
only the axes whose cue matches. A deck row gets name length and quantity, never error states; a static header with a
fixed Loc label gets container and language, never long text.

Write the kept scenarios down before building, one line each. Then name the dropped axes and why, in one line, so a
wrong inference is cheap to catch.

## 3. Build the scratch scene

One scratch scene per UI renders the component once per scenario, each PNG named for its scenario. How to build it,
feed it and keep it out of commits is in [scratch-scene.md](scratch-scene.md).

Where several scenarios fit one image, stack them in one tall PNG: a `StackPanel` with a caption `TextBlock` above each
instance, each instance in a `Border` of the scenario's width. Captions and widths are all the scene adds.

## 4. Look once

Render once with the game closed, open every scratch PNG of both UIs and note what visibly broke: "the deck name runs
under the win-rate column", never "spacing feels tight". A predicted failure is not a finding.

If the render could not run (the game is open, the build fails), say so and stop with the scenario list and the scene
files; no findings table.

## 5. Report what broke and stop

Findings as a table, broken scenarios first:

| Scenario | UI | Observed | Owner |
| --- | --- | --- | --- |
| Longest Russian card name | both | Runs under the cost column, no trimming | `better-typography` |
| Zero games | Avalonia | Blank card with no message | `better-writing` |
| Overlay scale 0.7, panel scale 0.6 | WPF | Counter label 5 DIP, unreadable | `better-typography` |

This skill owns no domain rules and issues no verdict. "Everything survived" is a complete report: list the scenarios
and the PNG paths so the owner can look. Do not pad a clean run with preferences.

Do not fix anything unasked. On a request to fix, follow the owner skill's rules, then re-render the failing scenarios.

## 6. Keep the scene until the owner is done

The PNGs and the scene are the report. Leave the scene in the working tree, uncommitted, and remove it only when the
owner says so, as [scratch-scene.md](scratch-scene.md) describes.

## Before you finish

| Mistake | Fix |
| --- | --- |
| Every axis run against every component | Keep only the axes whose cue matches, and name the ones dropped |
| A predicted failure reported as observed | Render it, or leave it out |
| A scenario PNG missing the content it was fed | The scene is broken, not the component: check the data reached the view model |
| A rebuilt lookalike in the scene | Build the real component from the project |
| The scene restyles or re-themes the component | Themes and languages are separate runs |
| Only one UI rendered | Both, the same PNG names |
| A render taken while `Hearthstone.exe` ran | Discard it; render with the game closed |
| Findings phrased as taste | What was visible in the PNG, or nothing |
| A break reported without an owner | Name the domain skill whose rules diagnose it |
| Scratch files in a commit | Stage by path; the scene never ships |
| Scene deleted in the same turn as the report | Delete only on the owner's word |
