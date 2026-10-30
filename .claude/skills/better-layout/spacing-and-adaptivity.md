# Spacing and adaptivity

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-layout/spacing-and-adaptivity.md`
> at `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> CSS breakpoints, container queries, safe areas and viewport units replaced by WPF/Avalonia panels, Avalonia compact
> mode, the live bar and overlay zones, and Russian text growth.

Recipes for control clearance, hidden content, reserved zones, adaptive structure and growth.

## Borderless controls need more clearance

| Between | Starting point |
| --- | --- |
| Bordered or filled controls (`GhostButton`, `PrimaryButton`, `Segment`) | `12` |
| Visible glyphs of borderless controls (`IconButton` glyphs, text buttons) | `24` |
| Unrelated control groups | `24` or more |

Nothing marks where one borderless target ends and the next begins, so the space is the boundary. Keep an established,
usable density rather than spreading controls to match these values. Hit areas are `better-accessibility`'s; space
controls so they never overlap.

## Hint at hidden content

- **Scroll cues.** A list that ends exactly at its container edge looks complete and nobody scrolls it. Let a row cut
  at the edge, or keep the scrollbar visible (`VerticalScrollBarVisibility="Auto"`).
- **Disclosures** name what they hide: "Показать ещё 12 колод" / "Show 12 more decks", not "Ещё" / "More".
- **Trimmed values** need a way to the full text: a `ToolTip` with the full value, or a detail page. Card names in
  the overlay can use the `CardPreview` hover (with `OverlayHit.Interactive`).

```xml
<!-- WPF -->
<TextBlock Text="{Binding DeckName}" TextTrimming="CharacterEllipsis" ToolTip="{Binding DeckName}" />
<!-- Avalonia -->
<TextBlock Text="{Binding DeckName}" TextTrimming="CharacterEllipsis" ToolTip.Tip="{Binding DeckName}" />
```

## Keep the reserved zones clear

- **Main window:** the floating live bar covers the bottom `120` of every page; the page grid's bottom margin keeps
  content above it. A new page or a page that grows a fixed footer checks the last row is not under the bar.
- **Overlay:** keep clear the hero portraits, the hand, End Turn, Discover cards and the BG tavern row. Anchor a panel
  to the edge it belongs to so wide and narrow screens work: `HorizontalAlignment="Right" VerticalAlignment="Bottom"`
  for things near End Turn. Panels of the same mode never share a spot; `overlay-layout.png` shows every placeholder.
- **Tooltips** stay inside the 1920×1080 surface and outside the live bar.

## Hold structure until it breaks

Collapse where the layout stops fitting, not at a round number: where a column of names drops below a readable width
or a choice row runs out of room.

- **Avalonia** adds the `compact` class to `MainView` below 900 px (`CompactWidth` in `MainView.axaml.cs`): nav turns
  icon-only, `.wide` elements hide, choice rows stack. A new element either works at 420 px or gets a compact rule.

  ```xml
  <Style Selector="m|MainView.compact Grid.choiceRow"> … </Style>
  ```

- **WPF** has no compact mode; its `MinWidth="1040" MinHeight="660"` is the floor. Check new content at that size.
- **The overlay** scales as a whole (`OverlayScale` 0.6-1.6) and per panel (0.6-1.6). Check the panel at both ends:
  text must stay readable at 0.6 and the panel must stay on screen at 1.6 (`MovablePanel.KeepOnScreen` pulls it back,
  check it at the bottom and right edges in layout mode).

## Plan for growth and clipping

```xml
<!-- Good: the label sets the size, and the row wraps -->
<WrapPanel>
    <Button Style="{StaticResource GhostButton}" Content="{i:Loc …}" Margin="0,0,12,12" />
    <Button Style="{StaticResource GhostButton}" Content="{i:Loc …}" Margin="0,0,12,12" />
</WrapPanel>

<!-- Bad: Russian overflows or trims -->
<Button Width="96" Content="{i:Loc …}" />
```

- A horizontal `StackPanel` measures its children with unlimited width, so text inside it never trims or wraps and
  pushes the row past the window. Use a `Grid` with a `*` column or a `DockPanel`.
- A `*` column trims only when the text inside has `TextTrimming` set; an `Auto` column grows with its content.
- Fixed-height containers clip text in Russian and at small overlay scales. Use `MinHeight`; where a box must cap its
  height, pair `MaxHeight` with a `ScrollViewer`.
- A dialog taller than the window keeps its action row outside the scrolled body.
- Card, hero and deck names come in the card locale and can be the longest strings on the screen. Render with a
  Russian data copy to see them.
