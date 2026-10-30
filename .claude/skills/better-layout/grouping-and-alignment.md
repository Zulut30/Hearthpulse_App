# Grouping and alignment

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-layout/grouping-and-alignment.md`
> at `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> CSS and Tailwind recipes rewritten in XAML/AXAML with the project's styles; logical properties and RTL removed (the
> interface ships in ru and en only).

Recipes for grouping, control distinction, shared edges and ordering.

## Group with space, not lines

Reach for each tool only where the one before it cannot carry the structure:

1. **Space**, the default.
2. **A plate**: `Card` / `card` for a section, `Row` / `row` for a selectable line, where a group must read as one
   unit.
3. **A hairline**: a 1 DIP `Border` brush line or the `Wave` rule, for dense data such as tables and long settings
   lists.

```xml
<!-- Good: spacing alone carries the grouping -->
<StackPanel>
    <StackPanel Margin="0,0,0,24">            <!-- group: 24 below -->
        <TextBlock Style="{StaticResource Label}" Text="{i:Loc Settings.Theme.Accent}" Margin="0,0,0,8" />
        <Slider />                             <!-- inside the group: 8 -->
    </StackPanel>
    <StackPanel>…next group…</StackPanel>
</StackPanel>

<!-- Bad: uniform spacing plus lines to compensate -->
<Border BorderThickness="0,0,0,1" BorderBrush="{StaticResource Border}" Margin="0,0,0,12">…</Border>
<Border BorderThickness="0,0,0,1" BorderBrush="{StaticResource Border}" Margin="0,0,0,12">…</Border>
```

Where a hairline is genuinely needed, keep it in `Border` or `Faint`. Never pair it with a large gap that already did
the job. Section headings use the `Ornament` control, which already separates.

## Keep controls distinct from content

```xml
<!-- Bad: the action looks like the caption next to it -->
<TextBlock Style="{StaticResource Caption}" Text="{i:Loc …}" MouseLeftButtonUp="OpenSettings" />

<!-- Good: a ghost button reads as a control -->
<TextBlock Style="{StaticResource Caption}" Text="{i:Loc …}" />
<Button Style="{StaticResource GhostButton}" Content="{i:Loc …}" Command="{Binding OpenSettings}" />
```

The inverse holds too. A `Pill` (a tag) shaped and coloured like the `Segment` chips beside it collects dead clicks.

## Align to shared edges

- Typical stray edges: an icon 2 DIP off the text edge, a card padded unlike its neighbour, a header that ignores the
  page margin of 40.
- Deeper nesting repeats the same step rather than inventing a new one.
- Numbers in table columns take `HorizontalAlignment="Right"` / `TextAlignment="Right"`, text takes the left. Tabular
  figures are `better-typography`'s.

```xml
<!-- Good: one leading edge, one indent step -->
<StackPanel Margin="40,0,40,120">
    <TextBlock Style="{StaticResource H2}" />
    <StackPanel Margin="16,8,0,0">…</StackPanel>
</StackPanel>

<!-- Bad: three unrelated leading edges in one column -->
<TextBlock Margin="20,0,0,0" />
<ListBox Margin="14,0,0,0" />
<Button Margin="24,0,0,0" />
```

Rows of the same list share one column grid (`Grid.IsSharedSizeScope` + `SharedSizeGroup` in WPF; equal column
definitions in a template in Avalonia), so names, numbers and badges line up from row to row.

## Order by importance

Never bury the one number the player came for under rows of detail. On a deck page that is the win rate and games
played; on a BG panel, the placement or the odds. Move secondary detail into a collapsed section, a tab or the match
page.

```xml
<!-- Good: the key number first, its label under it -->
<StackPanel>
    <TextBlock Style="{StaticResource Stat}" Text="{Binding WinRateText}" />
    <TextBlock Style="{StaticResource Caption}" Text="{i:Loc …}" />
</StackPanel>
```

Keep the element order equal to the visual order: the keyboard and Narrator follow the element order, not
`Grid.Column` placement.
