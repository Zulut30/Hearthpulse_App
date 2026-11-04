# Visual identity: "Hearth" (direction A)

Decided 2026-11-04 by the design session (the owner handed design decisions to the coordinator). Task card with the
render audit, the three directions and their mockups: `../reference/tasks/TASK-2026-11-04-design-identity/`.
Token and style mechanics stay in the `hearthpulse-design` skill; this page records what the look is meant to say.

## Why it changes

The app looked like one more Hearthstone menu: wood, parchment and gold on everything, so nothing stood out and nothing
came from the logo. The brand now comes from the logo (a flame with a pulse) while the tavern stays, so all seven
themes keep working through `ThemePalette`.

| Direction | Verdict |
|---|---|
| **A "Hearth"**: tavern surfaces, the flame gradient as the one accent, ember light from below, a pulse line | chosen |
| B "Pulse": slate surfaces from the logo's cup, flat monitor look | rejected: slate (hue ~215) is outside the 15-60 band that `ThemePalette.Map` recolours, so every theme collapses into the same slate |
| C "Scroll": light parchment pages, ink text, round shapes | rejected for now: inverts every token and contrast pair, light overlay panels glare in the game; a later light theme candidate |

## The language

- **One accent: the flame.** A vertical yellow-to-orange gradient, only on the primary button, the selected nav item,
  step marks and the pulse line. Gold gradients leave the headings; text stays parchment.
- **Light from below.** Plates darken slightly downwards and catch a warm ember edge at the bottom instead of a gilded
  top rim. The "now" card on Home gets the flame edge.
- **Pulse as the signature.** A heartbeat line in the flame gradient under page titles (replacing the diamond rule) and
  as the "tracking" indicator in the live bar.
- **Section mark: a drop.** Section headings carry the glyph in a small flame-tinted drop instead of the scallop cookie.
- **Less clutter, more air** (the owner's words: remove the junk, make it faster). One main action per surface; the
  rest goes to a "…" menu or shows on hover. Captions that repeat a heading or a nearby number go; an empty plate is
  not drawn, an empty area gets one line saying what will appear. Decoration (nav group diamonds, art behind titles,
  scallop cookies) is removed, not recoloured. No bitmap effects and no animation over 0.25 s.
- **Plain words.** Every empty state says what will appear and what to do; labels avoid tracker jargon.
- **Rhythm.** Spacing 4/8/12/16/24/32, radii 4/8/12/16, one type ramp (D1 tokens).

Meaning colours (`Positive`, `Negative`, `Tier*`, `Rarity*`, mana) do not change, and the logo keeps its own colours.
