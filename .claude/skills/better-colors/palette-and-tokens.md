# Palette and tokens

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-colors/palette-structure.md`,
> `token-naming.md`, `color-usage.md`, `palette-generation.md` and `color-formats.md` at `592aedc`, MIT, © 2026 Jakub
> Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: five files merged and rewritten
> for the project's theme files, `ThemePalette.Map` and XAML hex notation; ramps, OKLCH, gamut, Tailwind and
> light/dark removed.

How the project's colours are organised, what the theme engine changes, how to name and use a token and how to audit.
The key list and values live in `hearthpulse-design` (`reference/tokens.md`).

## Roles

| Group | Tokens | Use |
| --- | --- | --- |
| Surfaces | `Bg`, `Surface`, `Surface2`-`Surface4`, `Row`, `CardFill`, `OverlayCardFill`, `OverlayPanel(Solid)` | Window, panels, plates, rows, overlay cards |
| Text | `Text`, `Muted`, `Faint`, `OnAccent`, `OnAccentContainer`, `OnSecondaryContainer` | Body, secondary, decoration only, text on gold, on containers |
| Edges | `Border`, `Outline`, `GoldTrim`, `SelectedEdge`, `OverlayBorder`, `AccentLine`, `EdgeLight` | Hairlines, strong edges, trim, selection |
| Accent | `Accent`, `AccentDeep`, `AccentSoft`, `AccentContainer`, `PrimaryFill` | The one thing that matters, the primary action, selection |
| Second accent | `Accent2`, `Accent2Soft`, `Accent2Line`, `Accent2Glow` | Decoration only |
| Meaning (fixed) | `Positive(Soft)`, `Negative(Soft)`, `Info`, `Tier*`, `Rarity*`, `ManaGem`, `PoolStat*` | Results, tiers, rarity, mana, pool stats |
| States | `Hover`, `Pressed`, `SelectedFill` | Interaction layers |

A role with no token gets a new key in both theme files, named for the job (`OverlayBadgeShade`), never for a look
(`DarkBrown`) or a first use (`SidebarGray`). Borders and separators are separate roles even when they share a value
today.

## What the engine recolours

Both engines run the theme's colours through `ThemePalette.Map(argb, theme)`: WPF `ThemeEngine` rewrites every
`#RRGGBB` / `#AARRGGBB` of Theme.xaml line by line; Avalonia `ThemeEngine` rewrites the brushes of Theme.axaml in place,
brush by brush.

| Source colour | Becomes |
| --- | --- |
| Saturation below 0.08 | Unchanged |
| Teal 150-186°, chroma ≥ 0.15 | The preset's second accent |
| 15-60°, chroma ≥ 0.3 (gold) | The preset's accent, lightened by `AccentLift` |
| 15-60°, dull, dark or light (wood, parchment) | The preset's base hue, tinted by `BaseTint`, lifted by `SurfaceLift` |
| Any other hue | Unchanged |
| A line whose `x:Key` starts `Positive`, `Negative`, `Info`, `Rarity`, `Tier`, `ManaGem` or `PoolStat` | Never touched |

So a decorative gold that should follow the theme sits in 15-60°, and a meaning colour sits under a fixed-prefix key,
whatever its hue. In WPF the check is per line: a `GradientStop` line has no `x:Key`, so a hex stop inside a meaning
gradient is recoloured; give the stop a prefixed colour (`Color="{StaticResource PositiveColor}"`). Both theme files
hold the same keys (`ThemeFileTests`); the Avalonia renderer draws Tavern only.

## Use tokens in their role

```xml
<!-- Bad: a separator brush as text, a text brush as a fill -->
<TextBlock Foreground="{StaticResource Border}" />
<Border Background="{StaticResource Muted}" />

<!-- Good -->
<TextBlock Foreground="{StaticResource Muted}" />
<Border Background="{StaticResource Surface4}" />
```

A colour must also not be absent where its meaning occurs: a win shown in `Text` beside losses in `Negative` reads as
neutral.

## One coloured action per area

```xml
<!-- Good: one gold primary, neutral peers -->
<Button Style="{StaticResource PrimaryButton}" Content="{i:Loc Deck.Versions.Merge}" />
<Button Style="{StaticResource GhostButton}" Content="{i:Loc Deck.Versions.Cancel}" />
```

Selected chips and the selected nav item may use the accent: that is state, not emphasis.

## Hex notation

XAML hex is `#RRGGBB` or `#AARRGGBB`, alpha first. `#1FE9B949` is `E9B949` at alpha `1F` (12%). A CSS-style
`#RRGGBBAA` reads as a different colour. Write new values in the same notation as the neighbouring keys.

## Gradients

WPF `LinearGradientBrush` interpolates in sRGB by default; a two-hue gradient goes muddy in the middle. Keep the
project's gradients between neighbouring lightnesses of one hue, or add a middle stop. Keep text off gradients where
you can; the project's text-bearing gradients (`CardFill`, `SelectedFill`) are in the contrast test at every stop.

## Auditing

1. **Collect every literal** in the scope: the hex grep from `hearthpulse-review` (§4) over views, controls and render
   code.
2. **Assign each a role** from the table above. A literal with no role is a missing token or a mistake; say which.
3. **Check the prefix** of every meaning colour and the hue range of every decorative one.
4. **Collapse near-duplicates**: two keys with nearly the same value and the same role are one token that drifted.
   Keep the one used most.

Report the inventory before changing anything. Consolidating tokens changes screens nobody asked about, so it stays a
proposal until the owner accepts it.
