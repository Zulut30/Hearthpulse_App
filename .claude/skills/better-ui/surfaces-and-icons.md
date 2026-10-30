# Surfaces and icons

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-ui/surfaces.md` and `icons.md` at
> `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: two
> files merged; CSS shadows and SVG stroke rules replaced by the theme's gradients and MDI glyphs; RTL mirroring
> removed.

Recipes for nested radius, optical alignment, depth, art edges and glyph icons.

## Nested radius

```
outerRadius = innerRadius + padding + borderThickness
```

```xml
<!-- Good: a plate inside a padded frame -->
<Border CornerRadius="14" Padding="6" BorderThickness="0">
    <Border CornerRadius="8">…</Border>     <!-- 14 = 8 + 6 -->
</Border>

<!-- Bad: the same radius on both, the corners look pinched -->
<Border CornerRadius="8" Padding="6">
    <Border CornerRadius="8">…</Border>
</Border>
```

A `Card` (r12, padding 22,18) holds separate surfaces, not a nested pair: its rows keep their own radius. WPF
`ClipToBounds` ignores `CornerRadius`; clip art to rounded corners with the `RoundClip.Radius` attached property
(`hearthpulse-design`). Avalonia clips with `ClipToBounds` on a `Border` that has a `CornerRadius`.

## Optical alignment

### A button with text and a glyph

Give the glyph side 2 less padding than the text side, then judge by eye:

```xml
<Button Style="{StaticResource GhostButton}" Padding="18,9,16,9">
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="{i:Loc …}" />
        <TextBlock Style="{StaticResource Icon}" Text="&#xF0142;" Margin="8,0,0,0" FontSize="15" />
    </StackPanel>
</Button>
```

### Glyphs in a Scallop

MDI glyphs sit on a 24-unit grid; most centre well. Arrows, a play triangle and a star can look off-centre inside the
round `Scallop`. Nudge the glyph's `Margin` by one DIP toward its visual weight's opposite side (a play triangle toward
its point), never the `Scallop` itself.

## Depth

The theme already models light on every raised surface:

| Effect | Token or style |
| --- | --- |
| Lit top rim on a card | `CardEdge` |
| Raised plate (selected chip, live bar) | `EdgeLight` hairline + `SelectedFill` |
| Soft halo behind a highlighted element | `Glow`, `Accent2Glow`, `BestHalo` (gradients, overlay-safe) |
| Content backdrop with the theme motif | `ContentFill` |

Use them before anything new. A border that exists only to suggest depth is better as `CardEdge`. Keep real borders on
inputs (their edge needs 3:1, `better-accessibility`), separators, table cells and selected states.

The main window has a few `DropShadowEffect`s (`MainLook.xaml`, `MainWindow.xaml`; Avalonia `BoxShadow`); they are the convention there. In the overlay,
no `Effect` at all: the overlay is a transparent window over the running game, and effects cost every frame.

## Art edges

```xml
<!-- A token with saturation 0, so ThemePalette.Map leaves it unchanged in every preset -->
<SolidColorBrush x:Key="ArtEdge" Color="#1AFFFFFF" />   <!-- both theme files -->

<Border BorderBrush="{StaticResource ArtEdge}" BorderThickness="1" CornerRadius="6">
    <Image Source="{Binding Art}" />
</Border>
```

Add the token only when the first art edge is needed. Never an accent or wood-tinted edge on art.

## Icons

### One font, one style

Every UI glyph is a Material Design Icons code point written as an entity (`&#xF02DC;`) in a `TextBlock` with the
`Icon` style (WPF) or `icon` class (Avalonia). No image icons for chrome, no second icon font, no emoji.

### State from Foreground

```xml
<!-- WPF: a trigger switches the token -->
<Style TargetType="TextBlock" BasedOn="{StaticResource Icon}">
    <Style.Triggers>
        <DataTrigger Binding="{Binding IsPinned}" Value="True">
            <Setter Property="Foreground" Value="{StaticResource Accent}" />
        </DataTrigger>
    </Style.Triggers>
</Style>
```

| State | Foreground |
| --- | --- |
| Rest | `Muted` (the `Icon` style default) |
| Hover | `Text` |
| Active, selected, pinned | `Accent` |
| Disabled | `Faint` |

### Outline at rest, filled when active

MDI ships most glyphs in both forms (`star-outline` / `star`, `pin-outline` / `pin`, `heart-outline` / `heart`). Use
them as a state pair: outline at rest, filled when on. Filled everywhere leaves the active one without a signal.

### Size with the text

Inline glyphs are 1 to 1.25 times the text beside them: 13-16 next to 13 DIP text. The `Icon` style is 18 and
`IconButton` uses 17 for standalone glyphs. Check every glyph at the smallest size it renders, including the overlay at
`OverlayScale` 0.7 with panel scale 0.6.
