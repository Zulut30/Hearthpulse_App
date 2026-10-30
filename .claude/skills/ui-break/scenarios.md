# Scenario axes

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/break/scenarios.md` at `592aedc`, MIT,
> © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: values for card,
> deck and player names, Russian text, game modes and the overlay; RTL and emoji axes removed; viewport widths
> replaced by the window minimum, Avalonia compact and overlay scale.

The menu **Infer the scenarios from the component** in [SKILL.md](SKILL.md) selects from. Each axis carries a cue: the
property of the component that makes the axis worth running. Cue matches, axis stays; cue fails, axis is dropped and
named.

Add any value the component's own data makes obviously worse, copied into the fixture: the longest name in
`cards.ruRU.json`, the biggest deck the game allows, the longest Loc string of the area.

## Content length

**Cue: the component renders text it does not author.** Card, hero, deck and player names, archetype names from the
API, Loc strings in two languages. A fixed Loc label the area controls still gets the language axis.

| Scenario | What it catches |
| --- | --- |
| Empty string | Collapsed rows, a label with nothing beside it, a tooltip with no text |
| One short word | Pills and buttons sized to their longest expected content |
| Typical content | The baseline the others are judged against |
| Longest card name in the card locale (ruRU) | Names running under numbers, trimming with no tooltip |
| The longest custom deck name and BattleTag the game accepts | Name columns sized for short English |
| An unbreakable string: a deck code, a folder path | Overflow with no wrap opportunity |
| Several sentences (an error, an empty state) | Wrapping, line height, containers that assumed one line |

Breaks land in `better-typography` for trimming and wrapping, `better-layout` where the container has no room and
`better-writing` where the source text is the problem.

## Language and script

**Cue: the component shows interface text or names.** Always for interface text.

| Scenario | What it catches |
| --- | --- |
| Russian interface (a `"Language": "ru"` data copy) | Labels 15-30% longer than English |
| English interface | The baseline |
| Cyrillic capitals and descenders (Й, Щ, Ё, у, д) in a tight row | Clipped tops and tails at a small `LineHeight` |
| CJK or Latin-with-accents player names (BattleTags allow them) | Fallback fonts, a different line height |
| Numbers in a column (ranks, win rates, counters) | Jitter without tabular figures, especially in Avalonia |

Breaks land in `better-typography`; room lands in `better-layout`.

## Quantity

**Cue: the component repeats over items.** Deck lists, history rows, hand marks, counters, tier lists, discover
options.

| Scenario | What it catches |
| --- | --- |
| Zero items | Blank regions, missing empty states |
| One item | Layouts designed around plural content |
| The realistic count (a 30-card deck, 6 BG opponents, 3 Discover options) | The baseline |
| The game's maximum (40 cards with Renathal, 10 cards in hand, 7 minions a side) | Lists that never scroll, panels that leave the screen |
| Ten times the realistic count (history: 3000 games) | Missing virtualisation or paging, slow first render |

Zero-item breaks land in `better-writing` for the empty state and `better-layout` for the region; the rest in
`better-layout`.

## Container

**Cue: always.** The component does not choose its size.

| Scenario | What it catches |
| --- | --- |
| WPF minimum window `1040 × 660` | Clipping, controls leaving the page |
| Avalonia compact, 420 wide | Columns that do not stack, `.wide` content that stays |
| A cramped `*` column beside fixed columns | A child refusing to shrink |
| Overlay at `OverlayScale` 0.6 and 1.6, panel scale 0.6 and 1.6 | Unreadable text at the small end, a panel off screen at the large end |
| A very wide container (1920 for a main-window piece) | Unbounded line length, controls pinned to opposite edges |

Breaks land in `better-layout`; text too small to read lands in `better-typography`.

## State

**Cue: the component has the state.** Render only the states its view model can reach.

| Scenario | What it catches |
| --- | --- |
| Loading | Layout shift when content arrives, an unlabelled spinner |
| API offline or failed | Messages that overflow, colour as the only signal, no way to retry |
| Disabled | A reason that lives only in a tooltip, contrast collapse |
| Game not running / in menus / in a game | Panels that show in the wrong scene, stale data |
| Another game mode (Arena, Duos, Twist, Brawl, Wild) | Text or columns that assume Standard or solo BG |

Hover and focus are the owner's to try in the running app; name them in the report.

Breaks land in `better-accessibility`; purely visual state polish in `better-ui`; wrong mode logic in
`hearthpulse-review`.

## Environment

**Cue: the project supports the mode.** These are runs, not scene content, so the scene renders nothing for them: WPF
under each non-default preset (`--theme`), a ru and an en data copy. Windows' animation setting does not show in a PNG;
name it for the owner to toggle.

Theme breaks land in `better-colors`; motion in `better-accessibility`.
