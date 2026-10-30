# Contrast

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-colors/contrast.md` at `592aedc`,
> MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: measurement
> through `ThemeContrastTests` across the seven presets and by sampling rendered PNGs; CSS fixes and APCA removed.

Contrast is measured between a foreground (text, a glyph, a control edge, a focus ring) and the surface it actually
renders on. Identify that surface first: a card's `CardFill`, a `Row`, a selected `SelectedFill`, an overlay card's
`OverlayCardFill`, not the window `Bg`.

`better-accessibility` decides which requirement applies. The thresholds below are what a measured pair is reported
against.

## WCAG 2 thresholds

| Content | AA |
| --- | --- |
| Text | 4.5:1 |
| Large text, at least 24 DIP, or 18.67 DIP bold | 3:1 |
| Control boundaries, focus rings, meaningful glyphs and chart marks | 3:1 |

Disabled controls are exempt. `Faint` is tested at 3:1 and is never used for text the player reads.

## Measure declared pairs with the test

`tests/Hearthpulse.Tests/ThemeContrastTests.cs` recolours Theme.xaml through `ThemePalette.Map` for every preset and
checks each text token against every stop of every surface it is listed on. To cover a new pair, add a row to `Pairs`:

```csharp
("Muted", ["Bg", "Surface", "Row", "CardFill", "MyNewSurface"], 4.5),   // a new surface Muted text sits on
("MyNewEdge", ["CardFill", "SelectedFill"], 3.0),                        // a solid edge or glyph brush at 3:1
```

The test reads `Color`, `SolidColorBrush` and gradient stops and ignores alpha, so a translucent brush measures as if
it were opaque. Check those from a PNG (below).

Run it:

```
dotnet test tests/Hearthpulse.Tests --filter "FullyQualifiedName~ThemeContrastTests"
```

A failure names the token, the surface, the mapped colour and the ratio. The test reads WPF's Theme.xaml; Avalonia's
Theme.axaml holds the same values (`ThemeFileTests`) and its engine maps them with the same `ThemePalette.Map`, so the
test covers both.

## Measure rendered pairs from a PNG

Translucent fills (`OverlayPanel`, `Hover`, `AccentSoft`), text over card art and anything over the game have no
single declared background. Render the scene (`hearthpulse-visual-check`, the game closed), then sample the darkest
background pixel next to the text and a solid pixel inside a stroke:

```python
# python -B -I measure.py <png> <x_fg> <y_fg> <x_bg> <y_bg>
import sys
from PIL import Image

def lum(rgb):
    def ch(v):
        v /= 255
        return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4
    r, g, b = (ch(v) for v in rgb[:3])
    return 0.2126 * r + 0.7152 * g + 0.0722 * b

img = Image.open(sys.argv[1]).convert("RGB")
a = img.getpixel((int(sys.argv[2]), int(sys.argv[3])))
b = img.getpixel((int(sys.argv[4]), int(sys.argv[5])))
hi, lo = sorted((lum(a), lum(b)), reverse=True)
print(f"{(hi + 0.05) / (lo + 0.05):.2f}:1")
```

Keep the script in the scratchpad, not in the repo. Antialiased edges read low; sample the middle of a stroke. For
text over art or the game, measure the worst region, or put the text on a solid plate (`OverlayBadgeShade`).

## Fix a failing pair only on request

**Change lightness first.** Move the foreground away from its surface in lightness, holding hue, then remeasure. In
this project that means changing the Tavern value in both theme files and running the test for all seven presets, or,
when one preset fails alone, its `AccentLift` / `SurfaceLift` in `ThemePalette.cs`.

- A mid-lightness surface caps what any text colour can reach. Body text needs a surface near one extreme.
- A fix for one preset can break another; the test covers all of them.

## What to check

- **Every preset.** Frost, Fel, Arcane, Ember, Tide and Shadow move accents and surfaces differently.
- **Every state.** Hover (`Hover` layer), pressed, selected (`SelectedFill`), disabled.
- **Panel opacity.** The player can fade a panel's content to 0.3 (`PanelLook.Opacity`); measure at full opacity, the
  default, and treat lower values as the player's choice.
- **Gradients.** `CardFill`, `GildedText`, `StatText` and the other "Gilded Tavern" gradients vary across the box;
  the test checks every stop, a PNG check takes the worst spot.
