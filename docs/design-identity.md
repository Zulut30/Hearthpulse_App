# Visual identity

Status (2026-11-05): **V3 "Gold frame" chosen**, with the game's own mana gem (`Assets/mana-gem.png`, the `ManaCost`
control; the owner's decision of 2026-10-27, `LEGAL-HeartLens-claude-mana-assets.md`) in place of the drawn crystal
of the mockups. Direction A "Hearth" was rejected earlier and reverted. V3 lands in parts: first the overlay panel frame
and the deck rows (overlay and main window). The language: a thin bright gold panel edge, a header without a plate over a
gold hairline, card rows as capsules with the gem in the left end and a rarity-lit edge, counts on dark pills, legendary
names in their rarity colour. Task card with the render audit, all mockups and their sources:
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
