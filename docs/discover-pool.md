# Card pool panel ("Discover pool")

For a card that Discovers or creates random cards, the overlay shows the pool the cards come from: size,
Cost/Attack/Health histograms with medians, the share of each mechanic, a highlighted card and, on right-click, the full
list. Everything is built from game data and card text; no other tracker's code is used.

Code: `src/Hearthpulse.Core/CardPool/` (catalog, parser, engine, stats, tracker), `Tracking/GameSession.CardPool.cs`,
panels `Overlay/CardPoolPanel.*` in both UIs. Generated table: `CardPoolTables.g.cs` from
`tools/cardpool/gen_pool_tables.py`.

## Data

**Main source: Hearthpulse API** `GET /api/v1/constructed-cards?format=wild&per_page=200&page=N` (33 pages,
~3 MB gzipped, 6501 cards on 2026-10-23). Every collectible Standard/Wild card; `formats[].slug` says whether it is in
Standard, so one Wild download covers both formats (`format=standard` gives the same 1322 cards). Fields used:

| Field | Use |
|---|---|
| `card_id`, `dbf` | identity, join with `CardDb` |
| `card_type.slug` | MINION, SPELL, WEAPON, LOCATION, HERO |
| `class`, `multi_class[]` (CLASS ids 1 DK, 2 Druid, 3 Hunter, 4 Mage, 5 Paladin, 6 Priest, 7 Rogue, 8 Shaman, 9 Warlock, 10 Warrior, 12 Neutral, 14 DH) | class filters; multi-class cards have `class: null` |
| `minion_type` | tribe (one value only, see below) |
| `spell_school` | school filters |
| `mechanics[]`, `referenced_tags[]` | keyword filters and mechanic percentages (identical to HearthstoneJSON) |
| `mana_cost`, `attack`, `health`, `durability`, `rarity`, `card_set` | filters and histograms |
| `name.en`/`name.ru`, `text.en` | names; English text for generator parsing and text rules |

Not in the API and patched from `CardDb` (HearthstoneJSON): **dual minion types** - `minion_type` keeps one of two
types for 59 Standard minions (e.g. Beast + Dragon), so `CardPoolCatalog.WithRaces` takes HearthstoneJSON `races`.

Cached as a compact list in `%APPDATA%\Hearthpulse\cache\api\card-pool.json`, refreshed after 1 day; a stale cache is
used while the API is down. **Fallback**: HearthstoneJSON `cards.collectible.json` (enUS), Standard decided by
`CardPoolTables.StandardSets`.

The panel's highlighted card is the pool card played in the most meta-deck games (`ConstructedMeta.Decks`, sum of
`Games` over decks that run it). No per-card win rate is public on the API (`/v1/meta-intelligence/cards` needs a
token), so there is no "strongest card" rule; without meta data no card is highlighted.

### Proposals for the API (server not changed)

1. A light list: `GET /api/v1/constructed-cards?...&fields=card_id,dbf,formats,name,text,card_type,class,multi_class,`
   `minion_types,spell_school,mechanics,referenced_tags,mana_cost,attack,health,durability,rarity,card_set` - the
   current items carry `tile_layout` and image URLs, ~3.7 KB per card for ~0.3 KB the client needs.
2. `minion_types[]` with every type (today `minion_type` drops the second of dual-type minions) and `rune_cost`
   {blood, frost, unholy} (Death Knight "Discover a Frost Rune card" pools cannot be built without it).
3. Optional pool endpoint: `GET /api/v1/card-pool?format=standard&type=MINION&class=MAGE&include_neutral=1&race=BEAST`
   `&school=&mechanic=BATTLECRY&cost=3&cost_min=&cost_max=&rarity=&past=1` returning card ids and the same stats the
   panel draws (histograms, mechanic shares). The app does not need it - the full list is small - but other clients
   (site, bot) could reuse the rules.
4. `updated_since` already exists on `constructed-cards`; the app could refresh incrementally with it.

## Which cards are generators

`PoolTextParser` reads the normalised English text (`PoolText.Normalize`: no markup, `[x]`, `$`, `#`, `@`, `&`).
Collectible generators bring their text with the catalog; non-collectible ones (hero powers, tokens, Dark Gifts)
are in `CardPoolTables.GeneratorTexts`. Two anchors:

- **Discover**: `Discover [a|an|two|any|N] <descriptor>` - not after "you", "you've", "to", "Quest:" (those *count*
  Discovers), not "Discovered".
- **random**: `<verb> ... [count] random <descriptor>` where the nearest verb before "random" in the clause is a
  generation verb (Get, Add, Summon, Cast, Fill, Transform, Shuffle, Equip, Replace, Look at, Become). A nearer other
  verb (deal, destroy, give, return, draw, discard, trigger...) or a target word right after "random" (enemy,
  friendly, player's, adjacent, character...) means it is not a generator.

Descriptor = adjectives, a noun, then postfix clauses; every word must be known, or the card is **Unrecognized**: no
panel, and `hearthpulse.log` gets `Card pool: <name> (<id>) not understood: <rest of text> | <full text>`.

| Part | Accepted | Filter |
|---|---|---|
| filler | different, Temporary, new, random, additional, other, another | - |
| owner | friendly, enemy, your, their, your opponent's | not a pool (NotPool) |
| class | Death Knight ... Warrior, Neutral (`Mage spells`) | named classes |
| rarity | Legendary, Epic, Rare, Common | rarity |
| cost | `3-Cost`, `6, 4, and 2-Cost`, `10 and 1-Cost` | exact costs (a list = union) |
| parity | `odd-Attack`, `even-Cost`... | parity of the stat |
| school | Arcane, Fel, Fire, Frost, Holy, Nature, Shadow before "spell(s)"; `Holy and Shadow spell` | school (union) |
| keyword | `PoolKeywords.All` (Battlecry, Deathrattle, Taunt, Rush, Divine Shield, Choose One, Combo, Stealth...); lists `Combo, Battlecry, or Stealth` | one of the tags in mechanics (Imbue/Herald also in referencedTags) |
| tribe | Beast, Demon, Dragon, Elemental, Mech, Murloc, Pirate, Naga, Undead, Totem, Draenei, Quilboar (+plural); also a noun | minion of that type (or "ALL") |
| noun | card(s), minion(s), spell(s)/Spells, weapon(s), location(s), Secret(s), Hero card(s) | card type; Secret = spell + SECRET |
| postfix | `that costs (N) or more/less`, `that cost (N)`, `costing (N) or less` | cost bounds |
| postfix | `from another class` / `from a different class` / `from other classes` | class cards not of your class (no neutrals) |
| postfix | `from any class` (any case), `from your class`, `from your opponent's class` | scope |
| postfix | `from the past` | cards not in Standard (Wild-only sets), in any format |
| postfix | `with Taunt` (any keyword), `with N or more Attack`, `with multiple minion types` | keyword / attack / 2+ types |
| postfix | `with a Dark Gift` | ignored (a bonus, not a filter) |
| postfix | `in/from your hand/deck`, `that died`, `you've ...`, `copy of` | NotPool (not from the catalog) |
| end | end of text, `. , ; : ! ? ( )`, and, to, into, for, then, instead, if, when, after, until, it, they, at, on, plus, each, as, but | - |

### Class and format rules

- **Format**: `GameState.FormatType` FT_STANDARD -> Standard cards, FT_WILD -> all cards. Classic, Twist, Arena and
  others have no known pool: no panel.
- **Discover without a class**: your class + neutral cards (your class = local hero's card class; outside a game the
  generator's own class). Class cards are offered more often than neutrals in the game; the panel lists the pool
  unweighted.
- **Random without a class**: every class and neutral.
- Named classes (`Mage spells`) use the card's `multi_class` too: a Mage/Priest card is in a Mage pool.
- "from another class": class cards whose classes do not include yours; neutral cards excluded.

### Exception table

`tools/cardpool/gen_pool_tables.py` `OVERRIDES` maps text patterns to actions for every Constructed card carrying
them (ids written to `CardPoolTables.Overrides`):

- `skip` - generates, but the catalog cannot answer: cost from the game state (`with Cost equal to`, `of that Cost`,
  `playable spell`), pool from another card (`of the same Cost/Rarity`), rune costs (`Frost Rune card`), named token
  groups (Animal Companions, Dormant Dreadseeds, Dream cards, Leylines, Shatter, Masks, Rewind cards, Wild Gods, Old
  Gods, contraband, Mimiron's tech card). Logged as `has no catalog pool: <reason>`.
- `sub` / `text` - parse a corrected text (`2-Cost minion that is Dormant for 2 turns` -> `2-Cost minion`,
  `into random Druid ones` -> `random Druid cards`, Fyrakk's `Fire spells at random enemies` -> `random Fire spells`).

Regenerate after each patch:

```
python3 -I tools/cardpool/gen_pool_tables.py cards.enUS.json src/Hearthpulse.Core/CardPool/CardPoolTables.g.cs
```

## Mechanic percentages

Share of pool cards per mechanic, highest first, at most 12 tiles. A mechanic counts when it is in the card's
`mechanics` or `referencedTags` (so "Give a minion Taunt" counts as Taunt). Shown mechanics and labels:
`PoolKeywords.Shown`; technical tags (TRIGGER_VISUAL, AURA, ImmuneToSpellpower...) are not shown. Mechanics every pool
card has (the filter itself, e.g. Battlecry in a Battlecry pool) and those no card has are left out.

Text rules (`PoolStats`, over the English text):

| Tile | Rule |
|---|---|
| Card Draw | the word draw/draws (`\b[Dd]raws?\b`), not "draw this" (Casts When Drawn uses "drawn") |
| Deal Face Damage | `Deal N damage.` (any target), or N damage to a character / the enemy hero / your opponent / all enemies / all characters / both heroes / (random) enemies (not "enemy minion"), or damage split among enemies |
| Heal | restore(s) / heal(s) |

## Histograms

Cost 0..6 and 7+ (all pool cards); Attack 0..6 and 7+ (minions and weapons); Health 1..9 and 10+ (minions). The
median is the usual median of the cards that have the stat (halves shown as 3.5); its bar is highlighted.

## When the panel shows

`CardPoolTracker` (an `IPowerListener`), in priority order:

1. **Discover choice open** (`OnChoices`, GENERAL choice with a known source card) until the pick (`OnChoiceMade`).
2. **Requested**: `GameSession.ShowCardPool(cardId)` / `ShowCardPool(null)` - for hover over deck and hand rows.
   `GameSession.BuildCardPool(cardId, context)` builds a view outside the live game.
3. **Hover in hand**: the card under the mouse, read from game memory every 200 ms on its own thread, when it is in
   your hand (`GameState` HAND zone).
4. **Just played**: a generator you played (`PLAY` block), for 6 s of log time.

Only in Constructed-like games (not Battlegrounds/Mercenaries) and with the overlay setting "Show card pool" on.

### Card under the mouse (game memory)

Found by reading the client's classes with `tools/Hearthpulse.Probe` (`class`, `hover`), no other tracker's code:
`InputManager.s_instance` -> `m_mousedOverCard` (Card) -> `m_entity` -> `m_cardIdInternal`
(`HearthstoneMemory.GetHoveredCardId`). Checked live in a Constructed match for ~6 minutes: hand cards (CATA_467,
JAIL_444, BE_571, CATA_161) always matched the `m_mousedOver` flag of the same card in `m_myHandZone.m_cards`; it also
returns board minions, hero, hero power and weapon, and null when nothing is hovered. While dragging it is the card
under the cursor; opponent's hidden hand cards have no id (null). `InputManager.s_instance` is rebuilt every match, so
it is read fresh each time. Not checked: outside a match (expected null), during a Discover choice.
