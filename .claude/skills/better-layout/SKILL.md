---
name: better-layout
description: Use when setting or reviewing grouping, alignment, spacing, reading order and adaptive structure of a Hearthpulse main-window page or overlay panel in WPF and Avalonia, so it holds at the minimum window size, in Avalonia compact, at every overlay scale and with long Russian text. Triggers - "вёрстка", "отступы", "не влезает", "обрезается", "русский текст длиннее", "layout", "spacing", "compact".
---

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-layout/SKILL.md` at `592aedc`,
> MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: CSS, container
> queries, safe areas and RTL replaced by WPF/Avalonia panels, the project's page grid, compact mode, overlay scale and
> Russian text growth.

# Layout

This skill sets grouping, alignment, spacing and adaptive structure in both UIs. It then stress-tests that structure
by resizing it, scaling the overlay and switching the language to Russian.

Use the project's numbers first: page margins `40,0,40,120`, `24` between cards, card padding `22,18`, settings content
`MaxWidth="780"`, the overlay's 1920×1080 reference surface (`hearthpulse-design`). The other numbers here apply only
where the project has none. A finding needs a failure you can show, such as a clip, an overlap or a misread group. A
value that differs from these starting points is not one.

Hit areas and focus belong to `better-accessibility`. Radius, surfaces and motion belong to `better-ui`. Trimming and
wrapping mechanics belong to `better-typography`. The words belong to `better-writing`.

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

## Group with space, not lines

Space groups first, a `Card` / `card` or `Row` plate second, a hairline (`Border` brush, the `Wave` rule) last and only
where space alone cannot carry the structure. The gap between groups is at least twice the gap within one: `8` inside
a group means `16` or more between groups. Recipes are in
[grouping-and-alignment.md](grouping-and-alignment.md#group-with-space-not-lines).

## Keep controls distinct from content

Every interactive element has a shape the theme gives controls: `GhostButton`, `PrimaryButton`, `IconButton`,
`Segment`, `Switch`, a `RowButton` plate with a hover layer. A control styled like the text beside it does not read
as a control, and a `Pill` shaped like the buttons beside it collects dead clicks. See
[grouping-and-alignment.md](grouping-and-alignment.md#keep-controls-distinct-from-content).

## Align to shared edges

Pick a small set of edges and put everything on them: the page margin, the card padding, one indent step per level of
nesting. Numbers in columns align right, text left. See
[grouping-and-alignment.md](grouping-and-alignment.md#align-to-shared-edges).

## Order by importance

The most important content sits at the top and on the left. Within a row, the identifying content (card, hero, deck
name) leads and metadata and actions trail. Tab order follows the element order, so never reorder visually with
`Grid.Column` tricks against the source order. See
[grouping-and-alignment.md](grouping-and-alignment.md#order-by-importance).

## One primary action per area

Each area gets at most one `PrimaryButton` / `ghost primary` (`hearthpulse-design`). Put secondary actions behind a
menu once there are more than three. `better-colors` owns how the gold fill marks it.

## Hint at hidden content

Every scrolled or collapsed region has a visible cue: a scrollbar, a row cut by the edge, a disclosure that names
what it hides ("Ещё 12 колод", not "Ещё"). A trimmed value the player needs has a way to the full text: a `ToolTip`,
a wider column on hover or a detail page. Trimming mechanics are `better-typography`'s. Recipes are in
[spacing-and-adaptivity.md](spacing-and-adaptivity.md#hint-at-hidden-content).

## Borderless controls need more clearance

Without a project value, start with `12` between bordered or filled controls and `24` between the visible glyphs of
borderless text and icon buttons. Dense rows may use less as long as hit areas never overlap. See
[spacing-and-adaptivity.md](spacing-and-adaptivity.md#borderless-controls-need-more-clearance).

## Keep the reserved zones clear

The main window reserves its bottom `120` for the floating live bar; page content ends above it. The overlay keeps the
game's own UI clear: hero portraits, the hand, End Turn, Discover cards and the BG tavern row. Panels of the same mode
never share a spot (`hearthpulse-overlay-panel`). See
[spacing-and-adaptivity.md](spacing-and-adaptivity.md#keep-the-reserved-zones-clear).

## Hold structure until it breaks

Keep the wide layout until it stops fitting, then collapse. Avalonia does this with the `compact` class below 900 px
(`.wide` elements hide, choice rows stack); WPF stops at its minimum window size instead. Test the smallest and
largest sizes first. See [spacing-and-adaptivity.md](spacing-and-adaptivity.md#hold-structure-until-it-breaks).

## Plan for growth and clipping

Russian strings run 15-30% longer than English, and a one-word button label grows the most. Card, hero and deck names
follow the card locale and can be longer still. Size text containers with `MaxWidth` and `MinHeight`, never a fixed
`Width` or `Height`, and let rows wrap. Check every changed view in ru and en.

Never park a needed action where resizing, scaling or scrolling clips it. Keep it in the normal flow or in fixed
chrome. See [spacing-and-adaptivity.md](spacing-and-adaptivity.md#plan-for-growth-and-clipping).

## Before you finish

| Detection pattern | Fix |
| --- | --- |
| `TextTrimming` or `TextWrapping` inside a horizontal `StackPanel` | A `Grid` with a `*` column, or a `DockPanel`; a horizontal `StackPanel` gives its children unlimited width |
| `Width="…"` or `Height="…"` on a `TextBlock`, `Button` or `Border` that holds text | `MinWidth`/`MaxWidth`, `MinHeight`, or let it size to content |
| A row of buttons in a `StackPanel Orientation="Horizontal"` | A `WrapPanel`, so long Russian labels wrap to a second line |
| `ColumnDefinition Width="Auto"` on a column with names | `*` with trimming, so a long name cannot push the numbers off |
| A `Margin` that matches no neighbour (`0,0,0,18` beside `0,0,0,24`) | The project step |
| A page `StackPanel` without the `40,0,40,120` margin (WPF) or `ScrollViewer Classes="page"` (Avalonia) | The page grid from `hearthpulse-design` |
| A new overlay element outside a `MovablePanel`, or nested inside another one | A direct child of `Surface` with its own `MovablePanel` and `Title` |
| Overlay `Margin` anchored to the wrong edge (`Left`/`Top` for something near End Turn) | Anchor to the edge you mean, `Right` + `Bottom` |
| A new element that Avalonia compact does not handle | A `.wide` class or a compact style rule |
| A `Viewbox` around text to make it fit | Wrap or trim; a `Viewbox` shrinks text below the size floor |
| A `ScrollViewer` inside a `ScrollViewer` scrolling the same way | One scroller; the inner list sizes to content |

## Reporting

**Severity.** Use `better-interface`'s scale. Clipping or overlap at the minimum window size, in compact, at overlay
scale 0.6 or 1.6 or with Russian text is `M` on sight, as are truncated content with no way to the full value, content
past a scroll edge with no cue, an overlay element outside a `MovablePanel` and a panel over the game's own controls.
Harm to hierarchy, reading order or adaptability is `m`; isolated alignment or spacing polish is `n`.

**Verification.** From the code: panel types around text, fixed sizes, margins against the page grid, compact
handling, `MovablePanel` placement, the reserved zones. Rendered: `main-*` and `main-*-compact` PNGs, the overlay
scene and `overlay-layout.png`, each in ru and en (`hearthpulse-visual-check`, with the game closed). Report every
check you could not run as `Not verified`.

**Format.** The table in `better-interface`'s `review-format.md` without the `Domain` column, grouped under the
principle each finding violates, one row per root cause listing every location in both UIs. With nothing to report,
say «Замечаний по вёрстке нет» and list the checks.
