---
name: ui-variant
description: Build three Hearthpulse variants of one piece of UI that differ on one axis (structure, density, emphasis, type or voice), render each in its real page or overlay context to PNG, present the tradeoffs and build the one the owner picks in both UIs. Owner-invoked.
disable-model-invocation: true
---

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/variant/SKILL.md` at `592aedc`, MIT,
> © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: renamed from
> `variant`; the URL picker (`picker.md`, not imported) became one PNG per variant from a scratch `--render-ui` scene;
> the floor is `better-interface`'s escalation triggers; promotion follows the project's page and panel recipes.

# Variants

This skill takes one described piece of UI and builds three versions that differ on purpose. Each renders in the real
page or overlay surface to its own PNG, so the owner can compare them side by side and choose.

It produces candidates and never ranks them. Reviewing existing UI is `interface-review` and `better-interface`,
stress testing one component is `ui-break` and working through a component's states is `ui-states`.

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

## Different answers, not different tints

Each variant is a different answer to the same brief, on one axis a domain skill owns:

| Axis | Owner | What varies |
| --- | --- | --- |
| Structure | `better-layout` | Grouping, order, column count, what collapses |
| Density | `better-layout` | Spacing steps, how much fits, `Compact` behaviour |
| Emphasis | `better-colors` | Where the gold goes, what recedes |
| Type | `better-typography` | Ramp steps, weight contrast, line length |
| Voice | `better-writing` | Labels, tone, how much text |

Pick **one primary axis** and give each variant a different position on it. Secondary choices follow from it: a dense
variant may take a smaller ramp step, and that is coherence, not a second axis.

## The floor every variant clears

Before a variant is rendered for the owner it clears every escalation trigger in `better-interface`, and the owner's
rules above. The floor is identical across variants; it is not an axis and never trades against one. Where a direction
only works by breaking it, say so and drop the direction.

## 1. Scope one piece

One piece of UI per run. "The overlay" is not a piece; the BG counters panel is, and so is the deck page header. Where
the request spans several, list the candidates and ask which one.

Restate the brief in one sentence: what the piece is, where it renders, what it has to do.

## 2. Learn the ground

Variants have to look like they could ship tomorrow, so read what they stand on: `hearthpulse-design` (tokens, ramp,
styles, controls), the neighbouring views in both UIs, the task's `design.md`, and where the piece renders: which
page, which overlay spot, beside which panels, at which sizes. Hearthpulse is a dense tool used during games; that
bounds how far the boldest variant may go.

## 3. Name the axis before writing XAML

Default to three variants, five only when asked. Write the set down first, a name and an axis position each. Names
say the direction: `Quiet`, `Ledger`, `Dense`, never `Option A`.

This step is done when no two variants share a position and you can state each one's axis in a phrase.

## 4. Build each into the real surface

Each variant is its own `UserControl` beside the piece it replaces (`<Piece>.Variant<Name>.xaml`), built from the
project's styles and the same view model, with real Loc keys or plainly temporary text. The scratch scene shows the
real page or overlay surface with the piece swapped for one variant at a time, one PNG per variant:
`scratch-variant-<piece>-<name>.png`. How to build the scene and keep it out of commits is in
[scratch-scene.md](../ui-break/scratch-scene.md).

Build the variants in WPF, the shipped UI, to keep the loop short, and say so; the chosen one is built in both UIs at
promotion. Render each variant in Tavern and one non-default preset, and in ru, because a variant that fails there
fails the floor.

Give every variant real content: product-shaped names, real counts, real-length Russian. Three rows of "Test" make
every structure look good. Render full size: thumbnails distort spacing, which is usually the thing being chosen.

## 5. Present the tradeoffs and stop

Open every variant PNG; each renders, nothing is blank. Then hand the decision over:

| Variant | Axis position | Right when | Costs |
| --- | --- | --- | --- |
| Quiet | Lowest visual weight | The panel stays up all game | Least noticeable when it matters |
| Ledger | Numbers first, labels small | The player scans it every turn | Harder to read for a new player |

Give the PNG paths and the sizes and theme you judged at. Never mark a favourite. Asked directly, answer from how often
the piece is seen and from the product's character, not from which one you enjoyed building.

## 6. Promote one, delete the rest

On a choice, build that variant properly where it belongs, in both UIs, through `hearthpulse-main-page` or
`hearthpulse-overlay-panel`, with its Loc keys and a real render scene where the recipe asks for one. Then delete the
other variants, the variant files of the chosen one and the scratch scene. Search for `Variant` and `Scratch` and check
`git status` leaves nothing of the harness.

Asked for another round instead, keep the harness and run **Name the axis before writing XAML** again around the
direction the owner leaned toward.

## Before you finish

| Mistake | Fix |
| --- | --- |
| Variants differ only in an accent colour or a word | Move one to a different position on the axis, or cut it |
| Every axis varies at once | Vary one; let the rest follow |
| Judged on a blank background | Render in the real page or overlay surface |
| "Test", three rows, short English only | Real content in real quantities, in Russian too |
| The boldest variant drops the focus ring or the token rules | Clear the floor or drop the direction |
| A hex colour in a variant | Tokens only, even in a variant |
| A favourite marked in the table | State each variant's cost and let the owner choose |
| The chosen variant promoted in WPF only | Both UIs at promotion |
| Harness left after promotion | Delete it and search for `Variant` and `Scratch` |
