---
name: hearthstone-context
description: Use when a Hearthpulse task needs Hearthstone domain knowledge - game modes, Constructed decks/ranks/mulligan/archetypes, the Coin and opening hand, Discover and card generation, counters' game mechanics, Battlegrounds heroes/tiers/tribes/trinkets/combat rules, card ids/dbfIds/golden, Power.log format and tags, log.config - or field-level data from api.kolodahearthstone.com.
---

# Hearthstone for Hearthpulse developers

Hearthstone (Blizzard) is a digital card game. Hearthpulse tracks games from the client's log files and a read-only
look at client memory, then shows stats and an in-game overlay. This skill explains the game concepts and where the
app models them. Season-specific Battlegrounds mechanics change often: trust the code and current card data over
this text.

References:
- [reference/power-log.md](reference/power-log.md) - log files, `log.config`/`client.config`, Power.log grammar,
  zones, tags, game types.
- [reference/kolodahearthstone-api.md](reference/kolodahearthstone-api.md) - the Hearthstone data API
  (BG hero/minion/trinket stats, archetypes, card library) with verified response fields.

## Game modes

| Mode | What it is | In the log / app |
|---|---|---|
| Ranked / Casual Constructed | 1v1 with a prebuilt 30-card deck; formats Standard (recent sets), Wild (all sets), Classic, Twist (rotating special pool) | `GT_RANKED` / `GT_CASUAL`; `FormatType` `FT_STANDARD`/`FT_WILD`/`FT_CLASSIC`/`FT_TWIST`; `ConstructedTracker` |
| Arena | draft a deck from offered cards, play a run | `GameType` contains `ARENA`; Arena section in log.config |
| Tavern Brawl | special weekly rules | `GT_TAVERNBRAWL`, `GT_FSG_BRAWL*` -> "Brawl" |
| Practice / Friendly | vs AI / vs friend | `GT_VS_AI`, `GT_VS_FRIEND` |
| Battlegrounds Solo | 8-player auto-battler, free-for-all | `GameType` contains `BATTLEGROUNDS`; `BattlegroundsTracker` |
| Battlegrounds Duos | 4 teams of 2 | contains `BATTLEGROUNDS` and `DUO`; `GameState.IsDuos`, `BACON_DUO_TEAM_ID` |
| Mercenaries | separate mode | recorded as "Mercenaries", not tracked further |

Mode labels for history: `GameRecords.ModeOf` (`src/Hearthpulse.Core/Tracking/GameRecords.cs`).

## Constructed basics

- 11 classes (Death Knight, Demon Hunter, Druid, Hunter, Mage, Paladin, Priest, Rogue, Shaman, Warlock, Warrior) plus
  neutral cards. Each player has a hero (30 health) and gains one mana crystal per turn up to 10; cards cost mana.
- Deck: 30 cards, at most 2 copies of a card and 1 of a Legendary (some cards change deck rules).
- Mulligan: at the start each player may replace any of the opening cards; the second player also gets **The Coin**
  (`GAME_005`, excluded from original-deck cards in `Entity.IsOriginalDeckCard`). `Entity.Mulliganed`,
  `MULLIGAN_STATE` tag; mulligan stats per archetype in `ConstructedMeta` (`MetaMulliganCard`: keep rate,
  in-hand win rate) and the in-game mulligan guide (`Tracking/MulliganGuide.cs`, `docs/mulligan-v2.md`). In real logs
  `TURN=1` is set before the opening deal, and the Coin is often a cosmetic skin (e.g. `JAIL_COIN3` with
  `COIN_CARD=1`) rather than `GAME_005` - match the tag, not the id.
- Archetype: a family of similar decks ("Zoolock", "XL Rafaamlock"); matchups = archetype-vs-archetype win rates.
  Source in app: the Hearthpulse API only (`ApiConstructedMeta`: HSGuru `/v1/hsguru/*` meta and archetypes, HSReplay
  `/api/db/archetypes*` decks/mulligan/matchups); Russian archetype names come from `Constructed/ArchetypeNames.cs`.
  Firestone is not used anywhere.
- Ranks (per format): Bronze, Silver, Gold, Platinum, Diamond, each 10 -> 1, then Legend with a number.
  `RankInfo` in `Memory/HearthstoneMemory.cs`: star level 1-50 -> league/rank, >= 51 or a legend rank -> Legend;
  `MatchPlayerInfo` has Standard/Wild/Classic/Twist ranks read from memory.
- Deck code (deckstring): base64 of varints - reserved 0, version 1, format (1 Wild, 2 Standard, 3 Classic, 4 Twist),
  hero dbfIds, then lists of cards with 1 copy, 2 copies, and N copies (dbfId + count). `Cards/DeckString.cs`
  (`DeckDefinition`). The deck being played comes from `Decks.log` (`### name`, `# Deck ID:`, code).
- Tracking: `ConstructedTracker` (deck/opponent cards; ignores HERO, HERO_POWER, ENCHANTMENT, PLAYER, GAME entities),
  views `ConstructedView`, `DeckCardRow`, `PlayedCard`, `HandSlot` in `Tracking/Views.cs`. The opponent's hand shows
  only what the player can see (a drawn card stays a card back until revealed; `CREATOR_DBID` is never used).
- Discover / random generation: "Discover" offers 3 cards from a pool defined by class, format and card text; the
  overlay's card pool (`Core/CardPool/`, `docs/discover-pool.md`) shows that pool and its odds.
- Counters: game-wide running values (Corpses, Herald, C'Thun abilities, spells cast by school, quests...) - see the
  `hearthpulse-counters` skill and `docs/counters-constructed.md`.

## Battlegrounds

| Concept | Meaning | Where modelled |
|---|---|---|
| Hero pick | Choose one of several offered heroes at the start; each has a hero power and health + armor | choice options -> `BgHeroOption`; `BattlegroundsView.IsHeroSelection`, `HeroOptions`; advice `BattlegroundsAdvisor`; stats `BattlegroundsMeta` (`MetaHero`) |
| Tavern (Bob) | Shop of minions/spells each turn; gold refills and grows each turn; buy, sell, refresh (reroll), freeze | `BattlegroundsView.Shop`, `Gold`, `ButtonCosts` (`BattlegroundsButtonCosts` Freeze/Roll/Upgrade/DarkGift from memory), `RerollCost` default 1 |
| Tavern tier | Shop level 1-6 (upgrade costs gold); higher tiers offer stronger minions; some lobbies allow a 7th | `TechLevel` (`PLAYER_TECH_LEVEL`), `MaxTechLevel` (`BACON_MAX_PLAYER_TECH_LEVEL`, default 6), card `TECH_LEVEL`, `TierHistory` |
| Tribes | Minion types; each lobby uses a subset | `Battlegrounds/Simulation/Traits.cs` `Tribe` (Beast, Demon, Dragon, Elemental, Mech, Murloc, Naga, Pirate, Quilboar, Undead, Aberration, All); `Cards/DeckString.cs` `Races.ById` (CARDRACE ids); lobby tribes `BattlegroundsView.Races` (memory first) |
| Golden / triple | Three copies of a minion combine into a golden one (doubled stats/effect); playing it gives a reward that Discovers from a higher tier | `PREMIUM` tag / golden card id; `BgMinion.Golden`; `PLAYER_TRIPLES`; `Pairs` (two copies held) |
| Discover | Pick 1 of N offered cards | `PowerParser` choices -> `DiscoverOptions`, `DiscoverSource`, `DiscoverAdvice` |
| Tavern spells | Spells sold in the tavern | card type `BATTLEGROUND_SPELL`; `BestSpell`; `BattlegroundsMeta` card stats |
| Trinkets | Lesser/Greater trinket offers at set turns, passive bonuses | `GENERAL` choice of `BATTLEGROUND_TRINKET`/`_MagicItem_` cards; `TrinketOptions`, `Trinkets`; `MetaTrinket` |
| Anomaly | One lobby-wide rule change per game | `BACON_GLOBAL_ANOMALY_DBID` on the game entity -> `AnomalyCardId` |
| Deity / Old God | Lobby-wide card in some seasons | `BACON_GLOBAL_OLD_GOD_DBID`; effects `Effects/ongoing/Deity*.cs` |
| Dark Gift | Season button that Discovers with a bonus, costs gold | `DarkGiftReady`, `DarkGiftCost`, `DarkGiftAdvice` |
| Quests / quest rewards | Past-season mechanic: complete a quest for a reward card | card type `BATTLEGROUND_QUEST_REWARD` (treated as player-level card in the tracker); API `/api/v1/quests` |
| Buddies | Past-season hero-specific minions | not modelled in Core; API hero items have a `buddy` field |
| Timewarped | Timewarped Tavern cards | `BgCardSource.IsTimewarped` (excluded from the pool); `CombatRelevance`; API `/api/v1/timewarped-cards` |
| Comps | Target board archetypes (from the stats pool; empty until it runs) | `MetaComp`, `CompAdvice`, `AppSettings.CompMode`/`ChosenCompId`/`CompFocus` |
| Rating (MMR) | BG rating, separate for Solo and Duos; placement 1-8 changes it | `BattlegroundsRating(Solo, Duos)` from memory; `RatingPoint` history; meta brackets top 100/50/25/10/1 % |
| Leaderboard | Players by health; place 1-8 at the end | `PLAYER_LEADERBOARD_PLACE`; `BgOpponent` (health, armor, tier, triples, last board, last result) |
| Next opponent | Who you fight next | `NEXT_OPPONENT_PLAYER_ID`; `BgOpponent.IsNextOpponent` |

### Combat rules (what the simulator implements)

- Boards fight automatically. The side with more minions attacks first; equal counts -> coin flip
  (`Simulation/Fight.cs`). Sides alternate; each side's attacker is the next minion from the left.
- Target: random enemy, Taunt minions first; Stealth minions only when nothing else can be attacked
  (`CombatSimulator`).
- Attacker and defender deal their Attack as damage to each other simultaneously; minions at 0 health die in a death
  phase; Deathrattles then Reborn resolve; "after a friendly minion dies" effects and Avenge counters follow.
- Keywords (`Traits.Keyword`): **Taunt**; **Divine Shield** (absorbs the first damage); **Poisonous** (destroys any
  minion it damages); **Venomous** (like Poisonous, used up once it triggers); **Reborn** (returns once with 1 health
  and its printed Attack); **Windfury** (attacks twice), **Mega-Windfury** (four times); **Stealth**.
- Mechanics (`Traits.CardMechanic`): **Deathrattle** (on death), **Avenge (X)** (after X friendly deaths),
  **Rally** ("whenever this attacks", hooked in `OnAttack`), Battlecry, Start of Combat, Magnetic.
- The loser's hero takes damage after combat (armor first; tracked in `CombatRecord.DamageTaken/DamageDealt`): the
  winner's tavern tier + the tier of each surviving minion (tokens without a tier count 1), capped while the lobby's
  combat damage cap is on (`DamageRules`, `CombatDamage.cs`). The cap changes by season (15 until the first death in
  2022; a 5/10/15 ramp until Top 4 in patch 31.2; 2 then 5 on the first turns in build 254477), so it is read from the
  game entity: `BACON_COMBAT_DAMAGE_CAP` (value), `BACON_COMBAT_DAMAGE_CAP_ENABLED` (newer builds; 0 = lifted; older
  builds set the cap itself to 0). Duos damage is not modelled (the loser of one board is not the one who takes it).
- Odds: `CombatOdds(Win, Tie, Loss, Unsimulated)` plus `DamageTaken`/`DamageDealt` (`DamageSpread`: mean, 10th-90th
  percentile, max, histogram over the fights lost/won) and `LethalTaken`/`LethalDealt` (share of fights whose damage
  reaches the hero's health + armor, `SimSide.Health`); minions with combat-relevant text but no registered effect make
  the result `Approximate`. Adding effects: see `hearthpulse-dev` skill and `Battlegrounds/Effects/README.md`.

## Card identity

- `card_id` (string, a.k.a. CardID): e.g. `BG28_300` (Harmless Bonehead), `TB_BaconShop_HERO_37` (a BG hero),
  `BG35_MagicItem_701` (a trinket), `CORE_CS2_029`, `GAME_005` (The Coin). Prefix = set/mode.
- `dbfId` (int): database id used by deck codes, `*_DBID` tags and many stats sources (`Card.DbfId`).
- Golden BG minions have their own card id and dbfId; `Card.BattlegroundsPremiumDbfId` / `BattlegroundsNormalDbfId`
  link them. Effects are keyed by the **normal** id plus `Golden` (`BattlegroundsTracker` normalises).
- Hero skins: `Card.BattlegroundsSkinParentId`; `CardDb.BaseHeroId` maps a skin to its hero.
- Data sources: HearthstoneJSON (`https://api.hearthstonejson.com/v1/latest/{locale}/cards.json`, all cards, used by
  `CardDb`), hsbg.cards (`BgCardSource`: BG tiers, text, current pool), the kolodahearthstone API (Constructed meta, mulligan,
  card pool, BG heroes and minions) and the Hearthpulse stats pool (BG comps, trinkets, turn results, when set up). Rules and licences: `hearthpulse-data-sources` skill.
- `Card` fields: `Id`, `DbfId`, `Name`, `Text`, `Cost`, `Attack`, `Health`, `Armor`, `Type`, `Rarity`, `CardClass`,
  `Set`, `Races`, `Mechanics`, `Collectible`, `TechLevel`, `IsBattlegroundsPoolMinion/Spell`, `IsBattlegroundsHero`,
  `IsBattlegroundsDuosExclusive`. Rarities: COMMON, RARE, EPIC, LEGENDARY (theme brushes `Rarity*`).

## Where to look in code

| Topic | Path |
|---|---|
| Log grammar, entities, tags | `src/Hearthpulse.Core/Power/` (`PowerParser`, `GameState`, `Entity`, `GameTag`) |
| Live pipeline, history rows | `src/Hearthpulse.Core/Tracking/` (`GameSession`, `GameRecords`, `Views.cs`) |
| Battlegrounds state | `src/Hearthpulse.Core/Tracking/BattlegroundsTracker.cs` |
| Constructed state | `src/Hearthpulse.Core/Tracking/ConstructedTracker.cs` |
| Advice, meta, simulator | `src/Hearthpulse.Core/Battlegrounds/` |
| Cards, deck codes, tribes | `src/Hearthpulse.Core/Cards/` |
| Client memory (rating, ranks, tribes, costs) | `src/Hearthpulse.Core/Memory/HearthstoneMemory.cs` |
| Install, log.config, client.config | `src/Hearthpulse.Core/Hearthstone/HearthstoneInstall.cs` |
| Real-log behaviour | `tests/Hearthpulse.Tests/RealLogTests.cs` (uses the local client's kept logs) |
