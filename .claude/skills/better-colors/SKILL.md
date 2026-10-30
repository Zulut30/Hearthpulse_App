---
name: better-colors
description: Use when choosing, naming or checking colours in Hearthpulse - token roles in the theme files, meaning colours that must not recolour, the seven theme presets built by ThemePalette, and measured contrast of rendered pairs in every preset (ThemeContrastTests). Triggers - "цвет", "контраст", "не видно в теме", "токен", "палитра", "тема Frost/Arcane", "colour", "contrast".
---

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-colors/SKILL.md` at `592aedc`,
> MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: CSS variables,
> OKLCH ramps, gamut and light/dark replaced by the project's theme files, `ThemePalette.Map` presets and
> `ThemeContrastTests`.

# Colors

This skill applies and audits colour in both UIs. It maps every colour to a token role, keeps meaning colours fixed
across themes and measures rendered contrast pairs in every preset.

How the theme engine works, the token list and the hex values are `hearthpulse-design`'s; this skill does not restate
them. Whether a pair is required to pass belongs to `better-accessibility`. Surfaces and icon colour belong to
`better-ui`.

Never report a contrast value you did not measure, and never estimate a colour you could compute. A deliberate warm
palette or a dim surface is a project choice, not a finding. A broken role, a failing pair and a meaning colour that
changes with the theme are findings.

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

## Colours live in the theme files only

Every colour is a key in `Themes/Theme.xaml` and `Themes/Theme.axaml`, the same key in both (`ThemeFileTests`). A hex
literal, `Color.FromRgb`, `Colors.*` or `Brushes.*` in a view, a control or render code is not recoloured by the WPF
theme engine and leaves a Tavern-gold spot under Frost or Arcane. The deliberate exceptions are listed in
`hearthpulse-design` (the hue-slider rainbow, transparent stops).

## A token names a job

Colour keys (`AccentColor`) are the primitive tier; brush keys (`Accent`, `Muted`, `Surface2`) are the roles views
use. Use a token only in its role. A separator brush used as text works until borders get lighter, and then the text
goes with them. `Faint` is below AA: separators, glyphs, an empty box's placeholder and disabled things only
(`ReadableTextTests`). If a role has no token, add one in both theme files instead of borrowing. Details are in
[palette-and-tokens.md](palette-and-tokens.md#roles).

## Meaning colours never recolour

The WPF engine maps every hex in Theme.xaml through `ThemePalette.Map`: gold-range hues become the preset's accent,
patina teal becomes its second accent, wood and parchment follow its surface. A colour that carries meaning (win,
loss, tier, rarity, mana, pool stat, neutral info) lives under a key starting with `Positive`, `Negative`, `Info`,
`Rarity`, `Tier`, `ManaGem` or `PoolStat`, which the engine skips. A new meaning colour outside those prefixes turns
purple under Arcane. See [palette-and-tokens.md](palette-and-tokens.md#what-the-engine-recolours).

## One colour, one meaning

`Accent` marks the one thing that matters in an area. `Positive` and `Negative` mean results and nothing else; `Info`
is neutral information. `Accent2` is decoration only and never meaning, because each preset gives it a different hue.
A colour is never the only carrier of meaning: pair it with a sign, letter or icon, a requirement
`better-accessibility` owns.

## Fill exactly one action per area

The gold `PrimaryButton` / `ghost primary` fill marks the one primary action of an area; peers are `GhostButton`. A
selected `Segment`, `Tab` or `NavItem` is state, not emphasis, and may use the accent.

## Measure the rendered pair, then report

Measure a foreground against the surface it actually renders on: a `Card`'s `CardFill`, a `Row`, an overlay card's
`OverlayCardFill`, not the window background. Measure it in every preset. `ThemeContrastTests` does this for the
declared pairs; a new text-on-surface pair gets a row in its `Pairs` table. When a pair fails, report the pair, its
value and the threshold, then leave the colours alone unless asked. See [contrast.md](contrast.md).

## A new preset is a ThemeSpec

A new theme is one `ThemeSpec` in `src/Hearthpulse.Core/ThemePalette.cs` (accent hue, base hue and tint, second
accent hue, accent and surface lift, motif), not a second Theme.xaml. `ThemeContrastTests` runs every preset through
the contrast table, and the preset needs a `Motif<Name>` geometry (`hearthpulse-design`). Avalonia has no theme engine
and stays Tavern.

## Before you finish

| Pattern | Fix |
| --- | --- |
| `#RRGGBB`, `#AARRGGBB`, `FromRgb`, `FromArgb`, `Color.Parse`, `Colors.` or `Brushes.` outside the theme files | A token, existing or new in both theme files |
| A new key in one theme file only | Add it to the other with the same key |
| A win/loss/tier/rarity colour under a key without the fixed prefix | Rename the key with the prefix |
| A meaning colour in the gold (15-60°) or teal (150-186°) hue range | Move it out of the range, or it recolours |
| `Faint` on a bound or localized text | `Muted` |
| `Accent2` carrying meaning | A meaning token, plus a non-colour cue |
| `Negative` on a non-destructive action, or `Positive` for "selected" | The role's own token |
| Two `PrimaryButton`s in one area | One primary, the rest `GhostButton` |
| Text on a translucent brush (`OverlayPanel`, `Hover`, an `AccentSoft` wash) with no measurement | Measure the composited colour, or use a solid surface |
| A contrast fix that changes hue and leaves lightness | Change lightness, the channel contrast responds to |
| `#RRGGBBAA` written CSS-style | XAML hex is `#AARRGGBB`: alpha first |

## Reporting

**Severity.** Use `better-interface`'s scale. Text whose rendered pair fails in any preset, meaning carried by colour
alone, a semantic colour used against its meaning, a meaning colour that recolours and a hex colour in a view are `M`
on sight. A noticeable token or theme inconsistency is `m`; isolated polish is `n`.

**Verification.** From the code: the hex grep from `hearthpulse-review` on added lines, keys present in both theme
files, prefixes on meaning colours, roles against their use. Tests: `ThemeContrastTests`, `ReadableTextTests`,
`ThemeFileTests`. Rendered: WPF in Tavern and at least one non-default preset (`--theme Frost` or `Arcane`), with the
game closed. Report every check you could not run as `Not verified`.

**Format.** The table in `better-interface`'s `review-format.md` without the `Domain` column, grouped under the
principle each finding violates, one row per root cause listing every location. With nothing to report, say
«Замечаний по цвету нет» and list the checks.
