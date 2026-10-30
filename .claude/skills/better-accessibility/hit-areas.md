# Hit areas

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-accessibility/hit-areas.md` at
> `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> pseudo-element recipes replaced by WPF/Avalonia hit testing; the overlay's click-through rules.

Target sizes, what makes an area clickable, decorative layers, the overlay and drag alternatives.

## Target sizes

| Standard | Minimum |
| --- | --- |
| WCAG 2.5.8 (AA) | 24×24 DIP, with exceptions |
| WCAG 2.5.5 (AAA) | 44×44 DIP |
| Project `IconButton` / `icon` | 32×32 |
| Project `NavItem` / `nav` | 46 high |

Before reporting an undersized target, check the spacing exception: a target smaller than 24×24 passes when a 24 DIP
circle centred on it touches no other target or another small target's circle. Two 20 DIP targets need a 4 DIP gap.

The visible element can stay small; the clickable area is what must be big. Anything that looks clickable is
clickable across its whole visual extent.

## What is hit-testable

- **WPF:** an element is hit only where it paints. A `Border` or `Grid` with no `Background` lets clicks fall
  through its empty parts. Give the clickable container `Background="Transparent"`.
- **Avalonia:** the same holds for panels and borders without a background; set `Background="Transparent"`.
- To grow a small glyph's target without growing the glyph, give the `Button` `MinWidth`/`MinHeight` or padding and
  centre the glyph inside, rather than wrapping it in another control.

```xml
<!-- A 16 DIP glyph with a 32 DIP target -->
<Button Style="{StaticResource IconButton}" AutomationProperties.Name="{i:Loc Key}">
    <TextBlock Style="{StaticResource Icon}" Text="&#xF01B4;" FontSize="16" />
</Button>
```

## Collisions

Where two targets' areas would overlap, shrink the larger to the biggest size that does not collide. Overlapping
targets send the click to whichever is on top, which is rarely what the player aimed at.

## Decorative layers

A glow, sheen, `BestHalo`, motif or gradient drawn over controls absorbs every click its box covers. Give each one
`IsHitTestVisible="False"`. Keep hit testing on a layer the player is meant to click, such as a dialog scrim that
closes on click.

## The overlay

The overlay window is click-through. It takes the mouse only over elements marked `o:OverlayHit.Interactive="True"`
(`hearthpulse-overlay-panel`):

- Mark the clickable element, never a whole panel that is mostly text.
- An interactive area over Discover cards, the hand, the hero portraits or End Turn steals the player's clicks from
  the game, a review major.
- A hover preview (`c:CardPreview.*`) needs the mark too.
- A panel that reacts to the pointer without blocking the game reads the pointer from the platform layer instead
  (the card pool pattern in `hearthpulse-overlay-panel`).

## Drag alternatives

- Every new drag needs a click path to the same result (2.5.7), as the `MovablePanel` tag buttons give for size,
  opacity, compact and reset, and wheel steps give for scale.
- A drag ends on button up or when the element loses the mouse, never only on button up.
