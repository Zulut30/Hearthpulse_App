---
name: ui-states
description: Render every state of one Hearthpulse component (no data, loading, API offline, game modes, signed in or out, game not running) from fixture data in a scratch render scene, one PNG per state in both UIs, so you can work on each state. Owner-invoked.
disable-model-invocation: true
---

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/state-machine/SKILL.md` at `592aedc`,
> MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: renamed from
> `state-machine`; the throwaway page and its URL switcher (`switcher.md`, not imported) became a scratch `--render-ui`
> scene with one named PNG per state; states taken from view models, game scenes and settings.

# States

This skill takes one component and renders it in every state it can be in, in both UIs. Fixture data feeds each
state at the data boundary, every state becomes a PNG named after it, and the scratch scene is removed in one step
when the owner is done.

It is a workbench, not a review. The scene stays in the working tree while the owner works on the visuals, every state
a re-render away. Stacking hostile scenarios for a single look is `ui-break`, exploring alternative designs is
`ui-variant` and reviewing finished work is `interface-review`.

## Owner's rules

Every UI change here holds to these, whatever a skill says elsewhere:

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

One component per run: the BG counters panel, the deck page's matchup card, the account card in Settings. "Settings"
spans several, so list them and ask which one.

Restate it in one sentence: what it shows, where it renders (page and tab, or overlay panel Id) and where its data
comes from (snapshot, history, API, settings).

## 2. Find the states in the code

The states are the branches the component and its view model already have. Read both for every one:

| Kind | States |
| --- | --- |
| Data | Loading, no data yet (no games), empty after a filter, API offline or failed, one item, a typical set, a set long enough to scroll, stale data refreshing |
| Game | Hearthstone not running, in menus, mulligan, in a game, Discover open, game over; the modes it supports (Standard, Wild, Arena, BG solo and Duos, Twist, Brawl) |
| Account | Signed out, signing in, signed in, sign-in expired |
| Settings | The `Show*` flag off and on, a mode setting's values, `Compact` on and off |

A state the code cannot reach is not a state. Do not add a branch to render one. Where `design.md` shows a state the
code lacks, list it as missing and leave it out.

Write the set down before building, one line each, named the way the product talks about it (`no-games`,
`api-offline`, `bg-duos`), grouped under headings in the product's words (`Data`, `Game`, `Account`). Name the kinds
you dropped and why. Combine kinds only where the code branches on the combination, such as `signed-out-no-games`,
never as a cross product.

## 3. Build the scratch scene

One scratch scene per UI renders the component once per state. How to build it, feed it and keep it out of commits is
in [scratch-scene.md](../ui-break/scratch-scene.md). Each PNG is `scratch-<component>-<group>-<state>.png`, in the
order the states were written down, so the folder listing reads as the state list.

Render one instance per PNG in a container as wide as the component is in production; for an overlay panel, the full
1920×1080 surface with the panel at its default spot.

## 4. Feed each state at the data boundary

Supply the data where the component receives it, without editing the component. In order of preference:

1. The overlay's `LiveSnapshot` (`Scene`, `HearthstoneRunning`, `InGame`, `GameType`, the `Constructed` or
   `Battlegrounds` view) through the overlay view model's `Update(...)`.
2. The shared view model's input records or a copy of `AppSettings` with the flag the state needs.
3. The real data copy, when the state is "has history": the render already loads `history.db` from it.

Where the only way in is editing the component, say so and ask before adding a seam.

Make the data look like the product: real card ids, real-length names in both languages, the counts players have.
Loading and failed states hold still: set them directly rather than racing a timer.

## 5. Confirm every state renders, then hand over

Render with the game closed and open every scratch PNG of both UIs. Each shows its fixture, not the real data copy's
history and not a blank region. A PNG that ignores its fixture means the data never reached the view model; fix the
plumbing before handing over.

Hand over the PNG folder, the state list in order and the scene files. Stop there. Changing how a state looks is the
owner's next request, not part of this one.

## Re-render every state after each change

After a visual change, render again and look at every state: a fix for one state often breaks another.

## Remove it in one step, on the owner's word

Delete the scratch scene and any seam the owner approved, as [scratch-scene.md](../ui-break/scratch-scene.md)
describes, then search for the scene and fixture names.

## Before you finish

| Mistake | Fix |
| --- | --- |
| The component edited to accept fixtures | Feed data through the snapshot, the view model's inputs or settings; ask before a seam |
| A rebuilt lookalike in the scene | Build the real component from the project |
| The scene restyles or re-themes the component | Themes and languages are separate runs |
| A branch added to the component to show a state | Leave out states the code cannot reach; list missing ones |
| "Card 1", "Test deck", three rows | Product-shaped data in real quantities |
| Only one UI rendered | Both, the same PNG names |
| A render taken while `Hearthstone.exe` ran | Discard it; render with the game closed |
| Handed over without opening every PNG | Open every state; a blank or real-data PNG means broken plumbing |
| Visuals changed unasked | Hand over and wait |
| Scratch files in a commit | Stage by path; the scene never ships |
| Scene deleted in the same turn it was built | Remove it only on the owner's word |
