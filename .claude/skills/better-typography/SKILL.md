---
name: better-typography
description: Use when setting or reviewing how text renders in Hearthpulse's WPF and Avalonia views - the type ramp styles, font weights, line height, wrapping and trimming, tabular numbers, case and Russian/English punctuation. Triggers - "шрифт", "размер текста", "обрезается текст", "перенос строк", "цифры прыгают", "typography", "text trimming".
---

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-typography/SKILL.md` at
> `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> CSS properties replaced by WPF/Avalonia text properties; the project's fonts and ramp; web-only rules (font loading,
> `rem`, iOS input zoom, `text-box`, bidi) removed; Russian punctuation.

# Typography

This skill sets and reviews how text renders in both UIs: the ramp, weights, line height, wrapping, trimming,
numbers, case and punctuation. It writes every fix with the project's styles, and the
[cheat sheet](xaml-cheat-sheet.md) maps each property between WPF and Avalonia.

Fonts, the ramp and their styles are defined in `hearthpulse-design`; this skill applies them and does not restate
the table. The words belong to `better-writing`, accessible heading structure to `better-accessibility`, room for
the text to `better-layout`. Measure contrast with `better-colors`.

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

## Measured, not preferred

Some rules here are exact: a style from the ramp instead of a local `FontSize`, a weight the font ships, tabular
numbers on changing values, a way back to trimmed text, typographic quotes and dashes. They are findings when missed.
Letter-spacing and line-height tuning are heuristics: report them only where they break the project's own ramp.
Never propose a new typeface unless the task asks for one.

## Three fonts, one ramp

`UiFont` for text, `DisplayFont` (Montserrat) for headings and numbers, `IconFont` (Material Design Icons) for glyphs
(`hearthpulse-design`). No fourth font and no image text. Text takes a style or class from the ramp (`Display`, `H2`,
`Label`, `Caption`, `Stat`; Avalonia `display`, `h2`, `label`, `caption`, `stat`, `number`). A local `FontSize`,
`FontWeight` or `FontFamily` on a `TextBlock` that a ramp style covers is a finding; a size the ramp lacks is a
question for `hearthpulse-design`, not a new local value.

## Weights the font ships

Montserrat ships Bold and ExtraBold only (`Assets/Fonts/`). `DisplayFont` with `Normal`, `Medium` or `SemiBold`
renders a synthesized or substituted face. `UiFont` (Segoe UI Variable on Windows, Inter elsewhere in Avalonia) carries
the regular range. Below 18 DIP, use `Normal` or heavier; `Light` and `Thin` only at 28 DIP and up, checked against the
surface even there.

## Heading sizes descend with level

Page title `Display`, section `Ornament` heading or `H2`, then `Label` above a group. A subordinate heading never
outsizes its parent, and a heading is never smaller than the body text beside it unless it is a deliberate `Label`.

## Line height by role

Leave line height at the font's default for single lines. Text that wraps to three or more lines needs at least 1.4
times its size: WPF `LineHeight` with `LineStackingStrategy="BlockLineHeight"`, Avalonia `LineHeight`. Never set a
line height below the font size on text that can wrap; Cyrillic capitals and descenders clip first.

## Wrap deliberately

- `TextWrapping="Wrap"` on descriptions, empty states, errors and tooltips.
- `TextWrapping="NoWrap"` with trimming on names in rows, labels and badges, where a second line looks broken.
- `WrapWithOverflow` only where a long unbreakable string (a path, a deck code) may overflow rather than break.
- Text inside a horizontal `StackPanel` never wraps or trims; `better-layout` owns that fix.

Keep interface text left-aligned. `TextAlignment="Justify"` has no place in this UI.

## Tabular numbers on changing values

Timers, counters, ranks, win rates and numeric columns keep every digit one width so nothing shifts. WPF sets
`Typography.NumeralAlignment="Tabular"` for every window through the `AppWindow` style. Avalonia sets none: a changing
number there takes `FontFeatures` with `tnum`, or the jitter is a known difference to name in the report. Figures in
prose stay proportional.

## Trim with a way back

One line: `TextTrimming="CharacterEllipsis"` with `TextWrapping="NoWrap"`, the project's default (more than 200 uses).
Several lines: Avalonia `MaxLines`; WPF has no `MaxLines`, so cap `MaxHeight` at the line height times the line count
with `LineStackingStrategy="BlockLineHeight"`. A trimmed value the player needs has a way back to the full text,
which `better-layout` owns.

## Natural case, typographic punctuation

Strings are stored in sentence case; XAML has no text transform, so an ALL CAPS label means an ALL CAPS string, which
`hearthpulse-design` rules out. Rendered text uses typographic characters: Russian «ёлочки», English “curly” quotes,
the single `…`, en dashes in ranges, em dashes for asides, a non-breaking space between a number and its unit. The
table is in [wrapping-and-punctuation.md](wrapping-and-punctuation.md#typographic-characters). Code and deck codes keep
straight characters.

## Size floors

Body text is 13 DIP (`UiFont`, the windows' base); `Label` 12 and `Caption` 12.5 are the floor for text the player
reads. In the overlay, sizes are on the 1080-high reference surface and shrink with `OverlayScale` and panel scale
down to 0.6, so overlay text the player reads in a game stays at 12 or more on the reference surface.

## Rendering mode on the window

WPF sets `TextOptions.TextFormattingMode="Display"` on its windows and the theme sets the rendering mode where it
needs it. Set text rendering once on a window or style, never per element.

## Text the player copies

A deck code, a path or an error id the player may copy is a read-only `TextBox` (WPF) or a `SelectableTextBlock`
(Avalonia), or it has a copy button. Everything else stays a plain `TextBlock`.

## Before you finish

| Pattern | Fix |
| --- | --- |
| `FontSize="…"` on a `TextBlock` that a ramp style or class covers | The style or class |
| `FontFamily="Segoe UI"`, a font file path or a new font in a view | `UiFont`, `DisplayFont` or `IconFont` |
| `DisplayFont` with `FontWeight` `Normal`, `Medium` or `SemiBold` | `Bold` or `ExtraBold`, or `UiFont` |
| `FontWeight="Light"` or `"Thin"` under 18 DIP | `Normal` or heavier |
| `LineHeight` smaller than `FontSize` on text that can wrap | 1.4 × the size, or the default |
| A WPF multi-line clamp with `MaxHeight` but no `BlockLineHeight` | Add `LineStackingStrategy="BlockLineHeight"` and a `LineHeight` |
| `TextTrimming` with `TextWrapping="Wrap"` and no height cap | Pick one: trim one line, or cap the lines |
| A changing number in Avalonia with no tabular setting | `FontFeatures` with `tnum`, or name the difference |
| `"..."`, `--`, `"…"` with straight quotes, or a hyphen in a range in a Loc string | `…`, an em dash, «» / “”, an en dash |
| A number and unit split by an ordinary space («54 %», "10 s") | A non-breaking space ` ` in the string |
| An ALL CAPS Loc string | Sentence case |
| `TextAlignment="Justify"` | Left |

## Reporting

**Severity.** Use `better-interface`'s scale. Unreadable text, truncated content with no way back and text clipped at
the minimum size, compact width, overlay scale or in Russian are `M` on sight. A broken ramp or heading hierarchy is
`m`; isolated polish is `n`.

**Verification.** From the code: styles against the ramp, weights against the shipped faces, line height and
trimming on wrapping text, tabular settings on changing numbers, punctuation in both string files. Rendered: the
changed PNGs in ru and en, compact, and the overlay scene (`hearthpulse-visual-check`, with the game closed). Report
every check you could not run as `Not verified`.

**Format.** The table in `better-interface`'s `review-format.md` without the `Domain` column, grouped under the
principle each finding violates, one row per root cause listing every location in both UIs. With nothing to report,
say «Замечаний по типографике нет» and list the checks.
