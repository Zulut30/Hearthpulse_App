# Visual identity

Status (2026-11-04): **open**. Direction A "Hearth" was mocked up and rejected by the owner ("no mana crystals, it looks
bad"); its first slice was reverted. Three new directions with a drawn mana crystal and card-art deck rows wait for
the owner's choice. Task card with the render audit, all mockups and their sources:
`../reference/tasks/TASK-2026-11-04-design-identity/` (`directions/` for A-C, `directions-v2/` for V1-V3).
Token and style mechanics stay in the `hearthpulse-design` skill.

## What every direction keeps

- **Hearthstone's atmosphere, kept clean:** warm wood and stone, gold trim, soft shadows and volume rather than flat
  dark plates; text stays readable on every surface (`ThemeContrastTests`).
- **A mana crystal, not a circle:** a faceted blue gem with the cost on it, drawn as vector geometry in both UIs (no
  Blizzard asset), in fixed colours that no theme recolours.
- **Deck rows like the game's list:** card art on the right fading into the text, the crystal on the left, "×2" and
  the legendary star on the right, rarity visible on every row.
- **Less clutter** (the owner's words: remove the junk, make it faster): one main action per surface, no captions that
  repeat a heading or a nearby number, no empty plates, decoration removed rather than recoloured; no bitmap effects
  and no animation over 0.25 s.
- **Seven themes** through `ThemePalette`; meaning colours (`Positive`, `Negative`, `Tier*`, `Rarity*`, mana) and the
  logo never change.
- **Rhythm:** the D1 scale - spacing 4/8/12/16/24/32, radii 4/8/12, one type ramp.

## Rejected

| Direction | Why |
|---|---|
| A "Hearth": the logo's flame as the one accent, a pulse line, ember light | the owner: no mana crystals, looks bad |
| B "Pulse": slate surfaces, flat monitor look | slate (hue ~215) is outside the band `ThemePalette.Map` recolours, every theme collapses into one |
| C "Scroll": light parchment pages | inverts every token and contrast pair; light overlay panels glare in the game |
